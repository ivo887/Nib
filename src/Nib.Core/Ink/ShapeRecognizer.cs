using System;
using System.Collections.Generic;
using System.Numerics;

namespace Nib.Core.Ink;

public enum ShapeKind : byte { None = 0, Line, Ellipse, Circle, Triangle, Rectangle, Square, Polygon }

/// <summary>A recognized shape: its corner points (or ellipse outline samples) and whether it is closed.</summary>
public readonly record struct ShapeResult(ShapeKind Kind, Vector2[] Vertices, bool Closed)
{
    public static readonly ShapeResult None = new(ShapeKind.None, Array.Empty<Vector2>(), false);
    public bool IsShape => Kind != ShapeKind.None;
}

/// <summary>
/// Turns a hand-drawn stroke into a clean shape ("hold at the end to snap"): straight line (with 0/45/90 degree
/// angle snapping), circle, ellipse, triangle, rectangle or square. Pure geometry in page units, no UI.
/// A stroke that does not clearly match any shape is left alone (result <see cref="ShapeKind.None"/>).
/// </summary>
public static class ShapeRecognizer
{
    /// <summary>Marks smaller than this (page units) are dots or ticks, not shapes.</summary>
    public const float MinSize = 14f;
    /// <summary>Lines and rectangles within this many degrees of horizontal, vertical (or 45) snap exactly.</summary>
    public const float AngleSnapDegrees = 8f;
    private const float Deg2Rad = MathF.PI / 180f;

    public static ShapeResult Recognize(ReadOnlySpan<InkPoint> points, bool snapAngles = true)
    {
        var p = Dedupe(points);
        if (p.Count < 4) return ShapeResult.None;

        float length = PathLength(p);
        Bounds(p, out var min, out var max);
        var size = max - min;
        if (size.Length() < MinSize) return ShapeResult.None;

        float chord = Vector2.Distance(p[0], p[^1]);
        if (chord > 0.2f * length) return RecognizeOpen(p, length, chord, snapAngles);
        return RecognizeClosed(p, length, size, snapAngles);
    }

    // ---- open strokes: lines -------------------------------------------------------------------

    private static ShapeResult RecognizeOpen(List<Vector2> p, float length, float chord, bool snapAngles)
    {
        if (chord < 0.8f * length) return ShapeResult.None;
        float dev = MaxDeviationFromChord(p);
        if (dev > 0.09f * chord) return ShapeResult.None;
        var end = SnapEndpoint(p[0], p[^1], snapAngles);
        return new ShapeResult(ShapeKind.Line, new[] { p[0], end }, false);
    }

    /// <summary>Snaps the direction from <paramref name="a"/> to <paramref name="b"/> to the nearest multiple of 45 degrees when close.</summary>
    public static Vector2 SnapEndpoint(Vector2 a, Vector2 b, bool enabled)
    {
        if (!enabled) return b;
        var d = b - a;
        float len = d.Length();
        if (len < 1e-3f) return b;
        float angle = MathF.Atan2(d.Y, d.X) / Deg2Rad;
        float snapped = MathF.Round(angle / 45f) * 45f;
        if (MathF.Abs(angle - snapped) > AngleSnapDegrees) return b;
        float r = snapped * Deg2Rad;
        // Exact zeros for horizontal and vertical (no float noise leaving a 1e-6 slope).
        return new Vector2(a.X + SnapZero(MathF.Cos(r)) * len, a.Y + SnapZero(MathF.Sin(r)) * len);
    }

    private static float SnapZero(float v) => MathF.Abs(v) < 1e-6f ? 0f : v;

    // ---- closed strokes: ellipse, triangle, rectangle ------------------------------------------

    private static ShapeResult RecognizeClosed(List<Vector2> p, float length, Vector2 size, bool snapAngles)
    {
        // A line drawn out and back is not a loop.
        if (MathF.Min(size.X, size.Y) < 0.1f * MathF.Max(size.X, size.Y) && MathF.Min(size.X, size.Y) < 18f) return ShapeResult.None;

        var ring = SimplifyRing(p, length);
        float errPoly = ring.Count is 3 or 4 ? MeanDistanceToPolygon(p, ring) / length : float.MaxValue;

        bool haveEllipse = FitEllipse(p, out var center, out float a, out float b, out float theta);
        float errEllipse = haveEllipse ? MeanDistanceToEllipse(p, center, a, b, theta) / length : float.MaxValue;

        if (ring.Count is 3 or 4 && errPoly < 0.022f && errPoly <= errEllipse * 1.1f)
            return ring.Count == 3 ? new ShapeResult(ShapeKind.Triangle, ring.ToArray(), true) : MakeQuad(ring, snapAngles);

        if (haveEllipse && errEllipse < 0.03f) return MakeEllipse(center, a, b, theta, snapAngles);
        return ShapeResult.None;
    }

