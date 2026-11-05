using System;
using Nib.Core.Rendering;

namespace Nib.Core.Ink;

/// <summary>
/// Receives closed figures. Every figure a builder emits has the same orientation (positive shoelace
/// area in page coordinates), so filling all figures of a run with the non-zero rule yields their exact
/// union: no seams, no overlap darkening, correct at any zoom.
/// </summary>
public interface IPathSink
{
    void BeginFigure(float x, float y);
    void LineTo(float x, float y);
    void EndFigure();
    /// <summary>A full circle figure, traversed with increasing angle (same orientation as polygons).</summary>
    void AddCircle(float cx, float cy, float r);
}

/// <summary>A part of a stroke drawn with one fill. Opaque pens produce exactly one run per stroke.</summary>
public readonly struct StrokeRun
{
    /// <summary>The pen color (opaque ARGB).</summary>
    public readonly uint BaseColor;
    /// <summary>Ink strength 0..1 for this run (1 for opaque pens).</summary>
    public readonly float Strength;
    /// <summary>True for pencil/highlighter: draw with a darken (min) blend using <see cref="FillColor"/>.</summary>
    public readonly bool Multiply;
    /// <summary>The color to fill with: the base color for opaque pens, the color the ink produces on white paper for multiply inks.</summary>
    public readonly uint FillColor;

    public StrokeRun(uint baseColor, float strength, bool multiply)
    {
        BaseColor = baseColor;
        Strength = strength;
        Multiply = multiply;
        FillColor = multiply ? ColorUtil.OverWhite(baseColor, strength) : baseColor;
    }
}

public interface IStrokeRunSink
{
    IPathSink BeginRun(in StrokeRun run);
    void EndRun();
}

/// <summary>
/// Turns a stroke into fill geometry, identically for the screen renderer and the vector exporters.
/// Round nibs: tangent trapezoids between consecutive circles, plus a circle wherever the outline would
/// otherwise leave a gap (caps, sharp turns, width jumps). Elliptical and chisel nibs: convex hull of
/// consecutive footprints (a swept shape). Pencil and highlighter strokes are split into runs of equal
/// strength so pressure-driven opacity survives vector export.
/// Reuses internal buffers: one instance per thread, no allocations once warmed up.
/// </summary>
public sealed class StrokeGeometryBuilder
{
    private const int StrengthLevels = 16;
    /// <summary>Largest outline gap left unfilled at a joint, page units (≈ 0.3 px at 1000% zoom on a 200% display).</summary>
    private const float JointTolerance = 0.015f;

    private Footprint[] _fp = new Footprint[256];
    private float[] _cx = new float[256], _cy = new float[256];
    private int[] _kept = new int[256];
    private float[] _polyA = new float[2 * MaxPolygon], _polyB = new float[2 * MaxPolygon];
    private int _kA;
    private int _cachedIndex = -1;
    private int _endSupPlus = -1, _endSupMinus = -1;

    private const int MaxPolygon = 32;
    /// <summary>Unit-circle samples per vertex count, so footprint polygons need no trigonometry per vertex.</summary>
    private static readonly float[][] UnitCircle = BuildUnitCircles();

    private static float[][] BuildUnitCircles()
    {
        var t = new float[MaxPolygon + 1][];
        for (int k = 3; k <= MaxPolygon; k++)
        {
            t[k] = new float[2 * k];
            for (int i = 0; i < k; i++)
            {
                double a = 2 * Math.PI * i / k;
                t[k][2 * i] = (float)Math.Cos(a);
                t[k][2 * i + 1] = (float)Math.Sin(a);
            }
        }
        return t;
    }

    public void Build(Stroke stroke, IStrokeRunSink sink) =>
        Build(stroke.Kind, stroke.Color, stroke.Width, stroke.Dynamics, stroke.Flags, stroke.Points, sink);

