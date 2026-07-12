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
}
