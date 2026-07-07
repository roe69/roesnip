namespace RoeSnip.Core.Capture;

/// <summary>One physical display, enumerated once per capture trigger. Portable: no HMONITOR (the
/// Windows-only PLAN.md §2.1 field). <see cref="BackendKey"/> is opaque outside the ICaptureBackend
/// that produced it — never parse or compare it across backends.
///   Windows: HMONITOR formatted as "0x{hex}".
///   macOS: the display's CGDirectDisplayID, formatted as its decimal value.
///   Linux (X11 fallback): the RandR output name (e.g. "DP-1").
///   Linux (portal): a synthetic zero-based index — the portal returns one whole-desktop PNG with
///     no per-monitor handles, so there is nothing more meaningful to key on (see PLAN-XPLAT.md §5
///     Linux facts).
/// <see cref="Scale"/> is the OS-reported HiDPI scale factor (1.0, 1.25, 1.5, 2.0, ...) —
/// <see cref="DpiX"/>/<see cref="DpiY"/> / 96.0 on Windows, the compositor/portal-reported scale
/// elsewhere. WP-X3 uses this to sanity-check its own match between a CapturedFrame and the
/// Avalonia Screen it's drawing an overlay for (see PLAN-XPLAT.md §3.3's correlation note — this is
/// a real integration risk, not a formality).</summary>
public sealed record MonitorInfo(
    int Index,
    string DeviceName,
    string BackendKey,
    RectPhysical BoundsPx,
    int DpiX,
    int DpiY,
    double Scale,
    bool AdvancedColorActive,
    double SdrWhiteNits,
    double MaxLuminanceNits,
    bool IsPrimary
);
