using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace RoeSnip.App.Overlay;

/// <summary>Pictogram tool buttons (inline vector Path icons — no image assets), the editable
/// swatch palette (right-click "Replace..." per swatch — item 08), the numeric size input (item
/// 08 — one editable px/pt ComboBox instead of the old fixed stroke dots), undo/redo, the text-style
/// row (Bold/Italic + font family, item 08), and the terminal action buttons (Copy / Save / Save
/// HDR / Cancel). Always attached below-right of the current selection by the owning OverlayWindow
/// (flipping above if it would go off-screen). All buttons are always visible — no hover-only-
/// revealed controls, per the product's UI style requirement — and every control except the size
/// ComboBox's editable text box is Focusable=false so keystrokes (Esc/Enter/Ctrl+...) keep reaching
/// the overlay window's handler even right after a toolbar click. Ported from the frozen WPF app's
/// src/RoeSnip/Overlay/ToolbarControl.xaml(.cs) and src/RoeSnip/Overlay/SwatchPalette.cs /
/// SizeInput.cs.</summary>
public partial class ToolbarControl : UserControl
{
    /// <summary>Every installed system font family, sorted by display name — enumerated once per
    /// process (Lazy) since the installed set doesn't change under a running session. Replaces the
    /// old fixed six-font list. Avalonia's FontFamily.Name is already the best cross-platform
    /// display name (no XmlLanguage/FamilyNames lookup needed — that was a WPF-specific
    /// localization detail; see FontFamilyChoices in the WPF version for the fuller original).</summary>
    private static readonly Lazy<string[]> FontFamilyChoices = new(() =>
    {
        var names = new SortedSet<string>(StringComparer.CurrentCultureIgnoreCase);
        try
        {
            foreach (var family in FontManager.Current.SystemFonts)
            {
                if (!string.IsNullOrWhiteSpace(family.Name))
                {
                    names.Add(family.Name);
                }
            }
        }
        catch (Exception)
        {
            // A corrupt font registration must never take the whole toolbar down.
        }
        return names.Count > 0 ? names.ToArray() : new[] { "Segoe UI" };
    });

    private readonly ToggleButton _selectToolButton;
    private readonly ToggleButton[] _toolButtons;
    private readonly StackPanel _colorSwatchPanel;
    private readonly StackPanel _toolsGroupPanel;
    private readonly StackPanel _historyGroupPanel;
    private readonly Button _saveHdrButton;
    private readonly Button _undoButton;
    private readonly Button _redoButton;
    private readonly ComboBox _sizeComboBox;
    private readonly StackPanel _textStylePanel;
    private readonly ToggleButton _boldToggleButton;
    private readonly ToggleButton _italicToggleButton;
    private readonly ComboBox _fontFamilyComboBox;

    private ToggleButton? _selectedColorSwatch;
    private AnnotationTool _activeTool = AnnotationTool.None;

    // Size ComboBox state (item 08): whether it currently edits the font size (Text tool, "pt") or
    // the stroke width (every other tool, "px"), plus a guard so programmatic repopulation/text
    // writes never re-raise the events they themselves were driven by.
    private double _strokeWidth = 4.0;
    private double _fontSize = 20.0;
    private bool _sizeModeIsFont;
    private bool _sizeComboInitialized;
    private bool _suppressSizeEvents;
    private bool _suppressFontFamilyEvent;

    public event Action<AnnotationTool>? ToolSelected;
    public event Action<Color>? ColorSelected;
    public event Action<double>? StrokeWidthSelected;
    public event Action? UndoClicked;
    public event Action? RedoClicked;
    public event Action? CopyClicked;
    public event Action? SaveClicked;
    public event Action? SaveHdrClicked;
    public event Action? CancelClicked;

