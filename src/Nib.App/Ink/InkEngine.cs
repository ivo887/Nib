using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Nib.App.Platform;
using Nib.App.Windowing;
using Nib.Core.Device;
using Nib.Core.Diagnostics;
using Nib.Core.Editing;
using Nib.Core.Export;
using Nib.Core.Geometry;
using Nib.Core.Input;
using Nib.Core.Ink;
using Nib.Core.Model;
using Nib.Storage;

namespace Nib.App.Ink;

public enum ToolKind { Pen, Highlighter, Eraser, Lasso, Text, InsertSpace }

/// <summary>Immutable tool configuration. The UI swaps the whole object; the ink thread reads it at pointer-down, so a change applies to the very next stroke.</summary>
public sealed record ToolState(
    ToolKind Kind,
    PenPreset Pen,
    PenPreset Highlighter,
    EraserMode EraserMode,
    float EraserSizeDips,
    BarrelButtonAction Barrel,
    bool TouchDraws,
    float PredictionMs,
    bool AutoPrediction = false,
    PointerSettings? Pointer = null,
    bool TwoFingerUndo = true,
    bool ThreeFingerRedo = true,
    long PalmMs = PalmRejection.GraceMs,
    float PalmSizeDips = PalmRejection.PalmSize,
    bool ShapeSnap = true,
    int ShapeHoldMs = 600,
    bool SnapLineAngles = true,
    TiltSettings? Tilt = null);

/// <summary>Callbacks from the ink thread. The engine always invokes them on the UI thread.</summary>
public interface IInkEngineHost
{
    void OnUndoStateChanged(bool canUndo, bool canRedo);
    /// <summary>Selection bounds in surface DIPs, or null when nothing is selected.</summary>
    void OnSelectionChanged(Windows.Foundation.Rect? bounds);
    void OnTextEditRequested(TextContainer container, ViewState view, bool isNew, int caretParagraph);
    /// <summary>The engine ended editing on its own (page switch); the editor should close without committing.</summary>
    void OnTextEditEnded();
    void OnViewChanged(ViewState view);
    void OnContextMenuRequested(Windows.Foundation.Point position, bool hasSelection, bool canPaste);
    void OnPageShown(long pageId, string title, PageLayout layout);
    void OnStatus(string message);
    /// <summary>The mouse or pen entered or left the page (used to hide the Windows cursor when Nib draws its own).</summary>
    void OnPageHoverChanged(bool over);
}

/// <summary>
/// The ink engine owns everything on the dedicated ink thread: pointer input (pen, touch, mouse via an
/// independent input source on the swap chain panel), stroke processing, the live page model, undo,
/// rendering and presentation. The UI thread never touches ink or pixels, so XAML layout, sidebar
/// updates or a slow event handler can never delay a stroke.
/// </summary>
internal sealed partial class InkEngine : ITouchGestureHandler, IDisposable
{
    private const int PageCacheSize = 6;

    private readonly DispatcherQueue _ui;
    private readonly DispatcherQueueController _controller;
    private readonly DispatcherQueue _ink;
    private readonly IInkEngineHost _host;
    private readonly CanvasSwapChainPanel _panel;
    private readonly StorageService _storage;
    private ToolState _tool;

    // ---- ink-thread state ----
    private CanvasDevice? _device;
    private PageRenderer? _renderer;
    private InputPointerSource? _input;
    private PointerPredictor? _predictor;
    private PerformanceMode? _perf;
    private bool _perfEnabled;
    private uint[] _perfCores = Array.Empty<uint>();
    private float _predictionMs;
    private double _lastRefreshHz;
    private readonly ViewController _view = new();
    private readonly TouchGestureEngine _gestures;
    private readonly PalmRejection _palm = new();
    private readonly InertiaAnimator _inertia = new();
    private readonly InkProcessor _processor = new();
    private readonly StrokeEraser _eraser = new();
    private readonly Selection _selection = new();
    private readonly Dictionary<long, Page> _cache = new();
    /// <summary>Undo history of pages evicted from the cache. Commands address strokes and text by id, so they
    /// apply unchanged to the reloaded page: undo stays unlimited per page for the whole session.</summary>
    private readonly Dictionary<long, UndoStack> _evictedUndo = new();
    private readonly LinkedList<long> _lru = new();
    private readonly Dictionary<long, ViewState> _viewMemory = new();
    private readonly Dictionary<int, TagDefinition> _tags = new();
    private Page? _page;
    private AutosaveTracker? _tracker;
    private DispatcherQueueTimer? _saveTimer;
    private DispatcherQueueTimer? _settleTimer;
    private DispatcherQueueTimer? _holdTimer; // watches for the pen resting at the end of a stroke (shape snapping)
    private long _requestedPageId;
    private bool _renderQueued;
    private long _pendingInputTicks;
    private long _lastAnimTicks;
    private bool _dark, _trueBlack;
    private float _w = 1, _h = 1, _scale = 1;
    private bool _penPressureKnown;
    private long _editingId;
    private TextContainer? _editingOriginal;
    private bool _editingIsNew;
    private ViewState _lastPostedView;
    private Vector2 _lastPagePoint = new(96, 96);

