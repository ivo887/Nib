using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Nib.Core.Ink;
using Xunit;

namespace Nib.Core.Tests;

public class ShapeTests
{
    private static readonly Random Rng = new(12345);

    private static float Jitter(float amount) => (float)(Rng.NextDouble() * 2 - 1) * amount;

    /// <summary>Samples a polyline densely with hand-like wobble.</summary>
    private static InkPoint[] Draw(IEnumerable<Vector2> path, float wobble = 1.2f, float spacing = 3f)
    {
        var pts = new List<InkPoint>();
        Vector2? prev = null;
        int t = 0;
        foreach (var v in path)
        {
            if (prev is { } p)
            {
                float len = Vector2.Distance(p, v);
                int steps = Math.Max(1, (int)(len / spacing));
                for (int i = 1; i <= steps; i++)
                {
                    var q = Vector2.Lerp(p, v, i / (float)steps);
                    pts.Add(new InkPoint(q.X + Jitter(wobble), q.Y + Jitter(wobble), 0.5f, 0, 0, -1, t += 8));
                }
            }
            else pts.Add(new InkPoint(v.X, v.Y, 0.5f, 0, 0, -1, 0));
            prev = v;
        }
        return pts.ToArray();
    }

    private static IEnumerable<Vector2> Ellipse(Vector2 c, float a, float b, float rotation = 0, float turns = 1.04f, float start = 0.3f)
    {
        int n = 140;
        for (int i = 0; i <= n; i++)
        {
            float t = start + i / (float)n * MathF.Tau * turns;
            float u = a * MathF.Cos(t), v = b * MathF.Sin(t);
            yield return new Vector2(c.X + u * MathF.Cos(rotation) - v * MathF.Sin(rotation), c.Y + u * MathF.Sin(rotation) + v * MathF.Cos(rotation));
        }
    }

    [Fact]
    public void WobblyHorizontalLineBecomesAnExactHorizontalLine()
    {
        var s = Draw(new[] { new Vector2(100, 200), new Vector2(420, 209) }); // about 1.6 degrees off
        var r = ShapeRecognizer.Recognize(s);
        Assert.Equal(ShapeKind.Line, r.Kind);
        Assert.Equal(r.Vertices[0].Y, r.Vertices[1].Y, 3);
    }

    [Fact]
    public void NearVerticalAndFortyFiveDegreeLinesSnapToExactAngles()
    {
        var v = ShapeRecognizer.Recognize(Draw(new[] { new Vector2(300, 100), new Vector2(309, 380) }));
        Assert.Equal(ShapeKind.Line, v.Kind);
        Assert.Equal(v.Vertices[0].X, v.Vertices[1].X, 3);

        var d = ShapeRecognizer.Recognize(Draw(new[] { new Vector2(100, 100), new Vector2(330, 322) }));
        Assert.Equal(ShapeKind.Line, d.Kind);
        Assert.Equal(d.Vertices[1].X - d.Vertices[0].X, d.Vertices[1].Y - d.Vertices[0].Y, 2);
    }

    [Fact]
    public void ALineAtAnOddAngleIsStraightenedButKeepsItsAngle()
    {
        var a = new Vector2(100, 100); var b = new Vector2(400, 220); // about 21.8 degrees
        var r = ShapeRecognizer.Recognize(Draw(new[] { a, b }));
        Assert.Equal(ShapeKind.Line, r.Kind);
        var d = r.Vertices[1] - r.Vertices[0];
        float angle = MathF.Atan2(d.Y, d.X) * 180f / MathF.PI;
        Assert.InRange(angle, 19f, 24f);
    }

    [Fact]
    public void AngleSnappingCanBeSwitchedOff()
    {
        var r = ShapeRecognizer.Recognize(Draw(new[] { new Vector2(100, 200), new Vector2(420, 212) }), snapAngles: false);
        Assert.Equal(ShapeKind.Line, r.Kind);
        Assert.NotEqual(r.Vertices[0].Y, r.Vertices[1].Y, 1);
    }

    [Fact]
    public void RoundLoopBecomesACircle()
    {
        var r = ShapeRecognizer.Recognize(Draw(Ellipse(new Vector2(300, 300), 90, 88)));
        Assert.Equal(ShapeKind.Circle, r.Kind);
        var c = new Vector2(r.Vertices.Average(v => v.X), r.Vertices.Average(v => v.Y));
        Assert.InRange(c.X, 290, 310);
        Assert.InRange(c.Y, 290, 310);
        Assert.All(r.Vertices, v => Assert.InRange(Vector2.Distance(c, v), 85, 95));
    }

    [Fact]
    public void FlatLoopBecomesAnEllipse()
    {
        var r = ShapeRecognizer.Recognize(Draw(Ellipse(new Vector2(300, 300), 140, 60)));
        Assert.Equal(ShapeKind.Ellipse, r.Kind);
        float w = r.Vertices.Max(v => v.X) - r.Vertices.Min(v => v.X);
        float h = r.Vertices.Max(v => v.Y) - r.Vertices.Min(v => v.Y);
        Assert.InRange(w, 270, 290);
        Assert.InRange(h, 110, 130);
    }

