using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Nib.Core.Diagnostics;
using Nib.Core.Editing;
using Nib.Core.Export;
using Nib.Core.Geometry;
using Nib.Core.Ink;
using Nib.Core.Model;
using Nib.Storage;
using Xunit;

namespace Nib.Core.Tests;

public class PdfTests
{
    private static ExportPage SamplePage(PageLayout layout, int strokes = 50)
    {
        var list = new List<Stroke>();
        long z = 1;
        for (int i = 0; i < strokes; i++)
            list.Add(TestStrokes.Make(TestStrokes.Scribble(40, 60 + i * 20, 200, 0.6f, i), (PenKind)(i % 4), i % 4 == 3 ? 12f : 2f, StrokeFlags.HasPressure | StrokeFlags.HasTilt, z: z++));
        var texts = new List<TextContainer> { new(1, z++, 50, 30, 300, RichText.FromPlain("Title")) };
        return new ExportPage("Lecture 1 – Ünïcødé", layout, list, texts);
    }

    private sealed class BoxOutliner : ITextOutliner
    {
        public int Calls;
        public void Outline(TextContainer c, IVectorTarget t)
        {
            Calls++;
            t.BeginFill(0xFF000000);
            t.MoveTo(c.X, c.Y); t.LineTo(c.X + 50, c.Y); t.QuadTo(c.X + 60, c.Y + 5, c.X + 50, c.Y + 10); t.LineTo(c.X, c.Y + 10); t.ClosePath();
            t.EndFill(false);
        }
    }

    internal static string Latin1(byte[] b) => Encoding.Latin1.GetString(b);

    [Fact]
    public void Pdf_structure_is_valid_and_xref_offsets_point_at_objects()
    {
        var ms = new MemoryStream();
        var outliner = new BoxOutliner();
        int pages = PdfExporter.Export(new[] { SamplePage(new PageLayout { Background = BackgroundSpec.CollegeRuled }) }, ms, new PdfExportOptions(), outliner);
        Assert.Equal(1, pages);
        Assert.Equal(1, outliner.Calls);
        var bytes = ms.ToArray();
        var text = Latin1(bytes);
        Assert.StartsWith("%PDF-1.7", text);
        Assert.EndsWith("%%EOF\n", text);
        int startxref = int.Parse(Regex.Match(text, @"startxref\n(\d+)").Groups[1].Value);
        Assert.StartsWith("xref", text[startxref..]);
        var entries = Regex.Matches(text[startxref..], @"(\d{10}) 00000 n ");
        int obj = 1;
        foreach (Match m in entries)
        {
            int off = int.Parse(m.Groups[1].Value);
            while (!text[off..].StartsWith($"{obj} 0 obj")) obj++;
            Assert.StartsWith($"{obj} 0 obj", text[off..]);
            obj++;
        }
        Assert.True(entries.Count > 5);
        // Translucent ink uses Multiply blending and knockout groups; no raster images anywhere.
        Assert.Contains("/BM /Multiply", text);
        Assert.Contains("/K true", text);
        Assert.DoesNotContain("/Subtype /Image", text);
        Assert.Contains("/Outlines", text);
    }

    [Fact]
    public void Fixed_page_exports_one_pdf_page_per_used_sheet()
    {
        var layout = new PageLayout { SheetCount = 4 };
        var page = SamplePage(layout, 0) with { Texts = Array.Empty<TextContainer>() };
        var s = TestStrokes.Make(TestStrokes.Line(50, layout.SheetHeight * 2.5f, 200, layout.SheetHeight * 2.5f, 10));
        page = page with { Strokes = new[] { s } };
        var sheets = PdfExporter.Paginate(page, new PdfExportOptions());
        Assert.Equal(3, sheets.Count); // fourth (blank) sheet trimmed
        Assert.Equal(595.3f, sheets[0].WidthPt, 0); // A4 = 595 × 842 pt
        Assert.Equal(841.9f, sheets[0].HeightPt, 0);
    }

