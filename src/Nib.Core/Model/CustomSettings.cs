using System.Collections.Generic;

namespace Nib.Core.Model;

public enum PointerStyle : byte
{
    /// <summary>Outline of the nib footprint at the current tilt (the original behaviour).</summary>
    Nib = 0,
    Dot = 1,
    Crosshair = 2,
    Ring = 3,
    /// <summary>No Nib-drawn pointer at all.</summary>
    None = 4,
}

/// <summary>Material behind the window chrome. Mica picks up the wallpaper tint (Windows 11).</summary>
public enum BackdropKind : byte { Mica = 0, MicaAlt = 1, Acrylic = 2, None = 3 }

/// <summary>The Windows cursor shown over the page. Automatic hides it whenever Nib draws its own pointer.</summary>
public enum SystemCursorMode : byte { Auto = 0, Cross = 1, Arrow = 2, Hidden = 3 }

/// <summary>File format for exporting pages.</summary>
public enum ExportFormat : byte { Pdf = 0, Png = 1, Svg = 2 }

public enum PointerSizeMode : byte { FollowPen = 0, Fixed = 1 }
public enum PointerColorMode : byte { PenColor = 0, Custom = 1 }

/// <summary>How the hover pointer looks while the pen (or mouse) is over the page. Applied live.</summary>
public sealed record PointerSettings
{
    public PointerStyle Style { get; init; } = PointerStyle.Nib;
    /// <summary>The ordinary Windows cursor (the cross) shown under or instead of the Nib pointer.</summary>
    public SystemCursorMode SystemCursor { get; init; } = SystemCursorMode.Auto;
    public PointerSizeMode SizeMode { get; init; } = PointerSizeMode.FollowPen;
    /// <summary>Diameter on screen in DIPs when <see cref="SizeMode"/> is Fixed.</summary>
    public float FixedSize { get; init; } = 14f;
    public PointerColorMode ColorMode { get; init; } = PointerColorMode.PenColor;
    /// <summary>ARGB, used when <see cref="ColorMode"/> is Custom.</summary>
    public uint CustomColor { get; init; } = 0xFF2B6CB0;
    /// <summary>0.1 to 1.</summary>
    public float Opacity { get; init; } = 1f;
    /// <summary>Outline thickness in DIPs.</summary>
    public float LineWidth { get; init; } = 1f;
    /// <summary>A small dot at the exact contact point, drawn on top of any style.</summary>
    public bool CenterDot { get; init; }
    /// <summary>Keep showing the pointer while the pen is touching the page. Off: it shows only while hovering.</summary>
    public bool ShowWhileDrawing { get; init; }

    public PointerSettings Sanitized() => this with
    {
        FixedSize = System.Math.Clamp(FixedSize, 4f, 80f),
        Opacity = System.Math.Clamp(Opacity, 0.1f, 1f),
        LineWidth = System.Math.Clamp(LineWidth, 0.5f, 4f),
    };
}

/// <summary>How the pen's tilt reaches the brushes: on/off, a dead zone for an upright pen, and axis inversion for digitizers that report it flipped.</summary>
public sealed record TiltSettings
{
    public bool Enabled { get; init; } = true;
    /// <summary>Leans smaller than this (degrees) count as upright, so a held-still pen does not jitter the nib.</summary>
    public float DeadZoneDeg { get; init; } = 4f;
    public bool InvertX { get; init; }
    public bool InvertY { get; init; }

    /// <summary>Applies the user's tilt preferences to the raw XTilt/YTilt a pen reports (degrees, -90..90).</summary>
    public (float X, float Y) Apply(float x, float y)
    {
        if (!Enabled) return (0f, 0f);
        if (InvertX) x = -x;
        if (InvertY) y = -y;
        float dead = System.Math.Clamp(DeadZoneDeg, 0f, 30f);
        float mag = System.MathF.Sqrt(x * x + y * y);
        if (mag <= dead || mag < 1e-4f) return (0f, 0f);
        // Remove the dead zone but keep the full 90 degree range reachable.
        float scaled = (mag - dead) * (90f / (90f - dead));
        float k = scaled / mag;
        return (x * k, y * k);
    }
}

/// <summary>The buttons the toolbar can show. Settings keeps an ordered list of these ids.</summary>
public static class ToolbarLayout
{
    public const string Separator = "|";

    /// <summary>Every id the toolbar understands. "|" may appear any number of times, the rest at most once.</summary>
    public static readonly string[] AllIds =
    {
        "pens", "add-pen", "highlighter", "eraser", "lasso", "text", "snap",
        "undo", "redo",
        "zoom", "insert-space", "page-setup", "export", "focus", "sidebar", "settings",
        "nav", "exit-focus",
        "more",
    };

    public static readonly string[] Default =
    {
        "pens", "add-pen", Separator,
        "highlighter", "eraser", "lasso", "text", "snap", Separator,
        "undo", "redo", Separator,
        "zoom", "insert-space", "focus", Separator,
        "settings", "more",
    };

    /// <summary>Bump when a new default button is added; older settings files get it once (see <see cref="Migrate"/>).</summary>
    public const int CurrentVersion = 2;

    /// <summary>
    /// Brings an older toolbar up to date: version 2 added the full-screen focus button, which is placed just before the menu so
    /// people who customized the toolbar get it too (and can still remove it).
    /// </summary>
    public static List<string> Migrate(IEnumerable<string>? items, int version)
    {
        var list = Normalize(items);
        if (version < 2 && !list.Contains("focus"))
        {
            int more = list.IndexOf("more");
            list.Insert(more < 0 ? list.Count : more, "focus");
        }
        return Normalize(list);
    }

    /// <summary>
    /// The toolbar as the full-screen dock shows it: the notebook/section/page navigator first, the user's own buttons, then
    /// the sidebar and exit-focus buttons just before the menu. Nothing the user chose is dropped.
    /// </summary>
    public static List<string> ForFocus(IEnumerable<string> items)
    {
        var list = Normalize(items);
        list.RemoveAll(i => i is "nav" or "sidebar" or "exit-focus" or "focus"); // the dock has its own exit button
        // Collapse separators left doubled or dangling by the removals.
        list = Normalize(list);
        int more = list.IndexOf("more");
        list.InsertRange(more, new[] { Separator, "sidebar", "exit-focus" });
        list.InsertRange(0, new[] { "nav", Separator });
        return Normalize(list);
    }

    /// <summary>Drops unknown ids and duplicates, collapses repeated or edge separators, and always keeps the menu.</summary>
    public static List<string> Normalize(IEnumerable<string>? ids)
    {
        var result = new List<string>();
        var seen = new HashSet<string>();
        var known = new HashSet<string>(AllIds);
        if (ids is not null)
        {
            foreach (var id in ids)
            {
                if (id == Separator)
                {
                    if (result.Count > 0 && result[^1] != Separator) result.Add(Separator);
                }
                else if (known.Contains(id) && seen.Add(id)) result.Add(id);
            }
        }
        while (result.Count > 0 && result[^1] == Separator) result.RemoveAt(result.Count - 1);
        // The "more" menu is the only way to reach everything that isn't on the bar, so it can't be removed.
        if (!seen.Contains("more")) result.Add("more");
        return result;
    }
}
