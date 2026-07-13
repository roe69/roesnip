using System;
using RoeSnip.Imaging;

namespace RoeSnip.Recording;

/// <summary>Thin WPF-side wrapper around the portable <see cref="RoeSnip.Core.Recording.GifEncoder"/>
/// (the recording-core-extraction workstream moved the entire from-scratch GIF pipeline there — see
/// that class's own doc comment for the full design). This wrapper exists for exactly one reason:
/// this app's own <see cref="RoeSnip.Imaging.SdrImage"/> is a richer WPF-only type (a
/// <c>ToBitmapSource()</c> method plus a recording-cadence <c>reuseOutput</c> allocation
/// optimization on <c>FromCapturedFrame</c>) than the portable <see cref="RoeSnip.Core.Imaging.SdrImage"/>
/// the Core engine speaks, so every call here re-wraps this app's own <c>frame.Pixels/Width/Height</c>
/// into a Core <c>SdrImage</c> before delegating — see <see cref="Wrap"/> for why that re-wrap does
/// NOT reintroduce a per-frame allocation on the recording hot path despite looking like one.
///
/// Every method below is a straight passthrough; this class owns no state of its own beyond the
/// inner engine instance and the wrap cache.</summary>
public sealed class GifEncoder : IDisposable
{
    private readonly RoeSnip.Core.Recording.GifEncoder _inner;

    // Wrap-cache (see Wrap's own doc comment): a fresh Core SdrImage object is only ever allocated
    // when the underlying pixel array reference actually changes — RecordingController double/
    // triple-buffers its own reused pixel arrays, so in steady state this caches a small, fixed set
    // of wrapper instances rather than allocating one per candidate frame.
    private byte[]? _lastWrappedPixels;
    private RoeSnip.Core.Imaging.SdrImage? _lastWrapped;

    private GifEncoder(RoeSnip.Core.Recording.GifEncoder inner)
    {
        _inner = inner;
    }

    /// <summary>See <see cref="RoeSnip.Core.Recording.GifEncoder.Create"/> — identical contract,
    /// this just opens the portable engine and wraps it.</summary>
    public static GifEncoder Create(string tempFilePath, int width, int height, long timestampTicksPerSecond = 0, RoeSnip.Core.Recording.Gif.GifEncoderOptions? options = null) =>
        new(RoeSnip.Core.Recording.GifEncoder.Create(tempFilePath, width, height, timestampTicksPerSecond, options));

    /// <summary>See <see cref="RoeSnip.Core.Recording.GifEncoder.AddFrame(RoeSnip.Core.Imaging.SdrImage, long)"/>
    /// — identical contract; <paramref name="frame"/>.Pixels is only ever re-wrapped, never copied.</summary>
    public bool AddFrame(SdrImage frame, long timestampTicks) => _inner.AddFrame(Wrap(frame), timestampTicks);

    /// <summary>See <see cref="RoeSnip.Core.Recording.GifEncoder.AddFrame(RoeSnip.Core.Imaging.SdrImage, ushort)"/>.</summary>
    public void AddFrame(SdrImage frame, ushort delayCentiseconds) => _inner.AddFrame(Wrap(frame), delayCentiseconds);

    /// <summary>Re-wraps <paramref name="frame"/>'s own pixel array into a Core <c>SdrImage</c>,
    /// reusing the cached wrapper when the caller handed back the SAME array reference as last time
    /// (the common case: RecordingController's double-buffered tone-map/downsample destinations are
    /// a small, fixed set of arrays reused frame to frame, per this codebase's LOH/Gen2 discipline —
    /// see GifEncoder's own class doc). A genuinely new array reference (or the very first call)
    /// allocates one small, non-LOH wrapper object (three fields: Width, Height, a Pixels
    /// reference — no pixel data is copied), which is Gen0-only garbage, not the Gen2/LOH pressure
    /// that discipline exists to avoid.</summary>
    private RoeSnip.Core.Imaging.SdrImage Wrap(SdrImage frame)
    {
        if (_lastWrapped is null || !ReferenceEquals(_lastWrappedPixels, frame.Pixels))
        {
            _lastWrappedPixels = frame.Pixels;
            _lastWrapped = new RoeSnip.Core.Imaging.SdrImage(frame.Width, frame.Height, frame.Pixels);
        }
        return _lastWrapped;
    }

    /// <summary>See <see cref="RoeSnip.Core.Recording.GifEncoder.TryGetChangedBounds"/> — a direct
    /// static passthrough (no SdrImage involved, so nothing to wrap).</summary>
    public static bool TryGetChangedBounds(byte[] prev, byte[] cur, int width, int height, out RoeSnip.Core.Capture.RectPhysical bounds, byte tolerance = 0) =>
        RoeSnip.Core.Recording.GifEncoder.TryGetChangedBounds(prev, cur, width, height, out bounds, tolerance);

    public void FinalizeAndClose(long endTimestampTicks) => _inner.FinalizeAndClose(endTimestampTicks);

    public void FinalizeAndClose() => _inner.FinalizeAndClose();

    public void Dispose() => _inner.Dispose();
}
