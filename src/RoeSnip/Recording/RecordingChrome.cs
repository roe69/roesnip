using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using RoeSnip.Capture;
using RoeSnip.Interop;

namespace RoeSnip.Recording;

// Same aliasing convention as the sibling Overlay/* files (RoeSnip.csproj enables both UseWPF and
// UseWindowsForms, so System.Windows.Forms/System.Drawing collide with WPF names).
using Color = System.Windows.Media.Color;
using Button = System.Windows.Controls.Button;
using Brushes = System.Windows.Media.Brushes;
using Cursors = System.Windows.Input.Cursors;
using FontFamily = System.Windows.Media.FontFamily;
using Orientation = System.Windows.Controls.Orientation;
// RecordingChrome derives from Window (: FrameworkElement), which itself declares INSTANCE
// HorizontalAlignment/VerticalAlignment properties — a bare "HorizontalAlignment.Center" inside a
// member of this class can resolve to that inherited property instead of the enum type. Aliased
// under different names to sidestep the shadowing entirely rather than fully-qualifying every use.
using HAlign = System.Windows.HorizontalAlignment;
using VAlign = System.Windows.VerticalAlignment;

/// <summary>The on-screen recording HUD: a small floating control bar (red dot, mm:ss elapsed,
/// Stop, Cancel) anchored just outside the recorded selection — NOT a full-monitor overlay tracing
/// the selection outline (that would need the same DPI-aware layout machinery OverlayWindow already
/// carries; a compact HUD gets the same "recording is live, here's how to stop it" job done for a
/// fraction of the code, at the acceptable cost of not drawing a selection-outline border).
///
/// Unlike FlashDimmer's windows, this is NOT pooled/parked — one instance per recording, created at
/// start, closed at stop. Recording start is not on the hotkey-to-dim latency path, so there is no
/// park-don't-hide concern to engineer around here.
///
/// Built entirely in code (no XAML/InitializeComponent) — same choice FlashWindow already makes for
/// a small, code-owned chrome window.</summary>
internal sealed class RecordingChrome : Window
{
    private readonly TextBlock _elapsedText;
    private readonly MonitorInfo _monitor;
    private readonly RectPhysical _selectionPx;

    public event Action? StopRequested;
    public event Action? CancelRequested;

    public RecordingChrome(MonitorInfo monitor, RectPhysical selectionPx)
    {
        _monitor = monitor;
        _selectionPx = selectionPx;

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false; // never steals focus from the recorded window/overlay-less desktop
        WindowStartupLocation = WindowStartupLocation.Manual;
        Cursor = Cursors.Arrow;
        SizeToContent = SizeToContent.WidthAndHeight;

        var redDot = new Ellipse
        {
            Width = 10,
            Height = 10,
            Fill = new SolidColorBrush(Color.FromRgb(0xDC, 0x46, 0x46)),
            VerticalAlignment = VAlign.Center,
            Margin = new Thickness(0, 0, 8, 0),
        };

        _elapsedText = new TextBlock
        {
            Text = "00:00",
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.FromRgb(0xE4, 0xE4, 0xCE)), // RlTextPrimary
            VerticalAlignment = VAlign.Center,
            Margin = new Thickness(0, 0, 14, 0),
            MinWidth = 40,
        };

        // Content is set as a plain string (not parsed XAML), so a literal "&" needs no escaping —
        // WPF only interprets "_" as an access-key mnemonic, and only when set via AccessText/XAML.
        var stopButton = BuildButton("Stop & Save", isDanger: false);
        stopButton.Click += (_, _) => StopRequested?.Invoke();
        AutomationProperties.SetAutomationId(stopButton, "RecordingStopButton");

