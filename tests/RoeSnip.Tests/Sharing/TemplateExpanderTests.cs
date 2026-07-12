using System.Collections.Generic;
using RoeSnip.Sharing;
using Xunit;

namespace RoeSnip.Tests.Sharing;

public class TemplateExpanderTests
{
    [Fact]
    public void Expand_SubstitutesKnownToken()
    {
        var values = new Dictionary<string, string> { ["ApiKey"] = "abc123" };
        Assert.Equal("Bearer abc123", TemplateExpander.Expand("Bearer {ApiKey}", values));
    }

    [Fact]
    public void Expand_SubstitutesMultipleTokens()
    {
        var values = new Dictionary<string, string> { ["BaseUrl"] = "https://example.com", ["Id"] = "42" };
        Assert.Equal("https://example.com/items/42", TemplateExpander.Expand("{BaseUrl}/items/{Id}", values));
    }

    [Fact]
    public void Expand_UnknownToken_BecomesEmptyString()
    {
        var values = new Dictionary<string, string>();
        Assert.Equal("Client-ID ", TemplateExpander.Expand("Client-ID {ApiKey}", values));
    }

    [Fact]
    public void Expand_NoTokens_ReturnsLiteralUnchanged()
    {
        var values = new Dictionary<string, string> { ["ApiKey"] = "abc123" };
        Assert.Equal("fileupload", TemplateExpander.Expand("fileupload", values));
    }

    [Fact]
    public void Expand_EmptyTemplate_ReturnsEmptyString()
    {
        Assert.Equal(string.Empty, TemplateExpander.Expand("", new Dictionary<string, string>()));
    }

    [Fact]
    public void Expand_RepeatedToken_SubstitutesEveryOccurrence()
    {
        var values = new Dictionary<string, string> { ["Name"] = "roesnip" };
        Assert.Equal("roesnip/roesnip", TemplateExpander.Expand("{Name}/{Name}", values));
    }

    [Fact]
    public void Expand_TokenValueIsEmptyString_SubstitutesEmpty()
    {
        var values = new Dictionary<string, string> { ["ApiKey"] = "" };
        Assert.Equal("Client-ID ", TemplateExpander.Expand("Client-ID {ApiKey}", values));
    }

    [Fact]
    public void Expand_MalformedBraces_LeftLiteral()
    {
        // No closing brace, or a brace containing characters outside [A-Za-z0-9_] - not a token, so
        // the regex simply never matches it and it passes through unchanged.
        var values = new Dictionary<string, string> { ["ApiKey"] = "abc" };
        Assert.Equal("{ApiKey", TemplateExpander.Expand("{ApiKey", values));
        Assert.Equal("{Api-Key}", TemplateExpander.Expand("{Api-Key}", values));
    }
}
