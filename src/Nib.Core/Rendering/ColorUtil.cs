using System;
using System.Runtime.CompilerServices;

namespace Nib.Core.Rendering;

/// <summary>ARGB helpers shared by the screen renderer and exporters.</summary>
public static class ColorUtil
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte A(uint c) => (byte)(c >> 24);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte R(uint c) => (byte)(c >> 16);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte G(uint c) => (byte)(c >> 8);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte B(uint c) => (byte)c;

    public static uint Argb(byte a, byte r, byte g, byte b) => ((uint)a << 24) | ((uint)r << 16) | ((uint)g << 8) | b;

    /// <summary>The color an ink of the given strength leaves on white paper: c' = 1 − s·(1 − c).</summary>
    public static uint OverWhite(uint color, float strength)
    {
        float s = Math.Clamp(strength, 0f, 1f);
        return Argb(255,
            (byte)MathF.Round(255f - s * (255 - R(color))),
            (byte)MathF.Round(255f - s * (255 - G(color))),
            (byte)MathF.Round(255f - s * (255 - B(color))));
    }

    /// <summary>
    /// Dark-mode display transform: inverts lightness while keeping hue, so black ink on white paper
    /// becomes light ink on dark paper and colored ink stays recognizably the same color.
    /// Display only: exports always use the stored colors.
    /// </summary>
    public static uint ToDarkMode(uint color) => ToDarkMode(color, trueBlack: false);

    /// <summary>
    /// <paramref name="trueBlack"/>: white paper maps to pure black (#000) for OLED panels, where black
    /// pixels are switched off; ink keeps the same light tones as the default mapping.
    /// </summary>
    public static uint ToDarkMode(uint color, bool trueBlack)
    {
        float r = R(color) / 255f, g = G(color) / 255f, b = B(color) / 255f;
        float max = MathF.Max(r, MathF.Max(g, b)), min = MathF.Min(r, MathF.Min(g, b));
        float l = (max + min) * 0.5f;
        float h = 0, s = 0;
        float d = max - min;
        if (d > 1e-5f)
        {
            s = l > 0.5f ? d / (2f - max - min) : d / (max + min);
            if (max == r) h = (g - b) / d + (g < b ? 6 : 0);
            else if (max == g) h = (b - r) / d + 2;
            else h = (r - g) / d + 4;
            h /= 6f;
        }
        // Map lightness 1 → 0.12 (paper; 0 for true black) and 0 → 0.9 (ink); keep mid tones readable.
        float nl = trueBlack ? 0.9f - l * 0.9f : 0.9f - l * 0.78f;
        float ns = s * 0.85f;
        HslToRgb(h, ns, nl, out r, out g, out b);
        return Argb(A(color), (byte)MathF.Round(r * 255), (byte)MathF.Round(g * 255), (byte)MathF.Round(b * 255));
    }

    private static void HslToRgb(float h, float s, float l, out float r, out float g, out float b)
    {
        if (s <= 1e-5f) { r = g = b = l; return; }
        float q = l < 0.5f ? l * (1 + s) : l + s - l * s;
        float p = 2 * l - q;
        r = Hue(p, q, h + 1f / 3f);
        g = Hue(p, q, h);
        b = Hue(p, q, h - 1f / 3f);
    }

    private static float Hue(float p, float q, float t)
    {
        if (t < 0) t += 1;
        if (t > 1) t -= 1;
        if (t < 1f / 6f) return p + (q - p) * 6 * t;
        if (t < 0.5f) return q;
        if (t < 2f / 3f) return p + (q - p) * (2f / 3f - t) * 6;
        return p;
    }

    /// <summary>Relative luminance 0..1 (sRGB, approximate).</summary>
    public static float Luminance(uint c) => (0.2126f * R(c) + 0.7152f * G(c) + 0.0722f * B(c)) / 255f;

    public static string ToHex(uint c) => $"#{R(c):X2}{G(c):X2}{B(c):X2}";
}
