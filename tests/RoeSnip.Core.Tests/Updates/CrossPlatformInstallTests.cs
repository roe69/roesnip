using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using RoeSnip.Core.Updates;
using Xunit;

namespace RoeSnip.Core.Tests.Updates;

/// <summary>Asset names per OS/arch, Linux/macOS install paths, the .desktop / LaunchAgent file
/// content, and the rename swap / rollback - all against temp dirs and injected env, never the real
/// user dirs.</summary>
public sealed class CrossPlatformInstallTests : IDisposable
{
    private readonly string _tempDir;

    public CrossPlatformInstallTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "roesnip_install_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private static Func<string, string?> EnvOf(Dictionary<string, string> values) =>
        name => values.TryGetValue(name, out string? v) ? v : null;

    private static readonly Func<string, string?> NoEnv = _ => null;

    // ---------- asset names (must match release.yml exactly) ----------

    [Theory]
    [InlineData(UpdatePlatform.Windows, Architecture.X64, "RoeSnipApp-win-x64.exe", "RoeSnipApp-win-x64.exe.gz")]
    [InlineData(UpdatePlatform.Windows, Architecture.Arm64, "RoeSnipApp-win-arm64.exe", "RoeSnipApp-win-arm64.exe.gz")]
    [InlineData(UpdatePlatform.Linux, Architecture.X64, "RoeSnip-linux-x64.AppImage", null)]
    [InlineData(UpdatePlatform.Linux, Architecture.Arm64, "RoeSnip-linux-arm64.AppImage", null)]
    [InlineData(UpdatePlatform.MacOS, Architecture.X64, "RoeSnip-osx-x64.app.tar.gz", null)]
    [InlineData(UpdatePlatform.MacOS, Architecture.Arm64, "RoeSnip-osx-arm64.app.tar.gz", null)]
    public void AssetNames_MatchReleaseWorkflow(UpdatePlatform platform, Architecture arch, string primary, string? gz)
    {
        UpdateAssetSelection? selection = UpdateAssetNames.For(platform, arch);
        Assert.NotNull(selection);
        Assert.Equal(primary, selection!.Primary);
        Assert.Equal(gz, selection.Gz);
    }

