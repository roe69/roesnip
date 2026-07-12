using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace RoeSnip.Sharing;

// RoeSnip.Color (the HDR color-math namespace) is a sibling namespace that would otherwise shadow
// System.Drawing.Color by simple-name lookup from inside RoeSnip.Sharing - same issue and same fix
// as Overlay/ToolbarControl.xaml.cs's own "Color" alias (declared after the namespace line so it
// wins over the sibling-namespace candidate).
using Color = System.Drawing.Color;

/// <summary>Generates the tiny throwaway PNG ShareProviderEditWindow's Test button uploads - real
/// bytes through the real ShareManager/ProviderSpecShareProvider pipeline (never a canned stub
/// response), so a successful Test is genuine end-to-end evidence the configured provider works.
/// Uses System.Drawing (RoeSnip.csproj already enables UseWindowsForms, same as TrayApp's
/// procedurally-drawn tray icon) rather than the app's own HDR imaging pipeline
/// (Imaging/SdrImage + ToneMapper) - that pipeline exists to solve HDR-correct screenshot rendering,
/// a problem this 32x32 test swatch has no need to touch.</summary>
public static class ShareTestImage
{
    public static byte[] CreatePngBytes()
    {
        const int size = 32;
        using var bitmap = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bitmap))
        {
            using var brush = new LinearGradientBrush(
                new Rectangle(0, 0, size, size),
                Color.FromArgb(255, 0x4A, 0x9E, 0xFF),
                Color.FromArgb(255, 0xFF, 0x6B, 0x35),
                45f);
            g.FillRectangle(brush, 0, 0, size, size);
        }

        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }
}
