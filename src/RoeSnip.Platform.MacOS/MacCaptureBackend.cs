using System.Diagnostics;
using System.Globalization;
using System.Text;
using RoeSnip.Core.Capture;
using RoeSnip.Core.Diagnostics;

namespace RoeSnip.Platform.MacOS;

/// <summary>macOS capture backend: the long-lived <c>scksnap serve</c> helper first
/// (<see cref="ScksnapServer"/>, a copy of each display's kept-open stream frame), the one-shot
/// <c>scksnap capture</c> process per display second. All ScreenCaptureKit work happens in the
/// helper; this class maps its output onto Core's portable contracts.
///
/// Convention mapping:
///   - FP16 frames arrive in extended linear sRGB under the macOS EDR convention, so
///     <c>SdrWhiteInBufferUnits = 1.0</c> (1.0 buffer units IS SDR reference white by construction).
///   - BGRA8 frames are sRGB passthrough; 1.0 is the documented "n/a" sentinel for that format.
///   - <see cref="MonitorInfo.SdrWhiteNits"/> has no OS-reported absolute value on macOS (EDR is a
///     multiplier model, not nits) - 240.0 placeholder.
///   - <see cref="MonitorInfo.MaxLuminanceNits"/> = EDR headroom x SdrWhiteNits, so ToneMapper's
///     peak derivation (MaxLuminanceNits / SdrWhiteNits == headroom) needs no macOS branch.</summary>
public sealed class MacCaptureBackend : ICaptureBackend
{
    /// <summary>Placeholder - macOS reports EDR headroom as a multiplier, never absolute nits.</summary>
    public const double DefaultSdrWhiteNits = 240.0;

    private readonly ScksnapHelperClient _helper;
    private readonly ScksnapServer? _server;

    public MacCaptureBackend() : this(new ScksnapHelperClient(), ScksnapServer.Shared) { }

    /// <summary>Test seam: inject a client pointed at a fake helper path; null server = one-shot only.</summary>
    public MacCaptureBackend(ScksnapHelperClient helper, ScksnapServer? server = null)
    {
        _helper = helper;
        _server = server;
    }

    public string Name => "macOS (ScreenCaptureKit via scksnap)";

    /// <summary>Save HDR is Windows-only in v1 - no JXR-equivalent export path is defined for macOS
    /// EDR frames, even though they can carry real headroom.</summary>
    public bool SupportsHdrExport => false;

    public IReadOnlyList<MonitorInfo> EnumerateMonitors()
    {
        IReadOnlyList<ScksnapDisplay>? displays = _server?.TryListDisplays();
        if (displays is null)
        {
            try
            {
                displays = _helper.ListDisplays();
            }
            catch (CaptureException ex)
            {
                // Contract: empty list only if enumeration itself fails entirely.
                FileLog.Write($"RoeSnip: scksnap display enumeration failed: {ex.Message}");
                return Array.Empty<MonitorInfo>();
            }
        }

        var monitors = new List<MonitorInfo>(displays.Count);
        for (int i = 0; i < displays.Count; i++)
        {
            var d = displays[i];
            double headroom = Math.Max(d.EdrHeadroom, 1.0);
            int dpi = (int)Math.Round(96.0 * (d.Scale > 0 ? d.Scale : 1.0));
            monitors.Add(new MonitorInfo(
                Index: i,
                DeviceName: string.IsNullOrEmpty(d.Name) ? $"Display {d.Id}" : d.Name,
                BackendKey: d.Id.ToString(CultureInfo.InvariantCulture), // CGDirectDisplayID, decimal
                BoundsPx: RectPhysical.FromSize(d.X, d.Y, d.WidthPx, d.HeightPx),
                DpiX: dpi,
                DpiY: dpi,
                Scale: d.Scale > 0 ? d.Scale : 1.0,
                AdvancedColorActive: d.EdrPotentialHeadroom > 1.0,
                SdrWhiteNits: DefaultSdrWhiteNits,
                MaxLuminanceNits: headroom * DefaultSdrWhiteNits,
                IsPrimary: d.IsPrimary));
        }
        return monitors;
    }

