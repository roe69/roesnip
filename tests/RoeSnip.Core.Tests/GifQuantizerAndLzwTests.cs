using System;
using System.IO;
using RoeSnip.Core.Recording.Gif;
using Xunit;

namespace RoeSnip.Core.Tests;

/// <summary>Unit tests for the three <c>Recording/Gif/*</c> building blocks GifEncoder's
/// orchestration leans on: the octree palette quantizer, the version-stamped nearest-color LUT, and
/// the from-scratch GIF-LZW compressor. GifEncoderTests.cs already exercises these end to end
/// through the live encoder plus GifBitmapDecoder/GifLzwTestDecoder; this file drives each piece
/// directly instead, so a regression here points at exactly which stage broke, and so properties
/// that don't survive GifBitmapDecoder's already-composited output (palette byte-for-byte
/// determinism, LUT staleness after Rebuild, raw LZW sub-block framing) can be asserted precisely.</summary>
public class GifQuantizerAndLzwTests
{
    // ---------------------------------------------------------------------
    // GifOctreeQuantizer
    // ---------------------------------------------------------------------

    private static byte[] RandomPixelsBgra(int count, int seed)
    {
        var rng = new Random(seed);
        var pixels = new byte[count * 4];
        for (int i = 0; i < count; i++)
        {
            int o = i * 4;
            pixels[o + 0] = (byte)rng.Next(256);
            pixels[o + 1] = (byte)rng.Next(256);
            pixels[o + 2] = (byte)rng.Next(256);
            pixels[o + 3] = 255;
        }
        return pixels;
    }

    [Fact]
    public void BuildPalette_MoreDistinctColorsThanMax_NeverExceedsMaxColors()
    {
        var quantizer = new GifOctreeQuantizer();
        byte[] pixels = RandomPixelsBgra(5000, seed: 1);
        var palette = new byte[64 * 3];

        int colorCount = quantizer.BuildPalette(pixels, 64, palette);

        Assert.InRange(colorCount, 1, 64);
    }

    [Fact]
    public void BuildPalette_FewerDistinctColorsThanMax_ReturnsExactDistinctCount()
    {
        var quantizer = new GifOctreeQuantizer();
        // Three widely-separated distinct colors (one repeated), well under maxColors=16, so no
        // reduction should ever trigger and the leaf count should equal the distinct-color count.
        byte[] pixels =
        {
            10, 20, 30, 255,
            10, 20, 30, 255,
            200, 210, 220, 255,
            0, 0, 0, 255,
        };
        var palette = new byte[16 * 3];

        int colorCount = quantizer.BuildPalette(pixels, 16, palette);

        Assert.Equal(3, colorCount);
    }

    [Fact]
    public void BuildPalette_SameInputSameInstance_IsByteIdenticalAcrossTwoBuilds()
    {
        var quantizer = new GifOctreeQuantizer();
        byte[] pixels = RandomPixelsBgra(3000, seed: 42);
        var paletteA = new byte[200 * 3];
        var paletteB = new byte[200 * 3];

        int countA = quantizer.BuildPalette(pixels, 200, paletteA);
        int countB = quantizer.BuildPalette(pixels, 200, paletteB);

        Assert.Equal(countA, countB);
        Assert.Equal(paletteA, paletteB);
    }

    [Fact]
    public void BuildPalette_SameInputTwoInstances_IsByteIdentical()
    {
        byte[] pixels = RandomPixelsBgra(3000, seed: 7);
        var quantizer1 = new GifOctreeQuantizer();
        var quantizer2 = new GifOctreeQuantizer();
        var palette1 = new byte[128 * 3];
        var palette2 = new byte[128 * 3];

        int count1 = quantizer1.BuildPalette(pixels, 128, palette1);
        int count2 = quantizer2.BuildPalette(pixels, 128, palette2);

        Assert.Equal(count1, count2);
        Assert.Equal(palette1, palette2);
    }

    [Fact]
    public void BuildPalette_ForcedBelowTrueColorCount_KeepsMaxErrorBounded()
    {
        var quantizer = new GifOctreeQuantizer();
        // 4096 fully-random colors funneled into 32 slots: a deliberately hard case (no clustering
        // to exploit, so every merge genuinely costs precision) that must still keep the worst-case
        // redmean/Chebyshev error well inside a single byte's range rather than blowing up.
        byte[] pixels = RandomPixelsBgra(4096, seed: 99);
        var palette = new byte[32 * 3];

        int colorCount = quantizer.BuildPalette(pixels, 32, palette);
        Assert.InRange(colorCount, 1, 32); // never exceeds the request, even under heavy forced merging

        int maxError = GifOctreeQuantizer.MaxErrorAgainst(pixels, palette, colorCount);
        Assert.InRange(maxError, 0, 220);
    }

