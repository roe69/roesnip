using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using RoeSnip.Core.Capture;
using RoeSnip.Platform.Windows.Interop;
using Vortice.DXGI;

namespace RoeSnip.Platform.Windows;

/// <summary>Enumerates all active monitors. Ported from src/RoeSnip/Capture/MonitorInfo.cs's
/// MonitorEnumerator (PLAN.md §3.1's numbered steps: EnumDisplayMonitors + GetDpiForMonitor, DXGI
/// IDXGIOutput6.GetDesc1(), QueryDisplayConfig/DisplayConfigGetDeviceInfo SDR white level) with only
/// the output shape changed per PLAN-XPLAT.md §3.2: the portable Core MonitorInfo, with
/// <c>BackendKey = "0x" + hMonitor.ToString("X")</c> and <c>Scale = dpiX / 96.0</c>. Never throws
/// for a single bad monitor entry — logs to stderr and omits it. Returns empty list only if
/// enumeration itself fails entirely.</summary>
public static class MonitorEnumerator
{
    /// <summary>Recovers the HMONITOR that <see cref="Enumerate"/> encoded into
    /// <see cref="MonitorInfo.BackendKey"/> ("0x{hex}"). Only valid for MonitorInfo values produced
    /// by this backend — BackendKey is opaque across backends (PLAN-XPLAT.md §2.1).</summary>
    public static nint ParseHMonitor(MonitorInfo monitor)
    {
        string key = monitor.BackendKey;
        if (key.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            key = key[2..];
        }
        return (nint)long.Parse(key, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }

    private sealed class RawMonitor
    {
        public nint HMonitor;
        public NativeMethods.RECT Rect;
        public string DeviceName = string.Empty;
        public bool IsPrimary;
        public int DpiX = 96;
        public int DpiY = 96;
    }

    public static IReadOnlyList<MonitorInfo> Enumerate()
    {
        List<RawMonitor> raw;
        try
        {
            raw = EnumerateRawMonitors();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"RoeSnip: monitor enumeration failed entirely: {ex.Message}");
            return Array.Empty<MonitorInfo>();
        }

        var dxgiInfo = EnumerateDxgiOutputInfo();
        var sdrWhiteInfo = EnumerateSdrWhiteLevels();

        var result = new List<MonitorInfo>(raw.Count);
        for (int i = 0; i < raw.Count; i++)
        {
            var r = raw[i];
            bool advancedColor = false;
            double maxLuminance = 1000.0;
            if (dxgiInfo.TryGetValue(r.DeviceName, out var dxgi))
            {
                advancedColor = dxgi.AdvancedColor;
                maxLuminance = dxgi.MaxLuminanceNits;
            }

            double sdrWhiteNits = sdrWhiteInfo.TryGetValue(r.DeviceName, out var nits) ? nits : 240.0;

            result.Add(new MonitorInfo(
                Index: i,
                DeviceName: r.DeviceName,
                BackendKey: "0x" + r.HMonitor.ToString("X"),
                BoundsPx: new RectPhysical(r.Rect.Left, r.Rect.Top, r.Rect.Right, r.Rect.Bottom),
                DpiX: r.DpiX,
                DpiY: r.DpiY,
                Scale: r.DpiX / 96.0,
                AdvancedColorActive: advancedColor,
                SdrWhiteNits: sdrWhiteNits,
                MaxLuminanceNits: maxLuminance,
                IsPrimary: r.IsPrimary));
        }

        return result;
    }

