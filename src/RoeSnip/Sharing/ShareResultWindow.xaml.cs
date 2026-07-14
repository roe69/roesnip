using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using RoeSnip.Interop;

namespace RoeSnip.Sharing;

/// <summary>The small topmost, non-activating result toast for one share upload (ROESNIP SHARE UX):
/// created by <see cref="ShareFlowPresenter"/> on the tray/UI thread the moment a Share click fires,
/// positioned bottom-right of the PRIMARY monitor's work area (physical pixels via Win32
/// SetWindowPos, never WPF DIP properties - same "mixed-DPI discipline" FlashDimmer/RegionOutline
/// already use in this codebase), and driven through exactly one of its three terminal-ish states:
/// Uploading -&gt; (Success | Failure). Mouse-only controls throughout (no hover-reveal, per this
/// app's standing no-hidden-controls rule) - see the class's own KeyDown handler for why Esc/Ctrl+C
/// are ALSO wired despite that: this window only sets ShowActivated=false (unlike RegionOutline's
/// permanent WS_EX_NOACTIVATE), the same recipe FlashDimmer.FlashWindow already uses successfully
/// for its own Escape handling on this exact WinForms-pumped thread - once the user clicks into this
/// window (ordinary click-to-activate still works), the thread-wide WpfKeyboardBridge routes its
/// keyboard messages normally.</summary>
public partial class ShareResultWindow : Window
{
    private Action? _onCancelRequested;
    private string? _cleanUrl;
    private string? _openUrl;
    private bool _resolved; // true once Success/Failure has been shown - Cancel/Esc no longer cancels
    private bool _autoDismissEligible; // true only after ShowSuccess - a Failure toast never auto-dismisses
    private readonly DispatcherTimer _autoDismissTimer;

    public ShareResultWindow(string providerName)
    {
        InitializeComponent();
        HeaderText.Text = providerName;

        _autoDismissTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _autoDismissTimer.Tick += (_, _) => { _autoDismissTimer.Stop(); Close(); };

        PositionBottomRight();
    }

    /// <summary>Uploading state: indeterminate progress + a Cancel button. <paramref name="onCancelRequested"/>
    /// is invoked at most once (the button disables itself immediately on click) - the presenter is
    /// expected to cancel its CancellationTokenSource and let the upload converge to
    /// <see cref="ShowFailure"/> on its own, so "Cancel counts as failure (file kept)" stays true
    /// without this window needing to know anything about the upload's actual outcome.</summary>
    public void ShowUploading(Action onCancelRequested)
    {
        _onCancelRequested = onCancelRequested;
        StatusText.Text = "Uploading...";
        StatusText.Foreground = (System.Windows.Media.Brush)Resources["MutedBrush"] ?? System.Windows.Media.Brushes.Gray;
        ProgressBarControl.Visibility = Visibility.Visible;
        LinkText.Visibility = Visibility.Collapsed;
        KeptPathText.Visibility = Visibility.Collapsed;
        CancelButton.Visibility = Visibility.Visible;
        CancelButton.IsEnabled = true;
        OpenButton.Visibility = Visibility.Collapsed;
        CopyButton.Visibility = Visibility.Collapsed;
    }

    /// <summary>Success state: the clean URL as selectable, middle-truncated text (the full string
    /// lives only in <see cref="_cleanUrl"/>/<paramref name="openUrl"/> - Open/Copy never read the
    /// truncated display text), Open + Copy always visible together (no hover-reveal). Auto-dismisses
    /// after 15s unless the pointer is currently over the window (see Window_MouseEnter/Leave) -
    /// caller is responsible for having already auto-copied <paramref name="cleanUrl"/> to the
    /// clipboard before calling this (see ShareFlowPresenter - that copy happens once, here or on the
    /// Copy button, never silently redone on every render).</summary>
    public void ShowSuccess(string cleanUrl, string openUrl)
    {
        _resolved = true;
        _autoDismissEligible = true;
        _cleanUrl = cleanUrl;
        _openUrl = openUrl;

        StatusText.Text = "Uploaded";
        StatusText.Foreground = (System.Windows.Media.Brush)Resources["MutedBrush"];
        ProgressBarControl.Visibility = Visibility.Collapsed;
        KeptPathText.Visibility = Visibility.Collapsed;

        LinkText.Text = TruncateMiddle(cleanUrl, 48);
        LinkText.Visibility = Visibility.Visible;

        CancelButton.Visibility = Visibility.Collapsed;
        OpenButton.Visibility = Visibility.Visible;
        CopyButton.Visibility = Visibility.Visible;

        if (!IsMouseOver)
        {
            _autoDismissTimer.Start();
        }
    }

