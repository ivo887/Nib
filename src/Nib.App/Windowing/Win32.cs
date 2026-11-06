using System;
using System.Runtime.InteropServices;

namespace Nib.App.Windowing;

internal static unsafe class Win32
{
    public const uint WM_SETTINGCHANGE = 0x001A;
    public const uint WM_GETMINMAXINFO = 0x0024;
    public const uint WM_DISPLAYCHANGE = 0x007E;
    public const uint WM_DPICHANGED = 0x02E0;
    public const int SPI_SETWORKAREA = 0x002F;
    public const uint MONITOR_DEFAULTTONEAREST = 2;

    public const uint ABM_GETSTATE = 0x4;
    public const uint ABM_GETAUTOHIDEBAREX = 0xB;
    public const int ABS_AUTOHIDE = 0x1;
    public const uint ABE_LEFT = 0, ABE_TOP = 1, ABE_RIGHT = 2, ABE_BOTTOM = 3;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct APPBARDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public RECT rc;
        public IntPtr lParam;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MONITORINFOEX
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    [DllImport("shell32.dll")] public static extern IntPtr SHAppBarMessage(uint message, ref APPBARDATA data);
    [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool GetMonitorInfoW(IntPtr monitor, ref MONITORINFOEX info);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr hwnd, out RECT rect);

    /// <summary>MINMAXINFO from WM_GETMINMAXINFO: the sizes Windows allows the window to take.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MINMAXINFO { public POINT ptReserved, ptMaxSize, ptMaxPosition, ptMinTrackSize, ptMaxTrackSize; }
    [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr hwnd, ref POINT point);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("comctl32.dll")]
    public static extern bool SetWindowSubclass(IntPtr hwnd, delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, IntPtr, nuint, nuint, IntPtr> proc, nuint id, nuint refData);

    [DllImport("comctl32.dll")]
    public static extern bool RemoveWindowSubclass(IntPtr hwnd, delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, IntPtr, nuint, nuint, IntPtr> proc, nuint id);

    [DllImport("comctl32.dll")]
    public static extern IntPtr DefSubclassProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("dwmapi.dll")]
    public static extern int DwmGetCompositionTimingInfo(IntPtr hwnd, ref DWM_TIMING_INFO info);

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct UNSIGNED_RATIO { public uint Numerator, Denominator; }

    /// <summary>Only the leading fields are read; the struct is sized to the full Win32 definition.</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1, Size = 292)] // sizeof(DWM_TIMING_INFO), checked by DWM
    public struct DWM_TIMING_INFO
    {
        public uint cbSize;
        public UNSIGNED_RATIO rateRefresh;
    }

    /// <summary>Display refresh rate for the monitor showing the window (falls back to 60 Hz).</summary>
    public static double RefreshRate(IntPtr hwnd)
    {
        try
        {
            var info = new DWM_TIMING_INFO { cbSize = (uint)Marshal.SizeOf<DWM_TIMING_INFO>() };
            if (DwmGetCompositionTimingInfo(IntPtr.Zero, ref info) == 0 && info.rateRefresh.Denominator != 0)
                return info.rateRefresh.Numerator / (double)info.rateRefresh.Denominator;
        }
        catch { }
        return 60;
    }
}
