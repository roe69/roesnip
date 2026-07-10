using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using RoeSnip.Capture;
using RoeSnip.Imaging;

namespace RoeSnip.Overlay;

// RoeSnip.csproj enables both UseWPF and UseWindowsForms, so System.Windows.Forms/System.Drawing
// are in scope alongside System.Windows/System.Windows.Media — alias the colliding names to WPF's.
// (Declared after the namespace line — see AnnotationLayer.cs for why: RoeSnip.Color, a sibling
// WP-A namespace, would otherwise shadow an outer-scope alias for "Color".)
using Point = System.Windows.Point;
using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;
using Pen = System.Windows.Media.Pen;
using FontFamily = System.Windows.Media.FontFamily;
// Not a WinForms/System.Drawing collision — FrameworkElement itself declares an instance
// FlowDirection property, which shadows the enum type name "FlowDirection" inside any method of a
// class deriving from it (Magnifier here). Alias to a distinct name to avoid CS0176.
using FlowDir = System.Windows.FlowDirection;

/// <summary>Zoom loupe near the cursor with hex/RGB (sampled from the tone-mapped preview — what
/// the user "sees") and a nits readout (sampled from the raw FP16 CapturedFrame via
/// <see cref="CapturedFrame.ReadPixelNits"/>) — RoeSnip's signature feature: it can reveal an HDR
/// highlight even when the hex value reads as plain white. Click anywhere on the loupe widget to
/// copy the current hex string to the clipboard as plain text.</summary>
public sealed class Magnifier : FrameworkElement
{
    private const double LoupeDip = 154.0;       // fixed on-screen loupe size (the historical 11 x 14 DIP footprint)
    private const double WidgetMarginDip = 24.0; // offset from the cursor, in DIPs

    /// <summary>Clamp range for <see cref="SampleRadius"/>: 1 => 3x3 source pixels at ~51 DIPs
    /// each (tightest zoom), 15 => 31x31 at ~5 DIPs each (widest context; also keeps the
    /// per-render swatch count sane — the loupe redraws every mouse move). 5 (11x11) is the
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

    private SdrImage? _preview;
    private CapturedFrame? _frame;
    private int _sampleX = -1, _sampleY = -1;
    private Point _cursorDip;

    public double DeviceScaleX { get; set; } = 1.0;
    public double DeviceScaleY { get; set; } = 1.0;

    /// <summary>Which value lines the loupe shows below the pixel grid — the same ordered
    /// ColorFormats list the ColorPickerWindow's gear popover manages, so configuring the formats
    /// once configures both readouts. Set from OverlayWindow.AssignSessionFields; the widget
    /// sizes itself to exactly the enabled lines.</summary>
    public RoeSnipSettings Formats { get; set; } = RoeSnipSettings.Default;

    // The enabled subset of Formats' color-format list, derived lazily and cached by settings
    // reference — OnRender runs per mouse move, so it must not re-run the catalog merge each time.
    private RoeSnipSettings? _activeFormatsSource;
    private List<ColorFormatEntry> _activeFormats = new();

    private List<ColorFormatEntry> ActiveFormats
    {
        get
        {
            if (!ReferenceEquals(_activeFormatsSource, Formats))
            {
                _activeFormatsSource = Formats;
                _activeFormats = ColorFormatCatalog.EffectiveFormats(Formats).FindAll(e => e.Enabled);
            }
            return _activeFormats;
        }
    }

    public string CurrentHex { get; private set; } = string.Empty;

    private bool HasSample => _sampleX >= 0 && _sampleY >= 0 && _preview is not null && _frame is not null;

