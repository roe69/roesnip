using System.Runtime.InteropServices;
using RoeSnip.Core.Capture;
using RoeSnip.Core.Diagnostics;
using static RoeSnip.Platform.Linux.PipeWireNative;

namespace RoeSnip.Platform.Linux;

/// <summary>libpipewire-0.3 consumer for the 8-bit (BGRx/BGRA, memfd) streams of the snapshot screencast.
/// Same pattern as <see cref="PipeWireHdrReader"/>: pw_stream_set_active idles and wakes a node, and
/// mutter sends a frame within milliseconds of a wake (pipewiresrc needed a READY/PLAYING state change,
/// 40-400 ms). Runs on its own OpenPipeWireRemote fd of the same session (never a dup of another
/// consumer's fd); no EGL needed.</summary>
internal sealed unsafe class PipeWireSdrReader : IDisposable
{
    private static int s_initialized;

    private readonly IntPtr _loop;
    private readonly IntPtr _context;
    private readonly IntPtr _core;
    private readonly List<ShmScreenCastStream> _nodes = new();
    private int _disposed;

    private PipeWireSdrReader(IntPtr loop, IntPtr context, IntPtr core)
    {
        _loop = loop;
        _context = context;
        _core = core;
    }

    /// <summary>Opens the consumer on a dup of <paramref name="portalFd"/>; null with <paramref name="why"/>
    /// when libpipewire is missing or the connection fails.</summary>
    public static PipeWireSdrReader? TryStart(int portalFd, out string why)
    {
        why = "";
        if (!NativeLibrary.TryLoad(Lib, out _))
        {
            why = $"{Lib} not found";
            return null;
        }
        int fd = dup(portalFd);
        if (fd < 0)
        {
            why = "dup of the portal fd failed";
            return null;
        }
        if (Interlocked.Exchange(ref s_initialized, 1) == 0) pw_init(IntPtr.Zero, IntPtr.Zero);
        IntPtr loop = pw_thread_loop_new("roesnip-sdr", IntPtr.Zero);
        if (loop == IntPtr.Zero)
        {
            close(fd);
            why = "pw_thread_loop_new failed";
            return null;
        }
        IntPtr context = pw_context_new(pw_thread_loop_get_loop(loop), IntPtr.Zero, 0);
        if (context == IntPtr.Zero || pw_thread_loop_start(loop) < 0)
        {
            if (context != IntPtr.Zero) pw_context_destroy(context);
            pw_thread_loop_destroy(loop);
            close(fd);
            why = "pw_context_new or the loop start failed";
            return null;
        }
        pw_thread_loop_lock(loop);
        IntPtr core = pw_context_connect_fd(context, fd, IntPtr.Zero, 0); // owns fd from here on
        pw_thread_loop_unlock(loop);
        var reader = new PipeWireSdrReader(loop, context, core);
        if (core == IntPtr.Zero)
        {
            why = "pw_context_connect_fd failed";
            reader.Dispose();
            return null;
        }
        FileLog.Write("RoeSnip: 8-bit screencast reader ready (libpipewire, memfd buffers)");
        return reader;
    }

    /// <summary>Connects a consumer to the node and waits for the negotiation. Null with
    /// <paramref name="why"/> when it does not settle on an 8-bit format.</summary>
    public ShmScreenCastStream? AddNode(ScreenCastStreamGeometry geometry, TimeSpan wait, out string why)
    {
        why = "";
        if (Volatile.Read(ref _disposed) != 0)
        {
            why = "reader closed";
            return null;
        }
        var node = new ShmScreenCastStream(this, geometry);
        pw_thread_loop_lock(_loop);
        _nodes.Add(node);
        string? error = node.Connect(_core);
        pw_thread_loop_unlock(_loop);
        if (error is null && !node.Negotiated.Wait(wait)) error = "no 8-bit format within " + (int)wait.TotalMilliseconds + " ms";
        error ??= node.Error;
        if (error is not null)
        {
            RemoveNode(node);
            why = error;
            return null;
        }
        return node;
    }

    internal void SetStreamActive(ShmScreenCastStream node, bool active)
    {
        if (IsDisposed) return;
        pw_thread_loop_lock(_loop);
        try { node.SetActiveLocked(active); }
        finally { pw_thread_loop_unlock(_loop); }
    }

