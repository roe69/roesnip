using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RoeSnip.Overlay;
using Xunit;

namespace RoeSnip.Tests;

/// <summary>Renders committed annotations through AnnotationLayer.RenderForExport (the export path;
/// on screen draws through the same Draw). Set ROESNIP_RENDER_DIR to also write PNGs to look at.</summary>
public class AnnotationRenderTests
{
    private static void RunSta(Action body)
    {
        Exception? error = null;
        var t = new Thread(() =>
        {
            try { body(); } catch (Exception ex) { error = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error is not null)
        {
            throw new Exception("STA body failed", error);
        }
    }

    private static AnnotationLayer Layer(params (AnnotationTool Tool, double Width, Point[] Pts)[] shapes)
    {
        var layer = new AnnotationLayer();
        foreach (var s in shapes)
        {
            layer.BeginShape(s.Tool, s.Pts[0], Colors.Red, s.Width);
            for (int i = 1; i < s.Pts.Length; i++)
            {
                layer.UpdateShape(s.Pts[i]);
            }
            layer.EndShape();
        }
        return layer;
    }

    private static RenderTargetBitmap Render(AnnotationLayer layer, int w, int h, Color? background)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            if (background is { } bg)
            {
                dc.DrawRectangle(new SolidColorBrush(bg), null, new Rect(0, 0, w, h));
            }
            layer.RenderForExport(dc, new Point(0, 0));
        }
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        return rtb;
    }

    private static byte AlphaAt(BitmapSource bmp, int x, int y)
    {
        var px = new byte[4];
        bmp.CopyPixels(new Int32Rect(x, y, 1, 1), px, 4, 0);
        return px[3];
    }

    private static void SavePng(BitmapSource bmp, string path)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    [Fact]
    public void Arrow_TipStaysAtEndPoint_NothingPastIt()
    {
        RunSta(() =>
        {
            var layer = Layer((AnnotationTool.Arrow, 6, new[] { new Point(20, 50), new Point(120, 50) }));
            var bmp = Render(layer, 160, 100, null);
            for (int x = 123; x < 160; x++)
            {
                for (int y = 0; y < 100; y++)
                {
                    Assert.Equal(0, AlphaAt(bmp, x, y));
                }
            }
            Assert.True(AlphaAt(bmp, 117, 50) > 200);  // head body near the tip
            Assert.True(AlphaAt(bmp, 108, 54) > 200);  // head is wider than the 6 px shaft
            Assert.Equal(0, AlphaAt(bmp, 60, 54));     // shaft itself is not
        });
    }

    [Fact]
    public void Rectangle_HasSharpMitredCorners_OnWholePixels()
    {
        RunSta(() =>
        {
            var layer = Layer((AnnotationTool.Rectangle, 5, new[] { new Point(10, 10), new Point(60, 40) }));
            var bmp = Render(layer, 80, 60, null);
            Assert.Equal(255, AlphaAt(bmp, 8, 8));   // outer corner is square, not rounded
            Assert.Equal(0, AlphaAt(bmp, 7, 7));
            Assert.Equal(255, AlphaAt(bmp, 35, 8));  // top edge fully covered, no AA fringe row
            Assert.Equal(0, AlphaAt(bmp, 35, 7));
        });
    }

    [Fact]
    public void RenderSamples_WritesPngs_WhenRenderDirSet()
    {
        string? dir = Environment.GetEnvironmentVariable("ROESNIP_RENDER_DIR");
        if (string.IsNullOrEmpty(dir))
        {
            return; // no-op unless the conductor asks for images
        }
        Directory.CreateDirectory(dir);
        RunSta(() =>
        {
            var shapes = new System.Collections.Generic.List<(AnnotationTool, double, Point[])>();
            double[] widths = { 1, 2, 3, 5, 8, 14 };
            double y = 30;
            foreach (double w in widths)
            {
                shapes.Add((AnnotationTool.Arrow, w, new[] { new Point(30, y), new Point(230, y) }));
                shapes.Add((AnnotationTool.Line, w, new[] { new Point(260, y), new Point(430, y) }));
                shapes.Add((AnnotationTool.Rectangle, w, new[] { new Point(460, y - 12), new Point(540, y + 12) }));
                shapes.Add((AnnotationTool.Ellipse, w, new[] { new Point(570, y - 12), new Point(650, y + 12) }));
                y += 46;
            }
            // Arrows at several angles and lengths (width 4), fanned from a centre.
            var c = new Point(160, 420);
            for (int a = 0; a < 360; a += 30)
            {
                double r = a * Math.PI / 180;
                double len = a % 60 == 0 ? 90 : 55;
                shapes.Add((AnnotationTool.Arrow, 4, new[] { c, new Point(c.X + Math.Cos(r) * len, c.Y + Math.Sin(r) * len) }));
            }
            // Short arrows.
            double sx = 380;
            foreach (double len in new[] { 6.0, 12, 20, 30, 45 })
            {
                shapes.Add((AnnotationTool.Arrow, 3, new[] { new Point(sx, 360), new Point(sx + len, 360) }));
                shapes.Add((AnnotationTool.Arrow, 8, new[] { new Point(sx, 420), new Point(sx + len, 420) }));
                shapes.Add((AnnotationTool.Arrow, 3, new[] { new Point(sx, 480), new Point(sx + len * 0.7, 480 + len * 0.7) }));
                sx += 60;
            }
            // Freehand and highlight: a sparse zig-zag and a dense wave.
            shapes.Add((AnnotationTool.Freehand, 3, new[]
            {
                new Point(30, 560), new Point(70, 610), new Point(110, 560), new Point(150, 610), new Point(190, 560),
            }));
            var wave = new System.Collections.Generic.List<Point>();
            for (int x = 0; x <= 200; x += 3)
            {
                wave.Add(new Point(260 + x, 585 + Math.Sin(x / 20.0) * 25));
            }
            shapes.Add((AnnotationTool.Freehand, 4, wave.ToArray()));
            shapes.Add((AnnotationTool.Highlight, 4, new[] { new Point(500, 575), new Point(640, 575), new Point(640, 600), new Point(520, 600) }));

            var layer = Layer(shapes.ToArray());
            SavePng(Render(layer, 700, 660, Colors.White), Path.Combine(dir, "wpf-annotations-light.png"));
            SavePng(Render(layer, 700, 660, Color.FromRgb(0x20, 0x24, 0x2c)), Path.Combine(dir, "wpf-annotations-dark.png"));
        });
        Assert.True(File.Exists(Path.Combine(dir, "wpf-annotations-light.png")));
    }
}
