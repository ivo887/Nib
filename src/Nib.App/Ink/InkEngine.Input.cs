using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using Microsoft.UI.Input;
using Nib.Core.Diagnostics;
using Nib.Core.Editing;
using Nib.Core.Geometry;
using Nib.Core.Ink;
using Nib.Core.Model;
using Windows.System;

namespace Nib.App.Ink;

/// <summary>Pointer handling. Every method here runs on the ink thread.</summary>
internal sealed partial class InkEngine
{
    private enum Mode { None, Ink, Erase, Lasso, MoveSelection, ScaleSelection, RotateSelection, InsertSpace, Gesture, MousePan, Checkbox }
    private enum SelectionHit { None, Move, Scale, Rotate }

    private Mode _mode;
    private uint _activePointer;
    private bool _activeIsPen, _activeIsTouch;
    private ulong _lastSampleTs;
    private Vector2 _pressScreen, _pressPage, _lastScreen, _lastEraserPage;
    private float _eraserRadiusPage;
    private EraserMode _eraserMode;
    private Matrix3x2 _dragTransform = Matrix3x2.Identity;
    private Vector2 _selAnchor;
    private (long Id, int Paragraph) _pendingCheck;
    private bool _insertDecided, _insertVertical;
    private long _touchInkStartMs;
    private long _lastTouchTapMs;
    private Vector2 _lastTouchTapPos;
    private long _touchSessionStartMs;
    private Vector2 _touchSessionStart;
    private int _touchSessionMax;
    private readonly HashSet<uint> _rejectedTouches = new();
    private readonly HashSet<uint> _gestureTouches = new();
    private readonly List<Stroke> _touched = new(16);
    private readonly List<Stroke> _removed = new(16);
    private readonly List<Stroke> _added = new(16);
    private PointerPoint[] _sampleBuf = new PointerPoint[64];
    private readonly WetInk _wet = new();

    private static long NowMs => (long)PerfCounters.NowMs;

    // =====================================================================================
    // Pointer events
    // =====================================================================================

    private void OnPointerPressed(InputPointerSource sender, PointerEventArgs args)
    {
        if (_page is null || _renderer is null) return;
        var pt = args.CurrentPoint;
        var props = pt.Properties;
        var screen = new Vector2((float)pt.Position.X, (float)pt.Position.Y);
        var pagePt = _view.State.ToPage(screen.X, screen.Y);
        long now = NowMs;
        _inertia.Stop();
        args.Handled = true;

        switch (pt.PointerDeviceType)
        {
            case PointerDeviceType.Pen:
            {
                _palm.PenSeen(now);
                _perf?.PenDown();
                RefreshPredictionFromDisplay();
                if (_gestures.ContactCount > 0 || _mode == Mode.Gesture)
                {
                    _gestures.CancelAll();
                    _gestureTouches.Clear();
                    if (_mode == Mode.Gesture) _mode = Mode.None;
                }
                if (_mode == Mode.Ink && _activeIsTouch) CancelActiveInput(); // the pen always wins over a finger
                if (_mode != Mode.None) return;
                var kind = Tool.Kind;
                if (props.IsEraser || props.IsInverted) kind = ToolKind.Eraser;
                else if (props.IsBarrelButtonPressed)
                {
                    switch (Tool.Barrel)
                    {
                        case BarrelButtonAction.Eraser: kind = ToolKind.Eraser; break;
                        case BarrelButtonAction.Lasso: kind = ToolKind.Lasso; break;
                        case BarrelButtonAction.RightClick: RequestContextMenu(screen); return;
                    }
                }
                BeginTool(kind, pt, screen, pagePt, isPen: true, isTouch: false);
                break;
            }
            case PointerDeviceType.Touch:
            {
                var cr = props.ContactRect;
                if (_palm.ShouldRejectTouch(now, (float)cr.Width, (float)cr.Height) || (_mode != Mode.None && _mode != Mode.Gesture && !(_mode == Mode.Ink && _activeIsTouch)))
                {
                    _rejectedTouches.Add(pt.PointerId);
                    return;
                }
                if (_mode == Mode.Ink && _activeIsTouch)
                {
                    // A second finger right after the first: it was the start of a pan/zoom, not a stroke.
                    if (now - _touchInkStartMs < 180 || _processor.Count < 6)
                    {
                        uint first = _activePointer;
                        var firstPos = _lastScreen;
                        CancelActiveInput();
                        StartGesture(first, firstPos, now);
                    }
                    else
                    {
                        _rejectedTouches.Add(pt.PointerId);
                        return;
                    }
                }
                if (_mode == Mode.None && Tool.TouchDraws && _gestures.ContactCount == 0 &&
                    Tool.Kind is ToolKind.Pen or ToolKind.Highlighter or ToolKind.Eraser or ToolKind.Lasso)
                {
                    _touchInkStartMs = now;
                    BeginTool(Tool.Kind, pt, screen, pagePt, isPen: false, isTouch: true);
                    break;
                }
                StartGesture(pt.PointerId, screen, now);
                break;
            }
            default: // mouse
            {
                if (props.IsRightButtonPressed) { RequestContextMenu(screen); return; }
                if (props.IsMiddleButtonPressed)
                {
                    _mode = Mode.MousePan;
                    _activePointer = pt.PointerId;
                    _lastScreen = screen;
                    return;
                }
                if (_mode != Mode.None) return;
                BeginTool(Tool.Kind, pt, screen, pagePt, isPen: false, isTouch: false);
                break;
            }
        }
        MarkInput();
        RequestRender();
    }

