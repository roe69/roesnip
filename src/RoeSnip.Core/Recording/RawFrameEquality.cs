using RoeSnip.Core.Capture;

namespace RoeSnip.Core.Recording;

/// <summary>Encoder-thread-only pre-tonemap dedupe, ported verbatim from the WPF app's
/// RecordingController.RawFramesEqual (private static, ~2416): WGC's dirty tracking is monitor-wide,
/// so a change anywhere on the monitor delivers a frame even when the recorded crop itself is
/// untouched - comparing raw bytes here skips those before paying the much more expensive tone-map +
/// encode step. Extracted to Core (rather than kept private inside RecordingController) so it is
/// unit-testable directly against plain CapturedFrame instances with no capture backend or encoder
/// involved.</summary>
public static class RawFrameEquality
{
    /// <summary>True when two raw captured frames carry identical pixel bytes (row-wise, padding
    /// excluded). Dimension/format mismatches count as changed.</summary>
    public static bool RawFramesEqual(CapturedFrame a, CapturedFrame b)
    {
        if (a.Format != b.Format || a.Width != b.Width || a.Height != b.Height)
        {
            return false;
        }
        for (int y = 0; y < a.Height; y++)
        {
            if (!a.Row(y).SequenceEqual(b.Row(y)))
            {
                return false;
            }
        }
        return true;
    }
}
