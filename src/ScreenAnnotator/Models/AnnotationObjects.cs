using System.Windows;
using System.Windows.Media;

namespace ScreenAnnotator.Models;

public sealed class BoardDocument
{
    public int Version { get; set; } = 2;
    public CanvasInfo Canvas { get; set; } = new();
    /// <summary>旧版单列表，兼容读取；保存时仍写入为 overlay 副本。</summary>
    public List<AnnotationDto> Objects { get; set; } = [];
    public List<AnnotationDto>? ObjectsOverlay { get; set; }
    public List<AnnotationDto>? ObjectsBoard { get; set; }
    public string? ContentMode { get; set; }
    public int SelectedScreenIndex { get; set; }
}

public enum ContentMode
{
    Overlay = 0,
    Board = 1
}

public sealed class CanvasInfo
{
    public double Width { get; set; }
    public double Height { get; set; }
}

public sealed class AnnotationDto
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "";
    public string Color { get; set; } = "#FF0000";
    public double StrokeWidth { get; set; } = 4;
    public double Opacity { get; set; } = 1;
    public double Rotation { get; set; }
    public List<PointDto>? Points { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double W { get; set; }
    public double H { get; set; }
    public string? Text { get; set; }
    public double FontSize { get; set; }
}

public sealed class PointDto
{
    public double X { get; set; }
    public double Y { get; set; }
}

