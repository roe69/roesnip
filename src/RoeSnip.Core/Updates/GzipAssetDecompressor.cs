using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;

namespace RoeSnip.Core.Updates;

/// <summary>Decompresses the ".gz" transit asset release.yml attaches alongside each Windows
/// self-update exe (hardening item 9: gzip -9 cuts ~61% off the ~180 MB / ~120 MB single-file
/// downloads, fleet-wide) back into the plain exe bytes ApplyUpdateCoreAsync swaps into place.
/// Framework-free (System.IO.Compression only) so both UpdateManagers share it from RoeSnip.Core
/// rather than duplicating a GZipStream.CopyToAsync call - see AssetDigest's own doc comment for
/// the same reasoning applied to the hash check this runs alongside.
///
/// This only ever runs AFTER <see cref="AssetDigest.VerifyAsync"/> has confirmed the downloaded
/// ".gz" bytes match the asset's own published digest - decompressing first and hashing the
/// decompressed output would mean a corrupted/truncated ".gz" could still decompress to something
/// that passes (gzip's own CRC is not a substitute for verifying against GitHub's published
/// digest). Decompression itself is local CPU only, no network, and finishes in well under a
/// second for these asset sizes.</summary>
public static class GzipAssetDecompressor
{
    /// <summary>Streams <paramref name="sourceGzipPath"/> through <see cref="GZipStream"/> into
    /// <paramref name="destinationPath"/> (overwriting it if present). Throws on any I/O or
    /// gzip-format failure - callers are expected to treat that identically to a failed download
    /// (delete both files, fail the update, retry next cycle) rather than swallowing it here.</summary>
    public static async Task DecompressAsync(string sourceGzipPath, string destinationPath, CancellationToken cancellationToken = default)
    {
        await using FileStream source = File.OpenRead(sourceGzipPath);
        await using var gzip = new GZipStream(source, CompressionMode.Decompress);
        await using FileStream destination = File.Create(destinationPath);
        await gzip.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
    }
}
