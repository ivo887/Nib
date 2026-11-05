using System;
using System.Collections.Generic;
using Nib.Core.Ink;
using Nib.Core.Model;

namespace Nib.Core.Tests;

internal static class TestStrokes
{
    public static InkPoint[] Line(float x0, float y0, float x1, float y1, int n, float pressure = 0.5f, float tiltX = 0, float tiltY = 0)
    {
        var pts = new InkPoint[n];
        for (int i = 0; i < n; i++)
        {
            float t = n == 1 ? 0 : i / (float)(n - 1);
            pts[i] = new InkPoint(x0 + (x1 - x0) * t, y0 + (y1 - y0) * t, pressure, tiltX, tiltY, -1, i * 4);
        }
        return pts;
    }

    public static Stroke Make(InkPoint[] pts, PenKind kind = PenKind.Ballpoint, float width = 2f, StrokeFlags flags = StrokeFlags.HasPressure, PenDynamics? dyn = null, long? id = null, long z = 1, uint color = 0xFF000000) =>
        new(id ?? IdGenerator.Next(), z, kind, color, width, dyn ?? PenDynamics.Default, flags, pts);

    /// <summary>Realistic handwriting: smooth loops, smoothly varying pressure and tilt (as a real pen reports).</summary>
    public static InkPoint[] Handwriting(float x, float y, int n)
    {
        var pts = new InkPoint[n];
        for (int i = 0; i < n; i++)
        {
            float t = i / 60f;
            pts[i] = new InkPoint(x + i * 0.6f + MathF.Cos(t * 9) * 5, y + MathF.Sin(t * 9) * 8,
                0.45f + 0.35f * MathF.Sin(t * 2.3f), 28 + 6 * MathF.Sin(t), 12 + 4 * MathF.Cos(t * 0.7f), -1, i * 4);
        }
        return pts;
    }

    /// <summary>Stress input: a sine wave with random pressure and tilt on every sample.</summary>
    public static InkPoint[] Scribble(float x, float y, int n, float scale = 1f, int seed = 1)
    {
        var rnd = new Random(seed);
        var pts = new InkPoint[n];
        for (int i = 0; i < n; i++)
        {
            float t = i * 0.15f;
            pts[i] = new InkPoint(x + i * 0.8f * scale, y + MathF.Sin(t) * 6f * scale, 0.3f + 0.6f * (float)rnd.NextDouble(),
                (float)(rnd.NextDouble() * 40 - 20), (float)(rnd.NextDouble() * 40 - 20), -1, i * 4);
        }
        return pts;
    }
}

/// <summary>Collects stroke geometry and answers point-coverage queries with the non-zero rule.</summary>
internal sealed class CoverageSink : IStrokeRunSink, IPathSink
{
    private readonly List<List<(float X, float Y)>> _figures = new();
    private readonly List<(float Cx, float Cy, float R)> _circles = new();
    private List<(float X, float Y)>? _cur;
    public int Runs;
    public List<StrokeRun> RunInfo { get; } = new();
    public int FigureCount => _figures.Count + _circles.Count;
    public IReadOnlyList<List<(float X, float Y)>> Polygons => _figures;

    public IPathSink BeginRun(in StrokeRun run) { Runs++; RunInfo.Add(run); return this; }
    public void EndRun() { }
    public void BeginFigure(float x, float y) { _cur = new List<(float, float)> { (x, y) }; }
    public void LineTo(float x, float y) => _cur!.Add((x, y));
    public void EndFigure() { _figures.Add(_cur!); _cur = null; }
    public void AddCircle(float cx, float cy, float r) => _circles.Add((cx, cy, r));

    public static double SignedArea(List<(float X, float Y)> poly)
    {
        double s = 0;
        for (int i = 0; i < poly.Count; i++)
        {
            var a = poly[i];
            var b = poly[(i + 1) % poly.Count];
            s += a.X * (double)b.Y - b.X * (double)a.Y;
        }
        return s / 2;
    }

    /// <summary>Winding number sum of all figures at (x, y); circles count as positively oriented.</summary>
    public int Winding(float x, float y)
    {
        int w = 0;
        foreach (var c in _circles)
            if ((x - c.Cx) * (x - c.Cx) + (y - c.Cy) * (y - c.Cy) <= c.R * c.R) w++;
        foreach (var poly in _figures)
        {
            int wn = 0;
            for (int i = 0; i < poly.Count; i++)
            {
                var a = poly[i];
                var b = poly[(i + 1) % poly.Count];
                if (a.Y <= y)
                {
                    if (b.Y > y && IsLeft(a, b, x, y) > 0) wn++;
                }
                else if (b.Y <= y && IsLeft(a, b, x, y) < 0) wn--;
            }
            // Sunday's algorithm is purely algebraic: a polygon with positive shoelace area yields +1 inside,
            // the same sign the circles contribute. Mixed orientations would cancel and leave holes.
            w += wn;
        }
        return w;
    }

    private static float IsLeft((float X, float Y) a, (float X, float Y) b, float x, float y) =>
        (b.X - a.X) * (y - a.Y) - (x - a.X) * (b.Y - a.Y);

    public bool Covered(float x, float y) => Winding(x, y) != 0;

    /// <summary>Area of the union by grid sampling.</summary>
    public double UnionArea(float x0, float y0, float x1, float y1, float step)
    {
        long hits = 0;
        for (float y = y0 + step / 2; y < y1; y += step)
            for (float x = x0 + step / 2; x < x1; x += step)
                if (Covered(x, y)) hits++;
        return hits * (double)step * step;
    }
}
