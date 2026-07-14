using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using RoeSnip.Core.Updates;
using Xunit;

namespace RoeSnip.Core.Tests.Updates;

/// <summary>Records every request it sees and hands back canned responses from a caller-supplied
/// queue, in order — GitHubLatestReleaseClientTests needs to assert on a SEQUENCE of requests
/// (first probe vs. second probe headers, backoff window behavior), which the single-response
/// StubHttpMessageHandler used by the Sharing tests isn't shaped for.</summary>
internal sealed class SequenceHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<HttpResponseMessage> _responses;

    public List<HttpRequestMessage> Requests { get; } = new();

    public SequenceHttpMessageHandler(params HttpResponseMessage[] responses)
    {
        _responses = new Queue<HttpResponseMessage>(responses);
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        if (_responses.Count == 0)
        {
            throw new InvalidOperationException("SequenceHttpMessageHandler ran out of canned responses.");
        }

        return Task.FromResult(_responses.Dequeue());
    }

    public HttpClient ToClient() => new(this);
}

public class GitHubLatestReleaseClientTests
{
    private static HttpResponseMessage PayloadResponse(string etag) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent("""{ "tag_name": "v9.9.9", "assets": [] }"""),
            Headers = { ETag = new EntityTagHeaderValue(etag) },
        };

    [Fact]
    public async Task FirstProbe_SendsNoIfNoneMatch()
    {
        using var handler = new SequenceHttpMessageHandler(PayloadResponse("\"abc123\""));
        var sut = new GitHubLatestReleaseClient("owner", "repo");

        ProbeResult result = await sut.ProbeAsync(handler.ToClient());

        Assert.Single(handler.Requests);
        Assert.False(handler.Requests[0].Headers.Contains("If-None-Match"));
        Assert.Equal(ProbeStatus.Payload, result.Status);
        Assert.NotNull(result.Json);
        result.Json!.Dispose();
    }

    [Fact]
    public async Task Payload_DoesNotAutoStoreETag_UntilCommitted()
    {
        using var handler = new SequenceHttpMessageHandler(
            PayloadResponse("\"abc123\""),
            PayloadResponse("\"abc123\""));
        var sut = new GitHubLatestReleaseClient("owner", "repo");
        using HttpClient client = handler.ToClient();

        ProbeResult first = await sut.ProbeAsync(client);
        first.Json!.Dispose();

        // No CommitETag call here - a second probe must still send no If-None-Match, proving
        // ProbeAsync itself never auto-commits.
        ProbeResult second = await sut.ProbeAsync(client);
        second.Json!.Dispose();

        Assert.Equal(2, handler.Requests.Count);
        Assert.False(handler.Requests[1].Headers.Contains("If-None-Match"));
    }

    [Fact]
    public async Task AfterCommitETag_NextProbeSendsExactValue()
    {
        using var handler = new SequenceHttpMessageHandler(
            PayloadResponse("\"abc123\""),
            new HttpResponseMessage(HttpStatusCode.NotModified));
        var sut = new GitHubLatestReleaseClient("owner", "repo");
        using HttpClient client = handler.ToClient();

        ProbeResult first = await sut.ProbeAsync(client);
        first.Json!.Dispose();
        sut.CommitETag(first.ETag);

        ProbeResult second = await sut.ProbeAsync(client);

        Assert.Equal(2, handler.Requests.Count);
        Assert.True(handler.Requests[1].Headers.TryGetValues("If-None-Match", out var values));
        Assert.Equal("\"abc123\"", Assert.Single(values!));
        Assert.Equal(ProbeStatus.NotModified, second.Status);
        Assert.Null(second.Json);
    }

    [Fact]
    public async Task NotModified_ReturnsNoJsonAndDoesNotReadBody()
    {
        using var handler = new SequenceHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.NotModified));
        var sut = new GitHubLatestReleaseClient("owner", "repo");

        ProbeResult result = await sut.ProbeAsync(handler.ToClient());

        Assert.Equal(ProbeStatus.NotModified, result.Status);
        Assert.Null(result.Json);
        Assert.Null(result.ETag);
    }

    [Fact]
    public async Task RateLimited_WithRetryAfter_BacksOffUntilWindowExpires_UnlessBypassed()
    {
        var rateLimited = new HttpResponseMessage((HttpStatusCode)403);
        rateLimited.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(120));

        using var handler = new SequenceHttpMessageHandler(
            rateLimited,
            PayloadResponse("\"bypassed\""), // reached only by the bypassBackoff:true call
            PayloadResponse("\"resumed\""));  // reached only after the clock advances past the window

        DateTime now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var sut = new GitHubLatestReleaseClient("owner", "repo", () => now);
        using HttpClient client = handler.ToClient();

        ProbeResult first = await sut.ProbeAsync(client);
        Assert.Equal(ProbeStatus.RateLimited, first.Status);
        Assert.Single(handler.Requests);

        // Still inside the 120s backoff window - must not touch the network at all.
        ProbeResult second = await sut.ProbeAsync(client);
        Assert.Equal(ProbeStatus.RateLimited, second.Status);
        Assert.Single(handler.Requests); // unchanged - no new request was sent

        // A deliberate bypass reaches the network even inside the window.
        ProbeResult bypassed = await sut.ProbeAsync(client, bypassBackoff: true);
        Assert.Equal(ProbeStatus.Payload, bypassed.Status);
        Assert.Equal(2, handler.Requests.Count);
        bypassed.Json!.Dispose();

        // Advance the injected clock past the original window - normal probing resumes.
        now = now.AddSeconds(121);
        ProbeResult afterWindow = await sut.ProbeAsync(client);
        Assert.Equal(ProbeStatus.Payload, afterWindow.Status);
        Assert.Equal(3, handler.Requests.Count);
        afterWindow.Json!.Dispose();
    }

    [Fact]
    public async Task ServerError_ReturnsFailed_NeverThrows()
    {
        using var handler = new SequenceHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var sut = new GitHubLatestReleaseClient("owner", "repo");

        ProbeResult result = await sut.ProbeAsync(handler.ToClient());

        Assert.Equal(ProbeStatus.Failed, result.Status);
        Assert.Null(result.Json);
    }
}