    private static ShapeResult MakeQuad(List<Vector2> q, bool snapAngles)
    {
        // Right angles on all four corners: a rectangle (axis-aligned when close to it).
        for (int i = 0; i < 4; i++)
        {
            float angle = AngleAt(q[(i + 3) % 4], q[i], q[(i + 1) % 4]);
            if (MathF.Abs(angle - 90f) > 22f) return new ShapeResult(ShapeKind.Polygon, q.ToArray(), true);
        }
        // Mean edge direction folded to a quarter turn (circular mean of 4*angle).
        float sx = 0, sy = 0;
        for (int i = 0; i < 4; i++)
        {
            var e = q[(i + 1) % 4] - q[i];
            float t = MathF.Atan2(e.Y, e.X) * 4f;
            sx += MathF.Cos(t); sy += MathF.Sin(t);
        }
        float theta = MathF.Atan2(sy, sx) / 4f;
        if (snapAngles && MathF.Abs(theta / Deg2Rad) <= AngleSnapDegrees) theta = 0f;

        var (cos, sin) = (MathF.Cos(theta), MathF.Sin(theta));
        float uMin = float.MaxValue, uMax = float.MinValue, vMin = float.MaxValue, vMax = float.MinValue;
        foreach (var v in q)
        {
            float u = v.X * cos + v.Y * sin, w = -v.X * sin + v.Y * cos;
            uMin = MathF.Min(uMin, u); uMax = MathF.Max(uMax, u); vMin = MathF.Min(vMin, w); vMax = MathF.Max(vMax, w);
        }
        float width = uMax - uMin, height = vMax - vMin;
        bool square = MathF.Abs(width - height) <= 0.1f * MathF.Max(width, height);
        if (square)
        {
            float s = (width + height) * 0.5f, cu = (uMin + uMax) * 0.5f, cv = (vMin + vMax) * 0.5f;
            uMin = cu - s / 2; uMax = cu + s / 2; vMin = cv - s / 2; vMax = cv + s / 2;
        }
        Vector2 At(float u, float w) => new(u * cos - w * sin, u * sin + w * cos);
        var corners = new[] { At(uMin, vMin), At(uMax, vMin), At(uMax, vMax), At(uMin, vMax) };
        return new ShapeResult(square ? ShapeKind.Square : ShapeKind.Rectangle, corners, true);
    }

    private static ShapeResult MakeEllipse(Vector2 center, float a, float b, float theta, bool snapAngles)
    {
        bool circle = MathF.Abs(a - b) <= 0.12f * MathF.Max(a, b);
        if (circle) { a = b = (a + b) * 0.5f; theta = 0f; }
        else if (snapAngles)
        {
            // Principal axis close to horizontal or vertical: make it exact.
            float deg = theta / Deg2Rad;
            if (MathF.Abs(deg) <= AngleSnapDegrees) theta = 0f;
            else if (MathF.Abs(MathF.Abs(deg) - 90f) <= AngleSnapDegrees) { (a, b) = (b, a); theta = 0f; }
        }
        int n = Math.Clamp((int)(MathF.Tau * MathF.Max(a, b) / 3f), 48, 240);
        var pts = new Vector2[n + 1];
        var (cos, sin) = (MathF.Cos(theta), MathF.Sin(theta));
        for (int i = 0; i <= n; i++)
        {
            float t = i / (float)n * MathF.Tau;
            float u = a * MathF.Cos(t), v = b * MathF.Sin(t);
            pts[i] = new Vector2(center.X + u * cos - v * sin, center.Y + u * sin + v * cos);
        }
        return new ShapeResult(circle ? ShapeKind.Circle : ShapeKind.Ellipse, pts, true);
    }

    // ---- fitting helpers -----------------------------------------------------------------------

