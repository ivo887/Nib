using System;

namespace Nib.Core.Input;

public interface ITouchGestureHandler
{
    void OnPan(float dx, float dy);
    void OnZoom(float factor, float centerX, float centerY);
    /// <summary>Last finger lifted while panning; velocity in DIPs/second for inertia.</summary>
    void OnPanEnd(float velocityX, float velocityY);
    /// <summary>A quick tap with several fingers: 2 = undo, 3 = redo.</summary>
    void OnMultiFingerTap(int fingers);
}

/// <summary>
/// Touch gesture recognizer: two-finger pan + pinch zoom, optional one-finger pan, two/three-finger taps.
/// Small movement slop keeps taps clean, so a two-finger tap never nudges the page. Allocation-free.
/// </summary>
public sealed class TouchGestureEngine
{
    private const int MaxContacts = 10;
    private const float Slop = 8f;           // DIPs before a pan/zoom starts
    private const float TapMaxMove = 14f;     // DIPs
    private const long TapMaxDurationMs = 320;
    private const long TapMaxSpreadMs = 180;  // fingers must land almost together

    private readonly uint[] _ids = new uint[MaxContacts];
    private readonly float[] _x = new float[MaxContacts], _y = new float[MaxContacts];
    private readonly float[] _sx = new float[MaxContacts], _sy = new float[MaxContacts];
    private int _count;

    private bool _manipulating;
    private int _sessionMaxContacts;
    private long _sessionStart, _lastDown;
    private float _sessionMaxMove;
    private bool _tapPossible;

    private float _lastCx, _lastCy, _lastDist;

    // Velocity estimation ring buffer of centroid samples.
    private readonly long[] _vt = new long[8];
    private readonly float[] _vx = new float[8], _vy = new float[8];
    private int _vHead, _vCount;

    private readonly ITouchGestureHandler _handler;

    /// <summary>When true a single finger pans (used when "touch draws" is off).</summary>
    public bool OneFingerPans { get; set; } = true;
    public int ContactCount => _count;
    public bool IsManipulating => _manipulating;

    public TouchGestureEngine(ITouchGestureHandler handler) => _handler = handler;

    public void Down(uint id, float x, float y, long timeMs)
    {
        if (_count == 0)
        {
            _sessionStart = timeMs;
            _sessionMaxContacts = 0;
            _sessionMaxMove = 0;
            _tapPossible = true;
            _manipulating = false;
            _vCount = 0;
        }
        if (_count >= MaxContacts || IndexOf(id) >= 0) return;
        if (_count > 0 && timeMs - _lastDown > TapMaxSpreadMs) _tapPossible = false;
        _lastDown = timeMs;
        _ids[_count] = id;
        _x[_count] = _sx[_count] = x;
        _y[_count] = _sy[_count] = y;
        _count++;
        if (_count > _sessionMaxContacts) _sessionMaxContacts = _count;
        ResetReference();
    }

    public void Move(uint id, float x, float y, long timeMs)
    {
        int i = IndexOf(id);
        if (i < 0) return;
        _x[i] = x;
        _y[i] = y;
        float mdx = x - _sx[i], mdy = y - _sy[i];
        float moved = MathF.Sqrt(mdx * mdx + mdy * mdy);
        if (moved > _sessionMaxMove) _sessionMaxMove = moved;
        if (_sessionMaxMove > TapMaxMove) _tapPossible = false;

        bool canManipulate = _count >= 2 || (_count == 1 && OneFingerPans);
        if (!canManipulate) return;

        Centroid(out float cx, out float cy, out float dist);
        if (!_manipulating)
        {
            float pan = MathF.Sqrt((cx - _lastCx) * (cx - _lastCx) + (cy - _lastCy) * (cy - _lastCy));
            float pinch = _count >= 2 ? MathF.Abs(dist - _lastDist) : 0;
            if (pan < Slop && pinch < Slop) return;
            _manipulating = true;
            _tapPossible = false;
            // Start from the current position: no jump by the slop distance.
            _lastCx = cx; _lastCy = cy; _lastDist = dist;
            RecordVelocity(timeMs, cx, cy);
            return;
        }

        float dx = cx - _lastCx, dy = cy - _lastCy;
        if (dx != 0 || dy != 0) _handler.OnPan(dx, dy);
        if (_count >= 2 && _lastDist > 1f && dist > 1f)
        {
            float factor = dist / _lastDist;
            if (MathF.Abs(factor - 1f) > 1e-4f) _handler.OnZoom(factor, cx, cy);
        }
        _lastCx = cx; _lastCy = cy; _lastDist = dist;
        RecordVelocity(timeMs, cx, cy);
    }

