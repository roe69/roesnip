using System;
using System.Collections.Generic;

namespace RoeSnip.Core.Tests;

/// <summary>Standalone reference GIF-LZW decoder for tests only — <see cref="RoeSnip.Core.Recording.Gif.GifLzwEncoder"/>
/// has no matching decode side of its own (nothing in the app ever needs to decode a GIF it just
/// wrote), but tests need one to round-trip encoder output and assert per-pixel palette
/// index/transparency correctness that <c>GifBitmapDecoder</c>'s already-composited output can't
/// show. Deliberately independent of the encoder's own dictionary implementation (a from-scratch
/// classic string-table decoder, not the encoder's hash table read backwards) so a bug shared
/// between encode and decode can't hide from these tests.
///
/// Code-width growth: <c>dict.Count</c> (after adding a new entry) is NOT a same-tick analogue of
/// the encoder's post-increment <c>nextCode</c>, even though both start at <c>clearCode + 2</c> —
/// a decoder is structurally one dictionary insert BEHIND the encoder at any given code, because
/// the encoder learns a new (prefix, symbol) pair's symbol immediately (it has both prefix and
/// symbol in hand when it fails to extend a match), while the decoder can only complete its
/// mirroring insert once it has ALSO seen the *start* of the *next* code (the classic reason the
/// KwKwK special case exists at all: on the very first code after a clear, <c>prevCode</c> is -1
/// and no insert happens yet). <see cref="RoeSnip.Core.Recording.Gif.GifLzwEncoder"/> must therefore grow
/// its code width one entry earlier than this decoder does: the encoder grows when its
/// (post-increment) <c>nextCode</c> reaches <c>(1 &lt;&lt; codeWidth) + 1</c>, while this decoder —
/// exactly one insert behind — grows when its own (post-increment) <c>dict.Count</c> reaches
/// <c>1 &lt;&lt; codeWidth</c> (no "+1"; the lag itself supplies it). Both boundaries were derived
/// and cross-checked empirically against real-world decoders (WPF <c>GifBitmapDecoder</c> and GDI+
/// <c>Image.FromStream</c>), not by pattern-matching a convention name — see
/// GifLzwEncoderRealDecoderTests for the decode-through-real-decoders regression coverage.</summary>
internal static class GifLzwTestDecoder
{
    /// <summary>Decodes GIF LZW data starting at <c>gif[pos]</c> — a minimum-code-size byte
    /// followed by a chain of data sub-blocks (1-byte length + that many bytes) terminated by a
    /// 0x00 length byte — and returns the decompressed palette-index stream.
    /// <paramref name="endPos"/> is set to the offset just past the terminator, so callers can
    /// keep walking the rest of the GIF.</summary>
    public static byte[] Decode(byte[] gif, int pos, out int endPos)
    {
        int minCodeSize = gif[pos];
        pos++;

        var data = new List<byte>();
        while (gif[pos] != 0x00)
        {
            int size = gif[pos];
            pos++;
            for (int i = 0; i < size; i++)
            {
                data.Add(gif[pos + i]);
            }
            pos += size;
        }
        endPos = pos + 1; // past the terminator

        int clearCode = 1 << minCodeSize;
        int endCode = clearCode + 1;

        var dict = new List<byte[]>();
        void ResetDict()
        {
            dict.Clear();
            for (int i = 0; i < clearCode; i++)
            {
                dict.Add(new[] { (byte)i });
            }
            dict.Add(Array.Empty<byte>()); // clearCode slot (unused as a lookup)
            dict.Add(Array.Empty<byte>()); // endCode slot (unused as a lookup)
        }
        ResetDict();

        int codeWidth = minCodeSize + 1;
        int bitPos = 0;
        int ReadCode(int width)
        {
            int value = 0;
            for (int b = 0; b < width; b++)
            {
                int byteIndex = (bitPos + b) / 8;
                int bitIndex = (bitPos + b) % 8;
                int bit = (data[byteIndex] >> bitIndex) & 1;
                value |= bit << b;
            }
            bitPos += width;
            return value;
        }

        var output = new List<byte>();
        int prevCode = -1;
        while (true)
        {
            int code = ReadCode(codeWidth);
            if (code == clearCode)
            {
                ResetDict();
                codeWidth = minCodeSize + 1;
                prevCode = -1;
                continue;
            }
            if (code == endCode)
            {
                break;
            }

            byte[] entry;
            if (code < dict.Count && (code < clearCode || code >= clearCode + 2))
            {
                entry = dict[code];
            }
            else if (code == dict.Count && prevCode >= 0)
            {
                // KwKwK case: the code being emitted is the one about to be added.
                var prev = dict[prevCode];
                entry = new byte[prev.Length + 1];
                Array.Copy(prev, entry, prev.Length);
                entry[prev.Length] = prev[0];
            }
            else
            {
                throw new InvalidOperationException($"Invalid LZW code {code} at bit {bitPos}.");
            }

            output.AddRange(entry);

            if (prevCode >= 0 && dict.Count < 4096)
            {
                var prev = dict[prevCode];
                var newEntry = new byte[prev.Length + 1];
                Array.Copy(prev, newEntry, prev.Length);
                newEntry[prev.Length] = entry[0];
                dict.Add(newEntry);
                if (dict.Count == (1 << codeWidth) && codeWidth < 12)
                {
                    codeWidth++;
                }
            }
            prevCode = code;
        }

        return output.ToArray();
    }
}
