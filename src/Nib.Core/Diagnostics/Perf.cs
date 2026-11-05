using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Threading;

namespace Nib.Core.Diagnostics;

/// <summary>
/// ETW provider "Nib-Performance" (GUID derived from the name). Record with the WPR profile in
/// perf/Nib.wprp and inspect in Windows Performance Analyzer next to DxgKrnl present/flip events.
/// </summary>
[EventSource(Name = "Nib-Performance")]
public sealed class NibEventSource : EventSource
{
    public static readonly NibEventSource Log = new();

    [Event(1, Level = EventLevel.Informational)]
    public void InkInputToPresent(double microseconds) { if (IsEnabled()) WriteEvent(1, microseconds); }

    [Event(2, Level = EventLevel.Informational)]
    public void Frame(double frameMicroseconds, double renderMicroseconds) { if (IsEnabled()) WriteEvent(2, frameMicroseconds, renderMicroseconds); }

    [Event(3, Level = EventLevel.Warning)]
    public void UiThreadStall(double milliseconds) { if (IsEnabled()) WriteEvent(3, milliseconds); }

    [Event(4, Level = EventLevel.Informational)]
    public void PageSwitch(long pageId, double milliseconds) { if (IsEnabled()) WriteEvent(4, pageId, milliseconds); }

    [Event(5, Level = EventLevel.Informational)]
    public void StartupMilestone(string name, double milliseconds) { if (IsEnabled()) WriteEvent(5, name, milliseconds); }

    [Event(6, Level = EventLevel.Informational)]
    public void Autosave(int changes, double milliseconds) { if (IsEnabled()) WriteEvent(6, changes, milliseconds); }

    [Event(7, Level = EventLevel.Informational)]
    public void TileRaster(int tiles, double milliseconds) { if (IsEnabled()) WriteEvent(7, tiles, milliseconds); }
}

/// <summary>
/// Fixed-bucket latency histogram (0.05 ms buckets up to 250 ms). Recording is O(1), lock-free for a
/// single writer and allocation-free, so it can sit in the ink and render loops.
/// </summary>
public sealed class LatencyHistogram
{
    private const double BucketMs = 0.05;
    private const int Buckets = 5000;
    private readonly int[] _counts = new int[Buckets + 1];
    private long _total;
    private double _sum;
    private double _max;

    public long Count => Interlocked.Read(ref _total);
    public double Max => _max;
    public double Mean => _total == 0 ? 0 : _sum / _total;

    public void Record(double ms)
    {
        if (double.IsNaN(ms) || ms < 0) return;
        int b = (int)(ms / BucketMs);
        if (b > Buckets) b = Buckets;
        _counts[b]++;
        _sum += ms;
        if (ms > _max) _max = ms;
        Interlocked.Increment(ref _total);
    }

    /// <summary>Value at percentile p (0..100), upper bucket edge.</summary>
    public double Percentile(double p)
    {
        long total = Count;
        if (total == 0) return 0;
        long target = (long)Math.Ceiling(total * Math.Clamp(p, 0, 100) / 100.0);
        long acc = 0;
        for (int i = 0; i <= Buckets; i++)
        {
            acc += _counts[i];
            if (acc >= target) return i == Buckets ? _max : (i + 1) * BucketMs;
        }
        return _max;
    }

    public void Reset()
    {
        Array.Clear(_counts);
        _total = 0;
        _sum = 0;
        _max = 0;
    }
}

/// <summary>Live counters shown by the developer overlay and collected by the perf harness.</summary>
public sealed class PerfCounters
{
    public static readonly PerfCounters Instance = new();

    public LatencyHistogram InkInputToPresent { get; } = new();
    public LatencyHistogram FrameInterval { get; } = new();
    /// <summary>Frame intervals while a pan/zoom gesture or fling is running (must track the refresh rate).</summary>
    public LatencyHistogram PanFrameInterval { get; } = new();
    public LatencyHistogram RenderTime { get; } = new();
    public LatencyHistogram UiStall { get; } = new();
    public LatencyHistogram PageSwitch { get; } = new();
    public LatencyHistogram ToolApply { get; } = new();