    private void StartGesture(uint id, Vector2 screen, long now)
    {
        if (_gestureTouches.Count == 0)
        {
            _touchSessionStartMs = now;
            _touchSessionStart = screen;
            _touchSessionMax = 0;
        }
        _mode = Mode.Gesture;
        _gestureTouches.Add(id);
        _touchSessionMax = Math.Max(_touchSessionMax, _gestureTouches.Count);
        _gestures.OneFingerPans = !Tool.TouchDraws;
        _gestures.Down(id, screen.X, screen.Y, now);
    }

    private void OnPointerMoved(InputPointerSource sender, PointerEventArgs args)
    {
        if (_page is null || _renderer is null) return;
        var pt = args.CurrentPoint;
        var screen = new Vector2((float)pt.Position.X, (float)pt.Position.Y);
        args.Handled = true;

        if (pt.PointerDeviceType == PointerDeviceType.Pen && !pt.IsInContact)
        {
            _palm.PenSeen(NowMs);
            _perf?.PenNear();
            if (_mode == Mode.None) UpdateHover(pt, screen);
            return;
        }
        if (pt.PointerDeviceType == PointerDeviceType.Touch && _gestureTouches.Contains(pt.PointerId))
        {
            _gestures.Move(pt.PointerId, screen.X, screen.Y, NowMs);
            return;
        }
        if (pt.PointerDeviceType == PointerDeviceType.Mouse && _mode == Mode.None)
        {
            if (Tool.Kind is ToolKind.Pen or ToolKind.Highlighter or ToolKind.Eraser) UpdateHover(pt, screen);
            return;
        }
        if (pt.PointerId != _activePointer || _mode == Mode.None) return;

        var pagePt = _view.State.ToPage(screen.X, screen.Y);
        switch (_mode)
        {
            case Mode.Ink:
                ProcessSamples(args);
                if (_snapped) _renderer.Predicted.Clear(); else UpdatePrediction(pt);
                if (Tool.ShapeSnap) TrackHold(pagePt);
                if (Tool.Pointer?.ShowWhileDrawing == true) UpdateHover(pt, screen);
                MarkInput();
                break;
            case Mode.Erase:
                QueueErase(pagePt);
                MarkInput();
                break;
            case Mode.Lasso:
                if (_renderer.Lasso.Count == 0 || Vector2.Distance(_renderer.Lasso[^1], pagePt) * _view.State.Zoom > 2f) _renderer.Lasso.Add(pagePt);
                break;
            case Mode.MoveSelection:
            case Mode.ScaleSelection:
            case Mode.RotateSelection:
                DragSelection(pagePt);
                break;
            case Mode.InsertSpace:
                DragInsertSpace(pagePt);
                break;
            case Mode.MousePan:
                _view.Pan(screen.X - _lastScreen.X, screen.Y - _lastScreen.Y);
                PostViewIfEditing();
                break;
            case Mode.Checkbox:
                if (Vector2.Distance(screen, _pressScreen) > 6) _mode = Mode.None;
                break;
        }
        _lastScreen = screen;
        RequestRender();
    }

    private void OnPointerReleased(InputPointerSource sender, PointerEventArgs args)
    {
        if (_page is null || _renderer is null) return;
        var pt = args.CurrentPoint;
        var screen = new Vector2((float)pt.Position.X, (float)pt.Position.Y);
        long now = NowMs;
        args.Handled = true;

        if (pt.PointerDeviceType == PointerDeviceType.Touch)
        {
            if (_rejectedTouches.Remove(pt.PointerId)) return;
            if (_gestureTouches.Remove(pt.PointerId))
            {
                bool wasManipulating = _gestures.IsManipulating;
                _gestures.Up(pt.PointerId, screen.X, screen.Y, now);
                if (_gestureTouches.Count == 0)
                {
                    _mode = Mode.None;
                    if (!wasManipulating && _touchSessionMax == 1 && now - _touchSessionStartMs < 300 && Vector2.Distance(screen, _touchSessionStart) < 10)
                        OnSingleTap(screen, now);
                    if (!_inertia.IsActive) ScheduleSettle();
                }
                RequestRender();
                return;
            }
        }
        if (pt.PointerDeviceType == PointerDeviceType.Pen) _palm.PenSeen(now);
        if (pt.PointerId != _activePointer) return;

        var pagePt = _view.State.ToPage(screen.X, screen.Y);
        switch (_mode)
        {
            case Mode.Ink:
                ProcessSamples(args);
                CommitInk();
                MarkInput();
                break;
            case Mode.Erase:
                QueueErase(pagePt);
                FlushErase();
                _page.Undo.EndGroup();
                break;
            case Mode.Lasso:
                FinishLasso();
                break;
            case Mode.MoveSelection:
            case Mode.ScaleSelection:
            case Mode.RotateSelection:
                CommitSelectionDrag();
                break;
            case Mode.InsertSpace:
                CommitInsertSpace();
                break;
            case Mode.Checkbox:
                ToggleCheckbox();
                break;
            case Mode.MousePan:
                ScheduleSettle();
                break;
        }
        _mode = Mode.None;
        if (pt.PointerDeviceType == PointerDeviceType.Pen) UpdateHover(pt, screen);
        RequestRender();
    }

