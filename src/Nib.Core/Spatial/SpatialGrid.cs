using System;
using System.Collections.Generic;
using Nib.Core.Geometry;

namespace Nib.Core.Spatial;

/// <summary>An object that can live in a <see cref="SpatialGrid{T}"/>.</summary>
public interface ISpatialItem
{
    RectF Bounds { get; }
    /// <summary>Scratch field used by the grid to de-duplicate query results without allocating.</summary>
    int QueryStamp { get; set; }
}

/// <summary>
/// Sparse uniform grid over an unbounded plane (works for infinite canvases). Eraser, lasso, tile
/// rendering and hit testing query it, so their cost is O(items near the query), not O(items on the page).
/// Not thread-safe: owned by the ink thread.
/// </summary>
public sealed class SpatialGrid<T> where T : class, ISpatialItem
{
    private readonly float _cellSize;
    private readonly float _inv;
    private readonly Dictionary<long, List<T>> _cells = new();
    private readonly Stack<List<T>> _pool = new();
    private int _stamp;

    public int Count { get; private set; }

    public SpatialGrid(float cellSize = 256f)
    {
        if (cellSize <= 0) throw new ArgumentOutOfRangeException(nameof(cellSize));
        _cellSize = cellSize;
        _inv = 1f / cellSize;
    }

    public float CellSize => _cellSize;

    private static long Key(int cx, int cy) => ((long)cx << 32) | (uint)cy;

    private void CellRange(in RectF r, out int x0, out int y0, out int x1, out int y1)
    {
        x0 = (int)MathF.Floor(r.Left * _inv);
        y0 = (int)MathF.Floor(r.Top * _inv);
        x1 = (int)MathF.Floor(r.Right * _inv);
        y1 = (int)MathF.Floor(r.Bottom * _inv);
    }

    public void Insert(T item)
    {
        var b = item.Bounds;
        if (b.IsEmpty) return;
        CellRange(b, out int x0, out int y0, out int x1, out int y1);
        for (int cy = y0; cy <= y1; cy++)
        for (int cx = x0; cx <= x1; cx++)
        {
            long k = Key(cx, cy);
            if (!_cells.TryGetValue(k, out var list))
            {
                list = _pool.Count > 0 ? _pool.Pop() : new List<T>(8);
                _cells[k] = list;
            }
            list.Add(item);
        }
        Count++;
    }

    /// <summary>Removes an item. <paramref name="bounds"/> must be the bounds it was inserted with.</summary>
    public bool Remove(T item, RectF bounds)
    {
        if (bounds.IsEmpty) return false;
        bool removed = false;
        CellRange(bounds, out int x0, out int y0, out int x1, out int y1);
        for (int cy = y0; cy <= y1; cy++)
        for (int cx = x0; cx <= x1; cx++)
        {
            long k = Key(cx, cy);
            if (!_cells.TryGetValue(k, out var list)) continue;
            int idx = list.IndexOf(item);
            if (idx < 0) continue;
            // Order inside a cell is irrelevant: swap-remove is O(1).
            int last = list.Count - 1;
            list[idx] = list[last];
            list.RemoveAt(last);
            removed = true;
            if (list.Count == 0)
            {
                _cells.Remove(k);
                _pool.Push(list);
            }
        }
        if (removed) Count--;
        return removed;
    }

    public bool Remove(T item) => Remove(item, item.Bounds);

    /// <summary>Appends every item whose bounds intersect <paramref name="rect"/>. Each item appears once.</summary>
    public void Query(RectF rect, List<T> results)
    {
        if (rect.IsEmpty || _cells.Count == 0) return;
        int stamp = NextStamp();
        CellRange(rect, out int x0, out int y0, out int x1, out int y1);
        long cellsInRect = (long)(x1 - x0 + 1) * (y1 - y0 + 1);
        if (cellsInRect > _cells.Count * 4L)
        {
            // Huge query (zoomed far out): walking occupied cells is cheaper than walking the rect.
            foreach (var list in _cells.Values) Collect(list, rect, stamp, results);
            return;
        }
        for (int cy = y0; cy <= y1; cy++)
        for (int cx = x0; cx <= x1; cx++)
            if (_cells.TryGetValue(Key(cx, cy), out var list)) Collect(list, rect, stamp, results);
    }

    private static void Collect(List<T> list, in RectF rect, int stamp, List<T> results)
    {
        for (int i = 0; i < list.Count; i++)
        {
            var it = list[i];
            if (it.QueryStamp == stamp) continue;
            it.QueryStamp = stamp;
            if (it.Bounds.Intersects(rect)) results.Add(it);
        }
    }

    private int NextStamp()
    {
        _stamp++;
        if (_stamp == 0) _stamp = 1;
        return _stamp;
    }

    public void Clear()
    {
        foreach (var l in _cells.Values) { l.Clear(); _pool.Push(l); }
        _cells.Clear();
        Count = 0;
    }
}
