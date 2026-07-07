using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;

namespace RoeSnip.App.Overlay;

/// <summary>Pictogram tool buttons (inline vector Path icons — no image assets), color/stroke-width
/// pickers, undo, the three terminal action buttons (Copy / Save / Save HDR), and a Cancel X that
/// always closes the whole overlay. Always attached below-right of the current selection by the
/// owning OverlayWindow (flipping above if it would go off-screen). All buttons are always visible
/// — no hover-only-revealed controls, per the product's UI style requirement — and every button is
/// Focusable=false so keystrokes (Esc/Enter/Ctrl+...) keep reaching the overlay window's handler
/// even right after a toolbar click. Ported from the frozen WPF app's
/// src/RoeSnip/Overlay/ToolbarControl.xaml(.cs).</summary>
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

    private readonly ToggleButton _selectToolButton;
    private readonly ToggleButton[] _toolButtons;
    private readonly StackPanel _colorSwatchPanel;
    private readonly StackPanel _strokeWidthPanel;
    private readonly Button _saveHdrButton;

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
        AvaloniaXamlLoader.Load(this);

        T Find<T>(string name) where T : Control =>
            this.FindControl<T>(name) ?? throw new InvalidOperationException($"ToolbarControl.axaml is missing '{name}'.");

        _selectToolButton = Find<ToggleButton>("SelectToolButton");
        var rectToolButton = Find<ToggleButton>("RectToolButton");
        var ellipseToolButton = Find<ToggleButton>("EllipseToolButton");
        var arrowToolButton = Find<ToggleButton>("ArrowToolButton");
        var lineToolButton = Find<ToggleButton>("LineToolButton");
        var freehandToolButton = Find<ToggleButton>("FreehandToolButton");
        var textToolButton = Find<ToggleButton>("TextToolButton");
        _colorSwatchPanel = Find<StackPanel>("ColorSwatchPanel");
        _strokeWidthPanel = Find<StackPanel>("StrokeWidthPanel");
        _saveHdrButton = Find<Button>("SaveHdrButton");

        _toolButtons = new[]
        {
            _selectToolButton, rectToolButton, ellipseToolButton,
            arrowToolButton, lineToolButton, freehandToolButton, textToolButton,
        };

        _selectToolButton.Click += (_, _) => SelectTool(_selectToolButton, AnnotationTool.None);
        rectToolButton.Click += (_, _) => SelectTool(rectToolButton, AnnotationTool.Rectangle);
        ellipseToolButton.Click += (_, _) => SelectTool(ellipseToolButton, AnnotationTool.Ellipse);
        arrowToolButton.Click += (_, _) => SelectTool(arrowToolButton, AnnotationTool.Arrow);
        lineToolButton.Click += (_, _) => SelectTool(lineToolButton, AnnotationTool.Line);
        freehandToolButton.Click += (_, _) => SelectTool(freehandToolButton, AnnotationTool.Freehand);
        textToolButton.Click += (_, _) => SelectTool(textToolButton, AnnotationTool.Text);

        Find<Button>("UndoButton").Click += (_, _) => UndoClicked?.Invoke();
        Find<Button>("CopyButton").Click += (_, _) => CopyClicked?.Invoke();
        Find<Button>("SaveButton").Click += (_, _) => SaveClicked?.Invoke();
        _saveHdrButton.Click += (_, _) => SaveHdrClicked?.Invoke();
        Find<Button>("CancelButton").Click += (_, _) => CancelClicked?.Invoke();

        BuildColorSwatches();
        BuildStrokeWidthSwatches();
    }

    /// <summary>DESIGN-XPLAT.md: "Save HDR is Windows-only v1 (backend capability flag hides the
    /// button elsewhere)" — the owning OverlayWindow sets this from the composition root's
    /// WriteHdrExport hook being non-null.</summary>
    public void SetSaveHdrVisible(bool visible) => _saveHdrButton.IsVisible = visible;

    private void BuildColorSwatches()
    {
        bool first = true;
        foreach (var (color, name) in PresetColors)
        {
            var swatch = new ToggleButton
            {
                Background = new SolidColorBrush(color),
                Tag = color,
            };
            swatch.Classes.Add("swatch");
            ToolTip.SetTip(swatch, name);
            swatch.Click += OnColorSwatchClick;
            _colorSwatchPanel.Children.Add(swatch);

            if (first)
            {
                first = false;
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
                // Dot diameter scales with stroke width (min ~4 DIPs), same as the WPF version's
                // Tag-driven template ellipse — here the ellipse is plain content instead.
                Content = new Ellipse
                {
                    Fill = Brushes.White,
                    Width = width * 2.0,
                    Height = width * 2.0,
                },
            };
            swatch.Classes.Add("stroke");
            ToolTip.SetTip(swatch, name);
            swatch.Click += OnStrokeWidthSwatchClick;
            _strokeWidthPanel.Children.Add(swatch);

            if (width == StrokeWidths[1].Width) // default to the medium preset
            {
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

    private void OnColorSwatchClick(object? sender, RoutedEventArgs e)
    {
        var clicked = (ToggleButton)sender!;
        foreach (var child in _colorSwatchPanel.Children)
        {
            if (child is ToggleButton swatch)
            {
                swatch.IsChecked = ReferenceEquals(swatch, clicked);
            }
        }
        ColorSelected?.Invoke((Color)clicked.Tag!);
    }

    private void OnStrokeWidthSwatchClick(object? sender, RoutedEventArgs e)
    {
        var clicked = (ToggleButton)sender!;
        foreach (var child in _strokeWidthPanel.Children)
        {
            if (child is ToggleButton swatch)
            {
                swatch.IsChecked = ReferenceEquals(swatch, clicked);
            }
        }

        int index = _strokeWidthPanel.Children.IndexOf(clicked);
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
            button.IsChecked = ReferenceEquals(button, _selectToolButton);
        }
    }
}
