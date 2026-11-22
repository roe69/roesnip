using System;
using System.IO;
using RoeSnip.Core.Updates;
using Xunit;

namespace RoeSnip.Core.Tests.Updates;

/// <summary>Symlink resolution for install-path comparisons, against real temp symlinks. On a
/// Windows host without the symlink privilege each test returns early.</summary>
public sealed class RealPathTests : IDisposable
{
    private readonly string _tempDir;

    public RealPathTests()
    {
        // The temp dir itself may sit behind a symlink (macOS /var -> /private/var), so resolve it.
        string raw = Path.Combine(Path.GetTempPath(), "roesnip_realpath_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(raw);
        _tempDir = RealPath.Resolve(raw);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private static bool TryLink(string link, string target, bool directory)
    {
        try
        {
            if (directory)
            {
                Directory.CreateSymbolicLink(link, target);
            }
            else
            {
                File.CreateSymbolicLink(link, target);
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    [Fact]
    public void PlainPath_IsUnchanged()
    {
        string file = Path.Combine(_tempDir, "a", "RoeSnip.AppImage");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "x");

        Assert.Equal(file, RealPath.Resolve(file));
    }

    [Fact]
    public void SymlinkedHome_ResolvesToRealDir_EvenForMissingTail()
    {
        string realHome = Path.Combine(_tempDir, "real-home");
        Directory.CreateDirectory(Path.Combine(realHome, ".local", "share"));
        string linkHome = Path.Combine(_tempDir, "home");
        if (!TryLink(linkHome, realHome, directory: true))
        {
            return;
        }

        string viaLink = Path.Combine(linkHome, ".local", "share", "RoeSnip", "RoeSnip.AppImage");
        string real = Path.Combine(realHome, ".local", "share", "RoeSnip", "RoeSnip.AppImage");

        Assert.Equal(real, RealPath.Resolve(viaLink));
        Assert.True(RealPath.SameFile(viaLink, real, StringComparison.Ordinal));
    }

    [Fact]
    public void RelativeAndChainedLinks_Resolve()
    {
        string realData = Path.Combine(_tempDir, "disk", "data");
        Directory.CreateDirectory(realData);
        string mid = Path.Combine(_tempDir, "mid");
        string top = Path.Combine(_tempDir, "top");
        // mid -> disk/data (relative), top -> mid (absolute)
        if (!TryLink(mid, Path.Combine("disk", "data"), directory: true) || !TryLink(top, mid, directory: true))
        {
            return;
        }

        string file = Path.Combine(realData, "RoeSnip.AppImage");
        File.WriteAllText(file, "x");
        string fileLink = Path.Combine(_tempDir, "app-link");
        if (!TryLink(fileLink, Path.Combine(top, "RoeSnip.AppImage"), directory: false))
        {
            return;
        }

        Assert.Equal(file, RealPath.Resolve(Path.Combine(top, "RoeSnip.AppImage")));
        Assert.Equal(file, RealPath.Resolve(fileLink));
    }

    [Fact]
    public void LinkLoop_Throws_AndResolveOrFullFallsBack()
    {
        string a = Path.Combine(_tempDir, "loop-a");
        string b = Path.Combine(_tempDir, "loop-b");
        if (!TryLink(a, b, directory: false) || !TryLink(b, a, directory: false))
        {
            return;
        }

        Assert.Throws<IOException>(() => RealPath.Resolve(a));
        Assert.Equal(a, RealPath.ResolveOrFull(a));
    }

    [Fact]
    public void DisposableLinuxSource_TargetThroughSymlink_IsNotDisposable()
    {
        string realDir = Path.Combine(_tempDir, "share");
        Directory.CreateDirectory(realDir);
        string linkDir = Path.Combine(_tempDir, "share-link");
        if (!TryLink(linkDir, realDir, directory: true))
        {
            return;
        }

        string installed = Path.Combine(linkDir, "RoeSnip.AppImage");
        Assert.False(UpdateInstallLayout.IsDisposableLinuxSource(Path.Combine(realDir, "RoeSnip.AppImage"), installed));
    }

    [Fact]
    public void MacInstallTarget_UsesRunningBundleOnlyInWritableApplications()
    {
        string home = Path.Combine(_tempDir, "Users", "me");
        string userApps = Path.Combine(home, "Applications");
        Directory.CreateDirectory(userApps);
        string inUserApps = Path.Combine(userApps, "RoeSnip.app");
        string inDownloads = Path.Combine(home, "Downloads", "RoeSnip.app");
        string fallback = UpdateInstallLayout.MacBundlePath(home);

        Assert.Equal(inUserApps, UpdateInstallLayout.MacInstallTarget(inUserApps, home, _ => true));
        Assert.Equal(fallback, UpdateInstallLayout.MacInstallTarget(inUserApps, home, _ => false));
        Assert.Equal(fallback, UpdateInstallLayout.MacInstallTarget(inDownloads, home, _ => true));
        Assert.Equal(fallback, UpdateInstallLayout.MacInstallTarget("/Volumes/RoeSnip/RoeSnip.app", home, _ => true));
        Assert.Equal(fallback, UpdateInstallLayout.MacInstallTarget(null, home, _ => true));
        // A renamed copy in /Applications is still the one in use (a rooted Unix path only).
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        Assert.Equal("/Applications/RoeSnip 2.app",
            UpdateInstallLayout.MacInstallTarget("/Applications/RoeSnip 2.app", home, _ => true));
    }

    [Fact]
    public void MacInstallTarget_SymlinkedHome_StillMatchesUserApplications()
    {
        string realHome = Path.Combine(_tempDir, "real-me");
        Directory.CreateDirectory(Path.Combine(realHome, "Applications"));
        string linkHome = Path.Combine(_tempDir, "me");
        if (!TryLink(linkHome, realHome, directory: true))
        {
            return;
        }

        string running = Path.Combine(realHome, "Applications", "RoeSnip.app");
        Assert.Equal(running, UpdateInstallLayout.MacInstallTarget(running, linkHome, _ => true));
    }
}
