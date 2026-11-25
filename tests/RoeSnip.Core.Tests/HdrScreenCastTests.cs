using System.Diagnostics;
using System.Numerics;
using RoeSnip.Core.Capture;
using RoeSnip.Core.Color;
using RoeSnip.Core.Imaging;
using Xunit;

namespace RoeSnip.Core.Tests;

/// <summary>The Linux HDR screencast chain: PQ BT.2020 half frame to scRGB (PqBt2020), the EDID peak
/// luminance, the .jxr writer's TIFF and its scRGB scaling.</summary>
public class HdrScreenCastTests
{
    private static MonitorInfo Monitor() => new(0, "DP-1", "DP-1", new RectPhysical(0, 0, 4, 2),
        96, 96, 1.0, false, 240.0, 0.0, true);

    private static ushort Bits(double v) => BitConverter.HalfToUInt16Bits((Half)v);

    private static byte[] Pixels(params (double r, double g, double b)[] px)
    {
        var bytes = new byte[px.Length * 8];
        for (int i = 0; i < px.Length; i++)
        {
            BitConverter.GetBytes(Bits(px[i].r)).CopyTo(bytes, i * 8);
            BitConverter.GetBytes(Bits(px[i].g)).CopyTo(bytes, i * 8 + 2);
            BitConverter.GetBytes(Bits(px[i].b)).CopyTo(bytes, i * 8 + 4);
            BitConverter.GetBytes(Bits(0)).CopyTo(bytes, i * 8 + 6);
        }
        return bytes;
    }

    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(1.0, 10000.0)]
    [InlineData(0.508078, 100.0)]   // BT.2408 table: 100 nits = 50.8% PQ
    [InlineData(0.580689, 203.0)]   // 203 nits = 58.1% PQ
    [InlineData(0.751827, 1000.0)]  // 1000 nits = 75.2% PQ
    public void PqEotfMatchesKnownLevels(double encoded, double nits)
    {
        Assert.Equal(nits, PqBt2020.PqToNits(encoded), nits == 0 ? 6 : 0);
    }

    [Fact]
    public void PqLutReadsMutterReferenceWhite()
    {
        // The measured mutter frame: UI white = raw half 0.58057 = 202.77 nits.
        float v = PqBt2020.PqHalfToScRgb(Bits(0.58057));
        Assert.Equal(202.77 / 80.0, v, 2);
    }

    [Fact]
    public void LutHandlesNanNegativeAndOverrange()
    {
        Assert.Equal(0f, PqBt2020.PqHalfToScRgb(BitConverter.HalfToUInt16Bits(Half.NaN)));
        Assert.Equal(0f, PqBt2020.PqHalfToScRgb(Bits(-0.5)));
        Assert.Equal(10000f / 80f, PqBt2020.PqHalfToScRgb(Bits(3.0)), 1);
    }

    [Fact]
    public void GreyKeepsItsValueThroughThePrimariesMatrix()
    {
        // Each matrix row sums to 1: a neutral stays neutral.
        PqBt2020.ConvertPixel(Bits(0.5806), Bits(0.5806), Bits(0.5806), out float r, out float g, out float b);
        Assert.Equal(r, g, 3);
        Assert.Equal(g, b, 3);
        Assert.Equal(PqBt2020.PqHalfToScRgb(Bits(0.5806)), r, 3);
    }

    [Fact]
    public void Bt2020GreenLandsOutsideBt709()
    {
        PqBt2020.ConvertPixel(Bits(0), Bits(0.5806), Bits(0), out float r, out float g, out float b);
        Assert.True(r < 0f, "BT.2020 green is outside the BT.709 gamut: negative red");
        Assert.True(b < 0f);
        Assert.True(g > 2.5f);
    }

    [Fact]
    public void PrimariesMatrixIsTheBt2087One()
    {
        PqBt2020.ConvertPixel(Bits(1), Bits(0), Bits(0), out float r, out float g, out float b);
        float full = PqBt2020.PqHalfToScRgb(Bits(1));
        Assert.Equal(1.6605 * full, r, 0);
        Assert.Equal(-0.1246 * full, g, 0);
        Assert.Equal(-0.0182 * full, b, 0);
    }

    [Fact]
    public void ReferenceWhiteMapsToSdrWhiteAndToneMapsToWhite()
    {
        var px = Pixels((0.58057, 0.58057, 0.58057), (0.58057, 0.58057, 0.58057));
        var frame = PqBt2020.CreateFrame(px, 16, 2, 1, Monitor(), 0.0);

        Assert.Equal(FrameFormat.Fp16ScRgb, frame.Format);
        Assert.Equal(203.0, frame.Monitor.SdrWhiteNits);
        Assert.True(frame.Monitor.AdvancedColorActive);
        Assert.Equal(1000.0, frame.Monitor.MaxLuminanceNits); // unknown peak
        Assert.Equal(203.0 / 80.0, frame.SdrWhiteInBufferUnits, 6);
        Assert.Equal(1.0, frame.ReadPixelScRgb(0, 0).W);
        Assert.Equal(202.77, frame.ReadPixelNits(0, 0), 0);

        var sdr = ToneMapper.MapToSdr(frame, new ToneMapOptions());
        Assert.InRange(sdr.Pixels[2], 253, 255); // R of the first pixel (BGRA)
        Assert.InRange(sdr.Pixels[0], 253, 255);
    }

    [Fact]
    public void HighlightsAboveWhiteShoulderInsteadOfClipping()
    {
        // 1000 nits next to 203-nit white: the shoulder keeps the two apart below 255.
        var px = Pixels((0.58057, 0.58057, 0.58057), (0.7518, 0.7518, 0.7518), (0.35, 0.35, 0.35));
        var frame = PqBt2020.CreateFrame(px, 24, 3, 1, Monitor(), 1000.0);
        var sdr = ToneMapper.MapToSdr(frame, new ToneMapOptions());
        int white = sdr.Pixels[2], bright = sdr.Pixels[4 + 2], dim = sdr.Pixels[8 + 2];
        Assert.True(white < bright, $"white {white} bright {bright}");
        Assert.True(dim < white);
        Assert.True(white < 255, "203 nits must sit below the 1000-nit peak");
    }

    [Fact]
    public void EdidPeakLuminanceComesFromTheHdrStaticMetadataBlock()
    {
        var edid = new byte[256];
        edid[126] = 1;
        edid[128] = 0x02;                 // CTA-861
        edid[130] = 12;                   // data blocks end, DTDs start
        int a = 132;
        edid[a++] = (7 << 5) | 6;         // extended tag block, 6 bytes
        edid[a++] = 6;                    // HDR static metadata
        edid[a++] = 0x05;                 // SDR + PQ
        edid[a++] = 0x01;
        edid[a++] = 128;                  // 50 * 2^(128/32) = 800 nits
        edid[a++] = 100;
        edid[a++] = 10;
        Assert.True(EdidHdr.TryReadMaxLuminance(edid, out double nits));
        Assert.Equal(800.0, nits, 3);
        Assert.False(EdidHdr.TryReadMaxLuminance(new byte[128], out _));
    }

    [Fact]
    public void HdrEnumFormatReadsBackAsF16Pq()
    {
        byte[] pod = SpaPod.HdrEnumFormat(new ulong[] { 0x0300000000606010UL, 0x00FFFFFFFFFFFFFFUL });
        Assert.True(SpaPod.TryReadVideoFormat(pod, out var f));
        Assert.Equal(SpaPod.VideoRgbaF16, f.Format);
        Assert.Equal(SpaPod.TransferSmpte2084, f.Transfer);
        Assert.Equal(SpaPod.PrimariesBt2020, f.Primaries);
        Assert.True(f.HasModifier);
        Assert.True(f.ModifierIsChoice);
        Assert.Equal(0x0300000000606010UL, f.Modifier);
        Assert.Equal(1920, f.Width);
        Assert.Equal(0, pod.Length % 8);
    }

    [Fact]
    public void SdrDmaBufEnumFormatReadsBackAsBgrxWithoutColorimetry()
    {
        byte[] pod = SpaPod.SdrDmaBufEnumFormat(new ulong[] { 0x0300000000606010UL, 0x00FFFFFFFFFFFFFFUL });
        Assert.True(SpaPod.TryReadVideoFormat(pod, out var f));
        Assert.Equal(SpaPod.VideoBGRx, f.Format);
        Assert.Equal(0u, f.Transfer);
        Assert.Equal(0u, f.Primaries);
        Assert.True(f.HasModifier);
        Assert.True(f.ModifierIsChoice);
        Assert.Equal(0, pod.Length % 8);
    }

    [Fact]
    public void SdrEnumFormatHasNoModifierSoNodesAnswerWithMemfd()
    {
        byte[] pod = SpaPod.SdrEnumFormat();
        Assert.True(SpaPod.TryReadVideoFormat(pod, out var f));
        Assert.Equal(SpaPod.VideoBGRx, f.Format);
        Assert.False(f.HasModifier);
        Assert.Equal(0, pod.Length % 8);
        byte[] buffers = SpaPod.ShmBuffersParam();
        Assert.Equal(buffers.Length - 8, (int)BitConverter.ToUInt32(buffers, 0));
        Assert.Equal(0, buffers.Length % 8);
    }

    [Fact]
    public void DmaBufBuffersParamIsAnAlignedObject()
    {
        byte[] pod = SpaPod.DmaBufBuffersParam();
        Assert.Equal(pod.Length - 8, (int)BitConverter.ToUInt32(pod, 0));
        Assert.Equal(SpaPod.TypeObject, BitConverter.ToUInt32(pod, 4));
        Assert.Equal(0, pod.Length % 8);
    }

    [Fact]
    public void TiffHeaderDescribesRgbaFloat()
    {
        using var ms = new MemoryStream();
        JxrExport.WriteTiffHeader(ms, 5, 3);
        var b = ms.ToArray();
        Assert.Equal((byte)'I', b[0]);
        Assert.Equal(42, BitConverter.ToUInt16(b, 2));
        int entries = BitConverter.ToUInt16(b, 8);
        var tags = new Dictionary<int, uint>();
        for (int i = 0; i < entries; i++)
        {
            int o = 10 + i * 12;
            tags[BitConverter.ToUInt16(b, o)] = BitConverter.ToUInt16(b, o + 2) == 3 && BitConverter.ToUInt32(b, o + 4) == 1
                ? BitConverter.ToUInt16(b, o + 8) : BitConverter.ToUInt32(b, o + 8);
        }
        Assert.Equal(5u, tags[256]);
        Assert.Equal(3u, tags[257]);
        Assert.Equal(4u, tags[277]);
        Assert.Equal(3u, tags[339]);
        Assert.Equal((uint)(5 * 3 * 16), tags[279]);
        Assert.Equal(b.Length, (int)tags[273]); // pixel data starts right after the header
    }

    [Fact]
    public void RowFillRescalesToTrueScRgb()
    {
        var hdr = PqBt2020.CreateFrame(Pixels((0.58057, 0.58057, 0.58057)), 8, 1, 1, Monitor(), 1000.0);
        var row = new float[4];
        JxrExport.FillRow(hdr, 0, 0, 1, row);
        Assert.Equal(202.77 / 80.0, row[0], 1); // 203-nit white = 2.54 scRGB (1.0 would be 80 nits)
        Assert.Equal(1f, row[3]);

        // macOS EDR convention: 1.0 buffer unit = SDR white, which the writer pins at 203 nits.
        var mac = Monitor() with { SdrWhiteNits = 203.0 };
        var edr = new CapturedFrame(FrameFormat.Fp16ScRgb, 1, 1, 8, Pixels((1.0, 2.0, 0.5)), mac, 1.0);
        JxrExport.FillRow(edr, 0, 0, 1, row);
        Assert.Equal(203.0 / 80.0, row[0], 2);
        Assert.Equal(2 * 203.0 / 80.0, row[1], 1);

        var sdr = new CapturedFrame(FrameFormat.Bgra8Srgb, 1, 1, 4, new byte[] { 255, 255, 255, 255 }, Monitor(), 1.0);
        JxrExport.FillRow(sdr, 0, 0, 1, row);
        Assert.Equal(1f, row[0], 3); // sRGB white is 80 nits regardless of the monitor's SDR white
    }

    [Fact]
    public void JxrRoundTripKeepsValuesAboveOne()
    {
        string? enc = JxrExport.LocateEncoder();
        string? dec = new[] { "/usr/bin/JxrDecApp" }.FirstOrDefault(File.Exists);
        if (enc is null || dec is null) return; // needs jxrlib's tools on the box

        var frame = PqBt2020.CreateFrame(Pixels((0.58057, 0.58057, 0.58057), (0.7518, 0.7518, 0.7518)), 16, 2, 1, Monitor(), 1000.0);
        string dir = Path.Combine(Path.GetTempPath(), "roesnip-jxr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string jxr = Path.Combine(dir, "a.jxr"), tif = Path.Combine(dir, "b.tif");
            JxrExport.Write(jxr, frame, new RectPhysical(0, 0, 2, 1));
            Assert.True(new FileInfo(jxr).Length > 0);
            var p = Process.Start(new ProcessStartInfo(dec, $"-i \"{jxr}\" -o \"{tif}\"") { RedirectStandardError = true, RedirectStandardOutput = true })!;
            p.WaitForExit(30000);
            var bytes = File.ReadAllBytes(tif);
            var last = bytes.AsSpan(bytes.Length - 32);
            float r0 = BitConverter.ToSingle(last.Slice(0, 4)), r1 = BitConverter.ToSingle(last.Slice(16, 4));
            Assert.Equal(202.77 / 80.0, r0, 1);
            Assert.True(r1 > 10f, $"1000-nit pixel came back as {r1}");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
