using System;
using System.Collections.Generic;

namespace Nib.Core.Layout;

/// <summary>Integer rectangle in physical screen pixels.</summary>
public readonly record struct RectI(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public RectI Intersect(RectI o)
    {
        int l = Math.Max(X, o.X), t = Math.Max(Y, o.Y), r = Math.Min(Right, o.Right), b = Math.Min(Bottom, o.Bottom);
        return r > l && b > t ? new RectI(l, t, r - l, b - t) : default;
    }

    public bool Contains(RectI o) => o.X >= X && o.Y >= Y && o.Right <= Right && o.Bottom <= Bottom;
}

public enum ScreenEdge { None, Left, Top, Right, Bottom }

/// <summary>Insets in DIPs.</summary>
public readonly record struct Insets(double Left, double Top, double Right, double Bottom)
{
    public static readonly Insets Zero = new(0, 0, 0, 0);
}

/// <summary>Where the toolbar docks. Left/right docking keeps tools away from the writing hand.</summary>
public enum DockSide : byte { Top = 0, Bottom = 1, Left = 2, Right = 3 }

/// <summary>Everything the safe-area calculation needs, captured on the UI thread.</summary>
public readonly record struct ScreenContext(
    RectI ClientRectScreen,
    RectI MonitorRect,
    RectI WorkArea,
    bool IsFullScreen,
    bool IsMaximized,
    ScreenEdge AutoHideTaskbarEdge,
    RectI InputPaneOccluded,
    double RasterizationScale);

/// <summary>
/// Computes the insets that keep the toolbar, sidebar and page edges clear of the taskbar, an auto-hide
/// taskbar's reveal strip, the touch keyboard and the handwriting panel. Pure function, unit-tested
/// for every taskbar edge.
/// </summary>
public static class SafeAreaCalculator
{
    /// <summary>Width of the strip kept free at an auto-hide taskbar edge, in DIPs.</summary>
    public const double AutoHideStrip = 2;

    public static Insets Compute(in ScreenContext c)
    {
        double s = c.RasterizationScale <= 0 ? 1 : c.RasterizationScale;
        var client = c.ClientRectScreen;
        if (client.IsEmpty) return Insets.Zero;

        double l = 0, t = 0, r = 0, b = 0;

        // 1. Never lay out under a visible taskbar: any part of the client outside the work area is unsafe.
        //    (A full-screen window covers the taskbar by design, so the monitor rect is the limit there.)
        var safe = c.IsFullScreen ? c.MonitorRect : c.WorkArea;
        if (!safe.IsEmpty)
        {
            l = Math.Max(l, (safe.X - client.X) / s);
            t = Math.Max(t, (safe.Y - client.Y) / s);
            r = Math.Max(r, (client.Right - safe.Right) / s);
            b = Math.Max(b, (client.Bottom - safe.Bottom) / s);
        }

        // 2. Auto-hide taskbar: when our client touches that monitor edge, keep a thin strip free so the
        //    pen or mouse reaching the edge reveals the taskbar instead of pressing one of our buttons.
        if (c.AutoHideTaskbarEdge != ScreenEdge.None && !c.IsFullScreen && !c.MonitorRect.IsEmpty)
        {
            const int touch = 2;
            switch (c.AutoHideTaskbarEdge)
            {
                case ScreenEdge.Left when client.X <= c.MonitorRect.X + touch: l = Math.Max(l, AutoHideStrip); break;
                case ScreenEdge.Top when client.Y <= c.MonitorRect.Y + touch: t = Math.Max(t, AutoHideStrip); break;
                case ScreenEdge.Right when client.Right >= c.MonitorRect.Right - touch: r = Math.Max(r, AutoHideStrip); break;
                case ScreenEdge.Bottom when client.Bottom >= c.MonitorRect.Bottom - touch: b = Math.Max(b, AutoHideStrip); break;
            }
        }

        // 3. Touch keyboard / handwriting panel: inset the side the panel covers.
        var occ = c.InputPaneOccluded.Intersect(client);
        if (!occ.IsEmpty)
        {
            bool spansWidth = occ.Width >= client.Width * 0.6;
            if (spansWidth && occ.Bottom >= client.Bottom - 1) b = Math.Max(b, (client.Bottom - occ.Y) / s);
            else if (spansWidth && occ.Y <= client.Y + 1) t = Math.Max(t, (occ.Bottom - client.Y) / s);
            else if (occ.X <= client.X + 1) l = Math.Max(l, (occ.Right - client.X) / s);
            else if (occ.Right >= client.Right - 1) r = Math.Max(r, (client.Right - occ.X) / s);
            else b = Math.Max(b, (client.Bottom - occ.Y) / s); // floating panel: assume it covers the lower part
        }

        return new Insets(Round(l), Round(t), Round(r), Round(b));
    }

