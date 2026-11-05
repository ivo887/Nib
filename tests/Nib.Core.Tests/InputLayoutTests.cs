using System;
using System.Collections.Generic;
using Nib.Core.Geometry;
using Nib.Core.Input;
using Nib.Core.Layout;
using Nib.Core.Model;
using Nib.Core.Rendering;
using Xunit;

namespace Nib.Core.Tests;

public class GestureTests
{
    private sealed class Rec : ITouchGestureHandler
    {
        public float Dx, Dy, Zoom = 1;
        public int Tap;
        public float Vx, Vy;
        public bool Ended;
        public void OnPan(float dx, float dy) { Dx += dx; Dy += dy; }
        public void OnZoom(float f, float cx, float cy) => Zoom *= f;
        public void OnPanEnd(float vx, float vy) { Vx = vx; Vy = vy; Ended = true; }
        public void OnMultiFingerTap(int n) => Tap = n;
    }

    [Fact]
    public void Two_finger_tap_is_undo_and_does_not_pan()
    {
        var r = new Rec();
        var g = new TouchGestureEngine(r);
        g.Down(1, 100, 100, 0); g.Down(2, 160, 100, 20);
        g.Move(1, 102, 101, 60); g.Move(2, 161, 99, 60);
        g.Up(1, 102, 101, 120); g.Up(2, 161, 99, 130);
        Assert.Equal(2, r.Tap);
        Assert.Equal(0f, r.Dx);
        Assert.Equal(1f, r.Zoom);
    }

    [Fact]
    public void Three_finger_tap_is_redo()
    {
        var r = new Rec();
        var g = new TouchGestureEngine(r);
        g.Down(1, 100, 100, 0); g.Down(2, 150, 100, 10); g.Down(3, 200, 100, 20);
        g.Up(1, 100, 100, 100); g.Up(2, 150, 100, 105); g.Up(3, 200, 100, 110);
        Assert.Equal(3, r.Tap);
    }

    [Fact]
    public void Slow_tap_is_not_a_tap()
    {
        var r = new Rec();
        var g = new TouchGestureEngine(r);
        g.Down(1, 100, 100, 0); g.Down(2, 150, 100, 10);
        g.Up(1, 100, 100, 700); g.Up(2, 150, 100, 710);
        Assert.Equal(0, r.Tap);
    }

    [Fact]
    public void Pinch_zooms_by_distance_ratio_and_pans_with_centroid()
    {
        var r = new Rec();
        var g = new TouchGestureEngine(r) { OneFingerPans = false };
        g.Down(1, 100, 100, 0); g.Down(2, 200, 100, 0);
        // Spread to double distance, centroid moves +50 in y.
        for (int i = 1; i <= 10; i++)
        {
            float t = i / 10f;
            g.Move(1, 100 - 50 * t, 100 + 50 * t, i * 8);
            g.Move(2, 200 + 50 * t, 100 + 50 * t, i * 8);
        }
        Assert.InRange(r.Zoom, 1.8f, 2.05f);
        Assert.InRange(r.Dy, 35f, 51f);
        g.Up(1, 50, 150, 100); g.Up(2, 250, 150, 100);
        Assert.Equal(0, r.Tap);
    }

    [Fact]
    public void Fling_reports_velocity_for_inertia()
    {
        var r = new Rec();
        var g = new TouchGestureEngine(r) { OneFingerPans = true };
        g.Down(1, 100, 500, 0);
        for (int i = 1; i <= 12; i++) g.Move(1, 100, 500 - i * 20, i * 8);
        g.Up(1, 100, 260, 100);
        Assert.True(r.Ended);
        Assert.InRange(r.Vy, -3000f, -2000f); // 20 DIPs per 8 ms = 2500 DIPs/s upward
    }

    [Fact]
    public void Inertia_decays_and_stops()
    {
        var a = new InertiaAnimator();
        a.Start(0, 2000);
        float total = 0;
        int frames = 0;
        while (a.Step(1 / 120f, out _, out float dy) && frames < 1000) { total += dy; frames++; }
        Assert.False(a.IsActive);
        Assert.InRange(total, 550f, 660f); // v·τ = 650
        Assert.InRange(frames, 100, 400);
    }

