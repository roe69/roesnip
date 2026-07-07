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

    // Guards RunElevatedCheckBox.IsChecked assignments that must NOT re-enter
    // RunElevatedCheckBox_Checked/Unchecked (initial load, and reconciling the checkbox back to
    // the real task state after a toggle).
    private bool _loadingElevationState;

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

        _loadingElevationState = true;
        try
        {
            RunElevatedCheckBox.IsChecked = ElevationManager.IsElevatedTaskInstalled();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"RoeSnip: could not read elevated-startup task state: {ex.Message}");
            RunElevatedCheckBox.IsChecked = false;
        }
        finally
        {
            _loadingElevationState = false;
        }

        RefreshElevationStatusText();
    }

    private void RefreshElevationStatusText()
    {
        bool elevatedNow;
        try
        {
            elevatedNow = ElevationManager.IsProcessElevated();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"RoeSnip: could not read process elevation state: {ex.Message}");
            elevatedNow = false;
        }

        ElevationStatusText.Text = $"Currently running as administrator: {(elevatedNow ? "yes" : "no")}";
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

    private void RunElevatedCheckBox_Checked(object sender, RoutedEventArgs e) => ToggleElevatedStartup(enable: true);

    private void RunElevatedCheckBox_Unchecked(object sender, RoutedEventArgs e) => ToggleElevatedStartup(enable: false);

    /// <summary>Drives the "Run as administrator" checkbox. If this process is already elevated, the
    /// task is created/deleted in-process — no UAC needed. Otherwise a second RoeSnip.exe is
    /// launched with Verb=runas and the matching hidden --enable/--disable-elevated-startup verb
    /// (ONE UAC prompt), and this window waits for it to finish before re-querying the real state and
    /// reflecting it back onto the checkbox — so the checkbox always ends up showing what actually
    /// happened, not what the user clicked (it reverts on a cancelled UAC prompt, a failed schtasks
    /// call, etc).</summary>
    private void ToggleElevatedStartup(bool enable)
    {
        if (_loadingElevationState)
        {
            return;
        }

        bool wasInstalled;
        try
        {
            wasInstalled = ElevationManager.IsElevatedTaskInstalled();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"RoeSnip: could not read elevated-startup task state: {ex.Message}");
            wasInstalled = !enable; // assume the toggle is meaningful; proceed
        }

        if (enable == wasInstalled)
        {
            return; // already in the desired state (e.g. the reconciliation below re-set IsChecked)
        }

        if (ElevationManager.IsProcessElevated())
        {
            ApplyElevatedStartupChange(enable);
        }
        else
        {
            RunElevatedViaUac(enable);
        }

        bool nowInstalled;
        try
        {
            nowInstalled = ElevationManager.IsElevatedTaskInstalled();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"RoeSnip: could not re-read elevated-startup task state: {ex.Message}");
            nowInstalled = wasInstalled;
        }

        if (nowInstalled != (RunElevatedCheckBox.IsChecked == true))
        {
            _loadingElevationState = true;
            RunElevatedCheckBox.IsChecked = nowInstalled;
            _loadingElevationState = false;
        }

        RefreshElevationStatusText();

        if (enable && nowInstalled && !wasInstalled)
        {
            var restart = System.Windows.MessageBox.Show(
                this,
                "Elevated startup is enabled. Restart RoeSnip as administrator now?",
                "RoeSnip", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (restart == MessageBoxResult.Yes)
            {
                RestartElevatedNow();
            }
        }
    }

    /// <summary>In-process path (this window's process is already elevated): create/delete the task
    /// directly, then apply the same HKCU-Run-key interplay the CLI verbs apply (see
    /// StartupManager's doc comment) using the live checkbox state, since settings.json may not yet
    /// reflect an unsaved change to RunAtStartupCheckBox in this dialog session.</summary>
    private void ApplyElevatedStartupChange(bool enable)
    {
        try
        {
            if (enable)
            {
                string exePath = Environment.ProcessPath
                    ?? System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName
                    ?? throw new InvalidOperationException("Could not determine the current executable path.");
                ElevationManager.EnableElevatedStartup(exePath);
                StartupManager.SetRunAtStartup(false);
            }
            else
            {
                ElevationManager.DisableElevatedStartup();
                StartupManager.SetRunAtStartup(RunAtStartupCheckBox.IsChecked == true);
            }
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                this, $"Failed to update elevated startup: {ex.Message}",
                "RoeSnip", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>Non-elevated path: relaunch RoeSnip.exe elevated (one UAC prompt) with the matching
    /// hidden verb, wait for it, and surface any failure. A cancelled UAC prompt raises
    /// Win32Exception 1223 — that's treated as a silent no-op (the checkbox reverts via the
    /// re-query in the caller), not an error dialog, since the user made an explicit choice.</summary>
    private void RunElevatedViaUac(bool enable)
    {
        try
        {
            string exePath = Environment.ProcessPath
                ?? System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName
                ?? throw new InvalidOperationException("Could not determine the current executable path.");

            var psi = new System.Diagnostics.ProcessStartInfo(exePath)
            {
                UseShellExecute = true,
                Verb = "runas",
            };
            psi.ArgumentList.Add(enable ? "--enable-elevated-startup" : "--disable-elevated-startup");

            using var process = System.Diagnostics.Process.Start(psi);
            process?.WaitForExit();

            if (process is null || process.ExitCode != 0)
            {
                System.Windows.MessageBox.Show(
                    this,
                    enable
                        ? "Failed to enable elevated startup."
                        : "Failed to disable elevated startup.",
                    "RoeSnip", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // User clicked "No" on the UAC prompt — nothing changed; the checkbox reverts silently.
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                this, $"Failed to update elevated startup: {ex.Message}",
                "RoeSnip", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>Restarts RoeSnip elevated after enabling the task: spawns a detached helper that
    /// waits a beat then runs the task, so the mutex/pipe used for single-instance enforcement is
    /// free by the time the elevated instance starts (this window's process is exiting right after,
    /// not waiting around to hand off the mutex itself).</summary>
    private void RestartElevatedNow()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add($"timeout /t 1 /nobreak >nul && schtasks /run /tn \"{ElevationManager.TaskName}\"");
            System.Diagnostics.Process.Start(psi);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                this, $"Failed to restart RoeSnip elevated: {ex.Message}",
                "RoeSnip", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // Exits the WinForms message loop TrayApp.RunInstance() is running (this app's UI thread is
        // WinForms, not WPF — there is no App.xaml/System.Windows.Application instance to shut down).
        // The new elevated instance takes the single-instance mutex once this process has fully exited.
        System.Windows.Forms.Application.Exit();
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
            // When the elevated task is installed, its own onlogon trigger owns startup (see
            // StartupManager's doc comment) — keep the Run key clear rather than writing the
            // checkbox's raw value, regardless of what RunAtStartupCheckBox shows.
            bool elevatedTaskInstalled = ElevationManager.IsElevatedTaskInstalled();
            StartupManager.SetRunAtStartup(elevatedTaskInstalled ? false : runAtStartup);
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
