using System;
using System.Linq;
using Nib.Core.Ink;
using Nib.Core.Rendering;
using Xunit;

namespace Nib.Core.Tests;

public class PressureCurveTests
{
    [Fact]
    public void Presets_are_monotone_and_hit_endpoints()
    {
        foreach (var c in new[] { PressureCurve.Linear, PressureCurve.Soft, PressureCurve.Firm })
        {
            Assert.Equal(0f, c.Evaluate(0f), 3);
            Assert.Equal(1f, c.Evaluate(1f), 3);
            float prev = -1;
            for (int i = 0; i <= 4096; i++)
            {
                float v = c.Evaluate(i / 4096f);
                Assert.True(v >= prev - 1e-6f, $"{c.Kind} not monotone at {i}");
                prev = v;
            }
        }
    }

    [Fact]
    public void Soft_gives_more_at_light_pressure_and_firm_less()
    {
        Assert.True(PressureCurve.Soft.Evaluate(0.25f) > PressureCurve.Linear.Evaluate(0.25f) + 0.1f);
        Assert.True(PressureCurve.Firm.Evaluate(0.25f) < PressureCurve.Linear.Evaluate(0.25f) - 0.05f);
    }

    [Fact]
    public void Calibration_maps_light_writer_range_to_full_output()
    {
        // A light writer never presses past 0.4: calibration makes 0.4 produce full width.
        var c = PressureCurve.Linear.WithCalibration(0.02f, 0.4f);
        Assert.Equal(1f, c.Evaluate(0.4f), 2);
        Assert.Equal(0.5f, c.Evaluate(0.21f), 2);
        Assert.Equal(0f, c.Evaluate(0.01f), 3);
    }

    [Fact]
    public void Full_4096_level_resolution_is_preserved()
    {
        // Adjacent hardware levels must map to distinct outputs on the linear curve.
        var c = PressureCurve.Linear;
        int distinct = Enumerable.Range(0, 4096).Select(i => c.Evaluate(i / 4095f)).Distinct().Count();
        Assert.True(distinct > 4000, $"only {distinct} distinct outputs");
    }

    [Fact]
    public void Custom_curve_rejects_non_increasing_points()
    {
        Assert.Throws<ArgumentException>(() => new PressureCurve(PressureCurveKind.Custom, new[] { (0f, 0f), (0.5f, 0.5f), (0.5f, 0.7f), (1f, 1f) }));
    }
}

public class NibModelTests
{
    private static Footprint Fp(PenKind kind, float pressure, float tiltX = 0, float tiltY = 0, StrokeFlags flags = StrokeFlags.HasPressure | StrokeFlags.HasTilt, PenDynamics? dyn = null)
    {
        var p = new InkPoint(0, 0, pressure, tiltX, tiltY);
        NibModel.Compute(kind, 4f, dyn ?? PenDynamics.Default, flags, p, 0, out var f);
        return f;
    }

    [Fact]
    public void Ballpoint_width_follows_pressure_between_min_and_max()
    {
        var light = Fp(PenKind.Ballpoint, 0f);
        var hard = Fp(PenKind.Ballpoint, 1f);
        Assert.Equal(4f * 0.5f * PenDynamics.Default.MinWidthRatio, light.Rx, 3);
        Assert.Equal(2f, hard.Rx, 3);
        Assert.Equal(1f, light.Strength);
    }

    [Fact]
    public void Pressure_off_gives_constant_width()
    {
        var dyn = new PenDynamics { PressureEnabled = false };
        Assert.Equal(Fp(PenKind.Ballpoint, 0.1f, dyn: dyn).Rx, Fp(PenKind.Ballpoint, 0.9f, dyn: dyn).Rx);
    }

