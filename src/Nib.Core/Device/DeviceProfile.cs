using System;
using System.Collections.Generic;
using System.Linq;
using Nib.Core.Model;

namespace Nib.Core.Device;

// Hardware-aware tuning. The app probes the machine once at startup (digitizer HID descriptor, CPU sets,
// SMBIOS model name, display mode, posture); everything that turns those facts into settings lives here
// as pure functions so it is unit-tested on any OS.

/// <summary>One HID usage reported by the digitizer (GetPointerDeviceProperties).</summary>
public readonly record struct HidUsage(ushort Page, ushort Id, int LogicalMin, int LogicalMax, int PhysicalMin, int PhysicalMax, uint Unit, uint UnitExponent);

/// <summary>What the pen digitizer can report, read from its HID descriptor rather than guessed from samples.</summary>
public sealed record PenCapabilities(
    string Product,
    int PressureLevels,
    bool HasTilt,
    bool HasTwist,
    bool HasEraserButton,
    bool HasBarrelButton,
    float MaxTiltDegrees)
{
    public const ushort DigitizerPage = 0x0D;
    public const ushort TipPressure = 0x30;
    public const ushort XTilt = 0x3D;
    public const ushort YTilt = 0x3E;
    public const ushort Twist = 0x41;
    public const ushort Invert = 0x3C;
    public const ushort BarrelSwitch = 0x44;
    public const ushort Eraser = 0x45;
    public const ushort SecondaryBarrelSwitch = 0x5A;

    public static PenCapabilities FromHidUsages(string product, IEnumerable<HidUsage> usages)
    {
        int levels = 0;
        bool xt = false, yt = false, twist = false, eraser = false, barrel = false;
        float maxTilt = 0;
        foreach (var u in usages)
        {
            if (u.Page != DigitizerPage) continue;
            switch (u.Id)
            {
                case TipPressure: levels = Math.Max(levels, u.LogicalMax - u.LogicalMin + 1); break;
                case XTilt: xt = true; maxTilt = Math.Max(maxTilt, TiltDegrees(u)); break;
                case YTilt: yt = true; maxTilt = Math.Max(maxTilt, TiltDegrees(u)); break;
                case Twist: twist = true; break;
                case Invert: case Eraser: eraser = true; break;
                case BarrelSwitch: case SecondaryBarrelSwitch: barrel = true; break;
            }
        }
        return new PenCapabilities(product.Trim(), levels, xt && yt, twist, eraser, barrel, maxTilt);
    }

    /// <summary>Tilt range in degrees. HID reports degrees (unit 0x14, English rotation) with an exponent,
    /// often as ±9000 × 10⁻²; when the descriptor is incomplete we fall back to the logical range.</summary>
    private static float TiltDegrees(in HidUsage u)
    {
        int phys = Math.Max(Math.Abs(u.PhysicalMin), Math.Abs(u.PhysicalMax));
        if (phys == 0) return Math.Min(90, Math.Max(Math.Abs(u.LogicalMin), Math.Abs(u.LogicalMax)));
        int exp = (int)(u.UnitExponent & 0xF);
        if (exp >= 8) exp -= 16; // HID unit exponent is a signed nibble
        return (float)Math.Min(90, phys * Math.Pow(10, exp));
    }

    public string Describe()
    {
        var parts = new List<string>();
        parts.Add(PressureLevels > 0 ? $"{PressureLevels} pressure levels" : "no pressure");
        parts.Add(HasTilt ? (MaxTiltDegrees > 0 ? $"tilt ±{MaxTiltDegrees:0}°" : "tilt") : "no tilt");
        if (HasTwist) parts.Add("twist");
        if (HasEraserButton) parts.Add("eraser button");
        if (HasBarrelButton) parts.Add("barrel button");
        return string.Join(", ", parts);
    }
}

/// <summary>One logical processor from GetSystemCpuSetInformation. Higher efficiency class = faster core.</summary>
public readonly record struct CpuCore(uint CpuSetId, byte EfficiencyClass, bool Parked, bool Allocated);

public static class CpuTopology
{
    /// <summary>True on hybrid CPUs (e.g. Lunar Lake: 4 Lion Cove P-cores + 4 Skymont LP E-cores).</summary>
    public static bool IsHybrid(IReadOnlyList<CpuCore> cores) =>
        cores.Count > 1 && cores.Select(c => c.EfficiencyClass).Distinct().Count() > 1;

    /// <summary>
    /// CPU set ids of the fastest core class, for the ink thread while the pen is near the screen.
    /// Empty when the CPU is not hybrid (no preference needed) or when no fast core is usable
    /// (all allocated to other processes), so the scheduler stays free to choose.
    /// </summary>
    public static uint[] PerformanceCores(IReadOnlyList<CpuCore> cores)
    {
        if (!IsHybrid(cores)) return Array.Empty<uint>();
        byte top = cores.Max(c => c.EfficiencyClass);
        var ids = cores.Where(c => c.EfficiencyClass == top && !c.Allocated).Select(c => c.CpuSetId).ToArray();
        return ids;
    }

    public static string Describe(IReadOnlyList<CpuCore> cores)
    {
        if (cores.Count == 0) return "unknown";
        if (!IsHybrid(cores)) return $"{cores.Count} logical processors";
        byte top = cores.Max(c => c.EfficiencyClass);
        int p = cores.Count(c => c.EfficiencyClass == top);
        return $"{p} performance + {cores.Count - p} efficiency logical processors";
    }
}