    /// <summary>Principal-axis ellipse: orientation from the point covariance, axes from the extents along it.</summary>
    private static bool FitEllipse(List<Vector2> p, out Vector2 center, out float a, out float b, out float theta)
    {
        var mean = Vector2.Zero;
        foreach (var v in p) mean += v;
        mean /= p.Count;
        float sxx = 0, syy = 0, sxy = 0;
        foreach (var v in p) { var d = v - mean; sxx += d.X * d.X; syy += d.Y * d.Y; sxy += d.X * d.Y; }
        theta = 0.5f * MathF.Atan2(2 * sxy, sxx - syy);
        var (cos, sin) = (MathF.Cos(theta), MathF.Sin(theta));
        float uMin = float.MaxValue, uMax = float.MinValue, vMin = float.MaxValue, vMax = float.MinValue;
        foreach (var pt in p)
        {
            float u = (pt.X - mean.X) * cos + (pt.Y - mean.Y) * sin;
            float w = -(pt.X - mean.X) * sin + (pt.Y - mean.Y) * cos;
            uMin = MathF.Min(uMin, u); uMax = MathF.Max(uMax, u); vMin = MathF.Min(vMin, w); vMax = MathF.Max(vMax, w);
        }
        a = (uMax - uMin) * 0.5f; b = (vMax - vMin) * 0.5f;
        float cu = (uMin + uMax) * 0.5f, cv = (vMin + vMax) * 0.5f;
        center = new Vector2(mean.X + cu * cos - cv * sin, mean.Y + cu * sin + cv * cos);
        return a > 4f && b > 4f && MathF.Min(a, b) >= 0.08f * MathF.Max(a, b);
    }

    private static float MeanDistanceToEllipse(List<Vector2> p, Vector2 c, float a, float b, float theta)
    {
        var (cos, sin) = (MathF.Cos(theta), MathF.Sin(theta));
        float scale = (a + b) * 0.5f, sum = 0;
        foreach (var pt in p)
        {
            float u = (pt.X - c.X) * cos + (pt.Y - c.Y) * sin;
            float w = -(pt.X - c.X) * sin + (pt.Y - c.Y) * cos;
            sum += MathF.Abs(MathF.Sqrt(u * u / (a * a) + w * w / (b * b)) - 1f) * scale;
        }
        return sum / p.Count;
    }

    private static float MeanDistanceToPolygon(List<Vector2> p, List<Vector2> poly)
    {
        float sum = 0;
        foreach (var pt in p)
        {
            float best = float.MaxValue;
            for (int i = 0; i < poly.Count; i++) best = MathF.Min(best, DistanceToSegment(pt, poly[i], poly[(i + 1) % poly.Count]));
            sum += best;
        }
        return sum / p.Count;
    }

    /// <summary>Corner points of a closed stroke: Ramer-Douglas-Peucker on both halves, then drop near-straight and tiny corners.</summary>
    private static List<Vector2> SimplifyRing(List<Vector2> p, float length)
    {
        int pivot = 0;
        float far = -1;
        for (int i = 1; i < p.Count; i++)
        {
            float d = Vector2.DistanceSquared(p[0], p[i]);
            if (d > far) { far = d; pivot = i; }
        }
        float eps = 0.03f * length;
        var keep = new bool[p.Count];
        keep[0] = keep[pivot] = true;
        Rdp(p, 0, pivot, eps, keep);
        Rdp(p, pivot, p.Count - 1, eps, keep);
        var ring = new List<Vector2>();
        for (int i = 0; i < p.Count - 1; i++) if (keep[i]) ring.Add(p[i]); // the last point closes onto the first

        bool changed = true;
        while (changed && ring.Count > 3)
        {
            changed = false;
            for (int i = 0; i < ring.Count && ring.Count > 3; i++)
            {
                var prev = ring[(i + ring.Count - 1) % ring.Count];
                var next = ring[(i + 1) % ring.Count];
                bool straight = AngleAt(prev, ring[i], next) > 155f;
                bool tiny = Vector2.Distance(ring[i], next) < 0.07f * length;
                if (straight || tiny) { ring.RemoveAt(i); changed = true; break; }
            }
        }
        return ring;
    }

    private static void Rdp(List<Vector2> p, int first, int last, float eps, bool[] keep)
    {
        if (last <= first + 1) return;
        float best = -1; int idx = -1;
        for (int i = first + 1; i < last; i++)
        {
            float d = DistanceToSegment(p[i], p[first], p[last]);
            if (d > best) { best = d; idx = i; }
        }
        if (best <= eps) return;
        keep[idx] = true;
        Rdp(p, first, idx, eps, keep);
        Rdp(p, idx, last, eps, keep);
    }

    // ---- geometry ------------------------------------------------------------------------------