    public void Build(PenKind kind, uint color, float width, PenDynamics dyn, StrokeFlags flags, ReadOnlySpan<InkPoint> pts, IStrokeRunSink sink)
    {
        int n = pts.Length;
        if (n == 0) return;
        Ensure(n);

        var speed = new SpeedTracker();
        for (int i = 0; i < n; i++)
        {
            NibModel.Compute(kind, width, dyn, flags, pts[i], speed.Next(pts[i]), out _fp[i]);
            _cx[i] = pts[i].X;
            _cy[i] = pts[i].Y;
        }

        bool multiply = kind.IsMultiplyInk();
        int keptCount = Decimate(n, multiply);
        _cachedIndex = -1;

        if (keptCount == 1)
        {
            int k = _kept[0];
            var path = sink.BeginRun(new StrokeRun(color, multiply ? Quantize(_fp[k].Strength) : 1f, multiply));
            EmitFootprint(path, _cx[k], _cy[k], in _fp[k]);
            sink.EndRun();
            return;
        }

        // Segment s joins kept[s] and kept[s+1]. Group consecutive segments of equal strength into runs.
        int segCount = keptCount - 1;
        int runStart = 0;
        while (runStart < segCount)
        {
            float level = multiply ? SegmentLevel(runStart) : 1f;
            int runEnd = runStart;
            if (multiply)
                while (runEnd + 1 < segCount && SegmentLevel(runEnd + 1) == level) runEnd++;
            else
                runEnd = segCount - 1;

            var path = sink.BeginRun(new StrokeRun(color, level, multiply));
            bool prevSwept = false;
            for (int s = runStart; s <= runEnd; s++)
            {
                int a = _kept[s], b = _kept[s + 1];
                bool first = s == 0, last = s == segCount - 1;
                if (_fp[a].Shape == FootprintShape.Circle && _fp[b].Shape == FootprintShape.Circle)
                {
                    if (first) path.AddCircle(_cx[a], _cy[a], _fp[a].Rx);
                    else if (s == runStart || prevSwept || NeedsJoint(_kept[s - 1], a, b)) path.AddCircle(_cx[a], _cy[a], _fp[a].Rx);
                    EmitTrapezoid(path, a, b);
                    if (last) path.AddCircle(_cx[b], _cy[b], _fp[b].Rx);
                    prevSwept = false;
                }
                else
                {
                    EmitSwept(path, a, b, startOfChain: s == runStart || !prevSwept);
                    prevSwept = true;
                }
            }
            if (prevSwept) EmitPolygon(path, _polyA, _kA); // end footprint of the chain
            sink.EndRun();
            runStart = runEnd + 1;
        }
    }

    private float SegmentLevel(int s) => Quantize(MathF.Max(_fp[_kept[s]].Strength, _fp[_kept[s + 1]].Strength));

    private static float Quantize(float strength) =>
        Math.Clamp(MathF.Round(strength * StrengthLevels), 1f, StrengthLevels) / StrengthLevels;

    /// <summary>Drops samples that cannot change the outline (dense high-rate pens at slow speed).</summary>
    private int Decimate(int n, bool multiply)
    {
        int count = 0;
        _kept[count++] = 0;
        int last = 0;
        for (int i = 1; i < n - 1; i++)
        {
            ref var f = ref _fp[i];
            ref var lf = ref _fp[last];
            float dx = _cx[i] - _cx[last], dy = _cy[i] - _cy[last];
            float d2 = dx * dx + dy * dy;
            float r = MathF.Min(f.Rx, lf.Rx);
            float minDist = MathF.Max(0.2f * MathF.Min(f.Ry, lf.Ry), 0.04f);
            bool keep = d2 > minDist * minDist
                        || MathF.Abs(f.Rx - lf.Rx) > 0.06f * MathF.Max(r, 0.1f)
                        || MathF.Abs(f.Ry - lf.Ry) > 0.06f * MathF.Max(MathF.Min(f.Ry, lf.Ry), 0.1f)
                        || MathF.Abs(NibModel.LerpAngle(lf.Angle, f.Angle, 1f) - lf.Angle) > 0.05f
                        || (multiply && Quantize(f.Strength) != Quantize(lf.Strength));
            if (keep) { _kept[count++] = i; last = i; }
        }
        if (n > 1)
        {
            // Always keep the final sample, merging it with the previous one if they coincide.
            int lastIdx = n - 1;
            float dx = _cx[lastIdx] - _cx[last], dy = _cy[lastIdx] - _cy[last];
            if (count > 1 && dx * dx + dy * dy < 1e-6f) _kept[count - 1] = lastIdx;
            else _kept[count++] = lastIdx;
        }
        return count;
    }

    private bool NeedsJoint(int prev, int a, int next)
    {
        // Compare tangent normals of the incoming and outgoing segment at circle a.
        if (!TangentNormals(prev, a, out float n1x, out float n1y, out float n2x, out float n2y)) return true;
        if (!TangentNormals(a, next, out float m1x, out float m1y, out float m2x, out float m2y)) return true;
        float dot = MathF.Min(n1x * m1x + n1y * m1y, n2x * m2x + n2y * m2y);
        float chord = _fp[a].Rx * MathF.Sqrt(MathF.Max(0f, 2f - 2f * dot));
        return chord > JointTolerance;
    }

