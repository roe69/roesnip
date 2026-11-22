using System.Buffers.Binary;

namespace RoeSnip.Core.Capture;

/// <summary>A cursor sprite in tight premultiplied BGRA, hotspot in sprite px.</summary>
public sealed record CursorSprite(int Width, int Height, int HotspotX, int HotspotY, byte[] Bgra);

/// <summary>Parses PipeWire's SPA_META_Cursor block (spa/buffer/meta.h):
/// spa_meta_cursor {u32 id, u32 flags, i32 x, i32 y, i32 hotspot_x, i32 hotspot_y, u32 bitmap_offset}
/// then, at bitmap_offset from its start, spa_meta_bitmap {u32 format, u32 width, u32 height,
/// i32 stride, u32 offset} with the pixels at offset from the bitmap's start. id 0 means the cursor
/// is not on this stream; bitmap_offset 0 means the sprite did not change (mutter sends it only when
/// it does); a 0x0 bitmap means no sprite (hidden pointer).</summary>
public static class SpaCursorMeta
{
    public const int CursorSize = 28;
    public const int BitmapSize = 20;

    public readonly record struct Parsed(bool Valid, int X, int Y, bool HasSprite, CursorSprite? Sprite);

    /// <summary>Parses one meta block. <see cref="Parsed.HasSprite"/> is true when the block carried
    /// a sprite update (<see cref="Parsed.Sprite"/> null then means no sprite / unsupported format).</summary>
    public static bool TryParse(ReadOnlySpan<byte> meta, out Parsed parsed)
    {
        parsed = default;
        if (meta.Length < CursorSize) return false;
        uint id = U32(meta, 0);
        int x = I32(meta, 8), y = I32(meta, 12);
        int hx = I32(meta, 16), hy = I32(meta, 20);
        uint bitmapOffset = U32(meta, 24);
        if (id == 0)
        {
            parsed = new Parsed(false, 0, 0, false, null);
            return true;
        }
        if (bitmapOffset < CursorSize || bitmapOffset + BitmapSize > meta.Length)
        {
            parsed = new Parsed(true, x, y, false, null);
            return true;
        }

        var bmp = meta.Slice((int)bitmapOffset);
        uint format = U32(bmp, 0);
        int w = (int)U32(bmp, 4), h = (int)U32(bmp, 8);
        int stride = I32(bmp, 12);
        uint pixOffset = U32(bmp, 16);
        CursorSprite? sprite = null;
        if (w > 0 && h > 0 && w <= 1024 && h <= 1024 && stride >= w * 4
            && pixOffset >= BitmapSize && pixOffset + (long)stride * (h - 1) + w * 4 <= bmp.Length)
        {
            sprite = ToPremultipliedBgra(bmp.Slice((int)pixOffset), format, w, h, stride, hx, hy);
        }
        parsed = new Parsed(true, x, y, true, sprite);
        return true;
    }

    /// <summary>Reorders a 32-bit sprite to tight BGRA, alpha taken as already premultiplied
    /// (identical for the fully opaque / fully clear pixels of a pointer); unknown format gives null.</summary>
    public static CursorSprite? ToPremultipliedBgra(ReadOnlySpan<byte> src, uint format, int w, int h, int stride, int hx, int hy)
    {
        // Byte index of B, G, R, A within a pixel.
        (int b, int g, int r, int a) order = format switch
        {
            SpaPod.VideoBGRA => (0, 1, 2, 3),
            SpaPod.VideoRGBA => (2, 1, 0, 3),
            SpaPod.VideoARGB => (3, 2, 1, 0),
            SpaPod.VideoABGR => (1, 2, 3, 0),
            _ => (-1, 0, 0, 0),
        };
        if (order.b < 0) return null;
        var dst = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            var row = src.Slice(y * stride, w * 4);
            int o = y * w * 4;
            for (int x = 0; x < w; x++)
            {
                int i = x * 4;
                dst[o + i] = row[i + order.b];
                dst[o + i + 1] = row[i + order.g];
                dst[o + i + 2] = row[i + order.r];
                dst[o + i + 3] = row[i + order.a];
            }
        }
        return new CursorSprite(w, h, hx, hy, dst);
    }

    private static uint U32(ReadOnlySpan<byte> b, int at) => BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(at, 4));
    private static int I32(ReadOnlySpan<byte> b, int at) => BinaryPrimitives.ReadInt32LittleEndian(b.Slice(at, 4));
}