    private void OnPointerEntered(InputPointerSource sender, PointerEventArgs args)
    {
        if (args.CurrentPoint.PointerDeviceType != PointerDeviceType.Touch) _host.Post(_ui, h => h.OnPageHoverChanged(true));
        if (args.CurrentPoint.PointerDeviceType == PointerDeviceType.Pen)
        {
            _palm.PenSeen(NowMs);
            _perf?.PenNear(); // before the nib touches: high refresh and fast cores are ready for the first stroke
            // A pen arriving cancels any touch in progress (it is almost certainly the palm).
            if (_mode == Mode.Gesture) { _gestures.CancelAll(); _gestureTouches.Clear(); _mode = Mode.None; }
        }
    }

    private void OnPointerExited(InputPointerSource sender, PointerEventArgs args)
    {
        var pt = args.CurrentPoint;
        if (pt.PointerDeviceType != PointerDeviceType.Touch) _host.Post(_ui, h => h.OnPageHoverChanged(false));
        if (pt.PointerDeviceType == PointerDeviceType.Pen && !pt.IsInContact)
        {
            _palm.PenLeft(NowMs);
            _perf?.PenAway();
            if (_renderer is not null && _renderer.Hover is not null) { _renderer.Hover = null; RequestRender(); }
        }
        else if (pt.PointerDeviceType == PointerDeviceType.Mouse && _mode == Mode.None && _renderer is not null)
        {
            _renderer.Hover = null;
            RequestRender();
        }
    }

    private void OnPointerCaptureLost(InputPointerSource sender, PointerEventArgs args)
    {
        var id = args.CurrentPoint.PointerId;
        if (_gestureTouches.Remove(id))
        {
            if (_gestureTouches.Count == 0) { _gestures.CancelAll(); _mode = Mode.None; }
            return;
        }
        if (id != _activePointer) return;
        if (_mode == Mode.Ink) CommitInk(); // never lose ink that was already written
        else CancelActiveInput();
        _mode = Mode.None;
        RequestRender();
    }

    private void OnPointerWheelChanged(InputPointerSource sender, PointerEventArgs args)
    {
        var pt = args.CurrentPoint;
        int delta = pt.Properties.MouseWheelDelta;
        var screen = new Vector2((float)pt.Position.X, (float)pt.Position.Y);
        bool ctrl = (args.KeyModifiers & VirtualKeyModifiers.Control) != 0;
        bool shift = (args.KeyModifiers & VirtualKeyModifiers.Shift) != 0;
        if (ctrl)
        {
            _view.ZoomAt(MathF.Pow(1.0015f, delta), screen.X, screen.Y);
            ScheduleSettle();
        }
        else if (shift || pt.Properties.IsHorizontalMouseWheel) _view.Pan(pt.Properties.IsHorizontalMouseWheel ? -delta * 0.5f : delta * 0.5f, 0);
        else _view.Pan(0, delta * 0.5f);
        args.Handled = true;
        PostViewIfEditing();
        RequestRender();
    }

    // =====================================================================================
    // Tools
    // =====================================================================================

    private void BeginTool(ToolKind kind, PointerPoint pt, Vector2 screen, Vector2 pagePt, bool isPen, bool isTouch)
    {
        _activePointer = pt.PointerId;
        _activeIsPen = isPen;
        _activeIsTouch = isTouch;
        _pressScreen = _lastScreen = screen;
        _pressPage = pagePt;
        _lastPagePoint = pagePt;
        if (_renderer!.Hover is not null && kind != ToolKind.Eraser) _renderer.Hover = null;

        if (kind != ToolKind.Eraser && HitCheckbox(pagePt, out long tid, out int para))
        {
            _mode = Mode.Checkbox;
            _pendingCheck = (tid, para);
            return;
        }

        if (!_selection.IsEmpty)
        {
            var hit = HitSelection(screen);
            if (hit != SelectionHit.None)
            {
                BeginSelectionDrag(hit, pagePt);
                return;
            }
            ClearSelection();
        }

        switch (kind)
        {
            case ToolKind.Pen:
            case ToolKind.Highlighter:
                BeginInk(kind, pt, pagePt, isPen);
                break;
            case ToolKind.Eraser:
                BeginErase(pagePt);
                break;
            case ToolKind.Lasso:
                _mode = Mode.Lasso;
                _renderer.Lasso.Clear();
                _renderer.Lasso.Add(pagePt);
                break;
            case ToolKind.InsertSpace:
                _mode = Mode.InsertSpace;
                _insertDecided = false;
                _insertVertical = true;
                _renderer.InsertSpaceAt = pagePt.Y;
                _renderer.InsertSpaceVertical = true;
                _renderer.InsertSpaceDelta = 0;
                break;
            case ToolKind.Text:
                EditOrCreateTextAt(pagePt);
                break;
        }
    }

