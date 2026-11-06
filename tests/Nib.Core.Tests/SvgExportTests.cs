using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Nib.Core.Export;
using Nib.Core.Ink;
using Nib.Core.Model;
using Xunit;

namespace Nib.Core.Tests;

public class SvgExportTests
{
    private static ExportPage Page(PenKind kind = PenKind.Ballpoint, BackgroundSpec? bg = null)
    {
        var layout = new PageLayout { Mode = PageMode.Fixed, Background = bg ?? BackgroundSpec.Blank };
        var pts = Enumerable.Range(0, 40).Select(i => new InkPoint(60 + i * 8f, 100 + MathF.Sin(i / 5f) * 20f, 0.6f, 0, 0, -1, i * 8)).ToArray();
        var (w, d) = PenPreset.DefaultsFor(kind);
        var stroke = new Stroke(1, 1, kind, 0xFF1957C9, w, d, StrokeFlags.HasPressure, pts);
        return new ExportPage("Test", layout, new[] { stroke }, Array.Empty<TextContainer>());
    }

    private static XDocument Export(ExportPage page, bool background = true)
    {
        var sheet = PdfExporter.Paginate(page, new PdfExportOptions()).First();
        using var sw = new StringWriter();
        SvgExporter.WriteSheet(page, sheet, background, null, sw, "Test page");
        return XDocument.Parse(sw.ToString());   // throws if the SVG is not well-formed XML
    }

    private static readonly XNamespace Ns = "http://www.w3.org/2000/svg";

    [Fact]
    public void ProducesWellFormedSvgWithAViewBoxAndTitle()
    {
        var doc = Export(Page());
        var root = doc.Root!;
        Assert.Equal(Ns + "svg", root.Name);
        Assert.NotNull(root.Attribute("viewBox"));
        Assert.True(double.Parse(root.Attribute("width")!.Value, System.Globalization.CultureInfo.InvariantCulture) > 100);
        Assert.Equal("Test page", root.Element(Ns + "title")!.Value);
    }

    [Fact]
    public void InkBecomesAFilledPathInThePensColor()
    {
        var doc = Export(Page());
        var path = doc.Descendants(Ns + "path").First();
        Assert.Equal("#1957C9", path.Attribute("fill")!.Value);
        Assert.StartsWith("M", path.Attribute("d")!.Value);
        Assert.Contains("Z", path.Attribute("d")!.Value);
    }

    [Fact]
    public void TranslucentInksUseTheDarkenBlendSoOverlapsNeverStack()
    {
        foreach (var k in new[] { PenKind.Pencil, PenKind.Marker, PenKind.Watercolor, PenKind.Highlighter })
        {
            var doc = Export(Page(k));
            Assert.Contains(doc.Descendants(Ns + "path"), p => p.Attribute("style")?.Value.Contains("mix-blend-mode:darken") == true);
        }
        Assert.DoesNotContain(Export(Page(PenKind.Ballpoint)).Descendants(Ns + "path"), p => p.Attribute("style") is not null);
    }

    [Fact]
    public void RoundNibsAreEncodedAsArcsInTheSamePath()
    {
        var doc = Export(Page(PenKind.Brush));
        Assert.Contains(doc.Descendants(Ns + "path"), p => p.Attribute("d")!.Value.Contains('A'));
    }

    [Fact]
    public void PaperPatternsAreIncludedOnlyWhenAsked()
    {
        var lined = BackgroundSpec.Blank with { Kind = BackgroundKind.Lined };
        var withBg = Export(Page(bg: lined), background: true);
        var without = Export(Page(bg: lined), background: false);
        Assert.True(withBg.Descendants(Ns + "line").Count() > 10);
        Assert.Empty(without.Descendants(Ns + "line"));
    }

    [Fact]
    public void NumbersUseTheInvariantCultureAndTextIsEscaped()
    {
        var old = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            var doc = Export(Page());
            Assert.DoesNotContain(',', doc.Root!.Attribute("viewBox")!.Value);   // "1,5" would break the viewBox
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = old; }
        var page = Page() with { Title = "A & B <c>" };
        var sheet = PdfExporter.Paginate(page, new PdfExportOptions()).First();
        using var sw = new StringWriter();
        SvgExporter.WriteSheet(page, sheet, true, null, sw, page.Title);
        XDocument.Parse(sw.ToString());
    }

    private sealed class FakeText : ITextOutliner
    {
        public void Outline(TextContainer c, IVectorTarget t)
        {
            t.Shapes.StrokePolyline(new[] { new System.Numerics.Vector2(20, 20), new System.Numerics.Vector2(60, 60) }, false, 1.5f, 0x80FF0000);
            t.Shapes.FillCircle(30, 30, 4, 0xFF00AA00);
            t.BeginFill(0xFF000000);
            t.MoveTo(20, 20); t.LineTo(60, 20); t.CubicTo(70, 30, 70, 40, 60, 50); t.QuadTo(40, 60, 20, 50); t.ClosePath();
            t.EndFill(false);
        }
    }

    [Fact]
    public void TextOutlinesAndTagShapesProduceValidMarkup()
    {
        var baseline = Page();
        var text = new TextContainer(5, 2, 10, 10, 200, RichText.FromPlain("hello"));
        var page = baseline with { Texts = new[] { text } };
        var sheet = PdfExporter.Paginate(page, new PdfExportOptions()).First();
        using var sw = new StringWriter();
        SvgExporter.WriteSheet(page, sheet, true, new FakeText(), sw);
        var doc = XDocument.Parse(sw.ToString());
        var line = doc.Descendants(Ns + "polyline").Single();
        Assert.Equal("#FF0000", line.Attribute("stroke")!.Value);
        Assert.Equal("0.502", line.Attribute("stroke-opacity")!.Value);   // 0x80 alpha
        Assert.Contains(doc.Descendants(Ns + "path"), p => p.Attribute("d")!.Value.Contains('C') && p.Attribute("d")!.Value.Contains('Q'));
    }
}
