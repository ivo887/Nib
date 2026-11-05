using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Nib.Core.Export;
using Nib.Core.Geometry;
using Nib.Core.Model;
using Nib.Core.Rendering;
using Windows.UI.Text;
using FontWeights = Microsoft.UI.Text.FontWeights;

namespace Nib.App.Ink;

/// <summary>
/// DirectWrite layout of a text container, one layout per paragraph (so indents, bullets and tag gutters
/// are exact). The same layout feeds the screen renderer, hit testing for tag checkboxes and the vector
/// PDF exporter, so exported text wraps exactly like on screen. Device independent and immutable.
/// </summary>
internal sealed partial class TextVisual : IDisposable
{
    public const string DefaultFont = "Segoe UI";
    public const float IndentStep = 24f;
    public const float ParagraphSpacing = 2f;
    public const float GlyphSize = 14f;

    public sealed class Line
    {
        public required CanvasTextLayout Layout;
        public required float X;
        public required float Y;
        public string? Prefix;
        public CanvasTextFormat? PrefixFormat;
        public float PrefixX;
        public uint PrefixColor;
        public required Paragraph Paragraph;
        public required int Index;
        public List<(RectF Rect, uint Color)> Highlights = new();
    }

    public readonly List<Line> Lines = new();
    public readonly List<(RectF Rect, int Paragraph, int TagId)> TagGlyphs = new();
    public float Height { get; private set; }
    public RichText Source { get; }

    public TextVisual(ICanvasResourceCreator creator, TextContainer c, RichText? draft = null)
    {
        Source = draft ?? c.Text;
        float y = c.Y;
        int number = 0;
        for (int i = 0; i < Source.Paragraphs.Length; i++)
        {
            var p = Source.Paragraphs[i];
            number = p.Kind == ParagraphKind.Numbered ? number + 1 : 0;
            float indent = p.Indent * IndentStep + (p.Kind is ParagraphKind.Bullet or ParagraphKind.Numbered ? 18f : 0f);
            float x = c.X + indent;
            float width = MathF.Max(c.Width - indent, 20f);
            string text = p.PlainText;
            var baseStyle = p.Runs.Length > 0 ? p.Runs[0].Style : TextStyle.Default;
            using var format = MakeFormat(p, baseStyle);
            var layout = new CanvasTextLayout(creator, text.Length == 0 ? "\u200B" : text, format, width, 100000f);
            ApplyRuns(layout, p, baseStyle);
            var line = new Line { Layout = layout, X = x, Y = y, Paragraph = p, Index = i };
            if (p.Kind == ParagraphKind.Bullet || p.Kind == ParagraphKind.Numbered)
            {
                line.Prefix = p.Kind == ParagraphKind.Bullet ? (p.Indent % 2 == 0 ? "•" : "◦") : number.ToString(CultureInfo.InvariantCulture) + ".";
                line.PrefixFormat = new CanvasTextFormat { FontFamily = DefaultFont, FontSize = SizeFor(p, baseStyle) };
                line.PrefixX = c.X + p.Indent * IndentStep;
                line.PrefixColor = baseStyle.Color;
            }
            // Highlighted runs: background rectangles from the layout's character regions.
            int pos = 0;
            foreach (var run in p.Runs)
            {
                if (run.Style.Highlight != 0 && run.Text.Length > 0)
                {
                    foreach (var region in layout.GetCharacterRegions(pos, run.Text.Length))
                    {
                        var b = region.LayoutBounds;
                        line.Highlights.Add((RectF.FromXYWH(x + (float)b.X, y + (float)b.Y, (float)b.Width, (float)b.Height), run.Style.Highlight));
                    }
                }
                pos += run.Text.Length;
            }
            float h = (float)layout.LayoutBounds.Height;
            if (p.TagIds.Length > 0)
            {
                float gy = y + MathF.Max(0, (MathF.Min(h, SizeFor(p, baseStyle) * 1.35f) - GlyphSize) * 0.5f);
                float gx = c.X - TextContainer.TagGutter + 2;
                foreach (int tag in p.TagIds)
                {
                    TagGlyphs.Add((RectF.FromXYWH(gx, gy, GlyphSize, GlyphSize), i, tag));
                    gx -= GlyphSize + 2;
                }
            }
            Lines.Add(line);
            y += h + ParagraphSpacing;
        }
        Height = MathF.Max(y - c.Y, 16f);
    }

