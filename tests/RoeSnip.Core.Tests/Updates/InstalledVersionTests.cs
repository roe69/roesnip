using System;
using RoeSnip.Core.Updates;
using Xunit;

namespace RoeSnip.Core.Tests.Updates;

public sealed class InstalledVersionTests
{
    private static readonly Version Release = new(2, 7, 1);

    [Theory]
    [InlineData("2.7.1", true)]
    [InlineData("2.7.1.0", true)]
    [InlineData(" 2.7.1+abc123 ", true)]
    [InlineData("2.7.1-beta", true)]
    [InlineData("2.7.0", false)]
    [InlineData("2.8.1", false)]
    [InlineData("0.0.0", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("not a version", false)]
    public void Matches_Text(string? installed, bool expected) =>
        Assert.Equal(expected, InstalledVersion.Matches(installed, Release));

    [Fact]
    public void Matches_FileVersionParts_ZeroNeverMatches()
    {
        Assert.True(InstalledVersion.Matches(new Version(2, 7, 1), Release));
        Assert.True(InstalledVersion.Matches(new Version(2, 7, 1, 0), Release));
        Assert.False(InstalledVersion.Matches(new Version(0, 0, 0), new Version(0, 0, 0)));
        Assert.False(InstalledVersion.Matches(new Version(2, 7), Release));
    }

    // The exact shape release.yml writes into RoeSnip.app/Contents/Info.plist.
    private const string ReleasePlist = """
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0">
        <dict>
          <key>CFBundleName</key><string>RoeSnip</string>
          <key>CFBundleVersion</key><string>2.7.1</string>
          <key>CFBundleShortVersionString</key><string>2.7.1</string>
          <key>CFBundlePackageType</key><string>APPL</string>
        </dict>
        </plist>
        """;

    [Fact]
    public void FromMacInfoPlist_ReadsShortVersion()
    {
        Assert.Equal("2.7.1", InstalledVersion.FromMacInfoPlist(ReleasePlist));
        Assert.True(InstalledVersion.Matches(InstalledVersion.FromMacInfoPlist(ReleasePlist), Release));
    }

    [Fact]
    public void FromMacInfoPlist_MissingKeyOrNotXml_IsNull()
    {
        Assert.Null(InstalledVersion.FromMacInfoPlist(ReleasePlist.Replace("CFBundleShortVersionString", "Other")));
        Assert.Null(InstalledVersion.FromMacInfoPlist("bplist00\u0001\u0002"));
        Assert.Null(InstalledVersion.FromMacInfoPlist(""));
    }
}
