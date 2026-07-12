using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using RoeSnip.Sharing;

namespace RoeSnip.App;

// RoeSnip.csproj enables both UseWPF and UseWindowsForms, so several WPF/WinForms type names
// collide by simple name - same disambiguation convention Overlay/ToolbarControl.xaml.cs already
// uses for "Color"/"UserControl".
using Control = System.Windows.Controls.Control;
using Label = System.Windows.Controls.Label;
using TextBox = System.Windows.Controls.TextBox;
using Brush = System.Windows.Media.Brush;
using MessageBox = System.Windows.MessageBox;

/// <summary>The per-provider detail form: for a built-in provider, just its ProviderSpec.ConfigFields
/// (e.g. RoeShare's Server URL + API key) plus Enabled/DisplayName - the request shape itself (which
/// IS the built-in spec) is not editable here, only looked up fresh from ShareProviderCatalog every
/// time (see ProviderSpec's own doc comment for why). For a "Custom..." provider, the WHOLE
/// ProviderSpec is editable (endpoint, headers, response extraction, ...), because there is no
/// catalog entry to fall back on.
///
/// The Test button exercises the REAL pipeline (ShareManager.UploadAsync -> ProviderSpecShareProvider
/// -> the actual HTTP endpoint) against a tiny generated PNG (Sharing/ShareTestImage), using whatever
/// is currently typed into this form - not yet-saved state - so a user can validate a spec before
/// committing it. This button is a real, working implementation; it has only been exercised in this
/// codebase via unit tests against a mock HttpMessageHandler, never against the real network
/// (see TESTING.md).</summary>
public partial class ShareProviderEditWindow : Window
{
    private readonly ShareProviderConfig _originalConfig;
    private readonly ProviderSpec? _builtInSpec; // null when editing/adding a Custom provider
    private readonly bool _isNew;
    private readonly Action<ShareProviderConfig> _onSave;
    private readonly Action<string>? _onRemove;

    // Built-in mode: one control (TextBox or PasswordBox, per ShareConfigField.IsSecret) per
    // ProviderSpec.ConfigFields entry, keyed by ShareConfigField.Key.
    private readonly Dictionary<string, Control> _builtInFieldControls = new();

