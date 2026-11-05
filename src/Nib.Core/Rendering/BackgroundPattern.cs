using System;
using Nib.Core.Geometry;
using Nib.Core.Model;

namespace Nib.Core.Rendering;

/// <summary>Receives paper primitives. Implemented by the Win2D tile renderer and by the PDF exporter.</summary>
public interface IBackgroundSink
{
    void FillRect(RectF rect, uint color);
    void Line(float x0, float y0, float x1, float y1, float width, uint color);
    void Dot(float cx, float cy, float radius, uint color);
}

/// <summary>
/// Generates paper patterns (lined, squares, dot grid, graph, Cornell) for just the visible region,
/// so drawing cost is proportional to what is on screen, with level-of-detail that drops minor lines
/// when they would be denser than a few pixels.
/// </summary>
public static class BackgroundPattern
{
    public const float LineWidth = 0.75f;
    public const float MajorLineWidth = 1.1f;
    public const float MarginWidth = 1.0f;
    /// <summary>Top margin before the first rule on lined and Cornell paper (1 inch).</summary>
    public const float HeaderHeight = 96f;
    /// <summary>Margin line position (1.25 inch).</summary>
    public const float MarginX = 120f;
    /// <summary>Cornell cue column width (2.5 inch).</summary>
    public const float CornellCueWidth = 240f;
    /// <summary>Cornell summary area height (2 inch).</summary>
    public const float CornellSummaryHeight = 192f;

    /// <param name="dipsPerUnit">Current zoom (screen DIPs per page unit) for level of detail. Use 1 for export.</param>
    public static void Emit(PageLayout layout, RectF visible, float dipsPerUnit, IBackgroundSink sink)
    {
        var bg = layout.Background;
        if (visible.IsEmpty) return;
        if (layout.Mode == PageMode.Infinite)
        {
            sink.FillRect(visible, bg.PaperColor);
            EmitPattern(bg, visible, visible, dipsPerUnit, sink, infinite: true);
            return;
        }
        float sh = layout.SheetHeight;
        int first = Math.Max(0, (int)MathF.Floor(visible.Top / sh));
        int last = Math.Min(layout.SheetCount - 1, (int)MathF.Floor(visible.Bottom / sh));
        for (int i = first; i <= last; i++)
        {
            var sheet = layout.SheetRect(i);
            var clip = sheet.Intersect(visible);
            if (clip.IsEmpty) continue;
            sink.FillRect(clip, bg.PaperColor);
            EmitPattern(bg, sheet, clip, dipsPerUnit, sink, infinite: false);
        }
    }

    private static void EmitPattern(BackgroundSpec bg, RectF sheet, RectF clip, float zoom, IBackgroundSink sink, bool infinite)
    {
        float spacing = MathF.Max(bg.Spacing, 2f);
        bool minorVisible = spacing * zoom >= 3.5f;
        switch (bg.Kind)
        {
            case BackgroundKind.Lined:
            {
                float top = infinite ? float.NegativeInfinity : sheet.Top + HeaderHeight;
                if (minorVisible) HorizontalRules(sheet, clip, top, sheet.Bottom, spacing, sheet.Left, sheet.Right, bg.LineColor, LineWidth, sink, infinite);
                if (bg.MarginLine) VerticalLine(sheet.Left + MarginX, clip, sheet, bg.MarginColor, MarginWidth, sink, infinite);
                break;
            }
            case BackgroundKind.Grid:
                if (minorVisible) Grid(sheet, clip, spacing, bg.LineColor, LineWidth, sink, infinite, majorEvery: 0, majorColor: 0);
                break;
            case BackgroundKind.Graph:
            {
                int major = Math.Max(2, bg.MajorEvery);
                if (minorVisible) Grid(sheet, clip, spacing, bg.LineColor, LineWidth * 0.8f, sink, infinite, major, bg.MajorLineColor);
                else if (spacing * major * zoom >= 3.5f) Grid(sheet, clip, spacing * major, bg.MajorLineColor, MajorLineWidth, sink, infinite, 0, 0);
                break;
            }
            case BackgroundKind.Dots:
                if (minorVisible) Dots(sheet, clip, spacing, bg.LineColor, sink, infinite);
                break;
            case BackgroundKind.Cornell:
            {
                if (infinite)
                {
                    if (minorVisible) HorizontalRules(sheet, clip, float.NegativeInfinity, float.PositiveInfinity, spacing, clip.Left, clip.Right, bg.LineColor, LineWidth, sink, true);
                    break;
                }
                float summaryTop = sheet.Bottom - CornellSummaryHeight;
                float headerY = sheet.Top + HeaderHeight;
                if (minorVisible) HorizontalRules(sheet, clip, headerY, summaryTop, spacing, sheet.Left, sheet.Right, bg.LineColor, LineWidth, sink, false);
                // Structure lines are darker: title line, cue column, summary line.
                HLine(headerY, sheet.Left, sheet.Right, clip, bg.MajorLineColor, MajorLineWidth, sink);
                HLine(summaryTop, sheet.Left, sheet.Right, clip, bg.MajorLineColor, MajorLineWidth, sink);
                float cueX = sheet.Left + CornellCueWidth;
                if (cueX >= clip.Left && cueX <= clip.Right)
                {
                    float y0 = MathF.Max(headerY, clip.Top), y1 = MathF.Min(summaryTop, clip.Bottom);
                    if (y1 > y0) sink.Line(cueX, y0, cueX, y1, MajorLineWidth, bg.MajorLineColor);
                }
                break;
            }
        }
    }

