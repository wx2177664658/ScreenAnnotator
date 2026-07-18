using ScreenAnnotator.Models;

namespace ScreenAnnotator.Services;

public sealed class HistoryService
{
    private const int MaxSteps = 80;
    private readonly List<List<AnnotationObject>> _undo = [];
    private readonly List<List<AnnotationObject>> _redo = [];

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public void Push(IReadOnlyList<AnnotationObject> current)
    {
        _undo.Add(CloneList(current));
        if (_undo.Count > MaxSteps)
            _undo.RemoveAt(0);
        _redo.Clear();
    }

    public List<AnnotationObject>? Undo(IReadOnlyList<AnnotationObject> current)
    {
        if (_undo.Count == 0) return null;
        _redo.Add(CloneList(current));
        var snapshot = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        return CloneList(snapshot);
    }

    public List<AnnotationObject>? Redo(IReadOnlyList<AnnotationObject> current)
    {
        if (_redo.Count == 0) return null;
        _undo.Add(CloneList(current));
        var snapshot = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        return CloneList(snapshot);
    }

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
    }

    private static List<AnnotationObject> CloneList(IReadOnlyList<AnnotationObject> source)
        => source.Select(o => o.Clone()).ToList();
}
