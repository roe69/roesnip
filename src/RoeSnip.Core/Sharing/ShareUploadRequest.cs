using System.IO;

namespace RoeSnip.Core.Sharing;

/// <summary>Data-only description of one file to upload. Content is a seekable <see cref="Stream"/>
/// (a MemoryStream for a rendered PNG, or a FileStream over a finished recording's already-saved-to-
/// temp file), not a byte array: ProviderSpecShareProvider wraps this directly in a StreamContent that
/// copies straight to the network, so nothing here ever buffers a whole large file into managed
/// memory. Must support <see cref="Stream.Length"/> (every intended caller's stream does) - that's
/// how ShareManager.UploadAsync checks a spec's MaxUploadBytes ceiling before touching the network,
/// and how the outgoing request gets an accurate Content-Length instead of falling back to chunked
/// transfer encoding. The caller owns disposal via the same `using`/`await using` a Stream always
/// needs; ProviderSpecShareProvider reads it but does not assume ownership beyond that read.</summary>
public sealed record ShareUploadRequest(Stream Content, string FileName, string ContentType);

/// <summary>Outcome of one upload attempt. Errors are surfaced honestly as a human-readable
/// ErrorMessage rather than a bare exception/status code - callers (toolbar balloon, recording
/// chrome, settings Test button) show it verbatim.</summary>
public sealed record ShareUploadResult(bool Success, string? Url, string? ErrorMessage, int? HttpStatusCode = null);
