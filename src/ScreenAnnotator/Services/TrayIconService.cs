using System.Drawing;
using System.IO;
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
            Icon = LoadAppIcon()
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

    private static Icon LoadAppIcon()
    {
        try
        {
            var exe = Environment.ProcessPath ?? System.Windows.Forms.Application.ExecutablePath;
            if (!string.IsNullOrEmpty(exe) && File.Exists(exe))
            {
                var extracted = Icon.ExtractAssociatedIcon(exe);
                if (extracted != null)
                    return extracted;
            }
        }
        catch
        {
            // fall through
        }

        return CreateFallbackIcon();
    }

    private static Icon CreateFallbackIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(DrawingColor.Transparent);
            using var brush = new SolidBrush(DrawingColor.FromArgb(220, 70, 120, 200));
            g.FillEllipse(brush, 1, 1, 30, 30);
            using var board = new SolidBrush(DrawingColor.White);
            g.FillRectangle(board, 8, 8, 16, 14);
            using var pen = new DrawingPen(DrawingColor.FromArgb(60, 60, 70), 2);
            g.DrawRectangle(pen, 8, 8, 16, 14);
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
