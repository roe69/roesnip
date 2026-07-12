using System;
using System.IO;
using System.Runtime.InteropServices;
using RoeSnip.Imaging;
using RoeSnip.Recording;
using RoeSnip.Recording.Gif;
using Vortice.MediaFoundation;
using Xunit;

namespace RoeSnip.Tests;

/// <summary>The pure bitrate heuristic behind Mp4Encoder.Create — no live MF SinkWriter needed.</summary>
public class Mp4EncoderTests
{
    [Fact]
    public void ComputeBitrate_TypicalSelection_IsWithinTheClampedBand()
    {
        long bitrate = Mp4Encoder.ComputeBitrate(1280, 720, 30);
        // 0.1 * 1280 * 720 * 30 = 2,764,800 — inside [2e6, 16e6], not clamped.
        Assert.Equal(2_764_800, bitrate);
    }

    [Fact]
    public void ComputeBitrate_TinySelection_ClampsToTheTwoMbpsFloor()
    {
        long bitrate = Mp4Encoder.ComputeBitrate(64, 64, 12);
        Assert.Equal(2_000_000, bitrate);
    }

    [Fact]
    public void ComputeBitrate_HugeSelection_ClampsToTheSixteenMbpsCeiling()
    {
        long bitrate = Mp4Encoder.ComputeBitrate(3840, 2160, 60);
        Assert.Equal(16_000_000, bitrate);
    }

    [Theory]
    [InlineData(1, 1, 1)]
    [InlineData(7680, 4320, 60)]
    public void ComputeBitrate_NeverEscapesTheClampedBand(int width, int height, int fps)
    {
        long bitrate = Mp4Encoder.ComputeBitrate(width, height, fps);
        Assert.InRange(bitrate, 2_000_000, 16_000_000);
    }

    // ---------- Recording-size-tiers overload: ComputeBitrate(w, h, fps, GifSizePreset) ----------

    [Theory]
    [InlineData(1280, 720, 30)]
    [InlineData(64, 64, 12)]
    [InlineData(3840, 2160, 60)]
    [InlineData(640, 400, 50)]
    public void ComputeBitrate_QualityPreset_MatchesLegacyThreeArgOverloadExactly(int width, int height, int fps)
    {
        long viaPreset = Mp4Encoder.ComputeBitrate(width, height, fps, GifSizePreset.Quality);
        long legacy = Mp4Encoder.ComputeBitrate(width, height, fps);
        Assert.Equal(legacy, viaPreset);
    }

    [Fact]
    public void ComputeBitrate_Max_TypicalSelection_Is4xTheQualityFactor()
    {
        long bitrate = Mp4Encoder.ComputeBitrate(1280, 720, 30, GifSizePreset.Max);
        // 4.0 * 0.1 * 1280 * 720 * 30 = 11,059,200 — inside [8e6, 64e6], not clamped.
        Assert.Equal(11_059_200, bitrate);
    }

    [Fact]
    public void ComputeBitrate_Max_TinySelection_ClampsToTheEightMbpsFloor()
    {
        long bitrate = Mp4Encoder.ComputeBitrate(64, 64, 12, GifSizePreset.Max);
        Assert.Equal(8_000_000, bitrate);
    }

    [Fact]
    public void ComputeBitrate_Max_HugeSelection_ClampsToTheSixtyFourMbpsCeiling()
    {
        long bitrate = Mp4Encoder.ComputeBitrate(3840, 2160, 60, GifSizePreset.Max);
        Assert.Equal(64_000_000, bitrate);
    }

    [Fact]
    public void ComputeBitrate_Balanced_TypicalSelection_Is0Point6xTheQualityFactor()
    {
        long bitrate = Mp4Encoder.ComputeBitrate(1280, 720, 30, GifSizePreset.Balanced);
        // 0.6 * 0.1 * 1280 * 720 * 30 = 1,658,880 — inside [1.5e6, 10e6], not clamped.
        Assert.Equal(1_658_880, bitrate);
    }

