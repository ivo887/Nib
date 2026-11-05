using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Nib.Core.Editing;
using Nib.Core.Geometry;
using Nib.Core.Ink;
using Nib.Core.Model;
using Nib.Core.Serialization;
using Nib.Core.Spatial;
using Xunit;

namespace Nib.Core.Tests;

public class CodecTests
{
    [Fact]
    public void Ink_roundtrips_at_device_precision()
    {
        var pts = TestStrokes.Scribble(12.3f, 45.6f, 500);
        var s = TestStrokes.Make(pts, PenKind.Pencil, 2.5f, StrokeFlags.HasPressure | StrokeFlags.HasTilt,
            new PenDynamics { Curve = PressureCurve.Soft.WithCalibration(0.05f, 0.8f), MinWidthRatio = 0.4f, TiltSensitivity = 0.7f }, id: 42, z: 7, color: 0xFF224466);
        var blob = InkCodec.Encode(s);
        var d = InkCodec.Decode(42, 7, blob);
        Assert.Equal(s.Kind, d.Kind);
        Assert.Equal(s.Color, d.Color);
        Assert.Equal(s.Flags, d.Flags);
        Assert.Equal(s.PointCount, d.PointCount);
        Assert.True(s.Dynamics.Curve.Kind == d.Dynamics.Curve.Kind && MathF.Abs(s.Dynamics.Curve.InputMax - d.Dynamics.Curve.InputMax) < 1e-4f);
        for (int i = 0; i < pts.Length; i++)
        {
            Assert.InRange(MathF.Abs(pts[i].X - d.Points[i].X), 0, 1f / 64);
            Assert.InRange(MathF.Abs(pts[i].Y - d.Points[i].Y), 0, 1f / 64);
            Assert.InRange(MathF.Abs(pts[i].Pressure - d.Points[i].Pressure), 0, 1f / 65535 + 1e-6f);
            Assert.InRange(MathF.Abs(pts[i].TiltX - d.Points[i].TiltX), 0, 0.051f);
            Assert.Equal(pts[i].TimeMs, d.Points[i].TimeMs);
        }
    }

    [Fact]
    public void Ink_is_compact()
    {
        var s = TestStrokes.Make(TestStrokes.Scribble(0, 0, 1000), flags: StrokeFlags.HasPressure | StrokeFlags.HasTilt);
        var blob = InkCodec.Encode(s);
        double perPoint = blob.Length / 1000.0;
        Assert.True(perPoint < 12, $"{perPoint:0.0} bytes per point");
    }

    [Fact]
    public void Every_4096_pressure_level_survives_roundtrip()
    {
        var pts = Enumerable.Range(0, 4096).Select(i => new InkPoint(i, 0, i / 4095f)).ToArray();
        var d = InkCodec.Decode(1, 1, InkCodec.Encode(TestStrokes.Make(pts)));
        Assert.Equal(4096, d.Points.ToArray().Select(p => p.Pressure).Distinct().Count());
    }

    [Fact]
    public void Text_roundtrips_with_tags_and_formatting()
    {
        var rt = new RichText
        {
            Paragraphs = System.Collections.Immutable.ImmutableArray.Create(
                Paragraph.Plain("Buy milk") with { TagIds = System.Collections.Immutable.ImmutableArray.Create(TagDefinition.ToDo), Checked = true },
                new Paragraph { Kind = ParagraphKind.Bullet, Indent = 2, Runs = System.Collections.Immutable.ImmutableArray.Create(
                    new TextRun("Bold ", TextStyle.Default with { Flags = TextStyleFlags.Bold }),
                    new TextRun("Кирилица ✓", TextStyle.Default with { Color = 0xFFC62828, Highlight = 0xFFFFFF00, Font = "Segoe UI" })) })
        };
        var c = new TextContainer(9, 3, 10, 20, 300, rt, 44);
        var d = TextCodec.Decode(9, 3, TextCodec.Encode(c));
        Assert.Equal(c.Text.PlainText, d.Text.PlainText);
        Assert.True(d.Text.Paragraphs[0].Checked);
        Assert.Equal(TagDefinition.ToDo, d.Text.Paragraphs[0].TagIds[0]);
        Assert.Equal(ParagraphKind.Bullet, d.Text.Paragraphs[1].Kind);
        Assert.True(d.Text.Paragraphs[1].Runs[0].Style.Bold);
        Assert.Equal("Segoe UI", d.Text.Paragraphs[1].Runs[1].Style.Font);
        Assert.Equal(44f, d.MeasuredHeight);
    }