    private static List<RawMonitor> EnumerateRawMonitors()
    {
        var raw = new List<RawMonitor>();

        bool Callback(nint hMonitor, nint hdc, ref NativeMethods.RECT rect, nint data)
        {
            try
            {
                var mi = new NativeMethods.MONITORINFOEX
                {
                    cbSize = (uint)Marshal.SizeOf<NativeMethods.MONITORINFOEX>()
                };
                if (!NativeMethods.GetMonitorInfo(hMonitor, ref mi))
                {
                    Console.Error.WriteLine($"RoeSnip: GetMonitorInfo failed for HMONITOR {hMonitor}.");
                    return true;
                }

                uint dpiX = 96, dpiY = 96;
                try
                {
                    int hr = NativeMethods.GetDpiForMonitor(hMonitor, NativeMethods.MONITOR_DPI_TYPE.MDT_EFFECTIVE_DPI, out dpiX, out dpiY);
                    if (hr != 0) { dpiX = 96; dpiY = 96; }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"RoeSnip: GetDpiForMonitor failed for {mi.szDevice}: {ex.Message}");
                }

                raw.Add(new RawMonitor
                {
                    HMonitor = hMonitor,
                    Rect = mi.rcMonitor,
                    DeviceName = mi.szDevice,
                    IsPrimary = (mi.dwFlags & NativeMethods.MONITORINFOF_PRIMARY) != 0,
                    DpiX = (int)dpiX,
                    DpiY = (int)dpiY,
                });
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"RoeSnip: monitor enum callback failed: {ex.Message}");
            }
            return true;
        }

        // Keep the delegate alive for the duration of the unmanaged call.
        NativeMethods.MonitorEnumProc proc = Callback;
        if (!NativeMethods.EnumDisplayMonitors(0, 0, proc, 0))
        {
            Console.Error.WriteLine("RoeSnip: EnumDisplayMonitors reported failure.");
        }
        GC.KeepAlive(proc);
        return raw;
    }

    private readonly record struct DxgiOutputInfo(bool AdvancedColor, double MaxLuminanceNits);

    private static Dictionary<string, DxgiOutputInfo> EnumerateDxgiOutputInfo()
    {
        var result = new Dictionary<string, DxgiOutputInfo>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            for (uint a = 0; ; a++)
            {
                var adapterResult = factory.EnumAdapters1(a, out IDXGIAdapter1? adapter);
                if (!adapterResult.Success || adapter is null) break;
                using (adapter)
                {
                    for (uint o = 0; ; o++)
                    {
                        var outputResult = adapter.EnumOutputs(o, out IDXGIOutput? output);
                        if (!outputResult.Success || output is null) break;
                        using (output)
                        {
                            try
                            {
                                using var output6 = output.QueryInterfaceOrNull<IDXGIOutput6>();
                                if (output6 is null) continue;
                                var desc = output6.Description1;
                                bool advancedColor = desc.ColorSpace == ColorSpaceType.RgbFullG2084NoneP2020;
                                double maxLum = desc.MaxLuminance;
                                // Matches the MonitorInfo.MaxLuminanceNits doc comment: "<10 or
                                // >10000" is absurd, not just "<=0" (a monitor reporting e.g. 3 nits
                                // of max luminance is exactly as bogus as 0).
                                if (!double.IsFinite(maxLum) || maxLum < 10 || maxLum > 10000) maxLum = 1000.0;
                                result[desc.DeviceName] = new DxgiOutputInfo(advancedColor, maxLum);
                            }
                            catch (Exception ex)
                            {
                                Console.Error.WriteLine($"RoeSnip: DXGI output query failed: {ex.Message}");
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"RoeSnip: DXGI adapter enumeration failed: {ex.Message}");
        }
        return result;
    }

    private static Dictionary<string, double> EnumerateSdrWhiteLevels()
    {
        var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        try
        {
            uint pathCount = 0, modeCount = 0;
            int sizesHr = NativeMethods.GetDisplayConfigBufferSizes(NativeMethods.QDC_ONLY_ACTIVE_PATHS, out pathCount, out modeCount);
            if (sizesHr != 0)
            {
                Console.Error.WriteLine($"RoeSnip: GetDisplayConfigBufferSizes failed (0x{sizesHr:X8}); SDR white level will default to 240 nits.");
                return result;
            }

            var paths = new NativeMethods.DISPLAYCONFIG_PATH_INFO[pathCount];
            var modes = new NativeMethods.DISPLAYCONFIG_MODE_INFO[modeCount];
            int queryHr = NativeMethods.QueryDisplayConfig(
                NativeMethods.QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
            if (queryHr != 0)
            {
                Console.Error.WriteLine($"RoeSnip: QueryDisplayConfig failed (0x{queryHr:X8}); SDR white level will default to 240 nits.");
                return result;
            }

            for (int i = 0; i < pathCount; i++)
            {
                try
                {
                    var sourceNameRequest = new NativeMethods.DISPLAYCONFIG_SOURCE_DEVICE_NAME
                    {
                        header = new NativeMethods.DISPLAYCONFIG_DEVICE_INFO_HEADER
                        {
                            type = NativeMethods.DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME,
                            size = (uint)Marshal.SizeOf<NativeMethods.DISPLAYCONFIG_SOURCE_DEVICE_NAME>(),
                            adapterId = paths[i].sourceInfo.adapterId,
                            id = paths[i].sourceInfo.id,
                        }
                    };
                    int sourceHr = NativeMethods.DisplayConfigGetDeviceInfoSourceName(ref sourceNameRequest);
                    if (sourceHr != 0) continue;

                    var sdrRequest = new NativeMethods.DISPLAYCONFIG_SDR_WHITE_LEVEL
                    {
                        header = new NativeMethods.DISPLAYCONFIG_DEVICE_INFO_HEADER
                        {
                            type = NativeMethods.DISPLAYCONFIG_DEVICE_INFO_GET_SDR_WHITE_LEVEL,
                            size = (uint)Marshal.SizeOf<NativeMethods.DISPLAYCONFIG_SDR_WHITE_LEVEL>(),
                            adapterId = paths[i].sourceInfo.adapterId,
                            id = paths[i].sourceInfo.id,
                        }
                    };
                    int sdrHr = NativeMethods.DisplayConfigGetDeviceInfoSdrWhiteLevel(ref sdrRequest);
                    if (sdrHr != 0) continue;

                    double nits = sdrRequest.SDRWhiteLevel / 1000.0 * 80.0;
                    // Sanity floor: a query that "succeeds" but yields a near-zero or non-finite
                    // value would make ToneMapper's scale blow up to (near-)Infinity across the
                    // whole frame. Fall back to the documented 240 nits default in that case, same
                    // as an outright query failure.
                    if (!double.IsFinite(nits) || nits < 10.0) nits = 240.0;
                    result[sourceNameRequest.viewGdiDeviceName] = nits;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"RoeSnip: SDR white level query failed for path {i}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"RoeSnip: SDR white level enumeration failed: {ex.Message}");
        }
        return result;
    }
}
