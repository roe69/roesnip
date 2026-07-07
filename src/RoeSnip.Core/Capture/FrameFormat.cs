namespace RoeSnip.Core.Capture;

public enum FrameFormat
{
    Fp16ScRgb,   // linear HDR buffer (Windows scRGB or macOS EDR — see SdrWhiteInBufferUnits below).
                 // 8 bytes/pixel (4 x System.Half), channel order R,G,B,A.
    Bgra8Srgb,   // already sRGB-encoded passthrough. 4 bytes/pixel, channel order B,G,R,A.
}