    public void Up(uint id, float x, float y, long timeMs)
    {
        int i = IndexOf(id);
        if (i < 0) return;
        Move(id, x, y, timeMs);
        RemoveAt(i);
        if (_count == 0)
        {
            if (_manipulating)
            {
                EstimateVelocity(timeMs, out float vx, out float vy);
                _handler.OnPanEnd(vx, vy);
            }
            else if (_tapPossible && _sessionMaxContacts >= 2 && timeMs - _sessionStart <= TapMaxDurationMs)
            {
                _handler.OnMultiFingerTap(_sessionMaxContacts);
            }
            _manipulating = false;
            return;
        }
        if (_manipulating && _count == 1 && !OneFingerPans)
        {
            // Lifting one of two fingers ends the gesture when one finger can't pan.
            EstimateVelocity(timeMs, out float vx, out float vy);
            _handler.OnPanEnd(vx, vy);
            _manipulating = false;
        }
        ResetReference();
    }

    /// <summary>Cancels all contacts (e.g. the pen came into range: palm rejection).</summary>
    public void CancelAll()
    {
        _count = 0;
        _manipulating = false;
        _tapPossible = false;
    }

    private void ResetReference()
    {
        Centroid(out _lastCx, out _lastCy, out _lastDist);
    }

    private void Centroid(out float cx, out float cy, out float dist)
    {
        cx = cy = dist = 0;
        if (_count == 0) return;
        if (_count == 1) { cx = _x[0]; cy = _y[0]; return; }
        // Two-finger gestures use the first two contacts; extra fingers don't make zoom jumpy.
        cx = (_x[0] + _x[1]) * 0.5f;
        cy = (_y[0] + _y[1]) * 0.5f;
        float dx = _x[1] - _x[0], dy = _y[1] - _y[0];
        dist = MathF.Sqrt(dx * dx + dy * dy);
    }

    private void RecordVelocity(long t, float x, float y)
    {
        _vt[_vHead] = t; _vx[_vHead] = x; _vy[_vHead] = y;
        _vHead = (_vHead + 1) % _vt.Length;
        if (_vCount < _vt.Length) _vCount++;
    }

    private void EstimateVelocity(long now, out float vx, out float vy)
    {
        vx = vy = 0;
        if (_vCount < 2) return;
        int newest = (_vHead - 1 + _vt.Length) % _vt.Length;
        // Oldest sample within the last 100 ms.
        int oldest = newest;
        for (int k = 1; k < _vCount; k++)
        {
            int idx = (newest - k + _vt.Length) % _vt.Length;
            if (_vt[newest] - _vt[idx] > 100) break;
            oldest = idx;
        }
        if (now - _vt[newest] > 80) return; // finger rested before lifting: no fling
        long dt = _vt[newest] - _vt[oldest];
        if (dt <= 0) return;
        vx = (_vx[newest] - _vx[oldest]) * 1000f / dt;
        vy = (_vy[newest] - _vy[oldest]) * 1000f / dt;
    }

    private int IndexOf(uint id)
    {
        for (int i = 0; i < _count; i++) if (_ids[i] == id) return i;
        return -1;
    }

    private void RemoveAt(int i)
    {
        for (int k = i; k < _count - 1; k++)
        {
            _ids[k] = _ids[k + 1]; _x[k] = _x[k + 1]; _y[k] = _y[k + 1]; _sx[k] = _sx[k + 1]; _sy[k] = _sy[k + 1];
        }
        _count--;
    }
}

/// <summary>
/// Ignores touch while the pen is near or touching the screen, and for a short grace period after it
/// leaves (the palm usually lifts after the pen). Large contacts are treated as palms regardless.
/// </summary>
public sealed class PalmRejection
{
    public const long GraceMs = 450;
    /// <summary>Contacts wider or taller than this (DIPs) are palms.</summary>
    public const float PalmSize = 34f;

    private bool _penInRange;
    private long _penLastSeen = long.MinValue / 2;

    public bool PenInRange => _penInRange;

    public void PenSeen(long nowMs) { _penInRange = true; _penLastSeen = nowMs; }
    public void PenLeft(long nowMs) { _penInRange = false; _penLastSeen = nowMs; }

    public bool ShouldRejectTouch(long nowMs, float contactWidth, float contactHeight)
    {
        if (_penInRange) return true;
        if (nowMs - _penLastSeen < GraceMs) return true;
        return contactWidth > PalmSize || contactHeight > PalmSize;
    }
}
