using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Media.Imaging;
using RoeSnip.Imaging;

namespace RoeSnip.Recording;

/// <summary>Batches frames into an animated GIF via WPF's <see cref="GifBitmapEncoder"/>, then
/// byte-patches the output for two gaps that encoder leaves: it never emits the NETSCAPE2.0 loop
/// extension (so the GIF would play once and stop) and its per-frame delay defaults to 0 unless
/// each frame's own delay is set. See <see cref="SaveTo"/> for the patch details.
///
/// Encoder thread only. GifBitmapEncoder is a batch API — every frame added, then ONE Save() call;
/// there is no incremental/streaming write, which is why RecordingSession enforces a hard time cap
/// on GIF recordings (frames sit fully decoded in memory, ~W*H*4 bytes each, until Stop()).
///
/// Public (not internal), same reasoning as Mp4Encoder: <see cref="InjectLoopExtension"/> and
/// <see cref="PatchFrameDelays"/> are pure byte-layout logic worth unit-testing directly.</summary>
public sealed class GifEncoder
{
    private readonly List<(BitmapFrame Frame, ushort DelayCentiseconds)> _frames = new();

    public int FrameCount => _frames.Count;

    /// <summary>Defensive backstop for RecordingSession's own UI-thread DispatcherTimer cap check —
    /// evaluated on the encoder thread itself so a wedged/late UI timer can never let memory grow
    /// unbounded past the intended cap.</summary>
    public bool AtCap(int capSeconds, double targetFps) => _frames.Count >= capSeconds * targetFps;

    /// <summary>Encoder thread only. <see cref="SdrImage.ToBitmapSource"/> already Freezes its
    /// BitmapSource, but BitmapFrame.Create wraps it in a NEW Freezable that is NOT itself frozen —
    /// without an explicit Freeze here, this frame carries thread affinity to the encoder thread
    /// and <see cref="SaveTo"/> (called later from RecordingSession.Stop on the UI thread) throws
    /// "the calling thread cannot access this object because a different thread owns it" the moment
    /// it touches encoder.Frames. Caught by the smoke test: GIF recordings silently produced zero
    /// output file until this fix.</summary>
    public void AddFrame(SdrImage bgra8, ushort delayCentiseconds)
    {
        var frame = BitmapFrame.Create(bgra8.ToBitmapSource());
        if (frame.CanFreeze)
        {
            frame.Freeze();
        }
        _frames.Add((frame, delayCentiseconds));
    }

    public void SaveTo(string path)
    {
        var encoder = new GifBitmapEncoder();
        foreach (var (frame, _) in _frames)
        {
            encoder.Frames.Add(frame);
        }

        using var stream = new MemoryStream();
        encoder.Save(stream);
        byte[] bytes = stream.ToArray();

        bytes = InjectLoopExtension(bytes);
        bytes = PatchFrameDelays(bytes, _frames.Select(f => f.DelayCentiseconds).ToArray());

        File.WriteAllBytes(path, bytes);
    }

    /// <summary>Signature "GIF89a" (6 bytes) + Logical Screen Descriptor (7 bytes) = byte 13 is
    /// where the Global Color Table (if any) begins. The packed field's high bit (gif[10] & 0x80)
    /// signals its presence; its low 3 bits encode the table SIZE as 3 * 2^(N+1) bytes. Shared by
    /// <see cref="InjectLoopExtension"/> (where to insert) and <see cref="PatchFrameDelays"/> (where
    /// the block walk starts).</summary>
    private static int GetHeaderAndGctLength(byte[] gif)
    {
        byte packed = gif[10];
        int len = 13;
        if ((packed & 0x80) != 0)
        {
            len += 3 * (1 << ((packed & 0x07) + 1));
        }
        return len;
    }

