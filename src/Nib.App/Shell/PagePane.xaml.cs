using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace Nib.App.Shell;

/// <summary>The page list of the open section.</summary>
public sealed partial class PagePane : UserControl
{
    private bool _suppress;

    public ObservableCollection<PageItem> Pages { get; } = new();

    public event Action<PageItem>? PageSelected;
    public event Action? AddPageRequested;
    public event Action<PageItem, string>? PageCommand;
    public event Action<IReadOnlyList<PageItem>>? PagesReordered;

    public PagePane()
    {
        InitializeComponent();
        PageList.ItemsSource = Pages;
    }

    public string Title { set => Heading.Text = string.IsNullOrWhiteSpace(value) ? "Pages" : value; }

    public void Select(long id)
    {
        _suppress = true;
        try
        {
            var item = Pages.FirstOrDefault(p => p.Id == id);
            PageList.SelectedItem = item;
            if (item is not null) PageList.ScrollIntoView(item);
        }
        finally { _suppress = false; }
    }

    private void PageList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_suppress && PageList.SelectedItem is PageItem p) PageSelected?.Invoke(p);
    }

    private void AddPage_Click(object sender, RoutedEventArgs e) => AddPageRequested?.Invoke();

    private void PageList_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args) =>
        PagesReordered?.Invoke(Pages.ToList());

    private void PageList_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not PageItem item) return;
        var menu = new MenuFlyout();
        Add(menu, "Rename", "", () => PageCommand?.Invoke(item, "rename"));
        Add(menu, "New subpage", "", () => PageCommand?.Invoke(item, "subpage"));
        Add(menu, "Make subpage", "", () => PageCommand?.Invoke(item, "demote"));
        Add(menu, "Promote", "", () => PageCommand?.Invoke(item, "promote"));
        Add(menu, item.Info.Favorite ? "Remove from favorites" : "Add to favorites", "", () => PageCommand?.Invoke(item, "favorite"));
        Add(menu, "Move to section…", "", () => PageCommand?.Invoke(item, "move"));
        menu.Items.Add(new MenuFlyoutSeparator());
        Add(menu, "Delete page", "", () => PageCommand?.Invoke(item, "delete"));
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
