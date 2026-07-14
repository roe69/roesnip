using System;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RoeSnip.Core.Sharing;

/// <summary>Pulls the uploaded file's public URL out of a provider's successful response body, per
/// the spec's own <see cref="ProviderSpec.ResponseMode"/>. The three modes cover every built-in
/// provider researched for this feature: JsonPath (Imgur "data.link", RoeShare "url", GoFile
/// "data.downloadPage", file.io "link"), PlainBody (catbox.moe, litterbox, 0x0.st all just print the
/// URL as the whole response), and Regex (for a "Custom..." provider whose response is neither of
/// the above - e.g. a URL embedded in a larger non-JSON body).</summary>
public static class ResponseUrlExtractor
{
    // Regex specs come from user-entered "Custom..." providers (see ShareProviderEditWindow) - bound
    // the match so a pathological pattern against a large/adversarial response body can't hang the
    // upload indefinitely (the same ReDoS concern any user-supplied regex deserves).
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);

    public static bool TryExtract(ProviderSpec spec, string body, out string? url, out string? error)
    {
        switch (spec.ResponseMode)
        {
            case ResponseUrlMode.PlainBody:
                return TryExtractPlainBody(body, out url, out error);

            case ResponseUrlMode.Regex:
                return TryExtractRegex(spec.ResponseRegex, body, out url, out error);

            case ResponseUrlMode.JsonPath:
            default:
                return TryExtractJsonPath(spec.ResponseJsonPath, body, out url, out error);
        }
    }

    private static bool TryExtractPlainBody(string body, out string? url, out string? error)
    {
        string trimmed = body.Trim();
        if (trimmed.Length == 0)
        {
            url = null;
            error = "The provider returned an empty response.";
            return false;
        }

        return TryValidateUrl(trimmed, out url, out error);
    }

    private static bool TryExtractRegex(string? pattern, string body, out string? url, out string? error)
    {
        if (string.IsNullOrEmpty(pattern))
        {
            url = null;
            error = "This provider has no response regex configured.";
            return false;
        }

        Match match;
        try
        {
            match = Regex.Match(body, pattern, RegexOptions.None, RegexTimeout);
        }
        catch (Exception ex) when (ex is ArgumentException or RegexMatchTimeoutException)
        {
            url = null;
            error = $"The response regex could not be applied: {ex.Message}";
            return false;
        }

        if (!match.Success || match.Groups.Count < 2 || !match.Groups[1].Success)
        {
            url = null;
            error = "The provider's response did not match the expected pattern.";
            return false;
        }

        return TryValidateUrl(match.Groups[1].Value, out url, out error);
    }

    private static bool TryExtractJsonPath(string? path, string body, out string? url, out string? error)
    {
        if (string.IsNullOrEmpty(path))
        {
            url = null;
            error = "This provider has no response JSON path configured.";
            return false;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            url = null;
            error = $"The provider's response was not valid JSON: {ex.Message}";
            return false;
        }

        using (document)
        {
            if (!TryWalkPath(document.RootElement, path, out string? value) || string.IsNullOrEmpty(value))
            {
                url = null;
                error = $"Could not find '{path}' in the provider's response.";
                return false;
            }

            return TryValidateUrl(value, out url, out error);
        }
    }

    /// <summary>Last-mile check shared by all three ResponseMode branches above: the extracted string
    /// must be an absolute http(s) URL before it is ever treated as a trusted upload result. Without
    /// this, a malicious/compromised provider - or a MITM on a custom provider configured over plain
    /// http, which this engine's own endpoint validation explicitly allows - could return something
    /// like a UNC path or a "file:"/"ms-appinstaller:" URI as if it were the uploaded file's public
    /// link; callers report Success=true, copy the value to the clipboard verbatim, and ShellExecute
    /// it on the ShareResultWindow's Open button click, so an unvalidated "URL" here is a direct path
    /// to running attacker-controlled input.</summary>
    private static bool TryValidateUrl(string candidate, out string? url, out string? error)
    {
        if (Uri.TryCreate(candidate, UriKind.Absolute, out Uri? uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            url = candidate;
            error = null;
            return true;
        }

        url = null;
        error = "The provider's response did not contain a valid http(s) URL.";
        return false;
    }

    /// <summary>Tolerant JSON-path walk for an OPTIONAL response field (currently just RoeShare's
    /// <c>editToken</c> - see ProviderSpec.ResponseEditTokenJsonPath) where absence must never fail the
    /// upload, unlike <see cref="TryExtract"/>'s URL path which IS the primary contract. Returns null on
    /// ANY problem - no path configured, unparseable body, path not found, or a blank/non-string leaf -
    /// rather than an (error, out) pair, because callers never need to distinguish those cases: they all
    /// mean exactly the same thing here, "nothing to carry forward".</summary>
    public static string? TryExtractOptionalJsonPath(string? path, string body)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            if (TryWalkPath(document.RootElement, path, out string? value) && !string.IsNullOrEmpty(value))
            {
                return value;
            }
        }
        catch (JsonException)
        {
            // Tolerated - an older/different server whose response isn't JSON at all still succeeded
            // at the primary URL extraction (e.g. a PlainBody-mode provider); this optional field
            // simply isn't present for it.
        }

        return null;
    }

    /// <summary>Walks a dotted path ("data.link") through nested JSON objects. Only object-property
    /// traversal is supported (no array indexing) - every built-in spec's response shape is a plain
    /// nested object, and keeping this to the minimum that's actually needed matches the rest of the
    /// Sharing package's "no more than the built-ins need" scope.</summary>
    private static bool TryWalkPath(JsonElement root, string path, out string? value)
    {
        JsonElement current = root;
        foreach (string segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out JsonElement next))
            {
                value = null;
                return false;
            }
            current = next;
        }

        value = current.ValueKind switch
        {
            JsonValueKind.String => current.GetString(),
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => current.GetRawText(),
        };
        return true;
    }
}
