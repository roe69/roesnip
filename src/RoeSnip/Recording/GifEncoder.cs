using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using RoeSnip.Capture;
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
/// Size/smoothness scheme (the timestamped <see cref="AddFrame(SdrImage, long)"/> overload — the
/// recording path): high capture fps only costs bytes where pixels actually changed. Each incoming
/// frame is diffed against the previously EMITTED one; an identical frame is skipped outright, a
/// changed one is cropped to its changed bounding box and written as a GIF sub-rect frame (the
/// disposal method is already "do not dispose", so untouched canvas pixels persist). Frame delays
/// are exact-by-construction: a frame is written with a provisional delay and PATCHED to its real
/// display duration (next emit's timestamp minus its own, whole centiseconds with carried
/// remainder, 2cs floor — browsers clamp 0/1cs to 10cs) once the next frame lands; the temp
/// FileStream is seekable, so the patch is a seek-back write. Skipped-frame time folds into that
/// same patch for free. The raw <see cref="AddFrame(SdrImage, ushort)"/> primitive appends a
/// full-canvas frame verbatim (tests use it); do not interleave the two overloads on one instance.
///
/// Encoder thread only for <see cref="AddFrame(SdrImage, long)"/> and <see cref="FinalizeAndClose"/> — <see cref="Create"/>
/// runs on the UI thread before the encoder thread starts, and <see cref="Dispose"/> runs on the UI
/// thread after the encoder thread has joined, matching Mp4Encoder's documented discipline. No
/// locking: the two threads never touch the instance concurrently.</summary>
public sealed class GifEncoder : IDisposable
{
    /// <summary>Provisional delay written with every frame before its real duration is known. Every
    /// frame's delay is later patched: mid-take by the next emit, the final frame's by
    /// <see cref="FinalizeAndClose(long)"/>'s stop timestamp (the parameterless finalize skips that
    /// and leaves the final frame provisional).</summary>
    private const ushort ProvisionalDelayCs = 3;
    /// <summary>Browsers treat 0-1cs as "broken" and clamp to 10cs; 2cs (50fps) is the floor.</summary>
    private const int MinDelayCs = 2;

    private readonly FileStream _stream;
    private readonly int _width;
    private readonly int _height;
    private readonly long _timestampTicksPerSecond;
    private readonly MemoryStream _frameScratch = new();
    private bool _closed;

    private byte[]? _prevPixels;         // full-canvas pixels of the last EMITTED frame
    private long _lastEmitTimestampTicks;
    private long _lastGceDelayOffset = -1; // file offset of the last frame's GCE delay LE16
    private double _delayCarryCs;          // sub-centisecond remainder so rounding never drifts

    private GifEncoder(FileStream stream, int width, int height, long timestampTicksPerSecond)
    {
        _stream = stream;
        _width = width;
        _height = height;
        _timestampTicksPerSecond = timestampTicksPerSecond;
    }

    /// <summary>Opens <paramref name="tempFilePath"/> and writes everything that comes before the
    /// first frame: the GIF89a header, a Logical Screen Descriptor with no Global Color Table
    /// (every frame carries its own Local Color Table instead — see <see cref="AddFrame"/>), and
    /// the NETSCAPE2.0 loop-forever extension (WPF's encoder never emits this on its own, a
    /// well-documented gap; without it the GIF plays once and stops).</summary>
    public static GifEncoder Create(string tempFilePath, int width, int height, long timestampTicksPerSecond = 0)
    {
        if (timestampTicksPerSecond <= 0)
        {
            timestampTicksPerSecond = System.Diagnostics.Stopwatch.Frequency;
        }
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

        return new GifEncoder(stream, width, height, timestampTicksPerSecond);
    }

