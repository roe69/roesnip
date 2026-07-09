using RoeSnip.Recording;
using Xunit;

namespace RoeSnip.Tests;

/// <summary>The pure byte-layout patches GifEncoder.SaveTo applies on top of WPF's
/// GifBitmapEncoder output: the NETSCAPE2.0 loop extension it never emits, and the per-frame delay
/// fields it leaves at zero. Verified against hand-built minimal GIF headers rather than a live WPF
/// encode, so these run without touching the encoder at all.</summary>
public class GifEncoderTests
{
    // GIF89a signature (6) + Logical Screen Descriptor (7): width=1,height=1 LE16, packed, bg index,
    // pixel aspect. packed=0x00 => no Global Color Table.
    private static byte[] MinimalHeaderNoGct() =>
        new byte[]
        {
            (byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9', (byte)'a',
            0x01, 0x00, 0x01, 0x00, // width=1, height=1
            0x00,                   // packed: no GCT
            0x00,                   // background color index
            0x00,                   // pixel aspect ratio
            0x3B,                   // GIF trailer, standing in for "first real block" in this test
        };

    // Same header but WITH a Global Color Table: packed=0x80 | size bits 000 => table size
    // 3 * 2^(0+1) = 6 bytes.
    private static byte[] MinimalHeaderWithGct()
    {
        var header = new byte[]
        {
            (byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9', (byte)'a',
            0x01, 0x00, 0x01, 0x00,
            0x80, // packed: GCT present, size field 000
            0x00,
            0x00,
        };
        var gct = new byte[6]; // 2 colors * 3 bytes (RGB) each
        var result = new byte[header.Length + gct.Length + 1];
        header.CopyTo(result, 0);
        gct.CopyTo(result, header.Length);
        result[^1] = 0x3B; // trailer
        return result;
    }

    [Fact]
    public void InjectLoopExtension_NoGlobalColorTable_InsertsAtByte13()
    {
        byte[] result = GifEncoder.InjectLoopExtension(MinimalHeaderNoGct());

        // The 19-byte NETSCAPE2.0 block must start exactly at offset 13.
        Assert.Equal(0x21, result[13]); // Extension introducer
        Assert.Equal(0xFF, result[14]); // Application extension label
        Assert.Equal(0x0B, result[15]); // 11-byte application identifier + auth code block
        Assert.Equal((byte)'N', result[16]);
        Assert.Equal((byte)'E', result[17]);
        Assert.Equal((byte)'T', result[18]);
        Assert.Equal((byte)'S', result[19]);
        Assert.Equal((byte)'C', result[20]);
        Assert.Equal((byte)'A', result[21]);
        Assert.Equal((byte)'P', result[22]);
        Assert.Equal((byte)'E', result[23]);
        Assert.Equal((byte)'2', result[24]);
        Assert.Equal((byte)'.', result[25]);
        Assert.Equal((byte)'0', result[26]);
        Assert.Equal(0x03, result[27]); // sub-block length
        Assert.Equal(0x01, result[28]); // loop sub-block ID
        Assert.Equal(0x00, result[29]); // loop count LE16 low byte = 0 (infinite)
        Assert.Equal(0x00, result[30]); // loop count LE16 high byte
        Assert.Equal(0x00, result[31]); // block terminator

        // Everything after the inserted block is the original tail, byte-for-byte.
        Assert.Equal(0x3B, result[32]);
        Assert.Equal(MinimalHeaderNoGct().Length + 19, result.Length);
    }

    [Fact]
    public void InjectLoopExtension_WithGlobalColorTable_InsertsAfterTheTable()
    {
        byte[] source = MinimalHeaderWithGct();
        byte[] result = GifEncoder.InjectLoopExtension(source);

        // Header (13) + GCT (6) = 19 — the NETSCAPE block starts there, not at 13.
        Assert.Equal(0x21, result[19]);
        Assert.Equal(0xFF, result[20]);
        Assert.Equal(source.Length + 19, result.Length);
        // The trailer byte (originally the last byte of `source`) survives, shifted by 19.
        Assert.Equal(0x3B, result[^1]);
    }

    // A well-formed per-frame block: GCE (8 bytes, delay=0) + Image Descriptor (10-byte fixed
    // fields, no Local Color Table) + a single 3-byte LZW data sub-block containing bytes that
    // deliberately spell out a spurious "0x21,0xF9,0x04" GCE-marker look-alike, followed by the
    // sub-block terminator and the LZW-data terminator. PatchFrameDelays must walk PAST that
    // look-alike (it's declared image data, not a real block boundary) rather than "patching" it.
    private static byte[] BuildFrameBlock(byte lzwMinCodeSize = 0x02) =>
        new byte[]
        {
            0x21, 0xF9, 0x04, 0x00, 0x00, 0x00, 0x00, 0x00, // GCE: packed, delayLo, delayHi, transIdx, terminator
            0x2C, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00, // Image Descriptor: left/top/w/h=1x1, packed=0 (no LCT)
            lzwMinCodeSize,                                   // LZW minimum code size
            0x03, 0x21, 0xF9, 0x04,                           // one 3-byte data sub-block containing a GCE look-alike
            0x00,                                             // image data terminator
        };

    private static byte[] BuildMinimalGif(params byte[][] frameBlocks)
    {
        var header = MinimalHeaderNoGct();
        var body = new List<byte>(header.Take(header.Length - 1)); // header minus its own trailer byte
        foreach (var frame in frameBlocks)
        {
            body.AddRange(frame);
        }
        body.Add(0x3B); // trailer
        return body.ToArray();
    }

    [Fact]
    public void PatchFrameDelays_OverwritesEachGceInFrameOrder()
    {
        byte[] gif = BuildMinimalGif(BuildFrameBlock(), BuildFrameBlock());
        int gce1 = 13;                 // first block starts right after the 13-byte header (no GCT)
        int gce2 = 13 + BuildFrameBlock().Length;

        byte[] result = GifEncoder.PatchFrameDelays(gif, new ushort[] { 10, 300 });

        Assert.Equal(10, result[gce1 + 4] | (result[gce1 + 5] << 8));
        Assert.Equal(300, result[gce2 + 4] | (result[gce2 + 5] << 8));

        // The GCE-look-alike bytes embedded in each frame's own LZW data sub-block must survive
        // untouched — proof the walker skipped over declared image data instead of "patching" it.
        int lookalike1 = gce1 + 8 + 10 + 2; // GCE(8) + ImageDescriptor(10) + LZW-min-code-size(1) + sub-block-size(1)
        Assert.Equal(0x21, result[lookalike1]);
        Assert.Equal(0xF9, result[lookalike1 + 1]);
        Assert.Equal(0x04, result[lookalike1 + 2]);
    }

    [Fact]
    public void PatchFrameDelays_MoreGcesThanDelays_LeavesExtraGcesUntouched()
    {
        byte[] gif = BuildMinimalGif(BuildFrameBlock(), BuildFrameBlock());
        int gce1 = 13;
        int gce2 = 13 + BuildFrameBlock().Length;

        byte[] result = GifEncoder.PatchFrameDelays(gif, new ushort[] { 42 });

        Assert.Equal(42, result[gce1 + 4] | (result[gce1 + 5] << 8));
        Assert.Equal(0, result[gce2 + 4] | (result[gce2 + 5] << 8)); // second GCE never had a matching delay
    }

    [Fact]
    public void PatchFrameDelays_DoesNotCorruptGceLookalikeInsideImageData()
    {
        // Regression test for the false-positive byte-scan bug: a naive scan for 0x21,0xF9,0x04
        // anywhere in the stream would find the look-alike embedded in this frame's own LZW data
        // sub-block and "patch" 2 bytes into the middle of it, corrupting the frame.
        byte[] gif = BuildMinimalGif(BuildFrameBlock());
        byte[] original = (byte[])gif.Clone();

        byte[] result = GifEncoder.PatchFrameDelays(gif, new ushort[] { 55 });

        int frameStart = 13;
        int lzwDataStart = frameStart + 8 + 10 + 1; // past GCE + Image Descriptor + min-code-size byte
        // The sub-block size byte (0x03) and its 3 payload bytes (0x21,0xF9,0x04) must be byte-for-
        // byte identical to the un-patched original.
        for (int i = lzwDataStart; i < lzwDataStart + 4; i++)
        {
            Assert.Equal(original[i], result[i]);
        }
    }

    [Fact]
    public void AtCap_TrueOnceFrameCountReachesCapSecondsTimesFps()
    {
        var encoder = new GifEncoder();
        Assert.False(encoder.AtCap(capSeconds: 1, targetFps: 2)); // 0 frames < 2

        for (int i = 0; i < 2; i++)
        {
            encoder.AddFrame(new RoeSnip.Imaging.SdrImage(1, 1, new byte[4]), delayCentiseconds: 50);
        }

        Assert.True(encoder.AtCap(capSeconds: 1, targetFps: 2)); // 2 frames >= 1*2
        Assert.Equal(2, encoder.FrameCount);
    }
}
