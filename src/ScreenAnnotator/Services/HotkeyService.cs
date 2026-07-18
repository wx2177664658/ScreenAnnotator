using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using ScreenAnnotator.Native;

namespace ScreenAnnotator.Services;

public enum MouseGestureButton
{
    None = 0,
    Left,
    Right,
    Middle,
    XButton1,
    XButton2,
    WheelUp,
    WheelDown
}

public sealed class HotkeyService : IDisposable
{
    private readonly Window _host;
    private HwndSource? _source;
    private readonly Dictionary<int, string> _idToAction = [];
    private readonly Dictionary<string, int> _actionToId = [];
    private readonly List<(string ActionId, uint Modifiers, MouseGestureButton Button)> _mouseBindings = [];
    private int _nextId = 1;
    private bool _disposed;

    private IntPtr _mouseHook = IntPtr.Zero;
    private NativeMethods.LowLevelMouseProc? _mouseProc; // keep alive

    public event Action<string>? HotkeyPressed;

    public HotkeyService(Window host)
    {
        _host = host;
        _host.SourceInitialized += OnSourceInitialized;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var helper = new WindowInteropHelper(_host);
        _source = HwndSource.FromHwnd(helper.Handle);
        _source?.AddHook(WndProc);
    }

    public void EnsureInitialized()
    {
        if (_source != null) return;
        var helper = new WindowInteropHelper(_host);
        helper.EnsureHandle();
        _source = HwndSource.FromHwnd(helper.Handle);
        _source?.AddHook(WndProc);
    }

    public IReadOnlyList<(string ActionId, string Gesture, string? Error)> RegisterAll(
        IReadOnlyDictionary<string, string> hotkeys)
    {
        EnsureInitialized();
        UnregisterAll();
        var results = new List<(string, string, string?)>();
        foreach (var (actionId, gesture) in hotkeys)
        {
            var error = TryRegister(actionId, gesture);
            results.Add((actionId, gesture, error));
        }

        if (_mouseBindings.Count > 0)
        {
            var hookErr = InstallMouseHook();
            if (hookErr != null)
            {
                for (var i = 0; i < results.Count; i++)
                {
                    if (_mouseBindings.Any(b => b.ActionId == results[i].Item1))
                        results[i] = (results[i].Item1, results[i].Item2, hookErr);
                }
            }
        }

        return results;
    }

    public string? TryRegister(string actionId, string gesture)
    {
        EnsureInitialized();
        if (!TryParseAnyGesture(gesture, out var modifiers, out var key, out var mouse))
            return "无效的快捷键格式";

        if (mouse != MouseGestureButton.None)
        {
            if (!IsAllowedMouseGesture(modifiers, mouse))
                return "单独左键/右键不可绑定；请改用中键、侧键、滚轮，或加 Ctrl/Shift/Alt";

            _mouseBindings.Add((actionId, modifiers, mouse));
            return null;
        }

        var hwnd = new WindowInteropHelper(_host).Handle;
        var id = _nextId++;
        if (!NativeMethods.RegisterHotKey(hwnd, id, modifiers, (uint)KeyInterop.VirtualKeyFromKey(key)))
        {
            var err = Marshal.GetLastWin32Error();
            return $"注册失败（错误码 {err}），请更换快捷键";
        }

        _idToAction[id] = actionId;
        _actionToId[actionId] = id;
        return null;
    }

    public void UnregisterAll()
    {
        // 先清空绑定，再卸钩，避免回调里仍匹配业务动作
        _mouseBindings.Clear();
        UninstallMouseHook();

        var hwnd = new WindowInteropHelper(_host).Handle;
        if (hwnd == IntPtr.Zero) return;
        foreach (var id in _idToAction.Keys.ToList())
            NativeMethods.UnregisterHotKey(hwnd, id);
        _idToAction.Clear();
        _actionToId.Clear();
    }

    private string? InstallMouseHook()
    {
        if (_mouseHook != IntPtr.Zero) return null;
        _mouseProc = MouseHookCallback;
        using var curProcess = System.Diagnostics.Process.GetCurrentProcess();
        using var curModule = curProcess.MainModule!;
        _mouseHook = NativeMethods.SetWindowsHookEx(
            NativeMethods.WH_MOUSE_LL,
            _mouseProc,
            NativeMethods.GetModuleHandle(curModule.ModuleName),
            0);
        if (_mouseHook == IntPtr.Zero)
        {
            var err = Marshal.GetLastWin32Error();
            return $"鼠标快捷键注册失败（错误码 {err}）";
        }
        return null;
    }