    [Fact]
    public void Corrupt_data_throws_format_exception()
    {
        var blob = InkCodec.Encode(TestStrokes.Make(TestStrokes.Line(0, 0, 10, 10, 10)));
        Assert.Throws<FormatException>(() => InkCodec.Decode(1, 1, blob.AsSpan(0, blob.Length / 2)));
    }

    [Fact]
    public void Settings_json_roundtrip()
    {
        var s = new AppSettings { TouchDraws = true, ToolbarDock = Layout.DockSide.Left, ActivePen = 2 };
        s.Pens[0] = s.Pens[0] with { Dynamics = new PenDynamics { Curve = new PressureCurve(PressureCurveKind.Custom, new[] { (0f, 0f), (0.3f, 0.6f), (1f, 1f) }) } };
        var json = NibJson.Serialize(s);
        var back = NibJson.DeserializeSettings(json)!;
        Assert.True(back.TouchDraws);
        Assert.Equal(Layout.DockSide.Left, back.ToolbarDock);
        Assert.Equal(PressureCurveKind.Custom, back.Pens[0].Dynamics.Curve.Kind);
        Assert.Equal(0.6f, back.Pens[0].Dynamics.Curve.Points[1].Y, 3);
        Assert.Contains("\"Left\"", json);
    }

    [Fact]
    public void Layout_json_roundtrip()
    {
        var l = new PageLayout { Mode = PageMode.Fixed, Paper = PaperSize.Letter, Landscape = true, SheetCount = 3, Background = BackgroundSpec.Cornell };
        var back = NibJson.DeserializePageLayout(NibJson.Serialize(l))!;
        Assert.Equal(l, back);
        Assert.Equal(PaperSize.Letter, back.Paper);
        Assert.Equal(BackgroundKind.Cornell, back.Background.Kind);
        Assert.Equal(3, back.SheetCount);
    }
}

public class SpatialGridTests
{
    private sealed class Item : ISpatialItem
    {
        public RectF Bounds { get; init; }
        public int QueryStamp { get; set; }
    }

    [Fact]
    public void Query_matches_brute_force()
    {
        var rnd = new Random(7);
        var grid = new SpatialGrid<Item>(100);
        var items = new List<Item>();
        for (int i = 0; i < 3000; i++)
        {
            float x = (float)rnd.NextDouble() * 5000 - 2500, y = (float)rnd.NextDouble() * 5000 - 2500;
            var it = new Item { Bounds = RectF.FromXYWH(x, y, (float)rnd.NextDouble() * 300, (float)rnd.NextDouble() * 50) };
            items.Add(it);
            grid.Insert(it);
        }
        for (int q = 0; q < 200; q++)
        {
            var r = RectF.FromXYWH((float)rnd.NextDouble() * 5000 - 2500, (float)rnd.NextDouble() * 5000 - 2500, 400, 300);
            var got = new List<Item>();
            grid.Query(r, got);
            var expected = items.Where(i => i.Bounds.Intersects(r)).ToHashSet();
            Assert.Equal(expected.Count, got.Count);
            Assert.True(got.All(expected.Contains));
        }
    }

    [Fact]
    public void Remove_works_and_huge_queries_fall_back()
    {
        var grid = new SpatialGrid<Item>(64);
        var a = new Item { Bounds = RectF.FromXYWH(0, 0, 500, 10) };
        var b = new Item { Bounds = RectF.FromXYWH(1000, 1000, 10, 10) };
        grid.Insert(a); grid.Insert(b);
        Assert.True(grid.Remove(a));
        var got = new List<Item>();
        grid.Query(new RectF(-1e6f, -1e6f, 1e6f, 1e6f), got);
        Assert.Single(got);
        Assert.Same(b, got[0]);
        Assert.Equal(1, grid.Count);
    }
}

