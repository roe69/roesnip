using System;
using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace RoeSnip.Capture;

/// <summary>Primary capture path per DESIGN.md: IDXGIOutput5.DuplicateOutput1, requesting both
/// FP16 scRGB and BGRA8 sRGB as supported formats and branching on whichever format is actually
/// delivered (DXGI_OUTDUPL_DESC.ModeDesc.Format) — never on AdvancedColorActive.</summary>
public sealed class DesktopDuplicationCapturer : IScreenCapturer
{
    private const int MaxAcquireAttempts = 5;
    private const int AcquireRetryDelayMs = 100;

    public CapturedFrame Capture(MonitorInfo monitor) => CaptureCore(monitor, allowAccessLostRetry: true);

    private static CapturedFrame CaptureCore(MonitorInfo monitor, bool allowAccessLostRetry)
    {
        IDXGIOutput? output = null;
        ID3D11Device? device = null;
        IDXGIOutputDuplication? duplication = null;
        IDXGIResource? desktopResource = null;
        ID3D11Texture2D? acquiredTexture = null;
        ID3D11Texture2D? staging = null;
        bool frameAcquired = false;

        try
        {
            (output, device) = FindOutputAndCreateDevice(monitor);

            using var output5 = output.QueryInterface<IDXGIOutput5>();
            Format[] supportedFormats = { Format.R16G16B16A16_Float, Format.B8G8R8A8_UNorm };
            duplication = output5.DuplicateOutput1(device, supportedFormats);

            Result acquireResult = default;
            for (int attempt = 0; attempt < MaxAcquireAttempts; attempt++)
            {
                acquireResult = duplication.AcquireNextFrame(
                    (uint)AcquireRetryDelayMs, out OutduplFrameInfo frameInfo, out desktopResource);

                if (acquireResult.Success)
                {
                    frameAcquired = true;
                    break;
                }

                if (acquireResult == Vortice.DXGI.ResultCode.WaitTimeout)
                {
                    continue; // static screen — retry, per DESIGN.md's retry loop.
                }

                if (acquireResult == Vortice.DXGI.ResultCode.AccessLost)
                {
                    throw new AccessLostSignal();
                }

                throw new CaptureException(
                    $"AcquireNextFrame failed for monitor {monitor.DeviceName}: " +
                    $"0x{acquireResult.Code:X8} ({acquireResult.Description})");
            }

            if (!frameAcquired)
            {
                throw new CaptureException(
                    $"AcquireNextFrame timed out after {MaxAcquireAttempts} attempts for monitor {monitor.DeviceName}.");
            }

            // Branch on the ACTUAL delivered format, not AdvancedColorActive.
            var outduplDesc = duplication.Description;
            FrameFormat format = outduplDesc.ModeDescription.Format == Format.R16G16B16A16_Float
                ? FrameFormat.Fp16ScRgb
                : FrameFormat.Bgra8Srgb;

            acquiredTexture = desktopResource!.QueryInterface<ID3D11Texture2D>();
            var srcDesc = acquiredTexture.Description;

            var stagingDesc = new Texture2DDescription
            {
                Width = srcDesc.Width,
                Height = srcDesc.Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = srcDesc.Format,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CPUAccessFlags = CpuAccessFlags.Read,
                MiscFlags = ResourceOptionFlags.None,
            };
            staging = device.CreateTexture2D(stagingDesc);

            // Copy BEFORE ReleaseFrame, per DESIGN.md.
            device.ImmediateContext.CopyResource(staging, acquiredTexture);
            duplication.ReleaseFrame();
            frameAcquired = false; // frame handed back to the duplication object; no longer ours to release again.

            var mapped = device.ImmediateContext.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            byte[] pixels;
            try
            {
                int stride = (int)mapped.RowPitch;
                pixels = new byte[stride * (int)srcDesc.Height];
                Marshal.Copy(mapped.DataPointer, pixels, 0, pixels.Length);
            }
            finally
            {
                device.ImmediateContext.Unmap(staging, 0);
            }

            return new CapturedFrame(format, (int)srcDesc.Width, (int)srcDesc.Height, (int)mapped.RowPitch, pixels, monitor);
        }
        catch (AccessLostSignal) when (allowAccessLostRetry)
        {
            DisposeAll(staging, acquiredTexture, desktopResource, duplication, device, output);
            return CaptureCore(monitor, allowAccessLostRetry: false);
        }
        catch (AccessLostSignal ex)
        {
            throw new CaptureException(
                $"Desktop Duplication access lost for monitor {monitor.DeviceName}, and the retry also failed.", ex);
        }
        catch (CaptureException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new CaptureException(
                $"Desktop Duplication capture failed for monitor {monitor.DeviceName}: {ex.Message}", ex);
        }
        finally
        {
            if (frameAcquired)
            {
                try { duplication?.ReleaseFrame(); } catch { /* best-effort */ }
            }
            DisposeAll(staging, acquiredTexture, desktopResource, duplication, device, output);
        }
    }

    private static void DisposeAll(params IDisposable?[] items)
    {
        foreach (var item in items) item?.Dispose();
    }

    /// <summary>Finds the DXGI output matching <paramref name="monitor"/>.HMonitor and creates the
    /// D3D11 device on the adapter that owns it — never assume the default adapter (hybrid-GPU
    /// laptops).</summary>
    private static (IDXGIOutput output, ID3D11Device device) FindOutputAndCreateDevice(MonitorInfo monitor)
    {
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        for (uint a = 0; ; a++)
        {
            var adapterResult = factory.EnumAdapters1(a, out IDXGIAdapter1? adapter);
            if (!adapterResult.Success || adapter is null) break;

            bool adapterOwnsOutput = false;
            try
            {
                for (uint o = 0; ; o++)
                {
                    var outputResult = adapter.EnumOutputs(o, out IDXGIOutput? output);
                    if (!outputResult.Success || output is null) break;

                    if (output.Description.Monitor == monitor.HMonitor)
                    {
                        FeatureLevel[] levels =
                        {
                            FeatureLevel.Level_11_1, FeatureLevel.Level_11_0,
                            FeatureLevel.Level_10_1, FeatureLevel.Level_10_0,
                        };
                        D3D11.D3D11CreateDevice(
                            adapter, DriverType.Unknown, DeviceCreationFlags.None, levels,
                            out ID3D11Device? device).CheckError();
                        adapterOwnsOutput = true;
                        return (output, device!);
                    }

                    output.Dispose();
                }
            }
            finally
            {
                if (!adapterOwnsOutput) adapter.Dispose();
            }
        }

        throw new CaptureException(
            $"No DXGI output found for monitor {monitor.DeviceName} (HMONITOR 0x{monitor.HMonitor:X}).");
    }

    /// <summary>Internal signal used to unwind to the single retry-once policy for
    /// DXGI_ERROR_ACCESS_LOST; never escapes this class as a CaptureException subtype.</summary>
    private sealed class AccessLostSignal : Exception { }
}