    [Fact]
    public void Palm_rejection_ignores_touch_near_pen_and_big_contacts()
    {
        var p = new PalmRejection();
        Assert.False(p.ShouldRejectTouch(0, 10, 10));
        Assert.True(p.ShouldRejectTouch(0, 50, 40));
        p.PenSeen(1000);
        Assert.True(p.ShouldRejectTouch(1100, 10, 10));
        p.PenLeft(1200);
        Assert.True(p.ShouldRejectTouch(1300, 10, 10));
        Assert.False(p.ShouldRejectTouch(1200 + PalmRejection.GraceMs + 1, 10, 10));
    }
}

public class ViewTests
{
    [Fact]
    public void Page_width_preset_fits_and_resizing_keeps_reading_position()
    {
        var v = new ViewController();
        v.SetViewport(1200, 800);
        v.SetPage(new PageLayout { Paper = PaperSize.A4, SheetCount = 3 }, null);
        float pageW = new PageLayout().SheetWidth;
        Assert.Equal((1200 - 2 * v.Margin) / pageW, v.State.Zoom, 3);
        v.Pan(0, -900);
        float topBefore = v.State.ToPage(600, 0).Y;
        v.SetViewport(800, 1200); // rotate to portrait
        Assert.Equal((800 - 2 * v.Margin) / pageW, v.State.Zoom, 3);
        Assert.InRange(v.State.ToPage(400, 0).Y, topBefore - 2, topBefore + 2);
    }

    [Fact]
    public void Zoom_keeps_point_under_fingers_and_clamps()
    {
        var v = new ViewController();
        v.SetViewport(1000, 800);
        v.SetPage(new PageLayout { Mode = PageMode.Infinite }, null);
        var before = v.State.ToPage(300, 200);
        v.ZoomAt(2f, 300, 200);
        var after = v.State.ToPage(300, 200);
        Assert.InRange(after.X, before.X - 0.01f, before.X + 0.01f);
        Assert.InRange(after.Y, before.Y - 0.01f, before.Y + 0.01f);
        v.ZoomAt(1000f, 0, 0);
        Assert.Equal(ViewController.MaxZoom, v.State.Zoom);
        Assert.Null(v.StickyPreset);
    }

    [Fact]
    public void Fixed_page_cannot_be_scrolled_away()
    {
        var v = new ViewController();
        v.SetViewport(1000, 800);
        v.SetPage(new PageLayout(), null);
        v.Pan(5000, 5000);
        Assert.True(v.State.OffsetY <= v.Margin + 0.01f);
        v.Pan(-50000, -50000);
        var b = new PageLayout().Bounds;
        Assert.True(v.State.OffsetY + b.Height * v.State.Zoom >= 800 - v.Margin - 0.01f);
    }

    [Fact]
    public void Fit_page_shows_whole_sheet()
    {
        var v = new ViewController();
        v.SetViewport(1600, 900);
        v.SetPage(new PageLayout(), null);
        v.ApplyPreset(ZoomPreset.FitPage);
        var l = new PageLayout();
        Assert.True(l.SheetHeight * v.State.Zoom <= 900 - 2 * v.Margin + 0.01f);
        Assert.True(v.State.OffsetY >= 0);
    }

    [Fact]
    public void Ensure_visible_scrolls_caret_above_keyboard()
    {
        var v = new ViewController();
        v.SetViewport(1000, 800);
        v.SetPage(new PageLayout { SheetCount = 2 }, null);
        v.SetViewport(1000, 450); // touch keyboard took the bottom 350 DIPs
        var caret = RectF.FromXYWH(100, 900, 2, 20);
        v.EnsureVisible(caret);
        var screen = v.State.ToScreen(100, 920);
        Assert.True(screen.Y <= 450 - 15);
    }
}

public class SafeAreaTests
{
    private static readonly RectI Monitor = new(0, 0, 2880, 1920);

    private static ScreenContext Ctx(RectI client, RectI work, bool max = true, ScreenEdge auto = ScreenEdge.None, RectI pane = default, double scale = 2) =>
        new(client, Monitor, work, false, max, auto, pane, scale);