public class PageAndUndoTests
{
    private static Page NewPage() => new(1, "t", new PageLayout());

    [Fact]
    public void Add_erase_undo_redo()
    {
        var page = NewPage();
        var s1 = TestStrokes.Make(TestStrokes.Line(0, 0, 100, 0, 20), z: page.NextZ());
        var s2 = TestStrokes.Make(TestStrokes.Line(0, 50, 100, 50, 20), z: page.NextZ());
        page.Execute(StrokesCommand.Add(s1));
        page.Execute(StrokesCommand.Add(s2));
        Assert.Equal(2, page.Strokes.Count);

        var touched = new List<Stroke>();
        new StrokeEraser().FindTouched(page, 50, -3, 50, 3, 2, touched);
        Assert.Single(touched);
        page.Execute(new StrokesCommand("Erase", touched, null));
        Assert.Single(page.Strokes);
        Assert.Same(s2, page.Strokes[0]);

        Assert.True(page.UndoLast());
        Assert.Equal(2, page.Strokes.Count);
        Assert.Same(s1, page.Strokes[0]); // z order restored
        Assert.True(page.RedoLast());
        Assert.Single(page.Strokes);
    }

    [Fact]
    public void Undo_history_applies_to_a_page_reloaded_from_storage()
    {
        // The engine evicts pages from memory and reloads them; their undo history must keep working.
        var page = NewPage();
        var s1 = TestStrokes.Make(TestStrokes.Line(0, 0, 100, 0, 20), z: page.NextZ());
        var s2 = TestStrokes.Make(TestStrokes.Line(0, 50, 100, 50, 20), z: page.NextZ());
        page.Execute(StrokesCommand.Add(s1));
        page.Execute(StrokesCommand.Add(s2));
        page.Execute(new StrokesCommand("Erase", new[] { s1 }, null));

        var reloaded = Page.Load(page.Id, 0, page.Title, page.Layout,
            page.Strokes.Select(s => InkCodec.Decode(s.Id, s.Z, InkCodec.Encode(s))), page.Texts);
        reloaded.Undo = page.Undo;

        Assert.True(reloaded.UndoLast());          // un-erase s1
        Assert.Equal(2, reloaded.Strokes.Count);
        Assert.True(reloaded.UndoLast());          // remove s2 (the decoded copy, matched by id)
        Assert.Equal(s1.Id, Assert.Single(reloaded.Strokes).Id);
        Assert.True(reloaded.RedoLast());
        Assert.True(reloaded.RedoLast());
        Assert.Equal(s2.Id, Assert.Single(reloaded.Strokes).Id);
    }

    [Fact]
    public void Undo_is_unlimited_and_groups_collapse()
    {
        var page = NewPage();
        for (int i = 0; i < 5000; i++) page.Execute(StrokesCommand.Add(TestStrokes.Make(TestStrokes.Line(i, 0, i, 10, 3), z: page.NextZ())));
        page.Undo.BeginGroup("Erase");
        foreach (var s in page.Strokes.Take(10).ToList()) page.Execute(new StrokesCommand("Erase", new[] { s }, null));
        page.Undo.EndGroup();
        Assert.Equal(4990, page.Strokes.Count);
        page.UndoLast();
        Assert.Equal(5000, page.Strokes.Count);
        int undos = 0;
        while (page.UndoLast()) undos++;
        Assert.Equal(5000, undos);
        Assert.Empty(page.Strokes);
    }

    [Fact]
    public void Pixel_eraser_cuts_a_line_in_two()
    {
        var s = TestStrokes.Make(TestStrokes.Line(0, 0, 100, 0, 101));
        var pieces = new StrokeEraser().Cut(s, 50, -5, 50, 5, 4);
        Assert.NotNull(pieces);
        Assert.Equal(2, pieces!.Count);
        Assert.InRange(pieces[0].Points[^1].X, 45.5f, 46.5f);
        Assert.InRange(pieces[1].Points[0].X, 53.5f, 54.5f);
        Assert.All(pieces, p => Assert.Equal(s.Z, p.Z));
        Assert.All(pieces, p => Assert.NotEqual(s.Id, p.Id));
    }

