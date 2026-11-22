using System.Runtime.InteropServices;
using RoeSnip.Core.Capture;
using RoeSnip.Core.Diagnostics;

namespace RoeSnip.Platform.Linux;

/// <summary>Reads SPA_META_Cursor from the snapshot screencast's nodes with libpipewire-0.3
/// (session opened with cursor_mode METADATA). pipewiresrc requests the cursor meta but only turns
/// its position into a ROI meta, with no sprite, and drops mutter's cursor-only buffers (chunk size
/// 0), so the cursor would freeze over a still screen. This consumer runs alongside the GStreamer
/// pipelines on its own OpenPipeWireRemote fd of the same session (no second share dialog; never a
/// dup of the gst fd: that shares one socket between two cores), links a second input to each node
/// (mutter shares its buffers between links) with a format that intersects the one already fixed,
/// asks for no Buffers param (the output's are used), never maps pixel data and requeues every
/// buffer at once. The cursor meta range must reach mutter's fixed 384x384 size (589872 B).</summary>
internal sealed unsafe class PipeWireCursorReader : IDisposable
{
    private const string Lib = "libpipewire-0.3.so.0";
    private const int StateError = -1, StateStreaming = 3;
    private const uint FlagAutoconnect = 1 << 0, FlagDontReconnect = 1 << 7;
    private const int MaxSpriteSize = 384;

    private static int s_initialized;

    /// <summary>The latest cursor of one node, buffer px. Immutable; swapped whole.</summary>
    public sealed record State(bool Valid, int X, int Y, int BufferWidth, int BufferHeight, CursorSprite? Sprite, long Sequence, long At);

    private readonly IntPtr _loop;
    private readonly IntPtr _context;
    private readonly IntPtr _core;
    private readonly List<Node> _nodes = new();
    private int _disposed;

    public bool AnyMetaSeen => _nodes.Any(n => n.MetaSeen);

    public string StatesText => string.Join(", ", _nodes.Select(n => $"node {n.NodeId} {StateName(n.StreamState)}{(n.Error is null ? "" : " (" + n.Error + ")")}"));

    private PipeWireCursorReader(IntPtr loop, IntPtr context, IntPtr core)
    {
        _loop = loop;
        _context = context;
        _core = core;
    }

