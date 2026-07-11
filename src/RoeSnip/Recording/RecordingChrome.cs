using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
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

/// <summary>The on-screen recording control area (feature 10 redesign): a small floating panel
/// anchored just outside the recorded selection that walks the user through THREE states instead
/// of starting capture the instant the format is chosen:
///   Setup     - nothing is being captured yet. Shows Start, the MP4-only audio toggles (Mic /
///               System audio - hidden entirely for GIF, which has no audio track), and Cancel.
///   Recording - Start was pressed; the real WGC/encoder pipeline (RecordingController) is live.
///               Shows the red dot + ticking elapsed time and Stop. Audio toggles are disabled
///               here (the encoder already baked in whatever they were set to at Start - see
///               RecordingSession.BeginCapture) rather than hidden, so the panel doesn't jump.
///   Reviewing - Stop was pressed; the take is fully encoded to its temp file but not yet moved to
///               a real path. Shows Restart and Save (Cancel remains available to discard).
/// Restart (available once anything has been captured) asks for confirmation INLINE - swapping
/// this same panel's content rather than opening a second top-level window, so there is only ever
/// one HWND to keep WDA-excluded (see OnSourceInitialized) for the whole recording lifetime.
///
/// Not pooled/parked like FlashDimmer's windows - one instance per recording, created at Start,
/// closed at the end. Recording start is not on the hotkey-to-dim latency path.
///
/// Built entirely in code (no XAML/InitializeComponent) — same choice FlashWindow already makes for
/// a small, code-owned chrome window.</summary>
internal sealed class RecordingChrome : Window
{
    private enum ChromeState { Setup, Recording, Reviewing }

