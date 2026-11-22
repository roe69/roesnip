namespace RoeSnip.Core.Recording;

/// <summary>One GStreamer encoder element candidate and how to give it a target bitrate.</summary>
public sealed record GstEncoderCandidate(string Element, Func<long, string> PropertiesForBitrateBps)
{
    /// <summary>The element plus its properties, ready to drop into a gst_parse_launch string.</summary>
    public string Describe(long bitrateBps) => (Element + " " + PropertiesForBitrateBps(bitrateBps)).TrimEnd();
}

/// <summary>Probe orders for the Linux GStreamer MP4 path. Hardware first (NVENC, then VA-API in its
/// new and legacy plugins), then software. Bitrate units differ per element: nvh264enc, vah264enc,
/// vaapih264enc and x264enc take kbit/s, openh264enc and every audio encoder here take bit/s.</summary>
public static class GstEncoderCatalog
{
    public static readonly IReadOnlyList<GstEncoderCandidate> H264 = new[]
    {
        new GstEncoderCandidate("nvh264enc", bps => $"bitrate={Kbps(bps)}"),
        new GstEncoderCandidate("vah264enc", bps => $"bitrate={Kbps(bps)}"),
        new GstEncoderCandidate("vaapih264enc", bps => $"bitrate={Kbps(bps)}"),
        new GstEncoderCandidate("x264enc", bps => $"bitrate={Kbps(bps)} speed-preset=veryfast"),
        new GstEncoderCandidate("openh264enc", bps => $"rate-control=bitrate bitrate={bps}"),
    };

    /// <summary>AAC first (what the Windows MP4s carry); Opus in MP4 is the last resort when no AAC
    /// encoder is installed (stock Ubuntu ships none without the ffmpeg/fdk packages).</summary>
    public static readonly IReadOnlyList<GstEncoderCandidate> Audio = new[]
    {
        new GstEncoderCandidate("fdkaacenc", bps => $"bitrate={bps}"),
        new GstEncoderCandidate("avenc_aac", bps => $"bitrate={bps}"),
        new GstEncoderCandidate("voaacenc", bps => $"bitrate={bps}"),
        new GstEncoderCandidate("faac", bps => $"bitrate={bps}"),
        new GstEncoderCandidate("opusenc", bps => $"bitrate={bps}"),
    };

    /// <summary>The candidates whose element exists, in probe order.</summary>
    public static IReadOnlyList<GstEncoderCandidate> Available(
        IReadOnlyList<GstEncoderCandidate> order, Func<string, bool> elementExists)
        => order.Where(c => elementExists(c.Element)).ToArray();

    private static long Kbps(long bps) => Math.Max(1, (bps + 500) / 1000);
}