    internal void RemoveNode(ShmScreenCastStream node)
    {
        if (IsDisposed)
        {
            node.Free();
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
    }

    internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;
}

/// <summary>One monitor's 8-bit stream. The newest frame is copied out of mutter's buffer at once (the
/// buffer is requeued, so an idle or kept-open stream never pins one) into a single tight-stride array
/// that <see cref="TryCopy"/> reads under the same lock; <see cref="Sequence"/> is bumped after each copy.</summary>
internal sealed unsafe class ShmScreenCastStream : IScreenCastStream
{
    private const uint FlagAutoconnect = 1 << 0, FlagMapBuffers = 1 << 2, FlagDontReconnect = 1 << 7;
    private const int StateError = -1, StateStreaming = 3;

    private readonly PipeWireSdrReader _owner;
    private readonly object _gate = new();
    private readonly ManualResetEventSlim _firstSample = new(false);
    private IntPtr _stream;
    private readonly IntPtr _hook;
    private readonly IntPtr _events;
    private GCHandle _self;
    private SpaPodFormat _format;
    private bool _formatOk;
    private byte[]? _pixels;   // tight (width * 4) rows, guarded by _gate
    private int _w, _h;
    private readonly FrameSignal _frames = new();
    private volatile string? _deadReason;
    private int _firstLogged;
    private int _unmappedLogged;
    private int _freed;
    private long _minPtsNs;    // frames stamped before the last wake are dropped (CLOCK_MONOTONIC, like Stopwatch)
    private int _logFirstAfterWake;

    private readonly record struct SpaPodFormat(uint Format, int Width, int Height);

    public ScreenCastStreamGeometry Geometry { get; }
    public ManualResetEventSlim Negotiated { get; } = new(false);
    public volatile string? Error;
    public string? DeadReason => _deadReason;
    public bool IsDead => _deadReason is not null;
    public long Sequence => _frames.Sequence;

    internal ShmScreenCastStream(PipeWireSdrReader owner, ScreenCastStreamGeometry geometry)
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

    internal string? Connect(IntPtr core)
    {
        IntPtr props = pw_properties_new_string("media.type=Video media.category=Capture media.role=Screen node.name=roesnip-sdr");
        _stream = pw_stream_new(core, "roesnip-sdr", props);
        if (_stream == IntPtr.Zero) return "pw_stream_new failed";
        _self = GCHandle.Alloc(this);
        pw_stream_add_listener(_stream, _hook, _events, GCHandle.ToIntPtr(_self));
        byte[] format = SpaPod.SdrEnumFormat();
        fixed (byte* f = format)
        {
            IntPtr* list = stackalloc IntPtr[1];
            list[0] = (IntPtr)f;
            int rc = pw_stream_connect(_stream, 0 /* input */, Geometry.NodeId,
                FlagAutoconnect | FlagMapBuffers | FlagDontReconnect, list, 1);
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
        lock (_gate) _pixels = null;
    }

    private static ShmScreenCastStream? From(IntPtr data)
    {
        try { return GCHandle.FromIntPtr(data).Target as ShmScreenCastStream; }
        catch { return null; }
    }

    private static void OnStateChanged(IntPtr data, int old, int state, IntPtr error)
    {
        if (From(data) is not { } n) return;
        if (state != StateStreaming || old != StateStreaming)
        {
            FileLog.Write($"RoeSnip: 8-bit stream node {n.Geometry.NodeId} state {old} -> {state}" +
                (error == IntPtr.Zero ? "" : " (" + Marshal.PtrToStringUTF8(error) + ")"));
        }
        if (state == StateError)
        {
            string text = error == IntPtr.Zero ? "error" : Marshal.PtrToStringUTF8(error) ?? "error";
            if (!n.Negotiated.IsSet) n.Error = text;
            else n.MarkDead("stream error: " + text);
            n.Negotiated.Set();
            n._firstSample.Set();
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
                FileLog.Write($"RoeSnip: 8-bit stream node {n.Geometry.NodeId} sent an unreadable Format ({size} B)");
                return;
            }
            if ((f.Format != SpaPod.VideoBGRx && f.Format != SpaPod.VideoBGRA) || f.Width <= 0 || f.Height <= 0 || f.HasModifier)
            {
                n.Error = $"unexpected format {f.Format} {f.Width}x{f.Height}" + (f.HasModifier ? " with a modifier" : "");
                n.Negotiated.Set();
                return;
            }
            lock (n._gate)
            {
                n._format = new SpaPodFormat(f.Format, f.Width, f.Height);
                n._formatOk = true;
            }
            // Header + Cursor meta ride on the first consumer's params, as pipewiresrc asks for them.
            byte[] buffers = SpaPod.ShmBuffersParam();
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
            FileLog.Write($"RoeSnip: 8-bit stream node {n.Geometry.NodeId} negotiated {(f.Format == SpaPod.VideoBGRx ? "BGRx" : "BGRA")} {f.Width}x{f.Height}");
            n.Negotiated.Set();
        }
        catch (Exception ex)
        {
            n.Error = "format: " + ex.Message;
            n.Negotiated.Set();
        }
    }