/// <summary>In-memory annotation object (vector).</summary>
public abstract class AnnotationObject
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public Color Color { get; set; } = Colors.Red;
    public double StrokeWidth { get; set; } = 4;
    public double Opacity { get; set; } = 1.0;
    /// <summary>绕内容中心旋转角度（度）。几何存未旋转局部坐标。</summary>
    public double Rotation { get; set; }

    public abstract string TypeName { get; }
    public abstract void Draw(DrawingContext dc);
    public abstract bool HitTest(Point point, double tolerance);
    public virtual bool HitTestMoveable(Point point, double tolerance) => HitTest(point, tolerance);
    public abstract Rect GetBounds();
    /// <summary>不含描边的内容包围盒（未旋转局部坐标），用于缩放手柄。</summary>
    public abstract Rect GetContentBounds();
    public abstract void MoveBy(double dx, double dy);
    public abstract void ScaleAbout(Point origin, double sx, double sy);
    public virtual bool IntersectsRect(Rect rect) => GetBounds().IntersectsWith(rect);
    public abstract AnnotationObject Clone();
    public abstract AnnotationDto ToDto();

    public virtual Point GetCenter()
    {
        var b = GetContentBounds();
        return new Point(b.X + b.Width / 2, b.Y + b.Height / 2);
    }

    public void RotateBy(double deltaDegrees)
    {
        Rotation = NormalizeDegrees(Rotation + deltaDegrees);
    }

    public Point WorldToLocal(Point world) => RotatePoint(world, GetCenter(), -Rotation);
    public Point LocalToWorld(Point local) => RotatePoint(local, GetCenter(), Rotation);

    public static double NormalizeDegrees(double deg)
    {
        while (deg > 180) deg -= 360;
        while (deg <= -180) deg += 360;
        return deg;
    }

    public static Point RotatePoint(Point p, Point center, double degrees)
    {
        if (Math.Abs(degrees) < 0.0001) return p;
        var rad = degrees * Math.PI / 180.0;
        var cos = Math.Cos(rad);
        var sin = Math.Sin(rad);
        var dx = p.X - center.X;
        var dy = p.Y - center.Y;
        return new Point(center.X + dx * cos - dy * sin, center.Y + dx * sin + dy * cos);
    }

    public static Rect RotatedAabb(Rect local, Point center, double degrees)
    {
        if (local.IsEmpty) return local;
        if (Math.Abs(degrees) < 0.0001) return local;
        var pts = new[]
        {
            new Point(local.Left, local.Top),
            new Point(local.Right, local.Top),
            new Point(local.Left, local.Bottom),
            new Point(local.Right, local.Bottom)
        };
        var rotated = pts.Select(p => RotatePoint(p, center, degrees)).ToArray();
        var minX = rotated.Min(p => p.X);
        var minY = rotated.Min(p => p.Y);
        var maxX = rotated.Max(p => p.X);
        var maxY = rotated.Max(p => p.Y);
        return new Rect(minX, minY, Math.Max(1, maxX - minX), Math.Max(1, maxY - minY));
    }

    protected void PushRotation(DrawingContext dc)
    {
        if (Math.Abs(Rotation) < 0.0001) return;
        var c = GetCenter();
        dc.PushTransform(new RotateTransform(Rotation, c.X, c.Y));
    }

    protected void PopRotation(DrawingContext dc)
    {
        if (Math.Abs(Rotation) < 0.0001) return;
        dc.Pop();
    }

    protected Pen CreatePen()
    {
        var brush = new SolidColorBrush(Color) { Opacity = Opacity };
        brush.Freeze();
        var pen = new Pen(brush, StrokeWidth)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round
        };
        pen.Freeze();
        return pen;
    }

    protected AnnotationDto BaseDto() => new()
    {
        Id = Id,
        Type = TypeName,
        Color = Color.ToString(),
        StrokeWidth = StrokeWidth,
        Opacity = Opacity,
        Rotation = Rotation
    };

    public static AnnotationObject FromDto(AnnotationDto dto)
    {
        var color = (Color)ColorConverter.ConvertFromString(dto.Color)!;
        var shapeKind = ShapeKindInfo.ParseTypeName(dto.Type);
        if (shapeKind != null)
        {
            return new ShapeAnnotation
            {
                Id = dto.Id,
                Kind = shapeKind.Value,
                Color = color,
                StrokeWidth = dto.StrokeWidth,
                Opacity = dto.Opacity,
                Rotation = dto.Rotation,
                X = dto.X,
                Y = dto.Y,
                Width = dto.W,
                Height = dto.H
            };
        }

        return dto.Type switch
        {
            "pen" => new PathAnnotation
            {
                Id = dto.Id,
                IsHighlighter = false,
                Color = color,
                StrokeWidth = dto.StrokeWidth,
                Opacity = dto.Opacity,
                Rotation = dto.Rotation,
                Points = dto.Points?.Select(p => new Point(p.X, p.Y)).ToList() ?? []
            },
            "highlighter" => new PathAnnotation
            {
                Id = dto.Id,
                IsHighlighter = true,
                Color = color,
                StrokeWidth = dto.StrokeWidth,
                Opacity = dto.Opacity,
                Rotation = dto.Rotation,
                Points = dto.Points?.Select(p => new Point(p.X, p.Y)).ToList() ?? []
            },
            "line" => new LineAnnotation
            {
                Id = dto.Id,
                Color = color,
                StrokeWidth = dto.StrokeWidth,
                Opacity = dto.Opacity,
                Rotation = dto.Rotation,
                X1 = dto.X,
                Y1 = dto.Y,
                X2 = dto.X + dto.W,
                Y2 = dto.Y + dto.H
            },
            "arrow" => new ArrowAnnotation
            {
                Id = dto.Id,
                Color = color,
                StrokeWidth = dto.StrokeWidth,
                Opacity = dto.Opacity,
                Rotation = dto.Rotation,
                X1 = dto.X,
                Y1 = dto.Y,
                X2 = dto.X + dto.W,
                Y2 = dto.Y + dto.H
            },
            "text" => new TextAnnotation
            {
                Id = dto.Id,
                Color = color,
                Opacity = dto.Opacity,
                Rotation = dto.Rotation,
                X = dto.X,
                Y = dto.Y,
                Text = dto.Text ?? "",
                FontSize = dto.FontSize > 0 ? dto.FontSize : 28
            },
            _ => throw new InvalidOperationException($"Unknown type: {dto.Type}")
        };
    }
}

public sealed class PathAnnotation : AnnotationObject
{
    public bool IsHighlighter { get; set; }
    public List<Point> Points { get; set; } = [];
    public override string TypeName => IsHighlighter ? "highlighter" : "pen";

