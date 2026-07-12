using System;
using System.IO;
using System.Runtime.InteropServices;
using RoeSnip.Imaging;
using RoeSnip.Recording;
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
