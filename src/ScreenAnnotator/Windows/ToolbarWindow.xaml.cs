using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using ScreenAnnotator.Models;

namespace ScreenAnnotator.Windows;

public partial class ToolbarWindow : Window
{
    private readonly MainWindow _main;
    private bool _suppress;

    private static readonly (string Name, ToolType Tool)[] ToolsBeforeShape =
    [
        ("画笔", ToolType.Pen),
        ("临时画笔", ToolType.TempPen),
        ("荧光笔", ToolType.Highlighter),
        ("直线", ToolType.Line),
        ("箭头", ToolType.Arrow)
    ];

    private static readonly (string Name, ToolType Tool)[] ToolsAfterShape =
    [
        ("文字", ToolType.Text),
        ("移动", ToolType.Move),
        ("对象橡皮", ToolType.EraserObject),
        ("笔刷橡皮", ToolType.EraserBrush),
        ("框选删除", ToolType.EraserMarquee)
    ];

    private static readonly System.Windows.Media.Color[] PresetColors =
    [
        Colors.Red, Colors.Orange, Colors.Gold, Colors.LimeGreen,
        Colors.DeepSkyBlue, Colors.MediumPurple, Colors.White, Colors.Black
    ];

    private ToggleButton? _shapeToggle;
    private Popup? _shapePopup;
    private TextBlock? _shapeLabel;

    public ToolbarWindow(MainWindow main)
    {
        _main = main;
        InitializeComponent();
        BuildToolButtons();
        BuildColorButtons();
        RefreshUi();
    }