    public override void Draw(DrawingContext dc)
    {
        if (Points.Count < 2) return;
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(Points[0], false, false);
            for (var i = 1; i < Points.Count; i++)
                ctx.LineTo(Points[i], true, true);
        }
        geo.Freeze();
        PushRotation(dc);
        dc.DrawGeometry(null, CreatePen(), geo);
        PopRotation(dc);
    }

    public override bool HitTest(Point point, double tolerance)
    {
        var lp = WorldToLocal(point);
        var t = Math.Max(tolerance, StrokeWidth / 2 + 4);
        for (var i = 1; i < Points.Count; i++)
        {
            if (DistanceToSegment(lp, Points[i - 1], Points[i]) <= t)
                return true;
        }
        return false;
    }

    public override Rect GetBounds()
    {
        var content = GetContentBounds();
        if (content.IsEmpty) return Rect.Empty;
        var pad = StrokeWidth;
        var padded = new Rect(content.X - pad, content.Y - pad, content.Width + pad * 2, content.Height + pad * 2);
        return RotatedAabb(padded, GetCenter(), Rotation);
    }

    public override Rect GetContentBounds()
    {
        if (Points.Count == 0) return Rect.Empty;
        var minX = Points.Min(p => p.X);
        var minY = Points.Min(p => p.Y);
        var maxX = Points.Max(p => p.X);
        var maxY = Points.Max(p => p.Y);
        return new Rect(minX, minY, Math.Max(1, maxX - minX), Math.Max(1, maxY - minY));
    }

    public override AnnotationObject Clone() => new PathAnnotation
    {
        Id = Id,
        IsHighlighter = IsHighlighter,
        Color = Color,
        StrokeWidth = StrokeWidth,
        Opacity = Opacity,
        Rotation = Rotation,
        Points = Points.ToList()
    };

    public override AnnotationDto ToDto()
    {
        var dto = BaseDto();
        dto.Points = Points.Select(p => new PointDto { X = p.X, Y = p.Y }).ToList();
        return dto;
    }

    public override void MoveBy(double dx, double dy)
    {
        for (var i = 0; i < Points.Count; i++)
            Points[i] = new Point(Points[i].X + dx, Points[i].Y + dy);
    }

    public override void ScaleAbout(Point origin, double sx, double sy)
    {
        for (var i = 0; i < Points.Count; i++)
        {
            var p = Points[i];
            Points[i] = new Point(origin.X + (p.X - origin.X) * sx, origin.Y + (p.Y - origin.Y) * sy);
        }
    }

    public override bool HitTestMoveable(Point point, double tolerance)
    {
        if (HitTest(point, tolerance)) return true;
        var b = GetBounds();
        if (b.IsEmpty) return false;
        const double edge = 8;
        var outer = new Rect(b.X - 2, b.Y - 2, b.Width + 4, b.Height + 4);
        var inner = new Rect(b.X + edge, b.Y + edge, Math.Max(0, b.Width - edge * 2), Math.Max(0, b.Height - edge * 2));
        return outer.Contains(point) && (inner.Width < 1 || inner.Height < 1 || !inner.Contains(point));
    }

    public List<PathAnnotation> EraseAt(Point center, double radius)
    {
        var localCenter = WorldToLocal(center);
        var result = new List<PathAnnotation>();
        if (Points.Count == 0) return result;

        var remove = new bool[Points.Count];
        for (var i = 0; i < Points.Count; i++)
        {
            if ((Points[i] - localCenter).Length <= radius)
                remove[i] = true;
        }
        for (var i = 1; i < Points.Count; i++)
        {
            if (DistanceToSegment(localCenter, Points[i - 1], Points[i]) <= radius)
            {
                remove[i - 1] = true;
                remove[i] = true;
            }
        }

        if (!remove.Any(r => r))
            return [Clone() as PathAnnotation ?? this];

        var kept = new List<Point>();
        void Flush()
        {
            if (kept.Count >= 2)
            {
                result.Add(new PathAnnotation
                {
                    IsHighlighter = IsHighlighter,
                    Color = Color,
                    StrokeWidth = StrokeWidth,
                    Opacity = Opacity,
                    Rotation = Rotation,
                    Points = kept.ToList()
                });
            }
            kept.Clear();
        }

        for (var i = 0; i < Points.Count; i++)
        {
            if (remove[i]) Flush();
            else kept.Add(Points[i]);
        }
        Flush();
        return result;
    }

    private static double DistanceToSegment(Point p, Point a, Point b)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        if (dx == 0 && dy == 0) return (p - a).Length;
        var t = ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / (dx * dx + dy * dy);
        t = Math.Clamp(t, 0, 1);
        var proj = new Point(a.X + t * dx, a.Y + t * dy);
        return (p - proj).Length;
    }
}

