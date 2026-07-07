using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using RoeSnip.Core.Capture;
using Xunit;

namespace RoeSnip.Core.Tests;

/// <summary>Port of the WPF app's CaptureServiceTests (PLAN-XPLAT.md §3.1): the same
/// fallback/omission/memoization/ordering assertions, exercised against the generalized
/// FallbackCaptureBackend (two fake IScreenCapturers in priority order) instead of the old
/// CaptureService(primary, fallback, cache) 3-arg constructor, which no longer exists —
/// CaptureService is now the 1-arg ICaptureBackend-wrapping facade.</summary>
public class FallbackCaptureBackendTests : IDisposable
{
    private readonly string _tempDir;

    public FallbackCaptureBackendTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"roesnip_core_fallbackbackend_test_{Guid.NewGuid():N}");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    private CaptureCache NewCache() => new(Path.Combine(_tempDir, "capture-cache.json"));

    private static MonitorInfo Monitor(int index) => new(
        Index: index,
        DeviceName: $@"\\.\DISPLAY{index + 1}",
        BackendKey: "0x0",
        BoundsPx: RectPhysical.FromSize(index * 100, 0, 100, 100),
        DpiX: 96,
        DpiY: 96,
        Scale: 1.0,
        AdvancedColorActive: false,
        SdrWhiteNits: 240.0,
        MaxLuminanceNits: 1000.0,
        IsPrimary: index == 0);

    private static CapturedFrame Frame(MonitorInfo monitor) =>
        new(FrameFormat.Bgra8Srgb, 1, 1, 4, new byte[] { 1, 2, 3, 255 }, monitor, sdrWhiteInBufferUnits: 1.0);

    private static FallbackCaptureBackend NewBackend(
        MonitorInfo[] monitors, IScreenCapturer primary, IScreenCapturer fallback, CaptureCache cache) =>
        new("Test (primary/fallback)", supportsHdrExport: false,
            () => monitors, new[] { primary, fallback }, cache);

    /// <summary>Thread-safe fake: records which monitors it was called for and delegates to the
    /// given behavior (return a frame or throw CaptureException).</summary>
    private sealed class FakeCapturer : IScreenCapturer
    {
        private readonly Func<MonitorInfo, CapturedFrame> _behavior;
        public ConcurrentBag<string> CalledFor { get; } = new();

        public FakeCapturer(Func<MonitorInfo, CapturedFrame> behavior) => _behavior = behavior;

        public CapturedFrame Capture(MonitorInfo monitor)
        {
            CalledFor.Add(monitor.DeviceName);
            return _behavior(monitor);
        }
    }

    [Fact]
    public void CaptureAll_PreservesInputOrder_EvenWhenCapturesFinishOutOfOrder()
    {
        var monitors = new[] { Monitor(0), Monitor(1), Monitor(2) };
        // Monitor 0 is the slowest, monitor 2 the fastest — with parallel capture the completion
        // order is the reverse of the input order, but the results must still come back 0, 1, 2.
        var primary = new FakeCapturer(m =>
        {
            Thread.Sleep((monitors.Length - 1 - m.Index) * 40);
            return Frame(m);
        });
        var fallback = new FakeCapturer(m => throw new CaptureException("fallback should not be used"));
        var backend = NewBackend(monitors, primary, fallback, NewCache());

        var frames = backend.CaptureAll(monitors);

        Assert.Equal(new[] { 0, 1, 2 }, frames.Select(f => f.Monitor.Index));
        Assert.Empty(fallback.CalledFor);
    }

    [Fact]
    public void CaptureAll_PrimaryFails_UsesFallback_AndKeepsOrdering()
    {
        var monitors = new[] { Monitor(0), Monitor(1), Monitor(2) };
        var primary = new FakeCapturer(m => m.Index == 1
            ? throw new CaptureException("black frame")
            : Frame(m));
        var fallback = new FakeCapturer(Frame);
        var backend = NewBackend(monitors, primary, fallback, NewCache());

        var frames = backend.CaptureAll(monitors);

        Assert.Equal(new[] { 0, 1, 2 }, frames.Select(f => f.Monitor.Index));
        Assert.Equal(new[] { monitors[1].DeviceName }, fallback.CalledFor);
    }

    [Fact]
    public void CaptureAll_BothPathsFail_OmitsThatMonitorOnly()
    {
        var monitors = new[] { Monitor(0), Monitor(1), Monitor(2) };
        var primary = new FakeCapturer(m => throw new CaptureException("primary down"));
        var fallback = new FakeCapturer(m => m.Index == 1
            ? throw new CaptureException("fallback down too")
            : Frame(m));
        var backend = NewBackend(monitors, primary, fallback, NewCache());

        var frames = backend.CaptureAll(monitors);

        Assert.Equal(new[] { 0, 2 }, frames.Select(f => f.Monitor.Index));
    }

    [Fact]
    public void CaptureAll_PrimaryFailure_IsMemoized_SecondCallSkipsPrimary()
    {
        var monitors = new[] { Monitor(0) };
        var primary = new FakeCapturer(m => throw new CaptureException("black frame"));
        var fallback = new FakeCapturer(Frame);
        var cache = NewCache();
        var backend = NewBackend(monitors, primary, fallback, cache);

        backend.CaptureAll(monitors);
        backend.CaptureAll(monitors);

        Assert.Single(primary.CalledFor);      // only the first call paid the doomed attempt
        Assert.Equal(2, fallback.CalledFor.Count);
    }

    [Fact]
    public void CaptureAll_PrimaryFailureMemo_PersistsAcrossCacheInstances()
    {
        var monitors = new[] { Monitor(0) };
        var failingPrimary = new FakeCapturer(m => throw new CaptureException("black frame"));
        var fallback1 = new FakeCapturer(Frame);
        NewBackend(monitors, failingPrimary, fallback1, NewCache()).CaptureAll(monitors);

        // Fresh FallbackCaptureBackend + fresh CaptureCache on the same path = simulated app
        // relaunch: the persisted memo must route straight to the fallback capturer without
        // touching the primary.
        var primary2 = new FakeCapturer(Frame);
        var fallback2 = new FakeCapturer(Frame);
        var frames = NewBackend(monitors, primary2, fallback2, NewCache()).CaptureAll(monitors);

        Assert.Single(frames);
        Assert.Empty(primary2.CalledFor);
        Assert.Equal(new[] { monitors[0].DeviceName }, fallback2.CalledFor);
    }

    [Fact]
    public void CaptureAll_OnlyMonitorIndex_CapturesJustThatMonitor()
    {
        var monitors = new[] { Monitor(0), Monitor(1), Monitor(2) };
        var primary = new FakeCapturer(Frame);
        var fallback = new FakeCapturer(m => throw new CaptureException("fallback should not be used"));
        var backend = NewBackend(monitors, primary, fallback, NewCache());

        var frames = backend.CaptureAll(monitors, onlyMonitorIndex: 1);

        Assert.Single(frames);
        Assert.Equal(1, frames[0].Monitor.Index);
        Assert.Equal(new[] { monitors[1].DeviceName }, primary.CalledFor);
    }

    [Fact]
    public void CaptureAll_NullMonitors_UsesEnumerateMonitors()
    {
        var monitors = new[] { Monitor(0), Monitor(1) };
        var primary = new FakeCapturer(Frame);
        var fallback = new FakeCapturer(m => throw new CaptureException("fallback should not be used"));
        var backend = NewBackend(monitors, primary, fallback, NewCache());

        var frames = backend.CaptureAll(monitors: null);

        Assert.Equal(new[] { 0, 1 }, frames.Select(f => f.Monitor.Index));
    }
}
