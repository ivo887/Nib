using System;
using System.Runtime.CompilerServices;

namespace Nib.Core.Ink;

public enum FootprintShape : byte { Circle, Ellipse, Rect }

/// <summary>The contact shape of the nib at one sample, in page units.</summary>
public struct Footprint
{
    public FootprintShape Shape;
    /// <summary>Half extent along the shape's own x axis (radius for circles).</summary>
    public float Rx;
    /// <summary>Half extent along the shape's own y axis.</summary>
    public float Ry;
    /// <summary>Rotation of the shape's x axis, radians (screen coordinates, y down).</summary>
    public float Angle;
    /// <summary>Ink strength 0..1. Opaque pens always 1; pencil and highlighter vary with pressure and tilt.</summary>
    public float Strength;
}

/// <summary>
/// Physical model of each pen type. Pure function of (stored point, pen parameters, speed), so the
/// on-screen renderer and every exporter produce identical shapes.
/// </summary>
public static class NibModel
{
    private const float Deg2Rad = MathF.PI / 180f;
    /// <summary>Tilt angle (from vertical) at which tilt effects saturate.</summary>
    private const float TiltSaturation = 60f * Deg2Rad;
    /// <summary>Default broad-nib angle for the fountain pen: edge from lower-left to upper-right.</summary>
    private const float FountainBaseAngle = -MathF.PI / 4f;
    /// <summary>Default chisel orientation for the highlighter: long side vertical, good for marking lines of text.</summary>
    private const float HighlighterBaseAngle = MathF.PI / 2f;
    /// <summary>Calligraphy edge angle: from lower-left to upper-right, 40 degrees.</summary>
    private const float CalligraphyBaseAngle = -40f * Deg2Rad;
    /// <summary>Writing speed (units/ms) treated as "fast" for the speed-based width fallback (~3 in/s).</summary>
    private const float FastSpeed = 3f;

    /// <summary>Largest distance any footprint of this pen can extend from its sample point.</summary>
    public static float MaxExtent(PenKind kind, float width) => kind switch
    {
        PenKind.Pencil => width * 1.6f,
        PenKind.Brush => width * 1.7f,
        PenKind.Watercolor => width * 1.4f,
        PenKind.Marker => width * 1.2f,
        PenKind.Highlighter => width * 0.85f,
        _ => width * 0.55f,
    };

    /// <summary>Converts pressure (or the speed fallback) to the 0..1 response after the pen curve.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Response(PenDynamics dyn, StrokeFlags flags, float pressure, float speed)
    {
        if (!dyn.PressureEnabled) return 1f;
        if ((flags & StrokeFlags.HasPressure) != 0) return dyn.Curve.Evaluate(pressure);
        if ((flags & StrokeFlags.SpeedWidth) != 0)
        {
            float s = MathF.Min(speed / FastSpeed, 1f);
            return 1f - dyn.SpeedSensitivity * 0.65f * s;
        }
        return 1f;
    }

