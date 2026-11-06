using System.Linq;
using Nib.Core.Model;
using Nib.Storage;
using Xunit;

namespace Nib.Core.Tests;

public class NotebookCustomizationTests
{
    [Fact]
    public void NotebookColorRenameAndDefaultsArePersisted()
    {
        using var tmp = new TempDb();
        using var store = new NoteStore(tmp.Path);
        var nb = store.CreateNotebook("Uni", 0xFF7A5AF8);

        store.SetNotebookColor(nb.Id, 0xFF91BAAE);
        store.RenameNotebook(nb.Id, "University");
        store.SetNotebookDefaults(nb.Id, new PageDefaults { Mode = PageMode.Infinite });

        var back = store.ListNotebooks().Single(n => n.Id == nb.Id);
        Assert.Equal(0xFF91BAAEu, back.Color);
        Assert.Equal("University", back.Name);
        Assert.Equal(PageMode.Infinite, back.Defaults!.Mode);
    }

    [Fact]
    public void DraggingNotebooksIntoANewOrderIsRemembered()
    {
        using var tmp = new TempDb();
        using var store = new NoteStore(tmp.Path);
        var a = store.CreateNotebook("A", 1);
        var b = store.CreateNotebook("B", 2);
        var c = store.CreateNotebook("C", 3);

        store.ReorderNotebooks(new[] { c.Id, a.Id, b.Id });
        Assert.Equal(new[] { "C", "A", "B" }, store.ListNotebooks().Select(n => n.Name));
    }

    [Fact]
    public void DeletedNotebooksDisappearButOthersStay()
    {
        using var tmp = new TempDb();
        using var store = new NoteStore(tmp.Path);
        var a = store.CreateNotebook("A", 1);
        var b = store.CreateNotebook("B", 2);
        store.DeleteNotebook(a.Id);
        Assert.Equal(new[] { b.Id }, store.ListNotebooks().Select(n => n.Id));
    }
}

public class ToolbarMigrationTests
{
    [Fact]
    public void TheFocusButtonIsInTheDefaultToolbar()
    {
        Assert.Contains("focus", ToolbarLayout.Default);
        Assert.Equal(ToolbarLayout.Default, ToolbarLayout.Normalize(ToolbarLayout.Default));
    }

    [Fact]
    public void OlderCustomToolbarsGetTheFocusButtonBeforeTheMenuExactlyOnce()
    {
        var old = new[] { "pens", "eraser", "|", "undo", "settings", "more" };
        var migrated = ToolbarLayout.Migrate(old, version: 0);
        Assert.Equal(new[] { "pens", "eraser", "|", "undo", "settings", "focus", "more" }, migrated);

        // Already migrated (version 2): a user who removed it keeps it removed.
        Assert.Equal(old, ToolbarLayout.Migrate(old, ToolbarLayout.CurrentVersion));
        // Nothing doubles when it is already there.
        Assert.Equal(1, ToolbarLayout.Migrate(migrated, 0).Count(i => i == "focus"));
    }

    [Fact]
    public void MigrationNeverLosesTheMenuOrAddsUnknownButtons()
    {
        var m = ToolbarLayout.Migrate(new[] { "pens", "bogus" }, 0);
        Assert.Contains("more", m);
        Assert.DoesNotContain("bogus", m);
        Assert.Contains("focus", m);
    }

    [Fact]
    public void TheFocusDockReplacesTheFocusButtonWithItsOwnExitButton()
    {
        var dock = ToolbarLayout.ForFocus(ToolbarLayout.Default);
        Assert.Equal("nav", dock[0]);
        Assert.DoesNotContain("focus", dock);
        Assert.Contains("exit-focus", dock);
        Assert.Equal("more", dock[^1]);
        Assert.True(dock.IndexOf("sidebar") < dock.IndexOf("exit-focus"));
    }
}
