using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using RoeSnip.Core.Sharing;

namespace RoeSnip.App;

// RoeSnip.csproj enables both UseWPF and UseWindowsForms, so several WPF/WinForms type names
// collide by simple name - same disambiguation convention Overlay/ToolbarControl.xaml.cs already
// uses for "Color"/"UserControl".
using Brush = System.Windows.Media.Brush;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using MessageBox = System.Windows.MessageBox;

/// <summary>Master list for the Sharing/* subsystem's settings UI (SettingsWindow's "Providers..."
/// button): every built-in provider (ShareProviderCatalog.BuiltIns), whether configured yet or not,
/// plus every "Custom..." one the user has added, each with a quick Enabled toggle and a "Configure"
/// button that opens ShareProviderEditWindow for the fields that actually vary per provider. Rows are
/// built in code (mirrors ToolbarControl.SetPaletteColors' own code-built-rows pattern) since the
/// list's length and per-row content depend entirely on what's configured.
///
/// Self-persists: every change (Enabled toggle, Configure/Save, Custom Remove) writes straight to
/// SettingsStore the moment it happens - same "writes immediately" precedent SettingsWindow's own
/// elevated-startup checkbox already sets (ToggleElevatedStartup) - rather than staging changes for a
/// Save button of its own. SettingsWindow.ManageProvidersButton_Click reloads from disk after this
/// window closes so its own default-provider combo reflects whatever happened here.</summary>
public partial class ShareProvidersWindow : Window
{
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
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var subtitleParts = new System.Collections.Generic.List<string>();
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
            Foreground = (Brush)Resources["MutedBrush"],
        };
        if (spec is { Verified: false })
        {
            subtitleText.Foreground = (Brush)Resources["WarnBrush"];
        }

        var titleStack = new StackPanel();
        titleStack.Children.Add(nameText);
        titleStack.Children.Add(subtitleText);

        var enabledCheckBox = new CheckBox
        {
            IsChecked = config.Enabled,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
            ToolTip = "Offer this provider as an upload target",
        };
        enabledCheckBox.Checked += (_, _) => SaveEnabledToggle(config, true);
        enabledCheckBox.Unchecked += (_, _) => SaveEnabledToggle(config, false);

        var configureButton = new Button { Content = "Configure...", Margin = new Thickness(6, 0, 0, 0) };
        configureButton.Click += (_, _) => OpenEditWindow(config, isNew: false);

        var grid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(enabledCheckBox, 0);
        Grid.SetColumn(titleStack, 1);
        Grid.SetColumn(configureButton, 2);
        grid.Children.Add(enabledCheckBox);
        grid.Children.Add(titleStack);
        grid.Children.Add(configureButton);

        return new Border
        {
            Background = (Brush)Resources["PanelBrush"],
            BorderBrush = (Brush)Resources["BorderBrush2"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 8, 8, 8),
            Margin = new Thickness(0, 0, 0, 6),
            Child = grid,
        };
    }

    /// <summary>The row checkbox is a quick toggle only - it never changes credentials, so it's safe
    /// to persist immediately without routing through the full edit form.</summary>
    private void SaveEnabledToggle(ShareProviderConfig config, bool enabled)
    {
        UpsertAndSave(config with { Enabled = enabled });
    }

    private void OpenEditWindow(ShareProviderConfig config, bool isNew)
    {
        var editWindow = new ShareProviderEditWindow(
            config,
            isNew,
            onSave: updated => UpsertAndSave(updated),
            onRemove: config.IsCustom ? (id => RemoveAndSave(id)) : null)
        {
            Owner = this,
        };
        editWindow.ShowDialog();
        RefreshList();
    }

    private void AddCustomButton_Click(object sender, RoutedEventArgs e)
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
        OpenEditWindow(blank, isNew: true);
    }

    private void UpsertAndSave(ShareProviderConfig config)
    {
        var settings = SettingsStore.Load();
        // Replace IN PLACE (not remove-then-append) - display and default-fallback order now come
        // from ShareProviderCatalog.EffectiveConfigs' fixed catalog ordering, not from this persisted
        // array's order, so that order is inert either way. Kept in-place simply to avoid rewriting
        // the array shape (and thus the settings.json diff) on every routine Enabled-toggle or
        // Configure/Save.
        var list = settings.ShareProviders.ToList();
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
            MessageBox.Show(this, $"Failed to save provider settings: {ex.Message}", "RoeSnip", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        RefreshList();
    }

    private void RemoveAndSave(string configId)
    {
        var settings = SettingsStore.Load();
        var list = settings.ShareProviders.Where(c => !string.Equals(c.Id, configId, StringComparison.Ordinal)).ToList();
        string? defaultId = string.Equals(settings.DefaultShareProviderId, configId, StringComparison.Ordinal)
            ? null
            : settings.DefaultShareProviderId;
        try
        {
            SettingsStore.Save(settings with { ShareProviders = list, DefaultShareProviderId = defaultId });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Failed to remove the provider: {ex.Message}", "RoeSnip", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        RefreshList();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