    [Theory]
    [InlineData("bottom")]
    [InlineData("top")]
    [InlineData("left")]
    [InlineData("right")]
    public void Restored_window_under_taskbar_gets_inset_on_that_side(string edge)
    {
        RectI work = edge switch
        {
            "bottom" => new(0, 0, 2880, 1824),
            "top" => new(0, 96, 2880, 1824),
            "left" => new(96, 0, 2784, 1920),
            _ => new(0, 0, 2784, 1920),
        };
        var client = new RectI(0, 0, 2880, 1920); // e.g. restored to an old full-screen size
        var i = SafeAreaCalculator.Compute(Ctx(client, work, max: false));
        switch (edge)
        {
            case "bottom": Assert.Equal(48, i.Bottom); Assert.Equal(0, i.Top); break;
            case "top": Assert.Equal(48, i.Top); Assert.Equal(0, i.Bottom); break;
            case "left": Assert.Equal(48, i.Left); Assert.Equal(0, i.Right); break;
            case "right": Assert.Equal(48, i.Right); Assert.Equal(0, i.Left); break;
        }
    }

    [Fact]
    public void Maximized_window_inside_work_area_needs_no_inset()
    {
        var work = new RectI(0, 0, 2880, 1824);
        Assert.Equal(Insets.Zero, SafeAreaCalculator.Compute(Ctx(work, work)));
    }

    [Theory]
    [InlineData(ScreenEdge.Bottom)]
    [InlineData(ScreenEdge.Top)]
    [InlineData(ScreenEdge.Left)]
    [InlineData(ScreenEdge.Right)]
    public void Auto_hide_taskbar_keeps_reveal_strip(ScreenEdge edge)
    {
        var i = SafeAreaCalculator.Compute(Ctx(Monitor, Monitor, auto: edge));
        double v = edge switch { ScreenEdge.Bottom => i.Bottom, ScreenEdge.Top => i.Top, ScreenEdge.Left => i.Left, _ => i.Right };
        Assert.Equal(SafeAreaCalculator.AutoHideStrip, v);
    }

    [Fact]
    public void Full_screen_ignores_taskbar_and_auto_hide()
    {
        var c = new ScreenContext(Monitor, Monitor, new RectI(0, 0, 2880, 1824), true, false, ScreenEdge.Bottom, default, 2);
        Assert.Equal(Insets.Zero, SafeAreaCalculator.Compute(c));
    }

    [Fact]
    public void Docked_touch_keyboard_insets_bottom()
    {
        var work = new RectI(0, 0, 2880, 1824);
        var pane = new RectI(0, 1224, 2880, 600);
        var i = SafeAreaCalculator.Compute(Ctx(work, work, pane: pane));
        Assert.Equal(300, i.Bottom);
    }

    [Fact]
    public void Placement_is_clamped_and_restored_relative_to_work_area()
    {
        var work = new RectI(0, 0, 1920, 1040);
        var r = PlacementCalculator.ClampToWorkArea(new RectI(1500, 900, 800, 600), work);
        Assert.True(work.Contains(r));
        var saved = PlacementCalculator.ToSaved(new RectI(100, 50, 960, 520), work, false, false);
        // Taskbar moved to the left: work area shifts and shrinks.
        var newWork = new RectI(48, 0, 1872, 1080);
        var restored = PlacementCalculator.FromSaved(saved, newWork);
        Assert.True(newWork.Contains(restored));
        Assert.Equal(936, restored.Width);
    }

    [Theory]
    [InlineData(2736, 1824, 2.0, false)]  // 3:2 landscape (Surface Pro, 200%)
    [InlineData(1824, 2736, 2.0, true)]   // 3:2 portrait: sidebar overlays so the page keeps full width
    [InlineData(1920, 1280, 1.5, false)]  // 3:2 (Surface Go, 150%)
    [InlineData(1920, 1200, 1.0, false)]  // 16:10
    [InlineData(1080, 1920, 1.0, false)]  // 16:9 portrait monitor: wide enough for a docked sidebar
    [InlineData(1920, 1080, 1.0, false)]  // 16:9
    [InlineData(1024, 768, 1.0, false)]   // 4:3
    [InlineData(1366, 1024, 2.0, true)]   // 4:3 at 200%: narrow
    [InlineData(3440, 1440, 1.0, false)]  // ultrawide
    public void Responsive_layout_for_every_aspect_ratio(int w, int h, double scale, bool overlay)
    {
        var l = ResponsiveLayout.Decide(w / scale, h / scale, DockSide.Top);
        Assert.Equal(overlay, l.SidebarOverlays);
        Assert.InRange(l.SidebarWidth, 220, 280);
    }
}