    private void OnToolKindChanged(ToolKind kind)
    {
        if (kind != ToolKind.Lasso) ClearSelection();
        if (_renderer is not null) _renderer.Hover = null;
        ApplyCursorShape(Tool);
        RequestRender();
    }

    /// <summary>The Windows cursor shape over the page (when it is visible at all: see MainWindow.UpdateSystemCursor).</summary>
    private void ApplyCursorShape(ToolState tool)
    {
        if (_input is null) return;
        var shape = tool.Kind == ToolKind.Text ? InputSystemCursorShape.IBeam
            : (tool.Pointer?.SystemCursor ?? SystemCursorMode.Auto) == SystemCursorMode.Arrow ? InputSystemCursorShape.Arrow
            : InputSystemCursorShape.Cross;
        _input.Cursor = InputSystemCursor.Create(shape);
    }

    // ---- ink ----

    private static readonly TiltSettings DefaultTilt = new();

    private RawSample ToRaw(PointerPoint p)
    {
        var page = _view.State.ToPage((float)p.Position.X, (float)p.Position.Y);
        var pr = p.Properties;
        bool pen = p.PointerDeviceType == PointerDeviceType.Pen;
        var (tx, ty) = pen ? (Tool.Tilt ?? DefaultTilt).Apply(pr.XTilt, pr.YTilt) : (0f, 0f);
        return new RawSample(page.X, page.Y, pen ? pr.Pressure : 0.5f, tx, ty, pen ? pr.Twist : -1, p.Timestamp);
    }

    private void BeginInk(ToolKind kind, PointerPoint pt, Vector2 pagePt, bool isPen)
    {
        var tool = Tool;
        var preset = kind == ToolKind.Highlighter ? tool.Highlighter : tool.Pen;
        var raw = ToRaw(pt);
        if (isPen && raw.Pressure > 0 && raw.Pressure != 0.5f) _penPressureKnown = true;
        var flags = InkProcessor.BaseFlagsFor(isPen, _penPressureKnown, raw.Pressure);
        _processor.Begin(raw, preset.Stabilizer, flags);
        _lastSampleTs = pt.Timestamp;
        _wet.Kind = preset.Kind;
        _wet.Color = preset.Color;
        _wet.Width = preset.Width;
        _wet.Dynamics = preset.Dynamics;
        _wet.Flags = _processor.Flags;
        _renderer!.BeginWet(_wet);
        _mode = Mode.Ink;
        // Pointer shows only while hovering unless the user wants it while drawing too.
        if (tool.Pointer?.ShowWhileDrawing != true) _renderer.Hover = null;
        _snapped = false; _snapFailed = false;
        if (tool.ShapeSnap)
        {
            _holdAnchor = pagePt;
            _lastMoveTicks = Stopwatch.GetTimestamp();
            _holdTimer?.Start();
        }
    }

    /// <summary>Feeds every hardware sample since the last event (coalesced points included), oldest first.</summary>
    private void ProcessSamples(PointerEventArgs args)
    {
        var points = args.GetIntermediatePoints();
        int n = points.Count;
        if (n == 0) return;
        if (_sampleBuf.Length < n) _sampleBuf = new PointerPoint[n * 2];
        for (int i = 0; i < n; i++) _sampleBuf[i] = points[i];
        // Order is not guaranteed across sources: sort by timestamp (n ≤ 64, nearly sorted).
        for (int i = 1; i < n; i++)
        {
            var v = _sampleBuf[i];
            int j = i - 1;
            while (j >= 0 && _sampleBuf[j].Timestamp > v.Timestamp) { _sampleBuf[j + 1] = _sampleBuf[j]; j--; }
            _sampleBuf[j + 1] = v;
        }
        for (int i = 0; i < n; i++)
        {
            var p = _sampleBuf[i];
            if (p.Timestamp <= _lastSampleTs) continue;
            _lastSampleTs = p.Timestamp;
            _processor.Add(ToRaw(p));
        }
        Array.Clear(_sampleBuf, 0, n);
        _wet.Flags = _processor.Flags;
    }

    private void UpdatePrediction(PointerPoint current)
    {
        var predicted = _renderer!.Predicted;
        predicted.Clear();
        if (_predictor is null || _predictionMs <= 0) return;
        var last = _processor.Last;
        var list = _predictor.GetPredictedPoints(current);
        if (list is null || list.Length == 0) return;
        predicted.Add(new Vector2(last.X, last.Y));
        foreach (var p in list) predicted.Add(_view.State.ToPage((float)p.Position.X, (float)p.Position.Y));
    }

    // ---- shape snapping: hold the pen still at the end of a stroke ----

    private long _lastMoveTicks;
    private Vector2 _holdAnchor, _snapStart, _snapEnd;
    private bool _snapped, _snapFailed;
    private ShapeKind _snapKind;