    [Fact]
    public void Infinite_canvas_breaks_pages_between_strokes()
    {
        // Rows of strokes every 100 units; breaks should land in the gaps, never through a stroke.
        var strokes = new List<Stroke>();
        for (int i = 0; i < 40; i++)
            strokes.Add(TestStrokes.Make(TestStrokes.Line(0, i * 100 + 20, 600, i * 100 + 60, 20)));
        var page = new ExportPage("long", new PageLayout { Mode = PageMode.Infinite }, strokes, Array.Empty<TextContainer>());
        var sheets = PdfExporter.Paginate(page, new PdfExportOptions());
        Assert.True(sheets.Count >= 4);
        foreach (var sh in sheets.Skip(1))
        {
            float y = sh.Origin.Top;
            Assert.DoesNotContain(strokes, s => s.Bounds.Top < y && s.Bounds.Bottom > y);
        }
    }

    [Fact]
    public void Page_breaker_prefers_full_sheets_and_avoids_cutting_items()
    {
        // Nothing crosses the ideal break: use the full sheet height.
        Assert.Equal(new[] { 0f, 1000f, 2000f }, PageBreaker.FindBreaks(new List<(float, float)> { (0, 990) }, 0, 3000, 1000));
        // An item straddles the ideal break: break just above it instead.
        Assert.Equal(new[] { 0f, 994.5f, 1994.5f, 2994.5f }, PageBreaker.FindBreaks(new List<(float, float)> { (0, 990), (995, 1100) }, 0, 3000, 1000));
        // An item taller than a sheet must be cut, but never before 75% of the sheet.
        var tall = PageBreaker.FindBreaks(new List<(float, float)> { (100, 2900) }, 0, 3000, 1000);
        Assert.All(tall.Zip(tall.Skip(1)), p => Assert.True(p.Second - p.First >= 750));
    }

    [Fact]
    public void Exported_pdf_is_parsed_by_qpdf_when_available()
    {
        // Optional external validation (runs in CI images that have qpdf installed).
        string? qpdf = new[] { "/usr/bin/qpdf", "/usr/local/bin/qpdf", @"C:\Program Files\qpdf\bin\qpdf.exe" }.FirstOrDefault(File.Exists);
        if (qpdf is null) return;
        string path = Path.Combine(Path.GetTempPath(), $"nib-test-{Guid.NewGuid():N}.pdf");
        using (var f = File.Create(path))
            PdfExporter.Export(new[] { SamplePage(new PageLayout { Background = BackgroundSpec.Graph1mm }), SamplePage(new PageLayout { Mode = PageMode.Infinite }, 120) }, f, new PdfExportOptions(), new BoxOutliner());
        var psi = new System.Diagnostics.ProcessStartInfo(qpdf, $"--check \"{path}\"") { RedirectStandardOutput = true, RedirectStandardError = true };
        using var p = System.Diagnostics.Process.Start(psi)!;
        string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        File.Delete(path);
        Assert.True(p.ExitCode == 0, output);
    }
}

public sealed class TempDb : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"nib-{Guid.NewGuid():N}.db");
    public void Dispose()
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            try { File.Delete(Path + suffix); } catch { }
    }
}

public class StorageTests
{
    [Fact]
    public void Starter_content_hierarchy_and_page_roundtrip()
    {
        using var tmp = new TempDb();
        using var store = new NoteStore(tmp.Path);
        long pageId = store.EnsureStarterContent();
        Assert.Equal(pageId, store.EnsureStarterContent());
        var nb = store.ListNotebooks().Single();
        var sec = store.ListSections(nb.Id).Single();
        Assert.Equal("Quick Notes", sec.Name);

        var page = store.LoadPage(pageId)!;
        var tracker = new AutosaveTracker(pageId);
        page.AddObserver(tracker);
        var strokes = Enumerable.Range(0, 200).Select(i => TestStrokes.Make(TestStrokes.Scribble(0, i * 10, 100, 1, i), z: page.NextZ())).ToArray();
        page.Execute(new StrokesCommand("Ink", null, strokes));
        page.Execute(new TextCommand("Text", null, new TextContainer(IdGenerator.Next(), page.NextZ(), 10, 10, 200, RichText.FromPlain("hello\nworld"))));
        page.SetTitle("Lecture 3");
        store.Save(tracker.TakeChanges());

        var loaded = store.LoadPage(pageId)!;
        Assert.Equal(200, loaded.Strokes.Count);
        Assert.Equal("Lecture 3", loaded.Title);
        Assert.Equal("hello\nworld", loaded.Texts.Single().Text.PlainText);
        // Z order preserved.
        Assert.True(loaded.Strokes.Zip(loaded.Strokes.Skip(1)).All(p => p.First.Z < p.Second.Z));

        // Incremental: erase 10 strokes, save only those deletions.
        page.Execute(new StrokesCommand("Erase", page.Strokes.Take(10).ToList(), null));
        var changes = tracker.TakeChanges();
        Assert.Equal(10, changes.DeleteStrokes.Count);
        Assert.Empty(changes.UpsertStrokes);
        store.Save(changes);
        Assert.Equal(190, store.LoadPage(pageId)!.Strokes.Count);
    }

