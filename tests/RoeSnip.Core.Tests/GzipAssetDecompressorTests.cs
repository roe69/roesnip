using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading.Tasks;
using RoeSnip.Core.Updates;
using Xunit;

namespace RoeSnip.Core.Tests;

/// <summary>Round-trips <see cref="GzipAssetDecompressor.DecompressAsync"/> against a real gzip
/// stream - no network, no OS dependency, matching AssetDigestTests' own reasoning for living in
/// RoeSnip.Core rather than being duplicated per app.</summary>
public class GzipAssetDecompressorTests : IDisposable
{
    private readonly string _tempDir;

    public GzipAssetDecompressorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"roesnip_core_gzip_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    private static void WriteGzip(string path, byte[] payload)
    {
        using FileStream fileStream = File.Create(path);
        using var gzip = new GZipStream(fileStream, CompressionLevel.Optimal);
        gzip.Write(payload, 0, payload.Length);
    }

    [Fact]
    public async Task DecompressAsync_RoundTrips_MatchesOriginalPayload()
    {
        byte[] payload = Encoding.UTF8.GetBytes("RoeSnip update payload - round trip through gzip, item 9.");
        string gzPath = Path.Combine(_tempDir, "payload.bin.gz");
        string outPath = Path.Combine(_tempDir, "payload.bin");
        WriteGzip(gzPath, payload);

        await GzipAssetDecompressor.DecompressAsync(gzPath, outPath);

        byte[] result = await File.ReadAllBytesAsync(outPath);
        Assert.Equal(payload, result);
    }

    [Fact]
    public async Task DecompressAsync_EmptyPayload_RoundTrips()
    {
        byte[] payload = [];
        string gzPath = Path.Combine(_tempDir, "empty.bin.gz");
        string outPath = Path.Combine(_tempDir, "empty.bin");
        WriteGzip(gzPath, payload);

        await GzipAssetDecompressor.DecompressAsync(gzPath, outPath);

        Assert.True(File.Exists(outPath));
        Assert.Empty(await File.ReadAllBytesAsync(outPath));
    }

    [Fact]
    public async Task DecompressAsync_OverwritesExistingDestination()
    {
        byte[] payload = Encoding.UTF8.GetBytes("fresh content after overwrite");
        string gzPath = Path.Combine(_tempDir, "overwrite.bin.gz");
        string outPath = Path.Combine(_tempDir, "overwrite.bin");
        File.WriteAllText(outPath, "stale leftover from a previous attempt");
        WriteGzip(gzPath, payload);

        await GzipAssetDecompressor.DecompressAsync(gzPath, outPath);

        Assert.Equal(payload, await File.ReadAllBytesAsync(outPath));
    }

    [Fact]
    public async Task DecompressAsync_NotGzipFormat_Throws()
    {
        string badPath = Path.Combine(_tempDir, "not-gzip.bin");
        string outPath = Path.Combine(_tempDir, "not-gzip-out.bin");
        File.WriteAllText(badPath, "this is plain text, not a gzip stream");

        await Assert.ThrowsAsync<InvalidDataException>(() => GzipAssetDecompressor.DecompressAsync(badPath, outPath));
    }
}
