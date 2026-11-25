using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using RoeSnip.Core.Diagnostics;
using RoeSnip.Core.Notifications;
using RoeSnip.Interop;

namespace RoeSnip.Sharing;

/// <summary>The small topmost, non-activating result toast for one share upload (ROESNIP SHARE UX):
/// created by <see cref="ShareFlowPresenter"/> on the tray/UI thread the moment a Share click fires,
/// positioned next to the mouse cursor inside its monitor's work area (physical pixels via Win32
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
    private readonly RoeSnip.Capture.MonitorInfo _monitor;
    private Action? _onCancelRequested;
    private Action? _onRetryRequested;
    private string? _keptFilePath;
    private string? _cleanUrl;
    private string? _openUrl;
    private bool _resolved; // true once Success/Failure has been shown - Cancel/Esc no longer cancels
    private bool _autoDismissEligible; // true only after ShowSuccess - a Failure toast never auto-dismisses
    private readonly DispatcherTimer _autoDismissTimer;

    /// <summary>Set once <see cref="Window_Closed"/> has run, so <see cref="ShareFlowPresenter"/> can
    /// tell (on the UI thread, after the upload's continuation is queued) that this window is no
    /// longer visible and fall back to a tray notification for any message - in particular a kept-
    /// file path - that would otherwise be written into a window nobody can see.</summary>
    public bool IsClosed { get; private set; }

    public ShareResultWindow(string providerName, RoeSnip.Capture.MonitorInfo monitor)
    {
        InitializeComponent();
        HeaderText.Text = providerName;
        _monitor = monitor;

        _autoDismissTimer = new DispatcherTimer { Interval = RoeSnip.Core.Notifications.ToastDuration.Cap(TimeSpan.FromSeconds(10)) };
        _autoDismissTimer.Tick += (_, _) => { _autoDismissTimer.Stop(); Close(); };

        PositionNextToCursor();

        _followTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _followTimer.Tick += (_, _) => FollowPointer();
        _followTimer.Start();
    }

    private readonly DispatcherTimer _followTimer;
    private ToastFollower? _follower;
    private RoeSnip.Core.Capture.RectPhysical _placed;
    private int _followMoves;

    /// <summary>Keeps the window next to a moving pointer (Core ToastFollower: only when the pointer
    /// moved, frozen once the pointer heads into the window so it stays clickable).</summary>
    private void FollowPointer()
    {
        if (_follower is null || _follower.Frozen || !IsVisible)
        {
            return;
        }
        try
        {
            if (!Overlay.FlashDimmer.TryGetCursorPos(out int cx, out int cy))
            {
                return;
            }
            IntPtr hMonitor = NativeMethods.MonitorFromPoint(new NativeMethods.POINT { X = cx, Y = cy }, NativeMethods.MONITOR_DEFAULTTONEAREST);
            var monitorInfo = new NativeMethods.MONITORINFOEX { cbSize = (uint)Marshal.SizeOf<NativeMethods.MONITORINFOEX>() };
            if (!NativeMethods.GetMonitorInfo(hMonitor, ref monitorInfo))
            {
                return;
            }
            uint dpiX = 96;
            try { NativeMethods.GetDpiForMonitor(hMonitor, NativeMethods.MONITOR_DPI_TYPE.MDT_EFFECTIVE_DPI, out dpiX, out _); }
            catch { dpiX = 96; }
            double scale = dpiX / 96.0;
            int w = (int)Math.Round(Width * scale), h = (int)Math.Round(Height * scale);
            var wa = monitorInfo.rcWork;
            var workArea = new RoeSnip.Core.Capture.RectPhysical(wa.Left, wa.Top, wa.Right, wa.Bottom);
            if (_follower.Step(cx, cy, _placed, workArea, w, h, scale) is { } next)
            {
                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, next.X, next.Y, w, h, NativeMethods.SWP_NOACTIVATE);
                _placed = RoeSnip.Core.Capture.RectPhysical.FromSize(next.X, next.Y, w, h);
                _followMoves++;
            }
        }
        catch (Exception ex)
        {
            _followTimer.Stop();
            FileLog.Write($"RoeSnip: toast follow stopped ({ex.Message})");
        }
    }

    /// <summary>Uploading state: indeterminate progress + a Cancel button. <paramref name="onCancelRequested"/>
    /// is invoked at most once (the button disables itself immediately on click) - the presenter is
    /// expected to cancel its CancellationTokenSource and let the upload converge to
    /// <see cref="ShowFailure"/> on its own, so "Cancel counts as failure (file kept)" stays true
    /// without this window needing to know anything about the upload's actual outcome.</summary>
    public void ShowUploading(Action onCancelRequested)
    {
        // Also the Retry path: the toast may be coming back from its Failure state.
        _resolved = false;
        _onRetryRequested = null;
        _keptFilePath = null;
        RetryButton.Visibility = Visibility.Collapsed;
        ShowFolderButton.Visibility = Visibility.Collapsed;
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
    /// caller's onSuccess callback, never here). <paramref name="onRetry"/> non-null shows Retry;
    /// a kept file also gets Show in folder.</summary>
    public void ShowFailure(string message, string? keptFilePath, Action? onRetry = null)
    {
        _resolved = true;
        _onRetryRequested = onRetry;
        _keptFilePath = keptFilePath;
        RetryButton.Visibility = onRetry is not null ? Visibility.Visible : Visibility.Collapsed;
        ShowFolderButton.Visibility = keptFilePath is not null ? Visibility.Visible : Visibility.Collapsed;

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

    private void RetryButton_Click(object sender, RoutedEventArgs e)
    {
        Action? retry = _onRetryRequested;
        _onRetryRequested = null; // one click, one attempt
        retry?.Invoke();
    }

    private void ShowFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (_keptFilePath is { } path)
        {
            RoeSnip.Core.Sharing.FileReveal.Show(path);
        }
    }

    private void OpenButton_Click(object sender, RoutedEventArgs e)
    {
        if (_openUrl is null)
        {
            return;
        }
        try
        {
            RoeSnip.Core.Sharing.UrlLauncher.Open(_openUrl, "share Open button");
        }
        catch (Exception ex)
        {
            FileLog.Write($"RoeSnip: failed to open {_openUrl}: {ex.Message}");
        }
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        CopyCleanUrlToClipboard();
        Close();
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
            FileLog.Write($"RoeSnip: share link clipboard copy failed (non-fatal): {ex.Message}");
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

    private void Window_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _autoDismissTimer.Stop();
        _follower?.Freeze();
    }

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
        IsClosed = true;
        _autoDismissTimer.Stop();
        _followTimer.Stop();
        if (_followMoves > 0)
        {
            FileLog.Write($"RoeSnip: toast followed the pointer ({_followMoves} moves, {(_follower?.Frozen == true ? "frozen when approached" : "never approached")}, cursor)");
        }
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

    /// <summary>Positions and sizes this window next to the mouse cursor (ToastPlacement: down-right,
    /// flipped to stay inside the cursor monitor's work area), in physical pixels via SetWindowPos
    /// before <see cref="Window.Show"/> - the "EnsureHandle, SetWindowPos, then Show" ordering
    /// FlashDimmer.FlashWindow.PrepareHidden uses so there is no visible jump. The DIP size is scaled
    /// by the DPI of the monitor it lands on. No cursor: bottom-centre of the capture monitor.
    /// Best-effort: any failure leaves WPF's default placement.</summary>
    private void PositionNextToCursor()
    {
        try
        {
            IntPtr hwnd = new WindowInteropHelper(this).EnsureHandle();

            bool haveCursor = Overlay.FlashDimmer.TryGetCursorPos(out int cx, out int cy);
            IntPtr hMonitor = haveCursor
                ? NativeMethods.MonitorFromPoint(new NativeMethods.POINT { X = cx, Y = cy }, NativeMethods.MONITOR_DEFAULTTONEAREST)
                : _monitor.HMonitor;
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
                FileLog.Write($"RoeSnip: share result window DPI query failed (using 96): {ex.Message}");
            }

            double scale = dpiX / 96.0;
            int physicalWidth = (int)Math.Round(Width * scale);
            int physicalHeight = (int)Math.Round(Height * scale);
            var w = monitorInfo.rcWork;
            var workArea = new RoeSnip.Core.Capture.RectPhysical(w.Left, w.Top, w.Right, w.Bottom);
            var (x, y) = haveCursor
                ? ToastPlacement.NextToCursor(cx, cy, physicalWidth, physicalHeight, workArea,
                    (int)Math.Round(ToastPlacement.CursorOffsetDip * scale))
                : ToastPlacement.BottomCentre(physicalWidth, physicalHeight, workArea,
                    (int)Math.Round(ToastPlacement.EdgeMarginDip * scale));

            NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, x, y, physicalWidth, physicalHeight, NativeMethods.SWP_NOACTIVATE);
            _placed = RoeSnip.Core.Capture.RectPhysical.FromSize(x, y, physicalWidth, physicalHeight);
            _follower = new ToastFollower(haveCursor ? (cx, cy) : null, _placed);
            FileLog.Write($"RoeSnip: toast placed at {x},{y} ({(haveCursor ? "cursor" : "monitor fallback")})");
        }
        catch (Exception ex)
        {
            FileLog.Write($"RoeSnip: share result window placement failed (non-fatal): {ex.Message}");
        }
    }
}