    /// <summary>Called on every move while inking: restarts the hold clock when the pen really moved, or drags a snapped line's end.</summary>
    private void TrackHold(Vector2 pagePt)
    {
        if (_snapped)
        {
            if (_snapKind == ShapeKind.Line) FollowSnappedLine(pagePt);
            return;
        }
        if (Vector2.Distance(pagePt, _holdAnchor) * _view.State.Zoom > 3f)
        {
            _holdAnchor = pagePt;
            _lastMoveTicks = Stopwatch.GetTimestamp();
            _snapFailed = false;
        }
    }

    private void OnHoldTick()
    {
        if (_mode != Mode.Ink || !_processor.IsActive) { _holdTimer?.Stop(); return; }
        if (_snapped || _snapFailed) return;
        var tool = Tool;
        if (!tool.ShapeSnap) return;
        double restedMs = (Stopwatch.GetTimestamp() - _lastMoveTicks) * 1000.0 / Stopwatch.Frequency;
        if (restedMs < Math.Clamp(tool.ShapeHoldMs, 250, 2500)) return;
        TrySnap(tool);
    }

    private void TrySnap(ToolState tool)
    {
        var shape = ShapeRecognizer.Recognize(_processor.PointSpan, tool.SnapLineAngles);
        if (!shape.IsShape) { _snapFailed = true; return; }
        var ink = ShapeRecognizer.ToInkPoints(shape, _processor.PointSpan);
        _processor.ReplacePoints(ink);
        _snapped = true;
        _snapKind = shape.Kind;
        _snapStart = shape.Vertices[0];
        _snapEnd = shape.Vertices[^1];
        RedrawWetStroke();
        string name = shape.Kind switch
        {
            ShapeKind.Line => "Line",
            ShapeKind.Circle => "Circle",
            ShapeKind.Ellipse => "Ellipse",
            ShapeKind.Triangle => "Triangle",
            ShapeKind.Square => "Square",
            ShapeKind.Rectangle => "Rectangle",
            _ => "Shape",
        };
        _host.Post(_ui, h => h.OnStatus(name));
    }

    /// <summary>A snapped line keeps following the pen (with angle snapping) until it lifts, so its length can still be adjusted.</summary>
    private void FollowSnappedLine(Vector2 pagePt)
    {
        var end = ShapeRecognizer.SnapEndpoint(_snapStart, pagePt, Tool.SnapLineAngles);
        if (Vector2.DistanceSquared(end, _snapEnd) < 0.25f) return;
        _snapEnd = end;
        var line = new ShapeResult(ShapeKind.Line, new[] { _snapStart, end }, false);
        _processor.ReplacePoints(ShapeRecognizer.ToInkPoints(line, _processor.PointSpan));
        RedrawWetStroke();
    }

    /// <summary>The wet layer draws incrementally; after the points were replaced it has to start over.</summary>
    private void RedrawWetStroke()
    {
        _renderer!.Predicted.Clear();
        _renderer.BeginWet(_wet);
        RequestRender();
    }

    private void CommitInk()
    {
        _holdTimer?.Stop();
        _snapped = false;
        if (_page is null || _renderer is null || !_processor.IsActive) return;
        var pts = _processor.End();
        var stroke = new Stroke(IdGenerator.Next(), _page.NextZ(), _wet.Kind, _wet.Color, _wet.Width, _wet.Dynamics, _processor.Flags, pts);
        _renderer.EndWet();
        // Tiles receive the stroke incrementally in the same frame the wet layer disappears: no flicker.
        _page.Execute(StrokesCommand.Add(stroke));
        _lastPagePoint = new Vector2(pts[^1].X, pts[^1].Y + 24);
    }

    // ---- erasers ----

    private void BeginErase(Vector2 pagePt)
    {
        _mode = Mode.Erase;
        _eraserMode = Tool.EraserMode;
        _eraserRadiusPage = Tool.EraserSizeDips * 0.5f / _view.State.Zoom;
        _lastEraserPage = pagePt;
        _page!.Undo.BeginGroup("Erase");
        _renderer!.HoverEraser = true;
        _renderer.EraserRadiusDips = Tool.EraserSizeDips * 0.5f;
        _eraseQueue.Clear();
        QueueErase(pagePt);
    }

    // ---- erasing: positions are queued as they arrive and applied once per frame ----
    //
    // A pen reports 133-240 times a second. Cutting and repainting for every report made the pixel eraser stutter on
    // slower tablets, so each report only queues a position; the frame applies the whole sweep as ONE edit (one
    // observer notification, one undo step) and repaints only the area the eraser passed over. Every queued position
    // is still swept in order, so fast strokes cannot skip over ink.

    private readonly List<Vector2> _eraseQueue = new(32);

    private void QueueErase(Vector2 pagePt)
    {
        _eraseQueue.Add(pagePt);
        _renderer!.Hover = pagePt;
        RequestRender();
    }