    private static float SizeFor(Paragraph p, TextStyle s) => p.Kind switch
    {
        ParagraphKind.Heading1 => MathF.Max(s.SizeUnits, 26f),
        ParagraphKind.Heading2 => MathF.Max(s.SizeUnits, 21f),
        ParagraphKind.Heading3 => MathF.Max(s.SizeUnits, 17f),
        _ => s.SizeUnits,
    };

    private static CanvasTextFormat MakeFormat(Paragraph p, TextStyle s) => new()
    {
        FontFamily = s.Font ?? DefaultFont,
        FontSize = SizeFor(p, s),
        FontWeight = p.Kind is ParagraphKind.Heading1 or ParagraphKind.Heading2 or ParagraphKind.Heading3 ? FontWeights.SemiBold : FontWeights.Normal,
        WordWrapping = CanvasWordWrapping.Wrap,
        Options = CanvasDrawTextOptions.EnableColorFont,
    };

    private static void ApplyRuns(CanvasTextLayout layout, Paragraph p, TextStyle baseStyle)
    {
        int pos = 0;
        foreach (var run in p.Runs)
        {
            int n = run.Text.Length;
            if (n == 0) continue;
            var s = run.Style;
            if (s.Bold) layout.SetFontWeight(pos, n, FontWeights.Bold);
            if (s.Italic) layout.SetFontStyle(pos, n, FontStyle.Italic);
            if (s.Underline) layout.SetUnderline(pos, n, true);
            if (s.Strikethrough) layout.SetStrikethrough(pos, n, true);
            if (s.SizePt != baseStyle.SizePt) layout.SetFontSize(pos, n, s.SizeUnits);
            if (s.Font is not null && s.Font != baseStyle.Font) layout.SetFontFamily(pos, n, s.Font);
            layout.SetColor(pos, n, Colors2D.From(s.Color));
            pos += n;
        }
    }

    /// <summary>Hit test for tag glyphs (checkbox toggling).</summary>
    public bool HitTag(float x, float y, out int paragraph, out int tagId)
    {
        foreach (var (rect, para, tag) in TagGlyphs)
        {
            if (rect.Inflate(3).Contains(x, y)) { paragraph = para; tagId = tag; return true; }
        }
        paragraph = -1; tagId = 0;
        return false;
    }

    public void Draw(CanvasDrawingSession ds, Func<uint, uint> map, Win2DShapeCanvas shapes, IReadOnlyDictionary<int, TagDefinition> tags, bool drawText = true)
    {
        if (drawText)
        {
            foreach (var line in Lines)
            {
                foreach (var (rect, color) in line.Highlights)
                    ds.FillRectangle(rect.Left, rect.Top, rect.Width, rect.Height, Colors2D.From(map(color)));
                if (map(0xFF000000) != 0xFF000000) RecolorForDisplay(line, map);
                ds.DrawTextLayout(line.Layout, line.X, line.Y, Colors2D.From(map(TextStyle.Default.Color)));
                if (line.Prefix is not null)
                    ds.DrawText(line.Prefix, line.PrefixX, line.Y, Colors2D.From(map(line.PrefixColor)), line.PrefixFormat!);
            }
        }
        shapes.Session = ds;
        shapes.MapColor = map;
        foreach (var (rect, para, tagId) in TagGlyphs)
        {
            if (!tags.TryGetValue(tagId, out var def)) continue;
            TagGlyphs_Draw(def, Source.Paragraphs[para].Checked, rect, shapes);
        }
    }

    private static void TagGlyphs_Draw(TagDefinition def, bool isChecked, RectF rect, IShapeCanvas c) =>
        Nib.Core.Rendering.TagGlyphs.Draw(def, isChecked, rect.Left, rect.Top, rect.Width, c);

