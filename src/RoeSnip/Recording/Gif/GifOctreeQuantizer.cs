using System;
using System.Collections.Generic;

namespace RoeSnip.Recording.Gif;

/// <summary>Builds an up-to-N-color palette from a flat set of BGRA8 pixels via a classic octree
/// quantizer, with one deliberate deviation from the textbook version: when the tree must shed
/// leaves to fit under <c>maxColors</c>, the node chosen to collapse at the deepest reducible
/// level is the one whose children are perceptually CLOSEST together (least redmean-weighted
/// error introduced), not simply the oldest one — see <see cref="PopCheapestReducible"/>. This
/// keeps quantization error low without needing a second (slower) pass over the pixels.
///
/// Node storage is a fixed-capacity arena (parallel arrays, no per-node heap object, no children
/// array-of-arrays) that <see cref="BuildPalette"/> resets and reuses on every call — this runs
/// once per emitted GIF frame at recording cadence, so a fresh allocation per build would be
/// exactly the per-frame LOH/Gen2 pressure this codebase has already paid to eliminate elsewhere
/// (see GifEncoder's old EncodeAndWrite doc comment). <see cref="NodeCapacity"/> is sized generously
/// for real desktop content (a changed region's distinct-color count is bounded well under it in
/// practice); the rare pathological case (e.g. synthetic full-frame noise) degrades gracefully by
/// forcing early leaves instead of growing the arena — see <see cref="Insert"/>.
///
/// Insertion order is whatever order the caller supplies pixels in, and every reduction decision
/// is a deterministic function of the tree built so far (no randomness, no dictionary iteration
/// order) — identical input therefore always yields a byte-identical palette, which is what lets
/// the palette-reuse fast path (<see cref="MaxErrorAgainst"/>) and any golden-output tests compare
/// exact bytes.</summary>
public sealed class GifOctreeQuantizer
{
    // 8 levels: bit 7 (MSB) of each of R/G/B down to bit 0 — full 24-bit precision at the leaves.
    private const int MaxDepth = 8;
    // "A few thousand nodes" per the design spec — real screenshot content (UI chrome, text,
    // cursors) has nowhere near this many distinct colors in a typical changed region; a
    // synthetic worst case just triggers the graceful degradation in Insert instead of growing.
    private const int NodeCapacity = 4096;

    // Parallel arrays instead of a node class/struct-array-of-arrays: every field for node i lives
    // at index i in its own array, and _childIndex packs each node's 8 child slots contiguously.
    // Allocated once for the life of this instance; Reset() only rewinds counters, never resizes.
    private readonly long[] _pixelCount = new long[NodeCapacity];
    private readonly long[] _sumB = new long[NodeCapacity];
    private readonly long[] _sumG = new long[NodeCapacity];
    private readonly long[] _sumR = new long[NodeCapacity];
    private readonly byte[] _level = new byte[NodeCapacity];
    private readonly byte[] _childCount = new byte[NodeCapacity];
    private readonly bool[] _isLeaf = new bool[NodeCapacity];
    private readonly int[] _childIndex = new int[NodeCapacity * 8];

    // reducible[L] holds every node at level L that currently has at least one child — i.e. every
    // candidate MergeChildren can collapse back into a leaf. A node is pushed here exactly once,
    // the moment it gets its first child (see Insert), and popped exactly once when it is chosen
    // for reduction (see PopCheapestReducible). Sized once, Cleared (not reallocated) by Reset.
    private readonly List<int>[] _reducible;

    private int _nodeCount;
    private int _leafCount;
    private int _root;

    public GifOctreeQuantizer()
    {
        _reducible = new List<int>[MaxDepth];
        for (int i = 0; i < MaxDepth; i++)
        {
            _reducible[i] = new List<int>();
        }
    }

