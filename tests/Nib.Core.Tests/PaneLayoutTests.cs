using System.Collections.Generic;
using Nib.Core.Layout;
using Xunit;

namespace Nib.Core.Tests;

public class PaneLayoutTests
{
    [Theory]
    [InlineData(1920, 1080, RailMode.Expanded, PagePaneMode.Inline)]
    [InlineData(1440, 900, RailMode.Expanded, PagePaneMode.Inline)]
    [InlineData(1100, 800, RailMode.Compact, PagePaneMode.Inline)]
    [InlineData(900, 700, RailMode.Compact, PagePaneMode.Overlay)]
    [InlineData(700, 500, RailMode.Hidden, PagePaneMode.Overlay)]
    [InlineData(420, 800, RailMode.Hidden, PagePaneMode.Overlay)]
    public void NarrowerWindowsGiveUpPanesInOrder(double w, double h, RailMode rail, PagePaneMode pages)
    {
        var l = PaneLayoutRules.Decide(w, h, DockSide.Top);
        Assert.Equal(rail, l.Rail);
        Assert.Equal(pages, l.Pages);
    }

    [Theory]
    [InlineData(2560)] [InlineData(1920)] [InlineData(1366)] [InlineData(1100)] [InlineData(980)] [InlineData(800)] [InlineData(600)] [InlineData(400)]
    public void ThePageAlwaysKeepsItsWidthWhenPanesAreInline(double w)
    {
        var l = PaneLayoutRules.Decide(w, 900, DockSide.Top);
        if (l.Pages == PagePaneMode.Inline) Assert.True(l.PageWidthLeft >= PaneLayoutRules.MinPageWidth, $"{w}: {l.PageWidthLeft}");
        Assert.Equal(w - l.RailWidth - (l.Pages == PagePaneMode.Inline ? l.PageWidth : 0), l.PageWidthLeft, 3);
    }

    [Fact]
    public void PortraitTabletsKeepThePageListAsADrawer()
    {
        // 1000 DIPs wide but taller than wide: not enough room for a second column.
        Assert.Equal(PagePaneMode.Overlay, PaneLayoutRules.Decide(1000, 1400, DockSide.Top).Pages);
        Assert.Equal(PagePaneMode.Inline, PaneLayoutRules.Decide(1000, 700, DockSide.Top).Pages);
    }

    [Fact]
    public void UserPreferencesShrinkButNeverForceTooMuch()
    {
        Assert.Equal(RailMode.Compact, PaneLayoutRules.Decide(1920, 1080, DockSide.Top, railWantsCompact: true).Rail);
        Assert.Equal(PagePaneMode.Overlay, PaneLayoutRules.Decide(1920, 1080, DockSide.Top, pagesHidden: true).Pages);
        // A narrow window stays Hidden/Overlay whatever was preferred; "compact" is not forced on it.
        Assert.Equal(RailMode.Hidden, PaneLayoutRules.Decide(500, 800, DockSide.Top, railWantsCompact: true).Rail);
    }

    [Fact]
    public void TabsAndToolbarGoCompactOnNarrowOrShortWindows()
    {
        Assert.False(PaneLayoutRules.Decide(900, 700, DockSide.Top).CompactTabs);
        Assert.True(PaneLayoutRules.Decide(650, 700, DockSide.Top).CompactTabs);
        Assert.True(PaneLayoutRules.Decide(500, 800, DockSide.Top).CompactToolbar);
        Assert.False(PaneLayoutRules.Decide(1200, 500, DockSide.Top).CompactToolbar);   // wide: a top toolbar fits
        Assert.True(PaneLayoutRules.Decide(1200, 500, DockSide.Left).CompactToolbar);   // short: a side toolbar does not
    }

    [Fact]
    public void ZeroOrNegativeSizesDoNotThrow()
    {
        var l = PaneLayoutRules.Decide(0, 0, DockSide.Top);
        Assert.Equal(RailMode.Hidden, l.Rail);
        Assert.Equal(PagePaneMode.Overlay, l.Pages);
    }
}

public class WorkAreaFollowTests
{
    private static readonly RectI Monitor = new(0, 0, 2560, 1440);

    // Work areas for a 48 px taskbar on each edge, and for an auto-hide taskbar (the whole monitor).
    private static readonly RectI Bottom = new(0, 0, 2560, 1392);
    private static readonly RectI Top = new(0, 48, 2560, 1392);
    private static readonly RectI Left = new(64, 0, 2496, 1440);
    private static readonly RectI Right = new(0, 0, 2496, 1440);
    private static readonly RectI AutoHide = Monitor;