    private void FlushErase()
    {
        if (_eraseQueue.Count == 0 || _page is null) return;
        var page = _page;
        page.BeginBatch();
        RectF dirty = RectF.Empty;
        bool any = false;
        for (int q = 0; q < _eraseQueue.Count; q++)
        {
            var pagePt = _eraseQueue[q];
            _touched.Clear();
            _eraser.FindTouched(page, _lastEraserPage.X, _lastEraserPage.Y, pagePt.X, pagePt.Y, _eraserRadiusPage, _touched);
            if (_touched.Count > 0)
            {
                float extent = 0f;
                for (int i = 0; i < _touched.Count; i++) extent = MathF.Max(extent, NibModel.MaxExtent(_touched[i].Kind, _touched[i].Width));
                var swept = new RectF(MathF.Min(_lastEraserPage.X, pagePt.X), MathF.Min(_lastEraserPage.Y, pagePt.Y),
                    MathF.Max(_lastEraserPage.X, pagePt.X), MathF.Max(_lastEraserPage.Y, pagePt.Y)).Inflate(_eraserRadiusPage + extent + 2f);
                dirty = any ? dirty.Union(swept) : swept;
                any = true;

                if (_eraserMode == EraserMode.Stroke)
                {
                    new StrokesCommand("Erase", _touched.ToArray(), null).Do(page);
                }
                else
                {
                    _removed.Clear();
                    _added.Clear();
                    foreach (var s in _touched)
                    {
                        var pieces = _eraser.Cut(s, _lastEraserPage.X, _lastEraserPage.Y, pagePt.X, pagePt.Y, _eraserRadiusPage);
                        if (pieces is null) continue;
                        _removed.Add(s);
                        _added.AddRange(pieces);
                    }
                    if (_removed.Count > 0) new StrokesCommand("Erase", _removed.ToArray(), _added.ToArray()).Do(page);
                }
            }
            _lastEraserPage = pagePt;
        }
        _eraseQueue.Clear();
        var (removed, added) = page.EndBatch(any ? dirty : null);
        if (removed.Length > 0 || added.Length > 0) page.Undo.Push(new StrokesCommand("Erase", removed, added));
    }

    // ---- lasso and selection ----

    private void FinishLasso()
    {
        var lasso = _renderer!.Lasso;
        if (lasso.Count >= 3) SelectionOps.Select(_page!, lasso.ToArray(), _selection);
        lasso.Clear();
        if (_selection.IsEmpty) return;
        _renderer.SelectionBounds = _selection.Bounds;
        _renderer.SelectionTransform = Matrix3x2.Identity;
        _renderer.SelectionStrokes = _selection.Strokes;
        PostSelection();
    }

    private void PostSelection()
    {
        if (_selection.IsEmpty) { _host.Post(_ui, h => h.OnSelectionChanged(null)); return; }
        var b = _selection.Bounds;
        var tl = _view.State.ToScreen(b.Left, b.Top);
        var br = _view.State.ToScreen(b.Right, b.Bottom);
        var rect = new Windows.Foundation.Rect(tl.X, tl.Y, Math.Max(1, br.X - tl.X), Math.Max(1, br.Y - tl.Y));
        _host.Post(_ui, h => h.OnSelectionChanged(rect));
    }

    private void ClearSelection()
    {
        if (_selection.IsEmpty && (_renderer is null || _renderer.SelectionBounds.IsEmpty)) return;
        _selection.Clear();
        if (_renderer is not null)
        {
            _renderer.UnhideAll();
            _renderer.SelectionBounds = RectF.Empty;
            _renderer.SelectionStrokes = null;
            _renderer.SelectionTransform = Matrix3x2.Identity;
        }
        _host.Post(_ui, h => h.OnSelectionChanged(null));
    }

    private SelectionHit HitSelection(Vector2 screen)
    {
        var b = _selection.Bounds;
        var tl = _view.State.ToScreen(b.Left, b.Top);
        var br = _view.State.ToScreen(b.Right, b.Bottom);
        var r = new Windows.Foundation.Rect(tl.X - 6, tl.Y - 6, br.X - tl.X + 12, br.Y - tl.Y + 12);
        foreach (var h in PageRenderer.SelectionHandles(r))
            if (Vector2.Distance(h, screen) <= 14) return SelectionHit.Scale;
        var rot = new Vector2((float)(r.X + r.Width / 2), (float)r.Y - 22);
        if (Vector2.Distance(rot, screen) <= 14) return SelectionHit.Rotate;
        if (r.Contains(new Windows.Foundation.Point(screen.X, screen.Y))) return SelectionHit.Move;
        return SelectionHit.None;
    }

    private void BeginSelectionDrag(SelectionHit hit, Vector2 pagePt)
    {
        var b = _selection.Bounds;
        _mode = hit switch { SelectionHit.Scale => Mode.ScaleSelection, SelectionHit.Rotate => Mode.RotateSelection, _ => Mode.MoveSelection };
        if (_mode == Mode.ScaleSelection)
        {
            // Anchor is the corner opposite the grabbed handle.
            float ax = MathF.Abs(pagePt.X - b.Left) < MathF.Abs(pagePt.X - b.Right) ? b.Right : b.Left;
            float ay = MathF.Abs(pagePt.Y - b.Top) < MathF.Abs(pagePt.Y - b.Bottom) ? b.Bottom : b.Top;
            _selAnchor = new Vector2(ax, ay);
        }
        else _selAnchor = new Vector2(b.CenterX, b.CenterY);
        _dragTransform = Matrix3x2.Identity;
        _renderer!.Hide(_selection.Strokes);
        _renderer.SelectionStrokes = _selection.Strokes;
    }

