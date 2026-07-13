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

    public event Action? StartRequested;
    public event Action? StopRequested;
    public event Action? PauseRequested;
    public event Action? ResumeRequested;
    public event Action? RestartConfirmed;
    public event Action? SaveRequested;
    public event Action? ShareRequested;
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

        _startStopButton = BuildPrimaryButton("Start");
        _startStopButton.Click += (_, _) =>
        {
            if (_state == ChromeState.Setup) StartRequested?.Invoke();
            else if (_state == ChromeState.Recording) StopRequested?.Invoke();
        };

        _pauseResumeButton = BuildButton("Pause", isDanger: false);
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

        _restartButton = BuildButton("Restart", isDanger: false);
        _restartButton.Click += (_, _) => ShowRestartConfirm();

        _saveButton = BuildPrimaryButton("Save");
        _saveButton.Click += (_, _) => SaveRequested?.Invoke();

        _shareButton = BuildButton("Share", isDanger: false);
        _shareButton.Click += (_, _) => ShareRequested?.Invoke();

        _cancelButton = BuildButton("Cancel", isDanger: true);
        _cancelButton.Click += (_, _) => CancelRequested?.Invoke();

        var actionRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        actionRow.Children.Add(_startStopButton);
        actionRow.Children.Add(_pauseResumeButton);
        actionRow.Children.Add(_restartButton);
        actionRow.Children.Add(_saveButton);
        actionRow.Children.Add(_shareButton);
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

        var root = new Grid();
        root.Children.Add(_normalPanel);
        root.Children.Add(_confirmPanel);

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
        _normalPanel.IsVisible = !_showingRestartConfirm;

        _redDot.IsVisible = _state == ChromeState.Recording;

        _startStopButton.Content = _state == ChromeState.Recording ? "Stop" : "Start";
        _startStopButton.IsEnabled = _state != ChromeState.Reviewing;
        _startStopButton.IsVisible = _state != ChromeState.Reviewing;

        _pauseResumeButton.IsVisible = _state is ChromeState.Recording or ChromeState.Reviewing;

        _micToggle.IsEnabled = _state == ChromeState.Setup && _micSupported;
        _systemAudioToggle.IsEnabled = _state == ChromeState.Setup && _systemAudioSupported;
        _sizeRow.IsEnabled = _state == ChromeState.Setup;
        _fpsRow.IsEnabled = _state == ChromeState.Setup;

        _restartButton.IsEnabled = _state != ChromeState.Setup;
        _saveButton.IsEnabled = _state == ChromeState.Reviewing;
        _shareButton.IsEnabled = _state == ChromeState.Reviewing && _shareAvailable;

        RequestReposition();
    }

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
