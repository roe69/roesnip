using RoeSnip.Core.Notifications;
using RoeSnip.Core.Sharing;
using Xunit;

namespace RoeSnip.Core.Tests.Sharing;

public class UrlLauncherTests
{
    [Theory]
    [InlineData(20000, 10000)]
    [InlineData(10000, 10000)]
    [InlineData(8000, 8000)]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    public void ToastDuration_Cap_Clamps(int requested, int expected) =>
        Assert.Equal(expected, ToastDuration.Cap(requested));

    [Fact]
    public void ToastDuration_Cap_TimeSpan()
    {
        Assert.Equal(TimeSpan.FromSeconds(10), ToastDuration.Cap(TimeSpan.FromSeconds(15)));
        Assert.Equal(TimeSpan.FromSeconds(4), ToastDuration.Cap(TimeSpan.FromSeconds(4)));
    }

    [Theory]
    [InlineData("https://github.com/roe69/roesnip/releases/tag/v1.2.3", true)]
    [InlineData("http://example.com", true)]
    [InlineData("file:///etc/passwd", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("not a url", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsOpenable_AllowsOnlyWebPages(string? url, bool expected) =>
        Assert.Equal(expected, UrlLauncher.IsOpenable(url));

    [Fact]
    public void Sanitize_DropsAppImageMarkersAndMountPaths()
    {
        var env = new Dictionary<string, string?>
        {
            ["APPDIR"] = "/tmp/.mount_RoeSniXyz",
            ["APPIMAGE"] = "/home/u/RoeSnip.AppImage",
            ["ARGV0"] = "/home/u/RoeSnip.AppImage",
            ["OWD"] = "/home/u",
            ["LD_LIBRARY_PATH"] = "/tmp/.mount_RoeSniXyz/usr/lib:/tmp/.mount_RoeSniXyz/lib",
            ["LD_PRELOAD"] = "/tmp/.mount_RoeSniXyz/x.so",
            ["PATH"] = "/tmp/.mount_RoeSniXyz/usr/bin:/usr/local/bin:/usr/bin",
            ["XDG_DATA_DIRS"] = "/tmp/.mount_RoeSniXyz/usr/share:/usr/share",
            ["GST_PLUGIN_PATH"] = "/tmp/.mount_RoeSniXyz/gst",
            ["HOME"] = "/home/u",
            ["DISPLAY"] = ":0",
        };
        UrlLauncherEnvironment.Sanitize(env);

        Assert.False(env.ContainsKey("APPDIR"));
        Assert.False(env.ContainsKey("APPIMAGE"));
        Assert.False(env.ContainsKey("ARGV0"));
        Assert.False(env.ContainsKey("OWD"));
        Assert.False(env.ContainsKey("LD_LIBRARY_PATH"));
        Assert.False(env.ContainsKey("LD_PRELOAD"));
        Assert.False(env.ContainsKey("GST_PLUGIN_PATH"));
        Assert.Equal("/usr/local/bin:/usr/bin", env["PATH"]);
        Assert.Equal("/usr/share", env["XDG_DATA_DIRS"]);
        Assert.Equal("/home/u", env["HOME"]);
        Assert.Equal(":0", env["DISPLAY"]);
    }

    [Fact]
    public void Sanitize_KeepsUserLibraryPathEntries()
    {
        var env = new Dictionary<string, string?>
        {
            ["APPDIR"] = "/tmp/.mount_A",
            ["LD_LIBRARY_PATH"] = "/tmp/.mount_A/usr/lib:/opt/lib",
        };
        UrlLauncherEnvironment.Sanitize(env);
        Assert.Equal("/opt/lib", env["LD_LIBRARY_PATH"]);
    }

    [Fact]
    public void Sanitize_PlainEnvironmentUnchanged()
    {
        var env = new Dictionary<string, string?> { ["PATH"] = "/usr/bin:/bin", ["HOME"] = "/home/u" };
        UrlLauncherEnvironment.Sanitize(env);
        Assert.Equal("/usr/bin:/bin", env["PATH"]);
        Assert.Equal(2, env.Count);
    }

    [Fact]
    public void LinuxOpeners_XdgOpenFirstThenGio()
    {
        Assert.Equal(new[] { "xdg-open" }, UrlLauncher.LinuxOpeners[0]);
        Assert.Equal(new[] { "gio", "open" }, UrlLauncher.LinuxOpeners[1]);
    }
}