public class LineAnnotation : AnnotationObject
{
    public double X1 { get; set; }
    public double Y1 { get; set; }
    public double X2 { get; set; }
    public double Y2 { get; set; }
    public override string TypeName => "line";

    public override Point GetCenter() => new((X1 + X2) / 2, (Y1 + Y2) / 2);

    public override void Draw(DrawingContext dc)
    {
        PushRotation(dc);
        dc.DrawLine(CreatePen(), new Point(X1, Y1), new Point(X2, Y2));
        PopRotation(dc);
    }

    public override bool HitTest(Point point, double tolerance)
    {
        var lp = WorldToLocal(point);
        var t = Math.Max(tolerance, StrokeWidth / 2 + 4);
        return DistanceToSegment(lp, new Point(X1, Y1), new Point(X2, Y2)) <= t;
    }

    public override Rect GetBounds()
    {
        var pad = StrokeWidth;
        var local = new Rect(
            Math.Min(X1, X2) - pad,
            Math.Min(Y1, Y2) - pad,
            Math.Abs(X2 - X1) + pad * 2,
            Math.Abs(Y2 - Y1) + pad * 2);
        return RotatedAabb(local, GetCenter(), Rotation);
    }

    public override Rect GetContentBounds()
        => new(Math.Min(X1, X2), Math.Min(Y1, Y2), Math.Max(1, Math.Abs(X2 - X1)), Math.Max(1, Math.Abs(Y2 - Y1)));

    public override AnnotationObject Clone() => new LineAnnotation
    {
        Id = Id, Color = Color, StrokeWidth = StrokeWidth, Opacity = Opacity, Rotation = Rotation,
        X1 = X1, Y1 = Y1, X2 = X2, Y2 = Y2
    };

    public override AnnotationDto ToDto()
    {
        var dto = BaseDto();
        dto.X = X1; dto.Y = Y1; dto.W = X2 - X1; dto.H = Y2 - Y1;
        return dto;
    }

    public override void MoveBy(double dx, double dy)
    {
        X1 += dx; Y1 += dy; X2 += dx; Y2 += dy;
    }

    public override void ScaleAbout(Point origin, double sx, double sy)
    {
        X1 = origin.X + (X1 - origin.X) * sx;
        Y1 = origin.Y + (Y1 - origin.Y) * sy;
        X2 = origin.X + (X2 - origin.X) * sx;
        Y2 = origin.Y + (Y2 - origin.Y) * sy;
    }

    public void SetEndpoint(int index, Point p)
    {
        if (index == 0) { X1 = p.X; Y1 = p.Y; }
        else { X2 = p.X; Y2 = p.Y; }
    }

    private static double DistanceToSegment(Point p, Point a, Point b)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        if (dx == 0 && dy == 0) return (p - a).Length;
        var t = ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / (dx * dx + dy * dy);
        t = Math.Clamp(t, 0, 1);
        var proj = new Point(a.X + t * dx, a.Y + t * dy);
        return (p - proj).Length;
    }
}

public sealed class ArrowAnnotation : LineAnnotation
{
    public override string TypeName => "arrow";

