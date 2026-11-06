using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;

namespace Nib.App.Services;

/// <summary>
/// Where the notebooks live, and the safe way to change it. The choice is stored in a tiny bootstrap file that always
/// stays in the default folder (the data folder itself can move, so the pointer cannot live inside it). Moving or
/// restoring is scheduled and carried out at the next start, before the database is opened, so a live SQLite file
/// is never copied or replaced while Nib is writing to it.
/// </summary>
internal static class DataLocation
{
    private static readonly string[] DataFiles = { "notebooks.db", "notebooks.db-wal", "notebooks.db-shm", "settings.json" };

    /// <summary>%LOCALAPPDATA%\Nib, or the folder named by NIB_HOME (used by tests so they never touch real notebooks).</summary>
    public static string DefaultFolder => Environment.GetEnvironmentVariable("NIB_HOME") is { Length: > 0 } home
        ? home
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nib");
    private static string BootstrapFile => Path.Combine(DefaultFolder, "location.txt");
    private static string PendingFile => Path.Combine(DefaultFolder, "pending.txt");

    /// <summary>A note about what happened at startup (a finished move or restore) for the app to show once.</summary>
    public static string? StartupMessage { get; private set; }

    /// <summary>The user's chosen folder, or null for the default.</summary>
    public static string? Configured()
    {
        try
        {
            if (!File.Exists(BootstrapFile)) return null;
            var path = File.ReadAllText(BootstrapFile).Trim();
            return path.Length > 0 ? path : null;
        }
        catch (IOException) { return null; }
    }

    public static void SetConfigured(string? folder)
    {
        Directory.CreateDirectory(DefaultFolder);
        if (string.IsNullOrWhiteSpace(folder) || SamePath(folder, DefaultFolder))
        {
            if (File.Exists(BootstrapFile)) File.Delete(BootstrapFile);
            return;
        }
        File.WriteAllText(BootstrapFile, folder);
    }

    public static bool HasNotebooks(string folder) => File.Exists(Path.Combine(folder, "notebooks.db"));

    /// <summary>Schedules copying the notebooks and settings to <paramref name="to"/> (or just switching to it) at the next start.</summary>
    public static void ScheduleMove(string from, string to, bool copyData)
    {
        Directory.CreateDirectory(DefaultFolder);
        File.WriteAllText(PendingFile, $"move\n{from}\n{to}\n{(copyData ? "copy" : "switch")}\n");
    }

    /// <summary>Schedules replacing the current notebooks with the ones in a .nibpkg backup at the next start.</summary>
    public static void ScheduleRestore(string package, string dataFolder)
    {
        Directory.CreateDirectory(DefaultFolder);
        File.WriteAllText(PendingFile, $"restore\n{package}\n{dataFolder}\n");
    }

    public static void CancelPending()
    {
        try { if (File.Exists(PendingFile)) File.Delete(PendingFile); } catch (IOException) { }
    }

    /// <summary>Carries out a scheduled move or restore. Call first thing at startup, before the data folder is used.</summary>
    public static void ApplyPending()
    {
        try
        {
            if (!File.Exists(PendingFile)) return;
            var lines = File.ReadAllLines(PendingFile);
            File.Delete(PendingFile); // never retry a failing operation on every start
            if (lines.Length >= 4 && lines[0] == "move") StartupMessage = ApplyMove(lines[1], lines[2], lines[3] == "copy");
            else if (lines.Length >= 3 && lines[0] == "restore") StartupMessage = ApplyRestore(lines[1], lines[2]);
        }
        catch (Exception e)
        {
            StartupMessage = "Could not change the notebook location: " + e.Message;
            try { File.AppendAllText(Path.Combine(DefaultFolder, "crash.log"), $"[{DateTimeOffset.Now:O}] {e}\n\n"); } catch { }
        }
    }

    private static string ApplyMove(string from, string to, bool copy)
    {
        Directory.CreateDirectory(to);
        if (copy)
        {
            foreach (var name in DataFiles)
            {
                string src = Path.Combine(from, name);
                if (File.Exists(src)) File.Copy(src, Path.Combine(to, name), overwrite: true);
            }
        }
        SetConfigured(to);
        return copy
            ? $"Notebooks copied to {to}. Your previous copy is still in {from}; delete it when you are happy."
            : $"Now using the notebooks in {to}.";
    }

    private static string ApplyRestore(string package, string dataFolder)
    {
        Directory.CreateDirectory(dataFolder);
        string staged = Path.Combine(dataFolder, "notebooks.db.restoring");
        using (var zip = ZipFile.OpenRead(package))
        {
            var entry = zip.GetEntry("notebooks.db") ?? throw new InvalidDataException("This backup does not contain notebooks.");
            entry.ExtractToFile(staged, overwrite: true);
        }
        // A real SQLite file starts with this 16 byte header.
        using (var f = File.OpenRead(staged))
        {
            var head = new byte[16];
            if (f.Read(head, 0, 16) < 16 || System.Text.Encoding.ASCII.GetString(head, 0, 15) != "SQLite format 3")
            {
                f.Close(); File.Delete(staged);
                throw new InvalidDataException("This backup is damaged.");
            }
        }
        string db = Path.Combine(dataFolder, "notebooks.db");
        string keep = null!;
        if (File.Exists(db))
        {
            keep = Path.Combine(dataFolder, $"notebooks.before-restore-{DateTime.Now:yyyyMMdd-HHmmss}.db");
            File.Move(db, keep);
        }
        foreach (var extra in new[] { db + "-wal", db + "-shm" }) if (File.Exists(extra)) File.Delete(extra);
        File.Move(staged, db);
        return keep is null ? "Backup restored." : $"Backup restored. Your previous notebooks were kept as {Path.GetFileName(keep)}.";
    }

    /// <summary>Starts a fresh Nib that waits for this one to exit first, then closes this window (the caller closes it).</summary>
    public static void LaunchSuccessor()
    {
        var exe = Environment.ProcessPath;
        if (exe is null) return;
        Process.Start(new ProcessStartInfo(exe, $"--wait-for {Environment.ProcessId}") { UseShellExecute = false });
    }

    /// <summary>Called early with --wait-for: waits (briefly) for the previous process so its database is closed.</summary>
    public static void WaitForPredecessor(int pid)
    {
        try { using var p = Process.GetProcessById(pid); p.WaitForExit(15000); }
        catch (ArgumentException) { /* already gone */ }
        catch (InvalidOperationException) { }
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
}
