using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ScreenAnnotator.Models;
using ScreenAnnotator.Services;

namespace ScreenAnnotator.Windows;

public partial class HelpWindow : Window
{
    public HelpWindow()
    {
        InitializeComponent();
        BuildContent();
        var ver = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = ver != null ? $"版本 {ver.Major}.{ver.Minor}.{ver.Build}" : "";
    }

    private void BuildContent()
    {
        AddSection("两种模式",
            "屏幕标注：透明叠在桌面/PPT 上画。",
            "空白白板：不透明白底，适合纯板书。",
            "顶栏可切换；两套笔迹互不影响。");

        AddSection("标注与穿透",
            "标注模式：可绘制。",
            "穿透（屏幕标注）：可操作下层窗口，笔迹仍可见。",
            "收起（空白白板）：收起工具栏，不能点穿桌面。",
            $"显示/隐藏标注层默认：{Gesture("toggle_overlay")}。");

        AddSection("画笔与临时画笔",
            "画笔：松手后笔迹保留。",
            "临时画笔：讲解指位用，松手后约 0.3 秒淡出消失，不进保存/撤销。",
            "颜色、粗细使用工具栏当前样式。");

        AddSection("形状与线条",
            "形状：点「形状」展开面板（矩形、圆角矩形、圆、三角、菱形、多边形、星形、平行四边形、梯形等），对角拖拽绘制。",
            "直线、箭头在一级工具栏，不进形状面板。");

        AddSection("移动 / 缩放 / 旋转",
            "右键短按：开/关移动模式。",
            "移动模式或「移动」工具：拖对象移动；拖角点缩放；拖橙色圆点旋转（Shift 吸附 15°）。",
            "右键长按拖拽：框选多个，再一并移动。",
            "Delete / Backspace：删除选中（编辑文字时除外）。");

        AddSection("橡皮",
            "对象橡皮：单击删除整段对象。",
            "笔刷橡皮：拖过笔迹局部擦除。",
            "框选删除：拖框删除相交对象。");

        AddSection("清屏 / 保存 / 导出 / 截屏 / 录屏",
            $"清屏（{Gesture("clear_canvas")}）：有笔迹时二次确认，确认后仍可撤销。",
            $"保存（{Gesture("save")}）：保存 .board 工程（含 A/B 两套笔迹）。",
            "导出 PNG：导出当前形态笔迹。",
            "截屏：选中屏画面 + 标注（白板模式为白底笔迹）。",
            $"录屏（{Gesture("toggle_record")}）：录选中屏且含标注；麦克风/系统声可选，默认均关。");

        AddSection("屏幕",
            "工具栏「屏幕」下拉：选择在哪块显示器上标注、截屏与录屏。");

        AddHotkeySection();

        AddSection("工具栏",
            "吸顶显示；点「收起」只留顶部小按钮，再点可展开。",
            "颜色 / 粗细 / 字号：点击按钮展开选择。");
    }

    private void AddHotkeySection()
    {
        var lines = new List<string>
        {
            "打开「设置」可修改快捷键；支持键盘与鼠标中键/侧键/滚轮等。",
            "单独左键/右键不可绑定（右键已用于移动/框选）。",
            "默认常用键："
        };
        foreach (var (actionId, gesture) in AppSettings.DefaultHotkeys)
        {
            var label = AppSettings.HotkeyLabels.GetValueOrDefault(actionId, actionId);
            lines.Add($"· {label}：{HotkeyService.FormatGestureDisplay(gesture)}");
        }
        lines.Add("· 删除选中：Delete / Backspace");
        lines.Add("· 取消当前绘制：Esc（应用内）");
        AddSection("快捷键", lines.ToArray());
    }

    private static string Gesture(string actionId)
    {
        var g = AppSettings.DefaultHotkeys.GetValueOrDefault(actionId, "");
        return string.IsNullOrEmpty(g) ? "见快捷键设置" : HotkeyService.FormatGestureDisplay(g);
    }

    private void AddSection(string title, params string[] bullets)
    {
        ContentPanel.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A)),
            Margin = new Thickness(0, 10, 0, 4)
        });

        foreach (var line in bullets)
        {
            ContentPanel.Children.Add(new TextBlock
            {
                Text = line,
                FontSize = 12.5,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
                Margin = new Thickness(0, 0, 0, 3),
                LineHeight = 20
            });
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
