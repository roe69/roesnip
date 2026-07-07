using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace RoeSnip.Overlay;

// RoeSnip.csproj enables both UseWPF and UseWindowsForms, so System.Windows.Forms/System.Drawing
// are in scope alongside System.Windows/System.Windows.Media — alias the colliding names to WPF's.
// (Declared after the namespace line — see AnnotationLayer.cs for why: RoeSnip.Color, a sibling
// WP-A namespace, would otherwise shadow an outer-scope alias for "Color".)
using Color = System.Windows.Media.Color;
using UserControl = System.Windows.Controls.UserControl;

/// <summary>Pictogram tool buttons (inline vector Path icons — no image assets), color/stroke-width
/// pickers, undo, the three terminal action buttons (Copy / Save / Save HDR), and a Cancel X that
/// always closes the whole overlay. Always attached below-right of the current selection by the
/// owning OverlayWindow (flipping above if it would go off-screen). All buttons are always visible
/// — no hover-only-revealed controls, per the product's UI style requirement — and every button is
/// Focusable=false so keystrokes (Esc/Enter/Ctrl+...) keep reaching the overlay window's handler
/// even right after a toolbar click.</summary>
public partial class ToolbarControl : UserControl
{
    private static readonly (Color Color, string Name)[] PresetColors =
    {
        (Color.FromRgb(0xE5, 0x39, 0x35), "Red"),
        (Color.FromRgb(0xFF, 0xB3, 0x00), "Amber"),
        (Color.FromRgb(0x43, 0xA0, 0x47), "Green"),
        (Color.FromRgb(0x1E, 0x88, 0xE5), "Blue"),
        (Color.FromRgb(0xFF, 0xFF, 0xFF), "White"),
        (Color.FromRgb(0x21, 0x21, 0x21), "Black"),
    };

    private static readonly (double Width, string Name)[] StrokeWidths =
    {
        (2.0, "Thin (2 px)"),
        (4.0, "Medium (4 px)"),
        (8.0, "Thick (8 px)"),
    };

    /// <summary>The fixed font-family choices for the text-style group (item 4) — a small,
    /// universally-installed set rather than an open-ended system font enumeration.</summary>
    private static readonly string[] FontFamilyChoices =
    {
        "Segoe UI", "Arial", "Calibri", "Consolas", "Times New Roman", "Comic Sans MS",
    };

    private const double MinFontSize = 6.0;
    private const double MaxFontSize = 96.0;

    private readonly ToggleButton[] _toolButtons;
    private ToggleButton? _selectedColorSwatch;
    private ToggleButton? _selectedStrokeSwatch;
    private double _fontSize = 20.0;
    private bool _suppressFontFamilyEvent;

    public event Action<AnnotationTool>? ToolSelected;
    public event Action<Color>? ColorSelected;
    public event Action<double>? StrokeWidthSelected;
    public event Action? UndoClicked;
    public event Action? CopyClicked;
    public event Action? SaveClicked;
    public event Action? SaveHdrClicked;
    public event Action? CancelClicked;

    /// <summary>Text-style group (item 4) — font size (scroll target + click steppers), Bold,
    /// Italic, font family. Only ever visible while the Text tool is selected.</summary>
    public event Action<double>? FontSizeSelected;
    public event Action<bool>? BoldToggled;
    public event Action<bool>? ItalicToggled;
    public event Action<string>? FontFamilySelected;

    /// <summary>The "+" custom-color swatch (item 5) — OverlayWindow owns the actual
    /// System.Windows.Forms.ColorDialog (it needs the window's HWND as dialog owner).</summary>
    public event Action? CustomColorRequested;

    public ToolbarControl()
    {
        InitializeComponent();

        _toolButtons = new[]
        {
            SelectToolButton, RectToolButton, EllipseToolButton,
            ArrowToolButton, LineToolButton, FreehandToolButton, TextToolButton,
        };

        BuildColorSwatches();
        BuildStrokeWidthSwatches();
        BuildFontFamilyChoices();
    }

    private void BuildColorSwatches()
    {
        foreach (var (color, name) in PresetColors)
        {
            var swatch = new ToggleButton
            {
                Style = (Style)Resources["ColorSwatchStyle"],
                Background = new SolidColorBrush(color),
                Tag = color,
                ToolTip = name,
            };
            swatch.Click += OnColorSwatchClick;
            ColorSwatchPanel.Children.Add(swatch);

            if (_selectedColorSwatch is null)
            {
                _selectedColorSwatch = swatch;
                swatch.IsChecked = true;
            }
        }
    }