    /// <summary>Computes the footprint for one sample.</summary>
    /// <param name="speed">Smoothed writing speed at this sample, page units per millisecond.</param>
    public static void Compute(PenKind kind, float width, PenDynamics dyn, StrokeFlags flags, in InkPoint p, float speed, out Footprint f)
    {
        float response = Response(dyn, flags, p.Pressure, speed);
        float minRatio = kind == PenKind.Highlighter ? MathF.Max(dyn.MinWidthRatio, 0.6f) : dyn.MinWidthRatio;
        float widthFactor = dyn.PressureEnabled ? minRatio + (1f - minRatio) * response : 1f;

        // Tilt: t in [0,1] is how far the pen leans (scaled by sensitivity); azimuth is the lean direction.
        float t = 0f, azimuth = 0f;
        if ((flags & StrokeFlags.HasTilt) != 0 && dyn.TiltSensitivity > 0f)
        {
            float tx = MathF.Tan(Math.Clamp(p.TiltX, -89f, 89f) * Deg2Rad);
            float ty = MathF.Tan(Math.Clamp(p.TiltY, -89f, 89f) * Deg2Rad);
            float mag = MathF.Sqrt(tx * tx + ty * ty);
            if (mag > 1e-4f)
            {
                float lean = MathF.Atan(mag);
                t = MathF.Min(lean / TiltSaturation, 1f) * dyn.TiltSensitivity;
                azimuth = MathF.Atan2(ty, tx);
            }
        }

        float r = width * 0.5f * widthFactor;
        switch (kind)
        {
            case PenKind.Fountain:
            {
                // Flat nib: thick strokes perpendicular to the edge, thin strokes along it.
                float angle = FountainBaseAngle;
                if (t > 0f) angle = LerpAngle(angle, azimuth + MathF.PI / 2f, MathF.Min(t * 1.4f, 1f));
                if ((flags & StrokeFlags.HasTwist) != 0 && p.Twist >= 0) angle += p.Twist * Deg2Rad;
                f.Shape = FootprintShape.Ellipse;
                f.Rx = MathF.Max(r, 0.15f);
                f.Ry = MathF.Max(r * 0.2f, 0.12f);
                f.Angle = angle;
                f.Strength = 1f;
                break;
            }
            case PenKind.Pencil:
            {
                // Leaning the pencil uses the side of the lead: wider, lighter marks along the lean direction.
                f.Shape = t > 0.02f ? FootprintShape.Ellipse : FootprintShape.Circle;
                f.Rx = MathF.Max(r * (1f + 2.0f * t), 0.15f);
                f.Ry = MathF.Max(r * (1f + 0.3f * t), 0.15f);
                f.Angle = azimuth;
                float strength = dyn.PressureEnabled ? 0.3f + 0.7f * response : 0.85f;
                f.Strength = Math.Clamp(strength * (1f - 0.7f * t), 0.08f, 1f);
                break;
            }
            case PenKind.Highlighter:
            {
                // Chisel tip: tilting lays the chisel flatter, changing the contact rectangle.
                float halfLong = width * 0.5f * widthFactor;
                float halfShort = width * 0.16f * widthFactor;
                f.Shape = FootprintShape.Rect;
                f.Rx = MathF.Max(halfLong * (1f + 0.4f * t), 0.5f);
                f.Ry = MathF.Max(halfShort * (1f + 1.5f * t), 0.3f);
                f.Angle = t > 0f ? LerpAngle(HighlighterBaseAngle, azimuth + MathF.PI / 2f, t) : HighlighterBaseAngle;
                f.Strength = dyn.PressureEnabled ? 0.45f + 0.55f * response : 0.8f;
                break;
            }
            case PenKind.Brush:
            {
                // Pressure already widened r; leaning lays the bristles on their side: broad along the lean, a touch narrower across.
                f.Shape = t > 0.02f ? FootprintShape.Ellipse : FootprintShape.Circle;
                f.Rx = MathF.Max(r * (1f + 2.2f * t), 0.15f);
                f.Ry = MathF.Max(r * (1f - 0.25f * t), 0.12f);
                f.Angle = azimuth;
                f.Strength = 1f;
                break;
            }
            case PenKind.Calligraphy:
            {
                // Crisp chisel: the lean swings the edge round (more strongly than the fountain pen); barrel twist adds on top.
                float angle = CalligraphyBaseAngle;
                if (t > 0f) angle = LerpAngle(angle, azimuth + MathF.PI / 2f, MathF.Min(t * 1.2f, 1f));
                if ((flags & StrokeFlags.HasTwist) != 0 && p.Twist >= 0) angle += p.Twist * Deg2Rad;
                f.Shape = FootprintShape.Rect;
                f.Rx = MathF.Max(r, 0.3f);
                f.Ry = MathF.Max(r * 0.12f, 0.15f);
                f.Angle = angle;
                f.Strength = 1f;
                break;
            }
            case PenKind.Marker:
            {
                // Felt tip: leaning broadens the tip along the lean. Pressure only nudges how wet it is.
                f.Shape = t > 0.02f ? FootprintShape.Ellipse : FootprintShape.Circle;
                f.Rx = MathF.Max(r * (1f + 1.1f * t), 0.3f);
                f.Ry = MathF.Max(r * (1f - 0.3f * t), 0.25f);
                f.Angle = azimuth;
                f.Strength = dyn.PressureEnabled ? 0.7f + 0.3f * response : 0.85f;
                break;
            }
            case PenKind.Watercolor:
            {
                // A wet brush: pressure loads pigment, leaning spreads the wash wide and thins it.
                f.Shape = t > 0.02f ? FootprintShape.Ellipse : FootprintShape.Circle;
                f.Rx = MathF.Max(r * (1f + 1.6f * t), 0.5f);
                f.Ry = MathF.Max(r * (1f - 0.35f * t), 0.4f);
                f.Angle = azimuth;
                float pigment = dyn.PressureEnabled ? 0.15f + 0.5f * response : 0.4f;
                f.Strength = Math.Clamp(pigment * (1f - 0.35f * t), 0.06f, 1f);
                break;
            }
            default:
                f.Shape = FootprintShape.Circle;
                f.Rx = f.Ry = MathF.Max(r, 0.12f);
                f.Angle = 0f;
                f.Strength = 1f;
                break;
        }
    }

    /// <summary>Interpolates between two orientations of a symmetric shape (period π), taking the short way round.</summary>
    public static float LerpAngle(float a, float b, float t)
    {
        float d = b - a;
        // Shapes are symmetric under 180° rotation, so reduce to (-π/2, π/2].
        d -= MathF.PI * MathF.Round(d / MathF.PI);
        return a + d * Math.Clamp(t, 0f, 1f);
    }
}

/// <summary>
/// Exponentially smoothed writing speed along a stroke, in page units per millisecond.
/// Used only for the speed-based width fallback; deterministic so it re-renders identically.
/// </summary>
public struct SpeedTracker
{
    private float _speed;
    private bool _hasPrev;
    private float _px, _py;
    private int _pt;

    public float Next(in InkPoint p)
    {
        if (!_hasPrev)
        {
            _hasPrev = true; _px = p.X; _py = p.Y; _pt = p.TimeMs; _speed = 0;
            return 0;
        }
        float dx = p.X - _px, dy = p.Y - _py;
        int dt = Math.Max(p.TimeMs - _pt, 1);
        float inst = MathF.Sqrt(dx * dx + dy * dy) / dt;
        _speed += (inst - _speed) * 0.3f;
        _px = p.X; _py = p.Y; _pt = p.TimeMs;
        return _speed;
    }
}
