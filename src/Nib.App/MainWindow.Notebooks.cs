using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Nib.App.Shell;
using Nib.Core.Model;

namespace Nib.App;

/// <summary>Notebook, section and page navigation and structure edits. Storage work never runs on the UI thread.</summary>
public sealed partial class MainWindow
{
    private NotebookInfo? _notebook;
    private SectionInfo? _section;
    private long _pageId;

    private void WireSidebar()
    {
        var sb = SidebarControl;
        sb.NotebookSelected += nb => _ = SelectNotebookAsync(nb, null);
        sb.SectionSelected += s => _ = SelectSectionAsync(s.Info, null);
        sb.PageSelected += p => OpenPage(p.Id);
        sb.AddNotebookRequested += () => _ = NewNotebookAsync();
        sb.AddSectionRequested += () => _ = NewSectionAsync();
        sb.AddPageRequested += () => _ = NewPageAsync(0);
        sb.TagsRequested += () => _ = ShowTagSummaryAsync();
        sb.RecentRequested += () => _ = ShowPageListAsync(recent: true);
        sb.FavoritesRequested += () => _ = ShowPageListAsync(recent: false);
        sb.SectionCommand += (item, cmd) => _ = SectionCommandAsync(item, cmd);
        sb.PageCommand += (item, cmd) => _ = PageCommandAsync(item, cmd);
        sb.SectionsReordered += items => _storage?.Write(s => s.ReorderSections(items.Select(i => i.Id).ToList()));
        sb.PagesReordered += items => _storage?.Write(s => s.ReorderPages(items.Select(i => i.Id).ToList()));
    }

    private PageItem? CurrentPageItem() => SidebarControl.Pages.FirstOrDefault(p => p.Id == _pageId);

    // ---------------------------------------------------------------- startup

    private async Task LoadInitialPageAsync()
    {
        var storage = _storage!;
        long? last = Settings.LastPageId;
        var start = await storage.Write(s =>
        {
            long pageId = last is { } id && s.GetPageInfo(id) is not null ? id : s.EnsureStarterContent();
            var info = s.GetPageInfo(pageId)!;
            var section = s.GetSection(info.SectionId)!;
            return (pageId, section, notebooks: s.ListNotebooks());
        });
        // Open the page first (fastest path to a writable page), fill the sidebar afterwards.
        _pageId = start.pageId;
        _engine?.ShowPage(start.pageId);
        SidebarControl.Notebooks.Clear();
        foreach (var nb in start.notebooks) SidebarControl.Notebooks.Add(nb);
        var notebook = start.notebooks.First(n => n.Id == start.section.NotebookId);
        await SelectNotebookAsync(notebook, start.section.Id, start.pageId);
    }

    // ---------------------------------------------------------------- navigation

    private async Task SelectNotebookAsync(NotebookInfo nb, long? sectionId, long? pageId = null)
    {
        _notebook = nb;
        SidebarControl.SelectNotebook(nb.Id);
        var sections = await _storage!.Read(s => s.ListSections(nb.Id));
        SidebarControl.Sections.Clear();
        foreach (var s in sections) SidebarControl.Sections.Add(new SectionItem(s));
        var target = sections.FirstOrDefault(s => s.Id == sectionId) ?? sections.FirstOrDefault();
        if (target is null)
        {
            SidebarControl.Pages.Clear();
            return;
        }
        await SelectSectionAsync(target, pageId);
    }

    private async Task SelectSectionAsync(SectionInfo section, long? pageId)
    {
        _section = section;
        SidebarControl.SelectSection(section.Id);
        var pages = await _storage!.Read(s => s.ListPages(section.Id));
        SidebarControl.Pages.Clear();
        foreach (var p in pages) SidebarControl.Pages.Add(new PageItem(p));
        long? open = pageId ?? (pages.Any(p => p.Id == _pageId) ? _pageId : pages.FirstOrDefault()?.Id);
        if (open is { } id) OpenPage(id);
        else await NewPageAsync(0);
    }

