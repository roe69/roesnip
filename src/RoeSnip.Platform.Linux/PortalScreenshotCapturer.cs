using RoeSnip.Core.Capture;
using SkiaSharp;
using Tmds.DBus;

namespace RoeSnip.Platform.Linux;

/// <summary>Tmds.DBus proxy for <c>org.freedesktop.portal.Screenshot</c> on
/// <c>org.freedesktop.portal.Desktop</c> at <c>/org/freedesktop/portal/desktop</c>. Public because
/// Tmds.DBus emits its proxy implementation into a dynamic assembly that must see the interface.</summary>
[DBusInterface("org.freedesktop.portal.Screenshot")]
public interface IPortalScreenshot : IDBusObject
{
    Task<ObjectPath> ScreenshotAsync(string parentWindow, IDictionary<string, object> options);
}

/// <summary>Tmds.DBus proxy for <c>org.freedesktop.portal.Request</c> — the per-call request object
/// whose <c>Response</c> signal carries the actual portal result (signature <c>ua{sv}</c>).</summary>
[DBusInterface("org.freedesktop.portal.Request")]
public interface IPortalRequest : IDBusObject
{
    Task CloseAsync();
    Task<IDisposable> WatchResponseAsync(
        Action<(uint response, IDictionary<string, object> results)> handler,
        Action<Exception>? onError = null);
}

/// <summary>Primary Linux capturer (PLAN-XPLAT.md §3.4): asks xdg-desktop-portal for a silent,
/// non-interactive screenshot of the WHOLE virtual desktop (one PNG file URI), decodes it via
/// SkiaSharp, and slices this monitor's rectangle out of it using the RandR-enumerated bounds —
/// verifying the pixel scale at runtime by comparing the PNG's dimensions to the RandR virtual
/// desktop (HiDPI portals return physical pixels while some compositors report logical bounds).
///
/// Because the portal is inherently whole-desktop while <see cref="IScreenCapturer.Capture"/> is
/// per-monitor (and <c>FallbackCaptureBackend.CaptureAll</c> captures monitors in parallel),
/// concurrent per-monitor calls COALESCE onto one in-flight portal request, and a just-completed
/// desktop shot is reused for a short window (1s) so one capture trigger never fires N portal
/// requests (and N GNOME permission prompts) for N monitors.
///
/// GNOME may show a one-time (new portals) or per-shot (old portals) permission dialog — documented,
/// not fought, per DESIGN-XPLAT.md; the response wait allows 60s for the user to answer it.</summary>
public sealed class PortalScreenshotCapturer : IScreenCapturer
{
    private const string PortalService = "org.freedesktop.portal.Desktop";
    private static readonly ObjectPath PortalObjectPath = new("/org/freedesktop/portal/desktop");
    private static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ReuseWindow = TimeSpan.FromSeconds(1);

    private readonly Func<IReadOnlyList<MonitorInfo>> _enumerateMonitors;
    private readonly object _gate = new();
    private Task<DesktopShot>? _shotTask;

    /// <param name="enumerateMonitors">The backend's own RandR enumeration — needed here because
    /// slicing requires the union of ALL monitor bounds (the virtual-desktop origin and extent),
    /// not just the one monitor being captured.</param>
    public PortalScreenshotCapturer(Func<IReadOnlyList<MonitorInfo>> enumerateMonitors)
    {
        _enumerateMonitors = enumerateMonitors;
    }

    public CapturedFrame Capture(MonitorInfo monitor)
    {
        Task<DesktopShot> task;
        lock (_gate)
        {
            task = _shotTask switch
            {
                null => _shotTask = StartNewShot(),
                { IsCompleted: false } inflight => inflight, // coalesce concurrent per-monitor calls
                { IsCompletedSuccessfully: true } fresh
                    when DateTime.UtcNow - fresh.Result.TakenUtc < ReuseWindow => fresh,
                _ => _shotTask = StartNewShot(), // faulted or stale — take a new shot
            };
        }

        DesktopShot shot;
        try
        {
            shot = task.GetAwaiter().GetResult();
        }
        catch (CaptureException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new CaptureException($"xdg-desktop-portal screenshot failed: {ex.Message}", ex);
        }

        return Slice(shot, monitor);
    }

