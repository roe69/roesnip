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
using RoeSnip.Recording.Gif;

namespace RoeSnip.Recording;

// Same aliasing convention as the sibling Overlay/* files (RoeSnip.csproj enables both UseWPF and
// UseWindowsForms, so System.Windows.Forms/System.Drawing collide with WPF names).
using Color = System.Windows.Media.Color;
using Button = System.Windows.Controls.Button;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;
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
///               System audio - hidden entirely for GIF, which has no audio track), the Quality
///               row (Max/High/Medium/Low/Min pills, shown for BOTH formats as of the
///               recording-size-tiers workstream, since MP4 bitrate tiers and GIF encoder tiers
///               are the same five-way promise even though the codecs are unrelated - persisted
///               enum members/settings strings stay Max/Quality/Balanced/Compact/Minimal, only the
///               display label changed, see GifSizePresets.DisplayLabel), the FPS row directly
///               under it (a free-integer slider over the format's own Min/MaxFps range -
///               quality/framerate decoupling workstream, stage 3, widened from four fixed chips
///               to a slider by the quality/fps expansion workstream: fps is an independent Setup
///               choice instead of an emergent side effect of the quality tier) plus a live size
///               estimate readout under both, and Cancel.
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
    private static readonly Color GhostFill = Color.FromArgb(0x0F, 0xFF, 0xFF, 0xFF); // rgba(white,.06)
    private static readonly Color BorderStrong = Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF); // rgba(white,.14)
    private static readonly Color DangerFill = Color.FromArgb(0x26, 0xDC, 0x26, 0x26);
    private static readonly Color DangerSolid = Color.FromRgb(0xDC, 0x26, 0x26);

    private readonly MonitorInfo _monitor;
    private RectPhysical _selectionPx; // re-anchored when the user drags the region (RegionOutline)
    private readonly RecordingFormat _format;
    private ChromeState _state = ChromeState.Setup;
    private bool _showingRestartConfirm;

    private readonly Ellipse _redDot;
    private readonly TextBlock _elapsedText;
    private readonly Button _startStopButton;
    private readonly Button _pauseResumeButton;
    private bool _paused; // mirrors RecordingSession's own _paused, set via SetPaused - drives the button label + dot
    private readonly ToggleButton _micToggle;
    private readonly ToggleButton _systemAudioToggle;
    private readonly StackPanel _audioRow;
    private readonly ToggleButton _sizeMaxChip;
    private readonly ToggleButton _sizeQualityChip;
    private readonly ToggleButton _sizeBalancedChip;
    private readonly ToggleButton _sizeCompactChip;
    private readonly ToggleButton _sizeMinimalChip;
    private readonly StackPanel _sizeRow;
    // FPS row (quality/fps expansion workstream): a free-integer WPF Slider replaces the old
    // four-chip row — see the ctor's own comment (where the slider is built) for why a slider fits
    // a wide, mostly-continuous range better than a fixed chip set.
    private readonly Slider _fpsSlider;
    private readonly TextBlock _fpsValueLabel;
    private readonly StackPanel _fpsRow;
    // Debounces FpsChanged/SettingsStore.Save while the slider is actively being dragged — see
    // RestartFpsDebounce's own doc comment.
    private readonly DispatcherTimer _fpsDebounceTimer;
    private int _lastPersistedFps; // last value FpsChanged actually fired for — suppresses a redundant persist
    private readonly TextBlock _estimateText;
    private GifSizePreset _sizePreset; // mirrors whichever size chip is currently checked
    private int _fps; // mirrors the FPS slider's current value - drives the MP4/GIF estimate
                       // together with _sizePreset; was a ctor-only readonly value pre-decoupling,
                       // now user-editable in Setup like the size preset (see ApplyFpsValue)
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
    /// <summary>Recording state's Pause/Resume button, while not paused.</summary>
    public event Action? PauseRequested;
    /// <summary>Recording state's Pause/Resume button, while paused.</summary>
    public event Action? ResumeRequested;
    /// <summary>Raised only after the user confirms the inline "discard and start over?" prompt.</summary>
    public event Action? RestartConfirmed;
    /// <summary>Reviewing state's Save button.</summary>
    public event Action? SaveRequested;
    /// <summary>Available in every state - aborts the whole recording without saving.</summary>
    public event Action? CancelRequested;
    public event Action<bool>? MicToggled;
    public event Action<bool>? SystemAudioToggled;
    /// <summary>Setup state's size preset row - fires once per actual selection change (clicking
    /// the already-checked chip is a no-op, see SelectSizePreset). Fires for BOTH formats now (the
    /// row is shown for both) - RecordingSession.SetSizePreset persists to whichever settings key
    /// matches the take's own format.</summary>
    public event Action<GifSizePreset>? SizePresetChanged;
    /// <summary>Setup state's FPS row (quality/framerate decoupling workstream, stage 3) - fires
    /// once per actual selection change, same no-op-on-re-click contract as
    /// <see cref="SizePresetChanged"/> above (see <see cref="SelectFps"/>).
    /// RecordingSession.SetFps persists to whichever settings key matches the take's own format.</summary>
    public event Action<int>? FpsChanged;

    public RecordingChrome(
        MonitorInfo monitor, RectPhysical selectionPx, RecordingFormat format,
        bool initialMic, bool initialSystemAudio, GifSizePreset initialSizePreset, int fps)
    {
        _monitor = monitor;
        _selectionPx = selectionPx;
        _format = format;
        _fps = fps;

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

        // Pause/Resume is its own button (not folded into Start/Stop) - Stop must remain reachable
        // while paused (it ends the take into review), so the two need independent labels/events.
        _pauseResumeButton = BuildButton("Pause", isDanger: false);
        _pauseResumeButton.Click += (_, _) =>
        {
            if (_paused) ResumeRequested?.Invoke();
            else PauseRequested?.Invoke();
        };
        AutomationProperties.SetAutomationId(_pauseResumeButton, "RecordingPauseResumeButton");

        _micToggle = BuildAudioToggle("Mic", initialMic);
        _micToggle.IsChecked = initialMic;
        _micToggle.Click += (_, _) =>
        {
            bool on = _micToggle.IsChecked == true;
            SetAudioToggleLabel(_micToggle, "Mic", on);
            MicToggled?.Invoke(on);
            UpdateEstimate(); // MP4 only in practice (this toggle is hidden for GIF), but harmless either way
        };

        _systemAudioToggle = BuildAudioToggle("System audio", initialSystemAudio);
        _systemAudioToggle.IsChecked = initialSystemAudio;
        _systemAudioToggle.Click += (_, _) =>
        {
            bool on = _systemAudioToggle.IsChecked == true;
            SetAudioToggleLabel(_systemAudioToggle, "System audio", on);
            SystemAudioToggled?.Invoke(on);
            UpdateEstimate();
        };

        _audioRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        _audioRow.Children.Add(_micToggle);
        _audioRow.Children.Add(_systemAudioToggle);
        // GIF has no audio track at all - the row is gone, not just grayed, since it never applies.
        _audioRow.Visibility = _format == RecordingFormat.Mp4 ? Visibility.Visible : Visibility.Collapsed;

        // Size preset row - shown for BOTH formats as of the recording-size-tiers workstream (MP4
        // bitrate tiers and GIF encoder tiers are the same five-way promise as of the quality/fps
        // expansion workstream's Minimal tier). Radio behavior across the five chips, not five
        // independent booleans: exactly one is checked at all times, enforced by SelectSizePreset
        // explicitly setting all five chips' IsChecked values on every click (including a re-click
        // of the already-checked chip, which ToggleButton's own built-in click-toggles-IsChecked
        // behavior would otherwise uncheck). Chip TEXT is the Max/High/Medium/Low/Min display label
        // (GifSizePresets.DisplayLabel) - the enum member names and persisted settings strings
        // underneath stay Max/Quality/Balanced/Compact/Minimal unchanged.
        _sizePreset = initialSizePreset;
        _sizeMaxChip = BuildChip(GifSizePresets.DisplayLabel(GifSizePreset.Max), initialSizePreset == GifSizePreset.Max);
        _sizeMaxChip.Click += (_, _) => SelectSizePreset(GifSizePreset.Max);
        _sizeQualityChip = BuildChip(GifSizePresets.DisplayLabel(GifSizePreset.Quality), initialSizePreset == GifSizePreset.Quality);
        _sizeQualityChip.Click += (_, _) => SelectSizePreset(GifSizePreset.Quality);
        _sizeBalancedChip = BuildChip(GifSizePresets.DisplayLabel(GifSizePreset.Balanced), initialSizePreset == GifSizePreset.Balanced);
        _sizeBalancedChip.Click += (_, _) => SelectSizePreset(GifSizePreset.Balanced);
        _sizeCompactChip = BuildChip(GifSizePresets.DisplayLabel(GifSizePreset.Compact), initialSizePreset == GifSizePreset.Compact);
        _sizeCompactChip.Click += (_, _) => SelectSizePreset(GifSizePreset.Compact);
        _sizeMinimalChip = BuildChip(GifSizePresets.DisplayLabel(GifSizePreset.Minimal), initialSizePreset == GifSizePreset.Minimal);
        _sizeMinimalChip.Click += (_, _) => SelectSizePreset(GifSizePreset.Minimal);

        _sizeRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        _sizeRow.Children.Add(_sizeMaxChip);
        _sizeRow.Children.Add(_sizeQualityChip);
        _sizeRow.Children.Add(_sizeBalancedChip);
        _sizeRow.Children.Add(_sizeCompactChip);
        _sizeRow.Children.Add(_sizeMinimalChip);

        // FPS row - the decoupled framerate axis sitting directly under Quality (quality/framerate
        // decoupling workstream, stage 3). Quality/fps expansion workstream, stage 2: the old
        // four-chip row (one fixed choice per format) is REPLACED by a free-integer slider now that
        // RecordingSizeEstimator exposes a continuous Min/MaxFps range per format instead of an
        // allowed set — a slider reads naturally over a ~45-value range where four discrete chips
        // never could, and the patch-behind carry (GifEncoder.PatchLastDelay) makes every integer
        // fps in range legal, not just the four old divisors of 100.
        (int minFps, int maxFps) = _format == RecordingFormat.Gif
            ? (RecordingSizeEstimator.GifMinFps, RecordingSizeEstimator.GifMaxFps)
            : (RecordingSizeEstimator.Mp4MinFps, RecordingSizeEstimator.Mp4MaxFps);
        _fpsSlider = new Slider
        {
            Minimum = minFps,
            Maximum = maxFps,
            Value = fps,
            IsSnapToTickEnabled = true,
            TickFrequency = 1,
            Width = 150,
            VerticalAlignment = VAlign.Center,
            Focusable = false,
        };
        _fpsValueLabel = new TextBlock
        {
            Text = $"{fps} fps",
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 12,
            Foreground = new SolidColorBrush(TextPrimary),
            VerticalAlignment = VAlign.Center,
            Margin = new Thickness(8, 0, 0, 0),
            MinWidth = 44,
        };
        // ValueChanged fires on every tick while dragging - update the label/estimate live (cheap,
        // no I/O) but only ARM the debounce for the actual settings write (SetFps -> SettingsStore.
        // Save), so a drag across the whole range writes to disk once, not per-tick.
        _fpsSlider.ValueChanged += (_, e) =>
        {
            ApplyFpsValue((int)Math.Round(e.NewValue));
            RestartFpsDebounce();
        };
        _fpsDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _fpsDebounceTimer.Tick += (_, _) => PersistFpsNow();
        _lastPersistedFps = fps;

        _fpsRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        _fpsRow.Children.Add(_fpsSlider);
        _fpsRow.Children.Add(_fpsValueLabel);

        _estimateText = new TextBlock
        {
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 11,
            Foreground = new SolidColorBrush(TextMuted),
            Margin = new Thickness(0, 3, 0, 0),
        };

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
        actionRow.Children.Add(_pauseResumeButton);
        actionRow.Children.Add(_restartButton);
        actionRow.Children.Add(_saveButton);
        actionRow.Children.Add(_cancelButton);

        _normalPanel = new StackPanel { Margin = new Thickness(12, 8, 12, 8) };
        _normalPanel.Children.Add(indicatorRow);
        _normalPanel.Children.Add(_audioRow);
        _normalPanel.Children.Add(BuildRowHeader("Quality"));
        _normalPanel.Children.Add(_sizeRow);
        _normalPanel.Children.Add(BuildRowHeader("FPS"));
        _normalPanel.Children.Add(_fpsRow);
        _normalPanel.Children.Add(_estimateText);
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

        UpdateEstimate();
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

    /// <summary>Small pill-shaped checkable chip for the Mic / System audio toggles. The state must
    /// read at a glance, not just on close inspection: ON is a SOLID orange fill with dark
    /// on-primary text (the same recipe BuildPrimaryButton uses for Start/Save), OFF is a dim ghost
    /// pill with muted text. The label itself also carries the state ("Mic on"/"Mic off") via
    /// SetAudioToggleLabel, kept in sync from the Click handlers and the initial state below.</summary>
    private static ToggleButton BuildAudioToggle(string baseLabel, bool initiallyOn)
    {
        var toggle = new ToggleButton
        {
            Content = AudioToggleLabel(baseLabel, initiallyOn),
            Padding = new Thickness(10, 3, 10, 3),
            Margin = new Thickness(0, 0, 6, 0),
            Cursor = Cursors.Hand,
            Focusable = false,
            // Foreground is a LOCAL value (dark on-primary when on, muted when off), kept in sync by
            // SetAudioToggleLabel. It must NOT be a checked-trigger setter: a local value outranks a
            // template trigger in WPF, so a trigger would never apply and the on-text would stay gray.
            Foreground = new SolidColorBrush(initiallyOn ? TextOnPrimary : TextMuted),
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

        // Checked (ON): solid orange fill + orange border, unmistakably "active" - matches
        // BuildPrimaryButton's Start/Save recipe instead of the old faint tint. (The dark on-primary
        // text is applied as a local Foreground value, not here - see the ctor note above.)
        var checkedTrigger = new Trigger { Property = ToggleButton.IsCheckedProperty, Value = true };
        checkedTrigger.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(PrimaryOrange), "Bg"));
        checkedTrigger.Setters.Add(new Setter(Border.BorderBrushProperty, new SolidColorBrush(PrimaryOrangeBorder), "Bg"));
        var disabledTrigger = new Trigger { Property = ToggleButton.IsEnabledProperty, Value = false };
        disabledTrigger.Setters.Add(new Setter(ToggleButton.OpacityProperty, 0.5));
        template.Triggers.Add(checkedTrigger);
        template.Triggers.Add(disabledTrigger);

        toggle.Template = template;
        return toggle;
    }

    /// <summary>One chip of a radio-style row - visually IDENTICAL recipe to
    /// <see cref="BuildAudioToggle"/> (checked = solid orange fill + dark on-primary text, unchecked
    /// = dim ghost pill + muted text), but the label is just the choice's own name with no on/off
    /// suffix: unlike a standalone boolean toggle, this chip's meaning ("Balanced", "25") is stated
    /// by its own text regardless of which one is checked, and the checked/unchecked pair across
    /// the row's chips is what conveys the selection - reading any one chip's label already tells
    /// you what selecting it would do. Shared by BOTH the size preset row and the FPS row (the two
    /// chip rows differ only in their choice set and what each click means, not in how a chip looks
    /// or behaves) - see <see cref="SelectSizePreset"/>/<see cref="SelectFps"/> for the two radio
    /// handlers that use it.</summary>
    private static ToggleButton BuildChip(string label, bool initiallyChecked)
    {
        var chip = new ToggleButton
        {
            Content = label,
            IsChecked = initiallyChecked,
            Padding = new Thickness(10, 3, 10, 3),
            Margin = new Thickness(0, 0, 6, 0),
            Cursor = Cursors.Hand,
            Focusable = false,
            // Local value, not a checked-trigger setter - same reasoning as BuildAudioToggle's own
            // Foreground: a template trigger would never win over this local value in WPF.
            Foreground = new SolidColorBrush(initiallyChecked ? TextOnPrimary : TextMuted),
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
        checkedTrigger.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(PrimaryOrange), "Bg"));
        checkedTrigger.Setters.Add(new Setter(Border.BorderBrushProperty, new SolidColorBrush(PrimaryOrangeBorder), "Bg"));
        var disabledTrigger = new Trigger { Property = ToggleButton.IsEnabledProperty, Value = false };
        disabledTrigger.Setters.Add(new Setter(ToggleButton.OpacityProperty, 0.5));
        template.Triggers.Add(checkedTrigger);
        template.Triggers.Add(disabledTrigger);

        chip.Template = template;
        return chip;
    }

    /// <summary>Small muted caption above a chip row ("Quality" / "FPS") - the rows themselves carry
    /// no other label, so without this the four/four chips would read as an unlabeled block.</summary>
    private static TextBlock BuildRowHeader(string text) => new()
    {
        Text = text,
        FontFamily = new FontFamily("Segoe UI"),
        FontSize = 10,
        Foreground = new SolidColorBrush(TextMuted),
        Margin = new Thickness(0, 6, 0, 2),
    };

    /// <summary>Applies one chip's checked/unchecked visual state - the two properties every radio
    /// handler below (<see cref="SelectSizePreset"/>, <see cref="SelectFps"/>) needs to set on every
    /// chip in its row on every click (see <see cref="BuildChip"/>'s own doc comment for why
    /// Foreground must be set here as a local value, not a template trigger).</summary>
    private static void SetChipCheckedVisual(ToggleButton chip, bool isChecked)
    {
        chip.IsChecked = isChecked;
        chip.Foreground = new SolidColorBrush(isChecked ? TextOnPrimary : TextMuted);
    }

    /// <summary>One of the four size chips was clicked. Enforces the radio invariant (exactly
    /// one checked) by setting all four chips' IsChecked explicitly rather than trusting whichever
    /// one ToggleButton itself already flipped - this both un-checks the other three on a real switch
    /// AND re-checks the clicked chip when it was already the selected one (ToggleButton's built-in
    /// click behavior would otherwise leave a no-op click showing NOTHING selected). Only raises
    /// <see cref="SizePresetChanged"/> when the selection actually changed, but always recomputes the
    /// estimate readout (even a no-op re-click leaves the text correct, cheaply).</summary>
    private void SelectSizePreset(GifSizePreset preset)
    {
        bool changed = preset != _sizePreset;
        _sizePreset = preset;

        SetChipCheckedVisual(_sizeMaxChip, preset == GifSizePreset.Max);
        SetChipCheckedVisual(_sizeQualityChip, preset == GifSizePreset.Quality);
        SetChipCheckedVisual(_sizeBalancedChip, preset == GifSizePreset.Balanced);
        SetChipCheckedVisual(_sizeCompactChip, preset == GifSizePreset.Compact);
        SetChipCheckedVisual(_sizeMinimalChip, preset == GifSizePreset.Minimal);

        UpdateEstimate();
        if (changed)
        {
            SizePresetChanged?.Invoke(preset);
        }
    }

    /// <summary>Applies a new fps value to the slider's own visible state (mirror field, value
    /// label, live estimate) — called from every ValueChanged tick during a drag AND from
    /// <see cref="InvokeFps"/>, so both a live drag and an automation-driven set go through the
    /// exact same "what does this fps value look like" logic. Deliberately does NOT touch
    /// persistence/<see cref="FpsChanged"/> - see <see cref="RestartFpsDebounce"/>/
    /// <see cref="PersistFpsNow"/> for that half.</summary>
    private void ApplyFpsValue(int fps)
    {
        _fps = fps;
        _fpsValueLabel.Text = $"{fps} fps";
        UpdateEstimate();
    }

    /// <summary>Restarts the debounce window every time the slider moves (drag or arrow-key step):
    /// <see cref="_fpsDebounceTimer"/>'s Tick only ever fires once the value has sat still for its
    /// whole interval, so a continuous drag across the full range writes to
    /// <see cref="RoeSnip.App.SettingsStore"/> exactly once (at drag-end, or after the same short
    /// pause), not once per tick — "best-effort save must not spam disk per tick" per this
    /// workstream's own rule. <see cref="PersistFpsNow"/> is the eventual landing point either way,
    /// whether reached by this timer or by an immediate automation set.</summary>
    private void RestartFpsDebounce()
    {
        _fpsDebounceTimer.Stop();
        _fpsDebounceTimer.Start();
    }

    /// <summary>The actual persistence half of an fps change: raises <see cref="FpsChanged"/> (which
    /// RecordingSession.SetFps turns into a SettingsStore.Save), but only when the value genuinely
    /// differs from the last value actually persisted - same no-op-suppression contract the old
    /// chip row's SelectFps had, just decoupled from "the UI value changed" (which happens every
    /// slider tick) instead of coupled to it.</summary>
    private void PersistFpsNow()
    {
        _fpsDebounceTimer.Stop();
        if (_fps == _lastPersistedFps)
        {
            return;
        }
        _lastPersistedFps = _fps;
        FpsChanged?.Invoke(_fps);
    }

    /// <summary>Recomputes the live size-estimate readout from the current selection size, checked
    /// size chip, current FPS slider value, and (MP4 only) audio toggle state - see
    /// RecordingSizeEstimator for the actual math. Called from the ctor (initial value),
    /// SelectSizePreset (chip clicks) / ApplyFpsValue (every slider tick), UpdateSelection
    /// (Setup-phase region resize/move), and the audio toggle click handlers.</summary>
    private void UpdateEstimate()
    {
        int width = Math.Max(1, _selectionPx.Width);
        int height = Math.Max(1, _selectionPx.Height);
        if (_format == RecordingFormat.Mp4)
        {
            bool audioEnabled = _micToggle.IsChecked == true || _systemAudioToggle.IsChecked == true;
            double bytesPerSecond = RecordingSizeEstimator.Mp4BytesPerSecond(width, height, _fps, _sizePreset, audioEnabled);
            _estimateText.Text = RecordingSizeEstimator.FormatEstimate(bytesPerSecond);
        }
        else
        {
            double bytesPerSecond = RecordingSizeEstimator.GifTypicalBytesPerSecond(width, height, _fps, _sizePreset);
            // GIF's estimate is a typical-activity guess (see GifTypicalBytesPerSecond's own doc
            // comment), never a bound - the suffix says so, MP4's tighter target-bitrate estimate
            // above does not get one.
            _estimateText.Text = RecordingSizeEstimator.FormatEstimate(bytesPerSecond) + " (varies with motion)";
        }
    }

    private static string AudioToggleLabel(string baseLabel, bool on) => $"{baseLabel} {(on ? "on" : "off")}";

    /// <summary>Keeps a toggle's text in sync with its checked state after the user clicks it (the
    /// initial label is set once by BuildAudioToggle at construction).</summary>
    private static void SetAudioToggleLabel(ToggleButton toggle, string baseLabel, bool on)
    {
        toggle.Content = AudioToggleLabel(baseLabel, on);
        toggle.Foreground = new SolidColorBrush(on ? TextOnPrimary : TextMuted);
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

    /// <summary>The user dragged or resized the recorded region (RegionOutline): follow it so the
    /// HUD stays anchored to the region's new spot, and (a size change during Setup) so the live
    /// estimate readout reflects the new pixel dimensions. UI thread.</summary>
    public void UpdateSelection(RectPhysical selectionPx)
    {
        _selectionPx = selectionPx;
        UpdateEstimate();
        RequestReposition();
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
        _elapsedText.Text = "00:00"; // no take yet — clear a previous take's frozen clock
        ApplyState();
    }

    public void EnterRecording()
    {
        _state = ChromeState.Recording;
        _showingRestartConfirm = false;
        SetPaused(false); // fresh take (first Start, or after a Restart) never begins already-paused
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

        // Hidden (not disabled) in Reviewing: a grayed "Start" sitting next to the enabled
        // "Resume" would show two begin-recording controls in contradictory states.
        _startStopButton.Content = _state == ChromeState.Recording ? "Stop" : "Start";
        _startStopButton.IsEnabled = _state != ChromeState.Reviewing;
        _startStopButton.Visibility = _state == ChromeState.Reviewing ? Visibility.Collapsed : Visibility.Visible;

        // Pause/Resume shows mid-take AND in Reviewing: a soft-stopped take is a paused take (the
        // session calls SetPaused(true) on Stop, so the button reads and routes as Resume), and
        // resuming from review continues the same take with the review span cut out. Hidden only
        // in Setup, where there is no take yet. Stop stays reachable in Recording regardless of
        // _paused (the enable line above only checks _state), so Stop still works while paused.
        _pauseResumeButton.Visibility = _state is ChromeState.Recording or ChromeState.Reviewing
            ? Visibility.Visible : Visibility.Collapsed;

        // Audio config is fixed per take (baked into the encoder at Start) - only editable before
        // the first Start of the current take, i.e. in Setup. Disabled (not hidden) once running so
        // the panel doesn't resize; GIF hides the whole row regardless (see the ctor).
        _micToggle.IsEnabled = _state == ChromeState.Setup;
        _systemAudioToggle.IsEnabled = _state == ChromeState.Setup;

        // Size preset is likewise fixed per take (baked into GifEncoder.Create/Mp4Encoder.Create at
        // Start) - only editable in Setup. Disabled on the whole row (not hidden) once running,
        // mirroring the audio toggles above; the row itself is always visible now (both formats).
        _sizeRow.IsEnabled = _state == ChromeState.Setup;

        // FPS is likewise fixed per take (baked into RegionRecorder/GifEncoder.Create/Mp4Encoder.Create
        // at Start via RecordingSession._targetFps) - same Setup-only editability as the size row.
        _fpsRow.IsEnabled = _state == ChromeState.Setup;

        // Restart only makes sense once something has actually been captured.
        _restartButton.IsEnabled = _state != ChromeState.Setup;

        // Save only finalizes a take that has already been stopped.
        _saveButton.IsEnabled = _state == ChromeState.Reviewing;

        RequestReposition();
    }

    /// <summary>RecordingSession.Pause()/Resume() call this to flip the visible paused state: the
    /// button swaps Pause&lt;-&gt;Resume, and the red dot goes hollow (fill cleared, red outline
    /// instead) so a paused take reads as obviously paused rather than stopped - the elapsed text
    /// itself already freezes on its own (the controller's media clock stops advancing it), no
    /// action needed here for that part.</summary>
    public void SetPaused(bool paused)
    {
        _paused = paused;
        _pauseResumeButton.Content = paused ? "Resume" : "Pause";
        _redDot.Fill = paused ? Brushes.Transparent : new SolidColorBrush(DangerSolid);
        _redDot.Stroke = paused ? new SolidColorBrush(DangerSolid) : null;
        _redDot.StrokeThickness = paused ? 1.5 : 0;
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

    // ---------- Automation hooks (App/AutomationServer.cs) ----------
    //
    // Each Invoke* method raises the SAME Click routed event a real mouse click on that exact
    // button would (RaiseEvent, not a direct call into the handler) - the state-gating each
    // button's own Click handler already does (Start-vs-Stop sharing one button, e.g.) applies
    // unchanged, so these can never desync from what a real click does. RecordingSession's own
    // InvokeChromeAction wraps these with the phase validity checks a disabled button would
    // otherwise silently enforce (see that method).

    /// <summary>The live size-estimate readout's text, verbatim - AutomationServer's `state`
    /// command reports this exactly as shown, not a re-derived value.</summary>
    internal string EstimateText => _estimateText.Text;

    /// <summary>Mirrors whichever size chip is currently checked.</summary>
    internal GifSizePreset CurrentSizePreset => _sizePreset;

    /// <summary>Mirrors the FPS slider's current value.</summary>
    internal int CurrentFps => _fps;

    internal void InvokeStartStop() => _startStopButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    internal void InvokePauseResume() => _pauseResumeButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    internal void InvokeSave() => _saveButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    internal void InvokeCancel() => _cancelButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

    internal void InvokeSizePreset(GifSizePreset preset)
    {
        var chip = preset switch
        {
            GifSizePreset.Max => _sizeMaxChip,
            GifSizePreset.Quality => _sizeQualityChip,
            GifSizePreset.Balanced => _sizeBalancedChip,
            GifSizePreset.Compact => _sizeCompactChip,
            GifSizePreset.Minimal => _sizeMinimalChip,
            _ => throw new ArgumentOutOfRangeException(nameof(preset), preset, "Unknown GifSizePreset."),
        };
        chip.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    }

    /// <summary>Automation's equivalent of dragging the FPS slider to an exact value (quality/fps
    /// expansion workstream). RecordingSession.SetFpsForAutomation already rejects a value outside
    /// the current format's own Min/MaxFps range before ever calling this, so the range check below
    /// is a belt-and-braces invariant check, not a normal error path. Unlike a real drag, this must
    /// take effect (and persist) IMMEDIATELY rather than waiting out the debounce window — an
    /// automation caller has no way to "wait for the drag to settle", and the old chip-click
    /// contract this replaces always persisted synchronously — so this sets the slider's value
    /// (which raises ValueChanged when it actually differs), then unconditionally re-applies and
    /// force-persists rather than relying on that event alone, since WPF does not raise ValueChanged
    /// for a same-value set and a same-value automation call must still behave like a real
    /// re-click of the current value (recompute, no-op on persistence).</summary>
    internal void InvokeFps(int fps)
    {
        if (fps < (int)_fpsSlider.Minimum || fps > (int)_fpsSlider.Maximum)
        {
            throw new ArgumentOutOfRangeException(nameof(fps), fps, "fps is outside this format's slider range.");
        }
        _fpsSlider.Value = fps;
        ApplyFpsValue(fps);
        PersistFpsNow();
    }
}
