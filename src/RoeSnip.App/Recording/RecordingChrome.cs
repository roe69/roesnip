using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using RoeSnip.App.AppShell;
using RoeSnip.Core.Capture;
using RoeSnip.Core.Recording;
using RoeSnip.Core.Recording.Gif;
using RecordingSizeEstimator = RoeSnip.Core.Recording.RecordingSizeEstimator;

namespace RoeSnip.App.Recording;

/// <summary>The on-screen recording control area (item 21) - a small floating panel anchored just
/// outside the recorded selection that walks the user through THREE states, ported from the WPF
/// reference's src/RoeSnip/Recording/RecordingChrome.cs (that file's own doc comment is the full
/// design rationale; read it before changing behavior here):
///   Setup     - nothing is being captured yet. Start, the MP4-only audio toggles (Mic / System
///               audio - hidden entirely for GIF, disabled with a caption when this OS's
///               RecordingCapabilities reports them unsupported - see <see cref="SetAudioSupport"/>),
///               the Quality row (shown for both formats), the FPS row, a live size estimate, Cancel.
///   Recording - Start was pressed; RecordingController/RecordingSession is live. Red dot + ticking
///               elapsed time, Stop. Audio/quality/fps rows are disabled (not hidden) so the panel
///               doesn't jump.
///   Reviewing - Stop was pressed; the take is encoded to its temp file but not yet finalized. Shows
///               Restart, Save, Share (gated on <see cref="SetShareAvailable"/>); Cancel remains.
/// Restart asks for confirmation INLINE - swapping this same panel's content rather than opening a
/// second top-level window, so there is only ever one HWND to keep capture-excluded for the whole
/// recording lifetime (see <see cref="OnOpened"/>).
///
/// Not pooled/parked like FlashDimmer's windows - one instance per recording, created at Start,
/// closed at the end; recording start is not on the hotkey-to-dim latency path FlashDimmer optimizes.
///
/// Built entirely in code (no .axaml) - the same choice the WPF reference makes, and the same
/// choice this port's own FlashDimmer.cs (FlashWindow) already makes for a small, code-owned chrome
/// window. Deliberate simplification versus the WPF reference: buttons/toggles here are plain
/// Avalonia Button/ToggleButton controls with literal Background/Foreground colors instead of a
/// hand-built ControlTemplate+Trigger recipe - Avalonia's Fluent theme supplies the hover/press
/// chrome, so this loses WPF's exact hover recipe on this one compact HUD but keeps the same at-rest
/// on/off legibility (solid orange = on, dim ghost = off) the WPF version's own doc comment calls
/// out as the important part. Positioning uses Avalonia's own Position (PixelPoint, physical) /
/// Width/Height (DIP = physical / monitor.DpiX/96) the same way OverlayWindow already does - not
/// FlashDimmer's raw Win32 SetWindowPos, which exists there specifically for FlashDimmer's
/// latency-critical every-few-ms repositioning; this window repositions only on a user drag or a
/// state change, an ordinary case Avalonia's own window API already handles correctly.</summary>
public sealed class RecordingChrome : Window
{
    public enum ChromeState { Setup, Recording, Reviewing }

