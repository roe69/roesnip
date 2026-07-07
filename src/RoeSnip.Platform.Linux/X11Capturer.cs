using System.Numerics;
using System.Runtime.InteropServices;
using RoeSnip.Core.Capture;

namespace RoeSnip.Platform.Linux;

/// <summary>Raw X11 <c>XGetImage</c> fallback capturer for portal-less X sessions (PLAN-XPLAT.md
/// §3.4). Also hosts the RandR monitor-enumeration path (<see cref="EnumerateMonitorsViaRandR"/>)
/// that <see cref="LinuxCaptureBackend"/> uses regardless of which capturer ultimately succeeds —
/// both the portal slicer and this capturer need the same monitor-bounds list, and keeping the
/// enumeration on RandR (not Avalonia's Screens) keeps this project independent of RoeSnip.App.
///
/// All Xlib/XRandR functions are local <c>DllImport</c>s per the plan — they compile on any host OS
/// and only need <c>libX11.so.6</c>/<c>libXrandr.so.2</c> to exist at runtime on a real Linux box.
/// Output frames are always <see cref="FrameFormat.Bgra8Srgb"/> (X11 pixmaps are SDR by definition;
/// no HDR capture path on Linux in v1, per DESIGN-XPLAT.md).</summary>
public sealed class X11Capturer : IScreenCapturer
{
    private const string LibX11 = "libX11.so.6";
    private const string LibXrandr = "libXrandr.so.2";

    private const int ZPixmap = 2;
    private const int LsbFirst = 0;
    private const ushort RrConnected = 0;
    private static readonly nuint AllPlanes = unchecked((nuint)ulong.MaxValue);

    public CapturedFrame Capture(MonitorInfo monitor)
    {
        IntPtr display = XOpenDisplay(IntPtr.Zero);
        if (display == IntPtr.Zero)
        {
            throw new CaptureException(
                "XOpenDisplay failed — no X11 (or XWayland) server reachable via DISPLAY.");
        }

        // Xlib's DEFAULT error handler terminates the process (e.g. a BadMatch if the monitor
        // bounds raced a resolution change between enumeration and capture). Install a silent
        // handler around our synchronous calls and restore the previous one afterward.
        IntPtr previousHandler = XSetErrorHandler(SilentErrorHandler);
        try
        {
            nuint root = XDefaultRootWindow(display);
            var bounds = monitor.BoundsPx;

            // RandR-reported monitor geometry can exceed the actual root window (observed under
            // WSLg, where XWayland advertises the host's monitor layout while the root window is
            // smaller). XGetImage hard-fails on any out-of-root pixel, so clamp the request to
            // the root geometry and capture the intersection instead of failing the monitor.
            if (XGetGeometry(display, root, out _, out int rootX, out int rootY,
                    out uint rootW, out uint rootH, out _, out _) != 0)
            {
                var rootRect = RectPhysical.FromSize(rootX, rootY, (int)rootW, (int)rootH);
                var clamped = new RectPhysical(
                    Math.Max(bounds.Left, rootRect.Left), Math.Max(bounds.Top, rootRect.Top),
                    Math.Min(bounds.Right, rootRect.Right), Math.Min(bounds.Bottom, rootRect.Bottom));
                if (clamped.Width <= 0 || clamped.Height <= 0)
                {
                    throw new CaptureException(
                        $"Monitor {monitor.Index} ({monitor.DeviceName}) at {bounds.Left},{bounds.Top} " +
                        $"{bounds.Width}x{bounds.Height} lies entirely outside the X root window " +
                        $"({rootW}x{rootH}) — nothing to capture.");
                }
                if (clamped != bounds)
                {
                    Console.Error.WriteLine(
                        $"RoeSnip: monitor {monitor.Index} ({monitor.DeviceName}) extends beyond the " +
                        $"X root window ({rootW}x{rootH}); capturing the visible " +
                        $"{clamped.Width}x{clamped.Height} intersection.");
                }
                bounds = clamped;
            }

            IntPtr imagePtr = XGetImage(
                display, root, bounds.Left, bounds.Top,
                (uint)bounds.Width, (uint)bounds.Height, AllPlanes, ZPixmap);
            if (imagePtr == IntPtr.Zero)
            {
                throw new CaptureException(
                    $"XGetImage returned no image for monitor {monitor.Index} ({monitor.DeviceName}) " +
                    $"at {bounds.Left},{bounds.Top} {bounds.Width}x{bounds.Height} " +
                    "(X error, or bounds outside the root window).");
            }

            try
            {
                var image = Marshal.PtrToStructure<XImage>(imagePtr);
                byte[] pixels = ConvertZPixmapToBgra(in image, bounds.Width, bounds.Height, monitor);
                return new CapturedFrame(
                    FrameFormat.Bgra8Srgb, bounds.Width, bounds.Height, bounds.Width * 4, pixels,
                    monitor, sdrWhiteInBufferUnits: 1.0);
            }
            finally
            {
                DestroyImage(imagePtr);
            }
        }
        finally
        {
            XSetErrorHandlerNative(previousHandler);
            XCloseDisplay(display);
        }
    }

