using System;
using System.Collections.Generic;
using Nib.Core.Geometry;
using Nib.Core.Ink;
using Nib.Core.Model;

namespace Nib.Core.Editing;

/// <summary>
/// Stroke eraser (removes whole strokes it touches) and pixel eraser (cuts strokes where the eraser
/// passes, interpolating exact cut points). The eraser is swept as a capsule from its previous to its
/// current position, so fast eraser motion never skips over ink. Candidate strokes come from the
/// spatial index, so cost is O(strokes near the eraser).
/// </summary>
public sealed class StrokeEraser
{
    private readonly List<Stroke> _candidates = new(64);
    private readonly List<InkPoint> _piece = new(256);

    /// <summary>Strokes touched by the eraser capsule (a→b, radius r). Results are appended.</summary>
    public void FindTouched(Page page, float ax, float ay, float bx, float by, float radius, List<Stroke> results)
    {
        _candidates.Clear();
        var query = new RectF(MathF.Min(ax, bx), MathF.Min(ay, by), MathF.Max(ax, bx), MathF.Max(ay, by)).Inflate(radius);
        page.StrokeIndex.Query(query, _candidates);
        for (int i = 0; i < _candidates.Count; i++)
        {
            var s = _candidates[i];
            if (Touches(s, ax, ay, bx, by, radius)) results.Add(s);
        }
    }

    /// <summary>True if the stroke's ink (centerline plus local nib radius) intersects the capsule.</summary>
    public static bool Touches(Stroke s, float ax, float ay, float bx, float by, float radius)
    {
        var pts = s.Points;
        float nib = s.Width * 0.5f;
        float reach = radius + nib;
        float reach2 = reach * reach;
        if (pts.Length == 1) return Stroke.SegmentDistanceSquared(pts[0].X, pts[0].Y, ax, ay, bx, by) <= reach2;
        for (int i = 1; i < pts.Length; i++)
        {
            if (SegmentSegmentDistanceSquared(pts[i - 1].X, pts[i - 1].Y, pts[i].X, pts[i].Y, ax, ay, bx, by) <= reach2) return true;
        }
        return false;
    }

    /// <summary>
    /// Pixel eraser: returns the pieces left after cutting away everything within <paramref name="radius"/>
    /// of the capsule, or null if the stroke is untouched. Pieces get fresh ids and keep the original Z.
    /// </summary>
    public List<Stroke>? Cut(Stroke s, float ax, float ay, float bx, float by, float radius)
    {
        var pts = s.Points;
        float r2 = radius * radius;
        bool any = false;
        for (int i = 0; i < pts.Length; i++)
            if (Stroke.SegmentDistanceSquared(pts[i].X, pts[i].Y, ax, ay, bx, by) <= r2) { any = true; break; }
        if (!any)
        {
            // The eraser can still slice through a long segment between two outside points.
            bool crosses = false;
            for (int i = 1; i < pts.Length && !crosses; i++)
                crosses = SegmentSegmentDistanceSquared(pts[i - 1].X, pts[i - 1].Y, pts[i].X, pts[i].Y, ax, ay, bx, by) <= r2;
            if (!crosses) return null;
        }

        var pieces = new List<Stroke>(2);
        _piece.Clear();
        bool prevInside = Inside(pts[0], ax, ay, bx, by, r2);
        if (!prevInside) _piece.Add(pts[0]);
        for (int i = 1; i < pts.Length; i++)
        {
            var p0 = pts[i - 1];
            var p1 = pts[i];
            bool inside = Inside(p1, ax, ay, bx, by, r2);
            if (!prevInside && !inside)
            {
                // Both outside: the segment may still pass through the eraser.
                if (SegmentSegmentDistanceSquared(p0.X, p0.Y, p1.X, p1.Y, ax, ay, bx, by) <= r2)
                {
                    float tIn = FindBoundary(p0, p1, ax, ay, bx, by, r2, entering: true);
                    float tOut = FindBoundary(p0, p1, ax, ay, bx, by, r2, entering: false);
                    _piece.Add(Lerp(p0, p1, tIn));
                    Flush(s, pieces);
                    _piece.Add(Lerp(p0, p1, tOut));
                }
                _piece.Add(p1);
            }
            else if (!prevInside && inside)
            {
                _piece.Add(Lerp(p0, p1, FindBoundary(p0, p1, ax, ay, bx, by, r2, entering: true)));
                Flush(s, pieces);
            }
            else if (prevInside && !inside)
            {
                _piece.Add(Lerp(p0, p1, FindBoundary(p0, p1, ax, ay, bx, by, r2, entering: false)));
                _piece.Add(p1);
            }
            prevInside = inside;
        }
        Flush(s, pieces);
        return pieces;
    }

