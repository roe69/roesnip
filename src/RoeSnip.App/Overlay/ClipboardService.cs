using System.Runtime.InteropServices;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using RoeSnip.Core.Diagnostics;
using RoeSnip.Core.Imaging;

namespace RoeSnip.App.Overlay;

/// <summary>Per-OS clipboard adapters in one file with runtime OS branches (PLAN-XPLAT.md §3.3,
/// DESIGN-XPLAT.md "Clipboard adapters"):
///   - Windows: the exact P/Invoke PNG+CF_DIBV5 transaction ported from the frozen WPF app's
///     src/RoeSnip/Imaging/ClipboardService.cs — both "paste as PNG" and "paste as bitmap"
///     consumers work from one copy. The DllImport declarations compile on every host OS; only
///     the calls are gated behind OperatingSystem.IsWindows().
///   - macOS: NSPasteboard PNG via Objective-C-runtime P/Invoke (objc_msgSend), per the plan's
///     preferred approach; falls back to Avalonia's own clipboard if the objc path throws.
///     NOT runtime-verifiable on this machine — see the WP-X3 report.
///   - Linux (and any other case): Avalonia's IClipboard with an "image/png" data object —
///     Avalonia 12 fixed X11 INCR transfers, so full-size screenshots are expected to work.</summary>
public static class ClipboardService
{
    /// <summary>Copies the rendered snip as an image. Throws on failure (the caller treats a
    /// failed copy as non-fatal, exactly like the WPF app's OverlayController does).</summary>
    public static async Task CopyImageAsync(Visual owner, SdrImage image)
    {
        byte[] pngBytes = PngWriter.Encode(image);

        if (OperatingSystem.IsWindows())
        {
            CopyImageWindows(image, pngBytes);
            return;
        }

        if (OperatingSystem.IsMacOS())
        {
            try
            {
                MacPasteboard.SetPng(pngBytes);
                return;
            }
            catch (Exception ex)
            {
                FileLog.Write(
                    $"RoeSnip: NSPasteboard PNG write failed ({ex.Message}); falling back to the Avalonia clipboard.");
            }
        }

        await CopyPngViaAvaloniaAsync(owner, pngBytes);
    }

