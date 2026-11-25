using System;

namespace RoeSnip.Core.Color;

/// <summary>Reads the display's peak luminance from an EDID's CTA-861 HDR static metadata block, the
/// number the tone-mapper uses as the monitor's headroom (Linux: /sys/class/drm/card*-NAME/edid).</summary>
public static class EdidHdr
{
    /// <summary>Desired content max luminance in nits; false when the EDID has no HDR static
    /// metadata block or leaves the field at 0.</summary>
    public static bool TryReadMaxLuminance(ReadOnlySpan<byte> edid, out double nits)
    {
        nits = 0;
        for (int block = 128; block + 128 <= edid.Length; block += 128)
        {
            var ext = edid.Slice(block, 128);
            if (ext[0] != 0x02) continue; // CTA-861 extension
            int dtd = ext[2];
            if (dtd < 4 || dtd > 127) continue;
            int at = 4;
            while (at < dtd)
            {
                int tag = ext[at] >> 5, len = ext[at] & 0x1F;
                if (at + 1 + len > 128) break;
                // Extended tag block 7, extended tag 6 = HDR static metadata.
                if (tag == 7 && len >= 4 && ext[at + 1] == 6)
                {
                    int code = ext[at + 4]; // bytes: ext tag, EOTFs, descriptors, max luminance
                    if (code == 0) return false;
                    nits = 50.0 * Math.Pow(2.0, code / 32.0);
                    return true;
                }
                at += 1 + len;
            }
        }
        return false;
    }
}
