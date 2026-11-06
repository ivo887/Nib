using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Nib.Core.Layout;
using Windows.Graphics;
using Windows.UI.ViewManagement;

namespace Nib.App.Windowing;

/// <summary>
/// Keeps the window correct on any screen: lays out against the real work area, follows taskbar moves and
/// auto-hide changes while running, avoids the touch keyboard / handwriting panel, remembers placement per
/// monitor (relative to its work area), and switches full-screen focus mode.
/// </summary>
internal sealed unsafe class WindowManager : IDisposable
{
    private static WindowManager? s_instance;
    private readonly Window _window;
    private readonly IntPtr _hwnd;
    private readonly AppWindow _appWindow;
    private readonly PlacementStore _store;
    private InputPane? _inputPane;
    private RectI _occluded;
    private bool _subclassed;

    /// <summary>Raised on the UI thread whenever the safe-area insets may have changed.</summary>
    public event Action<Insets>? SafeAreaChanged;
    /// <summary>Raised when the window changes monitor (refresh rate / DPI may differ).</summary>
    public event Action? MonitorChanged;
    /// <summary>Resolution or refresh-rate mode changed (WM_DISPLAYCHANGE), on the UI thread.</summary>
    public event Action? DisplayModeChanged;
    /// <summary>A convertible was folded into or out of tablet posture ("ConvertibleSlateMode"), on the UI thread.</summary>
    public event Action? PostureChanged;

    public Insets SafeArea { get; private set; } = Insets.Zero;
    public AppWindow AppWindow => _appWindow;
    public IntPtr Hwnd => _hwnd;
    public bool IsFullScreen => _appWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen;
    private string? _lastMonitorKey;
    /// <summary>Smallest window the shell lays out well (DIPs).</summary>
    public const int MinWidthDips = 480, MinHeightDips = 360;
    private RectI _lastWork;

