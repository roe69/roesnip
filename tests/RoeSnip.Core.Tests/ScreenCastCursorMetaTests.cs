using System.Buffers.Binary;
using RoeSnip.Core.Capture;
using RoeSnip.Core.Recording;
using Xunit;

namespace RoeSnip.Core.Tests;

public class SpaCursorMetaTests
{
    /// <summary>A spa_meta_cursor block as mutter writes it, with an optional sprite bitmap.</summary>
    private static byte[] Meta(uint id, int x, int y, int hx, int hy, (uint Format, int W, int H, byte[] Px)? bitmap)
    {
        int bitmapAt = 32; // past the 28-byte cursor, 8-aligned
        int pixelsAt = 24; // past the 20-byte bitmap header
        int size = bitmap is { } b ? bitmapAt + pixelsAt + b.Px.Length : 28;
        var m = new byte[size];
        W(m, 0, id); W(m, 4, 0); W(m, 8, (uint)x); W(m, 12, (uint)y); W(m, 16, (uint)hx); W(m, 20, (uint)hy);
        W(m, 24, bitmap is null ? 0u : (uint)bitmapAt);
        if (bitmap is { } bm)
        {
            W(m, bitmapAt, bm.Format); W(m, bitmapAt + 4, (uint)bm.W); W(m, bitmapAt + 8, (uint)bm.H);
            W(m, bitmapAt + 12, (uint)(bm.W * 4)); W(m, bitmapAt + 16, (uint)pixelsAt);
            bm.Px.CopyTo(m, bitmapAt + pixelsAt);
        }
        return m;
    }

    private static void W(byte[] b, int at, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(at, 4), v);

    [Fact]
    public void Id0_IsNotOnThisStream()
    {
        Assert.True(SpaCursorMeta.TryParse(Meta(0, 5, 6, 0, 0, null), out var c));
        Assert.False(c.Valid);
    }

    [Fact]
    public void PositionWithoutBitmap_KeepsTheSprite()
    {
        Assert.True(SpaCursorMeta.TryParse(Meta(1, -3, 700, 1, 2, null), out var c));
        Assert.True(c.Valid);
        Assert.Equal((-3, 700), (c.X, c.Y));
        Assert.False(c.HasSprite);
    }

    [Fact]
    public void RgbaBitmap_BecomesBgraWithHotspot()
    {
        byte[] px = { 10, 20, 30, 255, 1, 2, 3, 0 }; // 2x1 RGBA
        Assert.True(SpaCursorMeta.TryParse(Meta(1, 100, 200, 1, 0, (SpaPod.VideoRGBA, 2, 1, px)), out var c));
        Assert.True(c.HasSprite);
        var s = Assert.IsType<CursorSprite>(c.Sprite);
        Assert.Equal((2, 1, 1, 0), (s.Width, s.Height, s.HotspotX, s.HotspotY));
        Assert.Equal(new byte[] { 30, 20, 10, 255, 3, 2, 1, 0 }, s.Bgra);
    }

    [Fact]
    public void ArgbAndAbgrBitmaps_AreReordered()
    {
        byte[] argb = { 255, 30, 20, 10 };
        Assert.True(SpaCursorMeta.TryParse(Meta(1, 0, 0, 0, 0, (SpaPod.VideoARGB, 1, 1, argb)), out var a));
        Assert.Equal(new byte[] { 10, 20, 30, 255 }, a.Sprite!.Bgra);
        byte[] abgr = { 255, 10, 20, 30 };
        Assert.True(SpaCursorMeta.TryParse(Meta(1, 0, 0, 0, 0, (SpaPod.VideoABGR, 1, 1, abgr)), out var b));
        Assert.Equal(new byte[] { 10, 20, 30, 255 }, b.Sprite!.Bgra);
    }

    [Fact]
    public void EmptyBitmap_IsASpriteUpdateWithNoSprite()
    {
        Assert.True(SpaCursorMeta.TryParse(Meta(1, 0, 0, 0, 0, (SpaPod.VideoRGBA, 0, 0, Array.Empty<byte>())), out var c));
        Assert.True(c.HasSprite);
        Assert.Null(c.Sprite);
    }

    [Fact]
    public void TruncatedBitmap_IsRejected()
    {
        var m = Meta(1, 0, 0, 0, 0, (SpaPod.VideoRGBA, 2, 2, new byte[16]));
        Assert.True(SpaCursorMeta.TryParse(m.AsSpan(0, m.Length - 4), out var c));
        Assert.Null(c.Sprite);
        Assert.False(SpaCursorMeta.TryParse(m.AsSpan(0, 20), out _));
    }
}

