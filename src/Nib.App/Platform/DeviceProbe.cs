using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Nib.App.Windowing;
using Nib.Core.Device;

namespace Nib.App.Platform;

/// <summary>
/// Reads the facts Nib tunes itself by: the pen digitizer's HID descriptor (pressure resolution, tilt,
/// buttons), the CPU's core classes, the SMBIOS model name, the display mode, posture and power state.
/// Runs once at startup on a background thread (about a millisecond) and again when the display changes.
/// Every probe is best-effort: a failure leaves that fact unknown, never breaks startup.
/// </summary>
internal static unsafe class DeviceProbe
{
    public static DeviceSnapshot Probe(IntPtr hwnd)
    {
        string? family = Bios("SystemFamily"), product = Bios("SystemProductName"), version = Bios("SystemVersion");
        var known = KnownDevices.Match(family, product, version);
        var (w, h) = MonitorPixels(hwnd);
        var (onBattery, saver) = Power();
        return new DeviceSnapshot(
            ModelName(Bios("SystemManufacturer"), family, version, product),
            known,
            ProbePen(),
            ProbeCores(),
            Win32.RefreshRate(hwnd),
            w, h,
            IsSlateMode(),
            onBattery, saver);
    }

    // ------------------------------------------------------------------ model

    private static string? Bios(string name)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
            return (key?.GetValue(name) as string)?.Trim();
        }
        catch (Exception) { return null; }
    }

    private static string ModelName(string? maker, params string?[] candidates)
    {
        foreach (var c in candidates)
        {
            if (string.IsNullOrWhiteSpace(c) || c.Contains("O.E.M", StringComparison.OrdinalIgnoreCase) || c.Contains("System Product", StringComparison.OrdinalIgnoreCase)) continue;
            return string.IsNullOrWhiteSpace(maker) || c.StartsWith(maker, StringComparison.OrdinalIgnoreCase) ? c : $"{maker} {c}";
        }
        return string.IsNullOrWhiteSpace(maker) ? "Unknown PC" : maker;
    }

    // ------------------------------------------------------------------ pen

    private const int POINTER_DEVICE_TYPE_INTEGRATED_PEN = 1;
    private const int POINTER_DEVICE_TYPE_EXTERNAL_PEN = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINTER_DEVICE_INFO
    {
        public uint displayOrientation;
        public IntPtr device;
        public int pointerDeviceType;
        public IntPtr monitor;
        public uint startingCursorId;
        public ushort maxActiveContacts;
        public fixed char productString[520]; // POINTER_DEVICE_PRODUCT_STRING_MAX
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINTER_DEVICE_PROPERTY
    {
        public int logicalMin, logicalMax, physicalMin, physicalMax;
        public uint unit, unitExponent;
        public ushort usagePageId, usageId;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetPointerDevices(ref uint deviceCount, POINTER_DEVICE_INFO* pointerDevices);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetPointerDeviceProperties(IntPtr device, ref uint propertyCount, POINTER_DEVICE_PROPERTY* pointerProperties);

    private static PenCapabilities? ProbePen()
    {
        try
        {
            uint count = 0;
            if (!GetPointerDevices(ref count, null) || count == 0) return null;
            var devices = new POINTER_DEVICE_INFO[count];
            fixed (POINTER_DEVICE_INFO* pd = devices)
            {
                if (!GetPointerDevices(ref count, pd)) return null;
                // Prefer the built-in digitizer over an external tablet.
                for (int pass = 0; pass < 2; pass++)
                {
                    int wanted = pass == 0 ? POINTER_DEVICE_TYPE_INTEGRATED_PEN : POINTER_DEVICE_TYPE_EXTERNAL_PEN;
                    for (int i = 0; i < count; i++)
                    {
                        if (pd[i].pointerDeviceType != wanted) continue;
                        string product = new string(pd[i].productString);
                        int nul = product.IndexOf('\0');
                        if (nul >= 0) product = product[..nul];
                        return PenCapabilities.FromHidUsages(product, Usages(pd[i].device));
                    }
                }
            }
        }
        catch (Exception) { }
        return null;
    }

    private static List<HidUsage> Usages(IntPtr device)
    {
        var list = new List<HidUsage>();
        uint n = 0;
        if (!GetPointerDeviceProperties(device, ref n, null) || n == 0) return list;
        var props = new POINTER_DEVICE_PROPERTY[n];
        fixed (POINTER_DEVICE_PROPERTY* pp = props)
        {
            if (!GetPointerDeviceProperties(device, ref n, pp)) return list;
            for (int i = 0; i < n; i++)
            {
                ref var p = ref pp[i];
                list.Add(new HidUsage(p.usagePageId, p.usageId, p.logicalMin, p.logicalMax, p.physicalMin, p.physicalMax, p.unit, p.unitExponent));
            }
        }
        return list;
    }

    // ------------------------------------------------------------------ CPU

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemCpuSetInformation(byte* information, uint bufferLength, out uint returnedLength, IntPtr process, uint flags);

    /// <summary>
    /// SYSTEM_CPU_SET_INFORMATION entries: Size @0, Type @4 (0 = CpuSetInformation), Id @8,
    /// EfficiencyClass @18, flags @19 (bit 0 Parked, bit 1 Allocated).
    /// </summary>
    private static IReadOnlyList<CpuCore> ProbeCores()
    {
        var cores = new List<CpuCore>();
        try
        {
            GetSystemCpuSetInformation(null, 0, out uint needed, IntPtr.Zero, 0);
            if (needed == 0) return cores;
            var buf = new byte[needed];
            fixed (byte* b = buf)
            {
                if (!GetSystemCpuSetInformation(b, needed, out uint got, IntPtr.Zero, 0)) return cores;
                uint offset = 0;
                while (offset + 20 <= got)
                {
                    uint size = *(uint*)(b + offset);
                    if (size == 0) break;
                    if (*(int*)(b + offset + 4) == 0)
                    {
                        byte flags = b[offset + 19];
                        cores.Add(new CpuCore(*(uint*)(b + offset + 8), b[offset + 18], (flags & 1) != 0, (flags & 2) != 0));
                    }
                    offset += size;
                }
            }
        }
        catch (Exception) { }
        return cores;
    }

    // ------------------------------------------------------------------ display, posture, power

    private static (int W, int H) MonitorPixels(IntPtr hwnd)
    {
        try
        {
            var mon = Win32.MonitorFromWindow(hwnd, Win32.MONITOR_DEFAULTTONEAREST);
            var info = new Win32.MONITORINFOEX { cbSize = (uint)Marshal.SizeOf<Win32.MONITORINFOEX>() };
            if (Win32.GetMonitorInfoW(mon, ref info))
                return (info.rcMonitor.Right - info.rcMonitor.Left, info.rcMonitor.Bottom - info.rcMonitor.Top);
        }
        catch (Exception) { }
        return (1920, 1080);
    }

    private const int SM_MAXIMUMTOUCHES = 95;
    private const int SM_CONVERTIBLESLATEMODE = 0x2003;

    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);

    /// <summary>
    /// True when a convertible is folded into a tablet (keyboard off). Windows reports 0 for slate mode;
    /// machines without a touch screen are never treated as slates.
    /// </summary>
    public static bool IsSlateMode()
    {
        try { return GetSystemMetrics(SM_MAXIMUMTOUCHES) > 0 && GetSystemMetrics(SM_CONVERTIBLESLATEMODE) == 0; }
        catch (Exception) { return false; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public uint BatteryLifeTime, BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")] private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

    private static (bool OnBattery, bool Saver) Power()
    {
        try
        {
            if (GetSystemPowerStatus(out var s)) return (s.ACLineStatus == 0, s.SystemStatusFlag == 1);
        }
        catch (Exception) { }
        return (false, false);
    }
}
