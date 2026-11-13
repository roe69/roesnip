using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RoeSnip.Core.Diagnostics;

namespace RoeSnip.Core.Sharing;

/// <summary>RoeShare's resumable upload (roeshare CONTRACT.md "Programmatic API"): create the share
/// with the API key, register the file (name, size, mime - the mime is what keeps the share
/// embed-eligible), PATCH it in chunks at explicit offsets, then finalize. Used for files above the
/// one-shot endpoint's 64 MiB request-body cap.
///
/// Each chunk is retried on its own (network error, timeout, 408/429/5xx). A 409 carries the server's
/// real <c>received</c> offset, so a chunk that landed but whose response was lost resumes from there
/// instead of failing. Create and register are retried only on 429/503 (the server's explicit "not
/// processed"): a blind retry could create a second share or register a second file, which would
/// block finalize.
///
/// Finalize returns the plain <c>/&lt;id&gt;</c> link; the one-shot endpoint returns the direct-media
/// <c>/&lt;id&gt;.&lt;ext&gt;</c> form for an embeddable image/mp4. The extension is added here from the
/// same mime table (roeshare routes/pages.js MIME_MEDIA_EXT) so the copied link is the same shape
/// either way. This uploader never sets a password, one-time or download cap, the only other things
/// that make the server refuse the direct-media form.</summary>
public sealed class RoeShareChunkedUploader
{
    public const int MaxAttempts = 5;
    private const int FallbackChunkSize = 8 * 1024 * 1024;
    // Bounds the one reusable chunk buffer whatever chunkSize the server advertises; smaller
    // chunks are always accepted.
    private const int MaxChunkSize = 16 * 1024 * 1024;
    private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(30);

    private readonly HttpClient _client;
    private readonly string _displayName;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    /// <param name="delay">Test seam for the retry backoff; defaults to Task.Delay.</param>
    public RoeShareChunkedUploader(HttpClient client, string displayName, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _client = client;
        _displayName = displayName;
        _delay = delay ?? ((d, ct) => Task.Delay(d, ct));
    }

    /// <summary>Never throws for an upload failure (returns Success=false); throws
    /// OperationCanceledException only when <paramref name="cancellationToken"/> is cancelled.
    /// <paramref name="expiresIn"/> is sent as-is when it parses as a non-negative integer ("0" =
    /// never) and omitted otherwise (server default).</summary>
    public async Task<ShareUploadResult> UploadAsync(
        string baseUrl, string apiKey, string? expiresIn, ShareUploadRequest request, CancellationToken cancellationToken)
    {
        Stream content = request.Content;
        long size = content.Length;
        string mime = string.IsNullOrWhiteSpace(request.ContentType) ? "application/octet-stream" : request.ContentType;
        string root = baseUrl.TrimEnd('/');
        FileLog.Write($"RoeSnip: share upload: {_displayName} chunked upload of {request.FileName} ({size} bytes)");

        // 1. Create the share.
        var createBody = new Dictionary<string, object>();
        if (long.TryParse(expiresIn, NumberStyles.None, CultureInfo.InvariantCulture, out long expiresSeconds))
        {
            createBody["expiresIn"] = expiresSeconds;
        }
        StepResult create = await SendAsync("create share", () =>
        {
            var m = new HttpRequestMessage(HttpMethod.Post, root + "/api/v1/shares") { Content = Json(createBody) };
            if (!string.IsNullOrEmpty(apiKey))
            {
                m.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiKey);
            }
            return m;
        }, retryAmbiguous: false, cancellationToken).ConfigureAwait(false);
        if (!create.Ok)
        {
            return Fail("creating the share", create);
        }
        string? shareId = ReadString(create.Body, "id");
        string? editToken = ReadString(create.Body, "editToken");
        if (string.IsNullOrEmpty(shareId) || string.IsNullOrEmpty(editToken))
        {
            return Fail("creating the share", create with { Ok = false, Error = "the response had no id or editToken" });
        }
        long advertised = ReadLong(create.Body, "chunkSize") ?? FallbackChunkSize;
        int chunkSize = (int)Math.Clamp(advertised <= 0 ? FallbackChunkSize : advertised, 1, MaxChunkSize);
        string shareRoot = root + "/api/shares/" + Uri.EscapeDataString(shareId);

