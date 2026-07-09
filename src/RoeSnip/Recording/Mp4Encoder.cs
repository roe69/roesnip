using System;
using System.Runtime.InteropServices;
using RoeSnip.Imaging;
using Vortice.MediaFoundation;

namespace RoeSnip.Recording;

/// <summary>Encodes BGRA8 <see cref="SdrImage"/> frames to H.264/MP4 via Media Foundation's
/// IMFSinkWriter — the exact API surface pinned in PLAN.md §1 (verified by reflecting the real
/// Vortice.MediaFoundation 3.8.3 DLL, not assumed). Encoder-thread-only (RecordingSession's
/// dedicated thread) — never the WGC callback thread, never the UI thread.
///
/// Public (not internal), same reasoning as Overlay/SizeInput.cs and Overlay/ToolCursorCache.cs:
/// <see cref="ComputeBitrate"/> is pure logic worth unit-testing directly, and this app's
/// convention is to make a class public rather than add an InternalsVisibleTo edit for it.</summary>
public sealed class Mp4Encoder : IDisposable
{
    private static bool s_mfStarted;
    private static readonly object s_mfGate = new();

    /// <summary>Called once, lazily, on the first-ever recording. Never calls MFShutdown — this is
    /// a resident tray process; the startup cost is paid once and never unwound, matching how this
    /// app already treats other process-lifetime resources (e.g. WgcCapturer's s_slots cache).</summary>
    public static void EnsureStarted()
    {
        if (s_mfStarted)
        {
            return;
        }
        lock (s_mfGate)
        {
            if (s_mfStarted)
            {
                return;
            }
            // MFStartup returns a raw Result (not auto-throwing) — verified via reflection;
            // CheckError() is SharpGen's idiomatic throw-on-failure call.
            MediaFactory.MFStartup(useLightVersion: false).CheckError();
            s_mfStarted = true;
        }
    }

    /// <summary>Bitrate heuristic: 0.1 bits/pixel/frame (a standard "medium quality" H.264 rule of
    /// thumb), clamped to a sane [2, 16] Mbps band so a tiny selection doesn't starve the encoder
    /// and a huge one doesn't produce an unreasonably large file. Pulled out as its own static
    /// method (no IMFSinkWriter dependency) so it's unit-testable without a live MF session —
    /// see Mp4EncoderTests.</summary>
    public static long ComputeBitrate(int width, int height, int fps) =>
        Math.Clamp((long)(0.1 * width * height * fps), 2_000_000, 16_000_000);

    private readonly IMFSinkWriter _writer;
    private readonly int _streamIndex;
    private readonly long _frameDuration100ns;

    private Mp4Encoder(IMFSinkWriter writer, int streamIndex, int fps)
    {
        _writer = writer;
        _streamIndex = streamIndex;
        _frameDuration100ns = 10_000_000L / fps;
    }

    public static Mp4Encoder Create(string tempFilePath, int width, int height, int fps)
    {
        EnsureStarted();
        long bitrate = ComputeBitrate(width, height, fps);

        using var outputType = MediaFactory.MFCreateMediaType();
        outputType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video).CheckError();
        outputType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264).CheckError();
        outputType.Set(MediaTypeAttributeKeys.AvgBitrate, (uint)bitrate).CheckError();
        outputType.Set(MediaTypeAttributeKeys.InterlaceMode, (uint)VideoInterlaceMode.Progressive).CheckError();
        MediaFactory.MFSetAttributeSize(outputType, MediaTypeAttributeKeys.FrameSize, (uint)width, (uint)height).CheckError();
        MediaFactory.MFSetAttributeRatio(outputType, MediaTypeAttributeKeys.FrameRate, (uint)fps, 1).CheckError();

        using var inputType = MediaFactory.MFCreateMediaType();
        inputType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video).CheckError();
        // MFVideoFormat_RGB32 = 32bpp BGRX in memory — SAME byte layout as SdrImage.Pixels
        // (B,G,R,A/X), no channel-swap needed on write.
        inputType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.Rgb32).CheckError();
        MediaFactory.MFSetAttributeSize(inputType, MediaTypeAttributeKeys.FrameSize, (uint)width, (uint)height).CheckError();
        MediaFactory.MFSetAttributeRatio(inputType, MediaTypeAttributeKeys.FrameRate, (uint)fps, 1).CheckError();
        inputType.Set(MediaTypeAttributeKeys.InterlaceMode, (uint)VideoInterlaceMode.Progressive).CheckError();
        // CRITICAL, easy to miss: MF's default DIB row order is bottom-up. SdrImage rows are
        // top-down (see that class's own doc comment). Without this negative-stride attribute the
        // .mp4 plays upside down — it encodes/plays/seeks fine otherwise, so the bug is invisible
        // until someone actually looks at a frame.
        int stride = width * 4;
        inputType.Set(MediaTypeAttributeKeys.DefaultStride, unchecked((uint)(-stride))).CheckError();

        using var attrs = MediaFactory.MFCreateAttributes(2);
        // Frames already arrive throttled by our own capture cadence (RegionRecorder) — don't let
        // MF pace-block WriteSample a second time on top of that.
        attrs.Set(SinkWriterAttributeKeys.DisableThrottling, true).CheckError();
        attrs.Set(SinkWriterAttributeKeys.LowLatency, true).CheckError();

        IMFSinkWriter writer = MediaFactory.MFCreateSinkWriterFromURL(tempFilePath, null, attrs);
        int streamIndex = writer.AddStream(outputType);
        writer.SetInputMediaType(streamIndex, inputType, null);
        writer.BeginWriting();

        return new Mp4Encoder(writer, streamIndex, fps);
    }

    /// <summary>Encoder thread only. SdrImage.Pixels is guaranteed tightly packed
    /// (Stride == Width * 4 — SdrImage's own doc comment), which is exactly the buffer MF's RGB32
    /// subtype expects — no repacking needed even with DefaultStride's sign flip (that attribute
    /// only affects row ORDER interpretation, not packing).</summary>
    public void WriteFrame(SdrImage bgra8, long timestamp100ns)
    {
        using var buffer = MediaFactory.MFCreateMemoryBuffer(bgra8.Pixels.Length);
        buffer.Lock(out IntPtr ptr, out _, out _);
        try
        {
            Marshal.Copy(bgra8.Pixels, 0, ptr, bgra8.Pixels.Length);
        }
        finally
        {
            buffer.Unlock();
        }
        buffer.CurrentLength = bgra8.Pixels.Length;

        using var sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(buffer);
        sample.SampleTime = timestamp100ns;
        sample.SampleDuration = _frameDuration100ns;
        _writer.WriteSample(_streamIndex, sample); // void-returning — SharpGen auto-throws on HRESULT failure
    }

    public void FinalizeAndClose() => _writer.Finalize(); // IMFSinkWriter's own interface method, not System.Object.Finalize

    public void Dispose() => _writer.Dispose();
}