    private bool _displayRecolored;

    private void RecolorForDisplay(Line line, Func<uint, uint> map)
    {
        // Dark mode: rewrite run colors on the layout once (layouts are display-only objects).
        if (_displayRecolored) return;
        _displayRecolored = true;
        foreach (var l in Lines)
        {
            int pos = 0;
            foreach (var run in l.Paragraph.Runs)
            {
                if (run.Text.Length > 0) l.Layout.SetColor(pos, run.Text.Length, Colors2D.From(map(run.Style.Color)));
                pos += run.Text.Length;
            }
        }
    }

    public void Dispose()
    {
        foreach (var l in Lines)
        {
            l.Layout.Dispose();
            l.PrefixFormat?.Dispose();
        }
    }
}

/// <summary>
/// Vector text for PDF export: walks the DirectWrite glyph runs of each paragraph layout and emits the
/// glyph outlines as filled paths, plus underline/strikethrough bars, highlights, bullets and tag glyphs.
/// Text in the PDF is therefore pixel-identical to the screen at any zoom, in any script.
/// </summary>
internal sealed partial class GlyphOutliner : ITextOutliner
{
    private readonly CanvasDevice _device;
    private readonly IReadOnlyDictionary<int, TagDefinition> _tags;

    public GlyphOutliner(CanvasDevice device, IReadOnlyDictionary<int, TagDefinition> tags)
    {
        _device = device;
        _tags = tags;
    }

    public void Outline(TextContainer container, IVectorTarget target)
    {
        using var visual = new TextVisual(_device, container);
        foreach (var line in visual.Lines)
        {
            foreach (var (rect, color) in line.Highlights) target.FillRect(rect, color);
            var renderer = new OutlineRenderer(_device, target, line.X, line.Y, line.Paragraph);
            line.Layout.DrawToTextRenderer(renderer, 0, 0);
            if (line.Prefix is not null)
            {
                using var prefix = new CanvasTextLayout(_device, line.Prefix, line.PrefixFormat!, 100, 100);
                prefix.DrawToTextRenderer(new OutlineRenderer(_device, target, line.PrefixX, line.Y, line.Paragraph, line.PrefixColor), 0, 0);
            }
        }
        foreach (var (rect, para, tagId) in visual.TagGlyphs)
            if (_tags.TryGetValue(tagId, out var def))
                TagGlyphs.Draw(def, visual.Source.Paragraphs[para].Checked, rect.Left, rect.Top, rect.Width, target.Shapes);
    }

    private sealed partial class OutlineRenderer : ICanvasTextRenderer
    {
        private readonly CanvasDevice _device;
        private readonly IVectorTarget _target;
        private readonly float _ox, _oy;
        private readonly Paragraph _paragraph;
        private readonly uint? _forcedColor;

        public OutlineRenderer(CanvasDevice device, IVectorTarget target, float ox, float oy, Paragraph paragraph, uint? forcedColor = null)
        {
            _device = device; _target = target; _ox = ox; _oy = oy; _paragraph = paragraph; _forcedColor = forcedColor;
        }

        public float Dpi => 96;
        public bool PixelSnappingDisabled => true;
        public Matrix3x2 Transform => Matrix3x2.Identity;

        private uint ColorAt(uint characterIndex)
        {
            if (_forcedColor is { } c) return c;
            int pos = 0;
            foreach (var run in _paragraph.Runs)
            {
                if (characterIndex < pos + run.Text.Length) return run.Style.Color;
                pos += run.Text.Length;
            }
            return _paragraph.Runs.Length > 0 ? _paragraph.Runs[^1].Style.Color : TextStyle.Default.Color;
        }