    /// <summary>Encoder thread only — the recording path. Diffs <paramref name="frame"/> (full
    /// canvas, same size every call) against the last emitted frame: identical frames are skipped
    /// (their display time folds into the previous frame's patched delay), changed frames are
    /// cropped to the changed bounding box and appended as a sub-rect frame. See the class doc for
    /// the delay patch-behind scheme. <paramref name="timestampTicks"/> is the frame's capture
    /// timestamp in <see cref="_timestampTicksPerSecond"/> units, monotonic across the take.</summary>
    public void AddFrame(SdrImage frame, long timestampTicks)
    {
        if (frame.Width != _width || frame.Height != _height)
        {
            throw new ArgumentException($"Frame is {frame.Width}x{frame.Height}; canvas is {_width}x{_height}.", nameof(frame));
        }

        if (_prevPixels is null)
        {
            // First frame establishes the whole canvas.
            EmitFrame(frame, RectPhysical.FromSize(0, 0, _width, _height));
            _prevPixels = frame.Pixels;
            _lastEmitTimestampTicks = timestampTicks;
            return;
        }

        if (!TryGetChangedBounds(_prevPixels, frame.Pixels, _width, _height, out var box))
        {
            return; // nothing changed — the previous frame just keeps displaying
        }

        PatchLastDelay(timestampTicks);
        EmitFrame(frame, box);
        _prevPixels = frame.Pixels; // full canvas, so future diffs see the true composite
        _lastEmitTimestampTicks = timestampTicks;
    }

    /// <summary>Raw primitive: appends <paramref name="frame"/> (must be full-canvas) verbatim with
    /// a fixed delay — no diffing, no delay patching. Kept for tests; the recording path uses the
    /// timestamped overload, and the two must not be interleaved on one instance.</summary>
    public void AddFrame(SdrImage frame, ushort delayCentiseconds)
    {
        if (frame.Width != _width || frame.Height != _height)
        {
            throw new ArgumentException($"Frame is {frame.Width}x{frame.Height}; canvas is {_width}x{_height}.", nameof(frame));
        }
        EncodeAndWrite(frame, RectPhysical.FromSize(0, 0, frame.Width, frame.Height), delayCentiseconds);
    }

    /// <summary>Writes the frame with the provisional delay and remembers where that delay lives in
    /// the file so <see cref="PatchLastDelay"/> can rewrite it once the real duration is known.</summary>
    private void EmitFrame(SdrImage canvasFrame, RectPhysical box)
    {
        _lastGceDelayOffset = _stream.Position + 4; // GCE = 21 F9 04 packed delayLo delayHi ...
        EncodeAndWrite(canvasFrame, box, ProvisionalDelayCs);
    }

    /// <summary>Rewrites the previous frame's GCE delay to its actual display duration, in whole
    /// centiseconds with the sub-centisecond remainder carried forward (so long runs never drift),
    /// floored at <see cref="MinDelayCs"/>.</summary>
    private void PatchLastDelay(long nowTicks)
    {
        if (_lastGceDelayOffset < 0)
        {
            return;
        }
        // Max(0, ...): a frame drained after a Resume can carry a pause-adjusted timestamp slightly
        // behind the previous emit's — a negative span must not poison the carry.
        double exactCs = Math.Max(0, nowTicks - _lastEmitTimestampTicks) * 100.0 / _timestampTicksPerSecond + _delayCarryCs;
        int delay = (int)Math.Clamp(Math.Round(exactCs), MinDelayCs, ushort.MaxValue);
        _delayCarryCs = Math.Clamp(exactCs - delay, -10.0, 10.0);

        long end = _stream.Position;
        _stream.Seek(_lastGceDelayOffset, SeekOrigin.Begin);
        _stream.WriteByte((byte)(delay & 0xFF));
        _stream.WriteByte((byte)(delay >> 8));
        _stream.Seek(end, SeekOrigin.Begin);
    }

