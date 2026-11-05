using System.Runtime.InteropServices;

namespace Nib.Core.Ink;

/// <summary>
/// One stored stroke sample. Positions are page units; pressure and tilt are the raw
/// (post-smoothing, pre-curve) device values so strokes re-render identically after reopening
/// and in vector export, independent of later changes to the user's pen settings.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct InkPoint
{
    public float X;
    public float Y;
    /// <summary>Normalized hardware pressure 0..1 (full device resolution, e.g. 4096 or 8192 levels).</summary>
    public float Pressure;
    /// <summary>Degrees, -90..90, as reported by PointerPointProperties.XTilt.</summary>
    public float TiltX;
    /// <summary>Degrees, -90..90, as reported by PointerPointProperties.YTilt.</summary>
    public float TiltY;
    /// <summary>Barrel rotation in degrees 0..360, or negative when the pen does not report it.</summary>
    public float Twist;
    /// <summary>Milliseconds since the first point of the stroke.</summary>
    public int TimeMs;

    public InkPoint(float x, float y, float pressure = 0.5f, float tiltX = 0, float tiltY = 0, float twist = -1, int timeMs = 0)
    {
        X = x; Y = y; Pressure = pressure; TiltX = tiltX; TiltY = tiltY; Twist = twist; TimeMs = timeMs;
    }

    public override readonly string ToString() => $"({X:0.##},{Y:0.##}) p={Pressure:0.###} t=({TiltX:0.#},{TiltY:0.#}) {TimeMs}ms";
}

public enum PenKind : byte
{
    Ballpoint = 0,
    Fountain = 1,
    Pencil = 2,
    Highlighter = 3,
}

[System.Flags]
public enum StrokeFlags : byte
{
    None = 0,
    /// <summary>Points carry real pressure from the digitizer.</summary>
    HasPressure = 1,
    /// <summary>Points carry real tilt.</summary>
    HasTilt = 2,
    /// <summary>Points carry barrel rotation (twist).</summary>
    HasTwist = 4,
    /// <summary>Device had no pressure (touch, mouse, basic pen): width follows speed instead.</summary>
    SpeedWidth = 8,
}

public static class PenKindExtensions
{
    /// <summary>Inks that behave like translucent media: drawn with a "darken" (min) blend so overlaps never stack.</summary>
    public static bool IsMultiplyInk(this PenKind k) => k is PenKind.Pencil or PenKind.Highlighter;
}