    // ---------- roeshare tokens (hand-copied byte tuples - this window has no XAML resource
    // dictionary to route through, same as the pre-existing red dot/background/border below). ----------
    private static readonly Color TextPrimary = Color.FromRgb(0xED, 0xED, 0xF0);
    private static readonly Color TextMuted = Color.FromRgb(0xA2, 0xA2, 0xAB);
    private static readonly Color PrimaryOrange = Color.FromRgb(0xFF, 0x6B, 0x35);
    private static readonly Color PrimaryOrangeLight = Color.FromRgb(0xFF, 0x8A, 0x5C);
    private static readonly Color PrimaryOrangeBorder = Color.FromRgb(0xE5, 0x56, 0x1F);
    private static readonly Color TextOnPrimary = Color.FromRgb(0x18, 0x0D, 0x07);
    private static readonly Color ActiveTint = Color.FromArgb(0x33, 0xFF, 0x6B, 0x35); // rgba(primary,.20) - a checked audio toggle
    private static readonly Color GhostFill = Color.FromArgb(0x0F, 0xFF, 0xFF, 0xFF); // rgba(white,.06)
    private static readonly Color BorderStrong = Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF); // rgba(white,.14)
    private static readonly Color DangerFill = Color.FromArgb(0x26, 0xDC, 0x26, 0x26);
    private static readonly Color DangerSolid = Color.FromRgb(0xDC, 0x26, 0x26);

    private readonly MonitorInfo _monitor;
    private readonly RectPhysical _selectionPx;
    private readonly RecordingFormat _format;
    private ChromeState _state = ChromeState.Setup;
    private bool _showingRestartConfirm;

    private readonly Ellipse _redDot;
    private readonly TextBlock _elapsedText;
    private readonly Button _startStopButton;
    private readonly ToggleButton _micToggle;
    private readonly ToggleButton _systemAudioToggle;
    private readonly StackPanel _audioRow;
    private readonly Button _restartButton;
    private readonly Button _saveButton;
    private readonly Button _cancelButton;
    private readonly StackPanel _normalPanel;
    private readonly StackPanel _confirmPanel;
    private readonly Grid _root;

    /// <summary>Setup state's Start button.</summary>
    public event Action? StartRequested;
    /// <summary>Recording state's Stop button - ends capture, moves to Reviewing (does NOT save).</summary>
    public event Action? StopRequested;
    /// <summary>Raised only after the user confirms the inline "discard and start over?" prompt.</summary>
    public event Action? RestartConfirmed;
    /// <summary>Reviewing state's Save button.</summary>
    public event Action? SaveRequested;
    /// <summary>Available in every state - aborts the whole recording without saving.</summary>
    public event Action? CancelRequested;
    public event Action<bool>? MicToggled;
    public event Action<bool>? SystemAudioToggled;

    public RecordingChrome(MonitorInfo monitor, RectPhysical selectionPx, RecordingFormat format, bool initialMic, bool initialSystemAudio)
    {
        _monitor = monitor;
        _selectionPx = selectionPx;
        _format = format;

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

        _redDot = new Ellipse
        {
            Width = 10,
            Height = 10,
            Fill = new SolidColorBrush(DangerSolid),
            VerticalAlignment = VAlign.Center,
            Margin = new Thickness(0, 0, 8, 0),
            Visibility = Visibility.Collapsed,
        };

        _elapsedText = new TextBlock
        {
            Text = "00:00",
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 13,
            Foreground = new SolidColorBrush(TextPrimary),
            VerticalAlignment = VAlign.Center,
            Margin = new Thickness(0, 0, 14, 0),
            MinWidth = 40,
        };

        var indicatorRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VAlign.Center };
        indicatorRow.Children.Add(_redDot);
        indicatorRow.Children.Add(_elapsedText);

        // Start/Stop is a single button whose label and raised event depend on _state - a real
        // toggle-like control rather than two separately-shown buttons, so its position never jumps.
        _startStopButton = BuildPrimaryButton("Start");
        _startStopButton.Click += (_, _) =>
        {
            if (_state == ChromeState.Setup) StartRequested?.Invoke();
            else if (_state == ChromeState.Recording) StopRequested?.Invoke();
        };
        AutomationProperties.SetAutomationId(_startStopButton, "RecordingStartStopButton");

        _micToggle = BuildAudioToggle("Mic");
        _micToggle.IsChecked = initialMic;
        _micToggle.Click += (_, _) => MicToggled?.Invoke(_micToggle.IsChecked == true);

        _systemAudioToggle = BuildAudioToggle("System audio");
        _systemAudioToggle.IsChecked = initialSystemAudio;
        _systemAudioToggle.Click += (_, _) => SystemAudioToggled?.Invoke(_systemAudioToggle.IsChecked == true);

        _audioRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        _audioRow.Children.Add(_micToggle);
        _audioRow.Children.Add(_systemAudioToggle);
        // GIF has no audio track at all - the row is gone, not just grayed, since it never applies.
        _audioRow.Visibility = _format == RecordingFormat.Mp4 ? Visibility.Visible : Visibility.Collapsed;

        _restartButton = BuildButton("Restart", isDanger: false);
        _restartButton.Click += (_, _) => ShowRestartConfirm();
        AutomationProperties.SetAutomationId(_restartButton, "RecordingRestartButton");

        _saveButton = BuildPrimaryButton("Save");
        _saveButton.Click += (_, _) => SaveRequested?.Invoke();
        AutomationProperties.SetAutomationId(_saveButton, "RecordingSaveButton");

        _cancelButton = BuildButton("Cancel", isDanger: true);
        _cancelButton.Click += (_, _) => CancelRequested?.Invoke();
        AutomationProperties.SetAutomationId(_cancelButton, "RecordingCancelButton");

        var actionRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        actionRow.Children.Add(_startStopButton);
        actionRow.Children.Add(_restartButton);
        actionRow.Children.Add(_saveButton);
        actionRow.Children.Add(_cancelButton);

        _normalPanel = new StackPanel { Margin = new Thickness(12, 8, 12, 8) };
        _normalPanel.Children.Add(indicatorRow);
        _normalPanel.Children.Add(_audioRow);
        _normalPanel.Children.Add(actionRow);

        // Inline restart confirmation (item: "guarded by a confirmation prompt"). Built in the SAME
        // window rather than a second dialog/window so there is only ever one HWND to keep
        // WDA-excluded for the whole recording - a second, un-excluded confirmation window on
        // screen mid-capture would bake itself into the recording.
        var confirmText = new TextBlock
        {
            Text = "Discard this recording and start over?",
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 13,
            Foreground = new SolidColorBrush(TextPrimary),
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 220,
        };
        var confirmYes = BuildButton("Discard & Restart", isDanger: true);
        confirmYes.Click += (_, _) =>
        {
            HideRestartConfirm();
            RestartConfirmed?.Invoke();
        };
        var confirmNo = BuildButton("Keep recording", isDanger: false);
        confirmNo.Click += (_, _) => HideRestartConfirm();
        var confirmRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        confirmRow.Children.Add(confirmYes);
        confirmRow.Children.Add(confirmNo);

        _confirmPanel = new StackPanel { Margin = new Thickness(12, 8, 12, 8), Visibility = Visibility.Collapsed };
        _confirmPanel.Children.Add(confirmText);
        _confirmPanel.Children.Add(confirmRow);

        _root = new Grid();
        _root.Children.Add(_normalPanel);
        _root.Children.Add(_confirmPanel);

        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xEB, 0x0E, 0x0E, 0x11)), // RlPanelBackground
            BorderBrush = new SolidColorBrush(BorderStrong),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Child = _root,
        };

        ApplyState();
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
            Background = isDanger ? new SolidColorBrush(DangerFill) : new SolidColorBrush(GhostFill),
            Foreground = new SolidColorBrush(TextPrimary),
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

    /// <summary>Orange-filled primary action (Start/Save) - the roeshare v_primary recipe (solid
    /// orange fill, dark-on-primary text, no gradient/glow), matching ToolbarControl's
    /// ActionButtonStyle at a smaller footprint for this compact HUD.</summary>
    private static Button BuildPrimaryButton(string text)
    {
        var button = new Button
        {
            Content = text,
            Padding = new Thickness(12, 4, 12, 4),
            Margin = new Thickness(0, 0, 4, 0),
            Cursor = Cursors.Hand,
            Focusable = false,
            Background = new SolidColorBrush(PrimaryOrange),
            Foreground = new SolidColorBrush(TextOnPrimary),
            BorderBrush = new SolidColorBrush(PrimaryOrangeBorder),
            BorderThickness = new Thickness(1),
        };
        var template = new ControlTemplate(typeof(Button));
        var borderFactory = new FrameworkElementFactory(typeof(Border));
        borderFactory.Name = "Bg";
        borderFactory.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Button.BackgroundProperty));
        borderFactory.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Button.BorderBrushProperty));
        borderFactory.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Button.BorderThicknessProperty));
        borderFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        borderFactory.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Button.PaddingProperty));
        var contentFactory = new FrameworkElementFactory(typeof(ContentPresenter));
        contentFactory.SetValue(ContentPresenter.HorizontalAlignmentProperty, HAlign.Center);
        contentFactory.SetValue(ContentPresenter.VerticalAlignmentProperty, VAlign.Center);
        borderFactory.AppendChild(contentFactory);
        template.VisualTree = borderFactory;

        var hoverTrigger = new Trigger { Property = Button.IsMouseOverProperty, Value = true };
        hoverTrigger.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(PrimaryOrangeLight), "Bg"));
        var disabledTrigger = new Trigger { Property = Button.IsEnabledProperty, Value = false };
        disabledTrigger.Setters.Add(new Setter(Button.OpacityProperty, 0.5));
        template.Triggers.Add(hoverTrigger);
        template.Triggers.Add(disabledTrigger);

        button.Template = template;
        return button;
    }

    /// <summary>Small pill-shaped checkable chip for the Mic / System audio toggles: flat neutral
    /// at rest, an orange tint when checked (the same "flat tint, no glow" active-item recipe
    /// ToolbarControl's ToolButtonStyle uses).</summary>
    private static ToggleButton BuildAudioToggle(string label)
    {
        var toggle = new ToggleButton
        {
            Content = label,
            Padding = new Thickness(10, 3, 10, 3),
            Margin = new Thickness(0, 0, 6, 0),
            Cursor = Cursors.Hand,
            Focusable = false,
            Foreground = new SolidColorBrush(TextPrimary),
        };
        var template = new ControlTemplate(typeof(ToggleButton));
        var borderFactory = new FrameworkElementFactory(typeof(Border));
        borderFactory.Name = "Bg";
        borderFactory.SetValue(Border.BackgroundProperty, new SolidColorBrush(GhostFill));
        borderFactory.SetValue(Border.BorderBrushProperty, new SolidColorBrush(BorderStrong));
        borderFactory.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        borderFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(12));
        borderFactory.SetValue(Border.PaddingProperty, new TemplateBindingExtension(ToggleButton.PaddingProperty));
        var contentFactory = new FrameworkElementFactory(typeof(ContentPresenter));
        contentFactory.SetValue(ContentPresenter.HorizontalAlignmentProperty, HAlign.Center);
        contentFactory.SetValue(ContentPresenter.VerticalAlignmentProperty, VAlign.Center);
        borderFactory.AppendChild(contentFactory);
        template.VisualTree = borderFactory;

        var checkedTrigger = new Trigger { Property = ToggleButton.IsCheckedProperty, Value = true };
        checkedTrigger.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(ActiveTint), "Bg"));
        checkedTrigger.Setters.Add(new Setter(Border.BorderBrushProperty, new SolidColorBrush(PrimaryOrange), "Bg"));
        var disabledTrigger = new Trigger { Property = ToggleButton.IsEnabledProperty, Value = false };
        disabledTrigger.Setters.Add(new Setter(ToggleButton.OpacityProperty, 0.5));
        template.Triggers.Add(checkedTrigger);
        template.Triggers.Add(disabledTrigger);

        toggle.Template = template;
        return toggle;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;

        // WS_EX_TOOLWINDOW: no Alt+Tab entry for this HUD.
        long exStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(exStyle | NativeMethods.WS_EX_TOOLWINDOW));

        // Hard rule: any window that can be on screen during a capture/recording must exclude
        // itself from capture, or it bakes itself into the recording. This one HWND stays up
        // through Setup, Recording AND Reviewing (and the inline restart-confirm content swap), so
        // excluding it once here covers the whole lifetime - see the class doc comment for why the
        // confirm prompt is inline instead of a second window. Same escape hatch
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
    /// off either side. Re-run after every state change (Setup/Recording/Reviewing/confirm all
    /// have different content sizes) so the panel stays anchored to the selection instead of
    /// drifting as SizeToContent grows or shrinks it from a fixed top-left corner.</summary>
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

    /// <summary>Re-anchors once the pending layout pass from a content/state change has actually
    /// measured the new size — mirrors OverlayWindow's own toolbar-reflow-after-layout pattern.</summary>
    private void RequestReposition()
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (IsLoaded && new WindowInteropHelper(this).Handle is { } hwnd && hwnd != IntPtr.Zero)
            {
                PositionNearSelection(hwnd);
            }
        }), DispatcherPriority.Loaded);
    }

    // ---------- State transitions (driven by RecordingSession) ----------

    public void EnterSetup()
    {
        _state = ChromeState.Setup;
        _showingRestartConfirm = false;
        ApplyState();
    }

    public void EnterRecording()
    {
        _state = ChromeState.Recording;
        _showingRestartConfirm = false;
        ApplyState();
    }

    public void EnterReviewing()
    {
        _state = ChromeState.Reviewing;
        _showingRestartConfirm = false;
        ApplyState();
    }

    private void ShowRestartConfirm()
    {
        _showingRestartConfirm = true;
        ApplyState();
    }

    private void HideRestartConfirm()
    {
        _showingRestartConfirm = false;
        ApplyState();
    }

    /// <summary>Single place that maps (_state, _showingRestartConfirm) onto visible/enabled
    /// controls - every state-changing method above just sets the fields and calls this, so the
    /// panel can never show an inconsistent combination.</summary>
    private void ApplyState()
    {
        _confirmPanel.Visibility = _showingRestartConfirm ? Visibility.Visible : Visibility.Collapsed;
        _normalPanel.Visibility = _showingRestartConfirm ? Visibility.Collapsed : Visibility.Visible;

        _redDot.Visibility = _state == ChromeState.Recording ? Visibility.Visible : Visibility.Collapsed;

        _startStopButton.Content = _state == ChromeState.Recording ? "Stop" : "Start";
        _startStopButton.IsEnabled = _state != ChromeState.Reviewing;

        // Audio config is fixed per take (baked into the encoder at Start) - only editable before
        // the first Start of the current take, i.e. in Setup. Disabled (not hidden) once running so
        // the panel doesn't resize; GIF hides the whole row regardless (see the ctor).
        _micToggle.IsEnabled = _state == ChromeState.Setup;
        _systemAudioToggle.IsEnabled = _state == ChromeState.Setup;

        // Restart only makes sense once something has actually been captured.
        _restartButton.IsEnabled = _state != ChromeState.Setup;

        // Save only finalizes a take that has already been stopped.
        _saveButton.IsEnabled = _state == ChromeState.Reviewing;

        RequestReposition();
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
