using System.Text.Json.Serialization;

namespace ScreenAnnotator.Models;

public sealed class AppSettings
{
    public Dictionary<string, string> Hotkeys { get; set; } = new(DefaultHotkeys);

    public static IReadOnlyDictionary<string, string> DefaultHotkeys { get; } = new Dictionary<string, string>
    {
        ["toggle_overlay"] = "Ctrl+Shift+A",
        ["clear_canvas"] = "Ctrl+Shift+C",
        ["undo"] = "Ctrl+Z",
        ["redo"] = "Ctrl+Y",
        ["save"] = "Ctrl+S",
        ["toggle_record"] = "Ctrl+Shift+R"
    };

    public static readonly Dictionary<string, string> HotkeyLabels = new()
    {
        ["toggle_overlay"] = "显示/隐藏标注层",
        ["clear_canvas"] = "清屏",
        ["undo"] = "撤销",
        ["redo"] = "重做",
        ["save"] = "保存",
        ["toggle_record"] = "开始/停止录屏"
    };

    public void ResetHotkeys()
    {
        Hotkeys = new Dictionary<string, string>(DefaultHotkeys);
    }
}

public static class StylePresets
{
    public static double StrokeWidth(StrokeThicknessTier tier) => tier switch
    {
        StrokeThicknessTier.Thin => 2,
        StrokeThicknessTier.Thick => 8,
        _ => 4
    };

    public static double FontSize(FontSizeTier tier) => tier switch
    {
        FontSizeTier.Small => 18,
        FontSizeTier.Large => 42,
        _ => 28
    };

    public const double HighlighterOpacity = 0.45;

    /// <summary>笔刷橡皮半径，跟随粗细档。</summary>
    public static double EraserBrushRadius(StrokeThicknessTier tier) => tier switch
    {
        StrokeThicknessTier.Thin => 10,
        StrokeThicknessTier.Thick => 28,
        _ => 18
    };

    public const double MoveDragThreshold = 4;
}
