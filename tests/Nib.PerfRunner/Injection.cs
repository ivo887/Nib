using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Nib.PerfRunner;

/// <summary>
/// Synthetic pen and touch via CreateSyntheticPointerDevice / InjectSyntheticPointerInput (Windows 10 1809+).
/// These go through the real Windows pointer stack, so the app sees them exactly like hardware input,
/// including pressure and tilt.
/// </summary>
internal static unsafe class Injection
{
    private const int PT_TOUCH = 2, PT_PEN = 3;
    private const int POINTER_FEEDBACK_DEFAULT = 1;

    private const uint POINTER_FLAG_NEW = 0x1;
    private const uint POINTER_FLAG_INRANGE = 0x2;
    private const uint POINTER_FLAG_INCONTACT = 0x4;
    private const uint POINTER_FLAG_FIRSTBUTTON = 0x10;
    private const uint POINTER_FLAG_PRIMARY = 0x2000;
    private const uint POINTER_FLAG_DOWN = 0x10000;
    private const uint POINTER_FLAG_UPDATE = 0x20000;
    private const uint POINTER_FLAG_UP = 0x40000;

    private const uint PEN_MASK_PRESSURE = 0x1, PEN_MASK_TILT_X = 0x4, PEN_MASK_TILT_Y = 0x8;
    private const uint TOUCH_MASK_CONTACTAREA = 0x1, TOUCH_MASK_PRESSURE = 0x4;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINTER_INFO
    {
        public int pointerType;
        public uint pointerId;
        public uint frameId;
        public uint pointerFlags;
        public IntPtr sourceDevice;
        public IntPtr hwndTarget;
        public POINT ptPixelLocation;
        public POINT ptHimetricLocation;
        public POINT ptPixelLocationRaw;
        public POINT ptHimetricLocationRaw;
        public uint dwTime;
        public uint historyCount;
        public int InputData;
        public uint dwKeyStates;
        public ulong PerformanceCount;
        public int ButtonChangeType;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINTER_PEN_INFO
    {
        public POINTER_INFO pointerInfo;
        public uint penFlags;
        public uint penMask;
        public uint pressure;
        public uint rotation;
        public int tiltX;
        public int tiltY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINTER_TOUCH_INFO
    {
        public POINTER_INFO pointerInfo;
        public uint touchFlags;
        public uint touchMask;
        public RECT rcContact;
        public RECT rcContactRaw;
        public uint orientation;
        public uint pressure;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct POINTER_TYPE_INFO
    {
        [FieldOffset(0)] public int type;
        [FieldOffset(8)] public POINTER_PEN_INFO penInfo;
        [FieldOffset(8)] public POINTER_TOUCH_INFO touchInfo;
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr CreateSyntheticPointerDevice(int pointerType, uint maxCount, int mode);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool InjectSyntheticPointerInput(IntPtr device, POINTER_TYPE_INFO* info, uint count);
    [DllImport("user32.dll")] private static extern void DestroySyntheticPointerDevice(IntPtr device);

    private static IntPtr s_pen, s_touch;
    private static uint s_frame;

    private static IntPtr Pen => s_pen != IntPtr.Zero ? s_pen : s_pen = CreateSyntheticPointerDevice(PT_PEN, 1, POINTER_FEEDBACK_DEFAULT);
    private static IntPtr Touch => s_touch != IntPtr.Zero ? s_touch : s_touch = CreateSyntheticPointerDevice(PT_TOUCH, 2, POINTER_FEEDBACK_DEFAULT);

    private static void PenEvent(int x, int y, uint flags, float pressure, int tiltX, int tiltY)
    {
        var info = new POINTER_TYPE_INFO { type = PT_PEN };
        info.penInfo.pointerInfo.pointerType = PT_PEN;
        info.penInfo.pointerInfo.pointerId = 0;
        info.penInfo.pointerInfo.frameId = ++s_frame;
        info.penInfo.pointerInfo.pointerFlags = flags;
        info.penInfo.pointerInfo.ptPixelLocation = new POINT { X = x, Y = y };
        info.penInfo.penMask = PEN_MASK_PRESSURE | PEN_MASK_TILT_X | PEN_MASK_TILT_Y;
        info.penInfo.pressure = (uint)Math.Clamp(pressure * 1024, 0, 1024);
        info.penInfo.tiltX = tiltX;
        info.penInfo.tiltY = tiltY;
        if (!InjectSyntheticPointerInput(Pen, &info, 1))
            throw new InvalidOperationException($"Pen injection failed (error {Marshal.GetLastWin32Error()}). Is the window visible and is the desktop unlocked?");
    }

    /// <summary>Draws one handwriting-like stroke at the device report rate (~240 Hz).</summary>
    public static void PenStroke(int x0, int y0, int length, int samples, int tilt)
    {
        PenEvent(x0, y0, POINTER_FLAG_INRANGE | POINTER_FLAG_UPDATE, 0, tilt, 0);
        Thread.Sleep(10);
        for (int i = 0; i <= samples; i++)
        {
            double t = i / (double)samples;
            int x = x0 + (int)(t * length);
            int y = y0 + (int)(Math.Sin(t * Math.PI * 6) * 18);
            float p = (float)(0.25 + 0.65 * Math.Sin(t * Math.PI));
            uint flags = i == 0
                ? POINTER_FLAG_INRANGE | POINTER_FLAG_INCONTACT | POINTER_FLAG_DOWN | POINTER_FLAG_FIRSTBUTTON
                : POINTER_FLAG_INRANGE | POINTER_FLAG_INCONTACT | POINTER_FLAG_UPDATE | POINTER_FLAG_FIRSTBUTTON;
            PenEvent(x, y, flags, p, tilt, tilt / 3);
            Thread.Sleep(4);
        }
        PenEvent(x0 + length, y0, POINTER_FLAG_INRANGE | POINTER_FLAG_UP, 0, tilt, 0);
        Thread.Sleep(4);
        PenEvent(x0 + length, y0, POINTER_FLAG_UPDATE, 0, 0, 0); // leave range
    }

    private static void TouchFrame((int X, int Y, uint Flags)[] contacts)
    {
        var infos = stackalloc POINTER_TYPE_INFO[contacts.Length];
        uint frame = ++s_frame;
        for (int i = 0; i < contacts.Length; i++)
        {
            infos[i] = new POINTER_TYPE_INFO { type = PT_TOUCH };
            ref var ti = ref infos[i].touchInfo;
            ti.pointerInfo.pointerType = PT_TOUCH;
            ti.pointerInfo.pointerId = (uint)i;
            ti.pointerInfo.frameId = frame;
            ti.pointerInfo.pointerFlags = contacts[i].Flags | (i == 0 ? POINTER_FLAG_PRIMARY : 0);
            ti.pointerInfo.ptPixelLocation = new POINT { X = contacts[i].X, Y = contacts[i].Y };
            ti.touchMask = TOUCH_MASK_CONTACTAREA | TOUCH_MASK_PRESSURE;
            ti.rcContact = new RECT { Left = contacts[i].X - 4, Top = contacts[i].Y - 4, Right = contacts[i].X + 4, Bottom = contacts[i].Y + 4 };
            ti.pressure = 512;
        }
        if (!InjectSyntheticPointerInput(Touch, infos, (uint)contacts.Length))
            throw new InvalidOperationException($"Touch injection failed (error {Marshal.GetLastWin32Error()}).");
    }

    /// <summary>Two-finger drag (pan) followed by a flick, at 120 Hz.</summary>
    public static void TwoFingerPan(int cx, int cy, int dy, int frames)
    {
        const uint down = POINTER_FLAG_INRANGE | POINTER_FLAG_INCONTACT | POINTER_FLAG_DOWN;
        const uint move = POINTER_FLAG_INRANGE | POINTER_FLAG_INCONTACT | POINTER_FLAG_UPDATE;
        TouchFrame(new[] { (cx - 60, cy, down), (cx + 60, cy, down) });
        Thread.Sleep(8);
        for (int i = 1; i <= frames; i++)
        {
            int y = cy + dy * i / frames;
            TouchFrame(new[] { (cx - 60, y, move), (cx + 60, y, move) });
            Thread.Sleep(8);
        }
        int end = cy + dy;
        TouchFrame(new[] { (cx - 60, end, POINTER_FLAG_UP), (cx + 60, end, POINTER_FLAG_UP) });
    }

    /// <summary>Pinch zoom around a point.</summary>
    public static void Pinch(int cx, int cy, int startGap, int endGap, int frames)
    {
        const uint down = POINTER_FLAG_INRANGE | POINTER_FLAG_INCONTACT | POINTER_FLAG_DOWN;
        const uint move = POINTER_FLAG_INRANGE | POINTER_FLAG_INCONTACT | POINTER_FLAG_UPDATE;
        TouchFrame(new[] { (cx - startGap / 2, cy, down), (cx + startGap / 2, cy, down) });
        Thread.Sleep(8);
        int gap = startGap;
        for (int i = 1; i <= frames; i++)
        {
            gap = startGap + (endGap - startGap) * i / frames;
            TouchFrame(new[] { (cx - gap / 2, cy, move), (cx + gap / 2, cy, move) });
            Thread.Sleep(8);
        }
        TouchFrame(new[] { (cx - gap / 2, cy, POINTER_FLAG_UP), (cx + gap / 2, cy, POINTER_FLAG_UP) });
    }

    public static void Dispose()
    {
        if (s_pen != IntPtr.Zero) DestroySyntheticPointerDevice(s_pen);
        if (s_touch != IntPtr.Zero) DestroySyntheticPointerDevice(s_touch);
        s_pen = s_touch = IntPtr.Zero;
    }
}

/// <summary>Keyboard shortcuts and window resizing for the scenario.</summary>
internal static unsafe class Keys
{
    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct INPUT
    {
        [FieldOffset(0)] public uint type;
        [FieldOffset(8)] public KEYBDINPUT ki;
    }

    [DllImport("user32.dll")] private static extern uint SendInput(uint count, INPUT* inputs, int size);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect { public int Left, Top, Right, Bottom; }

    private const uint KEYEVENTF_KEYUP = 0x2;
    public const ushort VK_CONTROL = 0x11, VK_MENU = 0x12, VK_NEXT = 0x22, VK_PRIOR = 0x21, VK_N = 0x4E, VK_Z = 0x5A;

    public static void Chord(params ushort[] keys)
    {
        var inputs = stackalloc INPUT[keys.Length * 2];
        for (int i = 0; i < keys.Length; i++) inputs[i] = new INPUT { type = 1, ki = new KEYBDINPUT { wVk = keys[i] } };
        for (int i = 0; i < keys.Length; i++) inputs[keys.Length + i] = new INPUT { type = 1, ki = new KEYBDINPUT { wVk = keys[keys.Length - 1 - i], dwFlags = KEYEVENTF_KEYUP } };
        SendInput((uint)(keys.Length * 2), inputs, sizeof(INPUT));
    }

    public static void Resize(IntPtr hwnd, int width, int height)
    {
        GetWindowRect(hwnd, out var r);
        SetWindowPos(hwnd, IntPtr.Zero, r.Left, r.Top, width, height, 0x0004 /*SWP_NOZORDER*/ | 0x0010 /*SWP_NOACTIVATE*/);
    }
}
