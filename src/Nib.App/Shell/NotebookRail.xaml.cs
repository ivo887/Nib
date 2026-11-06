using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Nib.App.Ink;
using Nib.Core.Model;

namespace Nib.App.Shell;

/// <summary>The notebook rail: badges and names (expanded) or badges only (compact), plus Tags / Recent / Favorites.</summary>
public sealed partial class NotebookRail : UserControl
{
    private bool _suppress, _expanded = true;

    internal ObservableCollection<NotebookItem> Items { get; } = new();

    internal event Action<long>? NotebookSelected;
    public event Action? AddNotebookRequested, TagsRequested, RecentRequested, FavoritesRequested;
    /// <summary>A right-click menu command for a notebook: rename, color:N, defaults, export or delete.</summary>
    internal event Action<long, string>? NotebookCommand;
    internal event Action<IReadOnlyList<long>>? NotebooksReordered;

    public NotebookRail()
    {
        InitializeComponent();
        NotebookList.ItemsSource = Items;
    }

    /// <summary>True: names beside the badges. False: a thin strip of badges (icon-only buttons).</summary>
    public bool Expanded
    {
        get => _expanded;
        set
        {
            _expanded = value;
            var vis = value ? Visibility.Visible : Visibility.Collapsed;
            HeaderText.Visibility = vis;
            TagsText.Visibility = RecentText.Visibility = FavoritesText.Visibility = vis;
            AddButton.HorizontalAlignment = value ? HorizontalAlignment.Right : HorizontalAlignment.Center;
            for (int i = 0; i < Items.Count; i++) ApplyNameVisibility(NotebookList.ContainerFromIndex(i) as ListViewItem);
        }
    }

    public void Select(long id)
    {
        _suppress = true;
        try { NotebookList.SelectedItem = Items.FirstOrDefault(n => n.Id == id); }
        finally { _suppress = false; }
    }

    private void NotebookList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_suppress && NotebookList.SelectedItem is NotebookItem n) NotebookSelected?.Invoke(n.Id);
    }

    // Names are shown only while the rail is wide enough; new containers pick the current mode up as they are created.
    private void NotebookList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args) =>
        ApplyNameVisibility(args.ItemContainer as ListViewItem);

    private void ApplyNameVisibility(ListViewItem? container)
    {
        if (container?.ContentTemplateRoot is StackPanel row && row.Children.Count > 1 && row.Children[1] is TextBlock name)
            name.Visibility = _expanded ? Visibility.Visible : Visibility.Collapsed;
    }

    private void NotebookList_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args) =>
        NotebooksReordered?.Invoke(Items.Select(i => i.Id).ToList());

    private void NotebookList_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not NotebookItem item) return;
        long id = item.Id;
        var menu = new MenuFlyout();
        Add(menu, "Rename", "\uE8AC", () => NotebookCommand?.Invoke(id, "rename"));
        var colors = new MenuFlyoutSubItem { Text = "Notebook color", Icon = new FontIcon { Glyph = "\uE790" } };
        for (int i = 0; i < SectionColors.Palette.Length; i++)
        {
            int idx = i;
            var mi = new MenuFlyoutItem { Text = $"Color {i + 1}", Icon = new FontIcon { Glyph = "\uE91F", Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Colors2D.From(SectionColors.Palette[i])) } };
            mi.Click += (_, _) => NotebookCommand?.Invoke(id, "color:" + idx);
            colors.Items.Add(mi);
        }
        menu.Items.Add(colors);
        Add(menu, "Page defaults…", "\uE771", () => NotebookCommand?.Invoke(id, "defaults"));
        Add(menu, "Export notebook…", "\uEA90", () => NotebookCommand?.Invoke(id, "export"));
        menu.Items.Add(new MenuFlyoutSeparator());
        Add(menu, "Delete notebook", "\uE74D", () => NotebookCommand?.Invoke(id, "delete"));
        menu.ShowAt(NotebookList, e.GetPosition(NotebookList));
        e.Handled = true;
    }

    private static void Add(MenuFlyout menu, string text, string glyph, Action action)
    {
        var mi = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
        mi.Click += (_, _) => action();
        menu.Items.Add(mi);
    }

    private void AddNotebook_Click(object sender, RoutedEventArgs e) => AddNotebookRequested?.Invoke();
    private void Tags_Click(object sender, RoutedEventArgs e) => TagsRequested?.Invoke();
    private void Recent_Click(object sender, RoutedEventArgs e) => RecentRequested?.Invoke();
    private void Favorites_Click(object sender, RoutedEventArgs e) => FavoritesRequested?.Invoke();
}
