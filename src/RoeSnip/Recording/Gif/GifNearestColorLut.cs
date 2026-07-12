using System;

namespace RoeSnip.Recording.Gif;

/// <summary>Maps a BGR888 color to its nearest entry in the CURRENT palette via a 32768-entry
/// (5 bits each of B, G, R) direct-indexed lookup table, filled lazily on miss instead of
/// eagerly rebuilt whenever the palette changes.
///
/// Eager rebuild would mean 32768 brute-force nearest-color searches (each up to 255 palette
/// comparisons) every time <see cref="Rebuild"/> runs, even though a typical changed-region frame
/// only ever queries a few thousand distinct 5-5-5 buckets out of the 32768 possible — most of
/// that work would be thrown away unused. Instead <see cref="Rebuild"/> is O(1): it just swaps in
/// the new palette reference and bumps <see cref="_version"/>. Every bucket carries its own
/// version stamp; a lookup whose stamp doesn't match the current version is stale (built against
/// an earlier palette, or never built) and gets refilled on the spot, then stamped current. No
/// array is ever cleared or reallocated between palettes — both the LUT and the stamp array are
/// permanent, instance-lifetime buffers, which is what keeps this allocation-free at recording
/// cadence.
///
/// A bucket's fill uses the BUCKET'S OWN CENTER color, not whichever exact query color happened to
/// trigger the fill (see <see cref="FillBucket"/>) — 5-5-5 already means every color sharing a
/// bucket is treated as equivalent, so the cached answer must be a function of the bucket alone.
/// Otherwise two pixels landing in the same bucket in different frames (or the same frame, in a
/// different order) could resolve to different palette entries depending purely on which one was
/// queried first, which would show up as a repaint-loop repainting non-drifting content.</summary>
public sealed class GifNearestColorLut
{
    private const int BucketCount = 32768; // 2^15 = 5 bits B, 5 bits G, 5 bits R

    private readonly short[] _nearest = new short[BucketCount];
    private readonly int[] _version = new int[BucketCount];
    private int _currentVersion;

    private byte[] _paletteBgr = Array.Empty<byte>();
    private int _colorCount;

    /// <summary>Swaps in a new (or reused, if the caller intentionally kept the same array —
    /// see the reuse-first fast path in the encoder) palette. O(1): does not touch the LUT itself,
    /// only invalidates it by making every existing bucket stamp stale.</summary>
    public void Rebuild(byte[] paletteBgr, int colorCount)
    {
        _paletteBgr = paletteBgr;
        _colorCount = colorCount;
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
            FillBucket(bucket);
        }
        return _nearest[bucket];
    }

    private void FillBucket(int bucket)
    {
        // Reconstruct the bucket's center color (mid-point of the 8 values each dropped 3 bits
        // span) rather than using the triggering query's exact color — see the class doc comment.
        byte b = (byte)(((bucket >> 10) & 0x1F) << 3 | 0x04);
        byte g = (byte)(((bucket >> 5) & 0x1F) << 3 | 0x04);
        byte r = (byte)((bucket & 0x1F) << 3 | 0x04);

        int best = 0;
        double bestDist = double.MaxValue;
        for (int p = 0; p < _colorCount; p++)
        {
            int o = p * 3;
            double dist = GifColorDistance.RedmeanSquared(b, g, r, _paletteBgr[o], _paletteBgr[o + 1], _paletteBgr[o + 2]);
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
}
