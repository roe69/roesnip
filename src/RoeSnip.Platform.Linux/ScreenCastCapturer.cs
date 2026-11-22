using RoeSnip.Core.Capture;
using RoeSnip.Core.Diagnostics;

namespace RoeSnip.Platform.Linux;

/// <summary>Wayland's primary capturer: copies the newest frame of the kept-open snapshot
/// screencast (<see cref="ScreenCastSession.Snapshot"/>), so a capture costs one memcpy instead of
/// a Screenshot portal round trip (~2.7 s on GNOME, plus a shutter sound and flash). The copy is
/// taken synchronously when the trigger's capture runs, before any overlay window exists.
///
/// The first call (the tray warmup capture) opens the session silently from a saved restore token.
/// Without a token nothing opens until the share dialog is asked for (<see cref="InstantCapture"/>:
/// the first user capture or Settings' "Allow instant capture"); the Screenshot portal is never the
/// stand-in on GNOME.
/// Every failure is a transient <see cref="CaptureException"/>, never memoized.</summary>
public sealed class ScreenCastCapturer : IScreenCapturer
{
    private static readonly TimeSpan WaitWithToken = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan WaitForDialog = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan FirstFrameWait = TimeSpan.FromSeconds(1);
    private static int s_sizeNoteLogged;

    private readonly Func<IReadOnlyList<MonitorInfo>> _enumerateMonitors;

    public ScreenCastCapturer(Func<IReadOnlyList<MonitorInfo>> enumerateMonitors)
    {
        _enumerateMonitors = enumerateMonitors;
    }

    public CapturedFrame Capture(MonitorInfo monitor)
    {
        var session = ScreenCastSession.Snapshot;
        // A background (warmup) capture starts the open but does not sit on the share dialog.
        bool hasToken = session.HasSavedToken;
        var wait = hasToken ? WaitWithToken : CaptureContext.IsBackground ? TimeSpan.Zero : WaitForDialog;
        var live = session.Acquire(wait, out string reason);
        if (live is null)
        {
            throw new CaptureException("screencast unavailable: " + reason);
        }

        var monitors = _enumerateMonitors();
        var geometry = live.StreamFor(monitor, monitors);
        if (geometry is null)
        {
            if (!CaptureContext.IsBackground) // a reopen may show the share dialog: only for a user's capture
            {
                session.ReportUnmappedMonitor(ScreenCastSession.MonitorsSignature(monitors));
            }
            throw new CaptureException(
                $"screencast has no stream for monitor {monitor.Index} ({monitor.DeviceName})");
        }

        var stream = live.GetStream(geometry.Value.NodeId);
        if (stream is null || !stream.WaitForFirstSample(FirstFrameWait))
        {
            throw new CaptureException(
                $"screencast stream node {geometry.Value.NodeId} has no frame" +
                (stream?.DeadReason is string why ? $" ({why})" : ""));
        }

        int width = monitor.BoundsPx.Width, height = monitor.BoundsPx.Height;
        var pixels = new byte[width * height * 4];
        if (!stream.TryCopy(0, 0, width, height, width, height, pixels, out _, out string? sizeNote))
        {
            throw new CaptureException($"screencast stream node {geometry.Value.NodeId} frame could not be read");
        }
        if (sizeNote is not null && Interlocked.Exchange(ref s_sizeNoteLogged, 1) == 0)
        {
            FileLog.Write($"RoeSnip: screencast monitor {monitor.DeviceName}: {sizeNote}");
        }

        return new CapturedFrame(FrameFormat.Bgra8Srgb, width, height, width * 4, pixels,
            monitor, sdrWhiteInBufferUnits: 1.0);
    }
}