    private static List<Stroke> s_clipStrokes = new();
    private static List<TextContainer> s_clipTexts = new();

    public bool IsReady => _renderer is not null;
    /// <summary>Width (DIPs) of the most recently presented frame; read by the perf harness.</summary>
    public float LastFrameWidth { get; private set; }
    public string DeviceDescription { get; private set; } = "";
    /// <summary>For the debug overlay only (read from the UI thread; a stale value is harmless).</summary>
    public int StrokeCount => _page?.Strokes.Count ?? 0;
    /// <summary>Debug overlay: prediction horizon in use and the performance-mode state.</summary>
    public string InputDescription => $"prediction {_predictionMs:0} ms · {_perf?.Describe() ?? "starting"}";

    /// <summary>Raised on the ink thread after a frame is presented (perf harness).</summary>
    public event Action<double>? FramePresented;

    public InkEngine(CanvasSwapChainPanel panel, DispatcherQueue ui, IInkEngineHost host, StorageService storage, ToolState tool)
    {
        _panel = panel;
        _ui = ui;
        _host = host;
        _storage = storage;
        _tool = tool;
        _gestures = new TouchGestureEngine(this);
        _controller = DispatcherQueueController.CreateOnDedicatedThread();
        _ink = _controller.DispatcherQueue;
    }

    /// <summary>Current tool. Written by the UI thread, read at pointer-down on the ink thread.</summary>
    public ToolState Tool
    {
        get => Volatile.Read(ref _tool);
        set
        {
            var old = Volatile.Read(ref _tool);
            Volatile.Write(ref _tool, value);
            long t = Stopwatch.GetTimestamp();
            _ink.TryEnqueue(() =>
            {
                PerfCounters.Instance.ToolApply.Record((Stopwatch.GetTimestamp() - t) * 1000.0 / Stopwatch.Frequency);
                if (old.Kind != value.Kind) OnToolKindChanged(value.Kind);
                ApplyPrediction(value);
                ApplyUserTuning(value);
            });
        }
    }

    // =====================================================================================
    // Startup, resize, device
    // =====================================================================================

