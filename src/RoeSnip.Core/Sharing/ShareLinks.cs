using System;

namespace RoeSnip.Core.Sharing;

/// <summary>Small, pure helpers for turning a completed <see cref="ShareUploadResult"/> into the two
/// different link strings the result UI shows (see ROESNIP SHARE UX's "OPEN vs COPY SEMANTICS"):
/// Copy/the displayed link text always use the clean URL, only the Open action carries the owner's
/// manage secret, and only as a URL FRAGMENT (never a query string - fragments never leave the
/// browser, so they never hit a proxy log or Referer header the way a query param would).</summary>
public static class ShareLinks
{
    /// <summary>The URL for the Open action: the share's clean URL with <c>#edit=&lt;token&gt;</c>
    /// appended when an edit token is present, unchanged otherwise. Percent-encodes the token
    /// (Uri.EscapeDataString) so it round-trips safely as a fragment regardless of what characters the
    /// server's token alphabet happens to use.</summary>
    public static string ComposeOpenUrl(string url, string? editToken) =>
        string.IsNullOrWhiteSpace(editToken) ? url : url + "#edit=" + Uri.EscapeDataString(editToken);

    /// <summary>The URL for Copy / the displayed link text / the auto-clipboard-copy on success: always
    /// the plain share URL, never the edit token. A passthrough today, but named and called explicitly
    /// (rather than callers just using the raw Url field) so the "Copy never carries the secret"
    /// decision reads as deliberate at every call site, not an accident of which field happened to be
    /// at hand.</summary>
    public static string CleanUrl(string url) => url;
}
