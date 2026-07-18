using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ScreenAnnotator.Models;

namespace ScreenAnnotator.Controls;

public enum MarqueeKind
{
    Select,
    Delete
}

public enum ResizeHandle
{
    None,
    NW,
    NE,
    SW,
    SE,
    Endpoint1,
    Endpoint2,
    /// <summary>CR-015：顶边中点上方旋转手柄。</summary>
    Rotate
}

public sealed class AnnotationCanvas : FrameworkElement
{
    /// <summary>CR-016：临时画笔淡出时长（秒）。</summary>
    public const double TempPenFadeSeconds = 0.3;

    private readonly List<AnnotationObject> _objects = [];
    private AnnotationObject? _preview;
    private readonly List<TempStroke> _tempStrokes = [];
    private DispatcherTimer? _tempFadeTimer;
    private readonly HashSet<string> _selectedIds = new(StringComparer.Ordinal);
    private Rect? _marqueePreview;
    private MarqueeKind _marqueeKind = MarqueeKind.Select;
    private readonly VisualCollection _visuals;
    private readonly DrawingVisual _contentVisual = new();

    private sealed class TempStroke
    {
        public required PathAnnotation Path { get; init; }
        public required double BaseOpacity { get; init; }
        public DateTime FadeStartUtc { get; set; }
    }

    public AnnotationCanvas()
    {
        _visuals = new VisualCollection(this) { _contentVisual };
        Focusable = true;
        SizeChanged += (_, _) => Redraw();
    }

    protected override HitTestResult? HitTestCore(PointHitTestParameters hitTestParameters)
        => new PointHitTestResult(this, hitTestParameters.HitPoint);

    public IReadOnlyList<AnnotationObject> Objects => _objects;
    public int SelectedCount => _selectedIds.Count;

    public IReadOnlyList<AnnotationObject> SelectedObjects
        => _objects.Where(o => _selectedIds.Contains(o.Id)).ToList();

    /// <summary>兼容单选：返回多选中的第一个。</summary>
    public AnnotationObject? SelectedObject
        => SelectedObjects.FirstOrDefault();

    public string? SelectedId => SelectedObject?.Id;

    public void SetObjects(IEnumerable<AnnotationObject> objects)
    {
        _objects.Clear();
        _objects.AddRange(objects);
        _selectedIds.RemoveWhere(id => _objects.All(o => o.Id != id));
        Redraw();
    }

    public void AddObject(AnnotationObject obj)
    {
        _objects.Add(obj);
        Redraw();
    }

    public void RemoveObject(AnnotationObject obj)
    {
        _objects.RemoveAll(o => o.Id == obj.Id);
        _selectedIds.Remove(obj.Id);
        Redraw();
    }

    public void RemoveObjects(IEnumerable<string> ids)
    {
        var set = ids.ToHashSet();
        _objects.RemoveAll(o => set.Contains(o.Id));
        foreach (var id in set)
            _selectedIds.Remove(id);
        Redraw();
    }

    public void ClearObjects()
    {
        _objects.Clear();
        _preview = null;
        _selectedIds.Clear();
        _marqueePreview = null;
        ClearTempStrokes();
        Redraw();
    }

    public void SetPreview(AnnotationObject? preview)
    {
        _preview = preview;
        Redraw();
    }

    /// <summary>松手后加入淡出队列（不进对象列表）。</summary>
    public void BeginTempStrokeFade(PathAnnotation path)
    {
        if (path.Points.Count < 2) return;
        _tempStrokes.Add(new TempStroke
        {
            Path = path,
            BaseOpacity = path.Opacity,
            FadeStartUtc = DateTime.UtcNow
        });
        EnsureTempFadeTimer();
        Redraw();
    }

    /// <summary>切换工具等：立即清除临时笔（含未完成预览由调用方清）。</summary>
    public void ClearTempStrokes()
    {
        _tempStrokes.Clear();
        StopTempFadeTimer();
        Redraw();
    }