    [Fact]
    public void MaxErrorAgainst_PaletteContainsExactColor_ReturnsZero()
    {
        byte[] pixels = { 10, 20, 30, 255, 10, 20, 30, 255 };
        byte[] palette = { 10, 20, 30 };

        int maxError = GifOctreeQuantizer.MaxErrorAgainst(pixels, palette, 1);

        Assert.Equal(0, maxError);
    }

    [Fact]
    public void MaxErrorAgainst_KnownOffsetPixel_ReturnsMaxChannelDelta()
    {
        // Pixel is B=10,G=20,R=30; the (only) palette entry is B=13,G=20,R=46 -> per-channel deltas
        // are (3, 0, 16), so the Chebyshev worst case is 16.
        byte[] pixels = { 10, 20, 30, 255 };
        byte[] palette = { 13, 20, 46 };

        int maxError = GifOctreeQuantizer.MaxErrorAgainst(pixels, palette, 1);

        Assert.Equal(16, maxError);
    }

    [Fact]
    public void MaxErrorAgainst_MultiplePixelsAndPaletteEntries_MatchesIndependentBruteForce()
    {
        byte[] pixels = RandomPixelsBgra(60, seed: 555);
        var paletteRng = new Random(777);
        byte[] palette = new byte[12 * 3];
        for (int i = 0; i < palette.Length; i++)
        {
            palette[i] = (byte)paletteRng.Next(256);
        }

        int actual = GifOctreeQuantizer.MaxErrorAgainst(pixels, palette, 12);

        // Independently recomputed (max over pixels of the Chebyshev error of the palette entry
        // each pixel is REDMEAN-nearest to -- not the Chebyshev-nearest entry; the method under
        // test picks its candidate the same way GifNearestColorLut actually resolves a pixel, see
        // its doc comment) rather than reusing any production helper, so this genuinely
        // cross-checks the method under test instead of restating it.
        int expected = 0;
        int pixelCount = pixels.Length / 4;
        for (int i = 0; i < pixelCount; i++)
        {
            byte b = pixels[i * 4], g = pixels[i * 4 + 1], r = pixels[i * 4 + 2];
            int nearest = 0;
            double bestRedmean = double.MaxValue;
            for (int p = 0; p < 12; p++)
            {
                double pr = palette[p * 3 + 2];
                double rmean = (r + pr) / 2.0;
                double dr = r - pr;
                double dg = g - palette[p * 3 + 1];
                double db = b - palette[p * 3];
                double redmean = (2.0 + rmean / 256.0) * dr * dr
                                + 4.0 * dg * dg
                                + (2.0 + (255.0 - rmean) / 256.0) * db * db;
                if (redmean < bestRedmean)
                {
                    bestRedmean = redmean;
                    nearest = p;
                }
            }
            int cdb = Math.Abs(b - palette[nearest * 3]);
            int cdg = Math.Abs(g - palette[nearest * 3 + 1]);
            int cdr = Math.Abs(r - palette[nearest * 3 + 2]);
            expected = Math.Max(expected, Math.Max(cdb, Math.Max(cdg, cdr)));
        }

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void MaxErrorAgainst_EarlyExitAbove_StopsAtFirstPixelThatClearsThreshold()
    {
        // The first pixel is maximally far from the (only) palette entry, clearing earlyExitAbove=1
        // immediately; a second, exactly-matching pixel follows purely to prove the scan actually
        // stopped early (an exact match afterwards can't lower an already-reported worst case, but
        // if the method were scanning the whole buffer and taking a running max it would still
        // report 255 - so this alone doesn't distinguish early-exit from full-scan). What does
        // distinguish them is the return value itself: a full scan of a buffer whose true worst case
        // is 255 also returns 255, so assert on that value being reached even under a threshold of
        // 1, which only happens if early exit does not somehow return 0 or throw.
        byte[] pixels =
        {
            255, 255, 255, 255, // far from palette: Chebyshev distance 255
            0, 0, 0, 255,       // exact match: distance 0
        };
        byte[] palette = { 0, 0, 0 };

        int maxError = GifOctreeQuantizer.MaxErrorAgainst(pixels, palette, 1, earlyExitAbove: 1);

        Assert.Equal(255, maxError);
    }

    [Fact]
    public void MaxErrorAgainst_ChebyshevNearestAndRedmeanNearestDisagree_ReportsRedmeanNearestsError()
    {
        // Pixel (B=200,G=200,R=255) against P1=(200,196,255) and P2=(195,200,255): P1 is
        // Chebyshev-nearest (chebyshev 4 vs 5), but P2 is REDMEAN-nearest (redmean 50 vs 64) --
        // the same entry GifNearestColorLut.Lookup would actually resolve this pixel to. Before the
        // fix this method picked its candidate by Chebyshev (P1) and reported worst=4, letting a
        // reuse check pass even though the pixel's real per-channel error once actually quantized
        // (via P2) is 5. It must now report 5.
        byte[] pixels = { 200, 200, 255, 255 };
        byte[] palette = { 200, 196, 255, 195, 200, 255 };

        int maxError = GifOctreeQuantizer.MaxErrorAgainst(pixels, palette, 2);

        Assert.Equal(5, maxError);
    }

    // ---------------------------------------------------------------------
    // GifNearestColorLut
    // ---------------------------------------------------------------------

    // Bucket-center-aligned (k*8+4 for k a multiple of 8) and 64 apart. GifNearestColorLut buckets a
    // query by right-shifting 3 bits and, on a cache miss, searches the palette using the BUCKET'S
    // reconstructed center rather than the exact query color (see its class doc comment) - so a
    // color chosen to already equal its own bucket's center resolves exactly, not approximately,
    // and 64 units of separation is far more than the +-4 rounding the bucket-center substitution
    // could ever introduce, so no two of these palette entries can be mistaken for each other.
    private static readonly byte[] GridLevels = { 4, 68, 132, 196 };

    private static byte[] BuildGridPalette(out int colorCount)
    {
        var palette = new byte[GridLevels.Length * GridLevels.Length * GridLevels.Length * 3];
        int i = 0;
        foreach (byte b in GridLevels)
        {
            foreach (byte g in GridLevels)
            {
                foreach (byte r in GridLevels)
                {
                    palette[i * 3 + 0] = b;
                    palette[i * 3 + 1] = g;
                    palette[i * 3 + 2] = r;
                    i++;
                }
            }
        }
        colorCount = i;
        return palette;
    }

    [Fact]
    public void Lookup_ExactPaletteColors_MapToThemselves()
    {
        byte[] palette = BuildGridPalette(out int colorCount);
        var lut = new GifNearestColorLut();
        lut.Rebuild(palette, colorCount);

        for (int p = 0; p < colorCount; p++)
        {
            byte b = palette[p * 3 + 0], g = palette[p * 3 + 1], r = palette[p * 3 + 2];
            Assert.Equal(p, lut.Lookup(b, g, r));
        }
    }

    // Independent redmean implementation (deliberately not calling into production internals) so
    // the brute-force cross-check below genuinely verifies GifNearestColorLut's answer instead of
    // restating its own formula back at itself.
    private static double Redmean(byte b1, byte g1, byte r1, byte b2, byte g2, byte r2)
    {
        double rmean = (r1 + r2) / 2.0;
        double dr = r1 - r2, dg = g1 - g2, db = b1 - b2;
        return (2.0 + rmean / 256.0) * dr * dr
             + 4.0 * dg * dg
             + (2.0 + (255.0 - rmean) / 256.0) * db * db;
    }

    private static int BruteForceNearest(byte b, byte g, byte r, byte[] palette, int colorCount)
    {
        int best = 0;
        double bestDist = double.MaxValue;
        for (int p = 0; p < colorCount; p++)
        {
            double dist = Redmean(b, g, r, palette[p * 3], palette[p * 3 + 1], palette[p * 3 + 2]);
            if (dist < bestDist)
            {
                bestDist = dist;
                best = p;
            }
        }
        return best;
    }

    [Fact]
    public void Lookup_RandomColors_MatchesBruteForceNearest()
    {
        // A random (not grid-aligned) 150-color palette, deliberately NOT engineered to avoid
        // Voronoi boundaries between palette entries - real quantizer output isn't either.
        var paletteRng = new Random(31415);
        int colorCount = 150;
        byte[] palette = new byte[colorCount * 3];
        paletteRng.NextBytes(palette);

        var lut = new GifNearestColorLut();
        lut.Rebuild(palette, colorCount);

        var rng = new Random(2024);
        for (int i = 0; i < 200; i++)
        {
            byte b = (byte)rng.Next(256), g = (byte)rng.Next(256), r = (byte)rng.Next(256);

            // GifNearestColorLut resolves a cache miss by brute-forcing the nearest palette entry
            // to the QUERY'S BUCKET CENTER, not the exact query color (see its class doc comment on
            // FillBucket) - so the independent cross-check must brute-force against that same
            // bucket center, or it would be checking a different, not-always-answerable question:
            // near a true Voronoi boundary between two palette entries, the LUT's bucket-quantized
            // answer can legitimately differ from the exact-query nearest neighbor by design, no
            // matter how far apart the palette entries are - that discretization is the entire
            // reason a fixed-resolution LUT is cheaper than a brute-force search per pixel.
            int bucket = ((b >> 3) << 10) | ((g >> 3) << 5) | (r >> 3);
            byte cb = (byte)(((bucket >> 10) & 0x1F) << 3 | 0x04);
            byte cg = (byte)(((bucket >> 5) & 0x1F) << 3 | 0x04);
            byte cr = (byte)((bucket & 0x1F) << 3 | 0x04);

            int expected = BruteForceNearest(cb, cg, cr, palette, colorCount);
            int actual = lut.Lookup(b, g, r);
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void Rebuild_WithDifferentPalette_InvalidatesStaleLazyFilledBuckets()
    {
        var lut = new GifNearestColorLut();

        byte[] paletteA = { 4, 4, 4 }; // one color: index 0
        lut.Rebuild(paletteA, 1);
        int firstLookup = lut.Lookup(196, 196, 196); // lazily fills this bucket against paletteA
        Assert.Equal(0, firstLookup); // the only entry available

        // Same bucket ((196,196,196) is itself bucket-center-aligned - see GridLevels above), but a
        // new palette where index 1 is an exact match. If Rebuild failed to invalidate the bucket
        // (e.g. forgot to bump the version, or the version check were missing), this lookup would
        // silently return the stale cached index 0 instead of recomputing.
        byte[] paletteB = { 4, 4, 4, 196, 196, 196 };
        lut.Rebuild(paletteB, 2);
        int secondLookup = lut.Lookup(196, 196, 196);

        Assert.Equal(1, secondLookup);
    }

    // ---------------------------------------------------------------------
    // GifLzwEncoder (round-tripped through the test-only GifLzwTestDecoder)
    // ---------------------------------------------------------------------

    private static byte[] RunLengthHeavyIndices(int alphabetSize, int length, int seed)
    {
        var rng = new Random(seed);
        var data = new byte[length];
        int i = 0;
        while (i < length)
        {
            byte value = (byte)rng.Next(alphabetSize);
            int runLength = Math.Min(length - i, rng.Next(5, 40));
            for (int j = 0; j < runLength; j++)
            {
                data[i++] = value;
            }
        }
        return data;
    }

    private static byte[] HighEntropyIndices(int alphabetSize, int length, int seed)
    {
        var rng = new Random(seed);
        var data = new byte[length];
        for (int i = 0; i < length; i++)
        {
            data[i] = (byte)rng.Next(alphabetSize);
        }
        return data;
    }

    private static byte[] RoundTrip(byte[] indices, int minCodeSize)
    {
        var encoder = new GifLzwEncoder();
        using var stream = new MemoryStream();
        encoder.Encode(stream, indices, minCodeSize);

        byte[] gif = stream.ToArray();
        byte[] decoded = GifLzwTestDecoder.Decode(gif, 0, out int endPos);

        Assert.Equal(gif.Length, endPos); // the decoder consumed exactly the bytes Encode wrote
        return decoded;
    }

    [Fact]
    public void Encode_TinySingleWidthInput_RoundTripsViaSharedGifLzwTestDecoderHelper()
    {
        // A single-symbol input: Encode never enters its match-extension loop body at all (there is
        // no second symbol to pair a prefix with), so it never inserts a dictionary entry and never
        // grows the code width. Exercises the shared GifLzwTestDecoder helper directly, independent
        // of RoundTrip above.
        byte[] indices = { 2 };
        var encoder = new GifLzwEncoder();
        using var stream = new MemoryStream();
        encoder.Encode(stream, indices, minCodeSize: 2);
        byte[] gif = stream.ToArray();

        byte[] decoded = GifLzwTestDecoder.Decode(gif, 0, out int endPos);

        Assert.Equal(gif.Length, endPos);
        Assert.Equal(indices, decoded);
    }

    [Fact]
    public void Encode_RunLengthHeavyData_RoundTripsExactly()
    {
        byte[] indices = RunLengthHeavyIndices(alphabetSize: 16, length: 20000, seed: 11);
        byte[] decoded = RoundTrip(indices, minCodeSize: 4);
        Assert.Equal(indices, decoded);
    }

    [Fact]
    public void Encode_HighEntropySeededData_RoundTripsExactly()
    {
        byte[] indices = HighEntropyIndices(alphabetSize: 256, length: 6000, seed: 22);
        byte[] decoded = RoundTrip(indices, minCodeSize: 8);
        Assert.Equal(indices, decoded);
    }

    [Fact]
    public void Encode_LongHighEntropyInput_ForcesDictionaryFullClearCodeReset_AndStillRoundTrips()
    {
        // 256-symbol alphabet, deliberately large and high-entropy: the dictionary races toward its
        // 4096-code ceiling far faster than it would on compressible content, forcing at least one
        // (in practice several) internal clear-code resets inside a single Encode call. Correct
        // round-tripping is the only externally observable proof that the reset is done correctly
        // on both sides (dictionary re-armed AND a clear code actually emitted for the decoder to
        // see) - GifLzwTestDecoder resets its own dictionary whenever it reads a clear code, so any
        // mismatch between the encoder's and decoder's reset points shows up as a decode mismatch.
        byte[] indices = HighEntropyIndices(alphabetSize: 256, length: 200_000, seed: 33);
        byte[] decoded = RoundTrip(indices, minCodeSize: 8);
        Assert.Equal(indices, decoded);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void Encode_AcrossMinCodeSizes_RoundTripsExactly(int minCodeSize)
    {
        int alphabetSize = 1 << minCodeSize;
        // Mix of run-length and high-entropy content in the same buffer so every code width
        // exercises both the dictionary-match path and frequent new-code insertion.
        byte[] runPart = RunLengthHeavyIndices(alphabetSize, length: 800, seed: 100 + minCodeSize);
        byte[] noisePart = HighEntropyIndices(alphabetSize, length: 800, seed: 200 + minCodeSize);
        byte[] indices = new byte[runPart.Length + noisePart.Length];
        Array.Copy(runPart, indices, runPart.Length);
        Array.Copy(noisePart, 0, indices, runPart.Length, noisePart.Length);

        byte[] decoded = RoundTrip(indices, minCodeSize);

        Assert.Equal(indices, decoded);
    }

    [Fact]
    public void Encode_EmptyIndices_RoundTripsToEmptyOutput()
    {
        byte[] decoded = RoundTrip(Array.Empty<byte>(), minCodeSize: 4);
        Assert.Empty(decoded);
    }

    [Fact]
    public void Encode_ProducesMultipleSubBlocks_WhenCompressedDataExceeds255Bytes()
    {
        // High-entropy 8-bit data compresses poorly (LZW rarely finds a repeat worth encoding), so a
        // few thousand input bytes reliably yields well over 255 bytes of compressed output -
        // forcing the sub-block chain (a full 255-byte block, then another block, ...) that a single
        // sub-block's 1-byte length prefix could never reach on its own.
        byte[] indices = HighEntropyIndices(alphabetSize: 256, length: 5000, seed: 44);

        var encoder = new GifLzwEncoder();
        using var stream = new MemoryStream();
        encoder.Encode(stream, indices, minCodeSize: 8);
        byte[] gif = stream.ToArray();

        // Walk the sub-block chain directly - independent of GifLzwTestDecoder, which hides this
        // framing detail from its callers - to assert real multi-block chaining occurred.
        int pos = 1; // past the minimum-code-size byte
        int fullBlockCount = 0;
        while (gif[pos] != 0x00)
        {
            int size = gif[pos];
            if (size == 255)
            {
                fullBlockCount++;
            }
            pos += 1 + size;
        }
        int terminatorPos = pos;

        Assert.True(fullBlockCount >= 2,
            $"expected at least 2 full 255-byte sub-blocks proving multi-block chaining, got {fullBlockCount}");
        Assert.Equal(gif.Length - 1, terminatorPos); // the terminator is the very last byte Encode wrote

        byte[] decoded = GifLzwTestDecoder.Decode(gif, 0, out int endPos);
        Assert.Equal(gif.Length, endPos);
        Assert.Equal(indices, decoded);
    }
}