        var cancelButton = BuildButton("Cancel", isDanger: true);
        cancelButton.Click += (_, _) => CancelRequested?.Invoke();
        AutomationProperties.SetAutomationId(cancelButton, "RecordingCancelButton");

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 8, 12, 8) };
        row.Children.Add(redDot);
        row.Children.Add(_elapsedText);
        row.Children.Add(stopButton);
        row.Children.Add(cancelButton);

        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xEB, 0x14, 0x10, 0x0C)), // RlPanelBackground
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x59, 0xFF, 0x9F, 0x09)), // RlBorderStrong
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Child = row,
        };
    }

    private static Button BuildButton(string text, bool isDanger)
    {
        var button = new Button
        {
            Content = text,
            Padding = new Thickness(10, 4, 10, 4),
            Margin = new Thickness(4, 0, 0, 0),
            Cursor = Cursors.Hand,
            Focusable = false,
            Background = isDanger
                ? new SolidColorBrush(Color.FromArgb(0x26, 0xDC, 0x46, 0x46))
                : new SolidColorBrush(Color.FromArgb(0x1A, 0xFF, 0x9F, 0x09)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xE4, 0xE4, 0xCE)),
            BorderThickness = new Thickness(0),
        };
        var template = new ControlTemplate(typeof(Button));
        var borderFactory = new FrameworkElementFactory(typeof(Border));
        borderFactory.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Button.BackgroundProperty));
        borderFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        borderFactory.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Button.PaddingProperty));
        var contentFactory = new FrameworkElementFactory(typeof(ContentPresenter));
        contentFactory.SetValue(ContentPresenter.HorizontalAlignmentProperty, HAlign.Center);
        contentFactory.SetValue(ContentPresenter.VerticalAlignmentProperty, VAlign.Center);
        borderFactory.AppendChild(contentFactory);
        template.VisualTree = borderFactory;
        button.Template = template;
        return button;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;

        // WS_EX_TOOLWINDOW: no Alt+Tab entry for this HUD.
        long exStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(exStyle | NativeMethods.WS_EX_TOOLWINDOW));

        // Hard rule: any window that can be on screen during a capture/recording must exclude
        // itself from capture, or it bakes itself into the recording. Same escape hatch
        // (ROESNIP_DIAG_NOEXCLUDE=1) as OverlayWindow/FlashDimmer so the external luma-sampler/UIA
        // harness can still screenshot this chrome deliberately when asked to.
        if (Environment.GetEnvironmentVariable("ROESNIP_DIAG_NOEXCLUDE") != "1"
            && !NativeMethods.SetWindowDisplayAffinity(hwnd, NativeMethods.WDA_EXCLUDEFROMCAPTURE))
        {
            Console.Error.WriteLine(
                "RoeSnip: SetWindowDisplayAffinity(EXCLUDEFROMCAPTURE) failed on the recording chrome; it will appear IN the recording!");
        }

        PositionNearSelection(hwnd);
    }

    /// <summary>Anchors the HUD just below-right of the selection, flipping above the selection if
    /// below would run off the bottom of the monitor, and clamping horizontally so it never runs
    /// off either side — the same edge cases OverlayWindow's own toolbar-attach math handles, scaled
    /// down to this HUD's simpler fixed-size box (SizeToContent has already run a layout pass by the
    /// time OnSourceInitialized fires, so ActualWidth/ActualHeight are real here).</summary>
    private void PositionNearSelection(IntPtr hwnd)
    {
        double scale = _monitor.DpiX / 96.0;
        int barWidthPx = Math.Max(1, (int)Math.Ceiling(ActualWidth * scale));
        int barHeightPx = Math.Max(1, (int)Math.Ceiling(ActualHeight * scale));

        var bounds = _monitor.BoundsPx;
        int selLeft = bounds.Left + _selectionPx.Left;
        int selRight = bounds.Left + _selectionPx.Right;
        int selTop = bounds.Top + _selectionPx.Top;
        int selBottom = bounds.Top + _selectionPx.Bottom;

        const int gap = 8;
        int x = selLeft;
        int y = selBottom + gap;
        bool fitsBelow = y + barHeightPx <= bounds.Bottom;
        if (!fitsBelow)
        {
            y = selTop - barHeightPx - gap; // flip above
        }
        bool fitsAbove = y >= bounds.Top;

        if (!fitsBelow && !fitsAbove)
        {
            // Neither below nor above the selection leaves room for the HUD on this monitor (e.g. a
            // near-full-height selection) — anchor beside it instead. Clamping y into monitor bounds
            // here like the below/above cases do would land the HUD's own WDA_EXCLUDEFROMCAPTURE
            // rect INSIDE [selTop, selBottom], and because that affinity applies to ANY capture of
            // this monitor — including the WGC session actively recording this exact region — the
            // compositor renders nothing there for the recording either: a black hole for the HUD's
            // entire lifetime, not just a visual overlap.
            y = Math.Clamp(selTop, bounds.Top, Math.Max(bounds.Top, bounds.Bottom - barHeightPx));
            x = selRight + gap + barWidthPx <= bounds.Right
                ? selRight + gap
                : selLeft - gap - barWidthPx;
        }

        x = Math.Clamp(x, bounds.Left, Math.Max(bounds.Left, bounds.Right - barWidthPx));
        y = Math.Clamp(y, bounds.Top, Math.Max(bounds.Top, bounds.Bottom - barHeightPx));

        NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, x, y, barWidthPx, barHeightPx, NativeMethods.SWP_NOACTIVATE);
    }

    public void SetElapsed(TimeSpan elapsed, TimeSpan? cap)
    {
        string text = $"{(int)elapsed.TotalMinutes:00}:{elapsed.Seconds:00}";
        if (cap is { } capValue)
        {
            var remaining = capValue - elapsed;
            if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;
            text += $"  ({(int)remaining.TotalMinutes:00}:{remaining.Seconds:00} left)";
        }
        _elapsedText.Text = text;
    }

    public void CloseChrome()
    {
        try { Close(); }
        catch (InvalidOperationException) { /* already closing */ }
    }
}
