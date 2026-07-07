using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using RoeSnip.Interop;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace RoeSnip.App;

/// <summary>Minimal WPF settings window (DESIGN.md §6 / PLAN.md §3.3): binds every
/// <see cref="RoeSnipSettings"/> field, saves via <see cref="SettingsStore.Save(RoeSnipSettings)"/>,
/// and hands the updated settings back to the caller (TrayApp) via <c>onSaved</c> so it can
/// re-register the hotkey.</summary>
public partial class SettingsWindow : Window
{
    private readonly RoeSnipSettings _original;
    private readonly Action<RoeSnipSettings> _onSaved;

    private bool _capturingHotkey;
    private uint _pendingModifiers;
    private uint _pendingVirtualKey;

    public SettingsWindow(RoeSnipSettings settings, Action<RoeSnipSettings> onSaved)
    {
        InitializeComponent();

        _original = settings;
        _onSaved = onSaved;
        _pendingModifiers = settings.HotkeyModifiers;
        _pendingVirtualKey = settings.HotkeyVirtualKey;

        LoadFromSettings();
    }

    private void LoadFromSettings()
    {
        HotkeyDisplay.Text = DescribeHotkey(_pendingModifiers, _pendingVirtualKey);
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

    private void ChangeHotkeyButton_Click(object sender, RoutedEventArgs e)
    {
        _capturingHotkey = true;
        HotkeyDisplay.Text = "Press a key combination...";
        Focus();
        Keyboard.Focus(this);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (!_capturingHotkey)
        {
            base.OnPreviewKeyDown(e);
            return;
        }

        e.Handled = true;

        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
        {
            return; // still waiting for a non-modifier key
        }

        uint modifiers = 0;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) modifiers |= NativeMethods.MOD_CONTROL;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) modifiers |= NativeMethods.MOD_ALT;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) modifiers |= NativeMethods.MOD_SHIFT;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows)) modifiers |= NativeMethods.MOD_WIN;

        _pendingModifiers = modifiers;
        _pendingVirtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);
        _capturingHotkey = false;
        HotkeyDisplay.Text = DescribeHotkey(_pendingModifiers, _pendingVirtualKey);
    }

    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            SelectedPath = SaveDirectoryBox.Text,
        };

        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            SaveDirectoryBox.Text = dialog.SelectedPath;
        }
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryValidateToneMapOverrides(out double? kneeOverride, out double? peakOverride, out string? validationError))
        {
            System.Windows.MessageBox.Show(
                this, validationError, "RoeSnip", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        bool runAtStartup = RunAtStartupCheckBox.IsChecked == true;

        var updated = _original with
        {
            HotkeyModifiers = _pendingModifiers,
            HotkeyVirtualKey = _pendingVirtualKey,
            SaveDirectory = SaveDirectoryBox.Text,
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
            System.Windows.MessageBox.Show(
                this, $"Failed to update the Windows startup entry: {ex.Message}",
                "RoeSnip", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        try
        {
            SettingsStore.Save(updated);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                this, $"Failed to save settings: {ex.Message}",
                "RoeSnip", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        _onSaved(updated);
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => Close();

    private static double? ParseNullableDouble(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            ? value
            : null;
    }

    /// <summary>Validates the raw Knee/Peak override text boxes before they're allowed into
    /// RoeSnipSettings (audit finding F). ToneMapper.MapToSdr also defensively sanitizes bad values
    /// at map-time (never trust options end-to-end), but bad input must never be SAVED in the first
    /// place: a Knee/Peak pair with PeakOverride == Knee divides by zero downstream (NaN -> HDR
    /// highlights render as black, since Math.Clamp does not clamp NaN), and Knee greater than Peak
    /// silently hard-clips the entire frame — exactly the failure mode this app exists to prevent.
    /// Blank boxes mean "no override" (null), matching the pre-existing ParseNullableDouble
    /// behavior. Returns false (with a user-facing message, matching this window's existing
    /// MessageBox-on-failure pattern) if either box contains non-blank text that fails to parse, or
    /// a Knee/Peak pair that would misbehave.</summary>
    private bool TryValidateToneMapOverrides(out double? kneeOverride, out double? peakOverride, out string? error)
    {
        kneeOverride = null;
        peakOverride = null;
        error = null;

        string kneeText = KneeOverrideBox.Text;
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

        string peakText = PeakOverrideBox.Text;
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
        if ((modifiers & NativeMethods.MOD_CONTROL) != 0) parts.Add("Ctrl");
        if ((modifiers & NativeMethods.MOD_ALT) != 0) parts.Add("Alt");
        if ((modifiers & NativeMethods.MOD_SHIFT) != 0) parts.Add("Shift");
        if ((modifiers & NativeMethods.MOD_WIN) != 0) parts.Add("Win");
        parts.Add(virtualKey == NativeMethods.VK_SNAPSHOT ? "PrintScreen" : $"VK 0x{virtualKey:X2}");
        return string.Join("+", parts);
    }
}
