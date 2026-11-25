using System.Diagnostics;
using System.Runtime.InteropServices;
using RoeSnip.Core.Capture;
using RoeSnip.Core.Color;
using RoeSnip.Core.Diagnostics;
using RoeSnip.Core.Imaging;
using static RoeSnip.Platform.Linux.PipeWireNative;

namespace RoeSnip.Platform.Linux;

/// <summary>libpipewire-0.3 consumer for the HDR streams of the snapshot screencast. GNOME 50 offers
/// RGBA_F16 (PQ-encoded BT.2020) only on a BT.2100 monitor and only as DMA-BUF, which GStreamer's
/// pipewiresrc cannot negotiate, so each HDR node gets a <see cref="HdrScreenCastStream"/> here
/// instead of a pipeline. A node that does not offer the format (an SDR monitor, GNOME before 50)
/// fails the probe at once and stays on the 8-bit pipeline.
///
/// Runs on its own OpenPipeWireRemote fd of the same session (never a dup of the gst fd: one socket
/// under two cores breaks both; no second share dialog). Every frame is imported through EGL
/// (<see cref="DmaBufGl"/>) into a GPU texture and the PipeWire buffer is requeued at once, so a
/// kept-open stream never pins mutter's buffers; a capture reads the texture back.</summary>
internal sealed unsafe class PipeWireHdrReader : IDisposable
{
    private static int s_initialized;

    private readonly IntPtr _loop;
    private readonly IntPtr _context;
    private readonly IntPtr _core;
    private readonly DmaBufGl _gl;
    private readonly IReadOnlyList<ulong> _modifiers;
    private readonly List<HdrScreenCastStream> _nodes = new();
    private int _disposed;

    private PipeWireHdrReader(IntPtr loop, IntPtr context, IntPtr core, DmaBufGl gl, IReadOnlyList<ulong> modifiers)
    {
        _loop = loop;
        _context = context;
        _core = core;
        _gl = gl;
        _modifiers = modifiers;
    }

    /// <summary>Opens the consumer on a dup of <paramref name="portalFd"/>. Null with
    /// <paramref name="why"/> when EGL cannot import half-float DMA-BUFs or libpipewire is missing.</summary>
    public static PipeWireHdrReader? TryStart(int portalFd, out string why)
    {
        why = "";
        if (!NativeLibrary.TryLoad(Lib, out _))
        {
            why = $"{Lib} not found";
            return null;
        }
        var gl = DmaBufGl.TryCreate(HdrDisplayProbe.ActiveDrmFiles(), out string glWhy);
        if (gl is null)
        {
            why = "EGL: " + glWhy;
            return null;
        }
        IReadOnlyList<ulong> modifiers;
        try { modifiers = gl.ModifiersFor(DmaBufGl.FourccAbgr16161616F); }
        catch (Exception ex) { modifiers = Array.Empty<ulong>(); glWhy = ex.Message; }
        if (modifiers.Count == 0)
        {
            gl.Dispose();
            why = "EGL imports no DMA-BUF modifier for half-float RGBA" + (glWhy.Length > 0 ? " (" + glWhy + ")" : "");
            return null;
        }

        int fd = dup(portalFd);
        if (fd < 0)
        {
            gl.Dispose();
            why = "dup of the portal fd failed";
            return null;
        }
        if (Interlocked.Exchange(ref s_initialized, 1) == 0) pw_init(IntPtr.Zero, IntPtr.Zero);
        IntPtr loop = pw_thread_loop_new("roesnip-hdr", IntPtr.Zero);
        if (loop == IntPtr.Zero)
        {
            close(fd);
            gl.Dispose();
            why = "pw_thread_loop_new failed";
            return null;
        }
        IntPtr context = pw_context_new(pw_thread_loop_get_loop(loop), IntPtr.Zero, 0);
        if (context == IntPtr.Zero || pw_thread_loop_start(loop) < 0)
        {
            if (context != IntPtr.Zero) pw_context_destroy(context);
            pw_thread_loop_destroy(loop);
            close(fd);
            gl.Dispose();
            why = "pw_context_new or the loop start failed";
            return null;
        }
        pw_thread_loop_lock(loop);
        IntPtr core = pw_context_connect_fd(context, fd, IntPtr.Zero, 0); // owns fd from here on
        pw_thread_loop_unlock(loop);
        var reader = new PipeWireHdrReader(loop, context, core, gl, modifiers);
        if (core == IntPtr.Zero)
        {
            why = "pw_context_connect_fd failed";
            reader.Dispose();
            return null;
        }
        FileLog.Write($"RoeSnip: HDR screencast reader ready (EGL {gl.DisplayKind}, {modifiers.Count} F16 DMA-BUF modifiers)");
        return reader;
    }

