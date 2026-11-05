using System;
using System.Runtime.InteropServices;
using Microsoft.Graphics.Canvas;
using Windows.Graphics.DirectX.Direct3D11;

namespace Nib.App.Ink;

/// <summary>
/// Creates the Win2D device on top of a D3D11 device whose DXGI maximum frame latency is 1, so the GPU
/// never queues more than one frame behind the pen (the default of 3 would add up to two refresh periods
/// of ink latency). Falls back to a default Win2D device if anything in the interop path fails.
/// </summary>
internal static unsafe class GraphicsDevice
{
    private const int D3D_DRIVER_TYPE_HARDWARE = 1;
    private const int D3D_DRIVER_TYPE_WARP = 5;
    private const uint D3D11_CREATE_DEVICE_BGRA_SUPPORT = 0x20;
    private const uint D3D11_SDK_VERSION = 7;
    private static readonly Guid IID_IDXGIDevice1 = new("77db970f-6276-48ba-ba28-070143b4392c");

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags,
        IntPtr featureLevels, uint featureLevelCount, uint sdkVersion, out IntPtr device, out int featureLevel, out IntPtr context);

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    public static CanvasDevice Create(out string description)
    {
        IntPtr device = IntPtr.Zero, context = IntPtr.Zero, dxgi = IntPtr.Zero, inspectable = IntPtr.Zero;
        try
        {
            int hr = D3D11CreateDevice(IntPtr.Zero, D3D_DRIVER_TYPE_HARDWARE, IntPtr.Zero, D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                IntPtr.Zero, 0, D3D11_SDK_VERSION, out device, out _, out context);
            description = "hardware";
            if (hr < 0)
            {
                hr = D3D11CreateDevice(IntPtr.Zero, D3D_DRIVER_TYPE_WARP, IntPtr.Zero, D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                    IntPtr.Zero, 0, D3D11_SDK_VERSION, out device, out _, out context);
                description = "WARP (software)";
            }
            Marshal.ThrowExceptionForHR(hr);

            var iid = IID_IDXGIDevice1;
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(device, in iid, out dxgi));
            // IDXGIDevice1::SetMaximumFrameLatency is vtable slot 12
            // (IUnknown 0–2, IDXGIObject 3–6, IDXGIDevice 7–11, IDXGIDevice1 12–13).
            var vtbl = *(IntPtr**)dxgi;
            var setMaxFrameLatency = (delegate* unmanaged[Stdcall]<IntPtr, uint, int>)vtbl[12];
            setMaxFrameLatency(dxgi, 1);

            Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi, out inspectable));
            var d3d = WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
            description += ", frame latency 1";
            return CanvasDevice.CreateFromDirect3D11Device(d3d);
        }
        catch (Exception)
        {
            description = "default Win2D device";
            return new CanvasDevice();
        }
        finally
        {
            if (inspectable != IntPtr.Zero) Marshal.Release(inspectable);
            if (dxgi != IntPtr.Zero) Marshal.Release(dxgi);
            if (context != IntPtr.Zero) Marshal.Release(context);
            if (device != IntPtr.Zero) Marshal.Release(device);
        }
    }
}

/// <summary>
/// Scheduling hints for the ink thread: highest thread priority, MMCSS "Games" class, and opting the
/// thread out of EcoQoS power throttling so battery-saver mode does not stretch input and frame timing.
/// Every call is best-effort.
/// </summary>
internal static class ThreadTuning
{
    private const int THREAD_PRIORITY_HIGHEST = 2;
    private const int ThreadPowerThrottling = 3;
    private const uint THREAD_POWER_THROTTLING_CURRENT_VERSION = 1;
    private const uint THREAD_POWER_THROTTLING_EXECUTION_SPEED = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct THREAD_POWER_THROTTLING_STATE
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentThread();
    [DllImport("kernel32.dll")] private static extern bool SetThreadPriority(IntPtr thread, int priority);
    [DllImport("kernel32.dll")] private static extern bool SetThreadInformation(IntPtr thread, int infoClass, ref THREAD_POWER_THROTTLING_STATE info, int size);
    [DllImport("avrt.dll", CharSet = CharSet.Unicode)] private static extern IntPtr AvSetMmThreadCharacteristicsW(string taskName, ref uint taskIndex);
    [DllImport("kernel32.dll")] private static extern int SetThreadDescription(IntPtr thread, [MarshalAs(UnmanagedType.LPWStr)] string description);

    public static void BoostCurrentThread(string name)
    {
        try
        {
            var t = GetCurrentThread();
            SetThreadDescription(t, name);
            SetThreadPriority(t, THREAD_PRIORITY_HIGHEST);
            var state = new THREAD_POWER_THROTTLING_STATE
            {
                Version = THREAD_POWER_THROTTLING_CURRENT_VERSION,
                ControlMask = THREAD_POWER_THROTTLING_EXECUTION_SPEED,
                StateMask = 0, // never throttle execution speed
            };
            SetThreadInformation(t, ThreadPowerThrottling, ref state, Marshal.SizeOf<THREAD_POWER_THROTTLING_STATE>());
            uint index = 0;
            AvSetMmThreadCharacteristicsW("Games", ref index);
        }
        catch (Exception)
        {
            // Hints only.
        }
    }
}
