using System;
using System.Collections.Generic;
using Nib.Core.Editing;
using Nib.Core.Geometry;
using Nib.Core.Ink;
using Nib.Core.Spatial;

namespace Nib.Core.Model;

/// <summary>Notified on the ink thread after every page mutation (renderer invalidation, autosave, tag index).</summary>
public interface IPageObserver
{
    void OnStrokesChanged(Page page, IReadOnlyList<Stroke> removed, IReadOnlyList<Stroke> added);
    void OnTextChanged(Page page, TextContainer? oldText, TextContainer? newText);
    void OnLayoutChanged(Page page, PageLayout oldLayout);
    void OnTitleChanged(Page page);
}

/// <summary>
/// The live, editable content of one page. Owned by the ink thread: every read and write happens there,
/// so no locks are needed and the renderer always sees a consistent page. Strokes and text containers are
/// immutable, so they can be handed to background threads (autosave, export) as-is.
/// </summary>
public sealed class Page
{
    private readonly List<Stroke> _strokes = new();
    private readonly Dictionary<long, Stroke> _strokesById = new();
    private readonly List<TextContainer> _texts = new();
    private readonly Dictionary<long, TextContainer> _textsById = new();
    private readonly List<IPageObserver> _observers = new(2);
    private static readonly IReadOnlyList<Stroke> NoStrokes = Array.Empty<Stroke>();
    private long _maxZ;

    public long Id { get; }
    public long SectionId { get; set; }
    public string Title { get; private set; }
    public PageLayout Layout { get; private set; }
    public IReadOnlyList<Stroke> Strokes => _strokes;
    public IReadOnlyList<TextContainer> Texts => _texts;
    public SpatialGrid<Stroke> StrokeIndex { get; } = new(256f);
    public SpatialGrid<TextContainer> TextIndex { get; } = new(512f);
    /// <summary>Undo history. Settable so the history survives the page being evicted from memory and reloaded.</summary>
    public UndoStack Undo { get; internal set; } = new();
    /// <summary>Incremented on every mutation.</summary>
    public int Version { get; private set; }

    public Page(long id, string title, PageLayout layout)
    {
        Id = id;
        Title = title;
        Layout = layout;
    }

    /// <summary>Builds a page from storage in one pass (no observer notifications, single sort).</summary>
    public static Page Load(long id, long sectionId, string title, PageLayout layout, IEnumerable<Stroke> strokes, IEnumerable<TextContainer> texts)
    {
        var p = new Page(id, title, layout) { SectionId = sectionId };
        foreach (var s in strokes)
        {
            if (!p._strokesById.TryAdd(s.Id, s)) continue;
            p._strokes.Add(s);
            p.StrokeIndex.Insert(s);
            if (s.Z > p._maxZ) p._maxZ = s.Z;
        }
        p._strokes.Sort(static (a, b) => a.Z.CompareTo(b.Z));
        foreach (var t in texts)
        {
            if (!p._textsById.TryAdd(t.Id, t)) continue;
            p._texts.Add(t);
            p.TextIndex.Insert(t);
            if (t.Z > p._maxZ) p._maxZ = t.Z;
        }
        p._texts.Sort(static (a, b) => a.Z.CompareTo(b.Z));
        return p;
    }

    public void AddObserver(IPageObserver o) { if (!_observers.Contains(o)) _observers.Add(o); }
    public void RemoveObserver(IPageObserver o) => _observers.Remove(o);

    /// <summary>Allocates the next paint order value (new content goes on top).</summary>
    public long NextZ() => ++_maxZ;

    public Stroke? FindStroke(long id) => _strokesById.TryGetValue(id, out var s) ? s : null;
    public TextContainer? FindText(long id) => _textsById.TryGetValue(id, out var t) ? t : null;

    /// <summary>Runs a command and records it for undo.</summary>
    public void Execute(IUndoCommand cmd)
    {
        cmd.Do(this);
        Undo.Push(cmd);
    }

    public bool UndoLast() => Undo.Undo(this);
    public bool RedoLast() => Undo.Redo(this);

    public RectF ContentBounds
    {
        get
        {
            var r = RectF.Empty;
            for (int i = 0; i < _strokes.Count; i++) r = r.Union(_strokes[i].Bounds);
            for (int i = 0; i < _texts.Count; i++) r = r.Union(_texts[i].Bounds);
            return r;
        }
    }

    // ---- Mutations: used by commands so every change is undoable and observed. ----

