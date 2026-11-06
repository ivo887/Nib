using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Nib.Core.Model;

namespace Nib.App.Shell;

/// <summary>
/// The page/notebook manager dropdown: notebook, sections and pages of the open notebook in one panel, with new notebook /
/// section / page buttons. It sits in the title bar and, in full-screen focus mode, on the dock, where the rail, tabs and
/// page list are hidden.
/// </summary>
/// <remarks>
/// Only strings and plain controls cross into the UI framework here. Rows are matched back to their notebook, section or page
/// by index into parallel lists: handing managed objects to WinRT properties such as <c>Tag</c> or <c>ComboBox.Items</c> fails
/// under Native AOT.
/// </remarks>
internal static class NavigatorFlyout
{
    public static Flyout Create(Sidebar sb)
    {
        var root = new StackPanel { Spacing = 6, Width = 380 };
        bool suppress = false;
        var notebookRows = new List<NotebookInfo>();
        var sectionRows = new List<SectionItem>();
        var pageRows = new List<PageItem>();

        // ---- notebooks ----
        root.Children.Add(Header("Notebook", "New notebook", sb.RequestAddNotebook));
        var notebook = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(notebook, "Notebook");
        root.Children.Add(notebook);

        // ---- sections ----
        root.Children.Add(Header("Section", "New section", sb.RequestAddSection));
        var sections = new ListView { SelectionMode = ListViewSelectionMode.Single, MaxHeight = 150 };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(sections, "Sections");
        root.Children.Add(sections);

        // ---- pages ----
        root.Children.Add(Header("Pages", "New page", sb.RequestAddPage));
        var pages = new ListView { SelectionMode = ListViewSelectionMode.Single, MaxHeight = 280 };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(pages, "Pages");
        root.Children.Add(pages);

        var flyout = new Flyout { Content = root };

        void FillNotebooks()
        {
            suppress = true;
            notebook.Items.Clear();
            notebookRows.Clear();
            int selected = -1;
            foreach (var nb in sb.Notebooks)
            {
                if (nb.Id == sb.CurrentNotebookId) selected = notebookRows.Count;
                notebookRows.Add(nb);
                notebook.Items.Add(nb.Name);
            }
            notebook.SelectedIndex = selected;
            suppress = false;
        }

        void FillSections()
        {
            suppress = true;
            sections.Items.Clear();
            sectionRows.Clear();
            int selected = -1;
            foreach (var s in sb.Sections)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
                row.Children.Add(new Microsoft.UI.Xaml.Shapes.Rectangle { Width = 4, Height = 18, RadiusX = 2, RadiusY = 2, Fill = s.ColorBrush });
                row.Children.Add(new TextBlock { Text = s.Name, VerticalAlignment = VerticalAlignment.Center });
                var item = new ListViewItem { Content = row };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(item, s.Name);
                if (s.Id == sb.CurrentSectionId) selected = sectionRows.Count;
                sectionRows.Add(s);
                sections.Items.Add(item);
            }
            sections.SelectedIndex = selected;
            suppress = false;
        }

        void FillPages()
        {
            suppress = true;
            pages.Items.Clear();
            pageRows.Clear();
            int selected = -1;
            foreach (var p in sb.Pages)
            {
                var item = new ListViewItem
                {
                    Content = new TextBlock { Text = p.Title, Opacity = p.TitleOpacity, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(p.Info.Level * 16, 0, 0, 0) },
                };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(item, p.Title);
                if (p.Id == sb.CurrentPageId) selected = pageRows.Count;
                pageRows.Add(p);
                pages.Items.Add(item);
            }
            pages.SelectedIndex = selected;
            if (selected >= 0 && pages.Items[selected] is ListViewItem current) pages.ScrollIntoView(current);
            suppress = false;
        }

        notebook.SelectionChanged += (_, _) =>
        {
            int i = notebook.SelectedIndex;
            if (!suppress && i >= 0 && i < notebookRows.Count && notebookRows[i].Id != sb.CurrentNotebookId) sb.RequestNotebook(notebookRows[i]);
        };
        sections.SelectionChanged += (_, _) =>
        {
            int i = sections.SelectedIndex;
            if (!suppress && i >= 0 && i < sectionRows.Count && sectionRows[i].Id != sb.CurrentSectionId) sb.RequestSection(sectionRows[i]);
        };
        pages.SelectionChanged += (_, _) =>
        {
            int i = pages.SelectedIndex;
            if (suppress || i < 0 || i >= pageRows.Count) return;
            sb.RequestPage(pageRows[i]);
            flyout.Hide();
        };

        // The lists reload while the open notebook or section changes: rebuild once per burst of changes.
        bool pending = false;
        void Refresh(object? s, NotifyCollectionChangedEventArgs e)
        {
            if (pending) return;
            pending = true;
            root.DispatcherQueue.TryEnqueue(() =>
            {
                pending = false;
                FillNotebooks();
                FillSections();
                FillPages();
            });
        }
        sb.Sections.CollectionChanged += Refresh;
        sb.Pages.CollectionChanged += Refresh;
        flyout.Closed += (_, _) => { sb.Sections.CollectionChanged -= Refresh; sb.Pages.CollectionChanged -= Refresh; };

        FillNotebooks();
        FillSections();
        FillPages();
        return flyout;
    }

    private static Grid Header(string title, string addTip, Action add)
    {
        var g = new Grid { Margin = new Thickness(2, 6, 0, 0) };
        g.Children.Add(new TextBlock { Text = title, Opacity = 0.7, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        var button = new Button
        {
            Content = new FontIcon { Glyph = "", FontSize = 12 },
            HorizontalAlignment = HorizontalAlignment.Right, Width = 30, Height = 26, Padding = new Thickness(0),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0),
        };
        ToolTipService.SetToolTip(button, addTip);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, addTip);
        button.Click += (_, _) => add();
        g.Children.Add(button);
        return g;
    }
}
