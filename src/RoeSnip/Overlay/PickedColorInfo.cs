namespace RoeSnip.Overlay;

/// <summary>Immutable snapshot of one eyedropper pick, handed from OverlayWindow (which has the
/// live CapturedFrame/SdrImage) to OverlayController's ColorPickerWindow singleton. Carries
/// everything the window needs to render itself and to position near the click — nothing here
/// outlives the pick itself, so there's no risk of the window holding a stale reference into a
/// CapturedFrame that AppComposition disposes once the (now-cancelled) overlay session returns.</summary>
public sealed record PickedColorInfo(
    byte R,
    byte G,
    byte B,
    double Nits,
    System.Windows.Point ScreenPx,   // absolute virtual-desktop physical pixel of the click
    int MonitorDpiX,
    int MonitorDpiY);