    [Theory]
    [InlineData(UpdatePlatform.Windows)]
    [InlineData(UpdatePlatform.Linux)]
    [InlineData(UpdatePlatform.MacOS)]
    public void AssetNames_NeverCollideWithWpfAsset(UpdatePlatform platform)
    {
        foreach (Architecture arch in new[] { Architecture.X64, Architecture.Arm64 })
        {
            UpdateAssetSelection s = UpdateAssetNames.For(platform, arch)!;
            Assert.NotEqual("RoeSnip.exe", s.Primary, StringComparer.OrdinalIgnoreCase);
            Assert.NotEqual("RoeSnip.exe.gz", s.Gz ?? "", StringComparer.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData(Architecture.X86)]
    [InlineData(Architecture.Arm)]
    public void AssetNames_UnshippedArch_IsNull(Architecture arch)
    {
        Assert.Null(UpdateAssetNames.For(UpdatePlatform.Linux, arch));
        Assert.Null(UpdateAssetNames.For(UpdatePlatform.Windows, arch));
    }

    // ---------- Linux / macOS paths ----------

    [Fact]
    public void LinuxPaths_DefaultToLocalShareAndConfig()
    {
        string home = "/home/u";
        Assert.Equal(Path.Combine(home, ".local", "share", "RoeSnip", "RoeSnip.AppImage"), UpdateInstallLayout.LinuxAppImagePath(NoEnv, home));
        Assert.Equal(Path.Combine(home, ".local", "share", "applications", "net.roelite.roesnip.desktop"), UpdateInstallLayout.LinuxMenuEntryPath(NoEnv, home));
        Assert.Equal(Path.Combine(home, ".local", "share", "icons", "hicolor", "256x256", "apps", "roesnip.png"), UpdateInstallLayout.LinuxIconPath(NoEnv, home));
        Assert.Equal(Path.Combine(home, ".config", "autostart", "net.roelite.roesnip.desktop"), UpdateInstallLayout.LinuxAutostartPath(NoEnv, home));
    }

    [Fact]
    public void LinuxPaths_RespectAbsoluteXdgDirs_IgnoreRelative()
    {
        string home = "/home/u";
        var env = EnvOf(new() { ["XDG_DATA_HOME"] = "/data", ["XDG_CONFIG_HOME"] = "/cfg" });
        Assert.Equal(Path.Combine("/data", "RoeSnip", "RoeSnip.AppImage"), UpdateInstallLayout.LinuxAppImagePath(env, home));
        Assert.Equal(Path.Combine("/cfg", "autostart", "net.roelite.roesnip.desktop"), UpdateInstallLayout.LinuxAutostartPath(env, home));

        var relative = EnvOf(new() { ["XDG_DATA_HOME"] = "rel/data", ["XDG_CONFIG_HOME"] = "rel" });
        Assert.Equal(Path.Combine(home, ".local", "share", "RoeSnip", "RoeSnip.AppImage"), UpdateInstallLayout.LinuxAppImagePath(relative, home));
        Assert.Equal(Path.Combine(home, ".config", "autostart", "net.roelite.roesnip.desktop"), UpdateInstallLayout.LinuxAutostartPath(relative, home));
    }

    [Fact]
    public void RunningAppImage_OnlyWhenAbsoluteAppImageSet()
    {
        Assert.Null(UpdateInstallLayout.RunningAppImage(NoEnv));
        Assert.Null(UpdateInstallLayout.RunningAppImage(EnvOf(new() { ["APPIMAGE"] = "" })));
        Assert.Equal("/x/RoeSnip.AppImage", UpdateInstallLayout.RunningAppImage(EnvOf(new() { ["APPIMAGE"] = "/x/RoeSnip.AppImage" })));
    }

    [Fact]
    public void AppImageFromEnviron_ReadsNulSeparatedBlock()
    {
        Assert.Equal("/home/u/.local/share/RoeSnip/RoeSnip.AppImage",
            UpdateInstallLayout.AppImageFromEnviron("HOME=/home/u\0APPIMAGE=/home/u/.local/share/RoeSnip/RoeSnip.AppImage\0APPDIR=/tmp/.mount_x\0"));
        Assert.Null(UpdateInstallLayout.AppImageFromEnviron("HOME=/home/u\0PATH=/bin\0"));
    }

    [Fact]
    public void MacPaths()
    {
        string home = "/Users/u";
        string bundle = UpdateInstallLayout.MacBundlePath(home);
        Assert.Equal(Path.Combine(home, "Applications", "RoeSnip.app"), bundle);
        Assert.Equal(Path.Combine(bundle, "Contents", "MacOS", "RoeSnip"), UpdateInstallLayout.MacBundleExecutable(bundle));
        Assert.Equal(Path.Combine(home, "Library", "LaunchAgents", "net.roelite.roesnip.plist"), UpdateInstallLayout.MacLaunchAgentPath(home));
    }

    [Fact]
    public void BundleOf_FindsEnclosingApp_OrNull()
    {
        string bundle = Path.Combine(_tempDir, "RoeSnip.app");
        Assert.Equal(bundle, UpdateInstallLayout.BundleOf(Path.Combine(bundle, "Contents", "MacOS", "RoeSnip")));
        Assert.Null(UpdateInstallLayout.BundleOf(Path.Combine(_tempDir, "publish", "RoeSnip")));
        Assert.Null(UpdateInstallLayout.BundleOf(null));
    }

    [Fact]
    public void DisposableLinuxSource_OnlyRoeSnipAppImagesThatAreNotTheTarget()
    {
        string target = Path.Combine(_tempDir, "RoeSnip", "RoeSnip.AppImage");
        Assert.True(UpdateInstallLayout.IsDisposableLinuxSource(Path.Combine(_tempDir, "RoeSnip-linux-x64.AppImage"), target));
        Assert.False(UpdateInstallLayout.IsDisposableLinuxSource(target, target));
        Assert.False(UpdateInstallLayout.IsDisposableLinuxSource(Path.Combine(_tempDir, "Other.AppImage"), target));
        Assert.False(UpdateInstallLayout.IsDisposableLinuxSource(Path.Combine(_tempDir, "RoeSnip-linux-x64.tar.gz"), target));
    }

    // ---------- .desktop / plist content ----------

    [Fact]
    public void MenuEntry_HasExecIconAndCaptureAction()
    {
        string content = DesktopIntegrationFiles.LinuxMenuEntry("/home/u/.local/share/RoeSnip/RoeSnip.AppImage", "2.6.0");
        Assert.StartsWith("[Desktop Entry]\n", content);
        Assert.Contains("\nType=Application\n", content);
        Assert.Contains("\nExec=\"/home/u/.local/share/RoeSnip/RoeSnip.AppImage\"\n", content);
        Assert.Contains("\nIcon=roesnip\n", content);
        Assert.Contains("\nActions=Capture;\n", content);
        Assert.Contains("\n[Desktop Action Capture]\nName=Capture\nExec=\"/home/u/.local/share/RoeSnip/RoeSnip.AppImage\" capture\n", content);
        Assert.Contains("\nX-AppImage-Version=2.6.0\n", content);
    }

    [Fact]
    public void QuoteExecArg_EscapesReservedCharacters()
    {
        Assert.Equal("\"/a b/RoeSnip.AppImage\"", DesktopIntegrationFiles.QuoteExecArg("/a b/RoeSnip.AppImage"));
        // '$' -> \$ inside the quotes, then the value-level backslash escape doubles it.
        Assert.Equal("\"/a\\\\$b\"", DesktopIntegrationFiles.QuoteExecArg("/a$b"));
        Assert.Equal("\"/a\\\\\"b\"", DesktopIntegrationFiles.QuoteExecArg("/a\"b"));
        Assert.Equal("\"/100%%\"", DesktopIntegrationFiles.QuoteExecArg("/100%"));
    }

    [Fact]
    public void AutostartEntry_EnabledUnlessHidden()
    {
        string content = DesktopIntegrationFiles.LinuxAutostartEntry("/x/RoeSnip.AppImage");
        Assert.Contains("\nExec=\"/x/RoeSnip.AppImage\"\n", content);
        Assert.Contains("\nX-GNOME-Autostart-enabled=true\n", content);
        Assert.True(DesktopIntegrationFiles.AutostartEntryEnabled(content));
        Assert.False(DesktopIntegrationFiles.AutostartEntryEnabled(content + "Hidden=true\n"));
        Assert.False(DesktopIntegrationFiles.AutostartEntryEnabled(content.Replace("X-GNOME-Autostart-enabled=true", "X-GNOME-Autostart-enabled=false")));
    }

    [Fact]
    public void LaunchAgent_RunsBundleExecutableAtLoad_XmlEscaped()
    {
        string content = DesktopIntegrationFiles.MacLaunchAgent("/Users/u/Applications/RoeSnip.app/Contents/MacOS/RoeSnip");
        Assert.Contains("<key>Label</key><string>net.roelite.roesnip</string>", content);
        Assert.Contains("<string>/Users/u/Applications/RoeSnip.app/Contents/MacOS/RoeSnip</string>", content);
        Assert.Contains("<key>RunAtLoad</key><true/>", content);
        Assert.DoesNotContain("KeepAlive", content);
        Assert.Contains("<string>/a&amp;b</string>", DesktopIntegrationFiles.MacLaunchAgent("/a&b"));
    }

    // ---------- swap / rollback ----------

    [Fact]
    public void SwapIn_File_KeepsOldAndPutsNewInPlace()
    {
        string target = Path.Combine(_tempDir, "RoeSnip.AppImage");
        File.WriteAllText(target, "v1");
        File.WriteAllText(target + ".new", "v2");
        File.WriteAllText(target + ".old", "stale");

        InstallSwap.SwapIn(target + ".new", target, target + ".old");

        Assert.Equal("v2", File.ReadAllText(target));
        Assert.Equal("v1", File.ReadAllText(target + ".old"));
        Assert.False(File.Exists(target + ".new"));
    }

    [Fact]
    public void SwapIn_File_NoPriorInstall()
    {
        string target = Path.Combine(_tempDir, "RoeSnip.AppImage");
        File.WriteAllText(target + ".new", "v2");

        InstallSwap.SwapIn(target + ".new", target, target + ".old");

        Assert.Equal("v2", File.ReadAllText(target));
        Assert.False(File.Exists(target + ".old"));
    }

    [Fact]
    public void SwapIn_Directory_SwapsBundle()
    {
        string target = Path.Combine(_tempDir, "RoeSnip.app");
        string staged = Path.Combine(_tempDir, ".RoeSnip-update", "RoeSnip.app");
        WriteBundle(target, "v1");
        WriteBundle(staged, "v2");
        WriteBundle(target + ".old", "stale");

        InstallSwap.SwapIn(staged, target, target + ".old");

        Assert.Equal("v2", ReadBundle(target));
        Assert.Equal("v1", ReadBundle(target + ".old"));
        Assert.False(Directory.Exists(staged));
    }

    [Fact]
    public void SwapIn_MissingStaged_Throws_AndLeavesTargetAlone()
    {
        string target = Path.Combine(_tempDir, "RoeSnip.AppImage");
        File.WriteAllText(target, "v1");

        Assert.Throws<FileNotFoundException>(() => InstallSwap.SwapIn(target + ".new", target, target + ".old"));
        Assert.Equal("v1", File.ReadAllText(target));
        Assert.False(File.Exists(target + ".old"));
    }

    [Fact]
    public void SwapIn_FailedMoveIn_RestoresPrevious()
    {
        // Target nested inside the staged dir: target -> old succeeds, then moving staged into its
        // own subtree fails, which must put the previous target back.
        string staged = Path.Combine(_tempDir, "stage");
        string target = Path.Combine(staged, "RoeSnip.app");
        string old = Path.Combine(_tempDir, "RoeSnip.app.old");
        WriteBundle(target, "v1");

        Assert.ThrowsAny<Exception>(() => InstallSwap.SwapIn(staged, target, old));

        Assert.Equal("v1", ReadBundle(target));
        Assert.False(Directory.Exists(old));
    }

    [Fact]
    public void RestorePrevious_PutsOldBack_AndParksBadAsOld()
    {
        string target = Path.Combine(_tempDir, "RoeSnip.AppImage");
        File.WriteAllText(target, "bad");
        File.WriteAllText(target + ".old", "good");

        InstallSwap.RestorePrevious(target, target + ".old", target + ".restore");

        Assert.Equal("good", File.ReadAllText(target));
        Assert.Equal("bad", File.ReadAllText(target + ".old"));
        Assert.False(File.Exists(target + ".restore"));
    }

    [Fact]
    public void RestorePrevious_Directory()
    {
        string target = Path.Combine(_tempDir, "RoeSnip.app");
        WriteBundle(target, "bad");
        WriteBundle(target + ".old", "good");

        InstallSwap.RestorePrevious(target, target + ".old", target + ".restore");

        Assert.Equal("good", ReadBundle(target));
        Assert.Equal("bad", ReadBundle(target + ".old"));
    }

    [Fact]
    public void SwapIn_File_OffWindows_ReplacesTargetInPlace()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // Windows keeps the two-rename swap (a running exe cannot be renamed over).
        }

        string target = Path.Combine(_tempDir, "RoeSnip.AppImage");
        File.WriteAllText(target, "v1");
        File.WriteAllText(target + ".new", "v2");
        // A reader holding the old file keeps its content: the rename replaces the directory entry.
        using FileStream held = File.OpenRead(target);

        InstallSwap.SwapIn(target + ".new", target, target + ".old");

        Assert.Equal("v2", File.ReadAllText(target));
        Assert.Equal("v1", File.ReadAllText(target + ".old"));
        Assert.Equal("v1", new StreamReader(held).ReadToEnd());
    }