    [Fact]
    public void Pencil_tilt_widens_and_lightens()
    {
        var upright = Fp(PenKind.Pencil, 0.7f);
        var tilted = Fp(PenKind.Pencil, 0.7f, tiltX: 50);
        Assert.True(tilted.Rx > upright.Rx * 1.8f, "tilted pencil should be much wider along the lean");
        Assert.True(tilted.Strength < upright.Strength * 0.7f, "tilted pencil should be lighter");
        Assert.Equal(0f, tilted.Angle, 3); // leaning toward +X
    }

    [Fact]
    public void Pencil_pressure_controls_opacity()
    {
        Assert.True(Fp(PenKind.Pencil, 1f).Strength > Fp(PenKind.Pencil, 0.1f).Strength + 0.4f);
    }

    [Fact]
    public void Fountain_nib_angle_follows_tilt()
    {
        var a = Fp(PenKind.Fountain, 0.8f, tiltX: 45, tiltY: 0);   // lean toward +X
        var b = Fp(PenKind.Fountain, 0.8f, tiltX: 0, tiltY: 45);   // lean toward +Y
        Assert.Equal(FootprintShape.Ellipse, a.Shape);
        float d = MathF.Abs(NibModel.LerpAngle(a.Angle, b.Angle, 1f) - a.Angle);
        Assert.True(d > 0.6f, $"nib angle should rotate with tilt direction (delta {d})");
        Assert.True(a.Rx > a.Ry * 3, "flat nib");
    }

    [Fact]
    public void Highlighter_tilt_changes_contact_shape_and_pressure_changes_opacity()
    {
        var flat = Fp(PenKind.Highlighter, 0.6f);
        var tilted = Fp(PenKind.Highlighter, 0.6f, tiltX: 0, tiltY: 55);
        Assert.Equal(FootprintShape.Rect, flat.Shape);
        Assert.NotEqual(flat.Ry, tilted.Ry);
        Assert.True(Fp(PenKind.Highlighter, 1f).Strength > Fp(PenKind.Highlighter, 0.05f).Strength);
    }

    [Fact]
    public void Tilt_sensitivity_zero_disables_tilt()
    {
        var dyn = new PenDynamics { TiltSensitivity = 0 };
        Assert.Equal(Fp(PenKind.Pencil, 0.7f, dyn: dyn).Rx, Fp(PenKind.Pencil, 0.7f, tiltX: 60, dyn: dyn).Rx);
    }

    [Fact]
    public void Speed_fallback_thins_fast_strokes_without_pressure()
    {
        var p = new InkPoint(0, 0, 0.5f);
        NibModel.Compute(PenKind.Ballpoint, 4f, PenDynamics.Default, StrokeFlags.SpeedWidth, p, speed: 0f, out var slow);
        NibModel.Compute(PenKind.Ballpoint, 4f, PenDynamics.Default, StrokeFlags.SpeedWidth, p, speed: 5f, out var fast);
        Assert.True(fast.Rx < slow.Rx);
        Assert.True(fast.Rx > 0.5f, "never vanishes");
    }
}

public class StrokeGeometryTests
{
    [Fact]
    public void All_figures_have_positive_orientation_for_every_pen()
    {
        var b = new StrokeGeometryBuilder();
        foreach (var kind in Enum.GetValues<PenKind>())
        {
            var s = TestStrokes.Make(TestStrokes.Scribble(0, 0, 300), kind, 4f, StrokeFlags.HasPressure | StrokeFlags.HasTilt);
            var sink = new CoverageSink();
            b.Build(s, sink);
            Assert.True(sink.FigureCount > 0);
            foreach (var poly in sink.Polygons)
                Assert.True(CoverageSink.SignedArea(poly) > -1e-6, $"{kind}: negatively oriented figure");
        }
    }

    [Fact]
    public void Constant_line_area_matches_capsule()
    {
        // Width 6, length 100 → rectangle 600 + circle π·9.
        var s = TestStrokes.Make(TestStrokes.Line(10, 50, 110, 50, 60, pressure: 1f), width: 6f);
        var sink = new CoverageSink();
        new StrokeGeometryBuilder().Build(s, sink);
        double area = sink.UnionArea(0, 40, 120, 60, 0.1f);
        double expected = 100 * 6 + Math.PI * 9;
        Assert.InRange(area, expected * 0.97, expected * 1.03);
    }

