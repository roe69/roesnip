using RoeSnip.Core.Capture;
using RoeSnip.Core.Recording;
using Xunit;

namespace RoeSnip.Core.Tests;

public class ScreenCastStreamMappingTests
{
    private static MonitorInfo Mon(int index, string name, int x, int y, int w, int h) =>
        new(index, name, name, RectPhysical.FromSize(x, y, w, h), 96, 96, 1.0, false, 240, 240, index == 0);

    // The owner's layout: 2560x1440, 1440x2560 portrait, 2560x1440.
    private static readonly MonitorInfo[] Owner =
    {
        Mon(0, "DP-1", 0, 560, 2560, 1440),
        Mon(1, "DP-2", 2560, 0, 1440, 2560),
        Mon(2, "HDMI-1", 4000, 560, 2560, 1440),
    };

    [Fact]
    public void ExactGeometry_MapsEveryMonitor_RegardlessOfStreamOrder()
    {
        var streams = new[]
        {
            new ScreenCastStreamGeometry(70, 4000, 560, 2560, 1440),
            new ScreenCastStreamGeometry(68, 0, 560, 2560, 1440),
            new ScreenCastStreamGeometry(69, 2560, 0, 1440, 2560),
        };
        var map = ScreenCastStreamMapping.Map(streams, Owner);
        Assert.Equal(68u, map[0].NodeId);
        Assert.Equal(69u, map[1].NodeId);
        Assert.Equal(70u, map[2].NodeId);
    }

    [Fact]
    public void UniformScaleDifference_MapsByNormalisedCentre()
    {
        // Portal reports logical coordinates at scale 2, RandR physical pixels.
        var streams = Owner.Select((m, i) => new ScreenCastStreamGeometry((uint)(100 + i),
            m.BoundsPx.Left / 2, m.BoundsPx.Top / 2, m.BoundsPx.Width / 2, m.BoundsPx.Height / 2)).Reverse().ToArray();
        var map = ScreenCastStreamMapping.Map(streams, Owner);
        Assert.Equal(3, map.Count);
        Assert.Equal(100u, map[0].NodeId);
        Assert.Equal(101u, map[1].NodeId);
        Assert.Equal(102u, map[2].NodeId);
    }

    [Fact]
    public void FewerStreamsThanMonitors_LeavesTheUnsharedMonitorUnmapped()
    {
        var streams = new[] { new ScreenCastStreamGeometry(7, 2560, 0, 1440, 2560) };
        var map = ScreenCastStreamMapping.Map(streams, Owner);
        Assert.Single(map);
        Assert.Equal(7u, map[1].NodeId);
    }

    [Fact]
    public void FarApartStream_IsNotForcedOntoAMonitor()
    {
        var monitors = new[] { Mon(0, "A", 0, 0, 1920, 1080), Mon(1, "B", 1920, 0, 1920, 1080) };
        // One stream exactly on A, one stream that sits nowhere near B's centre in its own union.
        var streams = new[]
        {
            new ScreenCastStreamGeometry(1, 0, 0, 1920, 1080),
            new ScreenCastStreamGeometry(2, 0, 5000, 1920, 1080),
        };
        var map = ScreenCastStreamMapping.Map(streams, monitors);
        Assert.Equal(1u, map[0].NodeId);
        Assert.False(map.ContainsKey(1));
    }
}

public class ScreenCastTokenStoreTests
{
    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), "roesnip-token-test-" + Guid.NewGuid().ToString("N"), "screencast-snapshot.token");

    [Fact]
    public void SaveThenLoad_RoundTripsAndReplacesTheOldToken()
    {
        var store = new ScreenCastTokenStore(TempPath());
        try
        {
            Assert.Null(store.Load());
            store.Save("first-token");
            Assert.Equal("first-token", store.Load());
            store.Save("  second-token\n");
            Assert.Equal("second-token", store.Load());
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(store.FilePath)!, recursive: true);
        }
    }

    [Fact]
    public void SaveNull_DeletesTheToken()
    {
        var store = new ScreenCastTokenStore(TempPath());
        try
        {
            store.Save("token");
            store.Save(null);
            Assert.Null(store.Load());
            Assert.False(File.Exists(store.FilePath));
        }
        finally
        {
            string dir = Path.GetDirectoryName(store.FilePath)!;
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void BlankFile_LoadsAsNoToken()
    {
        var store = new ScreenCastTokenStore(TempPath());
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath)!);
            File.WriteAllText(store.FilePath, "   ");
            Assert.Null(store.Load());
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(store.FilePath)!, recursive: true);
        }
    }

    [Fact]
    public void Save_IsOwnerOnlyOnUnix()
    {
        if (OperatingSystem.IsWindows()) return;
        var store = new ScreenCastTokenStore(TempPath());
        try
        {
            store.Save("token");
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(store.FilePath));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(store.FilePath)!, recursive: true);
        }
    }
}

public class GstEncoderCatalogTests
{
    [Fact]
    public void H264_ProbeOrder_IsHardwareFirst()
    {
        Assert.Equal(new[] { "nvh264enc", "vah264enc", "vaapih264enc", "x264enc", "openh264enc" },
            GstEncoderCatalog.H264.Select(c => c.Element));
    }

    [Fact]
    public void Available_KeepsProbeOrder_AndSkipsMissingElements()
    {
        var installed = new HashSet<string> { "openh264enc", "nvh264enc", "vah264enc" };
        var available = GstEncoderCatalog.Available(GstEncoderCatalog.H264, installed.Contains);
        Assert.Equal(new[] { "nvh264enc", "vah264enc", "openh264enc" }, available.Select(c => c.Element));
    }

    [Fact]
    public void Bitrate_IsKbpsForHardwareAndX264_AndBpsForOpenH264()
    {
        var byName = GstEncoderCatalog.H264.ToDictionary(c => c.Element);
        Assert.Equal("nvh264enc bitrate=8000", byName["nvh264enc"].Describe(8_000_000));
        Assert.Equal("vah264enc bitrate=8000", byName["vah264enc"].Describe(8_000_000));
        Assert.Equal("x264enc bitrate=8000 speed-preset=veryfast", byName["x264enc"].Describe(8_000_000));
        Assert.Equal("openh264enc rate-control=bitrate bitrate=8000000", byName["openh264enc"].Describe(8_000_000));
    }

    [Fact]
    public void Audio_PrefersAac_OpusLast()
    {
        var names = GstEncoderCatalog.Audio.Select(c => c.Element).ToArray();
        Assert.Equal("fdkaacenc", names[0]);
        Assert.Equal("opusenc", names[^1]);
        Assert.Equal("opusenc bitrate=128000", GstEncoderCatalog.Audio[^1].Describe(128_000));
    }
}

public class CaptureContextTests
{
    [Fact]
    public async Task BackgroundScope_FlowsIntoParallelWorkers_AndEndsOnDispose()
    {
        Assert.False(CaptureContext.IsBackground);
        using (CaptureContext.BeginBackground())
        {
            var seen = new bool[8];
            Parallel.For(0, seen.Length, i => seen[i] = CaptureContext.IsBackground);
            Assert.All(seen, Assert.True);
            Assert.True(await Task.Run(() => CaptureContext.IsBackground));
        }
        Assert.False(CaptureContext.IsBackground);
    }
}
