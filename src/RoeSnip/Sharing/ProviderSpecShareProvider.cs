using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace RoeSnip.Sharing;

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
            return new ShareUploadResult(false, null,
                $"{DisplayName} is not configured correctly: '{expandedEndpoint}' is not a valid http(s) URL.");
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
            string expandedValue = TemplateExpander.Expand(headerTemplate, values);
            if (string.IsNullOrEmpty(expandedValue))
            {
                // A credential the user hasn't filled in yet (e.g. Imgur's Client ID) expands to
                // empty - omit the header entirely rather than sending "Authorization: Client-ID "
                // and letting the provider's own error message stand in for ours.
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
            response = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseContentRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw; // teardown/cancel - let the caller see this as a cancellation, not an upload failure
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
                body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
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

            return new ShareUploadResult(true, url, null, (int)response.StatusCode);
        }
    }

    private HttpContent BuildMultipartContent(ShareUploadRequest request, IReadOnlyDictionary<string, string> values)
    {
        var multipart = new MultipartFormDataContent();

        foreach (var (fieldName, fieldTemplate) in _spec.ExtraFields)
        {
            string expandedValue = TemplateExpander.Expand(fieldTemplate, values);
            if (string.IsNullOrEmpty(expandedValue))
            {
                // Same omit-if-empty rule as headers above - e.g. catbox's optional userhash: an
                // empty field is how catbox.moe's own docs say "upload anonymously", so sending the
                // field at all (even empty) would be wrong, not just superfluous.
                continue;
            }
            multipart.Add(new StringContent(expandedValue), fieldName);
        }

        var fileContent = new ByteArrayContent(request.Content);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(
            string.IsNullOrWhiteSpace(request.ContentType) ? "application/octet-stream" : request.ContentType);

        string fieldNameForFile = string.IsNullOrWhiteSpace(_spec.MultipartFieldName) ? "file" : _spec.MultipartFieldName;
        multipart.Add(fileContent, fieldNameForFile, request.FileName);
        return multipart;
    }

    private static HttpContent BuildRawBodyContent(ShareUploadRequest request)
    {
        var content = new ByteArrayContent(request.Content);
        content.Headers.ContentType = new MediaTypeHeaderValue(
            string.IsNullOrWhiteSpace(request.ContentType) ? "application/octet-stream" : request.ContentType);
        return content;
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