    /// <summary>Connects an F16 consumer to the node and waits for the negotiation. Null with
    /// <paramref name="why"/> when the node does not offer HDR frames (the usual SDR answer).</summary>
    public HdrScreenCastStream? AddNode(ScreenCastStreamGeometry geometry, TimeSpan wait, out string why)
    {
        why = "";
        if (Volatile.Read(ref _disposed) != 0)
        {
            why = "reader closed";
            return null;
        }
        var node = new HdrScreenCastStream(this, geometry);
        pw_thread_loop_lock(_loop);
        _nodes.Add(node);
        string? error = node.Connect(_core, _modifiers);
        pw_thread_loop_unlock(_loop);
        if (error is null && !node.Negotiated.Wait(wait)) error = "no HDR format within " + (int)wait.TotalMilliseconds + " ms";
        error ??= node.Error;
        if (error is not null)
        {
            RemoveNode(node);
            why = error;
            return null;
        }
        return node;
    }

    internal void RemoveNode(HdrScreenCastStream node)
    {
        if (IsDisposed)
        {
            node.Free(); // the loop is gone and took the stream with it
            return;
        }
        pw_thread_loop_lock(_loop);
        _nodes.Remove(node);
        node.Destroy();
        pw_thread_loop_unlock(_loop);
        node.Free();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        pw_thread_loop_lock(_loop);
        var nodes = _nodes.ToArray();
        foreach (var n in nodes) n.Destroy();
        _nodes.Clear();
        if (_core != IntPtr.Zero) pw_core_disconnect(_core);
        pw_thread_loop_unlock(_loop);
        pw_thread_loop_stop(_loop);
        pw_context_destroy(_context);
        pw_thread_loop_destroy(_loop);
        foreach (var n in nodes) n.Free();
        _gl.Dispose();
    }

    internal DmaBufGl Gl => _gl;
    internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;
}

/// <summary>One HDR monitor's kept-open stream: the newest frame lives in a GPU texture.</summary>
internal sealed unsafe class HdrScreenCastStream : IScreenCastStream
{
    private const uint FlagAutoconnect = 1 << 0, FlagDontReconnect = 1 << 7;
    private const int StateError = -1, StateStreaming = 3;
    private const int ImportFailuresBeforeDead = 3;

    private readonly PipeWireHdrReader _owner;
    private readonly object _gate = new();
    private readonly ManualResetEventSlim _firstSample = new(false);
    private IntPtr _stream;
    private readonly IntPtr _hook;
    private readonly IntPtr _events;
    private GCHandle _self;
    private DmaBufGl.Target? _target;
    private SpaPod.VideoFormat _format;
    private bool _formatOk;
    private long _sequence;
    private int _importFailures;
    private volatile string? _deadReason;
    private int _firstImportLogged;
    private int _freed;
    private byte[]? _toneBuffer;

    public ScreenCastStreamGeometry Geometry { get; }
    public ManualResetEventSlim Negotiated { get; } = new(false);
    public volatile string? Error;
    public string? DeadReason => _deadReason;
    public bool IsDead => _deadReason is not null;
    public long Sequence => Interlocked.Read(ref _sequence);

    /// <summary>The stream's buffer size once negotiated.</summary>
    public (int Width, int Height) BufferSize => (_format.Width, _format.Height);

