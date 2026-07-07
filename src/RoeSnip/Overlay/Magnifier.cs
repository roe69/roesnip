using System;
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
    private const int SampleRadiusPx = 5;       // (2r+1)x(2r+1) block of preview pixels sampled
    private const double SwatchDip = 14.0;      // on-screen size (DIPs) per sampled source pixel
    private const double WidgetMarginDip = 24.0; // offset from the cursor, in DIPs

    private SdrImage? _preview;
    private CapturedFrame? _frame;
    private int _sampleX = -1, _sampleY = -1;
    private Point _cursorDip;

    public double DeviceScaleX { get; set; } = 1.0;
    public double DeviceScaleY { get; set; } = 1.0;

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

        double loupeSize = SwatchDip * (SampleRadiusPx * 2 + 1);
        double widgetWidth = Math.Max(loupeSize, 150.0);
        double widgetHeight = loupeSize + 78.0; // room for hex/rgb/nits lines below the loupe

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
        for (int dy = -SampleRadiusPx; dy <= SampleRadiusPx; dy++)
        {
            for (int dx = -SampleRadiusPx; dx <= SampleRadiusPx; dx++)
            {
                int sx = Math.Clamp(cx + dx, 0, preview.Width - 1);
                int sy = Math.Clamp(cy + dy, 0, preview.Height - 1);
                var (pr, pg, pb) = ReadPreviewPixel(preview, sx, sy);
                var brush = new SolidColorBrush(Color.FromRgb(pr, pg, pb));
                double swatchX = loupeLeft + (dx + SampleRadiusPx) * SwatchDip;
                double swatchY = y + 6.0 + (dy + SampleRadiusPx) * SwatchDip;
                dc.DrawRectangle(brush, null, new Rect(swatchX, swatchY, SwatchDip, SwatchDip));
            }
        }

        // Crosshair over the center (sampled) pixel.
        double centerX = loupeLeft + loupeSize / 2;
        double centerY = y + 6.0 + loupeSize / 2;
        var crossPen = new Pen(Brushes.White, 1.0);
        dc.DrawRectangle(null, crossPen, new Rect(centerX - SwatchDip / 2, centerY - SwatchDip / 2, SwatchDip, SwatchDip));

        var monoFace = new Typeface(new FontFamily("Consolas"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        var hexText = new FormattedText(CurrentHex, CultureInfo.InvariantCulture, FlowDir.LeftToRight, monoFace, 14.0, Brushes.White, 1.0);
        var rgbText = new FormattedText(
            string.Create(CultureInfo.InvariantCulture, $"R{r} G{g} B{b}"),
            CultureInfo.InvariantCulture, FlowDir.LeftToRight, monoFace, 12.0, Brushes.LightGray, 1.0);

        // The nits readout is the killer feature — largest, boldest, most prominent line, and
        // called out in amber whenever it exceeds SDR white (1.0 in the frame's normalized sense
        // corresponds to 80 nits; anything above plain 1.0 nits here just means "not pure black",
        // so use a clearly-HDR-ish threshold instead: highlight once we're brighter than a
        // typical SDR white level).
        bool isHighlight = nits > 250.0;
        var nitsBrush = isHighlight ? new SolidColorBrush(Color.FromRgb(0xFF, 0xD5, 0x4F)) : Brushes.White;
        var nitsText = new FormattedText(
            string.Create(CultureInfo.InvariantCulture, $"{nits:0.#} nits"),
            CultureInfo.InvariantCulture, FlowDir.LeftToRight,
            new Typeface(new FontFamily("Consolas"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
            19.0, nitsBrush, 1.0);

        double textY = y + loupeSize + 12.0;
        dc.DrawText(hexText, new Point(x + 8.0, textY));
        dc.DrawText(rgbText, new Point(x + 8.0, textY + hexText.Height + 2.0));
        dc.DrawText(nitsText, new Point(x + 8.0, textY + hexText.Height + rgbText.Height + 5.0));
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
