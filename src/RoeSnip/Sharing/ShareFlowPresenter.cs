using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using RoeSnip.Core.Sharing;

namespace RoeSnip.Sharing;

/// <summary>Owns the whole "click Share -&gt; progress -&gt; result" lifecycle (ROESNIP SHARE UX) for
/// every WPF call site (ToolbarControl's Share/dropdown, RecordingChrome's Share) - each call site
/// shrinks to "prepare the payload, call StartUpload" (plus, for the overlay paths, an immediate
/// Finish() the way Copy already does; see OverlayController). Deliberately takes a
/// <see cref="ShareProviderConfig"/> rather than a bare <see cref="IShareProvider"/> - unlike the
/// track brief's illustrative signature, ShareManager.UploadAsync is what performs the config ->
/// spec resolution AND the pre-network MaxUploadBytes fast-fail check every existing call site
/// already relies on; reaching around it to a raw IShareProvider would silently drop that check.
///
/// This runs on the SAME thread every one of those call sites already runs on - the tray app's
/// WinForms message-loop thread, which never calls System.Windows.Application.Run/Dispatcher.Run
/// (see OverlayController.EnsureApplication's own doc comment) - so the upload's continuation is
/// marshalled back via window.Dispatcher.BeginInvoke rather than relied on to resume there via a
/// bare ConfigureAwait(true) `await`, exactly the pattern OverlayController's own (now-removed)
/// UploadShareAsync used.</summary>
public static class ShareFlowPresenter
{
    /// <summary>Fires one upload: creates and shows a <see cref="ShareResultWindow"/> immediately
    /// (Uploading state), then runs the upload detached - this method itself returns as soon as the
    /// window is up, never awaited by its callers (matching every other "fire and forget from a UI
    /// click" handler's own contract in this codebase). <paramref name="request"/>'s Content stream
    /// is disposed here once the upload finishes (success, failure, or cancellation) - callers must
    /// not read or dispose it themselves afterward.
    ///
    /// <paramref name="keptFilePathOnFailure"/> is shown verbatim in the Failure state ("The recording
    /// file was kept at ...") when non-null - callers pass this only for a recording share, never for
    /// a toolbar (in-memory PNG) share. <paramref name="onSuccess"/> fires ONLY on a genuine upload
    /// success (never on cancel/failure) - the DATA-LOSS RULE's temp-file delete belongs there, at the
    /// caller, not in this shared plumbing. <paramref name="onFailure"/> fires on failure OR
    /// cancellation (Cancel counts as failure, file kept). <paramref name="notifier"/>: if the user
    /// closes the toast (X/Esc) while the upload is still in flight, the Failure state that would
    /// otherwise name a kept recording's path renders into a window nobody can see - when that
    /// happens AND <paramref name="keptFilePathOnFailure"/> is non-null, the message is also surfaced
    /// via <see cref="RoeSnip.ITrayNotifier.ShowError"/> so it is never silently lost.</summary>
    public static void StartUpload(
        ShareProviderConfig config,
        ShareUploadRequest request,
        string? keptFilePathOnFailure,
        Action? onSuccess,
        Action? onFailure,
        RoeSnip.ITrayNotifier? notifier = null)
    {
        string providerName = string.IsNullOrWhiteSpace(config.DisplayName)
            ? (ShareProviderCatalog.ResolveSpec(config)?.Name ?? "Share")
            : config.DisplayName;

        var window = new ShareResultWindow(providerName);
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
        RoeSnip.ITrayNotifier? notifier)
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
            // Cancel button or the window itself being closed mid-upload - "Cancel counts as failure
            // (file kept)", surfaced through the exact same Failure-state path a real network error
            // would take, not a special third UI state.
            result = new ShareUploadResult(false, null, "Upload cancelled.");
        }
        catch (Exception ex)
        {
            // Belt-and-braces, matching ShareManager.UploadAsync's own "never let an unexpected
            // exception escape as an unobserved-task-exception crash" contract.
            result = new ShareUploadResult(false, null, $"Upload failed: {ex.Message}");
        }
        finally
        {
            // Ownership of the stream transferred here for the duration of the upload (see this
            // method's own doc comment) - released now, BEFORE onSuccess/onFailure run, so a
            // recording's temp-file delete (the caller's onSuccess) never races an still-open
            // FileStream handle on the same path.
            try { request.Content.Dispose(); }
            catch (Exception ex) { Console.Error.WriteLine($"RoeSnip: share upload stream dispose failed (non-fatal): {ex.Message}"); }
        }

        // Discarded: DispatcherOperation is awaitable, but there is nothing further to do once the
        // UI-thread callback is QUEUED - mirrors OverlayController's own former UploadShareAsync.
        _ = window.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (result.Success && result.Url is not null)
            {
                string cleanUrl = ShareLinks.CleanUrl(result.Url);
                string openUrl = ShareLinks.ComposeOpenUrl(result.Url, result.EditToken);

                // Auto-copy on arrival (preserves today's behavior) - the Copy button re-does the
                // identical copy on demand, e.g. after the user pasted the link elsewhere and
                // overwrote the clipboard.
                try { System.Windows.Clipboard.SetText(cleanUrl); }
                catch (Exception ex) { Console.Error.WriteLine($"RoeSnip: share URL clipboard copy failed (non-fatal): {ex.Message}"); }

                window.ShowSuccess(cleanUrl, openUrl);
                onSuccess?.Invoke();
            }
            else
            {
                string message = result.ErrorMessage ?? "Share upload failed.";
                window.ShowFailure(message, keptFilePathOnFailure);
                // The user closed the toast (X/Esc) before the upload converged - ShowFailure above
                // just wrote into a window nobody can see. That is fine for an ordinary error, but a
                // kept recording's path is the ONLY place it is ever surfaced, so fall back to a tray
                // notification rather than silently losing it.
                if (window.IsClosed && keptFilePathOnFailure is not null)
                {
                    notifier?.ShowError($"{message} The recording file was kept at {keptFilePathOnFailure}");
                }
                onFailure?.Invoke();
            }
        }));
    }
}
