using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using RoeSnip.App;

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
using Cursors = System.Windows.Input.Cursors;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;

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
    private const double SwatchHeightDip = 64.0;

    private readonly Action _onPickRequested;
    private RoeSnipSettings _settings;

    private byte _r, _g, _b;
    private double? _nits; // null when the active color isn't a live capture sample (a shade or a reloaded recent)

    public ColorPickerWindow(Action onPickRequested)
    {
        InitializeComponent();
        _onPickRequested = onPickRequested;
        _settings = SettingsStore.Load();

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

        if (_settings.ColorFormatShowHex)
        {
            AddFormatRow("HEX", ColorFormatting.Hex(_r, _g, _b));
        }
        if (_settings.ColorFormatShowRgb)
        {
            AddFormatRow("RGB", ColorFormatting.Rgb(_r, _g, _b));
        }
        if (_settings.ColorFormatShowHsl)
        {
            AddFormatRow("HSL", ColorFormatting.Hsl(_r, _g, _b));
        }
        if (_settings.ColorFormatShowNits)
        {
            // No live nits sample for a shade / a reloaded recent color — show a dash rather than a
            // misleading 0.0.
            AddFormatRow("NITS", _nits is { } n ? ColorFormatting.Nits(n) : "—");
        }
    }

    private void AddFormatRow(string label, string value)
    {
        var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var labelText = new TextBlock
        {
            Text = label,
            Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A)),
            FontSize = 10.5,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(labelText, 0);

        var valueText = new TextBlock
        {
            Text = value,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(valueText, 1);

        var copyButton = new Button
        {
            Content = "Copy",
            Padding = new Thickness(8, 2, 8, 2),
            Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2E)),
            Foreground = Brushes.White,
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

    private void BuildFormatsPopup()
    {
        FormatsPopupPanel.Children.Clear();
        FormatsPopupPanel.Children.Add(BuildFormatCheckBox("HEX", _settings.ColorFormatShowHex,
            v => _settings = _settings with { ColorFormatShowHex = v }));
        FormatsPopupPanel.Children.Add(BuildFormatCheckBox("RGB", _settings.ColorFormatShowRgb,
            v => _settings = _settings with { ColorFormatShowRgb = v }));
        FormatsPopupPanel.Children.Add(BuildFormatCheckBox("HSL", _settings.ColorFormatShowHsl,
            v => _settings = _settings with { ColorFormatShowHsl = v }));
        FormatsPopupPanel.Children.Add(BuildFormatCheckBox("Nits", _settings.ColorFormatShowNits,
            v => _settings = _settings with { ColorFormatShowNits = v }));
    }

    private CheckBox BuildFormatCheckBox(string label, bool initial, Action<bool> applyToSettings)
    {
        var checkBox = new CheckBox
        {
            Content = label,
            IsChecked = initial,
            Foreground = Brushes.White,
            Margin = new Thickness(0, 2, 0, 2),
            Focusable = false,
        };
        checkBox.Checked += (_, _) => { applyToSettings(true); TrySaveSettings(); RefreshFormatRowsPanel(); };
        checkBox.Unchecked += (_, _) => { applyToSettings(false); TrySaveSettings(); RefreshFormatRowsPanel(); };
        return checkBox;
    }

    private void TrySaveSettings()
    {
        try
        {
            SettingsStore.Save(_settings);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"RoeSnip: failed to save color picker settings: {ex.Message}");
        }
    }
}
