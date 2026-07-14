using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using RoeSnip.Core.Settings;
using RoeSnip.Core.Sharing;

namespace RoeSnip.App.AppShell;

/// <summary>Avalonia port of the WPF app's ShareProviderEditWindow (PARITY.md item 12) — the
/// per-provider detail form: for a built-in provider, just its ProviderSpec.ConfigFields (e.g.
/// RoeShare's Server URL + API key) plus Enabled/DisplayName — the request shape itself (which IS
/// the built-in spec) is not editable here, only looked up fresh from ShareProviderCatalog every
/// time (see ProviderSpec's own doc comment for why). For a "Custom..." provider, the WHOLE
/// ProviderSpec is editable (endpoint, headers, response extraction, ...), because there is no
/// catalog entry to fall back on.
///
/// The Test button exercises the REAL pipeline (ShareManager.UploadAsync -> ProviderSpecShareProvider
/// -> the actual HTTP endpoint) against a tiny generated PNG (RoeSnip.Core.Sharing.ShareTestImage),
/// using whatever is currently typed into this form — not yet-saved state — so a user can validate a
/// spec before committing it.
///
/// Avalonia has no PasswordBox (or MessageBox): a secret ConfigField uses a plain TextBox with
/// PasswordChar set instead, and the WPF version's two MessageBox uses (the endpoint-required Save
/// guard, the Remove confirmation) became an inline ValidationErrorText and a small owned Yes/No
/// dialog respectively.</summary>
public partial class ShareProviderEditWindow : Window
{
    private readonly ShareProviderConfig _originalConfig;
    private readonly ProviderSpec? _builtInSpec; // null when editing/adding a Custom provider
    private readonly bool _isNew;
    private readonly Action<ShareProviderConfig> _onSave;
    private readonly Action<string>? _onRemove;

    // Built-in mode: one control per ProviderSpec.ConfigFields entry, keyed by ShareConfigField.Key -
    // a TextBox (PasswordChar set per ShareConfigField.IsSecret), or a ComboBox when the field
    // declares Options.
    private readonly Dictionary<string, Control> _builtInFieldControls = new();

    private CancellationTokenSource? _testCts;

    // Parameterless ctor for the XAML loader/previewer only (AVLN3001) — RoeSnip's own code always
    // uses the data-taking overload below; a window created this way is inert and must not be shown.
    public ShareProviderEditWindow()
        : this(new ShareProviderConfig(), isNew: true, onSave: _ => { }, onRemove: null)
    {
    }

    public ShareProviderEditWindow(
        ShareProviderConfig config, bool isNew, Action<ShareProviderConfig> onSave, Action<string>? onRemove)
    {
        InitializeComponent();

        _originalConfig = config;
        _isNew = isNew;
        _onSave = onSave;
        _onRemove = onRemove;
        _builtInSpec = config.IsCustom ? null : ShareProviderCatalog.ResolveSpec(config);

        DisplayNameBox.Text = config.DisplayName;
        EnabledCheckBox.IsChecked = config.Enabled;

        if (config.IsCustom)
        {
            HeaderText.Text = isNew ? "Add custom provider" : "Edit custom provider";
            CustomSpecPanel.IsVisible = true;
            LoadCustomSpecForm(config.CustomSpec ?? new ProviderSpec { IsBuiltIn = false });
            RemoveButton.IsVisible = !isNew;
        }
        else if (_builtInSpec is { } spec)
        {
            HeaderText.Text = $"Configure {spec.Name}";
            NotesText.Text = spec.Notes ?? "";
            NotesText.IsVisible = !string.IsNullOrEmpty(spec.Notes);
            if (!spec.Verified)
            {
                UntestedBadgeText.Text = "UNTESTED: this provider's spec could not be fully confirmed against current public docs. Double-check with Test below before relying on it.";
                UntestedBadgeText.IsVisible = true;
            }
            BuildBuiltInFields(spec, config.Values);
        }
        else
        {
            HeaderText.Text = "Unknown provider";
            NotesText.Text = "This provider's spec id is not recognized (it may have been removed in a newer RoeSnip build). You can only remove or disable it here.";
            NotesText.IsVisible = true;
            TestButton.IsEnabled = false;
        }

        Closed += (_, _) => _testCts?.Cancel();
    }

    // ---------- Built-in provider fields ----------

