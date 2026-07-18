using System.Windows;
using System.Windows.Media;

namespace ScreenAnnotator.Models;

public enum ShapeKind
{
    Rect,
    RoundedRect,
    Ellipse,
    Triangle,
    Diamond,
    Pentagon,
    Hexagon,
    Star,
    Parallelogram,
    Trapezoid
}

public static class ShapeKindInfo
{
    public static readonly (ShapeKind Kind, string Label)[] All =
    [
        (ShapeKind.Rect, "矩形"),
        (ShapeKind.RoundedRect, "圆角矩形"),
        (ShapeKind.Ellipse, "圆形"),
        (ShapeKind.Triangle, "三角形"),
        (ShapeKind.Diamond, "菱形"),
        (ShapeKind.Pentagon, "五边形"),
        (ShapeKind.Hexagon, "六边形"),
        (ShapeKind.Star, "五角星"),
        (ShapeKind.Parallelogram, "平行四边形"),
        (ShapeKind.Trapezoid, "梯形")
    ];

    public static string Label(ShapeKind kind)
        => All.FirstOrDefault(a => a.Kind == kind).Label ?? kind.ToString();

    public static string TypeName(ShapeKind kind) => kind switch
    {
        ShapeKind.Rect => "rect",
        ShapeKind.RoundedRect => "roundedRect",
        ShapeKind.Ellipse => "ellipse",
        ShapeKind.Triangle => "triangle",
        ShapeKind.Diamond => "diamond",
        ShapeKind.Pentagon => "pentagon",
        ShapeKind.Hexagon => "hexagon",
        ShapeKind.Star => "star",
        ShapeKind.Parallelogram => "parallelogram",
        ShapeKind.Trapezoid => "trapezoid",
        _ => "rect"
    };

    public static ShapeKind? ParseTypeName(string type) => type switch
    {
        "rect" => ShapeKind.Rect,
        "roundedRect" => ShapeKind.RoundedRect,
        "ellipse" => ShapeKind.Ellipse,
        "triangle" => ShapeKind.Triangle,
        "diamond" => ShapeKind.Diamond,
        "pentagon" => ShapeKind.Pentagon,
        "hexagon" => ShapeKind.Hexagon,
        "star" => ShapeKind.Star,
        "parallelogram" => ShapeKind.Parallelogram,
        "trapezoid" => ShapeKind.Trapezoid,
        _ => null
    };
}
