using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using RoeSnip.App;
using RoeSnip.Core.Diagnostics;

namespace RoeSnip.Overlay;

// RoeSnip.csproj enables both UseWPF and UseWindowsForms, so System.Windows.Forms/System.Drawing
// are in scope alongside System.Windows/System.Windows.Media/.Controls/.Input — alias the colliding
// names to WPF's, matching the convention already used throughout Overlay/* (see AnnotationLayer.cs).
using Point = System.Windows.Point;
using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;
using FontFamily = System.Windows.Media.FontFamily;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using Border = System.Windows.Controls.Border;
using TextBox = System.Windows.Controls.TextBox;
using Cursors = System.Windows.Input.Cursors;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using Orientation = System.Windows.Controls.Orientation;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

/// <summary>Small standalone always-on-top eyedropper window (modeled on PowerToys Color Picker),
/// singleton per app (owned/recreated on demand by OverlayController — see OnColorPicked there).
/// Opened by a plain click on the overlay (OverlayWindow.TriggerColorPick), which cancels the snip
/// and hands this window a <see cref="PickedColorInfo"/> snapshot via <see cref="ApplyPick"/>.
///
/// "Pick" re-launches the capture overlay in a lightweight pick-only mode (OverlaySession's
/// pickOnlyMode — suppresses selection/toolbar, shows just crosshair+magnifier) so the next click
/// picks a new color into this same window in place. Recent picks (newest first, capped at 8) and
/// which format rows are visible are persisted to RoeSnipSettings (additive fields) so they survive
/// across window instances/app restarts.</summary>
public partial class ColorPickerWindow : Window
{
    private const int MaxRecentColors = 8;
    private const int ShadeCount = 7;
    private const double SwatchHeightDip = 48.0; // must match MainSwatch's Height in the XAML

    private readonly Action _onPickRequested;
    private RoeSnipSettings _settings;

    /// <summary>Working copy of the ordered format list (see RoeSnipSettings.ColorFormats /
    /// ColorFormatCatalog.EffectiveFormats) — every popover mutation edits this list, persists it,
    /// and re-renders both the rows and the popover itself.</summary>
    private List<ColorFormatEntry> _formats;

    private byte _r, _g, _b;
    private double? _nits; // null when the active color isn't a live capture sample (a shade or a reloaded recent)