    private void BuildBuiltInFields(ProviderSpec spec, IReadOnlyDictionary<string, string> values)
    {
        BuiltInFieldsPanel.Children.Clear();
        _builtInFieldControls.Clear();

        foreach (var field in spec.ConfigFields)
        {
            BuiltInFieldsPanel.Children.Add(new TextBlock { Text = field.Label, Foreground = MutedBrush, Margin = new Thickness(0, 6, 0, 2) });

            values.TryGetValue(field.Key, out string? existingValue);

            Control control;
            if (field.Options is { Count: > 0 } options)
            {
                control = BuildOptionsComboBox(options, existingValue ?? field.DefaultValue);
            }
            else
            {
                var textBox = new TextBox { Text = existingValue ?? "" };
                if (field.IsSecret)
                {
                    textBox.PasswordChar = '●';
                }
                control = textBox;
            }

            BuiltInFieldsPanel.Children.Add(control);
            _builtInFieldControls[field.Key] = control;
        }

        if (spec.ConfigFields.Count == 0)
        {
            BuiltInFieldsPanel.Children.Add(new TextBlock
            {
                Text = "This provider needs no credentials.",
                Foreground = MutedBrush,
                FontSize = 11,
                Margin = new Thickness(0, 6, 0, 0),
            });
        }
    }

