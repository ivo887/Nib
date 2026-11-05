using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Nib.Core.Model;

namespace Nib.App.Shell;

/// <summary>Notebook picker, sections and pages. Collapses to a thin strip (or overlays on narrow windows).</summary>
public sealed partial class Sidebar : UserControl
{
    private bool _suppress;

    internal ObservableCollection<NotebookInfo> Notebooks { get; } = new();
    public ObservableCollection<SectionItem> Sections { get; } = new();
    public ObservableCollection<PageItem> Pages { get; } = new();

    public event Action<NotebookInfo>? NotebookSelected;
    public event Action<SectionItem>? SectionSelected;
    public event Action<PageItem>? PageSelected;
    public event Action? AddNotebookRequested, AddSectionRequested, AddPageRequested, TagsRequested, RecentRequested, FavoritesRequested;
    public event Action<SectionItem, string>? SectionCommand;
    public event Action<PageItem, string>? PageCommand;
    public event Action<IReadOnlyList<SectionItem>>? SectionsReordered;
    public event Action<IReadOnlyList<PageItem>>? PagesReordered;

    public Sidebar()
    {
        InitializeComponent();
        NotebookBox.ItemsSource = Notebooks;
        SectionList.ItemsSource = Sections;
        PageList.ItemsSource = Pages;
    }

    public void SelectNotebook(long id) => Quietly(() => NotebookBox.SelectedItem = Notebooks.FirstOrDefault(n => n.Id == id));
    public void SelectSection(long id) => Quietly(() => SectionList.SelectedItem = Sections.FirstOrDefault(s => s.Id == id));

    public void SelectPage(long id) => Quietly(() =>
    {
        var item = Pages.FirstOrDefault(p => p.Id == id);
        PageList.SelectedItem = item;
        if (item is not null) PageList.ScrollIntoView(item);
    });

    private void Quietly(Action a)
    {
        _suppress = true;
        try { a(); }
        finally { _suppress = false; }
    }

    private void NotebookBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_suppress && NotebookBox.SelectedItem is NotebookInfo nb) NotebookSelected?.Invoke(nb);
    }

    private void SectionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_suppress && SectionList.SelectedItem is SectionItem s) SectionSelected?.Invoke(s);
    }

    private void PageList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_suppress && PageList.SelectedItem is PageItem p) PageSelected?.Invoke(p);
    }

    private void AddNotebook_Click(object sender, RoutedEventArgs e) => AddNotebookRequested?.Invoke();
    private void AddSection_Click(object sender, RoutedEventArgs e) => AddSectionRequested?.Invoke();
    private void AddPage_Click(object sender, RoutedEventArgs e) => AddPageRequested?.Invoke();
    private void Tags_Click(object sender, RoutedEventArgs e) => TagsRequested?.Invoke();
    private void Recent_Click(object sender, RoutedEventArgs e) => RecentRequested?.Invoke();
    private void Favorites_Click(object sender, RoutedEventArgs e) => FavoritesRequested?.Invoke();

    private void SectionList_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args) =>
        SectionsReordered?.Invoke(Sections.ToList());

    private void PageList_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args) =>
        PagesReordered?.Invoke(Pages.ToList());

    private void SectionList_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not SectionItem item) return;
        var menu = new MenuFlyout();
        Add(menu, "Rename", "\uE8AC", () => SectionCommand?.Invoke(item, "rename"));
        var colors = new MenuFlyoutSubItem { Text = "Section color", Icon = new FontIcon { Glyph = "\uE790" } };
        for (int i = 0; i < SectionColors.Palette.Length; i++)
        {
            int idx = i;
            var mi = new MenuFlyoutItem { Text = $"Color {i + 1}", Icon = new FontIcon { Glyph = "\uE91F", Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Ink.Colors2D.From(SectionColors.Palette[i])) } };
            mi.Click += (_, _) => SectionCommand?.Invoke(item, "color:" + idx);
            colors.Items.Add(mi);
        }
        menu.Items.Add(colors);
        Add(menu, "Move to notebook…", "\uE8DE", () => SectionCommand?.Invoke(item, "move"));
        Add(menu, "Page defaults…", "\uE771", () => SectionCommand?.Invoke(item, "defaults"));
        Add(menu, "Export section as PDF…", "\uEA90", () => SectionCommand?.Invoke(item, "export"));
        menu.Items.Add(new MenuFlyoutSeparator());
        Add(menu, "Delete section", "\uE74D", () => SectionCommand?.Invoke(item, "delete"));
        menu.ShowAt(SectionList, e.GetPosition(SectionList));
        e.Handled = true;
    }

    private void PageList_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not PageItem item) return;
        var menu = new MenuFlyout();
        Add(menu, "Rename", "\uE8AC", () => PageCommand?.Invoke(item, "rename"));
        Add(menu, "New subpage", "\uE710", () => PageCommand?.Invoke(item, "subpage"));
        Add(menu, "Make subpage", "\uE72A", () => PageCommand?.Invoke(item, "demote"));
        Add(menu, "Promote", "\uE72B", () => PageCommand?.Invoke(item, "promote"));
        Add(menu, item.Info.Favorite ? "Remove from favorites" : "Add to favorites", "\uE734", () => PageCommand?.Invoke(item, "favorite"));
        Add(menu, "Move to section…", "\uE8DE", () => PageCommand?.Invoke(item, "move"));
        menu.Items.Add(new MenuFlyoutSeparator());
        Add(menu, "Delete page", "\uE74D", () => PageCommand?.Invoke(item, "delete"));
        menu.ShowAt(PageList, e.GetPosition(PageList));
        e.Handled = true;
    }

    private static void Add(MenuFlyout menu, string text, string glyph, Action action)
    {
        var mi = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
        mi.Click += (_, _) => action();
        menu.Items.Add(mi);
    }
}
