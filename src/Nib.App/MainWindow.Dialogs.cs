using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Nib.App.Ink;
using Nib.App.Shell;
using Nib.Core.Geometry;
using Nib.Core.Input;
using Nib.Core.Layout;
using Nib.Core.Device;
using Nib.Core.Model;

namespace Nib.App;

/// <summary>The overflow menu and the few dialogs (page setup, settings). Everything rarely used lives here, not on the toolbar.</summary>
public sealed partial class MainWindow
{
    private void BuildOverflowMenu()
    {
        var m = _toolbar.Overflow;
        m.Items.Clear();

        // Page: how the page looks and how you move around it.
        var page = Sub(m, "Page", "");
        Item(page, "Page setup…", "", () => _ = PageSetupAsync());
        Item(page, "Insert space", "", () => SelectTool(ToolKind.InsertSpace));
        _snapMenuItem = new ToggleMenuFlyoutItem { Text = "Snap to shapes and lines", IsChecked = Settings.ShapeSnap };
        _snapMenuItem.Click += (_, _) => { SetShapeSnap(_snapMenuItem.IsChecked); BuildToolbarControls(); };
        page.Items.Add(_snapMenuItem);
        m.Opening += (_, _) => _snapMenuItem.IsChecked = Settings.ShapeSnap;
        var zoom = Sub(page, "Zoom", "");
        FillZoom(zoom);

        // Text: paragraph styles and tags.
        var text = Sub(m, "Text and tags", "");
        var para = Sub(text, "Paragraph style", null);
        Item(para, "Normal", null, () => _editor?.SetParagraphKind(ParagraphKind.Normal));
        Item(para, "Heading 1", null, () => _editor?.SetParagraphKind(ParagraphKind.Heading1));
        Item(para, "Heading 2", null, () => _editor?.SetParagraphKind(ParagraphKind.Heading2));
        Item(para, "Heading 3", null, () => _editor?.SetParagraphKind(ParagraphKind.Heading3));
        Item(para, "Bulleted list", null, () => _editor?.SetParagraphKind(ParagraphKind.Bullet));
        Item(para, "Numbered list", null, () => _editor?.SetParagraphKind(ParagraphKind.Numbered));
        var tags = Sub(text, "Tag", "");
        foreach (var t in TagDefinition.BuiltIn)
        {
            int shortcut = t.Shortcut;
            Item(tags, $"{t.Name}	Ctrl+{shortcut}", null, () => ApplyTagShortcut(shortcut));
        }
        Item(tags, "Tag summary", null, () => _ = ShowTagSummaryAsync());

        // Export.
        var export = Sub(m, "Export PDF", "");
        FillExport(export);

        // View: where things sit on screen.
        var view = Sub(m, "View", "");
        Item(view, "Focus mode (F11)", "", ToggleFocusMode);
        Item(view, "Notebooks sidebar", "", ToggleSidebar);
        var dock = Sub(view, "Toolbar position", "");
        foreach (var side in new[] { DockSide.Top, DockSide.Bottom, DockSide.Left, DockSide.Right })
        {
            var s = side;
            Item(dock, s.ToString(), null, () => { ApplyDock(s); _lastEffectiveDock = (DockSide)255; ApplyResponsiveLayout(); _settings.SaveSoon(); });
        }
        Item(view, "Customize toolbar…", "", () => _ = SettingsDialogAsync("Toolbar"));

        m.Items.Add(new MenuFlyoutSeparator());
        Item(m, "Settings…", "", () => _ = SettingsDialogAsync());
    }

    private ToggleMenuFlyoutItem _snapMenuItem = new();

    private static MenuFlyoutSubItem Sub(object parent, string text, string? glyph)
    {
        var sub = new MenuFlyoutSubItem { Text = text };
        if (glyph is not null) sub.Icon = new FontIcon { Glyph = glyph };
        if (parent is MenuFlyout f) f.Items.Add(sub);
        else if (parent is MenuFlyoutSubItem s) s.Items.Add(sub);
        return sub;
    }