    /// <summary>Copies the newest pixel buffer of the batch and requeues every buffer at once.</summary>
    private static void OnProcess(IntPtr data)
    {
        if (From(data) is not { } n || n._stream == IntPtr.Zero) return;
        try
        {
            IntPtr last = IntPtr.Zero, b;
            while ((b = pw_stream_dequeue_buffer(n._stream)) != IntPtr.Zero)
            {
                if (n.HasPixels(*(IntPtr*)b))
                {
                    if (last != IntPtr.Zero) pw_stream_queue_buffer(n._stream, last);
                    last = b;
                }
                else
                {
                    pw_stream_queue_buffer(n._stream, b); // cursor-only
                }
            }
            if (last == IntPtr.Zero) return;
            try { n.Store(*(IntPtr*)last); }
            finally { pw_stream_queue_buffer(n._stream, last); }
        }
        catch (Exception ex)
        {
            n.MarkDead("process: " + ex.Message);
        }
    }

    /// <summary>spa_buffer {n_metas, n_datas, metas*, datas*}; spa_data {type, flags, i64 fd,
    /// mapoffset, maxsize, data*, chunk*}; spa_chunk {offset, size, stride, flags}.</summary>
    private bool HasPixels(IntPtr spaBuffer)
    {
        if (spaBuffer == IntPtr.Zero || *(uint*)((byte*)spaBuffer + 4) < 1) return false;
        byte* d = *(byte**)((byte*)spaBuffer + 16);
        if (d == null) return false;
        byte* chunk = *(byte**)(d + 32);
        return chunk != null && *(uint*)(chunk + 4) != 0 && (*(int*)(chunk + 12) & 2) == 0; // 2 = corrupted
    }

    private void Store(IntPtr spaBuffer)
    {
        byte* d = *(byte**)((byte*)spaBuffer + 16);
        byte* mem = *(byte**)(d + 24);
        byte* chunk = *(byte**)(d + 32);
        if (mem == null)
        {
            if (Interlocked.Exchange(ref _unmappedLogged, 1) == 0)
            {
                FileLog.Write($"RoeSnip: 8-bit stream node {Geometry.NodeId} buffer type {*(uint*)d} is not mapped");
            }
            return;
        }
        long ptsNs = SpaFrameClock.HeaderPts(spaBuffer);
        long minPts = Volatile.Read(ref _minPtsNs);
        if (ptsNs > 0 && ptsNs < minPts)
        {
            // A buffer mutter queued before the stream idled, delivered at the wake: not the screen as it is now.
            FileLog.Write($"RoeSnip: 8-bit stream node {Geometry.NodeId} dropped a frame from {(minPts - ptsNs) / 1_000_000} ms before the wake");
            return;
        }
        uint offset = *(uint*)chunk;
        int stride = *(int*)(chunk + 8);
        uint maxSize = *(uint*)(d + 20);
        int w, h;
        lock (_gate)
        {
            if (!_formatOk) return;
            w = _format.Width;
            h = _format.Height;
        }
        if (stride < w * 4 || (long)offset + (long)stride * (h - 1) + (long)w * 4 > maxSize)
        {
            MarkDead($"buffer {w}x{h} stride {stride} offset {offset} does not fit {maxSize} B");
            return;
        }
        var watch = System.Diagnostics.Stopwatch.StartNew();
        lock (_gate)
        {
            if (_pixels is null || _pixels.Length != w * h * 4) _pixels = new byte[w * h * 4];
            fixed (byte* dst = _pixels)
            {
                for (int y = 0; y < h; y++)
                {
                    Buffer.MemoryCopy(mem + offset + (long)y * stride, dst + (long)y * w * 4, w * 4, w * 4);
                }
            }
            _w = w;
            _h = h;
        }
        _frames.Bump();
        _firstSample.Set();
        if (Interlocked.Exchange(ref _logFirstAfterWake, 0) == 1)
        {
            FileLog.Write($"RoeSnip: 8-bit stream node {Geometry.NodeId} first frame after the wake: " +
                (ptsNs > 0 ? $"stamped {(ptsNs - minPts) / 1000} us after it" : "no timestamp") + $", copy {watch.ElapsedMilliseconds} ms");
        }
        if (Interlocked.Exchange(ref _firstLogged, 1) == 0)
        {
            FileLog.Write($"RoeSnip: 8-bit stream node {Geometry.NodeId} first frame copied in {watch.ElapsedMilliseconds} ms (stride {stride})");
        }
    }

