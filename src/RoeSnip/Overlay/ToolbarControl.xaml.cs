using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace RoeSnip.Overlay;

// RoeSnip.csproj enables both UseWPF and UseWindowsForms, so System.Windows.Forms/System.Drawing
// are in scope alongside System.Windows/System.Windows.Media — alias the colliding names to WPF's.
// (Declared after the namespace line — see AnnotationLayer.cs for why: RoeSnip.Color, a sibling
// WP-A namespace, would otherwise shadow an outer-scope alias for "Color".)
using Color = System.Windows.Media.Color;
using UserControl = System.Windows.Controls.UserControl;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

/// <summary>Pictogram tool buttons (inline vector Path icons — no image assets), the editable
/// swatch palette (right-click Replace/Remove per swatch — item 3, UX round 5), the numeric size
/// input (item 2, UX round 5 — one editable px/pt ComboBox instead of the old fixed stroke dots),
/// undo, the terminal action buttons (Copy / Save, with HDR export on Save's right-click menu —
/// item 4, UX round 5), and a Cancel X that always closes the whole overlay. Always attached
/// below-right of the current selection by the owning OverlayWindow (flipping above if it would go
/// off-screen). All buttons are always visible — no hover-only-revealed controls, per the product's
/// UI style requirement — and every control except the size ComboBox's editable TextBox is
/// Focusable=false so keystrokes (Esc/Enter/Ctrl+...) keep reaching the overlay window's handler
/// even right after a toolbar click. The size box is the one deliberate exception (typing needs
/// real keyboard focus); OverlayWindow.IsTextInputFocused + the session keyboard hook's pass-through
/// branch (OverlayInputInterop.cs) keep its keystrokes from triggering session commands.
///
/// The root Cursor=Arrow (item 1, UX round 5) beats the window-level brush-circle tool cursor via
/// WPF's element-level cursor resolution, so hovering any toolbar control shows a normal pointer;
/// the ComboBox dropdowns and context menus set their own Arrow explicitly since Popup subtrees
/// don't inherit it.</summary>
public partial class ToolbarControl : UserControl
{
    /// <summary>Every installed system font family, sorted by display name — enumerated once per
    /// process (Lazy) since the installed set doesn't change under a running session. Replaces the
    /// old fixed six-font list; the dropdown stays cheap despite hundreds of entries because
    /// DarkComboBoxStyle's popup virtualizes (only the visible rows realize their live-font "AaBb"
    /// preview). Names come from FamilyNames for the current UI culture (falling back to Source)
    /// so localized families show their friendly name.</summary>
    private static readonly Lazy<string[]> FontFamilyChoices = new(() =>
    {
        var names = new SortedSet<string>(StringComparer.CurrentCultureIgnoreCase);
        foreach (var family in System.Windows.Media.Fonts.SystemFontFamilies)
        {
            try
            {
                var language = System.Windows.Markup.XmlLanguage.GetLanguage(
                    System.Globalization.CultureInfo.CurrentUICulture.IetfLanguageTag);
                if (!family.FamilyNames.TryGetValue(language, out string? name) || string.IsNullOrWhiteSpace(name))
                {
                    name = family.FamilyNames.Values.FirstOrDefault(n => !string.IsNullOrWhiteSpace(n))
                        ?? family.Source;
                }
                if (!string.IsNullOrWhiteSpace(name))
                {
                    names.Add(name);
                }
            }
            catch (Exception)
            {
                // A single corrupt font registration must never take the whole list down.
            }
        }
        return names.ToArray();
    });

    private readonly ToggleButton[] _toolButtons;
    private ToggleButton? _selectedColorSwatch;
    private double _strokeWidth = 4.0;
    private double _fontSize = 20.0;
    private bool _suppressFontFamilyEvent;

    // Size ComboBox state (item 2): whether it currently edits the font size (Text tool, "pt") or
    // the stroke width (every other tool, "px"), plus a guard so programmatic repopulation/Text
    // writes never re-raise the *Selected events they themselves were driven by.
    private bool _sizeModeIsFont;
    private bool _suppressSizeEvents;