    private void FillZoom(object parent)
    {
        Item(parent, "Page width", null, () => _engine?.ZoomPreset(ZoomPreset.PageWidth));
        Item(parent, "100%", null, () => _engine?.ZoomPreset(ZoomPreset.Actual));
        Item(parent, "Fit page", null, () => _engine?.ZoomPreset(ZoomPreset.FitPage));
        Item(parent, "Zoom in", null, () => _engine?.ZoomBy(1.25f));
        Item(parent, "Zoom out", null, () => _engine?.ZoomBy(0.8f));
    }

    private void FillExport(object parent)
    {
        Item(parent, "This page", null, () => _ = ExportPageAsync());
        Item(parent, "This section", null, () => { if (_section is not null) _ = SectionCommandAsync(new SectionItem(_section), "export"); });
        Item(parent, "This notebook", null, () => _ = ExportNotebookAsync());

        void Add(MenuFlyoutItemBase item)
        {
            if (parent is MenuFlyout f) f.Items.Add(item);
            else if (parent is MenuFlyoutSubItem sub) sub.Items.Add(item);
        }
        Add(new MenuFlyoutSeparator());

        // The format used by the three commands above (also in Settings → Saving).
        var format = new MenuFlyoutSubItem { Text = "Format" };
        foreach (var (fmt, label) in new[] { (ExportFormat.Pdf, "PDF document"), (ExportFormat.Png, "PNG image"), (ExportFormat.Svg, "SVG drawing") })
        {
            var f = fmt;
            var radio = new RadioMenuFlyoutItem { Text = label, GroupName = "NibExportFormat", IsChecked = Settings.ExportFormat == f };
            radio.Click += (_, _) => { Settings.ExportFormat = f; _settings.SaveSoon(); };
            format.Items.Add(radio);
        }
        Add(format);

        var bg = new ToggleMenuFlyoutItem { Text = "Include page background", IsChecked = Settings.ExportBackground };
        bg.Click += (_, _) => { Settings.ExportBackground = bg.IsChecked; _settings.SaveSoon(); };
        Add(bg);
        Add(new MenuFlyoutSeparator());

        var backup = new MenuFlyoutItem { Text = "Back up all notebooks…", Icon = new FontIcon { Glyph = "\uE74E" } };
        backup.Click += (_, _) => _ = BackUpAsync();
        Add(backup);
    }

    /// <summary>Handles the toolbar's command buttons (the ones that are not drawing tools).</summary>
    private void OnToolbarCommand(string id, FrameworkElement anchor)
    {
        switch (id)
        {
            case "zoom":
            {
                var f = new MenuFlyout();
                FillZoom(f);
                f.ShowAt(anchor);
                break;
            }
            case "export":
            {
                var f = new MenuFlyout();
                FillExport(f);
                f.ShowAt(anchor);
                break;
            }
            case "page-setup": _ = PageSetupAsync(); break;
            case "focus": ToggleFocusMode(); break;
            case "sidebar": ToggleSidebar(); break;
            case "settings": _ = SettingsDialogAsync(); break;
            case "nav": ShowNavigator(anchor); break;
            case "exit-focus": ToggleFocusMode(); break;
        }
    }

    private static void Item(object parent, string text, string? glyph, Action a)
    {
        var mi = new MenuFlyoutItem { Text = text };
        if (glyph is not null) mi.Icon = new FontIcon { Glyph = glyph };
        mi.Click += (_, _) => a();
        if (parent is MenuFlyout f) f.Items.Add(mi);
        else if (parent is MenuFlyoutSubItem s) s.Items.Add(mi);
    }

    // ---------------------------------------------------------------- export


    private async Task ExportPageAsync()
    {
        if (_export is null || _engine is null) return;
        await RunExportAsync(() => _export.ExportPage(_engine));
    }

    private async Task ExportNotebookAsync()
    {
        if (_export is null || _engine is null || _notebook is null) return;
        await RunExportAsync(() => _export.ExportNotebook(_engine, _notebook));
    }

    private async Task BackUpAsync()
    {
        if (_export is null || _engine is null) return;
        await RunExportAsync(() => _export.BackUpAll(_engine));
    }

