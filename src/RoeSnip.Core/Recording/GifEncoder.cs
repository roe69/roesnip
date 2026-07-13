using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using RoeSnip.Core.Capture;
using RoeSnip.Core.Imaging;
using RoeSnip.Core.Recording.Gif;

namespace RoeSnip.Core.Recording;

/// <summary>Streams frames straight to a temp file as an animated GIF, building the LZW-compressed
/// bytes itself instead of round-tripping every frame through WPF's <c>GifBitmapEncoder</c> (the old
/// design — see git history for what that cost: a fresh managed byte[] per frame, routinely over the
/// 85,000-byte LOH threshold, at recording cadence). The whole delta/palette pipeline lives in
/// <c>RoeSnip.Core.Recording.Gif</c> (<see cref="GifOctreeQuantizer"/>, <see cref="GifNearestColorLut"/>,
/// <see cref="GifDelta"/>, <see cref="GifLzwEncoder"/>) — this class is orchestration only: it owns
/// the frame-to-frame state (the diff baseline, the current palette, the reusable scratch buffers)
/// and assembles each frame's GCE + Image Descriptor + Local Color Table + LZW data directly onto the
/// output stream.
///
/// PORTABLE CORE ENGINE: this class holds the entire from-scratch GIF pipeline (no WPF/PresentationCore
/// dependency anywhere — this file never used WPF's GifBitmapEncoder in the first place; that
/// reference in this doc comment and GifLzwEncoder's own is purely historical, describing the design
/// this pipeline replaced). The WPF app's own <c>RoeSnip.Recording.GifEncoder</c> is a thin wrapper
/// around this class: it exists only so the WPF-side <c>RoeSnip.Imaging.SdrImage</c> type (which
/// carries a WPF-only <c>ToBitmapSource()</c> plus a recording-cadence <c>reuseOutput</c> allocation
/// optimization this class doesn't need) can keep flowing through the WPF recording pipeline
/// unchanged — see that wrapper's own class doc comment for the adapter shape.
///
/// Size/quality scheme (the timestamped <see cref="AddFrame(SdrImage, long)"/> overload — the
/// recording path): a "last painted" canvas-sized BGRA8 buffer (allocated once, in <see cref="_lastPaintedPixelsBgra"/>)
/// tracks what a viewer's screen would actually show. Every incoming frame is diffed against it with a
/// per-channel tolerance (<see cref="GifEncoderOptions.ChannelTolerance"/>, Chebyshev) via
/// <see cref="TryGetChangedBounds"/> to find a tight changed bounding box; pixels inside that box that
/// are still within tolerance map to a reserved transparent palette index (disposal is already "do not
/// dispose", so untouched canvas pixels keep showing through) rather than being repainted, which is
/// exactly why the baseline must hold SOURCE pixel values, not palette-quantized ones — a pixel sitting
/// just inside tolerance has to keep comparing against the same reference every frame, or constant
/// quantization noise above tolerance would repaint "static" content forever. Per emitted frame, the
/// palette is an up-to-255-color octree built ONLY from the newly-painted pixels (plus one further
/// slot reserved for the transparent index), with a reuse-first fast path: if the existing palette
/// still fits the new painted set within <see cref="GifEncoderOptions.PaletteReuseErrorThreshold"/>,
/// it is kept byte-for-byte (no octree rebuild, no LUT invalidation) — an unavoidable per-frame LCT
/// write either way, since this file carries no Global Color Table for any frame to fall back on.
/// Frame delays are exact-by-construction via a patch-behind scheme: a frame is written with a
/// provisional delay and PATCHED to its real display duration once the next frame's timestamp is
/// known (2cs floor — browsers clamp 0/1cs to 10cs). Every 15 media-clock seconds
/// (<see cref="GifEncoderOptions.EffectiveKeyframeInterval"/>), a frame that would emit anyway
/// instead re-baselines the WHOLE canvas opaquely (fresh palette, no transparency, baseline reset) —
/// keyframes only ever piggyback on an emit that was already happening, so a take that never changes
/// emits nothing and never drifts, by construction. The raw <see cref="AddFrame(SdrImage, ushort)"/>
/// primitive is the same full-canvas opaque quantize+encode path with a caller-supplied fixed delay
/// and no diffing at all (tests use it as an always-full-frame comparison baseline); do not interleave
/// the two overloads on one instance.
///
/// RATE CONTROL LIVES AT CAPTURE CADENCE, NOT HERE (quality/framerate decoupling workstream): this
/// class used to also shape emit rate itself — a candidate frame whose changed bbox covered a large
/// enough fraction of the canvas was held back behind a minimum delay floor, so a GIF's effective
/// framerate on motion-heavy content was an accidental side effect of whichever size/quality tier was
/// picked. That coupling is gone. Every candidate frame that clears the tolerance test above emits
/// immediately (subject only to the keyframe promotion above); how often a candidate frame ARRIVES at
/// all is controlled upstream, by RegionRecorder's own capture-cadence schedule throttle against the
/// user's chosen fps. This class has no framerate opinion of its own anymore.
///
/// LOH/Gen2 discipline: every scratch buffer this class or the Gif/ primitives touch per frame (the
/// diff baseline, the canvas-sized index buffer, the box-sized packed-index buffer, the dense
/// painted-pixel buffer fed to the octree, the palette array, the octree's node arena, the LUT's
/// bucket/version arrays, the LZW encoder's hash table and bit buffers) is a fixed-size field
/// allocated once in <see cref="Create"/> and reused frame to frame — nothing here allocates at
/// recording cadence. That is not a style preference: a Gen2/LOH collection stops every managed
/// thread, including the UI thread, which is what made RegionOutline's WM_NCHITTEST hit-testing (and
/// so click-through into the app being recorded) intermittently unresponsive specifically during GIF
/// takes under the old per-frame-allocating design.
///
/// Encoder thread only for <see cref="AddFrame(SdrImage, long)"/> and <see cref="FinalizeAndClose"/> — <see cref="Create"/>
/// runs on the UI thread before the encoder thread starts, and <see cref="Dispose"/> runs on the UI
/// thread after the encoder thread has joined, matching Mp4Encoder's documented discipline. No
/// locking: the two threads never touch the instance concurrently.</summary>
public sealed class GifEncoder : IDisposable
{
    /// <summary>Provisional delay written with every timestamped frame before its real duration is
    /// known. Patched: mid-take by the next emit, the final frame's by
    /// <see cref="FinalizeAndClose(long)"/>'s stop timestamp (the parameterless finalize skips that
    /// and leaves the final frame provisional).</summary>
    private const ushort ProvisionalDelayCs = 3;
    /// <summary>Browsers treat 0-1cs as "broken" and clamp to 10cs; 2cs (50fps) is the floor.</summary>
    private const int MinDelayCs = 2;
    /// <summary>GIF disposal method 1 = "do not dispose" — a frame's own pixels stay as the
    /// background for the next one, which is what the transparent-index delta scheme needs.</summary>
    private const byte DisposalDoNotDispose = 0x04; // bits 2-4 of the GCE packed byte