    /// <summary>Call on the UI thread once the panel has a size.</summary>
    public void Start(float width, float height, float scale, Action? onFirstFrame = null)
    {
        _ink.TryEnqueue(DispatcherQueuePriority.High, () =>
        {
            ThreadTuning.BoostCurrentThread("Nib ink");
            _device = GraphicsDevice.Create(out var desc);
            DeviceDescription = desc;
            _device.DeviceLost += (_, _) => _ink.TryEnqueue(RecreateDevice);
            _renderer = new PageRenderer(_device) { Tags = _tags, WetPoints = () => _processor.PointSpan };
            _renderer.SetDarkMode(_dark, _trueBlack);
            CreateSwapChain(width, height, scale);

            // Pen, touch and mouse for the page arrive on this thread, never on the UI thread.
            _input = _panel.CreateCoreIndependentInputSource(
                InputPointerSourceDeviceKinds.Pen | InputPointerSourceDeviceKinds.Touch | InputPointerSourceDeviceKinds.Mouse);
            _input.PointerPressed += OnPointerPressed;
            _input.PointerMoved += OnPointerMoved;
            _input.PointerReleased += OnPointerReleased;
            _input.PointerExited += OnPointerExited;
            _input.PointerEntered += OnPointerEntered;
            _input.PointerCaptureLost += OnPointerCaptureLost;
            _input.PointerWheelChanged += OnPointerWheelChanged;
            ApplyCursorShape(Tool);
            _predictor = PointerPredictor.CreateForInputPointerSource(_input);
            _lastRefreshHz = PerfCounters.Instance.RefreshHz;
            ApplyPrediction(Tool);
            ApplyUserTuning(Tool);
            _perf = new PerformanceMode(_ink);
            _perf.Configure(_perfEnabled, _perfCores);

            _saveTimer = _ink.CreateTimer();
            _saveTimer.Interval = TimeSpan.FromMilliseconds(300);
            _saveTimer.IsRepeating = false;
            _saveTimer.Tick += (_, _) => FlushAutosave();

            _settleTimer = _ink.CreateTimer();
            _settleTimer.Interval = TimeSpan.FromMilliseconds(120);
            _settleTimer.IsRepeating = false;
            _settleTimer.Tick += (_, _) => SettleZoom();

            _holdTimer = _ink.CreateTimer();
            _holdTimer.Interval = TimeSpan.FromMilliseconds(80);
            _holdTimer.IsRepeating = true;
            _holdTimer.Tick += (_, _) => OnHoldTick();

            if (onFirstFrame is not null) FramePresentedOnce(onFirstFrame);
            RequestRender();
        });
    }

    private void FramePresentedOnce(Action a)
    {
        Action<double>? handler = null;
        handler = _ =>
        {
            if (_page is null) return;
            FramePresented -= handler;
            _ui.TryEnqueue(() => a());
        };
        FramePresented += handler;
    }

    private void CreateSwapChain(float w, float h, float scale)
    {
        _w = MathF.Max(w, 1); _h = MathF.Max(h, 1); _scale = scale;
        // Opaque (alpha "ignore"): the page always covers the panel, and an opaque swap chain lets the
        // compositor skip blending it and, on GPUs that support it (Intel Xe2 does), scan it out from a
        // hardware overlay plane instead of copying it into the desktop image.
        var swap = new CanvasSwapChain(_device!, _w, _h, 96f * scale,
            Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, CanvasAlphaMode.Ignore);
        _renderer!.AttachSwapChain(swap, _w, _h, scale);
        _view.SetViewport(_w, _h);
        // The XAML property must be set on the UI thread; rendering into it from here is fine.
        _ui.TryEnqueue(() => _panel.SwapChain = swap);
    }

    private void RecreateDevice()
    {
        if (_renderer is null) return;
        _device = GraphicsDevice.Create(out var desc);
        DeviceDescription = desc;
        foreach (var p in _cache.Values)
            foreach (var s in p.Strokes) { (s.RenderCache as IDisposable)?.Dispose(); s.RenderCache = null; }
        _renderer.ResetDevice(_device);
        CreateSwapChain(_w, _h, _scale);
        RequestRender();
    }

    /// <summary>Panel size or DPI changed (window resize, rotation, taskbar move, monitor change).</summary>
    public void Resize(float width, float height, float scale)
    {
        _ink.TryEnqueue(DispatcherQueuePriority.High, () =>
        {
            if (_renderer is null) return;
            _w = MathF.Max(width, 1); _h = MathF.Max(height, 1); _scale = scale;
            _renderer.Resize(_w, _h, scale);
            _view.SetViewport(_w, _h);
            RenderNow(); // reflow in the same frame: no stretched or blank content
            PostViewIfEditing();
        });
    }

    public void SetDarkMode(bool dark, bool trueBlack = false) => _ink.TryEnqueue(() =>
    {
        _dark = dark;
        _trueBlack = trueBlack;
        _renderer?.SetDarkMode(dark, trueBlack);
        RequestRender();
    });

    public void SetTags(IEnumerable<TagDefinition> tags)
    {
        var copy = tags.ToList();
        _ink.TryEnqueue(() =>
        {
            _tags.Clear();
            foreach (var t in copy) _tags[t.Id] = t;
            RequestRender();
        });
    }

    public void SetRefreshRate(double hz)
    {
        PerfCounters.Instance.RefreshHz = hz;
        _ink.TryEnqueue(() => { _lastRefreshHz = hz; ApplyPrediction(Tool); });
    }