    [Fact]
    public void Pixel_eraser_catches_fast_sweep_between_samples()
    {
        // The eraser jumped from (50,-40) to (50,40) in one event: the capsule must still cut.
        var s = TestStrokes.Make(TestStrokes.Line(0, 0, 100, 0, 3));
        var pieces = new StrokeEraser().Cut(s, 50, -40, 50, 40, 3);
        Assert.NotNull(pieces);
        Assert.Equal(2, pieces!.Count);
        Assert.Null(new StrokeEraser().Cut(s, 50, 20, 50, 40, 3));
    }

    [Fact]
    public void Fixed_page_grows_a_sheet_when_writing_near_the_end()
    {
        var page = NewPage();
        float h = page.Layout.SheetHeight;
        page.Execute(StrokesCommand.Add(TestStrokes.Make(TestStrokes.Line(10, h * 0.9f, 50, h * 0.92f, 5), z: page.NextZ())));
        Assert.Equal(2, page.Layout.SheetCount);
    }

    [Fact]
    public void Lasso_selects_and_transforms_with_tilt_rotation()
    {
        var page = NewPage();
        var inside = TestStrokes.Make(TestStrokes.Line(10, 10, 40, 10, 10, tiltX: 30), flags: StrokeFlags.HasPressure | StrokeFlags.HasTilt, z: page.NextZ());
        var outside = TestStrokes.Make(TestStrokes.Line(200, 200, 240, 200, 10), z: page.NextZ());
        page.Execute(new StrokesCommand("Ink", null, new[] { inside, outside }));
        var lasso = new[] { new Vector2(0, 0), new Vector2(60, 0), new Vector2(60, 30), new Vector2(0, 30) };
        var sel = new Selection();
        SelectionOps.Select(page, lasso, sel);
        Assert.Single(sel.Strokes);
        Assert.Same(inside, sel.Strokes[0]);

        var rot = SelectionOps.RotateAround(new Vector2(25, 10), MathF.PI / 2);
        page.Execute(SelectionOps.Transform(sel, rot, "Rotate"));
        var moved = page.FindStroke(inside.Id)!;
        Assert.InRange(moved.Points[0].X, 24.9f, 25.1f); // (10,10) rotated 90° around (25,10) → (25,-5)
        Assert.InRange(moved.Points[0].Y, -5.1f, -4.9f);
        // Tilt that leaned toward +X now leans toward +Y.
        Assert.InRange(moved.Points[0].TiltY, 29.5f, 30.5f);
        Assert.InRange(MathF.Abs(moved.Points[0].TiltX), 0, 0.5f);
        page.UndoLast();
        Assert.Same(inside, page.FindStroke(inside.Id));
    }

    [Fact]
    public void Resize_scales_width_and_recolor_is_undoable()
    {
        var page = NewPage();
        var s = TestStrokes.Make(TestStrokes.Line(0, 0, 10, 0, 5), width: 2f, z: page.NextZ());
        page.Execute(StrokesCommand.Add(s));
        var sel = new Selection();
        sel.Strokes.Add(s);
        page.Execute(SelectionOps.Transform(sel, SelectionOps.ScaleAround(Vector2.Zero, 2f), "Resize"));
        Assert.Equal(4f, page.FindStroke(s.Id)!.Width, 3);
        sel.Refresh(page);
        page.Execute(SelectionOps.Recolor(sel, 0xFFFF0000));
        Assert.Equal(0xFFFF0000u, page.FindStroke(s.Id)!.Color);
        page.UndoLast();
        Assert.Equal(s.Color, page.FindStroke(s.Id)!.Color);
    }

