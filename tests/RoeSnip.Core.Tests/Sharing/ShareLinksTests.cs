using RoeSnip.Core.Sharing;
using Xunit;

namespace RoeSnip.Core.Tests.Sharing;

public class ShareLinksTests
{
    [Fact]
    public void ComposeOpenUrl_NullToken_ReturnsUrlUnchanged()
    {
        Assert.Equal("https://share.example.com/abc", ShareLinks.ComposeOpenUrl("https://share.example.com/abc", null));
    }

    [Fact]
    public void ComposeOpenUrl_EmptyOrWhitespaceToken_ReturnsUrlUnchanged()
    {
        Assert.Equal("https://share.example.com/abc", ShareLinks.ComposeOpenUrl("https://share.example.com/abc", ""));
        Assert.Equal("https://share.example.com/abc", ShareLinks.ComposeOpenUrl("https://share.example.com/abc", "   "));
    }

    [Fact]
    public void ComposeOpenUrl_TokenPresent_AppendsEditFragment()
    {
        Assert.Equal("https://share.example.com/abc#edit=tok_secret123",
            ShareLinks.ComposeOpenUrl("https://share.example.com/abc", "tok_secret123"));
    }

    [Fact]
    public void ComposeOpenUrl_TokenWithUrlUnsafeCharacters_IsPercentEncoded()
    {
        string result = ShareLinks.ComposeOpenUrl("https://share.example.com/abc", "tok/with+special&chars=1");
        Assert.Equal("https://share.example.com/abc#edit=tok%2Fwith%2Bspecial%26chars%3D1", result);
    }

    [Fact]
    public void CleanUrl_ReturnsUrlAsIs()
    {
        Assert.Equal("https://share.example.com/abc", ShareLinks.CleanUrl("https://share.example.com/abc"));
    }
}
