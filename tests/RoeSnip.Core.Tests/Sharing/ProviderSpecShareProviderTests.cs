using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using RoeSnip.Core.Sharing;
using Xunit;

namespace RoeSnip.Core.Tests.Sharing;

/// <summary>Exercises ProviderSpecShareProvider end-to-end against a StubHttpMessageHandler - real
/// spec objects (several lifted straight from ShareProviderCatalog's own built-ins), zero network
/// access. Covers both ShareUploadKind shapes and the omit-if-empty templating rule that lets a
/// single spec serve both "credential filled in" and "anonymous" cases (catbox's userhash, Imgur's
/// Client ID).</summary>
public class ProviderSpecShareProviderTests
{
    // A fresh MemoryStream per use (not a single shared instance): ShareUploadRequest.Content is a
    // Stream now (see that record's own doc comment for why), and a Stream is single-read/positional
    // state - reusing one instance across tests that each read it to completion would silently hand
    // every test after the first an already-exhausted, zero-length stream.
    private static ShareUploadRequest SamplePng() => new(new MemoryStream(new byte[] { 1, 2, 3, 4 }), "shot.png", "image/png");

    [Fact]
    public async Task Upload_RawBody_SendsFileBytesAsBodyAndTemplatesHeaders()
    {
        var handler = StubHttpMessageHandler.ReturningText(HttpStatusCode.Created, """{"id":"abc","url":"https://share.example.com/abc"}""");
        var config = new ShareProviderConfig
        {
            Id = "roeshare-1",
            SpecId = "roeshare",
            Values = new Dictionary<string, string> { ["BaseUrl"] = "https://share.example.com", ["ApiKey"] = "rsk_test" },
        };
        var provider = new ProviderSpecShareProvider(ShareProviderCatalog.RoeShare, config, handler.ToClient());

        ShareUploadResult result = await provider.UploadAsync(SamplePng(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("https://share.example.com/abc", result.Url);
        Assert.Equal("https://share.example.com/api/v1/upload", handler.LastRequest!.RequestUri!.ToString());
        Assert.Equal(HttpMethod.Post, handler.LastRequest.Method);
        Assert.Equal("Bearer rsk_test", handler.LastRequest.Headers.GetValues("Authorization").First());
        Assert.Equal("shot.png", handler.LastRequest.Headers.GetValues("X-Filename").First());
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, handler.LastRequestBytes);
    }

    [Fact]
    public async Task Upload_Multipart_IncludesFileFieldAndConstantExtraFields()
    {
        var handler = StubHttpMessageHandler.ReturningText(HttpStatusCode.OK, "https://catbox.moe/abc.png");
        var config = new ShareProviderConfig { Id = "catbox-1", SpecId = "catbox" }; // no userhash - anonymous

        var provider = new ProviderSpecShareProvider(ShareProviderCatalog.Catbox, config, handler.ToClient());
        ShareUploadResult result = await provider.UploadAsync(SamplePng(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("https://catbox.moe/abc.png", result.Url);
        Assert.Contains("name=reqtype", handler.LastRequestBody);
        Assert.Contains("fileupload", handler.LastRequestBody);
        Assert.Contains("name=fileToUpload", handler.LastRequestBody);
        Assert.Contains("shot.png", handler.LastRequestBody);
        // Anonymous: userhash was never filled in, so the field is omitted entirely rather than
        // sent as an empty value (this IS what catbox's own docs mean by "anonymous").
        Assert.DoesNotContain("name=userhash", handler.LastRequestBody);
    }

    [Fact]
    public async Task Upload_Multipart_FilledOptionalField_IsIncluded()
    {
        var handler = StubHttpMessageHandler.ReturningText(HttpStatusCode.OK, "https://catbox.moe/abc.png");
        var config = new ShareProviderConfig
        {
            Id = "catbox-1",
            SpecId = "catbox",
            Values = new Dictionary<string, string> { ["ApiKey"] = "myuserhash" },
        };

        var provider = new ProviderSpecShareProvider(ShareProviderCatalog.Catbox, config, handler.ToClient());
        await provider.UploadAsync(SamplePng(), CancellationToken.None);

        Assert.Contains("name=userhash", handler.LastRequestBody);
        Assert.Contains("myuserhash", handler.LastRequestBody);
    }

    [Fact]
    public async Task Upload_Header_OmittedWhenTemplateExpandsEmpty()
    {
        var handler = StubHttpMessageHandler.ReturningText(HttpStatusCode.OK, """{"data":{"link":"https://imgur.com/x"}}""");
        var config = new ShareProviderConfig { Id = "imgur-1", SpecId = "imgur" }; // no Client ID configured

        var provider = new ProviderSpecShareProvider(ShareProviderCatalog.Imgur, config, handler.ToClient());
        await provider.UploadAsync(SamplePng(), CancellationToken.None);

        Assert.False(handler.LastRequest!.Headers.Contains("Authorization"));
    }

    [Fact]
    public async Task Upload_Header_PresentWhenConfigured()
    {
        var handler = StubHttpMessageHandler.ReturningText(HttpStatusCode.OK, """{"data":{"link":"https://imgur.com/x"}}""");
        var config = new ShareProviderConfig
        {
            Id = "imgur-1",
            SpecId = "imgur",
            Values = new Dictionary<string, string> { ["ApiKey"] = "myclientid" },
        };

        var provider = new ProviderSpecShareProvider(ShareProviderCatalog.Imgur, config, handler.ToClient());
        await provider.UploadAsync(SamplePng(), CancellationToken.None);

        Assert.Equal("Client-ID myclientid", handler.LastRequest!.Headers.GetValues("Authorization").First());
    }

    [Fact]
    public async Task Upload_NonSuccessStatus_ReturnsFailureWithBodyExcerpt()
    {
        var handler = StubHttpMessageHandler.ReturningText(HttpStatusCode.Forbidden, "invalid client id");
        var config = new ShareProviderConfig { Id = "imgur-1", SpecId = "imgur" };
        var provider = new ProviderSpecShareProvider(ShareProviderCatalog.Imgur, config, handler.ToClient());

        ShareUploadResult result = await provider.UploadAsync(SamplePng(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Null(result.Url);
        Assert.Equal(403, result.HttpStatusCode);
        Assert.Contains("403", result.ErrorMessage);
        Assert.Contains("invalid client id", result.ErrorMessage);
    }

    [Fact]
    public async Task Upload_SuccessStatus_ButUnparseableResponse_ReturnsFailure()
    {
        var handler = StubHttpMessageHandler.ReturningText(HttpStatusCode.OK, "not json");
        var config = new ShareProviderConfig { Id = "imgur-1", SpecId = "imgur" };
        var provider = new ProviderSpecShareProvider(ShareProviderCatalog.Imgur, config, handler.ToClient());

        ShareUploadResult result = await provider.UploadAsync(SamplePng(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Null(result.Url);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public async Task Upload_InvalidEndpointTemplate_FailsWithoutThrowing()
    {
        // RoeShare's BaseUrl was never configured -> the endpoint template expands to "/api/v1/upload"
        // (empty BaseUrl), which is not an absolute URL.
        var handler = StubHttpMessageHandler.ReturningText(HttpStatusCode.OK, "irrelevant");
        var config = new ShareProviderConfig { Id = "roeshare-1", SpecId = "roeshare" };
        var provider = new ProviderSpecShareProvider(ShareProviderCatalog.RoeShare, config, handler.ToClient());

        ShareUploadResult result = await provider.UploadAsync(SamplePng(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Null(result.Url);
        Assert.NotNull(result.ErrorMessage);
        Assert.Null(handler.LastRequest); // never even attempted the network call
    }

    [Fact]
    public async Task Upload_NetworkFailure_ReturnsFailureResult()
    {
        var handler = StubHttpMessageHandler.Throwing(new HttpRequestException("connection refused"));
        var config = new ShareProviderConfig
        {
            Id = "roeshare-1",
            SpecId = "roeshare",
            Values = new Dictionary<string, string> { ["BaseUrl"] = "https://share.example.com", ["ApiKey"] = "x" },
        };
        var provider = new ProviderSpecShareProvider(ShareProviderCatalog.RoeShare, config, handler.ToClient());

        ShareUploadResult result = await provider.UploadAsync(SamplePng(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("connection refused", result.ErrorMessage);
    }

    [Fact]
    public async Task Upload_Cancellation_PropagatesAsOperationCanceled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var handler = StubHttpMessageHandler.ReturningText(HttpStatusCode.OK, "irrelevant");
        var config = new ShareProviderConfig
        {
            Id = "roeshare-1",
            SpecId = "roeshare",
            Values = new Dictionary<string, string> { ["BaseUrl"] = "https://share.example.com", ["ApiKey"] = "x" },
        };
        var provider = new ProviderSpecShareProvider(ShareProviderCatalog.RoeShare, config, handler.ToClient());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => provider.UploadAsync(SamplePng(), cts.Token));
    }

    [Fact]
    public async Task Upload_PlainBodyResponse_ExtractsWholeTrimmedBody()
    {
        var handler = StubHttpMessageHandler.ReturningText(HttpStatusCode.OK, "  https://0x0.st/abcd.png\n");
        var config = new ShareProviderConfig { Id = "0x0st-1", SpecId = "0x0st" };
        var provider = new ProviderSpecShareProvider(ShareProviderCatalog.ZeroXZeroSt, config, handler.ToClient());

        ShareUploadResult result = await provider.UploadAsync(SamplePng(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("https://0x0.st/abcd.png", result.Url);
        Assert.Contains("name=file", handler.LastRequestBody);
    }
}