    /// <summary>Enumerates active monitors via XRandR (connected outputs with an active CRTC).
    /// Contract per ICaptureBackend.EnumerateMonitors: never throws for a single bad entry (logs
    /// and omits it); returns an empty list only if enumeration itself fails entirely (e.g. no X
    /// server at all — a pure-Wayland session without XWayland).</summary>
    internal static IReadOnlyList<MonitorInfo> EnumerateMonitorsViaRandR()
    {
        var result = new List<MonitorInfo>();
        IntPtr display = XOpenDisplay(IntPtr.Zero);
        if (display == IntPtr.Zero)
        {
            Console.Error.WriteLine(
                "RoeSnip: XOpenDisplay failed (no X11/XWayland DISPLAY?) — cannot enumerate monitors.");
            return result;
        }

        IntPtr previousHandler = XSetErrorHandler(SilentErrorHandler);
        try
        {
            nuint root = XDefaultRootWindow(display);
            IntPtr resourcesPtr = XRRGetScreenResources(display, root);
            if (resourcesPtr == IntPtr.Zero)
            {
                Console.Error.WriteLine("RoeSnip: XRRGetScreenResources failed — is the RandR extension available?");
                return result;
            }

            try
            {
                var resources = Marshal.PtrToStructure<XRRScreenResources>(resourcesPtr);
                nuint primaryOutput = XRRGetOutputPrimary(display, root);

                int index = 0;
                for (int i = 0; i < resources.NOutput; i++)
                {
                    try
                    {
                        nuint output = (nuint)(nint)Marshal.ReadIntPtr(resources.Outputs, i * IntPtr.Size);
                        MonitorInfo? monitor = ReadOneOutput(display, resourcesPtr, output, primaryOutput, ref index);
                        if (monitor is not null)
                        {
                            result.Add(monitor);
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"RoeSnip: skipping RandR output #{i}: {ex.Message}");
                    }
                }
            }
            finally
            {
                XRRFreeScreenResources(resourcesPtr);
            }

            // RRGetOutputPrimary may legitimately be None (0); the app still needs exactly one
            // primary for its own ordering conventions — promote the first monitor in that case.
            if (result.Count > 0 && !result.Any(m => m.IsPrimary))
            {
                result[0] = result[0] with { IsPrimary = true };
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"RoeSnip: RandR monitor enumeration failed: {ex.Message}");
        }
        finally
        {
            XSetErrorHandlerNative(previousHandler);
            XCloseDisplay(display);
        }

        return result;
    }

    private static MonitorInfo? ReadOneOutput(
        IntPtr display, IntPtr resourcesPtr, nuint output, nuint primaryOutput, ref int index)
    {
        IntPtr outputInfoPtr = XRRGetOutputInfo(display, resourcesPtr, output);
        if (outputInfoPtr == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var outputInfo = Marshal.PtrToStructure<XRROutputInfo>(outputInfoPtr);
            if (outputInfo.Connection != RrConnected || outputInfo.Crtc == 0)
            {
                return null; // disconnected, or connected but not driving a CRTC (inactive)
            }

            IntPtr crtcInfoPtr = XRRGetCrtcInfo(display, resourcesPtr, outputInfo.Crtc);
            if (crtcInfoPtr == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                var crtcInfo = Marshal.PtrToStructure<XRRCrtcInfo>(crtcInfoPtr);
                if (crtcInfo.Width == 0 || crtcInfo.Height == 0)
                {
                    return null;
                }

                string name = outputInfo.Name != IntPtr.Zero && outputInfo.NameLen > 0
                    ? Marshal.PtrToStringAnsi(outputInfo.Name, outputInfo.NameLen)
                    : $"output-{output}";

                // RandR reports physical pixels on real X11; DPI/scale have no reliable per-monitor
                // X11 story, so report the neutral 96/1.0 — the portal capturer independently
                // verifies the actual pixel scale at capture time (PLAN-XPLAT.md §3.4), and
                // SdrWhiteNits/AdvancedColorActive are the pinned Linux v1 constants (§3.4/§6 flag 6).
                return new MonitorInfo(
                    Index: index++,
                    DeviceName: name,
                    BackendKey: name,
                    BoundsPx: RectPhysical.FromSize(crtcInfo.X, crtcInfo.Y, (int)crtcInfo.Width, (int)crtcInfo.Height),
                    DpiX: 96,
                    DpiY: 96,
                    Scale: 1.0,
                    AdvancedColorActive: false,
                    SdrWhiteNits: 240.0,
                    MaxLuminanceNits: 1000.0,
                    IsPrimary: output == primaryOutput);
            }
            finally
            {
                XRRFreeCrtcInfo(crtcInfoPtr);
            }
        }
        finally
        {
            XRRFreeOutputInfo(outputInfoPtr);
        }
    }

