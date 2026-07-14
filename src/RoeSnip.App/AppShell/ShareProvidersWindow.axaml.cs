using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using RoeSnip.Core.Diagnostics;
using RoeSnip.Core.Settings;
using RoeSnip.Core.Sharing;

namespace RoeSnip.App.AppShell;

/// <summary>Avalonia port of the WPF app's ShareProvidersWindow (PARITY.md item 12) — master list
/// for the Sharing/* subsystem's settings UI (SettingsWindow's "Providers..." button): every
/// built-in provider (ShareProviderCatalog.BuiltIns), whether configured yet or not, plus every
/// "Custom..." one the user has added, each with a quick Enabled toggle and a "Configure" button
/// that opens ShareProviderEditWindow for the fields that actually vary per provider. Rows are
/// built in code (mirrors ToolbarControl's own code-built palette rows) since the list's length and
/// per-row content depend entirely on what's configured.
///
/// Self-persists: every change (Enabled toggle, Configure/Save, Custom Remove) writes straight to
/// SettingsStore the moment it happens — same "writes immediately" precedent this port's own
/// SettingsWindow already sets for run-at-startup — rather than staging changes for a Save button of
/// its own. SettingsWindow.ManageProvidersButton_Click reloads from disk after this window closes so
/// its own default-provider combo reflects whatever happened here.
///
/// Avalonia has no synchronous, input-blocking ShowDialog the way WPF does — Window.ShowDialog is a
/// Task, so the sub-window's close is awaited (OpenEditWindow) instead of the WPF version's
/// straight-line "ShowDialog(); RefreshList();".</summary>
public partial class ShareProvidersWindow : Window
{
    // Parameterless ctor for the XAML loader/previewer only.
    public ShareProvidersWindow() : this(RoeSnipSettings.Default) { }

    public ShareProvidersWindow(RoeSnipSettings currentSettings)
    {
        InitializeComponent();
        RefreshList(currentSettings);
    }

    /// <summary>Rebuilds the whole row list from a freshly LOADED settings snapshot (never the
    /// constructor-time one after the first call) so the list can never show a row as stale right
    /// after this window itself just persisted a change to it.</summary>
    private void RefreshList() => RefreshList(SettingsStore.Load());

    private void RefreshList(RoeSnipSettings settings)
    {
        ProvidersListPanel.Children.Clear();

        foreach (var config in ShareManager.EffectiveConfigs(settings.ShareProviders))
        {
            ProvidersListPanel.Children.Add(BuildRow(config));
        }
    }