    /// <summary>Runs an export, then tells the user what was written (or what went wrong).</summary>
    internal async Task RunExportAsync(Func<Task<string?>> export)
    {
        try
        {
            ShowToast("Exporting…");
            var path = await export();
            if (path is null) { Toast.Visibility = Visibility.Collapsed; return; }
            ShowToast("Saved " + System.IO.Path.GetFileName(path.TrimEnd('\\', '/')));
        }
        catch (Exception e)
        {
            Services.CrashLog.Write(e);
            ShowToast("Could not save: " + e.Message);
        }
    }

    private async Task PageSetupAsync()
    {
        if (_engine is null) return;
        var snapshot = await _engine.SnapshotCurrentPage();
        if (snapshot is null) return;
        var result = await PageSetupDialogAsync(snapshot.Layout, forDefaults: false);
        if (result is not { } r) return;
        _engine.SetLayout(r.Layout);
        var d = ToDefaults(r.Layout);
        if (r.Scope == 1 && _section is not null)
        {
            var sec = _section;
            await _storage!.Write(s => s.SetSectionDefaults(sec.Id, d));
            _section = sec with { Defaults = d };
        }
        else if (r.Scope == 2 && _notebook is not null)
        {
            var nb = _notebook;
            await _storage!.Write(s => s.SetNotebookDefaults(nb.Id, d));
            _notebook = nb with { Defaults = d };
        }
    }