    /// <summary>Encodes one frame in isolation via WPF's GifBitmapEncoder (see
    /// <see cref="SdrImage.ToBitmapSource"/> for the BGRA8 -> BitmapSource conversion and why
    /// Freeze is required before crossing back to the caller's expectations), then writes just the
    /// per-frame blocks straight to the output stream — see <see cref="WriteFrameBlocks"/> for the
    /// byte-layout details.
    ///
    /// Writes directly to <see cref="_stream"/> instead of building an intermediate concatenated
    /// byte[] (the old shape, still kept as <see cref="ExtractFrameBlocks"/> for tests): that array
    /// was sized to the compressed frame's byte count, which for a real desktop capture routinely
    /// exceeds the 85,000-byte Large Object Heap threshold — at recording cadence that
    /// meant a fresh LOH allocation, and thus LOH/Gen2 pressure, every single frame for the whole
    /// recording. Unlike Mp4Encoder.WriteFrame (whose per-frame buffer is native Media Foundation
    /// memory, never touching the managed heap), GifBitmapEncoder's output only exists as managed
    /// bytes, so this recording's whole managed-allocation profile was dominated by an array that
    /// was written once and immediately discarded. A blocking Gen2/LOH collection stops every
    /// managed thread, including the UI thread — which is what made RegionOutline's WM_NCHITTEST
    /// hit-testing (and so click-through into the app being recorded) intermittently unresponsive
    /// specifically during GIF takes, never during MP4 ones.</summary>
    private void EncodeAndWrite(SdrImage canvasFrame, RectPhysical box, ushort delayCentiseconds)
    {
        if (box.Left < 0 || box.Top < 0 || box.Right > _width || box.Bottom > _height)
        {
            throw new ArgumentOutOfRangeException(nameof(box),
                $"Frame rect {box} exceeds the {_width}x{_height} canvas.");
        }

        // Three source shapes, chosen to keep per-frame managed allocations out of the LOH (the
        // f7aa9a3 lesson: LOH churn at recording cadence = Gen2 pauses = frozen UI hit-testing):
        // full canvas encodes directly; a small box gets a cheap gen0 Crop; a large box goes
        // through CroppedBitmap, which reads the full-canvas source strided at encode time instead
        // of materializing a second LOH-sized managed copy.
        const int lohThresholdBytes = 85_000;
        BitmapSource source;
        if (box.Width == _width && box.Height == _height)
        {
            source = canvasFrame.ToBitmapSource();
        }
        else if (box.Width * 4 * box.Height < lohThresholdBytes)
        {
            source = canvasFrame.Crop(box).ToBitmapSource();
        }
        else
        {
            source = new CroppedBitmap(canvasFrame.ToBitmapSource(), new System.Windows.Int32Rect(box.Left, box.Top, box.Width, box.Height));
        }

        var bitmapFrame = BitmapFrame.Create(source);
        if (bitmapFrame.CanFreeze)
        {
            bitmapFrame.Freeze();
        }

        var encoder = new GifBitmapEncoder();
        encoder.Frames.Add(bitmapFrame);

        _frameScratch.SetLength(0);
        encoder.Save(_frameScratch);

        WriteFrameBlocks(_stream, _frameScratch.GetBuffer(), (int)_frameScratch.Length, delayCentiseconds, box.Width, box.Height, box.Left, box.Top);
    }