    private readonly FileStream _stream;
    private readonly int _width;
    private readonly int _height;
    private readonly long _timestampTicksPerSecond;
    private readonly GifEncoderOptions _options;

    // ---- Frame-to-frame state, all fixed-size and allocated exactly once (see class doc). ----
    private readonly byte[] _lastPaintedPixelsBgra;   // canvas-sized, SOURCE values of painted pixels
    private readonly byte[] _indexScratch;             // canvas-sized, one palette index per pixel
    private readonly byte[] _boxIndexScratch;           // worst-case canvas-sized, box-packed indices
    private readonly byte[] _paintedPixelScratch;        // worst-case canvas-sized dense BGRA quads
    private readonly byte[] _paletteBgr;                  // MaxPaletteColors * 3 bytes
    private readonly GifOctreeQuantizer _quantizer = new();
    private readonly GifNearestColorLut _lut = new();
    private readonly GifLzwEncoder _lzw = new();

    private int _paletteColorCount;
    private bool _hasFirstFrame;
    private bool _closed;

    private long _lastEmitTimestampTicks;
    private long _lastKeyframeTimestampTicks;
    private long _lastGceDelayOffset = -1; // file offset of the last frame's GCE delay LE16
    private double _delayCarryCs;           // sub-centisecond remainder so rounding never drifts

