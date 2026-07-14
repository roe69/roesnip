using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using RoeSnip.Core.Settings;
using RoeSnip.Core.Updates;

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

    // Item 14 / WPF Bugs 2 & 5 (SettingsWindow.xaml.cs:34-43): the global hotkey and this
    // window's own key capture compete for the same keystroke - on Windows RegisterHotKey claims
    // a registered combo before it ever reaches a focused window's key events at all, and even
    // this port's weaker SharpHook global hook still means "the OS action for that key already
    // fired" for a bare PrintScreen (Snipping Tool intercept). TrayApp supplies these so the
    // global hotkey can be suspended for the (brief) duration of an actual capture - fixing
    // rebinding PrintScreen back onto itself - while staying registered/armed the rest of the
    // time this window is open, so the hotkey still fires a normal snip with Settings open.
    private readonly Action _suspendGlobalHotkey;
    private readonly Action _resumeGlobalHotkey;

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

    // Item 15: the "Restart elevated now" flow needs to exit this WHOLE app (releasing the
    // single-instance lock) so the elevated task can take over — mirrors the WPF app's own
    // RestartElevatedNow exiting its WinForms message loop. TrayApp supplies this the same way it
    // supplies suspendGlobalHotkey/resumeGlobalHotkey above.
    private readonly Action _exitApplication;

    // Guards RunElevatedCheckBox.IsChecked assignments that must NOT re-enter
    // RunElevatedCheckBox_Checked/Unchecked (initial load, and reconciling the checkbox back to the
    // real task state after a toggle). Mirrors the WPF app's own _loadingElevationState.
    private bool _loadingElevationState;

    // Parameterless ctor for the XAML loader/previewer only.
    public SettingsWindow() : this(RoeSnipSettings.Default, hotkeyUnavailableOnWayland: false, _ => { }, () => { }, () => { }, () => { }) { }

    public SettingsWindow(
        RoeSnipSettings settings,
        bool hotkeyUnavailableOnWayland,
        Action<RoeSnipSettings> onSaved,
        Action suspendGlobalHotkey,
        Action resumeGlobalHotkey,
        Action exitApplication)
    {
        InitializeComponent();

        _original = settings;
        _current = settings;
        _hotkeyUnavailableOnWayland = hotkeyUnavailableOnWayland;
        _onSaved = onSaved;
        _suspendGlobalHotkey = suspendGlobalHotkey;
        _resumeGlobalHotkey = resumeGlobalHotkey;
        _exitApplication = exitApplication;
        _pendingModifiers = settings.HotkeyModifiers;
        _pendingVirtualKey = settings.HotkeyVirtualKey;

        LoadFromSettings();

        // Bug 5 safety net: however this window closes (Save, Cancel, titlebar X, or mid-capture),
        // make sure the global hotkey ends up (re)armed. Save's onSaved callback already
        // re-registers with the newly-saved settings before Close() runs, so by the time this
        // fires _resumeGlobalHotkey is a harmless, idempotent re-arm of the same (already correct)
        // hotkey; on Cancel/X mid-capture it is what restores the hotkey ChangeHotkeyButton_Click
        // suspended.
        Closed += (_, _) => _resumeGlobalHotkey();
    }

    private void LoadFromSettings()
    {
        HotkeyDisplay.Text = HotkeyDisplayFormat.DescribeHotkey(_pendingModifiers, _pendingVirtualKey);
        WaylandHotkeyCaption.IsVisible = _hotkeyUnavailableOnWayland;
        SaveDirectoryBox.Text = _original.SaveDirectory;
        AutoSaveHdrCheckBox.IsChecked = _original.AutoSaveHdrCopy;
        CopyOnSelectCheckBox.IsChecked = _original.CopyOnSelect;
        ColorPickerCheckBox.IsChecked = _original.ColorPickerEnabled;
        KneeOverrideBox.Text = _original.ToneMapKneeOverride?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        PeakOverrideBox.Text = _original.ToneMapPeakOverride?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

        // Item 15: the elevated-startup section is Windows-only (Scheduled Tasks / UIPI have no
        // portable equivalent) - HIDE it entirely on Linux/macOS rather than showing a
        // permanently-disabled control with no explanation (DESIGN-XPLAT.md's degrade-gracefully
        // rule; accepted limitation, docs/PARITY.md).
        bool elevatedSectionSupported = OperatingSystem.IsWindows();
        ElevatedStartupSection.IsVisible = elevatedSectionSupported;

        bool elevatedTaskInstalled = elevatedSectionSupported && ElevationManager.IsElevatedTaskInstalled();

        try
        {
            // Bug 6 (WPF SettingsWindow.xaml.cs:104-109): "Start with Windows" represents "starts at
            // login by ANY mechanism", not just the Run key. When the elevated task is installed its
            // own onlogon trigger is what starts RoeSnip at login (the Run key is deliberately kept
            // cleared - see StartupManager's doc comment), so reading the Run key alone would falsely
            // uncheck this box on every reopen while the task owned startup.
            RunAtStartupCheckBox.IsChecked = StartupManager.IsRunAtStartupEnabled() || elevatedTaskInstalled;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"RoeSnip: could not read run-at-startup state: {ex.Message}");
            RunAtStartupCheckBox.IsChecked = _original.RunAtStartup || elevatedTaskInstalled;
        }

        if (elevatedSectionSupported)
        {
            ApplyRunAtStartupLockState(elevatedTaskInstalled);

            _loadingElevationState = true;
            try
            {
                RunElevatedCheckBox.IsChecked = elevatedTaskInstalled;
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

        LoadUpdateFrequency();

        RefreshShareProvidersUi();
    }

    // ---------- Periodic update check frequency ----------

    /// <summary>Populates the frequency combo from <see cref="UpdateCheckFrequencies.UiChoices"/>
    /// (pattern: DefaultShareProviderCombo above) and selects the item matching the PARSED (not
    /// raw) current setting: running the same fail-safe Parse the loop itself uses means garbage in
    /// settings.json, or the hidden EveryMinute dev value (deliberately excluded from UiChoices -
    /// see that field's own doc comment), both land on Hourly here rather than leaving nothing
    /// selected. Saving from this window then rewrites EveryMinute to Hourly - acceptable, since
    /// EveryMinute is never reachable through the UI in the first place. Deferred-save like every
    /// other field below Sharing: no SelectionChanged handler, read back in SaveButton_Click.
    ///
    /// The no-match fallback below selects the Hourly item explicitly - NOT UiChoices[0]
    /// (StartupOnly). UiChoices[0] would silently turn Save into "disable periodic checking" for
    /// exactly the EveryMinute/undefined-value cases this method's own doc comment above says land
    /// on Hourly.</summary>
    private void LoadUpdateFrequency()
    {
        UpdateFrequencyCombo.Items.Clear();
        UpdateCheckFrequency current = UpdateCheckFrequencies.Parse(_original.UpdateCheckFrequency);
        ComboBoxItem? selected = null;
        ComboBoxItem? hourly = null;
        foreach (UpdateCheckFrequency choice in UpdateCheckFrequencies.UiChoices)
        {
            var item = new ComboBoxItem { Content = UpdateCheckFrequencies.DisplayLabel(choice), Tag = choice.ToString() };
            UpdateFrequencyCombo.Items.Add(item);
            if (choice == current)
            {
                selected = item;
            }
            if (choice == UpdateCheckFrequency.Hourly)
            {
                hourly = item;
            }
        }
        UpdateFrequencyCombo.SelectedItem = selected ?? hourly ?? UpdateFrequencyCombo.Items[0];

        // Windows portable/dev copies never run the update loop at all (TrayApp.Start's
        // IsInstalled guard); Linux/macOS run the passive-notice loop unconditionally but never
        // self-apply anything. Either way the combo still saves a value (for whenever a portable
        // copy might get installed later), but a hint makes clear what today's behavior actually
        // is instead of leaving the user to wonder why "updates don't work".
        bool installed;
        try
        {
            installed = UpdateManager.IsInstalled;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"RoeSnip: could not read install state for the update-frequency hint: {ex.Message}");
            installed = true; // fail toward showing no hint rather than a possibly-wrong one
        }

        string? hint = OperatingSystem.IsWindows()
            ? (installed
                ? null
                : "This portable copy never checks for updates. Use Install RoeSnip in the tray menu to turn updates on.")
            : "On Linux and macOS, RoeSnip only notifies you about new versions. Downloads stay manual.";

        UpdateFrequencyHintText.Text = hint ?? string.Empty;
        UpdateFrequencyHintText.IsVisible = hint is not null;
    }

    // ---------- Elevated startup (item 15) ----------

    /// <summary>Bug 6 UX (WPF SettingsWindow.xaml.cs:234-249): while the elevated task is installed,
    /// "Start with Windows" is CHECKED and DISABLED (you cannot have admin-startup without
    /// login-startup - the task's onlogon trigger implies it), with a hint explaining why instead of
    /// a mysteriously locked box.</summary>
    private void ApplyRunAtStartupLockState(bool elevatedTaskInstalled)
    {
        RunAtStartupCheckBox.IsEnabled = !elevatedTaskInstalled;
        if (elevatedTaskInstalled)
        {
            RunAtStartupCheckBox.IsChecked = true;
        }

        RunAtStartupHintText.Text = elevatedTaskInstalled
            ? "Implied by \"Run as administrator\" below - its scheduled task starts RoeSnip at login."
            : string.Empty;
        RunAtStartupHintText.IsVisible = elevatedTaskInstalled;
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
        RestartElevatedButton.IsEnabled = !elevatedNow && ElevationManager.IsElevatedTaskInstalled();
    }

    // Avalonia's CheckBox (Avalonia.Controls.Primitives.ToggleButton) has no separate Checked/
    // Unchecked routed events the way WPF's does — one IsCheckedChanged event covers both, so the
    // WPF app's two handlers (RunElevatedCheckBox_Checked/_Unchecked) collapse into this one.
    private void RunElevatedCheckBox_IsCheckedChanged(object? sender, RoutedEventArgs e) =>
        ToggleElevatedStartup(enable: RunElevatedCheckBox.IsChecked == true);

    /// <summary>Drives the "Run as administrator" checkbox (WPF SettingsWindow.xaml.cs:358-422). If
    /// this process is already elevated, the task is created/deleted in-process - no UAC needed.
    /// Otherwise a second RoeSnip is launched with Verb=runas and the matching hidden
    /// --enable/--disable-elevated-startup verb (ONE UAC prompt), and this window waits for it to
    /// finish before re-querying the real state and reflecting it back onto the checkbox - so the
    /// checkbox always ends up showing what actually happened, not what the user clicked (it reverts
    /// on a cancelled UAC prompt, a failed schtasks call, etc).</summary>
    private async void ToggleElevatedStartup(bool enable)
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
            await RunElevatedViaUacAsync(enable);
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

        ApplyRunAtStartupLockState(nowInstalled);
        RefreshElevationStatusText();

        if (enable && nowInstalled && !wasInstalled)
        {
            bool? restart = await TrayApp.ShowYesNoDialogAsync(
                "RoeSnip", "Elevated startup is enabled. Restart RoeSnip as administrator now?");
            if (restart == true)
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
        if (!OperatingSystem.IsWindows())
        {
            return; // defensive: ElevatedStartupSection is hidden on non-Windows, so this is unreachable via the UI
        }

        try
        {
            if (enable)
            {
                string exePath = ElevationManager.ResolveTargetExePath(
                    UpdateManager.InstallExists,
                    UpdateManager.InstalledExePath,
                    Environment.ProcessPath ?? System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName);
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
            ShowValidationError($"Failed to update elevated startup: {ex.Message}");
        }
    }

    /// <summary>Non-elevated path: relaunch RoeSnip elevated (one UAC prompt) with the matching
    /// hidden verb, wait for it, and surface any failure. A cancelled UAC prompt raises
    /// Win32Exception 1223 - that's treated as a silent no-op (the checkbox reverts via the re-query
    /// in the caller), not an error message, since the user made an explicit choice.</summary>
    private async Task RunElevatedViaUacAsync(bool enable)
    {
        try
        {
            string exePath = ElevationManager.ResolveTargetExePath(
                UpdateManager.InstallExists,
                UpdateManager.InstalledExePath,
                Environment.ProcessPath ?? System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName);

            // Drain any stale error left by a PREVIOUS attempt before launching, so a failure this
            // time can't be misattributed to (or masked by) leftover content from an earlier session.
            _ = ElevationManager.ConsumeLastError();

            var psi = new System.Diagnostics.ProcessStartInfo(exePath)
            {
                UseShellExecute = true,
                Verb = "runas",
            };
            psi.ArgumentList.Add(enable ? "--enable-elevated-startup" : "--disable-elevated-startup");

            using var process = System.Diagnostics.Process.Start(psi);
            if (process is not null)
            {
                await process.WaitForExitAsync();
            }

            if (process is null || process.ExitCode != 0)
            {
                // UseShellExecute=true (required for the runas verb) cannot redirect the elevated
                // child's stdout/stderr, and this is a console-less WinExe, so the real schtasks
                // failure would otherwise be completely invisible - the checkbox would just silently
                // revert. The elevated verb (ElevationManager.RunEnableElevatedStartupCli /
                // RunDisableElevatedStartupCli) relays its failure reason through a temp file for
                // exactly this case.
                string? detail = ElevationManager.ConsumeLastError();
                string baseMessage = enable
                    ? "Failed to enable elevated startup."
                    : "Failed to disable elevated startup.";
                ShowValidationError(detail is null ? baseMessage : $"{baseMessage} {detail}");
            }
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // User clicked "No" on the UAC prompt - nothing changed; the checkbox reverts silently.
        }
        catch (Exception ex)
        {
            ShowValidationError($"Failed to update elevated startup: {ex.Message}");
        }
    }

    private void RestartElevatedButton_Click(object? sender, RoutedEventArgs e) => RestartElevatedNow();

    /// <summary>Restarts RoeSnip elevated after enabling the task (WPF SettingsWindow.xaml.cs:
    /// 511-561): spawns a detached helper that waits a beat then runs the task, so the mutex/pipe
    /// used for single-instance enforcement is free by the time the elevated instance starts (this
    /// window's process is exiting right after, not waiting around to hand off the mutex
    /// itself).</summary>
    private void RestartElevatedNow()
    {
        if (!ElevationManager.IsElevatedTaskInstalled())
        {
            ShowValidationError("Cannot restart elevated: the scheduled task is not installed.");
            return;
        }

        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                // Same two fixes as the WPF app's own RestartElevatedNow, ported verbatim:
                // (1) a RAW Arguments string, not ArgumentList - a single ArgumentList entry holding
                //     this compound command (it contains & and >) gets wrapped in quotes, which then
                //     breaks cmd.exe's own quote handling for the chained command after "&".
                // (2) "ping -n 2 127.0.0.1 >nul" for the ~1s settle delay (lets this exiting instance
                //     release its single-instance mutex/pipe before the elevated one starts), not
                //     "timeout" - timeout reads the console and aborts under CreateNoWindow (no
                //     console), short-circuiting "&&". "&" runs schtasks regardless of ping's exit code.
                Arguments = $"/c ping -n 2 127.0.0.1 >nul & schtasks /run /tn \"{ElevationManager.TaskName}\"",
            };
            System.Diagnostics.Process.Start(psi);
        }
        catch (Exception ex)
        {
            ShowValidationError($"Failed to restart RoeSnip elevated: {ex.Message}");
            return;
        }

        _exitApplication();
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

        // Bug 2: suspend the global hotkey for the duration of capture so the OS/hook delivers
        // the keystroke to THIS window instead of acting on it as the still-armed hotkey - see
        // this window's own field-level doc comment on _suspendGlobalHotkey. Restored the moment
        // a key is committed (CommitCapturedKey) or this window closes without one ever being
        // committed (the Closed handler in the constructor).
        _suspendGlobalHotkey();

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

        CommitCapturedKey(key, e.KeyModifiers);
    }

    /// <summary>Bug 2 (WPF SettingsWindow.xaml.cs:305-326): PrintScreen (VK_SNAPSHOT) is a
    /// long-standing Windows quirk - it generates only a key-up, never a key-down, to whichever
    /// window has focus - so it can only ever be captured here, not in OnKeyDown above. Every
    /// other key is already handled on key-down and just passes through normally here.</summary>
    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (!_capturingHotkey || e.Key != Key.Snapshot)
        {
            base.OnKeyUp(e);
            return;
        }

        e.Handled = true;
        CommitCapturedKey(Key.Snapshot, e.KeyModifiers);
    }

    private void CommitCapturedKey(Key key, KeyModifiers keyModifiers)
    {
        uint? virtualKey = MapKeyToVirtualKey(key);
        if (virtualKey is null)
        {
            HotkeyDisplay.Text = "Unsupported key — press another combination...";
            return;
        }

        uint modifiers = 0;
        if (keyModifiers.HasFlag(KeyModifiers.Control)) modifiers |= HotkeyManager.ModControl;
        if (keyModifiers.HasFlag(KeyModifiers.Alt)) modifiers |= HotkeyManager.ModAlt;
        if (keyModifiers.HasFlag(KeyModifiers.Shift)) modifiers |= HotkeyManager.ModShift;
        if (keyModifiers.HasFlag(KeyModifiers.Meta)) modifiers |= HotkeyManager.ModWin;

        _pendingModifiers = modifiers;
        _pendingVirtualKey = virtualKey.Value;
        _capturingHotkey = false;
        HotkeyDisplay.Text = HotkeyDisplayFormat.DescribeHotkey(_pendingModifiers, _pendingVirtualKey);

        // Bug 5: capture is over - resume the global hotkey (still the OLD/saved binding; the new
        // one only takes effect on Save) so it keeps firing while this window stays open deciding
        // whether to Save or Cancel the new binding.
        _resumeGlobalHotkey();
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
            ColorPickerEnabled = ColorPickerCheckBox.IsChecked == true,
            ToneMapKneeOverride = kneeOverride,
            ToneMapPeakOverride = peakOverride,
            RunAtStartup = runAtStartup,
            UpdateCheckFrequency = (UpdateFrequencyCombo.SelectedItem as ComboBoxItem)?.Tag as string
                ?? _current.UpdateCheckFrequency,
        };

        try
        {
            // Item 15 (WPF SettingsWindow.xaml.cs:618-624): when the elevated task is installed, its
            // own onlogon trigger owns startup (see StartupManager's doc comment) - keep the Run key
            // clear rather than writing the checkbox's raw value, regardless of what
            // RunAtStartupCheckBox shows. IsElevatedTaskInstalled() is always false when the section
            // is hidden (non-Windows), so this collapses to the plain runAtStartup write there.
            bool elevatedTaskInstalled = ElevationManager.IsElevatedTaskInstalled();
            StartupManager.SetRunAtStartup(elevatedTaskInstalled ? false : runAtStartup);
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

}
