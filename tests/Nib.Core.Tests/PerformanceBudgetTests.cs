using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Nib.Core.Editing;
using Nib.Core.Geometry;
using Nib.Core.Ink;
using Nib.Core.Model;
using Nib.Core.Serialization;
using Xunit;

namespace Nib.Core.Tests;

/// <summary>
/// CPU budgets for the hot paths that sit under the app's frame and latency targets. Budgets are
/// generous multiples of what a Surface Go class CPU achieves, so a failure means an algorithmic
/// regression (O(n) where O(visible) was intended, an allocation in a loop), not a slow CI machine.
/// The end-to-end targets themselves are measured on device by tests/Nib.PerfRunner.
/// </summary>
public class PerformanceBudgetTests
{
    private static Page DensePage(int strokes)
    {
        var page = new Page(1, "dense", new PageLayout { Mode = PageMode.Infinite });
        var list = new List<Stroke>(strokes);
        int perRow = 40;
        for (int i = 0; i < strokes; i++)
        {
            float x = (i % perRow) * 40, y = (i / perRow) * 30;
            list.Add(TestStrokes.Make(TestStrokes.Scribble(x, y, 60, 0.5f, i), z: page.NextZ()));
        }
        page.Execute(new StrokesCommand("bulk", null, list));
        return page;
    }

    private static double TimeMs(Action a, int reps = 1)
    {
        // Warm up past tiered JIT (the shipped app is ReadyToRun/AOT compiled, so it starts optimized).
        for (int i = 0; i < 60; i++) a();
        System.Threading.Thread.Sleep(150);
        a();
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < reps; i++) a();
        return sw.Elapsed.TotalMilliseconds / reps;
    }

    private sealed class NullSink : IStrokeRunSink, IPathSink
    {
        public long Ops;
        public IPathSink BeginRun(in StrokeRun run) => this;
        public void EndRun() { }
        public void BeginFigure(float x, float y) => Ops++;
        public void LineTo(float x, float y) => Ops++;
        public void EndFigure() { }
        public void AddCircle(float cx, float cy, float r) => Ops++;
    }

    [Fact]
    public void Active_stroke_geometry_for_one_frame_is_well_under_a_millisecond()
    {
        // A long active stroke (1,000 samples ≈ 4 s of writing) fully rebuilt. The renderer only builds the
        // new tail each frame, so this is a deliberate worst case. Median of several runs (CI noise).
        var b = new StrokeGeometryBuilder();
        var sink = new NullSink();
        foreach (var kind in new[] { PenKind.Ballpoint, PenKind.Fountain, PenKind.Pencil, PenKind.Highlighter })
        {
            var s = TestStrokes.Make(TestStrokes.Handwriting(0, 0, 1000), kind, kind == PenKind.Highlighter ? 12f : 2f, StrokeFlags.HasPressure | StrokeFlags.HasTilt);
            var times = new double[15];
            TimeMs(() => b.Build(s, sink), 1);
            for (int i = 0; i < times.Length; i++)
            {
                var sw = Stopwatch.StartNew();
                b.Build(s, sink);
                times[i] = sw.Elapsed.TotalMilliseconds;
            }
            Array.Sort(times);
            Assert.True(times[times.Length / 2] < 1.0, $"{kind}: {times[times.Length / 2]:0.000} ms median");
        }
    }

    [Fact]
    public void Geometry_builder_does_not_allocate_after_warm_up()
    {
        var s = TestStrokes.Make(TestStrokes.Scribble(0, 0, 500), PenKind.Fountain, 3f, StrokeFlags.HasPressure | StrokeFlags.HasTilt);
        var b = new StrokeGeometryBuilder();
        var sink = new NullSink();
        b.Build(s, sink);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 50; i++) b.Build(s, sink);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 1024, $"{allocated} bytes allocated in the hot path");
    }

    [Fact]
    public void Ten_thousand_stroke_page_builds_geometry_within_page_switch_budget()
    {
        var page = DensePage(10_000);
        var b = new StrokeGeometryBuilder();
        var sink = new NullSink();
        double ms = TimeMs(() => { foreach (var s in page.Strokes) b.Build(s, sink); }, 3);
        // Background preparation for the page-switch target (≤ 100 ms end to end on device).
        Assert.True(ms < 400, $"{ms:0} ms for 10k strokes");
    }

    [Fact]
    public void Eraser_and_tile_queries_scale_with_visible_strokes_not_page_size()
    {
        var small = DensePage(1_000);
        var big = DensePage(20_000);
        var eraser = new StrokeEraser();
        var hits = new List<Stroke>();
        double Q(Page p) => TimeMs(() => { hits.Clear(); eraser.FindTouched(p, 100, 100, 130, 110, 6, hits); }, 2000);
        double tSmall = Q(small), tBig = Q(big);
        Assert.True(tBig < tSmall * 4 + 0.01, $"eraser query grew from {tSmall * 1000:0.0} µs to {tBig * 1000:0.0} µs");
        Assert.True(tBig < 0.25, $"{tBig * 1000:0} µs per eraser move");

        var visible = new List<Stroke>();
        var tile = RectF.FromXYWH(400, 400, 512, 512);
        double tq = TimeMs(() => { visible.Clear(); big.StrokeIndex.Query(tile, visible); }, 500);
        Assert.True(tq < 0.5, $"{tq * 1000:0} µs per tile query");
        Assert.True(visible.Count < big.Strokes.Count / 20);
    }

    [Fact]
    public void Decoding_a_dense_page_fits_the_page_switch_budget()
    {
        var page = DensePage(10_000);
        var blobs = page.Strokes.Select(s => (s.Id, s.Z, InkCodec.Encode(s))).ToList();
        double ms = TimeMs(() =>
        {
            var list = new List<Stroke>(blobs.Count);
            foreach (var (id, z, blob) in blobs) list.Add(InkCodec.Decode(id, z, blob));
            Page.Load(2, 1, "x", new PageLayout(), list, Array.Empty<TextContainer>());
        });
        Assert.True(ms < 250, $"{ms:0} ms to decode and index 10k strokes (600k points)");
    }

    [Fact]
    public void Stroke_commit_cost_is_constant_on_a_full_page()
    {
        var page = DensePage(10_000);
        var s = TestStrokes.Make(TestStrokes.Scribble(0, 0, 120), z: 0);
        double ms = TimeMs(() =>
        {
            var copy = s.WithZ(page.NextZ(), IdGenerator.Next());
            page.Execute(StrokesCommand.Add(copy));
        }, 200);
        Assert.True(ms < 0.5, $"{ms * 1000:0} µs per committed stroke");
    }
}
