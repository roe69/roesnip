using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using RoeSnip.Core.Settings;
using ColorFormatCatalog = RoeSnip.Core.Color.ColorFormatCatalog;
using ColorFormatEntry = RoeSnip.Core.Color.ColorFormatEntry;
using ColorFormatTemplate = RoeSnip.Core.Color.ColorFormatTemplate;
using ColorFormatting = RoeSnip.Core.Color.ColorFormatting;
using PickedColorInfo = RoeSnip.Core.Color.PickedColorInfo;
using BoundedColorList = RoeSnip.Core.Color.BoundedColorList;

namespace RoeSnip.App.Overlay;

/// <summary>Small standalone always-on-top eyedropper window (modeled on PowerToys Color Picker),
/// singleton per app (owned/recreated on demand by OverlayController — see OnColorPicked there).
/// Opened by a plain click on the overlay (OverlayWindow.TriggerColorPick), which cancels the snip
/// and hands this window a <see cref="PickedColorInfo"/> snapshot via <see cref="ApplyPick"/>.
///
/// "Pick" re-launches the capture overlay in a lightweight pick-only mode (OverlayController's
/// pick-only session — suppresses selection/toolbar, shows just crosshair+magnifier) so the next
/// click picks a new color into this same window in place. Recent picks (newest first, capped at
/// 8) and which format rows are visible are persisted to RoeSnipSettings (additive fields) so they
/// survive across window instances/app restarts. Ported from the frozen WPF app's own
/// src/RoeSnip/Overlay/ColorPickerWindow.xaml(.cs) (item 22) — same layout/behavior, rebuilt with
/// Avalonia controls (Popup instead of a WPF Popup, Button-based swatches instead of raw
/// Border+mouse-event wiring, BeginMoveDrag instead of DragMove).</summary>
public partial class ColorPickerWindow : Window
{
    private const int MaxRecentColors = 8;
    private const int ShadeCount = 7;
    private const double SwatchHeightDip = 48.0; // must match MainSwatch's Height in the XAML

    private readonly System.Action _onPickRequested;
    private RoeSnipSettings _settings;

    /// <summary>Working copy of the ordered format list (see RoeSnipSettings.ColorFormats /
    /// ColorFormatCatalog.EffectiveFormats) — every popover mutation edits this list, persists it,
    /// and re-renders both the rows and the popover itself.</summary>
    private List<ColorFormatEntry> _formats;

    private byte _r, _g, _b;
    private double? _nits; // null when the active color isn't a live capture sample (a shade or a reloaded recent)

    // XAML-infrastructure-only constructor (satisfies the XAML compiler's public-ctor expectation,
    // AVLN3001) — RoeSnip's own code always uses the data-taking overload below; a window created
    // this way is inert and must not be shown.
    public ColorPickerWindow()
        : this(static () => { })
    {
    }