    private void DragSelection(Vector2 pagePt)
    {
        switch (_mode)
        {
            case Mode.MoveSelection:
                _dragTransform = Matrix3x2.CreateTranslation(pagePt - _pressPage);
                break;
            case Mode.ScaleSelection:
            {
                float d0 = Vector2.Distance(_pressPage, _selAnchor), d1 = Vector2.Distance(pagePt, _selAnchor);
                float s = d0 > 1e-3f ? Math.Clamp(d1 / d0, 0.05f, 20f) : 1f;
                _dragTransform = SelectionOps.ScaleAround(_selAnchor, s);
                break;
            }
            case Mode.RotateSelection:
            {
                var a = _pressPage - _selAnchor;
                var c = pagePt - _selAnchor;
                float angle = MathF.Atan2(c.Y, c.X) - MathF.Atan2(a.Y, a.X);
                _dragTransform = SelectionOps.RotateAround(_selAnchor, angle);
                break;
            }
        }
        _renderer!.SelectionTransform = _dragTransform;
    }

    private void CommitSelectionDrag()
    {
        var page = _page!;
        string name = _mode switch { Mode.ScaleSelection => "Resize", Mode.RotateSelection => "Rotate", _ => "Move" };
        _renderer!.UnhideAll();
        _renderer.SelectionTransform = Matrix3x2.Identity;
        if (!_dragTransform.IsIdentity)
        {
            page.Execute(SelectionOps.Transform(_selection, _dragTransform, name));
            _selection.Refresh(page);
        }
        _renderer.SelectionStrokes = _selection.Strokes;
        _renderer.SelectionBounds = _selection.Bounds;
        PostSelection();
    }

    // ---- insert space ----

    private void DragInsertSpace(Vector2 pagePt)
    {
        var d = pagePt - _pressPage;
        if (!_insertDecided && d.Length() * _view.State.Zoom > 6)
        {
            _insertDecided = true;
            _insertVertical = MathF.Abs(d.Y) >= MathF.Abs(d.X);
            _renderer!.InsertSpaceVertical = _insertVertical;
            _renderer.InsertSpaceAt = _insertVertical ? _pressPage.Y : _pressPage.X;
        }
        _renderer!.InsertSpaceDelta = _insertVertical ? d.Y : d.X;
    }

    private void CommitInsertSpace()
    {
        var at = _renderer!.InsertSpaceAt;
        float delta = _renderer.InsertSpaceDelta;
        _renderer.InsertSpaceAt = null;
        _renderer.InsertSpaceDelta = 0;
        if (!_insertDecided || at is null) return;
        var cmd = SelectionOps.InsertSpace(_page!, at.Value, delta, _insertVertical);
        if (cmd is not null) _page!.Execute(cmd);
    }

    // ---- text, tags, taps ----

    private bool HitText(Vector2 pagePt, out TextContainer? hit)
    {
        hit = null;
        var list = new List<TextContainer>(4);
        _page!.TextIndex.Query(RectF.FromCenter(pagePt.X, pagePt.Y, 2, 2), list);
        long bestZ = long.MinValue;
        foreach (var t in list)
            if (t.Bounds.Contains(pagePt.X, pagePt.Y) && t.Z > bestZ) { hit = t; bestZ = t.Z; }
        return hit is not null;
    }

    private bool HitCheckbox(Vector2 pagePt, out long textId, out int paragraph)
    {
        textId = 0;
        paragraph = -1;
        var list = new List<TextContainer>(4);
        _page!.TextIndex.Query(RectF.FromCenter(pagePt.X, pagePt.Y, 4, 4), list);
        foreach (var t in list)
        {
            var visual = _renderer!.GetTextVisual(t);
            if (visual.HitTag(pagePt.X, pagePt.Y, out int para, out int tagId) && _tags.TryGetValue(tagId, out var def) && def.IsCheckbox)
            {
                textId = t.Id;
                paragraph = para;
                return true;
            }
        }
        return false;
    }

    private void ToggleCheckbox()
    {
        var c = _page!.FindText(_pendingCheck.Id);
        if (c is null || _pendingCheck.Paragraph >= c.Text.Paragraphs.Length) return;
        bool now = c.Text.Paragraphs[_pendingCheck.Paragraph].Checked;
        var cmd = TagOps.SetChecked(c, _pendingCheck.Paragraph, !now);
        if (cmd is not null) _page.Execute(cmd);
    }

    private void EditOrCreateTextAt(Vector2 pagePt)
    {
        if (HitText(pagePt, out var existing))
        {
            BeginTextEdit(existing!, isNew: false, caretParagraph: ParagraphAt(existing!, pagePt.Y));
            return;
        }
        float lineHeight = TextStyle.Default.SizeUnits * 1.35f;
        var c = new TextContainer(IdGenerator.Next(), _page!.NextZ(), pagePt.X, pagePt.Y - lineHeight * 0.5f,
            DefaultTextWidth(pagePt.X), RichText.Empty);
        BeginTextEdit(c, isNew: true, caretParagraph: 0);
    }