    private static void HorizontalRules(RectF sheet, RectF clip, float top, float bottom, float spacing, float left, float right,
        uint color, float width, IBackgroundSink sink, bool infinite)
    {
        float x0 = MathF.Max(left, clip.Left), x1 = MathF.Min(right, clip.Right);
        if (x1 <= x0) return;
        float origin = infinite ? 0f : top;
        float start = MathF.Max(clip.Top, infinite ? clip.Top : top);
        int k = (int)MathF.Ceiling((start - origin) / spacing);
        for (float y = origin + k * spacing; y <= clip.Bottom && y <= bottom; y += spacing)
        {
            if (!infinite && y >= bottom - 0.5f) break;
            sink.Line(x0, y, x1, y, width, color);
        }
    }

    private static void HLine(float y, float left, float right, RectF clip, uint color, float width, IBackgroundSink sink)
    {
        if (y < clip.Top || y > clip.Bottom) return;
        float x0 = MathF.Max(left, clip.Left), x1 = MathF.Min(right, clip.Right);
        if (x1 > x0) sink.Line(x0, y, x1, y, width, color);
    }

    private static void VerticalLine(float x, RectF clip, RectF sheet, uint color, float width, IBackgroundSink sink, bool infinite)
    {
        if (x < clip.Left || x > clip.Right) return;
        float y0 = infinite ? clip.Top : MathF.Max(clip.Top, sheet.Top);
        float y1 = infinite ? clip.Bottom : MathF.Min(clip.Bottom, sheet.Bottom);
        if (y1 > y0) sink.Line(x, y0, x, y1, width, color);
    }

    private static void Grid(RectF sheet, RectF clip, float spacing, uint color, float width, IBackgroundSink sink, bool infinite, int majorEvery, uint majorColor)
    {
        float ox = infinite ? 0 : sheet.Left, oy = infinite ? 0 : sheet.Top;
        int kx0 = (int)MathF.Ceiling((clip.Left - ox) / spacing), kx1 = (int)MathF.Floor((clip.Right - ox) / spacing);
        int ky0 = (int)MathF.Ceiling((clip.Top - oy) / spacing), ky1 = (int)MathF.Floor((clip.Bottom - oy) / spacing);
        // Minor lines first, majors on top.
        for (int pass = 0; pass < (majorEvery > 0 ? 2 : 1); pass++)
        {
            bool majorPass = pass == 1;
            for (int k = kx0; k <= kx1; k++)
            {
                bool isMajor = majorEvery > 0 && Mod(k, majorEvery) == 0;
                if (majorEvery > 0 && isMajor != majorPass) continue;
                float x = ox + k * spacing;
                sink.Line(x, clip.Top, x, clip.Bottom, isMajor ? MajorLineWidth : width, isMajor ? majorColor : color);
            }
            for (int k = ky0; k <= ky1; k++)
            {
                bool isMajor = majorEvery > 0 && Mod(k, majorEvery) == 0;
                if (majorEvery > 0 && isMajor != majorPass) continue;
                float y = oy + k * spacing;
                sink.Line(clip.Left, y, clip.Right, y, isMajor ? MajorLineWidth : width, isMajor ? majorColor : color);
            }
        }
    }

    private static void Dots(RectF sheet, RectF clip, float spacing, uint color, IBackgroundSink sink, bool infinite)
    {
        float ox = infinite ? 0 : sheet.Left, oy = infinite ? 0 : sheet.Top;
        int kx0 = (int)MathF.Ceiling((clip.Left - ox) / spacing), kx1 = (int)MathF.Floor((clip.Right - ox) / spacing);
        int ky0 = (int)MathF.Ceiling((clip.Top - oy) / spacing), ky1 = (int)MathF.Floor((clip.Bottom - oy) / spacing);
        float r = MathF.Max(0.6f, spacing * 0.045f);
        for (int ky = ky0; ky <= ky1; ky++)
        for (int kx = kx0; kx <= kx1; kx++)
            sink.Dot(ox + kx * spacing, oy + ky * spacing, r, color);
    }

    private static int Mod(int a, int m) => ((a % m) + m) % m;
}