    /// <summary>Builds the ComboBox for a <see cref="ShareConfigField"/> that declares
    /// <see cref="ShareConfigField.Options"/>. Selects the option matching <paramref name="value"/>
    /// (already resolved to the field's DefaultValue by the caller when nothing is persisted yet); if
    /// <paramref name="value"/> doesn't match any declared option (a value from a future RoeSnip build,
    /// or a hand-edited settings.json), that raw value is preserved verbatim as its own selectable
    /// entry rather than silently discarded or rewritten to some other choice.</summary>
    private static ComboBox BuildOptionsComboBox(IReadOnlyList<ShareConfigOption> options, string? value)
    {
        var items = new List<ShareConfigOption>(options);
        if (!string.IsNullOrEmpty(value) && !items.Any(o => o.Value == value))
        {
            items.Insert(0, new ShareConfigOption(value, value));
        }

        return new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = items,
            DisplayMemberBinding = new Avalonia.Data.Binding(nameof(ShareConfigOption.Label)),
            SelectedValueBinding = new Avalonia.Data.Binding(nameof(ShareConfigOption.Value)),
            SelectedValue = value,
        };
    }

    private static string ReadControlValue(Control control) => control switch
    {
        ComboBox cb => cb.SelectedValue as string ?? "",
        TextBox tb => tb.Text ?? "",
        _ => "",
    };

    private Dictionary<string, string> CollectBuiltInValues()
    {
        var result = new Dictionary<string, string>();
        foreach (var (key, control) in _builtInFieldControls)
        {
            string value = ReadControlValue(control).Trim();
            if (value.Length > 0)
            {
                result[key] = value;
            }
        }
        return result;
    }

    // ---------- Custom provider spec form ----------

    private void LoadCustomSpecForm(ProviderSpec spec)
    {
        EndpointBox.Text = spec.Endpoint;
        MethodBox.Text = string.IsNullOrWhiteSpace(spec.Method) ? "POST" : spec.Method;
        UploadKindCombo.SelectedIndex = spec.UploadKind == ShareUploadKind.RawBody ? 1 : 0;
        MultipartFieldNameBox.Text = spec.MultipartFieldName ?? "file";
        ExtraFieldsBox.Text = string.Join(Environment.NewLine, spec.ExtraFields.Select(kv => $"{kv.Key}={kv.Value}"));
        HeadersBox.Text = string.Join(Environment.NewLine, spec.Headers.Select(kv => $"{kv.Key}: {kv.Value}"));
        MaxSizeMbBox.Text = spec.MaxUploadBytes is { } bytes
            ? (bytes / 1024.0 / 1024.0).ToString("0.##", CultureInfo.InvariantCulture)
            : "";
        ResponseModeCombo.SelectedIndex = spec.ResponseMode switch
        {
            ResponseUrlMode.Regex => 1,
            ResponseUrlMode.PlainBody => 2,
            _ => 0,
        };
        ResponseJsonPathBox.Text = spec.ResponseJsonPath ?? "";
        ResponseRegexBox.Text = spec.ResponseRegex ?? "";
        CustomValuesBox.Text = string.Join(Environment.NewLine, _originalConfig.Values.Select(kv => $"{kv.Key}={kv.Value}"));

        UpdateCustomSpecVisibility();
    }

    private void UploadKindCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e) => UpdateCustomSpecVisibility();
    private void ResponseModeCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e) => UpdateCustomSpecVisibility();

    private void UpdateCustomSpecVisibility()
    {
        MultipartOnlyPanel.IsVisible = UploadKindCombo.SelectedIndex != 1;
        ResponseJsonPathPanel.IsVisible = ResponseModeCombo.SelectedIndex == 0;
        ResponseRegexPanel.IsVisible = ResponseModeCombo.SelectedIndex == 1;
    }

    /// <summary>Parses "one per line: key=value" / "one per line: Name: value" boxes into a
    /// dictionary. Blank lines and lines without the separator are silently skipped — a half-typed
    /// trailing line while editing must never throw or corrupt the rest of the form.</summary>
    private static Dictionary<string, string> ParseKeyValueLines(string text, char separator)
    {
        var result = new Dictionary<string, string>();
        foreach (string rawLine in text.Split('\n'))
        {
            string line = rawLine.Trim().TrimEnd('\r');
            if (line.Length == 0) continue;
            int sep = line.IndexOf(separator);
            if (sep <= 0) continue;
            string key = line[..sep].Trim();
            string value = line[(sep + 1)..].Trim();
            if (key.Length > 0)
            {
                result[key] = value;
            }
        }
        return result;
    }

    private ProviderSpec BuildCustomSpecFromForm()
    {
        long? maxBytes = null;
        if (double.TryParse((MaxSizeMbBox.Text ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double mb) && mb > 0)
        {
            maxBytes = (long)(mb * 1024 * 1024);
        }

        var uploadKind = UploadKindCombo.SelectedIndex == 1 ? ShareUploadKind.RawBody : ShareUploadKind.Multipart;
        var responseMode = ResponseModeCombo.SelectedIndex switch
        {
            1 => ResponseUrlMode.Regex,
            2 => ResponseUrlMode.PlainBody,
            _ => ResponseUrlMode.JsonPath,
        };

        string name = string.IsNullOrWhiteSpace(DisplayNameBox.Text) ? "Custom provider" : DisplayNameBox.Text!.Trim();

        return new ProviderSpec
        {
            Id = _originalConfig.CustomSpec?.Id is { Length: > 0 } existingId ? existingId : "custom-" + Guid.NewGuid().ToString("N"),
            Name = name,
            Endpoint = (EndpointBox.Text ?? "").Trim(),
            Method = string.IsNullOrWhiteSpace(MethodBox.Text) ? "POST" : MethodBox.Text!.Trim(),
            UploadKind = uploadKind,
            MultipartFieldName = string.IsNullOrWhiteSpace(MultipartFieldNameBox.Text) ? "file" : MultipartFieldNameBox.Text!.Trim(),
            ExtraFields = ParseKeyValueLines(ExtraFieldsBox.Text ?? "", '='),
            Headers = ParseKeyValueLines(HeadersBox.Text ?? "", ':'),
            MaxUploadBytes = maxBytes,
            ResponseMode = responseMode,
            ResponseJsonPath = string.IsNullOrWhiteSpace(ResponseJsonPathBox.Text) ? null : ResponseJsonPathBox.Text!.Trim(),
            ResponseRegex = string.IsNullOrWhiteSpace(ResponseRegexBox.Text) ? null : ResponseRegexBox.Text!.Trim(),
            IsBuiltIn = false,
            Verified = false, // a user-authored spec was never checked against any provider's docs by this build
        };
    }

    // ---------- Save / Remove / Cancel ----------

    private ShareProviderConfig BuildUpdatedConfig()
    {
        string displayName = string.IsNullOrWhiteSpace(DisplayNameBox.Text)
            ? (_builtInSpec?.Name ?? _originalConfig.DisplayName)
            : DisplayNameBox.Text!.Trim();

        if (_originalConfig.IsCustom)
        {
            var customSpec = BuildCustomSpecFromForm();
            return _originalConfig with
            {
                DisplayName = displayName,
                Enabled = EnabledCheckBox.IsChecked == true,
                CustomSpec = customSpec,
                Values = ParseKeyValueLines(CustomValuesBox.Text ?? "", '='),
            };
        }

        return _originalConfig with
        {
            DisplayName = displayName,
            Enabled = EnabledCheckBox.IsChecked == true,
            Values = CollectBuiltInValues(),
        };
    }

    private void SaveButton_Click(object? sender, RoutedEventArgs e)
    {
        var updated = BuildUpdatedConfig();

        if (updated.IsCustom && string.IsNullOrWhiteSpace(updated.CustomSpec?.Endpoint))
        {
            ShowValidationError("Enter an endpoint URL before saving.");
            return;
        }

        _onSave(updated);
        Close();
    }

    private async void RemoveButton_Click(object? sender, RoutedEventArgs e)
    {
        bool? confirm = await ShowYesNoDialogAsync(
            this, "RoeSnip", $"Remove '{DisplayNameBox.Text}'? This cannot be undone.");
        if (confirm != true)
        {
            return;
        }
        _onRemove?.Invoke(_originalConfig.Id);
        Close();
    }

    private void CancelButton_Click(object? sender, RoutedEventArgs e) => Close();

    private void ShowValidationError(string message)
    {
        ValidationErrorText.Text = message;
        ValidationErrorText.IsVisible = true;
    }

    // ---------- Test ----------

    private async void TestButton_Click(object? sender, RoutedEventArgs e)
    {
        var testConfig = BuildUpdatedConfig();
        ProviderSpec? spec = ShareProviderCatalog.ResolveSpec(testConfig);
        if (spec is null)
        {
            TestStatusText.Text = "Cannot test: no valid provider spec.";
            TestStatusText.Foreground = ErrorBrush;
            return;
        }

        _testCts?.Cancel();
        _testCts = new CancellationTokenSource();
        var token = _testCts.Token;

        TestButton.IsEnabled = false;
        TestStatusText.Text = "Testing...";
        TestStatusText.Foreground = MutedBrush;

        try
        {
            byte[] png = ShareTestImage.CreatePngBytes();
            using var pngStream = new MemoryStream(png);
            ShareUploadResult result = await ShareManager.UploadAsync(
                testConfig, pngStream, "roesnip-test.png", "image/png", token).ConfigureAwait(true);

            if (token.IsCancellationRequested)
            {
                return; // window closed mid-test - nothing left to update
            }

            if (result.Success)
            {
                TestStatusText.Text = $"Success: {result.Url}";
                TestStatusText.Foreground = OkBrush;
            }
            else
            {
                TestStatusText.Text = $"Failed: {result.ErrorMessage}";
                TestStatusText.Foreground = ErrorBrush;
            }
        }
        catch (OperationCanceledException)
        {
            // window closed mid-test - nothing left to update
        }
        finally
        {
            if (!token.IsCancellationRequested)
            {
                TestButton.IsEnabled = true;
            }
        }
    }

    // ---------- Shared dark-palette brushes + the owned Yes/No confirm (Avalonia has no MessageBox) ----------

    private static readonly IBrush MutedBrush = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A));
    private static readonly IBrush OkBrush = new SolidColorBrush(Color.FromRgb(0x4A, 0x9E, 0xFF));
    private static readonly IBrush ErrorBrush = new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26));

    /// <summary>Owned Yes/No dialog for the Remove confirmation — Avalonia has no MessageBox.
    /// Distinct from TrayApp.ShowYesNoDialogAsync (that one is deliberately ownerless/topmost, for
    /// the tray's own startup-time PrintScreen consent prompt outside any window); this one is a
    /// true owned modal via ShowDialog so it blocks input to THIS window specifically, matching the
    /// WPF app's Owner-scoped MessageBox. Returns true for Yes, false for No, null if closed without
    /// answering.</summary>
    private static async Task<bool?> ShowYesNoDialogAsync(Window owner, string title, string message)
    {
        bool? answer = null;

        var text = new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new Thickness(16) };

        var yesButton = new Button { Content = "Yes", MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
        var noButton = new Button { Content = "No", MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(16, 0, 16, 16),
        };
        buttons.Children.Add(yesButton);
        buttons.Children.Add(noButton);

        var root = new StackPanel();
        root.Children.Add(text);
        root.Children.Add(buttons);

        var dialog = new Window
        {
            Title = title,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = root,
        };

        yesButton.Click += (_, _) => { answer = true; dialog.Close(); };
        noButton.Click += (_, _) => { answer = false; dialog.Close(); };

        await dialog.ShowDialog(owner);
        return answer;
    }
}
