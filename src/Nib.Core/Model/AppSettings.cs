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

    // Layout
    public DockSide ToolbarDock { get; set; } = DockSide.Top;
    public bool SidebarOnRight { get; set; }
    public bool SidebarCollapsed { get; set; }

    // Appearance
    public ThemeMode Theme { get; set; } = ThemeMode.System;
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
