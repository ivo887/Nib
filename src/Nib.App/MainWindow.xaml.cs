using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Nib.App.Diagnostics;
using Nib.App.Ink;
using Nib.App.Platform;
using Nib.App.Services;
using Nib.App.Shell;
using Nib.App.Text;
using Nib.App.Windowing;
using Nib.Core.Device;
using Nib.Core.Diagnostics;
using Nib.Core.Input;
using Nib.Core.Layout;
using Nib.Core.Model;
using Nib.Storage;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;

namespace Nib.App;

public sealed partial class MainWindow : Window, IInkEngineHost
{
    private readonly LaunchOptions _options;
    private readonly SettingsStore _settings;
    private readonly WindowManager _windows;
    private readonly DispatcherQueue _ui;
    private readonly Toolbar _toolbar = new();
    private readonly UiStallWatchdog _watchdog;
    private readonly DebugOverlay _debug;
    private readonly Task<StorageService> _storageTask;
    private StorageService? _storage;
    private InkEngine? _engine;
    private TextEditorOverlay? _editor;
    private ExportService? _export;
    private PerfHarness? _harness;
    private readonly List<(string Action, KeyboardAccelerator Accelerator)> _accelerators = new();
    private readonly Dictionary<int, TagDefinition> _tags = new();
    private DispatcherQueueTimer? _toastTimer, _titleTimer;
    private bool _panelLoaded, _engineStarted, _suppressTitle, _darkPages;
    /// <summary>What the device probe found (pen, CPU cores, display, posture). Unknown until the probe returns.</summary>
    private DeviceSnapshot _device = DeviceSnapshot.Unknown;

    private AppSettings Settings => _settings.Current;

    internal MainWindow(LaunchOptions options)
    {
        _options = options;
        _ui = DispatcherQueue;
        // Open the database off the UI thread while the window appears.
        _storageTask = Task.Run(() => new StorageService(AppPaths.Database));
        _settings = new SettingsStore(AppPaths.Settings);
        InitializeComponent();
        StartupClock.Mark("XamlLoaded");

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(DragRegion);
        _windows = new WindowManager(this, Settings.Placement);
        _windows.AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        _windows.RestorePlacement();

        _watchdog = new UiStallWatchdog(_ui);
        _debug = new DebugOverlay(DebugText, _ui, _watchdog, () =>
            $"device {_engine?.DeviceDescription}\n{_engine?.InputDescription}\nstrokes on page {_engine?.StrokeCount}\nsafe area {_windows.SafeArea}");

        ApplyTheme();
        BuildToolbar();
        ApplyDock(Settings.ToolbarDock);
        WireSidebar();
        BuildOverflowMenu();
        RegisterAccelerators();

        Root.ActualThemeChanged += (_, _) => ApplyTheme();
        Root.SizeChanged += (_, _) => { ApplyResponsiveLayout(); UpdateCaptionInset(); };
        InkPanel.Loaded += (_, _) => { _panelLoaded = true; TryStartEngine(); };
        InkPanel.SizeChanged += (_, _) => ResizeEngine();
        InkPanel.CompositionScaleChanged += (_, _) => ResizeEngine();
        _windows.SafeAreaChanged += insets => Root.Padding = new Thickness(insets.Left, insets.Top, insets.Right, insets.Bottom);
        _windows.MonitorChanged += OnDisplayChanged;
        _windows.DisplayModeChanged += OnDisplayChanged;
        _windows.PostureChanged += () => { _device = _device with { SlateMode = DeviceProbe.IsSlateMode() }; ApplyDeviceTuning(); };
        _windows.AppWindow.Closing += OnClosing;
        Activated += OnFirstActivated;

        _toastTimer = _ui.CreateTimer();
        _toastTimer.Interval = TimeSpan.FromMilliseconds(1600);
        _toastTimer.IsRepeating = false;
        _toastTimer.Tick += (_, _) => Toast.Visibility = Visibility.Collapsed;
        _titleTimer = _ui.CreateTimer();
        _titleTimer.Interval = TimeSpan.FromMilliseconds(400);
        _titleTimer.IsRepeating = false;
        _titleTimer.Tick += (_, _) => CommitTitle();

        _ = InitializeStorageAsync();
        _ = ProbeDeviceAsync();
    }