    /// <summary>Text-style group (item 08) — Bold, Italic, font family. Only ever visible while the
    /// Text tool is selected. Font size is edited via the shared size ComboBox (pt mode).</summary>
    public event Action<double>? FontSizeSelected;
    public event Action<bool>? BoldToggled;
    public event Action<bool>? ItalicToggled;
    public event Action<string>? FontFamilySelected;

    /// <summary>Right-click palette editing (item 08): raised once the swatch's own
    /// ColorReplaceFlyout (shown by this control — see BuildSwatchContextMenu) has a picked color.
    /// The first argument is the swatch's index into the palette list last passed to
    /// <see cref="SetPaletteColors"/>. OverlayWindow owns the persistence and calls
    /// SetPaletteColors again. Deviates from WPF's index-only PaletteReplaceRequested (WPF's
    /// System.Windows.Forms.ColorDialog is a blocking modal OverlayWindow itself invokes; Avalonia's
    /// Flyout must be anchored to the swatch Control, which only this control holds — see
    /// ColorReplaceFlyout's own doc comment).</summary>
    public event Action<int, Color>? PaletteReplaceRequested;

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
        var highlightToolButton = Find<ToggleButton>("HighlightToolButton");
        var pixelateToolButton = Find<ToggleButton>("PixelateToolButton");
        var textToolButton = Find<ToggleButton>("TextToolButton");
        _colorSwatchPanel = Find<StackPanel>("ColorSwatchPanel");
        _toolsGroupPanel = Find<StackPanel>("ToolsGroupPanel");
        _historyGroupPanel = Find<StackPanel>("HistoryGroupPanel");
        _saveHdrButton = Find<Button>("SaveHdrButton");
        _undoButton = Find<Button>("UndoButton");
        _redoButton = Find<Button>("RedoButton");
        _sizeComboBox = Find<ComboBox>("SizeComboBox");
        _textStylePanel = Find<StackPanel>("TextStylePanel");
        _boldToggleButton = Find<ToggleButton>("BoldToggleButton");
        _italicToggleButton = Find<ToggleButton>("ItalicToggleButton");
        _fontFamilyComboBox = Find<ComboBox>("FontFamilyComboBox");

        _toolButtons = new[]
        {
            _selectToolButton, rectToolButton, ellipseToolButton, arrowToolButton, lineToolButton,
            freehandToolButton, highlightToolButton, pixelateToolButton, textToolButton,
        };

        _selectToolButton.Click += (_, _) => SelectTool(_selectToolButton, AnnotationTool.None);
        rectToolButton.Click += (_, _) => SelectTool(rectToolButton, AnnotationTool.Rectangle);
        ellipseToolButton.Click += (_, _) => SelectTool(ellipseToolButton, AnnotationTool.Ellipse);
        arrowToolButton.Click += (_, _) => SelectTool(arrowToolButton, AnnotationTool.Arrow);
        lineToolButton.Click += (_, _) => SelectTool(lineToolButton, AnnotationTool.Line);
        freehandToolButton.Click += (_, _) => SelectTool(freehandToolButton, AnnotationTool.Freehand);
        highlightToolButton.Click += (_, _) => SelectTool(highlightToolButton, AnnotationTool.Highlight);
        pixelateToolButton.Click += (_, _) => SelectTool(pixelateToolButton, AnnotationTool.Pixelate);
        textToolButton.Click += (_, _) => SelectTool(textToolButton, AnnotationTool.Text);

        _undoButton.Click += (_, _) => UndoClicked?.Invoke();
        _redoButton.Click += (_, _) => RedoClicked?.Invoke();
        Find<Button>("CopyButton").Click += (_, _) => CopyClicked?.Invoke();
        Find<Button>("SaveButton").Click += (_, _) => SaveClicked?.Invoke();
        _saveHdrButton.Click += (_, _) => SaveHdrClicked?.Invoke();
        Find<Button>("CancelButton").Click += (_, _) => CancelClicked?.Invoke();

