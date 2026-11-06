using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Nib.Core.Diagnostics;
using Nib.Core.Geometry;
using Nib.Core.Input;
using Nib.Core.Ink;
using Nib.Core.Model;
using Nib.Core.Rendering;
using Windows.Foundation;
using Windows.UI;

namespace Nib.App.Ink;

/// <summary>Everything about the stroke currently being written that the renderer needs.</summary>
internal sealed class WetInk
{
    public PenKind Kind;
    public uint Color;
    public float Width;
    public PenDynamics Dynamics = PenDynamics.Default;
    public StrokeFlags Flags;
    public int Drawn;
}

/// <summary>
/// GPU renderer for the page on the ink thread. A frame is: cached tiles (paper + committed ink + text),
/// the selection being dragged, the active ("wet") stroke drawn incrementally into its own layer, the
/// predicted tail, and overlays (lasso, handles, cursors). Committed ink is never redrawn per frame,
/// so a page with 10,000+ strokes pans as fast as an empty one.
/// </summary>
internal sealed class PageRenderer : IPageObserver, IDisposable
{
    private const float FineBudgetMs = 4f;
    private const float PageSwitchBudgetMs = 60f;
    private const int CoarseKeyDelta = 64; // 4× lower resolution fallback tiles

    private CanvasDevice _device;
    private CanvasSwapChain? _swap;
    private readonly TileCache _tiles;
    private readonly StrokeGeometryBuilder _builder = new();
    private readonly StrokeGeometryBuilder _wetBuilder = new();
    private readonly Win2DRunSink _wetSink;
    private readonly Win2DBackgroundSink _bg = new();
    private readonly Win2DShapeCanvas _shapes = new();
    private readonly List<Stroke> _query = new(512);
    private readonly List<TextContainer> _textQuery = new(32);
    private readonly HashSet<long> _hidden = new();
    private static readonly Comparison<Stroke> ByZ = static (a, b) => a.Z.CompareTo(b.Z);
    private static readonly Comparison<TextContainer> TextByZ = static (a, b) => a.Z.CompareTo(b.Z);
    private CanvasRenderTarget? _wet;
    private CanvasDrawingSession? _wetSession;
    private bool _wetClearWhite;
    private CanvasStrokeStyle? _dash, _round;
    private Page? _page;
    private float _w = 1, _h = 1, _scale = 1;
    private int _settledKey = int.MinValue;
    private bool _forceComplete = true;
    private long _lastFrameTicks;

    public ViewState View;
    public bool ZoomGestureActive;
    public bool DarkMode { get; private set; }
    public long EditingTextId { get; set; }
    public RichText? EditingDraft { get; set; }
    public IReadOnlyDictionary<int, TagDefinition> Tags { get; set; } = new Dictionary<int, TagDefinition>();

    // Overlays set by the engine.
    public WetInk? Wet;
    public Func<ReadOnlySpan<InkPoint>>? WetPoints;
    public readonly List<Vector2> Predicted = new(16);
    public readonly List<Vector2> Lasso = new(512);
    public RectF SelectionBounds = RectF.Empty;
    public Matrix3x2 SelectionTransform = Matrix3x2.Identity;
    public IReadOnlyList<Stroke>? SelectionStrokes;
    public Vector2? Hover;
    /// <summary>User-chosen pointer look. Written and read on the ink thread.</summary>
    public PointerSettings Pointer = new();
    public Footprint HoverFootprint;
    public bool HoverEraser;
    public float EraserRadiusDips = 7;
    public uint HoverColor;
    public float? InsertSpaceAt;
    public float InsertSpaceDelta;
    public bool InsertSpaceVertical = true;

    public CanvasDevice Device => _device;
    public Page? Page => _page;
    public float Width => _w;
    public float Height => _h;
    public float Scale => _scale;

    public PageRenderer(CanvasDevice device)
    {        _device = device;
        _tiles = new TileCache(device);
        _wetSink = new Win2DRunSink(device) { Immediate = DrawWetRun };
    }

