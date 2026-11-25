using System.Runtime.InteropServices;

namespace RoeSnip.Platform.Linux;

/// <summary>The libpipewire-0.3 entry points the cursor reader and the HDR reader P/Invoke (the
/// spa_pod builders are header-only inline C, so pods are built by hand in Core/Capture/SpaPod.cs).</summary>
internal static unsafe class PipeWireNative
{
    public const string Lib = "libpipewire-0.3.so.0";

    [DllImport("libc", SetLastError = true)] public static extern int dup(int fd);
    [DllImport("libc")] public static extern int close(int fd);

    [DllImport(Lib)] public static extern void pw_init(IntPtr argc, IntPtr argv);
    [DllImport(Lib)] public static extern IntPtr pw_thread_loop_new([MarshalAs(UnmanagedType.LPUTF8Str)] string name, IntPtr props);
    [DllImport(Lib)] public static extern IntPtr pw_thread_loop_get_loop(IntPtr loop);
    [DllImport(Lib)] public static extern int pw_thread_loop_start(IntPtr loop);
    [DllImport(Lib)] public static extern void pw_thread_loop_stop(IntPtr loop);
    [DllImport(Lib)] public static extern void pw_thread_loop_lock(IntPtr loop);
    [DllImport(Lib)] public static extern void pw_thread_loop_unlock(IntPtr loop);
    [DllImport(Lib)] public static extern void pw_thread_loop_destroy(IntPtr loop);
    [DllImport(Lib)] public static extern IntPtr pw_context_new(IntPtr loop, IntPtr props, nuint userDataSize);
    [DllImport(Lib)] public static extern void pw_context_destroy(IntPtr context);
    [DllImport(Lib)] public static extern IntPtr pw_context_connect_fd(IntPtr context, int fd, IntPtr props, nuint userDataSize);
    [DllImport(Lib)] public static extern int pw_core_disconnect(IntPtr core);
    [DllImport(Lib)] public static extern IntPtr pw_properties_new_string([MarshalAs(UnmanagedType.LPUTF8Str)] string text);
    [DllImport(Lib)] public static extern IntPtr pw_stream_new(IntPtr core, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, IntPtr props);
    [DllImport(Lib)] public static extern void pw_stream_add_listener(IntPtr stream, IntPtr hook, IntPtr events, IntPtr data);
    [DllImport(Lib)] public static extern int pw_stream_connect(IntPtr stream, int direction, uint targetId, uint flags, IntPtr* parameters, uint count);
    [DllImport(Lib)] public static extern int pw_stream_update_params(IntPtr stream, IntPtr* parameters, uint count);
    [DllImport(Lib)] public static extern IntPtr pw_stream_dequeue_buffer(IntPtr stream);
    [DllImport(Lib)] public static extern int pw_stream_queue_buffer(IntPtr stream, IntPtr buffer);
    [DllImport(Lib)] public static extern void pw_stream_destroy(IntPtr stream);
}
