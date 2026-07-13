using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace RoeSnip.App.Overlay;

/// <summary>The toolbar palette's right-click "Replace..." picker. WPF's own version
/// (OverlayWindow.xaml.cs TryPickColorFromDialog) opens a native System.Windows.Forms.ColorDialog,
/// which has no Avalonia/cross-platform equivalent; per PARITY.md item 08, the closest in-toolkit
/// substitute is this small owned Flyout anchored to the swatch that was right-clicked: a quick-pick
/// grid of the same ten <see cref="SwatchPalette.DefaultColors"/> plus a typed "#RRGGBB" hex box.
/// Framework-bound (Flyout/TextBox), unlike SwatchPalette's own pure list logic, so it deliberately
/// stays out of the unit-testable surface.</summary>
internal static class ColorReplaceFlyout
{
    public static void Show(Control target, Color initial, Action<Color> onPicked)
    {
        Flyout? flyout = null;

        void Apply(Color color)
        {
            onPicked(color);
            flyout?.Hide();
        }

        var grid = new WrapPanel { Width = 176 };
        foreach (var hex in SwatchPalette.DefaultColors)
        {
            if (!Color.TryParse(hex, out var color))
            {
                continue;
            }
            var swatch = new Button
            {
                Width = 24,
                Height = 24,
                Margin = new Thickness(2),
                Padding = new Thickness(0),
                Background = new SolidColorBrush(color),
                BorderBrush = Brushes.Gray,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Focusable = false,
                [ToolTip.TipProperty] = SwatchPalette.NameFor(hex),
            };
            swatch.Click += (_, _) => Apply(color);
            grid.Children.Add(swatch);
        }

        var hexBox = new TextBox
        {
            Width = 176,
            Text = FormatHex(initial),
            PlaceholderText = "#RRGGBB",
        };

        void CommitHex()
        {
            string text = (hexBox.Text ?? string.Empty).Trim();
            if (text.Length > 0 && text[0] != '#')
            {
                text = "#" + text;
            }
            if (Color.TryParse(text, out var parsed))
            {
                Apply(parsed);
            }
        }
        hexBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                CommitHex();
                e.Handled = true;
            }
        };

        var applyButton = new Button { Content = "Apply", HorizontalAlignment = HorizontalAlignment.Right };
        applyButton.Click += (_, _) => CommitHex();

        var panel = new StackPanel
        {
            Spacing = 8,
            Margin = new Thickness(10),
            Children = { grid, hexBox, applyButton },
        };

        flyout = new Flyout { Content = panel, Placement = PlacementMode.Bottom };
        flyout.ShowAt(target);
    }

    private static string FormatHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
}