public class SpaPodTests
{
    private static uint U(byte[] b, int at) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(at, 4));

    [Fact]
    public void EnumFormat_IsAnAlignedFormatObject()
    {
        var pod = SpaPod.RawVideoEnumFormat();
        Assert.Equal(0, pod.Length % 8);
        Assert.Equal((uint)(pod.Length - 8), U(pod, 0));
        Assert.Equal(SpaPod.TypeObject, U(pod, 4));
        Assert.Equal(SpaPod.ObjectFormat, U(pod, 8));
        Assert.Equal(SpaPod.ParamEnumFormat, U(pod, 12));
        // First prop: mediaType = Id video.
        Assert.Equal(SpaPod.KeyMediaType, U(pod, 16));
        Assert.Equal((4u, SpaPod.TypeId, SpaPod.MediaTypeVideo), (U(pod, 24), U(pod, 28), U(pod, 32)));
        // Its default size reads back through the format parser.
        Assert.True(SpaPod.TryReadVideoSize(pod, out int w, out int h));
        Assert.Equal((1920, 1080), (w, h));
    }

    [Fact]
    public void CursorMetaParam_RangesFromCursorOnlyToMaxBitmap()
    {
        var pod = SpaPod.CursorMetaParam(384);
        Assert.Equal(SpaPod.ObjectParamMeta, U(pod, 8));
        Assert.Equal(SpaPod.ParamMeta, U(pod, 12));
        Assert.Equal((SpaPod.KeyMetaType, SpaPod.MetaCursor), (U(pod, 16), U(pod, 32)));
        // Second prop at 40: key, flags, choice pod {size, type, choice type, flags, child size, child type, values}.
        Assert.Equal(SpaPod.KeyMetaSize, U(pod, 40));
        Assert.Equal(SpaPod.TypeChoice, U(pod, 52));
        Assert.Equal((SpaPod.ChoiceRange, 4u, SpaPod.TypeInt), (U(pod, 56), U(pod, 64), U(pod, 68)));
        uint max = (uint)(28 + 20 + 384 * 384 * 4);
        Assert.Equal((max, 28u, max), (U(pod, 72), U(pod, 76), U(pod, 80)));
        Assert.Equal(0, pod.Length % 8);
    }

    [Fact]
    public void FixedFormatSize_IsRead()
    {
        // Format object with mediaType then a plain rectangle size 2560x1440.
        var pod = new byte[16 + 24 + 24];
        void W(int at, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(pod.AsSpan(at, 4), v);
        W(0, (uint)(pod.Length - 8)); W(4, SpaPod.TypeObject); W(8, SpaPod.ObjectFormat); W(12, SpaPod.ParamFormat);
        W(16, SpaPod.KeyMediaType); W(24, 4); W(28, SpaPod.TypeId); W(32, SpaPod.MediaTypeVideo);
        W(40, SpaPod.KeyVideoSize); W(48, 8); W(52, SpaPod.TypeRectangle); W(56, 2560); W(60, 1440);
        Assert.True(SpaPod.TryReadVideoSize(pod, out int w, out int h));
        Assert.Equal((2560, 1440), (w, h));
        Assert.False(SpaPod.TryReadVideoSize(pod.AsSpan(0, 12), out _, out _));
    }
}

public class CursorCompositorTests
{
    private static byte[] Frame(int w, int h, byte value)
    {
        var f = new byte[w * h * 4];
        for (int i = 0; i < f.Length; i += 4) { f[i] = f[i + 1] = f[i + 2] = value; f[i + 3] = 255; }
        return f;
    }

    private static CursorSprite Sprite2x2() => new(2, 2, 1, 1, new byte[]
    {
        0, 0, 255, 255,   0, 255, 0, 255,
        0, 0, 0, 0,       64, 64, 64, 128, // clear, then half-alpha premultiplied grey
    });

    [Fact]
    public void Draw_PlacesSpriteAtPointerMinusHotspot()
    {
        var f = Frame(4, 4, 100);
        Assert.True(CursorCompositor.Draw(f, 4, 4, Sprite2x2(), 2, 2));
        int P(int x, int y) => (y * 4 + x) * 4;
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, f[P(1, 1)..(P(1, 1) + 4)]);
        Assert.Equal(new byte[] { 0, 255, 0, 255 }, f[P(2, 1)..(P(2, 1) + 4)]);
        Assert.Equal(new byte[] { 100, 100, 100, 255 }, f[P(1, 2)..(P(1, 2) + 4)]); // clear pixel
        // 64 + 100 * 127/255 = 64 + 50 = 114.
        Assert.Equal(new byte[] { 114, 114, 114, 255 }, f[P(2, 2)..(P(2, 2) + 4)]);
        Assert.Equal(new byte[] { 100, 100, 100, 255 }, f[P(0, 0)..4]);
    }

    [Fact]
    public void Draw_ClipsAtTheTopLeft()
    {
        var f = Frame(3, 3, 100);
        Assert.True(CursorCompositor.Draw(f, 3, 3, Sprite2x2(), 0, 0)); // sprite spans -1..0
        Assert.Equal(new byte[] { 114, 114, 114, 255 }, f[0..4]);
        Assert.Equal(new byte[] { 100, 100, 100, 255 }, f[4..8]);
    }

    [Fact]
    public void Draw_ClipsAtTheBottomRight()
    {
        var f = Frame(3, 3, 100);
        Assert.True(CursorCompositor.Draw(f, 3, 3, Sprite2x2(), 3, 3)); // only the red pixel at 2,2 lands
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, f[32..36]);
        Assert.Equal(new byte[] { 100, 100, 100, 255 }, f[28..32]);
    }

    [Fact]
    public void Draw_OutsideTheFrame_DrawsNothing()
    {
        var f = Frame(3, 3, 100);
        var before = (byte[])f.Clone();
        Assert.False(CursorCompositor.Draw(f, 3, 3, Sprite2x2(), 10, -5));
        Assert.Equal(before, f);
    }
}
