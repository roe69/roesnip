using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using RoeSnip.App.Overlay;
using RoeSnip.Core.Capture;
using RoeSnip.Core.Diagnostics;
using RoeSnip.Core.Sharing;

namespace RoeSnip.App.Sharing;

/// <summary>Owns the whole "click Share -&gt; progress -&gt; result" lifecycle (ROESNIP SHARE UX) for
/// every Avalonia call site (ToolbarControl's Share/dropdown, RecordingChrome's Share). Mirrors the
/// WPF app's identically-named class; see its doc comments for the contract (openContent per
/// attempt, onSuccess as the only delete point, onFailure with the message, Retry).
///
/// This runs on the Avalonia UI thread already, so the upload's continuation is marshalled back via
/// Dispatcher.UIThread.Post rather than relied on to resume there via a bare `await`.</summary>
public static class ShareFlowPresenter
{
    public static ShareUpload StartUpload(
        ShareProviderConfig config,
        Func<Stream> openContent,
        string fileName,
        string contentType,
        MonitorInfo? monitor,
        string? keptFilePathOnFailure,
        Action? onSuccess,
        Action<string>? onFailure,
        RoeSnip.App.ITrayNotifier? notifier = null)
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
        private readonly MonitorInfo? _monitor;
        private readonly string? _keptFilePath;
        private readonly Action? _onSuccess;
        private readonly Action<string>? _onFailure;
        private readonly RoeSnip.App.ITrayNotifier? _notifier;
        private readonly string _providerName;
        private ShareResultWindow? _window;

        internal ShareUpload(
            ShareProviderConfig config, Func<Stream> openContent, string fileName, string contentType,
            MonitorInfo? monitor, string? keptFilePath, Action? onSuccess, Action<string>? onFailure,
            RoeSnip.App.ITrayNotifier? notifier)
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

        public bool InFlight { get; private set; }

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

            // `() => _ = ...` rather than an async lambda: an async-void Post callback's exceptions
            // would vanish unobserved.
            Dispatcher.UIThread.Post(() => _ = FinishOnUiThreadAsync(window, result));
        }

        private async Task FinishOnUiThreadAsync(ShareResultWindow window, ShareUploadResult result)
        {
            InFlight = false;
            if (result.Success && result.Url is not null)
            {
                Succeeded = true;
                string cleanUrl = ShareLinks.CleanUrl(result.Url);
                string openUrl = ShareLinks.ComposeOpenUrl(result.Url, result.EditToken);

                // Auto-copy on arrival - the Copy button re-does the identical copy on demand.
                bool clipboardCopied = await ClipboardService.TryCopyTextAsync(window, cleanUrl);
                if (!clipboardCopied)
                {
                    FileLog.Write("RoeSnip: share URL clipboard copy failed (non-fatal).");
                }

                window.ShowSuccess(cleanUrl, openUrl);
                _onSuccess?.Invoke();
            }
            else
            {
                string message = result.ErrorMessage ?? "Share upload failed.";
                window.ShowFailure(message, _keptFilePath, Retry);
                // The toast was closed before the upload converged: a kept recording's path must
                // still reach the user.
                if (window.IsClosed && _keptFilePath is not null)
                {
                    _notifier?.ShowError($"{message} The recording file was kept at {_keptFilePath}");
                }
                _onFailure?.Invoke(message);
            }
        }
    }
}
