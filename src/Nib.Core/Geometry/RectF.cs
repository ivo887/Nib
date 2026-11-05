using System;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Nib.Core.Geometry;

/// <summary>Axis-aligned rectangle in page units (1/96 inch). Immutable, allocation-free.</summary>
public readonly struct RectF : IEquatable<RectF>
{
    public readonly float Left;
    public readonly float Top;
    public readonly float Right;
    public readonly float Bottom;

    public RectF(float left, float top, float right, float bottom)
    {
        Left = left; Top = top; Right = right; Bottom = bottom;
    }

    /// <summary>An empty rectangle that is the identity for <see cref="Union(RectF)"/>.</summary>
    public static readonly RectF Empty = new(float.PositiveInfinity, float.PositiveInfinity, float.NegativeInfinity, float.NegativeInfinity);

    public static RectF FromXYWH(float x, float y, float w, float h) => new(x, y, x + w, y + h);

    public static RectF FromCenter(float cx, float cy, float halfW, float halfH) => new(cx - halfW, cy - halfH, cx + halfW, cy + halfH);

    public bool IsEmpty
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => !(Right >= Left && Bottom >= Top);
    }

    public float Width => IsEmpty ? 0 : Right - Left;
    public float Height => IsEmpty ? 0 : Bottom - Top;
    public float CenterX => (Left + Right) * 0.5f;
    public float CenterY => (Top + Bottom) * 0.5f;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public RectF Union(RectF o) => new(MathF.Min(Left, o.Left), MathF.Min(Top, o.Top), MathF.Max(Right, o.Right), MathF.Max(Bottom, o.Bottom));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public RectF Union(float x, float y) => new(MathF.Min(Left, x), MathF.Min(Top, y), MathF.Max(Right, x), MathF.Max(Bottom, y));

    public RectF Inflate(float d) => IsEmpty ? this : new(Left - d, Top - d, Right + d, Bottom + d);

    public RectF Offset(float dx, float dy) => IsEmpty ? this : new(Left + dx, Top + dy, Right + dx, Bottom + dy);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Intersects(RectF o) => Left <= o.Right && o.Left <= Right && Top <= o.Bottom && o.Top <= Bottom;

    public bool Contains(float x, float y) => x >= Left && x <= Right && y >= Top && y <= Bottom;

    public bool Contains(RectF o) => !o.IsEmpty && o.Left >= Left && o.Right <= Right && o.Top >= Top && o.Bottom <= Bottom;

    public RectF Intersect(RectF o)
    {
        var r = new RectF(MathF.Max(Left, o.Left), MathF.Max(Top, o.Top), MathF.Min(Right, o.Right), MathF.Min(Bottom, o.Bottom));
        return r.IsEmpty ? Empty : r;
    }

    /// <summary>Bounding box of this rectangle after an affine transform.</summary>
    public RectF Transform(Matrix3x2 m)
    {
        if (IsEmpty) return this;
        var a = Vector2.Transform(new Vector2(Left, Top), m);
        var b = Vector2.Transform(new Vector2(Right, Top), m);
        var c = Vector2.Transform(new Vector2(Left, Bottom), m);
        var d = Vector2.Transform(new Vector2(Right, Bottom), m);
        return new RectF(
            MathF.Min(MathF.Min(a.X, b.X), MathF.Min(c.X, d.X)),
            MathF.Min(MathF.Min(a.Y, b.Y), MathF.Min(c.Y, d.Y)),
            MathF.Max(MathF.Max(a.X, b.X), MathF.Max(c.X, d.X)),
            MathF.Max(MathF.Max(a.Y, b.Y), MathF.Max(c.Y, d.Y)));
    }

    public bool Equals(RectF o) => Left == o.Left && Top == o.Top && Right == o.Right && Bottom == o.Bottom;
    public override bool Equals(object? obj) => obj is RectF r && Equals(r);
    public override int GetHashCode() => HashCode.Combine(Left, Top, Right, Bottom);
    public static bool operator ==(RectF a, RectF b) => a.Equals(b);
    public static bool operator !=(RectF a, RectF b) => !a.Equals(b);
    public override string ToString() => IsEmpty ? "Empty" : $"[{Left:0.##},{Top:0.##} – {Right:0.##},{Bottom:0.##}]";
}

/// <summary>Unit conversions. Page units are device-independent pixels at 100% zoom: 96 per inch.</summary>
public static class PageUnits
{
    public const float PerInch = 96f;
    public const float PerMillimeter = 96f / 25.4f;
    public const float PdfPointsPerUnit = 72f / 96f;

    public static float FromMillimeters(float mm) => mm * PerMillimeter;
    public static float ToMillimeters(float units) => units / PerMillimeter;
    public static float FromPoints(float pt) => pt * 96f / 72f;
}
