using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RoeSnip.Core.Sharing;
using Xunit;

namespace RoeSnip.Core.Tests.Sharing;

/// <summary>RoeShare's resumable flow against a scripted fake server (no network): chunk
/// boundaries, per-chunk retry, 409 resume, finalize, and the direct-media link shape.</summary>
public class RoeShareChunkedUploaderTests
{
    private const string Base = "https://share.example.com";

    private sealed record Captured(HttpMethod Method, string Url, Dictionary<string, string> Headers, byte[] Body);

    /// <summary>Records every request (body read before the uploader disposes it) and answers from
    /// a script; an answer of null throws HttpRequestException (a dropped connection).</summary>
    private sealed class FakeRoeShare : HttpMessageHandler
    {
        private readonly Func<Captured, int, HttpResponseMessage?> _respond;
        public List<Captured> Requests { get; } = new();

        public FakeRoeShare(Func<Captured, int, HttpResponseMessage?> respond) => _respond = respond;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            byte[] body = request.Content is null ? Array.Empty<byte>() : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
            var captured = new Captured(request.Method, request.RequestUri!.ToString(), headers, body);
            Requests.Add(captured);
            int sameUrlCount = Requests.Count(r => r.Method == captured.Method && r.Url == captured.Url);
            return _respond(captured, sameUrlCount) ?? throw new HttpRequestException("connection reset");
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>A well-behaved server with chunkSize 4 that tracks received bytes.</summary>
    private static Func<Captured, int, HttpResponseMessage?> HappyServer(long size, Func<Captured, int, HttpResponseMessage?>? overrideChunk = null)
    {
        long received = 0;
        return (req, n) =>
        {
            if (req.Method == HttpMethod.Post && req.Url == Base + "/api/v1/shares")
                return Json(HttpStatusCode.Created, """{"id":"abc","editToken":"tok","url":"https://share.example.com/abc","chunkSize":4}""");
            if (req.Method == HttpMethod.Post && req.Url == Base + "/api/shares/abc/files")
                return Json(HttpStatusCode.OK, """{"fileId":"f1","received":0}""");
            if (req.Method == HttpMethod.Patch)
            {
                if (overrideChunk?.Invoke(req, n) is { } overridden)
                    return overridden;
                long offset = long.Parse(req.Url.Split("offset=")[1]);
                if (offset != received)
                    return Json(HttpStatusCode.Conflict, $$"""{"error":"Offset mismatch","received":{{received}}}""");
                received += req.Body.Length;
                return Json(HttpStatusCode.OK, $$"""{"received":{{received}},"complete":{{(received == size ? "true" : "false")}}}""");
            }
            if (req.Method == HttpMethod.Post && req.Url == Base + "/api/shares/abc/finalize")
                return Json(HttpStatusCode.OK, """{"id":"abc","url":"https://share.example.com/abc"}""");
            return Json(HttpStatusCode.NotFound, """{"error":"unexpected"}""");
        };
    }

    private static byte[] Bytes(int n) => Enumerable.Range(1, n).Select(i => (byte)i).ToArray();

    private static (RoeShareChunkedUploader uploader, List<TimeSpan> delays) Make(FakeRoeShare fake)
    {
        var delays = new List<TimeSpan>();
        var uploader = new RoeShareChunkedUploader(new HttpClient(fake), "RoeShare", (d, _) => { delays.Add(d); return Task.CompletedTask; });
        return (uploader, delays);
    }

    private static Task<ShareUploadResult> Upload(RoeShareChunkedUploader uploader, byte[] data, string mime = "video/mp4") =>
        uploader.UploadAsync(Base, "rsk_test", "0", new ShareUploadRequest(new MemoryStream(data), "take.mp4", mime), CancellationToken.None);

    [Fact]
    public async Task Upload_SplitsAtChunkBoundaries_AndFinalizes()
    {
        byte[] data = Bytes(10);
        var fake = new FakeRoeShare(HappyServer(data.Length));
        var (uploader, delays) = Make(fake);

        ShareUploadResult result = await Upload(uploader, data);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal("https://share.example.com/abc.mp4", result.Url);
        Assert.Equal("tok", result.EditToken);
        Assert.Empty(delays);

        Captured create = fake.Requests[0];
        Assert.Equal("Bearer rsk_test", create.Headers["Authorization"]);
        Assert.Contains("\"expiresIn\":0", Encoding.UTF8.GetString(create.Body));

        string register = Encoding.UTF8.GetString(fake.Requests[1].Body);
        Assert.Contains("\"mime\":\"video/mp4\"", register);
        Assert.Contains("\"size\":10", register);
        Assert.Contains("\"name\":\"take.mp4\"", register);
        Assert.Equal("tok", fake.Requests[1].Headers["X-Edit-Token"]);

        var patches = fake.Requests.Where(r => r.Method == HttpMethod.Patch).ToList();
        Assert.Equal(new[] { "0", "4", "8" }, patches.Select(p => p.Url.Split("offset=")[1]));
        Assert.Equal(data[0..4], patches[0].Body);
        Assert.Equal(data[4..8], patches[1].Body);
        Assert.Equal(data[8..10], patches[2].Body);
        Assert.All(patches, p => Assert.Equal("tok", p.Headers["X-Edit-Token"]));

        Assert.Equal(Base + "/api/shares/abc/finalize", fake.Requests[^1].Url);
    }