    private static List<Vector2> Dedupe(ReadOnlySpan<InkPoint> pts)
    {
        var r = new List<Vector2>(pts.Length);
        foreach (var ip in pts)
        {
            var v = new Vector2(ip.X, ip.Y);
            if (r.Count == 0 || Vector2.DistanceSquared(r[^1], v) >= 0.25f) r.Add(v);
        }
        return r;
    }

    private static float PathLength(List<Vector2> p)
    {
        float l = 0;
        for (int i = 1; i < p.Count; i++) l += Vector2.Distance(p[i - 1], p[i]);
        return l;
    }

    private static void Bounds(List<Vector2> p, out Vector2 min, out Vector2 max)
    {
        min = new Vector2(float.MaxValue); max = new Vector2(float.MinValue);
        foreach (var v in p) { min = Vector2.Min(min, v); max = Vector2.Max(max, v); }
    }

    private static float MaxDeviationFromChord(List<Vector2> p)
    {
        float best = 0;
        for (int i = 1; i < p.Count - 1; i++) best = MathF.Max(best, DistanceToLine(p[i], p[0], p[^1]));
        return best;
    }

    private static float DistanceToLine(Vector2 pt, Vector2 a, Vector2 b)
    {
        var d = b - a;
        float len = d.Length();
        if (len < 1e-6f) return Vector2.Distance(pt, a);
        return MathF.Abs(d.X * (pt.Y - a.Y) - d.Y * (pt.X - a.X)) / len;
    }

    private static float DistanceToSegment(Vector2 pt, Vector2 a, Vector2 b)
    {
        var d = b - a;
        float l2 = d.LengthSquared();
        if (l2 < 1e-9f) return Vector2.Distance(pt, a);
        float t = Math.Clamp(Vector2.Dot(pt - a, d) / l2, 0f, 1f);
        return Vector2.Distance(pt, a + d * t);
    }

    /// <summary>Interior angle at <paramref name="b"/> in degrees (0..180).</summary>
    private static float AngleAt(Vector2 a, Vector2 b, Vector2 c)
    {
        var u = Vector2.Normalize(a - b); var v = Vector2.Normalize(c - b);
        return MathF.Acos(Math.Clamp(Vector2.Dot(u, v), -1f, 1f)) / Deg2Rad;
    }

    // ---- turning a result back into ink --------------------------------------------------------

    /// <summary>
    /// Samples the shape as ink points about <paramref name="spacing"/> apart, with the pressure, tilt and speed of the
    /// original stroke, so it is drawn with the same pen but with an even, clean line.
    /// </summary>
    public static InkPoint[] ToInkPoints(in ShapeResult shape, ReadOnlySpan<InkPoint> original, float spacing = 2.5f)
    {
        var path = new List<Vector2>(shape.Vertices);
        if (shape.Closed && shape.Kind is not (ShapeKind.Ellipse or ShapeKind.Circle)) path.Add(path[0]);

        float pressure = 0.5f, tx = 0, ty = 0, speed = 0.4f; // page units per ms
        if (original.Length > 0)
        {
            float sp = 0, stx = 0, sty = 0, len = 0;
            for (int i = 0; i < original.Length; i++)
            {
                sp += original[i].Pressure; stx += original[i].TiltX; sty += original[i].TiltY;
                if (i > 0) len += MathF.Sqrt(MathF.Pow(original[i].X - original[i - 1].X, 2) + MathF.Pow(original[i].Y - original[i - 1].Y, 2));
            }
            pressure = Math.Clamp(sp / original.Length, 0.2f, 1f);
            tx = stx / original.Length; ty = sty / original.Length;
            int ms = Math.Max(1, original[^1].TimeMs);
            speed = Math.Clamp(len / ms, 0.05f, 4f);
        }

        var result = new List<InkPoint>(256);
        float t = 0;
        result.Add(new InkPoint(path[0].X, path[0].Y, pressure, tx, ty, -1, 0));
        for (int i = 1; i < path.Count; i++)
        {
            var a = path[i - 1]; var b = path[i];
            float segLen = Vector2.Distance(a, b);
            int steps = Math.Max(1, (int)MathF.Ceiling(segLen / spacing));
            for (int s = 1; s <= steps; s++)
            {
                var v = Vector2.Lerp(a, b, s / (float)steps);
                t += segLen / steps / speed;
                result.Add(new InkPoint(v.X, v.Y, pressure, tx, ty, -1, (int)t));
            }
        }
        return result.ToArray();
    }
}
