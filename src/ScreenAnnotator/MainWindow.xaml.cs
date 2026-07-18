using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using ScreenAnnotator.Controls;
using ScreenAnnotator.Models;
using ScreenAnnotator.Native;
using ScreenAnnotator.Services;
using ScreenAnnotator.Windows;
using WinForms = System.Windows.Forms;

namespace ScreenAnnotator;

public partial class MainWindow : Window
{
    private readonly AnnotationCanvas _canvas = new();
    private readonly HistoryService _historyOverlay = new();
    private readonly HistoryService _historyBoard = new();
    private readonly BoardFileService _boardFile = new();
    private readonly ExportService _export = new();
    private readonly SettingsService _settings;
    private readonly HotkeyService _hotkeys;
    private readonly TrayIconService _tray = new();
    private ScreenRecordService? _recorder;
    private DispatcherTimer? _recordUiTimer;
    private string? _recordOutputPath;

    private List<AnnotationObject> _overlayStore = [];
    private List<AnnotationObject> _boardStore = [];

    private ToolbarWindow? _toolbar;
    private ToolbarTabWindow? _tab;
    private SettingsWindow? _settingsWindow;
    private HelpWindow? _helpWindow;
    private TextBox? _textBox;
    private Point _textPoint;

    private bool _isActiveMode = true;
    private bool _toolbarExpanded = true;
    private bool _pointerOverChrome;
    /// <summary>移动模式（CR-006）：右键或快捷键开关。</summary>
    private bool _moveModeEnabled;
    private ContentMode _contentMode = ContentMode.Overlay;
    private int _selectedScreenIndex;
    private bool _isDrawing;
    private bool _isMoving;
    private bool _pendingObjectInteract;
    private bool _brushErasing;
    private bool _marqueeSelecting; // 框选删除
    private bool _selectMarquee; // 框选多选（右键长按 / 移动工具空白拖）
    private bool _rightGestureActive;
    private bool _rightBecameMarquee;
    private DateTime _rightDownUtc;
    private Point _rightDownPos;
    private Point _startPoint;
    private Point _lastPoint;
    private AnnotationObject? _moveTarget;
    private List<AnnotationObject> _moveGroup = [];
    private bool _isResizing;
    private bool _isRotating;
    private ResizeHandle _resizeHandle = ResizeHandle.None;
    private Rect _resizeStartBounds;
    private List<AnnotationObject> _resizeSnapshots = [];
    private Point _rotateCenter;
    private double _rotateStartMouseAngle;
    private double _rotateStartObjectRotation;
    private ShapeKind _currentShapeKind = ShapeKind.Rect;
    private PathAnnotation? _currentPath;
    private AnnotationObject? _previewShape;
    private bool _historyPushedForStroke;
    private string? _currentFilePath;
    private bool _resolutionWarned;

    private HistoryService CurrentHistory
        => _contentMode == ContentMode.Board ? _historyBoard : _historyOverlay;

    public ToolType CurrentTool { get; private set; } = ToolType.Pen;
    public ShapeKind CurrentShapeKind => _currentShapeKind;
    public Color CurrentColor { get; private set; } = Colors.Red;
    public StrokeThicknessTier StrokeTier { get; private set; } = StrokeThicknessTier.Medium;
    public FontSizeTier FontTier { get; private set; } = FontSizeTier.Medium;

    public MainWindow()
    {
        InitializeComponent();

        _settings = new SettingsService();
        _hotkeys = new HotkeyService(this);
        _hotkeys.HotkeyPressed += OnHotkeyPressed;

        _canvas.HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch;
        _canvas.VerticalAlignment = System.Windows.VerticalAlignment.Stretch;
        Root.Children.Add(_canvas);
        Panel.SetZIndex(_canvas, 0);

        SourceInitialized += (_, _) => CoverSelectedScreen();
        Loaded += OnLoaded;
        DpiChanged += (_, _) =>
        {
            CoverSelectedScreen();
            PositionChrome();
            _canvas.Redraw();
        };
        Closed += OnClosed;
        KeyDown += OnWindowKeyDown;
        PreviewKeyDown += OnPreviewKeyDown;

        _tray.ToggleOverlayRequested += () => Dispatcher.Invoke(ToggleOverlayMode);
        _tray.ExitRequested += () => Dispatcher.Invoke(ExitApp);
        _tray.OpenSettingsRequested += () => Dispatcher.Invoke(OpenSettings);
        _tray.OpenHelpRequested += () => Dispatcher.Invoke(OpenHelp);

        _canvas.MouseLeftButtonDown += OnCanvasMouseDown;
        _canvas.MouseMove += OnCanvasMouseMove;
        _canvas.MouseLeftButtonUp += OnCanvasMouseUp;
        _canvas.MouseRightButtonDown += OnCanvasRightDown;
        _canvas.MouseRightButtonUp += OnCanvasRightUp;
        _canvas.MouseEnter += (_, _) =>
        {
            SyncChromePointerState();
            if (!_pointerOverChrome)
                UpdateCursor();
        };
        _canvas.MouseLeave += (_, _) =>
        {
            // Leave may race with chrome Enter; defer sync
            Dispatcher.BeginInvoke(SyncChromePointerState, System.Windows.Threading.DispatcherPriority.Input);
        };
        PreviewMouseMove += (_, _) => SyncChromePointerState();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        CoverSelectedScreen();
        WindowClickThrough.SetToolWindow(this);
        WindowClickThrough.SetClickThrough(this, false);
        ApplyContentModeVisuals();

        _toolbar = new ToolbarWindow(this)
        {
            // Owned 以保持 Z 序：Owner 窗之下且随主窗（BUG-003）
            Owner = this
        };
        _tab = new ToolbarTabWindow(this)
        {
            Owner = this
        };
        _toolbarExpanded = true;
        Activated += (_, _) => EnsureChromeAboveMain();
        UpdateChromeVisibility();
        Dispatcher.BeginInvoke(() =>
        {
            PositionChrome();
            EnsureChromeAboveMain();
        }, System.Windows.Threading.DispatcherPriority.Loaded);

        var results = _hotkeys.RegisterAll(_settings.Settings.Hotkeys);
        var failed = results.Where(r => r.Error != null).ToList();
        if (failed.Count > 0)
        {
            var msg = string.Join("\n", failed.Select(f => $"{AppSettings.HotkeyLabels.GetValueOrDefault(f.ActionId, f.ActionId)}: {f.Error}"));
            ShowInfo($"部分全局快捷键注册失败，请在设置中更换：\n\n{msg}", MessageBoxImage.Warning);
        }

        EnterActiveMode();
    }