    [Fact]
    public void Sharp_corner_has_no_gap()
    {
        // A "V" with a 20° turn: the outer corner must be filled (round join).
        var pts = TestStrokes.Line(0, 0, 50, 100, 30).Concat(TestStrokes.Line(50, 100, 100, 0, 30).Skip(1)).ToArray();
        var s = TestStrokes.Make(pts, width: 8f, flags: StrokeFlags.None, dyn: new PenDynamics { PressureEnabled = false });
        var sink = new CoverageSink();
        new StrokeGeometryBuilder().Build(s, sink);
        // Points around the apex within the nib radius must all be covered.
        for (int a = 0; a < 360; a += 10)
        {
            float x = 50 + 3.5f * MathF.Cos(a * MathF.PI / 180), y = 100 + 3.5f * MathF.Sin(a * MathF.PI / 180);
            Assert.True(sink.Covered(x, y), $"gap at angle {a}");
        }
    }

    [Fact]
    public void Fountain_pen_draws_thick_and_thin_by_direction()
    {
        var dyn = new PenDynamics { PressureEnabled = false };
        // Nib long axis at −45°: a stroke along +45° (perpendicular) is thick, along −45° is thin.
        var thick = TestStrokes.Make(TestStrokes.Line(0, 0, 70, 70, 40), PenKind.Fountain, 8f, StrokeFlags.None, dyn);
        var thin = TestStrokes.Make(TestStrokes.Line(0, 70, 70, 0, 40), PenKind.Fountain, 8f, StrokeFlags.None, dyn);
        double At(Stroke st)
        {
            var sink = new CoverageSink();
            new StrokeGeometryBuilder().Build(st, sink);
            return sink.UnionArea(-10, -10, 80, 80, 0.25f);
        }
        Assert.True(At(thick) > At(thin) * 2.5, "thick/thin contrast");
    }

    [Fact]
    public void Pencil_with_pressure_splits_into_strength_runs()
    {
        var pts = TestStrokes.Line(0, 0, 100, 0, 50);
        for (int i = 0; i < pts.Length; i++) pts[i].Pressure = i / 49f;
        var s = TestStrokes.Make(pts, PenKind.Pencil, 3f);
        var sink = new CoverageSink();
        new StrokeGeometryBuilder().Build(s, sink);
        Assert.True(sink.Runs > 4);
        Assert.All(sink.RunInfo, r => Assert.True(r.Multiply));
        // Fill color is the color on white paper: lighter for weaker runs.
        Assert.True(ColorUtil.Luminance(sink.RunInfo[0].FillColor) > ColorUtil.Luminance(sink.RunInfo[^1].FillColor));
    }

    [Fact]
    public void Ballpoint_is_a_single_opaque_run()
    {
        var sink = new CoverageSink();
        new StrokeGeometryBuilder().Build(TestStrokes.Make(TestStrokes.Scribble(0, 0, 200)), sink);
        Assert.Equal(1, sink.Runs);
        Assert.False(sink.RunInfo[0].Multiply);
    }

    [Fact]
    public void Single_point_stroke_is_a_dot()
    {
        var sink = new CoverageSink();
        new StrokeGeometryBuilder().Build(TestStrokes.Make(new[] { new InkPoint(5, 5, 1f) }, width: 4f), sink);
        Assert.True(sink.Covered(5, 5));
        Assert.True(sink.Covered(6.5f, 5));
        Assert.False(sink.Covered(7.5f, 5));
    }

    [Fact]
    public void Dense_input_is_decimated()
    {
        // 2000 samples over 20 units (240 Hz pen moving slowly) collapse to far fewer figures.
        var s = TestStrokes.Make(TestStrokes.Line(0, 0, 20, 0, 2000), width: 2f);
        var sink = new CoverageSink();
        new StrokeGeometryBuilder().Build(s, sink);
        Assert.True(sink.FigureCount < 400, $"{sink.FigureCount} figures");
    }
}