    public event Action<AnnotationTool>? ToolSelected;
    public event Action<Color>? ColorSelected;
    public event Action<double>? StrokeWidthSelected;
    public event Action? UndoClicked;
    public event Action? RedoClicked;
    public event Action? CopyClicked;
    public event Action? SaveClicked;
    public event Action? SaveHdrClicked;
    public event Action? RecordMp4Clicked;
    public event Action? RecordGifClicked;
    public event Action? CancelClicked;

    /// <summary>Text-style group (item 4) — Bold, Italic, font family. Only ever visible while the
    /// Text tool is selected. Font size is edited via the shared size ComboBox (pt mode).</summary>
    public event Action<double>? FontSizeSelected;
    public event Action<bool>? BoldToggled;
    public event Action<bool>? ItalicToggled;
    public event Action<string>? FontFamilySelected;

    /// <summary>Right-click palette editing: the argument is the swatch's index into the palette
    /// list last passed to <see cref="SetPaletteColors"/>. OverlayWindow owns the mutation +
    /// persistence (and the Replace color dialog) and calls SetPaletteColors again. Replace is the
    /// only palette edit: the row is a fixed set of recolorable slots, never grown or shrunk.</summary>
    public event Action<int>? PaletteReplaceRequested;

    /// <summary>UNUSED as of the feature 10 redesign: the record menu no longer has audio
    /// checkboxes (they moved into RecordingChrome's Setup panel, which persists them itself via
    /// SettingsStore — see RecordingController.cs). Kept declared, and never raised, purely because
    /// OverlayWindow.xaml.cs (outside this workstream) still subscribes to it.</summary>
    public event Action<bool, bool>? RecordAudioTogglesChanged;

    /// <summary>UNUSED as of the feature 10 redesign — see <see cref="RecordAudioTogglesChanged"/>'s
    /// doc comment. Kept as a no-op purely because OverlayWindow.xaml.cs still calls it once per
    /// toolbar show.</summary>
    public void SetRecordAudioToggles(bool microphone, bool systemAudio)
    {
    }

    public ToolbarControl()
    {
        InitializeComponent();

        _toolButtons = new[]
        {
            SelectToolButton, RectToolButton, EllipseToolButton, ArrowToolButton,
            LineToolButton, FreehandToolButton, HighlightToolButton, PixelateToolButton,
            TextToolButton,
        };

        BuildFontFamilyChoices();
        RefreshSizeCombo();

        // Commit whatever was typed whenever keyboard focus leaves the size box entirely (clicking
        // a tool, clicking the image, Enter's own focus hand-back) — never leave a typed-but-
        // uncommitted value silently reverting later.
        SizeComboBox.IsKeyboardFocusWithinChanged += (_, e) =>
        {
            if (e.NewValue is false)
            {
                CommitSizeText();
            }
        };

        // A ContextMenu isn't part of its owner Button's visual tree, so these can't be set as a
        // XAML RelativeSource binding — set once here so the menu opens directly under the button
        // (normal menu-button UX) instead of at the cursor (ContextMenu's default for a left-click
        // open, since it has no mouse-position event to place itself against).
        RecordContextMenu.PlacementTarget = RecordButton;
        RecordContextMenu.Placement = PlacementMode.Bottom;
    }

    private AnnotationTool _activeTool = AnnotationTool.None;

    private void SelectTool(ToggleButton selected, AnnotationTool tool)
    {
        // Clicking the tool that is ALREADY active toggles back to the Select tool - a quick
        // "put the tool away" gesture. The Select tool itself does not toggle to anything.
        if (tool != AnnotationTool.None && tool == _activeTool)
        {
            selected = SelectToolButton;
            tool = AnnotationTool.None;
        }
        _activeTool = tool;

        foreach (var button in _toolButtons)
        {
            button.IsChecked = ReferenceEquals(button, selected);
        }

        // The text-style group is always visible while the Text tool is active — no hover-hiding —
        // and collapsed for every other tool.
        TextStylePanel.Visibility = tool == AnnotationTool.Text ? Visibility.Visible : Visibility.Collapsed;

        // The size box follows the tool: pt (font size) for Text, px (stroke width) otherwise.
        SetSizeMode(isFont: tool == AnnotationTool.Text);

        ToolSelected?.Invoke(tool);
    }