    private static double Round(double v) => Math.Max(0, Math.Ceiling(v * 2) / 2);
}

/// <summary>A window position remembered per monitor, relative to that monitor's work area so it survives
/// taskbar moves, resolution and scaling changes.</summary>
public sealed record SavedPlacement(double RelX, double RelY, double RelWidth, double RelHeight, bool Maximized, bool FullScreen);

public static class PlacementCalculator
{
    /// <summary>Smallest window we restore to, physical pixels.</summary>
    public const int MinWidth = 480, MinHeight = 360;

    /// <summary>Moves (and if needed shrinks) a window rect so it lies entirely inside the work area.</summary>
    public static RectI ClampToWorkArea(RectI window, RectI workArea)
    {
        int w = Math.Clamp(window.Width, Math.Min(MinWidth, workArea.Width), workArea.Width);
        int h = Math.Clamp(window.Height, Math.Min(MinHeight, workArea.Height), workArea.Height);
        int x = Math.Clamp(window.X, workArea.X, workArea.Right - w);
        int y = Math.Clamp(window.Y, workArea.Y, workArea.Bottom - h);
        return new RectI(x, y, w, h);
    }

    public static SavedPlacement ToSaved(RectI window, RectI workArea, bool maximized, bool fullScreen) =>
        new((window.X - workArea.X) / (double)workArea.Width,
            (window.Y - workArea.Y) / (double)workArea.Height,
            window.Width / (double)workArea.Width,
            window.Height / (double)workArea.Height,
            maximized, fullScreen);

    public static RectI FromSaved(SavedPlacement p, RectI workArea)
    {
        var r = new RectI(
            workArea.X + (int)Math.Round(p.RelX * workArea.Width),
            workArea.Y + (int)Math.Round(p.RelY * workArea.Height),
            (int)Math.Round(p.RelWidth * workArea.Width),
            (int)Math.Round(p.RelHeight * workArea.Height));
        return ClampToWorkArea(r, workArea);
    }

    /// <summary>Default first-run placement: 80% of the work area, centered, portrait-friendly on tall screens.</summary>
    public static RectI DefaultFor(RectI workArea)
    {
        int w = (int)(workArea.Width * 0.8), h = (int)(workArea.Height * 0.85);
        return ClampToWorkArea(new RectI(workArea.X + (workArea.Width - w) / 2, workArea.Y + (workArea.Height - h) / 2, w, h), workArea);
    }
}

/// <summary>
/// Chooses how the shell arranges itself for the available size: on narrow or portrait windows the
/// sidebar overlays instead of pushing the page, and a top/bottom toolbar switches to its compact form.
/// </summary>
public static class ResponsiveLayout
{
    public const double CompactWidth = 720;
    public const double SidebarWidth = 280;
    public const double SidebarStrip = 44;

    public readonly record struct ShellLayout(bool SidebarOverlays, bool CompactToolbar, double SidebarWidth);

    public static ShellLayout Decide(double widthDips, double heightDips, DockSide dock)
    {
        bool portrait = heightDips > widthDips;
        bool narrow = widthDips < CompactWidth;
        bool compactToolbar = (dock is DockSide.Top or DockSide.Bottom) ? widthDips < 560 : heightDips < 560;
        double sidebar = Math.Min(SidebarWidth, Math.Max(220, widthDips * 0.3));
        return new ShellLayout(SidebarOverlays: narrow || (portrait && widthDips < 1000), CompactToolbar: compactToolbar, SidebarWidth: sidebar);
    }
}

/// <summary>Per-monitor placement memory keyed by a stable monitor identity string.</summary>
public sealed class PlacementStore
{
    public Dictionary<string, SavedPlacement> ByMonitor { get; set; } = new();
    public string? LastMonitor { get; set; }

    public void Remember(string monitorKey, SavedPlacement placement)
    {
        ByMonitor[monitorKey] = placement;
        LastMonitor = monitorKey;
    }
}
