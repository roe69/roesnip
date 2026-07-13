using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia.Controls;
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

    /// <summary>Sharing/* subsystem (item 12): ShareProvidersWindow self-persists (SettingsStore.Save)
    /// the moment a provider is added/edited/removed — same "writes immediately" precedent this
    /// window already sets for run-at-startup — rather than waiting for THIS window's own Save
    /// button. _current tracks that so SaveButton_Click bases its own `with` update off the latest
    /// on-disk ShareProviders/DefaultShareProviderId instead of the possibly-stale _original snapshot
    /// captured when this window was opened; every other field still only takes effect on Save
    /// exactly as before. Mirrors the WPF app's own SettingsWindow._current.</summary>
    private RoeSnipSettings _current;

    private bool _capturingHotkey;
    private uint _pendingModifiers;
    private uint _pendingVirtualKey;

    // Parameterless ctor for the XAML loader/previewer only.
    public SettingsWindow() : this(RoeSnipSettings.Default, hotkeyUnavailableOnWayland: false, _ => { }) { }

    public SettingsWindow(RoeSnipSettings settings, bool hotkeyUnavailableOnWayland, Action<RoeSnipSettings> onSaved)
    {
        InitializeComponent();

        _original = settings;
        _current = settings;
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

        RefreshShareProvidersUi();
    }

    // ---------- Sharing/* subsystem (item 12) ----------

    /// <summary>Guards DefaultShareProviderCombo.SelectedItem assignments below from re-entering
    /// DefaultShareProviderCombo_SelectionChanged.</summary>
    private bool _loadingShareProviders;

    private void RefreshShareProvidersUi()
    {
        _loadingShareProviders = true;
        try
        {
            DefaultShareProviderCombo.Items.Clear();

            var enabledProviders = RoeSnip.Core.Sharing.ShareManager.EffectiveConfigs(_current.ShareProviders)
                .Where(c => c.Enabled)
                .ToList();

            if (enabledProviders.Count == 0)
            {
                DefaultShareProviderCombo.Items.Add(new ComboBoxItem { Content = "No provider configured yet", Tag = "" });
                DefaultShareProviderCombo.SelectedIndex = 0;
                DefaultShareProviderCombo.IsEnabled = false;
                return;
            }

            DefaultShareProviderCombo.IsEnabled = true;
            ComboBoxItem? selected = null;
            foreach (var config in enabledProviders)
            {
                var item = new ComboBoxItem
                {
                    Content = string.IsNullOrWhiteSpace(config.DisplayName) ? config.Id : config.DisplayName,
                    Tag = config.Id,
                };
                DefaultShareProviderCombo.Items.Add(item);
                if (string.Equals(config.Id, _current.DefaultShareProviderId, StringComparison.Ordinal))
                {
                    selected = item;
                }
            }

            DefaultShareProviderCombo.SelectedItem = selected ?? DefaultShareProviderCombo.Items[0];
        }
        finally
        {
            _loadingShareProviders = false;
        }
    }

    private void DefaultShareProviderCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loadingShareProviders
            || DefaultShareProviderCombo.SelectedItem is not ComboBoxItem { Tag: string id } || id.Length == 0)
        {
            return;
        }
        _current = _current with { DefaultShareProviderId = id };
    }

    private async void ManageProvidersButton_Click(object? sender, RoutedEventArgs e)
    {
        var window = new ShareProvidersWindow(_current);
        await window.ShowDialog(this);

        // ShareProvidersWindow persists its own edits immediately (see this window's own field-level
        // doc comment on _current) — reload from disk so this window's combo/eventual Save reflect
        // whatever the user just changed, rather than the snapshot taken when Settings was opened.
        // Every OTHER field in this window is still only staged locally until Save is clicked — only
        // ShareProviders is meant to have been persisted by the sub-window (which never touches
        // DefaultShareProviderId — see ShareProvidersWindow.RemoveAndSave for the one exception,
        // clearing it when its own current provider is removed, a narrow case
        // ShareManager.ResolveDefault already tolerates by falling back to the first enabled
        // provider), so pull just that one field off the freshly-loaded settings and keep everything
        // else — INCLUDING DefaultShareProviderId — as this window's own in-progress (possibly
        // unsaved) edits. DefaultShareProviderId is edited by THIS window's own combo
        // (DefaultShareProviderCombo_SelectionChanged, staged onto _current same as every other field
        // below); pulling it from disk here would silently discard a combo selection the user made
        // just before clicking Providers... to double-check something, snapping it back to the stale
        // on-disk value the instant this dialog closes.
        var reloaded = SettingsStore.Load();
        _current = _current with { ShareProviders = reloaded.ShareProviders };
        RefreshShareProvidersUi();
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

        // Based on _current, not _original: _current already reflects any ShareProviders/
        // DefaultShareProviderId edits made (and self-persisted) via ManageProvidersButton_Click —
        // see that field's own doc comment. Every other field below is still this window's own
        // normal deferred-until-Save behavior, unchanged.
        var updated = _current with
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