    private void OpenPage(long pageId)
    {
        _editor?.Commit();
        _pageId = pageId;
        _engine?.ShowPage(pageId);
        SidebarControl.SelectPage(pageId);
        Settings.LastPageId = pageId;
        _settings.SaveSoon();
        _storage?.Write(s => s.MarkOpened(pageId));
        // Preload neighbours so the next page switch is instant.
        var pages = SidebarControl.Pages;
        int idx = pages.ToList().FindIndex(p => p.Id == pageId);
        if (idx >= 0)
        {
            var near = new List<long>();
            foreach (int d in new[] { 1, -1, 2, -2 })
                if (idx + d >= 0 && idx + d < pages.Count) near.Add(pages[idx + d].Id);
            _engine?.Preload(near);
        }
    }

    private void OpenAdjacentPage(int delta)
    {
        var pages = SidebarControl.Pages;
        int idx = pages.ToList().FindIndex(p => p.Id == _pageId);
        int next = idx + delta;
        if (next >= 0 && next < pages.Count) OpenPage(pages[next].Id);
    }

    /// <summary>Opens a page anywhere (tag summary, recent, favorites).</summary>
    private async Task NavigateToPageAsync(long pageId)
    {
        var where = await _storage!.Read(s =>
        {
            var info = s.GetPageInfo(pageId);
            return info is null ? null : s.GetSection(info.SectionId);
        });
        if (where is null) return;
        var nb = SidebarControl.Notebooks.FirstOrDefault(n => n.Id == where.NotebookId);
        if (nb is null) return;
        await SelectNotebookAsync(nb, where.Id, pageId);
    }

    // ---------------------------------------------------------------- creation

    private async Task NewPageAsync(int level)
    {
        if (_section is null || _storage is null) return;
        var nb = _notebook;
        var sec = _section;
        long? after = _pageId == 0 ? null : _pageId;
        int baseLevel = CurrentPageItem()?.Info.Level ?? 0;
        var page = await _storage.Write(s =>
            s.CreatePage(sec.Id, "", PageDefaults.ResolveNewPage(nb?.Defaults, sec.Defaults), Math.Clamp(baseLevel + level, 0, PageInfo.MaxLevel), after));
        await SelectSectionAsync(sec, page.Id);
        TitleBox.Focus(FocusState.Programmatic);
    }

    private async Task NewSectionAsync()
    {
        if (_notebook is null || _storage is null) return;
        var name = await PromptAsync("New section", "Section name", "New Section");
        if (name is null) return;
        var nb = _notebook;
        int count = SidebarControl.Sections.Count;
        var sec = await _storage.Write(s => s.CreateSection(nb.Id, null, name, SectionColors.ForIndex(count)));
        await SelectNotebookAsync(nb, sec.Id);
    }

    private async Task NewNotebookAsync()
    {
        if (_storage is null) return;
        var name = await PromptAsync("New notebook", "Notebook name", "New Notebook");
        if (name is null) return;
        var nb = await _storage.Write(s =>
        {
            var created = s.CreateNotebook(name, 0xFF7A5AF8);
            s.CreateSection(created.Id, null, "New Section", SectionColors.ForIndex(0));
            return created;
        });
        SidebarControl.Notebooks.Add(nb);
        await SelectNotebookAsync(nb, null);
    }

    // ---------------------------------------------------------------- commands

    private async Task PageCommandAsync(PageItem item, string cmd)
    {
        var storage = _storage!;
        long id = item.Id;
        switch (cmd)
        {
            case "rename":
            {
                var name = await PromptAsync("Rename page", "Title", item.Info.Title);
                if (name is null) return;
                if (id == _pageId) { _suppressTitle = true; TitleBox.Text = name; _suppressTitle = false; _engine?.SetTitle(name); }
                else await storage.Write(s => s.RenamePage(id, name));
                item.Info = item.Info with { Title = name };
                return;
            }
            case "subpage":
                _pageId = id;
                await NewPageAsync(1);
                return;
            case "demote":
                await storage.Write(s => s.SetPageLevel(id, item.Info.Level + 1));
                break;
            case "promote":
                await storage.Write(s => s.SetPageLevel(id, item.Info.Level - 1));
                break;
            case "favorite":
                await storage.Write(s => s.SetFavorite(id, !item.Info.Favorite));
                break;
            case "move":
            {
                var target = await PickSectionAsync("Move page to…");
                if (target is null) return;
                await storage.Write(s => s.MovePageToSection(id, target.Id));
                _engine?.ForgetPage(id);
                break;
            }
            case "delete":
            {
                if (!await ConfirmAsync("Delete page?", $"\"{item.Title}\" moves to the recycle bin (restorable for 60 days in v2).")) return;
                await storage.Write(s => s.DeletePage(id));
                _engine?.ForgetPage(id);
                if (id == _pageId) _pageId = 0;
                break;
            }
        }
        if (_section is not null) await SelectSectionAsync(_section, _pageId == 0 ? null : _pageId);
    }