    /// <summary>Finds the tight bounding box of pixels that differ between two same-sized BGRA8
    /// canvases (compared as whole 32-bit pixels — SdrImage alpha is always 255, so this is a color
    /// compare). Returns false when the frames are identical. Row scans are vectorized
    /// (SequenceEqual); the column scans are bounded by the best left/right found so far — cheap
    /// when changes cluster (a full-frame change costs one row of scalar work), though adversarial
    /// content (every row's only diffs hugging one edge) can degrade a scan toward
    /// width-per-changed-row; the vectorized row pass still gates all of it to changed rows.</summary>
    public static bool TryGetChangedBounds(byte[] prev, byte[] cur, int width, int height, out RectPhysical bounds)
    {
        if (prev.Length != cur.Length || prev.Length != width * 4 * height)
        {
            throw new ArgumentException("Canvas buffers must match width*4*height.", nameof(cur));
        }
        var p = MemoryMarshal.Cast<byte, uint>(prev.AsSpan());
        var c = MemoryMarshal.Cast<byte, uint>(cur.AsSpan());

        int top = -1;
        for (int y = 0; y < height; y++)
        {
            if (!p.Slice(y * width, width).SequenceEqual(c.Slice(y * width, width)))
            {
                top = y;
                break;
            }
        }
        if (top < 0)
        {
            bounds = default;
            return false;
        }

        int bottom = top;
        for (int y = height - 1; y > top; y--)
        {
            if (!p.Slice(y * width, width).SequenceEqual(c.Slice(y * width, width)))
            {
                bottom = y;
                break;
            }
        }

        int left = width, right = -1;
        for (int y = top; y <= bottom; y++)
        {
            var pr = p.Slice(y * width, width);
            var cr = c.Slice(y * width, width);
            for (int x = 0; x < left; x++)
            {
                if (pr[x] != cr[x])
                {
                    left = x;
                    break;
                }
            }
            for (int x = width - 1; x > right; x--)
            {
                if (pr[x] != cr[x])
                {
                    right = x;
                    break;
                }
            }
        }

        bounds = RectPhysical.FromSize(left, top, right - left + 1, bottom - top + 1);
        return true;
    }

    /// <summary>Pulls the per-frame GCE + Image Descriptor + Local Color Table + LZW data out of a
    /// single-frame GIF produced by <see cref="GifBitmapEncoder"/>, patching the delay and disposal
    /// method along the way, and returns them concatenated into one array. Public (not private) so
    /// GifEncoderTests can verify this — the one genuinely new piece of logic here — directly against
    /// hand-built single-frame GIFs, including the global-color-table reassembly branch a live WIC
    /// encode may or may not exercise. A thin allocating wrapper around <see cref="WriteFrameBlocks"/>,
    /// which is what <see cref="AddFrame"/> actually calls on the hot path — tests only need a byte[]
    /// to assert against, they don't run at recording cadence.</summary>
    public static byte[] ExtractFrameBlocks(byte[] gif, int length, ushort delayCentiseconds, int expectedWidth, int expectedHeight, int frameLeft = 0, int frameTop = 0)
    {
        using var ms = new MemoryStream();
        WriteFrameBlocks(ms, gif, length, delayCentiseconds, expectedWidth, expectedHeight, frameLeft, frameTop);
        return ms.ToArray();
    }

