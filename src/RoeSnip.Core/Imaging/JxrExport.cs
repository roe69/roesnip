using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using RoeSnip.Core.Capture;
using RoeSnip.Core.Color;
using RoeSnip.Core.Diagnostics;

namespace RoeSnip.Core.Imaging;

/// <summary>The untouched HDR original as JPEG XR on Linux and macOS (Windows writes the same file
/// through WIC, <c>Platform.Windows/JxrWriter</c>): 128bppRGBAFloat, linear scRGB (1.0 = 80 nits),
/// alpha 1.0, raw crop, no annotations, headroom above 1.0 kept. Writes a float TIFF (streamed row
/// by row) to a temp file and runs jxrlib's JxrEncApp on it with <c>-c 28 -q 1.0</c> (lossless
/// 128bppRGBAFloat). The encoder is bundled next to the app (<c>JxrEncApp</c>, built from jxrlib by
/// release.yml); <c>ROESNIP_JXRENC_PATH</c> overrides.
///
/// Values are true scRGB: Fp16 frames are rescaled by <c>(SdrWhiteNits/80) / SdrWhiteInBufferUnits</c>
/// (identity for Windows and <see cref="PqBt2020"/> frames; a macOS EDR frame has 1.0 = SdrWhiteNits); Bgra8 frames decode through the sRGB EOTF with white = 1.0 (80 nits).</summary>
public static class JxrExport
{
    private const int EncoderTimeoutMs = 60_000;

    /// <summary>The encoder's path, or null when it is not installed next to the app.</summary>
    public static string? LocateEncoder()
    {
        string? overridePath = Environment.GetEnvironmentVariable("ROESNIP_JXRENC_PATH");
        if (!string.IsNullOrEmpty(overridePath)) return File.Exists(overridePath) ? overridePath : null;
        string path = Path.Combine(AppContext.BaseDirectory, "JxrEncApp");
        return File.Exists(path) ? path : null;
    }

    public static bool IsAvailable => LocateEncoder() is not null;

    /// <summary>Writes <paramref name="frame"/> cropped to <paramref name="cropPx"/> as a .jxr.</summary>
    public static void Write(string path, CapturedFrame frame, RectPhysical cropPx)
    {
        var r = cropPx.Normalized();
        if (r.Left < 0 || r.Top < 0 || r.Right > frame.Width || r.Bottom > frame.Height)
        {
            throw new ArgumentOutOfRangeException(
                nameof(cropPx), $"Crop rect {r} is out of bounds for a {frame.Width}x{frame.Height} frame.");
        }
        Encode(path, r.Width, r.Height, (y, row) => FillRow(frame, r.Left, r.Top + y, r.Width, row));
    }

