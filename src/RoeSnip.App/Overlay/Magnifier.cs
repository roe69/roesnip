using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using RoeSnip.Core.Capture;
using RoeSnip.Core.Imaging;
using RoeSnip.Core.Settings;
using ColorFormatCatalog = RoeSnip.Core.Color.ColorFormatCatalog;
using ColorFormatEntry = RoeSnip.Core.Color.ColorFormatEntry;
using ColorFormatTemplate = RoeSnip.Core.Color.ColorFormatTemplate;

namespace RoeSnip.App.Overlay;

/// <summary>Zoom loupe near the cursor with a nits readout (sampled from the raw CapturedFrame via
/// <see cref="CapturedFrame.ReadPixelNits"/>) — RoeSnip's signature feature: it can reveal an HDR
/// highlight even when the hex value reads as plain white — plus the ordered, user-managed
/// <see cref="Formats"/> value lines (item 22, finishing this class's own item-06 deferral: it
/// used to render a hard-coded hex/RGB/nits triplet instead of the real ColorFormats list). Click
/// anywhere on the loupe widget to copy the current hex string to the clipboard as plain text —
/// the SAME gesture WPF's Magnifier uses (its OnMouseLeftButtonDown). Ported from the frozen WPF
/// app's src/RoeSnip/Overlay/Magnifier.cs.</summary>
public sealed class Magnifier : Control
{
    private const double LoupeDip = 154.0;       // fixed on-screen loupe size (the historical 11x14 DIP footprint)
    private const double WidgetMarginDip = 24.0; // offset from the cursor, in DIPs

    /// <summary>Clamp range for <see cref="SampleRadius"/>: 1 =&gt; 3x3 source pixels at ~51 DIPs
    /// each (tightest zoom), 15 =&gt; 31x31 at ~5 DIPs each (widest context). 5 (11x11) is the
    /// historical fixed sampling and remains the settings default.</summary>
    public const int MinSampleRadius = 1;
    public const int MaxSampleRadius = 15;

    private int _sampleRadius = 5;

    /// <summary>How many source pixels the loupe shows: a (2r+1)x(2r+1) block around the cursor,
    /// rendered inside the FIXED-size loupe (the widget never changes size — the per-pixel swatch
    /// size scales instead, so a smaller radius means fewer, bigger pixels: zoomed in).
    /// Wheel-adjustable per session (OverlayWindow's wheel handler), seeded from
    /// RoeSnipSettings.MagnifierSampleRadius.</summary>
    public int SampleRadius
    {
        get => _sampleRadius;
        set
        {
            int clamped = Math.Clamp(value, MinSampleRadius, MaxSampleRadius);
            if (clamped != _sampleRadius)
            {
                _sampleRadius = clamped;
                InvalidateVisual();
            }
        }
    }

    /// <summary>When false the loupe shows ONLY the zoomed pixel grid, with no hex/RGB/nits lines
    /// below it — used by the Pixelate tool, which wants the same precise-placement loupe as the
    /// initial selection but no color readout (there is no color being picked). The widget then
    /// hugs just the grid.</summary>
    public bool ShowColorReadout { get; set; } = true;

    private SdrImage? _preview;
    private CapturedFrame? _frame;
    private int _sampleX = -1, _sampleY = -1;
    private Point _cursorDip;

    public double DeviceScaleX { get; set; } = 1.0;
    public double DeviceScaleY { get; set; } = 1.0;

    /// <summary>Which value lines the loupe shows below the pixel grid — the same ordered
    /// ColorFormats list the eyedropper's gear popover manages, so configuring the formats once
    /// configures both readouts. Set once from OverlayWindow's constructor (mirrors WPF's own
    /// MagnifierControl.Formats = settings); the widget sizes itself to exactly the enabled lines.</summary>
    public RoeSnipSettings Formats { get; set; } = RoeSnipSettings.Default;

    // The enabled+in-loupe subset of Formats' color-format list, derived lazily and cached by
    // settings reference — Render runs per pointer move, so it must not re-run the catalog merge
    // every time.
    private RoeSnipSettings? _activeFormatsSource;
    private List<ColorFormatEntry> _activeFormats = new();

