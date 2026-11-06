using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Nib.Core.Ink;

/// <summary>A raw digitizer sample in page units, straight from the pointer event.</summary>
public struct RawSample
{
    public float X, Y;
    public float Pressure;
    public float TiltX, TiltY;
    public float Twist;
    /// <summary>Microseconds (PointerPoint.Timestamp).</summary>
    public ulong TimestampUs;

    public RawSample(float x, float y, float pressure, float tiltX, float tiltY, float twist, ulong timestampUs)
    {
        X = x; Y = y; Pressure = pressure; TiltX = tiltX; TiltY = tiltY; Twist = twist; TimestampUs = timestampUs;
    }
}

/// <summary>
/// One-Euro filter (Casiez et al., 2012): heavy smoothing when the pen is slow (kills jitter),
/// almost none when it is fast (no visible lag). Allocation-free.
/// </summary>
public struct OneEuroFilter
{
    private float _minCutoff, _beta, _dCutoff;
    private float _x, _dx;
    private bool _init;

    public OneEuroFilter(float minCutoff, float beta, float dCutoff = 1f)
    {
        _minCutoff = minCutoff; _beta = beta; _dCutoff = dCutoff;
        _x = _dx = 0; _init = false;
    }

    public float Filter(float value, float dtSeconds)
    {
        if (!_init) { _init = true; _x = value; _dx = 0; return value; }
        dtSeconds = MathF.Max(dtSeconds, 1e-4f);
        float dx = (value - _x) / dtSeconds;
        _dx += Alpha(_dCutoff, dtSeconds) * (dx - _dx);
        float cutoff = _minCutoff + _beta * MathF.Abs(_dx);
        _x += Alpha(cutoff, dtSeconds) * (value - _x);
        return _x;
    }

    private static float Alpha(float cutoff, float dt)
    {
        float tau = 1f / (2f * MathF.PI * cutoff);
        return 1f / (1f + tau / dt);
    }
}

/// <summary>
/// Converts raw samples into stored points for the active stroke: stabilizer (One-Euro on position,
/// light smoothing on pressure and tilt), duplicate removal, and capability detection.
/// Runs on the ink thread inside the input handler; a few dozen flops per sample.
/// </summary>
public sealed class InkProcessor
{
    private OneEuroFilter _fx, _fy;
    private float _pressure, _tiltX, _tiltY;
    private ulong _t0, _lastT;
    private bool _active;
    private InkPoint _last;
    private RawSample _lastRaw;
    private bool _sawPressureVariation, _sawTilt, _sawTwist;
    private float _firstPressure;
    private StrokeFlags _baseFlags;
    private readonly List<InkPoint> _points = new(512);

    public IReadOnlyList<InkPoint> Points => _points;
    /// <summary>Allocation-free view of the points written so far (valid until the next Add/Begin).</summary>
    public ReadOnlySpan<InkPoint> PointSpan => CollectionsMarshal.AsSpan(_points);
    public InkPoint Last => _last;
    public int Count => _points.Count;
    public bool IsActive => _active;
    /// <summary>True after <see cref="ReplacePoints"/>: the stroke was snapped to a shape and no longer takes raw samples.</summary>
    public bool Frozen { get; private set; }

    /// <summary>True once the device has shown real (varying) pressure during this stroke.</summary>
    public bool SawPressureVariation => _sawPressureVariation;
    public bool SawTilt => _sawTilt;
    public bool SawTwist => _sawTwist;

    /// <summary>
    /// Flags for the stroke being written. Pressure handling is fixed at <see cref="Begin"/> (so the live
    /// stroke and the committed stroke look identical); tilt and twist switch on as soon as the pen reports
    /// them, which is safe because earlier zero-tilt samples render the same either way.
    /// </summary>
    public StrokeFlags Flags
    {
        get
        {
            var f = _baseFlags;
            if (_sawTilt) f |= StrokeFlags.HasTilt;
            if (_sawTwist) f |= StrokeFlags.HasTwist;
            return f;
        }
    }

    /// <summary>
    /// Decides pressure handling for a new stroke from what is known about the device: a pen that reports
    /// anything other than the Windows "no pressure" value of 0.5 is treated as pressure-capable.
    /// </summary>
    public static StrokeFlags BaseFlagsFor(bool isPen, bool deviceKnownPressureCapable, float firstPressure)
    {
        bool pressure = isPen && (deviceKnownPressureCapable || (firstPressure > 0f && firstPressure != 0.5f));
        return pressure ? StrokeFlags.HasPressure : StrokeFlags.SpeedWidth;
    }