    /// <summary>Starts one cursor stream per node. Null with <paramref name="why"/> on failure.</summary>
    public static PipeWireCursorReader? TryStart(int portalFd, IEnumerable<uint> nodeIds, out string why)
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
        if (Interlocked.Exchange(ref s_initialized, 1) == 0)
        {
            pw_init(IntPtr.Zero, IntPtr.Zero);
        }
        IntPtr loop = pw_thread_loop_new("roesnip-cursor", IntPtr.Zero);
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
        var reader = new PipeWireCursorReader(loop, context, core);
        if (core == IntPtr.Zero)
        {
            why = "pw_context_connect_fd failed";
        }
        else
        {
            foreach (uint id in nodeIds)
            {
                var node = new Node(id);
                reader._nodes.Add(node); // freed by Teardown even when its connect failed
                if (node.Connect(core) is string err) why = $"node {id}: {err}";
            }
        }
        pw_thread_loop_unlock(loop);
        if (why.Length == 0) return reader;
        reader.Dispose();
        return null;
    }

    /// <summary>The newest cursor of <paramref name="nodeId"/>, or null while that stream is not
    /// streaming.</summary>
    public State? Get(uint nodeId)
    {
        if (Volatile.Read(ref _disposed) != 0) return null;
        foreach (var n in _nodes)
        {
            if (n.NodeId == nodeId) return n.StreamState == StateStreaming ? n.Latest : null;
        }
        return null;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        pw_thread_loop_lock(_loop);
        foreach (var n in _nodes) n.Destroy();
        if (_core != IntPtr.Zero) pw_core_disconnect(_core);
        pw_thread_loop_unlock(_loop);
        pw_thread_loop_stop(_loop);
        pw_context_destroy(_context);
        pw_thread_loop_destroy(_loop);
        foreach (var n in _nodes) n.Free();
    }

    private static string StateName(int state) => state switch
    {
        -1 => "error", 0 => "unconnected", 1 => "connecting", 2 => "paused", 3 => "streaming", _ => state.ToString(),
    };

    /// <summary>One pw_stream on one node. Callbacks run on the thread loop.</summary>
    private sealed class Node
    {
        private IntPtr _stream;
        private readonly IntPtr _hook;
        private readonly IntPtr _events;
        private GCHandle _self;
        private CursorSprite? _sprite;
        private int _bufW, _bufH;
        private long _sequence;
        private bool _buffersLogged;

        public uint NodeId { get; }
        public volatile int StreamState;
        public volatile bool MetaSeen;
        public volatile string? Error;
        public volatile State? Latest;

        public Node(uint nodeId)
        {
            NodeId = nodeId;
            _hook = Marshal.AllocHGlobal(64);
            new Span<byte>((void*)_hook, 64).Clear();
            _events = Marshal.AllocHGlobal(96);
            new Span<byte>((void*)_events, 96).Clear();
            // pw_stream_events version 0: version, destroy, state_changed, control_info, io_changed,
            // param_changed, add_buffer, remove_buffer, process, drained (pointers from offset 8).
            var e = (IntPtr*)_events;
            *(uint*)_events = 0;
            e[2] = Marshal.GetFunctionPointerForDelegate(s_stateChanged);
            e[5] = Marshal.GetFunctionPointerForDelegate(s_paramChanged);
            e[6] = Marshal.GetFunctionPointerForDelegate(s_addBuffer);
            e[8] = Marshal.GetFunctionPointerForDelegate(s_process);
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void StateChangedFn(IntPtr data, int old, int state, IntPtr error);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void ParamChangedFn(IntPtr data, uint id, IntPtr param);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void AddBufferFn(IntPtr data, IntPtr buffer);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void ProcessFn(IntPtr data);

        // Static so the thunks stay rooted for the process life.
        private static readonly StateChangedFn s_stateChanged = OnStateChanged;
        private static readonly ParamChangedFn s_paramChanged = OnParamChanged;
        private static readonly AddBufferFn s_addBuffer = OnAddBuffer;
        private static readonly ProcessFn s_process = OnProcess;

        public string? Connect(IntPtr core)
        {
            IntPtr props = pw_properties_new_string("media.type=Video media.category=Capture media.role=Screen node.name=roesnip-cursor");
            _stream = pw_stream_new(core, "roesnip-cursor", props);
            if (_stream == IntPtr.Zero) return "pw_stream_new failed";
            _self = GCHandle.Alloc(this);
            pw_stream_add_listener(_stream, _hook, _events, GCHandle.ToIntPtr(_self));
            byte[] format = SpaPod.RawVideoEnumFormat();
            fixed (byte* f = format)
            {
                IntPtr* list = stackalloc IntPtr[1];
                list[0] = (IntPtr)f;
                int rc = pw_stream_connect(_stream, 0 /* input */, NodeId, FlagAutoconnect | FlagDontReconnect, list, 1);
                if (rc < 0) return $"pw_stream_connect returned {rc}";
            }
            return null;
        }

        public void Destroy()
        {
            if (_stream != IntPtr.Zero)
            {
                pw_stream_destroy(_stream);
                _stream = IntPtr.Zero;
            }
        }

        public void Free()
        {
            if (_self.IsAllocated) _self.Free();
            Marshal.FreeHGlobal(_hook);
            Marshal.FreeHGlobal(_events);
        }

        private static Node? From(IntPtr data)
        {
            try { return GCHandle.FromIntPtr(data).Target as Node; }
            catch { return null; }
        }

        private static void OnStateChanged(IntPtr data, int old, int state, IntPtr error)
        {
            if (From(data) is not { } n) return;
            n.StreamState = state;
            if (state == StateError)
            {
                n.Error = error == IntPtr.Zero ? "error" : Marshal.PtrToStringUTF8(error);
                FileLog.Write($"RoeSnip: screencast cursor stream node {n.NodeId} failed: {n.Error}");
            }
        }

        private static void OnParamChanged(IntPtr data, uint id, IntPtr param)
        {
            if (From(data) is not { } n || id != SpaPod.ParamFormat || param == IntPtr.Zero) return;
            try
            {
                uint size = *(uint*)param;
                var pod = new ReadOnlySpan<byte>((void*)param, (int)Math.Min(size + 8, 65536));
                if (SpaPod.TryReadVideoSize(pod, out int w, out int h))
                {
                    n._bufW = w;
                    n._bufH = h;
                }
                byte[] header = SpaPod.MetaParam(SpaPod.MetaHeader, SpaPod.MetaHeaderSize);
                byte[] cursor = SpaPod.CursorMetaParam(MaxSpriteSize);
                fixed (byte* hp = header)
                fixed (byte* cp = cursor)
                {
                    IntPtr* list = stackalloc IntPtr[2];
                    list[0] = (IntPtr)hp;
                    list[1] = (IntPtr)cp;
                    int rc = pw_stream_update_params(n._stream, list, 2);
                    FileLog.Write($"RoeSnip: screencast cursor stream node {n.NodeId} format {w}x{h}, " +
                        $"asked for Header {SpaPod.MetaHeaderSize} B + Cursor up to {MaxSpriteSize}x{MaxSpriteSize} (update_params {rc})");
                }
            }
            catch (Exception ex)
            {
                n.Error = "format: " + ex.Message;
            }
        }

        private static void OnAddBuffer(IntPtr data, IntPtr pwBuffer)
        {
            if (From(data) is not { } n || pwBuffer == IntPtr.Zero) return;
            bool has = FindCursorMeta(*(IntPtr*)pwBuffer, out _, out int size);
            if (!n._buffersLogged)
            {
                n._buffersLogged = true;
                FileLog.Write($"RoeSnip: screencast cursor stream node {n.NodeId} buffers: " +
                    (has ? $"cursor meta {size} B" : "no cursor meta"));
            }
            if (has) n.MetaSeen = true;
        }

        private static void OnProcess(IntPtr data)
        {
            if (From(data) is not { } n || n._stream == IntPtr.Zero) return;
            try
            {
                IntPtr b;
                while ((b = pw_stream_dequeue_buffer(n._stream)) != IntPtr.Zero)
                {
                    try { n.Read(*(IntPtr*)b); }
                    finally { pw_stream_queue_buffer(n._stream, b); }
                }
            }
            catch (Exception ex)
            {
                n.Error = "process: " + ex.Message;
            }
        }

        private void Read(IntPtr spaBuffer)
        {
            if (!FindCursorMeta(spaBuffer, out IntPtr meta, out int size)) return;
            MetaSeen = true;
            if (!SpaCursorMeta.TryParse(new ReadOnlySpan<byte>((void*)meta, size), out var c)) return;
            if (c.HasSprite) _sprite = c.Sprite;
            var prev = Latest;
            if (prev is not null && prev.Valid == c.Valid && prev.X == c.X && prev.Y == c.Y
                && ReferenceEquals(prev.Sprite, _sprite) && prev.BufferWidth == _bufW && prev.BufferHeight == _bufH)
            {
                return;
            }
            Latest = new State(c.Valid, c.X, c.Y, _bufW, _bufH, _sprite, ++_sequence, System.Diagnostics.Stopwatch.GetTimestamp());
        }

        /// <summary>spa_buffer {u32 n_metas, u32 n_datas, spa_meta* metas, ...};
        /// spa_meta {u32 type, u32 size, void* data}.</summary>
        private static bool FindCursorMeta(IntPtr spaBuffer, out IntPtr data, out int size)
        {
            data = IntPtr.Zero;
            size = 0;
            if (spaBuffer == IntPtr.Zero) return false;
            uint count = *(uint*)spaBuffer;
            byte* metas = *(byte**)((byte*)spaBuffer + 8);
            for (uint i = 0; i < count && metas != null; i++)
            {
                byte* m = metas + i * 16;
                if (*(uint*)m != SpaPod.MetaCursor) continue;
                data = *(IntPtr*)(m + 8);
                size = (int)*(uint*)(m + 4);
                return data != IntPtr.Zero && size >= SpaCursorMeta.CursorSize;
            }
            return false;
        }
    }

    [DllImport("libc", SetLastError = true)] private static extern int dup(int fd);
    [DllImport("libc")] private static extern int close(int fd);

    [DllImport(Lib)] private static extern void pw_init(IntPtr argc, IntPtr argv);
    [DllImport(Lib)] private static extern IntPtr pw_thread_loop_new([MarshalAs(UnmanagedType.LPUTF8Str)] string name, IntPtr props);
    [DllImport(Lib)] private static extern IntPtr pw_thread_loop_get_loop(IntPtr loop);
    [DllImport(Lib)] private static extern int pw_thread_loop_start(IntPtr loop);
    [DllImport(Lib)] private static extern void pw_thread_loop_stop(IntPtr loop);
    [DllImport(Lib)] private static extern void pw_thread_loop_lock(IntPtr loop);
    [DllImport(Lib)] private static extern void pw_thread_loop_unlock(IntPtr loop);
    [DllImport(Lib)] private static extern void pw_thread_loop_destroy(IntPtr loop);
    [DllImport(Lib)] private static extern IntPtr pw_context_new(IntPtr loop, IntPtr props, nuint userDataSize);
    [DllImport(Lib)] private static extern void pw_context_destroy(IntPtr context);
    [DllImport(Lib)] private static extern IntPtr pw_context_connect_fd(IntPtr context, int fd, IntPtr props, nuint userDataSize);
    [DllImport(Lib)] private static extern int pw_core_disconnect(IntPtr core);
    [DllImport(Lib)] private static extern IntPtr pw_properties_new_string([MarshalAs(UnmanagedType.LPUTF8Str)] string text);
    [DllImport(Lib)] private static extern IntPtr pw_stream_new(IntPtr core, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, IntPtr props);
    [DllImport(Lib)] private static extern void pw_stream_add_listener(IntPtr stream, IntPtr hook, IntPtr events, IntPtr data);
    [DllImport(Lib)] private static extern int pw_stream_connect(IntPtr stream, int direction, uint targetId, uint flags, IntPtr* parameters, uint count);
    [DllImport(Lib)] private static extern int pw_stream_update_params(IntPtr stream, IntPtr* parameters, uint count);
    [DllImport(Lib)] private static extern IntPtr pw_stream_dequeue_buffer(IntPtr stream);
    [DllImport(Lib)] private static extern int pw_stream_queue_buffer(IntPtr stream, IntPtr buffer);
    [DllImport(Lib)] private static extern void pw_stream_destroy(IntPtr stream);
}