    private void BuildToolButtons()
    {
        foreach (var (name, tool) in ToolsBeforeShape)
            ToolPanel.Children.Add(MakeToolButton(name, tool));

        _shapeToggle = new ToggleButton
        {
            Margin = new Thickness(0, 0, 3, 2),
            Padding = new Thickness(7, 3, 7, 3),
            FontFamily = new FontFamily("Microsoft YaHei UI"),
            FontSize = 12,
            Focusable = false,
            Cursor = Cursors.Arrow,
            ToolTip = "形状"
        };
        _shapeLabel = new TextBlock { Text = "形状·矩形", VerticalAlignment = VerticalAlignment.Center };
        _shapeToggle.Content = _shapeLabel;
        _shapeToggle.Click += (_, _) =>
        {
            if (_shapePopup != null)
                _shapePopup.IsOpen = _shapeToggle.IsChecked == true;
            if (_shapeToggle.IsChecked == true)
                _main.SetTool(ToolType.Shape);
        };

        var shapePanel = new WrapPanel { MaxWidth = 280 };
        foreach (var (kind, label) in ShapeKindInfo.All)
        {
            var k = kind;
            var btn = new Button
            {
                Content = label,
                Margin = new Thickness(0, 0, 4, 4),
                Padding = new Thickness(8, 3, 8, 3),
                FontFamily = new FontFamily("Microsoft YaHei UI"),
                FontSize = 12,
                Focusable = false,
                Cursor = Cursors.Arrow
            };
            btn.Click += (_, _) =>
            {
                _main.SetShapeKind(k);
                if (_shapePopup != null) _shapePopup.IsOpen = false;
                if (_shapeToggle != null) _shapeToggle.IsChecked = false;
                RefreshUi();
            };
            shapePanel.Children.Add(btn);
        }

        _shapePopup = new Popup
        {
            PlacementTarget = _shapeToggle,
            Placement = PlacementMode.Bottom,
            StaysOpen = false,
            AllowsTransparency = true,
            Child = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xEE, 0xF5, 0xF5, 0xF5)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, 0, 0, 0)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8),
                Child = shapePanel,
                Cursor = Cursors.Arrow
            }
        };
        _shapePopup.Closed += (_, _) =>
        {
            if (_shapeToggle != null) _shapeToggle.IsChecked = false;
            _main.SyncChromePointerState();
        };

        ToolPanel.Children.Add(_shapeToggle);

        foreach (var (name, tool) in ToolsAfterShape)
            ToolPanel.Children.Add(MakeToolButton(name, tool));
    }

    private Button MakeToolButton(string name, ToolType tool)
    {
        var btn = new Button
        {
            Content = name,
            Tag = tool,
            Margin = new Thickness(0, 0, 3, 2),
            Padding = new Thickness(7, 3, 7, 3),
            FontFamily = new FontFamily("Microsoft YaHei UI"),
            FontSize = 12,
            ToolTip = name,
            Focusable = false,
            Cursor = Cursors.Arrow
        };
        btn.Click += (_, _) =>
        {
            ClosePopups();
            _main.SetTool(tool);
        };
        return btn;
    }

    private void BuildColorButtons()
    {
        foreach (var c in PresetColors)
        {
            var btn = new Button
            {
                Width = 22,
                Height = 22,
                Margin = new Thickness(0, 0, 4, 0),
                Background = new SolidColorBrush(c),
                BorderBrush = Brushes.Gray,
                BorderThickness = new Thickness(1),
                Tag = c,
                ToolTip = c.ToString(),
                Focusable = false,
                Cursor = Cursors.Arrow
            };
            btn.Click += (_, _) =>
            {
                _main.SetColor(c);
                ColorPopup.IsOpen = false;
                BtnColorToggle.IsChecked = false;
            };
            ColorPanel.Children.Add(btn);
        }
    }

    public void RefreshUi()
    {
        _suppress = true;
        foreach (var child in ToolPanel.Children)
        {
            if (child is not Button btn || btn.Tag is not ToolType tool) continue;
            var selected = tool == _main.CurrentTool;
            btn.FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal;
            btn.BorderBrush = selected ? new SolidColorBrush(Color.FromRgb(33, 150, 243)) : Brushes.Gray;
            btn.BorderThickness = new Thickness(selected ? 1.5 : 1);
        }

        if (_shapeToggle != null && _shapeLabel != null)
        {
            var shapeOn = _main.CurrentTool == ToolType.Shape;
            _shapeLabel.Text = "形状·" + ShapeKindInfo.Label(_main.CurrentShapeKind);
            _shapeToggle.FontWeight = shapeOn ? FontWeights.SemiBold : FontWeights.Normal;
            _shapeToggle.BorderBrush = shapeOn
                ? new SolidColorBrush(Color.FromRgb(33, 150, 243))
                : Brushes.Gray;
            _shapeToggle.BorderThickness = new Thickness(shapeOn ? 1.5 : 1);
        }

        StrokeThin.IsChecked = _main.StrokeTier == StrokeThicknessTier.Thin;
        StrokeMedium.IsChecked = _main.StrokeTier == StrokeThicknessTier.Medium;
        StrokeThick.IsChecked = _main.StrokeTier == StrokeThicknessTier.Thick;
        FontSmall.IsChecked = _main.FontTier == FontSizeTier.Small;
        FontMedium.IsChecked = _main.FontTier == FontSizeTier.Medium;
        FontLarge.IsChecked = _main.FontTier == FontSizeTier.Large;

        ColorSwatch.Background = new SolidColorBrush(_main.CurrentColor);
        StrokeSummary.Text = _main.StrokeTier switch
        {
            StrokeThicknessTier.Thin => "粗细·细",
            StrokeThicknessTier.Thick => "粗细·粗",
            _ => "粗细·中"
        };
        FontSummary.Text = _main.FontTier switch
        {
            FontSizeTier.Small => "字号·小",
            FontSizeTier.Large => "字号·大",
            _ => "字号·中"
        };

        var isBoard = _main.CurrentContentMode == ContentMode.Board;
        BrandText.Text = isBoard ? "空白白板" : "屏幕标注";
        StyleModeButton(BtnContentOverlay, !isBoard);
        StyleModeButton(BtnContentBoard, isBoard);

        var active = _main.IsActiveMode;
        StyleModeButton(BtnModeActive, active);
        StyleModeButton(BtnModePassthrough, !active);
        BtnModePassthrough.Content = isBoard ? "收起" : "穿透";
        BtnModePassthrough.ToolTip = isBoard
            ? "收起完整工具栏（白板不点穿桌面）"
            : "穿透模式：笔迹仍可见，点击下层";

        RefreshScreenCombo();

        // 移动工具或移动模式开时高亮「移动」按钮
        foreach (var child in ToolPanel.Children)
        {
            if (child is not Button btn || btn.Tag is not ToolType.Move) continue;
            var moveOn = _main.CurrentTool == ToolType.Move || _main.MoveModeEnabled;
            if (moveOn && _main.CurrentTool != ToolType.Move)
            {
                btn.FontWeight = FontWeights.SemiBold;
                btn.BorderBrush = new SolidColorBrush(Color.FromRgb(255, 152, 0));
                btn.BorderThickness = new Thickness(1.5);
            }
        }

        var moveHint = _main.MoveModeEnabled ? "移动:开" : "移动:关";
        var selCount = _main.SelectedObjectCount;
        var selHint = selCount > 0 ? $" · 已选 {selCount} 个" : "";
        StatusText.Text = active
            ? $"可绘制 · {moveHint}（右键短按切换；长按拖框选）{selHint}"
            : (isBoard ? "工具栏已收起（白板）" : "笔迹可见·可点下层");
        _suppress = false;

        if (IsMouseOver || ColorPopup.IsOpen || StrokePopup.IsOpen || FontPopup.IsOpen || (_shapePopup?.IsOpen == true))
            EnforceArrowCursor();
    }

    private void RefreshScreenCombo()
    {
        var options = _main.GetScreenOptions();
        var selected = _main.SelectedScreenIndex;
        ScreenCombo.Items.Clear();
        foreach (var (index, label) in options)
            ScreenCombo.Items.Add(new ComboBoxItem { Content = label, Tag = index });
        if (ScreenCombo.Items.Count > 0)
            ScreenCombo.SelectedIndex = Math.Clamp(selected, 0, ScreenCombo.Items.Count - 1);
    }

    private static void StyleModeButton(Button btn, bool selected)
    {
        btn.FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal;
        btn.Background = selected
            ? new SolidColorBrush(Color.FromRgb(33, 150, 243))
            : new SolidColorBrush(Color.FromArgb(0x88, 0xFF, 0xFF, 0xFF));
        btn.Foreground = selected ? Brushes.White : new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x22));
        btn.BorderThickness = new Thickness(0);
        btn.Cursor = Cursors.Arrow;
    }

    private void EnforceArrowCursor()
    {
        Cursor = Cursors.Arrow;
        ForceCursor = true;
        Mouse.OverrideCursor = Cursors.Arrow;
    }

    private void OnChromeMouseEnter(object sender, MouseEventArgs e)
    {
        _main.NotifyPointerOverChrome(true);
        EnforceArrowCursor();
    }

    private void OnChromeMouseMove(object sender, MouseEventArgs e)
    {
        // Capture 释放后可能错过 Enter，Move 时强制同步（BUG-002）
        _main.NotifyPointerOverChrome(true);
        EnforceArrowCursor();
    }

    private void OnChromeMouseLeave(object sender, MouseEventArgs e)
    {
        // Popup 打开时离开客户区不恢复画笔光标
        if (ColorPopup.IsOpen || StrokePopup.IsOpen || FontPopup.IsOpen)
            return;
        _main.SyncChromePointerState();
    }

    private void OnColorToggle(object sender, RoutedEventArgs e)
    {
        ColorPopup.IsOpen = BtnColorToggle.IsChecked == true;
        if (ColorPopup.IsOpen)
        {
            StrokePopup.IsOpen = false;
            FontPopup.IsOpen = false;
            BtnStrokeToggle.IsChecked = false;
            BtnFontToggle.IsChecked = false;
            _main.NotifyPointerOverChrome(true);
            EnforceArrowCursor();
        }
    }

    private void OnStrokeToggle(object sender, RoutedEventArgs e)
    {
        StrokePopup.IsOpen = BtnStrokeToggle.IsChecked == true;
        if (StrokePopup.IsOpen)
        {
            ColorPopup.IsOpen = false;
            FontPopup.IsOpen = false;
            BtnColorToggle.IsChecked = false;
            BtnFontToggle.IsChecked = false;
            _main.NotifyPointerOverChrome(true);
            EnforceArrowCursor();
        }
    }

    private void OnFontToggle(object sender, RoutedEventArgs e)
    {
        FontPopup.IsOpen = BtnFontToggle.IsChecked == true;
        if (FontPopup.IsOpen)
        {
            ColorPopup.IsOpen = false;
            StrokePopup.IsOpen = false;
            BtnColorToggle.IsChecked = false;
            BtnStrokeToggle.IsChecked = false;
            _main.NotifyPointerOverChrome(true);
            EnforceArrowCursor();
        }
    }

    private void OnColorPopupClosed(object? sender, EventArgs e)
    {
        BtnColorToggle.IsChecked = false;
        _main.SyncChromePointerState();
    }

    private void OnStrokePopupClosed(object? sender, EventArgs e)
    {
        BtnStrokeToggle.IsChecked = false;
        _main.SyncChromePointerState();
    }

    private void OnFontPopupClosed(object? sender, EventArgs e)
    {
        BtnFontToggle.IsChecked = false;
        _main.SyncChromePointerState();
    }

    private void OnCollapse(object sender, RoutedEventArgs e)
    {
        ClosePopups();
        _main.CollapseToolbar();
    }

    private void OnModeActive(object sender, RoutedEventArgs e)
    {
        ClosePopups();
        _main.EnterActiveMode(expandToolbar: true, activateMain: false);
        EnforceArrowCursor();
        _main.NotifyPointerOverChrome(true);
    }

    private void OnModePassthrough(object sender, RoutedEventArgs e)
    {
        ClosePopups();
        _main.EnterPassthroughMode();
    }

    private void OnContentOverlay(object sender, RoutedEventArgs e)
    {
        ClosePopups();
        _main.SetContentMode(ContentMode.Overlay);
        EnforceArrowCursor();
        _main.NotifyPointerOverChrome(true);
    }

    private void OnContentBoard(object sender, RoutedEventArgs e)
    {
        ClosePopups();
        _main.SetContentMode(ContentMode.Board);
        EnforceArrowCursor();
        _main.NotifyPointerOverChrome(true);
    }

    private void OnScreenChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress) return;
        if (ScreenCombo.SelectedItem is ComboBoxItem { Tag: int index })
            _main.SelectScreen(index);
    }

    private void ClosePopups()
    {
        ColorPopup.IsOpen = false;
        StrokePopup.IsOpen = false;
        FontPopup.IsOpen = false;
        BtnColorToggle.IsChecked = false;
        BtnStrokeToggle.IsChecked = false;
        BtnFontToggle.IsChecked = false;
        if (_shapePopup != null) _shapePopup.IsOpen = false;
        if (_shapeToggle != null) _shapeToggle.IsChecked = false;
    }

    private void OnCustomColor(object sender, RoutedEventArgs e)
    {
        _main.PickCustomColor();
        ColorPopup.IsOpen = false;
        BtnColorToggle.IsChecked = false;
    }

    private void OnStrokeChanged(object sender, RoutedEventArgs e)
    {
        if (_suppress) return;
        if (StrokeThin.IsChecked == true) _main.SetStrokeTier(StrokeThicknessTier.Thin);
        else if (StrokeMedium.IsChecked == true) _main.SetStrokeTier(StrokeThicknessTier.Medium);
        else if (StrokeThick.IsChecked == true) _main.SetStrokeTier(StrokeThicknessTier.Thick);
        StrokePopup.IsOpen = false;
        BtnStrokeToggle.IsChecked = false;
    }

    private void OnFontChanged(object sender, RoutedEventArgs e)
    {
        if (_suppress) return;
        if (FontSmall.IsChecked == true) _main.SetFontTier(FontSizeTier.Small);
        else if (FontMedium.IsChecked == true) _main.SetFontTier(FontSizeTier.Medium);
        else if (FontLarge.IsChecked == true) _main.SetFontTier(FontSizeTier.Large);
        FontPopup.IsOpen = false;
        BtnFontToggle.IsChecked = false;
    }

    private void OnUndo(object sender, RoutedEventArgs e) => _main.Undo();
    private void OnRedo(object sender, RoutedEventArgs e) => _main.Redo();
    private void OnClear(object sender, RoutedEventArgs e) => _main.ClearCanvas();
    private void OnSave(object sender, RoutedEventArgs e) => _main.SaveBoard();
    private void OnOpen(object sender, RoutedEventArgs e) => _main.OpenBoard();
    private void OnExport(object sender, RoutedEventArgs e) => _main.ExportPng();
    private void OnCapture(object sender, RoutedEventArgs e) => _main.CaptureWithAnnotations();
    private void OnRecord(object sender, RoutedEventArgs e) => _main.ToggleScreenRecording();
    private void OnHelp(object sender, RoutedEventArgs e) => _main.OpenHelp();
    private void OnSettings(object sender, RoutedEventArgs e) => _main.OpenSettings();
    private void OnExit(object sender, RoutedEventArgs e) => _main.ExitApp();
}
