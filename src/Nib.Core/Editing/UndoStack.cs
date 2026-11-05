using System;
using System.Collections.Generic;
using Nib.Core.Ink;
using Nib.Core.Model;

namespace Nib.Core.Editing;

public interface IUndoCommand
{
    string Name { get; }
    void Do(Page page);
    void Undo(Page page);
}

/// <summary>Unlimited per-page undo/redo with grouping (an eraser drag is one step).</summary>
public sealed class UndoStack
{
    private readonly List<IUndoCommand> _undo = new();
    private readonly List<IUndoCommand> _redo = new();
    private CompositeCommand? _group;
    private int _groupDepth;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public int UndoCount => _undo.Count;
    public string? NextUndoName => _undo.Count > 0 ? _undo[^1].Name : null;

    public event Action? Changed;

    /// <summary>Records a command that has already been applied.</summary>
    public void Push(IUndoCommand cmd)
    {
        if (_group is not null) { _group.Add(cmd); return; }
        _undo.Add(cmd);
        _redo.Clear();
        Changed?.Invoke();
    }

    public void BeginGroup(string name)
    {
        if (_groupDepth++ == 0) _group = new CompositeCommand(name);
    }

    public void EndGroup()
    {
        if (_groupDepth == 0) return;
        if (--_groupDepth > 0) return;
        var g = _group!;
        _group = null;
        if (g.Count > 0) Push(g.Count == 1 ? g.Single : g);
    }

    public bool Undo(Page page)
    {
        if (_groupDepth > 0 || _undo.Count == 0) return false;
        var cmd = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        cmd.Undo(page);
        _redo.Add(cmd);
        Changed?.Invoke();
        return true;
    }

    public bool Redo(Page page)
    {
        if (_groupDepth > 0 || _redo.Count == 0) return false;
        var cmd = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        cmd.Do(page);
        _undo.Add(cmd);
        Changed?.Invoke();
        return true;
    }

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
        _group = null;
        _groupDepth = 0;
        Changed?.Invoke();
    }
}

/// <summary>Adds, removes or replaces strokes. Covers writing, both erasers, lasso transforms, recolor, cut and paste.</summary>
public sealed class StrokesCommand : IUndoCommand
{
    public string Name { get; }
    public IReadOnlyList<Stroke> Removed { get; }
    public IReadOnlyList<Stroke> Added { get; }

    public StrokesCommand(string name, IReadOnlyList<Stroke>? removed, IReadOnlyList<Stroke>? added)
    {
        Name = name;
        Removed = removed ?? Array.Empty<Stroke>();
        Added = added ?? Array.Empty<Stroke>();
    }

    public static StrokesCommand Add(Stroke s) => new("Ink", null, new[] { s });

    public void Do(Page page) => page.ReplaceStrokes(Removed, Added);
    public void Undo(Page page) => page.ReplaceStrokes(Added, Removed);
}

/// <summary>Creates, edits, moves or deletes a text container.</summary>
public sealed class TextCommand : IUndoCommand
{
    public string Name { get; }
    public TextContainer? Old { get; }
    public TextContainer? New { get; }

    public TextCommand(string name, TextContainer? oldText, TextContainer? newText)
    {
        if (oldText is null && newText is null) throw new ArgumentException("Nothing to do.");
        Name = name; Old = oldText; New = newText;
    }

    public void Do(Page page)
    {
        if (New is not null) page.PutText(New);
        else page.RemoveText(Old!.Id);
    }

    public void Undo(Page page)
    {
        if (Old is not null) page.PutText(Old);
        else page.RemoveText(New!.Id);
    }
}

public sealed class LayoutCommand : IUndoCommand
{
    public string Name => "Page setup";
    public PageLayout Old { get; }
    public PageLayout New { get; }
    public LayoutCommand(PageLayout oldLayout, PageLayout newLayout) { Old = oldLayout; New = newLayout; }
    public void Do(Page page) => page.SetLayout(New);
    public void Undo(Page page) => page.SetLayout(Old);
}

public sealed class CompositeCommand : IUndoCommand
{
    private readonly List<IUndoCommand> _items = new();
    public string Name { get; }
    public int Count => _items.Count;
    internal IUndoCommand Single => _items[0];

    public CompositeCommand(string name, IEnumerable<IUndoCommand>? items = null)
    {
        Name = name;
        if (items is not null) _items.AddRange(items);
    }

    public void Add(IUndoCommand cmd) => _items.Add(cmd);

    public void Do(Page page)
    {
        for (int i = 0; i < _items.Count; i++) _items[i].Do(page);
    }

    public void Undo(Page page)
    {
        for (int i = _items.Count - 1; i >= 0; i--) _items[i].Undo(page);
    }
}
