using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using RoeSnip.Core.Diagnostics;
using RoeSnip.Core.Sharing;

namespace RoeSnip.Sharing;

/// <summary>Owns the whole "click Share -&gt; progress -&gt; result" lifecycle (ROESNIP SHARE UX) for
/// every WPF call site (ToolbarControl's Share/dropdown, RecordingChrome's Share) - each call site
/// shrinks to "say how to open the payload, call StartUpload" (plus, for the overlay paths, an
/// immediate Finish() the way Copy already does; see OverlayController). Takes a
/// <see cref="ShareProviderConfig"/> rather than a bare <see cref="IShareProvider"/>:
/// ShareManager.UploadAsync is what performs the config -> spec resolution AND the pre-network
/// MaxUploadBytes fast-fail check.
///
/// This runs on the tray app's WinForms message-loop thread, which never calls
/// System.Windows.Application.Run/Dispatcher.Run (see OverlayController.EnsureApplication), so the
/// upload's continuation is marshalled back via window.Dispatcher.BeginInvoke rather than relied on
/// to resume there via a bare ConfigureAwait(true) `await`.</summary>
public static class ShareFlowPresenter
{
    /// <summary>Fires one upload: shows a <see cref="ShareResultWindow"/> immediately (Uploading
    /// state) and runs the upload detached. <paramref name="openContent"/> is called once per attempt
    /// (first try and every Retry) and the stream it returns is disposed when that attempt ends.
    ///
    /// <paramref name="keptFilePathOnFailure"/> is shown in the Failure state ("The recording file
    /// was kept at ...", plus Show in folder) - recording shares only. <paramref name="onSuccess"/>
    /// fires ONLY on a genuine upload success - the DATA-LOSS RULE's temp-file delete belongs there,
    /// at the caller. <paramref name="onFailure"/> fires with the message on failure OR cancellation.
    /// The Failure state offers Retry, which re-runs the same upload; so does the returned handle.
    /// <paramref name="notifier"/>: if the toast was closed before a recording's upload failed, the
    /// kept path is also surfaced as a tray error so it is never silently lost.</summary>
    public static ShareUpload StartUpload(
        ShareProviderConfig config,
        Func<Stream> openContent,
        string fileName,
        string contentType,
        RoeSnip.Capture.MonitorInfo monitor,
        string? keptFilePathOnFailure,
        Action? onSuccess,
        Action<string>? onFailure,
        RoeSnip.ITrayNotifier? notifier = null)
    {
        var upload = new ShareUpload(config, openContent, fileName, contentType, monitor, keptFilePathOnFailure, onSuccess, onFailure, notifier);
        upload.Retry();
        return upload;
    }

    /// <summary>One share upload and its toast. UI thread only.</summary>
    public sealed class ShareUpload
    {
        private readonly ShareProviderConfig _config;
        private readonly Func<Stream> _openContent;
        private readonly string _fileName;
        private readonly string _contentType;
        private readonly RoeSnip.Capture.MonitorInfo _monitor;
        private readonly string? _keptFilePath;
        private readonly Action? _onSuccess;
        private readonly Action<string>? _onFailure;
        private readonly RoeSnip.ITrayNotifier? _notifier;
        private readonly string _providerName;
        private ShareResultWindow? _window;

        internal ShareUpload(
            ShareProviderConfig config, Func<Stream> openContent, string fileName, string contentType,
            RoeSnip.Capture.MonitorInfo monitor, string? keptFilePath, Action? onSuccess, Action<string>? onFailure,
            RoeSnip.ITrayNotifier? notifier)
        {
            _config = config;
            _openContent = openContent;
            _fileName = fileName;
            _contentType = contentType;
            _monitor = monitor;
            _keptFilePath = keptFilePath;
            _onSuccess = onSuccess;
            _onFailure = onFailure;
            _notifier = notifier;
            _providerName = string.IsNullOrWhiteSpace(config.DisplayName)
                ? (ShareProviderCatalog.ResolveSpec(config)?.Name ?? "Share")
                : config.DisplayName;
        }

        /// <summary>True from the start of an attempt until its result has been shown.</summary>
        public bool InFlight { get; private set; }

        /// <summary>True once an attempt has succeeded; Retry is then a no-op.</summary>
        public bool Succeeded { get; private set; }

        /// <summary>Starts an attempt (the first one, or a retry), reusing the toast while it is open
        /// and opening a fresh one if the user closed it. No-op while an attempt is running or after
        /// a success.</summary>
        public void Retry()
        {
            if (InFlight || Succeeded)
            {
                return;
            }
            InFlight = true;

            if (_window is null || _window.IsClosed)
            {
                _window = new ShareResultWindow(_providerName, _monitor);
            }
            var window = _window;
            var cts = new CancellationTokenSource();
            window.ShowUploading(() => cts.Cancel());
            window.Show();

            _ = RunAsync(window, cts);
        }

        private async Task RunAsync(ShareResultWindow window, CancellationTokenSource cts)
        {
            ShareUploadResult result;
            Stream? content = null;
            try
            {
                content = _openContent();
                result = await ShareManager
                    .UploadAsync(_config, content, _fileName, _contentType, cts.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Cancel button or the window being closed mid-upload: a failure, file kept.
                FileLog.Write($"RoeSnip: share upload of {_fileName} cancelled");
                result = new ShareUploadResult(false, null, "Upload cancelled.");
            }
            catch (Exception ex)
            {
                // Includes openContent failing (the take could not be opened for reading).
                FileLog.Write($"RoeSnip: share upload of {_fileName} failed before or outside the request: {ex}");
                result = new ShareUploadResult(false, null, $"Upload failed: {ex.Message}");
            }
            finally
            {
                // Released BEFORE onSuccess runs, so the caller's temp-file delete never races an
                // open handle on the same path.
                try { content?.Dispose(); }
                catch (Exception ex) { FileLog.Write($"RoeSnip: share upload stream dispose failed (non-fatal): {ex.Message}"); }
            }

            _ = window.Dispatcher.BeginInvoke(new Action(() =>
            {
                InFlight = false;
                if (result.Success && result.Url is not null)
                {
                    Succeeded = true;
                    string cleanUrl = ShareLinks.CleanUrl(result.Url);
                    string openUrl = ShareLinks.ComposeOpenUrl(result.Url, result.EditToken);

                    // Auto-copy on arrival - the Copy button re-does the identical copy on demand.
                    try { System.Windows.Clipboard.SetText(cleanUrl); }
                    catch (Exception ex) { FileLog.Write($"RoeSnip: share URL clipboard copy failed (non-fatal): {ex.Message}"); }

                    window.ShowSuccess(cleanUrl, openUrl);
                    _onSuccess?.Invoke();
                }
                else
                {
                    string message = result.ErrorMessage ?? "Share upload failed.";
                    window.ShowFailure(message, _keptFilePath, Retry);
                    // The toast was closed before the upload converged: a kept recording's path
                    // must still reach the user.
                    if (window.IsClosed && _keptFilePath is not null)
                    {
                        _notifier?.ShowError($"{message} The recording file was kept at {_keptFilePath}");
                    }
                    _onFailure?.Invoke(message);
                }
            }));
        }
    }
}
