using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using RoeSnip;
using RoeSnip.Sharing;
using Xunit;

namespace RoeSnip.Tests.Sharing;

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
        var settings = RoeSnipSettings.Default;
        Assert.Null(ShareManager.ResolveDefault(settings));
    }

    [Fact]
    public void ResolveDefault_OneEnabledProvider_NoExplicitDefault_ReturnsIt()
    {
        var settings = RoeSnipSettings.Default with { ShareProviders = new List<ShareProviderConfig> { ConfiguredRoeShare } };
        var resolved = ShareManager.ResolveDefault(settings);
        Assert.NotNull(resolved);
        Assert.Equal("roeshare", resolved!.Id);
    }

    [Fact]
    public void ResolveDefault_ExplicitDefaultId_Wins()
    {
        var settings = RoeSnipSettings.Default with
        {
            ShareProviders = new List<ShareProviderConfig> { ConfiguredRoeShare, ConfiguredImgur },
            DefaultShareProviderId = "imgur",
        };
        var resolved = ShareManager.ResolveDefault(settings);
        Assert.Equal("imgur", resolved!.Id);
    }

    [Fact]
    public void ResolveDefault_ExplicitDefaultId_ButDisabled_FallsBackToFirstEnabled()
    {
        var disabledImgur = ConfiguredImgur with { Enabled = false };
        var settings = RoeSnipSettings.Default with
        {
            ShareProviders = new List<ShareProviderConfig> { ConfiguredRoeShare, disabledImgur },
            DefaultShareProviderId = "imgur",
        };
        var resolved = ShareManager.ResolveDefault(settings);
        Assert.Equal("roeshare", resolved!.Id);
    }

    [Fact]
    public void ResolveDefault_ExplicitDefaultId_UnknownId_FallsBackToFirstEnabled()
    {
        var settings = RoeSnipSettings.Default with
        {
            ShareProviders = new List<ShareProviderConfig> { ConfiguredRoeShare },
            DefaultShareProviderId = "not-configured",
        };
        var resolved = ShareManager.ResolveDefault(settings);
        Assert.Equal("roeshare", resolved!.Id);
    }

    [Fact]
    public void ResolveDefault_NothingEnabled_ReturnsNullEvenIfConfigured()
    {
        var settings = RoeSnipSettings.Default with
        {
            ShareProviders = new List<ShareProviderConfig> { ConfiguredRoeShare with { Enabled = false } },
        };
        Assert.Null(ShareManager.ResolveDefault(settings));
    }

    // ---------- EffectiveConfigs passthrough ----------

    [Fact]
    public void EffectiveConfigs_DelegatesToShareProviderCatalog()
    {
        var settings = RoeSnipSettings.Default with { ShareProviders = new List<ShareProviderConfig> { ConfiguredRoeShare } };
        var effective = ShareManager.EffectiveConfigs(settings);
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
