using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ScreenAnnotator.Models;
using ScreenAnnotator.Services;

namespace ScreenAnnotator.Windows;

public partial class SettingsWindow : Window
{
    private readonly SettingsService _settings;
    private readonly Action _onApplied;
    private readonly Dictionary<string, TextBox> _boxes = new();
    private Dictionary<string, string> _draft = new();
    private string? _recordingAction;

    public SettingsWindow(SettingsService settings, Action onApplied)
    {
        _settings = settings;
        _onApplied = onApplied;
        InitializeComponent();
        LoadRows();
        PreviewKeyDown += OnPreviewKeyDown;
        PreviewMouseDown += OnPreviewMouseDown;
        PreviewMouseWheel += OnPreviewMouseWheel;
    }

    private void LoadRows()
    {
        HotkeyList.Children.Clear();
        _boxes.Clear();
        _draft = new Dictionary<string, string>(_settings.Settings.Hotkeys);

        foreach (var actionId in AppSettings.DefaultHotkeys.Keys)
        {
            var label = AppSettings.HotkeyLabels.GetValueOrDefault(actionId, actionId);
            var gesture = _draft.GetValueOrDefault(actionId, AppSettings.DefaultHotkeys[actionId]);

            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
            var title = new TextBlock
            {
                Text = label,
                Width = 140,
                VerticalAlignment = VerticalAlignment.Center
            };
            DockPanel.SetDock(title, Dock.Left);

            var recordBtn = new Button
            {
                Content = "录制",
                Width = 56,
                Margin = new Thickness(8, 0, 0, 0),
                Tag = actionId,
                Padding = new Thickness(6, 2, 6, 2)
            };
            DockPanel.SetDock(recordBtn, Dock.Right);
            recordBtn.Click += OnRecordClick;

            var box = new TextBox
            {
                Text = HotkeyService.FormatGestureDisplay(gesture),
                IsReadOnly = false,
                VerticalContentAlignment = VerticalAlignment.Center,
                Padding = new Thickness(6, 4, 6, 4),
                Tag = actionId,
                ToolTip = "可录制键盘/鼠标/滚轮，或手动输入（如 F6、MouseMiddle、滚轮上、Ctrl+鼠标中键）"
            };
            box.LostKeyboardFocus += OnBoxLostFocus;
            _boxes[actionId] = box;

            row.Children.Add(title);
            row.Children.Add(recordBtn);
            row.Children.Add(box);
            HotkeyList.Children.Add(row);
        }
    }

