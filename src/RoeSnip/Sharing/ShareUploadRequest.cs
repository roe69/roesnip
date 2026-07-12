namespace RoeSnip.Sharing;

/// <summary>Data-only description of one file to upload. Content-addressed by value (a plain byte
/// array, not a stream/file handle) since every caller in this phase already has the bytes in memory
/// (a rendered PNG, or a finished recording's temp file read whole) - see the track brief's "implicit
/// save-to-temp" note for the recording case.</summary>
public sealed record ShareUploadRequest(byte[] Content, string FileName, string ContentType);

/// <summary>Outcome of one upload attempt. Errors are surfaced HONESTLY (per the track brief) as a
/// human-readable ErrorMessage rather than a bare exception/status code - callers (toolbar balloon,
/// chrome, settings Test button) show it verbatim.</summary>
public sealed record ShareUploadResult(bool Success, string? Url, string? ErrorMessage, int? HttpStatusCode = null);
