using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace RoeSnip.Sharing;

/// <summary>Expands <c>{VarName}</c> tokens in a ProviderSpec's Endpoint/Headers/ExtraFields
/// templates against a per-upload value set (a provider config's saved credential/URL values, plus
/// the current upload's own Filename - see ProviderSpecShareProvider). Deliberately tiny: no nested
/// lookups, no expressions, no escaping syntax - "it's just web requests" extends to the templating
/// layer too. An unknown token expands to an empty string rather than throwing or leaving the literal
/// "{Foo}" in the request, so a spec/config mismatch degrades to "field omitted" (see the
/// omit-if-empty rule in ProviderSpecShareProvider) instead of a broken request going out with a
/// literal placeholder baked into it.</summary>
public static class TemplateExpander
{
    // [A-Za-z0-9_]+ mirrors ShareConfigField.Key's expected shape (plain identifier-like names:
    // ApiKey, BaseUrl, Filename, Time, ...) - never needs to match anything more exotic.
    private static readonly Regex TokenPattern = new(@"\{([A-Za-z0-9_]+)\}", RegexOptions.Compiled);

    public static string Expand(string template, IReadOnlyDictionary<string, string> values)
    {
        if (string.IsNullOrEmpty(template))
        {
            return string.Empty;
        }

        return TokenPattern.Replace(template, match =>
            values.TryGetValue(match.Groups[1].Value, out string? value) ? value : string.Empty);
    }

    /// <summary>Same substitution as <see cref="Expand"/>, but also reports whether EVERY token the
    /// template referenced actually had a non-empty value - used by ProviderSpecShareProvider to
    /// decide whether to omit a header/extra-field entirely (e.g. Imgur's
    /// "Client-ID {ApiKey}": a blank ApiKey must omit the WHOLE header, not send a broken
    /// "Client-ID " prefix with nothing after it) rather than only catching the narrower case where
    /// the template is nothing but a bare token (catbox's userhash: "{ApiKey}"). A template with no
    /// tokens at all (a pure literal, e.g. "fileupload") is always considered fully resolved -
    /// nothing in it depends on config values, so there is nothing to omit for.</summary>
    public static bool TryExpand(string template, IReadOnlyDictionary<string, string> values, out string result)
    {
        if (string.IsNullOrEmpty(template))
        {
            result = string.Empty;
            return true;
        }

        bool allTokensResolved = true;
        result = TokenPattern.Replace(template, match =>
        {
            if (values.TryGetValue(match.Groups[1].Value, out string? value) && !string.IsNullOrEmpty(value))
            {
                return value;
            }
            allTokensResolved = false;
            return string.Empty;
        });
        return allTokensResolved;
    }
}