    private void BuildStrokeWidthSwatches()
    {
        foreach (var (width, name) in StrokeWidths)
        {
            var swatch = new ToggleButton
            {
                Style = (Style)Resources["StrokeWidthSwatchStyle"],
                Tag = width * 2.0, // dot diameter scales with stroke width, min ~4 DIPs
                ToolTip = name,
            };
            swatch.Click += OnStrokeWidthSwatchClick;
            StrokeWidthPanel.Children.Add(swatch);

            if (width == StrokeWidths[1].Width) // default to the medium preset
            {
                _selectedStrokeSwatch = swatch;
                swatch.IsChecked = true;
            }
        }
    }

    private void SelectTool(ToggleButton selected, AnnotationTool tool)
    {
        foreach (var button in _toolButtons)
        {
            button.IsChecked = ReferenceEquals(button, selected);
        }

        // The text-style group is always visible while the Text tool is active — no hover-hiding —
        // and collapsed for every other tool.
        TextStylePanel.Visibility = tool == AnnotationTool.Text ? Visibility.Visible : Visibility.Collapsed;

        ToolSelected?.Invoke(tool);
    }

    private void OnSelectToolClick(object sender, RoutedEventArgs e) => SelectTool(SelectToolButton, AnnotationTool.None);
    private void OnRectToolClick(object sender, RoutedEventArgs e) => SelectTool(RectToolButton, AnnotationTool.Rectangle);
    private void OnEllipseToolClick(object sender, RoutedEventArgs e) => SelectTool(EllipseToolButton, AnnotationTool.Ellipse);
    private void OnArrowToolClick(object sender, RoutedEventArgs e) => SelectTool(ArrowToolButton, AnnotationTool.Arrow);
    private void OnLineToolClick(object sender, RoutedEventArgs e) => SelectTool(LineToolButton, AnnotationTool.Line);
    private void OnFreehandToolClick(object sender, RoutedEventArgs e) => SelectTool(FreehandToolButton, AnnotationTool.Freehand);
    private void OnTextToolClick(object sender, RoutedEventArgs e) => SelectTool(TextToolButton, AnnotationTool.Text);

    private void OnColorSwatchClick(object sender, RoutedEventArgs e)
    {
        var clicked = (ToggleButton)sender;
        SelectColorSwatch(clicked);
        ColorSelected?.Invoke((Color)clicked.Tag);
    }

    /// <summary>Shared by built-in and custom-color swatches (item 5) — they form a single
    /// selection group spanning both panels, so picking one always un-checks every other swatch
    /// regardless of which panel it lives in.</summary>
    private void SelectColorSwatch(ToggleButton clicked)
    {
        foreach (ToggleButton swatch in ColorSwatchPanel.Children)
        {
            swatch.IsChecked = ReferenceEquals(swatch, clicked);
        }
        foreach (ToggleButton swatch in CustomColorPanel.Children)
        {
            swatch.IsChecked = ReferenceEquals(swatch, clicked);
        }
        _selectedColorSwatch = clicked;
    }

    private void OnStrokeWidthSwatchClick(object sender, RoutedEventArgs e)
    {
        var clicked = (ToggleButton)sender;
        foreach (ToggleButton swatch in StrokeWidthPanel.Children)
        {
            swatch.IsChecked = ReferenceEquals(swatch, clicked);
        }
        _selectedStrokeSwatch = clicked;

        int index = StrokeWidthPanel.Children.IndexOf(clicked);
        double width = index >= 0 && index < StrokeWidths.Length ? StrokeWidths[index].Width : StrokeWidths[1].Width;
        SetStrokeWidth(width);
        StrokeWidthSelected?.Invoke(width);
    }

    /// <summary>Re-checks the default Select tool without raising ToolSelected — called by the
    /// owning OverlayWindow when the toolbar is hidden (selection cleared), which also resets its
    /// own current tool to None; this keeps the visible checked state from lying on re-show.</summary>
    public void ResetToolSelection()
    {
        foreach (var button in _toolButtons)
        {
            button.IsChecked = ReferenceEquals(button, SelectToolButton);
        }
        TextStylePanel.Visibility = Visibility.Collapsed;
    }

    // ---------- Scroll-wheel sizing support (item 3) — the numeric displays this updates ----------

    /// <summary>Live-updates the stroke-width label from a scroll-wheel change (OverlayWindow calls
    /// this; it does not itself raise StrokeWidthSelected — the wheel handler already knows the new
    /// width and drives OverlayWindow's own state directly). Also called internally when a preset
    /// swatch is clicked, so the label always reflects the true current width.</summary>
    public void SetStrokeWidth(double width)
    {
        StrokeWidthLabel.Text = string.Create(CultureInfo.InvariantCulture, $"{width:0}px");

        // Reflect an exact preset match in the swatch row; otherwise (a scroll-wheel value that
        // doesn't land on a preset) leave none of them checked rather than lying about which one
        // is "selected".
        ToggleButton? matching = null;
        for (int i = 0; i < StrokeWidths.Length && i < StrokeWidthPanel.Children.Count; i++)
        {
            if (Math.Abs(StrokeWidths[i].Width - width) < 0.01)
            {
                matching = (ToggleButton)StrokeWidthPanel.Children[i];
                break;
            }
        }
        foreach (ToggleButton swatch in StrokeWidthPanel.Children)
        {
            swatch.IsChecked = ReferenceEquals(swatch, matching);
        }
        _selectedStrokeSwatch = matching;
    }