    [Fact]
    public void Pages_subpages_order_move_and_recycle()
    {
        using var tmp = new TempDb();
        using var store = new NoteStore(tmp.Path);
        var nb = store.CreateNotebook("Uni", 0xFF000000);
        var calc = store.CreateSection(nb.Id, null, "Calculus", 0xFF8AA8E4);
        var java = store.CreateSection(nb.Id, store.CreateSectionGroup(nb.Id, null, "Year 1").Id, "Java", 0xFF91BAAE);
        var p1 = store.CreatePage(calc.Id, "Limits", new PageLayout());
        var p3 = store.CreatePage(calc.Id, "Integrals", new PageLayout());
        var sub = store.CreatePage(calc.Id, "Squeeze theorem", new PageLayout(), level: 1, afterPageId: p1.Id);
        var p2 = store.CreatePage(calc.Id, "Derivatives", new PageLayout(), afterPageId: p1.Id); // goes after p1 AND its subpage
        Assert.Equal(new[] { "Limits", "Squeeze theorem", "Derivatives", "Integrals" }, store.ListPages(calc.Id).Select(p => p.Title));
        store.SetPageLevel(p2.Id, 1);
        Assert.Equal(1, store.GetPageInfo(p2.Id)!.Level);
        store.MovePage(p3.Id, java.Id, 1);
        Assert.Single(store.ListPages(java.Id));
        store.DeletePage(sub.Id);
        Assert.Equal(2, store.ListPages(calc.Id).Count);
        store.RestorePage(sub.Id);
        Assert.Equal(3, store.ListPages(calc.Id).Count);
        store.MoveSection(java.Id, nb.Id, null, 0.5);
        Assert.Equal("Java", store.ListSections(nb.Id)[0].Name);
        store.SetFavorite(p1.Id, true);
        store.MarkOpened(p1.Id);
        Assert.Single(store.FavoritePages());
        Assert.Single(store.RecentPages(10));
    }

    [Fact]
    public void Tag_summary_spans_notebooks()
    {
        using var tmp = new TempDb();
        using var store = new NoteStore(tmp.Path);
        foreach (var name in new[] { "Work", "Uni" })
        {
            var nb = store.CreateNotebook(name, 0);
            var sec = store.CreateSection(nb.Id, null, "S", 0);
            var pi = store.CreatePage(sec.Id, name + " page", new PageLayout());
            var page = store.LoadPage(pi.Id)!;
            var tr = new AutosaveTracker(pi.Id);
            page.AddObserver(tr);
            var c = new TextContainer(IdGenerator.Next(), page.NextZ(), 0, 0, 200, RichText.FromPlain($"{name} task\n{name} question"));
            page.Execute(new TextCommand("t", null, c));
            page.Execute(TagOps.Toggle(page.FindText(c.Id)!, 0, TagDefinition.ToDo)!);
            page.Execute(TagOps.Toggle(page.FindText(c.Id)!, 1, TagDefinition.Question)!);
            if (name == "Work") page.Execute(TagOps.SetChecked(page.FindText(c.Id)!, 0, true)!);
            store.Save(tr.TakeChanges());
        }
        var all = store.TagSummary();
        Assert.Equal(4, all.Count);
        var todos = store.TagSummary(TagDefinition.ToDo, includeChecked: false);
        Assert.Single(todos);
        Assert.Equal("Uni task", todos[0].Snippet);
        Assert.Equal("Uni", todos[0].NotebookName);
        Assert.Equal(5, store.ListTagDefinitions().Count);
    }