        // Commit whatever was typed whenever keyboard focus leaves the size box entirely (clicking
        // a tool, clicking the image, Enter's own focus hand-back) — never leave a typed-but-
        // uncommitted value silently reverting later. Mirrors WPF's IsKeyboardFocusWithinChanged
        // wiring (SizeComboBox.IsKeyboardFocusWithinChanged, ToolbarControl.xaml.cs:141-147) —
        // Avalonia's InputElement.IsKeyboardFocusWithin is the direct analog.
        _sizeComboBox.PropertyChanged += (_, e) =>
        {
            if (e.Property == InputElement.IsKeyboardFocusWithinProperty && e.NewValue is false)
            {
                CommitSizeText();
            }
        };
        _sizeComboBox.KeyDown += OnSizeComboKeyDown;
        _sizeComboBox.SelectionChanged += OnSizeComboSelectionChanged;

        _boldToggleButton.Click += OnBoldToggleClick;
        _italicToggleButton.Click += OnItalicToggleClick;
        _fontFamilyComboBox.SelectionChanged += OnFontFamilyChanged;

        BuildFontFamilyChoices();
        RefreshSizeCombo();
        _sizeComboInitialized = true;
    }

    /// <summary>DESIGN-XPLAT.md: "Save HDR is Windows-only v1 (backend capability flag hides the
    /// button elsewhere)" — the owning OverlayWindow sets this from the composition root's
    /// WriteHdrExport hook being non-null.</summary>
    public void SetSaveHdrVisible(bool visible) => _saveHdrButton.IsVisible = visible;

    /// <summary>Cross-monitor selection (item 09): while the current selection spans multiple
    /// monitors, annotations stay off (see docs/DESIGN-MULTIMON-SELECTION.md) — collapses the
    /// annotation-tool groups (tools, undo/redo, palette) down to Copy/Save/Save HDR/Cancel, which
    /// stay visible and functional exactly as for an ordinary selection (RenderSpanningSelection/
    /// JxrWriter.WriteSpanning already produce the byte composite those buttons act on). Called by
    /// OverlayWindow.ShowToolbar every time the toolbar is shown, so it can never go stale across a
    /// spanning/non-spanning transition on the same reused instance. Mirrors the WPF app's
    /// ToolbarControl.SetSpanningMode.</summary>
    public void SetSpanningMode(bool spanning)
    {
        bool visible = !spanning;
        _toolsGroupPanel.IsVisible = visible;
        _historyGroupPanel.IsVisible = visible;
        _colorSwatchPanel.IsVisible = visible;
    }

    // ---------- Editable swatch palette (item 08) ----------

    /// <summary>Rebuilds the swatch row from the persisted palette (RoeSnipSettings.PaletteColors,
    /// migrated/capped by SwatchPalette) — called by OverlayWindow when the toolbar is (re)shown
    /// and after every palette edit. The swatch matching <paramref name="selectedColor"/> is
    /// checked (falling back to the first swatch, without raising ColorSelected — OverlayWindow
    /// keeps its current color aligned with the palette before calling this). Mirrors WPF's
    /// SetPaletteColors (ToolbarControl.xaml.cs:208-257).</summary>
    public void SetPaletteColors(IReadOnlyList<string> hexColors, Color selectedColor)
    {
        _colorSwatchPanel.Children.Clear();
        _selectedColorSwatch = null;

        string selectedHex = FormatHex(selectedColor);

        for (int i = 0; i < hexColors.Count; i++)
        {
            string hex = hexColors[i];
            if (!Color.TryParse(hex, out var color))
            {
                continue; // a hand-edited settings file can hold garbage; skip, never crash
            }

            var swatch = new ToggleButton
            {
                Background = new SolidColorBrush(color),
                Tag = color,
            };
            swatch.Classes.Add("swatch");
            ToolTip.SetTip(swatch, SwatchPalette.NameFor(hex));
            swatch.ContextMenu = BuildSwatchContextMenu(i, swatch, color);
            swatch.Click += OnColorSwatchClick;
            _colorSwatchPanel.Children.Add(swatch);

            if (_selectedColorSwatch is null && string.Equals(hex, selectedHex, StringComparison.OrdinalIgnoreCase))
            {
                _selectedColorSwatch = swatch;
                swatch.IsChecked = true;
            }
        }

        // Never show a palette with nothing checked: fall back to the first swatch visually
        // (no ColorSelected raise — display-only).
        if (_selectedColorSwatch is null && _colorSwatchPanel.Children.Count > 0)
        {
            _selectedColorSwatch = (ToggleButton)_colorSwatchPanel.Children[0];
            _selectedColorSwatch.IsChecked = true;
        }
    }

    /// <summary>Per-swatch right-click menu: "Replace..." only (a swatch is a recolorable slot,
    /// not a removable row). Index and color are captured per swatch — the whole row is rebuilt on
    /// every palette change, so they can never go stale. Mirrors WPF's BuildSwatchContextMenu
    /// (ToolbarControl.xaml.cs:262-274), swapping WPF's ColorDialog for ColorReplaceFlyout since
    /// this control (not OverlayWindow) is the one holding the swatch Control the flyout anchors
    /// to.</summary>
    private ContextMenu BuildSwatchContextMenu(int paletteIndex, ToggleButton swatch, Color currentColor)
    {
        var replaceItem = new MenuItem { Header = "Replace..." };
        replaceItem.Click += (_, _) =>
            ColorReplaceFlyout.Show(swatch, currentColor, picked => PaletteReplaceRequested?.Invoke(paletteIndex, picked));

        var menu = new ContextMenu();
        menu.Items.Add(replaceItem);
        return menu;
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
        _selectedColorSwatch = clicked;
        ColorSelected?.Invoke((Color)clicked.Tag!);
    }

    private static string FormatHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    // ---------- Tool selection ----------

    private void SelectTool(ToggleButton selected, AnnotationTool tool)
    {
        // Clicking the tool that is ALREADY active toggles back to the Select tool — a quick
        // "put the tool away" gesture. The Select tool itself does not toggle to anything.
        if (tool != AnnotationTool.None && tool == _activeTool)
        {
            selected = _selectToolButton;
            tool = AnnotationTool.None;
        }
        _activeTool = tool;

        foreach (var button in _toolButtons)
        {
            button.IsChecked = ReferenceEquals(button, selected);
        }

        // The text-style group is always visible while the Text tool is active — no hover-hiding —
        // and collapsed for every other tool.
        _textStylePanel.IsVisible = tool == AnnotationTool.Text;

        // The size box follows the tool: pt (font size) for Text, px (stroke width) otherwise.
        SetSizeMode(isFont: tool == AnnotationTool.Text);

        ToolSelected?.Invoke(tool);
    }

    /// <summary>Re-checks the default Select tool without raising ToolSelected — called by the
    /// owning OverlayWindow when the toolbar is hidden (selection cleared), which also resets its
    /// own current tool to None; this keeps the visible checked state from lying on re-show.</summary>
    public void ResetToolSelection()
    {
        _activeTool = AnnotationTool.None;
        foreach (var button in _toolButtons)
        {
            button.IsChecked = ReferenceEquals(button, _selectToolButton);
        }
        _textStylePanel.IsVisible = false;
        SetSizeMode(isFont: false);
    }

    // ---------- Numeric size input (item 08) ----------

    /// <summary>Live-updates the size box from a scroll-wheel change (OverlayWindow calls this; it
    /// does not itself raise StrokeWidthSelected — the wheel handler already knows the new width
    /// and drives OverlayWindow's own state directly). Also the canonical-text refresh after a
    /// typed commit.</summary>
    public void SetStrokeWidth(double width)
    {
        _strokeWidth = SizeInput.ClampStroke(width);
        if (!_sizeModeIsFont)
        {
            SetSizeComboTextSuppressed(SizeInput.FormatPx(_strokeWidth));
        }
    }

    /// <summary>Live-updates the size box from a scroll-wheel font resize — mirrors
    /// <see cref="SetStrokeWidth"/>'s role for the Text tool's pt mode.</summary>
    public void SetFontSize(double size)
    {
        _fontSize = SizeInput.ClampFont(size);
        if (_sizeModeIsFont)
        {
            SetSizeComboTextSuppressed(SizeInput.FormatPt(_fontSize));
        }
    }

    private void SetSizeMode(bool isFont)
    {
        if (_sizeModeIsFont == isFont && _sizeComboInitialized)
        {
            return;
        }
        _sizeModeIsFont = isFont;
        RefreshSizeCombo();
    }

    /// <summary>Repopulates the dropdown presets and the displayed text for the current mode
    /// (px: SizeInput.StrokePresetsPx / pt: SizeInput.FontPresetsPt), suppressing the selection/
    /// text events the repopulation itself fires.</summary>
    private void RefreshSizeCombo()
    {
        _suppressSizeEvents = true;
        try
        {
            var presets = _sizeModeIsFont ? SizeInput.FontPresetsPt : SizeInput.StrokePresetsPx;
            _sizeComboBox.ItemsSource = presets
                .Select(p => _sizeModeIsFont ? SizeInput.FormatPt(p) : SizeInput.FormatPx(p))
                .ToList();
            _sizeComboBox.Text = _sizeModeIsFont ? SizeInput.FormatPt(_fontSize) : SizeInput.FormatPx(_strokeWidth);
        }
        finally
        {
            _suppressSizeEvents = false;
        }
    }

    private void SetSizeComboTextSuppressed(string text)
    {
        _suppressSizeEvents = true;
        try
        {
            _sizeComboBox.Text = text;
        }
        finally
        {
            _suppressSizeEvents = false;
        }
    }

    /// <summary>Parses/clamps whatever is in the size box and applies it: updates the tool state
    /// via StrokeWidthSelected/FontSizeSelected (OverlayWindow updates the brush + cursor
    /// immediately) and rewrites the box to the canonical "Npx"/"Npt" form. Unparseable text
    /// reverts to the current value. Idempotent — safe to call from both Enter and focus-loss.</summary>
    private void CommitSizeText()
    {
        if (_suppressSizeEvents)
        {
            return;
        }

        if (!SizeInput.TryParse(_sizeComboBox.Text, out double raw))
        {
            SetSizeComboTextSuppressed(_sizeModeIsFont ? SizeInput.FormatPt(_fontSize) : SizeInput.FormatPx(_strokeWidth));
            return;
        }

        if (_sizeModeIsFont)
        {
            double value = SizeInput.ClampFont(raw);
            bool changed = Math.Abs(value - _fontSize) > 0.001;
            SetFontSize(value);
            if (changed)
            {
                FontSizeSelected?.Invoke(value);
            }
        }
        else
        {
            double value = SizeInput.ClampStroke(raw);
            bool changed = Math.Abs(value - _strokeWidth) > 0.001;
            SetStrokeWidth(value);
            if (changed)
            {
                StrokeWidthSelected?.Invoke(value);
            }
        }
    }

    /// <summary>Enter commits and hands keyboard focus back to the overlay window (so the NEXT
    /// Enter confirms the snip — this one never does). Esc reverts and hands focus back likewise,
    /// deliberately consuming it so it can't double as "cancel the snip" mid-typing.</summary>
    private void OnSizeComboKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitSizeText();
            _sizeComboBox.IsDropDownOpen = false;
            ReturnFocusToOverlayWindow();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            SetSizeComboTextSuppressed(_sizeModeIsFont ? SizeInput.FormatPt(_fontSize) : SizeInput.FormatPx(_strokeWidth));
            _sizeComboBox.IsDropDownOpen = false;
            ReturnFocusToOverlayWindow();
            e.Handled = true;
        }
    }

    private void OnSizeComboSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressSizeEvents || _sizeComboBox.SelectedItem is not string presetText)
        {
            return;
        }

        // Apply the preset directly (the editable Text may not have synced yet at this point),
        // then hand focus back — picking from the dropdown is a complete gesture.
        SetSizeComboTextSuppressed(presetText);
        CommitSizeText();
        ReturnFocusToOverlayWindow();
    }

    private void ReturnFocusToOverlayWindow()
    {
        if (TopLevel.GetTopLevel(this) is Window window)
        {
            window.Focus();
        }
    }

    /// <summary>True when the given event source sits inside the size ComboBox — OverlayWindow uses
    /// this to decide whether a toolbar click should yank keyboard focus back to the window (any
    /// toolbar click that is NOT into the size box should, since every other control is
    /// Focusable=false and would otherwise leave the box focused forever). A plain visual-only walk
    /// — mirrors WPF's own IsWithinSizeInput (ToolbarControl.xaml.cs:466-478), which likewise never
    /// bothers with the logical-parent popup bridge OverlayWindow.IsWithin needs: a click that lands
    /// inside the size box's OWN open dropdown already gets focus handed back by
    /// OnSizeComboSelectionChanged above, so under-reporting there is harmless.</summary>
    internal bool IsWithinSizeInput(object? source)
    {
        if (source is not Visual visual)
        {
            return false;
        }
        Visual? current = visual;
        while (current is not null)
        {
            if (ReferenceEquals(current, _sizeComboBox))
            {
                return true;
            }
            current = current.GetVisualParent();
        }
        return false;
    }

    // ---------- Text-style group (item 08) ----------

    private void BuildFontFamilyChoices()
    {
        _fontFamilyComboBox.ItemsSource = FontFamilyChoices.Value;
        int segoe = IndexOfFontFamily("Segoe UI");
        _fontFamilyComboBox.SelectedIndex = segoe >= 0 ? segoe : (FontFamilyChoices.Value.Length > 0 ? 0 : -1);
    }

    /// <summary>Case-insensitive lookup so a persisted name round-trips regardless of casing;
    /// -1 when the font isn't installed (anymore).</summary>
    private static int IndexOfFontFamily(string fontFamily)
    {
        var choices = FontFamilyChoices.Value;
        for (int i = 0; i < choices.Length; i++)
        {
            if (string.Equals(choices[i], fontFamily, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }
        return -1;
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
            // A persisted font that's since been uninstalled falls back to Segoe UI (then to the
            // list head as a last resort) rather than silently selecting an unrelated first entry.
            int index = IndexOfFontFamily(fontFamily);
            if (index < 0)
            {
                index = IndexOfFontFamily("Segoe UI");
            }
            _fontFamilyComboBox.SelectedIndex = index >= 0 ? index : (FontFamilyChoices.Value.Length > 0 ? 0 : -1);
        }
        finally
        {
            _suppressFontFamilyEvent = false;
        }

        _boldToggleButton.IsChecked = bold;
        _italicToggleButton.IsChecked = italic;
    }

    private void OnBoldToggleClick(object? sender, RoutedEventArgs e) =>
        BoldToggled?.Invoke(_boldToggleButton.IsChecked == true);

    private void OnItalicToggleClick(object? sender, RoutedEventArgs e) =>
        ItalicToggled?.Invoke(_italicToggleButton.IsChecked == true);

    private void OnFontFamilyChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressFontFamilyEvent)
        {
            return;
        }
        if (_fontFamilyComboBox.SelectedItem is string family)
        {
            FontFamilySelected?.Invoke(family);
        }
    }

    /// <summary>Grays out Undo/Redo when they'd be no-ops — driven by OverlayWindow from
    /// AnnotationLayer.HistoryChanged and on every toolbar (re)show.</summary>
    public void SetHistoryState(bool canUndo, bool canRedo)
    {
        _undoButton.IsEnabled = canUndo;
        _redoButton.IsEnabled = canRedo;
    }
}