    public static IEnumerable<object[]> Pairs()
    {
        var all = new[] { Bottom, Top, Left, Right, AutoHide };
        foreach (var a in all) foreach (var b in all) if (a != b) yield return new object[] { a, b };
    }

    [Theory]
    [MemberData(nameof(Pairs))]
    public void AWindowFillingTheWorkAreaKeepsFillingItWhenTheTaskbarChanges(RectI oldWork, RectI newWork)
    {
        var window = oldWork;
        var moved = PlacementCalculator.FollowWorkArea(window, oldWork, newWork);
        Assert.Equal(newWork, moved);
    }

    [Theory]
    [MemberData(nameof(Pairs))]
    public void TheInvisibleResizeBordersAreKept(RectI oldWork, RectI newWork)
    {
        // Windows 11 windows extend 8 px past the visible frame on each side.
        var window = new RectI(oldWork.X - 8, oldWork.Y, oldWork.Width + 16, oldWork.Height + 8);
        var moved = PlacementCalculator.FollowWorkArea(window, oldWork, newWork);
        Assert.Equal(new RectI(newWork.X - 8, newWork.Y, newWork.Width + 16, newWork.Height + 8), moved);
    }

    [Theory]
    [MemberData(nameof(Pairs))]
    public void AFreeFloatingWindowOnlyMovesIfItWouldBeCovered(RectI oldWork, RectI newWork)
    {
        var window = new RectI(700, 300, 1000, 700);   // well inside every work area
        Assert.Equal(window, PlacementCalculator.FollowWorkArea(window, oldWork, newWork));
    }

    [Fact]
    public void AFloatingWindowUnderANewTaskbarIsPushedClear()
    {
        var low = new RectI(300, 700, 1200, 700);        // bottom edge at 1400: under a 48 px taskbar at the bottom
        var moved = PlacementCalculator.FollowWorkArea(low, AutoHide, Bottom);
        Assert.True(moved.Bottom <= Bottom.Bottom);
        Assert.True(Bottom.Contains(moved));
    }

    [Fact]
    public void ResultsAlwaysStayInsideTheNewWorkArea()
    {
        foreach (var oldWork in new[] { Bottom, Top, Left, Right, AutoHide })
        foreach (var newWork in new[] { Bottom, Top, Left, Right, AutoHide })
        foreach (var w in new[] { oldWork, new RectI(0, 0, 2560, 1440), new RectI(100, 100, 800, 600), new RectI(2000, 1200, 560, 240) })
        {
            var r = PlacementCalculator.FollowWorkArea(w, oldWork, newWork);
            if (PlacementCalculator.Fills(w, oldWork)) continue; // filling windows keep their (tolerance-sized) overhang by design
            Assert.True(newWork.Contains(r), $"{w} {oldWork} -> {newWork} gave {r}");
        }
    }

    [Fact]
    public void AnEmptyOrMissingWorkAreaChangesNothing()
    {
        var w = new RectI(100, 100, 800, 600);
        Assert.Equal(w, PlacementCalculator.FollowWorkArea(w, Bottom, default));
        Assert.False(PlacementCalculator.Fills(w, default));
    }

    [Theory]
    [InlineData(1.0)] [InlineData(1.25)] [InlineData(1.5)] [InlineData(2.0)] [InlineData(3.0)]
    public void SafeAreaIsInDipsAtEveryScaleWithAnAutoHideTaskbarOnAnyEdge(double scale)
    {
        foreach (var edge in new[] { ScreenEdge.Bottom, ScreenEdge.Top, ScreenEdge.Left, ScreenEdge.Right })
        {
            var ctx = new ScreenContext(Monitor, Monitor, Monitor, false, true, edge, default, scale);
            var i = SafeAreaCalculator.Compute(ctx);
            double total = i.Left + i.Top + i.Right + i.Bottom;
            Assert.Equal(SafeAreaCalculator.AutoHideStrip, total, 3);  // exactly one edge keeps the reveal strip, whatever the scale
            double onEdge = edge switch { ScreenEdge.Bottom => i.Bottom, ScreenEdge.Top => i.Top, ScreenEdge.Left => i.Left, _ => i.Right };
            Assert.Equal(SafeAreaCalculator.AutoHideStrip, onEdge, 3);
        }
    }
}