    private uint Map(uint c) => DarkMode ? ColorUtil.ToDarkMode(c, TrueBlack) : c;
    private Color DeskColor => DarkMode ? Colors2D.From(0xFF1B1B1D) : Colors2D.From(0xFFE9E9EC);

    // ------------------------------------------------------------------ setup

    public void AttachSwapChain(CanvasSwapChain swap, float w, float h, float scale)
    {
        _swap = swap;
        _w = w; _h = h; _scale = scale;
        UpdateBudget();
        _forceComplete = true;
    }

    public void Resize(float w, float h, float scale)
    {
        if (_swap is null) return;
        if (w == _w && h == _h && scale == _scale) return;
        _w = MathF.Max(w, 1); _h = MathF.Max(h, 1); _scale = scale;
        _swap.ResizeBuffers(_w, _h, 96f * _scale);
        ResetWet();
        UpdateBudget();
        _forceComplete = true; // resize must never show blank areas
    }

    private void UpdateBudget()
    {
        int across = (int)MathF.Ceiling(_w * _scale / TileMath.TileSize) + 1;
        int down = (int)MathF.Ceiling(_h * _scale / TileMath.TileSize) + 1;
        _tiles.Budget = Math.Clamp(across * down * 3 + 8, 48, 160);
    }

    public void SetPage(Page page)
    {
        if (ReferenceEquals(page, _page)) return;
        _page?.RemoveObserver(this);
        _page = page;
        page.AddObserver(this);
        _hidden.Clear();
        _settledKey = int.MinValue;
        _forceComplete = true;
        ResetWet();
    }

    /// <summary>Dark pages with pure black paper (OLED: those pixels switch off).</summary>
    public bool TrueBlack { get; private set; }

    public void SetDarkMode(bool dark, bool trueBlack)
    {
        if (dark == DarkMode && trueBlack == TrueBlack) return;
        DarkMode = dark;
        TrueBlack = trueBlack;
        _tiles.Clear();
        if (_page is not null)
            foreach (var t in _page.Texts) { (t.RenderCache as IDisposable)?.Dispose(); t.RenderCache = null; }
        _forceComplete = true;
    }

    public void ResetDevice(CanvasDevice device)
    {
        _device = device;
        _tiles.ResetDevice(device);
        _wetSession?.Dispose(); _wetSession = null;
        _wet?.Dispose(); _wet = null;
        _swap = null;
        _forceComplete = true;
    }

    public void Hide(IEnumerable<Stroke> strokes)
    {
        if (_page is null) return;
        foreach (var s in strokes)
            if (_hidden.Add(s.Id)) _tiles.Invalidate(_page.Id, s.Bounds);
    }

    public void UnhideAll()
    {
        if (_page is null) return;
        foreach (var id in _hidden)
            if (_page.FindStroke(id) is { } s) _tiles.Invalidate(_page.Id, s.Bounds);
        _hidden.Clear();
    }

    public void InvalidateText(TextContainer? t)
    {
        if (t is null || _page is null) return;
        _tiles.Invalidate(_page.Id, t.Bounds.Inflate(4));
        if (t.RenderCache is TextVisual v) _tiles.Invalidate(_page.Id, RectF.FromXYWH(t.X - TextContainer.TagGutter, t.Y, t.Width + TextContainer.TagGutter, v.Height).Inflate(4));
    }

    // ------------------------------------------------------------------ IPageObserver

    public void OnStrokesChanged(Page page, IReadOnlyList<Stroke> removed, IReadOnlyList<Stroke> added)
    {
        if (removed.Count == 0)
        {
            foreach (var s in added) _tiles.AppendStroke(page.Id, s);
            return;
        }
        if (page.DirtyHint is { } hint)
        {
            // An eraser sweep changed only the area around the eraser; the rest of the old strokes looks the same.
            _tiles.Invalidate(page.Id, hint);
            return;
        }
        for (int i = 0; i < removed.Count; i++) _tiles.Invalidate(page.Id, removed[i].Bounds);
        for (int i = 0; i < added.Count; i++) _tiles.Invalidate(page.Id, added[i].Bounds);
    }

