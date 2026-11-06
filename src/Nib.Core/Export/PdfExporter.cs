using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Nib.Core.Geometry;
using Nib.Core.Ink;
using Nib.Core.Model;
using Nib.Core.Rendering;

namespace Nib.Core.Export;

/// <summary>A page to export (an immutable snapshot: safe to export on a background thread).</summary>
public sealed record ExportPage(string Title, PageLayout Layout, IReadOnlyList<Stroke> Strokes, IReadOnlyList<TextContainer> Texts);

/// <summary>Vector drawing target handed to the text outliner (glyph outlines, underline bars, highlights).</summary>
public interface IVectorTarget
{
    void BeginFill(uint color);
    void MoveTo(float x, float y);
    void LineTo(float x, float y);
    void CubicTo(float x1, float y1, float x2, float y2, float x3, float y3);
    void QuadTo(float x1, float y1, float x2, float y2);
    void ClosePath();
    void EndFill(bool evenOdd);
    void FillRect(RectF rect, uint color);
    IShapeCanvas Shapes { get; }
}

/// <summary>
/// Produces vector outlines for a text container using the same text layout engine as the screen
/// (DirectWrite on Windows), so exported text wraps and looks exactly as on screen.
/// </summary>
public interface ITextOutliner
{
    void Outline(TextContainer container, IVectorTarget target);
}

public sealed class PdfExportOptions
{
    public bool IncludeBackground { get; init; } = true;
    /// <summary>Paper used to paginate infinite canvases.</summary>
    public PaperSize InfinitePaper { get; init; } = PaperSize.A4;
    /// <summary>Margin around infinite-canvas content, page units.</summary>
    public float InfiniteMargin { get; init; } = 48f;
    public string Title { get; init; } = "Nib export";
    /// <summary>Drop trailing empty sheets of fixed-size pages (the blank sheet kept ready for writing).</summary>
    public bool TrimEmptyTrailingSheets { get; init; } = true;
}

/// <summary>
/// Exports pages to PDF with vector ink. Fixed pages export sheet by sheet; infinite canvases are
/// scaled to the paper width and broken into pages at gaps between strokes where possible.
/// </summary>
public static class PdfExporter
{
    public static int Export(IReadOnlyList<ExportPage> pages, Stream output, PdfExportOptions options,
        ITextOutliner? text = null, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        using var doc = new PdfDocumentBuilder(output, options.Title);
        var builder = new StrokeGeometryBuilder();
        for (int pi = 0; pi < pages.Count; pi++)
        {
            ct.ThrowIfCancellationRequested();
            var page = pages[pi];
            int firstSheetIndex = doc.PageCount;
            foreach (var sheet in Paginate(page, options))
            {
                ct.ThrowIfCancellationRequested();
                var canvas = doc.BeginPage(sheet.WidthPt, sheet.HeightPt);
                canvas.SetPageTransform(sheet.Origin.Left - sheet.MarginUnits, sheet.Origin.Top - sheet.MarginUnits, sheet.Scale);
                canvas.Save();
                canvas.ClipRect(sheet.Origin);
                if (options.IncludeBackground)
                    BackgroundPattern.Emit(page.Layout, sheet.Origin, 1f, canvas);
                PaintContent(page, sheet.Origin, canvas, builder, text);
                canvas.Restore();
                canvas.Finish();
            }
            doc.AddOutline(string.IsNullOrWhiteSpace(page.Title) ? "Untitled page" : page.Title, firstSheetIndex);
            progress?.Report((pi + 1) / (double)pages.Count);
        }
        doc.Finish();
        return doc.PageCount;
    }

    /// <summary>Same paint order as the screen: background, highlighter, text, ink.</summary>
    internal static void PaintContent(ExportPage page, RectF region, PdfPageCanvas canvas, StrokeGeometryBuilder builder, ITextOutliner? text)
    {
        foreach (var s in page.Strokes)
            if (s.Kind.IsBackgroundInk() && s.Bounds.Intersects(region)) DrawStroke(s, canvas, builder);
        if (text is not null)
        {
            var target = new PdfVectorTarget(canvas);
            foreach (var t in page.Texts)
                if (t.Bounds.Intersects(region)) text.Outline(t, target);
        }
        foreach (var s in page.Strokes)
            if (!s.Kind.IsBackgroundInk() && s.Bounds.Intersects(region)) DrawStroke(s, canvas, builder);
    }

    private static void DrawStroke(Stroke s, PdfPageCanvas canvas, StrokeGeometryBuilder builder)
    {
        if (!s.Kind.IsMultiplyInk())
        {
            builder.Build(s, canvas);
            return;
        }
        // Count runs first: a single-run translucent stroke needs no knockout group.
        var counter = new RunCounter();
        builder.Build(s, counter);
        if (counter.Runs <= 1)
        {
            builder.Build(s, canvas);
            return;
        }
        canvas.BeginTranslucentStroke(s.Bounds);
        builder.Build(s, canvas);
        canvas.EndTranslucentStroke();
    }

    public readonly record struct Sheet(RectF Origin, float Scale, float WidthPt, float HeightPt, float MarginUnits);