    internal void ReplaceStrokes(IReadOnlyList<Stroke>? removed, IReadOnlyList<Stroke>? added)
    {
        removed ??= NoStrokes;
        added ??= NoStrokes;
        for (int i = 0; i < removed.Count; i++)
        {
            var s = removed[i];
            if (!_strokesById.TryGetValue(s.Id, out var current)) continue;
            _strokesById.Remove(s.Id);
            StrokeIndex.Remove(current, current.Bounds);
            int idx = IndexOfZ(_strokes, current.Z, current);
            if (idx >= 0) _strokes.RemoveAt(idx);
        }
        float bottom = float.NegativeInfinity;
        for (int i = 0; i < added.Count; i++)
        {
            var s = added[i];
            if (_strokesById.TryGetValue(s.Id, out var dup))
            {
                // Defensive: replacing an id that was not listed as removed.
                StrokeIndex.Remove(dup, dup.Bounds);
                int di = IndexOfZ(_strokes, dup.Z, dup);
                if (di >= 0) _strokes.RemoveAt(di);
            }
            _strokesById[s.Id] = s;
            _strokes.Insert(InsertionIndex(_strokes, s.Z), s);
            StrokeIndex.Insert(s);
            if (s.Z > _maxZ) _maxZ = s.Z;
            if (s.Bounds.Bottom > bottom) bottom = s.Bounds.Bottom;
        }
        Version++;
        for (int i = 0; i < _observers.Count; i++) _observers[i].OnStrokesChanged(this, removed, added);
        if (added.Count > 0) GrowSheetsFor(bottom);
    }

    internal void PutText(TextContainer t)
    {
        _textsById.TryGetValue(t.Id, out var old);
        if (old is not null)
        {
            TextIndex.Remove(old, old.Bounds);
            int idx = IndexOfZ(_texts, old.Z, old);
            if (idx >= 0) _texts.RemoveAt(idx);
        }
        _textsById[t.Id] = t;
        _texts.Insert(InsertionIndex(_texts, t.Z), t);
        TextIndex.Insert(t);
        if (t.Z > _maxZ) _maxZ = t.Z;
        Version++;
        for (int i = 0; i < _observers.Count; i++) _observers[i].OnTextChanged(this, old, t);
        GrowSheetsFor(t.Bounds.Bottom);
    }

    internal void RemoveText(long id)
    {
        if (!_textsById.TryGetValue(id, out var old)) return;
        _textsById.Remove(id);
        TextIndex.Remove(old, old.Bounds);
        int idx = IndexOfZ(_texts, old.Z, old);
        if (idx >= 0) _texts.RemoveAt(idx);
        Version++;
        for (int i = 0; i < _observers.Count; i++) _observers[i].OnTextChanged(this, old, null);
    }

    /// <summary>
    /// Records a renderer-measured height without creating an undo step (it is derived data).
    /// </summary>
    public void UpdateMeasuredHeight(long textId, float height)
    {
        if (!_textsById.TryGetValue(textId, out var t) || MathF.Abs(t.MeasuredHeight - height) < 0.5f) return;
        var updated = t.WithMeasuredHeight(height);
        updated.RenderCache = t.RenderCache;
        TextIndex.Remove(t, t.Bounds);
        int idx = IndexOfZ(_texts, t.Z, t);
        if (idx >= 0) _texts[idx] = updated;
        _textsById[textId] = updated;
        TextIndex.Insert(updated);
        GrowSheetsFor(updated.Bounds.Bottom);
    }

    internal void SetLayout(PageLayout layout)
    {
        if (layout == Layout) return;
        var old = Layout;
        Layout = layout;
        Version++;
        for (int i = 0; i < _observers.Count; i++) _observers[i].OnLayoutChanged(this, old);
    }

    public void SetTitle(string title)
    {
        if (title == Title) return;
        Title = title;
        Version++;
        for (int i = 0; i < _observers.Count; i++) _observers[i].OnTitleChanged(this);
    }

    private void GrowSheetsFor(float bottom)
    {
        if (Layout.Mode != PageMode.Fixed || float.IsInfinity(bottom)) return;
        var grown = Layout.EnsureSheetsFor(bottom);
        if (!ReferenceEquals(grown, Layout)) SetLayout(grown);
    }

    private static int InsertionIndex<T>(List<T> list, long z) where T : class
    {
        int lo = 0, hi = list.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (ZOf(list[mid]) <= z) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    private static int IndexOfZ<T>(List<T> list, long z, T item) where T : class
    {
        int lo = 0, hi = list.Count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            long mz = ZOf(list[mid]);
            if (mz < z) lo = mid + 1;
            else if (mz > z) hi = mid - 1;
            else
            {
                // Equal Z values are rare (imports); scan the neighborhood.
                for (int i = mid; i >= 0 && ZOf(list[i]) == z; i--) if (ReferenceEquals(list[i], item)) return i;
                for (int i = mid + 1; i < list.Count && ZOf(list[i]) == z; i++) if (ReferenceEquals(list[i], item)) return i;
                return -1;
            }
        }
        return list.IndexOf(item);
    }

    private static long ZOf<T>(T item) => item switch
    {
        Stroke s => s.Z,
        TextContainer t => t.Z,
        _ => 0,
    };
}