    public void OnTextChanged(Page page, TextContainer? oldText, TextContainer? newText)
    {
        InvalidateText(oldText);
        InvalidateText(newText);
    }

    public void OnLayoutChanged(Page page, PageLayout oldLayout)
    {
        if (oldLayout with { SheetCount = page.Layout.SheetCount } == page.Layout && page.Layout.Mode == PageMode.Fixed)
        {
            // Only sheets were added: repaint just the area below the old last sheet.
            _tiles.Invalidate(page.Id, new RectF(-1e6f, oldLayout.Bounds.Bottom - 2, 1e6f, 1e7f));
            return;
        }
        _tiles.RemovePage(page.Id);
        _forceComplete = true;
    }

    public void OnTitleChanged(Page page) { }

    // ------------------------------------------------------------------ visuals

    private StrokeVisual GetVisual(Stroke s)
    {
        if (s.RenderCache is StrokeVisual v) return v;
        var sink = new Win2DRunSink(_device);
        _builder.Build(s, sink);
        v = sink.TakeVisual();
        s.RenderCache = v;
        return v;
    }

    /// <summary>Builds stroke geometry ahead of time (called on a background thread for preloaded pages).</summary>
    public static void Prepare(CanvasDevice device, Page page, StrokeGeometryBuilder builder)
    {
        foreach (var s in page.Strokes)
        {
            if (s.RenderCache is not null) continue;
            var sink = new Win2DRunSink(device);
            builder.Build(s, sink);
            s.RenderCache = sink.TakeVisual();
        }
    }

    public TextVisual GetTextVisual(TextContainer t)
    {
        bool editing = t.Id == EditingTextId && EditingDraft is not null;
        if (!editing && t.RenderCache is TextVisual v) return v;
        var visual = new TextVisual(_device, t, editing ? EditingDraft : null);
        if (!editing)
        {
            t.RenderCache = visual;
            _page?.UpdateMeasuredHeight(t.Id, visual.Height);
        }
        return visual;
    }

    private void DrawStroke(CanvasDrawingSession ds, Stroke s)
    {
        var v = GetVisual(s);
        for (int i = 0; i < v.Geometries.Length; i++) FillRun(ds, v.Geometries[i], v.Runs[i]);
    }

    private void FillRun(CanvasDrawingSession ds, CanvasGeometry g, in StrokeRun run)
    {
        if (run.Multiply)
        {
            if (!DarkMode)
            {
                // Darken blend: translucent ink never stacks, and dark ink below stays dark.
                ds.Blend = CanvasBlend.Min;
                ds.FillGeometry(g, Colors2D.From(run.FillColor));
                ds.Blend = CanvasBlend.SourceOver;
            }
            else
            {
                uint c = ColorUtil.ToDarkMode(run.BaseColor, TrueBlack);
                ds.FillGeometry(g, Colors2D.From((c & 0x00FFFFFF) | ((uint)(run.Strength * 200) << 24)));
            }
        }
        else
        {
            ds.FillGeometry(g, Colors2D.From(Map(run.FillColor)));
        }
    }

    // ------------------------------------------------------------------ tiles