    /// <summary>Computes the PDF pages for one note page.</summary>
    public static List<Sheet> Paginate(ExportPage page, PdfExportOptions options)
    {
        var sheets = new List<Sheet>();
        var layout = page.Layout;
        if (layout.Mode == PageMode.Fixed)
        {
            int count = Math.Max(1, layout.SheetCount);
            if (options.TrimEmptyTrailingSheets)
            {
                float bottom = 0;
                foreach (var s in page.Strokes) bottom = MathF.Max(bottom, s.Bounds.Bottom);
                foreach (var t in page.Texts) bottom = MathF.Max(bottom, t.Bounds.Bottom);
                count = Math.Clamp((int)MathF.Ceiling(bottom / layout.SheetHeight), 1, count);
            }
            float wPt = layout.SheetWidth * PageUnits.PdfPointsPerUnit, hPt = layout.SheetHeight * PageUnits.PdfPointsPerUnit;
            for (int i = 0; i < count; i++) sheets.Add(new Sheet(layout.SheetRect(i), 1f, wPt, hPt, 0));
            return sheets;
        }

        // Infinite canvas: fit content width to paper width (never enlarge), then break vertically.
        var paperW = PageUnits.FromMillimeters(options.InfinitePaper.WidthMm);
        var paperH = PageUnits.FromMillimeters(options.InfinitePaper.HeightMm);
        float m = options.InfiniteMargin;
        var content = RectF.Empty;
        foreach (var s in page.Strokes) content = content.Union(s.Bounds);
        foreach (var t in page.Texts) content = content.Union(t.Bounds);
        float pw = paperW * PageUnits.PdfPointsPerUnit, ph = paperH * PageUnits.PdfPointsPerUnit;
        if (content.IsEmpty)
        {
            sheets.Add(new Sheet(new RectF(0, 0, paperW - 2 * m, paperH - 2 * m), 1f, pw, ph, m));
            return sheets;
        }
        float scale = MathF.Min(1f, (paperW - 2 * m) / MathF.Max(content.Width, 1));
        float contentHeightPerSheet = (paperH - 2 * m) / scale;
        float contentWidth = (paperW - 2 * m) / scale;
        var intervals = new List<(float Top, float Bottom)>(page.Strokes.Count + page.Texts.Count);
        foreach (var s in page.Strokes) intervals.Add((s.Bounds.Top, s.Bounds.Bottom));
        foreach (var t in page.Texts) intervals.Add((t.Bounds.Top, t.Bounds.Bottom));
        var breaks = PageBreaker.FindBreaks(intervals, content.Top, content.Bottom, contentHeightPerSheet);
        for (int i = 0; i < breaks.Count; i++)
        {
            float top = breaks[i];
            float bottom = i + 1 < breaks.Count ? breaks[i + 1] : MathF.Min(content.Bottom, top + contentHeightPerSheet);
            var region = new RectF(content.Left, top, content.Left + contentWidth, MathF.Max(bottom, top + 1));
            // Margin in the sheet is expressed in page units at this scale.
            sheets.Add(new Sheet(region, scale, pw, ph, m / scale));
        }
        return sheets;
    }

    private sealed class RunCounter : IStrokeRunSink, IPathSink
    {
        public int Runs;
        public IPathSink BeginRun(in StrokeRun run) { Runs++; return this; }
        public void EndRun() { }
        public void BeginFigure(float x, float y) { }
        public void LineTo(float x, float y) { }
        public void EndFigure() { }
        public void AddCircle(float cx, float cy, float r) { }
    }

    private sealed class PdfVectorTarget : IVectorTarget
    {
        private readonly PdfPageCanvas _c;
        public PdfVectorTarget(PdfPageCanvas c) => _c = c;
        public IShapeCanvas Shapes => _c;
        public void BeginFill(uint color) => _c.BeginFill(color);
        public void MoveTo(float x, float y) => _c.MoveTo(x, y);
        public void LineTo(float x, float y) => _c.LineTo(x, y);
        public void CubicTo(float x1, float y1, float x2, float y2, float x3, float y3) => _c.CubicTo(x1, y1, x2, y2, x3, y3);
        public void QuadTo(float x1, float y1, float x2, float y2) => _c.QuadTo(x1, y1, x2, y2);
        public void ClosePath() => _c.ClosePath();
        public void EndFill(bool evenOdd) => _c.EndFill(evenOdd);
        public void FillRect(RectF rect, uint color) => _c.FillRect(rect, color);
    }
}

/// <summary>
/// Chooses page breaks for tall content: each break lands as close as possible to the ideal sheet
/// height (never earlier than 75% of it) at a position that cuts through the fewest strokes and text boxes.
/// </summary>
public static class PageBreaker
{
    public static List<float> FindBreaks(IReadOnlyList<(float Top, float Bottom)> items, float top, float bottom, float sheetHeight)
    {
        var breaks = new List<float> { top };
        if (sheetHeight <= 1) return breaks;
        float y = top;
        var candidates = new List<float>();
        int guard = 0;
        while (y + sheetHeight < bottom && guard++ < 10_000)
        {
            float target = y + sheetHeight;
            float min = y + sheetHeight * 0.75f;
            candidates.Clear();
            candidates.Add(target);
            foreach (var (t, b) in items)
            {
                if (b >= min && b <= target) candidates.Add(MathF.Min(b + 0.5f, target));
                if (t >= min && t <= target) candidates.Add(MathF.Max(t - 0.5f, min));
            }
            float best = target;
            int bestCross = int.MaxValue;
            foreach (float c in candidates)
            {
                int cross = 0;
                foreach (var (t, b) in items) if (t < c && b > c) cross++;
                if (cross < bestCross || (cross == bestCross && c > best))
                {
                    bestCross = cross;
                    best = c;
                }
            }
            if (best <= y) best = target;
            breaks.Add(best);
            y = best;
        }
        return breaks;
    }
}