    [Fact]
    public void RepairInterruptedSwap_RestoresOld_WhenTargetMissing()
    {
        string target = Path.Combine(_tempDir, "RoeSnip.app");
        WriteBundle(target + ".old", "v1");

        Assert.True(InstallSwap.RepairInterruptedSwap(target, target + ".old", target + ".restore"));
        Assert.Equal("v1", ReadBundle(target));
        Assert.False(Directory.Exists(target + ".old"));
    }

    [Fact]
    public void RepairInterruptedSwap_PrefersRestoreStaging()
    {
        // Crash inside RestorePrevious after target -> old: staging holds the good unit.
        string target = Path.Combine(_tempDir, "RoeSnip.app");
        WriteBundle(target + ".old", "bad");
        WriteBundle(target + ".restore", "good");

        Assert.True(InstallSwap.RepairInterruptedSwap(target, target + ".old", target + ".restore"));
        Assert.Equal("good", ReadBundle(target));
    }

    [Fact]
    public void RepairInterruptedSwap_CrashBeforeTargetToOld_PutsGoodBackAtOld()
    {
        // Crash inside RestorePrevious after old -> staging: target bad, staging good, old empty.
        string target = Path.Combine(_tempDir, "RoeSnip.AppImage");
        File.WriteAllText(target, "bad");
        File.WriteAllText(target + ".restore", "good");

        Assert.True(InstallSwap.RepairInterruptedSwap(target, target + ".old", target + ".restore"));
        Assert.Equal("bad", File.ReadAllText(target));
        Assert.Equal("good", File.ReadAllText(target + ".old"));
        Assert.False(File.Exists(target + ".restore"));

        // The rollback can then run as usual.
        InstallSwap.RestorePrevious(target, target + ".old", target + ".restore");
        Assert.Equal("good", File.ReadAllText(target));
    }

