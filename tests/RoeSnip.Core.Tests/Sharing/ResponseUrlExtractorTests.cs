using RoeSnip.Core.Sharing;
using Xunit;

namespace RoeSnip.Core.Tests.Sharing;

public class ResponseUrlExtractorTests
{
    private static ProviderSpec SpecWithJsonPath(string path) =>
        new() { ResponseMode = ResponseUrlMode.JsonPath, ResponseJsonPath = path };

    private static ProviderSpec SpecWithRegex(string pattern) =>
        new() { ResponseMode = ResponseUrlMode.Regex, ResponseRegex = pattern };

    private static readonly ProviderSpec PlainBodySpec = new() { ResponseMode = ResponseUrlMode.PlainBody };

    // ---------- JsonPath ----------

    [Fact]
    public void JsonPath_TopLevelField_Extracts()
    {
        bool ok = ResponseUrlExtractor.TryExtract(SpecWithJsonPath("url"), """{"id":"abc","url":"https://share.example.com/abc"}""", out string? url, out string? error);
        Assert.True(ok);
        Assert.Equal("https://share.example.com/abc", url);
        Assert.Null(error);
    }

    [Fact]
    public void JsonPath_NestedField_Extracts()
    {
        bool ok = ResponseUrlExtractor.TryExtract(SpecWithJsonPath("data.link"), """{"data":{"link":"https://imgur.com/abc","deletehash":"xyz"},"success":true}""", out string? url, out _);
        Assert.True(ok);
        Assert.Equal("https://imgur.com/abc", url);
    }

    [Fact]
    public void JsonPath_MissingField_Fails()
    {
        bool ok = ResponseUrlExtractor.TryExtract(SpecWithJsonPath("data.link"), """{"data":{}}""", out string? url, out string? error);
        Assert.False(ok);
        Assert.Null(url);
        Assert.Contains("data.link", error);
    }

    [Fact]
    public void JsonPath_InvalidJson_FailsWithReadableError()
    {
        bool ok = ResponseUrlExtractor.TryExtract(SpecWithJsonPath("url"), "not json at all", out string? url, out string? error);
        Assert.False(ok);
        Assert.Null(url);
        Assert.Contains("not valid JSON", error);
    }

    [Fact]
    public void JsonPath_PathTraversesThroughNonObject_Fails()
    {
        // "url" is a string, not an object - "url.nested" cannot descend into it.
        bool ok = ResponseUrlExtractor.TryExtract(SpecWithJsonPath("url.nested"), """{"url":"https://x"}""", out string? url, out _);
        Assert.False(ok);
        Assert.Null(url);
    }

    [Fact]
    public void JsonPath_NoPathConfigured_Fails()
    {
        var spec = new ProviderSpec { ResponseMode = ResponseUrlMode.JsonPath, ResponseJsonPath = null };
        bool ok = ResponseUrlExtractor.TryExtract(spec, """{"url":"https://x"}""", out _, out string? error);
        Assert.False(ok);
        Assert.NotNull(error);
    }

    // ---------- PlainBody ----------

    [Fact]
    public void PlainBody_TrimsWhitespace()
    {
        bool ok = ResponseUrlExtractor.TryExtract(PlainBodySpec, "  https://catbox.moe/abc.png\n", out string? url, out _);
        Assert.True(ok);
        Assert.Equal("https://catbox.moe/abc.png", url);
    }

    [Fact]
    public void PlainBody_EmptyResponse_Fails()
    {
        bool ok = ResponseUrlExtractor.TryExtract(PlainBodySpec, "   ", out string? url, out string? error);
        Assert.False(ok);
        Assert.Null(url);
        Assert.NotNull(error);
    }

    // ---------- Regex ----------

    [Fact]
    public void Regex_FirstCaptureGroup_Extracts()
    {
        bool ok = ResponseUrlExtractor.TryExtract(
            SpecWithRegex(@"URL:\s*(\S+)"), "Upload complete. URL: https://example.com/f/1 (expires in 1h)", out string? url, out _);
        Assert.True(ok);
        Assert.Equal("https://example.com/f/1", url);
    }

    [Fact]
    public void Regex_NoMatch_Fails()
    {
        bool ok = ResponseUrlExtractor.TryExtract(SpecWithRegex(@"URL:\s*(\S+)"), "nothing relevant here", out string? url, out string? error);
        Assert.False(ok);
        Assert.Null(url);
        Assert.NotNull(error);
    }

    [Fact]
    public void Regex_NoCaptureGroup_Fails()
    {
        // Pattern matches but has no group 1 to report as the URL.
        bool ok = ResponseUrlExtractor.TryExtract(SpecWithRegex("URL"), "the URL is here", out string? url, out string? error);
        Assert.False(ok);
        Assert.Null(url);
        Assert.NotNull(error);
    }

    [Fact]
    public void Regex_InvalidPattern_FailsWithoutThrowing()
    {
        bool ok = ResponseUrlExtractor.TryExtract(SpecWithRegex("("), "anything", out string? url, out string? error);
        Assert.False(ok);
        Assert.Null(url);
        Assert.NotNull(error);
    }

    [Fact]
    public void Regex_NoPatternConfigured_Fails()
    {
        var spec = new ProviderSpec { ResponseMode = ResponseUrlMode.Regex, ResponseRegex = null };
        bool ok = ResponseUrlExtractor.TryExtract(spec, "anything", out _, out string? error);
        Assert.False(ok);
        Assert.NotNull(error);
    }
}
