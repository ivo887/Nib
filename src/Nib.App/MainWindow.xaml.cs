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
using Nib.Core.Ink;
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
    /// <summary>Notebook rail, section tabs and page list behind one object (see <see cref="Sidebar"/>).</summary>
    private Sidebar SidebarControl = null!;
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
        SidebarControl = new Sidebar(RailControl, TabsControl, PagesControl);
        StartupClock.Mark("XamlLoaded");

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(DragRegion);
        _windows = new WindowManager(this, Settings.Placement);
        Platform.CursorHider.RecoverAfterCrash(AppPaths.DataFolder);
        _windows.AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        _windows.RestorePlacement();

        _watchdog = new UiStallWatchdog(_ui);
        _debug = new DebugOverlay(DebugText, _ui, _watchdog, () =>
            $"device {_engine?.DeviceDescription}\n{_engine?.InputDescription}\nstrokes on page {_engine?.StrokeCount}\nsafe area {_windows.SafeArea}");

        ApplyTheme();
        ApplyBackdrop();
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
        // Never leave the cursor hidden when focus moves to another window or Nib closes.
        Activated += (_, e) => { if (e.WindowActivationState == WindowActivationState.Deactivated) { _pointerOverPage = false; UpdateSystemCursor(); } };
        Closed += (_, _) => { _pointerOverPage = false; _cursorRestoreTimer?.Stop(); _cursorHidden = false; Platform.CursorHider.RestoreNow(); };
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
        double size = Settings.ToolbarButtonSize > 0 ? Settings.ToolbarButtonSize : DeviceTuner.ToolbarButtonSize(_device.SlateMode);
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
        if (DataLocation.StartupMessage is { } note) { ShowToast(note); }
        var tagDefs = await _storage.Read(s => s.ListTagDefinitions());
        foreach (var t in tagDefs) _tags[t.Id] = t;
        _export = new ExportService(_storage, _windows.Hwnd, () => _tags, () => Settings);
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
        _harness.ApplyTool = ApplyPerfTool;
        _watchdog.Start();
        _engine!.FramePresented += _ =>
        {
            // Compare the size the frame was rendered at with the panel's layout size.
            float rendered = _engine.LastFrameWidth;
            _ui.TryEnqueue(() => _harness?.OnFramePresentedAfterResize(Math.Abs(InkPanel.ActualWidth - rendered) < 1));
        };
    }

    /// <summary>Perf runs only: selects "pen:&lt;PenKind&gt;", "highlighter" or "eraser:pixel|stroke".</summary>
    private void ApplyPerfTool(string name)
    {
        if (name.StartsWith("pen:", StringComparison.Ordinal) && Enum.TryParse<PenKind>(name.AsSpan(4), out var kind))
        {
            int i = Math.Clamp(Settings.ActivePen, 0, Settings.Pens.Count - 1);
            Settings.Pens[i] = Settings.Pens[i].WithKind(kind);
            _toolbar.SetActive(ToolKind.Pen, i);
            PushTool();
        }
        else if (name == "highlighter") SelectTool(ToolKind.Highlighter);
        else if (name.StartsWith("eraser:", StringComparison.Ordinal))
        {
            Settings.EraserMode = name.EndsWith("pixel", StringComparison.Ordinal) ? EraserMode.Pixel : EraserMode.Stroke;
            SelectTool(ToolKind.Eraser);
        }
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
            Settings.BarrelButton, Settings.TouchDraws, Settings.PredictionMs, Settings.AutoPrediction,
            Settings.Pointer, Settings.TwoFingerTapUndo, Settings.ThreeFingerTapRedo, Settings.PalmRejectionMs, Settings.PalmSizeDips,
            Settings.ShapeSnap, Settings.ShapeSnapHoldMs, Settings.SnapLineAngles, Settings.Tilt);
    }

    private void PushTool()
    {
        if (_engine is not null) _engine.Tool = CurrentToolState();
        UpdateSystemCursor(); // the text tool shows its own I-beam, the others may hide the cross
    }

    /// <summary>Rebuilds the toolbar buttons from the user's item list and size setting.</summary>
    private void BuildToolbarControls()
    {
        Settings.ToolbarItems = ToolbarLayout.Normalize(Settings.ToolbarItems);
        // In full-screen focus mode the toolbar becomes a dock: navigator first, sidebar and exit buttons last.
        _toolbar.Items = _windows?.IsFullScreen == true ? ToolbarLayout.ForFocus(Settings.ToolbarItems) : Settings.ToolbarItems;
        _toolbar.Floating = _windows?.IsFullScreen == true;
        _toolbar.ShapeSnapOn = Settings.ShapeSnap;
        _toolbar.NavLabel = string.IsNullOrWhiteSpace(_pageTitle) ? "Untitled page" : _pageTitle;
        _toolbar.ButtonSize = PreferredButtonSize();
        _toolbar.Build(Settings.Pens, Settings.Highlighter, _toolbar.ActiveKind, Settings.ActivePen);
        // How wide the toolbar really is decides whether it fits in the title bar or has to move to the bottom edge.
        _toolbar.Measure(new Windows.Foundation.Size(double.PositiveInfinity, 120));
        _toolbarWidth = _widthAtPreferred = _toolbar.Orientation == Orientation.Horizontal ? _toolbar.DesiredSize.Width : 0;
    }

    private double _toolbarWidth, _widthAtPreferred;

    /// <summary>Width a floating horizontal toolbar may use: the page area (or the whole screen in focus mode) minus its margins.</summary>
    private double ToolbarSpace()
    {
        double rootWidth = Root.ActualWidth > 0 ? Root.ActualWidth : 1200;
        return _focus ? rootWidth - 40 : Math.Max(240, (_panes.PageWidthLeft > 0 ? _panes.PageWidthLeft : rootWidth) - 24);
    }

    /// <summary>The button size the user chose, or the device's (larger in tablet posture).</summary>
    private double PreferredButtonSize() =>
        Settings.ToolbarButtonSize > 0 ? Settings.ToolbarButtonSize : DeviceTuner.ToolbarButtonSize(_device.SlateMode);

    /// <summary>
    /// A horizontal toolbar that is wider than the space it floats in (a narrow window, a bottom dock) shrinks its buttons a step
    /// at a time until it fits, rather than running off the edge of the page and hiding the menu button.
    /// </summary>
    private void FitToolbarWidth(double available)
    {
        if (_toolbar.Orientation != Orientation.Horizontal || available <= 0) return;
        foreach (double size in new[] { 36.0, 32.0, 28.0 })
        {
            if (_toolbarWidth <= available) return;
            if (size >= _toolbar.ButtonSize) continue;
            _toolbar.ButtonSize = size;
            _toolbar.Build(Settings.Pens, Settings.Highlighter, _toolbar.ActiveKind, Settings.ActivePen);
            _toolbar.Measure(new Windows.Foundation.Size(double.PositiveInfinity, 120));
            _toolbarWidth = _toolbar.DesiredSize.Width;
        }
    }

    /// <summary>Does the toolbar fit in the title bar beside the list toggle, the navigator, the page title and the window buttons?</summary>
    private bool TopDockFits(double windowWidth)
    {
        // Use the widths the title-bar items will really have at this window size (they scale with it), the real
        // caption-button inset (larger on high-DPI tablets) and a minimum strip left for dragging the window.
        double caption = CaptionColumn.Width.Value > 0 ? CaptionColumn.Width.Value : 138;
        double title = Math.Clamp(windowWidth * 0.2, 100, 300);
        double nav = Math.Clamp(windowWidth * 0.2, 90, 260) + 44;
        return windowWidth >= _widthAtPreferred + 40 + nav + title + caption + 96;
    }

    private void BuildToolbar()
    {
        // Older settings files get the buttons that were added since (the full-screen focus button) exactly once.
        if (Settings.ToolbarVersion < ToolbarLayout.CurrentVersion)
        {
            Settings.ToolbarItems = ToolbarLayout.Migrate(Settings.ToolbarItems, Settings.ToolbarVersion);
            Settings.ToolbarVersion = ToolbarLayout.CurrentVersion;
            _settings.SaveSoon();
        }
        Settings.ToolbarItems = ToolbarLayout.Normalize(Settings.ToolbarItems);
        _toolbar.Items = Settings.ToolbarItems;
        _toolbar.ShapeSnapOn = Settings.ShapeSnap;
        if (Settings.ToolbarButtonSize > 0) _toolbar.ButtonSize = Settings.ToolbarButtonSize;
        _toolbar.Build(Settings.Pens, Settings.Highlighter, ToolKind.Pen, Math.Clamp(Settings.ActivePen, 0, Math.Max(0, Settings.Pens.Count - 1)));
        _toolbar.ToolSelected += (kind, index) =>
        {
            if (kind == ToolKind.Pen) Settings.ActivePen = index;
            PushTool();
            _settings.SaveSoon();
        };
        _toolbar.ToolSettingsRequested += ShowToolSettings;
        _toolbar.CommandInvoked += OnToolbarCommand;
        _toolbar.SnapToggled += SetShapeSnap;
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

    /// <summary>Turns shape and line snapping on or off from the toolbar, the menu or Settings.</summary>
    private void SetShapeSnap(bool on)
    {
        Settings.ShapeSnap = on;
        _toolbar.ShapeSnapOn = on;
        PushTool();
        _settings.SaveSoon();
        ShowToast(on ? "Snapping on: hold the pen still at the end of a stroke" : "Snapping off");
    }

    private void RebuildToolbar()
    {
        BuildToolbarControls();
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
        BuildToolbarControls();
        bool focus = _windows.IsFullScreen;
        var layout = PaneLayoutRules.Decide(Root.ActualWidth > 0 ? Root.ActualWidth : 1200, Root.ActualHeight > 0 ? Root.ActualHeight : 800, dock, Settings.RailCompact, Settings.PagesHidden);

        // Normal window: a top toolbar lives in the title bar (or moves to the bottom edge when that is too narrow).
        // Full-screen focus mode has no title bar: the toolbar floats over the page as a dock on the chosen edge.
        var effective = !focus && dock == DockSide.Top && (layout.CompactToolbar || !TopDockFits(Root.ActualWidth > 0 ? Root.ActualWidth : 1200)) ? DockSide.Bottom : dock;
        if (effective == DockSide.Top && !focus)
        {
            TitleToolbarHost.Content = _toolbar;
            return;
        }
        EdgeToolbarHost.Content = _toolbar;
        double rootWidth = Root.ActualWidth > 0 ? Root.ActualWidth : 1200;
        FitToolbarWidth(ToolbarSpace());
        const int Margin = 14;
        if (focus)
        {
            // Over the page, on every row and column, so showing it never shifts the writing area.
            Grid.SetRow(EdgeToolbarHost, 0); Grid.SetRowSpan(EdgeToolbarHost, 3);
            Grid.SetColumn(EdgeToolbarHost, 0); Grid.SetColumnSpan(EdgeToolbarHost, 3);
            EdgeToolbarHost.Margin = new Thickness(Margin);
            Canvas.SetZIndex(EdgeToolbarHost, 10);
        }
        else
        {
            Grid.SetRowSpan(EdgeToolbarHost, 1); Grid.SetColumnSpan(EdgeToolbarHost, 1);
            EdgeToolbarHost.Margin = new Thickness(0, 0, 0, 8);
        }
        switch (effective)
        {
            case DockSide.Top: // focus mode only
                EdgeToolbarHost.HorizontalAlignment = HorizontalAlignment.Center;
                EdgeToolbarHost.VerticalAlignment = VerticalAlignment.Top;
                break;
            case DockSide.Bottom:
                if (!focus) { Grid.SetRow(EdgeToolbarHost, 2); Grid.SetColumn(EdgeToolbarHost, 1); }
                EdgeToolbarHost.HorizontalAlignment = HorizontalAlignment.Center;
                EdgeToolbarHost.VerticalAlignment = VerticalAlignment.Bottom;
                break;
            case DockSide.Left:
                if (!focus) { Grid.SetRow(EdgeToolbarHost, 1); Grid.SetColumn(EdgeToolbarHost, 0); }
                EdgeToolbarHost.HorizontalAlignment = HorizontalAlignment.Left;
                EdgeToolbarHost.VerticalAlignment = VerticalAlignment.Center;
                if (!focus) EdgeToolbarHost.Margin = new Thickness(0);
                break;
            case DockSide.Right:
                if (!focus) { Grid.SetRow(EdgeToolbarHost, 1); Grid.SetColumn(EdgeToolbarHost, 2); }
                EdgeToolbarHost.HorizontalAlignment = HorizontalAlignment.Right;
                EdgeToolbarHost.VerticalAlignment = VerticalAlignment.Center;
                if (!focus) EdgeToolbarHost.Margin = new Thickness(0);
                break;
        }
    }

    private DockSide _lastEffectiveDock = (DockSide)255;
    private PaneLayout _panes;
    private bool _focus, _railDrawerOpen, _pagesDrawerOpen;
    private string _pageTitle = "";

    /// <summary>
    /// Lays the shell out for the current window size and mode: notebook rail, section tabs and page list shrink into drawers as
    /// the window narrows, and full-screen focus mode hides all of them (they come back as drawers from the dock).
    /// </summary>
    private void ApplyResponsiveLayout()
    {
        double w = Root.ActualWidth, h = Root.ActualHeight;
        if (w <= 0) return;
        bool wasFocus = _focus;
        _focus = _windows.IsFullScreen;
        var l = PaneLayoutRules.Decide(w, h, Settings.ToolbarDock, Settings.RailCompact, Settings.PagesHidden);
        _panes = l;

        // Drawers float over the page, so they need a surface; panes that sit inline stay on the window material.
        bool darkUi = Root.ActualTheme == ElementTheme.Dark;
        var surface = new Microsoft.UI.Xaml.Media.SolidColorBrush(Colors2D.From(darkUi ? 0xFF2B2B2B : 0xFFFAFAFA));
        var clear = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);

        // Left-handed layout: the notebook list and the page list swap sides.
        bool swap = Settings.SidebarOnRight;
        RailSplit.PanePlacement = swap ? SplitViewPanePlacement.Right : SplitViewPanePlacement.Left;
        PageSplit.PanePlacement = swap ? SplitViewPanePlacement.Left : SplitViewPanePlacement.Right;

        // Notebook rail.
        if (_focus)
        {
            RailSplit.DisplayMode = SplitViewDisplayMode.Overlay;
            RailSplit.CompactPaneLength = 0;
            RailSplit.OpenPaneLength = PaneLayoutRules.ExpandedRailWidth;
            RailSplit.PaneBackground = surface;
            RailSplit.IsPaneOpen = _railDrawerOpen;
            RailControl.Expanded = true;
        }
        else switch (l.Rail)
        {
            case RailMode.Expanded:
                RailSplit.DisplayMode = SplitViewDisplayMode.Inline;
                RailSplit.OpenPaneLength = PaneLayoutRules.ExpandedRailWidth;
                RailSplit.PaneBackground = clear;
                RailSplit.IsPaneOpen = true;
                RailControl.Expanded = true;
                break;
            case RailMode.Compact:
                // A strip of badges that expands over the page (not pushing it) while open.
                RailSplit.DisplayMode = SplitViewDisplayMode.CompactOverlay;
                RailSplit.CompactPaneLength = PaneLayoutRules.CompactRailWidth;
                RailSplit.OpenPaneLength = PaneLayoutRules.ExpandedRailWidth;
                RailSplit.PaneBackground = RailSplit.IsPaneOpen ? surface : clear;
                if (wasFocus) RailSplit.IsPaneOpen = false;
                RailControl.Expanded = RailSplit.IsPaneOpen;
                break;
            default:
                RailSplit.DisplayMode = SplitViewDisplayMode.Overlay;
                RailSplit.CompactPaneLength = 0;
                RailSplit.OpenPaneLength = PaneLayoutRules.ExpandedRailWidth;
                RailSplit.PaneBackground = surface;
                if (!_railDrawerOpen) RailSplit.IsPaneOpen = false;
                RailControl.Expanded = true;
                break;
        }

        // Page list.
        if (!_focus && l.Pages == PagePaneMode.Inline)
        {
            PageSplit.DisplayMode = SplitViewDisplayMode.Inline;
            PageSplit.OpenPaneLength = l.PageWidth;
            PageSplit.PaneBackground = clear;
            PageSplit.IsPaneOpen = true;
        }
        else
        {
            PageSplit.DisplayMode = SplitViewDisplayMode.Overlay;
            PageSplit.OpenPaneLength = Math.Clamp(l.PageWidth, 240, 300);
            PageSplit.PaneBackground = surface;
            PageSplit.IsPaneOpen = _pagesDrawerOpen;
        }

        // Title bar and section tabs: gone in focus mode (the dock has the navigator instead).
        TitleBar.Visibility = _focus ? Visibility.Collapsed : Visibility.Visible;
        TitleRow.Height = new GridLength(_focus ? 0 : 48);
        TabsControl.Visibility = _focus ? Visibility.Collapsed : Visibility.Visible;
        TabsControl.Compact = l.CompactTabs;
        TabsControl.PagesShown = PageSplit.IsPaneOpen;

        var effective = !_focus && Settings.ToolbarDock == DockSide.Top && (l.CompactToolbar || !TopDockFits(w)) ? DockSide.Bottom : Settings.ToolbarDock;
        if (effective != _lastEffectiveDock || wasFocus != _focus)
        {
            _lastEffectiveDock = effective;
            ApplyDock(Settings.ToolbarDock);
        }
        // A floating toolbar keeps fitting as the window is dragged narrower, and grows back when there is room again.
        else if (ReferenceEquals(EdgeToolbarHost.Content, _toolbar) && _toolbar.Orientation == Orientation.Horizontal)
        {
            double space = ToolbarSpace();
            bool shrink = _toolbarWidth > space && _toolbar.ButtonSize > 28;
            bool grow = _toolbar.ButtonSize < PreferredButtonSize() && _widthAtPreferred <= space;
            if (shrink || grow) ApplyDock(Settings.ToolbarDock);
        }
        TitleBox.Width = Math.Clamp(w * 0.2, 100, 300);
        NavText.MaxWidth = Math.Clamp(w * 0.2, 90, 260);
    }

    /// <summary>Notebook list button: wide windows switch it between names and badges, narrower ones open it as a drawer.</summary>
    private void ToggleSidebar()
    {
        if (_focus)
        {
            // Focus mode: one button brings both lists back as drawers, and sends them away again.
            bool open = !(_railDrawerOpen || _pagesDrawerOpen);
            _railDrawerOpen = _pagesDrawerOpen = open;
            ApplyResponsiveLayout();
            return;
        }
        switch (_panes.Rail)
        {
            case RailMode.Expanded:
                Settings.RailCompact = true;
                _settings.SaveSoon();
                ApplyResponsiveLayout();
                break;
            case RailMode.Compact when Settings.RailCompact && Root.ActualWidth >= PaneLayoutRules.ExpandedAt:
                Settings.RailCompact = false;
                _settings.SaveSoon();
                ApplyResponsiveLayout();
                break;
            default:
                RailSplit.IsPaneOpen = !RailSplit.IsPaneOpen;
                _railDrawerOpen = RailSplit.IsPaneOpen && _panes.Rail == RailMode.Hidden;
                break;
        }
    }

    /// <summary>Page list button (end of the section bar): hides or shows the inline list, or opens the drawer on narrow windows.</summary>
    private void TogglePages()
    {
        if (!_focus && _panes.Pages == PagePaneMode.Inline)
        {
            Settings.PagesHidden = true;
            _settings.SaveSoon();
            ApplyResponsiveLayout();
        }
        else if (!_focus && Settings.PagesHidden && PaneLayoutRules.Decide(Root.ActualWidth, Root.ActualHeight, Settings.ToolbarDock, Settings.RailCompact, false).Pages == PagePaneMode.Inline)
        {
            Settings.PagesHidden = false;
            _settings.SaveSoon();
            ApplyResponsiveLayout();
        }
        else
        {
            _pagesDrawerOpen = !PageSplit.IsPaneOpen;
            PageSplit.IsPaneOpen = _pagesDrawerOpen;
            TabsControl.PagesShown = _pagesDrawerOpen;
        }
    }

    private void SidebarToggle_Click(object sender, RoutedEventArgs e) => ToggleSidebar();

    private void NavButton_Click(object sender, RoutedEventArgs e) => ShowNavigator(NavButton);

    /// <summary>The notebook / section / page manager dropdown, anchored to the title bar button or the dock.</summary>
    private void ShowNavigator(FrameworkElement anchor)
    {
        var flyout = NavigatorFlyout.Create(SidebarControl);
        flyout.ShowAt(anchor);
    }

    /// <summary>Closes any open drawer (after choosing a page, so the page is not left covered).</summary>
    private void CloseDrawers()
    {
        _pagesDrawerOpen = false;
        if (PageSplit.DisplayMode == SplitViewDisplayMode.Overlay) PageSplit.IsPaneOpen = false;
        if (RailSplit.DisplayMode != SplitViewDisplayMode.Inline && (_focus || _panes.Rail == RailMode.Hidden)) { _railDrawerOpen = false; RailSplit.IsPaneOpen = false; }
        TabsControl.PagesShown = PageSplit.IsPaneOpen;
    }

    /// <summary>Navigator and title-bar text: "Notebook › Section".</summary>
    private void UpdateNavText()
    {
        string nb = _notebook?.Name ?? "Notebook";
        NavText.Text = _section is null ? nb : $"{nb} › {_section.Name}";
        SidebarControl.PagesTitle = _section?.Name ?? "";
    }

    private void UpdateCaptionInset()
    {
        double scale = Root.XamlRoot?.RasterizationScale ?? 1;
        CaptionColumn.Width = new GridLength(Math.Max(0, _windows.AppWindow.TitleBar.RightInset / scale));
    }

    /// <summary>Sets the material behind the window (Windows 11 Mica / Mica Alt / Acrylic). Falls back to solid on older systems.</summary>
    private void ApplyBackdrop()
    {
        try
        {
            SystemBackdrop = Settings.Backdrop switch
            {
                BackdropKind.Mica => new Microsoft.UI.Xaml.Media.MicaBackdrop { Kind = Microsoft.UI.Composition.SystemBackdrops.MicaKind.Base },
                BackdropKind.MicaAlt => new Microsoft.UI.Xaml.Media.MicaBackdrop { Kind = Microsoft.UI.Composition.SystemBackdrops.MicaKind.BaseAlt },
                BackdropKind.Acrylic => new Microsoft.UI.Xaml.Media.DesktopAcrylicBackdrop(),
                _ => null,
            };
        }
        catch (Exception) { SystemBackdrop = null; }
        ApplyTheme(); // the root background depends on whether a backdrop is active
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
        // Caption buttons follow the system theme by default; match them to the app theme so they stay visible.
        var caption = _windows.AppWindow.TitleBar;
        var fg = dark ? Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF) : Windows.UI.Color.FromArgb(0xFF, 0x1A, 0x1A, 0x1A);
        var clear = Windows.UI.Color.FromArgb(0, 0, 0, 0);
        caption.ButtonBackgroundColor = clear;
        caption.ButtonInactiveBackgroundColor = clear;
        caption.ButtonForegroundColor = fg;
        caption.ButtonHoverForegroundColor = fg;
        caption.ButtonPressedForegroundColor = fg;
        caption.ButtonInactiveForegroundColor = Windows.UI.Color.FromArgb(0xFF, 0x8A, 0x8A, 0x8A);
        caption.ButtonHoverBackgroundColor = dark ? Windows.UI.Color.FromArgb(0x19, 0xFF, 0xFF, 0xFF) : Windows.UI.Color.FromArgb(0x0F, 0, 0, 0);
        caption.ButtonPressedBackgroundColor = dark ? Windows.UI.Color.FromArgb(0x0D, 0xFF, 0xFF, 0xFF) : Windows.UI.Color.FromArgb(0x09, 0, 0, 0);
        _darkPages = dark && Settings.InvertPagesInDarkTheme;
        // With Mica/Acrylic the window material shows through; with None, fall back to a plain Windows 11 surface.
        Root.Background = Settings.Backdrop == BackdropKind.None
            ? new Microsoft.UI.Xaml.Media.SolidColorBrush(Colors2D.From(dark ? 0xFF202020 : 0xFFF3F3F3))
            : new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
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
        // Esc steps back: an open drawer first, then focus mode (the text editor keeps Esc for itself).
        var esc = new KeyboardAccelerator { Key = Windows.System.VirtualKey.Escape };
        esc.Invoked += (_, args) =>
        {
            if (_editor is { IsEditing: true }) return;
            if (_railDrawerOpen || _pagesDrawerOpen) { _railDrawerOpen = _pagesDrawerOpen = false; ApplyResponsiveLayout(); args.Handled = true; }
            else if (_focus) { ToggleFocusMode(); args.Handled = true; }
        };
        Root.KeyboardAccelerators.Add(esc);
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
        _railDrawerOpen = _pagesDrawerOpen = false;
        _windows.ToggleFullScreen();
        ApplyResponsiveLayout(); // title bar, tabs and lists out (or back), the toolbar becomes the dock (or returns)
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
        _pageTitle = title;
        _toolbar.NavLabel = string.IsNullOrWhiteSpace(title) ? "Untitled page" : title;
        SidebarControl.SelectPage(pageId);
        SelectionBar.Visibility = Visibility.Collapsed;
    }

    public void OnStatus(string message) => ShowToast(message);

    // ---- the Windows cursor over the page ----

    private bool _pointerOverPage, _cursorHidden;

    public void OnPageHoverChanged(bool over)
    {
        _pointerOverPage = over;
        UpdateSystemCursor(immediate: false);
    }

    /// <summary>
    /// Hides the Windows cursor over the page when the user chose Hidden, or Automatic with a Nib-drawn pointer
    /// (so the cross is not drawn on top of it). The text tool always keeps its I-beam.
    /// </summary>
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _cursorRestoreTimer;

    private void UpdateSystemCursor(bool immediate = true)
    {
        var p = Settings.Pointer;
        bool hideMode = p.SystemCursor == SystemCursorMode.Hidden || (p.SystemCursor == SystemCursorMode.Auto && p.Style != PointerStyle.None);
        bool hide = _pointerOverPage && hideMode && _toolbar.ActiveKind != ToolKind.Text;
        if (hide)
        {
            _cursorRestoreTimer?.Stop(); // the pen came back: keep the cursor hidden, nothing to do
            if (_cursorHidden) return;
            _cursorHidden = true;
            Platform.CursorHider.BlankAsync();
            return;
        }
        if (!_cursorHidden) return;
        if (immediate) { _cursorRestoreTimer?.Stop(); _cursorHidden = false; Platform.CursorHider.RestoreAsync(); return; }
        // A pen lifting out of range and coming back is not leaving the page: wait before putting the cursor back.
        if (_cursorRestoreTimer is null)
        {
            _cursorRestoreTimer = _ui.CreateTimer();
            _cursorRestoreTimer.Interval = TimeSpan.FromMilliseconds(600);
            _cursorRestoreTimer.IsRepeating = false;
            _cursorRestoreTimer.Tick += (_, _) => UpdateSystemCursor(immediate: true);
        }
        _cursorRestoreTimer.Stop();
        _cursorRestoreTimer.Start();
    }

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
