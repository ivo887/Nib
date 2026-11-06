using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Nib.Core.Diagnostics;
using Nib.Core.Serialization;

namespace Nib.App.Diagnostics;

/// <summary>
/// Measures UI-thread responsiveness: a background thread posts a no-op every 4 ms and records how long it
/// waited. Any wait over 8 ms is a stall (spec: the UI thread is never blocked for more than 8 ms).
/// Runs only while the developer overlay is visible or in perf mode, so it costs nothing normally.
/// </summary>
internal sealed class UiStallWatchdog : IDisposable
{
    private readonly DispatcherQueue _ui;
    private Thread? _thread;
    private volatile bool _running;

    public UiStallWatchdog(DispatcherQueue ui) => _ui = ui;

    public void Start()
    {
        if (_running) return;
        _running = true;
        _thread = new Thread(Loop) { IsBackground = true, Name = "Nib UI watchdog", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    public void Stop() => _running = false;

    private void Loop()
    {
        using var done = new AutoResetEvent(false);
        while (_running)
        {
            long t0 = Stopwatch.GetTimestamp();
            if (!_ui.TryEnqueue(DispatcherQueuePriority.High, () => done.Set())) break;
            done.WaitOne(2000);
            double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            var pc = PerfCounters.Instance;
            pc.UiStall.Record(ms);
            if (ms > 8)
            {
                pc.LastUiStallMs = ms;
                NibEventSource.Log.UiThreadStall(ms);
            }
            Thread.Sleep(4);
        }
    }

    public void Dispose() => Stop();
}

/// <summary>Developer-only overlay (Ctrl+Alt+Shift+F12): frame time, ink latency, UI stalls, tile and device info.</summary>
internal sealed class DebugOverlay
{
    private readonly TextBlock _text;
    private readonly DispatcherQueueTimer _timer;
    private readonly UiStallWatchdog _watchdog;
    private readonly Func<string> _extra;

    public DebugOverlay(TextBlock text, DispatcherQueue ui, UiStallWatchdog watchdog, Func<string> extra)
    {
        _text = text;
        _watchdog = watchdog;
        _extra = extra;
        _timer = ui.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(250);
        _timer.Tick += (_, _) => Update();
    }

    public bool IsVisible => _text.Visibility == Visibility.Visible;

    public void Toggle(bool? show = null)
    {
        bool on = show ?? !IsVisible;
        _text.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (on) { _watchdog.Start(); _timer.Start(); Update(); }
        else { _watchdog.Stop(); _timer.Stop(); }
    }

    private void Update()
    {
        var pc = PerfCounters.Instance;
        _text.Text =
            $"frame {pc.LastFrameMs,5:0.0} ms  (p99 {pc.FrameInterval.Percentile(99):0.0})  {pc.RefreshHz:0} Hz  dropped {pc.DroppedFrames}\n" +
            $"render p95 {pc.RenderTime.Percentile(95):0.00} ms\n" +
            $"ink input→present {pc.LastInkLatencyMs:0.0} ms  (p95 {pc.InkInputToPresent.Percentile(95):0.0})\n" +
            $"UI stall max {pc.UiStall.Max:0.0} ms  (>8 ms: last {pc.LastUiStallMs:0.0})\n" +
            $"page switch p95 {pc.PageSwitch.Percentile(95):0} ms\n" +
            _extra();
    }
}

/// <summary>
/// Perf mode (--perf): the app writes "ready.json" (window handle and ink surface rectangle in screen pixels)
/// so the runner can inject pen and touch input, then on "done" writes all measurements and exits.
/// </summary>
internal sealed class PerfHarness
{
    private readonly string _folder;
    private readonly DispatcherQueueTimer _poll;
    private readonly Func<(IntPtr Hwnd, int X, int Y, int W, int H)> _surface;
    private readonly Action _exit;
    private readonly string _scenario;
    public double? StartupMs { get; set; }
    private int _resizeFrames = -1;

    public PerfHarness(string folder, string scenario, DispatcherQueue ui, Func<(IntPtr, int, int, int, int)> surface, Action exit)
    {
        _folder = folder;
        _scenario = scenario;
        _surface = surface;
        _exit = exit;
        Directory.CreateDirectory(folder);
        _poll = ui.CreateTimer();
        _poll.Interval = TimeSpan.FromMilliseconds(100);
        _poll.Tick += (_, _) => Poll();
    }

    public void Ready()
    {
        var (hwnd, x, y, w, h) = _surface();
        File.WriteAllText(Path.Combine(_folder, "ready.json"),
            $"{{\"hwnd\":{hwnd.ToInt64()},\"x\":{x},\"y\":{y},\"width\":{w},\"height\":{h},\"startupMs\":{(StartupMs ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture)}}}");
        PerfCounters.Instance.ResetAll();
        _poll.Start();
    }

    /// <summary>Called by the shell when a resize happened and when each subsequent frame was presented.</summary>
    public void OnResizeStarted() => _resizeFrames = 0;

    public void OnFramePresentedAfterResize(bool layoutMatches)
    {
        if (_resizeFrames < 0) return;
        _resizeFrames++;
        if (layoutMatches)
        {
            Measurements.ResizeReflowFrames.Add(_resizeFrames);
            _resizeFrames = -1;
        }
    }

    public PerfMeasurements Measurements { get; } = new();
    /// <summary>Prediction horizon in use when the run ends (automatic prediction follows the refresh rate).</summary>
    public Func<double, float>? PredictionFor { get; set; }

    /// <summary>Applies a tool by name ("pen:Brush", "highlighter", "eraser:pixel"); lets the runner profile each tool in turn.</summary>
    public Action<string>? ApplyTool { get; set; }

    private void Poll()
    {
        string toolFile = Path.Combine(_folder, "tool");
        if (File.Exists(toolFile))
        {
            string name = File.ReadAllText(toolFile).Trim();
            File.Delete(toolFile);
            ApplyTool?.Invoke(name);
            PerfCounters.Instance.ResetAll(); // the next numbers belong to this tool only
        }
        if (!File.Exists(Path.Combine(_folder, "done"))) return;
        _poll.Stop();
        var pc = PerfCounters.Instance;
        var m = Measurements;
        m.Scenario = _scenario;
        m.RefreshHz = pc.RefreshHz;
        if (PredictionFor is not null) m.PredictionMs = PredictionFor(pc.RefreshHz);
        m.ColdStartMs ??= StartupMs;
        Dump(pc.InkInputToPresent, m.InputToPresentMs);
        Dump(pc.PanFrameInterval, m.PanFrameIntervalsMs);
        Dump(pc.UiStall, m.UiStallsMs);
        Dump(pc.PageSwitch, m.PageSwitchMs);
        Dump(pc.ToolApply, m.ToolApplyMs);
        Dump(pc.RenderTime, m.RenderMs);
        Dump(pc.FrameInterval, m.FrameIntervalMs);
        File.WriteAllText(Path.Combine(_folder, "measurements.json"), JsonSerializer.Serialize(m, NibJsonContext.Default.PerfMeasurements));
        _exit();
    }

    /// <summary>Re-expands a histogram into representative samples (bucket upper bounds) for the verdict.</summary>
    private static void Dump(LatencyHistogram h, System.Collections.Generic.List<double> into)
    {
        if (h.Count == 0) return;
        for (int p = 1; p <= 100; p++) into.Add(h.Percentile(p));
    }
}
