using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RoeSnip.Core.Capture;
using RoeSnip.Core.Recording;
using Xunit;

namespace RoeSnip.Core.Tests;

/// <summary>PolledRegionCaptureSource — the portable (non-Windows) IRegionCaptureSource fallback:
/// polls a fake ICaptureBackend and crops in managed code. Exercises the polling/crop/pause/origin-
/// clamp/fault-propagation logic with no real capture backend involved.</summary>
public class PolledRegionCaptureSourceTests
{
    private static MonitorInfo Monitor(int width = 4, int height = 4) => new(
        Index: 0, DeviceName: @"\\.\DISPLAY1", BackendKey: "0x0",
        BoundsPx: RectPhysical.FromSize(0, 0, width, height),
        DpiX: 96, DpiY: 96, Scale: 1.0, AdvancedColorActive: false,
        SdrWhiteNits: 240.0, MaxLuminanceNits: 1000.0, IsPrimary: true);

    /// <summary>4x4 monitor whose B channel holds each pixel's own linear index (0..15) — lets a
    /// test assert exactly which source pixels a crop pulled from.</summary>
    private static byte[] IndexedPixels(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (int i = 0; i < width * height; i++)
        {
            pixels[i * 4] = (byte)i;
            pixels[i * 4 + 3] = 255;
        }
        return pixels;
    }

    private sealed class FakeCaptureBackend : ICaptureBackend
    {
        private readonly Func<MonitorInfo, byte[]> _pixelsFactory;
        private readonly Func<bool>? _shouldThrow;
        public int CallCount;

        public FakeCaptureBackend(Func<MonitorInfo, byte[]> pixelsFactory, Func<bool>? shouldThrow = null)
        {
            _pixelsFactory = pixelsFactory;
            _shouldThrow = shouldThrow;
        }

        public string Name => "Fake";
        public bool SupportsHdrExport => false;
        public IReadOnlyList<MonitorInfo> EnumerateMonitors() => Array.Empty<MonitorInfo>();

        public IReadOnlyList<CapturedFrame> CaptureAll(IReadOnlyList<MonitorInfo>? monitors, int? onlyMonitorIndex)
        {
            Interlocked.Increment(ref CallCount);
            if (_shouldThrow?.Invoke() == true)
            {
                throw new InvalidOperationException("simulated capture failure");
            }
            var m = monitors![0];
            var pixels = _pixelsFactory(m);
            return new[] { new CapturedFrame(FrameFormat.Bgra8Srgb, m.BoundsPx.Width, m.BoundsPx.Height, m.BoundsPx.Width * 4, pixels, m, sdrWhiteInBufferUnits: 1.0) };
        }
    }

    private static async Task<RegionCaptureFrame> ReadOneFrameAsync(IRegionCaptureSource source, int timeoutMs = 5000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        return await source.Frames.ReadAsync(cts.Token);
    }

    [Fact]
    public async Task CropsFrameToRequestedRegion()
    {
        var monitor = Monitor(4, 4);
        var backend = new FakeCaptureBackend(m => IndexedPixels(m.BoundsPx.Width, m.BoundsPx.Height));
        var selection = RectPhysical.FromSize(1, 1, 2, 2);
        using var source = new PolledRegionCaptureSource(backend, monitor, selection, targetFps: 200);
        source.Start();

        var queued = await ReadOneFrameAsync(source);
        using (queued.Frame)
        {
            Assert.Equal(2, queued.Frame.Width);
            Assert.Equal(2, queued.Frame.Height);
            // Crop origin (1,1) of a 4-wide source: pixel (0,0) of the crop == source index 1*4+1 = 5.
            Assert.Equal(5, queued.Frame.Row(0)[0]);
            // pixel (1,0) of the crop == source index 1*4+2 = 6.
            Assert.Equal(6, queued.Frame.Row(0)[4]);
            // pixel (0,1) of the crop == source index 2*4+1 = 9.
            Assert.Equal(9, queued.Frame.Row(1)[0]);
        }

        source.Stop();
    }

    [Fact]
    public async Task Paused_ProducesNoFramesAndDoesNotCallBackend()
    {
        var monitor = Monitor();
        var backend = new FakeCaptureBackend(m => IndexedPixels(m.BoundsPx.Width, m.BoundsPx.Height));
        using var source = new PolledRegionCaptureSource(backend, monitor, RectPhysical.FromSize(0, 0, 2, 2), targetFps: 200)
        {
            Paused = true,
        };
        source.Start();

        await Task.Delay(150); // several scheduled ticks' worth at 200fps
        Assert.Equal(0, backend.CallCount);

        source.Paused = false;
        var queued = await ReadOneFrameAsync(source);
        queued.Frame.Dispose();
        Assert.True(backend.CallCount > 0);

        source.Stop();
    }

    [Fact]
    public async Task SetOrigin_ClampsToMonitorBounds()
    {
        var monitor = Monitor(4, 4);
        var backend = new FakeCaptureBackend(m => IndexedPixels(m.BoundsPx.Width, m.BoundsPx.Height));
        using var source = new PolledRegionCaptureSource(backend, monitor, RectPhysical.FromSize(0, 0, 2, 2), targetFps: 200);

        // Way out of bounds — must clamp to the monitor's own max origin (4-2, 4-2) = (2,2), not throw
        // and not silently accept an out-of-range crop box.
        source.SetOrigin(999, 999);
        source.Start();

        var queued = await ReadOneFrameAsync(source);
        using (queued.Frame)
        {
            // pixel (0,0) of the crop should now be source index 2*4+2 = 10, not the original (0,0)'s 0.
            Assert.Equal(10, queued.Frame.Row(0)[0]);
        }
        source.Stop();
    }

    [Fact]
    public async Task BackendFailure_RaisesFaultedAndStopsProducing()
    {
        var monitor = Monitor();
        var backend = new FakeCaptureBackend(m => IndexedPixels(m.BoundsPx.Width, m.BoundsPx.Height), shouldThrow: () => true);
        using var source = new PolledRegionCaptureSource(backend, monitor, RectPhysical.FromSize(0, 0, 2, 2), targetFps: 200);

        var faultedTcs = new TaskCompletionSource<Exception>();
        source.Faulted += ex => faultedTcs.TrySetResult(ex);
        source.Start();

        var faulted = await faultedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsType<InvalidOperationException>(faulted);

        source.Stop();
    }

    [Fact]
    public void Stop_CompletesTheChannel()
    {
        var monitor = Monitor();
        var backend = new FakeCaptureBackend(m => IndexedPixels(m.BoundsPx.Width, m.BoundsPx.Height));
        using var source = new PolledRegionCaptureSource(backend, monitor, RectPhysical.FromSize(0, 0, 2, 2), targetFps: 200);
        source.Start();
        source.Stop();

        // Channel.Completion only transitions once BOTH the writer has completed AND every already-
        // queued item has been drained (System.Threading.Channels semantics) - Stop() itself only
        // guarantees the former, so drain whatever the pump thread produced before it observed
        // _disposed before asserting completion.
        while (source.Frames.TryRead(out var queued))
        {
            queued.Frame.Dispose();
        }

        Assert.True(source.Frames.Completion.IsCompleted);
    }
}