    private bool TangentNormals(int a, int b, out float n1x, out float n1y, out float n2x, out float n2y)
    {
        float dx = _cx[b] - _cx[a], dy = _cy[b] - _cy[a];
        float d = MathF.Sqrt(dx * dx + dy * dy);
        float r0 = _fp[a].Rx, r1 = _fp[b].Rx;
        if (d <= MathF.Abs(r1 - r0) + 1e-5f)
        {
            n1x = n1y = n2x = n2y = 0;
            return false;
        }
        float ux = dx / d, uy = dy / d;
        float s = (r0 - r1) / d;
        float c = MathF.Sqrt(MathF.Max(0f, 1f - s * s));
        // perp(u) = (-uy, ux)
        n1x = s * ux - c * uy; n1y = s * uy + c * ux;
        n2x = s * ux + c * uy; n2y = s * uy - c * ux;
        return true;
    }

    private void EmitTrapezoid(IPathSink path, int a, int b)
    {
        float r0 = _fp[a].Rx, r1 = _fp[b].Rx;
        if (!TangentNormals(a, b, out float n1x, out float n1y, out float n2x, out float n2y))
        {
            // One circle contains the other: the larger circle is the whole segment.
            if (r0 >= r1) path.AddCircle(_cx[a], _cy[a], r0);
            else path.AddCircle(_cx[b], _cy[b], r1);
            return;
        }
        float ax = _cx[a] + r0 * n1x, ay = _cy[a] + r0 * n1y;
        float bx = _cx[b] + r1 * n1x, by = _cy[b] + r1 * n1y;
        float cx = _cx[b] + r1 * n2x, cy = _cy[b] + r1 * n2y;
        float dx = _cx[a] + r0 * n2x, dy = _cy[a] + r0 * n2y;
        float area = (ax * by - bx * ay) + (bx * cy - cx * by) + (cx * dy - dx * cy) + (dx * ay - ax * dy);
        path.BeginFigure(ax, ay);
        if (area >= 0) { path.LineTo(bx, by); path.LineTo(cx, cy); path.LineTo(dx, dy); }
        else { path.LineTo(dx, dy); path.LineTo(cx, cy); path.LineTo(bx, by); }
        path.EndFigure();
    }

    private void EmitFootprint(IPathSink path, float x, float y, in Footprint f)
    {
        if (f.Shape == FootprintShape.Circle)
        {
            path.AddCircle(x, y, f.Rx);
            return;
        }
        int k = FootprintPolygon(in f, x, y, _polyA);
        path.BeginFigure(_polyA[0], _polyA[1]);
        for (int i = 1; i < k; i++) path.LineTo(_polyA[2 * i], _polyA[2 * i + 1]);
        path.EndFigure();
    }

    /// <summary>
    /// Swept convex nib between two samples: the quad between the footprints' outer tangent points
    /// (support points refined twice toward the true bitangent), plus a footprint polygon at the chain start
    /// and wherever consecutive quads stop sharing their tangent vertices (a joint where the outline would
    /// otherwise open up). All parts are positively oriented, so their non-zero union is the swept shape.
    /// O(K) per segment, no sorting; straight runs emit only 4 vertices per sample.
    /// </summary>
    private void EmitSwept(IPathSink path, int a, int b, bool startOfChain)
    {
        if (_cachedIndex != a)
        {
            _kA = FootprintPolygon(in _fp[a], _cx[a], _cy[a], _polyA);
            _cachedIndex = a;
            startOfChain = true;
        }
        int ka = _kA;
        int kb = FootprintPolygon(in _fp[b], _cx[b], _cy[b], _polyB);

        float dx = _cx[b] - _cx[a], dy = _cy[b] - _cy[a];
        float len = MathF.Sqrt(dx * dx + dy * dy);
        if (len <= 1e-5f)
        {
            // Coincident samples: keep both shapes, start a fresh chain.
            EmitPolygon(path, _polyA, ka);
            _endSupPlus = _endSupMinus = -1;
        }
        else
        {
            float nx = -dy / len, ny = dx / len;
            Bitangent(ka, kb, nx, ny, out int ia, out int ib);
            Bitangent(ka, kb, -nx, -ny, out int ja, out int jb);
            if (startOfChain || ia != _endSupPlus || ja != _endSupMinus) EmitPolygon(path, _polyA, ka);
            float ax = _polyA[2 * ia], ay = _polyA[2 * ia + 1];
            float bx = _polyB[2 * ib], by = _polyB[2 * ib + 1];
            float cx = _polyB[2 * jb], cy = _polyB[2 * jb + 1];
            float qx = _polyA[2 * ja], qy = _polyA[2 * ja + 1];
            float area = (ax * by - bx * ay) + (bx * cy - cx * by) + (cx * qy - qx * cy) + (qx * ay - ax * qy);
            if (MathF.Abs(area) > 1e-6f)
            {
                path.BeginFigure(ax, ay);
                if (area > 0) { path.LineTo(bx, by); path.LineTo(cx, cy); path.LineTo(qx, qy); }
                else { path.LineTo(qx, qy); path.LineTo(cx, cy); path.LineTo(bx, by); }
                path.EndFigure();
            }
            _endSupPlus = ib;
            _endSupMinus = jb;
        }

        // The end footprint becomes the start of the next segment.
        (_polyA, _polyB) = (_polyB, _polyA);
        _kA = kb;
        _cachedIndex = b;
    }

