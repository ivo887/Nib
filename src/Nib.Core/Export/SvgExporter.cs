using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text;
using Nib.Core.Geometry;
using Nib.Core.Ink;
using Nib.Core.Model;
using Nib.Core.Rendering;

namespace Nib.Core.Export;

/// <summary>
/// Exports a page sheet as a standalone SVG: the same vector geometry as the PDF export (so ink looks identical at any
/// zoom), paper patterns, and text as outlines. Translucent inks (pencil, marker, highlighter, watercolor) use the
/// "darken" blend so overlaps never stack, exactly like on screen.
/// </summary>
public static class SvgExporter
{
    /// <summary>Writes one sheet (see <see cref="PdfExporter.Paginate"/>) as an SVG document.</summary>
    public static void WriteSheet(ExportPage page, PdfExporter.Sheet sheet, bool includeBackground, ITextOutliner? text, TextWriter output, string? title = null)
    {
        float k = PageUnits.PdfPointsPerUnit * sheet.Scale;
        float originX = sheet.Origin.Left - sheet.MarginUnits, originY = sheet.Origin.Top - sheet.MarginUnits;
        float viewW = sheet.WidthPt / k, viewH = sheet.HeightPt / k;
        float pxW = sheet.WidthPt / PageUnits.PdfPointsPerUnit * 1f, pxH = sheet.HeightPt / PageUnits.PdfPointsPerUnit * 1f;

        var c = new SvgCanvas();
        output.Write("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
        output.Write(string.Create(CultureInfo.InvariantCulture,
            $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{Num(pxW)}\" height=\"{Num(pxH)}\" viewBox=\"{Num(originX)} {Num(originY)} {Num(viewW)} {Num(viewH)}\" style=\"isolation:isolate\">\n"));
        if (!string.IsNullOrWhiteSpace(title)) output.Write($"<title>{Escape(title)}</title>\n");
        output.Write(string.Create(CultureInfo.InvariantCulture,
            $"<defs><clipPath id=\"sheet\"><rect x=\"{Num(sheet.Origin.Left)}\" y=\"{Num(sheet.Origin.Top)}\" width=\"{Num(sheet.Origin.Width)}\" height=\"{Num(sheet.Origin.Height)}\"/></clipPath></defs>\n"));
        output.Write("<rect x=\"-1e6\" y=\"-1e6\" width=\"2e6\" height=\"2e6\" fill=\"#FFFFFF\" clip-path=\"url(#sheet)\"/>\n");
        output.Write("<g clip-path=\"url(#sheet)\">\n");

        if (includeBackground) BackgroundPattern.Emit(page.Layout, sheet.Origin, 1f, c);

        var builder = new StrokeGeometryBuilder();
        foreach (var s in page.Strokes)
            if (s.Kind.IsBackgroundInk() && s.Bounds.Intersects(sheet.Origin)) DrawStroke(s, c, builder);
        if (text is not null)
            foreach (var t in page.Texts)
                if (t.Bounds.Intersects(sheet.Origin)) text.Outline(t, c.Vector);
        foreach (var s in page.Strokes)
            if (!s.Kind.IsBackgroundInk() && s.Bounds.Intersects(sheet.Origin)) DrawStroke(s, c, builder);

        output.Write(c.ToString());
        output.Write("</g>\n</svg>\n");
    }

    private static void DrawStroke(Stroke s, SvgCanvas c, StrokeGeometryBuilder builder)
    {
        c.BeginStroke();
        builder.Build(s, c);
        c.EndStroke();
    }

    internal static string Num(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    internal static string Hex(uint argb) => "#" + (argb & 0xFFFFFF).ToString("X6", CultureInfo.InvariantCulture);

    internal static string Escape(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    /// <summary>Collects SVG markup for everything drawn on a sheet.</summary>
    internal sealed class SvgCanvas : IPathSink, IStrokeRunSink, IBackgroundSink, IShapeCanvas
    {
        private readonly StringBuilder _sb = new(64 * 1024);
        private readonly StringBuilder _path = new(4096);
        private StrokeRun _run;
        private bool _inRun;
        public IVectorTarget Vector { get; }

        public SvgCanvas() => Vector = new VectorTarget(this);

        public override string ToString() => _sb.ToString();

        // ---- strokes (IStrokeRunSink / IPathSink) ----

        public void BeginStroke() => _sb.Append("<g>\n");
        public void EndStroke() => _sb.Append("</g>\n");

        public IPathSink BeginRun(in StrokeRun run)
        {
            _run = run;
            _inRun = true;
            _path.Clear();
            return this;
        }

        public void EndRun()
        {
            if (!_inRun) return;
            _inRun = false;
            if (_path.Length == 0) return;
            _sb.Append("<path d=\"").Append(_path).Append("\" fill=\"").Append(Hex(_run.FillColor)).Append('"');
            if (_run.Multiply) _sb.Append(" style=\"mix-blend-mode:darken\"");
            _sb.Append("/>\n");
        }

        public void BeginFigure(float x, float y) => _path.Append('M').Append(Num(x)).Append(' ').Append(Num(y));
        public void LineTo(float x, float y) => _path.Append('L').Append(Num(x)).Append(' ').Append(Num(y));
        public void EndFigure() => _path.Append('Z');

        public void AddCircle(float cx, float cy, float r)
        {
            // Two arcs with sweep flag 1 (increasing angle), the same orientation as the polygons, so the non-zero fill unions them.
            _path.Append('M').Append(Num(cx - r)).Append(' ').Append(Num(cy))
                 .Append('A').Append(Num(r)).Append(' ').Append(Num(r)).Append(" 0 1 1 ").Append(Num(cx + r)).Append(' ').Append(Num(cy))
                 .Append('A').Append(Num(r)).Append(' ').Append(Num(r)).Append(" 0 1 1 ").Append(Num(cx - r)).Append(' ').Append(Num(cy)).Append('Z');
        }

        // ---- paper (IBackgroundSink) ----

        public void FillRect(RectF r, uint color) =>
            _sb.Append("<rect x=\"").Append(Num(r.Left)).Append("\" y=\"").Append(Num(r.Top)).Append("\" width=\"").Append(Num(r.Width))
               .Append("\" height=\"").Append(Num(r.Height)).Append("\" fill=\"").Append(Hex(color)).Append(Opacity(color, "fill")).Append("/>\n");

        public void Line(float x0, float y0, float x1, float y1, float width, uint color) =>
            _sb.Append("<line x1=\"").Append(Num(x0)).Append("\" y1=\"").Append(Num(y0)).Append("\" x2=\"").Append(Num(x1)).Append("\" y2=\"").Append(Num(y1))
               .Append("\" stroke=\"").Append(Hex(color)).Append("\" stroke-width=\"").Append(Num(width)).Append(Opacity(color, "stroke")).Append("/>\n");

        public void Dot(float cx, float cy, float radius, uint color) =>
            _sb.Append("<circle cx=\"").Append(Num(cx)).Append("\" cy=\"").Append(Num(cy)).Append("\" r=\"").Append(Num(radius))
               .Append("\" fill=\"").Append(Hex(color)).Append(Opacity(color, "fill")).Append("/>\n");

        private static string Opacity(uint argb, string kind)
        {
            uint a = argb >> 24;
            return a >= 255 ? "\"" : $"\" {kind}-opacity=\"{(a / 255f).ToString("0.###", CultureInfo.InvariantCulture)}\"";
        }

        // ---- tag glyphs (IShapeCanvas) ----

        public void FillPolygon(ReadOnlySpan<Vector2> points, uint color)
        {
            _sb.Append("<polygon points=\"");
            for (int i = 0; i < points.Length; i++) _sb.Append(Num(points[i].X)).Append(',').Append(Num(points[i].Y)).Append(' ');
            _sb.Append("\" fill=\"").Append(Hex(color)).Append(Opacity(color, "fill")).Append("/>\n");
        }

        public void FillCircle(float cx, float cy, float r, uint color) => Dot(cx, cy, r, color);

        public void StrokePolyline(ReadOnlySpan<Vector2> points, bool closed, float width, uint color)
        {
            _sb.Append(closed ? "<polygon" : "<polyline").Append(" points=\"");
            for (int i = 0; i < points.Length; i++) _sb.Append(Num(points[i].X)).Append(',').Append(Num(points[i].Y)).Append(' ');
            // Attribute order matters here: Opacity() supplies the closing quote of the last attribute written.
            _sb.Append("\" fill=\"none\" stroke-width=\"").Append(Num(width))
               .Append("\" stroke-linecap=\"round\" stroke-linejoin=\"round\" stroke=\"").Append(Hex(color)).Append(Opacity(color, "stroke")).Append("/>\n");
        }

        // ---- text outlines (IVectorTarget) ----

        private sealed class VectorTarget : IVectorTarget
        {
            private readonly SvgCanvas _c;
            private uint _color;
            private bool _open;
            public VectorTarget(SvgCanvas c) => _c = c;
            public IShapeCanvas Shapes => _c;

            public void BeginFill(uint color)
            {
                _color = color; _open = true;
                _c._path.Clear();
            }

            public void MoveTo(float x, float y) => _c._path.Append('M').Append(Num(x)).Append(' ').Append(Num(y));
            public void LineTo(float x, float y) => _c._path.Append('L').Append(Num(x)).Append(' ').Append(Num(y));
            public void CubicTo(float x1, float y1, float x2, float y2, float x3, float y3) =>
                _c._path.Append('C').Append(Num(x1)).Append(' ').Append(Num(y1)).Append(' ').Append(Num(x2)).Append(' ').Append(Num(y2)).Append(' ').Append(Num(x3)).Append(' ').Append(Num(y3));
            public void QuadTo(float x1, float y1, float x2, float y2) =>
                _c._path.Append('Q').Append(Num(x1)).Append(' ').Append(Num(y1)).Append(' ').Append(Num(x2)).Append(' ').Append(Num(y2));
            public void ClosePath() => _c._path.Append('Z');

            public void EndFill(bool evenOdd)
            {
                if (!_open) return;
                _open = false;
                if (_c._path.Length == 0) return;
                _c._sb.Append("<path d=\"").Append(_c._path).Append("\" fill=\"").Append(Hex(_color)).Append(Opacity(_color, "fill"))
                      .Append(evenOdd ? " fill-rule=\"evenodd\"" : "").Append("/>\n");
                _c._path.Clear();
            }

            public void FillRect(RectF rect, uint color) => _c.FillRect(rect, color);
        }
    }
}