    private static byte[] ConvertZPixmapToBgra(in XImage image, int width, int height, MonitorInfo monitor)
    {
        if (image.Data == IntPtr.Zero)
        {
            throw new CaptureException($"XGetImage returned an image with no pixel data for monitor {monitor.Index}.");
        }
        if (image.BitsPerPixel != 24 && image.BitsPerPixel != 32)
        {
            throw new CaptureException(
                $"unsupported ZPixmap bits_per_pixel={image.BitsPerPixel} (depth {image.Depth}) for monitor " +
                $"{monitor.Index} ({monitor.DeviceName}) — only 24/32bpp are supported.");
        }
        if (image.Width < width || image.Height < height)
        {
            throw new CaptureException(
                $"XGetImage returned {image.Width}x{image.Height} but {width}x{height} was requested " +
                $"for monitor {monitor.Index} ({monitor.DeviceName}).");
        }

        int srcBytesPerPixel = image.BitsPerPixel / 8;
        int srcRowBytes = width * srcBytesPerPixel;
        var row = new byte[srcRowBytes];
        var output = new byte[width * height * 4];

        // The overwhelmingly common case on x86 X servers: 32bpp LSBFirst with the standard
        // 0xFF0000/0xFF00/0xFF masks is already B,G,R,X in memory — copy rows and force alpha.
        bool fastPath = image.BitsPerPixel == 32
            && image.ByteOrder == LsbFirst
            && (uint)image.RedMask == 0x00FF0000u
            && (uint)image.GreenMask == 0x0000FF00u
            && (uint)image.BlueMask == 0x000000FFu;

        (int redShift, uint redMax) = MaskInfo((uint)image.RedMask);
        (int greenShift, uint greenMax) = MaskInfo((uint)image.GreenMask);
        (int blueShift, uint blueMax) = MaskInfo((uint)image.BlueMask);

        for (int y = 0; y < height; y++)
        {
            Marshal.Copy(IntPtr.Add(image.Data, y * image.BytesPerLine), row, 0, srcRowBytes);
            int dst = y * width * 4;

            if (fastPath)
            {
                Buffer.BlockCopy(row, 0, output, dst, srcRowBytes);
                for (int x = 0; x < width; x++)
                {
                    output[dst + x * 4 + 3] = 255; // root windows have no alpha — force opaque
                }
            }
            else
            {
                for (int x = 0; x < width; x++)
                {
                    int o = x * srcBytesPerPixel;
                    uint value = 0;
                    if (image.ByteOrder == LsbFirst)
                    {
                        for (int k = srcBytesPerPixel - 1; k >= 0; k--)
                        {
                            value = (value << 8) | row[o + k];
                        }
                    }
                    else
                    {
                        for (int k = 0; k < srcBytesPerPixel; k++)
                        {
                            value = (value << 8) | row[o + k];
                        }
                    }

                    output[dst + x * 4 + 0] = ExtractChannel(value, blueShift, blueMax);
                    output[dst + x * 4 + 1] = ExtractChannel(value, greenShift, greenMax);
                    output[dst + x * 4 + 2] = ExtractChannel(value, redShift, redMax);
                    output[dst + x * 4 + 3] = 255;
                }
            }
        }

        return output;
    }

    private static (int Shift, uint Max) MaskInfo(uint mask)
    {
        if (mask == 0)
        {
            return (0, 0);
        }
        int shift = BitOperations.TrailingZeroCount(mask);
        return (shift, mask >> shift);
    }

    private static byte ExtractChannel(uint pixel, int shift, uint max)
    {
        if (max == 0)
        {
            return 0;
        }
        uint value = (pixel >> shift) & max;
        return max == 255 ? (byte)value : (byte)(value * 255u / max);
    }

    private static void DestroyImage(IntPtr imagePtr)
    {
        try
        {
            XDestroyImage(imagePtr);
        }
        catch (EntryPointNotFoundException)
        {
            // Ancient libX11 builds export XDestroyImage only as a macro — fall back to the
            // image's own destroy_image function pointer (offset-stable in the XImage funcs vtable).
            var image = Marshal.PtrToStructure<XImage>(imagePtr);
            if (image.DestroyImageFn != IntPtr.Zero)
            {
                Marshal.GetDelegateForFunctionPointer<XDestroyImageDelegate>(image.DestroyImageFn)(imagePtr);
            }
        }
    }

