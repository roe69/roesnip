using System;
using System.Collections.Generic;
using System.Linq;

namespace RoeSnip.Core.Sharing;

/// <summary>The built-in provider specs, plus the small amount of logic that layers a user's
/// persisted <see cref="ShareProviderConfig"/> list on top of them. "It's just web requests" is the
/// design center of the whole Sharing package (ProviderSpec.cs): every built-in provider below is
/// DATA - endpoint, field names, headers, response-extraction rule - not a bespoke C# class. Adding
/// a provider (built-in or, via the settings UI, "Custom...") means filling in a ProviderSpec, never
/// writing an IShareProvider implementation; ProviderSpecShareProvider is the ONE piece of code that
/// executes any of them.
///
/// Each spec below was checked against the provider's current public docs/behavior during this
/// feature's implementation (2026-07); see each entry's own Notes for what was confirmed and what
/// wasn't. Anything that couldn't be pinned down precisely is marked Verified=false ("untested" in
/// the settings UI) rather than shipped as if it were equally solid - none of these specs has yet
/// been exercised over the real network by this codebase (see TESTING.md).</summary>
public static class ShareProviderCatalog
{
    /// <summary>RoeShare (E:\GitHub\RoeLite\roeshare) - the primary, self-hosted target. Uses the
    /// programmatic one-shot upload endpoint from CONTRACT.md's "Programmatic API (/api/v1)" section:
    /// the request body IS the raw file bytes (RawBody, not multipart), filename via the X-Filename
    /// header, auth via "Authorization: Bearer rsk_...". Response is
    /// <c>201 { id, url, fileId, name, size }</c> - "url" is the ready-to-share link
    /// (config.baseUrl + '/' + id). Bounded by the server's own max request body size; CONTRACT.md
    /// says larger files need the resumable /api/v1/shares + PATCH-chunks flow instead, which this
    /// one-shot spec deliberately does not attempt to model (a multi-request resumable protocol does
    /// not fit the flat "one POST, one response" ProviderSpec shape - a real future limitation, not
    /// an oversight).</summary>
    public static readonly ProviderSpec RoeShare = new()
    {
        Id = "roeshare",
        Name = "RoeShare",
        Endpoint = "{BaseUrl}/api/v1/upload",
        Method = "POST",
        UploadKind = ShareUploadKind.RawBody,
        Headers = new Dictionary<string, string>
        {
            ["Authorization"] = "Bearer {ApiKey}",
            ["X-Filename"] = "{Filename}",
        },
        MaxUploadBytes = null, // server-configured (config.maxFileSize) - not knowable statically
        ResponseMode = ResponseUrlMode.JsonPath,
        ResponseJsonPath = "url",
        // Owner-management secret RoeShare's own upload endpoint returns alongside "url" (see
        // CONTRACT.md's D1 addition) - absent on an older server, which is fine, see
        // ProviderSpec.ResponseEditTokenJsonPath's own doc comment.
        ResponseEditTokenJsonPath = "editToken",
        ConfigFields = new List<ShareConfigField>
        {
            new("BaseUrl", "Server URL (e.g. https://share.example.com, no trailing slash)", Required: true, IsSecret: false),
            new("ApiKey", "API key (rsk_...)", Required: true, IsSecret: true),
        },
        IsBuiltIn = true,
        Verified = true,
        Notes = "Your own RoeShare server. Create an API key from RoeShare's admin panel " +
                "(Admin > API keys). One-shot upload only - very large files may exceed the " +
                "server's request size limit; use RoeShare's web upload page for those.",
    };