        public void DrawGlyphRun(Vector2 point, CanvasFontFace fontFace, float fontSize, CanvasGlyph[] glyphs, bool isSideways, uint bidiLevel,
            object brush, CanvasTextMeasuringMode measuringMode, string localeName, string textString, int[] clusterMapIndices,
            uint characterIndex, CanvasGlyphOrientation glyphOrientation)
        {
            using var geometry = CanvasGeometry.CreateGlyphRun(_device, new Vector2(point.X + _ox, point.Y + _oy), fontFace, fontSize, glyphs,
                isSideways, bidiLevel, measuringMode, glyphOrientation);
            var receiver = new PathReceiver(_target);
            _target.BeginFill(ColorAt(characterIndex));
            geometry.SendPathTo(receiver);
            _target.EndFill(receiver.EvenOdd);
        }

        public void DrawStrikethrough(Vector2 point, float strikethroughWidth, float strikethroughThickness, float strikethroughOffset,
            CanvasTextDirection textDirection, object brush, CanvasTextMeasuringMode textMeasuringMode, string localeName, CanvasGlyphOrientation glyphOrientation) =>
            _target.FillRect(RectF.FromXYWH(point.X + _ox, point.Y + _oy + strikethroughOffset, strikethroughWidth, MathF.Max(strikethroughThickness, 0.5f)), BrushColor(brush));

        public void DrawUnderline(Vector2 point, float underlineWidth, float underlineThickness, float underlineOffset, float runHeight,
            CanvasTextDirection textDirection, object brush, CanvasTextMeasuringMode textMeasuringMode, string localeName, CanvasGlyphOrientation glyphOrientation) =>
            _target.FillRect(RectF.FromXYWH(point.X + _ox, point.Y + _oy + underlineOffset, underlineWidth, MathF.Max(underlineThickness, 0.5f)), BrushColor(brush));

        public void DrawInlineObject(Vector2 point, ICanvasTextInlineObject inlineObject, bool isSideways, bool isRightToLeft, object brush, CanvasGlyphOrientation glyphOrientation)
        {
            // Nib text containers have no inline objects.
        }

        private uint BrushColor(object brush) =>
            brush is CanvasSolidColorBrush b ? (uint)(b.Color.A << 24 | b.Color.R << 16 | b.Color.G << 8 | b.Color.B) : (_forcedColor ?? TextStyle.Default.Color);
    }

    /// <summary>Forwards Win2D path data (lines, Béziers, arcs) to the vector target.</summary>
    private sealed partial class PathReceiver : ICanvasPathReceiver
    {
        private readonly IVectorTarget _t;
        private Vector2 _current;
        public bool EvenOdd { get; private set; }

        public PathReceiver(IVectorTarget t) => _t = t;

        public void BeginFigure(Vector2 startPoint, CanvasFigureFill figureFill) { _t.MoveTo(startPoint.X, startPoint.Y); _current = startPoint; }
        public void AddLine(Vector2 endPoint) { _t.LineTo(endPoint.X, endPoint.Y); _current = endPoint; }
        public void AddCubicBezier(Vector2 controlPoint1, Vector2 controlPoint2, Vector2 endPoint)
        {
            _t.CubicTo(controlPoint1.X, controlPoint1.Y, controlPoint2.X, controlPoint2.Y, endPoint.X, endPoint.Y);
            _current = endPoint;
        }
        public void AddQuadraticBezier(Vector2 controlPoint, Vector2 endPoint) { _t.QuadTo(controlPoint.X, controlPoint.Y, endPoint.X, endPoint.Y); _current = endPoint; }
        public void AddArc(Vector2 endPoint, float radiusX, float radiusY, float rotationAngle, CanvasSweepDirection sweepDirection, CanvasArcSize arcSize)
        {
            // Glyph outlines never contain arcs; approximate with a line to stay robust.
            _t.LineTo(endPoint.X, endPoint.Y);
            _current = endPoint;
        }
        public void EndFigure(CanvasFigureLoop figureLoop) => _t.ClosePath();
        public void SetFilledRegionDetermination(CanvasFilledRegionDetermination filledRegionDetermination) =>
            EvenOdd = filledRegionDetermination == CanvasFilledRegionDetermination.Alternate;
        public void SetSegmentOptions(CanvasFigureSegmentOptions figureSegmentOptions) { }
    }
}
