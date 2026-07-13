using System;
using System.Text.Json;
using RoeSnip.App.AppShell;
using Xunit;

namespace RoeSnip.App.Tests;

/// <summary>The pure, network-free half of the self-updater (item 13b/13d):
/// <see cref="UpdateManager.ParseUpdateInfo"/> parses one GitHub "releases/latest" JSON response
/// against a current version and a "does this OS need a matching Windows asset" flag, with no HTTP
/// call and no OS dependency of its own (<c>requireWindowsAsset</c> is an explicit parameter for
/// exactly this reason — both the Windows and the Linux/macOS-passive-notice code paths are
/// testable from this one Windows-hosted test project). The install/swap/registry mechanics are
/// reviewed by eye, not unit-tested, matching the WPF reference's own UpdateManager.cs doc
/// comment.</summary>
public class UpdateManagerTests
{
    private static readonly Version V100 = new(1, 0, 0);

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    private static string ReleaseJson(string tag, string? htmlUrl = "https://github.com/roe69/roesnip/releases/tag/" + "TAG",
        string? windowsAssetName = "RoeSnipApp-win-x64.exe", string windowsAssetUrl = "https://example.invalid/RoeSnipApp-win-x64.exe",
        bool includeAssets = true)
    {
        string windowsAssetEntry = windowsAssetName is null
            ? ""
            : $"{{\"name\":\"{windowsAssetName}\",\"browser_download_url\":\"{windowsAssetUrl}\"}},";
        string assetsJson = includeAssets
            ? $"[{windowsAssetEntry}{{\"name\":\"RoeSnip-linux-x64.AppImage\",\"browser_download_url\":\"https://example.invalid/linux\"}}]"
            : "null";
        string htmlUrlJson = htmlUrl is null ? "" : $"\"html_url\":\"{htmlUrl.Replace("TAG", tag)}\",";
        return $"{{{htmlUrlJson}\"tag_name\":\"{tag}\",\"assets\":{assetsJson}}}";
    }

    // ---------- version gating ----------

    [Fact]
    public void ParseUpdateInfo_NewerVersion_ReturnsUpdateInfo()
    {
        var info = UpdateManager.ParseUpdateInfo(Parse(ReleaseJson("v1.2.0")), V100, requireWindowsAsset: true);
        Assert.NotNull(info);
        Assert.Equal(new Version(1, 2, 0), info!.Version);
    }

    [Fact]
    public void ParseUpdateInfo_SameVersion_ReturnsNull()
    {
        Assert.Null(UpdateManager.ParseUpdateInfo(Parse(ReleaseJson("v1.0.0")), V100, requireWindowsAsset: true));
    }

    [Fact]
    public void ParseUpdateInfo_OlderVersion_ReturnsNull()
    {
        Assert.Null(UpdateManager.ParseUpdateInfo(Parse(ReleaseJson("v0.9.0")), V100, requireWindowsAsset: true));
    }

    [Theory]
    [InlineData("v1.5.0")]
    [InlineData("V1.5.0")]
    [InlineData("1.5.0")]
    public void ParseUpdateInfo_AcceptsLeadingVOrBare(string tag)
    {
        var info = UpdateManager.ParseUpdateInfo(Parse(ReleaseJson(tag)), V100, requireWindowsAsset: true);
        Assert.NotNull(info);
        Assert.Equal(new Version(1, 5, 0), info!.Version);
    }

    [Fact]
    public void ParseUpdateInfo_MissingTagName_ReturnsNull()
    {
        Assert.Null(UpdateManager.ParseUpdateInfo(Parse("{\"assets\":[]}"), V100, requireWindowsAsset: true));
    }

    [Fact]
    public void ParseUpdateInfo_UnparseableTag_ReturnsNull()
    {
        Assert.Null(UpdateManager.ParseUpdateInfo(Parse(ReleaseJson("not-a-version")), V100, requireWindowsAsset: true));
    }

    // ---------- Windows asset gating ----------

    [Fact]
    public void ParseUpdateInfo_RequireWindowsAsset_MissingAsset_ReturnsNull()
    {
        Assert.Null(UpdateManager.ParseUpdateInfo(
            Parse(ReleaseJson("v1.2.0", windowsAssetName: null)), V100, requireWindowsAsset: true));
    }

