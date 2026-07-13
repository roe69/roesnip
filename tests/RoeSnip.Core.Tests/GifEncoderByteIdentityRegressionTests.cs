using System;
using System.IO;
using System.Security.Cryptography;
using RoeSnip.Core.Imaging;
using RoeSnip.Core.Recording;
using RoeSnip.Core.Recording.Gif;
using Xunit;

namespace RoeSnip.Core.Tests;

/// <summary>GIF software-encode CPU workstream: locks the exact on-disk bytes GifEncoder produces
/// for a fixed, deterministic input to a hardcoded SHA-256 hash, specifically so
/// <see cref="GifNearestColorLut"/>'s SIMD-vectorized <c>FillBucket</c> rewrite (see that class's own
/// PERFORMANCE doc-comment note) can be verified byte-for-byte equivalent to the original scalar
/// brute-force scan it replaced, not just "close enough" or "passes the size-ratio benchmarks".
///
/// Every hash below was captured from the PRE-optimization scalar implementation (a checkout of
/// this same file from origin/main, built and run standalone, then reverted) and re-verified
/// identical after the SIMD rewrite landed. A hash mismatch here means GifEncoder's output changed for SOME
/// input, which — given every other GIF byte-shape test in this suite (GifSizeBenchmarkTests'
/// decode-verified size gates, GifEncoderTests' streaming lifecycle, GifQuantizerAndLzwTests'
/// component-level brute-force cross-checks) could all still pass despite that — is exactly the class
/// of regression those tests are not positioned to catch on their own.
///
/// Content mix deliberately exercises every <see cref="GifNearestColorLut"/> path the SIMD rewrite
/// touches: full-motion noise (the worst case that motivated the optimization — palette invalidates
/// almost every frame, so nearly every FillBucket call is a genuine miss) at both Quality (255-color
/// palette, well past one SIMD vector width) and Minimal (16-color palette, likely under one vector
/// width on most hardware, exercising the scalar remainder/fallback path) presets, plus a tiny
/// 2-frame take (palette sizes 1 and a handful) to hit the near-empty-palette edge the main sweep
/// might skip past.</summary>
public class GifEncoderByteIdentityRegressionTests
{
    private const int Width = 64;
    private const int Height = 48;
    private const long TicksPerSecond = 1000;

    private static string TempGifPath(string name) => Path.Combine(Path.GetTempPath(), $"gifhash_{name}_{Guid.NewGuid():N}.gif");

    private static uint XorShift32(uint state)
    {
        state ^= state << 13;
        state ^= state >> 17;
        state ^= state << 5;
        return state;
    }

    /// <summary>Same deterministic full-canvas noise generator shape as GifSizeBenchmarkTests'
    /// NoiseFrame (own xorshift32 copy — see that class's own doc comment for why duplicating this
    /// five-line pure function beats a cross-test-class dependency), at this file's smaller canvas.</summary>
    private static SdrImage NoiseFrame(int frameIndex)
    {
        var pixels = new byte[Width * 4 * Height];
        uint rng = 0x9E3779B9u ^ (uint)(frameIndex * 2654435761u + 1);
        if (rng == 0)
        {
            rng = 1;
        }
        for (int i = 0; i < pixels.Length; i += 4)
        {
            rng = XorShift32(rng);
            pixels[i + 0] = (byte)rng;
            pixels[i + 1] = (byte)(rng >> 8);
            pixels[i + 2] = (byte)(rng >> 16);
            pixels[i + 3] = 255;
        }
        return new SdrImage(Width, Height, pixels);
    }

    private static string EncodeAndHash(string name, GifSizePreset preset, int fps, int frameCount)
    {
        var options = GifSizePresets.ForPreset(preset);
        long frameIntervalTicks = TicksPerSecond / fps;
        string path = TempGifPath(name);
        try
        {
            var encoder = GifEncoder.Create(path, Width, Height, timestampTicksPerSecond: TicksPerSecond, options: options);
            for (int i = 0; i < frameCount; i++)
            {
                long tick = i * frameIntervalTicks;
                encoder.AddFrame(NoiseFrame(i), tick);
            }
            encoder.FinalizeAndClose(frameCount * frameIntervalTicks);

            byte[] bytes = File.ReadAllBytes(path);
            return Convert.ToHexString(SHA256.HashData(bytes));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Quality_FullMotionNoise_ProducesExactHistoricalBytes()
    {
        // 255-color palette, well past one SIMD vector width — the main vectorized loop in
        // GifNearestColorLut.ComputeDistances runs for the bulk of every FillBucket call here.
        string hash = EncodeAndHash("quality_noise", GifSizePreset.Quality, fps: 25, frameCount: 30);
        Assert.Equal("16504F28341E95A6144BBE23680028FF0F711E204D6D892E3F636A71C8BE55E9", hash);
    }

    [Fact]
    public void Max_FullMotionNoise_ProducesExactHistoricalBytes()
    {
        // Max's ChannelTolerance=0 / PaletteReuseErrorThreshold=0 makes this the strictest,
        // most-frequently-rebuilding palette path of any preset.
        string hash = EncodeAndHash("max_noise", GifSizePreset.Max, fps: 50, frameCount: 30);
        Assert.Equal("1A18E2D55BB31CD8BC2C0D5C01701E0A1EDD6B63C84EF53B6B5E08A6A91FDD0E", hash);
    }

    [Fact]
    public void Minimal_FullMotionNoise_ProducesExactHistoricalBytes()
    {
        // 16-color palette — likely under one SIMD vector width on typical x64 (Vector<double>.Count
        // is 2 or 4), so this is the case most likely to only ever exercise
        // ComputeDistances' scalar fallback/remainder path rather than the vectorized main loop.
        string hash = EncodeAndHash("minimal_noise", GifSizePreset.Minimal, fps: 25, frameCount: 30);
        Assert.Equal("57AC11490BB3065095FC657A36DC8519E6C77D52AA11C2CDFAA492941FA543BF", hash);
    }

    [Fact]
    public void Quality_TinyTwoFrameTake_ProducesExactHistoricalBytes()
    {
        // Edge case: a first-frame keyframe (full 64x48 canvas, palette built from scratch) followed
        // by exactly one delta frame — the near-smallest possible palette-size/frame-count
        // combination, deliberately separate from the 30-frame sweeps above.
        string hash = EncodeAndHash("quality_tiny", GifSizePreset.Quality, fps: 10, frameCount: 2);
        Assert.Equal("C82684E17A5976A8730E975B15CBF065E86135FEA6315E31159438CFBF36A1F0", hash);
    }
}
