using System;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using RoeSnip.App.Overlay;
using RoeSnip.Core.Diagnostics;

namespace RoeSnip.App.Sharing;

/// <summary>The small topmost, non-activating result toast for one share upload (ROESNIP SHARE UX):
/// created by <see cref="ShareFlowPresenter"/> on the Avalonia UI thread the moment a Share click
/// fires, positioned bottom-right of the PRIMARY screen's work area - same physical-pixel-before-
/// Show() recipe as TrayApp.ShowToast, grown from that primitive into a real 3-state window - and
/// driven through exactly one of Uploading -&gt; (Success | Failure). Mouse-only controls throughout
/// (no hover-reveal, per this app's standing no-hidden-controls rule); Esc/Ctrl+C are wired too since
/// Avalonia's own Dispatcher.UIThread already owns this thread's input pump (unlike the WPF app's
/// WinForms-hosted Dispatcher, there is no bridge to worry about here).</summary>
public partial class ShareResultWindow : Window
{
    // Local color fields rather than an App.axaml resource lookup from code - mirrors TrayApp.
    // ShowToast's own "duplicate as literal fields" convention (see that method's doc comment);
    // values match RlTextDimBrush/RlDangerHoverBrush exactly.
    private static readonly IBrush StatusMutedBrush = new SolidColorBrush(Color.FromRgb(0x71, 0x71, 0x7B));
    private static readonly IBrush StatusErrorBrush = new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26));

    private Action? _onCancelRequested;
    private string? _cleanUrl;
    private string? _openUrl;
    private bool _resolved; // true once Success/Failure has been shown
    private bool _autoDismissEligible; // true only after ShowSuccess - a Failure toast never auto-dismisses
    private readonly DispatcherTimer _autoDismissTimer;

    /// <summary>Set once <see cref="Window_Closed"/> has run - mirrors the WPF app's identically-named
    /// property, letting <see cref="ShareFlowPresenter"/> fall back to a tray notification for a kept-
    /// file message that would otherwise render into a window nobody can see.</summary>
    public bool IsClosed { get; private set; }

    /// <summary>Parameterless overload for Avalonia's XAML runtime loader only (AVLN3001 - the
    /// compiled .axaml needs a public no-arg constructor to be reachable via avares://, even though
    /// every real call site uses the parameterized one below).</summary>
    public ShareResultWindow() : this("RoeSnip")
    {
    }

    public ShareResultWindow(string providerName)
    {
        InitializeComponent();
        HeaderText.Text = providerName;

        _autoDismissTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _autoDismissTimer.Tick += (_, _) => { _autoDismissTimer.Stop(); Close(); };

        PositionBottomRight();
    }

    /// <summary>Uploading state: indeterminate progress + a Cancel button. See the WPF app's
    /// identically-named method for the contract <paramref name="onCancelRequested"/> follows.</summary>
    public void ShowUploading(Action onCancelRequested)
    {
        _onCancelRequested = onCancelRequested;
        StatusText.Text = "Uploading...";
        StatusText.Foreground = StatusMutedBrush;
        ProgressBarControl.IsVisible = true;
        LinkText.IsVisible = false;
        KeptPathText.IsVisible = false;
        CancelButton.IsVisible = true;
        CancelButton.IsEnabled = true;
        OpenButton.IsVisible = false;
        CopyButton.IsVisible = false;
    }

    /// <summary>Success state - see the WPF app's identically-named method for the full contract
    /// (selectable middle-truncated link text, Open+Copy always visible, 15s auto-dismiss suppressed
    /// while the pointer is over the window).</summary>
    public void ShowSuccess(string cleanUrl, string openUrl)
    {
        _resolved = true;
        _autoDismissEligible = true;
        _cleanUrl = cleanUrl;
        _openUrl = openUrl;

        StatusText.Text = "Uploaded";
        StatusText.Foreground = StatusMutedBrush;
        ProgressBarControl.IsVisible = false;
        KeptPathText.IsVisible = false;

        LinkText.Text = TruncateMiddle(cleanUrl, 48);
        LinkText.IsVisible = true;

        CancelButton.IsVisible = false;
        OpenButton.IsVisible = true;
        CopyButton.IsVisible = true;

        if (!IsPointerOver)
        {
            _autoDismissTimer.Start();
        }
    }

    /// <summary>Failure state (includes Cancel - "Cancel counts as failure, file kept"): persists
    /// until dismissed, no auto-dismiss timer.</summary>
    public void ShowFailure(string message, string? keptFilePath)
    {
        _resolved = true;

        StatusText.Text = message;
        StatusText.Foreground = StatusErrorBrush;
        ProgressBarControl.IsVisible = false;
        LinkText.IsVisible = false;

        if (keptFilePath is not null)
        {
            KeptPathText.Text = $"The recording file was kept at {keptFilePath}";
            KeptPathText.IsVisible = true;
        }
        else
        {
            KeptPathText.IsVisible = false;
        }

        CancelButton.IsVisible = false;
        OpenButton.IsVisible = false;
        CopyButton.IsVisible = false;
    }

    private void CancelButton_Click(object? sender, RoutedEventArgs e)
    {
        CancelButton.IsEnabled = false;
        StatusText.Text = "Cancelling...";
        _onCancelRequested?.Invoke();
    }

    private void OpenButton_Click(object? sender, RoutedEventArgs e)
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
            FileLog.Write($"RoeSnip: failed to open {_openUrl}: {ex.Message}");
        }
    }

    private void CopyButton_Click(object? sender, RoutedEventArgs e) => _ = CopyCleanUrlToClipboardAsync();

    private async System.Threading.Tasks.Task CopyCleanUrlToClipboardAsync()
    {
        if (_cleanUrl is null)
        {
            return;
        }
        bool copied = await ClipboardService.TryCopyTextAsync(this, _cleanUrl);
        if (!copied)
        {
            FileLog.Write("RoeSnip: share link clipboard copy failed (non-fatal).");
        }
    }

    private void CloseButton_Click(object? sender, RoutedEventArgs e) => Close();

    private void Window_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
            return;
        }

        if (e.Key == Key.C && e.KeyModifiers == KeyModifiers.Control && _resolved)
        {
            e.Handled = true;
            _ = CopyCleanUrlToClipboardAsync();
        }
    }

    private void Window_PointerEntered(object? sender, PointerEventArgs e) => _autoDismissTimer.Stop();

    private void Window_PointerExited(object? sender, PointerEventArgs e)
    {
        if (_autoDismissEligible)
        {
            _autoDismissTimer.Start();
        }
    }

    /// <summary>Closing this window (Esc, the X button, or an external close) always cancels a
    /// still-in-flight upload - mirrors the WPF app's identically-named handler.</summary>
    private void Window_Closed(object? sender, EventArgs e)
    {
        IsClosed = true;
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

    /// <summary>Positions this window at the PRIMARY screen's work-area bottom-right corner, in
    /// physical pixels, before <see cref="Window.Show"/> is ever called - the exact math TrayApp.
    /// ShowToast already uses (Screens.Primary.WorkingArea + Scaling), just applied to a real
    /// compiled window instead of one built ad hoc in code. Best-effort: any failure silently falls
    /// back to Avalonia's own default placement.</summary>
    private void PositionBottomRight()
    {
        try
        {
            var primary = Screens.Primary;
            if (primary is null)
            {
                return;
            }

            double scale = primary.Scaling;
            var workingArea = primary.WorkingArea;
            int x = workingArea.X + workingArea.Width - (int)Math.Round((Width + 16) * scale);
            int y = workingArea.Y + workingArea.Height - (int)Math.Round((Height + 16) * scale);
            Position = new PixelPoint(x, y);
        }
        catch (Exception ex)
        {
            FileLog.Write($"RoeSnip: share result window placement failed (non-fatal): {ex.Message}");
        }
    }
}