    private async Task SectionCommandAsync(SectionItem item, string cmd)
    {
        var storage = _storage!;
        long id = item.Id;
        if (cmd.StartsWith("color:", StringComparison.Ordinal))
        {
            uint color = SectionColors.ForIndex(int.Parse(cmd.AsSpan(6)));
            await storage.Write(s => s.SetSectionColor(id, color));
            item.Info = item.Info with { Color = color };
            return;
        }
        switch (cmd)
        {
            case "rename":
            {
                var name = await PromptAsync("Rename section", "Name", item.Name);
                if (name is null) return;
                await storage.Write(s => s.RenameSection(id, name));
                item.Info = item.Info with { Name = name };
                return;
            }
            case "move":
            {
                var nb = await PickNotebookAsync("Move section to…");
                if (nb is null) return;
                await storage.Write(s => s.MoveSection(id, nb.Id, null, s.ListSections(nb.Id).Count + 1));
                break;
            }
            case "defaults":
            {
                var result = await PageSetupDialogAsync(PageDefaults.ResolveNewPage(_notebook?.Defaults, item.Info.Defaults), forDefaults: true);
                if (result is null) return;
                var d = ToDefaults(result.Value.Layout);
                await storage.Write(s => s.SetSectionDefaults(id, d));
                item.Info = item.Info with { Defaults = d };
                if (_section?.Id == id) _section = item.Info;
                ShowToast("New pages in this section will use these settings");
                return;
            }
            case "export":
            {
                if (_export is null || _engine is null) return;
                var path = await _export.ExportSection(_engine, item.Info, _exportBackground);
                if (path is not null) ShowToast("Exported " + System.IO.Path.GetFileName(path));
                return;
            }
            case "delete":
            {
                if (!await ConfirmAsync("Delete section?", $"\"{item.Name}\" and its pages move to the recycle bin.")) return;
                await storage.Write(s => s.DeleteSection(id));
                break;
            }
        }
        if (_notebook is not null) await SelectNotebookAsync(_notebook, cmd == "delete" ? null : _section?.Id);
    }

    internal static PageDefaults ToDefaults(PageLayout l) => new()
    {
        Mode = l.Mode,
        Paper = l.Paper.Name,
        CustomWidthMm = l.Paper.Name == "Custom" ? l.Paper.WidthMm : null,
        CustomHeightMm = l.Paper.Name == "Custom" ? l.Paper.HeightMm : null,
        Landscape = l.Landscape,
        Background = l.Background,
    };

    // ---------------------------------------------------------------- lists

