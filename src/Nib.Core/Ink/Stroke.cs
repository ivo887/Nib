using System;
using System.Numerics;
using Nib.Core.Geometry;
using Nib.Core.Spatial;

namespace Nib.Core.Ink;

/// <summary>
/// A committed stroke. Immutable after construction (edits create a new instance with the same
/// <see cref="Id"/>), which makes strokes safe to hand to the autosave and export threads without copying.
/// </summary>
public sealed class Stroke : ISpatialItem
{
    private readonly InkPoint[] _points;

    /// <summary>Unique within the page; stable across edits of the same stroke.</summary>
    public long Id { get; }
    /// <summary>Paint order within the page (higher = on top). Stable across edits.</summary>
    public long Z { get; }
    public PenKind Kind { get; }
    /// <summary>Opaque ARGB.</summary>
    public uint Color { get; }
    /// <summary>Maximum nib width in page units.</summary>
    public float Width { get; }
    public PenDynamics Dynamics { get; }
    public StrokeFlags Flags { get; }
    public RectF Bounds { get; }

    public ReadOnlySpan<InkPoint> Points => _points;
    public int PointCount => _points.Length;
    internal InkPoint[] PointArray => _points;

    /// <summary>Renderer-owned cache (device geometry). Only touched by the ink thread.</summary>
    public object? RenderCache { get; set; }

    int ISpatialItem.QueryStamp { get; set; }
    RectF ISpatialItem.Bounds => Bounds;

    public Stroke(long id, long z, PenKind kind, uint color, float width, PenDynamics dynamics, StrokeFlags flags, InkPoint[] points)
    {
        if (points is null || points.Length == 0) throw new ArgumentException("A stroke needs at least one point.", nameof(points));
        Id = id;
        Z = z;
        Kind = kind;
        Color = color | 0xFF000000u;
        Width = width;
        Dynamics = dynamics;
        Flags = flags;
        _points = points;
        Bounds = ComputeBounds(points, NibModel.MaxExtent(kind, width));
    }

    private static RectF ComputeBounds(InkPoint[] pts, float extent)
    {
        float l = float.PositiveInfinity, t = float.PositiveInfinity, r = float.NegativeInfinity, b = float.NegativeInfinity;
        for (int i = 0; i < pts.Length; i++)
        {
            float x = pts[i].X, y = pts[i].Y;
            if (x < l) l = x;
            if (x > r) r = x;
            if (y < t) t = y;
            if (y > b) b = y;
        }
        return new RectF(l - extent, t - extent, r + extent, b + extent);
    }

    public Stroke WithPoints(InkPoint[] points, long? newId = null) =>
        new(newId ?? Id, Z, Kind, Color, Width, Dynamics, Flags, points);

    public Stroke WithColor(uint color) => new(Id, Z, Kind, color, Width, Dynamics, Flags, _points);

    public Stroke WithZ(long z, long? newId = null) => new(newId ?? Id, z, Kind, Color, Width, Dynamics, Flags, _points);

    /// <summary>
    /// Applies a similarity transform (move / uniform resize / rotate). Width scales with the transform
    /// and tilt directions rotate with it, so fountain-pen and pencil shapes keep their look.
    /// </summary>
    public Stroke Transformed(Matrix3x2 m)
    {
        float scale = MathF.Sqrt(MathF.Abs(m.M11 * m.M22 - m.M12 * m.M21));
        float rot = MathF.Atan2(m.M12, m.M11);
        bool rotates = MathF.Abs(rot) > 1e-5f;
        float c = MathF.Cos(rot), s = MathF.Sin(rot);
        var pts = new InkPoint[_points.Length];
        for (int i = 0; i < pts.Length; i++)
        {
            var p = _points[i];
            var v = Vector2.Transform(new Vector2(p.X, p.Y), m);
            p.X = v.X; p.Y = v.Y;
            if (rotates)
            {
                if ((Flags & StrokeFlags.HasTilt) != 0) RotateTilt(ref p, c, s);
                if ((Flags & StrokeFlags.HasTwist) != 0 && p.Twist >= 0) p.Twist = (p.Twist + rot * 180f / MathF.PI + 360f) % 360f;
            }
            pts[i] = p;
        }
        return new Stroke(Id, Z, Kind, Color, Width * scale, Dynamics, Flags, pts);
    }

    private static void RotateTilt(ref InkPoint p, float c, float s)
    {
        const float d2r = MathF.PI / 180f;
        float tx = MathF.Tan(p.TiltX * d2r), ty = MathF.Tan(p.TiltY * d2r);
        float rx = tx * c - ty * s, ry = tx * s + ty * c;
        p.TiltX = MathF.Atan(rx) / d2r;
        p.TiltY = MathF.Atan(ry) / d2r;
    }

    /// <summary>Distance from a point to the stroke's centerline, minus the local nib radius (≤ 0 means touching).</summary>
    public float DistanceTo(float x, float y)
    {
        var pts = _points;
        float r0 = Width * 0.5f;
        if (pts.Length == 1)
        {
            float dx = x - pts[0].X, dy = y - pts[0].Y;
            return MathF.Sqrt(dx * dx + dy * dy) - r0;
        }
        float best = float.MaxValue;
        for (int i = 1; i < pts.Length; i++)
        {
            float d = SegmentDistanceSquared(x, y, pts[i - 1].X, pts[i - 1].Y, pts[i].X, pts[i].Y);
            if (d < best) best = d;
        }
        return MathF.Sqrt(best) - r0;
    }

    internal static float SegmentDistanceSquared(float px, float py, float ax, float ay, float bx, float by)
    {
        float vx = bx - ax, vy = by - ay;
        float len2 = vx * vx + vy * vy;
        float t = len2 > 0 ? ((px - ax) * vx + (py - ay) * vy) / len2 : 0;
        t = Math.Clamp(t, 0f, 1f);
        float cx = ax + t * vx - px, cy = ay + t * vy - py;
        return cx * cx + cy * cy;
    }

    public override string ToString() => $"Stroke {Id} {Kind} {PointCount}pts {Bounds}";
}