    /// <summary>Starts a new whole-desktop shot AND schedules its own eager cleanup once the reuse
    /// window lapses (P6 leak fix). Without this, a resident tray app that captures once and then
    /// sits idle keeps a full whole-desktop <c>byte[]</c> buffer rooted via <see cref="_shotTask"/>
    /// for however long until the NEXT capture — which may be arbitrarily far in the future, or
    /// never, for the rest of the process's life. The delayed continuation only clears the field if
    /// it still points at THIS shot (a newer shot started in the meantime must not be clobbered).</summary>
    private Task<DesktopShot> StartNewShot()
    {
        var task = Task.Run(TakeDesktopShotAsync);
        _ = task.ContinueWith(t =>
        {
            if (!t.IsCompletedSuccessfully)
            {
                return;
            }
            _ = Task.Delay(ReuseWindow).ContinueWith(_ =>
            {
                lock (_gate)
                {
                    if (ReferenceEquals(_shotTask, t))
                    {
                        _shotTask = null;
                    }
                }
            }, TaskScheduler.Default);
        }, TaskScheduler.Default);
        return task;
    }

    private async Task<DesktopShot> TakeDesktopShotAsync()
    {
        var monitors = _enumerateMonitors();
        if (monitors.Count == 0)
        {
            throw new CaptureException(
                "no monitors enumerated — cannot slice the portal's whole-desktop screenshot.");
        }

        int left = int.MaxValue, top = int.MaxValue, right = int.MinValue, bottom = int.MinValue;
        foreach (var m in monitors)
        {
            left = Math.Min(left, m.BoundsPx.Left);
            top = Math.Min(top, m.BoundsPx.Top);
            right = Math.Max(right, m.BoundsPx.Right);
            bottom = Math.Max(bottom, m.BoundsPx.Bottom);
        }
        var union = new RectPhysical(left, top, right, bottom);
        if (union.Width <= 0 || union.Height <= 0)
        {
            throw new CaptureException(
                $"degenerate virtual-desktop bounds {union.Width}x{union.Height} from monitor enumeration.");
        }

        string uriString = await RequestScreenshotUriAsync().ConfigureAwait(false);

        Uri uri;
        try
        {
            uri = new Uri(uriString);
        }
        catch (UriFormatException ex)
        {
            throw new CaptureException($"portal returned an unparsable screenshot URI '{uriString}'.", ex);
        }
        if (!uri.IsFile)
        {
            throw new CaptureException($"portal returned a non-file screenshot URI '{uriString}'.");
        }
        string path = uri.LocalPath;

        byte[] bgra;
        int pngWidth, pngHeight, stride;
        try
        {
            using var codec = SKCodec.Create(path);
            if (codec is null)
            {
                throw new CaptureException($"could not open the portal's screenshot PNG at '{path}'.");
            }

            var info = new SKImageInfo(
                codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
            using var bitmap = new SKBitmap(info);
            var decodeResult = codec.GetPixels(info, bitmap.GetPixels());
            if (decodeResult != SKCodecResult.Success && decodeResult != SKCodecResult.IncompleteInput)
            {
                throw new CaptureException(
                    $"could not decode the portal's screenshot PNG at '{path}' ({decodeResult}).");
            }

            pngWidth = info.Width;
            pngHeight = info.Height;
            stride = bitmap.RowBytes;
            bgra = bitmap.Bytes;
        }
        finally
        {
            try { File.Delete(path); } catch { /* portal temp file — best-effort cleanup */ }
        }

        // Runtime pixel-scale verification (PLAN-XPLAT.md §3.4): HiDPI portals return physical
        // pixels while RandR bounds may be logical on some compositors. Discover the actual scale
        // instead of assuming 1:1, and log it on every capture so a mismatch is visible, not silent.
        double scaleX = (double)pngWidth / union.Width;
        double scaleY = (double)pngHeight / union.Height;
        bool oneToOne = Math.Abs(scaleX - 1.0) < 0.001 && Math.Abs(scaleY - 1.0) < 0.001;
        Console.Error.WriteLine(
            $"RoeSnip: portal screenshot {pngWidth}x{pngHeight}; RandR virtual desktop " +
            $"{union.Width}x{union.Height} at ({union.Left},{union.Top}); pixel scale {scaleX:F3}x{scaleY:F3}" +
            (oneToOne ? "" : " — portal/RandR unit mismatch detected, slicing with the discovered scale"));

        return new DesktopShot(bgra, pngWidth, pngHeight, stride, union, scaleX, scaleY, DateTime.UtcNow);
    }

    /// <summary>The two-step portal dance: subscribe to the (predicted) request object's Response
    /// signal FIRST, then call Screenshot — the token-derived request path avoids the classic race
    /// where the signal fires before the caller has subscribed. Returns the result's file URI.</summary>
    private static async Task<string> RequestScreenshotUriAsync()
    {
        string? address = Address.Session;
        if (string.IsNullOrEmpty(address))
        {
            throw new CaptureException(
                "no D-Bus session bus (DBUS_SESSION_BUS_ADDRESS unset) — xdg-desktop-portal is unavailable.");
        }

        using var connection = new Connection(address);
        ConnectionInfo connectionInfo;
        try
        {
            connectionInfo = await connection.ConnectAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new CaptureException($"could not connect to the D-Bus session bus: {ex.Message}", ex);
        }

        string token = "roesnip_" + Guid.NewGuid().ToString("N");
        string senderPart = connectionInfo.LocalName.TrimStart(':').Replace('.', '_');
        var expectedRequestPath = new ObjectPath(
            $"/org/freedesktop/portal/desktop/request/{senderPart}/{token}");

        var responseSource = new TaskCompletionSource<(uint Response, IDictionary<string, object> Results)>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        IDisposable? watcher = null;
        try
        {
            watcher = await connection.CreateProxy<IPortalRequest>(PortalService, expectedRequestPath)
                .WatchResponseAsync(
                    r => responseSource.TrySetResult(r),
                    ex => responseSource.TrySetException(ex))
                .ConfigureAwait(false);

            var portal = connection.CreateProxy<IPortalScreenshot>(PortalService, PortalObjectPath);
            var options = new Dictionary<string, object>
            {
                ["handle_token"] = token,
                ["interactive"] = false, // silent shot; GNOME may still show its own permission prompt
            };

            ObjectPath actualRequestPath;
            try
            {
                actualRequestPath = await portal.ScreenshotAsync("", options).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                throw new CaptureException(
                    $"org.freedesktop.portal.Screenshot call failed (is xdg-desktop-portal running?): {ex.Message}",
                    ex);
            }

            if (actualRequestPath.ToString() != expectedRequestPath.ToString())
            {
                // Pre-0.9 portals ignore handle_token and mint their own request path — re-subscribe
                // on the actual path (a small signal race exists on those old portals only).
                watcher.Dispose();
                watcher = await connection.CreateProxy<IPortalRequest>(PortalService, actualRequestPath)
                    .WatchResponseAsync(
                        r => responseSource.TrySetResult(r),
                        ex => responseSource.TrySetException(ex))
                    .ConfigureAwait(false);
            }

            var completed = await Task.WhenAny(responseSource.Task, Task.Delay(ResponseTimeout))
                .ConfigureAwait(false);
            if (completed != responseSource.Task)
            {
                throw new CaptureException(
                    $"the screenshot portal did not respond within {ResponseTimeout.TotalSeconds:F0}s " +
                    "(unanswered permission dialog?).");
            }

            var (response, results) = await responseSource.Task.ConfigureAwait(false);
            if (response != 0)
            {
                throw new CaptureException(response == 1
                    ? "screenshot request was cancelled (portal permission denied or dialog dismissed)."
                    : $"screenshot portal reported an error (response code {response}).");
            }

            if (!results.TryGetValue("uri", out object? uriValue)
                || uriValue is not string uriString
                || string.IsNullOrEmpty(uriString))
            {
                throw new CaptureException("screenshot portal response did not contain a 'uri' result.");
            }

            return uriString;
        }
        finally
        {
            watcher?.Dispose();
        }
    }

    private static CapturedFrame Slice(DesktopShot shot, MonitorInfo monitor)
    {
        var bounds = monitor.BoundsPx;
        int srcX = (int)Math.Round((bounds.Left - shot.Union.Left) * shot.ScaleX);
        int srcY = (int)Math.Round((bounds.Top - shot.Union.Top) * shot.ScaleY);
        int width = (int)Math.Round(bounds.Width * shot.ScaleX);
        int height = (int)Math.Round(bounds.Height * shot.ScaleY);

        srcX = Math.Clamp(srcX, 0, Math.Max(0, shot.Width - 1));
        srcY = Math.Clamp(srcY, 0, Math.Max(0, shot.Height - 1));
        width = Math.Min(width, shot.Width - srcX);
        height = Math.Min(height, shot.Height - srcY);
        if (width <= 0 || height <= 0)
        {
            throw new CaptureException(
                $"monitor {monitor.Index} ({monitor.DeviceName}) bounds {bounds.Left},{bounds.Top} " +
                $"{bounds.Width}x{bounds.Height} fall outside the portal screenshot ({shot.Width}x{shot.Height}).");
        }

        var pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            Buffer.BlockCopy(
                shot.Bgra, (srcY + y) * shot.Stride + srcX * 4,
                pixels, y * width * 4, width * 4);
        }
        for (int i = 3; i < pixels.Length; i += 4)
        {
            pixels[i] = 255; // desktops are opaque; don't trust the encoder's alpha
        }

        return new CapturedFrame(
            FrameFormat.Bgra8Srgb, width, height, width * 4, pixels,
            monitor, sdrWhiteInBufferUnits: 1.0);
    }

    /// <summary>One decoded whole-desktop portal screenshot plus the geometry needed to slice it.</summary>
    private sealed record DesktopShot(
        byte[] Bgra, int Width, int Height, int Stride,
        RectPhysical Union, double ScaleX, double ScaleY, DateTime TakenUtc);
}
