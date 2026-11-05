using System;
using System.Collections.Generic;
using Microsoft.Graphics.Canvas;
using Nib.Core.Geometry;
using Nib.Core.Ink;
using Nib.Core.Rendering;

namespace Nib.App.Ink;

internal readonly record struct TileKey(long PageId, int ZoomKey, int X, int Y);

internal sealed class TileEntry
{
    public required CanvasRenderTarget Target;
    public long LastUsedFrame;
    /// <summary>Content is stale and must be re-rasterized before it is shown again.</summary>
    public bool Dirty;
    /// <summary>Strokes added since rasterization, drawn on top at the next frame (cheap incremental update).</summary>
    public List<Stroke>? PendingAdds;
}

/// <summary>
/// GPU tile cache (512×512 px render targets). Least-recently-used eviction with a budget derived from
/// the viewport, and pooled render targets so panning never allocates GPU memory in steady state.
/// Ink-thread only.
/// </summary>
internal sealed class TileCache : IDisposable
{
    private readonly Dictionary<TileKey, TileEntry> _tiles = new();
    private readonly Stack<CanvasRenderTarget> _pool = new();
    private readonly List<TileKey> _scratch = new();
    private CanvasDevice _device;

    public int Budget { get; set; } = 96;
    public int Count => _tiles.Count;
    public long Frame { get; set; }

    public TileCache(CanvasDevice device) => _device = device;

    public bool TryGet(TileKey key, out TileEntry entry)
    {
        if (_tiles.TryGetValue(key, out entry!))
        {
            entry.LastUsedFrame = Frame;
            return true;
        }
        return false;
    }

    public TileEntry Add(TileKey key)
    {
        if (_tiles.Count >= Budget) EvictOldest(Math.Max(1, _tiles.Count - Budget + 1));
        var rt = _pool.Count > 0 ? _pool.Pop() : new CanvasRenderTarget(_device, TileMath.TileSize, TileMath.TileSize, 96f);
        var e = new TileEntry { Target = rt, LastUsedFrame = Frame };
        _tiles[key] = e;
        return e;
    }

    /// <summary>Marks tiles touching <paramref name="pageRect"/> on a page stale (all zoom levels).</summary>
    public void Invalidate(long pageId, RectF pageRect)
    {
        if (pageRect.IsEmpty) return;
        foreach (var (key, entry) in _tiles)
        {
            if (key.PageId != pageId || entry.Dirty) continue;
            if (TileMath.TileRect(key.X, key.Y, TileMath.PixelsPerUnit(key.ZoomKey)).Intersects(pageRect)) entry.Dirty = true;
        }
    }

    /// <summary>Queues a newly added stroke onto the tiles it touches (no re-rasterization needed).</summary>
    public void AppendStroke(long pageId, Stroke s)
    {
        var b = s.Bounds;
        foreach (var (key, entry) in _tiles)
        {
            if (key.PageId != pageId || entry.Dirty) continue;
            if (!TileMath.TileRect(key.X, key.Y, TileMath.PixelsPerUnit(key.ZoomKey)).Intersects(b)) continue;
            (entry.PendingAdds ??= new List<Stroke>(4)).Add(s);
            if (entry.PendingAdds.Count > 64) { entry.Dirty = true; entry.PendingAdds = null; }
        }
    }

    public void RemovePage(long pageId)
    {
        _scratch.Clear();
        foreach (var key in _tiles.Keys) if (key.PageId == pageId) _scratch.Add(key);
        foreach (var k in _scratch) Release(k);
    }

    /// <summary>Drops tiles of other zoom levels for a page once the new level is complete (keeps memory flat while zooming).</summary>
    public void TrimZoomLevels(long pageId, int keepKey, int keepCoarseKey)
    {
        _scratch.Clear();
        foreach (var (key, e) in _tiles)
            if (key.PageId == pageId && key.ZoomKey != keepKey && key.ZoomKey != keepCoarseKey && Frame - e.LastUsedFrame > 30)
                _scratch.Add(key);
        foreach (var k in _scratch) Release(k);
    }

    private void EvictOldest(int count)
    {
        for (int n = 0; n < count && _tiles.Count > 0; n++)
        {
            TileKey oldest = default;
            long best = long.MaxValue;
            foreach (var (k, e) in _tiles)
                if (e.LastUsedFrame < best) { best = e.LastUsedFrame; oldest = k; }
            Release(oldest);
        }
    }

    private void Release(TileKey key)
    {
        if (!_tiles.Remove(key, out var e)) return;
        if (_pool.Count < 16) _pool.Push(e.Target); else e.Target.Dispose();
    }

    public void Clear()
    {
        foreach (var e in _tiles.Values) e.Target.Dispose();
        _tiles.Clear();
        while (_pool.Count > 0) _pool.Pop().Dispose();
    }

    public void ResetDevice(CanvasDevice device)
    {
        Clear();
        _device = device;
    }

    public void Dispose() => Clear();
}
