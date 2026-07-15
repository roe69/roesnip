using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using RoeSnip.App.Overlay;
using RoeSnip.Core.Capture;
using RoeSnip.Core.Diagnostics;
using RoeSnip.Core.Sharing;

namespace RoeSnip.App.Sharing;

/// <summary>Owns the whole "click Share -&gt; progress -&gt; result" lifecycle (ROESNIP SHARE UX) for
/// every Avalonia call site (ToolbarControl's Share/dropdown, RecordingChrome's Share) - each call
/// site shrinks to "prepare the payload, call StartUpload" (plus, for the overlay paths, an immediate
/// Finish() the way Copy already does; see OverlayController). Mirrors the WPF app's identically-
/// named class field-for-field; see that class's own doc comment for why this takes a
/// <see cref="ShareProviderConfig"/> rather than the track brief's illustrative bare
/// IShareProvider signature (ShareManager.UploadAsync's pre-network MaxUploadBytes check).
///
/// This runs on the Avalonia UI thread already (every one of its call sites does - see
/// OverlayController's own doc comments), so the upload's continuation is marshalled back via
/// Dispatcher.UIThread.Post rather than relied on to resume there via a bare `await`, mirroring
/// OverlayController's own (now-removed) UploadShareAsync/FinishShareUploadAsync pair.</summary>
public static class ShareFlowPresenter
{
    /// <summary>Fires one upload: creates and shows a <see cref="ShareResultWindow"/> immediately
    /// (Uploading state), then runs the upload detached - never awaited by callers. See the WPF app's
    /// identically-named method for the full parameter contract (stream ownership, onSuccess/
    /// onFailure semantics). <paramref name="notifier"/>: if the user closes the toast (X/Esc) while
    /// the upload is still in flight, the Failure state that would otherwise name a kept recording's
    /// path renders into a window nobody can see - when that happens AND
    /// <paramref name="keptFilePathOnFailure"/> is non-null, the message is also surfaced via
    /// <see cref="RoeSnip.App.ITrayNotifier.ShowError"/> so it is never silently lost. Mirrors the WPF
    /// app's identically-named parameter.</summary>
    public static void StartUpload(
        ShareProviderConfig config,
        ShareUploadRequest request,
        MonitorInfo? monitor,
        string? keptFilePathOnFailure,
        Action? onSuccess,
        Action? onFailure,
        RoeSnip.App.ITrayNotifier? notifier = null)
    {
        string providerName = string.IsNullOrWhiteSpace(config.DisplayName)
            ? (ShareProviderCatalog.ResolveSpec(config)?.Name ?? "Share")
            : config.DisplayName;

        var window = new ShareResultWindow(providerName, monitor);
        var cts = new CancellationTokenSource();
        window.ShowUploading(() => cts.Cancel());
        window.Show();

        _ = RunUploadAsync(window, config, request, cts, keptFilePathOnFailure, onSuccess, onFailure, notifier);
    }

    private static async Task RunUploadAsync(
        ShareResultWindow window,
        ShareProviderConfig config,
        ShareUploadRequest request,
        CancellationTokenSource cts,
        string? keptFilePathOnFailure,
        Action? onSuccess,
        Action? onFailure,
        RoeSnip.App.ITrayNotifier? notifier)
    {
        ShareUploadResult result;
        try
        {
            result = await ShareManager
                .UploadAsync(config, request.Content, request.FileName, request.ContentType, cts.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            result = new ShareUploadResult(false, null, "Upload cancelled.");
        }
        catch (Exception ex)
        {
            result = new ShareUploadResult(false, null, $"Upload failed: {ex.Message}");
        }
        finally
        {
            // Ownership of the stream transferred here for the duration of the upload - released
            // now, BEFORE onSuccess/onFailure run, so a recording's temp-file delete (the caller's
            // onSuccess) never races a still-open FileStream handle on the same path.
            try { request.Content.Dispose(); }
            catch (Exception ex) { FileLog.Write($"RoeSnip: share upload stream dispose failed (non-fatal): {ex.Message}"); }
        }

        // Discarded: DispatcherOperation is awaitable, but there is nothing further to do once the
        // UI-thread callback is QUEUED - mirrors OverlayController's own former
        // UploadShareAsync/FinishShareUploadAsync pair (Dispatcher.UIThread.Post(() => _ =
        // FinishShareUploadAsync(...))), which this same `() => _ = ...` shape follows rather than
        // posting an async lambda directly (an async-void Post callback's exceptions would otherwise
        // vanish unobserved instead of at least reaching Console.Error below).
        Dispatcher.UIThread.Post(() => _ = FinishOnUiThreadAsync(window, result, keptFilePathOnFailure, onSuccess, onFailure, notifier));
    }

    private static async Task FinishOnUiThreadAsync(
        ShareResultWindow window,
        ShareUploadResult result,
        string? keptFilePathOnFailure,
        Action? onSuccess,
        Action? onFailure,
        RoeSnip.App.ITrayNotifier? notifier)
    {
        if (result.Success && result.Url is not null)
        {
            string cleanUrl = ShareLinks.CleanUrl(result.Url);
            string openUrl = ShareLinks.ComposeOpenUrl(result.Url, result.EditToken);

            // Auto-copy on arrival (preserves today's behavior) - the Copy button re-does the
            // identical copy on demand.
            bool clipboardCopied = await ClipboardService.TryCopyTextAsync(window, cleanUrl);
            if (!clipboardCopied)
            {
                FileLog.Write("RoeSnip: share URL clipboard copy failed (non-fatal).");
            }

            window.ShowSuccess(cleanUrl, openUrl);
            onSuccess?.Invoke();
        }
        else
        {
            string message = result.ErrorMessage ?? "Share upload failed.";
            window.ShowFailure(message, keptFilePathOnFailure);
            // The user closed the toast (X/Esc) before the upload converged - ShowFailure above just
            // wrote into a window nobody can see. Fine for an ordinary error, but a kept recording's
            // path is the ONLY place it is ever surfaced, so fall back to a tray notification.
            if (window.IsClosed && keptFilePathOnFailure is not null)
            {
                notifier?.ShowError($"{message} The recording file was kept at {keptFilePathOnFailure}");
            }
            onFailure?.Invoke();
        }
    }
}