    private static readonly Color TextPrimary = Color.FromRgb(0xED, 0xED, 0xF0);
    private static readonly Color TextMuted = Color.FromRgb(0xA2, 0xA2, 0xAB);
    private static readonly Color PrimaryOrange = Color.FromRgb(0xFF, 0x6B, 0x35);
    private static readonly Color PrimaryOrangeBorder = Color.FromRgb(0xE5, 0x56, 0x1F);
    private static readonly Color TextOnPrimary = Color.FromRgb(0x18, 0x0D, 0x07);
    private static readonly Color GhostFill = Color.FromArgb(0x0F, 0xFF, 0xFF, 0xFF);
    private static readonly Color BorderStrong = Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF);
    private static readonly Color DangerFill = Color.FromArgb(0x26, 0xDC, 0x26, 0x26);
    private static readonly Color DangerSolid = Color.FromRgb(0xDC, 0x26, 0x26);
    private static readonly Color PanelBackground = Color.FromArgb(0xEB, 0x0E, 0x0E, 0x11);

    private MonitorInfo _monitor;
    private RectPhysical _selectionPx; // monitor-relative physical pixels - see RecordingSession's own field doc
    private readonly RecordingFormat _format;
    private ChromeState _state = ChromeState.Setup;
    private bool _showingRestartConfirm;

    private readonly Ellipse _redDot;
    private readonly TextBlock _elapsedText;
    private readonly Button _startStopButton;
    private readonly Button _pauseResumeButton;
    private bool _paused;
    private readonly ToggleButton _micToggle;
    private readonly ToggleButton _systemAudioToggle;
    private readonly bool _micSupported;
    private readonly bool _systemAudioSupported;
    private readonly StackPanel _audioRow;
    private readonly ToggleButton[] _sizeChips;
    private readonly GifSizePreset[] _sizeChipPresets;
    private readonly StackPanel _sizeRow;
    private readonly Slider _fpsSlider;
    private readonly TextBlock _fpsValueLabel;
    private readonly StackPanel _fpsRow;
    private readonly TextBlock _qualityHeader;
    private readonly TextBlock _fpsHeader;
    private readonly DispatcherTimer _fpsDebounceTimer;
    private int _lastPersistedFps;
    private readonly TextBlock _estimateText;
    private GifSizePreset _sizePreset;
    private int _fps;
    private readonly Button _restartButton;
    private readonly Button _saveButton;
    private readonly Button _shareButton;
    private bool _shareAvailable;
    private readonly Button _cancelButton;
    private readonly StackPanel _normalPanel;
    private readonly StackPanel _confirmPanel;
    private readonly Button _copyButton;
    // "Saved. Record another?" - the take is finished and the user decides what happens next,
    // instead of the session silently re-arming itself for a take they may not want.
    private readonly StackPanel _donePanel;
    private readonly TextBlock _doneText;
    private readonly Button _doneYesButton;
    private readonly Button _doneNoButton;
    private bool _showingDonePrompt;

    public event Action? StartRequested;
    public event Action? StopRequested;
    public event Action? PauseRequested;
    public event Action? ResumeRequested;
    public event Action? RestartConfirmed;
    public event Action? SaveRequested;
    public event Action? ShareRequested;
    /// <summary>Reviewing state's Copy button (and, on Windows, the Ctrl+C hook) - puts the finished
    /// take on the clipboard as a file. Same division of labour as SaveRequested/ShareRequested: the
    /// chrome only raises the request, RecordingController owns the temp path and does the work.</summary>
    public event Action? CopyRequested;
    /// <summary>"Record another" on the post-save prompt - re-arm for a new take, same region.</summary>
    public event Action? RecordAnotherRequested;
    /// <summary>"Done" on the post-save prompt - tear the session down.</summary>
    public event Action? DoneRequested;
    public event Action? CancelRequested;
    public event Action<bool>? MicToggled;
    public event Action<bool>? SystemAudioToggled;
    public event Action<GifSizePreset>? SizePresetChanged;
    public event Action<int>? FpsChanged;

    public RecordingChrome(
        MonitorInfo monitor, RectPhysical selectionPx, RecordingFormat format,
        bool initialMic, bool initialSystemAudio, bool micSupported, bool systemAudioSupported,
        GifSizePreset initialSizePreset, int fps)
    {
        _monitor = monitor;
        _selectionPx = selectionPx;
        _format = format;
        _fps = fps;
        _sizePreset = initialSizePreset;
        _micSupported = micSupported;
        _systemAudioSupported = systemAudioSupported;

        WindowDecorations = WindowDecorations.None;
        CanResize = false;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false; // never steals focus from the recorded window
        WindowStartupLocation = WindowStartupLocation.Manual;
        Cursor = new Cursor(StandardCursorType.Arrow);
        SizeToContent = SizeToContent.WidthAndHeight;
        Position = new PixelPoint(-100000, -100000); // placed for real once Opened/Rendered gives us a size

        _redDot = new Ellipse
        {
            Width = 10,
            Height = 10,
            Fill = new SolidColorBrush(DangerSolid),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
            IsVisible = false,
        };
        _elapsedText = new TextBlock
        {
            Text = "00:00",
            FontSize = 13,
            Foreground = new SolidColorBrush(TextPrimary),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 14, 0),
            MinWidth = 40,
        };
        var indicatorRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        indicatorRow.Children.Add(_redDot);
        indicatorRow.Children.Add(_elapsedText);

        // Start carries the SAME record mark as the screenshot toolbar's Record button (cream ring
        // + red core), on a quiet button like the toolbar's - see the WPF twin's own note.
        _startStopButton = BuildButton(string.Empty, isDanger: false);
        _startStopButton.Content = BuildRecordIcon();
        _startStopButton.Padding = new Thickness(9, 5, 9, 5);
        _startStopButton.Margin = new Thickness(0, 0, 4, 0);
        ToolTip.SetTip(_startStopButton, "Start recording");
        _startStopButton.Click += (_, _) =>
        {
            if (_state == ChromeState.Setup) StartRequested?.Invoke();
            else if (_state == ChromeState.Recording) StopRequested?.Invoke();
        };

        _pauseResumeButton = BuildIconButton(Icons.Pause, "Pause", isDanger: false);
        _pauseResumeButton.Click += (_, _) =>
        {
            if (_paused) ResumeRequested?.Invoke();
            else PauseRequested?.Invoke();
        };

        _micToggle = BuildAudioToggle("Mic", initialMic, micSupported);
        _micToggle.Click += (_, _) =>
        {
            bool on = _micToggle.IsChecked == true;
            SetAudioToggleLabel(_micToggle, "Mic", on);
            MicToggled?.Invoke(on);
            UpdateEstimate();
        };

        _systemAudioToggle = BuildAudioToggle("System audio", initialSystemAudio, systemAudioSupported);
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
        _audioRow.IsVisible = _format == RecordingFormat.Mp4; // GIF has no audio track at all

        _sizeChipPresets = new[]
        {
            GifSizePreset.Max, GifSizePreset.Quality, GifSizePreset.Balanced, GifSizePreset.Compact, GifSizePreset.Minimal,
        };
        _sizeChips = new ToggleButton[_sizeChipPresets.Length];
        _sizeRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        for (int i = 0; i < _sizeChipPresets.Length; i++)
        {
            var preset = _sizeChipPresets[i];
            var chip = BuildChip(GifSizePresets.DisplayLabel(preset), preset == initialSizePreset);
            chip.Click += (_, _) => SelectSizePreset(preset);
            _sizeChips[i] = chip;
            _sizeRow.Children.Add(chip);
        }

        (int minFps, int maxFps) = format == RecordingFormat.Gif
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
            VerticalAlignment = VerticalAlignment.Center,
            Focusable = false,
        };
        _fpsValueLabel = new TextBlock
        {
            Text = $"{fps} fps",
            FontSize = 12,
            Foreground = new SolidColorBrush(TextPrimary),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
            MinWidth = 44,
        };
        _fpsSlider.PropertyChanged += (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty)
            {
                ApplyFpsValue((int)Math.Round(_fpsSlider.Value));
                RestartFpsDebounce();
            }
        };
        _fpsDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _fpsDebounceTimer.Tick += (_, _) => PersistFpsNow();
        _lastPersistedFps = fps;

        _fpsRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        _fpsRow.Children.Add(_fpsSlider);
        _fpsRow.Children.Add(_fpsValueLabel);

        _estimateText = new TextBlock
        {
            FontSize = 11,
            Foreground = new SolidColorBrush(TextMuted),
            Margin = new Thickness(0, 3, 0, 0),
            MaxWidth = 220,
            TextWrapping = TextWrapping.Wrap,
        };

        _restartButton = BuildIconButton(Icons.Restart, "Restart (discards this take)", isDanger: false);
        _restartButton.Click += (_, _) => ShowRestartConfirm();

        // Save/Copy mirror the toolbar's colour roles: Save quiet with a gold-stroked icon, Copy
        // the one solid-gold fill with dark ink.
        _saveButton = BuildButton(string.Empty, isDanger: false);
        _saveButton.Content = BuildIcon(Icons.Save, PrimaryOrange);
        _saveButton.Padding = new Thickness(8, 5, 8, 5);
        ToolTip.SetTip(_saveButton, "Save");
        _saveButton.Click += (_, _) => SaveRequested?.Invoke();

        _shareButton = BuildIconButton(Icons.Share, "Share", isDanger: false);
        _shareButton.Click += (_, _) => ShareRequested?.Invoke();

        // Copy sits beside Save/Share (all three Reviewing-only), quiet like Share: it puts the take
        // on the clipboard as a file so it can be pasted straight into a chat/file manager with no
        // save-then-attach detour. On Windows Ctrl+C does the same thing (ReviewCopyHook); the
        // button is what makes it discoverable, and on Linux/macOS it is the only way in.
        _copyButton = BuildPrimaryButton(string.Empty);
        _copyButton.Content = BuildIcon(Icons.Copy, TextOnPrimary);
        _copyButton.Padding = new Thickness(8, 5, 8, 5);
        _copyButton.Margin = new Thickness(4, 0, 0, 0);
        ToolTip.SetTip(_copyButton, "Copy to clipboard (Ctrl+C on Windows)");
        _copyButton.Click += (_, _) => CopyRequested?.Invoke();

        _cancelButton = BuildIconButton(Icons.Cancel, "Cancel (discards this take)", isDanger: true, size: 11);
        _cancelButton.Click += (_, _) => CancelRequested?.Invoke();

        var actionRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        actionRow.Children.Add(_startStopButton);
        actionRow.Children.Add(_pauseResumeButton);
        actionRow.Children.Add(_restartButton);
        actionRow.Children.Add(_saveButton);
        actionRow.Children.Add(_copyButton);
        actionRow.Children.Add(_shareButton);
        actionRow.Children.Add(_cancelButton);

        _normalPanel = new StackPanel { Margin = new Thickness(12, 8, 12, 8) };
        _normalPanel.Children.Add(indicatorRow);
        _normalPanel.Children.Add(_audioRow);
        _qualityHeader = BuildRowHeader("Quality");
        _normalPanel.Children.Add(_qualityHeader);
        _normalPanel.Children.Add(_sizeRow);
        _fpsHeader = BuildRowHeader("FPS");
        _normalPanel.Children.Add(_fpsHeader);
        _normalPanel.Children.Add(_fpsRow);
        _normalPanel.Children.Add(_estimateText);
        _normalPanel.Children.Add(actionRow);

        var confirmText = new TextBlock
        {
            Text = "Discard this recording and start over?",
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

        _confirmPanel = new StackPanel { Margin = new Thickness(12, 8, 12, 8), IsVisible = false };
        _confirmPanel.Children.Add(confirmText);
        _confirmPanel.Children.Add(confirmRow);

        // Post-save prompt, in the same window and for the same reason as the restart confirm above:
        // one capture-excluded window, so nothing new can bake itself into a following take.
        _doneText = new TextBlock
        {
            FontSize = 13,
            Foreground = new SolidColorBrush(TextPrimary),
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 220,
        };
        _doneYesButton = BuildPrimaryButton("Record another");
        _doneYesButton.Click += (_, _) =>
        {
            HideDonePrompt();
            RecordAnotherRequested?.Invoke();
        };
        _doneNoButton = BuildButton("Done", isDanger: false);
        _doneNoButton.Click += (_, _) =>
        {
            HideDonePrompt();
            DoneRequested?.Invoke();
        };
        var doneRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        doneRow.Children.Add(_doneNoButton);
        doneRow.Children.Add(_doneYesButton);

        _donePanel = new StackPanel { Margin = new Thickness(12, 8, 12, 8), IsVisible = false };
        _donePanel.Children.Add(_doneText);
        _donePanel.Children.Add(doneRow);

        var root = new Grid();
        root.Children.Add(_normalPanel);
        root.Children.Add(_confirmPanel);
        root.Children.Add(_donePanel);

        Content = new Border
        {
            Background = new SolidColorBrush(PanelBackground),
            BorderBrush = new SolidColorBrush(BorderStrong),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Child = root,
        };

        UpdateEstimate();
        ApplyState();

        Opened += OnOpened;
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        // Item 02's capture exclusion, honoring ROESNIP_DIAG_NOEXCLUDE internally - no-ops on
        // non-Windows. This one HWND stays up through Setup, Recording AND Reviewing (and the
        // inline restart-confirm content swap), so excluding it once here covers the whole
        // lifetime - same reasoning as the WPF reference's own OnSourceInitialized.
        WindowCaptureExclusion.Apply(this);
        PositionNearSelection();
    }

    /// <summary>Action-row icons, identical to the WPF twin's own Icons class, which in turn takes
    /// them from Overlay/ToolbarControl so panel and toolbar read as ONE icon set: same 16x16
    /// space, same 14px box, same 1.6 round-capped stroke, same path data for Save/Copy/Share/
    /// Cancel. Record is not a path - it is a cream RING with a small red core (never a solid red
    /// dot), rebuilt in BuildRecordIcon. No emoji anywhere: they render in the system emoji font,
    /// ignore the control's foreground colour and change shape between OS versions.</summary>
    private static class Icons
    {
        public const string Stop = "M4,4 H12 V12 H4 Z";      // solid square: the universal stop mark
        public const string Pause = "M6,3 V13 M10,3 V13";
        public const string Play = "M5.5,3 L13,8 L5.5,13 Z"; // solid triangle, pairs with Pause
        public const string Restart = "M3.2,10.5 A5.5,5.5 0 1 1 8,13.5 M8,13.5 L10.6,11.4 M8,13.5 L10.6,15.6";
        public const string Save = "M8,1 V9 M4.5,5.5 L8,9 L11.5,5.5 M1.5,11 V14.5 H14.5 V11";
        public const string Copy = "M5.5,5.5 H14.5 V14.5 H5.5 Z M10.5,5.5 V1.5 H1.5 V10.5 H5.5";
        public const string Share = "M8,9 V1 M4.5,4.5 L8,1 L11.5,4.5 M1.5,11 V14.5 H14.5 V11";
        public const string Cancel = "M2,2 L14,14 M14,2 L2,14";
    }

    /// <summary>The toolbar's record mark: cream ring, small red core. Deliberately not a solid red
    /// dot - a filled blob outweighs every line-drawn icon beside it.</summary>
    private static Grid BuildRecordIcon()
    {
        var grid = new Grid();
        grid.Children.Add(new Ellipse
        {
            Width = 13,
            Height = 13,
            Stroke = new SolidColorBrush(TextPrimary),
            StrokeThickness = 1.5,
        });
        grid.Children.Add(new Ellipse
        {
            Width = 6,
            Height = 6,
            Fill = new SolidColorBrush(Color.FromRgb(0xDC, 0x46, 0x46)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        });
        return grid;
    }

    /// <summary>One action-row icon at the toolbar's own weight - see the WPF twin's BuildIcon.</summary>
    private static Avalonia.Controls.Shapes.Path BuildIcon(string data, Color color, bool filled = false, double size = 14)
    {
        var path = new Avalonia.Controls.Shapes.Path
        {
            Data = Geometry.Parse(data),
            Stretch = Stretch.Uniform,
            Width = size,
            Height = size,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (filled)
        {
            path.Fill = new SolidColorBrush(color);
        }
        else
        {
            path.Stroke = new SolidColorBrush(color);
            path.StrokeThickness = 1.6;
            path.StrokeLineCap = PenLineCap.Round;
            path.StrokeJoin = PenLineJoin.Round;
        }
        return path;
    }

    /// <summary>Icon-only action button; the label moves into the tooltip rather than disappearing,
    /// since a control with no name at all is not discoverable.</summary>
    private static Button BuildIconButton(string iconData, string tooltip, bool isDanger, bool filled = false, double size = 14)
    {
        var button = BuildButton(string.Empty, isDanger);
        button.Content = BuildIcon(iconData, TextPrimary, filled, size);
        button.Padding = new Thickness(8, 5, 8, 5);
        ToolTip.SetTip(button, tooltip);
        return button;
    }

    private static Button BuildButton(string text, bool isDanger) => new()
    {
        Content = text,
        Padding = new Thickness(10, 4, 10, 4),
        Margin = new Thickness(4, 0, 0, 0),
        Cursor = new Cursor(StandardCursorType.Hand),
        Focusable = false,
        Background = new SolidColorBrush(isDanger ? DangerFill : GhostFill),
        Foreground = new SolidColorBrush(TextPrimary),
        BorderThickness = new Thickness(0),
        CornerRadius = new CornerRadius(6),
    };

    private static Button BuildPrimaryButton(string text) => new()
    {
        Content = text,
        Padding = new Thickness(12, 4, 12, 4),
        Margin = new Thickness(0, 0, 4, 0),
        Cursor = new Cursor(StandardCursorType.Hand),
        Focusable = false,
        Background = new SolidColorBrush(PrimaryOrange),
        Foreground = new SolidColorBrush(TextOnPrimary),
        BorderBrush = new SolidColorBrush(PrimaryOrangeBorder),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(6),
    };

    /// <summary>Small pill-shaped checkable chip for the Mic / System audio toggles.
    /// <paramref name="supported"/> false (item 21 capability gating, RoeSnip.Core.Recording.
    /// RecordingCapabilities - GIF-only OSes have neither microphone nor loopback) disables the
    /// toggle and swaps its tooltip to explain why, mirroring the "considered, not absent" rule
    /// SaveHdrButton/ToolCursorCache fallback already use elsewhere in this port rather than hiding
    /// the control outright.</summary>
    private static ToggleButton BuildAudioToggle(string baseLabel, bool initiallyOn, bool supported)
    {
        bool on = initiallyOn && supported;
        var toggle = new ToggleButton
        {
            Content = AudioToggleLabel(baseLabel, on),
            IsChecked = on,
            Padding = new Thickness(10, 3, 10, 3),
            Margin = new Thickness(0, 0, 6, 0),
            Cursor = new Cursor(StandardCursorType.Hand),
            Focusable = false,
            IsEnabled = supported,
            Background = new SolidColorBrush(on ? PrimaryOrange : GhostFill),
            BorderBrush = new SolidColorBrush(on ? PrimaryOrangeBorder : BorderStrong),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Foreground = new SolidColorBrush(on ? TextOnPrimary : TextMuted),
        };
        ToolTip.SetTip(toggle, supported ? null : $"{baseLabel}: not supported on this platform/build");
        ToolTip.SetShowOnDisabled(toggle, !supported);
        return toggle;
    }

    private static ToggleButton BuildChip(string label, bool initiallyChecked) => new()
    {
        Content = label,
        IsChecked = initiallyChecked,
        Padding = new Thickness(10, 3, 10, 3),
        Margin = new Thickness(0, 0, 6, 0),
        Cursor = new Cursor(StandardCursorType.Hand),
        Focusable = false,
        Background = new SolidColorBrush(initiallyChecked ? PrimaryOrange : GhostFill),
        BorderBrush = new SolidColorBrush(initiallyChecked ? PrimaryOrangeBorder : BorderStrong),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(12),
        Foreground = new SolidColorBrush(initiallyChecked ? TextOnPrimary : TextMuted),
    };

    private static TextBlock BuildRowHeader(string text) => new()
    {
        Text = text,
        FontSize = 10,
        Foreground = new SolidColorBrush(TextMuted),
        Margin = new Thickness(0, 6, 0, 2),
    };

    private static void SetChipCheckedVisual(ToggleButton chip, bool isChecked)
    {
        chip.IsChecked = isChecked;
        chip.Background = new SolidColorBrush(isChecked ? PrimaryOrange : GhostFill);
        chip.BorderBrush = new SolidColorBrush(isChecked ? PrimaryOrangeBorder : BorderStrong);
        chip.Foreground = new SolidColorBrush(isChecked ? TextOnPrimary : TextMuted);
    }

    private void SelectSizePreset(GifSizePreset preset)
    {
        bool changed = preset != _sizePreset;
        _sizePreset = preset;
        for (int i = 0; i < _sizeChipPresets.Length; i++)
        {
            SetChipCheckedVisual(_sizeChips[i], _sizeChipPresets[i] == preset);
        }
        UpdateEstimate();
        if (changed)
        {
            SizePresetChanged?.Invoke(preset);
        }
    }

    private void ApplyFpsValue(int fps)
    {
        _fps = fps;
        _fpsValueLabel.Text = $"{fps} fps";
        UpdateEstimate();
    }

    private void RestartFpsDebounce()
    {
        _fpsDebounceTimer.Stop();
        _fpsDebounceTimer.Start();
    }

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
            _estimateText.Text = RecordingSizeEstimator.FormatEstimate(bytesPerSecond) + " (varies with motion)";
        }
    }

    private static string AudioToggleLabel(string baseLabel, bool on) => $"{baseLabel} {(on ? "on" : "off")}";

    private static void SetAudioToggleLabel(ToggleButton toggle, string baseLabel, bool on)
    {
        toggle.Content = AudioToggleLabel(baseLabel, on);
        toggle.Background = new SolidColorBrush(on ? PrimaryOrange : GhostFill);
        toggle.BorderBrush = new SolidColorBrush(on ? PrimaryOrangeBorder : BorderStrong);
        toggle.Foreground = new SolidColorBrush(on ? TextOnPrimary : TextMuted);
    }

    /// <summary>Anchors the HUD just below-right of the selection, flipping above if that would run
    /// off the bottom of the monitor, and clamping horizontally so it never runs off either side.
    /// Re-run after every state change (Setup/Recording/Reviewing/confirm all have different content
    /// sizes) so the panel stays anchored instead of drifting as SizeToContent grows/shrinks it from
    /// a fixed top-left corner. Ported from the WPF reference's PositionNearSelection, adapted to
    /// Avalonia's Position (PixelPoint physical) / Bounds (DIP) API instead of raw SetWindowPos - see
    /// this class's own doc comment for why that swap is safe here.</summary>
    private void PositionNearSelection()
    {
        double scale = _monitor.DpiX / 96.0;
        int barWidthPx = Math.Max(1, (int)Math.Ceiling(Bounds.Width * scale));
        int barHeightPx = Math.Max(1, (int)Math.Ceiling(Bounds.Height * scale));

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
            y = selTop - barHeightPx - gap;
        }
        bool fitsAbove = y >= bounds.Top;

        if (!fitsBelow && !fitsAbove)
        {
            y = Math.Clamp(selTop, bounds.Top, Math.Max(bounds.Top, bounds.Bottom - barHeightPx));
            x = selRight + gap + barWidthPx <= bounds.Right ? selRight + gap : selLeft - gap - barWidthPx;
        }

        x = Math.Clamp(x, bounds.Left, Math.Max(bounds.Left, bounds.Right - barWidthPx));
        y = Math.Clamp(y, bounds.Top, Math.Max(bounds.Top, bounds.Bottom - barHeightPx));

        Position = new PixelPoint(x, y);
    }

    /// <summary>Re-anchors once the pending layout pass from a content/state change has actually
    /// measured the new size - mirrors OverlayWindow's own reflow-after-layout pattern.</summary>
    private void RequestReposition()
    {
        if (!IsVisible)
        {
            return; // not opened yet - OnOpened's own call handles the initial placement
        }
        Dispatcher.UIThread.Post(PositionNearSelection, DispatcherPriority.Loaded);
    }

    // ---------- State transitions (driven by RecordingOrchestrator) ----------

    public void EnterSetup()
    {
        _state = ChromeState.Setup;
        _showingRestartConfirm = false;
        _elapsedText.Text = "00:00";
        ApplyState();
    }

    public void EnterRecording()
    {
        _state = ChromeState.Recording;
        _showingRestartConfirm = false;
        SetPaused(false);
        ApplyState();
    }

    public void EnterReviewing()
    {
        _state = ChromeState.Reviewing;
        _showingRestartConfirm = false;
        ApplyState();
    }

    /// <summary>Called by the orchestrator immediately before <see cref="EnterReviewing"/>, with a
    /// FRESH (not stale) answer to "does an enabled share provider resolve right now" - see
    /// RecordingOrchestrator.RequestShare's own doc comment. ApplyState reads this flag instead of
    /// checking whether anything subscribes to <see cref="ShareRequested"/>, mirroring the WPF
    /// reference's own senior-review fix (see RecordingChrome.cs's SetShareAvailable doc comment).</summary>
    public void SetShareAvailable(bool available) => _shareAvailable = available;

    /// <summary>Shown after a take has been finished (saved, or copied to the clipboard) instead of
    /// silently re-arming for another one: the session stays put until the user picks. The message
    /// names what just happened so "Record another" is an informed choice.</summary>
    public void ShowDonePrompt(string message)
    {
        _doneText.Text = message;
        _showingDonePrompt = true;
        _showingRestartConfirm = false;
        ApplyState();
    }

    public void HideDonePrompt()
    {
        _showingDonePrompt = false;
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

    private void ApplyState()
    {
        _confirmPanel.IsVisible = _showingRestartConfirm;
        _donePanel.IsVisible = _showingDonePrompt;
        _normalPanel.IsVisible = !_showingRestartConfirm && !_showingDonePrompt;

        _redDot.IsVisible = _state == ChromeState.Recording;

        _startStopButton.Content = _state == ChromeState.Recording
            ? BuildIcon(Icons.Stop, TextPrimary, filled: true, size: 11)
            : BuildRecordIcon();
        ToolTip.SetTip(_startStopButton, _state == ChromeState.Recording ? "Stop" : "Start recording");
        _startStopButton.IsEnabled = _state != ChromeState.Reviewing;
        _startStopButton.IsVisible = _state != ChromeState.Reviewing;

        _pauseResumeButton.IsVisible = _state is ChromeState.Recording or ChromeState.Reviewing;

        // Everything that CONFIGURES a take - audio toggles, quality preset, fps, the size estimate -
        // is baked into the encoder at Start and cannot change afterwards, so once a take exists it
        // is HIDDEN rather than left greyed out (1:1 with the WPF twin): Recording and Reviewing
        // then show only the take's clock and the actions that still do something.
        bool configurable = _state == ChromeState.Setup;
        _micToggle.IsEnabled = configurable && _micSupported;
        _systemAudioToggle.IsEnabled = configurable && _systemAudioSupported;
        if (_format == RecordingFormat.Mp4) // GIF has no audio row at all - see the ctor
        {
            _audioRow.IsVisible = configurable;
        }
        _qualityHeader.IsVisible = configurable;
        _sizeRow.IsVisible = configurable;
        _fpsHeader.IsVisible = configurable;
        _fpsRow.IsVisible = configurable;
        _estimateText.IsVisible = configurable;

        _restartButton.IsVisible = _state != ChromeState.Setup;
        // Every action below is HIDDEN, not just disabled, in the states it does not apply to (1:1
        // with the WPF twin): a row of grayed-out buttons is noise to read past, and this panel sits
        // on top of whatever the user is recording.
        _saveButton.IsVisible = _state == ChromeState.Reviewing;
        // Share additionally needs a provider to exist - it stays out of the row entirely until it
        // can actually work, rather than sitting there permanently disabled.
        _shareButton.IsVisible = _state == ChromeState.Reviewing && _shareAvailable;

        // Copy needs a finished take exactly like Save does, and unlike Share it has nothing to
        // depend on beyond that (the clipboard is always there).
        _copyButton.IsVisible = _state == ChromeState.Reviewing;

        RequestReposition();
    }

    public void SetPaused(bool paused)
    {
        _paused = paused;
        _pauseResumeButton.Content = paused
            ? BuildIcon(Icons.Play, TextPrimary, filled: true)
            : BuildIcon(Icons.Pause, TextPrimary);
        ToolTip.SetTip(_pauseResumeButton, paused ? "Resume" : "Pause");
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

    /// <summary>The user dragged or resized the recorded region (RegionOutline) - follow it so the
    /// HUD stays anchored, and (a size change during Setup) so the live estimate reflects the new
    /// pixel dimensions.</summary>
    public void UpdateSelection(RectPhysical selectionPx)
    {
        _selectionPx = selectionPx;
        UpdateEstimate();
        RequestReposition();
    }

    public void CloseChrome()
    {
        try { Close(); }
        catch (InvalidOperationException) { /* already closing */ }
    }

    // ---------- Automation hooks (item 21f) ----------
    //
    // Each Invoke* method drives the SAME handler a real click would (not a re-implementation) - the
    // state gating each button's own Click handler already does applies unchanged, so these can
    // never desync from what a real click does.

    public string EstimateText => _estimateText.Text ?? string.Empty;
    public GifSizePreset CurrentSizePreset => _sizePreset;
    public int CurrentFps => _fps;
    public ChromeState State => _state;

    public void InvokeStartStop()
    {
        if (_state == ChromeState.Setup) StartRequested?.Invoke();
        else if (_state == ChromeState.Recording) StopRequested?.Invoke();
    }

    public void InvokePauseResume()
    {
        if (_paused) ResumeRequested?.Invoke();
        else PauseRequested?.Invoke();
    }

    public void InvokeSave() => SaveRequested?.Invoke();
    public void InvokeShare() => ShareRequested?.Invoke();
    public void InvokeCancel() => CancelRequested?.Invoke();
    public void InvokeCopy() => CopyRequested?.Invoke();
    // The post-save prompt's two answers.
    public void InvokeRecordAnother()
    {
        HideDonePrompt();
        RecordAnotherRequested?.Invoke();
    }
    public void InvokeDone()
    {
        HideDonePrompt();
        DoneRequested?.Invoke();
    }
    public void InvokeRestartConfirmed() => RestartConfirmed?.Invoke();

    public void InvokeSizePreset(GifSizePreset preset) => SelectSizePreset(preset);

    /// <summary>Automation's equivalent of dragging the FPS slider to an exact value. Unlike a real
    /// drag, this takes effect (and persists) IMMEDIATELY rather than waiting out the debounce
    /// window - an automation caller has no way to "wait for the drag to settle" - mirroring the WPF
    /// reference's own InvokeFps.</summary>
    public void InvokeFps(int fps)
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