    public override void Draw(DrawingContext dc)
    {
        var start = new Point(X1, Y1);
        var end = new Point(X2, Y2);
        PushRotation(dc);
        dc.DrawLine(CreatePen(), start, end);

        var angle = Math.Atan2(Y2 - Y1, X2 - X1);
        var headLen = Math.Max(12, StrokeWidth * 4);
        var a1 = angle + Math.PI * 0.85;
        var a2 = angle - Math.PI * 0.85;
        var p1 = new Point(end.X + headLen * Math.Cos(a1), end.Y + headLen * Math.Sin(a1));
        var p2 = new Point(end.X + headLen * Math.Cos(a2), end.Y + headLen * Math.Sin(a2));
        dc.DrawLine(CreatePen(), end, p1);
        dc.DrawLine(CreatePen(), end, p2);
        PopRotation(dc);
    }

    public override AnnotationObject Clone() => new ArrowAnnotation
    {
        Id = Id, Color = Color, StrokeWidth = StrokeWidth, Opacity = Opacity, Rotation = Rotation,
        X1 = X1, Y1 = Y1, X2 = X2, Y2 = Y2
    };
}

/// <summary>封闭形状：矩形/圆/多边形等，对角拖拽外接矩形生成。</summary>
public sealed class ShapeAnnotation : AnnotationObject
{
    public ShapeKind Kind { get; set; } = ShapeKind.Rect;
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public override string TypeName => ShapeKindInfo.TypeName(Kind);

    public override void Draw(DrawingContext dc)
    {
        var r = NormalizeRect();
        if (r.Width < 1 || r.Height < 1) return;
        PushRotation(dc);
        var pen = CreatePen();
        switch (Kind)
        {
            case ShapeKind.Rect:
                dc.DrawRectangle(null, pen, r);
                break;
            case ShapeKind.RoundedRect:
            {
                var rad = Math.Min(r.Width, r.Height) * 0.18;
                dc.DrawRoundedRectangle(null, pen, r, rad, rad);
                break;
            }
            case ShapeKind.Ellipse:
                dc.DrawEllipse(null, pen,
                    new Point(r.X + r.Width / 2, r.Y + r.Height / 2),
                    r.Width / 2, r.Height / 2);
                break;
            default:
                DrawPolygon(dc, pen, r);
                break;
        }
        PopRotation(dc);
    }