    private int ParagraphAt(TextContainer c, float y)
    {
        var visual = _renderer!.GetTextVisual(c);
        int best = 0;
        foreach (var line in visual.Lines) if (line.Y <= y) best = line.Index;
        return best;
    }

    private void OnSingleTap(Vector2 screen, long now)
    {
        // Double-tap with a finger: edit the text box under it, or start typing a new one there.
        var pagePt = _view.State.ToPage(screen.X, screen.Y);
        _lastPagePoint = pagePt;
        if (HitCheckbox(pagePt, out long tid, out int para))
        {
            _pendingCheck = (tid, para);
            ToggleCheckbox();
            return;
        }
        if (now - _lastTouchTapMs < 400 && Vector2.Distance(screen, _lastTouchTapPos) < 24)
        {
            _lastTouchTapMs = 0;
            EditOrCreateTextAt(pagePt);
            return;
        }
        _lastTouchTapMs = now;
        _lastTouchTapPos = screen;
    }

    private void RequestContextMenu(Vector2 screen)
    {
        _lastPagePoint = _view.State.ToPage(screen.X, screen.Y);
        bool sel = !_selection.IsEmpty, paste = HasClipboard;
        var p = new Windows.Foundation.Point(screen.X, screen.Y);
        _host.Post(_ui, h => h.OnContextMenuRequested(p, sel, paste));
    }

    // ---- hover cursor ----

    private void UpdateHover(PointerPoint pt, Vector2 screen)
    {
        var tool = Tool;
        var pr = pt.Properties;
        bool eraser = tool.Kind == ToolKind.Eraser || pr.IsEraser || pr.IsInverted;
        var pagePt = _view.State.ToPage(screen.X, screen.Y);
        if (eraser)
        {
            _renderer!.Hover = pagePt;
            _renderer.HoverEraser = true;
            _renderer.EraserRadiusDips = tool.EraserSizeDips * 0.5f;
        }
        else if (tool.Kind is ToolKind.Pen or ToolKind.Highlighter)
        {
            var preset = tool.Kind == ToolKind.Highlighter ? tool.Highlighter : tool.Pen;
            bool pen = pt.PointerDeviceType == PointerDeviceType.Pen;
            var (hx, hy) = pen ? (tool.Tilt ?? DefaultTilt).Apply(pr.XTilt, pr.YTilt) : (0f, 0f);
            var flags = StrokeFlags.HasPressure | (pen && (hx != 0 || hy != 0) ? StrokeFlags.HasTilt : 0);
            var p = new InkPoint(pagePt.X, pagePt.Y, 0.6f, hx, hy, pen ? pr.Twist : -1);
            NibModel.Compute(preset.Kind, preset.Width, preset.Dynamics, flags, p, 0, out var f);
            _renderer!.Hover = pagePt;
            _renderer.HoverEraser = false;
            _renderer.HoverFootprint = f;
            _renderer.HoverColor = preset.Color;
        }
        else if (_renderer!.Hover is null) return;
        else _renderer.Hover = null;
        RequestRender();
    }

    // ---- gestures ----

    public void OnPan(float dx, float dy)
    {
        _view.Pan(dx, dy);
        RequestRender();
        PostViewIfEditing();
    }

    public void OnZoom(float factor, float centerX, float centerY)
    {
        _view.ZoomAt(factor, centerX, centerY);
        RequestRender();
        PostViewIfEditing();
    }

    public void OnPanEnd(float velocityX, float velocityY)
    {
        _inertia.Start(velocityX, velocityY);
        _lastAnimTicks = 0;
        if (!_inertia.IsActive) ScheduleSettle();
        RequestRender();
    }

    public void OnMultiFingerTap(int fingers)
    {
        if (_page is null) return;
        var t = Tool;
        if (fingers == 2 && t.TwoFingerUndo && _page.UndoLast()) _host.Post(_ui, h => h.OnStatus("Undo"));
        else if (fingers == 3 && t.ThreeFingerRedo && _page.RedoLast()) _host.Post(_ui, h => h.OnStatus("Redo"));
        ClearSelection();
        RequestRender();
    }

    private void CancelActiveInput()
    {
        switch (_mode)
        {
            case Mode.Ink:
                _holdTimer?.Stop();
                _snapped = false;
                _processor.Cancel();
                _renderer?.EndWet();
                break;
            case Mode.Erase:
                FlushErase();
                _page?.Undo.EndGroup();
                break;
            case Mode.Lasso:
                _renderer?.Lasso.Clear();
                break;
            case Mode.MoveSelection:
            case Mode.ScaleSelection:
            case Mode.RotateSelection:
                _renderer?.UnhideAll();
                if (_renderer is not null) _renderer.SelectionTransform = Matrix3x2.Identity;
                break;
            case Mode.InsertSpace:
                if (_renderer is not null) { _renderer.InsertSpaceAt = null; _renderer.InsertSpaceDelta = 0; }
                break;
        }
        _mode = Mode.None;
    }
}