    [Fact]
    public void RepairInterruptedSwap_KeepsRestore_WhenOldAlsoPresent()
    {
        string target = Path.Combine(_tempDir, "RoeSnip.AppImage");
        File.WriteAllText(target, "v3");
        File.WriteAllText(target + ".old", "v2");
        File.WriteAllText(target + ".restore", "v1");

        Assert.False(InstallSwap.RepairInterruptedSwap(target, target + ".old", target + ".restore"));
        Assert.Equal("v1", File.ReadAllText(target + ".restore"));
    }

    [Fact]
    public void RestorePrevious_NeverDeletesAnOccupiedSlot()
    {
        string target = Path.Combine(_tempDir, "RoeSnip.AppImage");
        File.WriteAllText(target, "bad");
        File.WriteAllText(target + ".old", "older");
        File.WriteAllText(target + ".restore", "good");

        Assert.Throws<IOException>(() => InstallSwap.RestorePrevious(target, target + ".old", target + ".restore"));
        Assert.Equal("bad", File.ReadAllText(target));
        Assert.Equal("older", File.ReadAllText(target + ".old"));
        Assert.Equal("good", File.ReadAllText(target + ".restore"));
    }

    [Fact]
    public void RepairInterruptedSwap_NoOpWhenTargetPresentOrNothingToRestore()
    {
        string target = Path.Combine(_tempDir, "RoeSnip.app");
        Assert.False(InstallSwap.RepairInterruptedSwap(target, target + ".old", target + ".restore"));

        WriteBundle(target, "v2");
        WriteBundle(target + ".old", "v1");
        Assert.False(InstallSwap.RepairInterruptedSwap(target, target + ".old", target + ".restore"));
        Assert.Equal("v2", ReadBundle(target));
    }