    /// <summary>Most recent values for the overlay.</summary>
    public double LastFrameMs, LastInkLatencyMs, LastUiStallMs;
    public int DroppedFrames;
    public double RefreshHz = 60;

    public static readonly Stopwatch Clock = Stopwatch.StartNew();
    public static double NowMs => Clock.Elapsed.TotalMilliseconds;

    public void ResetAll()
    {
        InkInputToPresent.Reset(); FrameInterval.Reset(); PanFrameInterval.Reset(); RenderTime.Reset();
        UiStall.Reset(); PageSwitch.Reset(); ToolApply.Reset();
        DroppedFrames = 0;
    }
}

/// <summary>Performance targets from the spec. Loaded from perf/perf-targets.json by the runner.</summary>
public sealed class PerfTargets
{
    /// <summary>
    /// App-controlled part of pen-to-pixel: pointer event → frame presented, p95, may take at most one refresh
    /// period (waiting for the next vblank is inherent) plus this budget for processing and rendering.
    /// </summary>
    public double InputProcessingBudgetMs { get; set; } = 4;
    /// <summary>
    /// Effective pen-to-pixel, p95: input→present + display pipeline (next vblank, compositor frame,
    /// average scan-out ≈ 2 refresh periods) − stroke prediction horizon. Spec: ≤ 15–20 ms.
    /// Confirm on device with the high-speed camera procedure (docs/TEST-CHECKLIST.md).
    /// </summary>
    public double EffectivePenToPixelP95Ms { get; set; } = 20;
    /// <summary>Share of frames during pan/zoom that took longer than 1.5 refresh periods.</summary>
    public double MaxDroppedFramePercent { get; set; } = 1;
    public double UiStallMaxMs { get; set; } = 8;
    public double ColdStartMs { get; set; } = 1500;
    public double WarmStartMs { get; set; } = 500;
    public double PageSwitchP95Ms { get; set; } = 100;
    /// <summary>Tool/color change must apply to the very next stroke: measured as ms from change to the next stroke using it.</summary>
    public double ToolApplyMaxMs { get; set; } = 0.5;
    /// <summary>Frames between a resize/rotation/taskbar change and a correctly laid-out frame.</summary>
    public int ResizeReflowMaxFrames { get; set; } = 1;
}

/// <summary>Raw measurements produced by one perf run (app harness + runner).</summary>
public sealed class PerfMeasurements
{
    public string Scenario { get; set; } = "";
    public double RefreshHz { get; set; } = 60;
    public bool BatterySaver { get; set; }
    /// <summary>Stroke prediction horizon that was active during the run (0 = off).</summary>
    public double PredictionMs { get; set; }
    public List<double> InputToPresentMs { get; set; } = new();
    public List<double> PanFrameIntervalsMs { get; set; } = new();
    public List<double> UiStallsMs { get; set; } = new();
    public double? ColdStartMs { get; set; }
    public double? WarmStartMs { get; set; }
    public List<double> PageSwitchMs { get; set; } = new();
    public List<double> ToolApplyMs { get; set; } = new();
    public List<int> ResizeReflowFrames { get; set; } = new();
}

public sealed record PerfMetric(string Name, double Value, double Target, string Unit, bool Pass, string Note = "");

public sealed class PerfReport
{
    public string Device { get; set; } = Environment.MachineName;
    public string Scenario { get; set; } = "";
    public DateTimeOffset When { get; set; } = DateTimeOffset.Now;
    public double RefreshHz { get; set; }
    public bool BatterySaver { get; set; }
    public List<PerfMetric> Metrics { get; set; } = new();
    public bool Pass { get; set; }
}

