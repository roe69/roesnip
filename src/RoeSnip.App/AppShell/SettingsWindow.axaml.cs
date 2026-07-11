using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using RoeSnip.Core.Settings;

namespace RoeSnip.App.AppShell;

/// <summary>Avalonia port of the WPF app's SettingsWindow (PLAN-XPLAT.md §3.2): same fields
/// (hotkey capture, save directory picker, auto-save-HDR toggle, tone-map knee/peak overrides,
/// run-at-startup toggle, copy-on-select toggle), same validation rules, saves via
/// <see cref="SettingsStore.Save(RoeSnipSettings)"/> and hands the updated settings back to the
/// caller (TrayApp) via <c>onSaved</c> so it can re-register the hotkey. The Save-HDR and
/// tone-map fields stay visible on every OS (they're just settings values); run-at-startup calls
/// through to <see cref="StartupManager"/>, which decides per-OS behavior. WPF's MessageBox-based
/// validation errors became an inline error TextBlock (Avalonia has no MessageBox).</summary>
public partial class SettingsWindow : Avalonia.Controls.Window
{
    private readonly RoeSnipSettings _original;
    private readonly Action<RoeSnipSettings> _onSaved;
    private readonly bool _hotkeyUnavailableOnWayland;

    private bool _capturingHotkey;
    private uint _pendingModifiers;
    private uint _pendingVirtualKey;

    // Parameterless ctor for the XAML loader/previewer only.
    public SettingsWindow() : this(RoeSnipSettings.Default, hotkeyUnavailableOnWayland: false, _ => { }) { }

    public SettingsWindow(RoeSnipSettings settings, bool hotkeyUnavailableOnWayland, Action<RoeSnipSettings> onSaved)
    {
        InitializeComponent();

        _original = settings;
        _hotkeyUnavailableOnWayland = hotkeyUnavailableOnWayland;
        _onSaved = onSaved;
        _pendingModifiers = settings.HotkeyModifiers;
        _pendingVirtualKey = settings.HotkeyVirtualKey;

        LoadFromSettings();
    }