    private List<ColorFormatEntry> ActiveFormats
    {
        get
        {
            if (!ReferenceEquals(_activeFormatsSource, Formats))
            {
                _activeFormatsSource = Formats;
                _activeFormats = ColorFormatCatalog.EffectiveFormats(Formats)
                    .FindAll(e => e.Enabled && e.InLoupe);
            }
            return _activeFormats;
        }
    }

    public string CurrentHex { get; private set; } = string.Empty;

    private bool HasSample => _sampleX >= 0 && _sampleY >= 0 && _preview is not null && _frame is not null;

    public Magnifier()
    {
    }

    /// <summary>Updates the sample point. <paramref name="cursorDip"/> is the mouse position in
    /// this window's DIPs (used only to position the loupe widget on screen);
    /// <paramref name="cursorPx"/> is the same point already converted to physical pixels (used
    /// for the actual sampling, per the mixed-DPI contract — DIPs never cross into sampling math).</summary>
    public void Update(SdrImage preview, CapturedFrame frame, Point cursorDip, Point cursorPx)
    {
        _preview = preview;
        _frame = frame;
        _cursorDip = cursorDip;
        _sampleX = Math.Clamp((int)cursorPx.X, 0, preview.Width - 1);
        _sampleY = Math.Clamp((int)cursorPx.Y, 0, preview.Height - 1);
        InvalidateVisual();
    }

    public void Hide()
    {
        _preview = null;
        _frame = null;
        _sampleX = _sampleY = -1;
        CurrentHex = string.Empty;
        InvalidateVisual();
    }

    public override void Render(DrawingContext dc)
    {
        if (!HasSample)
        {
            CurrentHex = string.Empty;
            return;
        }

        var preview = _preview!;
        var frame = _frame!;
        int cx = _sampleX, cy = _sampleY;

        var (r, g, b) = ReadPreviewPixel(preview, cx, cy);
        double nits = frame.ReadPixelNits(
            Math.Clamp(cx, 0, frame.Width - 1),
            Math.Clamp(cy, 0, frame.Height - 1));
        CurrentHex = string.Create(CultureInfo.InvariantCulture, $"#{r:X2}{g:X2}{b:X2}");

        // Fixed-footprint loupe: the pixel grid never resizes with zoom — the per-pixel swatch size
        // is derived from how many pixels have to fit, so wheel-zooming only changes magnification.
        double loupeSize = LoupeDip;
        double swatchDip = LoupeDip / (_sampleRadius * 2 + 1);

        // Value lines below the pixel grid — the loupe-enabled subset of the same ordered,
        // user-managed format list the eyedropper shows (see ActiveFormats), each expanded by
        // ColorFormatTemplate. ONE consistent style for every line (same face, size, and color);
        // the single deliberate exception is a STATE signal, not a styling one: a nits-bearing
        // line turns amber when the pixel is brighter than a typical SDR white. Suppressed
        // entirely (ShowColorReadout=false) by the Pixelate tool, which wants the same placement
        // loupe with no color being "picked".
        var monoFace = new Typeface(OverlayFonts.Mono, FontStyle.Normal, FontWeight.SemiBold);
        const double LineFontSize = 12.0;
        bool isHighlight = nits > 250.0;
        var amber = new SolidColorBrush(Color.FromRgb(0xFF, 0xD5, 0x4F));
        var lines = new List<FormattedText>();
        if (ShowColorReadout)
        {
            foreach (var entry in ActiveFormats)
            {
                string value = ColorFormatTemplate.Format(entry.Format, r, g, b, nits);
                bool isNitsLine = entry.Format.Contains("%Nt", StringComparison.Ordinal);
                lines.Add(new FormattedText(
                    value, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, monoFace, LineFontSize,
                    isNitsLine && isHighlight ? amber : Brushes.White));
            }
        }

        // Fixed-footprint loupe: the widget's total size hugs exactly the enabled value lines
        // (compact: no reserved space for hidden formats), growing wider only if a long line
        // (cmyk) needs it.
        const double PadDip = 6.0, LineGapDip = 2.0;
        double textBlockHeight = 0;
        double maxLineWidth = 0;
        foreach (var line in lines)
        {
            textBlockHeight += line.Height + LineGapDip;
            maxLineWidth = Math.Max(maxLineWidth, line.Width);
        }
        double widgetWidth = Math.Max(loupeSize + PadDip * 2, maxLineWidth + PadDip * 2 + 4);
        double widgetHeight = PadDip + loupeSize + (textBlockHeight > 0 ? 5.0 + textBlockHeight : 0) + PadDip;

        double actualWidth = Bounds.Width;
        double actualHeight = Bounds.Height;
        double x = _cursorDip.X + WidgetMarginDip;
        double y = _cursorDip.Y + WidgetMarginDip;
        if (actualWidth > 0 && x + widgetWidth > actualWidth) x = _cursorDip.X - WidgetMarginDip - widgetWidth;
        if (actualHeight > 0 && y + widgetHeight > actualHeight) y = _cursorDip.Y - WidgetMarginDip - widgetHeight;
        x = Math.Max(0, x);
        y = Math.Max(0, y);

        var widgetRect = new Rect(x, y, widgetWidth, widgetHeight);
        dc.DrawRectangle(
            new SolidColorBrush(Color.FromArgb(0xEC, 0x14, 0x14, 0x16)),
            new Pen(Brushes.DimGray, 1.0),
            widgetRect, 6.0, 6.0);

        // Pixelated loupe: draw each sampled source pixel as a flat-colored square.
        double loupeLeft = x + (widgetWidth - loupeSize) / 2;
        double loupeTop = y + PadDip;
        for (int dy = -_sampleRadius; dy <= _sampleRadius; dy++)
        {
            for (int dx = -_sampleRadius; dx <= _sampleRadius; dx++)
            {
                int sx = Math.Clamp(cx + dx, 0, preview.Width - 1);
                int sy = Math.Clamp(cy + dy, 0, preview.Height - 1);
                var (pr, pg, pb) = ReadPreviewPixel(preview, sx, sy);
                var brush = new SolidColorBrush(Color.FromRgb(pr, pg, pb));
                double swatchX = loupeLeft + (dx + _sampleRadius) * swatchDip;
                double swatchY = loupeTop + (dy + _sampleRadius) * swatchDip;
                dc.DrawRectangle(brush, null, new Rect(swatchX, swatchY, swatchDip, swatchDip));
            }
        }

        // Crosshair over the center (sampled) pixel.
        double centerX = loupeLeft + loupeSize / 2;
        double centerY = loupeTop + loupeSize / 2;
        var crossPen = new Pen(Brushes.White, 1.0);
        dc.DrawRectangle(null, crossPen, new Rect(centerX - swatchDip / 2, centerY - swatchDip / 2, swatchDip, swatchDip));

        double textX = x + PadDip + 2;
        double textY = loupeTop + loupeSize + 5.0;
        foreach (var line in lines)
        {
            dc.DrawText(line, new Point(textX, textY));
            textY += line.Height + LineGapDip;
        }
    }