    private GifEncoder(FileStream stream, int width, int height, long timestampTicksPerSecond, GifEncoderOptions options)
    {
        _stream = stream;
        _width = width;
        _height = height;
        _timestampTicksPerSecond = timestampTicksPerSecond;
        _options = options;

        int canvasPixels = width * height;
        _lastPaintedPixelsBgra = new byte[canvasPixels * 4];
        _indexScratch = new byte[canvasPixels];
        _boxIndexScratch = new byte[canvasPixels];
        _paintedPixelScratch = new byte[canvasPixels * 4];
        _paletteBgr = new byte[options.MaxPaletteColors * 3];
    }

    /// <summary>Opens <paramref name="tempFilePath"/> and writes everything that comes before the
    /// first frame: the GIF89a header, a Logical Screen Descriptor with no Global Color Table (every
    /// frame carries its own Local Color Table instead), and the NETSCAPE2.0 loop-forever extension
    /// (without it the GIF plays once and stops).</summary>
    public static GifEncoder Create(string tempFilePath, int width, int height, long timestampTicksPerSecond = 0, GifEncoderOptions? options = null)
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

        return new GifEncoder(stream, width, height, timestampTicksPerSecond, options ?? new GifEncoderOptions());
    }

    /// <summary>Encoder thread only — the recording path. The first call establishes the baseline as
    /// an opaque full-canvas keyframe. Every later call diffs <paramref name="frame"/> against the
    /// last-painted baseline (tolerance-gated — see the class doc); no change means no emit. A change
    /// always emits (no emit-rate shaping here — see the class doc's "rate control lives at capture
    /// cadence" note) and, once 15 media-clock seconds have passed since the last keyframe, is
    /// promoted to a full-canvas re-baseline instead of a changed-region delta.
    ///
    /// Returns true when the frame was EMITTED. Unlike the old design this method no longer retains a
    /// reference to <paramref name="frame"/>.Pixels — the diff baseline is this instance's own
    /// internal buffer, updated pixel-by-pixel for whatever was actually painted — so a double-
    /// buffering caller is free to reuse/overwrite <paramref name="frame"/>.Pixels immediately after
    /// this call returns, on either outcome.</summary>
    public bool AddFrame(SdrImage frame, long timestampTicks)
    {
        ValidateSize(frame);

        if (!_hasFirstFrame)
        {
            var fullCanvas = RectPhysical.FromSize(0, 0, _width, _height);
            EmitTimestamped(frame, fullCanvas, allowTransparency: false, forceRebuild: true);
            _hasFirstFrame = true;
            _lastEmitTimestampTicks = timestampTicks;
            _lastKeyframeTimestampTicks = timestampTicks;
            return true;
        }

        bool changed;
        RectPhysical box;
        if (GifEncoderStageTimings.Enabled)
        {
            long t0 = Stopwatch.GetTimestamp();
            changed = TryGetChangedBounds(_lastPaintedPixelsBgra, frame.Pixels, _width, _height, out box, _options.ChannelTolerance);
            GifEncoderStageTimings.BboxScanTicks += Stopwatch.GetTimestamp() - t0;
        }
        else
        {
            changed = TryGetChangedBounds(_lastPaintedPixelsBgra, frame.Pixels, _width, _height, out box, _options.ChannelTolerance);
        }
        if (!changed)
        {
            return false; // nothing changed beyond tolerance — the previous frame just keeps displaying
        }

        bool isKeyframe = (timestampTicks - _lastKeyframeTimestampTicks) >= (long)(_options.EffectiveKeyframeInterval.TotalSeconds * _timestampTicksPerSecond);

        PatchLastDelay(timestampTicks);
        if (isKeyframe)
        {
            var fullCanvas = RectPhysical.FromSize(0, 0, _width, _height);
            EmitTimestamped(frame, fullCanvas, allowTransparency: false, forceRebuild: true);
            _lastKeyframeTimestampTicks = timestampTicks;
        }
        else
        {
            EmitTimestamped(frame, box, allowTransparency: true, forceRebuild: false);
        }
        _lastEmitTimestampTicks = timestampTicks;
        return true;
    }

    /// <summary>Raw primitive: quantizes and LZW-encodes <paramref name="frame"/> (must be
    /// full-canvas) as a single opaque frame with a fixed delay — no diffing against the baseline, no
    /// delay patching, no keyframe/emit-rate logic. Kept for tests; the recording path uses the
    /// timestamped overload, and the two must not be interleaved on one instance.</summary>
    public void AddFrame(SdrImage frame, ushort delayCentiseconds)
    {
        ValidateSize(frame);
        var fullCanvas = RectPhysical.FromSize(0, 0, frame.Width, frame.Height);
        WriteFrame(frame, fullCanvas, allowTransparency: false, forceRebuild: false, delayCentiseconds);
    }

    private void ValidateSize(SdrImage frame)
    {
        if (frame.Width != _width || frame.Height != _height)
        {
            throw new ArgumentException($"Frame is {frame.Width}x{frame.Height}; canvas is {_width}x{_height}.", nameof(frame));
        }
    }

    /// <summary>Writes the frame with the provisional delay and remembers where that delay lives in
    /// the file so <see cref="PatchLastDelay"/> can rewrite it once the real duration is known.</summary>
    private void EmitTimestamped(SdrImage frame, RectPhysical box, bool allowTransparency, bool forceRebuild)
    {
        _lastGceDelayOffset = WriteFrame(frame, box, allowTransparency, forceRebuild, ProvisionalDelayCs);
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
        double exactCs = Math.Max(0, nowTicks - _lastEmitTimestampTicks) * 100.0 / _timestampTicksPerSecond + _delayCarryCs;
        int delay = (int)Math.Clamp(Math.Round(exactCs), MinDelayCs, ushort.MaxValue);
        _delayCarryCs = Math.Clamp(exactCs - delay, -10.0, 10.0);

        long end = _stream.Position;
        _stream.Seek(_lastGceDelayOffset, SeekOrigin.Begin);
        _stream.WriteByte((byte)(delay & 0xFF));
        _stream.WriteByte((byte)(delay >> 8));
        _stream.Seek(end, SeekOrigin.Begin);
    }

    /// <summary>The one place that turns a frame + box into on-disk bytes: gathers the pixels this
    /// frame actually paints (the whole box when <paramref name="allowTransparency"/> is false — the
    /// keyframe/first-frame/raw-primitive shape, where box is always the full canvas — or a
    /// tolerance-filtered subset of it otherwise), resolves the palette (reuse-first fast path unless
    /// <paramref name="forceRebuild"/>), runs <see cref="GifDelta.ClassifyAndPaint"/> to both classify
    /// and index every pixel in the box in one pass, packs the box's indices out of the canvas-strided
    /// scratch buffer, and writes GCE + Image Descriptor + Local Color Table + LZW data straight to
    /// <see cref="_stream"/>. Returns the file offset of the GCE's delay field, so timestamped callers
    /// can patch it later.</summary>
    private long WriteFrame(SdrImage frame, RectPhysical box, bool allowTransparency, bool forceRebuild, ushort delayCs)
    {
        if (box.Left < 0 || box.Top < 0 || box.Right > _width || box.Bottom > _height)
        {
            throw new ArgumentOutOfRangeException(nameof(box), $"Frame rect {box} exceeds the {_width}x{_height} canvas.");
        }

        ReadOnlySpan<byte> currentPixels = frame.Pixels;
        ReadOnlySpan<byte> paintedPixels;
        int paintedCount;
        if (!allowTransparency)
        {
            // Keyframe/first-frame/raw-primitive shape: box is always the full canvas, so the whole
            // pixel array is already the dense "newly painted" set with no extraction pass needed.
            paintedPixels = currentPixels;
            paintedCount = _width * _height;
        }
        else if (GifEncoderStageTimings.Enabled)
        {
            long t0 = Stopwatch.GetTimestamp();
            paintedCount = CollectPaintedPixels(currentPixels, box);
            GifEncoderStageTimings.CollectPaintedTicks += Stopwatch.GetTimestamp() - t0;
            paintedPixels = _paintedPixelScratch.AsSpan(0, paintedCount * 4);
        }
        else
        {
            paintedCount = CollectPaintedPixels(currentPixels, box);
            paintedPixels = _paintedPixelScratch.AsSpan(0, paintedCount * 4);
        }

        long paletteT0 = GifEncoderStageTimings.Enabled ? Stopwatch.GetTimestamp() : 0;
        bool paletteReused = false;
        if (!forceRebuild && paintedCount > 0 && _paletteColorCount > 0)
        {
            int worst = GifOctreeQuantizer.MaxErrorAgainst(paintedPixels, _paletteBgr, _paletteColorCount, _options.PaletteReuseErrorThreshold + 1);
            paletteReused = worst <= _options.PaletteReuseErrorThreshold;
        }
        if (!paletteReused)
        {
            _paletteColorCount = paintedCount > 0
                ? _quantizer.BuildPalette(paintedPixels, _options.MaxPaletteColors, _paletteBgr)
                : 1;
            _lut.Rebuild(_paletteBgr, _paletteColorCount);
        }
        if (GifEncoderStageTimings.Enabled)
        {
            GifEncoderStageTimings.PaletteTicks += Stopwatch.GetTimestamp() - paletteT0;
        }

        int transparentIndex = _paletteColorCount;
        int entries = _paletteColorCount + 1; // + the reserved transparent slot, always present
        int physicalLctSize = NextPowerOfTwoAtLeastTwo(entries);
        int sizeBits = Log2(physicalLctSize) - 1;
        int minCodeSize = Math.Max(2, Log2(physicalLctSize));

        long classifyT0 = GifEncoderStageTimings.Enabled ? Stopwatch.GetTimestamp() : 0;
        GifDelta.ClassifyAndPaint(
            currentPixels, _lastPaintedPixelsBgra, _indexScratch, _width, box,
            _lut, _paletteBgr, transparentIndex, allowTransparency, _options.ChannelTolerance, _options.DitherErrorFloor,
            _options.LossyRunThresholdSq);
        if (GifEncoderStageTimings.Enabled)
        {
            GifEncoderStageTimings.ClassifyAndPaintTicks += Stopwatch.GetTimestamp() - classifyT0;
        }

        long packT0 = GifEncoderStageTimings.Enabled ? Stopwatch.GetTimestamp() : 0;
        int boxWidth = box.Width, boxHeight = box.Height;
        for (int y = 0; y < boxHeight; y++)
        {
            int srcRow = (box.Top + y) * _width + box.Left;
            Array.Copy(_indexScratch, srcRow, _boxIndexScratch, y * boxWidth, boxWidth);
        }

        // ---- Graphic Control Extension ----
        long gceStart = _stream.Position;
        byte gcePacked = (byte)(DisposalDoNotDispose | (allowTransparency ? 0x01 : 0x00));
        _stream.WriteByte(0x21);
        _stream.WriteByte(0xF9);
        _stream.WriteByte(0x04);
        _stream.WriteByte(gcePacked);
        long delayOffset = _stream.Position;
        _stream.WriteByte((byte)(delayCs & 0xFF));
        _stream.WriteByte((byte)(delayCs >> 8));
        _stream.WriteByte((byte)transparentIndex);
        _stream.WriteByte(0x00); // block terminator
        _ = gceStart;

        // ---- Image Descriptor ----
        _stream.WriteByte(0x2C);
        WriteUInt16LE(box.Left);
        WriteUInt16LE(box.Top);
        WriteUInt16LE(boxWidth);
        WriteUInt16LE(boxHeight);
        _stream.WriteByte((byte)(0x80 | sizeBits)); // LCT present, not interlaced/sorted

        // ---- Local Color Table: real colors, then the reserved (arbitrary) transparent slot, then
        // zero padding up to the physical power-of-two size the descriptor's size bits declare.
        // GIF89a mandates each entry as Red, Green, Blue in that order — but every internal palette
        // buffer (_paletteBgr, the quantizer, the nearest-color LUT) deliberately stores B,G,R to
        // match the app's own BGRA pixel convention throughout, so the swap happens right here, at
        // the one place bytes actually leave the process. Writing _paletteBgr's B,G,R bytes straight
        // through (as this used to) is spec-invalid: every real decoder renders such a file with red
        // and blue swapped — confirmed against WPF's GifBitmapDecoder, whose composited pixel output
        // came back with R/B exchanged for a known, asymmetric source color. ----
        for (int i = 0; i < _paletteColorCount; i++)
        {
            int po = i * 3;
            _stream.WriteByte(_paletteBgr[po + 2]); // R
            _stream.WriteByte(_paletteBgr[po + 1]); // G
            _stream.WriteByte(_paletteBgr[po + 0]); // B
        }
        int paddingEntries = physicalLctSize - _paletteColorCount;
        for (int i = 0; i < paddingEntries; i++)
        {
            _stream.WriteByte(0);
            _stream.WriteByte(0);
            _stream.WriteByte(0);
        }
        if (GifEncoderStageTimings.Enabled)
        {
            GifEncoderStageTimings.PackAndHeaderTicks += Stopwatch.GetTimestamp() - packT0;
        }

        // ---- LZW data ----
        long lzwT0 = GifEncoderStageTimings.Enabled ? Stopwatch.GetTimestamp() : 0;
        _lzw.Encode(_stream, _boxIndexScratch.AsSpan(0, boxWidth * boxHeight), minCodeSize);
        if (GifEncoderStageTimings.Enabled)
        {
            GifEncoderStageTimings.LzwTicks += Stopwatch.GetTimestamp() - lzwT0;
        }

        return delayOffset;
    }

    /// <summary>Preliminary tolerance-only scan over <paramref name="box"/> that gathers the dense
    /// BGRA quads of pixels NOT within <see cref="GifEncoderOptions.ChannelTolerance"/> of the
    /// baseline, into <see cref="_paintedPixelScratch"/> — exactly the "newly-painted pixels" set the
    /// octree quantizer is built from. Kept separate from <see cref="GifDelta.ClassifyAndPaint"/>
    /// (which does the same tolerance test) because the palette must exist BEFORE that method can map
    /// any pixel through the nearest-color LUT — this pass runs first and only re-does the cheap
    /// tolerance compare, never the LUT lookup.</summary>
    private int CollectPaintedPixels(ReadOnlySpan<byte> currentPixels, RectPhysical box)
    {
        byte tolerance = _options.ChannelTolerance;
        int count = 0;
        for (int y = box.Top; y < box.Bottom; y++)
        {
            int rowOffset = y * _width * 4;
            for (int x = box.Left; x < box.Right; x++)
            {
                int po = rowOffset + x * 4;
                byte b = currentPixels[po], g = currentPixels[po + 1], r = currentPixels[po + 2];
                byte lb = _lastPaintedPixelsBgra[po], lg = _lastPaintedPixelsBgra[po + 1], lr = _lastPaintedPixelsBgra[po + 2];
                if (Math.Abs(b - lb) <= tolerance && Math.Abs(g - lg) <= tolerance && Math.Abs(r - lr) <= tolerance)
                {
                    continue;
                }
                int o = count * 4;
                _paintedPixelScratch[o] = b;
                _paintedPixelScratch[o + 1] = g;
                _paintedPixelScratch[o + 2] = r;
                _paintedPixelScratch[o + 3] = 255;
                count++;
            }
        }
        return count;
    }

    private void WriteUInt16LE(int value)
    {
        _stream.WriteByte((byte)(value & 0xFF));
        _stream.WriteByte((byte)(value >> 8));
    }

    /// <summary>Smallest power of two, at least 2, that is &gt;= <paramref name="n"/> — the GIF color
    /// table's physical size is always a power of two from 2 to 256.</summary>
    private static int NextPowerOfTwoAtLeastTwo(int n)
    {
        int p = 2;
        while (p < n)
        {
            p <<= 1;
        }
        return p;
    }

    /// <summary>Base-2 log of a value already known to be a power of two (2..256), used both for the
    /// LZW minimum code size and the Image Descriptor's color-table size bits.</summary>
    private static int Log2(int powerOfTwo)
    {
        int n = 0;
        while ((1 << n) < powerOfTwo)
        {
            n++;
        }
        return n;
    }

    /// <summary>Finds the tight bounding box of pixels that differ between two same-sized BGRA8
    /// canvases by more than <paramref name="tolerance"/> in ANY channel (Chebyshev/max-channel — the
    /// same test <see cref="GifDelta"/> uses for its own per-pixel classification, kept consistent so
    /// the bbox this returns and the transparent-pixel decisions made inside it agree). Returns false
    /// when no pixel differs beyond tolerance. Tolerance 0 (the default, matching every pre-existing
    /// caller/test) is exact-equality, vectorized 32-bit-at-a-time (row scans are one SequenceEqual;
    /// column scans are bounded by the best left/right found so far); nonzero tolerance runs a
    /// straightforward scalar per-pixel scan, since it is only ever called once per candidate frame.</summary>
    public static bool TryGetChangedBounds(byte[] prev, byte[] cur, int width, int height, out RectPhysical bounds, byte tolerance = 0)
    {
        if (prev.Length != cur.Length || prev.Length != width * 4 * height)
        {
            throw new ArgumentException("Canvas buffers must match width*4*height.", nameof(cur));
        }

        return tolerance == 0
            ? TryGetChangedBoundsExact(prev, cur, width, height, out bounds)
            : TryGetChangedBoundsTolerant(prev, cur, width, height, tolerance, out bounds);
    }

    private static bool TryGetChangedBoundsExact(byte[] prev, byte[] cur, int width, int height, out RectPhysical bounds)
    {
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

        const int ChunkPx = 64;
        int left = width, right = -1;
        for (int y = top; y <= bottom; y++)
        {
            var pr = p.Slice(y * width, width);
            var cr = c.Slice(y * width, width);

            int x = 0;
            while (x < left)
            {
                int len = Math.Min(ChunkPx, left - x);
                if (!pr.Slice(x, len).SequenceEqual(cr.Slice(x, len)))
                {
                    while (pr[x] == cr[x]) { x++; }
                    left = x;
                    break;
                }
                x += len;
            }

            int hi = width;
            while (hi > right + 1)
            {
                int len = Math.Min(ChunkPx, hi - (right + 1));
                int start = hi - len;
                if (!pr.Slice(start, len).SequenceEqual(cr.Slice(start, len)))
                {
                    int i = hi - 1;
                    while (pr[i] == cr[i]) { i--; }
                    right = i;
                    break;
                }
                hi = start;
            }
        }

        bounds = RectPhysical.FromSize(left, top, right - left + 1, bottom - top + 1);
        return true;
    }

    private static bool TryGetChangedBoundsTolerant(byte[] prev, byte[] cur, int width, int height, byte tolerance, out RectPhysical bounds)
    {
        int top = -1, bottom = -1, left = width, right = -1;
        for (int y = 0; y < height; y++)
        {
            int rowOffset = y * width * 4;
            bool rowChanged = false;
            for (int x = 0; x < width; x++)
            {
                int po = rowOffset + x * 4;
                int db = Math.Abs(prev[po] - cur[po]);
                int dg = Math.Abs(prev[po + 1] - cur[po + 1]);
                int dr = Math.Abs(prev[po + 2] - cur[po + 2]);
                if (db <= tolerance && dg <= tolerance && dr <= tolerance)
                {
                    continue;
                }
                rowChanged = true;
                if (x < left) left = x;
                if (x > right) right = x;
            }
            if (rowChanged)
            {
                if (top < 0) top = y;
                bottom = y;
            }
        }

        if (top < 0)
        {
            bounds = default;
            return false;
        }
        bounds = RectPhysical.FromSize(left, top, right - left + 1, bottom - top + 1);
        return true;
    }

    /// <summary>Recording-path finalize: patches the LAST frame's delay to its real remaining display
    /// time (last emit to <paramref name="endTimestampTicks"/>, the take's stop moment) — without
    /// this, a recording that ends on a static tail (the common "hold the result, then Stop" flow)
    /// would snap off its final frame after the provisional delay on every loop — then writes the
    /// trailer and closes.</summary>
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
        _closed = true;
    }
}
