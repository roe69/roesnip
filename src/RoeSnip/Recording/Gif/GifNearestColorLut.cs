using System;
using System.Numerics;

namespace RoeSnip.Recording.Gif;

/// <summary>Maps a BGR888 color to its nearest entry in the CURRENT palette via a 32768-entry
/// (5 bits each of B, G, R) direct-indexed lookup table, filled lazily on miss instead of
/// eagerly rebuilt whenever the palette changes.
///
/// Eager rebuild would mean 32768 brute-force nearest-color searches (each up to 255 palette
/// comparisons) every time <see cref="Rebuild"/> runs, even though a typical changed-region frame
/// only ever queries a few thousand distinct 5-5-5 buckets out of the 32768 possible — most of
/// that work would be thrown away unused. Instead <see cref="Rebuild"/> only transposes the
/// (already tiny, &lt;=255-entry) palette into the SoA layout <see cref="FillBucket"/> needs — see
/// that method's own doc comment for why — and bumps <see cref="_version"/>; it never touches the
/// 32768-entry LUT itself. Every bucket carries its own version stamp; a lookup whose stamp doesn't
/// match the current version is stale (built against an earlier palette, or never built) and gets
/// refilled on the spot, then stamped current. No array is ever cleared or reallocated between
/// palettes — the LUT, the stamp array, and the transposed palette buffers below are all permanent,
/// instance-lifetime buffers, which is what keeps this allocation-free at recording cadence.
///
/// A bucket's fill uses the BUCKET'S OWN CENTER color, not whichever exact query color happened to
/// trigger the fill (see <see cref="FillBucket"/>) — 5-5-5 already means every color sharing a
/// bucket is treated as equivalent, so the cached answer must be a function of the bucket alone.
/// Otherwise two pixels landing in the same bucket in different frames (or the same frame, in a
/// different order) could resolve to different palette entries depending purely on which one was
/// queried first, which would show up as a repaint-loop repainting non-drifting content.
///
/// PERFORMANCE (GIF software-encode CPU workstream): stage-timing instrumentation
/// (<see cref="GifEncoderStageTimings"/>) measured on full-motion synthetic content that this
/// class's <see cref="FillBucket"/> — not the tolerance test or the dither math elsewhere in
/// GifDelta.ClassifyAndPaint — was the dominant cost: roughly three-quarters of ClassifyAndPaint's
/// own time (which itself was ~75-78% of total encode time) at both Quality@25fps and Max@50fps,
/// because full-motion content invalidates the palette almost every frame, so nearly every distinct
/// 5-5-5 bucket a frame touches is a genuine cache miss requiring a fresh brute-force scan. See
/// <see cref="FillBucket"/>'s own doc comment for the fix.</summary>
public sealed class GifNearestColorLut
{
    private const int BucketCount = 32768; // 2^15 = 5 bits B, 5 bits G, 5 bits R
    // GIF89a's Local Color Table caps opaque entries at 255 (GifEncoderOptions.MaxPaletteColors'
    // own doc comment has the full reasoning) — a fixed upper bound safe for every legal palette
    // size, so the transposed buffers below never need to grow.
    private const int MaxPaletteEntries = 255;

    private readonly short[] _nearest = new short[BucketCount];
    private readonly int[] _version = new int[BucketCount];
    private int _currentVersion;

    private byte[] _paletteBgr = Array.Empty<byte>();
    private int _colorCount;

    // Structure-of-arrays copy of _paletteBgr's first _colorCount entries, refreshed once per
    // Rebuild (cheap: <=255 elements, once per palette change, never per pixel) purely so
    // FillBucket's hot loop can load contiguous same-channel values into a SIMD vector across
    // several palette entries at once — the interleaved B,G,R byte triples GifEncoder's LCT-writing
    // and every other caller of _paletteBgr still use are exactly right for THEM (one entry at a
    // time, written straight to the output stream) but wrong for a vectorized distance scan across
    // MANY entries. Kept as double[], not float[]: see FillBucket's own doc comment for why single
    // precision is not safe here. Fixed-size, allocated once — same allocation-free-at-recording-
    // cadence discipline as every other buffer on this class.
    private readonly double[] _paletteB = new double[MaxPaletteEntries];
    private readonly double[] _paletteG = new double[MaxPaletteEntries];
    private readonly double[] _paletteR = new double[MaxPaletteEntries];
    // Reused scratch for FillBucket's distance-then-argmin two-pass scan (see that method's own
    // doc comment for why it's two passes) — sized once, never reallocated.
    private readonly double[] _distScratch = new double[MaxPaletteEntries];

    private static readonly Vector<double> Two = new(2.0);
    private static readonly Vector<double> Four = new(4.0);
    private static readonly Vector<double> TwoFiveFive = new(255.0);
    private static readonly Vector<double> InvTwoFiveSix = new(1.0 / 256.0);

    /// <summary>Swaps in a new (or reused, if the caller intentionally kept the same array —
    /// see the reuse-first fast path in the encoder) palette: re-transposes it into the SoA buffers
    /// <see cref="FillBucket"/> reads (O(colorCount), colorCount &lt;= 255, so this is cheap even
    /// though it is no longer the pure O(1) reference-swap this method used to be) and invalidates
    /// every existing LUT bucket stamp by bumping <see cref="_currentVersion"/>. Still never touches
    /// the 32768-entry LUT itself — that stays lazy, filled only for buckets an actual lookup
    /// queries.</summary>
    public void Rebuild(byte[] paletteBgr, int colorCount)
    {
        _paletteBgr = paletteBgr;
        _colorCount = colorCount;
        for (int p = 0; p < colorCount; p++)
        {
            int o = p * 3;
            _paletteB[p] = paletteBgr[o];
            _paletteG[p] = paletteBgr[o + 1];
            _paletteR[p] = paletteBgr[o + 2];
        }
        _currentVersion++;
    }