    private Border BuildRow(ShareProviderConfig config)
    {
        ProviderSpec? spec = ShareProviderCatalog.ResolveSpec(config);

        var nameText = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(config.DisplayName) ? (spec?.Name ?? config.SpecId) : config.DisplayName,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var subtitleParts = new List<string>();
        if (spec is null)
        {
            subtitleParts.Add("unknown provider spec");
        }
        else
        {
            subtitleParts.Add(spec.UploadKind == ShareUploadKind.RawBody ? "raw upload" : "multipart upload");
            if (!spec.Verified)
            {
                subtitleParts.Add("untested");
            }
        }
        if (config.IsCustom)
        {
            subtitleParts.Add("custom");
        }

        var subtitleText = new TextBlock
        {
            Text = string.Join(" - ", subtitleParts),
            FontSize = 11,
            Foreground = MutedBrush,
        };
        if (spec is { Verified: false })
        {
            subtitleText.Foreground = WarnBrush;
        }

        var titleStack = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        titleStack.Children.Add(nameText);
        titleStack.Children.Add(subtitleText);

        var enabledCheckBox = new CheckBox
        {
            IsChecked = config.Enabled,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
        };
        ToolTip.SetTip(enabledCheckBox, "Offer this provider as an upload target");
        enabledCheckBox.IsCheckedChanged += (_, _) => SaveEnabledToggle(config, enabledCheckBox.IsChecked == true);

        var configureButton = new Button { Content = "Configure...", Margin = new Thickness(6, 0, 0, 0) };
        configureButton.Click += async (_, _) => await OpenEditWindow(config, isNew: false);

        var grid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        Grid.SetColumn(enabledCheckBox, 0);
        Grid.SetColumn(titleStack, 1);
        Grid.SetColumn(configureButton, 2);
        grid.Children.Add(enabledCheckBox);
        grid.Children.Add(titleStack);
        grid.Children.Add(configureButton);

        return new Border
        {
            Background = PanelBrush,
            BorderBrush = BorderBrush2,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 8, 8, 8),
            Margin = new Thickness(0, 0, 0, 6),
            Child = grid,
        };
    }

    // Near-black panel/border tokens matching this port's own SettingsWindow dark palette — kept as
    // plain static brushes (not a XAML resource dictionary) since only this file's code-built rows
    // use them.
    private static readonly IBrush PanelBrush = new SolidColorBrush(Color.FromRgb(0x18, 0x18, 0x1D));
    private static readonly IBrush BorderBrush2 = new SolidColorBrush(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF));
    private static readonly IBrush MutedBrush = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A));
    private static readonly IBrush WarnBrush = new SolidColorBrush(Color.FromRgb(0xDC, 0x6B, 0x2E));

    /// <summary>The row checkbox is a quick toggle only — it never changes credentials, so it's safe
    /// to persist immediately without routing through the full edit form.</summary>
    private void SaveEnabledToggle(ShareProviderConfig config, bool enabled) =>
        UpsertAndSave(config with { Enabled = enabled });

    private async System.Threading.Tasks.Task OpenEditWindow(ShareProviderConfig config, bool isNew)
    {
        var editWindow = new ShareProviderEditWindow(
            config,
            isNew,
            onSave: updated => UpsertAndSave(updated),
            onRemove: config.IsCustom ? id => RemoveAndSave(id) : null);
        await editWindow.ShowDialog(this);
        RefreshList();
    }

    private async void AddCustomButton_Click(object? sender, RoutedEventArgs e)
    {
        var blank = new ShareProviderConfig
        {
            Id = Guid.NewGuid().ToString("N"),
            IsCustom = true,
            DisplayName = "Custom provider",
            CustomSpec = new ProviderSpec
            {
                Id = "custom",
                Name = "Custom provider",
                Method = "POST",
                UploadKind = ShareUploadKind.Multipart,
                MultipartFieldName = "file",
                ResponseMode = ResponseUrlMode.JsonPath,
                IsBuiltIn = false,
                Verified = false,
            },
            Enabled = false,
        };
        await OpenEditWindow(blank, isNew: true);
    }

    private void UpsertAndSave(ShareProviderConfig config)
    {
        var settings = SettingsStore.Load();
        // Replace IN PLACE (not remove-then-append) — display and default-fallback order now come
        // from ShareProviderCatalog.EffectiveConfigs' fixed catalog ordering, not from this persisted
        // array's order, so that order is inert either way. Kept in-place simply to avoid rewriting
        // the array shape (and thus the settings.json diff) on every routine Enabled-toggle or
        // Configure/Save.
        var list = new List<ShareProviderConfig>(settings.ShareProviders);
        int existingIndex = list.FindIndex(c => string.Equals(c.Id, config.Id, StringComparison.Ordinal));
        if (existingIndex >= 0)
        {
            list[existingIndex] = config;
        }
        else
        {
            list.Add(config);
        }
        try
        {
            SettingsStore.Save(settings with { ShareProviders = list });
        }
        catch (Exception ex)
        {
            ShowSaveFailure($"Failed to save provider settings: {ex.Message}");
        }
        RefreshList();
    }

    private void RemoveAndSave(string configId)
    {
        var settings = SettingsStore.Load();
        var list = settings.ShareProviders.FindAll(c => !string.Equals(c.Id, configId, StringComparison.Ordinal));
        string? defaultId = string.Equals(settings.DefaultShareProviderId, configId, StringComparison.Ordinal)
            ? null
            : settings.DefaultShareProviderId;
        try
        {
            SettingsStore.Save(settings with { ShareProviders = list, DefaultShareProviderId = defaultId });
        }
        catch (Exception ex)
        {
            ShowSaveFailure($"Failed to remove the provider: {ex.Message}");
        }
        RefreshList();
    }

    /// <summary>Avalonia has no MessageBox — a save/remove failure here is rare (disk full, settings
    /// file locked) and non-interactive-blocking, so it's surfaced the same way SettingsWindow's own
    /// ShowValidationError does: stderr, which the resident's console/log already captures. Unlike
    /// SettingsWindow this window has no persistent inline error TextBlock of its own (rows rebuild
    /// on every save), so stderr is the whole of it — the row list itself still reflects reality via
    /// the RefreshList() that follows every call site.</summary>
    private static void ShowSaveFailure(string message) => FileLog.Write($"RoeSnip: {message}");

    private void CloseButton_Click(object? sender, RoutedEventArgs e) => Close();
}