    private void RasterTile(TileKey key, TileEntry entry)
    {
        var page = _page!;
        float ppu = TileMath.PixelsPerUnit(key.ZoomKey);
        var rect = TileMath.TileRect(key.X, key.Y, ppu);
        using var ds = entry.Target.CreateDrawingSession();
        // A patch (an eraser sweep) repaints only its own pixels: clip to them and look up only what touches them.
        RectF? patch = entry.Dirty ? entry.DirtyRect : null;
        Microsoft.Graphics.Canvas.CanvasActiveLayer? clip = null;
        if (patch is { } pr)
        {
            float ox = key.X * (float)TileMath.TileSize, oy = key.Y * (float)TileMath.TileSize;
            float x0 = MathF.Max(0, MathF.Floor(pr.Left * ppu - ox) - 1), y0 = MathF.Max(0, MathF.Floor(pr.Top * ppu - oy) - 1);
            float x1 = MathF.Min(TileMath.TileSize, MathF.Ceiling(pr.Right * ppu - ox) + 1), y1 = MathF.Min(TileMath.TileSize, MathF.Ceiling(pr.Bottom * ppu - oy) + 1);
            if (x1 <= x0 || y1 <= y0) { entry.Dirty = false; entry.DirtyRect = null; return; }
            clip = ds.CreateLayer(1f, new Windows.Foundation.Rect(x0, y0, x1 - x0, y1 - y0));
            rect = RectF.FromXYWH((x0 + ox) / ppu, (y0 + oy) / ppu, (x1 - x0) / ppu, (y1 - y0) / ppu);
        }
        ds.Clear(DeskColor);
        ds.Transform = Matrix3x2.CreateScale(ppu) * Matrix3x2.CreateTranslation(-key.X * TileMath.TileSize, -key.Y * TileMath.TileSize);

        var layout = page.Layout;
        if (layout.Mode == PageMode.Fixed)
        {
            // Soft sheet shadow, then paper.
            var b = layout.Bounds;
            if (b.Inflate(6).Intersects(rect))
                ds.FillRectangle(b.Left + 1.5f, b.Top + 2.5f, b.Width, b.Height, DarkMode ? Colors2D.From(0xFF0E0E10) : Colors2D.From(0xFFD0D0D6));
        }
        _bg.Session = ds;
        _bg.MinLineWidth = 0.7f / ppu;
        _bg.MapColor = Map;
        BackgroundPattern.Emit(layout, rect, ppu / _scale, _bg);
        if (layout.Mode == PageMode.Fixed && layout.SheetCount > 1)
        {
            using var dash = new CanvasStrokeStyle { DashStyle = CanvasDashStyle.Dash };
            for (int i = 1; i < layout.SheetCount; i++)
            {
                float y = i * layout.SheetHeight;
                if (y >= rect.Top && y <= rect.Bottom)
                    ds.DrawLine(0, y, layout.SheetWidth, y, DarkMode ? Colors2D.From(0xFF3A3A40) : Colors2D.From(0xFFC4C4CC), 1.2f / ppu, dash);
            }
        }

        _query.Clear();
        page.StrokeIndex.Query(rect, _query);
        _query.Sort(ByZ);
        foreach (var s in _query)
            if (s.Kind.IsBackgroundInk() && !_hidden.Contains(s.Id)) DrawStroke(ds, s);

        _textQuery.Clear();
        page.TextIndex.Query(rect.Inflate(TextContainer.TagGutter), _textQuery);
        _textQuery.Sort(TextByZ);
        foreach (var t in _textQuery)
        {
            bool editing = t.Id == EditingTextId;
            var tv = GetTextVisual(t);
            tv.Draw(ds, Map, _shapes, Tags, drawText: !editing);
            if (!ReferenceEquals(t.RenderCache, tv) && !ReferenceEquals(page.FindText(t.Id)?.RenderCache, tv)) tv.Dispose();
        }

        foreach (var s in _query)
            if (!s.Kind.IsBackgroundInk() && !_hidden.Contains(s.Id)) DrawStroke(ds, s);

        clip?.Dispose();
        entry.Dirty = false;
        entry.DirtyRect = null;
        entry.PendingAdds = null;
    }

    private void ApplyPendingAdds(TileKey key, TileEntry entry)
    {
        if (entry.PendingAdds is null) return;
        float ppu = TileMath.PixelsPerUnit(key.ZoomKey);
        using var ds = entry.Target.CreateDrawingSession();
        ds.Transform = Matrix3x2.CreateScale(ppu) * Matrix3x2.CreateTranslation(-key.X * TileMath.TileSize, -key.Y * TileMath.TileSize);
        foreach (var s in entry.PendingAdds)
            if (!_hidden.Contains(s.Id)) DrawStroke(ds, s);
        entry.PendingAdds = null;
    }