    // ---------- Text-style group (item 4) ----------

    private void BuildFontFamilyChoices()
    {
        foreach (var family in FontFamilyChoices)
        {
            FontFamilyComboBox.Items.Add(family);
        }
        FontFamilyComboBox.SelectedIndex = 0;
    }

    /// <summary>Called by OverlayWindow whenever the toolbar is (re)shown, so the displayed style
    /// always matches the current/last-used text style rather than whatever this reused toolbar
    /// instance happened to show last.</summary>
    public void InitializeTextStyle(string fontFamily, double fontSize, bool bold, bool italic)
    {
        SetFontSize(fontSize);

        _suppressFontFamilyEvent = true;
        try
        {
            int index = Array.IndexOf(FontFamilyChoices, fontFamily);
            FontFamilyComboBox.SelectedIndex = index >= 0 ? index : 0;
        }
        finally
        {
            _suppressFontFamilyEvent = false;
        }

        BoldToggleButton.IsChecked = bold;
        ItalicToggleButton.IsChecked = italic;
    }

    /// <summary>Live-updates the font-size label from a scroll-wheel change — mirrors
    /// <see cref="SetStrokeWidth"/>'s role for the stroke-width label.</summary>
    public void SetFontSize(double size)
    {
        _fontSize = Math.Clamp(size, MinFontSize, MaxFontSize);
        FontSizeLabel.Text = string.Create(CultureInfo.InvariantCulture, $"{_fontSize:0}pt");
    }

    private void OnFontSizeDownClick(object sender, RoutedEventArgs e) => StepFontSize(-2.0);

    private void OnFontSizeUpClick(object sender, RoutedEventArgs e) => StepFontSize(2.0);

    private void StepFontSize(double delta)
    {
        double newSize = Math.Clamp(_fontSize + delta, MinFontSize, MaxFontSize);
        SetFontSize(newSize);
        FontSizeSelected?.Invoke(newSize);
    }

    private void OnBoldToggleClick(object sender, RoutedEventArgs e) => BoldToggled?.Invoke(BoldToggleButton.IsChecked == true);

    private void OnItalicToggleClick(object sender, RoutedEventArgs e) => ItalicToggled?.Invoke(ItalicToggleButton.IsChecked == true);

    private void OnFontFamilyChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressFontFamilyEvent)
        {
            return;
        }
        if (FontFamilyComboBox.SelectedItem is string family)
        {
            FontFamilySelected?.Invoke(family);
        }
    }

    // ---------- Custom colors (item 5) ----------

    private void OnAddCustomColorClick(object sender, RoutedEventArgs e)
    {
        // This is a plain trigger button, not a real toggle state — it never stays "checked".
        AddCustomColorButton.IsChecked = false;
        CustomColorRequested?.Invoke();
    }

    /// <summary>Rebuilds the custom-color swatch row from the persisted palette (newest first,
    /// capped at 8 by BoundedColorList) — called by OverlayWindow after every settings change and
    /// once when the toolbar is (re)shown so a reused toolbar instance always reflects the current
    /// palette.</summary>
    public void SetCustomColors(IReadOnlyList<string> hexColors)
    {
        CustomColorPanel.Children.Clear();

        foreach (var hex in hexColors)
        {
            if (System.Windows.Media.ColorConverter.ConvertFromString(hex) is not Color color)
            {
                continue;
            }

            var swatch = new ToggleButton
            {
                Style = (Style)Resources["ColorSwatchStyle"],
                Background = new SolidColorBrush(color),
                Tag = color,
                ToolTip = hex,
            };
            swatch.Click += OnColorSwatchClick;
            CustomColorPanel.Children.Add(swatch);
        }
    }

    private void OnUndoClick(object sender, RoutedEventArgs e) => UndoClicked?.Invoke();
    private void OnCopyClick(object sender, RoutedEventArgs e) => CopyClicked?.Invoke();
    private void OnSaveClick(object sender, RoutedEventArgs e) => SaveClicked?.Invoke();
    private void OnSaveHdrClick(object sender, RoutedEventArgs e) => SaveHdrClicked?.Invoke();
    private void OnCancelClick(object sender, RoutedEventArgs e) => CancelClicked?.Invoke();
}