    /// <summary>Imgur's anonymous upload - api.imgur.com/3/image, multipart field "image",
    /// Authorization: Client-ID {clientId}. Confirmed against current third-party documentation
    /// summaries of the v3 API (Imgur does not require full OAuth for anonymous image posts, only a
    /// registered app's Client ID). RoeSnip never bundles a shared/default Client ID - that would be
    /// either a fabricated value or someone else's credential shipped without their consent; the user
    /// registers their own free app.</summary>
    public static readonly ProviderSpec Imgur = new()
    {
        Id = "imgur",
        Name = "Imgur",
        Endpoint = "https://api.imgur.com/3/image",
        Method = "POST",
        UploadKind = ShareUploadKind.Multipart,
        MultipartFieldName = "image",
        Headers = new Dictionary<string, string> { ["Authorization"] = "Client-ID {ApiKey}" },
        MaxUploadBytes = 20L * 1024 * 1024, // documented still-image ceiling; animated GIFs allow more
        ResponseMode = ResponseUrlMode.JsonPath,
        ResponseJsonPath = "data.link",
        ConfigFields = new List<ShareConfigField>
        {
            new("ApiKey", "Client ID", Required: true, IsSecret: true),
        },
        IsBuiltIn = true,
        Verified = true,
        Notes = "Needs a free Client ID from api.imgur.com/oauth2/addclient (choose \"Anonymous " +
                "usage without user authorization\"). 20 MB limit is for still images; GIFs allow more.",
    };

    /// <summary>catbox.moe - https://catbox.moe/user/api.php, multipart, reqtype=fileupload +
    /// optional userhash (blank = anonymous upload; the app never sends an empty userhash field -
    /// see ProviderSpecShareProvider's omit-if-empty rule). Response body is the plain-text URL.</summary>
    public static readonly ProviderSpec Catbox = new()
    {
        Id = "catbox",
        Name = "catbox.moe",
        Endpoint = "https://catbox.moe/user/api.php",
        Method = "POST",
        UploadKind = ShareUploadKind.Multipart,
        MultipartFieldName = "fileToUpload",
        ExtraFields = new Dictionary<string, string>
        {
            ["reqtype"] = "fileupload",
            ["userhash"] = "{ApiKey}",
        },
        MaxUploadBytes = 200L * 1024 * 1024, // documented per-file limit
        ResponseMode = ResponseUrlMode.PlainBody,
        ConfigFields = new List<ShareConfigField>
        {
            new("ApiKey", "User hash (optional; blank uploads anonymously)", Required: false, IsSecret: true),
        },
        IsBuiltIn = true,
        Verified = true,
        Notes = "Permanent hosting, no account required. A user hash (from a free catbox.moe " +
                "account) lets you manage/delete your uploads later; anonymous uploads work too.",
    };

    /// <summary>litterbox.catbox.moe - the same operator's TEMPORARY-file sibling API. Same
    /// multipart shape as Catbox but a required "time" field (1h/12h/24h/72h) instead of a userhash.
    /// Plain-text URL response.</summary>
    public static readonly ProviderSpec Litterbox = new()
    {
        Id = "litterbox",
        Name = "Litterbox (temporary)",
        Endpoint = "https://litterbox.catbox.moe/resources/internals/api.php",
        Method = "POST",
        UploadKind = ShareUploadKind.Multipart,
        MultipartFieldName = "fileToUpload",
        ExtraFields = new Dictionary<string, string>
        {
            ["reqtype"] = "fileupload",
            ["time"] = "{Time}",
        },
        MaxUploadBytes = 1L * 1024 * 1024 * 1024, // documented per-file limit
        ResponseMode = ResponseUrlMode.PlainBody,
        ConfigFields = new List<ShareConfigField>
        {
            // DefaultValue seeds "1h" for the same reason it always did (the one required-but-not-
            // secret field with an obviously sensible default) - now via the generic DefaultConfigFor
            // loop instead of a Litterbox-specific branch, see that method's own doc comment.
            new("Time", "Expiry: 1h, 12h, 24h, or 72h", Required: true, IsSecret: false, DefaultValue: "1h"),
        },
        IsBuiltIn = true,
        Verified = true,
        Notes = "Files are deleted automatically after the chosen expiry (max 72 hours) - not for " +
                "anything you need to keep. No account, no delete/manage link.",
    };

