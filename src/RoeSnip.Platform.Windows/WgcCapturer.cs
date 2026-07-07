using System;
using System.Runtime.InteropServices;
using System.Threading;
using RoeSnip.Core.Capture;
using RoeSnip.Platform.Windows.Interop;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

namespace RoeSnip.Platform.Windows;

/// <summary>Fallback capture path per DESIGN.md: Windows.Graphics.Capture via
/// IGraphicsCaptureItemInterop.CreateForMonitor, using Direct3D11CaptureFramePool.CreateFreeThreaded
/// (NOT plain Create, which needs a DispatcherQueue and throws from CLI/console contexts). Covers
/// RDP and other Desktop-Duplication-denied contexts. The yellow capture border may appear
/// (unpackaged apps lack the graphicsCaptureWithoutBorder capability) — accepted per DESIGN.md,
/// since Desktop Duplication (the primary path) is borderless.
/// Ported from src/RoeSnip/Capture/WgcCapturer.cs (PLAN-XPLAT.md §3.2) with the same two mechanical
/// changes as DesktopDuplicationCapturer: HMONITOR via <see cref="MonitorEnumerator.ParseHMonitor"/>,
/// and the 7-arg CapturedFrame constructor (<c>sdrWhiteInBufferUnits: monitor.SdrWhiteNits / 80.0</c>).</summary>
public sealed class WgcCapturer : IScreenCapturer
{
    public CapturedFrame Capture(MonitorInfo monitor)
    {
        try
        {
            return CaptureCore(monitor);
        }
        catch (CaptureException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new CaptureException($"WGC capture failed for monitor {monitor.DeviceName}: {ex.Message}", ex);
        }
    }