    private void UninstallMouseHook()
    {
        var hook = _mouseHook;
        _mouseHook = IntPtr.Zero;
        _mouseProc = null;
        if (hook == IntPtr.Zero) return;
        NativeMethods.UnhookWindowsHookEx(hook);
    }

    private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        var hook = _mouseHook;
        // 已退出/卸钩：立刻放行，不做任何派发（避免退出卡顿）
        if (_disposed || hook == IntPtr.Zero || nCode < 0 || _mouseBindings.Count == 0)
            return NativeMethods.CallNextHookEx(hook, nCode, wParam, lParam);

        try
        {
            if (_host.Dispatcher.HasShutdownStarted)
                return NativeMethods.CallNextHookEx(hook, nCode, wParam, lParam);

            var msg = wParam.ToInt32();
            var data = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
            MouseGestureButton button = MouseGestureButton.None;

            if (msg == NativeMethods.WM_LBUTTONDOWN) button = MouseGestureButton.Left;
            else if (msg == NativeMethods.WM_RBUTTONDOWN) button = MouseGestureButton.Right;
            else if (msg == NativeMethods.WM_MBUTTONDOWN) button = MouseGestureButton.Middle;
            else if (msg == NativeMethods.WM_XBUTTONDOWN)
            {
                var xb = (data.mouseData >> 16) & 0xFFFF;
                button = xb == NativeMethods.XBUTTON1 ? MouseGestureButton.XButton1
                    : xb == NativeMethods.XBUTTON2 ? MouseGestureButton.XButton2
                    : MouseGestureButton.None;
            }
            else if (msg == NativeMethods.WM_MOUSEWHEEL)
            {
                var delta = (short)(data.mouseData >> 16);
                button = delta > 0 ? MouseGestureButton.WheelUp : MouseGestureButton.WheelDown;
            }

            if (button != MouseGestureButton.None)
            {
                var mods = ReadCurrentModifiers();
                string? action = null;
                foreach (var b in _mouseBindings)
                {
                    if (b.Button == button && b.Modifiers == mods)
                    {
                        action = b.ActionId;
                        break;
                    }
                }

                if (action != null)
                {
                    // 异步轻量派发；禁止同步 Invoke（易与卸钩死锁）
                    var act = action;
                    _host.Dispatcher.BeginInvoke(
                        () =>
                        {
                            if (!_disposed)
                                HotkeyPressed?.Invoke(act);
                        },
                        System.Windows.Threading.DispatcherPriority.Input);
                }
            }
        }
        catch
        {
            // keep hook light
        }

