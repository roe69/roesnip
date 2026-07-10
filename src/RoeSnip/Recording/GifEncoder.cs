using System;
using System.IO;
using System.Windows.Media.Imaging;
using RoeSnip.Imaging;

namespace RoeSnip.Recording;

/// <summary>Streams frames straight to a temp file as an animated GIF instead of batching them
/// (the old design held every frame fully decoded in memory, ~W*H*4 bytes each, until Stop() —
/// ~1-1.5 GB/min at typical recording sizes; see IdleMemoryTrimmer's doc comment for the fallout).
/// Each frame is encoded alone via WPF's <see cref="GifBitmapEncoder"/> (it has no incremental
/// multi-Save API, so there is no way to feed it frames one at a time and keep its own output
/// streaming), then this class extracts just that single frame's GCE + Image Descriptor + Local
/// Color Table + LZW data and appends them to the output file. Net effect: flat memory (one
/// frame's worth of encode scratch at a time) instead of the whole recording.
///
/// Encoder thread only for <see cref="AddFrame"/> and <see cref="FinalizeAndClose"/> — <see cref="Create"/>
/// runs on the UI thread before the encoder thread starts, and <see cref="Dispose"/> runs on the UI
/// thread after the encoder thread has joined, matching Mp4Encoder's documented discipline. No
/// locking: the two threads never touch the instance concurrently.</summary>
public sealed class GifEncoder : IDisposable
{
    private readonly FileStream _stream;
    private readonly int _width;
    private readonly int _height;
    private readonly MemoryStream _frameScratch = new();
    private bool _closed;

    private GifEncoder(FileStream stream, int width, int height)
    {
        _stream = stream;
        _width = width;
        _height = height;
    }