    private void DrawPolygon(DrawingContext dc, Pen pen, Rect r)
    {
        var pts = GetPolygonPoints(r);
        if (pts.Length < 2) return;
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(pts[0], false, true);
            for (var i = 1; i < pts.Length; i++)
                ctx.LineTo(pts[i], true, true);
        }
        geo.Freeze();
        dc.DrawGeometry(null, pen, geo);
    }

    public Point[] GetPolygonPoints(Rect? rect = null)
    {
        var r = rect ?? NormalizeRect();
        var unit = GetUnitPoints(Kind);
        return unit.Select(u => new Point(r.X + u.X * r.Width, r.Y + u.Y * r.Height)).ToArray();
    }

    private static Point[] GetUnitPoints(ShapeKind kind) => kind switch
    {
        ShapeKind.Triangle => [new(0.5, 0), new(1, 1), new(0, 1)],
        ShapeKind.Diamond => [new(0.5, 0), new(1, 0.5), new(0.5, 1), new(0, 0.5)],
        ShapeKind.Pentagon => RegularPolygon(5, startAngleDeg: -90),
        ShapeKind.Hexagon => RegularPolygon(6, startAngleDeg: -90),
        ShapeKind.Star => StarPolygon(5),
        ShapeKind.Parallelogram => [new(0.22, 0), new(1, 0), new(0.78, 1), new(0, 1)],
        ShapeKind.Trapezoid => [new(0.18, 0), new(0.82, 0), new(1, 1), new(0, 1)],
        _ => [new(0, 0), new(1, 0), new(1, 1), new(0, 1)]
    };

    private static Point[] RegularPolygon(int sides, double startAngleDeg)
    {
        var pts = new Point[sides];
        for (var i = 0; i < sides; i++)
        {
            var a = (startAngleDeg + i * 360.0 / sides) * Math.PI / 180.0;
            pts[i] = new Point(0.5 + 0.5 * Math.Cos(a), 0.5 + 0.5 * Math.Sin(a));
        }
        return pts;
    }

    private static Point[] StarPolygon(int points)
    {
        var pts = new Point[points * 2];
        for (var i = 0; i < points * 2; i++)
        {
            var a = (-90 + i * 180.0 / points) * Math.PI / 180.0;
            var rad = i % 2 == 0 ? 0.5 : 0.2;
            pts[i] = new Point(0.5 + rad * Math.Cos(a), 0.5 + rad * Math.Sin(a));
        }
        return pts;
    }

    public override bool HitTest(Point point, double tolerance)
    {
        var lp = WorldToLocal(point);
        var r = NormalizeRect();
        var t = Math.Max(tolerance, StrokeWidth / 2 + 4);
        if (Kind == ShapeKind.Ellipse)
        {
            if (r.Width < 1 || r.Height < 1) return false;
            var cx = r.X + r.Width / 2;
            var cy = r.Y + r.Height / 2;
            var nx = (lp.X - cx) / (r.Width / 2);
            var ny = (lp.Y - cy) / (r.Height / 2);
            var d = Math.Sqrt(nx * nx + ny * ny);
            var tt = t / Math.Min(r.Width, r.Height) * 2;
            return Math.Abs(d - 1) <= tt;
        }

        if (Kind is ShapeKind.Rect or ShapeKind.RoundedRect)
        {
            var outer = new Rect(r.X - t, r.Y - t, r.Width + 2 * t, r.Height + 2 * t);
            var inner = new Rect(r.X + t, r.Y + t, Math.Max(0, r.Width - 2 * t), Math.Max(0, r.Height - 2 * t));
            return outer.Contains(lp) && !inner.Contains(lp);
        }

        var pts = GetPolygonPoints(r);
        for (var i = 0; i < pts.Length; i++)
        {
            var a = pts[i];
            var b = pts[(i + 1) % pts.Length];
            if (DistanceToSegment(lp, a, b) <= t) return true;
        }
        return false;
    }

    public override Rect GetBounds()
    {
        var r = NormalizeRect();
        var pad = StrokeWidth;
        var padded = new Rect(r.X - pad, r.Y - pad, r.Width + pad * 2, r.Height + pad * 2);
        return RotatedAabb(padded, GetCenter(), Rotation);
    }

    public override Rect GetContentBounds() => NormalizeRect();

    public override AnnotationObject Clone() => new ShapeAnnotation
    {
        Id = Id, Kind = Kind, Color = Color, StrokeWidth = StrokeWidth, Opacity = Opacity, Rotation = Rotation,
        X = X, Y = Y, Width = Width, Height = Height
    };

    public override AnnotationDto ToDto()
    {
        var r = NormalizeRect();
        var dto = BaseDto();
        dto.X = r.X; dto.Y = r.Y; dto.W = r.Width; dto.H = r.Height;
        return dto;
    }

    private Rect NormalizeRect()
    {
        var x = Width < 0 ? X + Width : X;
        var y = Height < 0 ? Y + Height : Y;
        return new Rect(x, y, Math.Abs(Width), Math.Abs(Height));
    }

    public override void MoveBy(double dx, double dy)
    {
        X += dx;
        Y += dy;
    }

    public override void ScaleAbout(Point origin, double sx, double sy)
    {
        var r = NormalizeRect();
        var x1 = origin.X + (r.X - origin.X) * sx;
        var y1 = origin.Y + (r.Y - origin.Y) * sy;
        var x2 = origin.X + (r.X + r.Width - origin.X) * sx;
        var y2 = origin.Y + (r.Y + r.Height - origin.Y) * sy;
        if (Kind == ShapeKind.Ellipse)
        {
            var size = Math.Max(2, Math.Max(Math.Abs(x2 - x1), Math.Abs(y2 - y1)));
            X = Math.Min(x1, x2);
            Y = Math.Min(y1, y2);
            Width = size;
            Height = size;
            return;
        }
        X = Math.Min(x1, x2);
        Y = Math.Min(y1, y2);
        Width = Math.Max(2, Math.Abs(x2 - x1));
        Height = Math.Max(2, Math.Abs(y2 - y1));
    }

    public void SetContentRect(Rect r)
    {
        X = r.X;
        Y = r.Y;
        Width = Math.Max(2, r.Width);
        Height = Math.Max(2, r.Height);
    }

    public void SetContentCircle(Point fixedCorner, Point movingCorner)
    {
        var size = Math.Max(2, Math.Max(Math.Abs(movingCorner.X - fixedCorner.X), Math.Abs(movingCorner.Y - fixedCorner.Y)));
        X = movingCorner.X < fixedCorner.X ? fixedCorner.X - size : fixedCorner.X;
        Y = movingCorner.Y < fixedCorner.Y ? fixedCorner.Y - size : fixedCorner.Y;
        Width = size;
        Height = size;
    }

    private static double DistanceToSegment(Point p, Point a, Point b)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        if (dx == 0 && dy == 0) return (p - a).Length;
        var t = ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / (dx * dx + dy * dy);
        t = Math.Clamp(t, 0, 1);
        var proj = new Point(a.X + t * dx, a.Y + t * dy);
        return (p - proj).Length;
    }
}