    // =====================================================================================
    // Device tuning (pen digitizer, hybrid CPU, refresh rate, OLED, tablet posture)
    // =====================================================================================

    private async Task ProbeDeviceAsync()
    {
        IntPtr hwnd = _windows.Hwnd;
        try { _device = await Task.Run(() => DeviceProbe.Probe(hwnd)); }
        catch (Exception e) { CrashLog.Write(e); return; }
        if (_settings.IsFirstRun)
        {
            DeviceTuner.ApplyFirstRunDefaults(Settings, _device);
            _settings.SaveSoon();
            ApplyTheme();
        }
        ApplyDeviceTuning();
    }

    private void ApplyDeviceTuning()
    {
        _engine?.ConfigureDevice(Settings.PerformanceModeWhileWriting, CpuTopology.PerformanceCores(_device.Cores));
        double size = DeviceTuner.ToolbarButtonSize(_device.SlateMode);
        if (_toolbar.ButtonSize != size)
        {
            _toolbar.ButtonSize = size;
            ApplyDock(Settings.ToolbarDock); // rebuilds the toolbar at the new size
        }
        PushTool();
    }

    private void OnDisplayChanged()
    {
        double hz = Win32.RefreshRate(_windows.Hwnd);
        _device = _device with { RefreshHz = hz };
        _engine?.SetRefreshRate(hz);
    }

    private void OnFirstActivated(object sender, WindowActivatedEventArgs args)
    {
        Activated -= OnFirstActivated;
        _windows.Start();
        WindowFeedback.DisablePenVisuals(_windows.Hwnd);
        UpdateCaptionInset();
        ApplyResponsiveLayout();
        if (_options.DebugOverlay || Settings.DebugOverlay) _debug.Toggle(true);
    }

    private async Task InitializeStorageAsync()
    {
        try
        {
            _storage = await _storageTask;
        }
        catch (Exception e)
        {
            CrashLog.Write(e);
            ShowToast("Could not open your notebooks. See crash.log in the data folder.");
            return;
        }
        StartupClock.Mark("StorageOpen");
        var tagDefs = await _storage.Read(s => s.ListTagDefinitions());
        foreach (var t in tagDefs) _tags[t.Id] = t;
        _export = new ExportService(_storage, _windows.Hwnd, () => _tags);
        TryStartEngine();
        await LoadInitialPageAsync();
    }

    // =====================================================================================
    // Engine lifecycle
    // =====================================================================================

    private void TryStartEngine()
    {
        if (_engineStarted || !_panelLoaded || _storage is null) return;
        _engineStarted = true;
        _engine = new InkEngine(InkPanel, _ui, this, _storage, CurrentToolState());
        _editor = new TextEditorOverlay(Overlay, _engine, _ui) { DarkPages = _darkPages };
        _engine.SetTags(_tags.Values);
        _engine.SetDarkMode(_darkPages, Settings.OledBlackPages);
        _engine.SetRefreshRate(Win32.RefreshRate(_windows.Hwnd));
        _engine.ConfigureDevice(Settings.PerformanceModeWhileWriting, CpuTopology.PerformanceCores(_device.Cores));
        if (_options.PerfMode) SetUpPerfHarness();
        _engine.Start((float)InkPanel.ActualWidth, (float)InkPanel.ActualHeight, InkPanel.CompositionScaleX, OnFirstFrame);
        // Storage can finish before the panel loads; LoadInitialPageAsync had no engine to show the page on.
        if (_pageId != 0) _engine.ShowPage(_pageId);
    }

