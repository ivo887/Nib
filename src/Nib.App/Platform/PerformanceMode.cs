using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;

namespace Nib.App.Platform;

/// <summary>
/// "Performance while writing", driven by the pen's hover state. Ink-thread only.
///
/// When the pen comes within hover range (before it touches the glass):
/// <list type="bullet">
/// <item>asks Windows for the panel's high refresh rate (<c>DCompositionBoostCompositorClock</c>), so a
///   Dynamic-refresh-rate display such as the Yoga 7's 2.8K OLED is already at 120 Hz for the first stroke;</item>
/// <item>restricts the ink thread to the CPU's performance cores (Lunar Lake: the four Lion Cove cores)
///   with <c>SetThreadSelectedCpuSets</c>, so pointer events are never handled on a low-power E-core.</item>
/// </list>
/// Both are released 1.5 s after the pen leaves, so a quick lift between words keeps them and the
/// laptop returns to its power-saving state as soon as you stop writing.
/// </summary>
internal sealed unsafe class PerformanceMode
{
    [DllImport("dcomp.dll", ExactSpelling = true)]
    private static extern int DCompositionBoostCompositorClock(int enable);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetThreadSelectedCpuSets(IntPtr thread, uint* cpuSetIds, uint cpuSetIdCount);

    private readonly DispatcherQueueTimer _release;
    private bool _releasePending;
    private bool _boostAvailable = true;
    private uint[] _cores = Array.Empty<uint>();
    private long _lastBoostTicks;

    public bool Enabled { get; private set; }
    public bool Active { get; private set; }
    public bool Boosting { get; private set; }
    public bool Pinned { get; private set; }

    public PerformanceMode(DispatcherQueue inkQueue)
    {
        _release = inkQueue.CreateTimer();
        _release.Interval = TimeSpan.FromMilliseconds(1500);
        _release.IsRepeating = false;
        _release.Tick += (_, _) => { _releasePending = false; Release(); };
    }

    /// <summary>Called on the ink thread when settings or the CPU probe change.</summary>
    public void Configure(bool enabled, uint[] performanceCores)
    {
        bool wasActive = Active;
        if (wasActive) Release();
        Enabled = enabled;
        _cores = performanceCores;
        if (wasActive && enabled) PenNear();
    }

    /// <summary>Pen entered hover range or moved while hovering. Cheap when already active.</summary>
    public void PenNear()
    {
        if (_releasePending) { _release.Stop(); _releasePending = false; }
        if (!Enabled || Active) return;
        Active = true;
        Boost();
        Pin(true);
    }

    /// <summary>Pen touched down: refresh the refresh-rate request at most once a second.</summary>
    public void PenDown()
    {
        PenNear();
        if (Active && Stopwatch.GetElapsedTime(_lastBoostTicks).TotalMilliseconds > 1000) Boost();
    }

    public void PenAway()
    {
        if (!Active || _releasePending) return;
        _releasePending = true;
        _release.Start();
    }

    private void Release()
    {
        if (!Active) return;
        Active = false;
        if (Boosting && _boostAvailable)
        {
            try { DCompositionBoostCompositorClock(0); } catch (Exception) { }
        }
        Boosting = false;
        Pin(false);
    }

    private void Boost()
    {
        _lastBoostTicks = Stopwatch.GetTimestamp();
        if (!_boostAvailable) return;
        try { Boosting = DCompositionBoostCompositorClock(1) >= 0; }
        catch (EntryPointNotFoundException) { _boostAvailable = false; } // Windows 10: no boost API
        catch (DllNotFoundException) { _boostAvailable = false; }
    }

    private void Pin(bool on)
    {
        try
        {
            if (on && _cores.Length > 0)
            {
                fixed (uint* ids = _cores) Pinned = SetThreadSelectedCpuSets(GetCurrentThread(), ids, (uint)_cores.Length);
            }
            else if (Pinned)
            {
                SetThreadSelectedCpuSets(GetCurrentThread(), null, 0); // back to "any core"
                Pinned = false;
            }
        }
        catch (Exception) { Pinned = false; }
    }

    public string Describe() =>
        !Enabled ? "performance mode off" :
        !Active ? "performance mode idle" :
        $"performance mode on{(Boosting ? ", high refresh requested" : "")}{(Pinned ? $", ink thread on {_cores.Length} P-cores" : "")}";
}