    private void OnSelectToolClick(object sender, RoutedEventArgs e) => SelectTool(SelectToolButton, AnnotationTool.None);
    private void OnRectToolClick(object sender, RoutedEventArgs e) => SelectTool(RectToolButton, AnnotationTool.Rectangle);
    private void OnEllipseToolClick(object sender, RoutedEventArgs e) => SelectTool(EllipseToolButton, AnnotationTool.Ellipse);
    private void OnArrowToolClick(object sender, RoutedEventArgs e) => SelectTool(ArrowToolButton, AnnotationTool.Arrow);
    private void OnLineToolClick(object sender, RoutedEventArgs e) => SelectTool(LineToolButton, AnnotationTool.Line);
    private void OnFreehandToolClick(object sender, RoutedEventArgs e) => SelectTool(FreehandToolButton, AnnotationTool.Freehand);
    private void OnHighlightToolClick(object sender, RoutedEventArgs e) => SelectTool(HighlightToolButton, AnnotationTool.Highlight);
    private void OnPixelateToolClick(object sender, RoutedEventArgs e) => SelectTool(PixelateToolButton, AnnotationTool.Pixelate);
    private void OnTextToolClick(object sender, RoutedEventArgs e) => SelectTool(TextToolButton, AnnotationTool.Text);

    // ---------- Editable swatch palette (item 3, UX round 5) ----------

    /// <summary>Rebuilds the swatch row from the persisted palette (RoeSnipSettings.PaletteColors,
    /// migrated/capped by SwatchPalette) — called by OverlayWindow when the toolbar is (re)shown
    /// and after every palette edit. The swatch matching <paramref name="selectedColor"/> is
    /// checked (falling back to the first swatch, without raising ColorSelected — OverlayWindow
    /// keeps its current color aligned with the palette before calling this).</summary>
    public void SetPaletteColors(IReadOnlyList<string> hexColors, Color selectedColor)
    {
        ColorSwatchPanel.Children.Clear();
        _selectedColorSwatch = null;

        string selectedHex = ColorFormatting.HexWithHash(selectedColor.R, selectedColor.G, selectedColor.B);

        for (int i = 0; i < hexColors.Count; i++)
        {
            string hex = hexColors[i];
            Color color;
            try
            {
                if (System.Windows.Media.ColorConverter.ConvertFromString(hex) is not Color parsed)
                {
                    continue;
                }
                color = parsed;
            }
            catch (FormatException)
            {
                continue; // a hand-edited settings file can hold garbage; skip, never crash
            }

            var swatch = new ToggleButton
            {
                Style = (Style)Resources["ColorSwatchStyle"],
                Background = new SolidColorBrush(color),
                Tag = color,
                ToolTip = SwatchPalette.NameFor(hex),
                ContextMenu = BuildSwatchContextMenu(i),
            };
            swatch.Click += OnColorSwatchClick;
            ColorSwatchPanel.Children.Add(swatch);

            if (_selectedColorSwatch is null && string.Equals(hex, selectedHex, StringComparison.OrdinalIgnoreCase))
            {
                _selectedColorSwatch = swatch;
                swatch.IsChecked = true;
            }
        }

        // Never show a palette with nothing checked: fall back to the first swatch visually
        // (no ColorSelected raise — display-only, mirroring how the old preset row initialized).
        if (_selectedColorSwatch is null && ColorSwatchPanel.Children.Count > 0)
        {
            _selectedColorSwatch = (ToggleButton)ColorSwatchPanel.Children[0];
            _selectedColorSwatch.IsChecked = true;
        }
    }

