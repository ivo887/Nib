using System.Collections.Generic;
using Nib.Core.Ink;
using Nib.Core.Layout;

namespace Nib.Core.Model;

public enum BarrelButtonAction : byte { Eraser = 0, Lasso = 1, RightClick = 2, None = 3 }
public enum EraserMode : byte { Stroke = 0, Pixel = 1 }
public enum ThemeMode : byte { System = 0, Light = 1, Dark = 2 }

/// <summary>
/// User settings. Defaults are chosen so most people never open the settings page.
/// Stored as JSON (source-generated serializer, AOT safe) in the app's local data folder.
/// </summary>
public sealed class AppSettings
{
    public List<PenPreset> Pens { get; set; } = new(PenPreset.DefaultFavorites());
    public PenPreset Highlighter { get; set; } = PenPreset.DefaultHighlighter();
    public int ActivePen { get; set; }

    // Pen & touch
    public bool TouchDraws { get; set; }
    public BarrelButtonAction BarrelButton { get; set; } = BarrelButtonAction.Eraser;
    public EraserMode EraserMode { get; set; } = EraserMode.Stroke;
    /// <summary>Eraser diameter on screen, DIPs (constant regardless of zoom).</summary>
    public float EraserSize { get; set; } = 14f;
    /// <summary>How far ahead the stroke is predicted, ms. 0 disables prediction. Used when <see cref="AutoPrediction"/> is off.</summary>
    public float PredictionMs { get; set; } = 15f;
    /// <summary>Choose the prediction horizon from the display's current refresh rate (60 Hz → 15 ms, 120 Hz → 9 ms).</summary>
    public bool AutoPrediction { get; set; } = true;
    /// <summary>
    /// While the pen is near the screen: ask Windows for the panel's high refresh rate (displays with
    /// Dynamic refresh rate) and keep the ink thread on performance cores (hybrid CPUs such as Lunar Lake).
    /// </summary>
    public bool PerformanceModeWhileWriting { get; set; } = true;

    /// <summary>Palm rejection: touch is ignored while the pen is near and for this long after it leaves (ms).</summary>
    public int PalmRejectionMs { get; set; } = 450;
    /// <summary>Touch contacts wider or taller than this (DIPs) are treated as a palm.</summary>
    public float PalmSizeDips { get; set; } = 34f;
    public bool TwoFingerTapUndo { get; set; } = true;
    public bool ThreeFingerTapRedo { get; set; } = true;

    /// <summary>Hold the pen still at the end of a stroke and it snaps to a line, circle, ellipse, triangle or rectangle.</summary>
    public bool ShapeSnap { get; set; } = true;
    /// <summary>How long the pen must rest before it snaps (ms).</summary>
    public int ShapeSnapHoldMs { get; set; } = 600;
    /// <summary>Snapped lines and rectangles lock to horizontal, vertical and 45 degrees when close.</summary>
    public bool SnapLineAngles { get; set; } = true;

    // Saving and exporting
    /// <summary>Format used by the Export commands.</summary>
    public ExportFormat ExportFormat { get; set; } = ExportFormat.Pdf;
    /// <summary>Resolution for PNG export (dots per inch).</summary>
    public int PngDpi { get; set; } = 150;
    /// <summary>Paper patterns (lines, grids) are included in exports.</summary>
    public bool ExportBackground { get; set; } = true;
    /// <summary>When true every export asks where to save; when false it goes to <see cref="ExportFolder"/>.</summary>
    public bool AskExportLocation { get; set; } = true;
    /// <summary>Default export folder; null uses the Documents folder.</summary>
    public string? ExportFolder { get; set; }

    /// <summary>Pen tilt: on/off, dead zone and axis inversion.</summary>
    public TiltSettings Tilt { get; set; } = new();

    /// <summary>Hover pointer: style, size, color.</summary>
    public PointerSettings Pointer { get; set; } = new();

    /// <summary>Toolbar buttons, in order ("|" is a divider). See <see cref="ToolbarLayout"/>.</summary>
    public List<string> ToolbarItems { get; set; } = new(ToolbarLayout.Default);
    /// <summary>Which defaults the toolbar list has been migrated to (see <see cref="ToolbarLayout.Migrate"/>).</summary>
    public int ToolbarVersion { get; set; }
    /// <summary>Toolbar button size in DIPs; 0 picks it automatically (larger in tablet posture).</summary>
    public int ToolbarButtonSize { get; set; }

    // Layout
    public DockSide ToolbarDock { get; set; } = DockSide.Top;
    public bool SidebarOnRight { get; set; }
    public bool SidebarCollapsed { get; set; }
    /// <summary>Show the notebook rail as a thin strip of badges even when the window is wide.</summary>
    public bool RailCompact { get; set; }
    /// <summary>Keep the page list closed (it opens as a drawer from the section bar).</summary>
    public bool PagesHidden { get; set; }

    // Appearance
    public ThemeMode Theme { get; set; } = ThemeMode.System;
    /// <summary>Window material: Mica (default), Mica Alt, Acrylic, or a plain solid color.</summary>
    public BackdropKind Backdrop { get; set; } = BackdropKind.Mica;
    /// <summary>In dark theme, show pages inverted (dark paper, light ink). Exports keep real colors.</summary>
    public bool InvertPagesInDarkTheme { get; set; } = true;
    /// <summary>Dark pages use pure black paper, so OLED pixels switch off (less power, no grey glow).</summary>
    public bool OledBlackPages { get; set; }

    // Pages
    public PageDefaults Defaults { get; set; } = PageDefaults.AppDefaults;

    /// <summary>Keyboard shortcut overrides: action id → gesture ("Ctrl+Shift+N").</summary>
    public Dictionary<string, string> KeyMap { get; set; } = new();

    public PlacementStore Placement { get; set; } = new();
    public long? LastPageId { get; set; }

    /// <summary>Developer overlay (frame time, ink latency, UI stalls). Toggle: Ctrl+Alt+Shift+F12.</summary>
    public bool DebugOverlay { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public PenPreset ActivePenPreset => Pens.Count == 0 ? PenPreset.DefaultFavorites()[0] : Pens[System.Math.Clamp(ActivePen, 0, Pens.Count - 1)];
}
