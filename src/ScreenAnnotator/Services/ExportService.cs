using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ScreenAnnotator.Models;
using ScreenAnnotator.Native;
using Drawing = System.Drawing;
using Imaging = System.Windows.Interop;

namespace ScreenAnnotator.Services;

public sealed class ExportService
{
    public void ExportAnnotationsPng(
        string path,
        double width,
        double height,
        IEnumerable<AnnotationObject> objects,
        bool opaqueWhiteBackground = false)
    {
        var w = Math.Max(1, (int)Math.Ceiling(width));
        var h = Math.Max(1, (int)Math.Ceiling(height));
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            if (opaqueWhiteBackground)
            {
                var brush = Brushes.White;
                dc.DrawRectangle(brush, null, new Rect(0, 0, w, h));
            }
            foreach (var obj in objects)
                obj.Draw(dc);
        }

        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        encoder.Save(fs);
    }

    public void CaptureScreenWithAnnotations(string path, Rect screenBoundsDevicePx, IEnumerable<AnnotationObject> objects)
    {
        var w = Math.Max(1, (int)Math.Ceiling(screenBoundsDevicePx.Width));
        var h = Math.Max(1, (int)Math.Ceiling(screenBoundsDevicePx.Height));
        var left = (int)screenBoundsDevicePx.Left;
        var top = (int)screenBoundsDevicePx.Top;

        using var screenBmp = CaptureScreenRegion(left, top, w, h);
        var screenSource = ConvertBitmap(screenBmp);

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawImage(screenSource, new Rect(0, 0, w, h));
            foreach (var obj in objects)
                obj.Draw(dc);
        }

        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        encoder.Save(fs);
    }

    /// <summary>模式 B：白板底 + 笔迹（不抓桌面）。</summary>
    public void ExportBoardFullscreen(string path, double width, double height, IEnumerable<AnnotationObject> objects)
        => ExportAnnotationsPng(path, width, height, objects, opaqueWhiteBackground: true);

    private static Drawing.Bitmap CaptureScreenRegion(int left, int top, int width, int height)
    {
        var bmp = new Drawing.Bitmap(width, height, Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Drawing.Graphics.FromImage(bmp);
        g.CopyFromScreen(left, top, 0, 0, new Drawing.Size(width, height), Drawing.CopyPixelOperation.SourceCopy);
        return bmp;
    }

    private static BitmapSource ConvertBitmap(Drawing.Bitmap bitmap)
    {
        var hBitmap = bitmap.GetHbitmap();
        try
        {
            return Imaging.Imaging.CreateBitmapSourceFromHBitmap(
                hBitmap,
                IntPtr.Zero,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
        }
        finally
        {
            NativeMethods.DeleteObject(hBitmap);
        }
    }
}