    /// <summary>Paper, background and page mode. Scope 0 = this page, 1 = also section default, 2 = also notebook default.</summary>
    private async Task<(PageLayout Layout, int Scope)?> PageSetupDialogAsync(PageLayout current, bool forDefaults)
    {
        var bg = current.Background;
        var panel = new StackPanel { Spacing = 12, MinWidth = 340 };

        var kind = new ComboBox { Header = "Background", HorizontalAlignment = HorizontalAlignment.Stretch };
        string[] kinds = { "Blank", "Lined", "Squares", "Dot grid", "Graph paper", "Cornell notes" };
        foreach (var k in kinds) kind.Items.Add(k);
        kind.SelectedIndex = bg.Kind switch
        {
            BackgroundKind.Lined => 1, BackgroundKind.Grid => 2, BackgroundKind.Dots => 3, BackgroundKind.Graph => 4, BackgroundKind.Cornell => 5, _ => 0,
        };
        panel.Children.Add(kind);

        var rule = new ComboBox { Header = "Line spacing", HorizontalAlignment = HorizontalAlignment.Stretch };
        rule.Items.Add("Narrow ruled (¼ in)"); rule.Items.Add("College ruled (9⁄32 in)"); rule.Items.Add("Wide ruled (11⁄32 in)");
        rule.SelectedIndex = bg.Rule switch { LineRule.Narrow => 0, LineRule.Wide => 2, _ => 1 };
        panel.Children.Add(rule);

        var grid = new NumberBox { Header = "Grid size (mm)", Minimum = 1, Maximum = 50, SmallChange = 1, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
            Value = Math.Round(PageUnits.ToMillimeters(bg.Kind is BackgroundKind.Grid or BackgroundKind.Dots or BackgroundKind.Graph ? bg.Spacing : PageUnits.FromMillimeters(5)), 1) };
        panel.Children.Add(grid);

        var lineColor = new ComboBox { Header = "Line color", HorizontalAlignment = HorizontalAlignment.Stretch };
        (string Name, uint Color)[] lineColors = { ("Blue", 0xFFB9D3EA), ("Gray", 0xFFCFCFD4), ("Green", 0xFFC6E3C8), ("Dark", 0xFF8C8C96) };
        foreach (var lc in lineColors) lineColor.Items.Add(lc.Name);
        lineColor.SelectedIndex = Math.Max(0, Array.FindIndex(lineColors, c => c.Color == bg.LineColor));
        panel.Children.Add(lineColor);

        var paperColor = new ComboBox { Header = "Paper color", HorizontalAlignment = HorizontalAlignment.Stretch };
        (string Name, uint Color)[] papers = { ("White", 0xFFFFFFFF), ("Cream", 0xFFFBF6E9), ("Light gray", 0xFFF2F2F4), ("Mint", 0xFFEFF7F1) };
        foreach (var p in papers) paperColor.Items.Add(p.Name);
        paperColor.SelectedIndex = Math.Max(0, Array.FindIndex(papers, c => c.Color == bg.PaperColor));
        panel.Children.Add(paperColor);

        var margin = new ToggleSwitch { Header = "Margin line", IsOn = bg.MarginLine };
        panel.Children.Add(margin);

        var mode = new ComboBox { Header = "Page", HorizontalAlignment = HorizontalAlignment.Stretch };
        mode.Items.Add("A4"); mode.Items.Add("A5"); mode.Items.Add("Letter"); mode.Items.Add("Infinite canvas");
        mode.SelectedIndex = current.Mode == PageMode.Infinite ? 3 : current.Paper.Name switch { "A5" => 1, "Letter" => 2, _ => 0 };
        panel.Children.Add(mode);

        var landscape = new ToggleSwitch { Header = "Landscape", IsOn = current.Landscape };
        panel.Children.Add(landscape);

        RadioButtons? scope = null;
        if (!forDefaults)
        {
            scope = new RadioButtons { Header = "Apply to" };
            scope.Items.Add("This page");
            scope.Items.Add("This page and new pages in this section");
            scope.Items.Add("This page and new pages in this notebook");
            scope.SelectedIndex = 0;
            panel.Children.Add(scope);
        }

        void Sync()
        {
            int k = kind.SelectedIndex;
            rule.Visibility = k is 1 or 5 ? Visibility.Visible : Visibility.Collapsed;
            grid.Visibility = k is 2 or 3 or 4 ? Visibility.Visible : Visibility.Collapsed;
            lineColor.Visibility = k == 0 ? Visibility.Collapsed : Visibility.Visible;
            margin.Visibility = k == 1 ? Visibility.Visible : Visibility.Collapsed;
            landscape.Visibility = mode.SelectedIndex == 3 ? Visibility.Collapsed : Visibility.Visible;
        }
        kind.SelectionChanged += (_, _) => Sync();
        mode.SelectionChanged += (_, _) => Sync();
        Sync();

        var dialog = new ContentDialog
        {
            Title = forDefaults ? "Default page for new pages" : "Page setup",
            Content = new ScrollViewer { Content = panel, MaxHeight = 560 },
            PrimaryButtonText = "Apply",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Root.XamlRoot, RequestedTheme = Root.ActualTheme,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return null;

        var rules = new[] { LineRule.Narrow, LineRule.College, LineRule.Wide };
        var r = rules[Math.Clamp(rule.SelectedIndex, 0, 2)];
        float gridUnits = PageUnits.FromMillimeters((float)(double.IsNaN(grid.Value) ? 5 : grid.Value));
        var newBg = kind.SelectedIndex switch
        {
            1 => new BackgroundSpec { Kind = BackgroundKind.Lined, Rule = r, Spacing = BackgroundSpec.Rules.For(r, 27), MarginLine = margin.IsOn },
            2 => new BackgroundSpec { Kind = BackgroundKind.Grid, Spacing = gridUnits },
            3 => new BackgroundSpec { Kind = BackgroundKind.Dots, Spacing = gridUnits },
            4 => new BackgroundSpec { Kind = BackgroundKind.Graph, Spacing = gridUnits, MajorEvery = gridUnits < 8 ? 10 : 5 },
            5 => new BackgroundSpec { Kind = BackgroundKind.Cornell, Rule = r, Spacing = BackgroundSpec.Rules.For(r, 27) },
            _ => new BackgroundSpec(),
        };
        newBg = newBg with { LineColor = lineColors[Math.Max(0, lineColor.SelectedIndex)].Color, PaperColor = papers[Math.Max(0, paperColor.SelectedIndex)].Color };
        if (kind.SelectedIndex == 4) newBg = newBg with { MajorLineColor = lineColors[Math.Max(0, lineColor.SelectedIndex)].Color };

        var layout = current with
        {
            Background = newBg,
            Mode = mode.SelectedIndex == 3 ? PageMode.Infinite : PageMode.Fixed,
            Paper = mode.SelectedIndex switch { 1 => PaperSize.A5, 2 => PaperSize.Letter, _ => PaperSize.A4 },
            Landscape = landscape.IsOn,
        };
        return (layout, scope?.SelectedIndex ?? 0);
    }

    private static TextBlock Header(string text) => new()
    {
        Text = text,
        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        Margin = new Thickness(0, 8, 0, 0),
    };
}
