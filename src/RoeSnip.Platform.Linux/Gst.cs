using System.Runtime.InteropServices;
using RoeSnip.Core.Diagnostics;

namespace RoeSnip.Platform.Linux;

/// <summary>Minimal GStreamer 1.x P/Invoke surface (libgstreamer-1.0.so.0, libgstapp-1.0.so.0,
/// libglib-2.0.so.0). Pipelines are built with gst_parse_launch and driven by polling (appsink
/// pulls, bus pops), so no GLib main loop is needed.</summary>
internal static unsafe class Gst
{
    private const string LibGst = "libgstreamer-1.0.so.0";
    private const string LibGstApp = "libgstapp-1.0.so.0";
    private const string LibGlib = "libglib-2.0.so.0";

    public const int StateNull = 1;
    public const int StateReady = 2;
    public const int StatePlaying = 4;
    public const int StateChangeFailure = 0;

    public const int MessageEos = 1 << 0;
    public const int MessageError = 1 << 1;

    public const int MapRead = 1 << 0;
    public const int MapWrite = 1 << 1;

    public const long ClockTimeNone = -1;

    // GstBuffer public layout (64-bit): GstMiniObject (64 bytes), pool pointer, then pts/dts/duration.
    private const int BufferPtsOffset = 72;
    private const int BufferDurationOffset = 88;

    [StructLayout(LayoutKind.Sequential)]
    public struct MapInfo
    {
        public IntPtr Memory;
        public int Flags;
        public IntPtr Data;
        public nuint Size;
        public nuint MaxSize;
        public IntPtr User0, User1, User2, User3;
        public IntPtr Reserved0, Reserved1, Reserved2, Reserved3;
    }