    /// <summary>Click anywhere on the loupe copies the current hex to the clipboard — mirrors WPF's
    /// Magnifier.OnMouseLeftButtonDown exactly, including its reachability: OverlayWindow's own
    /// tunnel-routed pointer-pressed handler (AddHandler(..., RoutingStrategies.Tunnel), same as
    /// WPF's Preview* events) marks most clicks Handled before they ever reach this control's own
    /// bubble-phase handler, same as WPF — this exists for the cases that DON'T get pre-handled
    /// (and for a future standalone eyedropper window, item 22, that has no such outer handler).</summary>
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || string.IsNullOrEmpty(CurrentHex))
        {
            return;
        }
        _ = TryCopyHexToClipboardAsync(this);
        e.Handled = true;
    }

    private async Task TryCopyHexToClipboardAsync(Visual owner)
    {
        string hex = CurrentHex;
        if (string.IsNullOrEmpty(hex))
        {
            return;
        }
        try
        {
            await ClipboardService.TryCopyTextAsync(owner, hex);
        }
        catch (Exception)
        {
            // Click-to-copy is a convenience, not part of the critical Copy/Save path — swallow and
            // let the user retry (same tolerance as ShowColorInfo's own copy attempt).
        }
    }

    private static (byte R, byte G, byte B) ReadPreviewPixel(SdrImage preview, int x, int y)
    {
        int o = y * preview.Stride + x * 4;
        var pixels = preview.Pixels;
        return (pixels[o + 2], pixels[o + 1], pixels[o + 0]); // BGRA8 -> R,G,B
    }
}