    /// <returns>True if some tiles are still low-resolution and another frame should follow.</returns>
    private bool DrawTiles(CanvasDrawingSession ds, ViewState view, long startTicks)
    {
        var page = _page!;
        int currentKey = TileMath.ZoomKey(view.Zoom * _scale);
        if (_settledKey == int.MinValue || !ZoomGestureActive || Math.Abs(currentKey - _settledKey) > 32) _settledKey = currentKey;
        int key = _settledKey;
        int coarse = key - CoarseKeyDelta;
        float ppu = TileMath.PixelsPerUnit(key);

        var visible = view.VisiblePageRect(_w, _h);
        if (page.Layout.Mode == PageMode.Fixed) visible = visible.Intersect(page.Layout.Bounds.Inflate(8));
        if (visible.IsEmpty) return false;

        TileMath.TileRange(visible, ppu, out int tx0, out int ty0, out int tx1, out int ty1);
        float budget = _forceComplete ? PageSwitchBudgetMs : FineBudgetMs;
        bool pending = false;
        ds.Antialiasing = CanvasAntialiasing.Aliased; // tile edges meet exactly: no seams

        for (int ty = ty0; ty <= ty1; ty++)
        for (int tx = tx0; tx <= tx1; tx++)
        {
            var tk = new TileKey(page.Id, key, tx, ty);
            bool have = _tiles.TryGet(tk, out var entry);
            if (have && entry.Dirty)
            {
                // Edits on screen are always shown in the same frame.
                RasterTile(tk, entry);
            }
            else if (!have)
            {
                if (ElapsedMs(startTicks) < budget)
                {
                    entry = _tiles.Add(tk);
                    RasterTile(tk, entry);
                    have = true;
                }
                else
                {
                    DrawCoarse(ds, view, page, coarse, TileMath.TileRect(tx, ty, ppu));
                    pending = true;
                    continue;
                }
            }
            ApplyPendingAdds(tk, entry);
            DrawTileImage(ds, view, entry.Target, TileMath.TileRect(tx, ty, ppu));
        }
        ds.Antialiasing = CanvasAntialiasing.Antialiased;
        if (!pending && !ZoomGestureActive) _tiles.TrimZoomLevels(page.Id, key, coarse);
        _forceComplete = false;
        return pending;
    }

    /// <summary>Low-resolution stand-in so newly revealed areas are never blank while sharp tiles render.</summary>
    private void DrawCoarse(CanvasDrawingSession ds, ViewState view, Page page, int coarseKey, RectF region)
    {
        float cppu = TileMath.PixelsPerUnit(coarseKey);
        TileMath.TileRange(region, cppu, out int x0, out int y0, out int x1, out int y1);
        for (int y = y0; y <= y1; y++)
        for (int x = x0; x <= x1; x++)
        {
            var ck = new TileKey(page.Id, coarseKey, x, y);
            if (!_tiles.TryGet(ck, out var e))
            {
                e = _tiles.Add(ck);
                RasterTile(ck, e);
            }
            else if (e.Dirty) RasterTile(ck, e);
            ApplyPendingAdds(ck, e);
            var tileRect = TileMath.TileRect(x, y, cppu);
            var part = tileRect.Intersect(region);
            if (part.IsEmpty) continue;
            var src = new Rect((part.Left - tileRect.Left) * cppu, (part.Top - tileRect.Top) * cppu, part.Width * cppu, part.Height * cppu);
            ds.DrawImage(e.Target, ScreenRect(view, part), src);
        }
    }

    private void DrawTileImage(CanvasDrawingSession ds, ViewState view, CanvasRenderTarget rt, RectF pageRect) =>
        ds.DrawImage(rt, ScreenRect(view, pageRect), new Rect(0, 0, TileMath.TileSize, TileMath.TileSize));

    /// <summary>Page rect → DIP rect with edges snapped to device pixels, so neighbouring tiles share exact edges.</summary>
    private Rect ScreenRect(ViewState v, RectF r)
    {
        double s = _scale;
        double l = Math.Round((r.Left * v.Zoom + v.OffsetX) * s) / s;
        double t = Math.Round((r.Top * v.Zoom + v.OffsetY) * s) / s;
        double rr = Math.Round((r.Right * v.Zoom + v.OffsetX) * s) / s;
        double b = Math.Round((r.Bottom * v.Zoom + v.OffsetY) * s) / s;
        return new Rect(l, t, Math.Max(0, rr - l), Math.Max(0, b - t));
    }

