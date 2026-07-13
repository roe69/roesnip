using System;
using System.IO;
using System.Runtime.InteropServices;
using RoeSnip.Core.Imaging;
using RoeSnip.Core.Recording;
using RoeSnip.Core.Recording.Gif;
using Vortice.MediaFoundation;
using Xunit;

namespace RoeSnip.Platform.Windows.Tests;

/// <summary>Port-fidelity checks for this assembly's <see cref="Mp4Encoder"/> — the WPF app's own
/// Mp4EncoderTests already exhaustively covers <c>ComputeBitrate</c> (a straight passthrough to the
/// shared <see cref="Mp4BitrateEstimator"/>, unchanged by this port, so not re-tested here). What
/// this file DOES re-verify: the historical upside-down-video regression (MF_MT_DEFAULT_STRIDE's
/// sign — see Mp4Encoder.Create's own doc comment) survived the copy from
/// src/RoeSnip/Recording/Mp4Encoder.cs into this Windows-only Platform assembly, exercised through
/// the <see cref="IVideoEncoder"/> seam exactly as RecordingController (item 20) will call it.</summary>
public class Mp4EncoderTests
{
    [Fact]
    public async System.Threading.Tasks.Task WriteFrame_ThroughTheSeam_AlwaysReturnsTrue()
    {
        string tempPath = Path.Combine(Path.GetTempPath(), $"roesnip-mp4seam-{Guid.NewGuid():N}.mp4");
        try
        {
            IVideoEncoder encoder = Mp4Encoder.Create(tempPath, 64, 64, fps: 30, withAudio: false);
            try
            {
                var frame = new SdrImage(64, 64, new byte[64 * 4 * 64]);
                // MP4 never dedupes — every WriteFrame call is a straight encode, unlike GIF's own
                // IVideoEncoder implementation (see GifVideoEncoderTests' identical-repeat test).
                Assert.True(encoder.WriteFrame(frame, timestamp100ns: 0));
                Assert.True(encoder.WriteFrame(frame, timestamp100ns: 333_333));
                await encoder.FinishAsync();
            }
            finally
            {
                encoder.Dispose();
            }

            Assert.True(new FileInfo(tempPath).Length > 0);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    /// <summary>Decode-back regression test for the vertical-flip bug the WPF original's own doc
    /// comment warns about: a red-top/blue-bottom clip must decode back top-down, not flipped —
    /// see Mp4Encoder.Create's DefaultStride comment for the historical bug this guards against.</summary>
    [Fact]
    public void WriteFrame_EncodedVideo_PreservesTopDownOrientation()
    {
        const int width = 64;
        const int height = 64;
        const int fps = 30;
        const int frameCount = 10;

        string tempPath = Path.Combine(Path.GetTempPath(), $"roesnip-mp4orientation-{Guid.NewGuid():N}.mp4");
        try
        {
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

                    (byte b, byte g, byte r) PixelAt(int x, int trueY)
                    {
                        int bufferRow = decodedBottomUp ? height - 1 - trueY : trueY;
                        int o = bufferRow * rowStride + x * 4;
                        return (decoded[o], decoded[o + 1], decoded[o + 2]);
                    }

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
