using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace Nib.App.Platform;

/// <summary>
/// Hides the Windows cursor over the page. WinUI picks the cursor through the input stack in a way that cannot be
/// intercepted per window, and there is no blank <c>InputSystemCursorShape</c>, so while the pointer is over the page
/// (and the user wants no Windows cursor) the system's "cross" cursor, the shape the page uses in those modes, is
/// swapped for a fully transparent one. It is put back as soon as the pointer leaves, the window loses focus or Nib
/// closes. A marker file makes the next start restore it too, so a crash can never leave the cross invisible.
/// </summary>
internal static class CursorHider
{
    private const uint OCR_CROSS = 32515;
    private const uint SPI_SETCURSORS = 0x0057;
    private static bool _blank;
    private static string? _marker;

    [DllImport("user32.dll")] private static extern IntPtr CreateCursor(IntPtr hInstance, int xHotSpot, int yHotSpot, int width, int height, byte[] andPlane, byte[] xorPlane);
    [DllImport("user32.dll")] private static extern bool SetSystemCursor(IntPtr cursor, uint id);
    [DllImport("user32.dll")] private static extern bool SystemParametersInfoW(uint action, uint param, IntPtr pv, uint winIni);

    /// <summary>Call once at startup: undoes a blank cross left behind by an earlier crash.</summary>
    public static void RecoverAfterCrash(string dataFolder)
    {
        _marker = Path.Combine(dataFolder, "cursor-blanked");
        if (File.Exists(_marker)) Restore();
    }

    public static bool IsBlank => _blank;

    // The system calls take ~20 ms (reloading every cursor), so the UI thread hands them to a worker, in order.
    private static readonly object s_gate = new();
    private static Task s_chain = Task.CompletedTask;
    private static void Enqueue(Action op) { lock (s_gate) s_chain = s_chain.ContinueWith(_ => op(), TaskScheduler.Default); }
    public static void BlankAsync() => Enqueue(Blank);
    public static void RestoreAsync() => Enqueue(Restore);

    /// <summary>For shutdown: waits for queued work, then puts the cursor back before returning.</summary>
    public static void RestoreNow()
    {
        Task pending;
        lock (s_gate) pending = s_chain;
        try { pending.Wait(1000); } catch (Exception) { }
        if (_blank) Restore();
    }

    public static void Blank()
    {
        if (_blank) return;
        try
        {
            var andPlane = new byte[32 * 32 / 8];
            Array.Fill(andPlane, (byte)0xFF);           // AND = 1, XOR = 0: the screen shows through
            var xorPlane = new byte[32 * 32 / 8];
            var cursor = CreateCursor(IntPtr.Zero, 0, 0, 32, 32, andPlane, xorPlane);
            if (cursor == IntPtr.Zero) return;
            if (_marker is not null) File.WriteAllText(_marker, "The Windows cross cursor is temporarily blank while Nib hides it. Nib restores it on exit.");
            _blank = SetSystemCursor(cursor, OCR_CROSS); // the system takes ownership of the handle
        }
        catch (Exception) { /* cosmetic only */ }
    }

    public static void Restore()
    {
        try
        {
            SystemParametersInfoW(SPI_SETCURSORS, 0, IntPtr.Zero, 0); // reload every system cursor from the user's scheme
            if (_marker is not null && File.Exists(_marker)) File.Delete(_marker);
        }
        catch (Exception) { /* nothing more to do */ }
        _blank = false;
    }
}
