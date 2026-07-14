using System;
using System.Text.Json;
using RoeSnip.App;
using Xunit;

namespace RoeSnip.Tests;

/// <summary>The pure, network-free half of the self-updater: <see cref="UpdateManager.ParsePayload"/>
/// parses one GitHub "releases/latest" JSON response against a current version, with no HTTP call
/// and no dependency on the running assembly's own version (<paramref name="currentVersion"/> is an
/// explicit parameter for exactly that reason). Ported from the Avalonia twin's
/// UpdateManagerTests (tests/RoeSnip.App.Tests) - the WPF app has no requireWindowsAsset parameter
/// (its asset name, "RoeSnip.exe", is a hardcoded constant here) and no ReleaseUrl fallback, so
/// those cases are dropped. The install/swap/registry mechanics are reviewed by eye, not
/// unit-tested, matching UpdateManager.cs's own doc comment.</summary>
public class UpdateManagerTests
{
    private static readonly Version V100 = new(1, 0, 0);

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    private static string ReleaseJson(string tag, string? assetName = "RoeSnip.exe",
        string assetUrl = "https://example.invalid/RoeSnip.exe", bool includeAssets = true,
        string? digest = null, long? size = null,
        string? gzAssetName = null, string gzAssetUrl = "https://example.invalid/RoeSnip.exe.gz",
        string? gzDigest = null, long? gzSize = null)
    {
        string digestJson = digest is null ? "" : $",\"digest\":\"{digest}\"";
        string sizeJson = size is null ? "" : $",\"size\":{size}";
        string assetEntry = assetName is null
            ? ""
            : $"{{\"name\":\"{assetName}\",\"browser_download_url\":\"{assetUrl}\"{digestJson}{sizeJson}}},";
        string gzDigestJson = gzDigest is null ? "" : $",\"digest\":\"{gzDigest}\"";
        string gzSizeJson = gzSize is null ? "" : $",\"size\":{gzSize}";
        string gzAssetEntry = gzAssetName is null
            ? ""
            : $"{{\"name\":\"{gzAssetName}\",\"browser_download_url\":\"{gzAssetUrl}\"{gzDigestJson}{gzSizeJson}}},";
        string assetsJson = includeAssets
            ? $"[{assetEntry}{gzAssetEntry}{{\"name\":\"RoeSnipApp-win-x64.exe\",\"browser_download_url\":\"https://example.invalid/other\"}}]"
            : "null";
        return $"{{\"tag_name\":\"{tag}\",\"assets\":{assetsJson}}}";
    }

    // ---------- version gating ----------

    [Fact]
    public void ParsePayload_NewerVersion_ReturnsUpdateInfo()
    {
        var info = UpdateManager.ParsePayload(Parse(ReleaseJson("v1.2.0")), V100);
        Assert.NotNull(info);
        Assert.Equal(new Version(1, 2, 0), info!.Version);
    }

    [Fact]
    public void ParsePayload_SameVersion_ReturnsNull()
    {
        Assert.Null(UpdateManager.ParsePayload(Parse(ReleaseJson("v1.0.0")), V100));
    }

    [Fact]
    public void ParsePayload_OlderVersion_ReturnsNull()
    {
        Assert.Null(UpdateManager.ParsePayload(Parse(ReleaseJson("v0.9.0")), V100));
    }

    [Theory]
    [InlineData("v1.5.0")]
    [InlineData("V1.5.0")]
    [InlineData("1.5.0")]
    public void ParsePayload_AcceptsLeadingVOrBare(string tag)
    {
        var info = UpdateManager.ParsePayload(Parse(ReleaseJson(tag)), V100);
        Assert.NotNull(info);
        Assert.Equal(new Version(1, 5, 0), info!.Version);
    }

    [Fact]
    public void ParsePayload_MissingTagName_ReturnsNull()
    {
        Assert.Null(UpdateManager.ParsePayload(Parse("{\"assets\":[]}"), V100));
    }

    [Fact]
    public void ParsePayload_UnparseableTag_ReturnsNull()
    {
        Assert.Null(UpdateManager.ParsePayload(Parse(ReleaseJson("not-a-version")), V100));
    }

    // ---------- asset gating ----------

    [Fact]
    public void ParsePayload_MissingAsset_ReturnsNull()
    {
        Assert.Null(UpdateManager.ParsePayload(Parse(ReleaseJson("v1.2.0", assetName: null)), V100));
    }

