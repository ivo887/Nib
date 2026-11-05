using System;
using System.Collections.Generic;
using Windows.System;

namespace Nib.App.Services;

/// <summary>
/// Keyboard shortcuts. Defaults follow OneNote; every one can be remapped in settings.json ("KeyMap":
/// {"NewPage": "Ctrl+Shift+N"}). A remapping UI arrives in v1.
/// </summary>
internal static class KeyMap
{
    public static readonly (string Action, string Gesture)[] Defaults =
    {
        ("NewPage", "Ctrl+N"),
        ("NewSubpage", "Ctrl+Alt+N"),
        ("NewSection", "Ctrl+T"),
        ("Undo", "Ctrl+Z"),
        ("Redo", "Ctrl+Y"),
        ("NextPage", "Ctrl+PageDown"),
        ("PreviousPage", "Ctrl+PageUp"),
        ("DemotePage", "Ctrl+Alt+]"),
        ("PromotePage", "Ctrl+Alt+["),
        ("ZoomIn", "Ctrl+Alt+Plus"),
        ("ZoomOut", "Ctrl+Alt+Minus"),
        ("Zoom100", "Ctrl+Alt+0"),
        ("FullPage", "F11"),
        ("Copy", "Ctrl+C"),
        ("Cut", "Ctrl+X"),
        ("Paste", "Ctrl+V"),
        ("Delete", "Delete"),
        ("Tag1", "Ctrl+1"), ("Tag2", "Ctrl+2"), ("Tag3", "Ctrl+3"), ("Tag4", "Ctrl+4"), ("Tag5", "Ctrl+5"),
        ("Tag6", "Ctrl+6"), ("Tag7", "Ctrl+7"), ("Tag8", "Ctrl+8"), ("Tag9", "Ctrl+9"),
        ("ExportPdf", "Ctrl+Shift+E"),
        ("ToggleSidebar", "Ctrl+Shift+B"),
        ("DebugOverlay", "Ctrl+Alt+Shift+F12"),
    };

    /// <summary>Actions that belong to the page and must not steal keys from the text editor.</summary>
    public static readonly HashSet<string> PageOnly = new() { "Undo", "Redo", "Copy", "Cut", "Paste", "Delete", "Tag1", "Tag2", "Tag3", "Tag4", "Tag5", "Tag6", "Tag7", "Tag8", "Tag9" };

    public static IEnumerable<(string Action, VirtualKeyModifiers Modifiers, VirtualKey Key)> Resolve(IReadOnlyDictionary<string, string> overrides)
    {
        foreach (var (action, gesture) in Defaults)
        {
            string g = overrides.TryGetValue(action, out var o) && !string.IsNullOrWhiteSpace(o) ? o : gesture;
            if (TryParse(g, out var mods, out var key)) yield return (action, mods, key);
        }
    }

    public static bool TryParse(string gesture, out VirtualKeyModifiers modifiers, out VirtualKey key)
    {
        modifiers = VirtualKeyModifiers.None;
        key = VirtualKey.None;
        var parts = gesture.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return false;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i].ToLowerInvariant())
            {
                case "ctrl": case "control": modifiers |= VirtualKeyModifiers.Control; break;
                case "alt": case "menu": modifiers |= VirtualKeyModifiers.Menu; break;
                case "shift": modifiers |= VirtualKeyModifiers.Shift; break;
                case "win": modifiers |= VirtualKeyModifiers.Windows; break;
                default: return false;
            }
        }
        string k = parts[^1];
        switch (k.ToLowerInvariant())
        {
            case "plus": key = (VirtualKey)187; return true;   // VK_OEM_PLUS
            case "minus": key = (VirtualKey)189; return true;  // VK_OEM_MINUS
            case "]": key = (VirtualKey)221; return true;      // VK_OEM_6
            case "[": key = (VirtualKey)219; return true;      // VK_OEM_4
        }
        if (k.Length == 1 && char.IsDigit(k[0])) { key = VirtualKey.Number0 + (k[0] - '0'); return true; }
        if (k.Length == 1 && char.IsLetter(k[0])) { key = VirtualKey.A + (char.ToUpperInvariant(k[0]) - 'A'); return true; }
        return Enum.TryParse(k, ignoreCase: true, out key);
    }
}
