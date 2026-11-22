using RoeSnip.Core.Capture;

namespace RoeSnip.Core.Recording;

/// <summary>Draws a cursor sprite into a tight BGRA frame (premultiplied "over", alpha kept 255),
/// clipped to the frame. Used where the capture source delivers frames without a pointer.</summary>
public static class CursorCompositor
{
    /// <summary><paramref name="pointerX"/>/<paramref name="pointerY"/> are the pointer hotspot in
    /// frame px; the sprite's top-left goes at pointer minus hotspot. Returns false when nothing
    /// of the sprite falls inside the frame.</summary>
    public static bool Draw(byte[] frame, int frameWidth, int frameHeight, CursorSprite sprite, int pointerX, int pointerY)
    {
        int left = pointerX - sprite.HotspotX, top = pointerY - sprite.HotspotY;
        int x0 = Math.Max(0, left), y0 = Math.Max(0, top);
        int x1 = Math.Min(frameWidth, left + sprite.Width), y1 = Math.Min(frameHeight, top + sprite.Height);
        if (x0 >= x1 || y0 >= y1) return false;
        var src = sprite.Bgra;
        for (int y = y0; y < y1; y++)
        {
            int s = ((y - top) * sprite.Width + (x0 - left)) * 4;
            int d = (y * frameWidth + x0) * 4;
            for (int x = x0; x < x1; x++, s += 4, d += 4)
            {
                int a = src[s + 3];
                if (a == 0) continue;
                if (a == 255)
                {
                    frame[d] = src[s];
                    frame[d + 1] = src[s + 1];
                    frame[d + 2] = src[s + 2];
                }
                else
                {
                    int inv = 255 - a;
                    frame[d] = (byte)Math.Min(255, src[s] + (frame[d] * inv + 127) / 255);
                    frame[d + 1] = (byte)Math.Min(255, src[s + 1] + (frame[d + 1] * inv + 127) / 255);
                    frame[d + 2] = (byte)Math.Min(255, src[s + 2] + (frame[d + 2] * inv + 127) / 255);
                }
                frame[d + 3] = 255;
            }
        }
        return true;
    }
}