    [Fact]
    public void Insert_space_pushes_content_below_the_line()
    {
        var page = NewPage();
        var above = TestStrokes.Make(TestStrokes.Line(0, 10, 50, 10, 5), z: page.NextZ());
        var below = TestStrokes.Make(TestStrokes.Line(0, 200, 50, 200, 5), z: page.NextZ());
        page.Execute(new StrokesCommand("Ink", null, new[] { above, below }));
        page.Execute(new TextCommand("Text", null, new TextContainer(5, page.NextZ(), 0, 300, 200, RichText.FromPlain("hello"))));
        page.Execute(SelectionOps.InsertSpace(page, 100, 80, vertical: true)!);
        Assert.Equal(10f, page.FindStroke(above.Id)!.Points[0].Y, 3);
        Assert.Equal(280f, page.FindStroke(below.Id)!.Points[0].Y, 3);
        Assert.Equal(380f, page.FindText(5)!.Y, 3);
        page.UndoLast();
        Assert.Equal(200f, page.FindStroke(below.Id)!.Points[0].Y, 3);
    }

    [Fact]
    public void Tags_toggle_and_extract()
    {
        var page = NewPage();
        var c = new TextContainer(1, page.NextZ(), 0, 0, 300, RichText.FromPlain("Call Ana\nQuestion for prof"));
        page.Execute(new TextCommand("Text", null, c));
        page.Execute(TagOps.Toggle(page.FindText(1)!, 0, TagDefinition.ToDo)!);
        page.Execute(TagOps.Toggle(page.FindText(1)!, 1, TagDefinition.Question)!);
        page.Execute(TagOps.SetChecked(page.FindText(1)!, 0, true)!);
        var rows = TagOps.Extract(page.FindText(1)!).ToList();
        Assert.Equal(2, rows.Count);
        Assert.True(rows[0].Checked);
        Assert.Equal("Question for prof", rows[1].Snippet);
        page.Execute(TagOps.ClearTags(page.FindText(1)!, 1)!);
        Assert.Single(TagOps.Extract(page.FindText(1)!));
    }

    [Fact]
    public void Tags_follow_their_paragraphs_through_free_form_edits()
    {
        var prev = RichText.FromPlain("Buy milk\nCall Ana\nRead ch. 3");
        prev = prev.ReplaceParagraph(0, prev.Paragraphs[0].WithTag(TagDefinition.ToDo, true) with { Checked = true });
        prev = prev.ReplaceParagraph(2, prev.Paragraphs[2].WithTag(TagDefinition.Important, true));
        // A line inserted at the top, the second line edited, the third unchanged.
        var r = TagOps.CarryTags(prev, new[] { "New first line", "Buy milk", "Call Ana tonight", "Read ch. 3" });
        Assert.Empty(r[0].Tags);
        Assert.Equal(TagDefinition.ToDo, r[1].Tags.Single());
        Assert.True(r[1].Checked);
        Assert.Empty(r[2].Tags); // "Call Ana" had none
        Assert.Equal(TagDefinition.Important, r[3].Tags.Single());
        // Editing a tagged line in place keeps its tag.
        var edited = TagOps.CarryTags(prev, new[] { "Buy oat milk", "Call Ana", "Read ch. 3" });
        Assert.Equal(TagDefinition.ToDo, edited[0].Tags.Single());
    }

    [Fact]
    public void Observer_sees_every_change()
    {
        var page = NewPage();
        var tracker = new Nib.Storage.AutosaveTracker(page.Id);
        page.AddObserver(tracker);
        var s = TestStrokes.Make(TestStrokes.Line(0, 0, 5, 5, 3), z: page.NextZ());
        page.Execute(StrokesCommand.Add(s));
        var pieces = new StrokeEraser().Cut(s, 2.5f, 2.5f, 2.5f, 2.5f, 1)!;
        page.Execute(new StrokesCommand("Erase", new[] { s }, pieces));
        page.UndoLast();
        var set = tracker.TakeChanges();
        Assert.Contains(set.UpsertStrokes, x => x.Id == s.Id);
        Assert.All(pieces, p => Assert.Contains(p.Id, set.DeleteStrokes));
        Assert.False(tracker.HasChanges);
    }
}