    /// <summary>Per-swatch right-click menu: Replace... only (a swatch is a recolorable slot, not
    /// a removable row). Index is captured per swatch — the whole row is rebuilt on every palette
    /// change, so indices can never go stale.</summary>
    private ContextMenu BuildSwatchContextMenu(int paletteIndex)
    {
        var replaceItem = new MenuItem
        {
            Header = "Replace...",
            Style = (Style)Resources["DarkMenuItemStyle"],
        };
        replaceItem.Click += (_, _) => PaletteReplaceRequested?.Invoke(paletteIndex);

        var menu = new ContextMenu { Style = (Style)Resources["DarkContextMenuStyle"] };
        menu.Items.Add(replaceItem);
        return menu;
    }

    private void OnColorSwatchClick(object sender, RoutedEventArgs e)
    {
        var clicked = (ToggleButton)sender;
        SelectColorSwatch(clicked);
        ColorSelected?.Invoke((Color)clicked.Tag);
    }

    /// <summary>Swatches form a single selection group: picking one un-checks every other.</summary>
    private void SelectColorSwatch(ToggleButton clicked)
    {
        foreach (ToggleButton swatch in ColorSwatchPanel.Children)
        {
            swatch.IsChecked = ReferenceEquals(swatch, clicked);
        }
        _selectedColorSwatch = clicked;
    }

    /// <summary>Re-checks the default Select tool without raising ToolSelected — called by the
    /// owning OverlayWindow when the toolbar is hidden (selection cleared), which also resets its
    /// own current tool to None; this keeps the visible checked state from lying on re-show.</summary>
    public void ResetToolSelection()
    {
        _activeTool = AnnotationTool.None;
        foreach (var button in _toolButtons)
        {
            button.IsChecked = ReferenceEquals(button, SelectToolButton);
        }
        TextStylePanel.Visibility = Visibility.Collapsed;
        SetSizeMode(isFont: false);
    }