    /// <summary>录屏合成：永久对象 + 淡出中临时笔 + 当前预览。</summary>
    public List<AnnotationObject> SnapshotForRecording()
    {
        var list = new List<AnnotationObject>(_objects.Count + _tempStrokes.Count + 1);
        list.AddRange(_objects);
        foreach (var t in _tempStrokes)
            list.Add(t.Path);
        if (_preview != null)
            list.Add(_preview);
        return list;
    }

    private void EnsureTempFadeTimer()
    {
        if (_tempFadeTimer != null) return;
        _tempFadeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _tempFadeTimer.Tick += (_, _) => TickTempFade();
        _tempFadeTimer.Start();
    }

    private void StopTempFadeTimer()
    {
        if (_tempFadeTimer == null) return;
        _tempFadeTimer.Stop();
        _tempFadeTimer = null;
    }

    private void TickTempFade()
    {
        if (_tempStrokes.Count == 0)
        {
            StopTempFadeTimer();
            return;
        }

        var now = DateTime.UtcNow;
        var changed = false;
        for (var i = _tempStrokes.Count - 1; i >= 0; i--)
        {
            var t = _tempStrokes[i];
            var elapsed = (now - t.FadeStartUtc).TotalSeconds;
            if (elapsed >= TempPenFadeSeconds)
            {
                _tempStrokes.RemoveAt(i);
                changed = true;
                continue;
            }

            var factor = 1.0 - elapsed / TempPenFadeSeconds;
            var next = t.BaseOpacity * factor;
            if (Math.Abs(t.Path.Opacity - next) > 0.01)
            {
                t.Path.Opacity = next;
                changed = true;
            }
        }

        if (_tempStrokes.Count == 0)
            StopTempFadeTimer();
        if (changed)
            Redraw();
    }

    public void SetMarqueePreview(Rect? rect, MarqueeKind kind = MarqueeKind.Select)
    {
        _marqueePreview = rect;
        _marqueeKind = kind;
        Redraw();
    }

    public void SetSelected(AnnotationObject? obj)
    {
        _selectedIds.Clear();
        if (obj != null)
            _selectedIds.Add(obj.Id);
        Redraw();
    }

    public void SetSelection(IEnumerable<AnnotationObject> objects)
    {
        _selectedIds.Clear();
        foreach (var o in objects)
            _selectedIds.Add(o.Id);
        Redraw();
    }

    public bool IsSelected(AnnotationObject obj) => _selectedIds.Contains(obj.Id);

    public void ClearSelection()
    {
        if (_selectedIds.Count == 0) return;
        _selectedIds.Clear();
        Redraw();
    }

    public AnnotationObject? HitTestTop(Point point)
    {
        for (var i = _objects.Count - 1; i >= 0; i--)
        {
            if (_objects[i].HitTest(point, 6))
                return _objects[i];
        }
        return null;
    }

    public AnnotationObject? HitTestMoveableTop(Point point)
    {
        for (var i = _objects.Count - 1; i >= 0; i--)
        {
            if (_objects[i].HitTestMoveable(point, 6))
                return _objects[i];
        }
        return null;
    }

    public const double HandleSize = 8;
    public const double RotateHandleOffset = 22;

    /// <summary>移动模式/移动工具下显示旋转手柄（CR-015）。</summary>
    public bool ShowRotateHandle { get; set; }