    [Fact]
    public async Task Upload_ExactMultipleOfChunkSize_SendsNoEmptyChunk()
    {
        byte[] data = Bytes(8);
        var fake = new FakeRoeShare(HappyServer(data.Length));
        var (uploader, _) = Make(fake);

        ShareUploadResult result = await Upload(uploader, data);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(2, fake.Requests.Count(r => r.Method == HttpMethod.Patch));
    }

    [Fact]
    public async Task Upload_TransientChunkFailure_RetriesThatChunkOnly()
    {
        byte[] data = Bytes(10);
        var fake = new FakeRoeShare(HappyServer(data.Length, (req, n) =>
            req.Url.EndsWith("offset=4") && n == 1 ? Json(HttpStatusCode.ServiceUnavailable, """{"error":"busy"}""") : null));
        var (uploader, delays) = Make(fake);

        ShareUploadResult result = await Upload(uploader, data);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Single(delays);
        Assert.Equal(new[] { "0", "4", "4", "8" },
            fake.Requests.Where(r => r.Method == HttpMethod.Patch).Select(p => p.Url.Split("offset=")[1]));
    }

    [Fact]
    public async Task Upload_LostResponse_ResumesFromServerReceivedOffset()
    {
        byte[] data = Bytes(10);
        long received = 0;
        var fake = new FakeRoeShare((req, n) =>
        {
            if (req.Method != HttpMethod.Patch)
                return HappyServer(data.Length)(req, n);
            long offset = long.Parse(req.Url.Split("offset=")[1]);
            if (offset != received)
                return Json(HttpStatusCode.Conflict, $$"""{"error":"Offset mismatch","received":{{received}}}""");
            received += req.Body.Length;
            // The chunk at 4 lands, but its response is lost.
            return offset == 4 && n == 1 ? null : Json(HttpStatusCode.OK, $$"""{"received":{{received}},"complete":false}""");
        });
        var (uploader, _) = Make(fake);

        ShareUploadResult result = await Upload(uploader, data);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(new[] { "0", "4", "4", "8" },
            fake.Requests.Where(r => r.Method == HttpMethod.Patch).Select(p => p.Url.Split("offset=")[1]));
        Assert.Equal(10, received);
    }

    [Fact]
    public async Task Upload_ChunkKeepsFailing_GivesUpWithoutFinalizing()
    {
        byte[] data = Bytes(10);
        var fake = new FakeRoeShare(HappyServer(data.Length, (req, _) =>
            req.Url.EndsWith("offset=4") ? Json(HttpStatusCode.BadGateway, "bad gateway") : null));
        var (uploader, delays) = Make(fake);

        ShareUploadResult result = await Upload(uploader, data);

        Assert.False(result.Success);
        Assert.Equal(502, result.HttpStatusCode);
        Assert.Contains("502", result.ErrorMessage);
        Assert.Equal(RoeShareChunkedUploader.MaxAttempts - 1, delays.Count);
        Assert.Equal(RoeShareChunkedUploader.MaxAttempts, fake.Requests.Count(r => r.Url.EndsWith("offset=4")));
        Assert.DoesNotContain(fake.Requests, r => r.Url.EndsWith("/finalize"));
    }

    [Fact]
    public async Task Upload_ClientError_IsNotRetried()
    {
        byte[] data = Bytes(10);
        var fake = new FakeRoeShare(HappyServer(data.Length, (req, _) =>
            req.Url.EndsWith("offset=0") ? Json(HttpStatusCode.Forbidden, """{"error":"API key is no longer valid"}""") : null));
        var (uploader, delays) = Make(fake);

        ShareUploadResult result = await Upload(uploader, data);

        Assert.False(result.Success);
        Assert.Contains("API key is no longer valid", result.ErrorMessage);
        Assert.Empty(delays);
    }

