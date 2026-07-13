using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace RoeSnip.Core.Sharing;

/// <summary>The convenience facade every UI-facing caller (ToolbarControl's Share split-button,
/// RecordingChrome's Reviewing-state Share button, SettingsWindow's Test button) is meant to go
/// through, instead of constructing a ProviderSpecShareProvider directly: resolves a
/// ShareProviderConfig against the catalog, checks the spec's known size ceiling BEFORE making any
/// network call, and runs the upload against one process-wide HttpClient (never one-per-call - same
/// convention App/UpdateManager.cs already uses for its own GitHub calls).
///
/// Deliberately takes the provider list / default id as plain parameters rather than a whole
/// RoeSnipSettings object: the WPF app and RoeSnip.Core.Settings.RoeSnipSettings are two distinct
/// settings records by design (see RoeSnip.Core.Settings.RoeSnipSettings' own doc comment for why
/// the two settings files stay separate), so a shared Core facade can't take a dependency on either
/// one's full shape - callers on both sides just pass their own settings' ShareProviders/
/// DefaultShareProviderId fields straight through.
///
/// Unit tests (ShareManagerTests) exercise this facade only against a mock HttpMessageHandler - no
/// test here opens a real socket; see TESTING.md's "Sharing/upload subsystem" section for what has
/// and hasn't been verified against a real provider.</summary>
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
    public static IReadOnlyList<ShareProviderConfig> EffectiveConfigs(IReadOnlyList<ShareProviderConfig> providers) =>
        ShareProviderCatalog.EffectiveConfigs(providers);

    /// <summary>The provider a plain (non-dropdown) Share click should upload to: the configured
    /// default if it still resolves to an enabled config, else the first enabled config in list
    /// order, else null (nothing configured yet - callers surface that as "no share provider is set
    /// up" rather than silently no-op'ing).</summary>
    public static ShareProviderConfig? ResolveDefault(IReadOnlyList<ShareProviderConfig> providers, string? defaultProviderId)
    {
        var effective = EffectiveConfigs(providers);
        if (defaultProviderId is { } defaultId)
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