    public IReadOnlyList<(ResizeHandle Handle, Point Center)> GetHandlesForSelection()
    {
        var list = new List<(ResizeHandle, Point)>();
        if (_selectedIds.Count == 0) return list;

        if (_selectedIds.Count == 1)
        {
            var obj = SelectedObject!;
            if (obj is LineAnnotation line)
            {
                list.Add((ResizeHandle.Endpoint1, obj.LocalToWorld(new Point(line.X1, line.Y1))));
                list.Add((ResizeHandle.Endpoint2, obj.LocalToWorld(new Point(line.X2, line.Y2))));
                if (ShowRotateHandle)
                    list.Add((ResizeHandle.Rotate, GetRotateHandleCenter(obj)));
                return list;
            }

            var b = obj.GetContentBounds();
            if (b.IsEmpty) return list;
            list.Add((ResizeHandle.NW, obj.LocalToWorld(new Point(b.Left, b.Top))));
            list.Add((ResizeHandle.NE, obj.LocalToWorld(new Point(b.Right, b.Top))));
            list.Add((ResizeHandle.SW, obj.LocalToWorld(new Point(b.Left, b.Bottom))));
            list.Add((ResizeHandle.SE, obj.LocalToWorld(new Point(b.Right, b.Bottom))));
            if (ShowRotateHandle)
                list.Add((ResizeHandle.Rotate, GetRotateHandleCenter(obj)));
            return list;
        }

        // 多选：包络框四角（整体等比缩放）
        var env = GetSelectionEnvelope();
        if (env.IsEmpty) return list;
        list.Add((ResizeHandle.NW, new Point(env.Left, env.Top)));
        list.Add((ResizeHandle.NE, new Point(env.Right, env.Top)));
        list.Add((ResizeHandle.SW, new Point(env.Left, env.Bottom)));
        list.Add((ResizeHandle.SE, new Point(env.Right, env.Bottom)));
        return list;
    }

    public static Point GetRotateHandleCenter(AnnotationObject obj)
    {
        var b = obj.GetContentBounds();
        var local = new Point(b.Left + b.Width / 2, b.Top - RotateHandleOffset);
        return obj.LocalToWorld(local);
    }

    public Rect GetSelectionEnvelope()
    {
        Rect? env = null;
        foreach (var o in SelectedObjects)
        {
            var b = o.GetContentBounds();
            if (b.IsEmpty) continue;
            env = env == null ? b : Rect.Union(env.Value, b);
        }
        return env ?? Rect.Empty;
    }

    public ResizeHandle HitTestHandle(Point point)
    {
        const double hit = HandleSize + 4;
        const double rotateHit = HandleSize + 6;
        // 旋转手柄优先于角点，减少误触
        foreach (var (handle, center) in GetHandlesForSelection())
        {
            if (handle != ResizeHandle.Rotate) continue;
            if ((point - center).Length <= rotateHit)
                return ResizeHandle.Rotate;
        }
        foreach (var (handle, center) in GetHandlesForSelection())
        {
            if (handle == ResizeHandle.Rotate) continue;
            if ((point - center).Length <= hit)
                return handle;
        }
        return ResizeHandle.None;
    }

    public static Point OppositeCorner(ResizeHandle handle, Rect bounds) => handle switch
    {
        ResizeHandle.NW => new Point(bounds.Right, bounds.Bottom),
        ResizeHandle.NE => new Point(bounds.Left, bounds.Bottom),
        ResizeHandle.SW => new Point(bounds.Right, bounds.Top),
        ResizeHandle.SE => new Point(bounds.Left, bounds.Top),
        _ => new Point(bounds.Left, bounds.Top)
    };

    public static System.Windows.Input.Cursor CursorForHandle(ResizeHandle handle) => handle switch
    {
        ResizeHandle.NW or ResizeHandle.SE => System.Windows.Input.Cursors.SizeNWSE,
        ResizeHandle.NE or ResizeHandle.SW => System.Windows.Input.Cursors.SizeNESW,
        ResizeHandle.Endpoint1 or ResizeHandle.Endpoint2 => System.Windows.Input.Cursors.Cross,
        ResizeHandle.Rotate => System.Windows.Input.Cursors.Hand,
        _ => System.Windows.Input.Cursors.Arrow
    };

    public void ReplacePathWithSegments(PathAnnotation original, List<PathAnnotation> segments)
    {
        var idx = _objects.FindIndex(o => o.Id == original.Id);
        if (idx < 0) return;
        _objects.RemoveAt(idx);
        _selectedIds.Remove(original.Id);
        for (var i = 0; i < segments.Count; i++)
            _objects.Insert(idx + i, segments[i]);
        Redraw();
    }

    public void NotifyChanged() => Redraw();