    /// <summary>Hardware tuning from the device probe: performance mode and the CPU's fast cores.</summary>
    public void ConfigureDevice(bool performanceMode, uint[] performanceCores) => _ink.TryEnqueue(() =>
    {
        _perfEnabled = performanceMode;
        _perfCores = performanceCores;
        _perf?.Configure(performanceMode, performanceCores);
    });

    /// <summary>Pointer look and palm rejection come from user settings; applied on the ink thread.</summary>
    private void ApplyUserTuning(ToolState tool)
    {
        ApplyCursorShape(tool);
        _palm.Grace = Math.Clamp(tool.PalmMs, 0, 3000);
        _palm.Size = Math.Clamp(tool.PalmSizeDips, 10f, 200f);
        if (_renderer is not null)
        {
            var p = (tool.Pointer ?? new PointerSettings()).Sanitized();
            if (p != _renderer.Pointer) { _renderer.Pointer = p; RequestRender(); }
        }
    }

    private void ApplyPrediction(ToolState tool)
    {
        _predictionMs = tool.AutoPrediction ? DeviceTuner.AutoPredictionMs(_lastRefreshHz) : Math.Clamp(tool.PredictionMs, 0, 30);
        if (_predictor is not null) _predictor.PredictionTime = TimeSpan.FromMilliseconds(Math.Max(1, _predictionMs));
    }

    /// <summary>
    /// At pen-down: re-read the compositor's refresh rate. With Dynamic refresh rate the panel switches
    /// between 60 and 120 Hz, so the prediction horizon (and dropped-frame accounting) follows it.
    /// </summary>
    private void RefreshPredictionFromDisplay()
    {
        double hz = Win32.RefreshRate(IntPtr.Zero);
        if (Math.Abs(hz - _lastRefreshHz) < 0.5) return;
        _lastRefreshHz = hz;
        PerfCounters.Instance.RefreshHz = hz;
        ApplyPrediction(Tool);
    }

    // =====================================================================================
    // Pages
    // =====================================================================================

    public void ShowPage(long pageId) => _ink.TryEnqueue(DispatcherQueuePriority.High, () => ShowPageOnInk(pageId));

    private void ShowPageOnInk(long pageId)
    {
        var sw = Stopwatch.StartNew();
        _requestedPageId = pageId;
        if (_page?.Id == pageId) return;
        if (_cache.TryGetValue(pageId, out var cached))
        {
            Activate(cached, sw);
            return;
        }
        var device = _device;
        _storage.Read(s => s.LoadPage(pageId)).ContinueWith(t =>
        {
            var p = t.IsCompletedSuccessfully ? t.Result : null;
            if (p is not null && device is not null) PageRenderer.Prepare(device, p, new StrokeGeometryBuilder());
            _ink.TryEnqueue(DispatcherQueuePriority.High, () =>
            {
                if (p is null)
                {
                    _host.Post(_ui, h => h.OnStatus("That page could not be opened."));
                    return;
                }
                AddToCache(p);
                if (_requestedPageId == pageId) Activate(p, sw);
            });
        }, TaskScheduler.Default);
    }