    private void MarkDead(string reason)
    {
        if (_deadReason is not null) return;
        _deadReason = reason;
        FileLog.Write($"RoeSnip: 8-bit stream node {Geometry.NodeId} stopped: {reason}");
        _firstSample.Set();
    }

    public bool WaitForFirstSample(TimeSpan timeout) => _firstSample.Wait(timeout) && !IsDead;

    public void SetActive(bool active) => _owner.SetStreamActive(this, active);

    internal void SetActiveLocked(bool active)
    {
        if (active)
        {
            Volatile.Write(ref _minPtsNs, SpaFrameClock.NowNs());
            Volatile.Write(ref _logFirstAfterWake, 1);
        }
        if (_stream != IntPtr.Zero) pw_stream_set_active(_stream, active);
    }

    public bool WaitForFrameAfter(long sequence, TimeSpan timeout)
        => _frames.WaitAbove(sequence, timeout, () => IsDead) && !IsDead;

    /// <summary>Copies a block of the newest frame as tight BGRA (alpha forced to 255), nearest-sampling
    /// when the buffer is not the monitor's size. <paramref name="sequence"/> is the frame's own
    /// sequence, read under the lock the frame is written under.</summary>
    public bool TryCopy(int srcLeft, int srcTop, int outWidth, int outHeight,
        int monitorWidth, int monitorHeight, byte[] dest, out long sequence, out string? sizeNote)
    {
        sequence = 0;
        sizeNote = null;
        lock (_gate)
        {
            if (_pixels is null || _w <= 0 || _h <= 0) return false;
            sequence = _frames.Sequence;
            int fw = _w, fh = _h, outStride = outWidth * 4;
            fixed (byte* src = _pixels)
            fixed (byte* dst0 = dest)
            {
                if (fw == monitorWidth && fh == monitorHeight)
                {
                    int left = Math.Clamp(srcLeft, 0, Math.Max(0, fw - outWidth));
                    int top = Math.Clamp(srcTop, 0, Math.Max(0, fh - outHeight));
                    int w = Math.Min(outWidth, fw - left);
                    int h = Math.Min(outHeight, fh - top);
                    for (int y = 0; y < h; y++)
                    {
                        Buffer.MemoryCopy(src + ((long)(top + y) * fw + left) * 4, dst0 + (long)y * outStride, outStride, w * 4);
                    }
                }
                else
                {
                    sizeNote = $"stream buffer {fw}x{fh} != monitor {monitorWidth}x{monitorHeight}, nearest-sampling";
                    double sx = (double)fw / monitorWidth, sy = (double)fh / monitorHeight;
                    for (int y = 0; y < outHeight; y++)
                    {
                        int fy = Math.Min(fh - 1, (int)((srcTop + y) * sy));
                        uint* srow = (uint*)(src + (long)fy * fw * 4);
                        uint* drow = (uint*)(dst0 + (long)y * outStride);
                        for (int x = 0; x < outWidth; x++)
                        {
                            drow[x] = srow[Math.Min(fw - 1, (int)((srcLeft + x) * sx))];
                        }
                    }
                }
                uint* px = (uint*)dst0;
                long count = (long)outWidth * outHeight;
                for (long i = 0; i < count; i++) px[i] |= 0xFF000000u; // BGRx: X byte is undefined
            }
            return true;
        }
    }

    public void Dispose() => _owner.RemoveNode(this);
}
