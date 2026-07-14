using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RoeSnip.Core.Sharing;

/// <summary>The one piece of code that executes ANY <see cref="ProviderSpec"/> - built-in or custom,
/// multipart or raw-body. Every built-in provider's own quirk (RoeShare's raw-body + X-Filename
/// header, catbox's constant reqtype field, litterbox's Time field, Imgur's Client-ID auth header) is
/// data the spec carries, not a branch in this class; the only kind-level branch is
/// <see cref="ProviderSpec.UploadKind"/> (Multipart vs RawBody), which is a genuine wire-format
/// difference, not a per-provider one.
///
/// Takes its HttpClient by constructor injection (never creates its own) so tests can point it at a
/// mock HttpMessageHandler with zero real network access - see ProviderSpecShareProviderTests.</summary>
public sealed class ProviderSpecShareProvider : IShareProvider
{
    private readonly ProviderSpec _spec;
    private readonly ShareProviderConfig _config;
    private readonly HttpClient _client;

    public ProviderSpecShareProvider(ProviderSpec spec, ShareProviderConfig config, HttpClient client)
    {
        _spec = spec;
        _config = config;
        _client = client;
    }

    public string DisplayName => string.IsNullOrWhiteSpace(_config.DisplayName) ? _spec.Name : _config.DisplayName;

