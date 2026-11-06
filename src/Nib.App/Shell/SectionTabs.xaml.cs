using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Nib.App.Ink;
using Nib.Core.Model;

namespace Nib.App.Shell;

/// <summary>The section tab strip. On narrow windows it shows a single dropdown instead of the tabs.</summary>
public sealed partial class SectionTabs : UserControl
{
    private bool _suppress, _compact;

    public ObservableCollection<SectionItem> Sections { get; } = new();

    public event Action<SectionItem>? SectionSelected;
    public event Action? AddSectionRequested, PagesToggleRequested;
    public event Action<SectionItem, string>? SectionCommand;
    public event Action<IReadOnlyList<SectionItem>>? SectionsReordered;

    public SectionTabs()
    {
        InitializeComponent();
        TabList.ItemsSource = Sections;
    }

    /// <summary>Collapse the tabs into one dropdown (narrow windows).</summary>
    public bool Compact
    {
        get => _compact;
        set
        {
            _compact = value;
            TabList.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
            CompactButton.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            RefreshCompact();
        }
    }

    /// <summary>Reflects whether the page list is currently shown (inline) or open (drawer).</summary>
    public bool PagesShown { get => PagesToggle.IsChecked == true; set => PagesToggle.IsChecked = value; }

    public void Select(long id)
    {
        _suppress = true;
        try { TabList.SelectedItem = Sections.FirstOrDefault(s => s.Id == id); }
        finally { _suppress = false; }
        RefreshCompact();
        if (TabList.SelectedItem is { } item) TabList.ScrollIntoView(item);
    }

    private void RefreshCompact()
    {
        var current = TabList.SelectedItem as SectionItem;
        CompactText.Text = current?.Name ?? "Sections";
        CompactColor.Fill = current?.ColorBrush ?? new Microsoft.UI.Xaml.Media.SolidColorBrush(Colors2D.From(0xFF8A8A8A));
    }

    private void TabList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_suppress && TabList.SelectedItem is SectionItem s) SectionSelected?.Invoke(s);
        RefreshCompact();
    }

    private void AddSection_Click(object sender, RoutedEventArgs e) => AddSectionRequested?.Invoke();
    private void PagesToggle_Click(object sender, RoutedEventArgs e) => PagesToggleRequested?.Invoke();

    private void TabList_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args) =>
        SectionsReordered?.Invoke(Sections.ToList());

    private void CompactButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = new MenuFlyout();
        foreach (var section in Sections)
        {
            var s = section;
            var mi = new MenuFlyoutItem { Text = s.Name, Icon = new FontIcon { Glyph = "", Foreground = s.ColorBrush } };
            mi.Click += (_, _) => { Select(s.Id); SectionSelected?.Invoke(s); };
            menu.Items.Add(mi);
        }
        menu.Items.Add(new MenuFlyoutSeparator());
        var add = new MenuFlyoutItem { Text = "New section", Icon = new FontIcon { Glyph = "" } };
        add.Click += (_, _) => AddSectionRequested?.Invoke();
        menu.Items.Add(add);
        menu.ShowAt(CompactButton);
    }

    private void TabList_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not SectionItem item) return;
        var menu = new MenuFlyout();
        Add(menu, "Rename", "", () => SectionCommand?.Invoke(item, "rename"));
        var colors = new MenuFlyoutSubItem { Text = "Section color", Icon = new FontIcon { Glyph = "" } };
        for (int i = 0; i < SectionColors.Palette.Length; i++)
        {
            int idx = i;
            var mi = new MenuFlyoutItem { Text = $"Color {i + 1}", Icon = new FontIcon { Glyph = "", Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Colors2D.From(SectionColors.Palette[i])) } };
            mi.Click += (_, _) => SectionCommand?.Invoke(item, "color:" + idx);
            colors.Items.Add(mi);
        }
        menu.Items.Add(colors);
        Add(menu, "Move to notebook…", "", () => SectionCommand?.Invoke(item, "move"));
        Add(menu, "Page defaults…", "", () => SectionCommand?.Invoke(item, "defaults"));
        Add(menu, "Export section…", "", () => SectionCommand?.Invoke(item, "export"));
        menu.Items.Add(new MenuFlyoutSeparator());
        Add(menu, "Delete section", "", () => SectionCommand?.Invoke(item, "delete"));
        menu.ShowAt(TabList, e.GetPosition(TabList));
        e.Handled = true;
    }

    private static void Add(MenuFlyout menu, string text, string glyph, Action action)
    {
        var mi = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
        mi.Click += (_, _) => action();
        menu.Items.Add(mi);
    }
}