    [Fact]
    public void ParsePayload_PresentAsset_PopulatesDownloadUrl()
    {
        var info = UpdateManager.ParsePayload(Parse(ReleaseJson("v1.2.0")), V100);
        Assert.NotNull(info);
        Assert.Equal("https://example.invalid/RoeSnip.exe", info!.DownloadUrl);
    }

    [Fact]
    public void ParsePayload_AssetNameMatchIsCaseInsensitive()
    {
        var info = UpdateManager.ParsePayload(Parse(ReleaseJson("v1.2.0", assetName: "ROESNIP.EXE")), V100);
        Assert.NotNull(info);
        Assert.Equal("https://example.invalid/RoeSnip.exe", info!.DownloadUrl);
    }

    [Fact]
    public void ParsePayload_DifferentlyNamedAsset_NotMatched()
    {
        // The Avalonia app's own asset is "RoeSnipApp-win-x64.exe" - a release that only has THAT
        // asset (no "RoeSnip.exe") must never be treated as actionable here; downloading it would
        // silently swap this app's install for the wrong product's binary.
        Assert.Null(UpdateManager.ParsePayload(
            Parse(ReleaseJson("v1.2.0", assetName: "RoeSnipApp-win-x64.exe")), V100));
    }

    [Fact]
    public void ParsePayload_NoAssetsArray_ReturnsNull()
    {
        Assert.Null(UpdateManager.ParsePayload(Parse(ReleaseJson("v1.2.0", includeAssets: false)), V100));
    }

    // ---------- digest / size capture ----------

    [Fact]
    public void ParsePayload_CapturesDigestAndSize()
    {
        string hex = new string('a', 64);
        var info = UpdateManager.ParsePayload(
            Parse(ReleaseJson("v1.2.0", digest: $"sha256:{hex}", size: 123456)), V100);
        Assert.NotNull(info);
        Assert.Equal($"sha256:{hex}", info!.Digest);
        Assert.Equal(123456, info.Size);
    }

    [Fact]
    public void ParsePayload_NoDigestOrSizeInPayload_LeavesThemNull()
    {
        var info = UpdateManager.ParsePayload(Parse(ReleaseJson("v1.2.0")), V100);
        Assert.NotNull(info);
        Assert.Null(info!.Digest);
        Assert.Null(info.Size);
    }

    // ---------- gzip transit asset (hardening item 9) ----------

    [Fact]
    public void ParsePayload_PrefersGzAssetWhenBothPresent()
    {
        var info = UpdateManager.ParsePayload(
            Parse(ReleaseJson("v1.2.0", gzAssetName: "RoeSnip.exe.gz")), V100);
        Assert.NotNull(info);
        Assert.True(info!.IsGzip);
        Assert.Equal("https://example.invalid/RoeSnip.exe.gz", info.DownloadUrl);
    }

    [Fact]
    public void ParsePayload_FallsBackToPlainAssetWhenGzAbsent()
    {
        // Protects against a release.yml slip that only publishes the plain asset.
        var info = UpdateManager.ParsePayload(Parse(ReleaseJson("v1.2.0")), V100);
        Assert.NotNull(info);
        Assert.False(info!.IsGzip);
        Assert.Equal("https://example.invalid/RoeSnip.exe", info.DownloadUrl);
    }

    [Fact]
    public void ParsePayload_GzAssetCarriesItsOwnDigestAndSize()
    {
        string plainHex = new string('a', 64);
        string gzHex = new string('b', 64);
        var info = UpdateManager.ParsePayload(
            Parse(ReleaseJson("v1.2.0", digest: $"sha256:{plainHex}", size: 1000,
                gzAssetName: "RoeSnip.exe.gz", gzDigest: $"sha256:{gzHex}", gzSize: 400)),
            V100);
        Assert.NotNull(info);
        Assert.True(info!.IsGzip);
        Assert.Equal($"sha256:{gzHex}", info.Digest);
        Assert.Equal(400, info.Size);
    }

    [Fact]
    public void ParsePayload_GzAssetNameMatchIsCaseInsensitive()
    {
        var info = UpdateManager.ParsePayload(
            Parse(ReleaseJson("v1.2.0", gzAssetName: "ROESNIP.EXE.GZ")), V100);
        Assert.NotNull(info);
        Assert.True(info!.IsGzip);
    }
}
