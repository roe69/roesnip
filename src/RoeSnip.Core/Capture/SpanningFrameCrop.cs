namespace RoeSnip.Core.Capture;

/// <summary>Cross-monitor selection (item 09) HDR save: one contributing monitor's own RAW
/// <see cref="CapturedFrame"/> (untouched, no tone-mapping) plus the crop-local rect (relative to
/// that frame's own (0,0)) and the destination offset (relative to the spanning selection's own
/// composite canvas origin) that place its pixels correctly in the stitched output. A list of
/// these — one per monitor the selection actually touches — is what
/// RoeSnip.App.OverlayResult.SpanningFrameCrops carries, and what
/// RoeSnip.Platform.Windows.JxrWriter.WriteSpanning consumes: see that method's own doc comment for
/// why stitching raw scRGB crops from different monitors is well-defined (unlike stitching
/// already-tone-mapped ones would be). A Core type (not an App one) specifically so both
/// RoeSnip.App and RoeSnip.Platform.Windows can reference it without Platform.Windows taking a
/// dependency on App (App references Platform.Windows, never the reverse). Mirrors the WPF app's
/// SpanningFrameCrop (src/RoeSnip/Program.cs).</summary>
public sealed record SpanningFrameCrop(CapturedFrame Frame, RectPhysical LocalCropPx, int DestX, int DestY);