    private void Flush(Stroke original, List<Stroke> pieces)
    {
        if (_piece.Count >= 2)
        {
            // Drop slivers shorter than a fraction of the nib: they would read as stray dots.
            float len = 0;
            for (int i = 1; i < _piece.Count; i++)
            {
                float dx = _piece[i].X - _piece[i - 1].X, dy = _piece[i].Y - _piece[i - 1].Y;
                len += MathF.Sqrt(dx * dx + dy * dy);
            }
            if (len >= original.Width * 0.25f)
                pieces.Add(original.WithPoints(_piece.ToArray(), IdGenerator.Next()));
        }
        _piece.Clear();
    }

    private static bool Inside(in InkPoint p, float ax, float ay, float bx, float by, float r2) =>
        Stroke.SegmentDistanceSquared(p.X, p.Y, ax, ay, bx, by) <= r2;

    /// <summary>Bisection for the parameter where the segment p0→p1 crosses the capsule boundary.</summary>
    private static float FindBoundary(in InkPoint p0, in InkPoint p1, float ax, float ay, float bx, float by, float r2, bool entering)
    {
        // For a both-outside segment, first locate an inside sample.
        float lo = 0f, hi = 1f;
        bool startInside = Inside(p0, ax, ay, bx, by, r2);
        bool endInside = Inside(p1, ax, ay, bx, by, r2);
        if (!startInside && !endInside)
        {
            float best = 0.5f, bestD = float.MaxValue;
            for (int k = 1; k < 16; k++)
            {
                float t = k / 16f;
                float x = p0.X + (p1.X - p0.X) * t, y = p0.Y + (p1.Y - p0.Y) * t;
                float d = Stroke.SegmentDistanceSquared(x, y, ax, ay, bx, by);
                if (d < bestD) { bestD = d; best = t; }
            }
            if (entering) hi = best; else lo = best;
            startInside = !entering;
        }
        // Invariant: Inside(lo) == startInside, Inside(hi) != startInside.
        for (int iter = 0; iter < 18; iter++)
        {
            float mid = (lo + hi) * 0.5f;
            float x = p0.X + (p1.X - p0.X) * mid, y = p0.Y + (p1.Y - p0.Y) * mid;
            bool mi = Stroke.SegmentDistanceSquared(x, y, ax, ay, bx, by) <= r2;
            if (mi == startInside) lo = mid; else hi = mid;
        }
        return entering ? lo : hi;
    }

    private static InkPoint Lerp(in InkPoint a, in InkPoint b, float t) => new(
        a.X + (b.X - a.X) * t,
        a.Y + (b.Y - a.Y) * t,
        a.Pressure + (b.Pressure - a.Pressure) * t,
        a.TiltX + (b.TiltX - a.TiltX) * t,
        a.TiltY + (b.TiltY - a.TiltY) * t,
        a.Twist < 0 ? a.Twist : a.Twist + (b.Twist - a.Twist) * t,
        a.TimeMs + (int)((b.TimeMs - a.TimeMs) * t));

    /// <summary>Squared distance between segments p1→p2 and q1→q2.</summary>
    public static float SegmentSegmentDistanceSquared(float p1x, float p1y, float p2x, float p2y, float q1x, float q1y, float q2x, float q2y)
    {
        if (SegmentsIntersect(p1x, p1y, p2x, p2y, q1x, q1y, q2x, q2y)) return 0f;
        float d = Stroke.SegmentDistanceSquared(p1x, p1y, q1x, q1y, q2x, q2y);
        d = MathF.Min(d, Stroke.SegmentDistanceSquared(p2x, p2y, q1x, q1y, q2x, q2y));
        d = MathF.Min(d, Stroke.SegmentDistanceSquared(q1x, q1y, p1x, p1y, p2x, p2y));
        d = MathF.Min(d, Stroke.SegmentDistanceSquared(q2x, q2y, p1x, p1y, p2x, p2y));
        return d;
    }

    private static bool SegmentsIntersect(float ax, float ay, float bx, float by, float cx, float cy, float dx, float dy)
    {
        float d1 = Orient(cx, cy, dx, dy, ax, ay);
        float d2 = Orient(cx, cy, dx, dy, bx, by);
        float d3 = Orient(ax, ay, bx, by, cx, cy);
        float d4 = Orient(ax, ay, bx, by, dx, dy);
        return ((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0));
    }

    private static float Orient(float ax, float ay, float bx, float by, float cx, float cy) =>
        (bx - ax) * (cy - ay) - (by - ay) * (cx - ax);
}