    private void OnFirstFrame()
    {
        double ms = StartupClock.SinceProcessStartMs;
        StartupClock.Mark("FirstWritableFrame");
        if (_harness is not null)
        {
            _harness.StartupMs = ms;
            _harness.Ready();
        }
    }

    private void ResizeEngine()
    {
        if (_engine is null) return;
        _harness?.OnResizeStarted();
        _engine.Resize((float)InkPanel.ActualWidth, (float)InkPanel.ActualHeight, InkPanel.CompositionScaleX);
    }

    private void SetUpPerfHarness()
    {
        string folder = _options.PerfOut ?? System.IO.Path.Combine(AppPaths.DataFolder, "perf");
        _harness = new PerfHarness(folder, _options.PerfScenario ?? "default", _ui, () =>
        {
            var t = InkPanel.TransformToVisual(null).TransformPoint(new Point(0, 0));
            double s = InkPanel.XamlRoot.RasterizationScale;
            Win32.POINT p = new() { X = 0, Y = 0 };
            Win32.ClientToScreen(_windows.Hwnd, ref p);
            return (_windows.Hwnd, p.X + (int)(t.X * s), p.Y + (int)(t.Y * s), (int)(InkPanel.ActualWidth * s), (int)(InkPanel.ActualHeight * s));
        }, () => { _ui.TryEnqueue(Close); });
        _harness.Measurements.PredictionMs = Settings.PredictionMs;
        _harness.PredictionFor = hz => DeviceTuner.EffectivePredictionMs(Settings, hz);
        _watchdog.Start();
        _engine!.FramePresented += _ =>
        {
            // Compare the size the frame was rendered at with the panel's layout size.
            float rendered = _engine.LastFrameWidth;
            _ui.TryEnqueue(() => _harness?.OnFramePresentedAfterResize(Math.Abs(InkPanel.ActualWidth - rendered) < 1));
        };
    }

    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        _editor?.Commit();
        _windows.SavePlacement();
        _settings.SaveNow();
        _engine?.Dispose();
        _storage?.Dispose();
        _watchdog.Dispose();
        _windows.Dispose();
    }

    // =====================================================================================
    // Tools
    // =====================================================================================

    private ToolState CurrentToolState()
    {
        var kind = _toolbar.ActiveKind;
        return new ToolState(kind, Settings.ActivePenPreset, Settings.Highlighter, Settings.EraserMode, Settings.EraserSize,
            Settings.BarrelButton, Settings.TouchDraws, Settings.PredictionMs, Settings.AutoPrediction);
    }

    private void PushTool()
    {
        if (_engine is not null) _engine.Tool = CurrentToolState();
    }

    private void BuildToolbar()
    {
        _toolbar.Build(Settings.Pens, Settings.Highlighter, ToolKind.Pen, Math.Clamp(Settings.ActivePen, 0, Math.Max(0, Settings.Pens.Count - 1)));
        _toolbar.ToolSelected += (kind, index) =>
        {
            if (kind == ToolKind.Pen) Settings.ActivePen = index;
            PushTool();
            _settings.SaveSoon();
        };
        _toolbar.ToolSettingsRequested += ShowToolSettings;
        _toolbar.UndoClicked += () => _engine?.Undo();
        _toolbar.RedoClicked += () => _engine?.Redo();
        _toolbar.AddPenClicked += () =>
        {
            if (Settings.Pens.Count >= 8) { ShowToast("Up to 8 favorite pens"); return; }
            Settings.Pens.Add(Settings.ActivePenPreset with { });
            Settings.ActivePen = Settings.Pens.Count - 1;
            RebuildToolbar();
        };
    }

    private void RebuildToolbar()
    {
        _toolbar.Build(Settings.Pens, Settings.Highlighter, _toolbar.ActiveKind, Settings.ActivePen);
        PushTool();
        _settings.SaveSoon();
    }

    private void ShowToolSettings(ToolKind kind, int index, FrameworkElement anchor)
    {
        Flyout flyout;
        switch (kind)
        {
            case ToolKind.Pen:
                flyout = ToolFlyouts.ForPen(Settings.Pens[index], highlighter: false, p =>
                {
                    Settings.Pens[index] = p;
                    PushTool();
                    _settings.SaveSoon();
                }, Settings.Pens.Count > 1 ? () =>
                {
                    Settings.Pens.RemoveAt(index);
                    Settings.ActivePen = Math.Clamp(Settings.ActivePen, 0, Settings.Pens.Count - 1);
                    RebuildToolbar();
                } : null);
                flyout.Closed += (_, _) => RebuildToolbar();
                break;
            case ToolKind.Highlighter:
                flyout = ToolFlyouts.ForPen(Settings.Highlighter, highlighter: true, p =>
                {
                    Settings.Highlighter = p;
                    PushTool();
                    _settings.SaveSoon();
                }, null);
                flyout.Closed += (_, _) => RebuildToolbar();
                break;
            case ToolKind.Eraser:
                flyout = ToolFlyouts.ForEraser(Settings.EraserMode, Settings.EraserSize, (mode, size) =>
                {
                    Settings.EraserMode = mode;
                    Settings.EraserSize = size;
                    PushTool();
                    _settings.SaveSoon();
                });
                break;
            default:
                return;
        }
        flyout.ShowAt(anchor);
    }

    // =====================================================================================
    // Layout: toolbar docking, sidebar, title bar, theme
    // =====================================================================================

    private void ApplyDock(DockSide dock)
    {
        Settings.ToolbarDock = dock;
        TitleToolbarHost.Content = null;
        EdgeToolbarHost.Content = null;
        _toolbar.Orientation = dock is DockSide.Top or DockSide.Bottom ? Orientation.Horizontal : Orientation.Vertical;
        _toolbar.Build(Settings.Pens, Settings.Highlighter, _toolbar.ActiveKind, Settings.ActivePen);
        var layout = ResponsiveLayout.Decide(Root.ActualWidth > 0 ? Root.ActualWidth : 1200, Root.ActualHeight > 0 ? Root.ActualHeight : 800, dock);
        // On narrow windows a top toolbar would crowd the title bar: move it to the bottom edge.
        var effective = dock == DockSide.Top && layout.CompactToolbar ? DockSide.Bottom : dock;
        if (effective == DockSide.Top)
        {
            TitleToolbarHost.Content = _toolbar;
            return;
        }
        EdgeToolbarHost.Content = _toolbar;
        switch (effective)
        {
            case DockSide.Bottom:
                Grid.SetRow(EdgeToolbarHost, 2); Grid.SetColumn(EdgeToolbarHost, 1);
                EdgeToolbarHost.HorizontalAlignment = HorizontalAlignment.Center;
                EdgeToolbarHost.VerticalAlignment = VerticalAlignment.Bottom;
                break;
            case DockSide.Left:
                Grid.SetRow(EdgeToolbarHost, 1); Grid.SetColumn(EdgeToolbarHost, 0);
                EdgeToolbarHost.HorizontalAlignment = HorizontalAlignment.Left;
                EdgeToolbarHost.VerticalAlignment = VerticalAlignment.Center;
                break;
            case DockSide.Right:
                Grid.SetRow(EdgeToolbarHost, 1); Grid.SetColumn(EdgeToolbarHost, 2);
                EdgeToolbarHost.HorizontalAlignment = HorizontalAlignment.Right;
                EdgeToolbarHost.VerticalAlignment = VerticalAlignment.Center;
                break;
        }
    }

    private DockSide _lastEffectiveDock = (DockSide)255;

    private void ApplyResponsiveLayout()
    {
        double w = Root.ActualWidth, h = Root.ActualHeight;
        if (w <= 0) return;
        var layout = ResponsiveLayout.Decide(w, h, Settings.ToolbarDock);
        Split.DisplayMode = layout.SidebarOverlays ? SplitViewDisplayMode.Overlay : SplitViewDisplayMode.Inline;
        Split.OpenPaneLength = layout.SidebarWidth;
        Split.PanePlacement = Settings.SidebarOnRight ? SplitViewPanePlacement.Right : SplitViewPanePlacement.Left;
        if (layout.SidebarOverlays && Split.IsPaneOpen && !_userOpenedOverlay) Split.IsPaneOpen = false;
        else if (!layout.SidebarOverlays) Split.IsPaneOpen = !Settings.SidebarCollapsed && !_windows.IsFullScreen;
        var effective = Settings.ToolbarDock == DockSide.Top && layout.CompactToolbar ? DockSide.Bottom : Settings.ToolbarDock;
        if (effective != _lastEffectiveDock)
        {
            _lastEffectiveDock = effective;
            ApplyDock(Settings.ToolbarDock);
        }
        TitleBox.Width = Math.Clamp(w * 0.22, 120, 320);
    }

    private bool _userOpenedOverlay;

    private void SidebarToggle_Click(object sender, RoutedEventArgs e) => ToggleSidebar();

    private void ToggleSidebar()
    {
        Split.IsPaneOpen = !Split.IsPaneOpen;
        if (Split.DisplayMode == SplitViewDisplayMode.Inline) Settings.SidebarCollapsed = !Split.IsPaneOpen;
        else _userOpenedOverlay = Split.IsPaneOpen;
        _settings.SaveSoon();
    }

    private void UpdateCaptionInset()
    {
        double scale = Root.XamlRoot?.RasterizationScale ?? 1;
        CaptionColumn.Width = new GridLength(Math.Max(0, _windows.AppWindow.TitleBar.RightInset / scale));
    }

    private void ApplyTheme()
    {
        Root.RequestedTheme = Settings.Theme switch
        {
            ThemeMode.Light => ElementTheme.Light,
            ThemeMode.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
        bool dark = Root.ActualTheme == ElementTheme.Dark;
        _darkPages = dark && Settings.InvertPagesInDarkTheme;
        Root.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Colors2D.From(_darkPages ? 0xFF1B1B1D : 0xFFE9E9EC));
        _engine?.SetDarkMode(_darkPages, Settings.OledBlackPages);
        if (_editor is not null) _editor.DarkPages = _darkPages;
    }

    // =====================================================================================
    // Keyboard
    // =====================================================================================

    private void RegisterAccelerators()
    {
        foreach (var (action, mods, key) in KeyMap.Resolve(Settings.KeyMap))
        {
            var acc = new KeyboardAccelerator { Key = key, Modifiers = mods };
            string a = action;
            acc.Invoked += (_, args) => { args.Handled = true; RunAction(a); };
            Root.KeyboardAccelerators.Add(acc);
            _accelerators.Add((action, acc));
        }
        Root.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
    }

    /// <summary>Page shortcuts step aside while the text editor has focus (it handles undo, copy, delete itself).</summary>
    private void SetPageShortcutsEnabled(bool enabled)
    {
        foreach (var (action, acc) in _accelerators)
            if (KeyMap.PageOnly.Contains(action)) acc.IsEnabled = enabled;
    }

    private void RunAction(string action)
    {
        var e = _engine;
        switch (action)
        {
            case "NewPage": _ = NewPageAsync(0); break;
            case "NewSubpage": _ = NewPageAsync(1); break;
            case "NewSection": _ = NewSectionAsync(); break;
            case "Undo": e?.Undo(); break;
            case "Redo": e?.Redo(); break;
            case "NextPage": OpenAdjacentPage(+1); break;
            case "PreviousPage": OpenAdjacentPage(-1); break;
            case "DemotePage": if (CurrentPageItem() is { } d) _ = PageCommandAsync(d, "demote"); break;
            case "PromotePage": if (CurrentPageItem() is { } p) _ = PageCommandAsync(p, "promote"); break;
            case "ZoomIn": e?.ZoomBy(1.25f); break;
            case "ZoomOut": e?.ZoomBy(0.8f); break;
            case "Zoom100": e?.ZoomPreset(ZoomPreset.Actual); break;
            case "FullPage": ToggleFocusMode(); break;
            case "Copy": e?.CopySelection(CopyTextToClipboard); break;
            case "Cut": e?.CutSelection(CopyTextToClipboard); break;
            case "Paste": e?.Paste(); break;
            case "Delete": e?.DeleteSelection(); break;
            case "ExportPdf": _ = ExportPageAsync(); break;
            case "ToggleSidebar": ToggleSidebar(); break;
            case "DebugOverlay":
                _debug.Toggle();
                Settings.DebugOverlay = _debug.IsVisible;
                break;
            default:
                if (action.StartsWith("Tag", StringComparison.Ordinal) && int.TryParse(action.AsSpan(3), out int n)) ApplyTagShortcut(n);
                break;
        }
    }

    private void ApplyTagShortcut(int shortcut)
    {
        var def = _tags.Values.FirstOrDefault(t => t.Shortcut == shortcut);
        if (def is null) return;
        if (_editor is { IsEditing: true }) _editor.ToggleTag(def.Id);
        else _engine?.TagAtCursor(def.Id);
    }

    private static void CopyTextToClipboard(string text)
    {
        var data = new DataPackage();
        data.SetText(text);
        Clipboard.SetContent(data);
    }

    private void ToggleFocusMode()
    {
        _windows.ToggleFullScreen();
        if (_windows.IsFullScreen) Split.IsPaneOpen = false;
        else ApplyResponsiveLayout();
    }

    // =====================================================================================
    // IInkEngineHost (always called on the UI thread)
    // =====================================================================================

    public void OnUndoStateChanged(bool canUndo, bool canRedo) => _toolbar.SetUndoState(canUndo, canRedo);

    public void OnSelectionChanged(Rect? bounds)
    {
        if (bounds is not { } r)
        {
            SelectionBar.Visibility = Visibility.Collapsed;
            return;
        }
        BuildSelectionBar();
        SelectionBar.Visibility = Visibility.Visible;
        SelectionBar.UpdateLayout();
        double top = r.Y - SelectionBar.ActualHeight - 40;
        if (top < 8) top = r.Y + r.Height + 16;
        double left = Math.Clamp(r.X + r.Width / 2 - SelectionBar.ActualWidth / 2, 8, Math.Max(8, PageHost.ActualWidth - SelectionBar.ActualWidth - 8));
        SelectionBar.Margin = new Thickness(left, Math.Clamp(top, 8, Math.Max(8, PageHost.ActualHeight - SelectionBar.ActualHeight - 8)), 0, 0);
    }

    private bool _selectionBarBuilt;

    private void BuildSelectionBar()
    {
        if (_selectionBarBuilt) return;
        _selectionBarBuilt = true;
        foreach (uint c in ToolFlyouts.PenColors.Take(6))
        {
            var b = new Button
            {
                Width = 30, Height = 30, Padding = new Thickness(0), Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0),
                Content = new Microsoft.UI.Xaml.Shapes.Ellipse { Width = 16, Height = 16, Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(Colors2D.From(c)) },
            };
            AutomationProperties_SetName(b, "Recolor " + Nib.Core.Rendering.ColorUtil.ToHex(c));
            uint color = c;
            b.Click += (_, _) => _engine?.RecolorSelection(color);
            SelectionBarItems.Children.Add(b);
        }
        AddBarButton("\uE8C8", "Copy", () => _engine?.CopySelection(CopyTextToClipboard));
        AddBarButton("\uE8C6", "Cut", () => _engine?.CutSelection(CopyTextToClipboard));
        AddBarButton("\uE74D", "Delete", () => _engine?.DeleteSelection());
    }

    private void AddBarButton(string glyph, string name, Action a)
    {
        var b = new Button { Content = new FontIcon { Glyph = glyph, FontSize = 14 }, Width = 34, Height = 30, Padding = new Thickness(0), BorderThickness = new Thickness(0), Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        ToolTipService.SetToolTip(b, name);
        AutomationProperties_SetName(b, name);
        b.Click += (_, _) => a();
        SelectionBarItems.Children.Add(b);
    }

    private static void AutomationProperties_SetName(DependencyObject o, string name) =>
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(o, name);

    public void OnTextEditRequested(TextContainer container, ViewState view, bool isNew, int caretParagraph)
    {
        if (_editor is null) return;
        SetPageShortcutsEnabled(false);
        _editor.Begin(container, view, caretParagraph);
        _editorClosedWatch ??= StartEditorWatch();
    }

    private DispatcherQueueTimer? _editorClosedWatch;

    /// <summary>Re-enables page shortcuts once the editor closes (focus moved back to the page).</summary>
    private DispatcherQueueTimer StartEditorWatch()
    {
        var t = _ui.CreateTimer();
        t.Interval = TimeSpan.FromMilliseconds(200);
        t.Tick += (_, _) => { if (_editor is { IsEditing: false }) SetPageShortcutsEnabled(true); };
        t.Start();
        return t;
    }

    public void OnTextEditEnded() => _editor?.Close();

    public void OnViewChanged(ViewState view) => _editor?.UpdateView(view);

    public void OnContextMenuRequested(Point position, bool hasSelection, bool canPaste)
    {
        var menu = new MenuFlyout();
        void Add(string text, string glyph, Action a, bool enabled = true)
        {
            var mi = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph }, IsEnabled = enabled };
            mi.Click += (_, _) => a();
            menu.Items.Add(mi);
        }
        if (hasSelection)
        {
            Add("Copy", "\uE8C8", () => _engine?.CopySelection(CopyTextToClipboard));
            Add("Cut", "\uE8C6", () => _engine?.CutSelection(CopyTextToClipboard));
            Add("Delete", "\uE74D", () => _engine?.DeleteSelection());
            menu.Items.Add(new MenuFlyoutSeparator());
        }
        Add("Paste", "\uE77F", () => _engine?.Paste(), canPaste);
        Add("Insert space", "\uE75B", () => SelectTool(ToolKind.InsertSpace));
        Add("Type text here", "\uE8D2", () => SelectTool(ToolKind.Text));
        menu.ShowAt(InkPanel, position);
    }

    private void SelectTool(ToolKind kind)
    {
        _toolbar.SetActive(kind, Settings.ActivePen);
        PushTool();
    }

    public void OnPageShown(long pageId, string title, PageLayout layout)
    {
        _suppressTitle = true;
        TitleBox.Text = title;
        _suppressTitle = false;
        Title = string.IsNullOrWhiteSpace(title) ? "Nib" : $"{title} – Nib";
        SidebarControl.SelectPage(pageId);
        SelectionBar.Visibility = Visibility.Collapsed;
    }

    public void OnStatus(string message) => ShowToast(message);

    private void ShowToast(string message)
    {
        ToastText.Text = message;
        Toast.Visibility = Visibility.Visible;
        _toastTimer?.Stop();
        _toastTimer?.Start();
    }

    // =====================================================================================
    // Title
    // =====================================================================================

    private void TitleBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressTitle) return;
        _titleTimer?.Stop();
        _titleTimer?.Start();
    }

    private void CommitTitle()
    {
        string title = TitleBox.Text.Trim();
        _engine?.SetTitle(title);
        if (CurrentPageItem() is { } item) item.Info = item.Info with { Title = title };
        Title = string.IsNullOrWhiteSpace(title) ? "Nib" : $"{title} – Nib";
    }
}
