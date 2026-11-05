using System;
using System.Collections.Generic;
using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.UI;
using Nib.Core.Geometry;
using Nib.Core.Ink;
using Nib.Core.Rendering;
using Windows.Foundation;
using Windows.UI;

namespace Nib.App.Ink;

internal static class Colors2D
{
    public static Color From(uint argb) => ColorHelper.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
}

/// <summary>Device-ready geometry of one committed stroke (one fill per run). Stored in <see cref="Stroke.RenderCache"/>.</summary>
internal sealed class StrokeVisual : IDisposable
{
    public readonly CanvasGeometry[] Geometries;
    public readonly StrokeRun[] Runs;

    public StrokeVisual(CanvasGeometry[] geometries, StrokeRun[] runs)
    {
        Geometries = geometries;
        Runs = runs;
    }

    public void Dispose()
    {
        foreach (var g in Geometries) g.Dispose();
    }
}

/// <summary>
/// Turns stroke geometry into Win2D path geometry: all figures in a run share one path with the
/// non-zero fill rule, which unions them exactly (see <see cref="StrokeGeometryBuilder"/>).
/// Circles become two arcs, so they stay perfectly round at any zoom.
/// </summary>
internal sealed class Win2DRunSink : IStrokeRunSink, IPathSink
{
    private readonly ICanvasResourceCreator _creator;
    private CanvasPathBuilder? _builder;
    private StrokeRun _run;
    private readonly List<CanvasGeometry> _geometries = new(2);
    private readonly List<StrokeRun> _runs = new(2);

    public Win2DRunSink(ICanvasResourceCreator creator) => _creator = creator;

    /// <summary>When set, each finished run is drawn immediately instead of collected (active stroke).</summary>
    public Action<CanvasGeometry, StrokeRun>? Immediate { get; set; }

    public IPathSink BeginRun(in StrokeRun run)
    {
        _run = run;
        _builder = new CanvasPathBuilder(_creator);
        _builder.SetFilledRegionDetermination(CanvasFilledRegionDetermination.Winding);
        return this;
    }

    public void EndRun()
    {
        if (_builder is null) return;
        var geometry = CanvasGeometry.CreatePath(_builder);
        _builder.Dispose();
        _builder = null;
        if (Immediate is { } draw)
        {
            draw(geometry, _run);
            geometry.Dispose();
        }
        else
        {
            _geometries.Add(geometry);
            _runs.Add(_run);
        }
    }

    public void BeginFigure(float x, float y) => _builder!.BeginFigure(new Vector2(x, y), CanvasFigureFill.Default);
    public void LineTo(float x, float y) => _builder!.AddLine(new Vector2(x, y));
    public void EndFigure() => _builder!.EndFigure(CanvasFigureLoop.Closed);

    public void AddCircle(float cx, float cy, float r)
    {
        // Increasing angle in y-down coordinates is clockwise on screen: same orientation as the polygons.
        var b = _builder!;
        b.BeginFigure(new Vector2(cx + r, cy), CanvasFigureFill.Default);
        b.AddArc(new Vector2(cx - r, cy), r, r, 0, CanvasSweepDirection.Clockwise, CanvasArcSize.Small);
        b.AddArc(new Vector2(cx + r, cy), r, r, 0, CanvasSweepDirection.Clockwise, CanvasArcSize.Small);
        b.EndFigure(CanvasFigureLoop.Closed);
    }

    public StrokeVisual TakeVisual()
    {
        var v = new StrokeVisual(_geometries.ToArray(), _runs.ToArray());
        _geometries.Clear();
        _runs.Clear();
        return v;
    }
}

/// <summary>Draws paper patterns into a drawing session whose transform maps page units to pixels.</summary>
internal sealed class Win2DBackgroundSink : IBackgroundSink
{
    public CanvasDrawingSession? Session;
    /// <summary>Minimum line width in page units so rules stay visible when zoomed out (≈ 0.7 px).</summary>
    public float MinLineWidth = 0.75f;
    public Func<uint, uint> MapColor = static c => c;

    public void FillRect(RectF r, uint color) =>
        Session!.FillRectangle(new Rect(r.Left, r.Top, r.Width, r.Height), Colors2D.From(MapColor(color)));

    public void Line(float x0, float y0, float x1, float y1, float width, uint color) =>
        Session!.DrawLine(x0, y0, x1, y1, Colors2D.From(MapColor(color)), MathF.Max(width, MinLineWidth));

    public void Dot(float cx, float cy, float radius, uint color) =>
        Session!.FillCircle(cx, cy, MathF.Max(radius, MinLineWidth * 0.6f), Colors2D.From(MapColor(color)));
}

/// <summary>Tag glyphs and other small vector shapes for Win2D.</summary>
internal sealed class Win2DShapeCanvas : IShapeCanvas
{
    private static CanvasStrokeStyle? s_round;
    public CanvasDrawingSession? Session;
    public Func<uint, uint> MapColor = static c => c;

    private static CanvasStrokeStyle Round => s_round ??= new CanvasStrokeStyle
    {
        StartCap = CanvasCapStyle.Round,
        EndCap = CanvasCapStyle.Round,
        LineJoin = CanvasLineJoin.Round,
    };

    public void FillPolygon(ReadOnlySpan<Vector2> points, uint color)
    {
        using var g = CanvasGeometry.CreatePolygon(Session!, points.ToArray());
        Session!.FillGeometry(g, Colors2D.From(MapColor(color)));
    }

    public void FillCircle(float cx, float cy, float r, uint color) => Session!.FillCircle(cx, cy, r, Colors2D.From(MapColor(color)));

    public void StrokePolyline(ReadOnlySpan<Vector2> points, bool closed, float width, uint color)
    {
        var c = Colors2D.From(MapColor(color));
        for (int i = 1; i < points.Length; i++)
            Session!.DrawLine(points[i - 1], points[i], c, width, Round);
        if (closed && points.Length > 2)
            Session!.DrawLine(points[^1], points[0], c, width, Round);
    }
}