    /// <summary>Loads neighbors in the background so the next page switch is instant.</summary>
    public void Preload(IReadOnlyList<long> pageIds)
    {
        var ids = pageIds.ToArray();
        _ink.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            var device = _device;
            foreach (long id in ids)
            {
                if (_cache.ContainsKey(id)) { Touch(id); continue; }
                long pid = id;
                _storage.Read(s => s.LoadPage(pid)).ContinueWith(t =>
                {
                    var p = t.IsCompletedSuccessfully ? t.Result : null;
                    if (p is null) return;
                    if (device is not null && p.Strokes.Count <= 30_000) PageRenderer.Prepare(device, p, new StrokeGeometryBuilder());
                    _ink.TryEnqueue(DispatcherQueuePriority.Low, () => { if (!_cache.ContainsKey(pid)) AddToCache(p); });
                }, TaskScheduler.Default);
            }
        });
    }

    private void AddToCache(Page p)
    {
        if (_evictedUndo.Remove(p.Id, out var history)) p.Undo = history;
        _cache[p.Id] = p;
        Touch(p.Id);
        while (_lru.Count > PageCacheSize)
        {
            long victim = _lru.Last!.Value;
            if (victim == _page?.Id) { _lru.RemoveLast(); _lru.AddFirst(victim); continue; }
            _lru.RemoveLast();
            if (_cache.Remove(victim, out var old))
            {
                foreach (var s in old.Strokes) { (s.RenderCache as IDisposable)?.Dispose(); s.RenderCache = null; }
                if (old.Undo.CanUndo || old.Undo.CanRedo) _evictedUndo[victim] = old.Undo;
            }
        }
    }

    private void Touch(long id)
    {
        _lru.Remove(id);
        _lru.AddFirst(id);
    }

    private void Activate(Page page, Stopwatch sw)
    {
        if (_renderer is null) return;
        if (_page is not null)
        {
            EndTextEditImmediately();
            FlushAutosave();
            _viewMemory[_page.Id] = _view.State;
            if (_tracker is not null) _page.RemoveObserver(_tracker);
            _page.Undo.Changed -= OnUndoChanged;
            ClearSelection();
        }
        CancelActiveInput();
        _page = page;
        Touch(page.Id);
        _tracker = new AutosaveTracker(page.Id);
        _tracker.Changed += _ => { _saveTimer?.Stop(); _saveTimer?.Start(); };
        page.AddObserver(_tracker);
        page.Undo.Changed += OnUndoChanged;
        _renderer.SetPage(page);
        _view.SetPage(page.Layout, _viewMemory.TryGetValue(page.Id, out var v) ? v : null);
        RenderNow();
        double ms = sw.Elapsed.TotalMilliseconds;
        PerfCounters.Instance.PageSwitch.Record(ms);
        NibEventSource.Log.PageSwitch(page.Id, ms);
        string title = page.Title;
        var layout = page.Layout;
        long id = page.Id;
        _host.Post(_ui, h => h.OnPageShown(id, title, layout));
        OnUndoChanged();
    }

    public void ForgetPage(long pageId) => _ink.TryEnqueue(() =>
    {
        if (_page?.Id == pageId) return;
        _cache.Remove(pageId);
        _lru.Remove(pageId);
        _evictedUndo.Remove(pageId);
    });

    /// <summary>Immutable snapshot of the current page for export (taken on the ink thread).</summary>
    public Task<ExportPage?> SnapshotCurrentPage()
    {
        var tcs = new TaskCompletionSource<ExportPage?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ink.TryEnqueue(() =>
        {
            if (_page is null) { tcs.SetResult(null); return; }
            FlushAutosave();
            tcs.SetResult(new ExportPage(_page.Title, _page.Layout, _page.Strokes.ToArray(), _page.Texts.ToArray()));
        });
        return tcs.Task;
    }

    public void SetTitle(string title) => _ink.TryEnqueue(() => _page?.SetTitle(title));

    public void SetLayout(PageLayout layout) => _ink.TryEnqueue(() =>
    {
        if (_page is null) return;
        var merged = layout with { SheetCount = Math.Max(layout.SheetCount, _page.Layout.SheetCount) };
        if (merged == _page.Layout) return;
        _page.Execute(new LayoutCommand(_page.Layout, merged));
        _view.UpdateLayout(_page.Layout);
        RequestRender();
    });

    // =====================================================================================
    // Autosave
    // =====================================================================================

    /// <summary>Writes pending changes. Runs on the ink thread: only collects the change set; disk I/O happens on the storage writer thread.</summary>
    private Task FlushAutosave()
    {
        _saveTimer?.Stop();
        if (_tracker is null || !_tracker.HasChanges) return Task.CompletedTask;
        return _storage.Save(_tracker.TakeChanges());
    }

    /// <summary>Flushes and waits (window closing). Safe to call from the UI thread.</summary>
    public void FlushAndWait(TimeSpan timeout)
    {
        var tcs = new TaskCompletionSource<Task>();
        if (!_ink.TryEnqueue(DispatcherQueuePriority.High, () => tcs.SetResult(FlushAutosave()))) return;
        if (tcs.Task.Wait(timeout)) tcs.Task.Result.Wait(timeout);
        _storage.Flush(timeout);
    }

    // =====================================================================================
    // Commands from the UI
    // =====================================================================================

    public void Undo() => _ink.TryEnqueue(() =>
    {
        if (_page is null) return;
        ClearSelection();
        if (_page.UndoLast()) RequestRender();
    });

    public void Redo() => _ink.TryEnqueue(() =>
    {
        if (_page is null) return;
        ClearSelection();
        if (_page.RedoLast()) RequestRender();
    });

    private void OnUndoChanged()
    {
        if (_page is null) return;
        bool u = _page.Undo.CanUndo, r = _page.Undo.CanRedo;
        _host.Post(_ui, h => h.OnUndoStateChanged(u, r));
    }

    public void ZoomPreset(ZoomPreset preset) => _ink.TryEnqueue(() =>
    {
        _view.ApplyPreset(preset);
        SnapZoomAround(_w / 2, _h / 2);
        RequestRender();
        PostViewIfEditing();
    });

    public void ZoomBy(float factor) => _ink.TryEnqueue(() =>
    {
        _view.ZoomAt(factor, _w / 2, _h / 2);
        SnapZoomAround(_w / 2, _h / 2);
        RequestRender();
        PostViewIfEditing();
    });

    public void ScrollBy(float dx, float dy) => _ink.TryEnqueue(() =>
    {
        _view.Pan(dx, dy);
        RequestRender();
        PostViewIfEditing();
    });

    public void DeleteSelection() => _ink.TryEnqueue(() =>
    {
        if (_page is null || _selection.IsEmpty) return;
        _page.Execute(SelectionOps.Delete(_selection));
        ClearSelection();
        RequestRender();
    });

    public void CopySelection(Action<string>? copyText = null) => _ink.TryEnqueue(() =>
    {
        if (_selection.IsEmpty) return;
        s_clipStrokes = _selection.Strokes.ToList();
        s_clipTexts = _selection.Texts.ToList();
        string text = string.Join("\n", s_clipTexts.Select(t => t.Text.PlainText));
        if (copyText is not null && text.Length > 0) _ui.TryEnqueue(() => copyText(text));
    });

    public void CutSelection(Action<string>? copyText = null)
    {
        CopySelection(copyText);
        DeleteSelection();
    }

    public void Paste() => _ink.TryEnqueue(() =>
    {
        if (_page is null || (s_clipStrokes.Count == 0 && s_clipTexts.Count == 0)) return;
        var bounds = RectF.Empty;
        foreach (var s in s_clipStrokes) bounds = bounds.Union(s.Bounds);
        foreach (var t in s_clipTexts) bounds = bounds.Union(t.Bounds);
        // Paste at the last touched point, or offset from the original when pasting on the same spot.
        float dx = _lastPagePoint.X - bounds.Left, dy = _lastPagePoint.Y - bounds.Top;
        var cmd = SelectionOps.PasteCopies(_page, s_clipStrokes, s_clipTexts, dx, dy);
        _page.Execute(cmd);
        ClearSelection();
        RequestRender();
    });

    public bool HasClipboard => s_clipStrokes.Count > 0 || s_clipTexts.Count > 0;

    public void RecolorSelection(uint color) => _ink.TryEnqueue(() =>
    {
        if (_page is null || _selection.Strokes.Count == 0) return;
        _page.Execute(SelectionOps.Recolor(_selection, color));
        _selection.Refresh(_page);
        RequestRender();
    });

    public void ClearSelectionAsync() => _ink.TryEnqueue(() => { ClearSelection(); RequestRender(); });

    /// <summary>Ctrl+1..9 when no text is being edited: starts a new tagged paragraph where the user last wrote or tapped.</summary>
    public void TagAtCursor(int tagId) => _ink.TryEnqueue(() =>
    {
        if (_page is null) return;
        var p = Paragraph.Plain(string.Empty).WithTag(tagId, true);
        var c = new TextContainer(IdGenerator.Next(), _page.NextZ(), _lastPagePoint.X + TextContainer.TagGutter, _lastPagePoint.Y,
            DefaultTextWidth(_lastPagePoint.X), new RichText { Paragraphs = System.Collections.Immutable.ImmutableArray.Create(p) });
        BeginTextEdit(c, isNew: true, caretParagraph: 0);
    });

    private float DefaultTextWidth(float x)
    {
        if (_page is null) return TextContainer.DefaultWidth;
        var l = _page.Layout;
        return l.Mode == PageMode.Fixed ? Math.Clamp(l.SheetWidth - x - 48, 120, TextContainer.DefaultWidth) : TextContainer.DefaultWidth;
    }

    // =====================================================================================
    // Text editing (the editor itself is a RichEditBox on the UI thread)
    // =====================================================================================

    private void BeginTextEdit(TextContainer c, bool isNew, int caretParagraph)
    {
        if (_page is null || _renderer is null) return;
        EndTextEditImmediately();
        ClearSelection();
        _editingId = c.Id;
        _editingIsNew = isNew;
        _editingOriginal = isNew ? null : c;
        if (isNew) _page.PutText(c); // visible immediately; recorded for undo only when committed
        _renderer.EditingTextId = c.Id;
        _renderer.EditingDraft = c.Text;
        _renderer.InvalidateText(c);
        RequestRender();
        var view = _view.State;
        _lastPostedView = view;
        _host.Post(_ui, h => h.OnTextEditRequested(c, view, isNew, caretParagraph));
    }

    /// <summary>Live draft while typing: the page shows tags and reserves space; no undo entries.</summary>
    public void UpdateTextDraft(long id, RichText draft) => _ink.TryEnqueue(() =>
    {
        if (_editingId != id || _renderer is null || _page?.FindText(id) is not { } c) return;
        _renderer.EditingDraft = draft;
        _renderer.InvalidateText(c);
        _renderer.InvalidateText(c.WithText(draft));
        RequestRender();
    });

    /// <summary>Editing finished: one undo step for the whole edit; empty new boxes disappear.</summary>
    public void CommitTextEdit(long id, RichText final, float x, float y, float width) => _ink.TryEnqueue(() =>
    {
        if (_editingId != id || _page is null || _renderer is null) return;
        var current = _page.FindText(id);
        _editingId = 0;
        _renderer.EditingTextId = 0;
        _renderer.EditingDraft = null;
        if (current is null) return;
        _renderer.InvalidateText(current);
        if (final.IsBlank)
        {
            if (_editingIsNew) _page.RemoveText(id);
            else _page.Execute(new TextCommand("Delete text", _editingOriginal, null));
        }
        else
        {
            var orig = _editingOriginal;
            bool unchanged = !_editingIsNew && orig is not null && RichTextEquals(orig.Text, final) && TagsEqual(orig.Text, final)
                             && orig.X == x && orig.Y == y && orig.Width == width;
            if (!unchanged)
            {
                var updated = new TextContainer(id, current.Z, x, y, width, final, 0);
                _page.PutText(updated);
                _page.Undo.Push(new TextCommand(_editingIsNew ? "Text" : "Edit text", orig, updated));
            }
        }
        _editingOriginal = null;
        RequestRender();
    });

    private static bool TagsEqual(RichText a, RichText b)
    {
        if (a.Paragraphs.Length != b.Paragraphs.Length) return false;
        for (int i = 0; i < a.Paragraphs.Length; i++)
            if (!a.Paragraphs[i].TagIds.SequenceEqual(b.Paragraphs[i].TagIds) || a.Paragraphs[i].Checked != b.Paragraphs[i].Checked) return false;
        return true;
    }

    private static bool RichTextEquals(RichText? a, RichText b)
    {
        if (a is null || a.Paragraphs.Length != b.Paragraphs.Length) return false;
        for (int i = 0; i < a.Paragraphs.Length; i++)
        {
            var pa = a.Paragraphs[i];
            var pb = b.Paragraphs[i];
            if (pa.Kind != pb.Kind || pa.Indent != pb.Indent || pa.Runs.Length != pb.Runs.Length) return false;
            for (int r = 0; r < pa.Runs.Length; r++)
                if (pa.Runs[r].Text != pb.Runs[r].Text || pa.Runs[r].Style != pb.Runs[r].Style) return false;
        }
        return true;
    }

    private void EndTextEditImmediately()
    {
        if (_editingId == 0) return;
        // The UI commits through CommitTextEdit; if the page changes first, keep what is on the page.
        if (_editingIsNew && _page?.FindText(_editingId) is { } c && c.Text.IsBlank) _page.RemoveText(_editingId);
        _editingId = 0;
        if (_renderer is not null)
        {
            _renderer.EditingTextId = 0;
            _renderer.EditingDraft = null;
        }
        _host.Post(_ui, h => h.OnTextEditEnded());
    }

    private void PostViewIfEditing()
    {
        if (_editingId == 0 && _selection.IsEmpty) return;
        var v = _view.State;
        if (v.Equals(_lastPostedView)) return;
        _lastPostedView = v;
        if (_editingId != 0) _host.Post(_ui, h => h.OnViewChanged(v));
        if (!_selection.IsEmpty) PostSelection();
    }

    // =====================================================================================
    // Rendering loop
    // =====================================================================================

    /// <summary>Coalesces render requests: runs once after all pending input has been processed.</summary>
    private void RequestRender()
    {
        if (_renderQueued) return;
        _renderQueued = true;
        _ink.TryEnqueue(DispatcherQueuePriority.Low, RenderFrame);
    }

    private void RenderNow()
    {
        _renderQueued = false;
        RenderFrame();
    }

    private void RenderFrame()
    {
        _renderQueued = false;
        if (_renderer is null || _page is null) return;
        if (_eraseQueue.Count > 0) FlushErase(); // the eraser sweep gathered since the last frame, applied as one edit
        long now = Stopwatch.GetTimestamp();
        if (_inertia.IsActive)
        {
            float dt = _lastAnimTicks == 0 ? 1f / 60 : (float)((now - _lastAnimTicks) / (double)Stopwatch.Frequency);
            if (_inertia.Step(dt, out float dx, out float dy)) _view.Pan(dx, dy);
            if (!_inertia.IsActive) ScheduleSettle();
            PostViewIfEditing();
        }
        _lastAnimTicks = now;
        _renderer.View = _view.State;
        _renderer.ZoomGestureActive = _gestures.IsManipulating;
        bool more = _renderer.Render();
        LastFrameWidth = _renderer.Width;

        long presented = Stopwatch.GetTimestamp();
        if (_pendingInputTicks != 0)
        {
            double ms = (presented - _pendingInputTicks) * 1000.0 / Stopwatch.Frequency;
            PerfCounters.Instance.InkInputToPresent.Record(ms);
            PerfCounters.Instance.LastInkLatencyMs = ms;
            NibEventSource.Log.InkInputToPresent(ms * 1000);
            _pendingInputTicks = 0;
        }
        bool animating = _inertia.IsActive || _gestures.IsManipulating;
        if (animating) PerfCounters.Instance.PanFrameInterval.Record(PerfCounters.Instance.LastFrameMs);
        FramePresented?.Invoke((presented - now) * 1000.0 / Stopwatch.Frequency);
        if (more || _inertia.IsActive) RequestRender();
        else if (!animating && _mode == Mode.None) _renderer.MarkIdle();
    }

    private void MarkInput()
    {
        if (_pendingInputTicks == 0) _pendingInputTicks = Stopwatch.GetTimestamp();
    }

    private void ScheduleSettle()
    {
        _settleTimer?.Stop();
        _settleTimer?.Start();
    }

    /// <summary>After a zoom gesture, snap to a tile zoom level so tiles are pixel-exact and crisp.</summary>
    private void SettleZoom()
    {
        if (_gestures.IsManipulating || _inertia.IsActive) return;
        SnapZoomAround(_w / 2, _h / 2);
        RequestRender();
    }

    private void SnapZoomAround(float cx, float cy)
    {
        float snapped = Nib.Core.Rendering.TileMath.SnapZoom(_view.State.Zoom, _scale);
        if (MathF.Abs(snapped - _view.State.Zoom) > 1e-5f) _view.ZoomAt(snapped / _view.State.Zoom, cx, cy);
    }

    public void Dispose()
    {
        try { FlushAndWait(TimeSpan.FromSeconds(3)); } catch { }
        _ink.TryEnqueue(() =>
        {
            _perf?.Configure(false, Array.Empty<uint>()); // drop the refresh-rate request
            _predictor?.Dispose();
            _renderer?.Dispose();
        });
        _ = _controller.ShutdownQueueAsync();
    }
}

internal static class HostExtensions
{
    /// <summary>Invokes a host callback on the UI thread.</summary>
    public static void Post(this IInkEngineHost host, DispatcherQueue ui, Action<IInkEngineHost> action) => ui.TryEnqueue(() => action(host));
}