    /// <summary>Starts a stroke. <paramref name="stabilizer"/> 0 = raw, 1 = strongest smoothing.</summary>
    public void Begin(in RawSample s, float stabilizer, StrokeFlags baseFlags)
    {
        stabilizer = Math.Clamp(stabilizer, 0f, 1f);
        // Position is in page units: min cutoff in Hz, beta in 1/(units/s).
        float minCutoff = Lerp(25f, 1.2f, stabilizer);
        float beta = Lerp(0.05f, 0.006f, stabilizer);
        _fx = new OneEuroFilter(minCutoff, beta);
        _fy = new OneEuroFilter(minCutoff, beta);
        _points.Clear();
        Frozen = false;
        _t0 = _lastT = s.TimestampUs;
        _pressure = s.Pressure;
        _tiltX = s.TiltX;
        _tiltY = s.TiltY;
        _firstPressure = s.Pressure;
        _sawPressureVariation = false;
        _sawTilt = s.TiltX != 0 || s.TiltY != 0;
        _sawTwist = s.Twist > 0;
        _active = true;
        _lastRaw = s;
        _baseFlags = baseFlags & (StrokeFlags.HasPressure | StrokeFlags.SpeedWidth);
        var p = new InkPoint(_fx.Filter(s.X, 0), _fy.Filter(s.Y, 0), s.Pressure, s.TiltX, s.TiltY, s.Twist, 0);
        _points.Add(p);
        _last = p;
    }

    /// <summary>Adds a sample; returns false if it was a duplicate and dropped.</summary>
    public bool Add(in RawSample s)
    {
        if (!_active || Frozen) return false;
        if (s.TimestampUs < _lastT) return false; // out-of-order duplicate
        float dt = (s.TimestampUs - _lastT) / 1_000_000f;
        _lastT = s.TimestampUs;
        _lastRaw = s;

        if (!_sawPressureVariation && MathF.Abs(s.Pressure - _firstPressure) > 0.002f) _sawPressureVariation = true;
        if (!_sawTilt && (s.TiltX != 0 || s.TiltY != 0)) _sawTilt = true;
        if (!_sawTwist && s.Twist > 0) _sawTwist = true;

        float x = _fx.Filter(s.X, dt);
        float y = _fy.Filter(s.Y, dt);
        _pressure += (s.Pressure - _pressure) * 0.65f;
        _tiltX += (s.TiltX - _tiltX) * 0.35f;
        _tiltY += (s.TiltY - _tiltY) * 0.35f;

        float dx = x - _last.X, dy = y - _last.Y;
        if (dx * dx + dy * dy < 0.0004f && MathF.Abs(_pressure - _last.Pressure) < 0.004f) return false;

        var p = new InkPoint(x, y, Math.Clamp(_pressure, 0f, 1f), _tiltX, _tiltY, s.Twist, (int)((s.TimestampUs - _t0) / 1000));
        _points.Add(p);
        _last = p;
        return true;
    }

    /// <summary>Ends the stroke, adding the raw pen-up position if the filter lagged behind it.</summary>
    public InkPoint[] End()
    {
        _active = false;
        float dx = _lastRaw.X - _last.X, dy = _lastRaw.Y - _last.Y;
        if (!Frozen && _points.Count > 1 && dx * dx + dy * dy > 0.01f)
        {
            var p = _last;
            p.X = _lastRaw.X; p.Y = _lastRaw.Y;
            p.TimeMs = (int)((_lastRaw.TimestampUs - _t0) / 1000) + 1;
            _points.Add(p);
        }
        return _points.ToArray();
    }

    /// <summary>
    /// Replaces everything written so far (used to snap the stroke to a clean shape). The stroke is frozen: later raw
    /// samples are ignored, but it can be replaced again (a snapped line follows the pen until it lifts).
    /// </summary>
    public void ReplacePoints(ReadOnlySpan<InkPoint> pts)
    {
        if (!_active || pts.Length == 0) return;
        _points.Clear();
        for (int i = 0; i < pts.Length; i++) _points.Add(pts[i]);
        _last = pts[^1];
        Frozen = true;
    }

    public void Cancel()
    {
        _active = false;
        Frozen = false;
        _points.Clear();
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
}
