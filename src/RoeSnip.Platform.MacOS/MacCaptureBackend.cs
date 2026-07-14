using System.Globalization;
using RoeSnip.Core.Capture;
using RoeSnip.Core.Diagnostics;

namespace RoeSnip.Platform.MacOS;

/// <summary>macOS capture backend (PLAN-XPLAT.md §3.5). Exactly one capturer — the scksnap Swift
/// helper — so this implements <see cref="ICaptureBackend"/> directly rather than composing a
/// <see cref="FallbackCaptureBackend"/> (a helper-invocation failure has nowhere else to fall back
/// to). All ScreenCaptureKit work happens inside the helper process; this class only shells out and
/// maps its output onto Core's portable contracts.
///
/// Convention mapping (see §2.2's worked semantics):
///   - FP16 frames arrive in extended linear sRGB under the macOS EDR convention, so
///     <c>SdrWhiteInBufferUnits = 1.0</c> (1.0 buffer units IS SDR reference white by construction).
///   - BGRA8 frames are sRGB passthrough; 1.0 is the documented "n/a" sentinel for that format.
///   - <see cref="MonitorInfo.SdrWhiteNits"/> has no OS-reported absolute value on macOS (EDR is a
///     multiplier model, not nits) — 240.0 placeholder per PLAN-XPLAT.md §6 flag 3.
///   - <see cref="MonitorInfo.MaxLuminanceNits"/> = EDR headroom × SdrWhiteNits, so ToneMapper's
///     unchanged peak derivation (MaxLuminanceNits / SdrWhiteNits == headroom) shoulders correctly
///     without a macOS-specific branch.</summary>
public sealed class MacCaptureBackend : ICaptureBackend
{
    /// <summary>Placeholder — macOS reports EDR headroom as a multiplier, never absolute nits
    /// (PLAN-XPLAT.md §6 flags 3/6). Matches the Windows query-failure default for consistency.</summary>
    public const double DefaultSdrWhiteNits = 240.0;

    private readonly ScksnapHelperClient _helper;

    public MacCaptureBackend() : this(new ScksnapHelperClient()) { }

    /// <summary>Test seam: inject a client pointed at a fake helper path / recorded output.</summary>
    public MacCaptureBackend(ScksnapHelperClient helper) => _helper = helper;

    public string Name => "macOS (ScreenCaptureKit via scksnap)";

    /// <summary>Save HDR is Windows-only in v1 (DESIGN-XPLAT.md) — no JXR-equivalent export path is
    /// defined for macOS EDR frames, even though they can carry real headroom.</summary>
    public bool SupportsHdrExport => false;

    public IReadOnlyList<MonitorInfo> EnumerateMonitors()
    {
        IReadOnlyList<ScksnapDisplay> displays;
        try
        {
            displays = _helper.ListDisplays();
        }
        catch (CaptureException ex)
        {
            // §2.3 contract: empty list only if enumeration itself fails entirely.
            FileLog.Write($"RoeSnip: scksnap display enumeration failed: {ex.Message}");
            return Array.Empty<MonitorInfo>();
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
                BackendKey: d.Id.ToString(CultureInfo.InvariantCulture), // CGDirectDisplayID, decimal (§2.1)
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

    /// <summary>Per the §2.3 contract, a per-monitor failure logs to stderr and omits that monitor —
    /// with ONE deliberate, documented deviation: a TCC Screen Recording denial
    /// (<see cref="ScreenRecordingPermissionDeniedException"/>) PROPAGATES instead of being swallowed.
    /// Every monitor fails identically under a TCC denial, and DESIGN-XPLAT.md requires it to be a
    /// first-class, UI-surfaced error — "zero frames, reason buried in stderr" would violate that.
    /// Flagged for the integrator in WP-X5's report.</summary>
    public IReadOnlyList<CapturedFrame> CaptureAll(
        IReadOnlyList<MonitorInfo>? monitors = null, int? onlyMonitorIndex = null)
    {
        var targets = monitors ?? EnumerateMonitors();
        var results = new List<CapturedFrame>();
        foreach (var monitor in targets)
        {
            if (onlyMonitorIndex is int idx && monitor.Index != idx) continue;
            try
            {
                results.Add(CaptureOne(monitor));
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

    private CapturedFrame CaptureOne(MonitorInfo monitor)
    {
        if (!uint.TryParse(monitor.BackendKey, NumberStyles.None, CultureInfo.InvariantCulture, out uint displayId))
        {
            throw new CaptureException(
                $"Monitor {monitor.Index} has a non-scksnap BackendKey \"{monitor.BackendKey}\" — " +
                "MonitorInfo instances must come from this backend's own EnumerateMonitors (§2.1).");
        }

        var raw = _helper.Capture(displayId);
        FrameFormat format = raw.FormatCode switch
        {
            1 => FrameFormat.Fp16ScRgb, // FP16 RGBA extended linear sRGB (macOS EDR convention)
            2 => FrameFormat.Bgra8Srgb, // SDR passthrough
            _ => throw new CaptureException(
                $"scksnap returned unknown frame format code {raw.FormatCode} for display {displayId}."),
        };

        // Both conventions: 1.0. For Fp16 this is the macOS EDR rule (1.0 buffer units == SDR white);
        // for Bgra8Srgb it is the documented unused sentinel (§2.2).
        return new CapturedFrame(
            format, raw.Width, raw.Height, raw.Stride, raw.Pixels, monitor,
            sdrWhiteInBufferUnits: 1.0);
    }
}

file static class ModuleInit
{
    // CA2255 warns against ModuleInitializer in libraries; this IS the sanctioned "advanced
    // scenario" — PLAN-XPLAT.md §2.3 mandates each Platform.* assembly self-register with
    // CaptureBackendRegistry exactly this way (Core/App never name a concrete backend type).
#pragma warning disable CA2255
    [System.Runtime.CompilerServices.ModuleInitializer]
#pragma warning restore CA2255
    internal static void Init() => CaptureBackendRegistry.Register(
        () => OperatingSystem.IsMacOS(), () => new MacCaptureBackend());
}