    [DllImport(LibGst)] private static extern int gst_init_check(IntPtr argc, IntPtr argv, out IntPtr error);
    [DllImport(LibGst)] public static extern IntPtr gst_parse_launch([MarshalAs(UnmanagedType.LPUTF8Str)] string description, out IntPtr error);
    [DllImport(LibGst)] public static extern IntPtr gst_bin_get_by_name(IntPtr bin, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(LibGst)] public static extern int gst_element_set_state(IntPtr element, int state);
    [DllImport(LibGst)] public static extern int gst_element_get_state(IntPtr element, out int state, out int pending, long timeoutNs);
    [DllImport(LibGst)] public static extern IntPtr gst_element_get_bus(IntPtr element);
    [DllImport(LibGst)] public static extern IntPtr gst_element_factory_find([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(LibGst)] public static extern void gst_object_unref(IntPtr obj);
    [DllImport(LibGst)] public static extern void gst_mini_object_unref(IntPtr obj);
    [DllImport(LibGst)] public static extern IntPtr gst_bus_timed_pop_filtered(IntPtr bus, long timeoutNs, int types);
    [DllImport(LibGst)] public static extern void gst_message_parse_error(IntPtr message, out IntPtr gerror, out IntPtr debug);
    [DllImport(LibGst)] public static extern IntPtr gst_sample_get_buffer(IntPtr sample);
    [DllImport(LibGst)] public static extern IntPtr gst_sample_get_caps(IntPtr sample);
    [DllImport(LibGst)] public static extern IntPtr gst_caps_get_structure(IntPtr caps, uint index);
    [DllImport(LibGst)] public static extern int gst_structure_get_int(IntPtr structure, [MarshalAs(UnmanagedType.LPUTF8Str)] string field, out int value);
    [DllImport(LibGst)] public static extern int gst_buffer_map(IntPtr buffer, MapInfo* info, int flags);
    [DllImport(LibGst)] public static extern void gst_buffer_unmap(IntPtr buffer, MapInfo* info);
    [DllImport(LibGst)] public static extern IntPtr gst_buffer_new_allocate(IntPtr allocator, nuint size, IntPtr parameters);

    [DllImport(LibGstApp)] public static extern IntPtr gst_app_sink_try_pull_sample(IntPtr appsink, long timeoutNs);
    [DllImport(LibGstApp)] public static extern int gst_app_sink_is_eos(IntPtr appsink);
    [DllImport(LibGstApp)] public static extern int gst_app_src_push_buffer(IntPtr appsrc, IntPtr buffer);
    [DllImport(LibGstApp)] public static extern int gst_app_src_end_of_stream(IntPtr appsrc);

    [DllImport(LibGlib)] private static extern void g_error_free(IntPtr error);
    [DllImport(LibGlib)] private static extern void g_free(IntPtr mem);

    private static readonly object s_initGate = new();
    private static bool? s_available;
    private static string s_unavailableReason = "";

    /// <summary>True once gst_init_check succeeded and libgstapp loads. Cached for the process.</summary>
    public static bool Available => EnsureInit();

    public static string UnavailableReason
    {
        get { EnsureInit(); return s_unavailableReason; }
    }

    private static bool EnsureInit()
    {
        if (s_available is bool known) return known;
        lock (s_initGate)
        {
            if (s_available is bool again) return again;
            try
            {
                if (gst_init_check(IntPtr.Zero, IntPtr.Zero, out IntPtr err) == 0)
                {
                    s_unavailableReason = "gst_init_check failed: " + TakeErrorMessage(err);
                    s_available = false;
                }
                else
                {
                    // A missing libgstapp must fail here, not mid-capture.
                    if (NativeLibrary.TryLoad(LibGstApp, out _))
                    {
                        s_available = true;
                    }
                    else
                    {
                        s_unavailableReason = LibGstApp + " not found";
                        s_available = false;
                    }
                }
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            {
                s_unavailableReason = "GStreamer libraries not found: " + ex.Message;
                s_available = false;
            }
            if (s_available == true)
            {
                FileLog.Write("RoeSnip: GStreamer initialised");
            }
            else
            {
                FileLog.Write($"RoeSnip: GStreamer unavailable ({s_unavailableReason})");
            }
            return s_available.Value;
        }
    }

    public static bool HasElement(string factoryName)
    {
        if (!Available) return false;
        IntPtr f = gst_element_factory_find(factoryName);
        if (f == IntPtr.Zero) return false;
        gst_object_unref(f);
        return true;
    }

    /// <summary>gst_parse_launch that throws with GStreamer's own message. A non-null result with a
    /// non-null error is a recoverable parse warning; the pipeline is used and the error dropped.</summary>
    public static IntPtr ParseLaunch(string description)
    {
        IntPtr pipeline = gst_parse_launch(description, out IntPtr err);
        if (pipeline == IntPtr.Zero)
        {
            throw new InvalidOperationException("gst_parse_launch failed: " + TakeErrorMessage(err));
        }
        if (err != IntPtr.Zero) TakeErrorMessage(err);
        return pipeline;
    }

    /// <summary>Pops one pending ERROR or EOS message without blocking (or waiting up to
    /// <paramref name="timeoutNs"/>). Returns null when the bus is clean, else a description.</summary>
    public static string? PopFailure(IntPtr bus, long timeoutNs = 0)
    {
        IntPtr msg = gst_bus_timed_pop_filtered(bus, timeoutNs, MessageError);
        if (msg != IntPtr.Zero)
        {
            gst_message_parse_error(msg, out IntPtr gerr, out IntPtr debug);
            string text = TakeErrorMessage(gerr);
            if (debug != IntPtr.Zero) g_free(debug);
            gst_mini_object_unref(msg);
            return "error: " + text;
        }
        msg = gst_bus_timed_pop_filtered(bus, 0, MessageEos);
        if (msg != IntPtr.Zero)
        {
            gst_mini_object_unref(msg);
            return "end of stream";
        }
        return null;
    }

    /// <summary>Waits up to <paramref name="timeoutNs"/> for EOS or ERROR. Returns null on EOS,
    /// the error text on ERROR, or "timeout".</summary>
    public static string? WaitEos(IntPtr bus, long timeoutNs)
    {
        IntPtr msg = gst_bus_timed_pop_filtered(bus, timeoutNs, MessageEos | MessageError);
        if (msg == IntPtr.Zero) return "timeout";
        // GstMessage: GstMiniObject (64 bytes) then GstMessageType type.
        int type = Marshal.ReadInt32(msg, 64);
        string? result = null;
        if (type == MessageError)
        {
            gst_message_parse_error(msg, out IntPtr gerr, out IntPtr debug);
            result = TakeErrorMessage(gerr);
            if (debug != IntPtr.Zero) g_free(debug);
        }
        gst_mini_object_unref(msg);
        return result;
    }

    public static void SetBufferTiming(IntPtr buffer, long ptsNs, long durationNs)
    {
        Marshal.WriteInt64(buffer, BufferPtsOffset, ptsNs);
        Marshal.WriteInt64(buffer, BufferDurationOffset, durationNs);
    }

    /// <summary>Stops and releases a pipeline. Safe on IntPtr.Zero.</summary>
    public static void Shutdown(IntPtr pipeline)
    {
        if (pipeline == IntPtr.Zero) return;
        try
        {
            gst_element_set_state(pipeline, StateNull);
            gst_object_unref(pipeline);
        }
        catch
        {
            // teardown is best-effort
        }
    }

    private static string TakeErrorMessage(IntPtr gerror)
    {
        if (gerror == IntPtr.Zero) return "unknown error";
        // GError: GQuark domain (4), gint code (4), gchar* message.
        string text = Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(gerror, 8)) ?? "unknown error";
        g_error_free(gerror);
        return text;
    }
}
