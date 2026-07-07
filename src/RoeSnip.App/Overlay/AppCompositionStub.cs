using RoeSnip.Core.Capture;
using RoeSnip.Core.Imaging;
using RoeSnip.Core.Settings;

namespace RoeSnip.App;

// =====================================================================================
// TEMPORARY WP-X3 STUB — DELETE THIS FILE WHEN WP-X2's Program.cs LANDS.
//
// PLAN-XPLAT.md §2.8 puts OverlayResult and AppComposition in Program.cs, which WP-X2
// owns and has not landed yet. WP-X3 (this package) only *consumes* these types, but the
// App project must still compile with the Overlay/ files added, so the two members WP-X3
// actually touches are stubbed here verbatim from §2.8:
//   - OverlayResult             (returned by OverlayController.RunAsync)
//   - AppComposition.RunOverlay (set by OverlayController's [ModuleInitializer])
//   - AppComposition.WriteHdrExport (read-only here: non-null gates the toolbar's
//     Save-HDR button visibility — DESIGN-XPLAT.md "backend capability flag hides the
//     button elsewhere"; WP-X2 sets it from Platform.Windows's JxrWriter on Windows)
//
// WP-X2's real Program.cs defines both types in full (CliOptions, RunTray, etc.); when it
// lands, this file must be deleted wholesale — nothing in Overlay/ references it by file,
// only by these type names, which WP-X2's definitions satisfy identically.
// =====================================================================================

/// <summary>Data-only result of one overlay session — identical fields to the WPF app's
/// OverlayResult (PLAN-XPLAT.md §2.8, copied verbatim).</summary>
public sealed record OverlayResult(
    MonitorInfo Monitor,
    RectPhysical SelectionPx,
    SdrImage RenderedImage,
    CapturedFrame SourceFrame,
    bool CopyPerformed,
    string? SavedPngPath,
    bool SaveHdrRequested
);

/// <summary>Stubbed subset of PLAN-XPLAT.md §2.8's AppComposition — see the file header.</summary>
public static class AppComposition
{
    /// <summary>Set by Overlay/OverlayController.cs via [ModuleInitializer] (WP-X3).</summary>
    public static Func<IReadOnlyList<(CapturedFrame Frame, SdrImage Preview)>, RoeSnipSettings, Task<OverlayResult?>>? RunOverlay { get; set; }

    /// <summary>Set by WP-X2 from RoeSnip.Platform.Windows's JxrWriter — null on non-Windows
    /// builds/RIDs. WP-X3 only reads it (null ⇒ hide the toolbar's Save-HDR button).</summary>
    public static Action<string, CapturedFrame, RectPhysical>? WriteHdrExport { get; set; }
}