    public Magnifier()
    {
        IsHitTestVisible = true; // click-to-copy the hex readout
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

    protected override void OnRender(DrawingContext dc)
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

        // Value lines below the pixel grid — the same ordered, user-managed format list the
        // ColorPickerWindow shows (see ActiveFormats), each expanded by ColorFormatTemplate. The
        // first line leads slightly larger, the rest are quiet secondary lines, and any
        // nits-bearing format (the killer HDR feature) stays the largest and boldest, amber when
        // the pixel is brighter than a typical SDR white.
        var mono = new FontFamily("Consolas");
        var semiFace = new Typeface(mono, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        var bodyFace = new Typeface(mono, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var boldFace = new Typeface(mono, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

        bool isHighlight = nits > 250.0;
        var lines = new List<FormattedText>();
        FormattedText? nitsText = null;
        bool firstLine = true;
        foreach (var entry in ActiveFormats)
        {
            string value = ColorFormatTemplate.Format(entry.Format, r, g, b, nits);
            if (entry.Format.Contains("%Nt", StringComparison.Ordinal))
            {
                var nitsBrush = isHighlight ? new SolidColorBrush(Color.FromRgb(0xFF, 0xD5, 0x4F)) : Brushes.White;
                nitsText = new FormattedText(value, CultureInfo.InvariantCulture, FlowDir.LeftToRight, boldFace, 16.0, nitsBrush, 1.0);
                continue;
            }
            lines.Add(firstLine
                ? new FormattedText(value, CultureInfo.InvariantCulture, FlowDir.LeftToRight, semiFace, 12.5, Brushes.White, 1.0)
                : new FormattedText(value, CultureInfo.InvariantCulture, FlowDir.LeftToRight, bodyFace, 11.0, Brushes.LightGray, 1.0));
            firstLine = false;
        }

        // Fixed-footprint loupe: the pixel grid never resizes with zoom — the per-pixel swatch
        // size is derived from how many pixels have to fit, so wheel-zooming only changes
        // magnification. The widget's total size hugs exactly the enabled value lines (compact:
        // no reserved space for hidden formats), growing wider only if a long line (cmyk) needs it.
        double loupeSize = LoupeDip;
        double swatchDip = LoupeDip / (_sampleRadius * 2 + 1);

        const double PadDip = 6.0, LineGapDip = 1.5;
        double textHeight = 0;
        double maxLineWidth = 0;
        foreach (var line in lines)
        {
            textHeight += line.Height + LineGapDip;
            maxLineWidth = Math.Max(maxLineWidth, line.Width);
        }
        if (nitsText is not null)
        {
            textHeight += nitsText.Height + (lines.Count > 0 ? 2.5 : 0);
            maxLineWidth = Math.Max(maxLineWidth, nitsText.Width);
        }
        double widgetWidth = Math.Max(loupeSize + PadDip * 2, maxLineWidth + PadDip * 2 + 4);
        double widgetHeight = PadDip + loupeSize + (textHeight > 0 ? 5.0 + textHeight : 0) + PadDip;

        double x = _cursorDip.X + WidgetMarginDip;
        double y = _cursorDip.Y + WidgetMarginDip;
        if (ActualWidth > 0 && x + widgetWidth > ActualWidth) x = _cursorDip.X - WidgetMarginDip - widgetWidth;
        if (ActualHeight > 0 && y + widgetHeight > ActualHeight) y = _cursorDip.Y - WidgetMarginDip - widgetHeight;
        x = Math.Max(0, x);
        y = Math.Max(0, y);

        var widgetRect = new Rect(x, y, widgetWidth, widgetHeight);
        dc.DrawRoundedRectangle(
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
        if (nitsText is not null)
        {
            dc.DrawText(nitsText, new Point(textX, textY + (lines.Count > 0 ? 2.5 : 0)));
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (!string.IsNullOrEmpty(CurrentHex))
        {
            TryCopyHexToClipboard();
            e.Handled = true;
        }
    }

    private void TryCopyHexToClipboard()
    {
        try
        {
            System.Windows.Clipboard.SetText(CurrentHex);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // The clipboard can be transiently locked by another process; click-to-copy is a
            // convenience, not part of the critical Copy/Save path — swallow and let the user retry.
        }
    }

    private static (byte R, byte G, byte B) ReadPreviewPixel(SdrImage preview, int x, int y)
    {
        int o = y * preview.Stride + x * 4;
        var pixels = preview.Pixels;
        return (pixels[o + 2], pixels[o + 1], pixels[o + 0]); // BGRA8 -> R,G,B
    }
}
