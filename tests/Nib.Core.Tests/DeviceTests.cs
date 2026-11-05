using System;
using System.Linq;
using Nib.Core.Device;
using Nib.Core.Model;
using Nib.Core.Rendering;
using Xunit;

namespace Nib.Core.Tests;

public class DeviceTests
{
    // A typical MPP 2.0 descriptor: 12-bit pressure, ±90.00° tilt (unit degrees, exponent −2),
    // barrel switch, invert + eraser (second side button), no twist.
    private static readonly HidUsage[] Mpp2 =
    {
        new(0x0D, 0x42, 0, 1, 0, 0, 0, 0),             // tip switch
        new(0x0D, 0x30, 0, 4095, 0, 0, 0, 0),          // tip pressure
        new(0x0D, 0x3D, -9000, 9000, -9000, 9000, 0x14, 0x0E), // X tilt
        new(0x0D, 0x3E, -9000, 9000, -9000, 9000, 0x14, 0x0E), // Y tilt
        new(0x0D, 0x44, 0, 1, 0, 0, 0, 0),             // barrel switch
        new(0x0D, 0x3C, 0, 1, 0, 0, 0, 0),             // invert
        new(0x0D, 0x45, 0, 1, 0, 0, 0, 0),             // eraser
        new(0x01, 0x30, 0, 30000, 0, 3000, 0x11, 0x0E),// generic desktop X (ignored)
    };

    [Fact]
    public void Pen_capabilities_come_from_the_hid_descriptor()
    {
        var p = PenCapabilities.FromHidUsages(" ELAN pen ", Mpp2);
        Assert.Equal("ELAN pen", p.Product);
        Assert.Equal(4096, p.PressureLevels);
        Assert.True(p.HasTilt);
        Assert.False(p.HasTwist);
        Assert.True(p.HasEraserButton);
        Assert.True(p.HasBarrelButton);
        Assert.Equal(90f, p.MaxTiltDegrees, 3);
        Assert.Equal("4096 pressure levels, tilt ±90°, eraser button, barrel button", p.Describe());
    }

    [Fact]
    public void A_pen_without_tilt_usages_reports_no_tilt()
    {
        var p = PenCapabilities.FromHidUsages("", Mpp2.Where(u => u.Id is not (0x3D or 0x3E or 0x3C or 0x45)));
        Assert.False(p.HasTilt);
        Assert.False(p.HasEraserButton);
        Assert.Equal(0f, p.MaxTiltDegrees);
    }

    [Fact]
    public void Lunar_lake_ink_thread_prefers_the_four_p_cores()
    {
        // Core Ultra 7 256V: 4 Lion Cove P-cores (class 1) + 4 Skymont LP E-cores (class 0), no SMT.
        var cores = Enumerable.Range(0, 8)
            .Select(i => new CpuCore((uint)(0x100 + i), (byte)(i < 4 ? 1 : 0), Parked: i == 2, Allocated: false))
            .ToArray();
        Assert.True(CpuTopology.IsHybrid(cores));
        Assert.Equal(new uint[] { 0x100, 0x101, 0x102, 0x103 }, CpuTopology.PerformanceCores(cores));
        Assert.Equal("4 performance + 4 efficiency logical processors", CpuTopology.Describe(cores));
    }

    [Fact]
    public void Non_hybrid_or_fully_allocated_cpus_get_no_preference()
    {
        var uniform = Enumerable.Range(0, 8).Select(i => new CpuCore((uint)i, 0, false, false)).ToArray();
        Assert.Empty(CpuTopology.PerformanceCores(uniform));
        var allocated = Enumerable.Range(0, 4).Select(i => new CpuCore((uint)i, (byte)(i < 2 ? 1 : 0), false, Allocated: i < 2)).ToArray();
        Assert.Empty(CpuTopology.PerformanceCores(allocated));
    }

    [Theory]
    [InlineData(60.0, 15f)]
    [InlineData(90.0, 13f)]
    [InlineData(120.0, 9f)]
    [InlineData(144.0, 6f)]
    [InlineData(240.0, 2f)]
    [InlineData(0.0, 15f)]       // unknown → treat as 60 Hz
    [InlineData(double.NaN, 15f)]
    public void Prediction_follows_the_refresh_rate(double hz, float expected) =>
        Assert.Equal(expected, DeviceTuner.AutoPredictionMs(hz));