    // ------------------------------------------------------------------ wet ink

    public void BeginWet(WetInk wet)
    {
        Wet = wet;
        wet.Drawn = 0;
        EnsureWetTarget();
        _wetClearWhite = wet.Kind.IsMultiplyInk() && !DarkMode;
        using (var ds = _wet!.CreateDrawingSession()) ds.Clear(_wetClearWhite ? Colors2D.From(0xFFFFFFFF) : Colors2D.From(0x00000000));
    }

    public void EndWet()
    {
        Wet = null;
        Predicted.Clear();
    }

    /// <summary>Called when the view changes mid-stroke: the wet layer is in screen space, so redraw it.</summary>
    public void ResetWet()
    {
        if (Wet is { } w) BeginWet(w);
        else
        {
            _wetSession?.Dispose();
            _wetSession = null;
            if (_wet is not null && (Math.Abs(_wet.Size.Width - _w) > 0.5 || Math.Abs(_wet.Size.Height - _h) > 0.5 || _wet.Dpi != 96f * _scale))
            {
                _wet.Dispose();
                _wet = null;
            }
        }
    }

    private void EnsureWetTarget()
    {
        if (_wet is not null && Math.Abs(_wet.Size.Width - _w) < 0.5 && Math.Abs(_wet.Size.Height - _h) < 0.5 && _wet.Dpi == 96f * _scale) return;
        _wet?.Dispose();
        _wet = new CanvasRenderTarget(_device, _w, _h, 96f * _scale);
    }

    private void DrawWetRun(CanvasGeometry g, StrokeRun run)
    {
        var ds = _wetSession!;
        if (run.Multiply && !DarkMode)
        {
            ds.Blend = CanvasBlend.Min;
            ds.FillGeometry(g, Colors2D.From(run.FillColor));
            ds.Blend = CanvasBlend.SourceOver;
        }
        else FillRun(ds, g, run);
    }

    private void DrawWet(CanvasDrawingSession ds, ViewState view)
    {
        if (Wet is not { } wet || WetPoints is null || _wet is null) return;
        var pts = WetPoints();
        if (pts.Length > wet.Drawn)
        {
            int start = Math.Max(0, wet.Drawn - 1);
            using (_wetSession = _wet.CreateDrawingSession())
            {
                _wetSession.Transform = view.PageToScreen;
                _wetBuilder.Build(wet.Kind, wet.Color, wet.Width, wet.Dynamics, wet.Flags, pts[start..], _wetSink);
            }
            _wetSession = null;
            wet.Drawn = pts.Length;
        }
        ds.Blend = _wetClearWhite ? CanvasBlend.Min : CanvasBlend.SourceOver;
        ds.DrawImage(_wet, 0, 0);
        ds.Blend = CanvasBlend.SourceOver;

        // Predicted tail: drawn fresh every frame, never committed.
        if (Predicted.Count > 1)
        {
            float width = MathF.Max(wet.Width * 0.5f * view.Zoom, 0.8f);
            var color = wet.Kind.IsMultiplyInk() && !DarkMode ? Colors2D.From(ColorUtil.OverWhite(wet.Color, 0.6f)) : Colors2D.From(Map(wet.Color));
            ds.Blend = _wetClearWhite ? CanvasBlend.Min : CanvasBlend.SourceOver;
            _round ??= new CanvasStrokeStyle { StartCap = CanvasCapStyle.Round, EndCap = CanvasCapStyle.Round, LineJoin = CanvasLineJoin.Round };
            for (int i = 1; i < Predicted.Count; i++)
                ds.DrawLine(view.ToScreen(Predicted[i - 1].X, Predicted[i - 1].Y), view.ToScreen(Predicted[i].X, Predicted[i].Y), color, width, _round);
            ds.Blend = CanvasBlend.SourceOver;
        }
    }