    // ---- Xlib / XRandR interop (local to this file per PLAN-XPLAT.md §3.4) --------------------

    private delegate int XErrorHandlerDelegate(IntPtr display, IntPtr errorEvent);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int XDestroyImageDelegate(IntPtr image);

    // Kept rooted in a static field so the native side never sees a collected delegate.
    private static readonly XErrorHandlerDelegate SilentErrorHandler = static (_, _) => 0;

    [DllImport(LibX11)]
    private static extern IntPtr XOpenDisplay(IntPtr displayName);

    [DllImport(LibX11)]
    private static extern int XCloseDisplay(IntPtr display);

    [DllImport(LibX11)]
    private static extern nuint XDefaultRootWindow(IntPtr display);

    [DllImport(LibX11)]
    private static extern IntPtr XGetImage(
        IntPtr display, nuint drawable, int x, int y, uint width, uint height, nuint planeMask, int format);

    [DllImport(LibX11)]
    private static extern int XGetGeometry(
        IntPtr display, nuint drawable, out nuint rootReturn, out int x, out int y,
        out uint width, out uint height, out uint borderWidth, out uint depth);

    [DllImport(LibX11)]
    private static extern int XDestroyImage(IntPtr image);

    [DllImport(LibX11)]
    private static extern IntPtr XSetErrorHandler(XErrorHandlerDelegate? handler);

    [DllImport(LibX11, EntryPoint = "XSetErrorHandler")]
    private static extern IntPtr XSetErrorHandlerNative(IntPtr handler);

    [DllImport(LibXrandr)]
    private static extern IntPtr XRRGetScreenResources(IntPtr display, nuint window);

    [DllImport(LibXrandr)]
    private static extern void XRRFreeScreenResources(IntPtr resources);

    [DllImport(LibXrandr)]
    private static extern IntPtr XRRGetOutputInfo(IntPtr display, IntPtr resources, nuint output);

    [DllImport(LibXrandr)]
    private static extern void XRRFreeOutputInfo(IntPtr outputInfo);

    [DllImport(LibXrandr)]
    private static extern IntPtr XRRGetCrtcInfo(IntPtr display, IntPtr resources, nuint crtc);

    [DllImport(LibXrandr)]
    private static extern void XRRFreeCrtcInfo(IntPtr crtcInfo);

    [DllImport(LibXrandr)]
    private static extern nuint XRRGetOutputPrimary(IntPtr display, nuint window);

#pragma warning disable CS0169, CS0649 // interop layout structs: fields assigned by marshalling, some present only for layout

    /// <summary>LP64 layout of Xlib's XImage (fields through the first two funcs vtable slots —
    /// PtrToStructure only reads the declared prefix, which is all we need).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct XImage
    {
        public int Width;
        public int Height;
        public int XOffset;
        public int Format;
        public IntPtr Data;
        public int ByteOrder;      // 0 = LSBFirst, 1 = MSBFirst
        public int BitmapUnit;
        public int BitmapBitOrder;
        public int BitmapPad;
        public int Depth;
        public int BytesPerLine;
        public int BitsPerPixel;
        public nuint RedMask;      // unsigned long on LP64
        public nuint GreenMask;
        public nuint BlueMask;
        public IntPtr ObData;
        public IntPtr CreateImageFn;  // funcs.create_image (layout only)
        public IntPtr DestroyImageFn; // funcs.destroy_image (fallback destroy path)
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XRRScreenResources
    {
        public nuint Timestamp;       // Time (unsigned long)
        public nuint ConfigTimestamp;
        public int NCrtc;
        public IntPtr Crtcs;          // RRCrtc*
        public int NOutput;
        public IntPtr Outputs;        // RROutput*
        public int NMode;
        public IntPtr Modes;          // XRRModeInfo*
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XRROutputInfo
    {
        public nuint Timestamp;
        public nuint Crtc;            // RRCrtc
        public IntPtr Name;           // char*
        public int NameLen;
        public nuint MmWidth;
        public nuint MmHeight;
        public ushort Connection;     // 0 = RR_Connected
        public ushort SubpixelOrder;
        public int NCrtc;
        public IntPtr Crtcs;
        public int NClone;
        public IntPtr Clones;
        public int NMode;
        public int NPreferred;
        public IntPtr Modes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XRRCrtcInfo
    {
        public nuint Timestamp;
        public int X;
        public int Y;
        public uint Width;
        public uint Height;
        public nuint Mode;            // RRMode
        public ushort Rotation;
        public int NOutput;
        public IntPtr Outputs;
        public ushort Rotations;
        public int NPossible;
        public IntPtr Possible;
    }

#pragma warning restore CS0169, CS0649
}
