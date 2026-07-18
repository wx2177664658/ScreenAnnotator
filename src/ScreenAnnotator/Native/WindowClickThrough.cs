using System.Windows;
using System.Windows.Interop;

namespace ScreenAnnotator.Native;

internal static class WindowClickThrough
{
    public static void SetClickThrough(Window window, bool enabled)
    {
        var helper = new WindowInteropHelper(window);
        var hwnd = helper.EnsureHandle();
        var style = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);

        if (enabled)
            style |= NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_LAYERED;
        else
            style &= ~NativeMethods.WS_EX_TRANSPARENT;

        // Keep layered for transparency
        style |= NativeMethods.WS_EX_LAYERED;
        NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE, style);
    }

    public static void SetToolWindow(Window window)
    {
        var helper = new WindowInteropHelper(window);
        var hwnd = helper.EnsureHandle();
        var style = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
        style |= NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_LAYERED;
        NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE, style);
    }

    /// <summary>
    /// 将 chrome 窗抬到最顶（不激活），避免主窗 Activate/绘制后盖住工具栏（BUG-003）。
    /// </summary>
    public static void BringToTopMost(Window window)
    {
        if (!window.IsVisible) return;
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        NativeMethods.SetWindowPos(
            hwnd,
            NativeMethods.HWND_TOPMOST,
            0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
        window.Topmost = true;
    }
}