    /// <summary>A per-monitor failure logs and omits that monitor, with ONE deliberate deviation: a
    /// TCC Screen Recording denial (<see cref="ScreenRecordingPermissionDeniedException"/>)
    /// PROPAGATES - every monitor fails identically under it, and it must reach the UI as a
    /// first-class error. A background capture (the tray warmup) never shows the Screen Recording
    /// prompt through serve mode; it gets the denial instead.</summary>
    public IReadOnlyList<CapturedFrame> CaptureAll(
        IReadOnlyList<MonitorInfo>? monitors = null, int? onlyMonitorIndex = null)
    {
        var targets = new List<(MonitorInfo Monitor, uint DisplayId)>();
        foreach (var monitor in monitors ?? EnumerateMonitors())
        {
            if (onlyMonitorIndex is int idx && monitor.Index != idx) continue;
            try
            {
                targets.Add((monitor, ParseDisplayId(monitor)));
            }
            catch (CaptureException ex)
            {
                FileLog.Write($"RoeSnip: {ex.Message} Omitting this monitor.");
            }
        }
        if (targets.Count == 0) return Array.Empty<CapturedFrame>();

        IReadOnlyList<ScksnapFrame>? served = null;
        if (_server is not null)
        {
            bool background = CaptureContext.IsBackground || Thread.CurrentThread.Name == "RoeSnip-Warmup";
            var watch = Stopwatch.StartNew();
            served = _server.TrySnapshot(targets.Select(t => t.DisplayId).ToArray(), background);
            if (served is not null)
            {
                FileLog.Write($"RoeSnip: scksnap snapshot {served.Count}/{targets.Count} display(s) in " +
                    $"{watch.ElapsedMilliseconds} ms ({Describe(served)})");
            }
        }

        var results = new List<CapturedFrame>(targets.Count);
        foreach (var (monitor, displayId) in targets)
        {
            var raw = served?.FirstOrDefault(f => f.DisplayId == displayId);
            try
            {
                raw ??= _helper.Capture(displayId);
                results.Add(ToCapturedFrame(raw, monitor));
            }
            catch (ScreenRecordingPermissionDeniedException)
            {
                throw;
            }
            catch (CaptureException ex)
            {
                FileLog.Write(
                    $"RoeSnip: scksnap capture failed for monitor {monitor.Index} ({monitor.DeviceName}): " +
                    $"{ex.Message}. Omitting this monitor.");
            }
        }
        return results;
    }

    private static string Describe(IReadOnlyList<ScksnapFrame> frames)
    {
        var text = new StringBuilder();
        foreach (var f in frames)
        {
            if (text.Length > 0) text.Append(", ");
            text.Append(f.DisplayId.ToString(CultureInfo.InvariantCulture)).Append(": ");
            text.Append(f.Source == ScksnapProtocol.SourceStream
                ? $"stream frame {f.AgeMs} ms old"
                : "screenshot");
        }
        return text.ToString();
    }

    internal static uint ParseDisplayId(MonitorInfo monitor)
    {
        if (!uint.TryParse(monitor.BackendKey, NumberStyles.None, CultureInfo.InvariantCulture, out uint displayId))
        {
            throw new CaptureException(
                $"Monitor {monitor.Index} has a non-scksnap BackendKey \"{monitor.BackendKey}\" - " +
                "MonitorInfo instances must come from this backend's own EnumerateMonitors.");
        }
        return displayId;
    }

    internal static CapturedFrame ToCapturedFrame(ScksnapFrame raw, MonitorInfo monitor)
    {
        FrameFormat format = raw.FormatCode switch
        {
            1 => FrameFormat.Fp16ScRgb, // FP16 RGBA extended linear sRGB (macOS EDR convention)
            2 => FrameFormat.Bgra8Srgb, // SDR passthrough
            _ => throw new CaptureException(
                $"scksnap returned unknown frame format code {raw.FormatCode} for display {raw.DisplayId}."),
        };

        // Both conventions: 1.0. For Fp16 this is the macOS EDR rule (1.0 buffer units == SDR white);
        // for Bgra8Srgb it is the documented unused sentinel.
        return new CapturedFrame(
            format, raw.Width, raw.Height, raw.Stride, raw.Pixels, monitor,
            sdrWhiteInBufferUnits: 1.0);
    }
}

file static class ModuleInit
{
    // CA2255 warns against ModuleInitializer in libraries; cross-assembly self-registration into
    // CaptureBackendRegistry is the pattern every Platform.* project uses.
#pragma warning disable CA2255
    [System.Runtime.CompilerServices.ModuleInitializer]
#pragma warning restore CA2255
    internal static void Init() => CaptureBackendRegistry.Register(
        () => OperatingSystem.IsMacOS(), () => new MacCaptureBackend());
}