    private void Bitangent(int ka, int kb, float nx, float ny, out int ia, out int ib)
    {
        ia = Support(_polyA, ka, nx, ny);
        ib = Support(_polyB, kb, nx, ny);
        for (int iter = 0; iter < 2; iter++)
        {
            float ex = _polyB[2 * ib] - _polyA[2 * ia], ey = _polyB[2 * ib + 1] - _polyA[2 * ia + 1];
            float mx = -ey, my = ex;
            if (mx * nx + my * ny < 0) { mx = -mx; my = -my; }
            float ml = MathF.Sqrt(mx * mx + my * my);
            if (ml < 1e-6f) return;
            int na = Support(_polyA, ka, mx, my), nb = Support(_polyB, kb, mx, my);
            if (na == ia && nb == ib) return;
            ia = na; ib = nb;
        }
    }

    private static int Support(float[] poly, int k, float nx, float ny)
    {
        int best = 0;
        float bestDot = poly[0] * nx + poly[1] * ny;
        for (int i = 1; i < k; i++)
        {
            float d = poly[2 * i] * nx + poly[2 * i + 1] * ny;
            if (d > bestDot) { bestDot = d; best = i; }
        }
        return best;
    }

    private static void EmitPolygon(IPathSink path, float[] poly, int k)
    {
        path.BeginFigure(poly[0], poly[1]);
        for (int i = 1; i < k; i++) path.LineTo(poly[2 * i], poly[2 * i + 1]);
        path.EndFigure();
    }

    /// <summary>Writes the footprint outline (increasing angle = positive orientation) and returns the vertex count.</summary>
    private static int FootprintPolygon(in Footprint f, float x, float y, float[] dst)
    {
        float c = MathF.Cos(f.Angle), s = MathF.Sin(f.Angle);
        if (f.Shape == FootprintShape.Rect)
        {
            Corner(dst, 0, f.Rx, f.Ry, c, s, x, y);
            Corner(dst, 1, -f.Rx, f.Ry, c, s, x, y);
            Corner(dst, 2, -f.Rx, -f.Ry, c, s, x, y);
            Corner(dst, 3, f.Rx, -f.Ry, c, s, x, y);
            return 4;
        }
        int k = Math.Clamp((int)MathF.Ceiling(MathF.Max(f.Rx, f.Ry) * 3f) + 8, 10, MaxPolygon);
        var unit = UnitCircle[k];
        for (int i = 0; i < k; i++)
            Corner(dst, i, f.Rx * unit[2 * i], f.Ry * unit[2 * i + 1], c, s, x, y);
        return k;
    }

    private static void Corner(float[] dst, int i, float lx, float ly, float c, float s, float x, float y)
    {
        dst[2 * i] = x + lx * c - ly * s;
        dst[2 * i + 1] = y + lx * s + ly * c;
    }

    private void Ensure(int n)
    {
        if (_fp.Length >= n) return;
        int size = Math.Max(n, _fp.Length * 2);
        _fp = new Footprint[size];
        _cx = new float[size];
        _cy = new float[size];
        _kept = new int[size];
    }
}