    public void Redraw()
    {
        using var dc = _contentVisual.RenderOpen();
        var w = ActualWidth > 0 ? ActualWidth : RenderSize.Width;
        var h = ActualHeight > 0 ? ActualHeight : RenderSize.Height;
        if (w > 0 && h > 0)
        {
            var hitBrush = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
            hitBrush.Freeze();
            dc.DrawRectangle(hitBrush, null, new Rect(0, 0, w, h));
        }

        foreach (var obj in _objects)
            obj.Draw(dc);

        // CR-016：临时画笔（淡出中），画在永久对象之上、选框之下
        foreach (var t in _tempStrokes)
            t.Path.Draw(dc);

        foreach (var sel in SelectedObjects)
        {
            var b = sel.GetBounds();
            if (b.IsEmpty) continue;
            var pen = new Pen(new SolidColorBrush(Color.FromArgb(180, 33, 150, 243)), 1.5)
            {
                DashStyle = DashStyles.Dash
            };
            pen.Freeze();
            dc.DrawRectangle(null, pen, Rect.Inflate(b, 3, 3));
        }

        if (_selectedIds.Count > 1)
        {
            var env = GetSelectionEnvelope();
            if (!env.IsEmpty)
            {
                var pen = new Pen(new SolidColorBrush(Color.FromArgb(160, 255, 152, 0)), 1.5)
                {
                    DashStyle = DashStyles.Dash
                };
                pen.Freeze();
                dc.DrawRectangle(null, pen, Rect.Inflate(env, 4, 4));
            }
        }

        // 调节手柄（旋转手柄画成圆点）
        var handleFill = new SolidColorBrush(Color.FromRgb(33, 150, 243));
        handleFill.Freeze();
        var rotateFill = new SolidColorBrush(Color.FromRgb(255, 152, 0));
        rotateFill.Freeze();
        var handleStroke = new Pen(Brushes.White, 1);
        handleStroke.Freeze();
        var hs = HandleSize;
        foreach (var (handle, center) in GetHandlesForSelection())
        {
            if (handle == ResizeHandle.Rotate)
            {
                // 顶边中点到旋转手柄连线
                if (SelectedObject is { } sel)
                {
                    var b = sel.GetContentBounds();
                    var topMid = sel.LocalToWorld(new Point(b.Left + b.Width / 2, b.Top));
                    var linePen = new Pen(new SolidColorBrush(Color.FromArgb(160, 255, 152, 0)), 1);
                    linePen.Freeze();
                    dc.DrawLine(linePen, topMid, center);
                }
                dc.DrawEllipse(rotateFill, handleStroke, center, hs / 2, hs / 2);
            }
            else
            {
                var r = new Rect(center.X - hs / 2, center.Y - hs / 2, hs, hs);
                dc.DrawRectangle(handleFill, handleStroke, r);
            }
        }

        if (_marqueePreview is { } mq && mq.Width > 0 && mq.Height > 0)
        {
            Color fillC;
            Color strokeC;
            if (_marqueeKind == MarqueeKind.Delete)
            {
                fillC = Color.FromArgb(40, 244, 67, 54);
                strokeC = Color.FromArgb(200, 244, 67, 54);
            }
            else
            {
                fillC = Color.FromArgb(40, 33, 150, 243);
                strokeC = Color.FromArgb(200, 33, 150, 243);
            }
            var fill = new SolidColorBrush(fillC);
            fill.Freeze();
            var pen = new Pen(new SolidColorBrush(strokeC), 1) { DashStyle = DashStyles.Dash };
            pen.Freeze();
            dc.DrawRectangle(fill, pen, mq);
        }

        _preview?.Draw(dc);
    }

    protected override int VisualChildrenCount => _visuals.Count;
    protected override Visual GetVisualChild(int index) => _visuals[index];

    protected override Size MeasureOverride(Size availableSize)
    {
        var w = double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width;
        var h = double.IsInfinity(availableSize.Height) ? 0 : availableSize.Height;
        return new Size(w, h);
    }

    protected override Size ArrangeOverride(Size finalSize) => finalSize;
}