    [Fact]
    public void FourCornersBecomeARectangle()
    {
        var corners = new[] { new Vector2(100, 100), new Vector2(380, 104), new Vector2(378, 260), new Vector2(98, 256), new Vector2(102, 98) };
        var r = ShapeRecognizer.Recognize(Draw(corners));
        Assert.Equal(ShapeKind.Rectangle, r.Kind);
        Assert.Equal(4, r.Vertices.Length);
        // Snapped to the page axes: every edge is exactly horizontal or vertical.
        for (int i = 0; i < 4; i++)
        {
            var e = r.Vertices[(i + 1) % 4] - r.Vertices[i];
            Assert.True(MathF.Abs(e.X) < 0.01f || MathF.Abs(e.Y) < 0.01f);
        }
    }

    [Fact]
    public void EqualSidesBecomeASquare()
    {
        var c = new[] { new Vector2(100, 100), new Vector2(300, 103), new Vector2(298, 301), new Vector2(99, 299), new Vector2(101, 99) };
        Assert.Equal(ShapeKind.Square, ShapeRecognizer.Recognize(Draw(c)).Kind);
    }

    [Fact]
    public void ThreeCornersBecomeATriangle()
    {
        var t = new[] { new Vector2(200, 100), new Vector2(340, 330), new Vector2(60, 326), new Vector2(202, 98) };
        var r = ShapeRecognizer.Recognize(Draw(t));
        Assert.Equal(ShapeKind.Triangle, r.Kind);
        Assert.Equal(3, r.Vertices.Length);
    }

    [Fact]
    public void ScribblesDotsAndSquigglesAreLeftAlone()
    {
        Assert.Equal(ShapeKind.None, ShapeRecognizer.Recognize(Draw(new[] { new Vector2(100, 100), new Vector2(104, 103) })).Kind); // a dot
        var zigzag = new List<Vector2>();
        for (int i = 0; i < 8; i++) zigzag.Add(new Vector2(100 + i * 30, 100 + (i % 2 == 0 ? 0 : 60)));
        Assert.Equal(ShapeKind.None, ShapeRecognizer.Recognize(Draw(zigzag)).Kind);
        var wave = Enumerable.Range(0, 60).Select(i => new Vector2(100 + i * 6, 200 + MathF.Sin(i / 5f) * 50));
        Assert.Equal(ShapeKind.None, ShapeRecognizer.Recognize(Draw(wave)).Kind);
    }

    [Fact]
    public void ALineDrawnOutAndBackIsNotALoop()
    {
        var back = new[] { new Vector2(100, 100), new Vector2(300, 102), new Vector2(102, 103) };
        Assert.Equal(ShapeKind.None, ShapeRecognizer.Recognize(Draw(back)).Kind);
    }

    [Fact]
    public void SnappedShapeKeepsThePenFeelAndIsEvenlySampled()
    {
        var original = Draw(new[] { new Vector2(100, 100), new Vector2(400, 100) }).Select(p => { var q = p; q.Pressure = 0.8f; return q; }).ToArray();
        var shape = ShapeRecognizer.Recognize(original);
        var ink = ShapeRecognizer.ToInkPoints(shape, original);
        Assert.True(ink.Length > 50);
        Assert.All(ink, p => Assert.Equal(0.8f, p.Pressure, 2));
        Assert.Equal(100f, ink[0].X, 1);
        Assert.Equal(shape.Vertices[1].X, ink[^1].X, 1);
        for (int i = 1; i < ink.Length; i++) Assert.True(ink[i].TimeMs >= ink[i - 1].TimeMs);
    }

    [Fact]
    public void ClosedShapesCloseBackOntoTheirStart()
    {
        var corners = new[] { new Vector2(100, 100), new Vector2(380, 104), new Vector2(378, 260), new Vector2(98, 256), new Vector2(102, 98) };
        var original = Draw(corners);
        var ink = ShapeRecognizer.ToInkPoints(ShapeRecognizer.Recognize(original), original);
        Assert.Equal(ink[0].X, ink[^1].X, 1);
        Assert.Equal(ink[0].Y, ink[^1].Y, 1);
    }

    [Fact]
    public void ProcessorFreezesAfterSnappingAndTakesReplacements()
    {
        var proc = new InkProcessor();
        proc.Begin(new RawSample(0, 0, 0.5f, 0, 0, -1, 0), 0f, StrokeFlags.HasPressure);
        proc.Add(new RawSample(5, 0, 0.5f, 0, 0, -1, 10_000));
        proc.ReplacePoints(new[] { new InkPoint(0, 0), new InkPoint(50, 0) });
        Assert.True(proc.Frozen);
        Assert.False(proc.Add(new RawSample(99, 99, 0.5f, 0, 0, -1, 20_000)));
        var end = proc.End();
        Assert.Equal(2, end.Length);
        Assert.Equal(50f, end[^1].X);
    }

    [Fact]
    public void ChangingNibTypeAppliesThatNibsDefaultsUnlessCustomized()
    {
        var ballpoint = PenPreset.DefaultFavorites()[0];
        var fountain = ballpoint.WithKind(PenKind.Fountain);
        Assert.Equal(PenKind.Fountain, fountain.Kind);
        Assert.Equal(3.2f, fountain.Width, 3);
        Assert.Equal(0.25f, fountain.Dynamics.MinWidthRatio, 3);

        var custom = (ballpoint with { Width = 5f }).WithKind(PenKind.Pencil);
        Assert.Equal(5f, custom.Width); // the user's own width survives
        Assert.Equal(0.8f, custom.Dynamics.TiltSensitivity, 3);
        Assert.Equal(ballpoint.Color, custom.Color);
    }
}
