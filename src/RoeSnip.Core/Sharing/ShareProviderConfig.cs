using System.Collections.Generic;

namespace RoeSnip.Core.Sharing;

/// <summary>One configured provider INSTANCE - persisted in a settings record's own ShareProviders
/// list (RoeSnip.Core.Settings.RoeSnipSettings on this port; the WPF app's own RoeSnipSettings record
/// carries the same field independently - see that record's own doc comment for why the two settings
/// files stay separate). For a built-in provider this is just "which spec (<see cref="SpecId"/>)
/// plus which credential/URL values the user typed in"; the spec's own request shape (endpoint,
/// headers, field names, response extraction) is never duplicated here, it's looked up fresh from
/// ShareProviderCatalog.BuiltIns every time (ShareProviderCatalog.ResolveSpec) - so a future RoeSnip
/// build that fixes a built-in spec's endpoint automatically applies without touching the user's
/// settings.json. A Custom entry instead carries its own whole <see cref="CustomSpec"/> inline, since
/// there is no catalog entry for it to reference.</summary>
public sealed record ShareProviderConfig
{
    /// <summary>Stable identity within the settings list. Built-in configs use their spec's own Id
    /// (ShareProviderCatalog.DefaultConfigFor) so re-seeding a not-yet-configured built-in can never
    /// duplicate it; custom configs get a fresh GUID at creation.</summary>
    public string Id { get; init; } = "";

    /// <summary>Matches a ShareProviderCatalog.BuiltIns entry's Id. Ignored when <see cref="IsCustom"/>
    /// is true (kept populated anyway, mirroring Id, purely for readability of a hand-edited
    /// settings.json - never read in that case).</summary>
    public string SpecId { get; init; } = "";

    public bool IsCustom { get; init; }

    /// <summary>Only set (and only meaningful) when <see cref="IsCustom"/> is true - the user's own
    /// full ProviderSpec, edited via ShareProviderEditWindow's "Custom..." form.</summary>
    public ProviderSpec? CustomSpec { get; init; }

    /// <summary>Shown in the settings UI and the toolbar/chrome's provider picker. Defaults to the
    /// spec's own Name; only meaningfully DIFFERENT for a custom provider, or a built-in the user has
    /// renamed for their own reference (e.g. two RoeShare configs against different servers).</summary>
    public string DisplayName { get; init; } = "";

    /// <summary>Template variable values keyed by ShareConfigField.Key (e.g. "ApiKey", "BaseUrl",
    /// "Time") - substituted into the resolved spec's Endpoint/Headers/ExtraFields templates by
    /// TemplateExpander at upload time. Stored PLAINTEXT, same as every other settings field -
    /// the settings UI says so explicitly next to any secret field rather than implying a protection
    /// this build doesn't actually apply.</summary>
    public Dictionary<string, string> Values { get; init; } = new();

    /// <summary>Whether this provider is offered as an upload target at all (toolbar/chrome picker,
    /// default-provider dropdown). A built-in with no credential filled in yet is seeded disabled
    /// (see ShareProviderCatalog.DefaultConfigFor) so the settings UI can list every built-in without
    /// any of them being silently usable half-configured.</summary>
    public bool Enabled { get; init; }
}
