using System;
using System.IO;
using RoeSnip.Core.Imaging;
using RoeSnip.Core.Recording;
using RoeSnip.Core.Recording.Gif;
using Xunit;

namespace RoeSnip.Core.Tests;

/// <summary>Exercises <see cref="GifVideoEncoder"/> entirely THROUGH the <see cref="IVideoEncoder"/>
/// seam (never the concrete type directly) — the recording-seams workstream's whole point is that
/// RecordingController (item 20) never has to know it is talking to a GIF encoder specifically, so
/// these tests hold it to that contract.</summary>
public class GifVideoEncoderTests : IDisposable
{
    private readonly string _tempPath = Path.Combine(Path.GetTempPath(), $"roesnip-gifvideoencoder-{Guid.NewGuid():N}.gif");

    public void Dispose()
    {
        if (File.Exists(_tempPath))
        {
            File.Delete(_tempPath);
        }
    }

    private static SdrImage SolidFrame(int width, int height, byte b, byte g, byte r)
    {
        var pixels = new byte[width * 4 * height];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i + 0] = b;
            pixels[i + 1] = g;
            pixels[i + 2] = r;
            pixels[i + 3] = 255;
        }
        return new SdrImage(width, height, pixels);
    }

    [Fact]
    public void HasAudio_IsAlwaysFalse()
    {
        IVideoEncoder encoder = GifVideoEncoder.Create(_tempPath, 8, 8, fps: 30, withAudio: true, GifSizePreset.Quality);
        try
        {
            Assert.False(encoder.HasAudio);
        }
        finally
        {
            encoder.Dispose();
        }
    }

    [Fact]
    public void WriteAudioSamples_IsANoOp_NeverThrows()
    {
        IVideoEncoder encoder = GifVideoEncoder.Create(_tempPath, 8, 8, fps: 30, withAudio: false, GifSizePreset.Quality);
        try
        {
            // Must be safe to call unconditionally per IVideoEncoder's own contract, even with a
            // nonsense length/short buffer — GIF has no audio track to write into at all.
            encoder.WriteAudioSamples(new byte[4], 4, timestamp100ns: 0);
        }
        finally
        {
            encoder.Dispose();
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task WriteFrame_FirstFrame_AlwaysEmits()
    {
        IVideoEncoder encoder = GifVideoEncoder.Create(_tempPath, 8, 8, fps: 30, withAudio: false, GifSizePreset.Quality);
        try
        {
            bool emitted = encoder.WriteFrame(SolidFrame(8, 8, 10, 20, 30), timestamp100ns: 0);
            Assert.True(emitted); // the very first frame always establishes the baseline keyframe
            await encoder.FinishAsync(endTimestampTicks: 1_000_000);
        }
        finally
        {
            encoder.Dispose();
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task WriteFrame_IdenticalRepeat_ReportsNotEmitted()
    {
        IVideoEncoder encoder = GifVideoEncoder.Create(_tempPath, 8, 8, fps: 30, withAudio: false, GifSizePreset.Quality);
        try
        {
            var frame = SolidFrame(8, 8, 10, 20, 30);
            Assert.True(encoder.WriteFrame(frame, timestamp100ns: 0));
            // Same pixels, later timestamp (still within the 100ns domain this seam is pinned to) —
            // GifEncoder's own dedupe must skip it, and that "no" must surface through this seam's
            // return value exactly like it does calling GifEncoder.AddFrame directly.
            bool emittedAgain = encoder.WriteFrame(frame, timestamp100ns: 1_000_000);
            Assert.False(emittedAgain);
            await encoder.FinishAsync(endTimestampTicks: 2_000_000);
        }
        finally
        {
            encoder.Dispose();
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task FinishAsync_ProducesAValidGifFile()
    {
        IVideoEncoder encoder = GifVideoEncoder.Create(_tempPath, 8, 8, fps: 30, withAudio: false, GifSizePreset.Quality);
        try
        {
            encoder.WriteFrame(SolidFrame(8, 8, 10, 20, 30), timestamp100ns: 0);
            await encoder.FinishAsync(endTimestampTicks: 1_000_000);
        }
        finally
        {
            encoder.Dispose();
        }

        byte[] bytes = File.ReadAllBytes(_tempPath);
        Assert.True(bytes.Length > 16);
        Assert.Equal("GIF89a", System.Text.Encoding.ASCII.GetString(bytes, 0, 6));
        Assert.Equal(0x3B, bytes[^1]); // GIF trailer byte — proves FinishAsync actually closed the stream
    }

    [Fact]
    public async System.Threading.Tasks.Task FinishAsync_WithoutEndTimestamp_StillClosesTheFile()
    {
        IVideoEncoder encoder = GifVideoEncoder.Create(_tempPath, 8, 8, fps: 30, withAudio: false, GifSizePreset.Quality);
        try
        {
            encoder.WriteFrame(SolidFrame(8, 8, 10, 20, 30), timestamp100ns: 0);
            await encoder.FinishAsync(); // null endTimestampTicks — the hard/error-stop path
        }
        finally
        {
            encoder.Dispose();
        }

        byte[] bytes = File.ReadAllBytes(_tempPath);
        Assert.Equal(0x3B, bytes[^1]);
    }
}
