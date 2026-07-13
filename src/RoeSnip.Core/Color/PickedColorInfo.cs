namespace RoeSnip.Core.Color;

/// <summary>Immutable snapshot of one eyedropper pick, handed from the overlay (which has the live
/// CapturedFrame/SdrImage) up to the standalone picker window. Carries everything the window needs
/// to render itself and to position near the click — nothing here outlives the pick itself.
/// Adapted from the frozen WPF app's own src/RoeSnip/Overlay/PickedColorInfo.cs (item 22):
/// <see cref="ScreenPxX"/>/<see cref="ScreenPxY"/> replace WPF's System.Windows.Point (Core has no
/// UI-framework dependency to spend on a point type) but carry the exact same meaning — the
/// absolute virtual-desktop PHYSICAL pixel of the click.</summary>
public sealed record PickedColorInfo(
    byte R,
    byte G,
    byte B,
    double Nits,
    double ScreenPxX,
    double ScreenPxY,
    int MonitorDpiX,
    int MonitorDpiY);