    /// <summary>WPF's GifBitmapEncoder never emits the NETSCAPE2.0 Application Extension, so its
    /// output plays ONCE and stops — a well-documented gap. Inserts the standard 19-byte block
    /// right after the Logical Screen Descriptor (+ Global Color Table, if present), before the
    /// first real block (a Graphic Control Extension or Image Descriptor). Public (not private) so
    /// GifEncoderTests can verify the byte layout directly against a hand-built minimal GIF header
    /// without going through a live WPF encode.</summary>
    public static byte[] InjectLoopExtension(byte[] gif)
    {
        int insertAt = GetHeaderAndGctLength(gif);

        byte[] netscape =
        {
            0x21, 0xFF, 0x0B,
            (byte)'N', (byte)'E', (byte)'T', (byte)'S', (byte)'C', (byte)'A', (byte)'P', (byte)'E', (byte)'2', (byte)'.', (byte)'0',
            0x03, 0x01, 0x00, 0x00, // sub-block: loop count LE16 = 0 => infinite
            0x00,                   // block terminator
        };

        var result = new byte[gif.Length + netscape.Length];
        Array.Copy(gif, 0, result, 0, insertAt);
        Array.Copy(netscape, 0, result, insertAt, netscape.Length);
        Array.Copy(gif, insertAt, result, insertAt + netscape.Length, gif.Length - insertAt);
        return result;
    }

    /// <summary>Overwrites each Graphic Control Extension's 2-byte delay field, in frame order, with
    /// the actual measured inter-frame gap (so playback speed matches what was really captured even
    /// if a frame or two was dropped under load) — WPF's encoder does emit one GCE per frame by
    /// default, just with delay=0 unless told otherwise. Public for the same testability reason as
    /// <see cref="InjectLoopExtension"/>.
    ///
    /// Walks the actual GIF block structure (extension/image-descriptor/trailer) instead of scanning
    /// raw bytes for the 0x21,0xF9,0x04 marker: that 3-byte sequence is not a block boundary — it can
    /// and does occur by coincidence inside a frame's own LZW-compressed pixel data (verified against
    /// a live WPF-encoded 200-frame GIF: a naive byte scan found 202 matches for 200 real frames).
    /// A false-positive match there would both corrupt that frame's compressed stream (writing 2
    /// arbitrary "delay" bytes into the middle of it) and steal a delay-array slot meant for a later
    /// real frame, leaving the recording's trailing frame(s) at delay=0. Walking declared block sizes
    /// instead means only bytes that are actually block-boundary bytes are ever inspected.</summary>
    public static byte[] PatchFrameDelays(byte[] gif, ushort[] delaysCentiseconds)
    {
        var result = (byte[])gif.Clone();
        int pos = GetHeaderAndGctLength(gif);
        int frameIndex = 0;

        while (pos < result.Length && frameIndex < delaysCentiseconds.Length)
        {
            byte marker = result[pos];
            if (marker == 0x3B) // Trailer — end of stream
            {
                break;
            }
            if (marker == 0x21) // Extension Introducer: 0x21, label, data sub-blocks..., 0x00 terminator
            {
                if (result[pos + 1] == 0xF9) // Graphic Control Extension — always exactly one 4-byte sub-block
                {
                    ushort delay = delaysCentiseconds[frameIndex++];
                    result[pos + 4] = (byte)(delay & 0xFF);
                    result[pos + 5] = (byte)(delay >> 8);
                }
                pos = SkipSubBlocks(result, pos + 2); // past introducer + label, to the sub-block chain
            }
            else if (marker == 0x2C) // Image Descriptor: fixed fields, optional Local Color Table, LZW data
            {
                byte packed = result[pos + 9];
                int dataStart = pos + 10;
                if ((packed & 0x80) != 0)
                {
                    dataStart += 3 * (1 << ((packed & 0x07) + 1)); // Local Color Table
                }
                pos = SkipSubBlocks(result, dataStart + 1); // +1 past the LZW minimum code size byte
            }
            else
            {
                // Not a recognized block-boundary byte — the stream isn't laid out the way
                // GifBitmapEncoder is known to emit it. Bail out rather than mis-walk the rest and
                // risk writing into arbitrary data; whatever delays were already patched stand.
                break;
            }
        }
        return result;
    }

    /// <summary>Advances past a chain of GIF data sub-blocks starting at <paramref name="pos"/> (each
    /// a 1-byte size N followed by N data bytes), stopping just after the 0x00 terminator. Shared by
    /// the Extension and Image Descriptor cases in <see cref="PatchFrameDelays"/> — both end in
    /// exactly this shape.</summary>
    private static int SkipSubBlocks(byte[] gif, int pos)
    {
        while (gif[pos] != 0x00)
        {
            int size = gif[pos];
            pos += 1 + size;
        }
        return pos + 1; // past the terminator
    }
}