        // 2. Register the file.
        var registerBody = new Dictionary<string, object> { ["name"] = request.FileName, ["size"] = size, ["mime"] = mime };
        StepResult register = await SendAsync("register file", () =>
            WithEditToken(new HttpRequestMessage(HttpMethod.Post, shareRoot + "/files") { Content = Json(registerBody) }, editToken),
            retryAmbiguous: false, cancellationToken).ConfigureAwait(false);
        if (!register.Ok)
        {
            return Fail("registering the file", register);
        }
        string? fileId = ReadString(register.Body, "fileId");
        if (string.IsNullOrEmpty(fileId))
        {
            return Fail("registering the file", register with { Ok = false, Error = "the response had no fileId" });
        }
        string fileUrl = shareRoot + "/files/" + Uri.EscapeDataString(fileId);

        // 3. Chunks.
        long offset = ReadLong(register.Body, "received") ?? 0;
        byte[] buffer = new byte[(int)Math.Min(chunkSize, Math.Max(size, 1))];
        int resyncs = 0;
        while (offset < size)
        {
            long at = offset;
            int length = (int)Math.Min(chunkSize, size - at);
            content.Position = at;
            await content.ReadExactlyAsync(buffer.AsMemory(0, length), cancellationToken).ConfigureAwait(false);

            StepResult chunk = await SendAsync($"chunk at {at}", () =>
            {
                var body = new ByteArrayContent(buffer, 0, length);
                body.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                return WithEditToken(
                    new HttpRequestMessage(HttpMethod.Patch, fileUrl + "?offset=" + at.ToString(CultureInfo.InvariantCulture)) { Content = body },
                    editToken);
            }, retryAmbiguous: true, cancellationToken).ConfigureAwait(false);

            if (chunk.Ok)
            {
                long received = ReadLong(chunk.Body, "received") ?? at + length;
                if (received <= at || received > size)
                {
                    return Fail($"uploading at {at}", chunk with { Ok = false, Error = $"the server reported {received} bytes received" });
                }
                offset = received;
                continue;
            }

            // A chunk whose response was lost already landed: the 409 names where to resume.
            if (chunk.Status == 409 && ReadLong(chunk.Body, "received") is { } serverReceived
                && serverReceived >= 0 && serverReceived <= size && resyncs++ < MaxAttempts)
            {
                FileLog.Write($"RoeSnip: share upload: offset {at} rejected, server has {serverReceived}; resuming there");
                offset = serverReceived;
                continue;
            }
            return Fail($"uploading at {at} of {size} bytes", chunk);
        }

        // 4. Finalize (idempotent server-side, so safe to retry blind).
        StepResult finalize = await SendAsync("finalize", () =>
            WithEditToken(new HttpRequestMessage(HttpMethod.Post, shareRoot + "/finalize"), editToken),
            retryAmbiguous: true, cancellationToken).ConfigureAwait(false);
        if (!finalize.Ok)
        {
            return Fail("finalizing the share", finalize);
        }
        string? url = ReadString(finalize.Body, "url") ?? ReadString(create.Body, "url");
        if (string.IsNullOrEmpty(url))
        {
            return Fail("finalizing the share", finalize with { Ok = false, Error = "the response had no url" });
        }

