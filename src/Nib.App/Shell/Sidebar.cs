using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using Nib.Core.Model;

namespace Nib.App.Shell;

/// <summary>
/// The navigation model behind the notebook rail, the section tabs and the page list. The three controls sit in different
/// parts of the window; this class owns the shared collections and forwards their events, so the rest of the app talks
/// to one object (and so does the navigator dropdown, which shows all three levels in one place).
/// </summary>
internal sealed class Sidebar
{
    private readonly NotebookRail _rail;
    private readonly SectionTabs _tabs;
    private readonly PagePane _pages;

    public ObservableCollection<NotebookInfo> Notebooks { get; } = new();
    public ObservableCollection<SectionItem> Sections => _tabs.Sections;
    public ObservableCollection<PageItem> Pages => _pages.Pages;

    public long CurrentNotebookId { get; private set; }
    public long CurrentSectionId { get; private set; }
    public long CurrentPageId { get; private set; }

    public event Action<NotebookInfo>? NotebookSelected;
    public event Action<SectionItem>? SectionSelected;
    public event Action<PageItem>? PageSelected;
    public event Action? AddNotebookRequested, AddSectionRequested, AddPageRequested, TagsRequested, RecentRequested, FavoritesRequested;
    public event Action? PagesToggleRequested;
    public event Action<long, string>? NotebookCommand;
    public event Action<IReadOnlyList<long>>? NotebooksReordered;
    public event Action<SectionItem, string>? SectionCommand;
    public event Action<PageItem, string>? PageCommand;
    public event Action<IReadOnlyList<SectionItem>>? SectionsReordered;
    public event Action<IReadOnlyList<PageItem>>? PagesReordered;

    public Sidebar(NotebookRail rail, SectionTabs tabs, PagePane pages)
    {
        _rail = rail; _tabs = tabs; _pages = pages;

        Notebooks.CollectionChanged += OnNotebooksChanged;
        _rail.NotebookSelected += id => { if (Notebooks.FirstOrDefault(n => n.Id == id) is { } nb) NotebookSelected?.Invoke(nb); };
        _rail.AddNotebookRequested += () => AddNotebookRequested?.Invoke();
        _rail.NotebookCommand += (id, cmd) => NotebookCommand?.Invoke(id, cmd);
        _rail.NotebooksReordered += ids => NotebooksReordered?.Invoke(ids);
        _rail.TagsRequested += () => TagsRequested?.Invoke();
        _rail.RecentRequested += () => RecentRequested?.Invoke();
        _rail.FavoritesRequested += () => FavoritesRequested?.Invoke();

        _tabs.SectionSelected += s => SectionSelected?.Invoke(s);
        _tabs.AddSectionRequested += () => AddSectionRequested?.Invoke();
        _tabs.PagesToggleRequested += () => PagesToggleRequested?.Invoke();
        _tabs.SectionCommand += (s, c) => SectionCommand?.Invoke(s, c);
        _tabs.SectionsReordered += items => SectionsReordered?.Invoke(items);

        _pages.PageSelected += p => PageSelected?.Invoke(p);
        _pages.AddPageRequested += () => AddPageRequested?.Invoke();
        _pages.PageCommand += (p, c) => PageCommand?.Invoke(p, c);
        _pages.PagesReordered += items => PagesReordered?.Invoke(items);
    }

    private void OnNotebooksChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        _rail.Items.Clear();
        foreach (var nb in Notebooks) _rail.Items.Add(new NotebookItem(nb));
        _rail.Select(CurrentNotebookId);
    }

    public void SelectNotebook(long id) { CurrentNotebookId = id; _rail.Select(id); }
    public void SelectSection(long id) { CurrentSectionId = id; _tabs.Select(id); }
    public void SelectPage(long id) { CurrentPageId = id; _pages.Select(id); }

    /// <summary>Shown above the page list (the open section's name).</summary>
    public string PagesTitle { set => _pages.Title = value; }

    // ---- the same requests the controls make, for the navigator dropdown ----
    public void RequestNotebook(NotebookInfo nb) => NotebookSelected?.Invoke(nb);
    public void RequestSection(SectionItem s) => SectionSelected?.Invoke(s);
    public void RequestPage(PageItem p) => PageSelected?.Invoke(p);
    public void RequestAddNotebook() => AddNotebookRequested?.Invoke();
    public void RequestAddSection() => AddSectionRequested?.Invoke();
    public void RequestAddPage() => AddPageRequested?.Invoke();

    public NotebookRail Rail => _rail;
    public SectionTabs Tabs => _tabs;
    public PagePane PagePane => _pages;
}