    /// <summary>Failure state (includes Cancel - "Cancel counts as failure, file kept"): persists
    /// until the user dismisses it (no auto-dismiss timer) so a data-loss-relevant message never
    /// disappears unread. <paramref name="keptFilePath"/> non-null only for a recording share whose
    /// temp file was kept (the DATA-LOSS RULE: deleted only on upload success, enforced by the
    /// caller's onSuccess callback, never here).</summary>
    public void ShowFailure(string message, string? keptFilePath)
    {
        _resolved = true;

        StatusText.Text = message;
        StatusText.Foreground = (System.Windows.Media.Brush)Resources["ErrorBrush"];
        ProgressBarControl.Visibility = Visibility.Collapsed;
        LinkText.Visibility = Visibility.Collapsed;

        if (keptFilePath is not null)
        {
            KeptPathText.Text = $"The recording file was kept at {keptFilePath}";
            KeptPathText.Visibility = Visibility.Visible;
        }
        else
        {
            KeptPathText.Visibility = Visibility.Collapsed;
        }

        CancelButton.Visibility = Visibility.Collapsed;
        OpenButton.Visibility = Visibility.Collapsed;
        CopyButton.Visibility = Visibility.Collapsed;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        // Momentary guard only, matching ToolbarControl.SetShareBusy's own "double-click guard, not a
        // real state machine" convention - a second click before the upload actually observes
        // cancellation must not invoke the callback twice.
        CancelButton.IsEnabled = false;
        StatusText.Text = "Cancelling...";
        _onCancelRequested?.Invoke();
    }

    private void OpenButton_Click(object sender, RoutedEventArgs e)
    {
        if (_openUrl is null)
        {
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(_openUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"RoeSnip: failed to open {_openUrl}: {ex.Message}");
        }
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        CopyCleanUrlToClipboard();
    }

    private void CopyCleanUrlToClipboard()
    {
        if (_cleanUrl is null)
        {
            return;
        }
        try
        {
            System.Windows.Clipboard.SetText(_cleanUrl);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"RoeSnip: share link clipboard copy failed (non-fatal): {ex.Message}");
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
            return;
        }

        if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control && _resolved)
        {
            e.Handled = true;
            CopyCleanUrlToClipboard();
        }
    }

    private void Window_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e) => _autoDismissTimer.Stop();

    private void Window_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_autoDismissEligible)
        {
            _autoDismissTimer.Start();
        }
    }

    /// <summary>Closing this window (Esc, the X button, or an in-flight upload's own success/failure
    /// swap never gets here since the window stays open) always cancels a still-in-flight upload -
    /// there is nowhere left to show its progress/result. Safe to call unconditionally: the presenter
    /// treats a cancellation exactly like a Cancel-button click, and cancelling an already-finished
    /// CancellationTokenSource is simply a no-op.</summary>
    private void Window_Closed(object sender, EventArgs e)
    {
        _autoDismissTimer.Stop();
        _onCancelRequested?.Invoke();
    }

    private static string TruncateMiddle(string text, int maxLength)
    {
        if (text.Length <= maxLength)
        {
            return text;
        }
        int head = (maxLength - 1) / 2;
        int tail = maxLength - 1 - head;
        return string.Concat(text.AsSpan(0, head), "…", text.AsSpan(text.Length - tail, tail));
    }

    /// <summary>Positions and sizes this window at the PRIMARY monitor's work-area bottom-right
    /// corner, in physical pixels, before <see cref="Window.Show"/> is ever called - same
    /// "EnsureHandle, SetWindowPos, then Show" ordering FlashDimmer.FlashWindow.PrepareHidden uses so
    /// there is no visible jump. Best-effort: on any failure this silently falls back to WPF's own
    /// default placement rather than surfacing a positioning error for what is a non-load-bearing
    /// toast.</summary>
    private void PositionBottomRight()
    {
        try
        {
            IntPtr hwnd = new WindowInteropHelper(this).EnsureHandle();

            IntPtr hMonitor = NativeMethods.MonitorFromPoint(default, NativeMethods.MONITOR_DEFAULTTOPRIMARY);
            var monitorInfo = new NativeMethods.MONITORINFOEX
            {
                cbSize = (uint)Marshal.SizeOf<NativeMethods.MONITORINFOEX>(),
            };
            if (!NativeMethods.GetMonitorInfo(hMonitor, ref monitorInfo))
            {
                return;
            }

            uint dpiX = 96, dpiY = 96;
            try
            {
                NativeMethods.GetDpiForMonitor(hMonitor, NativeMethods.MONITOR_DPI_TYPE.MDT_EFFECTIVE_DPI, out dpiX, out dpiY);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"RoeSnip: share result window DPI query failed (using 96): {ex.Message}");
            }

            double scale = dpiX / 96.0;
            int physicalWidth = (int)Math.Round(Width * scale);
            int physicalHeight = (int)Math.Round(Height * scale);
            int margin = (int)Math.Round(16 * scale);
            int x = monitorInfo.rcWork.Right - physicalWidth - margin;
            int y = monitorInfo.rcWork.Bottom - physicalHeight - margin;

            NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, x, y, physicalWidth, physicalHeight, NativeMethods.SWP_NOACTIVATE);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"RoeSnip: share result window placement failed (non-fatal): {ex.Message}");
        }
    }
}