        string link = WithMediaExtension(url, mime);
        FileLog.Write($"RoeSnip: share upload: {_displayName} chunked upload finished: {link}");
        return new ShareUploadResult(true, link, null, finalize.Status, editToken);
    }

    /// <summary>The direct-media form of a share link for an embeddable mime (roeshare
    /// MIME_MEDIA_EXT); any other mime, or a link that already carries an extension, is returned
    /// unchanged. Share ids are [A-Za-z0-9_-], so a dot in the last segment is an extension.</summary>
    public static string WithMediaExtension(string url, string mime)
    {
        string? ext = mime.Split(';')[0].Trim().ToLowerInvariant() switch
        {
            "image/png" => "png",
            "image/jpeg" => "jpg",
            "image/gif" => "gif",
            "image/webp" => "webp",
            "image/avif" => "avif",
            "video/mp4" => "mp4",
            _ => null,
        };
        int lastSlash = url.LastIndexOf('/');
        if (ext is null || url.IndexOf('.', lastSlash + 1) >= 0)
        {
            return url;
        }
        return url + "." + ext;
    }

    private sealed record StepResult(bool Ok, int? Status, string Body, string? Error);

    private async Task<StepResult> SendAsync(
        string step, Func<HttpRequestMessage> build, bool retryAmbiguous, CancellationToken cancellationToken)
    {
        for (int attempt = 1; ; attempt++)
        {
            TimeSpan? wait;
            StepResult result;
            using (HttpRequestMessage request = build())
            {
                try
                {
                    using HttpResponseMessage response = await _client
                        .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                    string body = await ProviderSpecShareProvider.ReadResponseBodyAsync(response.Content, cancellationToken)
                        .ConfigureAwait(false);
                    int status = (int)response.StatusCode;
                    if (response.IsSuccessStatusCode)
                    {
                        return new StepResult(true, status, body, null);
                    }
                    result = new StepResult(false, status, body, null);
                    bool retriable = retryAmbiguous
                        ? status is 408 or 429 or >= 500
                        : status is 429 or 503;
                    if (!retriable || attempt >= MaxAttempts)
                    {
                        return result;
                    }
                    wait = RetryAfter(response) ?? Backoff(attempt);
                    FileLog.Write($"RoeSnip: share upload: {step} attempt {attempt} got {status}: {Truncate(body, 200)}; retrying in {wait.Value.TotalSeconds:0.#}s");
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // Includes HttpClient's own Timeout (a TaskCanceledException on a token nobody cancelled).
                    result = new StepResult(false, null, "", $"{ex.GetType().Name}: {ex.Message}");
                    FileLog.Write($"RoeSnip: share upload: {step} attempt {attempt} failed: {ex}");
                    if (!retryAmbiguous || attempt >= MaxAttempts)
                    {
                        return result;
                    }
                    wait = Backoff(attempt);
                }
            }
            await _delay(wait.Value, cancellationToken).ConfigureAwait(false);
        }
    }

    private ShareUploadResult Fail(string what, StepResult step)
    {
        string reason = step.Error
            ?? (string.IsNullOrWhiteSpace(step.Body) ? "request failed" : ReadString(step.Body, "error") ?? Truncate(step.Body, 300));
        string message = step.Status is { } status && step.Error is null
            ? $"{_displayName} returned {status} while {what}: {reason}"
            : $"{_displayName}: {what} failed: {reason}";
        FileLog.Write($"RoeSnip: share upload failed: {message}");
        return new ShareUploadResult(false, null, message, step.Status);
    }

    private static TimeSpan Backoff(int attempt) => TimeSpan.FromSeconds(Math.Min(16, 1 << (attempt - 1)));

    private static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        TimeSpan? delta = response.Headers.RetryAfter?.Delta;
        if (delta is null || delta.Value < TimeSpan.Zero)
        {
            return null;
        }
        return delta.Value > MaxRetryAfter ? MaxRetryAfter : delta.Value;
    }

    private static HttpRequestMessage WithEditToken(HttpRequestMessage message, string editToken)
    {
        message.Headers.TryAddWithoutValidation("X-Edit-Token", editToken);
        return message;
    }

    private static StringContent Json(Dictionary<string, object> body) =>
        new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

    private static string? ReadString(string body, string property)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(body);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty(property, out JsonElement value)
                && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static long? ReadLong(string body, string property)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(body);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty(property, out JsonElement value)
                && value.ValueKind == JsonValueKind.Number
                && value.TryGetInt64(out long n)
                ? n
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength] + "...";
}
