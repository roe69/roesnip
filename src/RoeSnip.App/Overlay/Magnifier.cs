using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using RoeSnip.Core.Capture;
using RoeSnip.Core.Imaging;

namespace RoeSnip.App.Overlay;

/// <summary>Zoom loupe near the cursor with hex/RGB (sampled from the tone-mapped preview — what
/// the user "sees") and a nits readout (sampled from the raw CapturedFrame via
/// <see cref="CapturedFrame.ReadPixelNits"/>) — RoeSnip's signature feature: it can reveal an HDR
/// highlight even when the hex value reads as plain white. Click anywhere on the loupe widget to
/// copy the current hex string to the clipboard as plain text. Ported from the frozen WPF app's
/// src/RoeSnip/Overlay/Magnifier.cs.</summary>
public sealed class Magnifier : Control
{
    private const int SampleRadiusPx = 5;        // (2r+1)x(2r+1) block of preview pixels sampled
    private const double SwatchDip = 14.0;       // on-screen size (DIPs) per sampled source pixel
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

        double loupeSize = SwatchDip * (SampleRadiusPx * 2 + 1);
        double widgetWidth = Math.Max(loupeSize, 150.0);
        double widgetHeight = loupeSize + 78.0; // room for hex/rgb/nits lines below the loupe

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

        var monoFace = new Typeface(OverlayFonts.Mono, FontStyle.Normal, FontWeight.SemiBold);
        var hexText = new FormattedText(CurrentHex, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, monoFace, 14.0, Brushes.White);
        var rgbText = new FormattedText(
            string.Create(CultureInfo.InvariantCulture, $"R{r} G{g} B{b}"),
            CultureInfo.InvariantCulture, FlowDirection.LeftToRight, monoFace, 12.0, Brushes.LightGray);

        // The nits readout is the killer feature — largest, boldest, most prominent line, and
        // called out in amber whenever it exceeds a typical SDR white level (same >250-nits
        // threshold as the WPF app and the click color inspector).
        bool isHighlight = nits > 250.0;
        var nitsBrush = isHighlight ? new SolidColorBrush(Color.FromRgb(0xFF, 0xD5, 0x4F)) : (IBrush)Brushes.White;
        var nitsText = new FormattedText(
            string.Create(CultureInfo.InvariantCulture, $"{nits:0.#} nits"),
            CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(OverlayFonts.Mono, FontStyle.Normal, FontWeight.Bold),
            19.0, nitsBrush);

        double textY = y + loupeSize + 12.0;
        dc.DrawText(hexText, new Point(x + 8.0, textY));
        dc.DrawText(rgbText, new Point(x + 8.0, textY + hexText.Height + 2.0));
        dc.DrawText(nitsText, new Point(x + 8.0, textY + hexText.Height + rgbText.Height + 5.0));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!string.IsNullOrEmpty(CurrentHex))
        {
            // Click-to-copy is a convenience, not part of the critical Copy/Save path — failures
            // are swallowed inside TryCopyTextAsync and the user just retries.
            _ = ClipboardService.TryCopyTextAsync(this, CurrentHex);
            e.Handled = true;
        }
    }

    private static (byte R, byte G, byte B) ReadPreviewPixel(SdrImage preview, int x, int y)
    {
        int o = y * preview.Stride + x * 4;
        var pixels = preview.Pixels;
        return (pixels[o + 2], pixels[o + 1], pixels[o + 0]); // BGRA8 -> R,G,B
    }
}