    private static CapturedFrame CaptureCore(MonitorInfo monitor)
    {
        GraphicsCaptureItem item = CreateItemForMonitor(MonitorEnumerator.ParseHMonitor(monitor));

        ID3D11Device? d3dDevice = null;
        Direct3D11CaptureFramePool? framePool = null;
        GraphicsCaptureSession? session = null;
        IDirect3DDevice? winrtDevice = null;

        try
        {
            FeatureLevel[] levels =
            {
                FeatureLevel.Level_11_1, FeatureLevel.Level_11_0,
                FeatureLevel.Level_10_1, FeatureLevel.Level_10_0,
            };
            d3dDevice = D3D11.D3D11CreateDevice(DriverType.Hardware, DeviceCreationFlags.BgraSupport, levels);
            using var dxgiDevice = d3dDevice.QueryInterface<IDXGIDevice>();

            // Deliberately NOT using Vortice's generic CreateDirect3D11DeviceFromDXGIDevice<T> helper
            // here: it returns a classic .NET COM RCW (System.__ComObject), and CsWinRT's projection
            // marshaler cannot build a CCW for that object when it's later passed into
            // Direct3D11CaptureFramePool.CreateFreeThreaded ("Failed to create a CCW ... the
            // specified cast is not valid"). Call the native factory directly and wrap the raw ABI
            // pointer with WinRT.MarshalInterface<T>.FromAbi instead, exactly like CreateItemForMonitor
            // does below, so the resulting object is a proper CsWinRT-projected IDirect3DDevice.
            // FromAbi AddRefs internally (verified empirically: refcount 1 -> 3) and does NOT
            // consume the caller's reference, so we must Release the raw pointer we own after
            // wrapping — otherwise the device leaks once per capture.
            CreateDirect3D11DeviceFromDXGIDeviceNative(dxgiDevice.NativePointer, out IntPtr winrtDevicePtr);
            try
            {
                winrtDevice = WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(winrtDevicePtr);
            }
            finally
            {
                Marshal.Release(winrtDevicePtr);
            }

            // WGC doesn't expose a "delivered format" the way Desktop Duplication does — request
            // FP16 for advanced-color displays, BGRA8 otherwise (DESIGN.md's one legitimate use of
            // AdvancedColorActive for branching).
            DirectXPixelFormat pixelFormat = monitor.AdvancedColorActive
                ? DirectXPixelFormat.R16G16B16A16Float
                : DirectXPixelFormat.B8G8R8A8UIntNormalized;

            framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(winrtDevice, pixelFormat, 1, item.Size);
            session = framePool.CreateCaptureSession(item);
            session.IsCursorCaptureEnabled = false;
            try { session.IsBorderRequired = false; }
            catch { /* property may not exist on older Windows builds, or capability denied — accepted. */ }

            using var frameReady = new ManualResetEventSlim(false);
            Direct3D11CaptureFrame? capturedFrame = null;
            Exception? callbackError = null;

            void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
            {
                try
                {
                    capturedFrame = sender.TryGetNextFrame();
                }
                catch (Exception ex)
                {
                    callbackError = ex;
                }
                finally
                {
                    frameReady.Set();
                }
            }

            framePool.FrameArrived += OnFrameArrived;
            try
            {
                session.StartCapture();
                if (!frameReady.Wait(TimeSpan.FromSeconds(5)))
                {
                    throw new CaptureException($"WGC frame wait timed out for monitor {monitor.DeviceName}.");
                }
            }
            finally
            {
                framePool.FrameArrived -= OnFrameArrived;
            }

            if (callbackError is not null)
            {
                throw new CaptureException($"WGC FrameArrived callback failed for monitor {monitor.DeviceName}: {callbackError.Message}", callbackError);
            }
            if (capturedFrame is null)
            {
                throw new CaptureException($"WGC delivered no frame for monitor {monitor.DeviceName}.");
            }

            using (capturedFrame)
            {
                using var surfaceTexture = GetTextureForSurface(capturedFrame.Surface);
                var srcDesc = surfaceTexture.Description;

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
                using var staging = d3dDevice.CreateTexture2D(stagingDesc);
                d3dDevice.ImmediateContext.CopyResource(staging, surfaceTexture);

                var mapped = d3dDevice.ImmediateContext.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                byte[] pixels;
                try
                {
                    int stride = (int)mapped.RowPitch;
                    pixels = new byte[stride * (int)srcDesc.Height];
                    Marshal.Copy(mapped.DataPointer, pixels, 0, pixels.Length);
                }
                finally
                {
                    d3dDevice.ImmediateContext.Unmap(staging, 0);
                }

                FrameFormat format = srcDesc.Format == Format.R16G16B16A16_Float
                    ? FrameFormat.Fp16ScRgb
                    : FrameFormat.Bgra8Srgb;

                return new CapturedFrame(
                    format, (int)srcDesc.Width, (int)srcDesc.Height, (int)mapped.RowPitch, pixels, monitor,
                    sdrWhiteInBufferUnits: monitor.SdrWhiteNits / 80.0);
            }
        }
        finally
        {
            session?.Dispose();
            framePool?.Dispose();
            d3dDevice?.Dispose();

            // winrtDevice and item are both CsWinRT projections wrapping a native COM reference
            // that FromAbi added internally — without an explicit release here, that reference (and
            // driver-side memory behind it) only got freed whenever the GC finalizer pass eventually
            // ran, leaking 2 native refs per fallback capture until then (audit finding H). This is
            // purely additive: the existing Marshal.Release calls above are unrelated, already-
            // balanced refcounting for the raw ABI pointers and are left untouched.
            //
            // IDirect3DDevice (winrtDevice's static type) implements System.IDisposable directly.
            // GraphicsCaptureItem does not (verified against the projection metadata) — it only
            // implements WinRT.IWinRTObject, so its underlying native reference is released via its
            // NativeObject instead.
            winrtDevice?.Dispose();
            if (item is WinRT.IWinRTObject winrtItem)
            {
                winrtItem.NativeObject?.Dispose();
            }
        }
    }

