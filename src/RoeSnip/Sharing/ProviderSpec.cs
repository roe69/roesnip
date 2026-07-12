using System.Collections.Generic;

namespace RoeSnip.Sharing;

/// <summary>How the file bytes travel to the provider. Every built-in provider researched for this
/// feature is one of these two shapes ("it's just web requests" - see the Sharing package's own
/// design note in ShareProviderCatalog.cs): a normal multipart/form-data file upload (Imgur,
/// catbox.moe, litterbox, 0x0.st, GoFile, file.io), or a raw-bytes POST body with metadata carried
/// in headers (RoeShare's <c>/api/v1/upload</c> one-shot endpoint - see CONTRACT.md in the roeshare
/// repo).</summary>
public enum ShareUploadKind
{
    Multipart,
    RawBody,
}

/// <summary>How the uploaded file's public URL is pulled out of a successful response body.</summary>
public enum ResponseUrlMode
{
    /// <summary>Parse the body as JSON and walk a dotted path (e.g. "data.link") to a string value.</summary>
    JsonPath,

    /// <summary>Match the body against a regex; the URL is the first capture group.</summary>
    Regex,

    /// <summary>The entire (trimmed) response body IS the URL - several of these services just
    /// print the link as plain text (catbox.moe, litterbox, 0x0.st).</summary>
    PlainBody,
}

/// <summary>One user-facing, per-provider settings field (e.g. "API key", "Server URL") that a
/// <see cref="ProviderSpec"/> declares it needs. Purely descriptive - <see cref="ShareProviderConfig.Values"/>
/// holds the actual values, keyed by <see cref="Key"/>, which is also the template variable name
/// (<c>{Key}</c>) that <see cref="TemplateExpander"/> substitutes into the spec's Endpoint/Headers/
/// ExtraFields templates. <see cref="Required"/> is advisory (shown as a hint in the settings UI,
/// e.g. "optional; blank = anonymous upload") - the templating layer never hard-fails on a missing
/// value, it just expands the token to an empty string (see TemplateExpander), and
/// <see cref="ProviderSpecShareProvider"/> omits any header/extra-field whose expansion comes out
/// empty rather than sending a broken "Authorization: Client-ID " or similar.</summary>
public sealed record ShareConfigField(string Key, string Label, bool Required, bool IsSecret);

/// <summary>The whole declarative description of a share-upload endpoint: this IS the "it's just web
/// requests" design center the Sharing package is built around. A built-in provider is DATA (see
/// ShareProviderCatalog.BuiltIns), not code - adding a new one, or letting a user add "Custom..." via
/// the settings UI, means filling in this record, never writing a new C# class. The whole record is
/// serialized as-is into settings.json for a custom provider (<see cref="ShareProviderConfig.CustomSpec"/>);
/// built-in providers are looked up by <see cref="Id"/> instead, so a future RoeSnip build's own spec
/// corrections/fixes automatically apply to every user who never touched that provider's config
/// (only the user-entered credential/URL VALUES live in settings.json for those - see
/// ShareProviderConfig).</summary>
public sealed record ProviderSpec
{
    /// <summary>Stable identity. For a built-in, this is also the key ShareProviderConfig.SpecId
    /// looks up (ShareProviderCatalog.FindBuiltIn) - never rename an existing built-in's Id, that
    /// would silently orphan every user's saved config for it.</summary>
    public string Id { get; init; } = "";

    public string Name { get; init; } = "";

    /// <summary>The request URL. May reference <c>{Key}</c> tokens matching a <see cref="ShareConfigField"/>
    /// (e.g. RoeShare's own spec templates in <c>{BaseUrl}</c>) - expanded by TemplateExpander before
    /// every request, never cached, so an edited BaseUrl/etc. takes effect on the very next upload.</summary>
    public string Endpoint { get; init; } = "";

    public string Method { get; init; } = "POST";

    public ShareUploadKind UploadKind { get; init; } = ShareUploadKind.Multipart;

    /// <summary>The multipart form field name the file itself is attached under (e.g. Imgur's
    /// "image", catbox's "fileToUpload"). Only meaningful when <see cref="UploadKind"/> is Multipart;
    /// defaults to "file" if left blank.</summary>
    public string? MultipartFieldName { get; init; }

    /// <summary>Constant or templated multipart form fields sent alongside the file (e.g. catbox's
    /// <c>reqtype=fileupload</c>, litterbox's <c>time={Time}</c>). Only meaningful for Multipart.
    /// A field whose expanded value comes out empty (an unfilled optional ShareConfigField) is
    /// OMITTED from the request entirely, not sent as an empty field - see
    /// ProviderSpecShareProvider.BuildMultipartContent.</summary>
    public Dictionary<string, string> ExtraFields { get; init; } = new();

    /// <summary>HTTP headers, name -> templated value (e.g. RoeShare's
    /// <c>Authorization: Bearer {ApiKey}</c>, Imgur's <c>Authorization: Client-ID {ApiKey}</c>).
    /// Same omit-if-empty-after-expansion rule as ExtraFields.</summary>
    public Dictionary<string, string> Headers { get; init; } = new();

    /// <summary>Best-known upload size ceiling in bytes, checked client-side BEFORE any network call
    /// (ShareManager.UploadAsync) so an oversized file fails fast with a clear message instead of a
    /// slow doomed upload. Null when the provider's limit is server-configured/unknown/variable (e.g.
    /// RoeShare's own <c>maxFileSize</c> lives in ITS <c>/api/config</c>, not something this static
    /// spec can know) - in that case the provider's own error response is what surfaces the limit.</summary>
    public long? MaxUploadBytes { get; init; }

    public ResponseUrlMode ResponseMode { get; init; } = ResponseUrlMode.JsonPath;

    /// <summary>Dotted path into the JSON response body, e.g. "data.link". Used when ResponseMode is
    /// JsonPath.</summary>
    public string? ResponseJsonPath { get; init; }

    /// <summary>Regex applied to the raw response body; group 1 is the URL. Used when ResponseMode is
    /// Regex.</summary>
    public string? ResponseRegex { get; init; }

    /// <summary>Per-provider settings fields shown in the settings UI (ShareProviderEditWindow),
    /// e.g. RoeShare needs BaseUrl + ApiKey, Imgur needs just a Client ID, several need nothing at
    /// all. Empty for a provider with no configurable credential.</summary>
    public List<ShareConfigField> ConfigFields { get; init; } = new();

    /// <summary>True for the seven ProviderSpec.BuiltIns entries; false for anything a user typed in
    /// via "Custom...". Drives whether ShareProviderEditWindow lets the spec fields themselves be
    /// edited (only Custom ones can be) versus just the ConfigFields values.</summary>
    public bool IsBuiltIn { get; init; }

    /// <summary>False means "could not be confirmed against the provider's current public docs
    /// during this feature's implementation" (per the track brief: never ship a guess as fact) - the
    /// settings UI shows an explicit "untested" badge rather than silently presenting it as
    /// equally trustworthy to the verified ones. See each BuiltIns entry's own Notes for specifics.</summary>
    public bool Verified { get; init; } = true;

    /// <summary>Free-text shown in the settings UI under this provider's row - caveats, size limits
    /// that couldn't be pinned down precisely, credential-acquisition instructions, etc.</summary>
    public string? Notes { get; init; }
}