    public ColorPickerWindow(System.Action onPickRequested)
    {
        InitializeComponent();
        _onPickRequested = onPickRequested;
        _settings = SettingsStore.Load();
        _formats = ColorFormatCatalog.EffectiveFormats(_settings);

        FormatsPopup.PlacementTarget = GearButton;
        FormatsPopup.Placement = PlacementMode.Bottom;

        TitleBar.PointerPressed += TitleBar_PointerPressed;
        CloseButton.Click += (_, _) => Close();
        PickButton.Click += (_, _) => _onPickRequested();
        GearButton.Click += (_, _) => FormatsPopup.IsOpen = !FormatsPopup.IsOpen;

        // Item 02 parity: exclude this window from screen capture — a "Pick" re-capture freezes
        // the screen WITH this window still up; without the exclusion it photobombs the frozen
        // frame and the user can't pick colors from pixels underneath it. Same seam every other
        // Avalonia window in this app uses (overlay/flash/recording chrome); handles the
        // ROESNIP_DIAG_NOEXCLUDE=1 escape hatch and the non-Windows no-op itself.
        Opened += (_, _) => AppShell.WindowCaptureExclusion.Apply(this);

        // Esc closes this window (user-reported WPF fix: it previously fell through and could end
        // up cancelling an overlay session instead — see ColorPickerWindow.xaml.cs:81-99). Tunnel
        // so it wins over any focused child; the formats popup gets to close itself first if it is
        // open. Deliberately does NOT call into OverlayController/any shared cancel routine — this
        // window has no relationship to whichever overlay session (if any) happens to be active,
        // so closing it must never reach over and cancel one.
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key != Key.Escape)
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
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);

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
            PositionNear(info.ScreenPxX, info.ScreenPxY, info.MonitorDpiX, info.MonitorDpiY);
        }
    }

    /// <summary>Approximate placement "near" the click, per spec — not pixel-exact (that precision
    /// isn't needed for a small utility popup). Position is in physical screen pixels (Avalonia's
    /// PixelPoint, unlike Width/Height's DIPs), and the click's ScreenPx is already an absolute
    /// physical-pixel virtual-desktop coordinate, so only the DIP-specified offset needs converting
    /// via the source monitor's DPI — no divide-then-remultiply round trip WPF's own DIP-based
    /// Left/Top needed.</summary>
    private void PositionNear(double screenPxX, double screenPxY, int dpiX, int dpiY)
    {
        double scaleX = dpiX > 0 ? dpiX / 96.0 : 1.0;
        double scaleY = dpiY > 0 ? dpiY / 96.0 : 1.0;
        const double offsetDip = 18.0;
        Position = new PixelPoint(
            (int)Math.Round(screenPxX + offsetDip * scaleX),
            (int)Math.Round(screenPxY + offsetDip * scaleY));
    }

    // ---------- Custom title bar ----------

    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }
        try
        {
            BeginMoveDrag(e);
        }
        catch (InvalidOperationException)
        {
            // BeginMoveDrag can throw if the platform can't start a move-drag from this event
            // (e.g. a very fast click where the button was already released) — nothing to do, the
            // window just doesn't move. Mirrors WPF's own DragMove try/catch here.
        }
    }

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

            var swatch = new Button
            {
                Height = SwatchHeightDip / ShadeCount,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(0),
                Background = new SolidColorBrush(Color.FromRgb(sr, sg, sb)),
                BorderThickness = new Thickness(0),
                Cursor = new Cursor(StandardCursorType.Hand),
                Focusable = false,
            };
            ToolTip.SetTip(swatch, ColorFormatting.HexWithHash(sr, sg, sb));
            swatch.Click += (_, _) => SelectShade(sr, sg, sb);
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
            // %Nt renders "n/a" when _nits is null (a shade / a reloaded recent color has no live
            // capture sample) — handled inside the template engine.
            AddFormatRow(entry.Name, ColorFormatTemplate.Format(entry.Format, _r, _g, _b, _nits));
        }
    }

    private void AddFormatRow(string label, string value)
    {
        var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        row.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(38)));
        row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

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
            FontFamily = OverlayFonts.Mono,
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
            Cursor = new Cursor(StandardCursorType.Hand),
            Focusable = false,
        };
        copyButton.Click += async (_, _) => await CopyRowValueAsync(value, copyButton);
        Grid.SetColumn(copyButton, 2);

        row.Children.Add(labelText);
        row.Children.Add(valueText);
        row.Children.Add(copyButton);
        FormatRowsPanel.Children.Add(row);
    }

    private async Task CopyRowValueAsync(string value, Button sourceButton)
    {
        bool copied = false;
        try
        {
            copied = await ClipboardService.TryCopyTextAsync(this, value);
        }
        catch (Exception)
        {
            // Clipboard transiently locked by another process — a copy button is a convenience,
            // not worth an error dialog.
        }
        if (!copied)
        {
            return;
        }

        object? original = sourceButton.Content;
        sourceButton.Content = "Copied";
        await Task.Delay(TimeSpan.FromMilliseconds(900));
        sourceButton.Content = original;
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

            var swatch = new Button
            {
                Width = 18,
                Height = 18,
                Margin = new Thickness(2, 0, 0, 0),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(0),
                Background = new SolidColorBrush(Color.FromRgb(r, g, b)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1),
                Cursor = new Cursor(StandardCursorType.Hand),
                Focusable = false,
            };
            ToolTip.SetTip(swatch, hex);
            swatch.Click += (_, _) =>
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
            };
            ToolTip.SetTip(checkBox, entry.Format);
            checkBox.IsCheckedChanged += (_, _) =>
                MutateFormats(() => _formats[index] = _formats[index] with { Enabled = checkBox.IsChecked == true });
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
            };
            ToolTip.SetTip(loupeBox, "Also show this format under the magnifier preview");
            loupeBox.IsCheckedChanged += (_, _) =>
                MutateFormats(() => _formats[index] = _formats[index] with { InLoupe = loupeBox.IsChecked == true });
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
                edit.Click += async (_, _) =>
                {
                    var dialog = new CustomFormatDialog(entry.Name, entry.Format, _r, _g, _b, _nits);
                    bool saved = await dialog.ShowDialog<bool>(this);
                    if (saved)
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
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Background = new SolidColorBrush(Color.FromRgb(0x18, 0x18, 0x1D)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xED, 0xED, 0xF0)),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF)),
            Cursor = new Cursor(StandardCursorType.Hand),
            Focusable = false,
        };
        addButton.Click += async (_, _) =>
        {
            var dialog = new CustomFormatDialog(null, null, _r, _g, _b, _nits);
            bool saved = await dialog.ShowDialog<bool>(this);
            if (saved)
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
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(26)));
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        return grid;
    }

    private static TextBlock PopupColumnLabel(string text) => new()
    {
        Text = text,
        FontSize = 9.5,
        Foreground = new SolidColorBrush(Color.FromRgb(0xA2, 0xA2, 0xAB)),
        Margin = new Thickness(0, 0, 0, 2),
    };

    private static Button PopupIconButton(string glyph, string tooltip, bool enabled)
    {
        var button = new Button
        {
            Content = glyph,
            Width = 20,
            Height = 20,
            Margin = new Thickness(2, 0, 0, 0),
            Padding = new Thickness(0),
            Background = new SolidColorBrush(Color.FromRgb(0x18, 0x18, 0x1D)),
            Foreground = enabled
                ? new SolidColorBrush(Color.FromRgb(0xED, 0xED, 0xF0))
                : new SolidColorBrush(Color.FromRgb(0x71, 0x71, 0x7B)),
            BorderThickness = new Thickness(0),
            Cursor = enabled ? new Cursor(StandardCursorType.Hand) : new Cursor(StandardCursorType.Arrow),
            Focusable = false,
            IsEnabled = enabled,
        };
        ToolTip.SetTip(button, tooltip);
        return button;
    }

    /// <summary>Single mutation seam for the format list: apply, persist (the stored list becomes
    /// the user's truth from the first edit on — see ColorFormatCatalog.EffectiveFormats), and
    /// re-render the rows AND the popover (indices/enabled states baked into the closures above
    /// go stale after any mutation, so the popover is always rebuilt wholesale).</summary>
    private void MutateFormats(System.Action mutation)
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
            Console.Error.WriteLine($"RoeSnip: failed to save color picker settings: {ex.Message}");
        }
    }

    /// <summary>"Add/edit custom color format" dialog (PowerToys parity), built entirely in code
    /// to match the picker's dark chrome: name + template inputs, a live preview against the
    /// picker's current color, and a condensed parameter reference. Modal (ShowDialog) so it
    /// always sits above the (topmost) picker window. Mirrors the WPF app's own nested
    /// ColorPickerWindow.CustomFormatDialog; Enter/Escape are handled locally (Avalonia's Button
    /// has no WPF-style IsDefault/IsCancel) rather than relying on default-button plumbing.</summary>
    private sealed class CustomFormatDialog : Window
    {
        public string FormatName => (_nameBox.Text ?? string.Empty).Trim();
        public string FormatTemplate => _formatBox.Text ?? string.Empty;

        private readonly TextBox _nameBox;
        private readonly TextBox _formatBox;
        private readonly TextBlock _preview;
        private readonly Button _saveButton;
        private readonly byte _r, _g, _b;
        private readonly double? _nits;

        public CustomFormatDialog(string? name, string? format, byte r, byte g, byte b, double? nits)
        {
            _r = r; _g = g; _b = b; _nits = nits;

            Title = name is null ? "Add custom color format" : "Edit custom color format";
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            SizeToContent = SizeToContent.Height;
            Width = 360;
            CanResize = false;
            ShowInTaskbar = false;
            WindowDecorations = WindowDecorations.BorderOnly;
            Background = new SolidColorBrush(Color.FromRgb(0x18, 0x18, 0x1D));
            Foreground = new SolidColorBrush(Color.FromRgb(0xED, 0xED, 0xF0));
            FontSize = 12;
            UseLayoutRounding = true;

            _nameBox = DarkTextBox(name ?? string.Empty);
            _formatBox = DarkTextBox(format ?? "new Color (R = %Re, G = %Gr, B = %Bl)");
            _formatBox.FontFamily = OverlayFonts.Mono;
            _preview = new TextBlock
            {
                FontFamily = OverlayFonts.Mono,
                Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x35)),
                Margin = new Thickness(0, 4, 0, 8),
                TextWrapping = TextWrapping.Wrap,
            };

            var reference = new TextBlock
            {
                FontFamily = OverlayFonts.Mono,
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
                FontWeight = FontWeight.SemiBold,
                BorderThickness = new Thickness(0),
                Cursor = new Cursor(StandardCursorType.Hand),
            };
            _saveButton.Click += (_, _) => Close(true);

            var cancelButton = new Button
            {
                Content = "Cancel",
                Margin = new Thickness(8, 0, 0, 0),
                Padding = new Thickness(14, 4, 14, 4),
                Background = new SolidColorBrush(Color.FromRgb(0x18, 0x18, 0x1D)),
                Foreground = new SolidColorBrush(Color.FromRgb(0xED, 0xED, 0xF0)),
                BorderThickness = new Thickness(0),
                Cursor = new Cursor(StandardCursorType.Hand),
            };
            cancelButton.Click += (_, _) => Close(false);

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

            // Enter saves (when the current template is valid), Escape cancels — the local
            // equivalent of WPF's IsDefault/IsCancel button flags, which Avalonia's Button has no
            // direct analog for.
            AddHandler(KeyDownEvent, (_, e) =>
            {
                if (e.Key == Key.Enter && _saveButton.IsEnabled)
                {
                    Close(true);
                    e.Handled = true;
                }
                else if (e.Key == Key.Escape)
                {
                    Close(false);
                    e.Handled = true;
                }
            }, Avalonia.Interactivity.RoutingStrategies.Tunnel);

            _nameBox.TextChanged += (_, _) => RefreshState();
            _formatBox.TextChanged += (_, _) => RefreshState();
            RefreshState();
        }

        private void RefreshState()
        {
            _preview.Text = ColorFormatTemplate.Format(_formatBox.Text ?? string.Empty, _r, _g, _b, _nits);
            _saveButton.IsEnabled = FormatName.Length > 0 && (_formatBox.Text ?? string.Empty).Length > 0;
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
