using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace RoeSnip.Core.Updates;

/// <summary>Verifies a downloaded update exe against the "sha256:&lt;hex&gt;"-shaped digest GitHub's
/// Releases API publishes per asset (the "digest" field alongside "size" on every entry in
/// "assets") - catches transport corruption, a truncated download, or a download-CDN-path MITM.
/// Framework-free (System.Security.Cryptography only) so both UpdateManagers share it from
/// RoeSnip.Core rather than duplicating the hash/compare logic.
///
/// Deliberately NOT a defense against a compromised publisher: the digest arrives over the exact
/// same TLS/GitHub-API channel as the download URL itself, so an attacker capable of rewriting one
/// could rewrite the other - this closes the "URL is honest, bytes got mangled/swapped in transit"
/// gap, not the "GitHub account or release pipeline is compromised" one. That would need something
/// like Authenticode signing, which is out of scope here.</summary>
public static class AssetDigest
{
    private const string Sha256Prefix = "sha256:";

    /// <summary>Extracts the 64-character hex payload from a "sha256:&lt;hex&gt;" digest string.
    /// Returns null - never throws - for anything not shaped exactly like that: missing/empty,
    /// a different algorithm prefix, or a hex payload that isn't valid 64-character hex. Callers
    /// treat null as "cannot verify" and fail OPEN (see this class's own doc comment for why a
    /// stricter fail-closed policy wouldn't actually buy any security here) rather than as a
    /// verification failure.</summary>
    public static string? ParseSha256Hex(string? digest)
    {
        if (string.IsNullOrWhiteSpace(digest))
        {
            return null;
        }

        if (!digest.StartsWith(Sha256Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string hex = digest[Sha256Prefix.Length..];
        if (hex.Length != 64)
        {
            return null;
        }

        foreach (char c in hex)
        {
            if (!Uri.IsHexDigit(c))
            {
                return null;
            }
        }

        return hex;
    }

    /// <summary>Streams <paramref name="filePath"/> and returns its SHA-256 as hex (case not
    /// guaranteed - compare with <see cref="StringComparison.OrdinalIgnoreCase"/>). Uses
    /// SHA256.HashDataAsync over a FileStream rather than reading the whole file into memory first,
    /// so verifying a ~180 MB update exe doesn't double its peak memory footprint.</summary>
    public static async Task<string> ComputeSha256HexAsync(string filePath, CancellationToken cancellationToken = default)
    {
        await using FileStream stream = File.OpenRead(filePath);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    /// <summary>Verifies <paramref name="filePath"/> against <paramref name="expectedDigest"/> (a
    /// raw "sha256:&lt;hex&gt;" string straight off a GitHub asset's "digest" field). Returns:
    /// true (hash matches), false (hash mismatch - the file is corrupt, truncated, or tampered with
    /// and must not be used), or null when <paramref name="expectedDigest"/> doesn't parse (missing
    /// field, different algorithm, malformed hex) - a distinct outcome from false because "cannot
    /// verify" and "verified and failed" call for different caller behavior (fail open vs. reject
    /// the download).</summary>
    public static async Task<bool?> VerifyAsync(string filePath, string? expectedDigest, CancellationToken cancellationToken = default)
    {
        string? expectedHex = ParseSha256Hex(expectedDigest);
        if (expectedHex is null)
        {
            return null;
        }

        string actualHex = await ComputeSha256HexAsync(filePath, cancellationToken).ConfigureAwait(false);
        return string.Equals(actualHex, expectedHex, StringComparison.OrdinalIgnoreCase);
    }
}