    private async Task ShowTagSummaryAsync()
    {
        var items = await _storage!.Read(s => s.TagSummary());
        var list = new ListView { SelectionMode = ListViewSelectionMode.None, IsItemClickEnabled = true, MaxHeight = 520, Width = 420 };
        foreach (var group in items.GroupBy(i => i.TagId))
        {
            string tagName = _tags.TryGetValue(group.Key, out var def) ? def.Name : "Tag";
            list.Items.Add(new TextBlock { Text = tagName, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 2) });
            foreach (var item in group)
            {
                var row = new StackPanel { Tag = item.PageId, Padding = new Thickness(4) };
                var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                if (def?.IsCheckbox == true) line.Children.Add(new FontIcon { Glyph = item.Checked ? "\uE73A" : "\uE739", FontSize = 14 });
                line.Children.Add(new TextBlock { Text = string.IsNullOrWhiteSpace(item.Snippet) ? "(empty)" : item.Snippet, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 360 });
                row.Children.Add(line);
                row.Children.Add(new TextBlock { Text = $"{item.NotebookName} › {item.SectionName} › {(string.IsNullOrWhiteSpace(item.PageTitle) ? "Untitled page" : item.PageTitle)}", Opacity = 0.6, FontSize = 12 });
                list.Items.Add(row);
            }
        }
        if (items.Count == 0) list.Items.Add(new TextBlock { Text = "No tags yet. Press Ctrl+1 for a To-Do, Ctrl+2 Important, Ctrl+3 Question.", TextWrapping = TextWrapping.Wrap });
        var flyout = new Flyout { Content = list };
        list.ItemClick += (_, e) =>
        {
            if (e.ClickedItem is StackPanel { Tag: long pageId })
            {
                flyout.Hide();
                _ = NavigateToPageAsync(pageId);
            }
        };
        flyout.ShowAt(SidebarControl);
    }

    private async Task ShowPageListAsync(bool recent)
    {
        var pages = await _storage!.Read(s => recent ? s.RecentPages(15) : s.FavoritePages());
        var menu = new MenuFlyout();
        foreach (var p in pages)
        {
            var mi = new MenuFlyoutItem { Text = p.DisplayTitle };
            long id = p.Id;
            mi.Click += (_, _) => _ = NavigateToPageAsync(id);
            menu.Items.Add(mi);
        }
        if (pages.Count == 0) menu.Items.Add(new MenuFlyoutItem { Text = recent ? "No recent pages" : "No favorites yet", IsEnabled = false });
        menu.ShowAt(SidebarControl);
    }

    private async Task<SectionInfo?> PickSectionAsync(string title)
    {
        var all = await _storage!.Read(s => s.ListNotebooks().SelectMany(nb => s.ListSections(nb.Id).Select(sec => (nb, sec))).ToList());
        var combo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var (nb, sec) in all) combo.Items.Add($"{nb.Name} › {sec.Name}");
        combo.SelectedIndex = 0;
        var dialog = new ContentDialog { Title = title, Content = combo, PrimaryButtonText = "Move", CloseButtonText = "Cancel", XamlRoot = Root.XamlRoot, DefaultButton = ContentDialogButton.Primary };
        return await dialog.ShowAsync() == ContentDialogResult.Primary && combo.SelectedIndex >= 0 ? all[combo.SelectedIndex].sec : null;
    }

    private async Task<NotebookInfo?> PickNotebookAsync(string title)
    {
        var combo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var nb in SidebarControl.Notebooks) combo.Items.Add(nb.Name);
        combo.SelectedIndex = 0;
        var dialog = new ContentDialog { Title = title, Content = combo, PrimaryButtonText = "Move", CloseButtonText = "Cancel", XamlRoot = Root.XamlRoot, DefaultButton = ContentDialogButton.Primary };
        return await dialog.ShowAsync() == ContentDialogResult.Primary && combo.SelectedIndex >= 0 ? SidebarControl.Notebooks[combo.SelectedIndex] : null;
    }

    private async Task<string?> PromptAsync(string title, string label, string initial)
    {
        var box = new TextBox { Header = label, Text = initial, SelectionStart = 0, SelectionLength = initial.Length };
        var dialog = new ContentDialog { Title = title, Content = box, PrimaryButtonText = "OK", CloseButtonText = "Cancel", XamlRoot = Root.XamlRoot, DefaultButton = ContentDialogButton.Primary };
        dialog.Opened += (_, _) => box.Focus(FocusState.Programmatic);
        var r = await dialog.ShowAsync();
        string text = box.Text.Trim();
        return r == ContentDialogResult.Primary && text.Length > 0 ? text : null;
    }

    private async Task<bool> ConfirmAsync(string title, string message)
    {
        var dialog = new ContentDialog { Title = title, Content = message, PrimaryButtonText = "Delete", CloseButtonText = "Cancel", XamlRoot = Root.XamlRoot, DefaultButton = ContentDialogButton.Close };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }
}