    // ---------- Numeric size input (item 2, UX round 5) ----------

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
        if (_sizeModeIsFont == isFont && SizeComboBox.Items.Count > 0)
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
            SizeComboBox.Items.Clear();
            var presets = _sizeModeIsFont ? SizeInput.FontPresetsPt : SizeInput.StrokePresetsPx;
            foreach (double preset in presets)
            {
                SizeComboBox.Items.Add(_sizeModeIsFont ? SizeInput.FormatPt(preset) : SizeInput.FormatPx(preset));
            }
            SizeComboBox.Text = _sizeModeIsFont ? SizeInput.FormatPt(_fontSize) : SizeInput.FormatPx(_strokeWidth);
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
            SizeComboBox.Text = text;
        }
        finally
        {
            _suppressSizeEvents = false;
        }
    }

    /// <summary>Parses/clamps whatever is in the size box and applies it: updates the tool state
    /// via StrokeWidthSelected/FontSizeSelected (OverlayWindow updates the brush + cursor circle
    /// immediately) and rewrites the box to the canonical "Npx"/"Npt" form. Unparseable text
    /// reverts to the current value. Idempotent — safe to call from both Enter and focus-loss.</summary>
    private void CommitSizeText()
    {
        if (_suppressSizeEvents)
        {
            return;
        }

        if (!SizeInput.TryParse(SizeComboBox.Text, out double raw))
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
    /// Enter confirms the snip — this one never does; the session hook passes keys through while
    /// the box is focused, see OverlayInputInterop.cs). Esc reverts and hands focus back likewise,
    /// deliberately consuming it so it can't double as "cancel the snip" mid-typing.</summary>
    private void OnSizeComboKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitSizeText();
            SizeComboBox.IsDropDownOpen = false;
            ReturnFocusToOverlayWindow();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            SetSizeComboTextSuppressed(_sizeModeIsFont ? SizeInput.FormatPt(_fontSize) : SizeInput.FormatPx(_strokeWidth));
            SizeComboBox.IsDropDownOpen = false;
            ReturnFocusToOverlayWindow();
            e.Handled = true;
        }
    }

    private void OnSizeComboSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSizeEvents || SizeComboBox.SelectedItem is not string presetText)
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
        if (Window.GetWindow(this) is { } window)
        {
            window.Focus();
            Keyboard.Focus(window);
        }
    }

    /// <summary>True when the given event source sits inside the size ComboBox — OverlayWindow uses
    /// this to decide whether a toolbar click should yank keyboard focus back to the window (any
    /// toolbar click that is NOT into the size box should, since every other control is
    /// Focusable=false and would otherwise leave the box focused forever).</summary>
    internal bool IsWithinSizeInput(DependencyObject? source)
    {
        var current = source;
        while (current is not null)
        {
            if (ReferenceEquals(current, SizeComboBox))
            {
                return true;
            }
            current = current is Visual ? VisualTreeHelper.GetParent(current) : null;
        }
        return false;
    }

    // ---------- Text-style group (item 4) ----------

    private void BuildFontFamilyChoices()
    {
        foreach (var family in FontFamilyChoices.Value)
        {
            FontFamilyComboBox.Items.Add(family);
        }
        int segoe = IndexOfFontFamily("Segoe UI");
        FontFamilyComboBox.SelectedIndex = segoe >= 0 ? segoe : 0;
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
            FontFamilyComboBox.SelectedIndex = index >= 0 ? index : 0;
        }
        finally
        {
            _suppressFontFamilyEvent = false;
        }

        BoldToggleButton.IsChecked = bold;
        ItalicToggleButton.IsChecked = italic;
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

    /// <summary>Grays out Undo/Redo when they'd be no-ops — driven by OverlayWindow from
    /// AnnotationLayer.HistoryChanged and on every toolbar (re)show.</summary>
    public void SetHistoryState(bool canUndo, bool canRedo)
    {
        UndoButton.IsEnabled = canUndo;
        RedoButton.IsEnabled = canRedo;
    }

    /// <summary>Cross-monitor selection (multimon-selection): while the current selection spans
    /// multiple monitors, annotations are a documented v1 cut (see docs/DESIGN-MULTIMON-SELECTION.md
    /// — a spanning composite has no single window's coordinate space for shapes to live in) and
    /// Record has no per-monitor-stitched live-capture implementation. Collapses everything except
    /// Save/Copy/Cancel — the same three actions the DESIGN.md "acceptable v1" carve-out names.
    /// Called by OverlayWindow.ShowToolbar/UpdateToolbarPlacement every time the toolbar is shown,
    /// so it can never go stale across a spanning/non-spanning transition on the same reused
    /// instance. Upload is also hidden (it's already permanently disabled, but keeping the action
    /// row down to exactly the three live actions reads cleaner than a disabled placeholder sitting
    /// next to them).</summary>
    public void SetSpanningMode(bool spanning)
    {
        var collapseVisibility = spanning ? Visibility.Collapsed : Visibility.Visible;
        ToolsGroupPanel.Visibility = collapseVisibility;
        HistoryGroupPanel.Visibility = collapseVisibility;
        PaletteGroupPanel.Visibility = collapseVisibility;
        RecordButton.Visibility = collapseVisibility;
        UploadButton.Visibility = collapseVisibility;
    }

    private void OnUndoClick(object sender, RoutedEventArgs e) => UndoClicked?.Invoke();
    private void OnRedoClick(object sender, RoutedEventArgs e) => RedoClicked?.Invoke();
    private void OnCopyClick(object sender, RoutedEventArgs e) => CopyClicked?.Invoke();
    private void OnSaveClick(object sender, RoutedEventArgs e) => SaveClicked?.Invoke();
    private void OnSaveHdrClick(object sender, RoutedEventArgs e) => SaveHdrClicked?.Invoke();

    // Left-click opens the format menu (unlike Save's right-click-only HDR menu — Record has no
    // single default format a plain click could commit to).
    private void OnRecordClick(object sender, RoutedEventArgs e) => RecordButton.ContextMenu!.IsOpen = true;
    private void OnRecordMp4Click(object sender, RoutedEventArgs e) => RecordMp4Clicked?.Invoke();
    private void OnRecordGifClick(object sender, RoutedEventArgs e) => RecordGifClicked?.Invoke();

    private void OnCancelClick(object sender, RoutedEventArgs e) => CancelClicked?.Invoke();
}
