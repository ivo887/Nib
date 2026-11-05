using System;
using System.Collections.Generic;

namespace Nib.Core.Ink;

public enum PressureCurveKind : byte { Linear = 0, Soft = 1, Firm = 2, Custom = 3 }

/// <summary>
/// Maps normalized hardware pressure to a 0..1 response. Calibration (from the calibration screen)
/// first rescales [InputMin, InputMax] to [0, 1] so light and heavy writers both reach the full range;
/// the curve is then a monotone cubic (Fritsch–Carlson) through control points.
/// Evaluation goes through a 257-entry lookup table: one multiply-add, no branches beyond clamping.
/// </summary>
public sealed class PressureCurve : IEquatable<PressureCurve>
{
    private const int LutSize = 256;
    private readonly float[] _lut = new float[LutSize + 1];

    public PressureCurveKind Kind { get; }
    /// <summary>Control points, x strictly increasing in [0,1], y in [0,1], first (0,y0) last (1,y1).</summary>
    public IReadOnlyList<(float X, float Y)> Points { get; }
    public float InputMin { get; }
    public float InputMax { get; }

    public static readonly PressureCurve Linear = new(PressureCurveKind.Linear, new[] { (0f, 0f), (1f, 1f) });
    /// <summary>Light touch already produces substantial width.</summary>
    public static readonly PressureCurve Soft = new(PressureCurveKind.Soft, new[] { (0f, 0f), (0.25f, 0.45f), (0.6f, 0.82f), (1f, 1f) });
    /// <summary>Needs a firm press to reach full width.</summary>
    public static readonly PressureCurve Firm = new(PressureCurveKind.Firm, new[] { (0f, 0f), (0.4f, 0.18f), (0.75f, 0.55f), (1f, 1f) });

    public PressureCurve(PressureCurveKind kind, IReadOnlyList<(float X, float Y)> points, float inputMin = 0f, float inputMax = 1f)
    {
        if (points.Count < 2) throw new ArgumentException("A curve needs at least two points.", nameof(points));
        var pts = new (float X, float Y)[points.Count];
        for (int i = 0; i < pts.Length; i++)
        {
            pts[i] = (Math.Clamp(points[i].X, 0f, 1f), Math.Clamp(points[i].Y, 0f, 1f));
            if (i > 0 && pts[i].X <= pts[i - 1].X) throw new ArgumentException("Curve x values must be strictly increasing.", nameof(points));
        }
        Kind = kind;
        Points = pts;
        InputMin = Math.Clamp(inputMin, 0f, 0.95f);
        InputMax = Math.Clamp(inputMax, InputMin + 0.05f, 1f);
        BuildLut(pts);
    }

    public static PressureCurve FromKind(PressureCurveKind kind) => kind switch
    {
        PressureCurveKind.Soft => Soft,
        PressureCurveKind.Firm => Firm,
        _ => Linear,
    };

    public PressureCurve WithCalibration(float inputMin, float inputMax) => new(Kind, Points, inputMin, inputMax);

    /// <summary>Allocation-free evaluation, safe to call from the ink thread per point.</summary>
    public float Evaluate(float pressure)
    {
        float t = (pressure - InputMin) / (InputMax - InputMin);
        if (!(t > 0f)) return _lut[0];
        if (t >= 1f) return _lut[LutSize];
        float f = t * LutSize;
        int i = (int)f;
        float frac = f - i;
        return _lut[i] + (_lut[i + 1] - _lut[i]) * frac;
    }

    private void BuildLut((float X, float Y)[] p)
    {
        int n = p.Length;
        // Fritsch–Carlson monotone tangents.
        var d = new float[n - 1];
        for (int i = 0; i < n - 1; i++) d[i] = (p[i + 1].Y - p[i].Y) / (p[i + 1].X - p[i].X);
        var m = new float[n];
        m[0] = d[0];
        m[n - 1] = d[n - 2];
        for (int i = 1; i < n - 1; i++) m[i] = d[i - 1] * d[i] <= 0 ? 0 : (d[i - 1] + d[i]) * 0.5f;
        for (int i = 0; i < n - 1; i++)
        {
            if (d[i] == 0) { m[i] = 0; m[i + 1] = 0; continue; }
            float a = m[i] / d[i], b = m[i + 1] / d[i];
            float s = a * a + b * b;
            if (s > 9f)
            {
                float tau = 3f / MathF.Sqrt(s);
                m[i] = tau * a * d[i];
                m[i + 1] = tau * b * d[i];
            }
        }
        int seg = 0;
        for (int k = 0; k <= LutSize; k++)
        {
            float x = k / (float)LutSize;
            while (seg < n - 2 && x > p[seg + 1].X) seg++;
            float h = p[seg + 1].X - p[seg].X;
            float t = Math.Clamp((x - p[seg].X) / h, 0f, 1f);
            float t2 = t * t, t3 = t2 * t;
            float h00 = 2 * t3 - 3 * t2 + 1, h10 = t3 - 2 * t2 + t, h01 = -2 * t3 + 3 * t2, h11 = t3 - t2;
            float y = h00 * p[seg].Y + h10 * h * m[seg] + h01 * p[seg + 1].Y + h11 * h * m[seg + 1];
            _lut[k] = Math.Clamp(y, 0f, 1f);
        }
    }

    public bool Equals(PressureCurve? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        if (Kind != other.Kind || InputMin != other.InputMin || InputMax != other.InputMax || Points.Count != other.Points.Count) return false;
        for (int i = 0; i < Points.Count; i++) if (Points[i] != other.Points[i]) return false;
        return true;
    }

    public override bool Equals(object? obj) => Equals(obj as PressureCurve);
    public override int GetHashCode() => HashCode.Combine(Kind, InputMin, InputMax, Points.Count);
}