/// <summary>Turns measurements into pass/fail against targets. Used by the runner (and unit-tested).</summary>
public static class PerfVerdict
{
    public static PerfReport Evaluate(PerfMeasurements m, PerfTargets t)
    {
        var r = new PerfReport { Scenario = m.Scenario, RefreshHz = m.RefreshHz, BatterySaver = m.BatterySaver };
        double period = 1000.0 / Math.Max(m.RefreshHz, 1);

        if (m.InputToPresentMs.Count > 0)
        {
            double p95 = Percentile(m.InputToPresentMs, 95);
            double target = period + t.InputProcessingBudgetMs;
            r.Metrics.Add(new("Input → present p95", p95, target, "ms", p95 <= target, "One refresh period + processing budget"));
            // Measured value already includes the wait for the next vblank; the compositor adds ~1 period and
            // scan-out averages ½ period.
            double pipeline = p95 + period * 1.5;
            double effective = Math.Max(0, pipeline - m.PredictionMs);
            r.Metrics.Add(new("Effective pen-to-pixel p95", effective, t.EffectivePenToPixelP95Ms, "ms", effective <= t.EffectivePenToPixelP95Ms,
                $"Estimate: {pipeline:0.0} ms pipeline − {m.PredictionMs:0} ms prediction. Confirm with the camera procedure in docs/TEST-CHECKLIST.md"));
        }
        if (m.PanFrameIntervalsMs.Count > 0)
        {
            int dropped = 0;
            foreach (var f in m.PanFrameIntervalsMs) if (f > period * 1.5) dropped++;
            double pct = 100.0 * dropped / m.PanFrameIntervalsMs.Count;
            r.Metrics.Add(new("Pan/zoom dropped frames", pct, t.MaxDroppedFramePercent, "%", pct <= t.MaxDroppedFramePercent));
        }
        {
            double max = 0;
            foreach (var s in m.UiStallsMs) if (s > max) max = s;
            r.Metrics.Add(new("UI thread longest stall", max, t.UiStallMaxMs, "ms", max <= t.UiStallMaxMs));
        }
        if (m.ColdStartMs is { } cold) r.Metrics.Add(new("Cold start to writable page", cold, t.ColdStartMs, "ms", cold <= t.ColdStartMs));
        if (m.WarmStartMs is { } warm) r.Metrics.Add(new("Warm start to writable page", warm, t.WarmStartMs, "ms", warm <= t.WarmStartMs));
        if (m.PageSwitchMs.Count > 0)
        {
            double p95 = Percentile(m.PageSwitchMs, 95);
            r.Metrics.Add(new("Page switch p95", p95, t.PageSwitchP95Ms, "ms", p95 <= t.PageSwitchP95Ms));
        }
        if (m.ToolApplyMs.Count > 0)
        {
            double max = 0;
            foreach (var v in m.ToolApplyMs) if (v > max) max = v;
            r.Metrics.Add(new("Tool change applied to next stroke", max, t.ToolApplyMaxMs, "ms", max <= t.ToolApplyMaxMs));
        }
        if (m.ResizeReflowFrames.Count > 0)
        {
            int max = 0;
            foreach (var v in m.ResizeReflowFrames) if (v > max) max = v;
            r.Metrics.Add(new("Resize / rotation reflow", max, t.ResizeReflowMaxFrames, "frames", max <= t.ResizeReflowMaxFrames));
        }
        bool pass = true;
        foreach (var metric in r.Metrics) pass &= metric.Pass;
        r.Pass = pass && r.Metrics.Count > 0;
        return r;
    }

    public static double Percentile(List<double> values, double p)
    {
        if (values.Count == 0) return 0;
        var sorted = values.ToArray();
        Array.Sort(sorted);
        double rank = Math.Clamp(p, 0, 100) / 100.0 * (sorted.Length - 1);
        int lo = (int)Math.Floor(rank), hi = (int)Math.Ceiling(rank);
        return sorted[lo] + (sorted[hi] - sorted[lo]) * (rank - lo);
    }
}