public sealed class TextAnnotation : AnnotationObject
{
    public double X { get; set; }
    public double Y { get; set; }
    public string Text { get; set; } = "";
    public double FontSize { get; set; } = 28;
    public override string TypeName => "text";

    private FormattedText CreateFt(System.Windows.Media.Brush brush)
    {
        var dpi = 1.0;
        if (Application.Current?.MainWindow != null)
            dpi = VisualTreeHelper.GetDpi(Application.Current.MainWindow).PixelsPerDip;
        return new FormattedText(
            string.IsNullOrEmpty(Text) ? " " : Text,
            System.Globalization.CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface("Microsoft YaHei UI"),
            FontSize,
            brush,
            dpi);
    }

    public override void Draw(DrawingContext dc)
    {
        var brush = new SolidColorBrush(Color) { Opacity = Opacity };
        brush.Freeze();
        var ft = CreateFt(brush);
        PushRotation(dc);
        dc.DrawText(ft, new Point(X, Y));
        PopRotation(dc);
    }

    public override bool HitTest(Point point, double tolerance)
    {
        var lp = WorldToLocal(point);
        return GetContentBounds().Contains(lp);
    }

    public override Rect GetBounds()
    {
        var content = GetContentBounds();
        return RotatedAabb(content, GetCenter(), Rotation);
    }

    public override Rect GetContentBounds()
    {
        var ft = CreateFt(Brushes.Black);
        return new Rect(X, Y, ft.WidthIncludingTrailingWhitespace + 4, ft.Height + 4);
    }

    public override AnnotationObject Clone() => new TextAnnotation
    {
        Id = Id, Color = Color, Opacity = Opacity, Rotation = Rotation,
        X = X, Y = Y, Text = Text, FontSize = FontSize
    };

    public override AnnotationDto ToDto()
    {
        var dto = BaseDto();
        dto.X = X; dto.Y = Y; dto.Text = Text; dto.FontSize = FontSize;
        return dto;
    }

    public override void MoveBy(double dx, double dy)
    {
        X += dx;
        Y += dy;
    }

    public override void ScaleAbout(Point origin, double sx, double sy)
    {
        var s = (Math.Abs(sx) + Math.Abs(sy)) / 2;
        if (s < 0.05) s = 0.05;
        X = origin.X + (X - origin.X) * (sx < 0 ? -s : s);
        Y = origin.Y + (Y - origin.Y) * (sy < 0 ? -s : s);
        FontSize = Math.Clamp(FontSize * s, 8, 200);
    }
}