    // ------------------------------------------------------------------ overlays

    private void DrawOverlays(CanvasDrawingSession ds, ViewState view)
    {
        _dash ??= new CanvasStrokeStyle { DashStyle = CanvasDashStyle.Dash, StartCap = CanvasCapStyle.Round, EndCap = CanvasCapStyle.Round };
        var accent = Colors2D.From(0xFF2B6CB0);

        // Selection being dragged: drawn live with its transform (hidden in the tiles meanwhile).
        if (SelectionStrokes is { Count: > 0 } sel && _hidden.Count > 0)
        {
            var old = ds.Transform;
            ds.Transform = SelectionTransform * view.PageToScreen;
            foreach (var s in sel) DrawStroke(ds, s);
            ds.Transform = old;
        }

        if (!SelectionBounds.IsEmpty)
        {
            var b = SelectionBounds.Transform(SelectionTransform);
            var tl = view.ToScreen(b.Left, b.Top);
            var br = view.ToScreen(b.Right, b.Bottom);
            var r = new Rect(tl.X - 6, tl.Y - 6, br.X - tl.X + 12, br.Y - tl.Y + 12);
            ds.DrawRectangle(r, accent, 1.25f, _dash);
            foreach (var p in SelectionHandles(r))
            {
                ds.FillCircle(p, 6, Colors2D.From(0xFFFFFFFF));
                ds.DrawCircle(p, 6, accent, 1.5f);
            }
            var top = new Vector2((float)(r.X + r.Width / 2), (float)r.Y);
            ds.DrawLine(top, top - new Vector2(0, 22), accent, 1.25f);
        }

        if (Lasso.Count > 1)
        {
            for (int i = 1; i < Lasso.Count; i++)
                ds.DrawLine(view.ToScreen(Lasso[i - 1].X, Lasso[i - 1].Y), view.ToScreen(Lasso[i].X, Lasso[i].Y), accent, 1.5f, _dash);
        }

        if (InsertSpaceAt is { } at)
        {
            if (InsertSpaceVertical)
            {
                float y = at * view.Zoom + view.OffsetY;
                float dy = InsertSpaceDelta * view.Zoom;
                if (MathF.Abs(dy) > 0.5f)
                    ds.FillRectangle(0, MathF.Min(y, y + dy), _w, MathF.Abs(dy), Colors2D.From(0x332B6CB0));
                ds.DrawLine(0, y, _w, y, accent, 1.5f, _dash);
            }
            else
            {
                float x = at * view.Zoom + view.OffsetX;
                float dx = InsertSpaceDelta * view.Zoom;
                if (MathF.Abs(dx) > 0.5f)
                    ds.FillRectangle(MathF.Min(x, x + dx), 0, MathF.Abs(dx), _h, Colors2D.From(0x332B6CB0));
                ds.DrawLine(x, 0, x, _h, accent, 1.5f, _dash);
            }
        }

        if (Hover is { } hp)
        {
            var c = view.ToScreen(hp.X, hp.Y);
            if (HoverEraser)
            {
                ds.DrawCircle(c, EraserRadiusDips, Colors2D.From(0xFF707070), 1f);
            }
            else DrawPointer(ds, c, view.Zoom);
        }
    }