    public async Task<ShareUploadResult> UploadAsync(ShareUploadRequest request, CancellationToken cancellationToken)
    {
        var values = new Dictionary<string, string>(_config.Values, StringComparer.OrdinalIgnoreCase)
        {
            ["Filename"] = request.FileName,
        };

        string expandedEndpoint = TemplateExpander.Expand(_spec.Endpoint, values);
        if (!Uri.TryCreate(expandedEndpoint, UriKind.Absolute, out Uri? endpointUri)
            || (endpointUri.Scheme != Uri.UriSchemeHttp && endpointUri.Scheme != Uri.UriSchemeHttps))
        {
            // Report the UN-expanded template, not expandedEndpoint: a spec whose endpoint carries a
            // credential (query-param auth like "{BaseUrl}/upload?key={ApiKey}" is a common provider
            // shape the templating explicitly supports) would otherwise leak that credential verbatim
            // into ErrorMessage, which callers (tray balloons, TestStatusText) show on screen as-is.
            return new ShareUploadResult(false, null,
                $"{DisplayName} is not configured correctly: '{_spec.Endpoint}' does not expand to a valid http(s) URL.");
        }

        string method = string.IsNullOrWhiteSpace(_spec.Method) ? "POST" : _spec.Method;
        using var httpRequest = new HttpRequestMessage(new HttpMethod(method), endpointUri)
        {
            Content = _spec.UploadKind == ShareUploadKind.Multipart
                ? BuildMultipartContent(request, values)
                : BuildRawBodyContent(request),
        };

        foreach (var (headerName, headerTemplate) in _spec.Headers)
        {
            // TryExpand (not the plain Expand Endpoint uses above): a credential the user hasn't
            // filled in yet (e.g. Imgur's Client ID) must omit the WHOLE header, not send
            // "Authorization: Client-ID " with nothing after it - see TryExpand's own doc comment
            // for why a plain empty-string check on the final result isn't enough here.
            if (!TemplateExpander.TryExpand(headerTemplate, values, out string expandedValue))
            {
                continue;
            }

            if (!TrySetHeader(httpRequest, headerName, expandedValue))
            {
                return new ShareUploadResult(false, null, $"{DisplayName}: invalid header '{headerName}'.");
            }
        }

        HttpResponseMessage response;
        try
        {
            // ResponseHeadersRead (not ResponseContentRead): lets ReadResponseBodyAsync below bound
            // how much of the body it actually buffers, instead of HttpClient already having read
            // the whole thing (up to its own 2GB MaxResponseContentBufferSize default) before our
            // code even gets control - see ReadResponseBodyAsync's own doc comment.
            response = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // teardown/cancel - let the caller see this as a cancellation, not an upload failure
        }
        catch (OperationCanceledException ex)
        {
            // The token the CALLER gave us was never cancelled - this is HttpClient's own Timeout
            // (60s, see ShareManager.CreateHttpClient) firing, which .NET surfaces as a
            // TaskCanceledException (an OperationCanceledException subclass) indistinguishable from a
            // real cancellation by type alone. Without this branch that timeout would rethrow as if
            // the caller cancelled - ShareManager's own OCE rethrow would then propagate it further,
            // defeating the "never let an unexpected exception escape as an unobserved-task-exception
            // crash for a fire-and-forget UI caller" guarantee that facade documents.
            return new ShareUploadResult(false, null, $"{DisplayName}: upload timed out: {ex.Message}");
        }
        catch (Exception ex)
        {
            return new ShareUploadResult(false, null, $"{DisplayName}: upload request failed: {ex.Message}");
        }

        using (response)
        {
            string body;
            try
            {
                body = await ReadResponseBodyAsync(response.Content, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return new ShareUploadResult(false, null,
                    $"{DisplayName}: could not read the response: {ex.Message}", (int)response.StatusCode);
            }

            if (!response.IsSuccessStatusCode)
            {
                string reason = string.IsNullOrWhiteSpace(body) ? response.ReasonPhrase ?? "request failed" : Truncate(body, 300);
                return new ShareUploadResult(false, null,
                    $"{DisplayName} returned {(int)response.StatusCode}: {reason}", (int)response.StatusCode);
            }

            if (!ResponseUrlExtractor.TryExtract(_spec, body, out string? url, out string? extractError))
            {
                return new ShareUploadResult(false, null,
                    $"{DisplayName}: {extractError ?? "could not find a URL in the response."}", (int)response.StatusCode);
            }

            // Optional, best-effort - absence (an older server, or a spec that doesn't declare the
            // path at all) is not an error; see ResponseEditTokenJsonPath's own doc comment.
            string? editToken = ResponseUrlExtractor.TryExtractOptionalJsonPath(_spec.ResponseEditTokenJsonPath, body);

            return new ShareUploadResult(true, url, null, (int)response.StatusCode, editToken);
        }
    }

    private HttpContent BuildMultipartContent(ShareUploadRequest request, IReadOnlyDictionary<string, string> values)
    {
        var multipart = new MultipartFormDataContent();

        foreach (var (fieldName, fieldTemplate) in _spec.ExtraFields)
        {
            // Same TryExpand omit-if-unresolved rule as headers above - e.g. catbox's optional
            // userhash ("{ApiKey}"): an unfilled field is how catbox.moe's own docs say "upload
            // anonymously", so sending the field at all (even empty) would be wrong, not just
            // superfluous.
            if (!TemplateExpander.TryExpand(fieldTemplate, values, out string expandedValue))
            {
                continue;
            }
            multipart.Add(new StringContent(expandedValue), fieldName);
        }

        var fileContent = new StreamContent(request.Content);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(
            string.IsNullOrWhiteSpace(request.ContentType) ? "application/octet-stream" : request.ContentType);
        // StreamContent leaves Content-Length unset (falling back to chunked transfer encoding)
        // unless told otherwise - set it explicitly from the stream's own Length so a plain
        // Content-Length upload (what every built-in provider here expects) is what actually goes
        // out, same as ByteArrayContent always did implicitly.
        if (request.Content.CanSeek)
        {
            fileContent.Headers.ContentLength = request.Content.Length;
        }

        string fieldNameForFile = string.IsNullOrWhiteSpace(_spec.MultipartFieldName) ? "file" : _spec.MultipartFieldName;
        multipart.Add(fileContent, fieldNameForFile, request.FileName);
        return multipart;
    }

    private static HttpContent BuildRawBodyContent(ShareUploadRequest request)
    {
        var content = new StreamContent(request.Content);
        content.Headers.ContentType = new MediaTypeHeaderValue(
            string.IsNullOrWhiteSpace(request.ContentType) ? "application/octet-stream" : request.ContentType);
        if (request.Content.CanSeek)
        {
            content.Headers.ContentLength = request.Content.Length;
        }
        return content;
    }

    // Regex ReDoS protection already bounds ResponseUrlExtractor's regex mode (see its own
    // RegexTimeout); this is the matching bound for the OTHER half of "don't let a misbehaving or
    // hostile provider make the resident tray app buffer an unbounded response" - a share-URL
    // response needs at most a few KB, so this cap is generous, not tight. Reading via
    // ResponseHeadersRead + a manually bounded stream copy (rather than
    // HttpContent.ReadAsStringAsync, which would let HttpClient buffer up to its own 2GB
    // MaxResponseContentBufferSize default before this code ever runs) keeps a hostile multi-GB
    // response from ever landing fully in memory in the first place.
    private const int MaxResponseBytes = 1024 * 1024; // 1 MB

    private static async Task<string> ReadResponseBodyAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using Stream stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > MaxResponseBytes)
            {
                throw new InvalidOperationException($"response exceeded the {MaxResponseBytes / 1024} KB cap");
            }
        }
        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    /// <summary>Content-Type (and other content-level headers) live on HttpContent.Headers, not
    /// HttpRequestMessage.Headers - TryAddWithoutValidation on the request silently no-ops for those,
    /// so a spec that (unusually) wants to override Content-Type via its Headers dict is routed to
    /// the right collection instead of being quietly dropped.</summary>
    private static bool TrySetHeader(HttpRequestMessage request, string name, string value)
    {
        if (string.Equals(name, "Content-Type", StringComparison.OrdinalIgnoreCase))
        {
            if (request.Content is null)
            {
                return false;
            }
            try
            {
                request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(value);
                return true;
            }
            catch (FormatException)
            {
                return false;
            }
        }

        return request.Headers.TryAddWithoutValidation(name, value);
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength] + "...";
}