    private void CoverSelectedScreen()
    {
        var screen = GetSelectedScreen();
        // Screen.Bounds is device pixels; WPF Left/Width are DIPs — convert for high DPI.
        var deviceTopLeft = new Point(screen.Bounds.Left, screen.Bounds.Top);
        var deviceBottomRight = new Point(screen.Bounds.Right, screen.Bounds.Bottom);

        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget != null)
        {
            var toDip = source.CompositionTarget.TransformFromDevice;
            var topLeft = toDip.Transform(deviceTopLeft);
            var bottomRight = toDip.Transform(deviceBottomRight);
            Left = topLeft.X;
            Top = topLeft.Y;
            Width = Math.Max(1, bottomRight.X - topLeft.X);
            Height = Math.Max(1, bottomRight.Y - topLeft.Y);
        }
        else
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            Left = deviceTopLeft.X / dpi.DpiScaleX;
            Top = deviceTopLeft.Y / dpi.DpiScaleY;
            Width = Math.Max(1, screen.Bounds.Width / dpi.DpiScaleX);
            Height = Math.Max(1, screen.Bounds.Height / dpi.DpiScaleY);
        }
    }

    public WinForms.Screen GetSelectedScreen()
    {
        var screens = WinForms.Screen.AllScreens;
        if (screens.Length == 0)
            throw new InvalidOperationException("无法获取显示器。");
        var idx = Math.Clamp(_selectedScreenIndex, 0, screens.Length - 1);
        _selectedScreenIndex = idx;
        return screens[idx];
    }

    public int SelectedScreenIndex => _selectedScreenIndex;

    public IReadOnlyList<(int Index, string Label)> GetScreenOptions()
    {
        var screens = WinForms.Screen.AllScreens;
        var list = new List<(int, string)>(screens.Length);
        for (var i = 0; i < screens.Length; i++)
        {
            var s = screens[i];
            var primary = s.Primary ? "（主）" : "";
            list.Add((i, $"显示器 {i + 1}{primary} {s.Bounds.Width}×{s.Bounds.Height}"));
        }
        return list;
    }

    public void SelectScreen(int index)
    {
        var screens = WinForms.Screen.AllScreens;
        if (screens.Length == 0) return;
        var idx = Math.Clamp(index, 0, screens.Length - 1);
        _selectedScreenIndex = idx;
        CoverSelectedScreen();
        PositionChrome();
        _canvas.Redraw();
        _toolbar?.RefreshUi();
    }

    public ContentMode CurrentContentMode => _contentMode;

    public void SetContentMode(ContentMode mode)
    {
        if (mode == _contentMode) return;
        CancelInProgress();
        PersistCurrentStore();
        _contentMode = mode;
        LoadCurrentStoreToCanvas();
        ApplyContentModeVisuals();

        if (_contentMode == ContentMode.Board && !_isActiveMode)
            EnterPassthroughMode();
        else if (_contentMode == ContentMode.Overlay && _isActiveMode)
            WindowClickThrough.SetClickThrough(this, false);

        _toolbar?.RefreshUi();
    }

    private void PersistCurrentStore()
    {
        var snapshot = _canvas.Objects.Select(o => o.Clone()).ToList();
        if (_contentMode == ContentMode.Board)
            _boardStore = snapshot;
        else
            _overlayStore = snapshot;
    }

    private void LoadCurrentStoreToCanvas()
    {
        var source = _contentMode == ContentMode.Board ? _boardStore : _overlayStore;
        _canvas.SetObjects(source.Select(o => o.Clone()).ToList());
    }

    private void ApplyContentModeVisuals()
    {
        if (_contentMode == ContentMode.Board)
        {
            Background = Brushes.White;
            if (_isActiveMode)
                WindowClickThrough.SetClickThrough(this, false);
        }
        else
        {
            Background = new SolidColorBrush(Color.FromArgb(0x01, 0, 0, 0));
        }
        _canvas.Redraw();
    }

    private void PositionChrome()
    {
        // ????????????????????
        if (_toolbar != null)
        {
            const double sidePad = 12;
            _toolbar.Width = Math.Max(320, Width - sidePad * 2);
            _toolbar.Left = Left + sidePad;
            _toolbar.Top = Top;
            _toolbar.UpdateLayout();
        }

        // ????????????
        if (_tab != null)
        {
            _tab.UpdateLayout();
            var tabW = _tab.ActualWidth > 0 ? _tab.ActualWidth : _tab.Width;
            _tab.Left = Left + (Width - tabW) / 2;
            _tab.Top = Top;
        }
    }

    /// <summary>
    /// 完整工具栏与顶部小入口互斥显示。
    /// </summary>
    private void UpdateChromeVisibility()
    {
        var recording = _recorder?.IsRecording == true;
        if (recording)
        {
            _toolbar?.Hide();
            _tab?.SetRecordingUi(true, _recorder!.Elapsed);
            _tab?.Show();
            PositionChrome();
            EnsureChromeAboveMain();
            return;
        }

        _tab?.SetRecordingUi(false, TimeSpan.Zero);
        var showFull = _isActiveMode && _toolbarExpanded;
        if (showFull)
        {
            _tab?.Hide();
            _toolbar?.Show();
        }
        else
        {
            _toolbar?.Hide();
            _tab?.Show();
        }

        PositionChrome();
        EnsureChromeAboveMain();
        _toolbar?.RefreshUi();
    }

    /// <summary>????????/????HWND ???????????/summary>
    public void EnsureChromeAboveMain()
    {
        if (_toolbar is { IsVisible: true })
            WindowClickThrough.BringToTopMost(_toolbar);
        if (_tab is { IsVisible: true })
            WindowClickThrough.BringToTopMost(_tab);
    }

    public void ExpandToolbar()
    {
        if (_recorder?.IsRecording == true) return;
        _toolbarExpanded = true;
        if (!_isActiveMode)
            EnterActiveMode(expandToolbar: true);
        else
            UpdateChromeVisibility();
    }

    public void CollapseToolbar()
    {
        _toolbarExpanded = false;
        UpdateChromeVisibility();
    }

    public void OnToolbarTabClicked()
    {
        if (_recorder?.IsRecording == true)
        {
            StopScreenRecording();
            return;
        }
        // 点顶部小入口：回到可绘制并展开完整工具栏
        EnterActiveMode(expandToolbar: true, activateMain: false);
    }

    public bool IsScreenRecording => _recorder?.IsRecording == true;

    public void ToggleScreenRecording()
    {
        if (_recorder?.IsRecording == true)
            StopScreenRecording();
        else
            StartScreenRecording();
    }

    public void StartScreenRecording()
    {
        if (_recorder?.IsRecording == true) return;

        if (!_isActiveMode)
            EnterActiveMode(expandToolbar: true);

        var owner = _toolbar is { IsVisible: true } ? (Window)_toolbar
            : _tab is { IsVisible: true } ? _tab
            : this;
        var dlg = new RecordSettingsWindow(owner);
        if (dlg.ShowDialog() != true) return;

        try
        {
            var screen = GetSelectedScreen();
            var bounds = new Rect(screen.Bounds.Left, screen.Bounds.Top, screen.Bounds.Width, screen.Bounds.Height);
            _recorder?.Dispose();
            _recorder = new ScreenRecordService();
            _recorder.Warning += msg => Dispatcher.BeginInvoke(() =>
                ShowInfo(msg, MessageBoxImage.Warning));
            _recordOutputPath = dlg.OutputPath;

            _recorder.Start(new ScreenRecordOptions
            {
                OutputPath = dlg.OutputPath,
                CaptureMicrophone = dlg.CaptureMicrophone,
                CaptureSystemAudio = dlg.CaptureSystemAudio,
                ScreenBoundsDevicePx = bounds,
                CanvasWidthDip = Width,
                CanvasHeightDip = Height,
                OpaqueWhiteBackground = _contentMode == ContentMode.Board,
                Fps = 24
            },
            () => _canvas.SnapshotForRecording(),
            action => Dispatcher.Invoke(action));

            _toolbarExpanded = false;
            StartRecordUiTimer();
            UpdateChromeVisibility();
        }
        catch (Exception ex)
        {
            ShowInfo($"无法开始录制：{ex.Message}", MessageBoxImage.Error);
            _recorder?.Dispose();
            _recorder = null;
            _recordOutputPath = null;
        }
    }

    public void StopScreenRecording()
    {
        if (_recorder == null || !_recorder.IsRecording) return;

        StopRecordUiTimer();
        var path = _recordOutputPath;
        try
        {
            _recorder.Stop();
            var fatal = _recorder.TakeFatalError();
            var warn = _recorder.LastWarning;
            _recorder.Dispose();
            _recorder = null;
            _recordOutputPath = null;
            UpdateChromeVisibility();

            if (fatal != null)
                ShowInfo($"录制结束但保存失败：{fatal.Message}", MessageBoxImage.Error);
            else if (!string.IsNullOrEmpty(warn))
                ShowInfo($"录制已停止。\n{warn}\n{path}", MessageBoxImage.Warning);
            else
                ShowInfo($"录制已保存：\n{path}");
        }
        catch (Exception ex)
        {
            _recorder?.Dispose();
            _recorder = null;
            _recordOutputPath = null;
            UpdateChromeVisibility();
            ShowInfo($"停止录制失败：{ex.Message}", MessageBoxImage.Error);
        }
    }

    private void StartRecordUiTimer()
    {
        StopRecordUiTimer();
        _recordUiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _recordUiTimer.Tick += (_, _) =>
        {
            if (_recorder?.IsRecording == true)
                _tab?.SetRecordingUi(true, _recorder.Elapsed);
            else
                StopRecordUiTimer();
        };
        _recordUiTimer.Start();
    }

    private void StopRecordUiTimer()
    {
        if (_recordUiTimer == null) return;
        _recordUiTimer.Stop();
        _recordUiTimer = null;
    }

    public void SetTool(ToolType tool)
    {
        CancelInProgress();
        // CR-016：切换工具时清掉未完成/淡出中的临时笔
        if (tool != ToolType.TempPen)
            _canvas.ClearTempStrokes();
        CurrentTool = tool;
        if (tool.IsEraserTool())
            _canvas.ClearSelection();
        SyncRotateHandleVisibility();
        if (!_pointerOverChrome)
            UpdateCursor();
        _toolbar?.RefreshUi();
    }

    public void SetShapeKind(ShapeKind kind)
    {
        _currentShapeKind = kind;
        SetTool(ToolType.Shape);
    }

    private void SyncRotateHandleVisibility()
    {
        var show = CanInteractMoveObjects && _canvas.SelectedCount == 1;
        if (_canvas.ShowRotateHandle == show) return;
        _canvas.ShowRotateHandle = show;
        _canvas.NotifyChanged();
    }

    public void SetColor(Color color)
    {
        CurrentColor = color;
        ApplyStyleToSelection(color: color);
        _toolbar?.RefreshUi();
    }

    public void SetStrokeTier(StrokeThicknessTier tier)
    {
        StrokeTier = tier;
        ApplyStyleToSelection(stroke: StylePresets.StrokeWidth(tier));
        _toolbar?.RefreshUi();
    }

    public void SetFontTier(FontSizeTier tier)
    {
        FontTier = tier;
        ApplyStyleToSelection(fontSize: StylePresets.FontSize(tier));
        _toolbar?.RefreshUi();
    }

    private void ApplyStyleToSelection(Color? color = null, double? stroke = null, double? fontSize = null)
    {
        var sels = _canvas.SelectedObjects;
        if (sels.Count == 0) return;

        CurrentHistory.Push(_canvas.Objects);
        foreach (var sel in sels)
        {
            if (color.HasValue)
                sel.Color = color.Value;
            if (stroke.HasValue && sel is not TextAnnotation)
                sel.StrokeWidth = stroke.Value;
            if (fontSize.HasValue && sel is TextAnnotation text)
                text.FontSize = fontSize.Value;
        }
        _canvas.NotifyChanged();
    }

    public int SelectedObjectCount => _canvas.SelectedCount;

    public void PickCustomColor()
    {
        using var dlg = new WinForms.ColorDialog
        {
            FullOpen = true,
            Color = System.Drawing.Color.FromArgb(CurrentColor.A, CurrentColor.R, CurrentColor.G, CurrentColor.B)
        };
        if (dlg.ShowDialog() == WinForms.DialogResult.OK)
        {
            SetColor(Color.FromRgb(dlg.Color.R, dlg.Color.G, dlg.Color.B));
        }
    }

    public void ToggleOverlayMode()
    {
        if (_isActiveMode) EnterPassthroughMode();
        else EnterActiveMode(expandToolbar: true);
    }

    public void EnterActiveMode(bool expandToolbar = true, bool activateMain = true)
    {
        _isActiveMode = true;
        if (expandToolbar)
            _toolbarExpanded = true;

        WindowClickThrough.SetClickThrough(this, false);
        IsHitTestVisible = true;
        _canvas.IsHitTestVisible = true;
        UpdateChromeVisibility();

        // 从顶栏点回标注时勿 Activate 主窗抢焦点（CR-003 R4）
        if (activateMain && !_pointerOverChrome)
        {
            Activate();
            EnsureChromeAboveMain();
        }

        if (!_pointerOverChrome)
            UpdateCursor();
        else
        {
            Mouse.OverrideCursor = Cursors.Arrow;
            Cursor = Cursors.Arrow;
            EnsureChromeAboveMain();
        }
        _toolbar?.RefreshUi();
    }

    public void EnterPassthroughMode()
    {
        CancelInProgress();
        CommitTextBox(confirm: false);
        _isActiveMode = false;
        _toolbarExpanded = false;

        // CR-008：模式 B 不点穿桌面，仅收起完整工具栏
        if (_contentMode == ContentMode.Board)
            WindowClickThrough.SetClickThrough(this, false);
        else
            WindowClickThrough.SetClickThrough(this, true);

        _settingsWindow?.Hide();
        UpdateChromeVisibility();
        Cursor = Cursors.Arrow;
        if (_pointerOverChrome)
            Mouse.OverrideCursor = Cursors.Arrow;
        else
            Mouse.OverrideCursor = null;
        _toolbar?.RefreshUi();
    }


    public bool IsActiveMode => _isActiveMode;
    public bool IsToolbarExpanded => _toolbarExpanded;
    public bool MoveModeEnabled => _moveModeEnabled;

    /// <summary>????????/??????????????????/summary>
    public bool CanInteractMoveObjects
        => _isActiveMode
           && !CurrentTool.IsEraserTool()
           && (CurrentTool.IsMoveTool() || _moveModeEnabled);

    public void ToggleMoveMode()
    {
        _moveModeEnabled = !_moveModeEnabled;
        CancelInProgress();
        // CR-013：打开移动模式时，橡皮/框选删除自动切到「移动」工具
        if (_moveModeEnabled && CurrentTool.IsEraserTool())
            SetTool(ToolType.Move);
        SyncRotateHandleVisibility();
        if (!_pointerOverChrome)
            UpdateCursor();
        _toolbar?.RefreshUi();
    }

    /// <summary>??????????????? chrome ?????/summary>
    public void NotifyPointerOverChrome(bool over)
    {
        ApplyChromePointerState(over);
    }

    /// <summary>
    /// 综合 MouseEnter 与几何命中，修正 Z 序与光标（BUG-002/003）。
    /// </summary>
    public void SyncChromePointerState()
    {
        var realOver = IsChromeReallyHovered();
        var geoOver = IsPointerGeometricallyOverChrome();

        if (geoOver && !realOver)
        {
            // ???????????? ??????Z ??????
            ReleaseCanvasCaptureIfNeeded();
            EnsureChromeAboveMain();
        }

        if (geoOver || realOver)
        {
            ReleaseCanvasCaptureIfNeeded();
            EnsureChromeAboveMain();
            ApplyChromePointerState(true);
        }
        else
        {
            ApplyChromePointerState(false);
        }
    }

    private void ReleaseCanvasCaptureIfNeeded()
    {
        if (_rightGestureActive)
        {
            _rightGestureActive = false;
            _rightBecameMarquee = false;
            if (_selectMarquee)
                EndSelectMarquee(commit: false);
            else if (_canvas.IsMouseCaptured)
                _canvas.ReleaseMouseCapture();
            return;
        }
        if (_isDrawing)
            FinishDrawing();
        else if (_brushErasing)
            EndBrushErase();
        else if (_marqueeSelecting)
            EndMarquee(commit: false);
        else if (_selectMarquee)
            EndSelectMarquee(commit: false);
        else if (_isResizing)
            EndResize();
        else if (_isRotating)
            EndRotate();
        else if (_pendingObjectInteract || _isMoving)
            EndObjectInteract(commitMove: _isMoving);
        else if (_canvas.IsMouseCaptured)
            _canvas.ReleaseMouseCapture();
    }

    private void ApplyChromePointerState(bool over)
    {
        if (over)
        {
            ReleaseCanvasCaptureIfNeeded();
            EnsureChromeAboveMain();
        }

        if (_pointerOverChrome == over)
        {
            if (over)
            {
                Mouse.OverrideCursor = Cursors.Arrow;
                Cursor = Cursors.Arrow;
            }
            return;
        }

        _pointerOverChrome = over;
        if (over)
        {
            Mouse.OverrideCursor = Cursors.Arrow;
            Cursor = Cursors.Arrow;
        }
        else
        {
            Mouse.OverrideCursor = null;
            if (_isActiveMode && _canvas.IsMouseOver && !_canvas.IsMouseCaptured)
                UpdateCursor();
            else
                Cursor = Cursors.Arrow;
        }
    }

    private bool IsChromeReallyHovered()
        => _toolbar is { IsVisible: true, IsMouseOver: true }
           || _tab is { IsVisible: true, IsMouseOver: true };

    private bool IsPointerGeometricallyOverChrome()
    {
        var screen = WinForms.Control.MousePosition;
        return WindowContainsScreenPoint(_toolbar, screen)
               || WindowContainsScreenPoint(_tab, screen);
    }

    private bool IsPointerOverChrome()
        => IsChromeReallyHovered() || IsPointerGeometricallyOverChrome();

    private static bool WindowContainsScreenPoint(Window? window, System.Drawing.Point screenPx)
    {
        if (window is not { IsVisible: true })
            return false;
        if (window.ActualWidth <= 0 || window.ActualHeight <= 0)
            return false;

        try
        {
            var topLeft = window.PointToScreen(new Point(0, 0));
            var bottomRight = window.PointToScreen(new Point(window.ActualWidth, window.ActualHeight));
            return screenPx.X >= topLeft.X && screenPx.X <= bottomRight.X
                   && screenPx.Y >= topLeft.Y && screenPx.Y <= bottomRight.Y;
        }
        catch
        {
            return false;
        }
    }

    private void UpdateCursor()
    {
        if (IsPointerOverChrome())
        {
            ApplyChromePointerState(true);
            return;
        }

        if (!_isActiveMode)
        {
            Cursor = Cursors.Arrow;
            return;
        }

        if (CurrentTool.IsEraserTool())
        {
            Cursor = Cursors.Cross;
            return;
        }

        SyncRotateHandleVisibility();
        if (_canvas.SelectedCount > 0)
        {
            var handle = _canvas.HitTestHandle(Mouse.GetPosition(_canvas));
            if (handle != ResizeHandle.None)
            {
                Cursor = AnnotationCanvas.CursorForHandle(handle);
                return;
            }
        }

        if (CanInteractMoveObjects && !_isDrawing && !_isMoving && !_pendingObjectInteract && !_isResizing && !_isRotating)
        {
            var pos = Mouse.GetPosition(_canvas);
            if (_canvas.HitTestMoveableTop(pos) != null)
            {
                Cursor = Cursors.SizeAll;
                return;
            }
        }

        if (CurrentTool.IsMoveTool())
        {
            Cursor = Cursors.Arrow;
            return;
        }

        Cursor = CurrentTool switch
        {
            ToolType.Text => Cursors.IBeam,
            _ => Cursors.Pen
        };
    }

    private double CurrentStrokeWidth() => StylePresets.StrokeWidth(StrokeTier);
    private double CurrentFontSize() => StylePresets.FontSize(FontTier);
    private double CurrentEraserRadius() => StylePresets.EraserBrushRadius(StrokeTier);

    private void OnCanvasMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_isActiveMode) return;
        var pos = e.GetPosition(_canvas);

        // CR-012：双击文字改文案
        if (e.ClickCount == 2)
        {
            var textHit = _canvas.HitTestTop(pos) as TextAnnotation
                          ?? (_canvas.SelectedObject as TextAnnotation);
            if (textHit != null)
            {
                if (_canvas.SelectedObject != textHit)
                    _canvas.SetSelected(textHit);
                BeginEditExistingText(textHit);
                e.Handled = true;
                return;
            }
        }

        _startPoint = pos;
        _lastPoint = pos;

        if (CurrentTool.IsEraserTool())
        {
            _canvas.ClearSelection();
            HandleEraserDown(pos);
            return;
        }

        // CR-012/015：调节/旋转手柄优先
        SyncRotateHandleVisibility();
        if (_canvas.SelectedCount > 0)
        {
            var handle = _canvas.HitTestHandle(pos);
            if (handle == ResizeHandle.Rotate)
            {
                BeginRotate(pos);
                _canvas.CaptureMouse();
                Cursor = AnnotationCanvas.CursorForHandle(handle);
                return;
            }
            if (handle != ResizeHandle.None)
            {
                BeginResize(handle);
                _canvas.CaptureMouse();
                Cursor = AnnotationCanvas.CursorForHandle(handle);
                return;
            }
        }

        // 可交互移动时优先命中对象（移动工具 / 移动模式）
        if (CanInteractMoveObjects)
        {
            var hit = _canvas.HitTestMoveableTop(pos);
            if (hit != null)
            {
                _pendingObjectInteract = true;
                _moveTarget = hit;
                // 命中已在多选中 → 整组拖；否则以该对象为单选起点
                if (_canvas.IsSelected(hit) && _canvas.SelectedCount > 1)
                    _moveGroup = _canvas.SelectedObjects.ToList();
                else
                    _moveGroup = [hit];
                _isMoving = false;
                _canvas.CaptureMouse();
                Cursor = Cursors.SizeAll;
                return;
            }

            // 移动工具未命中：空白拖 = 框选多选（加分）；单击空白清选
            if (CurrentTool.IsMoveTool())
            {
                _selectMarquee = true;
                _startPoint = pos;
                _canvas.CaptureMouse();
                _canvas.SetMarqueePreview(new Rect(pos, pos), MarqueeKind.Select);
                return;
            }
        }

        _canvas.ClearSelection();
        _toolbar?.RefreshUi();

        if (CurrentTool == ToolType.Text)
        {
            BeginTextInput(pos);
            return;
        }

        if (CurrentTool.IsMoveTool())
            return;

        _isDrawing = true;
        _canvas.CaptureMouse();

        if (CurrentTool.IsPenLike())
        {
            _currentPath = new PathAnnotation
            {
                IsHighlighter = CurrentTool == ToolType.Highlighter,
                Color = CurrentColor,
                StrokeWidth = CurrentTool == ToolType.Highlighter
                    ? Math.Max(CurrentStrokeWidth() * 2.5, 10)
                    : CurrentStrokeWidth(),
                Opacity = CurrentTool switch
                {
                    ToolType.Highlighter => StylePresets.HighlighterOpacity,
                    ToolType.TempPen => 0.9,
                    _ => 1.0
                },
                Points = [pos]
            };
            _canvas.SetPreview(_currentPath);
        }
    }

    private void HandleEraserDown(Point pos)
    {
        if (CurrentTool == ToolType.EraserObject)
        {
            var hit = _canvas.HitTestTop(pos);
            if (hit != null)
            {
                CurrentHistory.Push(_canvas.Objects);
                _canvas.RemoveObject(hit);
                _toolbar?.RefreshUi();
            }
            return;
        }

        if (CurrentTool == ToolType.EraserBrush)
        {
            _brushErasing = true;
            _historyPushedForStroke = false;
            _canvas.CaptureMouse();
            ApplyBrushEraseAt(pos);
            return;
        }

        if (CurrentTool == ToolType.EraserMarquee)
        {
            _marqueeSelecting = true;
            _canvas.CaptureMouse();
            _canvas.SetMarqueePreview(new Rect(pos, pos), MarqueeKind.Delete);
        }
    }

    private void ApplyBrushEraseAt(Point pos)
    {
        var radius = CurrentEraserRadius();
        var snapshotNeeded = false;
        var toRemove = new List<AnnotationObject>();
        var pathReplacements = new List<(PathAnnotation Orig, List<PathAnnotation> Segs)>();

        foreach (var obj in _canvas.Objects.ToList())
        {
            if (obj is PathAnnotation path)
            {
                if (!path.HitTest(pos, radius)) continue;
                var segs = path.EraseAt(pos, radius);
                var unchanged = segs.Count == 1 && segs[0].Points.Count == path.Points.Count;
                if (unchanged) continue;
                snapshotNeeded = true;
                if (segs.Count == 0)
                    toRemove.Add(path);
                else
                    pathReplacements.Add((path, segs));
            }
            else if (obj.HitTest(pos, radius))
            {
                snapshotNeeded = true;
                toRemove.Add(obj);
            }
        }

        if (!snapshotNeeded) return;
        if (!_historyPushedForStroke)
        {
            CurrentHistory.Push(_canvas.Objects);
            _historyPushedForStroke = true;
        }

        foreach (var (orig, segs) in pathReplacements)
            _canvas.ReplacePathWithSegments(orig, segs);
        foreach (var obj in toRemove)
            _canvas.RemoveObject(obj);
        _toolbar?.RefreshUi();
    }

    private void OnCanvasMouseMove(object sender, MouseEventArgs e)
    {
        if (_rightGestureActive)
        {
            HandleRightGestureMove(e.GetPosition(_canvas));
            return;
        }

        if (_isResizing)
        {
            if (IsPointerOverChrome())
            {
                EndResize();
                SyncChromePointerState();
                return;
            }
            ApplyResize(e.GetPosition(_canvas));
            return;
        }

        if (_isRotating)
        {
            if (IsPointerOverChrome())
            {
                EndRotate();
                SyncChromePointerState();
                return;
            }
            ApplyRotate(e.GetPosition(_canvas));
            return;
        }

        if (_isDrawing && IsPointerOverChrome())
        {
            FinishDrawing();
            SyncChromePointerState();
            return;
        }

        if (_brushErasing && IsPointerOverChrome())
        {
            EndBrushErase();
            SyncChromePointerState();
            return;
        }

        if ((_marqueeSelecting || _selectMarquee) && IsPointerOverChrome())
        {
            if (_marqueeSelecting) EndMarquee(commit: false);
            if (_selectMarquee) EndSelectMarquee(commit: false);
            SyncChromePointerState();
            return;
        }

        if (_pendingObjectInteract || _isMoving)
        {
            if (IsPointerOverChrome())
            {
                EndObjectInteract(commitMove: _isMoving);
                SyncChromePointerState();
                return;
            }

            var pos = e.GetPosition(_canvas);
            if (_pendingObjectInteract && !_isMoving)
            {
                if ((pos - _startPoint).Length >= StylePresets.MoveDragThreshold)
                {
                    _isMoving = true;
                    _pendingObjectInteract = false;
                    CurrentHistory.Push(_canvas.Objects);
                    if (_moveGroup.Count > 0)
                    {
                        _canvas.SetSelection(_moveGroup);
                        Cursor = Cursors.SizeAll;
                    }
                }
            }

            if (_isMoving && _moveGroup.Count > 0)
            {
                var dx = pos.X - _lastPoint.X;
                var dy = pos.Y - _lastPoint.Y;
                foreach (var obj in _moveGroup)
                    obj.MoveBy(dx, dy);
                _lastPoint = pos;
                _canvas.NotifyChanged();
            }
            return;
        }

        if (!_isDrawing && !_brushErasing && !_marqueeSelecting && !_selectMarquee)
        {
            SyncChromePointerState();
            if (!_pointerOverChrome)
                UpdateCursor();
            return;
        }

        if (!_isActiveMode) return;
        var p = e.GetPosition(_canvas);

        if (_brushErasing)
        {
            ApplyBrushEraseAt(p);
            _lastPoint = p;
            return;
        }

        if (_marqueeSelecting)
        {
            var r = NormalizeRect(_startPoint, p);
            _canvas.SetMarqueePreview(r, MarqueeKind.Delete);
            return;
        }

        if (_selectMarquee)
        {
            var r = NormalizeRect(_startPoint, p);
            _canvas.SetMarqueePreview(r, MarqueeKind.Select);
            return;
        }

        if (_currentPath != null)
        {
            var last = _currentPath.Points[^1];
            if ((p - last).Length >= 1.5)
            {
                _currentPath.Points.Add(p);
                _canvas.SetPreview(_currentPath);
            }
            return;
        }

        _previewShape = CurrentTool switch
        {
            ToolType.Line => new LineAnnotation
            {
                Color = CurrentColor, StrokeWidth = CurrentStrokeWidth(), Opacity = 1,
                X1 = _startPoint.X, Y1 = _startPoint.Y, X2 = p.X, Y2 = p.Y
            },
            ToolType.Arrow => new ArrowAnnotation
            {
                Color = CurrentColor, StrokeWidth = CurrentStrokeWidth(), Opacity = 1,
                X1 = _startPoint.X, Y1 = _startPoint.Y, X2 = p.X, Y2 = p.Y
            },
            ToolType.Shape => new ShapeAnnotation
            {
                Kind = _currentShapeKind,
                Color = CurrentColor, StrokeWidth = CurrentStrokeWidth(), Opacity = 1,
                X = _startPoint.X, Y = _startPoint.Y,
                Width = p.X - _startPoint.X, Height = p.Y - _startPoint.Y
            },
            _ => null
        };
        _canvas.SetPreview(_previewShape);
    }

    private static Rect NormalizeRect(Point a, Point b)
    {
        var x = Math.Min(a.X, b.X);
        var y = Math.Min(a.Y, b.Y);
        return new Rect(x, y, Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));
    }

    private void OnCanvasMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_isResizing)
        {
            EndResize();
            SyncChromePointerState();
            return;
        }

        if (_isRotating)
        {
            EndRotate();
            SyncChromePointerState();
            return;
        }

        if (_pendingObjectInteract || _isMoving)
        {
            EndObjectInteract(commitMove: _isMoving);
            SyncChromePointerState();
            Dispatcher.BeginInvoke(SyncChromePointerState, System.Windows.Threading.DispatcherPriority.Input);
            return;
        }

        if (_brushErasing)
        {
            EndBrushErase();
            SyncChromePointerState();
            return;
        }

        if (_marqueeSelecting)
        {
            EndMarquee(commit: true);
            SyncChromePointerState();
            return;
        }

        if (_selectMarquee)
        {
            EndSelectMarquee(commit: true);
            SyncChromePointerState();
            return;
        }

        if (!_isDrawing) return;
        FinishDrawing();
        SyncChromePointerState();
        Dispatcher.BeginInvoke(SyncChromePointerState, System.Windows.Threading.DispatcherPriority.Input);
    }

    private void BeginResize(ResizeHandle handle)
    {
        _isResizing = true;
        _resizeHandle = handle;
        _resizeSnapshots = _canvas.SelectedObjects.Select(o => o.Clone()).ToList();
        _resizeStartBounds = _canvas.SelectedCount == 1
            ? _canvas.SelectedObject!.GetContentBounds()
            : _canvas.GetSelectionEnvelope();
        CurrentHistory.Push(_canvas.Objects);
    }

    private void BeginRotate(Point pos)
    {
        if (_canvas.SelectedObject == null) return;
        _isRotating = true;
        _resizeHandle = ResizeHandle.Rotate;
        _resizeSnapshots = [_canvas.SelectedObject.Clone()];
        _rotateCenter = _canvas.SelectedObject.GetCenter();
        _rotateStartMouseAngle = Math.Atan2(pos.Y - _rotateCenter.Y, pos.X - _rotateCenter.X);
        _rotateStartObjectRotation = _canvas.SelectedObject.Rotation;
        CurrentHistory.Push(_canvas.Objects);
    }

    private void ApplyRotate(Point pos)
    {
        if (_canvas.SelectedObject == null || _resizeSnapshots.Count == 0) return;
        var angle = Math.Atan2(pos.Y - _rotateCenter.Y, pos.X - _rotateCenter.X);
        var deltaDeg = (angle - _rotateStartMouseAngle) * 180.0 / Math.PI;
        var rot = AnnotationObject.NormalizeDegrees(_rotateStartObjectRotation + deltaDeg);
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
            rot = Math.Round(rot / 15.0) * 15.0;
        _canvas.SelectedObject.Rotation = rot;
        _canvas.NotifyChanged();
    }

    private void EndRotate()
    {
        _isRotating = false;
        _resizeHandle = ResizeHandle.None;
        _resizeSnapshots = [];
        if (_canvas.IsMouseCaptured)
            _canvas.ReleaseMouseCapture();
        SyncRotateHandleVisibility();
        EnsureChromeAboveMain();
        UpdateCursor();
        _toolbar?.RefreshUi();
    }

    private void ApplyResize(Point pos)
    {
        if (_resizeSnapshots.Count == 0) return;

        // 直线/箭头：拖端点（局部坐标，适配旋转）
        if (_canvas.SelectedCount == 1
            && _canvas.SelectedObject is LineAnnotation liveLine
            && _resizeSnapshots[0] is LineAnnotation
            && _resizeHandle is ResizeHandle.Endpoint1 or ResizeHandle.Endpoint2)
        {
            var local = liveLine.WorldToLocal(pos);
            liveLine.SetEndpoint(_resizeHandle == ResizeHandle.Endpoint1 ? 0 : 1, local);
            _canvas.NotifyChanged();
            return;
        }

        RestoreFromSnapshots();
        var start = _resizeStartBounds;
        if (start.Width < 1) start.Width = 1;
        if (start.Height < 1) start.Height = 1;
        var origin = AnnotationCanvas.OppositeCorner(_resizeHandle, start);

        if (_canvas.SelectedCount == 1)
        {
            var obj = _canvas.SelectedObject!;
            var localPos = obj.WorldToLocal(pos);
            if (obj is ShapeAnnotation shape)
            {
                if (shape.Kind == ShapeKind.Ellipse)
                    shape.SetContentCircle(origin, localPos);
                else
                    shape.SetContentRect(NormalizeRect(origin, localPos));
            }
            else
            {
                var newW = Math.Abs(localPos.X - origin.X);
                var newH = Math.Abs(localPos.Y - origin.Y);
                var s = Math.Max(newW / start.Width, newH / start.Height);
                if (s < 0.05) s = 0.05;
                var signX = localPos.X >= origin.X ? 1.0 : -1.0;
                var signY = localPos.Y >= origin.Y ? 1.0 : -1.0;
                obj.ScaleAbout(origin, signX * (newW / start.Width), signY * (newH / start.Height));
                if (obj is PathAnnotation or TextAnnotation)
                {
                    RestoreFromSnapshots();
                    obj.ScaleAbout(origin, signX * s, signY * s);
                }
            }
        }
        else
        {
            RestoreFromSnapshots();
            var newW = Math.Max(2, Math.Abs(pos.X - origin.X));
            var newH = Math.Max(2, Math.Abs(pos.Y - origin.Y));
            var s = Math.Max(newW / start.Width, newH / start.Height);
            if (s < 0.05) s = 0.05;
            var signX = pos.X >= origin.X ? 1.0 : -1.0;
            var signY = pos.Y >= origin.Y ? 1.0 : -1.0;
            foreach (var obj in _canvas.SelectedObjects)
                obj.ScaleAbout(origin, signX * s, signY * s);
        }

        _canvas.NotifyChanged();
    }

    private void RestoreFromSnapshots()
    {
        var lives = _canvas.SelectedObjects.ToList();
        for (var i = 0; i < lives.Count && i < _resizeSnapshots.Count; i++)
            CopyGeometry(_resizeSnapshots[i], lives[i]);
    }

    private static void CopyGeometry(AnnotationObject from, AnnotationObject to)
    {
        to.Color = from.Color;
        to.StrokeWidth = from.StrokeWidth;
        to.Opacity = from.Opacity;
        to.Rotation = from.Rotation;
        switch (from)
        {
            case PathAnnotation fp when to is PathAnnotation tp:
                tp.Points = fp.Points.Select(p => p).ToList();
                tp.IsHighlighter = fp.IsHighlighter;
                break;
            case LineAnnotation fl when to is LineAnnotation tl:
                tl.X1 = fl.X1; tl.Y1 = fl.Y1; tl.X2 = fl.X2; tl.Y2 = fl.Y2;
                break;
            case ShapeAnnotation fs when to is ShapeAnnotation ts:
                ts.Kind = fs.Kind;
                ts.X = fs.X; ts.Y = fs.Y; ts.Width = fs.Width; ts.Height = fs.Height;
                break;
            case TextAnnotation ft when to is TextAnnotation tt:
                tt.X = ft.X; tt.Y = ft.Y; tt.Text = ft.Text; tt.FontSize = ft.FontSize;
                break;
        }
    }

    private void EndResize()
    {
        _isResizing = false;
        _resizeHandle = ResizeHandle.None;
        _resizeSnapshots = [];
        if (_canvas.IsMouseCaptured)
            _canvas.ReleaseMouseCapture();
        SyncRotateHandleVisibility();
        EnsureChromeAboveMain();
        UpdateCursor();
        _toolbar?.RefreshUi();
    }

    private void BeginEditExistingText(TextAnnotation text)
    {
        CommitTextBox(confirm: true);
        _textPoint = new Point(text.X, text.Y);
        _textBox = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Microsoft YaHei UI"),
            FontSize = text.FontSize,
            Foreground = new SolidColorBrush(text.Color),
            Background = new SolidColorBrush(Color.FromArgb(220, 255, 255, 255)),
            BorderBrush = new SolidColorBrush(text.Color),
            BorderThickness = new Thickness(1),
            MinWidth = 160,
            MinHeight = 36,
            Padding = new Thickness(4),
            MaxWidth = 480,
            Text = text.Text,
            Tag = text.Id
        };
        Canvas.SetLeft(_textBox, text.X);
        Canvas.SetTop(_textBox, text.Y);
        TextHost.Children.Add(_textBox);
        _textBox.Focus();
        _textBox.SelectAll();
        _textBox.LostKeyboardFocus += (_, _) => CommitTextBox(confirm: true);
        _textBox.KeyDown += (s, ev) =>
        {
            if (ev.Key == Key.Escape)
            {
                CommitTextBox(confirm: false);
                ev.Handled = true;
            }
            else if (ev.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
            {
                CommitTextBox(confirm: true);
                ev.Handled = true;
            }
        };
        // 编辑时暂隐原文字
        text.Opacity = 0.15;
        _canvas.NotifyChanged();
    }

    private void EndObjectInteract(bool commitMove)
    {
        var wasMoving = _isMoving;
        var group = _moveGroup.ToList();
        var target = _moveTarget;
        _pendingObjectInteract = false;
        _isMoving = false;
        _moveTarget = null;
        _moveGroup = [];
        if (_canvas.IsMouseCaptured)
            _canvas.ReleaseMouseCapture();

        if (wasMoving || commitMove)
        {
            if (group.Count > 0)
                _canvas.SetSelection(group);
            else if (target != null)
                _canvas.SetSelected(target);
        }
        else if (target != null)
        {
            // 单击选中（单选）
            _canvas.SetSelected(target);
        }

        SyncRotateHandleVisibility();
        EnsureChromeAboveMain();
        UpdateCursor();
        _toolbar?.RefreshUi();
    }

    private void EndBrushErase()
    {
        _brushErasing = false;
        _historyPushedForStroke = false;
        if (_canvas.IsMouseCaptured)
            _canvas.ReleaseMouseCapture();
        EnsureChromeAboveMain();
    }

    private void EndMarquee(bool commit)
    {
        _marqueeSelecting = false;
        if (_canvas.IsMouseCaptured)
            _canvas.ReleaseMouseCapture();

        var end = Mouse.GetPosition(_canvas);
        var r = NormalizeRect(_startPoint, end);
        _canvas.SetMarqueePreview(null);

        if (commit && r.Width > 2 && r.Height > 2)
        {
            var hits = _canvas.Objects.Where(o => o.IntersectsRect(r)).ToList();
            if (hits.Count > 0)
            {
                CurrentHistory.Push(_canvas.Objects);
                _canvas.RemoveObjects(hits.Select(h => h.Id));
                _toolbar?.RefreshUi();
            }
        }

        EnsureChromeAboveMain();
    }

    private void EndSelectMarquee(bool commit)
    {
        _selectMarquee = false;
        _rightBecameMarquee = false;
        if (_canvas.IsMouseCaptured && !_rightGestureActive)
            _canvas.ReleaseMouseCapture();

        var end = Mouse.GetPosition(_canvas);
        var origin = _rightGestureActive ? _rightDownPos : _startPoint;
        var r = NormalizeRect(origin, end);
        _canvas.SetMarqueePreview(null);

        if (commit && (r.Width > 2 || r.Height > 2))
        {
            var hits = _canvas.Objects.Where(o => o.IntersectsRect(r)).ToList();
            _canvas.SetSelection(hits);
            _toolbar?.RefreshUi();
        }
        else if (commit)
        {
            _canvas.ClearSelection();
            _toolbar?.RefreshUi();
        }

        SyncRotateHandleVisibility();
        EnsureChromeAboveMain();
        UpdateCursor();
    }

    private void HandleRightGestureMove(Point pos)
    {
        var dist = (pos - _rightDownPos).Length;
        var heldMs = (DateTime.UtcNow - _rightDownUtc).TotalMilliseconds;

        // CR-010：拖距≥5px，或长按≥300ms且已有轻微移动 → 进入框选
        if (!_rightBecameMarquee && (dist >= 5 || (heldMs >= 300 && dist >= 2)))
        {
            _rightBecameMarquee = true;
            _selectMarquee = true;
            _startPoint = _rightDownPos;
            _canvas.SetMarqueePreview(NormalizeRect(_rightDownPos, pos), MarqueeKind.Select);
        }

        if (_rightBecameMarquee)
            _canvas.SetMarqueePreview(NormalizeRect(_rightDownPos, pos), MarqueeKind.Select);
    }

    private void OnCanvasRightDown(object sender, MouseButtonEventArgs e)
    {
        if (!_isActiveMode) return;

        // 绘制中右键先取消；长短按判定在 Up
        if (_isDrawing || _brushErasing || _marqueeSelecting || _selectMarquee
            || _pendingObjectInteract || _isMoving)
            CancelInProgress();

        var pos = e.GetPosition(_canvas);
        _rightGestureActive = true;
        _rightBecameMarquee = false;
        _rightDownUtc = DateTime.UtcNow;
        _rightDownPos = pos;
        _startPoint = pos;
        _canvas.CaptureMouse();
        e.Handled = true;
    }

    private void OnCanvasRightUp(object sender, MouseButtonEventArgs e)
    {
        if (!_rightGestureActive)
        {
            e.Handled = true;
            return;
        }

        var pos = e.GetPosition(_canvas);
        var dist = (pos - _rightDownPos).Length;
        var heldMs = (DateTime.UtcNow - _rightDownUtc).TotalMilliseconds;
        var becameMarquee = _rightBecameMarquee;

        if (!becameMarquee && (dist >= 5 || (heldMs >= 300 && dist >= 2)))
        {
            // Up 瞬间刚达到阈值：仍按框选收尾
            becameMarquee = true;
            _rightBecameMarquee = true;
            _selectMarquee = true;
        }

        _rightGestureActive = false;

        if (becameMarquee)
        {
            EndSelectMarquee(commit: true);
        }
        else if (dist < 5 && heldMs < 300)
        {
            // 短按：切换移动模式
            if (_canvas.IsMouseCaptured)
                _canvas.ReleaseMouseCapture();
            ToggleMoveMode();
        }
        else
        {
            // 长按未拖：不切换、不框选
            if (_canvas.IsMouseCaptured)
                _canvas.ReleaseMouseCapture();
            _canvas.SetMarqueePreview(null);
            _selectMarquee = false;
        }

        SyncChromePointerState();
        e.Handled = true;
    }

    private void FinishDrawing()
    {
        if (!_isDrawing) return;
        _isDrawing = false;
        if (_canvas.IsMouseCaptured)
            _canvas.ReleaseMouseCapture();

        // CR-016：临时画笔松手后淡出，不进对象/撤销
        if (CurrentTool == ToolType.TempPen && _currentPath != null)
        {
            var temp = _currentPath;
            _currentPath = null;
            _previewShape = null;
            _canvas.SetPreview(null);
            if (temp.Points.Count >= 2)
                _canvas.BeginTempStrokeFade(temp);
            EnsureChromeAboveMain();
            return;
        }

        AnnotationObject? committed = null;
        if (_currentPath != null && _currentPath.Points.Count >= 2)
            committed = _currentPath;
        else if (_previewShape != null)
        {
            // Skip tiny accidental shapes (lines/arrows always keep)
            var b = _previewShape.GetBounds();
            if (b.Width >= 2 || b.Height >= 2 || _previewShape is LineAnnotation or ArrowAnnotation)
                committed = _previewShape;
        }

        _currentPath = null;
        _previewShape = null;
        _canvas.SetPreview(null);

        if (committed != null)
        {
            CurrentHistory.Push(_canvas.Objects);
            _canvas.AddObject(committed);
            _toolbar?.RefreshUi();
        }

        EnsureChromeAboveMain();
    }

    private void BeginTextInput(Point pos)
    {
        CommitTextBox(confirm: true);
        _textPoint = pos;
        _textBox = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Microsoft YaHei UI"),
            FontSize = CurrentFontSize(),
            Foreground = new SolidColorBrush(CurrentColor),
            Background = new SolidColorBrush(Color.FromArgb(220, 255, 255, 255)),
            BorderBrush = new SolidColorBrush(CurrentColor),
            BorderThickness = new Thickness(1),
            MinWidth = 160,
            MinHeight = 36,
            Padding = new Thickness(4),
            MaxWidth = 480
        };
        Canvas.SetLeft(_textBox, pos.X);
        Canvas.SetTop(_textBox, pos.Y);
        // Host textbox in an overlay canvas layer
        if (TextHost.Children.Count == 0 || !TextHost.Children.Contains(_textBox))
            TextHost.Children.Add(_textBox);
        _textBox.LostKeyboardFocus += (_, _) => CommitTextBox(confirm: true);
        _textBox.KeyDown += (s, ev) =>
        {
            if (ev.Key == Key.Escape)
            {
                CommitTextBox(confirm: false);
                ev.Handled = true;
            }
            else if (ev.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
            {
                CommitTextBox(confirm: true);
                ev.Handled = true;
            }
        };
        _textBox.Focus();
    }

    private void CommitTextBox(bool confirm)
    {
        if (_textBox == null) return;
        var box = _textBox;
        _textBox = null;
        var text = box.Text?.Trim() ?? "";
        var editId = box.Tag as string;
        TextHost.Children.Remove(box);

        // 恢复被半透明隐藏的文字
        if (editId != null)
        {
            var existing = _canvas.Objects.OfType<TextAnnotation>().FirstOrDefault(t => t.Id == editId);
            if (existing != null)
                existing.Opacity = 1;
        }

        if (confirm && !string.IsNullOrWhiteSpace(text))
        {
            if (editId != null)
            {
                var existing = _canvas.Objects.OfType<TextAnnotation>().FirstOrDefault(t => t.Id == editId);
                if (existing != null)
                {
                    CurrentHistory.Push(_canvas.Objects);
                    existing.Text = text;
                    existing.FontSize = box.FontSize;
                    existing.Opacity = 1;
                    _canvas.NotifyChanged();
                    _toolbar?.RefreshUi();
                    return;
                }
            }

            CurrentHistory.Push(_canvas.Objects);
            _canvas.AddObject(new TextAnnotation
            {
                Color = CurrentColor,
                Opacity = 1,
                X = _textPoint.X,
                Y = _textPoint.Y,
                Text = text,
                FontSize = CurrentFontSize()
            });
            _toolbar?.RefreshUi();
        }
        else
        {
            _canvas.NotifyChanged();
        }
    }

    private void CancelInProgress()
    {
        if (_isResizing)
            EndResize();
        if (_isRotating)
            EndRotate();
        if (_rightGestureActive)
        {
            _rightGestureActive = false;
            _rightBecameMarquee = false;
            _selectMarquee = false;
            _canvas.SetMarqueePreview(null);
            if (_canvas.IsMouseCaptured)
                _canvas.ReleaseMouseCapture();
        }
        if (_pendingObjectInteract || _isMoving)
            EndObjectInteract(commitMove: false);
        if (_brushErasing)
            EndBrushErase();
        if (_marqueeSelecting)
            EndMarquee(commit: false);
        if (_selectMarquee)
            EndSelectMarquee(commit: false);
        if (_isDrawing)
        {
            _isDrawing = false;
            _currentPath = null;
            _previewShape = null;
            _canvas.SetPreview(null);
            if (_canvas.IsMouseCaptured)
                _canvas.ReleaseMouseCapture();
        }
        CommitTextBox(confirm: false);
        SyncChromePointerState();
    }

    public void Undo()
    {
        if (!_isActiveMode) return;
        CancelInProgress();
        var snapshot = CurrentHistory.Undo(_canvas.Objects);
        if (snapshot != null)
        {
            _canvas.SetObjects(snapshot);
            _toolbar?.RefreshUi();
        }
    }

    public void Redo()
    {
        if (!_isActiveMode) return;
        CancelInProgress();
        var snapshot = CurrentHistory.Redo(_canvas.Objects);
        if (snapshot != null)
        {
            _canvas.SetObjects(snapshot);
            _toolbar?.RefreshUi();
        }
    }    public void ClearCanvas()
    {
        if (!_isActiveMode) return;
        if (_canvas.Objects.Count == 0) return;

        // CR-007：有笔迹时二次确认；确认后仍入撤销栈
        var confirm = MessageBox.Show(
            _toolbar is { IsVisible: true } ? (Window)_toolbar : this,
            "确定清空当前画布上的所有标注吗？此操作可撤销。",
            "清屏",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question);
        if (confirm != MessageBoxResult.OK) return;

        CancelInProgress();
        CurrentHistory.Push(_canvas.Objects);
        _canvas.ClearObjects();
        _toolbar?.RefreshUi();
    }    public void SaveBoard(bool saveAs = false)
    {
        var path = _currentFilePath;
        if (saveAs || string.IsNullOrEmpty(path))
        {
            var dlg = new SaveFileDialog
            {
                Filter = "标注工程 (*.board)|*.board|JSON (*.json)|*.json",
                DefaultExt = ".board",
                FileName = "标注_" + DateTime.Now.ToString("yyyyMMdd_HHmmss")
            };
            if (dlg.ShowDialog(this) != true) return;
            path = dlg.FileName;
        }

        PersistCurrentStore();
        _boardFile.Save(
            path!,
            Width,
            Height,
            _overlayStore,
            _boardStore,
            _contentMode,
            _selectedScreenIndex);
        _currentFilePath = path;
        ShowInfo("工程已保存。");
    }    public void OpenBoard()
    {
        var dlg = new OpenFileDialog
        {
            Filter = "标注工程 (*.board;*.json)|*.board;*.json|所有文件|*.*"
        };
        if (dlg.ShowDialog(this) != true) return;

        try
        {
            var (doc, overlay, board) = _boardFile.Open(dlg.FileName);
            if (!_resolutionWarned &&
                (Math.Abs(doc.Canvas.Width - Width) > 1 || Math.Abs(doc.Canvas.Height - Height) > 1))
            {
                ShowInfo(
                    $"工程画布尺寸（{doc.Canvas.Width:0}×{doc.Canvas.Height:0}）与当前屏幕（{Width:0}×{Height:0}）不一致，已按左上对齐显示。");
                _resolutionWarned = true;
            }

            CancelInProgress();
            _overlayStore = overlay;
            _boardStore = board;
            _historyOverlay.Clear();
            _historyBoard.Clear();
            _contentMode = string.Equals(doc.ContentMode, "board", StringComparison.OrdinalIgnoreCase)
                ? ContentMode.Board
                : ContentMode.Overlay;
            if (doc.SelectedScreenIndex >= 0)
                _selectedScreenIndex = doc.SelectedScreenIndex;
            CoverSelectedScreen();
            ApplyContentModeVisuals();
            LoadCurrentStoreToCanvas();
            _currentFilePath = dlg.FileName;
            _toolbar?.RefreshUi();
            if (!_isActiveMode) EnterActiveMode();
        }
        catch (Exception ex)
        {
            ShowInfo($"打开失败：{ex.Message}", MessageBoxImage.Error);
        }
    }    public void ExportPng()
    {
        var dlg = new SaveFileDialog
        {
            Filter = "PNG 图片 (*.png)|*.png",
            DefaultExt = ".png",
            FileName = "标注导出_" + DateTime.Now.ToString("yyyyMMdd_HHmmss")
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var whiteBg = _contentMode == ContentMode.Board;
            _export.ExportAnnotationsPng(dlg.FileName, Width, Height, _canvas.Objects, opaqueWhiteBackground: whiteBg);
            ShowInfo(whiteBg ? "已导出白板 PNG。" : "已导出透明底 PNG。");
        }
        catch (Exception ex)
        {
            ShowInfo($"导出失败：{ex.Message}", MessageBoxImage.Error);
        }
    }    public void CaptureWithAnnotations()
    {
        var dlg = new SaveFileDialog
        {
            Filter = "PNG 图片 (*.png)|*.png",
            DefaultExt = ".png",
            FileName = "截屏标注_" + DateTime.Now.ToString("yyyyMMdd_HHmmss")
        };
        if (dlg.ShowDialog(this) != true) return;

        try
        {
            if (_contentMode == ContentMode.Board)
            {
                _export.ExportBoardFullscreen(dlg.FileName, Width, Height, _canvas.Objects);
                ShowInfo("白板截图已保存。");
                return;
            }

            _toolbar?.Hide();
            _tab?.Hide();
            Opacity = 0;
            Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Render);
            System.Threading.Thread.Sleep(80);

            var screen = GetSelectedScreen();
            var bounds = new Rect(screen.Bounds.Left, screen.Bounds.Top, screen.Bounds.Width, screen.Bounds.Height);
            _export.CaptureScreenWithAnnotations(dlg.FileName, bounds, _canvas.Objects);

            ShowInfo("截屏带标注已保存。");
        }
        catch (Exception ex)
        {
            ShowInfo($"截屏失败：{ex.Message}", MessageBoxImage.Error);
        }
        finally
        {
            Opacity = 1;
            UpdateChromeVisibility();
        }
    }


    public void OpenSettings()
    {
        if (!_isActiveMode)
            EnterActiveMode(expandToolbar: true);
        if (_settingsWindow == null)
        {
            _settingsWindow = new SettingsWindow(_settings, ApplyHotkeys);
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        }
        _settingsWindow.Owner = _toolbar is { IsVisible: true } ? _toolbar : _tab;
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    public void OpenHelp()
    {
        if (!_isActiveMode)
            EnterActiveMode(expandToolbar: true);
        if (_helpWindow == null)
        {
            _helpWindow = new HelpWindow();
            _helpWindow.Closed += (_, _) => _helpWindow = null;
        }
        _helpWindow.Owner = _toolbar is { IsVisible: true } ? _toolbar : _tab;
        _helpWindow.Show();
        _helpWindow.Activate();
    }

    private void ApplyHotkeys()
    {
        var results = _hotkeys.RegisterAll(_settings.Settings.Hotkeys);
        var failed = results.Where(r => r.Error != null).ToList();
        if (failed.Count > 0)
        {
            var msg = string.Join("\n", failed.Select(f =>
                $"{AppSettings.HotkeyLabels.GetValueOrDefault(f.ActionId, f.ActionId)}: {f.Error}"));
            ShowInfo($"部分快捷键注册失败：\n\n{msg}", MessageBoxImage.Warning);
        }
        else
        {
            ShowInfo("快捷键已生效。");
        }
    }

    private void ShowInfo(string message, MessageBoxImage icon = MessageBoxImage.Information)
    {
        var wasTopmost = Topmost;
        try
        {
            Topmost = false;
            if (_toolbar != null) _toolbar.Topmost = false;
            if (_tab != null) _tab.Topmost = false;
            var owner = _toolbar is { IsVisible: true } ? (Window)_toolbar
                : _tab is { IsVisible: true } ? _tab
                : this;
            MessageBox.Show(owner, message, "屏幕标注白板", MessageBoxButton.OK, icon);
        }
        finally
        {
            Topmost = wasTopmost;
            if (_toolbar != null) _toolbar.Topmost = true;
            if (_tab != null) _tab.Topmost = true;
        }
    }

    private void OnHotkeyPressed(string actionId)
    {
        Dispatcher.BeginInvoke(() =>
        {
            switch (actionId)
            {
                case "toggle_overlay":
                    ToggleOverlayMode();
                    break;
                case "clear_canvas":
                    ClearCanvas();
                    break;
                case "undo":
                    Undo();
                    break;
                case "redo":
                    Redo();
                    break;
                case "save":
                    SaveBoard();
                    break;
                case "toggle_record":
                    ToggleScreenRecording();
                    break;
            }
        });
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CancelInProgress();
            e.Handled = true;
            return;
        }

        // CR-013：Delete / Backspace 删除选中（文字输入框内不删对象）
        if (e.Key is Key.Delete or Key.Back)
        {
            if (TryDeleteSelection())
                e.Handled = true;
        }
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        // Local shortcuts as fallback when window focused
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Z) { Undo(); e.Handled = true; }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Y) { Redo(); e.Handled = true; }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.S) { SaveBoard(); e.Handled = true; }
    }

    /// <returns>true 若已处理删除。</returns>
    private bool TryDeleteSelection()
    {
        if (!_isActiveMode) return false;
        if (_textBox != null) return false;
        if (Keyboard.FocusedElement is System.Windows.Controls.TextBox) return false;
        if (_canvas.SelectedCount == 0) return false;

        CancelInProgress();
        CurrentHistory.Push(_canvas.Objects);
        _canvas.RemoveObjects(_canvas.SelectedObjects.Select(o => o.Id));
        _toolbar?.RefreshUi();
        return true;
    }

    public bool CanUndo => CurrentHistory.CanUndo;
    public bool CanRedo => CurrentHistory.CanRedo;

    public void ExitApp()
    {
        if (_recorder?.IsRecording == true)
        {
            var r = MessageBox.Show(
                _toolbar is { IsVisible: true } ? _toolbar : this,
                "正在录屏，是否停止并保存后退出？",
                "屏幕标注白板",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question);
            if (r == MessageBoxResult.Cancel) return;
            if (r == MessageBoxResult.Yes)
                StopScreenRecording();
            else
            {
                try { _recorder.Stop(discardOutput: true); } catch { }
                try { _recorder.Dispose(); } catch { }
                _recorder = null;
                _recordOutputPath = null;
                StopRecordUiTimer();
            }
        }

        // CR-013：先卸钩子/热键/托盘，再 Shutdown，避免退出后桌面卡顿
        CleanupForExit();
        Application.Current.Shutdown();
    }

    private void CleanupForExit()
    {
        StopRecordUiTimer();
        try
        {
            if (_recorder?.IsRecording == true)
                _recorder.Stop();
            _recorder?.Dispose();
            _recorder = null;
        }
        catch { /* ignore */ }
        try { _hotkeys.Dispose(); } catch { /* ignore */ }
        try { _tray.Dispose(); } catch { /* ignore */ }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        CleanupForExit();
        try { _toolbar?.Close(); } catch { }
        try { _tab?.Close(); } catch { }
        try { _settingsWindow?.Close(); } catch { }
        try { _helpWindow?.Close(); } catch { }
    }
}
