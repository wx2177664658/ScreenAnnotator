using System.Drawing;
using System.Windows.Forms;
using DrawingColor = System.Drawing.Color;
using DrawingPen = System.Drawing.Pen;

namespace ScreenAnnotator.Services;

public sealed class TrayIconService : IDisposable
{
    private readonly NotifyIcon _icon;
    private bool _disposed;

    public event Action? ToggleOverlayRequested;
    public event Action? ExitRequested;
    public event Action? OpenSettingsRequested;
    public event Action? OpenHelpRequested;

    public TrayIconService()
    {
        _icon = new NotifyIcon
        {
            Text = "屏幕标注白板",
            Visible = true,
            Icon = CreateIcon()
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add("显示/隐藏标注层", null, (_, _) => ToggleOverlayRequested?.Invoke());
        menu.Items.Add("帮助", null, (_, _) => OpenHelpRequested?.Invoke());
        menu.Items.Add("快捷键设置", null, (_, _) => OpenSettingsRequested?.Invoke());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => ExitRequested?.Invoke());
        _icon.ContextMenuStrip = menu;
        _icon.DoubleClick += (_, _) => ToggleOverlayRequested?.Invoke();
    }

    private static Icon CreateIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(DrawingColor.Transparent);
            using var brush = new SolidBrush(DrawingColor.FromArgb(220, 220, 60, 40));
            g.FillEllipse(brush, 2, 2, 28, 28);
            using var pen = new DrawingPen(DrawingColor.White, 3);
            g.DrawLine(pen, 8, 20, 14, 12);
            g.DrawLine(pen, 14, 12, 24, 10);
        }
        var hIcon = bmp.GetHicon();
        return Icon.FromHandle(hIcon);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _icon.Visible = false;
            _icon.ContextMenuStrip?.Dispose();
            _icon.Dispose();
        }
        catch
        {
            // ignore
        }
    }
}
