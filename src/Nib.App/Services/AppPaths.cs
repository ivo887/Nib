using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Nib.Core.Diagnostics;
using Nib.Core.Model;
using Nib.Core.Serialization;

namespace Nib.App.Services;

/// <summary>Where Nib keeps its data. Local-first: everything lives in one folder (v2 sync = put it in a sync folder).</summary>
internal static class AppPaths
{
    public static string DataFolder
    {
        get
        {
            string root = Environment.GetEnvironmentVariable("NIB_DATA") is { Length: > 0 } custom
                ? custom
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nib");
            Directory.CreateDirectory(root);
            return root;
        }
    }

    public static string Database => Path.Combine(DataFolder, "notebooks.db");
    public static string Settings => Path.Combine(DataFolder, "settings.json");
    public static string CrashLog => Path.Combine(DataFolder, "crash.log");
}

/// <summary>Command-line switches (used by the performance runner and for development).</summary>
internal sealed record LaunchOptions(bool PerfMode, string? PerfOut, string? PerfScenario, bool DebugOverlay, string? DataFolder)
{
    public static LaunchOptions Parse(string[] args)
    {
        bool perf = false, overlay = false;
        string? perfOut = null, scenario = null, data = null;
        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--perf": perf = true; break;
                case "--perf-out" when i + 1 < args.Length: perfOut = args[++i]; break;
                case "--perf-scenario" when i + 1 < args.Length: scenario = args[++i]; break;
                case "--overlay": overlay = true; break;
                case "--data" when i + 1 < args.Length: data = args[++i]; break;
            }
        }
        if (data is not null) Environment.SetEnvironmentVariable("NIB_DATA", data);
        return new LaunchOptions(perf, perfOut, scenario, overlay, data);
    }
}

/// <summary>Startup milestones relative to process start (cold/warm start measurement).</summary>
internal static class StartupClock
{
    private static readonly DateTime ProcessStart = Process.GetCurrentProcess().StartTime;

    public static double SinceProcessStartMs => (DateTime.Now - ProcessStart).TotalMilliseconds;

    public static void Mark(string name) => NibEventSource.Log.StartupMilestone(name, SinceProcessStartMs);
}

internal static class CrashLog
{
    public static void Write(Exception? e)
    {
        if (e is null) return;
        try { File.AppendAllText(AppPaths.CrashLog, $"[{DateTimeOffset.Now:O}] {e}\n\n"); }
        catch { /* never throw from the crash handler */ }
    }
}

/// <summary>Loads and saves settings (debounced, off the UI thread).</summary>
internal sealed class SettingsStore
{
    private readonly string _path;
    private Timer? _timer;
    private readonly object _gate = new();
    public AppSettings Current { get; }
    /// <summary>No settings file existed: Nib is running for the first time on this machine (device defaults apply).</summary>
    public bool IsFirstRun { get; }

    public SettingsStore(string path)
    {
        _path = path;
        AppSettings? loaded = null;
        try { if (File.Exists(path)) loaded = NibJson.DeserializeSettings(File.ReadAllText(path)); }
        catch (IOException) { }
        IsFirstRun = loaded is null && !File.Exists(path);
        Current = loaded ?? new AppSettings();
        if (Current.Pens.Count == 0) Current.Pens.AddRange(Nib.Core.Ink.PenPreset.DefaultFavorites());
    }

    /// <summary>Snapshots the settings now (on the caller's thread, so no concurrent mutation) and writes them shortly after.</summary>
    public void SaveSoon()
    {
        string json = NibJson.Serialize(Current);
        lock (_gate)
        {
            _pendingJson = json;
            _timer?.Dispose();
            _timer = new Timer(_ => WritePending(), null, 800, Timeout.Infinite);
        }
    }

    /// <summary>Synchronous save (app exit).</summary>
    public void SaveNow()
    {
        lock (_gate) _pendingJson = NibJson.Serialize(Current);
        WritePending();
    }

    private string? _pendingJson;

    private void WritePending()
    {
        string? json;
        lock (_gate) { json = _pendingJson; _pendingJson = null; }
        if (json is null) return;
        try
        {
            string tmp = _path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _path, overwrite: true);
        }
        catch (IOException) { }
    }
}