    private static ID3D11Texture2D GetTextureForSurface(IDirect3DSurface surface)
    {
        // surface is a CsWinRT-projected object, not a classic COM RCW — a direct cast to our
        // ComImport interface fails ("Invalid cast from WinRT.IInspectable"). Query the interface
        // through CsWinRT's own IWinRTObject/IObjectReference machinery instead, then hand the
        // resulting raw pointer to classic COM interop for the actual GetInterface call.
        var winrtObj = (WinRT.IWinRTObject)surface;
        using var accessRef = winrtObj.NativeObject.As(typeof(IDirect3DDxgiInterfaceAccess).GUID);
        var access = (IDirect3DDxgiInterfaceAccess)Marshal.GetTypedObjectForIUnknown(
            accessRef.ThisPtr, typeof(IDirect3DDxgiInterfaceAccess));

        // GetInterface returns an AddRef'd pointer that we own. SharpGen's ComObject(IntPtr)
        // constructor ATTACHES to the pointer (no AddRef of its own) and releases it on Dispose —
        // so ownership transfers to the wrapper here and there must be no Marshal.Release of our
        // own (an extra Release over-frees a texture the WinRT surface still references, which
        // crashes later in WinRT.IObjectReference.Finalize once the GC runs).
        IntPtr texturePtr = access.GetInterface(typeof(ID3D11Texture2D).GUID);
        return new ID3D11Texture2D(texturePtr);
    }

    private static GraphicsCaptureItem CreateItemForMonitor(nint hmonitor)
    {
        // .NET Core's P/Invoke marshaler does not support UnmanagedType.HString directly (that
        // relies on the old .NET Framework WinRT-metadata interop path) — build the HSTRING by
        // hand via WindowsCreateString/WindowsDeleteString instead, which is what CsWinRT itself
        // does internally.
        const string className = "Windows.Graphics.Capture.GraphicsCaptureItem";
        WindowsCreateString(className, (uint)className.Length, out IntPtr classNameHandle);
        try
        {
            Guid interopIid = typeof(NativeMethods.IGraphicsCaptureItemInterop).GUID;
            RoGetActivationFactory(classNameHandle, ref interopIid, out IntPtr factoryPtr);
            try
            {
                var interop = (NativeMethods.IGraphicsCaptureItemInterop)Marshal.GetTypedObjectForIUnknown(
                    factoryPtr, typeof(NativeMethods.IGraphicsCaptureItemInterop));
                Guid itemIid = ResolveGraphicsCaptureItemIid();
                // FromAbi AddRefs internally and does NOT consume this reference — Release the
                // pointer we own after wrapping (same semantics as the device wrap above).
                IntPtr itemPtr = interop.CreateForMonitor(hmonitor, ref itemIid);
                try
                {
                    return WinRT.MarshalInterface<GraphicsCaptureItem>.FromAbi(itemPtr);
                }
                finally
                {
                    Marshal.Release(itemPtr);
                }
            }
            finally
            {
                Marshal.Release(factoryPtr);
            }
        }
        finally
        {
            WindowsDeleteString(classNameHandle);
        }
    }

    /// <summary>The default-interface IID of GraphicsCaptureItem (IGraphicsCaptureItem), resolved
    /// from the projection assembly at runtime; falls back to the well-known published GUID if the
    /// internal ABI type can't be found (projection implementation detail, per PLAN.md flag on
    /// IGraphicsCaptureItemInterop's stability).</summary>
    private static Guid ResolveGraphicsCaptureItemIid()
    {
        var t = typeof(GraphicsCaptureItem).Assembly.GetType("Windows.Graphics.Capture.IGraphicsCaptureItem");
        return t?.GUID ?? new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    }

    [DllImport("d3d11.dll", EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice", PreserveSig = false)]
    private static extern void CreateDirect3D11DeviceFromDXGIDeviceNative(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    [DllImport("combase.dll", PreserveSig = false)]
    private static extern void RoGetActivationFactory(
        IntPtr activatableClassId,
        [In] ref Guid iid,
        out IntPtr factory);

    [DllImport("combase.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void WindowsCreateString(
        string sourceString, uint length, out IntPtr hstring);

    [DllImport("combase.dll")]
    private static extern int WindowsDeleteString(IntPtr hstring);

    [ComImport]
    [Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirect3DDxgiInterfaceAccess
    {
        IntPtr GetInterface([In] Guid iid);
    }
}
