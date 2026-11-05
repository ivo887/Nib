using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Nib.App.Platform;

/// <summary>
/// Turns off Windows' own pen feedback visuals (tap ripples, the press-and-hold ring, the barrel-button
/// indicator) for Nib's window and its child windows. Nib already shows its own nib-shaped hover cursor,
/// and the system ring appearing while you rest the pen to start a dot is visual noise on a writing
/// surface. Touch feedback is left alone.
/// </summary>
internal static unsafe class WindowFeedback
{
    // FEEDBACK_TYPE: PEN_BARRELVISUALIZATION 2, PEN_TAP 3, PEN_DOUBLETAP 4, PEN_PRESSANDHOLD 5, PEN_RIGHTTAP 6
    private static readonly int[] PenFeedback = { 2, 3, 4, 5, 6 };

    [DllImport("user32.dll")]
    private static extern bool SetWindowFeedbackSetting(IntPtr hwnd, int feedback, uint flags, uint size, int* configuration);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr parent, delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int> callback, IntPtr lParam);

    public static void DisablePenVisuals(IntPtr hwnd)
    {
        try
        {
            Apply(hwnd);
            EnumChildWindows(hwnd, &OnChild, IntPtr.Zero);
        }
        catch (Exception) { /* cosmetic only */ }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int OnChild(IntPtr hwnd, IntPtr lParam)
    {
        try { Apply(hwnd); } catch { }
        return 1; // continue enumeration
    }

    private static void Apply(IntPtr hwnd)
    {
        int off = 0;
        foreach (int f in PenFeedback) SetWindowFeedbackSetting(hwnd, f, 0, sizeof(int), &off);
    }
}