    public WindowManager(Window window, PlacementStore store)
    {
        _window = window;
        _store = store;
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(_hwnd));
        s_instance = this;
        try
        {
            string icon = System.IO.Path.Combine(AppContext.BaseDirectory, "Nib.ico");
            if (System.IO.File.Exists(icon)) _appWindow.SetIcon(icon);
        }
        catch (Exception) { /* cosmetic only */ }
    }

    // ------------------------------------------------------------------ placement

    /// <summary>Restores the last placement for the monitor the window was on, clamped to today's work area.</summary>
    public void RestorePlacement()
    {
        DisplayArea? target = null;
        if (_store.LastMonitor is { } key)
        {
            // Index instead of foreach: enumerating a WinRT IReadOnlyList throws InvalidCastException under Native AOT.
            var areas = DisplayArea.FindAll();
            for (int i = 0; i < areas.Count; i++)
                if (MonitorKey(areas[i]) == key) { target = areas[i]; break; }
        }
        target ??= DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Primary);
        var work = ToRect(target.WorkArea);
        string monitor = MonitorKey(target);
        RectI rect = _store.ByMonitor.TryGetValue(monitor, out var saved)
            ? PlacementCalculator.FromSaved(saved, work)
            : PlacementCalculator.DefaultFor(work);
        _appWindow.MoveAndResize(new RectInt32(rect.X, rect.Y, rect.Width, rect.Height));
        if (saved is { Maximized: true } && _appWindow.Presenter is OverlappedPresenter op) op.Maximize();
        _lastMonitorKey = monitor;
    }

    public void SavePlacement()
    {
        var da = DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Nearest);
        if (da is null) return;
        bool max = _appWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized };
        if (_appWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized }) return;
        var pos = _appWindow.Position;
        var size = _appWindow.Size;
        var work = ToRect(da.WorkArea);
        // For a maximized window, keep its previous restored rectangle if we had one.
        string key = MonitorKey(da);
        if (max && _store.ByMonitor.TryGetValue(key, out var prev))
            _store.Remember(key, prev with { Maximized = true, FullScreen = false });
        else
            _store.Remember(key, PlacementCalculator.ToSaved(new RectI(pos.X, pos.Y, size.Width, size.Height), work, max, IsFullScreen));
    }

    // ------------------------------------------------------------------ watching

    public void Start()
    {
        _appWindow.Changed += OnAppWindowChanged;
        // Taskbar moved / resized / auto-hide toggled, resolution change: Windows broadcasts these.
        _subclassed = Win32.SetWindowSubclass(_hwnd, &SubclassProc, 1, 0);
        try
        {
            _inputPane = InputPaneInterop.GetForWindow(_hwnd);
            _inputPane.Showing += (_, e) => { _occluded = ToPhysical(e.OccludedRect); Recompute(); };
            _inputPane.Hiding += (_, _) => { _occluded = default; Recompute(); };
        }
        catch (Exception)
        {
            _inputPane = null; // no touch keyboard support on this system
        }
        Recompute();
    }

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (args.DidPositionChange || args.DidSizeChange || args.DidPresenterChange)
        {
            Recompute();
            var da = DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Nearest);
            string key = da is null ? "" : MonitorKey(da);
            if (key != _lastMonitorKey)
            {
                _lastMonitorKey = key;
                MonitorChanged?.Invoke();
            }
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static IntPtr SubclassProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, nuint id, nuint refData)
    {
        try
        {
            var self = s_instance;
            if ((msg == Win32.WM_SETTINGCHANGE && (int)wParam == Win32.SPI_SETWORKAREA) || msg == Win32.WM_DISPLAYCHANGE)
            {
                // Let Windows finish its own repositioning first, then re-check.
                self?._window.DispatcherQueue.TryEnqueue(() => self.OnWorkAreaChanged());
            }
            if (msg == Win32.WM_GETMINMAXINFO && lParam != IntPtr.Zero)
            {
                // Below this the shell cannot lay itself out; scale with the monitor the window is on.
                var info = (Win32.MINMAXINFO*)lParam;
                double scale = Math.Max(1, Win32.GetDpiForWindow(hwnd)) / 96.0;
                info->ptMinTrackSize.X = (int)Math.Ceiling(MinWidthDips * scale);
                info->ptMinTrackSize.Y = (int)Math.Ceiling(MinHeightDips * scale);
            }
            if (msg == Win32.WM_DISPLAYCHANGE)
                self?._window.DispatcherQueue.TryEnqueue(() => self.DisplayModeChanged?.Invoke());
            else if (msg == Win32.WM_SETTINGCHANGE && lParam != IntPtr.Zero
                     && Marshal.PtrToStringUni(lParam) == "ConvertibleSlateMode")
                self?._window.DispatcherQueue.TryEnqueue(() => self.PostureChanged?.Invoke());
        }
        catch { /* never throw across the native boundary */ }
        return Win32.DefSubclassProc(hwnd, msg, wParam, lParam);
    }

    private void OnWorkAreaChanged()
    {
        // Maximized windows are resized by Windows. A restored window that filled the work area (snapped to full size, or sized
        // by hand to it) should keep filling it when the taskbar moves, resizes or auto-hides; any other restored window
        // just has to stay inside the new work area.
        if (_appWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Restored })
        {
            var da = DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Nearest);
            var work = ToRect(da.WorkArea);
            var r = new RectI(_appWindow.Position.X, _appWindow.Position.Y, _appWindow.Size.Width, _appWindow.Size.Height);
            var target = PlacementCalculator.FollowWorkArea(r, _lastWork, work);
            if (target != r) _appWindow.MoveAndResize(new RectInt32(target.X, target.Y, target.Width, target.Height));
            _lastWork = work;
        }
        else if (DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Nearest) is { } display) _lastWork = ToRect(display.WorkArea);
        Recompute();
    }

    /// <summary>Recomputes safe-area insets from the real work area, auto-hide taskbars and the input pane.</summary>
    public void Recompute()
    {
        var da = DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Nearest);
        if (da is null) return;
        var monitor = ToRect(da.OuterBounds);
        var work = ToRect(da.WorkArea);
        if (_lastWork.IsEmpty) _lastWork = work; // first measurement; later changes are tracked in OnWorkAreaChanged
        double scale = _window.Content?.XamlRoot?.RasterizationScale ?? 1.0;
        var ctx = new ScreenContext(
            ClientRectScreen(),
            monitor,
            work,
            IsFullScreen,
            _appWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized },
            AutoHideEdge(monitor),
            _occluded,
            scale);
        var insets = SafeAreaCalculator.Compute(ctx);
        if (insets != SafeArea)
        {
            SafeArea = insets;
            SafeAreaChanged?.Invoke(insets);
        }
    }

    private RectI ClientRectScreen()
    {
        Win32.GetClientRect(_hwnd, out var rc);
        var p = new Win32.POINT { X = 0, Y = 0 };
        Win32.ClientToScreen(_hwnd, ref p);
        return new RectI(p.X, p.Y, rc.Right - rc.Left, rc.Bottom - rc.Top);
    }

    private static ScreenEdge AutoHideEdge(RectI monitor)
    {
        var data = new Win32.APPBARDATA { cbSize = (uint)Marshal.SizeOf<Win32.APPBARDATA>() };
        long state = (long)Win32.SHAppBarMessage(Win32.ABM_GETSTATE, ref data);
        if ((state & Win32.ABS_AUTOHIDE) == 0) return ScreenEdge.None;
        (uint Edge, ScreenEdge Mapped)[] edges =
        {
            (Win32.ABE_BOTTOM, ScreenEdge.Bottom), (Win32.ABE_LEFT, ScreenEdge.Left),
            (Win32.ABE_RIGHT, ScreenEdge.Right), (Win32.ABE_TOP, ScreenEdge.Top),
        };
        foreach (var (edge, mapped) in edges)
        {
            var d = new Win32.APPBARDATA
            {
                cbSize = (uint)Marshal.SizeOf<Win32.APPBARDATA>(),
                uEdge = edge,
                rc = new Win32.RECT { Left = monitor.X, Top = monitor.Y, Right = monitor.Right, Bottom = monitor.Bottom },
            };
            if (Win32.SHAppBarMessage(Win32.ABM_GETAUTOHIDEBAREX, ref d) != IntPtr.Zero) return mapped;
        }
        return ScreenEdge.None;
    }

    private RectI ToPhysical(Windows.Foundation.Rect r)
    {
        // InputPane reports DIPs in screen space; convert with the window's scale.
        double s = _window.Content?.XamlRoot?.RasterizationScale ?? 1.0;
        return new RectI((int)(r.X * s), (int)(r.Y * s), (int)(r.Width * s), (int)(r.Height * s));
    }

    // ------------------------------------------------------------------ focus mode

    public void ToggleFullScreen()
    {
        if (IsFullScreen) _appWindow.SetPresenter(AppWindowPresenterKind.Default);
        else _appWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
        Recompute();
    }

    public void ExitFullScreen()
    {
        if (IsFullScreen) _appWindow.SetPresenter(AppWindowPresenterKind.Default);
    }

    // ------------------------------------------------------------------ helpers

    private static RectI ToRect(RectInt32 r) => new(r.X, r.Y, r.Width, r.Height);

    /// <summary>Stable per-monitor identity: device name plus resolution (DisplayId values can change between boots).</summary>
    private static string MonitorKey(DisplayArea da)
    {
        string name = "display";
        try
        {
            IntPtr hmon = Win32Interop.GetMonitorFromDisplayId(da.DisplayId);
            var info = new Win32.MONITORINFOEX { cbSize = (uint)Marshal.SizeOf<Win32.MONITORINFOEX>() };
            if (Win32.GetMonitorInfoW(hmon, ref info)) name = info.szDevice;
        }
        catch { }
        return $"{name}@{da.OuterBounds.Width}x{da.OuterBounds.Height}";
    }

    public void Dispose()
    {
        _appWindow.Changed -= OnAppWindowChanged;
        if (_subclassed) Win32.RemoveWindowSubclass(_hwnd, &SubclassProc, 1);
        if (ReferenceEquals(s_instance, this)) s_instance = null;
    }
}