        return NativeMethods.CallNextHookEx(hook, nCode, wParam, lParam);
    }

    private static uint ReadCurrentModifiers()
    {
        uint mods = 0;
        if ((NativeMethods.GetAsyncKeyState(NativeMethods.VK_CONTROL) & 0x8000) != 0)
            mods |= NativeMethods.MOD_CONTROL;
        if ((NativeMethods.GetAsyncKeyState(NativeMethods.VK_SHIFT) & 0x8000) != 0)
            mods |= NativeMethods.MOD_SHIFT;
        if ((NativeMethods.GetAsyncKeyState(NativeMethods.VK_MENU) & 0x8000) != 0)
            mods |= NativeMethods.MOD_ALT;
        if ((NativeMethods.GetAsyncKeyState(NativeMethods.VK_LWIN) & 0x8000) != 0
            || (NativeMethods.GetAsyncKeyState(NativeMethods.VK_RWIN) & 0x8000) != 0)
            mods |= NativeMethods.MOD_WIN;
        return mods;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY)
        {
            var id = wParam.ToInt32();
            if (_idToAction.TryGetValue(id, out var action))
            {
                HotkeyPressed?.Invoke(action);
                handled = true;
            }
        }
        return IntPtr.Zero;
    }

    public static bool IsAllowedMouseGesture(uint modifiers, MouseGestureButton button)
    {
        if (button is MouseGestureButton.Left or MouseGestureButton.Right)
            return modifiers != 0; // 必须带修饰键
        return button != MouseGestureButton.None;
    }

    public static bool TryParseAnyGesture(
        string gesture, out uint modifiers, out Key key, out MouseGestureButton mouse)
    {
        modifiers = 0;
        key = Key.None;
        mouse = MouseGestureButton.None;
        if (string.IsNullOrWhiteSpace(gesture)) return false;

        // 去掉空白，保留 +
        var raw = gesture.Trim();
        // 中文整词别名（无 +）
        if (TryMapChineseAlias(raw, out modifiers, out mouse) && mouse != MouseGestureButton.None)
            return true;

        var normalized = raw.Replace(" ", "", StringComparison.Ordinal);
        var parts = normalized.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return false;

        for (var i = 0; i < parts.Length - 1; i++)
        {
            if (!TryParseModifier(parts[i], out var m))
                return false;
            modifiers |= m;
        }

        var last = parts[^1];
        if (IsModifierToken(last))
            return false;

        if (TryParseMouseToken(last, out mouse))
            return mouse != MouseGestureButton.None;

        if (!TryParseKeyToken(last, out key) || key == Key.None || key == Key.Escape)
            return false;

        return true;
    }

    private static bool TryMapChineseAlias(string raw, out uint modifiers, out MouseGestureButton mouse)
    {
        modifiers = 0;
        mouse = MouseGestureButton.None;
        // 支持「Ctrl+鼠标中键」形式
        var parts = raw.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return false;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            if (!TryParseModifier(parts[i], out var m))
                return false;
            modifiers |= m;
        }
        return TryParseMouseToken(parts[^1], out mouse);
    }

    private static bool TryParseModifier(string token, out uint modifier)
    {
        modifier = 0;
        switch (token.ToLowerInvariant())
        {
            case "ctrl":
            case "control":
            case "控制":
                modifier = NativeMethods.MOD_CONTROL;
                return true;
            case "shift":
            case "上档":
                modifier = NativeMethods.MOD_SHIFT;
                return true;
            case "alt":
                modifier = NativeMethods.MOD_ALT;
                return true;
            case "win":
            case "windows":
            case "meta":
                modifier = NativeMethods.MOD_WIN;
                return true;
            default:
                return false;
        }
    }

    private static bool IsModifierToken(string token) =>
        TryParseModifier(token, out _);

    private static bool TryParseMouseToken(string token, out MouseGestureButton mouse)
    {
        mouse = MouseGestureButton.None;
        var t = token.Trim().ToLowerInvariant();
        switch (t)
        {
            case "mouseleft":
            case "左键":
            case "鼠标左键":
                mouse = MouseGestureButton.Left;
                return true;
            case "mouseright":
            case "右键":
            case "鼠标右键":
                mouse = MouseGestureButton.Right;
                return true;
            case "mousemiddle":
            case "middle":
            case "中键":
            case "鼠标中键":
                mouse = MouseGestureButton.Middle;
                return true;
            case "mousexbutton1":
            case "xbutton1":
            case "侧键1":
            case "鼠标侧键1":
                mouse = MouseGestureButton.XButton1;
                return true;
            case "mousexbutton2":
            case "xbutton2":
            case "侧键2":
            case "鼠标侧键2":
                mouse = MouseGestureButton.XButton2;
                return true;
            case "wheelup":
            case "滚轮上":
            case "滚轮向上":
                mouse = MouseGestureButton.WheelUp;
                return true;
            case "wheeldown":
            case "滚轮下":
            case "滚轮向下":
                mouse = MouseGestureButton.WheelDown;
                return true;
            default:
                return false;
        }
    }

    /// <summary>兼容旧 API：仅键盘。</summary>
    public static bool TryParseGesture(string gesture, out uint modifiers, out Key key)
        => TryParseAnyGesture(gesture, out modifiers, out key, out _) && key != Key.None;

    private static bool TryParseKeyToken(string keyPart, out Key key)
    {
        key = Key.None;

        if (keyPart.Length == 1)
        {
            var ch = char.ToUpperInvariant(keyPart[0]);
            if (ch is >= 'A' and <= 'Z')
            {
                key = KeyInterop.KeyFromVirtualKey(ch);
                return true;
            }
            if (ch is >= '0' and <= '9')
            {
                key = KeyInterop.KeyFromVirtualKey(ch);
                return true;
            }
        }

        switch (keyPart.ToLowerInvariant())
        {
            case "space":
            case "spacebar":
                key = Key.Space;
                return true;
            case "enter":
            case "return":
                key = Key.Return;
                return true;
            case "tab":
                key = Key.Tab;
                return true;
            case "backspace":
            case "back":
                key = Key.Back;
                return true;
            case "delete":
            case "del":
                key = Key.Delete;
                return true;
            case "insert":
            case "ins":
                key = Key.Insert;
                return true;
            case "home":
                key = Key.Home;
                return true;
            case "end":
                key = Key.End;
                return true;
            case "pageup":
            case "pgup":
                key = Key.PageUp;
                return true;
            case "pagedown":
            case "pgdn":
                key = Key.PageDown;
                return true;
            case "up":
                key = Key.Up;
                return true;
            case "down":
                key = Key.Down;
                return true;
            case "left":
                key = Key.Left;
                return true;
            case "right":
                key = Key.Right;
                return true;
            case "esc":
            case "escape":
                key = Key.Escape;
                return true;
        }

        if (keyPart.StartsWith("F", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(keyPart[1..], out var fn)
            && fn is >= 1 and <= 24)
        {
            key = Key.F1 + (fn - 1);
            return true;
        }

        if (keyPart.Length == 2
            && (keyPart[0] is 'D' or 'd')
            && keyPart[1] is >= '0' and <= '9')
        {
            key = Key.D0 + (keyPart[1] - '0');
            return true;
        }

        if (Enum.TryParse<Key>(keyPart, true, out var parsed) && parsed != Key.None)
        {
            key = parsed;
            return true;
        }

        return false;
    }

    public static string? NormalizeGesture(string gesture)
    {
        if (!TryParseAnyGesture(gesture, out var modifiers, out var key, out var mouse))
            return null;
        if (mouse != MouseGestureButton.None)
            return FormatMouseGesture(ModifiersFromNative(modifiers), mouse);
        return FormatGesture(ModifiersFromNative(modifiers), key);
    }

    public static string FormatGestureDisplay(string canonical)
    {
        if (!TryParseAnyGesture(canonical, out var modifiers, out var key, out var mouse))
            return canonical;
        var parts = new List<string>();
        if ((modifiers & NativeMethods.MOD_CONTROL) != 0) parts.Add("Ctrl");
        if ((modifiers & NativeMethods.MOD_SHIFT) != 0) parts.Add("Shift");
        if ((modifiers & NativeMethods.MOD_ALT) != 0) parts.Add("Alt");
        if ((modifiers & NativeMethods.MOD_WIN) != 0) parts.Add("Win");
        if (mouse != MouseGestureButton.None)
        {
            parts.Add(mouse switch
            {
                MouseGestureButton.Left => "鼠标左键",
                MouseGestureButton.Right => "鼠标右键",
                MouseGestureButton.Middle => "鼠标中键",
                MouseGestureButton.XButton1 => "侧键1",
                MouseGestureButton.XButton2 => "侧键2",
                MouseGestureButton.WheelUp => "滚轮上",
                MouseGestureButton.WheelDown => "滚轮下",
                _ => mouse.ToString()
            });
        }
        else
        {
            parts.Add(FormatGesture(ModifierKeys.None, key));
        }
        return string.Join("+", parts);
    }

    private static ModifierKeys ModifiersFromNative(uint modifiers)
    {
        var mods = ModifierKeys.None;
        if ((modifiers & NativeMethods.MOD_CONTROL) != 0) mods |= ModifierKeys.Control;
        if ((modifiers & NativeMethods.MOD_SHIFT) != 0) mods |= ModifierKeys.Shift;
        if ((modifiers & NativeMethods.MOD_ALT) != 0) mods |= ModifierKeys.Alt;
        if ((modifiers & NativeMethods.MOD_WIN) != 0) mods |= ModifierKeys.Windows;
        return mods;
    }

    public static string FormatGesture(ModifierKeys mods, Key key)
    {
        var parts = new List<string>();
        if (mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (mods.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (mods.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (mods.HasFlag(ModifierKeys.Windows)) parts.Add("Win");

        var keyName = key switch
        {
            >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
            >= Key.A and <= Key.Z => key.ToString(),
            >= Key.F1 and <= Key.F24 => key.ToString(),
            Key.Space => "Space",
            Key.Return => "Enter",
            _ => key.ToString()
        };
        parts.Add(keyName);
        return string.Join("+", parts);
    }

    public static string FormatMouseGesture(ModifierKeys mods, MouseGestureButton button)
    {
        var parts = new List<string>();
        if (mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (mods.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (mods.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (mods.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(button switch
        {
            MouseGestureButton.Left => "MouseLeft",
            MouseGestureButton.Right => "MouseRight",
            MouseGestureButton.Middle => "MouseMiddle",
            MouseGestureButton.XButton1 => "MouseXButton1",
            MouseGestureButton.XButton2 => "MouseXButton2",
            MouseGestureButton.WheelUp => "WheelUp",
            MouseGestureButton.WheelDown => "WheelDown",
            _ => "MouseMiddle"
        });
        return string.Join("+", parts);
    }

    public void Dispose()
    {
        if (_disposed) return;
        // 先标记，再卸钩：钩子回调立刻空跑放行
        _disposed = true;
        UnregisterAll();
        _source?.RemoveHook(WndProc);
        _host.SourceInitialized -= OnSourceInitialized;
    }
}
