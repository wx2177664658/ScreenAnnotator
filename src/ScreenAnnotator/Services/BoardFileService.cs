using System.IO;
using System.Text.Json;
using ScreenAnnotator.Models;

namespace ScreenAnnotator.Services;

public sealed class BoardFileService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public void Save(
        string path,
        double canvasWidth,
        double canvasHeight,
        IEnumerable<AnnotationObject> overlayObjects,
        IEnumerable<AnnotationObject> boardObjects,
        ContentMode contentMode,
        int selectedScreenIndex)
    {
        var overlay = overlayObjects.Select(o => o.ToDto()).ToList();
        var board = boardObjects.Select(o => o.ToDto()).ToList();
        var doc = new BoardDocument
        {
            Version = 2,
            Canvas = new CanvasInfo { Width = canvasWidth, Height = canvasHeight },
            Objects = overlay,
            ObjectsOverlay = overlay,
            ObjectsBoard = board,
            ContentMode = contentMode == ContentMode.Board ? "board" : "overlay",
            SelectedScreenIndex = selectedScreenIndex
        };
        File.WriteAllText(path, JsonSerializer.Serialize(doc, JsonOptions));
    }

    public (BoardDocument Doc, List<AnnotationObject> Overlay, List<AnnotationObject> Board) Open(string path)
    {
        var json = File.ReadAllText(path);
        var doc = JsonSerializer.Deserialize<BoardDocument>(json, JsonOptions)
                  ?? throw new InvalidOperationException("无法解析工程文件。");

        List<AnnotationDto> overlayDto;
        List<AnnotationDto> boardDto;

        if (doc.ObjectsOverlay != null || doc.ObjectsBoard != null)
        {
            overlayDto = doc.ObjectsOverlay ?? doc.Objects ?? [];
            boardDto = doc.ObjectsBoard ?? [];
        }
        else
        {
            // 旧文件：仅 objects → 视为模式 A
            overlayDto = doc.Objects ?? [];
            boardDto = [];
        }

        var overlay = overlayDto.Select(AnnotationObject.FromDto).ToList();
        var board = boardDto.Select(AnnotationObject.FromDto).ToList();
        return (doc, overlay, board);
    }
}