    [Fact]
    public void Prediction_keeps_the_effective_latency_within_target()
    {
        // Same model as PerfVerdict: p95(input→present) + 1.5 refresh periods − prediction ≤ 20 ms.
        foreach (double hz in new[] { 60.0, 90, 120, 144, 165, 240 })
        {
            double effective = DeviceTuner.ProcessingBudgetMs + 1.5 * 1000 / hz - DeviceTuner.AutoPredictionMs(hz);
            Assert.InRange(effective, 0, 20);
        }
    }

    [Fact]
    public void Fixed_prediction_is_used_when_automatic_is_off()
    {
        var s = new AppSettings { AutoPrediction = false, PredictionMs = 22 };
        Assert.Equal(22f, DeviceTuner.EffectivePredictionMs(s, 120));
        s.AutoPrediction = true;
        Assert.Equal(9f, DeviceTuner.EffectivePredictionMs(s, 120));
    }

    [Theory]
    [InlineData("Yoga 7 2-in-1 14ILL10", "83JQ", "Yoga 7 2-in-1 14ILL10")]
    [InlineData(null, "83JQ0013GE", null)]
    [InlineData("", "", "Lenovo Yoga 7 2-in-1 14ILL10")]
    public void Yoga_7_14ILL10_is_recognized(string? family, string? product, string? version)
    {
        var d = KnownDevices.Match(family, product, version);
        Assert.NotNull(d);
        Assert.True(d!.OledPanel);
    }

    [Theory]
    [InlineData(false, 40.0)]
    [InlineData(true, 48.0)]
    public void Toolbar_gets_touch_sized_in_tablet_posture(bool slate, double size) =>
        Assert.Equal(size, DeviceTuner.ToolbarButtonSize(slate));

    [Fact]
    public void Other_machines_are_not_matched() =>
        Assert.Null(KnownDevices.Match("Surface Pro", "Surface Pro 9", "124I:00036T:000M:0300000D:11"));

    [Fact]
    public void First_run_defaults_for_the_yoga()
    {
        var pen = PenCapabilities.FromHidUsages("pen", Mpp2);
        var snap = DeviceSnapshot.Unknown with { Known = KnownDevices.Match("Yoga 7 2-in-1 14ILL10", "83JQ", null), Pen = pen, RefreshHz = 120 };
        var s = new AppSettings { AutoPrediction = false, PerformanceModeWhileWriting = false };
        DeviceTuner.ApplyFirstRunDefaults(s, snap);
        Assert.Equal(BarrelButtonAction.Lasso, s.BarrelButton); // the pen's other button already erases
        Assert.True(s.OledBlackPages);
        Assert.True(s.AutoPrediction);
        Assert.True(s.PerformanceModeWhileWriting);

        var plain = new AppSettings();
        DeviceTuner.ApplyFirstRunDefaults(plain, DeviceSnapshot.Unknown);
        Assert.Equal(BarrelButtonAction.Eraser, plain.BarrelButton);
        Assert.False(plain.OledBlackPages);
    }

    [Fact]
    public void True_black_dark_mode_turns_paper_off_and_keeps_ink_light()
    {
        Assert.Equal(0xFF000000u, ColorUtil.ToDarkMode(0xFFFFFFFF, trueBlack: true));
        Assert.NotEqual(0xFF000000u, ColorUtil.ToDarkMode(0xFFFFFFFF)); // default: dark grey paper
        uint ink = ColorUtil.ToDarkMode(0xFF1F1F1F, trueBlack: true);
        Assert.True(ColorUtil.Luminance(ink) > 0.75f);
        // Light ruling stays faintly visible on black.
        uint rule = ColorUtil.ToDarkMode(0xFFD0D8E0, trueBlack: true);
        Assert.InRange(ColorUtil.Luminance(rule), 0.08f, 0.3f);
    }

    [Fact]
    public void Device_description_lists_the_facts()
    {
        var snap = DeviceSnapshot.Unknown with
        {
            Known = KnownDevices.Match(null, "83JQ", null),
            Pen = PenCapabilities.FromHidUsages("", Mpp2),
            RefreshHz = 120, DisplayWidthPx = 2880, DisplayHeightPx = 1800,
        };
        var lines = DeviceTuner.Describe(snap, new AppSettings());
        Assert.Contains("14ILL10", lines[0]);
        Assert.Equal("Display: 2880×1800, 120 Hz, OLED", lines[1]);
        Assert.Equal("Stroke prediction: 9 ms (automatic)", lines[^1]);
    }
}