    /// <summary>Opens <paramref name="tempFilePath"/> and writes everything that comes before the
    /// first frame: the GIF89a header, a Logical Screen Descriptor with no Global Color Table
    /// (every frame carries its own Local Color Table instead — see <see cref="AddFrame"/>), and
    /// the NETSCAPE2.0 loop-forever extension (WPF's encoder never emits this on its own, a
    /// well-documented gap; without it the GIF plays once and stops).</summary>
    public static GifEncoder Create(string tempFilePath, int width, int height)
    {
        var stream = new FileStream(tempFilePath, FileMode.Create, FileAccess.Write, FileShare.None);

        byte[] header =
        {
            (byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9', (byte)'a',
            (byte)(width & 0xFF), (byte)(width >> 8),
            (byte)(height & 0xFF), (byte)(height >> 8),
            0x00, // packed: no Global Color Table
            0x00, // background color index
            0x00, // pixel aspect ratio
        };
        stream.Write(header, 0, header.Length);

        byte[] netscape =
        {
            0x21, 0xFF, 0x0B,
            (byte)'N', (byte)'E', (byte)'T', (byte)'S', (byte)'C', (byte)'A', (byte)'P', (byte)'E', (byte)'2', (byte)'.', (byte)'0',
            0x03, 0x01, 0x00, 0x00, // sub-block: loop count LE16 = 0 => infinite
            0x00,                   // block terminator
        };
        stream.Write(netscape, 0, netscape.Length);

        return new GifEncoder(stream, width, height);
    }

    /// <summary>Encoder thread only. Encodes this one frame in isolation via WPF's GifBitmapEncoder
    /// (see <see cref="SdrImage.ToBitmapSource"/> for the BGRA8 -> BitmapSource conversion and why
    /// Freeze is required before crossing back to the caller's expectations), then extracts and
    /// appends just the per-frame blocks to the output stream — see <see cref="ExtractFrameBlocks"/>
    /// for the byte-layout details.</summary>
    public void AddFrame(SdrImage frame, ushort delayCentiseconds)
    {
        var bitmapFrame = BitmapFrame.Create(frame.ToBitmapSource());
        if (bitmapFrame.CanFreeze)
        {
            bitmapFrame.Freeze();
        }

        var encoder = new GifBitmapEncoder();
        encoder.Frames.Add(bitmapFrame);

        _frameScratch.SetLength(0);
        encoder.Save(_frameScratch);

        byte[] blocks = ExtractFrameBlocks(_frameScratch.GetBuffer(), (int)_frameScratch.Length, delayCentiseconds, _width, _height);
        _stream.Write(blocks, 0, blocks.Length);
    }

    /// <summary>Pulls the per-frame GCE + Image Descriptor + Local Color Table + LZW data out of a
    /// single-frame GIF produced by <see cref="GifBitmapEncoder"/>, patching the delay and disposal
    /// method along the way. Public (not private) so GifEncoderTests can verify this — the one
    /// genuinely new piece of logic here — directly against hand-built single-frame GIFs, including
    /// the global-color-table reassembly branch a live WIC encode may or may not exercise.
    ///
    /// Reuses <see cref="GetHeaderAndGctLength"/> and <see cref="SkipSubBlocks"/>, both carried over
    /// unchanged from the old batch implementation's block-walk machinery.</summary>
    public static byte[] ExtractFrameBlocks(byte[] gif, int length, ushort delayCentiseconds, int expectedWidth, int expectedHeight)
    {
        int pos = GetHeaderAndGctLength(gif);
        byte[] sourceGct = Array.Empty<byte>();
        if ((gif[10] & 0x80) != 0)
        {
            int gctSize = 3 * (1 << ((gif[10] & 0x07) + 1));
            sourceGct = new byte[gctSize];
            Array.Copy(gif, 13, sourceGct, 0, gctSize);
        }

        byte[]? gce = null;
        byte[]? descriptorAndData = null;

        while (pos < length)
        {
            byte marker = gif[pos];
            if (marker == 0x3B) // Trailer
            {
                break;
            }
            if (marker == 0x21) // Extension
            {
                if (gif[pos + 1] == 0xF9) // Graphic Control Extension
                {
                    gce = BuildGce(gif, pos, delayCentiseconds);
                }
                pos = SkipSubBlocks(gif, pos + 2);
            }
            else if (marker == 0x2C) // Image Descriptor
            {
                descriptorAndData = BuildDescriptorAndData(gif, pos, length, expectedWidth, expectedHeight, sourceGct);
                break; // exactly one frame in this GIF — nothing after its data matters
            }
            else
            {
                throw new InvalidOperationException(
                    $"Unrecognized GIF block marker 0x{marker:X2} at offset {pos} in single-frame encode output.");
            }
        }

        gce ??= new byte[] { 0x21, 0xF9, 0x04, 0x04, (byte)(delayCentiseconds & 0xFF), (byte)(delayCentiseconds >> 8), 0x00, 0x00 };

        if (descriptorAndData is null)
        {
            throw new InvalidOperationException("Single-frame GIF encode produced no Image Descriptor.");
        }

        var result = new byte[gce.Length + descriptorAndData.Length];
        Array.Copy(gce, 0, result, 0, gce.Length);
        Array.Copy(descriptorAndData, 0, result, gce.Length, descriptorAndData.Length);
        return result;
    }

    /// <summary>Copies an existing GCE, patching the delay (bytes +4/+5) and forcing the disposal
    /// method to "do not dispose" (0x04 in packed-byte bits 2-4: packed = (packed &amp; ~0x1C) | 0x04
    /// — leaving a frame's own pixels as the background for the next one is exactly what a naive
    /// frame-by-frame append needs) while leaving the transparency flag bit and transparent color
    /// index byte exactly as WIC wrote them.</summary>
    private static byte[] BuildGce(byte[] gif, int pos, ushort delayCentiseconds)
    {
        var gce = new byte[8];
        Array.Copy(gif, pos, gce, 0, 8);
        gce[3] = (byte)((gce[3] & ~0x1C) | 0x04);
        gce[4] = (byte)(delayCentiseconds & 0xFF);
        gce[5] = (byte)(delayCentiseconds >> 8);
        return gce;
    }

    /// <summary>Validates the Image Descriptor's geometry, then either copies it (+ its Local Color
    /// Table + LZW data) verbatim, or — when WIC put the single frame's palette in a GLOBAL color
    /// table instead of a local one — synthesizes an equivalent descriptor with the LCT-present bit
    /// set and the source GCT re-emitted as this frame's LCT.</summary>
    private static byte[] BuildDescriptorAndData(byte[] gif, int pos, int length, int expectedWidth, int expectedHeight, byte[] sourceGct)
    {
        int left = gif[pos + 1] | (gif[pos + 2] << 8);
        int top = gif[pos + 3] | (gif[pos + 4] << 8);
        int width = gif[pos + 5] | (gif[pos + 6] << 8);
        int height = gif[pos + 7] | (gif[pos + 8] << 8);
        byte packed = gif[pos + 9];

        if (left != 0 || top != 0)
        {
            throw new InvalidOperationException($"Single-frame GIF encode has a non-zero image offset ({left},{top}); expected (0,0).");
        }
        if (width != expectedWidth || height != expectedHeight)
        {
            throw new InvalidOperationException(
                $"Single-frame GIF encode is {width}x{height}; expected {expectedWidth}x{expectedHeight}.");
        }
        if ((packed & 0x40) != 0) // interlace flag
        {
            throw new InvalidOperationException("Single-frame GIF encode is interlaced; streaming append does not support this.");
        }

        if ((packed & 0x80) != 0)
        {
            // Already carries its own Local Color Table — copy descriptor + LCT + LZW data verbatim.
            int lctSize = 3 * (1 << ((packed & 0x07) + 1));
            int dataStart = pos + 10 + lctSize;
            int dataEnd = SkipSubBlocks(gif, dataStart + 1); // +1 past the LZW min-code-size byte
            var result = new byte[dataEnd - pos];
            Array.Copy(gif, pos, result, 0, result.Length);
            return result;
        }
        else
        {
            // No LCT on the descriptor itself — WIC put the (single frame's) palette in the file's
            // Global Color Table instead. Re-emit it as this frame's Local Color Table so the frame
            // stays self-contained (this GIF carries no Global Color Table of its own — see Create).
            if (sourceGct.Length == 0)
            {
                throw new InvalidOperationException("Single-frame GIF encode has neither a Local nor a Global Color Table.");
            }
            byte gctSizeBits = (byte)(gif[10] & 0x07);
            byte newPacked = (byte)(packed | 0x80 | gctSizeBits);

            int dataStart = pos + 10;
            int dataEnd = SkipSubBlocks(gif, dataStart + 1);

            var result = new byte[10 + sourceGct.Length + (dataEnd - dataStart)];
            Array.Copy(gif, pos, result, 0, 9); // left/top/width/height, unpacked fields
            result[9] = newPacked;
            Array.Copy(sourceGct, 0, result, 10, sourceGct.Length);
            Array.Copy(gif, dataStart, result, 10 + sourceGct.Length, dataEnd - dataStart);
            return result;
        }
    }

    /// <summary>Writes the trailer byte and flushes/closes the output stream. Idempotent — safe to
    /// call even if a prior call (or <see cref="Dispose"/>) already closed the stream, since Stop()
    /// paths and exception-cleanup paths can both reach this.</summary>
    public void FinalizeAndClose()
    {
        if (_closed)
        {
            return;
        }
        _stream.WriteByte(0x3B);
        _stream.Flush();
        _stream.Dispose();
        _frameScratch.Dispose();
        _closed = true;
    }

    /// <summary>UI thread, after the encoder thread has joined. Idempotent and safe to call after
    /// <see cref="FinalizeAndClose"/> already ran (e.g. a failed Start() cleanup path that disposes
    /// unconditionally) — closing an already-closed stream is a no-op.</summary>
    public void Dispose()
    {
        if (_closed)
        {
            return;
        }
        _stream.Dispose();
        _frameScratch.Dispose();
        _closed = true;
    }

    /// <summary>Signature "GIF89a" (6 bytes) + Logical Screen Descriptor (7 bytes) = byte 13 is
    /// where the Global Color Table (if any) begins. The packed field's high bit (gif[10] & 0x80)
    /// signals its presence; its low 3 bits encode the table SIZE as 3 * 2^(N+1) bytes. Carried over
    /// unchanged from the old batch implementation.</summary>
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

    /// <summary>Advances past a chain of GIF data sub-blocks starting at <paramref name="pos"/> (each
    /// a 1-byte size N followed by N data bytes), stopping just after the 0x00 terminator. Carried
    /// over unchanged from the old batch implementation.</summary>
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
