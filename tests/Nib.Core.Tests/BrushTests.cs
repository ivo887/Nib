using System;
using System.Linq;
using Nib.Core.Ink;
using Nib.Core.Model;
using Xunit;

namespace Nib.Core.Tests;

public class BrushTests
{
    private static readonly PenKind[] NewKinds = { PenKind.Brush, PenKind.Calligraphy, PenKind.Marker, PenKind.Watercolor };

    private static Footprint Foot(PenKind kind, float pressure = 0.7f, float tiltX = 0, float tiltY = 0, float twist = -1)
    {
        var (width, dyn) = PenPreset.DefaultsFor(kind);
        var flags = StrokeFlags.HasPressure | (tiltX != 0 || tiltY != 0 ? StrokeFlags.HasTilt : 0) | (twist >= 0 ? StrokeFlags.HasTwist : 0);
        NibModel.Compute(kind, width, dyn, flags, new InkPoint(0, 0, pressure, tiltX, tiltY, twist), 0f, out var f);
        return f;
    }

    [Fact]
    public void EveryNewNibHasADefaultWidthAndFeel()
    {
        foreach (var k in NewKinds)
        {
            var (w, d) = PenPreset.DefaultsFor(k);
            Assert.True(w > 1f);
            Assert.True(d.TiltSensitivity > 0.5f);
        }
    }

    [Fact]
    public void LeaningWidensTheBrushAlongTheLean()
    {
        var upright = Foot(PenKind.Brush, tiltX: 0, tiltY: 0);
        var leaning = Foot(PenKind.Brush, tiltX: 55, tiltY: 0);
        Assert.Equal(FootprintShape.Circle, upright.Shape);
        Assert.Equal(FootprintShape.Ellipse, leaning.Shape);
        Assert.True(leaning.Rx > upright.Rx * 1.8f, "a leaning brush lays its bristles flat");
        Assert.True(leaning.Ry < upright.Ry);
    }

    [Fact]
    public void BrushWidthFollowsPressure()
    {
        Assert.True(Foot(PenKind.Brush, pressure: 1f).Rx > Foot(PenKind.Brush, pressure: 0.1f).Rx * 3f);
    }

    [Fact]
    public void LeaningDirectionSetsTheFootprintAngle()
    {
        var right = Foot(PenKind.Brush, tiltX: 50, tiltY: 0);
        var down = Foot(PenKind.Brush, tiltX: 0, tiltY: 50);
        Assert.NotEqual(right.Angle, down.Angle, 2);
    }

    [Fact]
    public void CalligraphyIsACrispFlatChiselThatTurnsWithTiltAndTwist()
    {
        var flat = Foot(PenKind.Calligraphy);
        Assert.Equal(FootprintShape.Rect, flat.Shape);
        Assert.True(flat.Ry < flat.Rx * 0.2f);
        Assert.Equal(1f, flat.Strength);

        Assert.NotEqual(flat.Angle, Foot(PenKind.Calligraphy, tiltX: 50).Angle, 2);
        Assert.NotEqual(flat.Angle, Foot(PenKind.Calligraphy, twist: 90).Angle, 2);
    }

    [Fact]
    public void MarkerAndWatercolorAreTranslucentAndLeaningBroadensThem()
    {
        foreach (var k in new[] { PenKind.Marker, PenKind.Watercolor })
        {
            Assert.True(k.IsMultiplyInk());
            var upright = Foot(k);
            var leaning = Foot(k, tiltX: 55);
            Assert.InRange(upright.Strength, 0.05f, 0.95f);
            Assert.True(leaning.Rx > upright.Rx);
        }
        Assert.True(Foot(PenKind.Watercolor, tiltX: 55).Strength < Foot(PenKind.Watercolor).Strength, "a leaning wash is thinner");
    }

    [Fact]
    public void WatercolorPressureLoadsPigment()
    {
        Assert.True(Foot(PenKind.Watercolor, pressure: 1f).Strength > Foot(PenKind.Watercolor, pressure: 0.1f).Strength + 0.2f);
    }