    [Fact]
    public void ComputeBitrate_Balanced_TinySelection_ClampsToTheOnePointFiveMbpsFloor()
    {
        long bitrate = Mp4Encoder.ComputeBitrate(64, 64, 12, GifSizePreset.Balanced);
        Assert.Equal(1_500_000, bitrate);
    }

    [Fact]
    public void ComputeBitrate_Balanced_HugeSelection_ClampsToTheTenMbpsCeiling()
    {
        long bitrate = Mp4Encoder.ComputeBitrate(3840, 2160, 60, GifSizePreset.Balanced);
        Assert.Equal(10_000_000, bitrate);
    }

    [Fact]
    public void ComputeBitrate_Compact_TypicalSelection_Is0Point35xTheQualityFactor()
    {
        long bitrate = Mp4Encoder.ComputeBitrate(1920, 1080, 30, GifSizePreset.Compact);
        // 0.35 * 0.1 * 1920 * 1080 * 30 = 2,177,280 mathematically, but 0.35 * 0.1 is not exactly
        // representable in double (like the legacy overload's own 0.1 factor) — the actual
        // left-to-right double multiplication lands one ulp below the whole number, and the
        // (long) cast in ComputeBitrate truncates rather than rounds, so the real return value is
        // 2,177,279. Still comfortably inside [1e6, 6e6], not clamped.
        Assert.Equal(2_177_279, bitrate);
    }

    [Fact]
    public void ComputeBitrate_Compact_SmallSelection_ClampsToTheOneMbpsFloor()
    {
        // 0.35 * 0.1 * 1280 * 720 * 30 = 967,680 — below the 1e6 floor even at a common 720p30
        // selection, unlike Balanced/Max at the same size.
        long bitrate = Mp4Encoder.ComputeBitrate(1280, 720, 30, GifSizePreset.Compact);
        Assert.Equal(1_000_000, bitrate);
    }

    [Fact]
    public void ComputeBitrate_Compact_HugeSelection_ClampsToTheSixMbpsCeiling()
    {
        long bitrate = Mp4Encoder.ComputeBitrate(3840, 2160, 60, GifSizePreset.Compact);
        Assert.Equal(6_000_000, bitrate);
    }

    [Theory]
    [InlineData(GifSizePreset.Max, 8_000_000, 64_000_000)]
    [InlineData(GifSizePreset.Quality, 2_000_000, 16_000_000)]
    [InlineData(GifSizePreset.Balanced, 1_500_000, 10_000_000)]
    [InlineData(GifSizePreset.Compact, 1_000_000, 6_000_000)]
    [InlineData(GifSizePreset.Minimal, 500_000, 3_000_000)]
    public void ComputeBitrate_NeverEscapesItsOwnClampedBand(GifSizePreset preset, long min, long max)
    {
        Assert.InRange(Mp4Encoder.ComputeBitrate(1, 1, 1, preset), min, max);
        Assert.InRange(Mp4Encoder.ComputeBitrate(7680, 4320, 60, preset), min, max);
    }

    // ---------- Minimal tier (quality/fps expansion workstream) ----------

    [Fact]
    public void ComputeBitrate_Minimal_TypicalSelection_Is0Point15xTheQualityFactor()
    {
        long bitrate = Mp4Encoder.ComputeBitrate(1280, 720, 30, GifSizePreset.Minimal);
        // 0.15 * 0.1 * 1280 * 720 * 30 = 414,720 — below the 500k floor at this common 720p30
        // selection (Minimal's clamp band starts noticeably higher than its unclamped formula
        // would produce here, unlike Balanced/Max at the same size).
        Assert.Equal(500_000, bitrate);
    }

    [Fact]
    public void ComputeBitrate_Minimal_LargeSelection_UnclampedInBand()
    {
        long bitrate = Mp4Encoder.ComputeBitrate(1920, 1080, 30, GifSizePreset.Minimal);
        // 0.15 * 0.1 * 1920 * 1080 * 30 = 933,120 mathematically, but (like the Compact case above)
        // 0.15 * 0.1 is not exactly representable in double, so the real left-to-right double
        // multiplication lands one ulp below the whole number and the truncating (long) cast in
        // ComputeBitrate returns 933,119. Still comfortably inside [500k, 3M], not clamped.
        Assert.Equal(933_119, bitrate);
    }