    [Fact]
    public void Storage_service_reads_while_writing()
    {
        using var tmp = new TempDb();
        using var svc = new StorageService(tmp.Path);
        long pageId = svc.Write(s => s.EnsureStarterContent()).Result;
        var page = svc.Read(s => s.LoadPage(pageId)).Result!;
        var tr = new AutosaveTracker(pageId);
        page.AddObserver(tr);
        for (int i = 0; i < 50; i++) page.Execute(StrokesCommand.Add(TestStrokes.Make(TestStrokes.Scribble(0, i * 5, 300), z: page.NextZ())));
        var save = svc.Save(tr.TakeChanges());
        var read = svc.Read(s => s.ListNotebooks().Count);
        Assert.Equal(1, read.Result);
        save.Wait();
        Assert.Equal(50, svc.Read(s => s.LoadPage(pageId)!.Strokes.Count).Result);
        svc.Flush(TimeSpan.FromSeconds(5));
    }
}

public class PerfVerdictTests
{
    [Fact]
    public void Verdict_fails_when_any_target_is_missed()
    {
        var m = new PerfMeasurements
        {
            RefreshHz = 120,
            PredictionMs = 15,
            InputToPresentMs = Enumerable.Repeat(2.0, 100).ToList(),
            PanFrameIntervalsMs = Enumerable.Repeat(8.33, 300).ToList(),
            UiStallsMs = new() { 3, 5 },
            ColdStartMs = 1200,
            PageSwitchMs = new() { 40, 60, 80 },
        };
        var ok = PerfVerdict.Evaluate(m, new PerfTargets());
        Assert.True(ok.Pass, string.Join("; ", ok.Metrics.Where(x => !x.Pass).Select(x => x.Name)));
        m.UiStallsMs.Add(12);
        Assert.False(PerfVerdict.Evaluate(m, new PerfTargets()).Pass);
    }

    [Fact]
    public void Effective_latency_accounts_for_display_pipeline_and_prediction()
    {
        var m = new PerfMeasurements { RefreshHz = 60, PredictionMs = 0, InputToPresentMs = Enumerable.Repeat(2.0, 50).ToList() };
        var raw = PerfVerdict.Evaluate(m, new PerfTargets()).Metrics.Single(x => x.Name.StartsWith("Effective"));
        Assert.Equal(2 + 1.5 * 1000.0 / 60, raw.Value, 1);
        Assert.False(raw.Pass); // at 60 Hz the display pipeline alone is ~25 ms without prediction
        m.PredictionMs = 15;
        m.RefreshHz = 90;
        var predicted = PerfVerdict.Evaluate(m, new PerfTargets()).Metrics.Single(x => x.Name.StartsWith("Effective"));
        Assert.True(predicted.Pass, $"{predicted.Value}");
    }

    [Fact]
    public void Dropped_frames_are_counted_against_refresh_rate()
    {
        var m = new PerfMeasurements { RefreshHz = 120, PanFrameIntervalsMs = Enumerable.Repeat(8.3, 98).Concat(new[] { 16.6, 16.6 }).ToList() };
        var r = PerfVerdict.Evaluate(m, new PerfTargets { MaxDroppedFramePercent = 1 });
        var metric = r.Metrics.Single(x => x.Name.StartsWith("Pan"));
        Assert.Equal(2, metric.Value, 3);
        Assert.False(metric.Pass);
    }

    [Fact]
    public void Histogram_percentiles()
    {
        var h = new LatencyHistogram();
        for (int i = 1; i <= 100; i++) h.Record(i);
        Assert.InRange(h.Percentile(50), 49.9, 50.1);
        Assert.InRange(h.Percentile(95), 94.9, 95.1);
        Assert.Equal(100, h.Max);
    }
}
