using System;
using System.Numerics;
using Nib.Core.Model;

namespace Nib.Core.Rendering;

/// <summary>Minimal vector drawing surface for tag symbols (screen and PDF draw identical glyphs).</summary>
public interface IShapeCanvas
{
    void FillPolygon(ReadOnlySpan<Vector2> points, uint color);
    void FillCircle(float cx, float cy, float r, uint color);
    void StrokePolyline(ReadOnlySpan<Vector2> points, bool closed, float width, uint color);
}

/// <summary>Draws tag symbols as simple vector shapes in a square of side <c>size</c> at (x, y).</summary>
public static class TagGlyphs
{
    public static void Draw(TagDefinition tag, bool isChecked, float x, float y, float size, IShapeCanvas c)
    {
        float s = size;
        Span<Vector2> buf = stackalloc Vector2[12];
        switch (tag.Symbol)
        {
            case TagSymbol.Checkbox:
            {
                float m = s * 0.12f;
                buf[0] = new(x + m, y + m); buf[1] = new(x + s - m, y + m); buf[2] = new(x + s - m, y + s - m); buf[3] = new(x + m, y + s - m);
                if (isChecked) c.FillPolygon(buf[..4], tag.Color);
                c.StrokePolyline(buf[..4], true, s * 0.09f, tag.Color);
                if (isChecked)
                {
                    buf[0] = new(x + s * 0.27f, y + s * 0.52f); buf[1] = new(x + s * 0.44f, y + s * 0.69f); buf[2] = new(x + s * 0.74f, y + s * 0.33f);
                    c.StrokePolyline(buf[..3], false, s * 0.12f, 0xFFFFFFFF);
                }
                break;
            }
            case TagSymbol.Star:
            {
                float cx = x + s / 2, cy = y + s * 0.53f, ro = s * 0.48f, ri = ro * 0.42f;
                for (int i = 0; i < 10; i++)
                {
                    float a = -MathF.PI / 2 + i * MathF.PI / 5;
                    float r = (i & 1) == 0 ? ro : ri;
                    buf[i] = new(cx + r * MathF.Cos(a), cy + r * MathF.Sin(a));
                }
                c.FillPolygon(buf[..10], tag.Color);
                break;
            }
            case TagSymbol.Question:
            case TagSymbol.Exclamation:
            {
                c.FillCircle(x + s / 2, y + s / 2, s * 0.46f, tag.Color);
                if (tag.Symbol == TagSymbol.Question)
                {
                    buf[0] = new(x + s * 0.36f, y + s * 0.38f); buf[1] = new(x + s * 0.42f, y + s * 0.26f); buf[2] = new(x + s * 0.55f, y + s * 0.24f);
                    buf[3] = new(x + s * 0.64f, y + s * 0.33f); buf[4] = new(x + s * 0.60f, y + s * 0.45f); buf[5] = new(x + s * 0.50f, y + s * 0.52f);
                    buf[6] = new(x + s * 0.50f, y + s * 0.62f);
                    c.StrokePolyline(buf[..7], false, s * 0.1f, 0xFFFFFFFF);
                }
                else
                {
                    buf[0] = new(x + s * 0.5f, y + s * 0.22f); buf[1] = new(x + s * 0.5f, y + s * 0.6f);
                    c.StrokePolyline(buf[..2], false, s * 0.12f, 0xFFFFFFFF);
                }
                c.FillCircle(x + s * 0.5f, y + s * 0.75f, s * 0.065f, 0xFFFFFFFF);
                break;
            }
            case TagSymbol.Flag:
            {
                buf[0] = new(x + s * 0.22f, y + s * 0.1f); buf[1] = new(x + s * 0.22f, y + s * 0.92f);
                c.StrokePolyline(buf[..2], false, s * 0.09f, tag.Color);
                buf[0] = new(x + s * 0.26f, y + s * 0.12f); buf[1] = new(x + s * 0.85f, y + s * 0.3f); buf[2] = new(x + s * 0.26f, y + s * 0.52f);
                c.FillPolygon(buf[..3], tag.Color);
                break;
            }
            case TagSymbol.Bookmark:
            {
                buf[0] = new(x + s * 0.24f, y + s * 0.08f); buf[1] = new(x + s * 0.76f, y + s * 0.08f); buf[2] = new(x + s * 0.76f, y + s * 0.92f);
                buf[3] = new(x + s * 0.5f, y + s * 0.7f); buf[4] = new(x + s * 0.24f, y + s * 0.92f);
                c.FillPolygon(buf[..5], tag.Color);
                break;
            }
            case TagSymbol.Lightbulb:
            {
                c.FillCircle(x + s / 2, y + s * 0.4f, s * 0.3f, tag.Color);
                buf[0] = new(x + s * 0.38f, y + s * 0.62f); buf[1] = new(x + s * 0.62f, y + s * 0.62f); buf[2] = new(x + s * 0.6f, y + s * 0.88f); buf[3] = new(x + s * 0.4f, y + s * 0.88f);
                c.FillPolygon(buf[..4], tag.Color);
                break;
            }
            default:
                c.FillCircle(x + s / 2, y + s / 2, s * 0.22f, tag.Color);
                break;
        }
    }
}