    private void OnBoxLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_recordingAction != null) return;
        if (sender is not TextBox box || box.Tag is not string actionId) return;
        _draft[actionId] = box.Text.Trim();
    }

    private void OnRecordClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string actionId) return;
        SyncDraftFromBoxes();
        _recordingAction = actionId;
        if (_boxes.TryGetValue(actionId, out var box))
        {
            box.Text = "请按下键盘按键或鼠标键 / 拨动滚轮…（Esc 取消）";
            box.IsReadOnly = true;
            box.Background = new SolidColorBrush(Color.FromRgb(255, 249, 196));
        }
        Focus();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_recordingAction == null) return;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
            or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin or Key.None)
        {
            e.Handled = true;
            return;
        }

        if (key == Key.Escape)
        {
            FinishRecording(_draft.GetValueOrDefault(_recordingAction,
                AppSettings.DefaultHotkeys[_recordingAction]));
            e.Handled = true;
            return;
        }

        var mods = Keyboard.Modifiers;
        var gesture = HotkeyService.FormatGesture(mods, key);
        FinishRecording(gesture);
        e.Handled = true;
    }

    private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_recordingAction == null) return;
        // 录制按钮点击不计入
        if (e.OriginalSource is DependencyObject d && FindAncestor<Button>(d) != null)
            return;

        var button = e.ChangedButton switch
        {
            MouseButton.Left => MouseGestureButton.Left,
            MouseButton.Right => MouseGestureButton.Right,
            MouseButton.Middle => MouseGestureButton.Middle,
            MouseButton.XButton1 => MouseGestureButton.XButton1,
            MouseButton.XButton2 => MouseGestureButton.XButton2,
            _ => MouseGestureButton.None
        };
        if (button == MouseGestureButton.None) return;

        var mods = Keyboard.Modifiers;
        if (!HotkeyService.IsAllowedMouseGesture(ToNativeMods(mods), button))
        {
            MessageBox.Show(
                "单独左键/右键不可绑定为业务快捷键（右键已用于移动/框选；左键会破坏点击）。\n请改用中键、侧键、滚轮，或使用 Ctrl/Shift/Alt + 鼠标键。",
                "快捷键设置", MessageBoxButton.OK, MessageBoxImage.Information);
            e.Handled = true;
            return;
        }

        FinishRecording(HotkeyService.FormatMouseGesture(mods, button));
        e.Handled = true;
    }

    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_recordingAction == null) return;
        var button = e.Delta > 0 ? MouseGestureButton.WheelUp : MouseGestureButton.WheelDown;
        FinishRecording(HotkeyService.FormatMouseGesture(Keyboard.Modifiers, button));
        e.Handled = true;
    }

    private static uint ToNativeMods(ModifierKeys mods)
    {
        uint m = 0;
        if (mods.HasFlag(ModifierKeys.Control)) m |= Native.NativeMethods.MOD_CONTROL;
        if (mods.HasFlag(ModifierKeys.Shift)) m |= Native.NativeMethods.MOD_SHIFT;
        if (mods.HasFlag(ModifierKeys.Alt)) m |= Native.NativeMethods.MOD_ALT;
        if (mods.HasFlag(ModifierKeys.Windows)) m |= Native.NativeMethods.MOD_WIN;
        return m;
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current != null)
        {
            if (current is T t) return t;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private void FinishRecording(string gesture)
    {
        if (_recordingAction == null) return;
        var action = _recordingAction;
        _recordingAction = null;
        var norm = HotkeyService.NormalizeGesture(gesture) ?? gesture;
        _draft[action] = norm;
        if (_boxes.TryGetValue(action, out var box))
        {
            box.Text = HotkeyService.FormatGestureDisplay(norm);
            box.IsReadOnly = false;
            box.Background = Brushes.White;
        }
    }

    private void SyncDraftFromBoxes()
    {
        foreach (var (id, box) in _boxes)
        {
            if (_recordingAction == id) continue;
            _draft[id] = box.Text.Trim();
        }
    }

    private void OnReset(object sender, RoutedEventArgs e)
    {
        _draft = new Dictionary<string, string>(AppSettings.DefaultHotkeys);
        foreach (var (id, box) in _boxes)
        {
            box.Text = HotkeyService.FormatGestureDisplay(_draft[id]);
            box.IsReadOnly = false;
            box.Background = Brushes.White;
        }
        _recordingAction = null;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (_recordingAction != null)
        {
            MessageBox.Show("请先完成或按 Esc 取消当前录制。", "快捷键设置",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        SyncDraftFromBoxes();

        var normalized = new Dictionary<string, string>();
        foreach (var (actionId, raw) in _draft)
        {
            if (string.Equals(raw, "Esc", StringComparison.OrdinalIgnoreCase)
                || string.Equals(raw, "Escape", StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show("Esc 不可注册为业务快捷键（用于取消录制）。",
                    "快捷键设置", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!HotkeyService.TryParseAnyGesture(raw, out var mods, out _, out var mouse))
            {
                MessageBox.Show(
                    $"「{AppSettings.HotkeyLabels.GetValueOrDefault(actionId, actionId)}」的快捷键格式无效。\n可输入如 F6、MouseMiddle、滚轮上、Ctrl+鼠标中键。",
                    "快捷键设置", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (mouse != MouseGestureButton.None && !HotkeyService.IsAllowedMouseGesture(mods, mouse))
            {
                MessageBox.Show(
                    $"「{AppSettings.HotkeyLabels.GetValueOrDefault(actionId, actionId)}」：单独左键/右键不可绑定。\n请改用中键、侧键、滚轮，或加 Ctrl/Shift/Alt。",
                    "快捷键设置", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var norm = HotkeyService.NormalizeGesture(raw);
            if (norm == null)
            {
                MessageBox.Show($"「{AppSettings.HotkeyLabels.GetValueOrDefault(actionId, actionId)}」格式无效。",
                    "快捷键设置", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            normalized[actionId] = norm;
            if (_boxes.TryGetValue(actionId, out var box))
                box.Text = HotkeyService.FormatGestureDisplay(norm);
        }

        var used = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (actionId, gesture) in normalized)
        {
            if (used.TryGetValue(gesture, out var other))
            {
                MessageBox.Show(
                    $"快捷键「{HotkeyService.FormatGestureDisplay(gesture)}」冲突：\n「{AppSettings.HotkeyLabels[other]}」与「{AppSettings.HotkeyLabels[actionId]}」\n请修改后再保存。",
                    "快捷键设置", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            used[gesture] = actionId;
        }

        _draft = normalized;
        _settings.Settings.Hotkeys = new Dictionary<string, string>(_draft);
        _settings.Save();
        _onApplied();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
