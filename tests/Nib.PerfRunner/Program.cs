using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Nib.Core.Diagnostics;
using Nib.Core.Serialization;
using Nib.PerfRunner;

// Usage:
//   Nib.PerfRunner --app <path\to\Nib.exe> [--targets perf\perf-targets.json] [--out perf-results] [--label "Surface Go 3, battery saver"]
//
// Runs cold start, a pen / touch / page-switch / resize scenario, then warm start, and checks every
// target. Exit code 0 = all targets met, 1 = a target was missed, 2 = the run could not complete.

string? app = null, targets = null, outDir = "perf-results", label = Environment.MachineName;
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--app": app = args[++i]; break;
        case "--targets": targets = args[++i]; break;
        case "--out": outDir = args[++i]; break;
        case "--label": label = args[++i]; break;
    }
}
if (app is null || !File.Exists(app))
{
    Console.Error.WriteLine("Pass --app <path to Nib.exe> (a Release build).");
    return 2;
}

var perfTargets = targets is not null && File.Exists(targets)
    ? JsonSerializer.Deserialize(File.ReadAllText(targets), NibJsonContext.Default.PerfTargets) ?? new PerfTargets()
    : new PerfTargets();

outDir = Path.GetFullPath(outDir);
Directory.CreateDirectory(outDir);
string data = Path.Combine(outDir, "data");
if (Directory.Exists(data)) Directory.Delete(data, recursive: true);
Directory.CreateDirectory(data);

try
{
    // 1. Cold start + full scenario.
    Console.WriteLine("Cold start + scenario…");
    var scenario = RunApp(app, data, Path.Combine(outDir, "cold"), drive: Drive);
    // 2. Warm start (same data, files now in the OS cache).
    Console.WriteLine("Warm start…");
    var warm = RunApp(app, data, Path.Combine(outDir, "warm"), drive: null);
    scenario.WarmStartMs = warm.ColdStartMs;
    scenario.Scenario = label;

    var report = PerfVerdict.Evaluate(scenario, perfTargets);
    report.Device = label;
    File.WriteAllText(Path.Combine(outDir, "report.json"), JsonSerializer.Serialize(report, NibJsonContext.Default.PerfReport));
    Console.WriteLine();
    Console.WriteLine($"{"Metric",-38} {"Value",10} {"Target",10}  Result");
    foreach (var m in report.Metrics)
        Console.WriteLine($"{m.Name,-38} {m.Value,10:0.0} {m.Target,10:0.0}  {(m.Pass ? "PASS" : "FAIL")} {m.Unit} {m.Note}");
    Console.WriteLine();
    Console.WriteLine(report.Pass ? "All targets met." : "TARGETS MISSED.");
    return report.Pass ? 0 : 1;
}
catch (Exception e)
{
    Console.Error.WriteLine(e);
    return 2;
}
finally
{
    Injection.Dispose();
}

static PerfMeasurements RunApp(string app, string data, string folder, Action<IntPtr, JsonElement>? drive)
{
    if (Directory.Exists(folder)) Directory.Delete(folder, true);
    Directory.CreateDirectory(folder);
    var psi = new ProcessStartInfo(app, $"--perf --perf-out \"{folder}\" --data \"{data}\"") { UseShellExecute = false };
    using var proc = Process.Start(psi)!;
    string ready = Path.Combine(folder, "ready.json");
    var sw = Stopwatch.StartNew();
    while (!File.Exists(ready))
    {
        if (proc.HasExited) throw new InvalidOperationException("Nib exited before becoming ready.");
        if (sw.Elapsed > TimeSpan.FromSeconds(30)) throw new TimeoutException("Nib did not become ready within 30 s.");
        Thread.Sleep(10);
    }
    Thread.Sleep(50);
    var info = JsonDocument.Parse(File.ReadAllText(ready)).RootElement;
    var hwnd = new IntPtr(info.GetProperty("hwnd").GetInt64());
    Keys.SetForegroundWindow(hwnd);
    Thread.Sleep(300);
    drive?.Invoke(hwnd, info);
    File.WriteAllText(Path.Combine(folder, "done"), "");
    if (!proc.WaitForExit(20_000)) { proc.Kill(); throw new TimeoutException("Nib did not write its measurements."); }
    string results = Path.Combine(folder, "measurements.json");
    return JsonSerializer.Deserialize(File.ReadAllText(results), NibJsonContext.Default.PerfMeasurements)!;
}

static void Drive(IntPtr hwnd, JsonElement s)
{
    int x = s.GetProperty("x").GetInt32(), y = s.GetProperty("y").GetInt32();
    int w = s.GetProperty("width").GetInt32(), h = s.GetProperty("height").GetInt32();

    // Pen latency: 40 strokes with pressure and tilt, written down the page.
    Console.WriteLine("  pen strokes");
    for (int i = 0; i < 40; i++)
        Injection.PenStroke(x + w / 8, y + 80 + (i % 20) * (h - 160) / 20, w * 3 / 4, 120, i % 2 == 0 ? 0 : 40);

    // Pan and zoom smoothness: two-finger drags and pinches.
    Console.WriteLine("  touch pan / zoom");
    for (int i = 0; i < 4; i++)
    {
        Injection.TwoFingerPan(x + w / 2, y + h * 3 / 4, -h / 2, 60);
        Thread.Sleep(600); // let the fling decay (also measured)
        Injection.TwoFingerPan(x + w / 2, y + h / 4, h / 2, 60);
        Thread.Sleep(600);
    }
    Injection.Pinch(x + w / 2, y + h / 2, 200, 500, 50);
    Thread.Sleep(300);
    Injection.Pinch(x + w / 2, y + h / 2, 500, 200, 50);
    Thread.Sleep(300);

    // Page switching: create pages, then flip through them (preloaded neighbours).
    Console.WriteLine("  page switching");
    for (int i = 0; i < 5; i++) { Keys.Chord(Keys.VK_CONTROL, Keys.VK_N); Thread.Sleep(250); }
    for (int round = 0; round < 3; round++)
    {
        for (int i = 0; i < 5; i++) { Keys.Chord(Keys.VK_CONTROL, Keys.VK_PRIOR); Thread.Sleep(150); }
        for (int i = 0; i < 5; i++) { Keys.Chord(Keys.VK_CONTROL, Keys.VK_NEXT); Thread.Sleep(150); }
    }

    // Resize / reflow: portrait-ish, wide, back.
    Console.WriteLine("  resize");
    foreach (var (rw, rh) in new[] { (900, 1200), (1600, 800), (1200, 900), (1400, 1000) })
    {
        Keys.Resize(hwnd, rw, rh);
        Thread.Sleep(400);
    }
}
