using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using RoeSnip.Core.Sharing;
using Xunit;

namespace RoeSnip.Core.Tests.Sharing;

public class ShareManagerTests
{
    private static readonly ShareProviderConfig ConfiguredRoeShare = new()
    {
        Id = "roeshare",
        SpecId = "roeshare",
        DisplayName = "My RoeShare",
        Enabled = true,
        Values = new Dictionary<string, string> { ["BaseUrl"] = "https://share.example.com", ["ApiKey"] = "rsk_x" },
    };

    private static readonly ShareProviderConfig ConfiguredImgur = new()
    {
        Id = "imgur",
        SpecId = "imgur",
        DisplayName = "My Imgur",
        Enabled = true,
        Values = new Dictionary<string, string> { ["ApiKey"] = "clientid" },
    };

    // ---------- ResolveDefault ----------

    [Fact]
    public void ResolveDefault_NoProvidersConfigured_ReturnsNull()
    {
        Assert.Null(ShareManager.ResolveDefault(new List<ShareProviderConfig>(), defaultProviderId: null));
    }

    [Fact]
    public void ResolveDefault_OneEnabledProvider_NoExplicitDefault_ReturnsIt()
    {
        var resolved = ShareManager.ResolveDefault(new List<ShareProviderConfig> { ConfiguredRoeShare }, defaultProviderId: null);
        Assert.NotNull(resolved);
        Assert.Equal("roeshare", resolved!.Id);
    }

    [Fact]
    public void ResolveDefault_ExplicitDefaultId_Wins()
    {
        var resolved = ShareManager.ResolveDefault(
            new List<ShareProviderConfig> { ConfiguredRoeShare, ConfiguredImgur }, defaultProviderId: "imgur");
        Assert.Equal("imgur", resolved!.Id);
    }

    [Fact]
    public void ResolveDefault_ExplicitDefaultId_ButDisabled_FallsBackToFirstEnabled()
    {
        var disabledImgur = ConfiguredImgur with { Enabled = false };
        var resolved = ShareManager.ResolveDefault(
            new List<ShareProviderConfig> { ConfiguredRoeShare, disabledImgur }, defaultProviderId: "imgur");
        Assert.Equal("roeshare", resolved!.Id);
    }

    [Fact]
    public void ResolveDefault_ExplicitDefaultId_UnknownId_FallsBackToFirstEnabled()
    {
        var resolved = ShareManager.ResolveDefault(
            new List<ShareProviderConfig> { ConfiguredRoeShare }, defaultProviderId: "not-configured");
        Assert.Equal("roeshare", resolved!.Id);
    }

    [Fact]
    public void ResolveDefault_NothingEnabled_ReturnsNullEvenIfConfigured()
    {
        var resolved = ShareManager.ResolveDefault(
            new List<ShareProviderConfig> { ConfiguredRoeShare with { Enabled = false } }, defaultProviderId: null);
        Assert.Null(resolved);
    }

    // ---------- EffectiveConfigs passthrough ----------

    [Fact]
    public void EffectiveConfigs_DelegatesToShareProviderCatalog()
    {
        var effective = ShareManager.EffectiveConfigs(new List<ShareProviderConfig> { ConfiguredRoeShare });
        Assert.Contains(effective, c => c.Id == "roeshare" && c.Enabled);
        Assert.Equal(ShareProviderCatalog.BuiltIns.Count, effective.Count);
    }

    // ---------- UploadAsync ----------

    [Fact]
    public async Task UploadAsync_UnknownSpec_FailsWithoutNetworkCall()
    {
        var handler = StubHttpMessageHandler.ReturningText(HttpStatusCode.OK, "unused");
        var config = new ShareProviderConfig { Id = "ghost", SpecId = "does-not-exist" };

        var result = await ShareManager.UploadAsync(config, new MemoryStream(new byte[] { 1 }), "x.png", "image/png", CancellationToken.None, handler.ToClient());

        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
        Assert.Null(handler.LastRequest);
    }

    [Fact]
    public async Task UploadAsync_OversizedContent_FailsFastWithoutNetworkCall()
    {
        var handler = StubHttpMessageHandler.ReturningText(HttpStatusCode.OK, "unused");
        // Imgur's built-in spec declares a 20 MB ceiling.
        var tooLarge = new MemoryStream(new byte[21 * 1024 * 1024]);

        var result = await ShareManager.UploadAsync(ConfiguredImgur, tooLarge, "big.png", "image/png", CancellationToken.None, handler.ToClient());

        Assert.False(result.Success);
        Assert.Contains("20", result.ErrorMessage);
        Assert.Null(handler.LastRequest);
    }

    [Fact]
    public async Task UploadAsync_WithinSizeLimit_ProceedsToNetworkCall()
    {
        var handler = StubHttpMessageHandler.ReturningText(HttpStatusCode.OK, """{"data":{"link":"https://imgur.com/x"}}""");
        var small = new MemoryStream(new byte[1024]);

        var result = await ShareManager.UploadAsync(ConfiguredImgur, small, "small.png", "image/png", CancellationToken.None, handler.ToClient());

        Assert.True(result.Success);
        Assert.Equal("https://imgur.com/x", result.Url);
        Assert.NotNull(handler.LastRequest);
    }

    [Fact]
    public async Task UploadAsync_NoMaxSizeDeclared_NeverShortCircuits()
    {
        // RoeShare declares no MaxUploadBytes (server-configured) - any size should reach the network.
        var handler = StubHttpMessageHandler.ReturningText(HttpStatusCode.Created, """{"url":"https://share.example.com/x"}""");
        var content = new MemoryStream(new byte[5 * 1024 * 1024]);

        var result = await ShareManager.UploadAsync(ConfiguredRoeShare, content, "big.png", "image/png", CancellationToken.None, handler.ToClient());

        Assert.True(result.Success);
        Assert.NotNull(handler.LastRequest);
    }

    [Fact]
    public async Task UploadAsync_UnexpectedException_IsCaughtAndReturnedAsFailure()
    {
        var handler = StubHttpMessageHandler.Throwing(new InvalidOperationException("boom"));
        var result = await ShareManager.UploadAsync(ConfiguredImgur, new MemoryStream(new byte[] { 1 }), "x.png", "image/png", CancellationToken.None, handler.ToClient());

        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
    }
}