    [Fact]
    public async Task Upload_CreateServerError_IsNotRetriedBlind()
    {
        var fake = new FakeRoeShare((req, _) => Json(HttpStatusCode.InternalServerError, """{"error":"boom"}"""));
        var (uploader, delays) = Make(fake);

        ShareUploadResult result = await Upload(uploader, Bytes(10));

        Assert.False(result.Success);
        Assert.Single(fake.Requests);
        Assert.Empty(delays);
    }

    [Fact]
    public async Task Upload_CreateBusy_IsRetried()
    {
        byte[] data = Bytes(6);
        var happy = HappyServer(data.Length);
        var fake = new FakeRoeShare((req, n) =>
            req.Url == Base + "/api/v1/shares" && n == 1
                ? Json(HttpStatusCode.ServiceUnavailable, """{"error":"Server is busy. Please retry shortly.","retryAfter":2}""")
                : happy(req, n));
        var (uploader, delays) = Make(fake);

        ShareUploadResult result = await Upload(uploader, data);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Single(delays);
    }

    [Fact]
    public async Task Upload_FinalizeTransientFailure_IsRetried()
    {
        byte[] data = Bytes(5);
        var happy = HappyServer(data.Length);
        var fake = new FakeRoeShare((req, n) =>
            req.Url.EndsWith("/finalize") && n == 1 ? null : happy(req, n));
        var (uploader, delays) = Make(fake);

        ShareUploadResult result = await Upload(uploader, data, "image/gif");

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal("https://share.example.com/abc.gif", result.Url);
        Assert.Single(delays);
        Assert.Equal(2, fake.Requests.Count(r => r.Url.EndsWith("/finalize")));
    }

    [Theory]
    [InlineData("https://s.example/abc", "video/mp4", "https://s.example/abc.mp4")]
    [InlineData("https://s.example/abc", "image/jpeg", "https://s.example/abc.jpg")]
    [InlineData("https://s.example/abc", "image/png; charset=binary", "https://s.example/abc.png")]
    [InlineData("https://s.example/abc", "application/zip", "https://s.example/abc")]
    [InlineData("https://s.example/abc.gif", "image/gif", "https://s.example/abc.gif")]
    public void WithMediaExtension_MatchesServerTable(string url, string mime, string expected)
    {
        Assert.Equal(expected, RoeShareChunkedUploader.WithMediaExtension(url, mime));
    }

    [Fact]
    public async Task Provider_RoeShareAboveThreshold_UsesChunkedFlow()
    {
        byte[] data = Bytes(10);
        var fake = new FakeRoeShare(HappyServer(data.Length));
        var spec = ShareProviderCatalog.RoeShare with { ChunkedAboveBytes = 8 };
        var config = new ShareProviderConfig
        {
            Id = "roeshare-1",
            SpecId = "roeshare",
            Values = new Dictionary<string, string> { ["BaseUrl"] = Base, ["ApiKey"] = "rsk_test" },
        };
        var provider = new ProviderSpecShareProvider(spec, config, new HttpClient(fake));

        ShareUploadResult result = await provider.UploadAsync(new ShareUploadRequest(new MemoryStream(data), "take.mp4", "video/mp4"), CancellationToken.None);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal("https://share.example.com/abc.mp4", result.Url);
        Assert.DoesNotContain(fake.Requests, r => r.Url.Contains("/api/v1/upload"));
        // ExpiresIn backfilled to "0" (never) on the chunked path too.
        Assert.Contains("\"expiresIn\":0", Encoding.UTF8.GetString(fake.Requests[0].Body));
    }

    [Fact]
    public async Task Provider_RoeShareAtOrBelowThreshold_KeepsOneShot()
    {
        var fake = new FakeRoeShare((_, _) => Json(HttpStatusCode.Created, """{"id":"abc","url":"https://share.example.com/abc.png"}"""));
        var spec = ShareProviderCatalog.RoeShare with { ChunkedAboveBytes = 8 };
        var config = new ShareProviderConfig
        {
            Id = "roeshare-1",
            SpecId = "roeshare",
            Values = new Dictionary<string, string> { ["BaseUrl"] = Base, ["ApiKey"] = "rsk_test" },
        };
        var provider = new ProviderSpecShareProvider(spec, config, new HttpClient(fake));

        ShareUploadResult result = await provider.UploadAsync(new ShareUploadRequest(new MemoryStream(Bytes(8)), "shot.png", "image/png"), CancellationToken.None);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Single(fake.Requests);
        Assert.StartsWith(Base + "/api/v1/upload?", fake.Requests[0].Url);
    }

    [Fact]
    public void CustomSpecs_DefaultToOneShot()
    {
        var custom = new ProviderSpec { Id = "custom", Endpoint = "https://x.example/up" };
        Assert.Equal(ChunkedUploadProtocol.None, custom.ChunkedProtocol);
        Assert.Null(custom.ChunkedAboveBytes);
    }
}