    public ColorPickerWindow(Action onPickRequested)
    {
        InitializeComponent();
        _onPickRequested = onPickRequested;
        _settings = SettingsStore.Load();
        _formats = ColorFormatCatalog.EffectiveFormats(_settings);

        // Exclude this window from screen capture (same fix as FlashDimmer): a "Pick" re-capture
        // freezes the screen WITH this window still up — without the affinity it photobombs the
        // frozen frame and the user can't pick colors from pixels underneath it.
        SourceInitialized += (_, _) =>
        {
            if (Environment.GetEnvironmentVariable("ROESNIP_DIAG_NOEXCLUDE") == "1")
            {
                return; // same diagnostic escape hatch as FlashDimmer/OverlayWindow — lets the
                        // external screenshot harness see this window
            }
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (!SetWindowDisplayAffinity(hwnd, 0x00000011 /* WDA_EXCLUDEFROMCAPTURE */))
            {
                FileLog.Write(
                    "RoeSnip: SetWindowDisplayAffinity failed on the color picker window; " +
                    "pick-mode re-captures will include this window in the frozen frame.");
            }
        };

        // Esc closes this window (user-reported: it previously fell through and could end up
        // cancelling an overlay session instead). PreviewKeyDown so it wins over any focused
        // child; the formats popup gets to close itself first if it is open.
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != System.Windows.Input.Key.Escape)
            {
                return;
            }
            if (FormatsPopup.IsOpen)
            {
                FormatsPopup.IsOpen = false;
            }
            else
            {
                Close();
            }
            e.Handled = true;
        };

        BuildFormatsPopup();
        RefreshRecentColorsPanel();

        // Sensible pre-pick default so the window never shows a blank swatch/rows — in practice
        // OverlayController always calls ApplyPick before the first Show().
        _r = _g = _b = 0x80;
        _nits = null;
        RefreshDisplay();
    }

    /// <summary>Applies a new pick (from the overlay's eyedropper click or a pick-mode re-pick),
    /// updates the recent-colors list (persisted), and — only the first time this instance becomes
    /// visible — positions the window near the click. Subsequent re-picks update content in place
    /// without moving the window, per spec ("re-picks update it in place").</summary>
    internal void ApplyPick(PickedColorInfo info)
    {
        bool wasVisible = IsVisible;

        _r = info.R; _g = info.G; _b = info.B; _nits = info.Nits;
        RefreshDisplay();

        _settings = _settings with
        {
            RecentPickedColors = BoundedColorList.Push(
                _settings.RecentPickedColors, ColorFormatting.HexWithHash(_r, _g, _b), MaxRecentColors),
        };
        TrySaveSettings();
        RefreshRecentColorsPanel();

        if (!wasVisible)
        {
            PositionNear(info.ScreenPx, info.MonitorDpiX, info.MonitorDpiY);
        }
    }

    /// <summary>Approximate placement "near" the click, per spec — not the pixel-exact physical
    /// positioning OverlayWindow uses for its fullscreen monitor windows (that precision isn't
    /// needed for a small utility popup). Converts the absolute physical-pixel click point to DIPs
    /// using the source monitor's DPI so it lands close to the right spot even on a differently
    /// scaled secondary monitor.</summary>
    private void PositionNear(Point screenPx, int dpiX, int dpiY)
    {
        double scaleX = dpiX > 0 ? dpiX / 96.0 : 1.0;
        double scaleY = dpiY > 0 ? dpiY / 96.0 : 1.0;
        const double offsetDip = 18.0;
        Left = screenPx.X / scaleX + offsetDip;
        Top = screenPx.Y / scaleY + offsetDip;
    }

    // ---------- Custom title bar ----------

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // DragMove throws if the mouse button was already released by the time it's called
            // (e.g. a very fast click) — nothing to do, the window just doesn't move.
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void PickButton_Click(object sender, RoutedEventArgs e) => _onPickRequested();

    // ---------- Swatch / shades / format rows ----------

    private void RefreshDisplay()
    {
        MainSwatch.Background = new SolidColorBrush(Color.FromRgb(_r, _g, _b));
        RefreshShadesPanel();
        RefreshFormatRowsPanel();
    }

    private void RefreshShadesPanel()
    {
        ShadesPanel.Children.Clear();
        var (h, s, l) = ColorFormatting.RgbToHsl(_r, _g, _b);

        // ShadeCount stops spanning a lightness band centered on the current color (step*count/2 in
        // each direction), clamped so the strip never bottoms out at pure black/white unless the
        // source color already does.
        const double step = 0.12;
        int half = ShadeCount / 2;
        for (int i = -half; i <= half; i++)
        {
            double shadeL = Math.Clamp(l + step * i, 0.04, 0.96);
            var (sr, sg, sb) = ColorFormatting.HslToRgb(h, s, shadeL);

            var swatch = new Border
            {
                Height = SwatchHeightDip / ShadeCount,
                Background = new SolidColorBrush(Color.FromRgb(sr, sg, sb)),
                Cursor = Cursors.Hand,
                ToolTip = ColorFormatting.HexWithHash(sr, sg, sb),
            };
            swatch.MouseLeftButtonUp += (_, _) => SelectShade(sr, sg, sb);
            ShadesPanel.Children.Add(swatch);
        }
    }

    private void SelectShade(byte r, byte g, byte b)
    {
        _r = r; _g = g; _b = b;
        _nits = null; // a shade is derived, not a live capture sample
        RefreshDisplay();
    }

    private void RefreshFormatRowsPanel()
    {
        FormatRowsPanel.Children.Clear();
        foreach (var entry in _formats)
        {
            if (!entry.Enabled)
            {
                continue;
            }
            // %Nt renders an em dash when _nits is null (a shade / a reloaded recent color has no
            // live capture sample) — handled inside the template engine.
            AddFormatRow(entry.Name, ColorFormatTemplate.Format(entry.Format, _r, _g, _b, _nits));
        }
    }

    private void AddFormatRow(string label, string value)
    {
        var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(38) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var labelText = new TextBlock
        {
            Text = label,
            Foreground = new SolidColorBrush(Color.FromRgb(0xA2, 0xA2, 0xAB)),
            FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(labelText, 0);

        var valueText = new TextBlock
        {
            Text = value,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(valueText, 1);

        var copyButton = new Button
        {
            Content = "Copy",
            Padding = new Thickness(7, 1, 7, 1),
            FontSize = 11,
            Background = new SolidColorBrush(Color.FromRgb(0x18, 0x18, 0x1D)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xED, 0xED, 0xF0)),
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            Focusable = false,
        };
        copyButton.Click += (_, _) => CopyRowValue(value, copyButton);
        Grid.SetColumn(copyButton, 2);

        row.Children.Add(labelText);
        row.Children.Add(valueText);
        row.Children.Add(copyButton);
        FormatRowsPanel.Children.Add(row);
    }

    private static void CopyRowValue(string value, Button sourceButton)
    {
        try
        {
            System.Windows.Clipboard.SetText(value);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // Clipboard transiently locked by another process — a copy button is a convenience,
            // not worth an error dialog.
            return;
        }

        object original = sourceButton.Content;
        sourceButton.Content = "Copied";
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            sourceButton.Content = original;
        };
        timer.Start();
    }

    // ---------- Recent colors ----------

    private void RefreshRecentColorsPanel()
    {
        RecentColorsPanel.Children.Clear();
        foreach (var hex in _settings.RecentPickedColors)
        {
            if (!TryParseHex(hex, out byte r, out byte g, out byte b))
            {
                continue;
            }

            var swatch = new Border
            {
                Width = 18,
                Height = 18,
                Margin = new Thickness(2, 0, 0, 0),
                CornerRadius = new CornerRadius(3),
                Background = new SolidColorBrush(Color.FromRgb(r, g, b)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                ToolTip = hex,
            };
            swatch.MouseLeftButtonUp += (_, _) =>
            {
                _r = r; _g = g; _b = b;
                _nits = null; // reloaded from history — no live nits sample available for it
                RefreshDisplay();
            };
            RecentColorsPanel.Children.Add(swatch);
        }
    }

    private static bool TryParseHex(string hex, out byte r, out byte g, out byte b)
    {
        r = g = b = 0;
        string s = hex.TrimStart('#');
        return s.Length == 6
            && byte.TryParse(s.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out r)
            && byte.TryParse(s.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out g)
            && byte.TryParse(s.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out b);
    }

    // ---------- Formats gear popover ----------

    private void GearButton_Click(object sender, RoutedEventArgs e) => FormatsPopup.IsOpen = !FormatsPopup.IsOpen;

    /// <summary>The gear popover is the format MANAGER (PowerToys parity): every entry can be
    /// toggled and reordered; custom entries can be edited and deleted; "Add custom format" opens
    /// the template dialog. Every mutation goes through <see cref="MutateFormats"/>, which
    /// persists the list and re-renders both this popover and the visible rows.</summary>
    private void BuildFormatsPopup()
    {
        FormatsPopupPanel.Children.Clear();

        // Header: names the two checkbox columns (rows here / lines under the magnifier loupe).
        var header = FormatGrid();
        var showLabel = PopupColumnLabel("show");
        Grid.SetColumn(showLabel, 0);
        header.Children.Add(showLabel);
        var loupeLabel = PopupColumnLabel("loupe");
        loupeLabel.HorizontalAlignment = HorizontalAlignment.Center;
        Grid.SetColumn(loupeLabel, 1);
        header.Children.Add(loupeLabel);
        FormatsPopupPanel.Children.Add(header);

        for (int i = 0; i < _formats.Count; i++)
        {
            int index = i; // capture a stable copy for the click closures below
            var entry = _formats[i];

            var row = FormatGrid();

            var checkBox = new CheckBox
            {
                Content = entry.Name,
                IsChecked = entry.Enabled,
                Foreground = new SolidColorBrush(Color.FromRgb(0xED, 0xED, 0xF0)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0),
                Focusable = false,
                ToolTip = entry.Format,
            };
            checkBox.Checked += (_, _) => MutateFormats(() => _formats[index] = _formats[index] with { Enabled = true });
            checkBox.Unchecked += (_, _) => MutateFormats(() => _formats[index] = _formats[index] with { Enabled = false });
            Grid.SetColumn(checkBox, 0);
            row.Children.Add(checkBox);

            var loupeBox = new CheckBox
            {
                IsChecked = entry.InLoupe,
                IsEnabled = entry.Enabled, // the loupe shows the enabled subset — a disabled format never renders there
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
                Focusable = false,
                ToolTip = "Also show this format under the magnifier preview",
            };
            loupeBox.Checked += (_, _) => MutateFormats(() => _formats[index] = _formats[index] with { InLoupe = true });
            loupeBox.Unchecked += (_, _) => MutateFormats(() => _formats[index] = _formats[index] with { InLoupe = false });
            Grid.SetColumn(loupeBox, 1);
            row.Children.Add(loupeBox);

            var up = PopupIconButton("▴", "Move up", enabled: index > 0);
            up.Click += (_, _) => MutateFormats(() =>
                (_formats[index - 1], _formats[index]) = (_formats[index], _formats[index - 1]));
            Grid.SetColumn(up, 2);
            row.Children.Add(up);

            var down = PopupIconButton("▾", "Move down", enabled: index < _formats.Count - 1);
            down.Click += (_, _) => MutateFormats(() =>
                (_formats[index + 1], _formats[index]) = (_formats[index], _formats[index + 1]));
            Grid.SetColumn(down, 3);
            row.Children.Add(down);

            if (entry.IsCustom)
            {
                var edit = PopupIconButton("✎", "Edit this custom format", enabled: true);
                edit.Click += (_, _) =>
                {
                    var dialog = new CustomFormatDialog(this, entry.Name, entry.Format, _r, _g, _b, _nits);
                    if (dialog.ShowDialog() == true)
                    {
                        MutateFormats(() => _formats[index] = _formats[index] with
                        {
                            Name = dialog.FormatName,
                            Format = dialog.FormatTemplate,
                        });
                    }
                };
                Grid.SetColumn(edit, 4);
                row.Children.Add(edit);

                var delete = PopupIconButton("✕", "Delete this custom format", enabled: true);
                delete.Click += (_, _) => MutateFormats(() => _formats.RemoveAt(index));
                Grid.SetColumn(delete, 5);
                row.Children.Add(delete);
            }

            FormatsPopupPanel.Children.Add(row);
        }

        var addButton = new Button
        {
            Content = "+ Add custom format",
            Margin = new Thickness(0, 6, 0, 0),
            Padding = new Thickness(8, 3, 8, 3),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Background = new SolidColorBrush(Color.FromRgb(0x18, 0x18, 0x1D)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xED, 0xED, 0xF0)),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF)),
            Cursor = Cursors.Hand,
            Focusable = false,
        };
        addButton.Click += (_, _) =>
        {
            var dialog = new CustomFormatDialog(this, null, null, _r, _g, _b, _nits);
            if (dialog.ShowDialog() == true)
            {
                MutateFormats(() => _formats.Add(new ColorFormatEntry
                {
                    Name = dialog.FormatName,
                    Format = dialog.FormatTemplate,
                    Enabled = true,
                    IsCustom = true,
                }));
            }
        };
        FormatsPopupPanel.Children.Add(addButton);
    }

    /// <summary>Shared column layout for the popover's header and every format row, so the two
    /// checkbox columns line up: [name+show (star)] [loupe] [up] [down] [edit] [delete].</summary>
    private static Grid FormatGrid()
    {
        var grid = new Grid { Margin = new Thickness(0, 1, 0, 1) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        return grid;
    }

    private static TextBlock PopupColumnLabel(string text) => new()
    {
        Text = text,
        FontSize = 9.5,
        Foreground = new SolidColorBrush(Color.FromRgb(0xA2, 0xA2, 0xAB)),
        Margin = new Thickness(0, 0, 0, 2),
    };

    private static Button PopupIconButton(string glyph, string tooltip, bool enabled) => new()
    {
        Content = glyph,
        Width = 20,
        Height = 20,
        Margin = new Thickness(2, 0, 0, 0),
        Padding = new Thickness(0),
        Background = new SolidColorBrush(Color.FromRgb(0x18, 0x18, 0x1D)),
        Foreground = enabled ? new SolidColorBrush(Color.FromRgb(0xED, 0xED, 0xF0)) : new SolidColorBrush(Color.FromRgb(0x71, 0x71, 0x7B)),
        BorderThickness = new Thickness(0),
        Cursor = enabled ? Cursors.Hand : Cursors.Arrow,
        Focusable = false,
        IsEnabled = enabled,
        ToolTip = tooltip,
    };

    /// <summary>Single mutation seam for the format list: apply, persist (the stored list becomes
    /// the user's truth from the first edit on — see ColorFormatCatalog.EffectiveFormats), and
    /// re-render the rows AND the popover (indices/enabled states baked into the closures above
    /// go stale after any mutation, so the popover is always rebuilt wholesale).</summary>
    private void MutateFormats(Action mutation)
    {
        mutation();
        _settings = _settings with { ColorFormats = new List<ColorFormatEntry>(_formats) };
        TrySaveSettings();
        RefreshFormatRowsPanel();
        BuildFormatsPopup();
    }

    private void TrySaveSettings()
    {
        try
        {
            SettingsStore.Save(_settings);
        }
        catch (Exception ex)
        {
            FileLog.Write($"RoeSnip: failed to save color picker settings: {ex.Message}");
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);

    /// <summary>"Add/edit custom color format" dialog (PowerToys parity), built in code to match
    /// the picker's dark chrome: name + template inputs, a live preview against the picker's
    /// current color, and a condensed parameter reference. Owned + modal so it always sits above
    /// the (topmost) picker window.</summary>
    private sealed class CustomFormatDialog : Window
    {
        public string FormatName => _nameBox.Text.Trim();
        public string FormatTemplate => _formatBox.Text;

        private readonly TextBox _nameBox;
        private readonly TextBox _formatBox;
        private readonly TextBlock _preview;
        private readonly Button _saveButton;
        private readonly byte _r, _g, _b;
        private readonly double? _nits;

        public CustomFormatDialog(Window owner, string? name, string? format, byte r, byte g, byte b, double? nits)
        {
            _r = r; _g = g; _b = b; _nits = nits;

            Owner = owner;
            Title = name is null ? "Add custom color format" : "Edit custom color format";
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            SizeToContent = SizeToContent.Height;
            Width = 360;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            WindowStyle = WindowStyle.ToolWindow;
            Background = new SolidColorBrush(Color.FromRgb(0x18, 0x18, 0x1D));
            Foreground = new SolidColorBrush(Color.FromRgb(0xED, 0xED, 0xF0));
            FontSize = 12;
            UseLayoutRounding = true;

            _nameBox = DarkTextBox(name ?? string.Empty);
            _formatBox = DarkTextBox(format ?? "new Color (R = %Re, G = %Gr, B = %Bl)");
            _formatBox.FontFamily = new FontFamily("Consolas");
            _preview = new TextBlock
            {
                FontFamily = new FontFamily("Consolas"),
                Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x35)),
                Margin = new Thickness(0, 4, 0, 8),
                TextWrapping = TextWrapping.Wrap,
            };

            var reference = new TextBlock
            {
                FontFamily = new FontFamily("Consolas"),
                FontSize = 10.5,
                Foreground = new SolidColorBrush(Color.FromRgb(0x71, 0x71, 0x7B)),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8),
                Text =
                    "%Re %Gr %Bl %Al  red/green/blue/alpha\n" +
                    "%Cy %Ma %Ye %Bk  cmyk        %Hu hue  %Hn natural hue\n" +
                    "%Sl %Sb %Si      saturation (hsl/hsb/hsi)\n" +
                    "%Ll %Va %Br %In  lightness/value/brightness/intensity\n" +
                    "%Wh %Bn          whiteness/blackness    %Na color name\n" +
                    "%Lc %Ca %Cb      CIELab      %Xv %Yv %Zv  CIEXYZ\n" +
                    "%Lo %Oa %Ob      oklab       %Oc %Oh      oklch\n" +
                    "%Dv %Dr          decimal (BGR/RGB)      %Nt nits\n" +
                    "suffixes on %Re/%Gr/%Bl/%Al: b byte  h/H hex1  x/X hex2\n" +
                    "f/F float (with/without leading 0);  i on CIELab values\n" +
                    "example: %ReX = red as hex uppercase two digits",
            };

            _saveButton = new Button
            {
                Content = "Save",
                Padding = new Thickness(18, 4, 18, 4),
                Background = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x35)),
                Foreground = new SolidColorBrush(Color.FromRgb(0x18, 0x0D, 0x07)),
                FontWeight = FontWeights.SemiBold,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                IsDefault = true,
            };
            _saveButton.Click += (_, _) => DialogResult = true;

            var cancelButton = new Button
            {
                Content = "Cancel",
                Margin = new Thickness(8, 0, 0, 0),
                Padding = new Thickness(14, 4, 14, 4),
                Background = new SolidColorBrush(Color.FromRgb(0x18, 0x18, 0x1D)),
                Foreground = new SolidColorBrush(Color.FromRgb(0xED, 0xED, 0xF0)),
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                IsCancel = true,
            };

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            buttons.Children.Add(_saveButton);
            buttons.Children.Add(cancelButton);

            var root = new StackPanel { Margin = new Thickness(12) };
            root.Children.Add(Label("Name"));
            root.Children.Add(_nameBox);
            root.Children.Add(Label("Format"));
            root.Children.Add(_formatBox);
            root.Children.Add(_preview);
            root.Children.Add(reference);
            root.Children.Add(buttons);
            Content = root;

            _nameBox.TextChanged += (_, _) => RefreshState();
            _formatBox.TextChanged += (_, _) => RefreshState();
            RefreshState();
        }

        private void RefreshState()
        {
            _preview.Text = ColorFormatTemplate.Format(_formatBox.Text, _r, _g, _b, _nits);
            _saveButton.IsEnabled = FormatName.Length > 0 && _formatBox.Text.Length > 0;
        }

        private static TextBlock Label(string text) => new()
        {
            Text = text,
            FontSize = 10.5,
            Foreground = new SolidColorBrush(Color.FromRgb(0x71, 0x71, 0x7B)),
            Margin = new Thickness(0, 0, 0, 2),
        };

        private static TextBox DarkTextBox(string text) => new()
        {
            Text = text,
            Margin = new Thickness(0, 0, 0, 8),
            Padding = new Thickness(5, 3, 5, 3),
            Background = new SolidColorBrush(Color.FromRgb(0x18, 0x18, 0x1D)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xED, 0xED, 0xF0)),
            CaretBrush = new SolidColorBrush(Color.FromRgb(0xED, 0xED, 0xF0)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
        };
    }
}