    /// <summary>The hover pointer, styled by the user's pointer settings (style, size, color, opacity).</summary>
    private void DrawPointer(CanvasDrawingSession ds, Vector2 c, float zoom)
    {
        var ps = Pointer;
        if (ps.Style == PointerStyle.None) return;
        var col = Colors2D.From(ps.ColorMode == PointerColorMode.Custom ? ps.CustomColor : Map(HoverColor));
        col.A = (byte)Math.Clamp(col.A * ps.Opacity, 0f, 255f);
        float lw = ps.LineWidth;
        var f = HoverFootprint;
        bool fixedSize = ps.SizeMode == PointerSizeMode.Fixed;
        float rx = fixedSize ? ps.FixedSize * 0.5f : MathF.Max(f.Rx * zoom, 1.5f);
        float ry = fixedSize ? rx : MathF.Max(f.Ry * zoom, 1.5f);
        float r = MathF.Max(rx, ry);
        switch (ps.Style)
        {
            case PointerStyle.Dot:
                ds.FillCircle(c, MathF.Max(r, 2f), col);
                break;
            case PointerStyle.Ring:
                ds.DrawCircle(c, MathF.Max(r, 2f), col, lw);
                break;
            case PointerStyle.Crosshair:
            {
                float arm = MathF.Max(r, 5f) + 3f, gap = 2f;
                ds.DrawLine(c.X - arm, c.Y, c.X - gap, c.Y, col, lw);
                ds.DrawLine(c.X + gap, c.Y, c.X + arm, c.Y, col, lw);
                ds.DrawLine(c.X, c.Y - arm, c.X, c.Y - gap, col, lw);
                ds.DrawLine(c.X, c.Y + gap, c.X, c.Y + arm, col, lw);
                break;
            }
            default: // Nib: the footprint at the current tilt, or a circle when a fixed size is chosen
                if (fixedSize) ds.DrawCircle(c, rx, col, lw);
                else
                {
                    var old = ds.Transform;
                    ds.Transform = Matrix3x2.CreateRotation(f.Angle) * Matrix3x2.CreateTranslation(c);
                    if (f.Shape == FootprintShape.Rect) ds.DrawRectangle(-rx, -ry, 2 * rx, 2 * ry, col, lw);
                    else ds.DrawEllipse(0, 0, rx, ry, col, lw);
                    ds.Transform = old;
                }
                break;
        }
        if (ps.CenterDot) ds.FillCircle(c, 1.5f, col);
    }

    public static Vector2[] SelectionHandles(Rect r) => new[]
    {
        new Vector2((float)r.X, (float)r.Y),
        new Vector2((float)(r.X + r.Width), (float)r.Y),
        new Vector2((float)(r.X + r.Width), (float)(r.Y + r.Height)),
        new Vector2((float)r.X, (float)(r.Y + r.Height)),
    };

    // ------------------------------------------------------------------ frame

    /// <summary>Renders and presents one frame. Returns true when another frame is needed (progressive tiles).</summary>
    public bool Render()
    {
        if (_swap is null || _page is null) return false;
        long start = Stopwatch.GetTimestamp();
        var view = View;
        // Snap the translation to whole device pixels: tiles stay 1:1 and crisp.
        view.OffsetX = MathF.Round(view.OffsetX * _scale) / _scale;
        view.OffsetY = MathF.Round(view.OffsetY * _scale) / _scale;
        _tiles.Frame++;
        bool pending;
        using (var ds = _swap.CreateDrawingSession(DeskColor))
        {
            pending = DrawTiles(ds, view, start);
            DrawWet(ds, view);
            DrawOverlays(ds, view);
        }
        double renderMs = ElapsedMs(start);
        _swap.Present();

        long now = Stopwatch.GetTimestamp();
        if (_lastFrameTicks != 0)
        {
            double interval = (now - _lastFrameTicks) * 1000.0 / Stopwatch.Frequency;
            var pc = PerfCounters.Instance;
            pc.FrameInterval.Record(interval);
            pc.LastFrameMs = interval;
            if (interval > 1500.0 / pc.RefreshHz && interval < 250) pc.DroppedFrames++;
        }
        PerfCounters.Instance.RenderTime.Record(renderMs);
        NibEventSource.Log.Frame(_lastFrameTicks == 0 ? 0 : (now - _lastFrameTicks) * 1e6 / Stopwatch.Frequency, renderMs * 1000);
        _lastFrameTicks = now;
        return pending;
    }

    /// <summary>Restarts frame-interval accounting after an idle period (idle gaps are not dropped frames).</summary>
    public void MarkIdle() => _lastFrameTicks = 0;

    private static float ElapsedMs(long start) => (float)((Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency);

    public void Dispose()
    {
        _page?.RemoveObserver(this);
        _tiles.Dispose();
        _wet?.Dispose();
    }
}
