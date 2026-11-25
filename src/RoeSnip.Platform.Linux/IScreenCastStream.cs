using RoeSnip.Core.Capture;

namespace RoeSnip.Platform.Linux;

/// <summary>A kept-open screencast stream holding the newest frame of one monitor: the 8-bit GStreamer
/// pipeline (<see cref="ScreenCastStream"/>) or, on an HDR monitor, the half-float DMA-BUF consumer
/// (<see cref="HdrScreenCastStream"/>).</summary>
internal interface IScreenCastStream : IDisposable
{
    ScreenCastStreamGeometry Geometry { get; }
    string? DeadReason { get; }
    bool IsDead { get; }

    /// <summary>Bumped for every new frame; lets the recorder skip unchanged frames.</summary>
    long Sequence { get; }

    bool WaitForFirstSample(TimeSpan timeout);

    /// <summary>Copies a block of the newest frame as tight BGRA8 (see <see cref="ScreenCastStream.TryCopy"/>).</summary>
    bool TryCopy(int srcLeft, int srcTop, int outWidth, int outHeight,
        int monitorWidth, int monitorHeight, byte[] dest, out long sequence, out string? sizeNote);
}
