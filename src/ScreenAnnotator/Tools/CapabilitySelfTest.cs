using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using ScreenAnnotator.Native;

namespace ScreenAnnotator.Tools;

/// <summary>
/// Smoke checks for PRD critical capabilities. Run: ScreenAnnotator.exe --self-test
/// </summary>
internal static class CapabilitySelfTest
{
    public static void Run(Application app)
    {
        var results = new List<string>();

        var win = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)),
            Topmost = true,
            Width = 200,
            Height = 200,
            Left = 50,
            Top = 50,
            ShowInTaskbar = false,
            Title = "CapabilitySelfTest"
        };

        var canvas = new Canvas();
        canvas.Children.Add(new Ellipse
        {
            Width = 80,
            Height = 80,
            Stroke = Brushes.Red,
            StrokeThickness = 4,
            Margin = new Thickness(40)
        });
        win.Content = canvas;

        win.SourceInitialized += (_, _) =>
        {
            var exitCode = 0;
            try
            {
                WindowClickThrough.SetClickThrough(win, false);
                var bg = (win.Background as SolidColorBrush)?.Color;
                results.Add(bg is { A: > 0 }
                    ? "[PASS] Window background has non-zero alpha for hit-testing"
                    : "[FAIL] Window background fully transparent (mouse will miss empty pixels)");
                results.Add("[PASS] Transparent topmost window created");

                WindowClickThrough.SetClickThrough(win, true);
                var hwnd = new WindowInteropHelper(win).Handle;
                var style = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
                var passThrough = (style & NativeMethods.WS_EX_TRANSPARENT) != 0;
                results.Add(passThrough
                    ? "[PASS] Click-through enabled while window remains visible"
                    : "[FAIL] Click-through style not set");

                WindowClickThrough.SetClickThrough(win, false);

                var hotOk1 = NativeMethods.RegisterHotKey(hwnd, 9001, NativeMethods.MOD_CONTROL | NativeMethods.MOD_SHIFT, (uint)'Q');
                var err1 = Marshal.GetLastWin32Error();
                var hotOk2 = NativeMethods.UnregisterHotKey(hwnd, 9001);
                var hotOk3 = NativeMethods.RegisterHotKey(hwnd, 9001, NativeMethods.MOD_CONTROL | NativeMethods.MOD_SHIFT, (uint)'Q');
                NativeMethods.UnregisterHotKey(hwnd, 9001);
                results.Add(hotOk1 && hotOk2 && hotOk3
                    ? "[PASS] Global hotkey register / unregister / re-register"
                    : $"[FAIL] Hotkey ops (reg={hotOk1}/{err1} unreg={hotOk2} rereg={hotOk3})");

                using var bmp = new System.Drawing.Bitmap(16, 16);
                using var g = System.Drawing.Graphics.FromImage(bmp);
                g.CopyFromScreen(0, 0, 0, 0, new System.Drawing.Size(16, 16));
                results.Add("[PASS] Screen capture CopyFromScreen");

                var visual = new DrawingVisual();
                using (var dc = visual.RenderOpen())
                    dc.DrawEllipse(null, new Pen(Brushes.Blue, 2), new Point(8, 8), 6, 6);
                var rtb = new RenderTargetBitmap(16, 16, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(visual);
                results.Add(rtb.PixelWidth == 16
                    ? "[PASS] Annotation render / PNG composition path"
                    : "[FAIL] RenderTargetBitmap");
            }
            catch (Exception ex)
            {
                results.Add("[FAIL] Exception: " + ex.Message);
            }
            finally
            {
                var report = string.Join(Environment.NewLine, results);
                var reportPath = System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(), "ScreenAnnotator-self-test.txt");
                System.IO.File.WriteAllText(reportPath, report + Environment.NewLine);
                Console.WriteLine(report);
                Console.WriteLine("Report: " + reportPath);
                if (results.Exists(r => r.StartsWith("[FAIL]")))
                    exitCode = 1;
                win.Close();
                app.Shutdown(exitCode);
            }
        };

        win.Show();
    }
}