    private void LoadFromSettings()
    {
        HotkeyDisplay.Text = DescribeHotkey(_pendingModifiers, _pendingVirtualKey);
        WaylandHotkeyCaption.IsVisible = _hotkeyUnavailableOnWayland;
        SaveDirectoryBox.Text = _original.SaveDirectory;
        AutoSaveHdrCheckBox.IsChecked = _original.AutoSaveHdrCopy;
        CopyOnSelectCheckBox.IsChecked = _original.CopyOnSelect;
        KneeOverrideBox.Text = _original.ToneMapKneeOverride?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        PeakOverrideBox.Text = _original.ToneMapPeakOverride?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

        try
        {
            RunAtStartupCheckBox.IsChecked = StartupManager.IsRunAtStartupEnabled();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"RoeSnip: could not read run-at-startup state: {ex.Message}");
            RunAtStartupCheckBox.IsChecked = _original.RunAtStartup;
        }
    }

    private void ChangeHotkeyButton_Click(object? sender, RoutedEventArgs e)
    {
        _capturingHotkey = true;
        HotkeyDisplay.Text = "Press a key combination...";
        Focus();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (!_capturingHotkey)
        {
            base.OnKeyDown(e);
            return;
        }

        e.Handled = true;

        Key key = e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
        {
            return; // still waiting for a non-modifier key
        }

        uint? virtualKey = MapKeyToVirtualKey(key);
        if (virtualKey is null)
        {
            HotkeyDisplay.Text = "Unsupported key — press another combination...";
            return;
        }

        uint modifiers = 0;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control)) modifiers |= HotkeyManager.ModControl;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Alt)) modifiers |= HotkeyManager.ModAlt;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) modifiers |= HotkeyManager.ModShift;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Meta)) modifiers |= HotkeyManager.ModWin;

        _pendingModifiers = modifiers;
        _pendingVirtualKey = virtualKey.Value;
        _capturingHotkey = false;
        HotkeyDisplay.Text = DescribeHotkey(_pendingModifiers, _pendingVirtualKey);
    }

    /// <summary>Maps an Avalonia <see cref="Key"/> to the Windows virtual-key code the settings
    /// store persists (PLAN-XPLAT.md §2.6: HotkeyModifiers/HotkeyVirtualKey keep their Windows
    /// VK/MOD_* shape on every OS). The inverse of HotkeyManager.VirtualKeyToKeyCode's coverage —
    /// the same key set is supported on both ends.</summary>
    internal static uint? MapKeyToVirtualKey(Key key) => key switch
    {
        Key.Snapshot => HotkeyManager.VkSnapshot,
        Key.Pause => 0x13,
        Key.Space => 0x20,
        Key.PageUp => 0x21,
        Key.PageDown => 0x22,
        Key.End => 0x23,
        Key.Home => 0x24,
        Key.Left => 0x25,
        Key.Up => 0x26,
        Key.Right => 0x27,
        Key.Down => 0x28,
        Key.Insert => 0x2D,
        Key.Delete => 0x2E,
        >= Key.A and <= Key.Z => 0x41u + (uint)(key - Key.A),
        >= Key.D0 and <= Key.D9 => 0x30u + (uint)(key - Key.D0),
        >= Key.NumPad0 and <= Key.NumPad9 => 0x60u + (uint)(key - Key.NumPad0),
        >= Key.F1 and <= Key.F24 => 0x70u + (uint)(key - Key.F1),
        _ => null,
    };

    private async void BrowseButton_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            IStorageFolder? startLocation = null;
            string current = SaveDirectoryBox.Text ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(current))
            {
                startLocation = await StorageProvider.TryGetFolderFromPathAsync(current);
            }

            var result = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Choose the RoeSnip save directory",
                AllowMultiple = false,
                SuggestedStartLocation = startLocation,
            });

            if (result.Count > 0)
            {
                string? path = result[0].TryGetLocalPath();
                if (path is not null)
                {
                    SaveDirectoryBox.Text = path;
                }
            }
        }
        catch (Exception ex)
        {
            ShowValidationError($"Folder picker failed: {ex.Message}");
        }
    }

    private void SaveButton_Click(object? sender, RoutedEventArgs e)
    {
        if (!TryValidateToneMapOverrides(out double? kneeOverride, out double? peakOverride, out string? validationError))
        {
            ShowValidationError(validationError ?? "Invalid tone-map override.");
            return;
        }

        ClearValidationError();

        bool runAtStartup = RunAtStartupCheckBox.IsChecked == true;

        var updated = _original with
        {
            HotkeyModifiers = _pendingModifiers,
            HotkeyVirtualKey = _pendingVirtualKey,
            SaveDirectory = SaveDirectoryBox.Text ?? _original.SaveDirectory,
            AutoSaveHdrCopy = AutoSaveHdrCheckBox.IsChecked == true,
            CopyOnSelect = CopyOnSelectCheckBox.IsChecked == true,
            ToneMapKneeOverride = kneeOverride,
            ToneMapPeakOverride = peakOverride,
            RunAtStartup = runAtStartup,
        };

        try
        {
            StartupManager.SetRunAtStartup(runAtStartup);
        }
        catch (Exception ex)
        {
            // Non-fatal, matching the WPF window: warn but continue to save the settings file.
            ShowValidationError($"Failed to update the startup entry: {ex.Message}");
        }

        try
        {
            SettingsStore.Save(updated);
        }
        catch (Exception ex)
        {
            ShowValidationError($"Failed to save settings: {ex.Message}");
            return;
        }

        _onSaved(updated);
        Close();
    }

    private void CancelButton_Click(object? sender, RoutedEventArgs e) => Close();

    private void ShowValidationError(string message)
    {
        ValidationErrorText.Text = message;
        ValidationErrorText.IsVisible = true;
    }

    private void ClearValidationError()
    {
        ValidationErrorText.Text = string.Empty;
        ValidationErrorText.IsVisible = false;
    }

    /// <summary>Validates the raw Knee/Peak override text boxes before they're allowed into
    /// RoeSnipSettings (ported unchanged from the WPF window — audit finding F). ToneMapper also
    /// defensively sanitizes bad values at map-time, but bad input must never be SAVED in the
    /// first place: PeakOverride == Knee divides by zero downstream (NaN → HDR highlights render
    /// as black), and Knee greater than Peak silently hard-clips the entire frame. Blank boxes
    /// mean "no override" (null).</summary>
    private bool TryValidateToneMapOverrides(out double? kneeOverride, out double? peakOverride, out string? error)
    {
        kneeOverride = null;
        peakOverride = null;
        error = null;

        string kneeText = KneeOverrideBox.Text ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(kneeText))
        {
            if (!double.TryParse(kneeText, NumberStyles.Float, CultureInfo.InvariantCulture, out double knee)
                || !double.IsFinite(knee) || knee < 0.5 || knee > 0.99)
            {
                error = "Knee override must be a number between 0.5 and 0.99 (or blank for automatic).";
                return false;
            }
            kneeOverride = knee;
        }

        string peakText = PeakOverrideBox.Text ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(peakText))
        {
            if (!double.TryParse(peakText, NumberStyles.Float, CultureInfo.InvariantCulture, out double peak)
                || !double.IsFinite(peak))
            {
                error = "Peak override must be a number (or blank for automatic).";
                return false;
            }

            double effectiveKnee = kneeOverride ?? 0.90;
            double minPeak = effectiveKnee + 0.05;
            if (peak < minPeak)
            {
                error = $"Peak override must be at least {minPeak.ToString("0.00", CultureInfo.InvariantCulture)} " +
                        $"(0.05 above the knee, {effectiveKnee.ToString("0.00", CultureInfo.InvariantCulture)}).";
                return false;
            }
            peakOverride = peak;
        }

        return true;
    }

    private static string DescribeHotkey(uint modifiers, uint virtualKey)
    {
        var parts = new List<string>();
        if ((modifiers & HotkeyManager.ModControl) != 0) parts.Add("Ctrl");
        if ((modifiers & HotkeyManager.ModAlt) != 0) parts.Add("Alt");
        if ((modifiers & HotkeyManager.ModShift) != 0) parts.Add("Shift");
        if ((modifiers & HotkeyManager.ModWin) != 0) parts.Add("Win");
        parts.Add(virtualKey == HotkeyManager.VkSnapshot ? "PrintScreen" : $"VK 0x{virtualKey:X2}");
        return string.Join("+", parts);
    }
}