    // ---------- asset names vs the real release.yml ----------

    [Fact]
    public void AssetNames_AreAllProducedByReleaseWorkflow()
    {
        // Comment lines dropped: a name only mentioned in the header comment is not produced.
        string yml = string.Join('\n', Array.FindAll(
            File.ReadAllLines(FindReleaseWorkflow()), line => !line.TrimStart().StartsWith('#')));

        var rids = new HashSet<string>(StringComparer.Ordinal);
        foreach (System.Text.RegularExpressions.Match m in
                 System.Text.RegularExpressions.Regex.Matches(yml, @"^\s*-\s*rid:\s*(\S+)\s*$", System.Text.RegularExpressions.RegexOptions.Multiline))
        {
            rids.Add(m.Groups[1].Value);
        }
        Assert.NotEmpty(rids);

        // Every RoeSnip(App)-<rid><ext> token, literal or templated on the matrix rid.
        var produced = new HashSet<string>(StringComparer.Ordinal);
        foreach (System.Text.RegularExpressions.Match m in
                 System.Text.RegularExpressions.Regex.Matches(yml, @"(RoeSnip(?:App)?-)(\$\{\{ matrix\.rid \}\}|[a-z]+-[a-z0-9]+)((?:\.[A-Za-z]+)+)"))
        {
            string prefix = m.Groups[1].Value, ext = m.Groups[3].Value;
            if (m.Groups[2].Value.StartsWith("$", StringComparison.Ordinal))
            {
                foreach (string rid in rids)
                {
                    produced.Add(prefix + rid + ext);
                }
            }
            else
            {
                produced.Add(prefix + m.Groups[2].Value + ext);
            }
        }

        foreach (UpdatePlatform platform in Enum.GetValues<UpdatePlatform>())
        {
            foreach (Architecture arch in new[] { Architecture.X64, Architecture.Arm64 })
            {
                UpdateAssetSelection s = UpdateAssetNames.For(platform, arch)!;
                Assert.Contains(s.Primary, produced);
                if (s.Gz is not null)
                {
                    Assert.Contains(s.Gz, produced);
                }
            }
        }
    }

    // Walks up from the test output dir to the repo root holding .github/workflows/release.yml.
    private static string FindReleaseWorkflow()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, ".github", "workflows", "release.yml");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        throw new FileNotFoundException("release.yml not found above " + AppContext.BaseDirectory);
    }

    private static void WriteBundle(string bundle, string version)
    {
        string macOs = Path.Combine(bundle, "Contents", "MacOS");
        Directory.CreateDirectory(macOs);
        File.WriteAllText(Path.Combine(macOs, "RoeSnip"), version);
    }

    private static string ReadBundle(string bundle) =>
        File.ReadAllText(Path.Combine(bundle, "Contents", "MacOS", "RoeSnip"));
}
