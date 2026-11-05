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
        Item(m, "Page setup…", "\uE7C3", () => _ = PageSetupAsync());
        Item(m, "Insert space", "\uE75B", () => SelectTool(ToolKind.InsertSpace));

        var zoom = new MenuFlyoutSubItem { Text = "Zoom", Icon = new FontIcon { Glyph = "\uE71E" } };
        Item(zoom, "Page width", null, () => _engine?.ZoomPreset(ZoomPreset.PageWidth));
        Item(zoom, "100%", null, () => _engine?.ZoomPreset(ZoomPreset.Actual));
        Item(zoom, "Fit page", null, () => _engine?.ZoomPreset(ZoomPreset.FitPage));
        m.Items.Add(zoom);

        var text = new MenuFlyoutSubItem { Text = "Paragraph style", Icon = new FontIcon { Glyph = "\uE8D2" } };
        Item(text, "Normal", null, () => _editor?.SetParagraphKind(ParagraphKind.Normal));
        Item(text, "Heading 1", null, () => _editor?.SetParagraphKind(ParagraphKind.Heading1));
        Item(text, "Heading 2", null, () => _editor?.SetParagraphKind(ParagraphKind.Heading2));
        Item(text, "Heading 3", null, () => _editor?.SetParagraphKind(ParagraphKind.Heading3));
        Item(text, "Bulleted list", null, () => _editor?.SetParagraphKind(ParagraphKind.Bullet));
        Item(text, "Numbered list", null, () => _editor?.SetParagraphKind(ParagraphKind.Numbered));
        m.Items.Add(text);

        var tags = new MenuFlyoutSubItem { Text = "Tag", Icon = new FontIcon { Glyph = "\uE8EC" } };
        foreach (var t in TagDefinition.BuiltIn)
        {
            int shortcut = t.Shortcut;
            Item(tags, $"{t.Name}\tCtrl+{shortcut}", null, () => ApplyTagShortcut(shortcut));
        }
        Item(tags, "Tag summary", null, () => _ = ShowTagSummaryAsync());
        m.Items.Add(tags);

        m.Items.Add(new MenuFlyoutSeparator());
        var export = new MenuFlyoutSubItem { Text = "Export PDF", Icon = new FontIcon { Glyph = "\uEA90" } };
        Item(export, "This page", null, () => _ = ExportPageAsync());
        Item(export, "This section", null, () => { if (_section is not null) _ = SectionCommandAsync(new SectionItem(_section), "export"); });
        Item(export, "This notebook", null, () => _ = ExportNotebookAsync());
        export.Items.Add(new MenuFlyoutSeparator());
        var bg = new ToggleMenuFlyoutItem { Text = "Include page background", IsChecked = _exportBackground };
        bg.Click += (_, _) => _exportBackground = bg.IsChecked;
        export.Items.Add(bg);
        m.Items.Add(export);

        m.Items.Add(new MenuFlyoutSeparator());
        var dock = new MenuFlyoutSubItem { Text = "Toolbar position", Icon = new FontIcon { Glyph = "\uE8A0" } };
        foreach (var side in new[] { DockSide.Top, DockSide.Bottom, DockSide.Left, DockSide.Right })
        {
            var s = side;
            Item(dock, s.ToString(), null, () => { ApplyDock(s); _lastEffectiveDock = (DockSide)255; ApplyResponsiveLayout(); _settings.SaveSoon(); });
        }
        m.Items.Add(dock);
        Item(m, "Focus mode (F11)", "\uE740", ToggleFocusMode);
        Item(m, "Settings", "\uE713", () => _ = SettingsDialogAsync());
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

    /// <summary>Lined/grid/dot patterns in exported PDFs; off gives plain white sheets.</summary>
    private bool _exportBackground = true;

    private async Task ExportPageAsync()
    {
        if (_export is null || _engine is null) return;
        var path = await _export.ExportPage(_engine, _exportBackground);
        if (path is not null) ShowToast("Exported " + System.IO.Path.GetFileName(path));
    }

    private async Task ExportNotebookAsync()
    {
        if (_export is null || _engine is null || _notebook is null) return;
        var path = await _export.ExportNotebook(_engine, _notebook, _exportBackground);
        if (path is not null) ShowToast("Exported " + System.IO.Path.GetFileName(path));
    }

    // ---------------------------------------------------------------- page setup

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
            XamlRoot = Root.XamlRoot,
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

    // ---------------------------------------------------------------- settings

    private async Task SettingsDialogAsync()
    {
        var s = Settings;
        var panel = new StackPanel { Spacing = 10, MinWidth = 380 };

        panel.Children.Add(Header("Pen & touch"));
        var touchDraws = new ToggleSwitch { Header = "Draw with finger", IsOn = s.TouchDraws, OffContent = "Off: one finger scrolls", OnContent = "On: two fingers scroll" };
        panel.Children.Add(touchDraws);
        var barrel = new ComboBox { Header = "Pen barrel button", HorizontalAlignment = HorizontalAlignment.Stretch };
        barrel.Items.Add("Eraser"); barrel.Items.Add("Lasso select"); barrel.Items.Add("Right-click"); barrel.Items.Add("Nothing");
        barrel.SelectedIndex = (int)s.BarrelButton;
        panel.Children.Add(barrel);
        var autoPrediction = new ToggleSwitch
        {
            Header = "Stroke prediction",
            IsOn = s.AutoPrediction,
            OnContent = $"Automatic from the refresh rate ({DeviceTuner.AutoPredictionMs(_device.RefreshHz):0} ms at {_device.RefreshHz:0} Hz)",
            OffContent = "Fixed",
        };
        panel.Children.Add(autoPrediction);
        var prediction = new Slider { Header = "Fixed prediction (ms, 0 = off)", Minimum = 0, Maximum = 30, StepFrequency = 1, Value = s.PredictionMs, IsEnabled = !s.AutoPrediction };
        autoPrediction.Toggled += (_, _) => prediction.IsEnabled = !autoPrediction.IsOn;
        panel.Children.Add(prediction);
        var perfMode = new ToggleSwitch
        {
            Header = "Performance while writing",
            IsOn = s.PerformanceModeWhileWriting,
            OnContent = "On: high refresh rate and performance cores while the pen is near",
            OffContent = "Off: let Windows decide",
        };
        panel.Children.Add(perfMode);

        panel.Children.Add(Header("Layout"));
        var dock = new ComboBox { Header = "Toolbar position (left/right keeps tools away from your writing hand)", HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var d in new[] { "Top", "Bottom", "Left", "Right" }) dock.Items.Add(d);
        dock.SelectedIndex = (int)s.ToolbarDock;
        panel.Children.Add(dock);
        var sidebarRight = new ToggleSwitch { Header = "Notebooks on the right", IsOn = s.SidebarOnRight };
        panel.Children.Add(sidebarRight);

        panel.Children.Add(Header("Appearance"));
        var theme = new ComboBox { Header = "Theme", HorizontalAlignment = HorizontalAlignment.Stretch };
        theme.Items.Add("Use system setting"); theme.Items.Add("Light"); theme.Items.Add("Dark");
        theme.SelectedIndex = (int)s.Theme;
        panel.Children.Add(theme);
        var invert = new ToggleSwitch { Header = "Dark pages in dark theme (exports keep real colors)", IsOn = s.InvertPagesInDarkTheme };
        panel.Children.Add(invert);
        var oled = new ToggleSwitch { Header = "Pure black dark pages (best on OLED screens: black pixels switch off)", IsOn = s.OledBlackPages };
        panel.Children.Add(oled);

        panel.Children.Add(Header("This device"));
        panel.Children.Add(new TextBlock
        {
            Text = string.Join("\n", DeviceTuner.Describe(_device, s)),
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.8,
            IsTextSelectionEnabled = true,
        });

        panel.Children.Add(Header("About"));
        panel.Children.Add(new TextBlock { Text = $"Nib 0.1 (MVP) · data: {Services.AppPaths.DataFolder}", TextWrapping = TextWrapping.Wrap, Opacity = 0.7, IsTextSelectionEnabled = true });

        var dialog = new ContentDialog
        {
            Title = "Settings",
            Content = new ScrollViewer { Content = panel, MaxHeight = 600 },
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Root.XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        s.TouchDraws = touchDraws.IsOn;
        s.BarrelButton = (BarrelButtonAction)Math.Max(0, barrel.SelectedIndex);
        s.PredictionMs = (float)prediction.Value;
        s.AutoPrediction = autoPrediction.IsOn;
        s.PerformanceModeWhileWriting = perfMode.IsOn;
        s.OledBlackPages = oled.IsOn;
        s.SidebarOnRight = sidebarRight.IsOn;
        s.Theme = (ThemeMode)Math.Max(0, theme.SelectedIndex);
        s.InvertPagesInDarkTheme = invert.IsOn;
        ApplyDock((DockSide)Math.Max(0, dock.SelectedIndex));
        _lastEffectiveDock = (DockSide)255;
        ApplyTheme();
        ApplyResponsiveLayout();
        ApplyDeviceTuning(); // also pushes the tool state
        _settings.SaveSoon();
    }

    private static TextBlock Header(string text) => new()
    {
        Text = text,
        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        Margin = new Thickness(0, 8, 0, 0),
    };
}