    [Fact]
    public void ParseUpdateInfo_RequireWindowsAsset_PresentAsset_PopulatesDownloadUrl()
    {
        var info = UpdateManager.ParseUpdateInfo(Parse(ReleaseJson("v1.2.0")), V100, requireWindowsAsset: true);
        Assert.NotNull(info);
        Assert.Equal("https://example.invalid/RoeSnipApp-win-x64.exe", info!.DownloadUrl);
    }

    [Fact]
    public void ParseUpdateInfo_NotRequireWindowsAsset_MissingAsset_StillReturnsUpdateInfo()
    {
        // Linux/macOS passive notice (item 13d): a newer release with no Windows asset (or none at
        // all, e.g. only .AppImage/.dmg published so far) still deserves a notice - this OS never
        // reads DownloadUrl, only Version/ReleaseUrl.
        var info = UpdateManager.ParseUpdateInfo(
            Parse(ReleaseJson("v1.2.0", windowsAssetName: null)), V100, requireWindowsAsset: false);
        Assert.NotNull(info);
        Assert.Null(info!.DownloadUrl);
    }

    [Fact]
    public void ParseUpdateInfo_AssetNameMatchIsCaseInsensitive()
    {
        var info = UpdateManager.ParseUpdateInfo(
            Parse(ReleaseJson("v1.2.0", windowsAssetName: "ROESNIPAPP-WIN-X64.EXE")), V100, requireWindowsAsset: true);
        Assert.NotNull(info);
        Assert.Equal("https://example.invalid/RoeSnipApp-win-x64.exe", info!.DownloadUrl);
    }

    [Fact]
    public void ParseUpdateInfo_DifferentlyNamedAsset_NotMatched()
    {
        // The WPF app's own asset is "RoeSnip.exe" - a release that only has THAT asset (no
        // "RoeSnipApp-win-x64.exe") must never be treated as actionable here; downloading it would
        // silently swap this app's install for the wrong product's binary.
        Assert.Null(UpdateManager.ParseUpdateInfo(
            Parse(ReleaseJson("v1.2.0", windowsAssetName: "RoeSnip.exe")), V100, requireWindowsAsset: true));
    }

    // ---------- release URL ----------

    [Fact]
    public void ParseUpdateInfo_UsesHtmlUrlWhenPresent()
    {
        var info = UpdateManager.ParseUpdateInfo(
            Parse(ReleaseJson("v1.2.0", htmlUrl: "https://github.com/roe69/roesnip/releases/tag/v1.2.0")),
            V100, requireWindowsAsset: false);
        Assert.NotNull(info);
        Assert.Equal("https://github.com/roe69/roesnip/releases/tag/v1.2.0", info!.ReleaseUrl);
    }

    [Fact]
    public void ParseUpdateInfo_FallsBackToConstructedReleaseUrlWhenHtmlUrlMissing()
    {
        var info = UpdateManager.ParseUpdateInfo(Parse(ReleaseJson("v1.2.0", htmlUrl: null)), V100, requireWindowsAsset: false);
        Assert.NotNull(info);
        Assert.Equal("https://github.com/roe69/roesnip/releases/tag/v1.2.0", info!.ReleaseUrl);
    }

    // ---------- identity constants ----------

    [Fact]
    public void InstallDir_DiffersFromWpfAppsInstallDir()
    {
        // Both products can be installed side by side (docs/PARITY.md) - the directory names must
        // never collide.
        string localAppData = System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData);
        Assert.Equal(System.IO.Path.Combine(localAppData, "RoeSnip.App"), UpdateManager.InstallDir);
        Assert.NotEqual(System.IO.Path.Combine(localAppData, "RoeSnip"), UpdateManager.InstallDir);
    }

    [Fact]
    public void CurrentVersionText_HasNoRevisionComponent()
    {
        // "x.y.z", never "x.y.z.0" - see CurrentVersionText's own doc comment.
        string text = UpdateManager.CurrentVersionText;
        Assert.Equal(2, text.Split('.').Length - 1);
    }
}