    /// <summary>Same block-walk as the old <see cref="ExtractFrameBlocks"/>, but WRITES each piece
    /// straight to <paramref name="output"/> instead of copying it into an intermediate array first
    /// — see <see cref="AddFrame"/>'s doc comment for why that matters. The only allocation left is
    /// the fixed 8-byte GCE (trivial, never LOH-sized): everything else (descriptor, Local Color
    /// Table, LZW data) is written as a direct slice of <paramref name="gif"/>, which is itself
    /// <see cref="_frameScratch"/>'s already-allocated, frame-to-frame-reused buffer.
    ///
    /// Reuses <see cref="GetHeaderAndGctLength"/> and <see cref="SkipSubBlocks"/>, both carried over
    /// unchanged from the old batch implementation's block-walk machinery.</summary>
    private static void WriteFrameBlocks(Stream output, byte[] gif, int length, ushort delayCentiseconds, int expectedWidth, int expectedHeight, int frameLeft, int frameTop)
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
        bool wroteDescriptor = false;

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
                gce ??= new byte[] { 0x21, 0xF9, 0x04, 0x04, (byte)(delayCentiseconds & 0xFF), (byte)(delayCentiseconds >> 8), 0x00, 0x00 };
                output.Write(gce, 0, gce.Length);
                WriteDescriptorAndData(output, gif, pos, length, expectedWidth, expectedHeight, frameLeft, frameTop, sourceGct);
                wroteDescriptor = true;
                break; // exactly one frame in this GIF — nothing after its data matters
            }
            else
            {
                throw new InvalidOperationException(
                    $"Unrecognized GIF block marker 0x{marker:X2} at offset {pos} in single-frame encode output.");
            }
        }

        if (!wroteDescriptor)
        {
            throw new InvalidOperationException("Single-frame GIF encode produced no Image Descriptor.");
        }
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

    /// <summary>Validates the Image Descriptor's geometry, then either writes it (+ its Local Color
    /// Table + LZW data) verbatim, or — when WIC put the single frame's palette in a GLOBAL color
    /// table instead of a local one — synthesizes an equivalent descriptor with the LCT-present bit
    /// set and the source GCT re-emitted as this frame's LCT. The descriptor's left/top are patched
    /// to <paramref name="frameLeft"/>/<paramref name="frameTop"/> — WIC encoded a standalone image
    /// at (0,0), but on the animation's canvas this frame is a changed-region sub-rect. Writes
    /// straight to <paramref name="output"/> — see <see cref="WriteFrameBlocks"/>'s doc comment for
    /// why: every byte written here is either a slice of <paramref name="gif"/> (zero-copy) or one
    /// of a handful of small fixed-size writes (the 10-byte descriptor, never LOH-sized), never a
    /// fresh allocation sized to the whole frame.</summary>
    private static void WriteDescriptorAndData(Stream output, byte[] gif, int pos, int length, int expectedWidth, int expectedHeight, int frameLeft, int frameTop, byte[] sourceGct)
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
            // Already carries its own Local Color Table — write the position-patched descriptor,
            // then LCT + LZW data verbatim straight out of gif (a slice of the caller's own
            // buffer, not a copy).
            int lctSize = 3 * (1 << ((packed & 0x07) + 1));
            int dataStart = pos + 10 + lctSize;
            int dataEnd = SkipSubBlocks(gif, dataStart + 1); // +1 past the LZW min-code-size byte
            WriteDescriptorHeader(output, gif, pos, frameLeft, frameTop, packed);
            output.Write(gif, pos + 10, dataEnd - (pos + 10));
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

            WriteDescriptorHeader(output, gif, pos, frameLeft, frameTop, newPacked);
            output.Write(sourceGct, 0, sourceGct.Length);
            output.Write(gif, dataStart, dataEnd - dataStart);
        }
    }

    /// <summary>The 10-byte Image Descriptor: 0x2C, left/top rewritten to the frame's canvas
    /// position, width/height copied from the encode, and the caller's packed byte.</summary>
    private static void WriteDescriptorHeader(Stream output, byte[] gif, int pos, int frameLeft, int frameTop, byte packed)
    {
        output.WriteByte(0x2C);
        output.WriteByte((byte)(frameLeft & 0xFF));
        output.WriteByte((byte)(frameLeft >> 8));
        output.WriteByte((byte)(frameTop & 0xFF));
        output.WriteByte((byte)(frameTop >> 8));
        output.Write(gif, pos + 5, 4); // width/height LE16 pair, as encoded
        output.WriteByte(packed);
    }

    /// <summary>Recording-path finalize: patches the LAST frame's delay to its real remaining
    /// display time (last emit to <paramref name="endTimestampTicks"/>, the take's stop moment) —
    /// without this, a recording that ends on a static tail (the common "hold the result, then
    /// Stop" flow) would snap off its final frame after the 3cs provisional delay on every loop —
    /// then writes the trailer and closes.</summary>
    public void FinalizeAndClose(long endTimestampTicks)
    {
        if (!_closed)
        {
            PatchLastDelay(endTimestampTicks);
        }
        FinalizeAndClose();
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