    [Fact]
    public void ComputeBitrate_Minimal_TinySelection_ClampsToTheFiveHundredKbpsFloor()
    {
        long bitrate = Mp4Encoder.ComputeBitrate(64, 64, 12, GifSizePreset.Minimal);
        Assert.Equal(500_000, bitrate);
    }

    [Fact]
    public void ComputeBitrate_Minimal_HugeSelection_ClampsToTheThreeMbpsCeiling()
    {
        long bitrate = Mp4Encoder.ComputeBitrate(3840, 2160, 60, GifSizePreset.Minimal);
        Assert.Equal(3_000_000, bitrate);
    }

    [Fact]
    public void ComputeBitrate_Minimal_IsTheSmallestOfAllTiersAtTheSameSelection()
    {
        long minimal = Mp4Encoder.ComputeBitrate(1920, 1080, 30, GifSizePreset.Minimal);
        long compact = Mp4Encoder.ComputeBitrate(1920, 1080, 30, GifSizePreset.Compact);
        long balanced = Mp4Encoder.ComputeBitrate(1920, 1080, 30, GifSizePreset.Balanced);
        long quality = Mp4Encoder.ComputeBitrate(1920, 1080, 30, GifSizePreset.Quality);
        long max = Mp4Encoder.ComputeBitrate(1920, 1080, 30, GifSizePreset.Max);
        Assert.True(minimal <= compact);
        Assert.True(compact <= balanced);
        Assert.True(balanced <= quality);
        Assert.True(quality <= max);
    }