    /// <summary>Quantizes <paramref name="paintedPixelsBgra"/> (a dense span of B,G,R,A quads —
    /// alpha is ignored, callers pass only the pixels that are actually being painted this frame,
    /// never the whole canvas) down to at most <paramref name="maxColors"/> colors, writing the
    /// result into <paramref name="paletteBgrOut"/> (caller-owned, at least
    /// <paramref name="maxColors"/> * 3 bytes — an output parameter rather than a return value
    /// specifically so callers can reuse one fixed-size buffer across frames instead of allocating
    /// a fresh palette array every time) as B,G,R triples in a fixed, deterministic order. Returns
    /// the number of distinct colors actually produced (0 for an empty input, otherwise between 1
    /// and <paramref name="maxColors"/>).</summary>
    public int BuildPalette(ReadOnlySpan<byte> paintedPixelsBgra, int maxColors, byte[] paletteBgrOut)
    {
        if (maxColors < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxColors), "Must request at least 1 color.");
        }
        if (paletteBgrOut.Length < maxColors * 3)
        {
            throw new ArgumentException("Output buffer is smaller than maxColors * 3 bytes.", nameof(paletteBgrOut));
        }

        Reset();

        int pixelCount = paintedPixelsBgra.Length / 4;
        for (int i = 0; i < pixelCount; i++)
        {
            int o = i * 4;
            Insert(paintedPixelsBgra[o], paintedPixelsBgra[o + 1], paintedPixelsBgra[o + 2]);
            if (_leafCount > maxColors)
            {
                ReduceUntil(maxColors);
            }
        }

        int outIndex = 0;
        VisitLeaf(_root, paletteBgrOut, ref outIndex, maxColors);
        return outIndex;
    }

    /// <summary>Reuse-check helper: the worst-case (max over all pixels) per-channel (Chebyshev)
    /// error against whichever palette entry each pixel would ACTUALLY be quantized to. "Actually
    /// be quantized to" means nearest by the same redmean distance <see cref="GifNearestColorLut"/>
    /// resolves lookups with, NOT the palette entry that happens to be Chebyshev-nearest — picking
    /// the selection candidate by Chebyshev here would let this method approve a reuse where the
    /// pixel's real per-channel error, once <see cref="GifNearestColorLut.Lookup"/> actually
    /// resolves it, quietly exceeds the very threshold this check exists to enforce (two
    /// close-but-different palette entries can rank in the opposite order under the two metrics).
    /// The error itself is still measured Chebyshev/max-channel once the entry is chosen — that
    /// mirrors the plain per-channel tolerance the diff/dither logic uses elsewhere, so "palette
    /// still fits" means the same thing as "pixel still fits". Brute-forces the (<=255-entry)
    /// palette per pixel — this runs once per candidate frame, not per pixel of a hot inner loop, so
    /// that's cheap enough. <paramref name="earlyExitAbove"/> lets a caller that only cares "is this
    /// <= threshold?" bail out the moment the running worst case clears it, without scanning the
    /// rest of the frame; pass 256 (the default) to force a full, exact scan.</summary>
    public static int MaxErrorAgainst(ReadOnlySpan<byte> paintedPixelsBgra, ReadOnlySpan<byte> paletteBgr, int colorCount, int earlyExitAbove = 256)
    {
        int worst = 0;
        int pixelCount = paintedPixelsBgra.Length / 4;
        for (int i = 0; i < pixelCount; i++)
        {
            int o = i * 4;
            byte b = paintedPixelsBgra[o];
            byte g = paintedPixelsBgra[o + 1];
            byte r = paintedPixelsBgra[o + 2];

            int nearest = 0;
            double bestRedmean = double.MaxValue;
            for (int p = 0; p < colorCount; p++)
            {
                int po = p * 3;
                double redmean = GifColorDistance.RedmeanSquared(b, g, r, paletteBgr[po], paletteBgr[po + 1], paletteBgr[po + 2]);
                if (redmean < bestRedmean)
                {
                    bestRedmean = redmean;
                    nearest = p;
                    if (redmean == 0)
                    {
                        break;
                    }
                }
            }

            int npo = nearest * 3;
            int db = Math.Abs(b - paletteBgr[npo]);
            int dg = Math.Abs(g - paletteBgr[npo + 1]);
            int dr = Math.Abs(r - paletteBgr[npo + 2]);
            int chebyshev = Math.Max(db, Math.Max(dg, dr));

            if (chebyshev > worst)
            {
                worst = chebyshev;
                if (worst >= earlyExitAbove)
                {
                    return worst;
                }
            }
        }
        return worst;
    }

    private void Reset()
    {
        _nodeCount = 0;
        _leafCount = 0;
        for (int i = 0; i < MaxDepth; i++)
        {
            _reducible[i].Clear();
        }
        _root = AllocNode(0, -1);
    }

    private int AllocNode(byte level, int parent)
    {
        if (_nodeCount >= NodeCapacity)
        {
            return -1;
        }
        int idx = _nodeCount++;
        _pixelCount[idx] = 0;
        _sumB[idx] = 0;
        _sumG[idx] = 0;
        _sumR[idx] = 0;
        _level[idx] = level;
        _childCount[idx] = 0;
        _isLeaf[idx] = false;
        int baseIdx = idx * 8;
        for (int k = 0; k < 8; k++)
        {
            _childIndex[baseIdx + k] = -1;
        }
        return idx;
    }

    /// <summary>Descends from the root by successive R/G/B bit triples (MSB first), creating child
    /// nodes on demand, until it either falls off a freshly-forced leaf (an earlier reduction, or
    /// this insert's own pool-exhaustion fallback) or reaches full 24-bit depth, then accumulates
    /// the pixel's color into whichever node it landed on.
    ///
    /// Pool-exhaustion fallback: if the arena is full and a needed child cannot be allocated, the
    /// current node is force-converted into a leaf right there instead of growing further — every
    /// future pixel that would have passed through it now stops one level early and blends into
    /// its bucket. This trades a little palette precision for never crashing or reallocating; it
    /// only engages on inputs pathological enough to exhaust <see cref="NodeCapacity"/> in the
    /// first place, which real screenshot content does not do.</summary>
    private void Insert(byte b, byte g, byte r)
    {
        int node = _root;
        int level;
        for (level = 0; level < MaxDepth; level++)
        {
            if (_isLeaf[node])
            {
                break;
            }

            int shift = 7 - level;
            int bit = (((r >> shift) & 1) << 2) | (((g >> shift) & 1) << 1) | ((b >> shift) & 1);
            int slot = node * 8 + bit;
            int child = _childIndex[slot];
            if (child < 0)
            {
                child = AllocNode((byte)(level + 1), node);
                if (child < 0)
                {
                    if (!_isLeaf[node])
                    {
                        _isLeaf[node] = true;
                        _leafCount++;
                    }
                    break;
                }

                _childIndex[slot] = child;
                if (_childCount[node]++ == 0)
                {
                    _reducible[_level[node]].Add(node);
                }

                if (level + 1 == MaxDepth)
                {
                    _isLeaf[child] = true;
                    _leafCount++;
                }
            }

            node = child;
        }

        _pixelCount[node]++;
        _sumB[node] += b;
        _sumG[node] += g;
        _sumR[node] += r;
    }

    /// <summary>Collapses nodes one at a time — always at the deepest level that still has any
    /// reducible node, so every child being merged is guaranteed to already be a leaf — until the
    /// leaf count fits under <paramref name="maxColors"/>.</summary>
    private void ReduceUntil(int maxColors)
    {
        while (_leafCount > maxColors)
        {
            int level = -1;
            for (int l = MaxDepth - 1; l >= 0; l--)
            {
                if (_reducible[l].Count > 0)
                {
                    level = l;
                    break;
                }
            }
            if (level < 0)
            {
                return; // nothing left to collapse (only possible if maxColors was <= 0, guarded above)
            }

            int node = PopCheapestReducible(level);
            MergeChildren(node);
        }
    }

    /// <summary>Picks, among all reducible nodes at <paramref name="level"/>, the one whose
    /// children are cheapest to merge — total pixel-weighted redmean-squared distance from each
    /// child's average color to what the merged node's average color would become. Merging the
    /// cheapest node first means every collapse the tree is forced to make loses the least
    /// perceptual information available at that depth.</summary>
    private int PopCheapestReducible(int level)
    {
        var candidates = _reducible[level];
        int bestPos = 0;
        double bestCost = double.MaxValue;

        for (int pos = 0; pos < candidates.Count; pos++)
        {
            double cost = MergeCost(candidates[pos]);
            if (cost < bestCost)
            {
                bestCost = cost;
                bestPos = pos;
            }
        }

        int node = candidates[bestPos];
        candidates.RemoveAt(bestPos);
        return node;
    }

    private double MergeCost(int node)
    {
        long sumB = _sumB[node], sumG = _sumG[node], sumR = _sumR[node], count = _pixelCount[node];
        int baseIdx = node * 8;
        for (int k = 0; k < 8; k++)
        {
            int c = _childIndex[baseIdx + k];
            if (c < 0)
            {
                continue;
            }
            sumB += _sumB[c];
            sumG += _sumG[c];
            sumR += _sumR[c];
            count += _pixelCount[c];
        }
        if (count == 0)
        {
            return 0;
        }
        byte mb = (byte)(sumB / count), mg = (byte)(sumG / count), mr = (byte)(sumR / count);

        double cost = 0;
        for (int k = 0; k < 8; k++)
        {
            int c = _childIndex[baseIdx + k];
            if (c < 0 || _pixelCount[c] == 0)
            {
                continue;
            }
            byte cb = (byte)(_sumB[c] / _pixelCount[c]);
            byte cg = (byte)(_sumG[c] / _pixelCount[c]);
            byte cr = (byte)(_sumR[c] / _pixelCount[c]);
            cost += _pixelCount[c] * GifColorDistance.RedmeanSquared(cb, cg, cr, mb, mg, mr);
        }
        return cost;
    }

    private void MergeChildren(int node)
    {
        int baseIdx = node * 8;
        long sumB = 0, sumG = 0, sumR = 0, count = 0;
        int childrenFound = 0;
        for (int k = 0; k < 8; k++)
        {
            int c = _childIndex[baseIdx + k];
            if (c < 0)
            {
                continue;
            }
            childrenFound++;
            sumB += _sumB[c];
            sumG += _sumG[c];
            sumR += _sumR[c];
            count += _pixelCount[c];
            _childIndex[baseIdx + k] = -1; // detach; the arena slot itself is never reclaimed
        }

        if (childrenFound == 0)
        {
            // Defensive only: a node only ever enters the reducible list once it has a child.
            _isLeaf[node] = true;
            return;
        }

        _sumB[node] += sumB;
        _sumG[node] += sumG;
        _sumR[node] += sumR;
        _pixelCount[node] += count;
        _isLeaf[node] = true;
        _leafCount -= (childrenFound - 1);
    }

    private void VisitLeaf(int node, byte[] paletteBgrOut, ref int outIndex, int maxColors)
    {
        if (_isLeaf[node])
        {
            if (_pixelCount[node] > 0 && outIndex < maxColors)
            {
                long c = _pixelCount[node];
                paletteBgrOut[outIndex * 3 + 0] = (byte)(_sumB[node] / c);
                paletteBgrOut[outIndex * 3 + 1] = (byte)(_sumG[node] / c);
                paletteBgrOut[outIndex * 3 + 2] = (byte)(_sumR[node] / c);
                outIndex++;
            }
            return;
        }

        int baseIdx = node * 8;
        for (int k = 0; k < 8; k++)
        {
            int c = _childIndex[baseIdx + k];
            if (c >= 0)
            {
                VisitLeaf(c, paletteBgrOut, ref outIndex, maxColors);
            }
        }
    }
}

/// <summary>Shared color-distance math for both palette building (<see cref="GifOctreeQuantizer"/>'s
/// merge-cost decisions) and nearest-color lookup (<see cref="GifNearestColorLut"/>'s cache-miss
/// fills) — one formula, so a palette built to minimize this distance is the same distance used to
/// map pixels onto it. Redmean weights the R/B channels by how bright red the pair of colors being
/// compared is (a cheap approximation of human luminance sensitivity that doesn't need a full color
/// space conversion); squared throughout because every call site only ever compares or sums
/// distances against each other, never needs the actual metric distance, so the sqrt is pure waste.</summary>
internal static class GifColorDistance
{
    public static double RedmeanSquared(byte b1, byte g1, byte r1, byte b2, byte g2, byte r2)
    {
        double rmean = (r1 + r2) / 2.0;
        double dr = r1 - r2;
        double dg = g1 - g2;
        double db = b1 - b2;
        return (2.0 + rmean / 256.0) * dr * dr
             + 4.0 * dg * dg
             + (2.0 + (255.0 - rmean) / 256.0) * db * db;
    }
}