    internal HdrScreenCastStream(PipeWireHdrReader owner, ScreenCastStreamGeometry geometry)
    {
        _owner = owner;
        Geometry = geometry;
        _hook = Marshal.AllocHGlobal(64);
        new Span<byte>((void*)_hook, 64).Clear();
        _events = Marshal.AllocHGlobal(96);
        new Span<byte>((void*)_events, 96).Clear();
        // pw_stream_events v0: version, destroy, state_changed, control_info, io_changed,
        // param_changed, add_buffer, remove_buffer, process, drained.
        var e = (IntPtr*)_events;
        *(uint*)_events = 0;
        e[2] = Marshal.GetFunctionPointerForDelegate(s_stateChanged);
        e[5] = Marshal.GetFunctionPointerForDelegate(s_paramChanged);
        e[8] = Marshal.GetFunctionPointerForDelegate(s_process);
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void StateChangedFn(IntPtr data, int old, int state, IntPtr error);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void ParamChangedFn(IntPtr data, uint id, IntPtr param);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void ProcessFn(IntPtr data);
    private static readonly StateChangedFn s_stateChanged = OnStateChanged;
    private static readonly ParamChangedFn s_paramChanged = OnParamChanged;
    private static readonly ProcessFn s_process = OnProcess;

    internal string? Connect(IntPtr core, IReadOnlyList<ulong> modifiers)
    {
        IntPtr props = pw_properties_new_string("media.type=Video media.category=Capture media.role=Screen node.name=roesnip-hdr");
        _stream = pw_stream_new(core, "roesnip-hdr", props);
        if (_stream == IntPtr.Zero) return "pw_stream_new failed";
        _self = GCHandle.Alloc(this);
        pw_stream_add_listener(_stream, _hook, _events, GCHandle.ToIntPtr(_self));
        byte[] format = SpaPod.HdrEnumFormat(modifiers);
        fixed (byte* f = format)
        {
            IntPtr* list = stackalloc IntPtr[1];
            list[0] = (IntPtr)f;
            int rc = pw_stream_connect(_stream, 0 /* input */, Geometry.NodeId, FlagAutoconnect | FlagDontReconnect, list, 1);
            if (rc < 0) return $"pw_stream_connect returned {rc}";
        }
        return null;
    }

    internal void Destroy()
    {
        if (_stream != IntPtr.Zero)
        {
            pw_stream_destroy(_stream);
            _stream = IntPtr.Zero;
        }
    }

    internal void Free()
    {
        if (Interlocked.Exchange(ref _freed, 1) != 0) return;
        if (_self.IsAllocated) _self.Free();
        Marshal.FreeHGlobal(_hook);
        Marshal.FreeHGlobal(_events);
        lock (_gate)
        {
            if (_target is not null)
            {
                _owner.Gl.DestroyTarget(_target);
                _target = null;
            }
        }
    }

    private static HdrScreenCastStream? From(IntPtr data)
    {
        try { return GCHandle.FromIntPtr(data).Target as HdrScreenCastStream; }
        catch { return null; }
    }

    private static void OnStateChanged(IntPtr data, int old, int state, IntPtr error)
    {
        if (From(data) is not { } n) return;
        if (state != StateStreaming || old != StateStreaming)
        {
            FileLog.Write($"RoeSnip: HDR stream node {n.Geometry.NodeId} state {old} -> {state}" +
                (error == IntPtr.Zero ? "" : " (" + Marshal.PtrToStringUTF8(error) + ")"));
        }
        if (state == StateError)
        {
            string text = error == IntPtr.Zero ? "error" : Marshal.PtrToStringUTF8(error) ?? "error";
            if (!n.Negotiated.IsSet) n.Error = text; // the probe failed: no HDR format on this node
            else n.MarkDead("stream error: " + text);
            n.Negotiated.Set();
            n._firstSample.Set();
        }
        else if (state == StateStreaming && old != StateStreaming && n._formatOk)
        {
            n.Negotiated.Set();
        }
    }

    private static void OnParamChanged(IntPtr data, uint id, IntPtr param)
    {
        if (From(data) is not { } n || id != SpaPod.ParamFormat || param == IntPtr.Zero) return;
        try
        {
            uint size = *(uint*)param;
            var pod = new ReadOnlySpan<byte>((void*)param, (int)Math.Min(size + 8, 65536));
            if (!SpaPod.TryReadVideoFormat(pod, out var f))
            {
                FileLog.Write($"RoeSnip: HDR stream node {n.Geometry.NodeId} sent an unreadable Format ({size} B)");
                return;
            }
            FileLog.Write($"RoeSnip: HDR stream node {n.Geometry.NodeId} Format {f.Format} {f.Width}x{f.Height} " +
                $"modifier {(f.HasModifier ? "0x" + f.Modifier.ToString("X") + (f.ModifierIsChoice ? " (choice)" : "") : "none")} " +
                $"primaries {f.Primaries} transfer {f.Transfer}");
            if (f.Format != SpaPod.VideoRgbaF16 || f.Width <= 0 || f.Height <= 0
                || (f.Primaries != 0 && f.Primaries != SpaPod.PrimariesBt2020)
                || (f.Transfer != 0 && f.Transfer != SpaPod.TransferSmpte2084))
            {
                n.Error = $"unexpected format {f.Format} {f.Width}x{f.Height} primaries {f.Primaries} transfer {f.Transfer}";
                n.Negotiated.Set();
                return;
            }
            if (!f.HasModifier)
            {
                n.Error = "the node offered half-float frames without DMA-BUF (memfd paints 8-bit garbage)";
                n.Negotiated.Set();
                return;
            }
            // Mutter fixates the modifier itself and re-sends the Format: answer only the fixed one.
            if (f.ModifierIsChoice) return;

            lock (n._gate)
            {
                if (n._target is null || n._target.Width != f.Width || n._target.Height != f.Height)
                {
                    if (n._target is not null) n._owner.Gl.DestroyTarget(n._target);
                    n._target = n._owner.Gl.CreateTarget(f.Width, f.Height);
                }
                n._format = f;
                n._formatOk = true;
            }
            // The Cursor meta rides on the buffers of the first consumer's params (the cursor reader
            // joins later), exactly as pipewiresrc asks for it on the 8-bit nodes.
            byte[] buffers = SpaPod.DmaBufBuffersParam();
            byte[] header = SpaPod.MetaParam(SpaPod.MetaHeader, SpaPod.MetaHeaderSize);
            byte[] cursor = SpaPod.CursorMetaParam(384);
            fixed (byte* bp = buffers)
            fixed (byte* hp = header)
            fixed (byte* cp = cursor)
            {
                IntPtr* list = stackalloc IntPtr[3];
                list[0] = (IntPtr)bp;
                list[1] = (IntPtr)hp;
                list[2] = (IntPtr)cp;
                pw_stream_update_params(n._stream, list, 3);
            }
            FileLog.Write($"RoeSnip: HDR stream node {n.Geometry.NodeId} negotiated RGBA_F16 {f.Width}x{f.Height}, PQ BT.2020, modifier 0x{f.Modifier:X}");
            n.Negotiated.Set();
        }
        catch (Exception ex)
        {
            n.Error = "format: " + ex.Message;
            n.Negotiated.Set();
        }
    }

    private static void OnProcess(IntPtr data)
    {
        if (From(data) is not { } n || n._stream == IntPtr.Zero) return;
        try
        {
            IntPtr b;
            while ((b = pw_stream_dequeue_buffer(n._stream)) != IntPtr.Zero)
            {
                try { n.Import(*(IntPtr*)b); }
                finally { pw_stream_queue_buffer(n._stream, b); }
            }
        }
        catch (Exception ex)
        {
            n.MarkDead("process: " + ex.Message);
        }
    }

    /// <summary>spa_buffer {n_metas, n_datas, metas*, datas*}; spa_data {type, flags, i64 fd,
    /// mapoffset, maxsize, data*, chunk*}; spa_chunk {offset, size, stride, flags}.</summary>
    private void Import(IntPtr spaBuffer)
    {
        if (spaBuffer == IntPtr.Zero || IsDead) return;
        if (*(uint*)((byte*)spaBuffer + 4) < 1) return;
        byte* d = *(byte**)((byte*)spaBuffer + 16);
        if (d == null || *(uint*)d != SpaPod.DataDmaBuf) return;
        byte* chunk = *(byte**)(d + 32);
        if (chunk == null || *(uint*)(chunk + 4) == 0) return; // cursor-only buffer
        int fd = (int)*(long*)(d + 8);
        int offset = (int)*(uint*)chunk;
        int stride = *(int*)(chunk + 8);
        if (fd < 0) return;

        DmaBufGl.Target? target;
        ulong modifier;
        lock (_gate)
        {
            target = _target;
            modifier = _format.Modifier;
        }
        if (target is null) return;
        var watch = Stopwatch.StartNew();
        string? error = _owner.Gl.CopyFrame(target, fd, offset, stride, modifier);
        if (error is not null)
        {
            if (++_importFailures >= ImportFailuresBeforeDead)
            {
                MarkDead("dmabuf import failed: " + error);
            }
            return;
        }
        _importFailures = 0;
        if (Interlocked.Exchange(ref _firstImportLogged, 1) == 0)
        {
            FileLog.Write($"RoeSnip: HDR stream node {Geometry.NodeId} first frame imported in {watch.ElapsedMilliseconds} ms (stride {stride})");
        }
        Interlocked.Increment(ref _sequence);
        _firstSample.Set();
    }

    private void MarkDead(string reason)
    {
        if (_deadReason is not null) return;
        _deadReason = reason;
        FileLog.Write($"RoeSnip: HDR stream node {Geometry.NodeId} stopped: {reason}");
        _firstSample.Set();
    }

    public bool WaitForFirstSample(TimeSpan timeout) => _firstSample.Wait(timeout) && !IsDead;

    /// <summary>Reads the newest frame back as a linear scRGB frame at the monitor's size, SDR white
    /// 203 nits (PQ BT.2020 converted by <see cref="PqBt2020"/>). Null with <paramref name="why"/> when
    /// there is no frame or the GPU readback failed.</summary>
    public CapturedFrame? ReadFrame(MonitorInfo monitor, double maxLuminanceNits, out string why, out string timing)
    {
        why = "";
        timing = "";
        var watch = Stopwatch.StartNew();
        byte[] raw;
        int w, h;
        lock (_gate)
        {
            if (_target is null || Sequence == 0)
            {
                why = "no HDR frame yet";
                return null;
            }
            w = _target.Width;
            h = _target.Height;
            raw = new byte[w * h * 8];
            string? err = _owner.Gl.ReadHalf(_target, 0, 0, w, h, raw);
            if (err is not null)
            {
                why = err;
                return null;
            }
        }
        long readMs = watch.ElapsedMilliseconds;

        int mw = monitor.BoundsPx.Width, mh = monitor.BoundsPx.Height;
        if (w != mw || h != mh)
        {
            var scaled = new byte[mw * mh * 8];
            var src = MemoryMarshal.Cast<byte, ulong>(raw);
            var dst = MemoryMarshal.Cast<byte, ulong>(scaled);
            for (int y = 0; y < mh; y++)
            {
                int sy = Math.Min(h - 1, (int)((long)y * h / mh));
                for (int x = 0; x < mw; x++)
                {
                    dst[y * mw + x] = src[sy * w + Math.Min(w - 1, (int)((long)x * w / mw))];
                }
            }
            raw = scaled;
            w = mw;
            h = mh;
        }
        var frame = PqBt2020.CreateFrame(raw, w * 8, w, h, monitor, maxLuminanceNits);
        timing = $"hdr readback {readMs} ms, pq convert {watch.ElapsedMilliseconds - readMs} ms";
        return frame;
    }

    /// <summary>Recording path: the region tone-mapped to BGRA8 with the Core tone-mapper.</summary>
    public bool TryCopy(int srcLeft, int srcTop, int outWidth, int outHeight,
        int monitorWidth, int monitorHeight, byte[] dest, out long sequence, out string? sizeNote)
    {
        sizeNote = null;
        sequence = Sequence;
        if (sequence == 0 || outWidth <= 0 || outHeight <= 0) return false;
        var (bw, bh) = BufferSize;
        var monitor = new MonitorInfo(0, "hdr", "hdr", new RectPhysical(0, 0, monitorWidth, monitorHeight),
            96, 96, 1.0, true, PqBt2020.ReferenceWhiteNits, 1000.0, false);
        CapturedFrame? frame;
        int left = srcLeft, top = srcTop;
        lock (_gate)
        {
            if (_target is null) return false;
            if (bw == monitorWidth && bh == monitorHeight)
            {
                left = Math.Clamp(srcLeft, 0, Math.Max(0, bw - outWidth));
                top = Math.Clamp(srcTop, 0, Math.Max(0, bh - outHeight));
                int w = Math.Min(outWidth, bw - left), h = Math.Min(outHeight, bh - top);
                var raw = new byte[w * h * 8];
                if (_owner.Gl.ReadHalf(_target, left, top, w, h, raw) is not null) return false;
                frame = PqBt2020.CreateFrame(raw, w * 8, w, h, monitor with
                {
                    BoundsPx = new RectPhysical(0, 0, w, h),
                }, 1000.0);
                left = 0;
                top = 0;
            }
            else
            {
                sizeNote = $"stream buffer {bw}x{bh} != monitor {monitorWidth}x{monitorHeight}, nearest-sampling";
                frame = null;
            }
        }
        frame ??= ReadFrame(monitor, 1000.0, out _, out _);
        if (frame is null) return false;
        using (frame)
        {
            int need = frame.Width * frame.Height * 4;
            _toneBuffer = _toneBuffer is { } tb && tb.Length == need ? tb : new byte[need];
            var sdr = ToneMapper.MapToSdr(frame, new ToneMapOptions(), _toneBuffer);
            int w = Math.Min(outWidth, frame.Width - left), h = Math.Min(outHeight, frame.Height - top);
            for (int y = 0; y < h; y++)
            {
                Buffer.BlockCopy(sdr.Pixels, ((top + y) * frame.Width + left) * 4, dest, y * outWidth * 4, w * 4);
            }
        }
        return true;
    }

    public void Dispose() => _owner.RemoveNode(this);
}
