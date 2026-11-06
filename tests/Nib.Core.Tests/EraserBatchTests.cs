using System;
using System.Collections.Generic;
using System.Linq;
using Nib.Core.Editing;
using Nib.Core.Geometry;
using Nib.Core.Ink;
using Nib.Core.Model;
using Xunit;

namespace Nib.Core.Tests;

public class EraserBatchTests
{
    private sealed class Spy : IPageObserver
    {
        public int Calls;
        public int RemovedCount, AddedCount;
        public RectF? HintSeen;
        public void OnStrokesChanged(Page page, IReadOnlyList<Stroke> removed, IReadOnlyList<Stroke> added)
        {
            Calls++; RemovedCount += removed.Count; AddedCount += added.Count; HintSeen = page.DirtyHint;
        }
        public void OnTextChanged(Page page, TextContainer? o, TextContainer? n) { }
        public void OnLayoutChanged(Page page, PageLayout o) { }
        public void OnTitleChanged(Page page) { }
    }

    /// <summary>A page of long horizontal strokes, one every 20 units.</summary>
    private static Page PageWithLines(int count)
    {
        var page = new Page(1, "p", new PageLayout { Mode = PageMode.Infinite });
        for (int i = 0; i < count; i++)
        {
            var pts = Enumerable.Range(0, 200).Select(k => new InkPoint(k * 5f, 20f + i * 20f, 0.5f)).ToArray();
            page.Execute(StrokesCommand.Add(new Stroke(IdGenerator.Next(), page.NextZ(), PenKind.Ballpoint, 0xFF000000, 2f, PenDynamics.Default, StrokeFlags.HasPressure, pts)));
        }
        return page;
    }

    private static (Stroke[] Removed, Stroke[] Added) Sweep(Page page, StrokeEraser eraser, IEnumerable<(float X, float Y)> path, float radius, RectF dirty)
    {
        page.BeginBatch();
        var last = path.First();
        var touched = new List<Stroke>();
        foreach (var p in path.Skip(1))
        {
            touched.Clear();
            eraser.FindTouched(page, last.X, last.Y, p.X, p.Y, radius, touched);
            var removed = new List<Stroke>(); var added = new List<Stroke>();
            foreach (var s in touched)
            {
                var pieces = eraser.Cut(s, last.X, last.Y, p.X, p.Y, radius);
                if (pieces is null) continue;
                removed.Add(s); added.AddRange(pieces);
            }
            if (removed.Count > 0) new StrokesCommand("Erase", removed.ToArray(), added.ToArray()).Do(page);
            last = p;
        }
        var net = page.EndBatch(dirty);
        if (net.Removed.Length > 0 || net.Added.Length > 0) page.Undo.Push(new StrokesCommand("Erase", net.Removed, net.Added));
        return net;
    }

    [Fact]
    public void AWholeSweepIsOneNotificationAndOneUndoStep()
    {
        var page = PageWithLines(5);
        var spy = new Spy(); page.AddObserver(spy);
        int undoDepth = page.Undo.UndoCount;

        // Drag straight down across all five lines in 40 small steps.
        var path = Enumerable.Range(0, 41).Select(i => (X: 400f, Y: i * 3f)).ToArray();
        var (removed, added) = Sweep(page, new StrokeEraser(), path, 6f, new RectF(380, 0, 420, 130));

        Assert.Equal(1, spy.Calls);
        Assert.Equal(5, spy.RemovedCount);          // the five originals, however many cuts it took
        Assert.Equal(10, spy.AddedCount);           // each split into a left and a right piece
        Assert.Equal(spy.HintSeen, new RectF(380, 0, 420, 130));
        Assert.Null(page.DirtyHint);                 // the hint only lives during the notification
        Assert.Equal(undoDepth + 1, page.Undo.UndoCount);
    }

    [Fact]
    public void IntermediatePiecesCancelOutOfTheNetChange()
    {
        var page = PageWithLines(1);
        var original = page.Strokes[0];
        // Two separate cuts in the same line: the second cuts a piece created by the first.
        var path = new[] { (X: 300f, Y: 0f), (X: 300f, Y: 30f), (X: 305f, Y: 30f), (X: 305f, Y: 0f), (X: 500f, Y: 0f), (X: 500f, Y: 30f) };
        var (removed, added) = Sweep(page, new StrokeEraser(), path, 5f, new RectF(0, 0, 600, 60));

        Assert.Single(removed);
        Assert.Equal(original.Id, removed[0].Id);
        Assert.Equal(page.Strokes.Count, added.Length);   // the net additions are exactly what is on the page now
        Assert.All(added, a => Assert.NotNull(page.FindStroke(a.Id)));
    }

    [Fact]
    public void UndoRestoresTheOriginalInkExactly()
    {
        var page = PageWithLines(3);
        var before = page.Strokes.Select(s => (s.Id, s.Points.Length)).OrderBy(t => t.Id).ToArray();
        var path = Enumerable.Range(0, 30).Select(i => (X: 250f, Y: i * 2f)).ToArray();
        Sweep(page, new StrokeEraser(), path, 6f, new RectF(230, 0, 270, 60));
        Assert.NotEqual(before.Length, page.Strokes.Count);   // really cut

        Assert.True(page.UndoLast());
        var after = page.Strokes.Select(s => (s.Id, s.Points.Length)).OrderBy(t => t.Id).ToArray();
        Assert.Equal(before, after);
        Assert.True(page.RedoLast());
        Assert.NotEqual(before.Length, page.Strokes.Count);
    }

    [Fact]
    public void BatchedAndStepwiseErasingLeaveTheSameInk()
    {
        var stepwise = PageWithLines(4);
        var batched = PageWithLines(4);
        var path = Enumerable.Range(0, 60).Select(i => (X: 100f + i * 6f, Y: 10f + i * 1.2f)).ToArray();

        var eraser = new StrokeEraser();
        var last = path[0];
        foreach (var p in path.Skip(1))
        {
            var touched = new List<Stroke>(); eraser.FindTouched(stepwise, last.X, last.Y, p.X, p.Y, 6f, touched);
            var removed = new List<Stroke>(); var added = new List<Stroke>();
            foreach (var s in touched) { var pcs = eraser.Cut(s, last.X, last.Y, p.X, p.Y, 6f); if (pcs is null) continue; removed.Add(s); added.AddRange(pcs); }
            if (removed.Count > 0) stepwise.Execute(new StrokesCommand("Erase", removed, added));
            last = p;
        }
        Sweep(batched, new StrokeEraser(), path, 6f, new RectF(0, 0, 600, 100));

        static string Shape(Page pg) => string.Join("|", pg.Strokes.Select(s => $"{s.Points[0].X:0.#},{s.Points[0].Y:0.#}-{s.Points[^1].X:0.#},{s.Points[^1].Y:0.#}").OrderBy(x => x));
        Assert.Equal(Shape(stepwise), Shape(batched));
    }

    [Fact]
    public void OneBatchedSweepIsFarCheaperToObserveThanPerStepEdits()
    {
        // Every observer notification costs the renderer a tile repaint, so the number of notifications is what matters.
        var page = PageWithLines(20);
        var spy = new Spy(); page.AddObserver(spy);
        var path = Enumerable.Range(0, 200).Select(i => (X: 50f + i * 4f, Y: 10f + (i % 2) * 380f)).ToArray();
        Sweep(page, new StrokeEraser(), path, 6f, new RectF(0, 0, 900, 400));
        Assert.Equal(1, spy.Calls);
    }
}