public class InkProcessorTests
{
    [Fact]
    public void Stabilizer_reduces_jitter_but_reaches_pen_up_point()
    {
        var proc = new InkProcessor();
        var rnd = new Random(3);
        var first = new RawSample(0, 0, 0.6f, 0, 0, -1, 0);
        proc.Begin(first, 0.8f, StrokeFlags.HasPressure);
        double rawJitter = 0, outJitter = 0;
        for (int i = 1; i <= 200; i++)
        {
            float jitter = (float)(rnd.NextDouble() - 0.5) * 1.2f;
            proc.Add(new RawSample(i * 0.5f, jitter, 0.6f, 0, 0, -1, (ulong)(i * 4000)));
            rawJitter += Math.Abs(jitter);
        }
        var pts = proc.End();
        for (int i = 10; i < pts.Length - 1; i++) outJitter += Math.Abs(pts[i].Y);
        Assert.True(outJitter / (pts.Length - 11) < rawJitter / 200 * 0.6, "jitter not reduced");
        Assert.Equal(100f, pts[^1].X, 1);
    }

    [Fact]
    public void Tilt_flag_turns_on_when_pen_reports_tilt()
    {
        var proc = new InkProcessor();
        proc.Begin(new RawSample(0, 0, 0.5f, 0, 0, -1, 0), 0, StrokeFlags.HasPressure);
        Assert.Equal(StrokeFlags.HasPressure, proc.Flags);
        proc.Add(new RawSample(1, 0, 0.5f, 20, 5, -1, 4000));
        Assert.Equal(StrokeFlags.HasPressure | StrokeFlags.HasTilt, proc.Flags);
    }

    [Fact]
    public void Base_flags_detect_pressure_capability()
    {
        Assert.Equal(StrokeFlags.HasPressure, InkProcessor.BaseFlagsFor(isPen: true, deviceKnownPressureCapable: false, firstPressure: 0.23f));
        Assert.Equal(StrokeFlags.SpeedWidth, InkProcessor.BaseFlagsFor(isPen: true, deviceKnownPressureCapable: false, firstPressure: 0.5f));
        Assert.Equal(StrokeFlags.SpeedWidth, InkProcessor.BaseFlagsFor(isPen: false, deviceKnownPressureCapable: false, firstPressure: 0.8f));
    }

    [Fact]
    public void Duplicate_samples_are_dropped_but_none_are_throttled()
    {
        var proc = new InkProcessor();
        proc.Begin(new RawSample(0, 0, 0.5f, 0, 0, -1, 0), 0, StrokeFlags.HasPressure);
        Assert.False(proc.Add(new RawSample(0, 0, 0.5f, 0, 0, -1, 1000)));
        int accepted = 0;
        for (int i = 1; i <= 240; i++) if (proc.Add(new RawSample(i * 0.3f, 0, 0.5f, 0, 0, -1, (ulong)(i * 4166)))) accepted++;
        Assert.Equal(240, accepted);
    }
}

public class ColorTests
{
    [Fact]
    public void Over_white_is_identity_at_full_strength_and_white_at_zero()
    {
        Assert.Equal(0xFF336699u, ColorUtil.OverWhite(0xFF336699, 1f));
        Assert.Equal(0xFFFFFFFFu, ColorUtil.OverWhite(0xFF336699, 0f));
    }

    [Fact]
    public void Dark_mode_inverts_lightness()
    {
        Assert.True(ColorUtil.Luminance(ColorUtil.ToDarkMode(0xFF000000)) > 0.8f);
        Assert.True(ColorUtil.Luminance(ColorUtil.ToDarkMode(0xFFFFFFFF)) < 0.2f);
        uint blue = ColorUtil.ToDarkMode(0xFF1957C9);
        Assert.True(ColorUtil.B(blue) > ColorUtil.R(blue), "hue preserved");
    }
}
