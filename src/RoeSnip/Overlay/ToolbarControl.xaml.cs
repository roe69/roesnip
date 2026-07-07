using System;
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

    private readonly ToggleButton[] _toolButtons;
    private ToggleButton? _selectedColorSwatch;
    private ToggleButton? _selectedStrokeSwatch;

    public event Action<AnnotationTool>? ToolSelected;
    public event Action<Color>? ColorSelected;
    public event Action<double>? StrokeWidthSelected;
    public event Action? UndoClicked;
    public event Action? CopyClicked;
    public event Action? SaveClicked;
    public event Action? SaveHdrClicked;
    public event Action? CancelClicked;

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
        foreach (ToggleButton swatch in ColorSwatchPanel.Children)
        {
            swatch.IsChecked = ReferenceEquals(swatch, clicked);
        }
        _selectedColorSwatch = clicked;
        ColorSelected?.Invoke((Color)clicked.Tag);
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
    }

    private void OnUndoClick(object sender, RoutedEventArgs e) => UndoClicked?.Invoke();
    private void OnCopyClick(object sender, RoutedEventArgs e) => CopyClicked?.Invoke();
    private void OnSaveClick(object sender, RoutedEventArgs e) => SaveClicked?.Invoke();
    private void OnSaveHdrClick(object sender, RoutedEventArgs e) => SaveHdrClicked?.Invoke();
    private void OnCancelClick(object sender, RoutedEventArgs e) => CancelClicked?.Invoke();
}