    /// <summary>Cross-monitor selection: every contributing crop stitched into one canvas, gaps
    /// opaque black (the same fill as <c>Platform.Windows/JxrWriter.WriteSpanning</c>).</summary>
    public static void WriteSpanning(string path, RectPhysical virtualRect, IReadOnlyList<SpanningFrameCrop> crops)
    {
        var n = virtualRect.Normalized();
        int width = Math.Max(1, n.Width), height = Math.Max(1, n.Height);
        foreach (var crop in crops)
        {
            var c = crop.LocalCropPx.Normalized();
            if (c.Left < 0 || c.Top < 0 || c.Right > crop.Frame.Width || c.Bottom > crop.Frame.Height)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(crops), $"Crop rect {c} is out of bounds for a {crop.Frame.Width}x{crop.Frame.Height} frame.");
            }
        }
        Encode(path, width, height, (y, row) =>
        {
            for (int i = 0; i < width; i++)
            {
                row[i * 4] = row[i * 4 + 1] = row[i * 4 + 2] = 0f;
                row[i * 4 + 3] = 1f;
            }
            foreach (var crop in crops)
            {
                var c = crop.LocalCropPx.Normalized();
                int local = y - crop.DestY;
                if (local < 0 || local >= c.Height) continue;
                int destX = Math.Max(0, crop.DestX);
                int skip = destX - crop.DestX;
                int count = Math.Min(c.Width - skip, width - destX);
                if (count <= 0) continue;
                FillRow(crop.Frame, c.Left + skip, c.Top + local, count, row.Slice(destX * 4));
            }
        });
    }

    /// <summary>Fills <paramref name="row"/> (R,G,B,A floats per pixel) with scRGB pixels
    /// (<paramref name="x0"/>.., <paramref name="y"/>) of the frame, alpha 1.</summary>
    internal static void FillRow(CapturedFrame frame, int x0, int y, int count, Span<float> row)
    {
        var src = frame.Row(y);
        if (frame.Format == FrameFormat.Fp16ScRgb)
        {
            var halves = MemoryMarshal.Cast<byte, Half>(src);
            float scale = (float)(frame.Monitor.SdrWhiteNits / PqBt2020.ScRgbNits / frame.SdrWhiteInBufferUnits);
            for (int i = 0; i < count; i++)
            {
                int o = (x0 + i) * 4;
                row[i * 4] = (float)halves[o] * scale;
                row[i * 4 + 1] = (float)halves[o + 1] * scale;
                row[i * 4 + 2] = (float)halves[o + 2] * scale;
                row[i * 4 + 3] = 1f;
            }
        }
        else
        {
            for (int i = 0; i < count; i++)
            {
                int o = (x0 + i) * 4; // B,G,R,A
                row[i * 4] = ColorMath.SrgbByteToLinear(src[o + 2]);
                row[i * 4 + 1] = ColorMath.SrgbByteToLinear(src[o + 1]);
                row[i * 4 + 2] = ColorMath.SrgbByteToLinear(src[o]);
                row[i * 4 + 3] = 1f;
            }
        }
    }

    private delegate void RowFiller(int y, Span<float> row);

    private static void Encode(string path, int width, int height, RowFiller fill)
    {
        string encoder = LocateEncoder()
            ?? throw new InvalidOperationException(
                "the JPEG XR encoder (JxrEncApp) is not installed next to RoeSnip (or ROESNIP_JXRENC_PATH is wrong).");

        string dir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { Length: > 0 } xdg && Directory.Exists(xdg)
            ? xdg : Path.GetTempPath();
        string tiff = Path.Combine(dir, $"roesnip-hdr-{Environment.ProcessId}-{Guid.NewGuid():N}.tif");
        var watch = Stopwatch.StartNew();
        try
        {
            using (var file = new FileStream(tiff, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20))
            {
                WriteTiffHeader(file, width, height);
                var row = new float[width * 4];
                var bytes = new byte[width * 16];
                for (int y = 0; y < height; y++)
                {
                    fill(y, row);
                    Buffer.BlockCopy(row, 0, bytes, 0, bytes.Length); // little-endian hosts only (x64, arm64)
                    file.Write(bytes, 0, bytes.Length);
                }
            }
            long tiffMs = watch.ElapsedMilliseconds;

            var psi = new ProcessStartInfo
            {
                FileName = encoder,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };
            foreach (var a in new[] { "-i", tiff, "-o", path, "-c", "28", "-q", "1.0" }) psi.ArgumentList.Add(a);
            using var process = Process.Start(psi) ?? throw new InvalidOperationException("JxrEncApp did not start.");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(EncoderTimeoutMs))
            {
                try { process.Kill(true); } catch { /* already gone */ }
                throw new InvalidOperationException("JxrEncApp timed out.");
            }
            if (process.ExitCode != 0 || !File.Exists(path) || new FileInfo(path).Length == 0)
            {
                string why = (stderr.GetAwaiter().GetResult() + stdout.GetAwaiter().GetResult()).Trim();
                throw new InvalidOperationException($"JxrEncApp failed (exit {process.ExitCode}): {why}");
            }
            FileLog.Write($"RoeSnip: wrote HDR copy {width}x{height} ({new FileInfo(path).Length / 1024} KB) in " +
                $"{watch.ElapsedMilliseconds} ms (tiff {tiffMs} ms)");
        }
        finally
        {
            try { File.Delete(tiff); } catch { /* temp file */ }
        }
    }

    /// <summary>Header + IFD of an uncompressed little-endian RGBA float32 TIFF with one strip;
    /// <paramref name="width"/> x <paramref name="height"/> x 16 bytes of pixels follow.</summary>
    internal static void WriteTiffHeader(Stream s, int width, int height)
    {
        const int entryCount = 12;
        int ifdOffset = 8;
        int bpsOffset = ifdOffset + 2 + entryCount * 12 + 4;
        int dataOffset = bpsOffset + 8;
        long dataBytes = (long)width * height * 16;
        if (dataBytes > uint.MaxValue) throw new InvalidOperationException("image too large for a classic TIFF");

        using var w = new BinaryWriter(s, System.Text.Encoding.ASCII, leaveOpen: true);
        w.Write((byte)'I'); w.Write((byte)'I'); w.Write((ushort)42); w.Write((uint)ifdOffset);
        w.Write((ushort)entryCount);
        void Long(ushort tag, uint v) { w.Write(tag); w.Write((ushort)4); w.Write(1u); w.Write(v); }
        void Short(ushort tag, ushort v) { w.Write(tag); w.Write((ushort)3); w.Write(1u); w.Write(v); w.Write((ushort)0); }
        Long(256, (uint)width);
        Long(257, (uint)height);
        w.Write((ushort)258); w.Write((ushort)3); w.Write(4u); w.Write((uint)bpsOffset); // 4 x 32 bits
        Short(259, 1);   // no compression
        Short(262, 2);   // RGB
        Long(273, (uint)dataOffset);
        Short(277, 4);   // samples per pixel
        Long(278, (uint)height);
        Long(279, (uint)dataBytes);
        Short(284, 1);   // chunky
        Short(338, 2);   // extra sample: unassociated alpha
        Short(339, 3);   // IEEE float
        w.Write(0u);     // no next IFD
        for (int i = 0; i < 4; i++) w.Write((ushort)32);
        w.Flush();
    }
}