    private CancellationTokenSource? _testCts;

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
            CustomSpecPanel.Visibility = Visibility.Visible;
            LoadCustomSpecForm(config.CustomSpec ?? new ProviderSpec { IsBuiltIn = false });
            RemoveButton.Visibility = isNew ? Visibility.Collapsed : Visibility.Visible;
        }
        else if (_builtInSpec is { } spec)
        {
            HeaderText.Text = $"Configure {spec.Name}";
            NotesText.Text = spec.Notes ?? "";
            NotesText.Visibility = string.IsNullOrEmpty(spec.Notes) ? Visibility.Collapsed : Visibility.Visible;
            if (!spec.Verified)
            {
                UntestedBadgeText.Text = "UNTESTED: this provider's spec could not be fully confirmed against current public docs. Double-check with Test below before relying on it.";
                UntestedBadgeText.Visibility = Visibility.Visible;
            }
            BuildBuiltInFields(spec, config.Values);
        }
        else
        {
            HeaderText.Text = "Unknown provider";
            NotesText.Text = "This provider's spec id is not recognized (it may have been removed in a newer RoeSnip build). You can only remove or disable it here.";
            NotesText.Visibility = Visibility.Visible;
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
            BuiltInFieldsPanel.Children.Add(new Label { Content = field.Label });

            values.TryGetValue(field.Key, out string? existingValue);

            Control control;
            if (field.IsSecret)
            {
                var passwordBox = new PasswordBox { Margin = new Thickness(0, 0, 0, 10) };
                if (!string.IsNullOrEmpty(existingValue))
                {
                    passwordBox.Password = existingValue;
                }
                control = passwordBox;
            }
            else
            {
                var textBox = new TextBox { Text = existingValue ?? "", Margin = new Thickness(0, 0, 0, 10) };
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
                Foreground = (Brush)Resources["MutedBrush"],
                FontSize = 11,
                Margin = new Thickness(0, 0, 0, 10),
            });
        }
    }

    private static string ReadControlValue(Control control) => control switch
    {
        PasswordBox pb => pb.Password,
        TextBox tb => tb.Text,
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

    private void UploadKindCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateCustomSpecVisibility();
    private void ResponseModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateCustomSpecVisibility();

    private void UpdateCustomSpecVisibility()
    {
        MultipartOnlyPanel.Visibility = UploadKindCombo.SelectedIndex == 1 ? Visibility.Collapsed : Visibility.Visible;
        ResponseJsonPathPanel.Visibility = ResponseModeCombo.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        ResponseRegexPanel.Visibility = ResponseModeCombo.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Parses "one per line: key=value" / "one per line: Name: value" boxes into a
    /// dictionary. Blank lines and lines without the separator are silently skipped - a half-typed
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
        if (double.TryParse(MaxSizeMbBox.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double mb) && mb > 0)
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

        string name = string.IsNullOrWhiteSpace(DisplayNameBox.Text) ? "Custom provider" : DisplayNameBox.Text.Trim();

        return new ProviderSpec
        {
            Id = _originalConfig.CustomSpec?.Id is { Length: > 0 } existingId ? existingId : "custom-" + Guid.NewGuid().ToString("N"),
            Name = name,
            Endpoint = EndpointBox.Text.Trim(),
            Method = string.IsNullOrWhiteSpace(MethodBox.Text) ? "POST" : MethodBox.Text.Trim(),
            UploadKind = uploadKind,
            MultipartFieldName = string.IsNullOrWhiteSpace(MultipartFieldNameBox.Text) ? "file" : MultipartFieldNameBox.Text.Trim(),
            ExtraFields = ParseKeyValueLines(ExtraFieldsBox.Text, '='),
            Headers = ParseKeyValueLines(HeadersBox.Text, ':'),
            MaxUploadBytes = maxBytes,
            ResponseMode = responseMode,
            ResponseJsonPath = string.IsNullOrWhiteSpace(ResponseJsonPathBox.Text) ? null : ResponseJsonPathBox.Text.Trim(),
            ResponseRegex = string.IsNullOrWhiteSpace(ResponseRegexBox.Text) ? null : ResponseRegexBox.Text.Trim(),
            IsBuiltIn = false,
            Verified = false, // a user-authored spec was never checked against any provider's docs by this build
        };
    }

    // ---------- Save / Remove / Cancel ----------

    private ShareProviderConfig BuildUpdatedConfig()
    {
        string displayName = string.IsNullOrWhiteSpace(DisplayNameBox.Text)
            ? (_builtInSpec?.Name ?? _originalConfig.DisplayName)
            : DisplayNameBox.Text.Trim();

        if (_originalConfig.IsCustom)
        {
            var customSpec = BuildCustomSpecFromForm();
            return _originalConfig with
            {
                DisplayName = displayName,
                Enabled = EnabledCheckBox.IsChecked == true,
                CustomSpec = customSpec,
                Values = ParseKeyValueLines(CustomValuesBox.Text, '='),
            };
        }

        return _originalConfig with
        {
            DisplayName = displayName,
            Enabled = EnabledCheckBox.IsChecked == true,
            Values = CollectBuiltInValues(),
        };
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        var updated = BuildUpdatedConfig();

        if (updated.IsCustom && string.IsNullOrWhiteSpace(updated.CustomSpec?.Endpoint))
        {
            MessageBox.Show(this, "Enter an endpoint URL before saving.", "RoeSnip", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _onSave(updated);
        Close();
    }

    private void RemoveButton_Click(object sender, RoutedEventArgs e)
    {
        var confirm = MessageBox.Show(
            this, $"Remove '{DisplayNameBox.Text}'? This cannot be undone.",
            "RoeSnip", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }
        _onRemove?.Invoke(_originalConfig.Id);
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => Close();

    // ---------- Test ----------

    private async void TestButton_Click(object sender, RoutedEventArgs e)
    {
        var testConfig = BuildUpdatedConfig();
        ProviderSpec? spec = ShareProviderCatalog.ResolveSpec(testConfig);
        if (spec is null)
        {
            TestStatusText.Text = "Cannot test: no valid provider spec.";
            TestStatusText.Foreground = (Brush)Resources["ErrorBrush"];
            return;
        }

        _testCts?.Cancel();
        _testCts = new CancellationTokenSource();
        var token = _testCts.Token;

        TestButton.IsEnabled = false;
        TestStatusText.Text = "Testing...";
        TestStatusText.Foreground = (Brush)Resources["MutedBrush"];

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
                TestStatusText.Foreground = (Brush)Resources["OkBrush"];
            }
            else
            {
                TestStatusText.Text = $"Failed: {result.ErrorMessage}";
                TestStatusText.Foreground = (Brush)Resources["ErrorBrush"];
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
}