public class BackgroundTests
{
    private sealed class Count : IBackgroundSink
    {
        public int Lines, Dots, Fills;
        public List<float> Ys = new();
        public void FillRect(RectF r, uint c) => Fills++;
        public void Line(float x0, float y0, float x1, float y1, float w, uint c) { Lines++; Ys.Add(y0); }
        public void Dot(float cx, float cy, float r, uint c) => Dots++;
    }

    [Fact]
    public void College_ruled_a4_sheet_has_expected_rules()
    {
        var layout = new PageLayout { Background = BackgroundSpec.CollegeRuled with { MarginLine = false } };
        var sink = new Count();
        BackgroundPattern.Emit(layout, layout.SheetRect(0), 1f, sink);
        int expected = (int)((layout.SheetHeight - BackgroundPattern.HeaderHeight) / BackgroundSpec.Rules.College);
        Assert.InRange(sink.Lines, expected - 1, expected + 1);
        Assert.Equal(BackgroundPattern.HeaderHeight, sink.Ys[0], 2);
    }

    [Fact]
    public void Only_visible_region_is_emitted_and_minor_lines_drop_when_zoomed_out()
    {
        var layout = new PageLayout { Mode = PageMode.Infinite, Background = BackgroundSpec.Squares5mm };
        var near = new Count();
        BackgroundPattern.Emit(layout, RectF.FromXYWH(0, 0, 200, 200), 1f, near);
        Assert.InRange(near.Lines, 20, 24);
        var far = new Count();
        BackgroundPattern.Emit(layout, RectF.FromXYWH(0, 0, 20000, 20000), 0.1f, far);
        Assert.Equal(0, far.Lines);
    }

    [Fact]
    public void Cornell_has_structure_lines_and_dots_grid_has_dots()
    {
        var cornell = new PageLayout { Background = BackgroundSpec.Cornell };
        var s = new Count();
        BackgroundPattern.Emit(cornell, cornell.SheetRect(0), 1f, s);
        Assert.True(s.Lines > 20);
        var dots = new PageLayout { Background = BackgroundSpec.DotGrid5mm };
        var d = new Count();
        BackgroundPattern.Emit(dots, RectF.FromXYWH(0, 0, 189, 189), 1f, d);
        Assert.InRange(d.Dots, 100, 121);
    }

    [Fact]
    public void Defaults_inherit_section_then_notebook_then_app()
    {
        var nb = new PageDefaults { Background = BackgroundSpec.DotGrid5mm, Paper = "Letter" };
        var sec = new PageDefaults { Background = BackgroundSpec.Cornell };
        var l = PageDefaults.ResolveNewPage(nb, sec);
        Assert.Equal(BackgroundKind.Cornell, l.Background.Kind);
        Assert.Equal(PaperSize.Letter, l.Paper);
        Assert.Equal(PageMode.Fixed, l.Mode);
        var app = PageDefaults.ResolveNewPage(null, null);
        Assert.Equal(PaperSize.A4, app.Paper);
    }

    [Fact]
    public void Tile_math_is_consistent()
    {
        float ppu = TileMath.PixelsPerUnit(TileMath.ZoomKey(1.5f));
        Assert.InRange(ppu, 1.47f, 1.53f);
        TileMath.TileRange(RectF.FromXYWH(0, 0, 1000, 1000), ppu, out int x0, out int y0, out int x1, out int y1);
        var last = TileMath.TileRect(x1, y1, ppu);
        Assert.True(last.Right >= 1000 && last.Left <= 1000);
        Assert.Equal(0, x0);
        Assert.Equal(0, y0);
        float snapped = TileMath.SnapZoom(1.234f, 2f);
        Assert.Equal(TileMath.ZoomKey(snapped * 2f), TileMath.ZoomKey(1.234f * 2f));
    }
}