    /// <summary>0x0.st - the simplest of the lot: POST multipart field "file" to the bare origin,
    /// plain-text URL back. The upload mechanics themselves are well-documented and confirmed; the
    /// service's exact size/retention limits are NOT published as a fixed number (retention scales
    /// down for larger files) so MaxUploadBytes is left null rather than guessed.</summary>
    public static readonly ProviderSpec ZeroXZeroSt = new()
    {
        Id = "0x0st",
        Name = "0x0.st",
        Endpoint = "https://0x0.st",
        Method = "POST",
        UploadKind = ShareUploadKind.Multipart,
        MultipartFieldName = "file",
        MaxUploadBytes = null,
        ResponseMode = ResponseUrlMode.PlainBody,
        IsBuiltIn = true,
        Verified = true,
        Notes = "No account, no size field published (retention shrinks for larger files - the " +
                "service does not commit to an exact ceiling).",
    };

    /// <summary>GoFile - unofficial/community documentation only (no first-party API reference was
    /// found to confirm the exact response JSON shape or whether a fixed "store1" server is always
    /// reachable versus GoFile's own docs recommending a dynamic server-selection call first, which
    /// this flat one-POST spec does not perform). Marked untested rather than presented as equally
    /// solid as the others.</summary>
    public static readonly ProviderSpec GoFile = new()
    {
        Id = "gofile",
        Name = "GoFile",
        Endpoint = "https://store1.gofile.io/contents/uploadfile",
        Method = "POST",
        UploadKind = ShareUploadKind.Multipart,
        MultipartFieldName = "file",
        ExtraFields = new Dictionary<string, string> { ["token"] = "{ApiKey}" },
        MaxUploadBytes = null,
        ResponseMode = ResponseUrlMode.JsonPath,
        ResponseJsonPath = "data.downloadPage",
        ConfigFields = new List<ShareConfigField>
        {
            new("ApiKey", "Account token (optional; blank = guest upload)", Required: false, IsSecret: true),
        },
        IsBuiltIn = true,
        Verified = false, // UNTESTED - see class doc comment
        Notes = "UNTESTED: only unofficial/community docs were available. GoFile's official flow " +
                "recommends querying an active server first rather than posting to a fixed one " +
                "(\"store1\") - this may not always work. Verify before relying on it.",
    };

    /// <summary>file.io - POST multipart field "file" to the bare origin, JSON response
    /// <c>{success, key, link, expiry}</c> - "link" is the URL. Confirmed against current
    /// documentation/behavior summaries.</summary>
    public static readonly ProviderSpec FileIo = new()
    {
        Id = "fileio",
        Name = "file.io",
        Endpoint = "https://file.io",
        Method = "POST",
        UploadKind = ShareUploadKind.Multipart,
        MultipartFieldName = "file",
        MaxUploadBytes = 4L * 1024 * 1024 * 1024,
        ResponseMode = ResponseUrlMode.JsonPath,
        ResponseJsonPath = "link",
        IsBuiltIn = true,
        Verified = true,
        Notes = "Free tier; files expire automatically (14 days by default, or possibly sooner - " +
                "file.io's policy has changed over time). Not for anything you need to keep.",
    };

    public static readonly IReadOnlyList<ProviderSpec> BuiltIns = new[]
    {
        RoeShare, Imgur, Catbox, Litterbox, ZeroXZeroSt, GoFile, FileIo,
    };

    public static ProviderSpec? FindBuiltIn(string specId) =>
        BuiltIns.FirstOrDefault(s => string.Equals(s.Id, specId, StringComparison.Ordinal));

