namespace ScreenAnnotator.Models;

public enum ToolType
{
    Pen,
    TempPen,
    Highlighter,
    Line,
    Arrow,
    Shape,
    Text,
    Move,
    EraserObject,
    EraserBrush,
    EraserMarquee
}

public enum StrokeThicknessTier
{
    Thin = 0,
    Medium = 1,
    Thick = 2
}

public enum FontSizeTier
{
    Small = 0,
    Medium = 1,
    Large = 2
}

public static class ToolTypeExtensions
{
    public static bool IsDrawingTool(this ToolType t) => t is
        ToolType.Pen or ToolType.TempPen or ToolType.Highlighter or ToolType.Line or ToolType.Arrow
        or ToolType.Shape or ToolType.Text;

    public static bool IsPenLike(this ToolType t) => t is ToolType.Pen or ToolType.TempPen or ToolType.Highlighter;

    public static bool IsEraserTool(this ToolType t) => t is
        ToolType.EraserObject or ToolType.EraserBrush or ToolType.EraserMarquee;

    public static bool IsMoveTool(this ToolType t) => t == ToolType.Move;
}
