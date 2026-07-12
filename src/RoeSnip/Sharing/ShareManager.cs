using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace RoeSnip.Sharing;

/// <summary>The convenience facade every UI-facing caller (ToolbarControl's Share split-button,
/// RecordingChrome's Reviewing-state Share button, SettingsWindow's Test button) is meant to go
/// through, instead of constructing a ProviderSpecShareProvider directly: resolves a
/// ShareProviderConfig against the catalog, checks the spec's known size ceiling BEFORE making any
/// network call, and runs the upload against one process-wide HttpClient (never one-per-call - same
/// convention App/UpdateManager.cs already uses for its own GitHub calls).
///
/// NOTE on this codebase's current wiring state: this facade is fully implemented and unit-tested
/// against a mock HttpMessageHandler (see ShareManagerTests), but nothing calls it against the real
/// network yet, and no caller currently invokes it from a live UI click - wiring an actual UI click
/// all the way through to a live call requires changes in Overlay/OverlayWindow.xaml.cs (for the
/// toolbar's Share button - it needs to resolve the current selection's rendered bytes) and
/// Recording/RecordingController.cs (for the recording chrome's Share button - it owns the finished
/// take's temp file path), neither of which this facade or its callers touch (see those two
/// components' own doc comments for the events/hooks they expose for that future wiring).</summary>
public static class ShareManager
{
    private static readonly Lazy<HttpClient> SharedClient = new(CreateHttpClient);

    private static HttpClient CreateHttpClient()
    {
        // 60s was too tight for this catalog's own advertised payload sizes (litterbox alone allows
        // up to 1 GB) - any upload slower than that deterministically hit the timeout, which
        // ProviderSpecShareProvider now surfaces as a proper Success=false "upload timed out" result
        // rather than a misclassified cancellation, but a generous ceiling still means real users
        // with real bandwidth actually succeed instead of failing on every larger file. 10 minutes
        // comfortably covers a multi-hundred-MB upload on very ordinary consumer upstream while still
        // eventually giving up on a truly hung connection.
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("RoeSnip-Sharing");
        return client;
    }

    /// <summary>Every provider the settings UI / picker should show: persisted configs (built-in,
    /// touched-or-not, plus every custom one) with a seeded placeholder for any BuiltIns entry the
    /// user has never configured - see ShareProviderCatalog.EffectiveConfigs.</summary>
    public static IReadOnlyList<ShareProviderConfig> EffectiveConfigs(RoeSnipSettings settings) =>
        ShareProviderCatalog.EffectiveConfigs(settings.ShareProviders);

    /// <summary>The provider a plain (non-dropdown) Share click should upload to: the configured
    /// default if it still resolves to an enabled config, else the first enabled config in list
    /// order, else null (nothing configured yet - callers surface that as "no share provider is set
    /// up" rather than silently no-op'ing).</summary>
    public static ShareProviderConfig? ResolveDefault(RoeSnipSettings settings)
    {
        var effective = EffectiveConfigs(settings);
        if (settings.DefaultShareProviderId is { } defaultId)
        {
            var match = effective.FirstOrDefault(c => c.Enabled && string.Equals(c.Id, defaultId, StringComparison.Ordinal));
            if (match is not null)
            {
                return match;
            }
        }

        return effective.FirstOrDefault(c => c.Enabled);
    }

    /// <summary>Runs one upload. Validates the config resolves to a real spec and (when the spec
    /// declares one) that the content fits under MaxUploadBytes BEFORE touching the network - a
    /// deliberately fast, offline failure for the common "picked the wrong file" case rather than a
    /// slow doomed request. <paramref name="content"/> is a seekable Stream (MemoryStream for a
    /// rendered PNG, FileStream for a finished recording's temp file) - see ShareUploadRequest's own
    /// doc comment for why this is a stream and not a byte array. <paramref name="client"/> is
    /// test-only (defaults to the shared instance); production callers never pass it.</summary>
    public static async Task<ShareUploadResult> UploadAsync(
        ShareProviderConfig config,
        Stream content,
        string fileName,
        string contentType,
        CancellationToken cancellationToken,
        HttpClient? client = null)
    {
        ProviderSpec? spec = ShareProviderCatalog.ResolveSpec(config);
        if (spec is null)
        {
            return new ShareUploadResult(false, null,
                $"'{config.DisplayName}' is not configured (unknown or missing provider spec).");
        }

        if (spec.MaxUploadBytes is { } maxBytes && content.Length > maxBytes)
        {
            double limitMb = maxBytes / 1024.0 / 1024.0;
            double actualMb = content.Length / 1024.0 / 1024.0;
            return new ShareUploadResult(false, null,
                $"{spec.Name} allows at most {limitMb:0.#} MB; this file is {actualMb:0.#} MB.");
        }

        var provider = new ProviderSpecShareProvider(spec, config, client ?? SharedClient.Value);
        try
        {
            return await provider.UploadAsync(new ShareUploadRequest(content, fileName, contentType), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Belt-and-braces: ProviderSpecShareProvider already catches network/parse failures
            // (including its own Timeout-vs-real-cancellation disambiguation) into a Success=false
            // result, but this facade must never let an unexpected exception (e.g. a future spec
            // shape it doesn't handle yet, or - before the `when` clause above existed - a timeout
            // misread as the caller's own cancellation) become an unobserved-task-exception crash for
            // a fire-and-forget UI caller.
            return new ShareUploadResult(false, null, $"Upload failed: {ex.Message}");
        }
    }
}