/// <summary>A machine Nib has specific knowledge about (matched on the SMBIOS model strings).</summary>
public sealed record KnownDevice(string Name, bool OledPanel, string PenNote);

public static class KnownDevices
{
    /// <summary>
    /// Lenovo consumer machines report the model name (e.g. "Yoga 7 2-in-1 14ILL10") in SMBIOS
    /// SystemFamily or SystemVersion and the machine type (e.g. "83JQ") at the start of SystemProductName.
    /// </summary>
    public static KnownDevice? Match(string? family, string? productName, string? version)
    {
        string all = $"{family} | {version} | {productName}";
        bool machineType83JQ = productName?.Trim().StartsWith("83JQ", StringComparison.OrdinalIgnoreCase) == true;
        if (machineType83JQ || all.Contains("14ILL10", StringComparison.OrdinalIgnoreCase))
            return new KnownDevice(
                "Lenovo Yoga 7 2-in-1 14\" Gen 10 (14ILL10)",
                OledPanel: true, // every 14ILL10 panel option is OLED (WUXGA 60 Hz or 2.8K 120 Hz)
                PenNote: "Lenovo Yoga Pen / Digital Pen (MPP 2.0, 4096 levels)");
        return null;
    }
}

/// <summary>Everything the app learned about the machine at startup.</summary>
public sealed record DeviceSnapshot(
    string ModelName,
    KnownDevice? Known,
    PenCapabilities? Pen,
    IReadOnlyList<CpuCore> Cores,
    double RefreshHz,
    int DisplayWidthPx,
    int DisplayHeightPx,
    bool SlateMode,
    bool OnBattery,
    bool BatterySaver)
{
    public static DeviceSnapshot Unknown { get; } = new("Unknown PC", null, null, Array.Empty<CpuCore>(), 60, 1920, 1080, false, false, false);
}

public static class DeviceTuner
{
    /// <summary>Software latency budget assumed for input processing + rendering (see PerfTargets).</summary>
    public const float ProcessingBudgetMs = 4f;
    /// <summary>Lag we accept after prediction. Predicting further hides more lag but overshoots at corners.</summary>
    public const float ResidualTargetMs = 8f;
    public const float MaxPredictionMs = 15f;

    /// <summary>
    /// Prediction horizon for a refresh rate: the display pipeline costs about
    /// budget + 1.5 refresh periods; predict away everything above the residual target,
    /// but never more than 15 ms. 60 Hz → 15 ms, 90 Hz → 13 ms, 120 Hz → 9 ms, 144 Hz → 6 ms.
    /// </summary>
    public static float AutoPredictionMs(double refreshHz)
    {
        if (!(refreshHz > 1)) refreshHz = 60;
        double pipeline = ProcessingBudgetMs + 1.5 * 1000.0 / refreshHz;
        return (float)Math.Round(Math.Clamp(pipeline - ResidualTargetMs, 0, MaxPredictionMs), MidpointRounding.AwayFromZero);
    }

    /// <summary>Prediction actually used: automatic from the refresh rate, or the user's fixed value.</summary>
    public static float EffectivePredictionMs(AppSettings s, double refreshHz) =>
        s.AutoPrediction ? AutoPredictionMs(refreshHz) : Math.Clamp(s.PredictionMs, 0, 30);

    /// <summary>Toolbar button size: touch-sized when the convertible is folded into a tablet.</summary>
    public static double ToolbarButtonSize(bool slateMode) => slateMode ? 48 : 40;

    /// <summary>
    /// Defaults chosen once, when Nib runs for the first time on this machine (no settings file yet).
    /// Later the user's own choices always win.
    /// </summary>
    public static void ApplyFirstRunDefaults(AppSettings s, DeviceSnapshot d)
    {
        // A pen whose second side button (or tail) already erases gets lasso on the barrel button,
        // so both tools are one click away without touching the toolbar.
        if (d.Pen is { HasEraserButton: true }) s.BarrelButton = BarrelButtonAction.Lasso;
        // OLED: dark pages with truly black paper switch pixels off (less power, no grey glow).
        if (d.Known is { OledPanel: true }) s.OledBlackPages = true;
        s.AutoPrediction = true;
        s.PerformanceModeWhileWriting = true;
    }

    /// <summary>One line per fact, for the Settings "This device" section and the debug overlay.</summary>
    public static IReadOnlyList<string> Describe(DeviceSnapshot d, AppSettings s)
    {
        var lines = new List<string>
        {
            d.Known?.Name ?? d.ModelName,
            $"Display: {d.DisplayWidthPx}×{d.DisplayHeightPx}, {d.RefreshHz:0} Hz{(d.Known?.OledPanel == true ? ", OLED" : "")}",
            "Pen: " + (d.Pen is null ? "none detected" : $"{(d.Pen.Product.Length > 0 ? d.Pen.Product + ", " : "")}{d.Pen.Describe()}"),
            "CPU: " + CpuTopology.Describe(d.Cores),
            $"Stroke prediction: {EffectivePredictionMs(s, d.RefreshHz):0} ms{(s.AutoPrediction ? " (automatic)" : "")}",
        };
        if (d.OnBattery) lines.Add(d.BatterySaver ? "On battery, battery saver on" : "On battery");
        return lines;
    }
}
