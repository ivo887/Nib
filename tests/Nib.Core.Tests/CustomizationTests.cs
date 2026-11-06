using System.Linq;
using Nib.Core.Input;
using Nib.Core.Model;
using Nib.Core.Serialization;
using Xunit;

namespace Nib.Core.Tests;

public class CustomizationTests
{
    [Fact]
    public void DefaultToolbarIsAlreadyNormal()
    {
        Assert.Equal(ToolbarLayout.Default, ToolbarLayout.Normalize(ToolbarLayout.Default));
        Assert.All(ToolbarLayout.Default.Where(i => i != ToolbarLayout.Separator), id => Assert.Contains(id, ToolbarLayout.AllIds));
    }

    [Fact]
    public void NormalizeDropsUnknownDuplicatesAndStraySeparators()
    {
        var r = ToolbarLayout.Normalize(new[] { "|", "eraser", "nope", "eraser", "|", "|", "undo", "|" });
        Assert.Equal(new[] { "eraser", "|", "undo", "more" }, r);
    }

    [Fact]
    public void MoreMenuCannotBeRemoved()
    {
        Assert.Equal(new[] { "pens", "more" }, ToolbarLayout.Normalize(new[] { "pens" }));
        Assert.Equal(new[] { "more" }, ToolbarLayout.Normalize(null));
    }

    [Fact]
    public void MoreMenuKeepsItsPosition()
    {
        Assert.Equal(new[] { "more", "|", "eraser" }, ToolbarLayout.Normalize(new[] { "more", "|", "eraser" }));
    }

    [Fact]
    public void PointerSettingsAreClampedToSaneRanges()
    {
        var p = new PointerSettings { FixedSize = 5000, Opacity = 0, LineWidth = -3 }.Sanitized();
        Assert.Equal(80f, p.FixedSize);
        Assert.Equal(0.1f, p.Opacity);
        Assert.Equal(0.5f, p.LineWidth);
    }

    [Fact]
    public void CustomSettingsSurviveAJsonRoundTrip()
    {
        var s = new AppSettings
        {
            Pointer = new PointerSettings { Style = PointerStyle.Crosshair, SizeMode = PointerSizeMode.Fixed, FixedSize = 22, ColorMode = PointerColorMode.Custom, CustomColor = 0xFFE53935, Opacity = 0.6f, CenterDot = true, ShowWhileDrawing = true },
            ToolbarItems = new() { "pens", "|", "eraser", "zoom", "more" },
            ToolbarButtonSize = 48,
            PalmRejectionMs = 800,
            PalmSizeDips = 50,
            TwoFingerTapUndo = false,
        };
        s.KeyMap["NewPage"] = "Ctrl+Shift+N";

        var back = NibJson.DeserializeSettings(NibJson.Serialize(s))!;
        Assert.Equal(s.Pointer, back.Pointer);
        Assert.Equal(s.ToolbarItems, back.ToolbarItems);
        Assert.Equal(48, back.ToolbarButtonSize);
        Assert.Equal(800, back.PalmRejectionMs);
        Assert.False(back.TwoFingerTapUndo);
        Assert.Equal("Ctrl+Shift+N", back.KeyMap["NewPage"]);
    }

    [Fact]
    public void OldSettingsFilesStillLoadWithDefaults()
    {
        var back = NibJson.DeserializeSettings("{\"Theme\":\"Dark\"}")!;
        Assert.Equal(ThemeMode.Dark, back.Theme);
        Assert.Equal(PointerStyle.Nib, back.Pointer.Style);
        Assert.Equal(ToolbarLayout.Default, back.ToolbarItems);
        Assert.Equal(450, back.PalmRejectionMs);
    }

    [Fact]
    public void PalmRejectionHonoursTheConfiguredDelayAndSize()
    {
        var p = new PalmRejection { Grace = 100, Size = 60 };
        p.PenLeft(1000);
        Assert.True(p.ShouldRejectTouch(1050, 10, 10));
        Assert.False(p.ShouldRejectTouch(1101, 10, 10));
        Assert.False(p.ShouldRejectTouch(2000, 50, 50)); // wider than the default 34, under the custom 60
        Assert.True(p.ShouldRejectTouch(2000, 70, 10));
    }
}
