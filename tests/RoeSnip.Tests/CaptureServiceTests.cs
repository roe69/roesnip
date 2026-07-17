using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using RoeSnip.Capture;
using Xunit;

namespace RoeSnip.Tests;

/// <summary>Exercises CaptureService's fallback/omission contract and the parallel-capture
/// ordering guarantee with fake capturers, using the public test constructor and an isolated
/// temp-path CaptureCache — never real monitors or the real %APPDATA% cache file.</summary>
public class CaptureServiceTests : IDisposable
{
    private readonly string _tempDir;

    public CaptureServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"roesnip_captureservice_test_{Guid.NewGuid():N}");
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
        HMonitor: 0,
        BoundsPx: RectPhysical.FromSize(index * 100, 0, 100, 100),
        DpiX: 96,
        DpiY: 96,
        AdvancedColorActive: false,
        SdrWhiteNits: 240.0,
        MaxLuminanceNits: 1000.0,
        IsPrimary: index == 0);

    private static CapturedFrame Frame(MonitorInfo monitor) =>
        new(FrameFormat.Bgra8Srgb, 1, 1, 4, new byte[] { 1, 2, 3, 255 }, monitor);

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
        var service = new CaptureService(primary, fallback, NewCache());

        var frames = service.CaptureAll(monitors);

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
        var service = new CaptureService(primary, fallback, NewCache());

        var frames = service.CaptureAll(monitors);

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
        var service = new CaptureService(primary, fallback, NewCache());

        var frames = service.CaptureAll(monitors);

        Assert.Equal(new[] { 0, 2 }, frames.Select(f => f.Monitor.Index));
    }

    [Fact]
    public void CaptureAll_PrimaryFailure_IsMemoized_SecondCallSkipsPrimary()
    {
        var monitors = new[] { Monitor(0) };
        // The memo only records failures that PROVE the permanent quirk (all-zero frame) — see
        // CaptureException.IndicatesPermanentlyBroken; transient failures are covered below.
        var primary = new FakeCapturer(m =>
            throw new CaptureException("black frame") { IndicatesPermanentlyBroken = true });
        var fallback = new FakeCapturer(Frame);
        var cache = NewCache();
        var service = new CaptureService(primary, fallback, cache);

        service.CaptureAll(monitors);
        service.CaptureAll(monitors);

        Assert.Single(primary.CalledFor);      // only the first call paid the doomed attempt
        Assert.Equal(2, fallback.CalledFor.Count);
    }

    [Fact]
    public void CaptureAll_PrimaryFailureMemo_PersistsAcrossCacheInstances()
    {
        var monitors = new[] { Monitor(0) };
        var failingPrimary = new FakeCapturer(m =>
            throw new CaptureException("black frame") { IndicatesPermanentlyBroken = true });
        var fallback1 = new FakeCapturer(Frame);
        new CaptureService(failingPrimary, fallback1, NewCache()).CaptureAll(monitors);

        // Fresh CaptureService + fresh CaptureCache on the same path = simulated app relaunch:
        // the persisted memo must route straight to WGC without touching Desktop Duplication.
        var primary2 = new FakeCapturer(Frame);
        var fallback2 = new FakeCapturer(Frame);
        var frames = new CaptureService(primary2, fallback2, NewCache()).CaptureAll(monitors);

        Assert.Single(frames);
        Assert.Empty(primary2.CalledFor);
        Assert.Equal(new[] { monitors[0].DeviceName }, fallback2.CalledFor);
    }

    [Fact]
    public void CaptureAll_TransientPrimaryFailure_FallsBackButIsNotMemoized()
    {
        var monitors = new[] { Monitor(0) };
        // Plain CaptureException (IndicatesPermanentlyBroken unset) = transient/environmental
        // failure (ghost wake-time display, duplication slot held by an abandoned capture, access
        // lost): every call still falls back to WGC, but the doomed-primary memo must NOT persist.
        var primary = new FakeCapturer(m => throw new CaptureException("transient failure"));
        var fallback = new FakeCapturer(Frame);
        var cache = NewCache();
        var service = new CaptureService(primary, fallback, cache);

        service.CaptureAll(monitors);
        service.CaptureAll(monitors);

        Assert.Equal(2, primary.CalledFor.Count);  // second call still tries the primary
        Assert.Equal(2, fallback.CalledFor.Count);
        Assert.False(cache.IsDesktopDuplicationBroken(monitors[0].DeviceName));
    }

    [Fact]
    public void CaptureAll_OnlyMonitorIndex_CapturesJustThatMonitor()
    {
        var monitors = new[] { Monitor(0), Monitor(1), Monitor(2) };
        var primary = new FakeCapturer(Frame);
        var fallback = new FakeCapturer(m => throw new CaptureException("fallback should not be used"));
        var service = new CaptureService(primary, fallback, NewCache());

        var frames = service.CaptureAll(monitors, onlyMonitorIndex: 1);

        Assert.Single(frames);
        Assert.Equal(1, frames[0].Monitor.Index);
        Assert.Equal(new[] { monitors[1].DeviceName }, primary.CalledFor);
    }
}