    [Fact]
    public void WashesAndHighlightersGoUnderTheInk()
    {
        Assert.True(PenKind.Highlighter.IsBackgroundInk());
        Assert.True(PenKind.Watercolor.IsBackgroundInk());
        Assert.False(PenKind.Marker.IsBackgroundInk());
        Assert.False(PenKind.Brush.IsBackgroundInk());
    }

    [Fact]
    public void FootprintsNeverExceedTheDeclaredExtent()
    {
        foreach (var k in NewKinds)
        {
            var (width, _) = PenPreset.DefaultsFor(k);
            float max = NibModel.MaxExtent(k, width);
            foreach (var tilt in new[] { 0f, 30f, 60f, 85f })
            {
                var f = Foot(k, pressure: 1f, tiltX: tilt, tiltY: tilt / 2);
                Assert.True(MathF.Max(f.Rx, f.Ry) <= max + 0.01f, $"{k} at tilt {tilt}: {MathF.Max(f.Rx, f.Ry)} > {max}");
            }
        }
    }

    [Fact]
    public void EveryNewNibBuildsGeometryAtEveryTilt()
    {
        var builder = new StrokeGeometryBuilder();
        foreach (var k in NewKinds)
        foreach (var tilt in new[] { 0f, 40f, 80f })
        {
            var (width, dyn) = PenPreset.DefaultsFor(k);
            var pts = Enumerable.Range(0, 60).Select(i => new InkPoint(10 + i * 2.5f, 50 + MathF.Sin(i / 6f) * 12f, 0.2f + 0.8f * (i % 20) / 20f, tilt, tilt / 3, -1, i * 8)).ToArray();
            var sink = new CoverageSink();
            builder.Build(k, 0xFF224488, width, dyn, StrokeFlags.HasPressure | StrokeFlags.HasTilt, pts, sink);
            Assert.True(sink.FigureCount > 0, $"{k} tilt {tilt}");
            Assert.True(sink.Winding(pts[30].X, pts[30].Y) > 0, $"{k} covers its own path at tilt {tilt}");
            if (k.IsMultiplyInk()) Assert.All(sink.RunInfo, r => Assert.True(r.Multiply));
        }
    }

    [Fact]
    public void NibKindsSurviveTheStrokeCodec()
    {
        foreach (var k in NewKinds)
        {
            var (width, dyn) = PenPreset.DefaultsFor(k);
            var pts = new[] { new InkPoint(1, 2, 0.5f, 10, 20, 30, 0), new InkPoint(5, 6, 0.7f, 12, 18, 31, 16) };
            var stroke = new Stroke(1, 1, k, 0xFF112233, width, dyn, StrokeFlags.HasPressure | StrokeFlags.HasTilt, pts);
            var bytes = Nib.Core.Serialization.InkCodec.Encode(stroke);
            var back = Nib.Core.Serialization.InkCodec.Decode(1, 1, bytes);
            Assert.Equal(k, back.Kind);
        }
    }

    [Fact]
    public void TiltFilterHonoursOnOffDeadZoneAndFlipping()
    {
        Assert.Equal((0f, 0f), new TiltSettings { Enabled = false }.Apply(40, 20));

        var t = new TiltSettings { DeadZoneDeg = 6 };
        Assert.Equal((0f, 0f), t.Apply(3, 2));                    // inside the dead zone: upright
        var (x, y) = t.Apply(45, 0);
        Assert.InRange(x, 40f, 45f);                              // still close to the real lean once outside it
        var (fx, _) = new TiltSettings { DeadZoneDeg = 0, InvertX = true }.Apply(30, 10);
        Assert.Equal(-30f, fx, 3);
        var (_, fy) = new TiltSettings { DeadZoneDeg = 0, InvertY = true }.Apply(30, 10);
        Assert.Equal(-10f, fy, 3);
        var (mx, _) = new TiltSettings { DeadZoneDeg = 10 }.Apply(90, 0);
        Assert.Equal(90f, mx, 2);                                 // full range stays reachable
    }
}
