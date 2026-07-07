using System;
using System.Runtime.InteropServices;
using RoeSnip.Interop;

namespace RoeSnip.Imaging;

/// <summary>Writes both a PNG (the well-known registered "PNG" clipboard format, recognized by
/// browsers, Discord, Paint.NET) and a CF_DIBV5 (BITMAPV5HEADER, 32bpp BGRA with an explicit
/// alpha mask, recognized by Word/Paint/most legacy GDI consumers) to the clipboard in one
/// OpenClipboard/EmptyClipboard/SetClipboardData(x2)/CloseClipboard transaction, per PLAN.md
/// §3.2 — so both "paste as PNG" and "paste as bitmap" consumers work from a single copy.</summary>
public static class ClipboardService
{
    private const uint BI_BITFIELDS = 3;
    private const uint LCS_sRGB = 0x73524742; // 'sRGB' FourCC, per wingdi.h
    private const uint LCS_GM_IMAGES = 4;

    // Not in Interop/NativeMethods.cs (WP-A's §5 P/Invoke list) — added locally per PLAN.md §3.1's
    // "may add its own P/Invoke declaration inside its own file" allowance, used only to release a
    // GlobalAlloc block on the (rare) error paths below where ownership never transferred to the OS.
    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalFree(IntPtr hMem);

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

    public static void CopyToClipboard(SdrImage image)
    {
        byte[] pngBytes = PngWriter.Encode(image);
        byte[] dibBytes = BuildDibV5(image);

        if (!NativeMethods.OpenClipboard(IntPtr.Zero))
        {
            throw new InvalidOperationException("Failed to open the clipboard (it may be locked by another process).");
        }

        try
        {
            if (!NativeMethods.EmptyClipboard())
            {
                throw new InvalidOperationException("Failed to empty the clipboard.");
            }

            uint pngFormat = NativeMethods.RegisterClipboardFormat("PNG");
            SetGlobalClipboardData(pngFormat, pngBytes);
            SetGlobalClipboardData(NativeMethods.CF_DIBV5, dibBytes);
        }
        finally
        {
            NativeMethods.CloseClipboard();
        }
    }

    private static void SetGlobalClipboardData(uint format, byte[] bytes)
    {
        IntPtr hGlobal = NativeMethods.GlobalAlloc(NativeMethods.GMEM_MOVEABLE, (UIntPtr)(uint)bytes.Length);
        if (hGlobal == IntPtr.Zero)
        {
            throw new OutOfMemoryException("GlobalAlloc failed while preparing clipboard data.");
        }

        IntPtr locked = NativeMethods.GlobalLock(hGlobal);
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
            NativeMethods.GlobalUnlock(hGlobal);
        }

        // On success, ownership of hGlobal transfers to the system (SetClipboardData's documented
        // contract) — we must NOT free it in that case, only on failure.
        if (NativeMethods.SetClipboardData(format, hGlobal) == IntPtr.Zero)
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
}
