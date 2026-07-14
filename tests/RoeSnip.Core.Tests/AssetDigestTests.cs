using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using RoeSnip.Core.Updates;
using Xunit;

namespace RoeSnip.Core.Tests;

/// <summary>Verifies the downloaded-update hash check both UpdateManagers rely on
/// (ApplyUpdateCoreAsync, WPF and Avalonia) against real temp files - no network, no OS dependency,
/// which is exactly why this lives in RoeSnip.Core rather than being duplicated per app.</summary>
public class AssetDigestTests : IDisposable
{
    private readonly string _tempDir;

    public AssetDigestTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"roesnip_core_assetdigest_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    private string WriteTempFile(byte[] content)
    {
        string path = Path.Combine(_tempDir, $"{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(path, content);
        return path;
    }

    private static string Sha256HexOf(byte[] content) => Convert.ToHexString(SHA256.HashData(content));

    // ---------- ParseSha256Hex ----------

    [Fact]
    public void ParseSha256Hex_WellFormedDigest_ReturnsHex()
    {
        string hex = new string('a', 64);
        Assert.Equal(hex, AssetDigest.ParseSha256Hex($"sha256:{hex}"));
    }

    [Fact]
    public void ParseSha256Hex_IsCaseInsensitiveOnPrefix()
    {
        string hex = new string('b', 64);
        Assert.Equal(hex, AssetDigest.ParseSha256Hex($"SHA256:{hex}"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-digest")]
    [InlineData("md5:abcd")]
    [InlineData("sha256:")]
    [InlineData("sha256:tooshort")]
    public void ParseSha256Hex_MalformedOrMissing_ReturnsNull(string? malformed)
    {
        Assert.Null(AssetDigest.ParseSha256Hex(malformed));
    }

    [Fact]
    public void ParseSha256Hex_NonHexCharacters_ReturnsNull()
    {
        // 64 characters, but the 'z' makes it invalid hex.
        string notHex = "z" + new string('a', 63);
        Assert.Null(AssetDigest.ParseSha256Hex($"sha256:{notHex}"));
    }

    [Fact]
    public void ParseSha256Hex_WrongLengthHex_ReturnsNull()
    {
        Assert.Null(AssetDigest.ParseSha256Hex("sha256:" + new string('a', 63)));
        Assert.Null(AssetDigest.ParseSha256Hex("sha256:" + new string('a', 65)));
    }

    // ---------- ComputeSha256HexAsync ----------

    [Fact]
    public async System.Threading.Tasks.Task ComputeSha256HexAsync_MatchesKnownHash()
    {
        byte[] content = Encoding.UTF8.GetBytes("RoeSnip update payload");
        string path = WriteTempFile(content);

        string actual = await AssetDigest.ComputeSha256HexAsync(path);

        Assert.Equal(Sha256HexOf(content), actual, ignoreCase: true);
    }

    // ---------- VerifyAsync ----------

    [Fact]
    public async System.Threading.Tasks.Task VerifyAsync_MatchingDigest_ReturnsTrue()
    {
        byte[] content = Encoding.UTF8.GetBytes("matching content");
        string path = WriteTempFile(content);
        string digest = $"sha256:{Sha256HexOf(content)}";

        Assert.True(await AssetDigest.VerifyAsync(path, digest));
    }

    [Fact]
    public async System.Threading.Tasks.Task VerifyAsync_MatchingDigest_IsCaseInsensitive()
    {
        byte[] content = Encoding.UTF8.GetBytes("matching content, mixed case digest");
        string path = WriteTempFile(content);
        string digest = $"sha256:{Sha256HexOf(content).ToLowerInvariant()}";

        Assert.True(await AssetDigest.VerifyAsync(path, digest));
    }

    [Fact]
    public async System.Threading.Tasks.Task VerifyAsync_MismatchedDigest_ReturnsFalse()
    {
        byte[] content = Encoding.UTF8.GetBytes("actual content");
        string path = WriteTempFile(content);
        string wrongDigest = $"sha256:{new string('0', 64)}";

        Assert.False(await AssetDigest.VerifyAsync(path, wrongDigest));
    }

    [Fact]
    public async System.Threading.Tasks.Task VerifyAsync_MalformedDigest_ReturnsNull()
    {
        byte[] content = Encoding.UTF8.GetBytes("anything");
        string path = WriteTempFile(content);

        Assert.Null(await AssetDigest.VerifyAsync(path, "not-a-real-digest"));
    }

    [Fact]
    public async System.Threading.Tasks.Task VerifyAsync_NullDigest_ReturnsNull()
    {
        byte[] content = Encoding.UTF8.GetBytes("anything");
        string path = WriteTempFile(content);

        Assert.Null(await AssetDigest.VerifyAsync(path, null));
    }
}