    /// <summary>Resolves a config's effective spec: its own inline CustomSpec if IsCustom, otherwise
    /// a live lookup into BuiltIns by SpecId (never a stale copy - see ProviderSpec's own doc
    /// comment for why built-ins are looked up fresh instead of duplicated into settings.json).
    /// Null if a built-in config's SpecId doesn't match anything (a settings.json hand-edit, or a
    /// built-in retired in a later build).</summary>
    public static ProviderSpec? ResolveSpec(ShareProviderConfig config) =>
        config.IsCustom ? config.CustomSpec : FindBuiltIn(config.SpecId);

    /// <summary>A fresh, not-yet-configured config for a built-in spec: disabled, empty credential
    /// values except for any field that declares a <see cref="ShareConfigField.DefaultValue"/> (seeded
    /// verbatim), Id/SpecId both set to the spec's own Id so re-seeding an unconfigured built-in can
    /// never produce a duplicate row.
    ///
    /// RoeShare's ExpiresIn field is the reason this loop exists and must never be "simplified" back
    /// to a per-provider branch: its DefaultValue is the literal "0" (never expire), and a config that
    /// never gets this seed falls through to Endpoint templating expanding the missing token to an
    /// EMPTY string, which the server reads as "use the 7-day default" - see ProviderSpecShareProvider
    /// upload-time backfill's own comment for the second half of this defense (existing rows that
    /// predate the field entirely).</summary>
    public static ShareProviderConfig DefaultConfigFor(ProviderSpec spec)
    {
        var values = new Dictionary<string, string>();
        foreach (var field in spec.ConfigFields)
        {
            if (field.DefaultValue is not null)
            {
                values[field.Key] = field.DefaultValue;
            }
        }

        return new ShareProviderConfig
        {
            Id = spec.Id,
            SpecId = spec.Id,
            IsCustom = false,
            DisplayName = spec.Name,
            Values = values,
            Enabled = false,
        };
    }

    /// <summary>The full list the settings UI (and the toolbar/chrome provider pickers) shows: every
    /// built-in always in BuiltIns catalog declaration order (RoeShare, Imgur, Catbox, Litterbox,
    /// 0x0.st, GoFile, file.io), each slot filled by its persisted row if the user has configured it
    /// or a freshly-seeded disabled placeholder otherwise, followed by every custom config and any
    /// orphaned/unmatched persisted row (a settings.json hand-edit, or a retired built-in), in their
    /// persisted order. Display order is therefore stable and independent of touch history - enabling
    /// or configuring a built-in fills its existing catalog slot in place, it never jumps to the front.
    /// Persisted array order in settings.json is storage only, never display order. Same
    /// "seed defaults, persist only once edited" convention as SwatchPalette.EffectivePalette /
    /// ColorFormatCatalog.EffectiveFormats elsewhere in this app.</summary>
    public static IReadOnlyList<ShareProviderConfig> EffectiveConfigs(IReadOnlyList<ShareProviderConfig> persisted)
    {
        // Track consumed rows by index, not value/record equality - ShareProviderConfig is a record,
        // so two distinct persisted rows that happen to compare equal must not be conflated.
        var consumed = new bool[persisted.Count];
        var result = new List<ShareProviderConfig>(Math.Max(persisted.Count, BuiltIns.Count));

        foreach (var spec in BuiltIns)
        {
            var matchIndex = -1;
            for (var i = 0; i < persisted.Count; i++)
            {
                if (!consumed[i] && !persisted[i].IsCustom &&
                    string.Equals(persisted[i].SpecId, spec.Id, StringComparison.Ordinal))
                {
                    matchIndex = i;
                    break;
                }
            }

            if (matchIndex >= 0)
            {
                consumed[matchIndex] = true;
                result.Add(persisted[matchIndex]);
            }
            else
            {
                result.Add(DefaultConfigFor(spec));
            }
        }

        for (var i = 0; i < persisted.Count; i++)
        {
            if (!consumed[i])
            {
                result.Add(persisted[i]);
            }
        }

        return result;
    }
}