    /// <summary>Returns the palette index nearest <paramref name="b"/>,<paramref name="g"/>,
    /// <paramref name="r"/> under redmean distance. Must not be called before the first
    /// <see cref="Rebuild"/>, and the palette passed to that <see cref="Rebuild"/> must contain at
    /// least one color.</summary>
    public int Lookup(byte b, byte g, byte r)
    {
        int bucket = ((b >> 3) << 10) | ((g >> 3) << 5) | (r >> 3);
        if (_version[bucket] != _currentVersion)
        {
            if (GifEncoderStageTimings.Enabled)
            {
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                FillBucket(bucket);
                GifEncoderStageTimings.LutFillTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
                GifEncoderStageTimings.LutFillCount++;
            }
            else
            {
                FillBucket(bucket);
            }
        }
        if (GifEncoderStageTimings.Enabled)
        {
            GifEncoderStageTimings.LutLookupCount++;
        }
        return _nearest[bucket];
    }

    /// <summary>The measured hot loop (see class doc's PERFORMANCE note): a brute-force nearest-
    /// palette-entry search under redmean-squared distance, now computed in two passes instead of
    /// one fused scan-with-early-exit —
    ///   1. <see cref="ComputeDistances"/> fills <see cref="_distScratch"/>[0..colorCount) via
    ///      <see cref="Vector{T}"/> of double, <see cref="Vector{T}.Count"/> palette entries at a
    ///      time (falls back to a plain scalar loop for whatever remainder doesn't fill a full
    ///      vector), using EXACTLY <see cref="GifColorDistance.RedmeanSquared"/>'s own operation
    ///      order (same two operands per term, same +/-/*// sequence) so every lane's result is
    ///      bit-identical to calling that scalar method directly — never reordered/fused
    ///      arithmetic, and deliberately double (not float): redmean's rmean/256 and (255-rmean)/256
    ///      terms are exact in either precision (256 is a power of two), but the PRODUCT of that
    ///      term with dr*dr/dg*dg/db*db is where float32's coarser rounding could disagree with the
    ///      double-precision scalar method on a close-enough pair of candidate palette entries —
    ///      exactness here is what keeps this a real optimization rather than a "probably still
    ///      right" one.
    ///   2. A cheap scalar min-index scan over the now-fully-computed distances, breaking the
    ///      instant it finds an exact zero (the global minimum by construction, so its first
    ///      occurrence is the same answer the OLD single-pass early-exit loop found) and otherwise
    ///      picking the first index achieving the smallest value with the same strict `&lt;`
    ///      comparison the old loop used — so ties resolve to the same (lowest) index either way.
    /// Net effect: identical output to the original one-pass scalar loop on every input, with the
    /// expensive per-candidate arithmetic vectorized.</summary>
    private void FillBucket(int bucket)
    {
        // Reconstruct the bucket's center color (mid-point of the 8 values each dropped 3 bits
        // span) rather than using the triggering query's exact color — see the class doc comment.
        byte b = (byte)(((bucket >> 10) & 0x1F) << 3 | 0x04);
        byte g = (byte)(((bucket >> 5) & 0x1F) << 3 | 0x04);
        byte r = (byte)((bucket & 0x1F) << 3 | 0x04);

        ComputeDistances(b, g, r, _colorCount);

        int best = 0;
        double bestDist = double.MaxValue;
        for (int p = 0; p < _colorCount; p++)
        {
            double dist = _distScratch[p];
            if (dist < bestDist)
            {
                bestDist = dist;
                best = p;
                if (dist == 0)
                {
                    break;
                }
            }
        }

        _nearest[bucket] = (short)best;
        _version[bucket] = _currentVersion;
    }

    /// <summary>Fills <see cref="_distScratch"/>[0..colorCount) with each palette entry's redmean-
    /// squared distance from (b,g,r) — see <see cref="FillBucket"/>'s own doc comment for the exact
    /// equivalence argument with <see cref="GifColorDistance.RedmeanSquared"/>.</summary>
    private void ComputeDistances(byte b, byte g, byte r, int colorCount)
    {
        double qb = b, qg = g, qr = r;
        int width = Vector<double>.Count;
        if (width > 1 && colorCount >= width)
        {
            var vb = new Vector<double>(qb);
            var vg = new Vector<double>(qg);
            var vr = new Vector<double>(qr);

            int i = 0;
            int vectorizableCount = colorCount - (colorCount % width);
            for (; i < vectorizableCount; i += width)
            {
                var pb = new Vector<double>(_paletteB, i);
                var pg = new Vector<double>(_paletteG, i);
                var pr = new Vector<double>(_paletteR, i);

                var rmean = (vr + pr) * 0.5;
                var dr = vr - pr;
                var dg = vg - pg;
                var db = vb - pb;

                var dist = (Two + rmean * InvTwoFiveSix) * dr * dr
                         + Four * dg * dg
                         + (Two + (TwoFiveFive - rmean) * InvTwoFiveSix) * db * db;

                dist.CopyTo(_distScratch, i);
            }
            for (; i < colorCount; i++)
            {
                _distScratch[i] = GifColorDistance.RedmeanSquared(b, g, r, (byte)_paletteB[i], (byte)_paletteG[i], (byte)_paletteR[i]);
            }
        }
        else
        {
            for (int i = 0; i < colorCount; i++)
            {
                _distScratch[i] = GifColorDistance.RedmeanSquared(b, g, r, (byte)_paletteB[i], (byte)_paletteG[i], (byte)_paletteR[i]);
            }
        }
    }
}