    /// <summary>Decode-back regression test for the vertical-flip bug: Mp4Encoder.Create's input
    /// media type once set MF_MT_DEFAULT_STRIDE to a NEGATIVE value (declaring the buffer
    /// bottom-up) while WriteFrame fed it SdrImage's top-down rows verbatim, so every recorded
    /// .mp4 played upside down. This writes a red-top/blue-bottom clip, decodes it back with an
    /// IMFSourceReader, and asserts the TRUE image orientation (not raw buffer layout — the
    /// decoder's own negotiated stride can itself come back negative, so this test must resolve
    /// that sign before comparing rows, or it would just be checking two wrongs cancel out).</summary>
    [Fact]
    public void WriteFrame_EncodedVideo_PreservesTopDownOrientation()
    {
        const int width = 64;
        const int height = 64;
        const int fps = 30;
        const int frameCount = 10;

        string tempPath = Path.Combine(Path.GetTempPath(), $"roesnip-orientation-{Guid.NewGuid():N}.mp4");
        try
        {
            // --- Encode: top half pure red, bottom half pure blue (BGRA8, top-down). ---
            var pixels = new byte[width * 4 * height];
            for (int y = 0; y < height; y++)
            {
                bool topHalf = y < height / 2;
                for (int x = 0; x < width; x++)
                {
                    int o = y * width * 4 + x * 4;
                    if (topHalf)
                    {
                        pixels[o + 0] = 0;   // B
                        pixels[o + 1] = 0;   // G
                        pixels[o + 2] = 255; // R
                    }
                    else
                    {
                        pixels[o + 0] = 255; // B
                        pixels[o + 1] = 0;   // G
                        pixels[o + 2] = 0;   // R
                    }
                    pixels[o + 3] = 255; // A
                }
            }
            var frame = new SdrImage(width, height, pixels);

            using (var encoder = Mp4Encoder.Create(tempPath, width, height, fps, withAudio: false))
            {
                for (int i = 0; i < frameCount; i++)
                {
                    encoder.WriteFrame(frame, i * 333_333L);
                }
                encoder.FinalizeAndClose();
            }

            // --- Decode back and inspect the actual (not raw-buffer) top/bottom rows. ---
            // EnableVideoProcessing lets the source reader insert a color-conversion MFT so the
            // H.264 decoder's native YUV (NV12) output can be requested as RGB32 below — without
            // it, SetCurrentMediaType(RGB32) fails with MF_E_INVALIDMEDIATYPE.
            using var readerAttrs = MediaFactory.MFCreateAttributes(1);
            readerAttrs.Set(SourceReaderAttributeKeys.EnableVideoProcessing, true).CheckError();
            IMFSourceReader reader = MediaFactory.MFCreateSourceReaderFromURL(tempPath, readerAttrs);
            try
            {
                using var rgbType = MediaFactory.MFCreateMediaType();
                rgbType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video).CheckError();
                rgbType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.Rgb32).CheckError();
                reader.SetCurrentMediaType(SourceReaderIndex.FirstVideoStream, rgbType);

                using var currentType = reader.GetCurrentMediaType(SourceReaderIndex.FirstVideoStream);

                MediaFactory.MFGetAttributeSize(
                    currentType, MediaTypeAttributeKeys.FrameSize, out uint decodedWidth, out uint decodedHeight);
                Assert.Equal((uint)width, decodedWidth);
                Assert.Equal((uint)height, decodedHeight);

                // MF_MT_DEFAULT_STRIDE's sign tells us row order: negative = bottom-up (the
                // buffer's first row is the image's LAST row). Absent = assume top-down,
                // tightly packed (width * 4), same default the encoder side documents.
                var strideResult = currentType.GetUInt32(MediaTypeAttributeKeys.DefaultStride, out uint strideBits);
                bool strideKnown = strideResult.Success;
                int signedStride = strideKnown ? unchecked((int)strideBits) : width * 4;
                bool decodedBottomUp = strideKnown && signedStride < 0;
                int rowStride = Math.Abs(signedStride);

                IMFSample? sample = null;
                for (int attempt = 0; attempt < 30 && sample is null; attempt++)
                {
                    sample = reader.ReadSample(
                        SourceReaderIndex.FirstVideoStream,
                        SourceReaderControlFlag.None,
                        out _,
                        out SourceReaderFlag streamFlags,
                        out _);
                    if (streamFlags.HasFlag(SourceReaderFlag.EndOfStream))
                    {
                        break;
                    }
                }
                Assert.NotNull(sample);

                using (sample)
                {
                    using var buffer = sample!.ConvertToContiguousBuffer();
                    buffer.Lock(out IntPtr ptr, out _, out int currentLength);
                    byte[] decoded;
                    try
                    {
                        decoded = new byte[currentLength];
                        Marshal.Copy(ptr, decoded, 0, currentLength);
                    }
                    finally
                    {
                        buffer.Unlock();
                    }

                    // RGB32 is 32bpp BGRX, same byte order as SdrImage.Pixels (see Mp4Encoder's
                    // own comment) — no channel swap needed, only the row-order correction above.
                    (byte b, byte g, byte r) PixelAt(int x, int trueY)
                    {
                        int bufferRow = decodedBottomUp ? height - 1 - trueY : trueY;
                        int o = bufferRow * rowStride + x * 4;
                        return (decoded[o], decoded[o + 1], decoded[o + 2]);
                    }

                    // Sample a few columns, a few rows away from the exact middle seam (H.264 is
                    // lossy — blocky compression can smear pixels right at the seam).
                    int[] sampleXs = { width / 4, width / 2, 3 * width / 4 };
                    int topY = height / 4;
                    int bottomY = 3 * height / 4;

                    foreach (int x in sampleXs)
                    {
                        var top = PixelAt(x, topY);
                        Assert.True(top.r > 150, $"Expected top row red-dominant at x={x}, got R={top.r}, B={top.b}");
                        Assert.True(top.b < 100, $"Expected top row red-dominant at x={x}, got R={top.r}, B={top.b}");

                        var bottom = PixelAt(x, bottomY);
                        Assert.True(bottom.b > 150, $"Expected bottom row blue-dominant at x={x}, got R={bottom.r}, B={bottom.b}");
                        Assert.True(bottom.r < 100, $"Expected bottom row blue-dominant at x={x}, got R={bottom.r}, B={bottom.b}");
                    }
                }
            }
            finally
            {
                reader.Dispose();
            }
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }
}