    /// <summary>Copies plain text (the color inspector / magnifier hex readout). Returns whether
    /// the copy succeeded — a transiently locked clipboard reports false, never throws.</summary>
    public static async Task<bool> TryCopyTextAsync(Visual owner, string text)
    {
        if (OperatingSystem.IsWindows())
        {
            return TryCopyTextWindows(text);
        }

        if (OperatingSystem.IsMacOS())
        {
            try
            {
                MacPasteboard.SetText(text);
                return true;
            }
            catch (Exception)
            {
                // Fall through to the Avalonia clipboard below.
            }
        }

        try
        {
            var clipboard = TopLevel.GetTopLevel(owner)?.Clipboard;
            if (clipboard is null)
            {
                return false;
            }
            await clipboard.SetTextAsync(text);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static async Task CopyPngViaAvaloniaAsync(Visual owner, byte[] pngBytes)
    {
        var clipboard = (TopLevel.GetTopLevel(owner)
            ?? throw new InvalidOperationException("No TopLevel available for clipboard access.")).Clipboard
            ?? throw new InvalidOperationException("No clipboard available on this platform.");

        // Avalonia 12's DataTransfer API (IDataObject/DataObject are inert since 12). The raw
        // platform format identifier is a mime type everywhere except macOS, which uses UTIs
        // (per DataFormat.CreateBytesPlatformFormat's own contract) — macOS only reaches this
        // path as the objc-P/Invoke fallback.
        string identifier = OperatingSystem.IsMacOS() ? "public.png" : "image/png";
        var dataTransfer = new DataTransfer();
        dataTransfer.Add(DataTransferItem.Create(DataFormat.CreateBytesPlatformFormat(identifier), pngBytes));
        await clipboard.SetDataAsync(dataTransfer);
    }

    // ================================ Windows (P/Invoke) ================================
    // Ported verbatim from the frozen WPF app's src/RoeSnip/Imaging/ClipboardService.cs +
    // the clipboard section of src/RoeSnip/Interop/NativeMethods.cs. Declarations compile on
    // every host OS; calls are Windows-only at runtime.

    private const uint BI_BITFIELDS = 3;
    private const uint LCS_sRGB = 0x73524742; // 'sRGB' FourCC, per wingdi.h
    private const uint LCS_GM_IMAGES = 4;
    private const uint GMEM_MOVEABLE = 0x0002;
    private const uint CF_UNICODETEXT = 13;
    private const uint CF_DIBV5 = 17;

    [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(IntPtr hWndNewOwner);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool CloseClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern bool EmptyClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterClipboardFormat(string lpszFormat);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalLock(IntPtr hMem);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalUnlock(IntPtr hMem);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalFree(IntPtr hMem);

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPV5HEADER
    {
        public uint bV5Size;
        public int bV5Width;
        public int bV5Height;
        public ushort bV5Planes;
        public ushort bV5BitCount;
        public uint bV5Compression;
        public uint bV5SizeImage;
        public int bV5XPelsPerMeter;
        public int bV5YPelsPerMeter;
        public uint bV5ClrUsed;
        public uint bV5ClrImportant;
        public uint bV5RedMask;
        public uint bV5GreenMask;
        public uint bV5BlueMask;
        public uint bV5AlphaMask;
        public uint bV5CSType;
        // CIEXYZTRIPLE bV5Endpoints: 3 x CIEXYZ, each 3 x FXPT2DOT30(=int) = 36 bytes. Unused when
        // bV5CSType == LCS_sRGB (consumers ignore these per the sRGB profile) — zero-filled.
        public int bV5Endpoint0X, bV5Endpoint0Y, bV5Endpoint0Z;
        public int bV5Endpoint1X, bV5Endpoint1Y, bV5Endpoint1Z;
        public int bV5Endpoint2X, bV5Endpoint2Y, bV5Endpoint2Z;
        public uint bV5GammaRed;
        public uint bV5GammaGreen;
        public uint bV5GammaBlue;
        public uint bV5Intent;
        public uint bV5ProfileData;
        public uint bV5ProfileSize;
        public uint bV5Reserved;
    }

    private static void CopyImageWindows(SdrImage image, byte[] pngBytes)
    {
        byte[] dibBytes = BuildDibV5(image);

        if (!OpenClipboard(IntPtr.Zero))
        {
            throw new InvalidOperationException("Failed to open the clipboard (it may be locked by another process).");
        }

        try
        {
            if (!EmptyClipboard())
            {
                throw new InvalidOperationException("Failed to empty the clipboard.");
            }

            uint pngFormat = RegisterClipboardFormat("PNG");
            SetGlobalClipboardData(pngFormat, pngBytes);
            SetGlobalClipboardData(CF_DIBV5, dibBytes);
        }
        finally
        {
            CloseClipboard();
        }
    }

    // O6 audit fix: WPF's own System.Windows.Clipboard.SetText retries OpenClipboard internally
    // (another process — including this app's own magnifier/color-inspector on a rapid second
    // click — can transiently hold the clipboard open); the raw P/Invoke path here had no such
    // resilience and failed outright on the first contended attempt.
    private const int OpenClipboardRetryCount = 10;
    private const int OpenClipboardRetryDelayMs = 100;

    private static bool TryOpenClipboardWithRetry()
    {
        for (int attempt = 0; attempt < OpenClipboardRetryCount; attempt++)
        {
            if (OpenClipboard(IntPtr.Zero))
            {
                return true;
            }
            if (attempt < OpenClipboardRetryCount - 1)
            {
                Thread.Sleep(OpenClipboardRetryDelayMs);
            }
        }
        return false;
    }

    private static bool TryCopyTextWindows(string text)
    {
        byte[] bytes = System.Text.Encoding.Unicode.GetBytes(text + "\0");
        try
        {
            if (!TryOpenClipboardWithRetry())
            {
                return false;
            }
            try
            {
                if (!EmptyClipboard())
                {
                    return false;
                }
                SetGlobalClipboardData(CF_UNICODETEXT, bytes);
                return true;
            }
            finally
            {
                CloseClipboard();
            }
        }
        catch (Exception)
        {
            // Clipboard transiently locked by another process — the caller surfaces the failure;
            // text copy is a convenience, never worth crashing the overlay over.
            return false;
        }
    }

    private static void SetGlobalClipboardData(uint format, byte[] bytes)
    {
        IntPtr hGlobal = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)(uint)bytes.Length);
        if (hGlobal == IntPtr.Zero)
        {
            throw new OutOfMemoryException("GlobalAlloc failed while preparing clipboard data.");
        }

        IntPtr locked = GlobalLock(hGlobal);
        if (locked == IntPtr.Zero)
        {
            GlobalFree(hGlobal);
            throw new InvalidOperationException("GlobalLock failed while preparing clipboard data.");
        }

        try
        {
            Marshal.Copy(bytes, 0, locked, bytes.Length);
        }
        finally
        {
            GlobalUnlock(hGlobal);
        }

        // On success, ownership of hGlobal transfers to the system (SetClipboardData's documented
        // contract) — we must NOT free it in that case, only on failure.
        if (SetClipboardData(format, hGlobal) == IntPtr.Zero)
        {
            GlobalFree(hGlobal);
            throw new InvalidOperationException($"SetClipboardData failed for clipboard format {format}.");
        }
    }

    private static byte[] BuildDibV5(SdrImage image)
    {
        int width = image.Width;
        int height = image.Height;
        int stride = width * 4;
        int pixelBytes = stride * height;
        int headerSize = Marshal.SizeOf<BITMAPV5HEADER>();

        var header = new BITMAPV5HEADER
        {
            bV5Size = (uint)headerSize,
            bV5Width = width,
            bV5Height = height, // positive => bottom-up DIB, the conventional/most-compatible layout
            bV5Planes = 1,
            bV5BitCount = 32,
            bV5Compression = BI_BITFIELDS,
            bV5SizeImage = (uint)pixelBytes,
            bV5RedMask = 0x00FF0000,
            bV5GreenMask = 0x0000FF00,
            bV5BlueMask = 0x000000FF,
            bV5AlphaMask = 0xFF000000,
            bV5CSType = LCS_sRGB,
            bV5Intent = LCS_GM_IMAGES,
        };

        var buffer = new byte[headerSize + pixelBytes];

        IntPtr headerPtr = Marshal.AllocHGlobal(headerSize);
        try
        {
            Marshal.StructureToPtr(header, headerPtr, false);
            Marshal.Copy(headerPtr, buffer, 0, headerSize);
        }
        finally
        {
            Marshal.FreeHGlobal(headerPtr);
        }

        // SdrImage is top-down (row 0 = top); a positive-height DIB is bottom-up, so rows are
        // written back-to-front.
        var pixels = image.Pixels;
        for (int y = 0; y < height; y++)
        {
            int srcOffset = y * image.Stride;
            int dstRow = height - 1 - y;
            int dstOffset = headerSize + dstRow * stride;
            Buffer.BlockCopy(pixels, srcOffset, buffer, dstOffset, stride);
        }

        return buffer;
    }

    // ================================ macOS (objc runtime) ================================
    // Compiles on every host OS; only ever called at runtime on macOS. Cannot be runtime-verified
    // on this machine (PLAN-XPLAT.md §4 step 6 — macOS is compile-only this cycle).

    private static class MacPasteboard
    {
        private const string LibObjC = "/usr/lib/libobjc.A.dylib";

        [DllImport(LibObjC)] private static extern IntPtr objc_getClass(string name);
        [DllImport(LibObjC)] private static extern IntPtr sel_registerName(string name);
        [DllImport(LibObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr MsgSend(IntPtr receiver, IntPtr selector);
        [DllImport(LibObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr MsgSend(IntPtr receiver, IntPtr selector, IntPtr arg1);
        [DllImport(LibObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr MsgSend(IntPtr receiver, IntPtr selector, IntPtr arg1, IntPtr arg2);
        [DllImport(LibObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr MsgSendBytes(IntPtr receiver, IntPtr selector, IntPtr bytes, nuint length);

        public static void SetPng(byte[] pngBytes)
        {
            IntPtr data = MakeNSData(pngBytes);
            IntPtr type = MakeNSString("public.png"); // NSPasteboardTypePNG's UTI value
            SetData(data, type);
        }

        public static void SetText(string text)
        {
            byte[] utf8 = System.Text.Encoding.UTF8.GetBytes(text);
            IntPtr data = MakeNSData(utf8);
            IntPtr type = MakeNSString("public.utf8-plain-text"); // NSPasteboardTypeString's UTI value
            SetData(data, type);
        }

        private static void SetData(IntPtr nsData, IntPtr nsType)
        {
            IntPtr pasteboardClass = objc_getClass("NSPasteboard");
            if (pasteboardClass == IntPtr.Zero)
            {
                throw new InvalidOperationException("NSPasteboard class not found (AppKit not loaded).");
            }
            IntPtr pasteboard = MsgSend(pasteboardClass, sel_registerName("generalPasteboard"));
            if (pasteboard == IntPtr.Zero)
            {
                throw new InvalidOperationException("NSPasteboard.generalPasteboard returned nil.");
            }
            _ = MsgSend(pasteboard, sel_registerName("clearContents"));
            IntPtr ok = MsgSend(pasteboard, sel_registerName("setData:forType:"), nsData, nsType);
            if (ok == IntPtr.Zero)
            {
                throw new InvalidOperationException("NSPasteboard setData:forType: returned NO.");
            }
        }

        private static unsafe IntPtr MakeNSData(byte[] bytes)
        {
            IntPtr dataClass = objc_getClass("NSData");
            if (dataClass == IntPtr.Zero)
            {
                throw new InvalidOperationException("NSData class not found.");
            }
            IntPtr data;
            fixed (byte* p = bytes)
            {
                // dataWithBytes:length: copies the buffer, so the fixed scope is sufficient.
                data = MsgSendBytes(dataClass, sel_registerName("dataWithBytes:length:"), (IntPtr)p, (nuint)bytes.Length);
            }
            if (data == IntPtr.Zero)
            {
                throw new InvalidOperationException("NSData dataWithBytes:length: returned nil.");
            }
            return data;
        }

        private static IntPtr MakeNSString(string value)
        {
            IntPtr stringClass = objc_getClass("NSString");
            if (stringClass == IntPtr.Zero)
            {
                throw new InvalidOperationException("NSString class not found.");
            }
            IntPtr utf8 = Marshal.StringToCoTaskMemUTF8(value);
            try
            {
                IntPtr ns = MsgSend(stringClass, sel_registerName("stringWithUTF8String:"), utf8);
                if (ns == IntPtr.Zero)
                {
                    throw new InvalidOperationException("NSString stringWithUTF8String: returned nil.");
                }
                return ns;
            }
            finally
            {
                Marshal.FreeCoTaskMem(utf8);
            }
        }
    }
}
