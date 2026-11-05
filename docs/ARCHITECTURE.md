# Nib architecture

Nib is a pen-first note-taking app for Windows. Responsiveness is the first requirement and a clean UI the
second, and most of the design follows from those two. The rule behind most of it is that **the UI thread
never touches ink or pixels**.

## 1. Stack, and where it deviates from the brief

| Brief asked for | Nib uses | Why |
|---|---|---|
| C# + WinUI 3, .NET 8+ | C# 14, WinUI 3 (Windows App SDK 2.6.5 stable), **.NET 10** | .NET 8 support ends on 10 Nov 2026. .NET 10 is the current LTS. |
| InkPresenter / InkCanvas for low-latency ink | **Own ink engine**: Win2D `CanvasSwapChain` in a `CanvasSwapChainPanel`, input from `SwapChainPanel.CreateCoreIndependentInputSource` on a dedicated thread, `PointerPredictor` for prediction | In November 2026, WinUI 3 has InkCanvas/InkPresenter only in the *experimental* Windows App SDK channel, which can't ship in a stable app. Also, InkPresenter's built-in renderer can't draw the pen models the brief wants (fountain nib angle from tilt, pencil that widens and lightens with tilt, chisel highlighter whose shape follows tilt). The engine uses the same pattern as the platform's low-latency path: pointer input on a background thread, drawn straight into a composition swap chain, never routed through XAML. |
| Win2D for backgrounds, shapes, export | Win2D for **all** on-screen rendering. A dependency-free vector **PDF writer** for export | The PDF writer keeps ink as real vector paths with per-point width and opacity, uses multiply blending for highlighter and pencil, and is Native-AOT safe. |
| SQLite + ISF or custom vector ink | SQLite through the OS's own `winsqlite3.dll`, plus the custom **NIB1** stroke format | Uses no NuGet package and adds no load time. NIB1 takes about 6 bytes per point with pressure and about 8 with tilt (measured on synthetic handwriting), keeps pressure, tilt, twist and timing per point, and stores the pen's dynamics with every stroke so a stroke looks the same forever. |
| (not specified) | Unpackaged, self-contained, **Native AOT** in Release | Native AOT is the biggest single cold-start win for WinUI 3. xcopy-deployable, and the perf runner can launch it directly. |

## 2. Projects

```
Nib.sln
├─ src/Nib.Core          net10.0, no dependencies, AOT-compatible. Everything testable without Windows:
│                        ink processing and geometry, page model, undo, eraser/lasso/insert-space math,
│                        gestures, palm rejection, view/zoom math, layout (work area, docking), tile math,
│                        backgrounds, NIB1 codec, JSON, PDF writer/exporter, perf counters and verdicts.
├─ src/Nib.Storage       net10.0. SQLite P/Invoke wrapper, NoteStore (schema, queries), StorageService
│                        (one writer thread + one reader thread), AutosaveTracker.
├─ src/Nib.App           WinUI 3 shell (AssemblyName "Nib"). Ink engine, Win2D renderers, text editor
│                        overlay, toolbar, sidebar, dialogs, window/work-area manager, diagnostics.
├─ tests/Nib.Core.Tests  xUnit: 127 tests incl. CPU budget tests for the hot paths.
└─ tests/Nib.PerfRunner  Launches the AOT app, injects synthetic pen/touch/keys, reads the app's
                         measurements and fails the run if any target is missed.
```

### Main classes

| Area | Class | Role |
|---|---|---|
| Ink input | `InkEngine` (+ `InkEngine.Input`) | Owns the ink thread: pointer events, tool dispatch, live page, undo, saving, render scheduling. |
| | `InkProcessor` | Raw samples → `InkPoint`s: One-Euro stabilizer, pressure curve, speed fallback. Predicted positions from `PointerPredictor` are never added to the stroke: the renderer draws them as a tail that is replaced each frame. |
| | `PalmRejection`, `TouchGestures` | Touch is ignored while the pen is in range and for 450 ms after it leaves. Contacts > 34 DIP count as palms. Two-finger pan/pinch, 2-/3-finger taps, inertia. |
| Geometry | `StrokeGeometryBuilder`, `NibModel` | Points → filled outline per pen model (see §5). Output goes through `IStrokeRunSink`, so screen (Win2D) and PDF share one implementation. |
| Rendering | `PageRenderer`, `TileCache`, `Win2DRunSink`, `TextVisual` | Tiles, wet layer, overlays, text layout. |
| Model | `Page`, `Stroke`, `TextContainer`, `RichText`, `PageLayout`, `BackgroundSpec`, `TagDefinition` | Strokes, text and layouts are immutable, so an edit creates a new instance with the same id, and autosave and export threads can hold them without copying. `Page` is the mutable container, and only the ink thread changes it. |
| Editing | `UndoStack`, `StrokeEraser`, `SelectionOps`, `TagOps` | Unlimited per-page undo; stroke and pixel erase; lasso transform/recolor/copy; tags. |
| Spatial | `SpatialGrid` | Uniform grid over page units. Eraser, lasso, hit tests and tile queries are O(nearby strokes). |
| Storage | `NoteStore`, `StorageService`, `AutosaveTracker` | Schema, incremental saves, background reads. |
| Shell | `MainWindow` (+ `.Notebooks`, `.Dialogs`), `Toolbar`, `ToolFlyouts`, `Sidebar`, `TextEditorOverlay` | XAML UI. |
| Window | `WindowManager`, `WindowLayout` | Work area, auto-hide taskbar strip, touch keyboard occlusion, per-monitor placement, focus mode, docking. |
| Export | `PdfWriter`, `PdfExporter`, `PageBreaker`, `GlyphOutliner` | Vector PDF with smart page breaks. Text is written as glyph outlines. |
| Diagnostics | `NibEventSource`, `PerfCounters`, `PerfVerdict`, `UiStallWatchdog`, `DebugOverlay`, `PerfHarness` | ETW events, histograms, pass/fail logic, the hidden overlay, and the harness for automated runs. |

## 3. Threading model

```
 UI thread (XAML)                     Ink thread (DispatcherQueueController, boosted)
 ───────────────────────              ─────────────────────────────────────────────────
 toolbar, sidebar, dialogs            InputPointerSource (pen + touch + mouse) on the swap chain panel
 RichEditBox while typing             InkProcessor → wet stroke → StrokeGeometryBuilder
 swaps ToolState (immutable) ──────►  reads ToolState at pointer-down (next stroke)
 receives callbacks ◄────────────     live Page, UndoStack, SpatialGrid, TileCache
                                      render + Present (frame latency 1)
                                             │ PageChangeSet (immutable)
                                             ▼
 Storage writer thread  ◄──────────── debounced autosave; SQLite WAL writes
 Storage reader thread  ────────────► page loads and neighbour preloads (prev/next page)
 Thread pool                          PDF export
 Watchdog thread                      UiStallWatchdog: pings the UI thread and records stalls > 8 ms
```

* **The ink thread does all the ink work.** Input, model, undo and rendering run on one thread, so they
  need no locks and nothing has to be copied between threads. The thread runs at highest priority,
  registers with MMCSS ("Games") and opts out of EcoQoS, so battery saver doesn't stretch its timing.
  The GC runs in `SustainedLowLatency` mode.
* **Rendering is coalesced.** Pointer events only update state. One render is queued at low dispatcher
  priority, so it runs after every pending input event has been processed: one frame per batch of
  input, never one frame per point.
* **Tool changes apply on the next stroke.** The UI replaces an immutable `ToolState` reference. The
  ink thread reads it at pointer-down. Nothing waits on a dispatcher.
* **The UI thread only does UI work.** Disk I/O, page loading, export and settings writes run
  elsewhere. Settings are snapshotted to JSON on the caller thread and written in the background.

## 4. Rendering pipeline

Device: a D3D11 device whose DXGI maximum frame latency is set to **1** (the default is 3, which adds up
to two refresh periods of ink latency), wrapped with `CanvasDevice.CreateFromDirect3D11Device`. If that
fails, Nib falls back to a default Win2D device.

Each frame, in order:

1. **Tiles.** These are 512 × 512 px render targets keyed by (page, zoom level, x, y). Zoom levels are
   quantized to 1/32 octave so tiles are reused while pinching. During a zoom, missing tiles are drawn
   from a coarser level (key − 64) instead of leaving gaps. A tile holds the background, highlighter
   and ink, and text. Tiles go through LRU eviction with a budget derived from the viewport, and render
   targets are pooled, so panning allocates no GPU memory in steady state. Tiles are drawn aliased at
   pixel-snapped rectangles, so no seams appear.
2. **Committed strokes are baked into tiles.** A finished stroke is queued onto the tiles it touches as
   a pending add and drawn on top of them in the same frame the wet stroke disappears, so nothing
   flickers and no re-rasterization is needed. Erase and move operations invalidate only the affected
   tile rectangles. Only visible tiles are drawn, so a page with 10,000+ strokes costs no more per frame
   than a page with ten.
3. **Wet layer.** The active stroke is drawn incrementally from its last stable point, plus the
   predicted tail from `PointerPredictor` (15 ms by default, adjustable from 0 to 30 ms).
4. **Overlays.** Lasso path, selection handles, eraser circle, hover cursor (sized and shaped like the
   current nib), and the insert-space guide.
5. **Present.** When a zoom gesture ends, the view settles to the nearest tile zoom level, so tiles are
   shown pixel-exact and crisp.

Dark mode maps colors at draw time. Lightness is inverted and hue is kept, so black ink on white paper
becomes light ink on dark paper and colored ink stays recognizable. The stored colors and exports are
unchanged.

## 5. Pen models

All pens produce a union of positively oriented figures that are filled with the non-zero rule, so the
outline never self-intersects visibly and needs no polygon clipping:

| Pen | Footprint | Pressure | Tilt | Twist |
|---|---|---|---|---|
| Ballpoint | circle | width (with min-width ratio and curve) | none | none |
| Fountain | flat ellipse | width | nib angle follows tilt azimuth, so thick/thin strokes | used when reported |
| Pencil | circle | **opacity** and width | widens and lightens (shading with the side) | none |
| Highlighter | chisel rectangle | opacity (and a little width) | lays the chisel flatter: wider contact, angle follows azimuth | none |

* Round nibs are built from trapezoids plus joint and cap circles. Elliptical and rectangular nibs are
  built as swept convex footprints, using bitangent support points between consecutive samples.
* Pencil and highlighter are *multiply inks*. On screen they use `CanvasBlend.Min` with their over-white
  color. In PDF they use `/BM /Multiply` inside knockout transparency groups. Overlapping passes of the
  same stroke don't darken, and the highlighter shows *behind* ink without any z-ordering. Opacity is
  quantized to 16 levels, and runs of equal strength are emitted as one path.
* Pens without pressure or tilt (mouse, touch, basic pens) get speed-based width, so lines still look
  natural.
* The stabilizer is a One-Euro filter. Its strength is set per pen, and at 0 it passes raw input through.
* The pen's dynamics (curve, calibration range, min width, tilt and speed sensitivity) are copied into
  every stroke. Editing a pen later never changes existing ink.

## 6. Data model

```
Notebook (name, color, sort, defaults JSON)
 └─ SectionGroup (nested, optional)
     └─ Section (name, color, sort, defaults JSON)
         └─ Page (title, sort REAL, level = subpage depth, layout JSON, favorite, opened)
             ├─ Stroke[]          id, z, NIB1 blob
             ├─ TextContainer[]   id, z, rich text blob + plain text (for search in v1)
             └─ tag_items         index of tagged paragraphs (feeds the tag summary)
TagDefinition (id, name, symbol, color, checkbox?, shortcut 1–9)
```

* `PageLayout` = mode (fixed sheets or infinite canvas), paper (A4/A5/Letter/custom), orientation, sheet
  count and `BackgroundSpec`. Background kinds are blank, lined (narrow/college/wide/custom), grid, dots,
  graph (major/minor), Cornell, plus image and PDF page in v1. Each background has its own spacing,
  line color, paper color and margin line. Fixed pages add a sheet when you write in the last 20% of
  the final sheet.
* A new page's layout comes from **section defaults → notebook defaults → app defaults**. Page setup can
  apply to the page, the section or the notebook.
* `sort` is a REAL, so reordering writes only the rows that moved. Subpages are pages with `level > 0`
  directly below their parent. Moving a page carries its subpages with it.
* Deletes are soft (a `deleted` timestamp), so the v1 recycle bin needs no migration.

## 7. Storage and file format

`%LOCALAPPDATA%\Nib\` (or `--data <folder>`): `notebooks.db`, `settings.json`, `crash.log`.

* SQLite settings: `journal_mode=WAL`, `synchronous=NORMAL`, foreign keys on, 16 MB cache. Schema version
  in `PRAGMA user_version` (currently 1).
* Strokes and text containers are one row each (`WITHOUT ROWID`, key `(page_id, id)`), so autosave writes
  only what changed. Changes are debounced (300 ms after the last edit), collected on the ink thread
  as an immutable `PageChangeSet` and written by the storage writer thread. Writes are last-write-wins per id, so
  undo/redo storms stay correct. On close, the app flushes and waits.
* Crash safety: WAL plus small per-change transactions means a crash loses at most the debounce window.
  Unhandled exceptions are written to `crash.log`.

**NIB1 stroke blob** (all integers are LEB128 varints, signed values zig-zag encoded, per-point values
delta-encoded):

```
u8  version (=1)      u8 pen kind     u8 flags (HasPressure, HasTilt, HasTwist, ...)
u32 color ARGB        f32 width (page units, 1 unit = 1/96 in)
dynamics: u8 curve kind [u8 n, n × (u16 x, u16 y) if custom] u16 inputMin u16 inputMax
          u8 pressureEnabled u16 minWidthRatio u16 tiltSensitivity u16 speedSensitivity ...
uvar point count
per point: svar dx, dy        (1/64 unit ≈ 4 µm)
           svar dpressure     (16-bit: keeps every level of 4096- and 8192-level pens)
           [svar dtiltX, dtiltY]   (0.1°)   if HasTilt
           [svar dtwist]           (0.1°)   if HasTwist
           uvar dt                 (ms)
```

Text containers use a sibling format (TextCodec v1): position, width, measured height, then paragraphs
(kind, indent, checked, tag ids, runs with text, flags, size, color, highlight and font). Layouts and
defaults are JSON from a source-generated serializer, so they are AOT-safe.

## 8. Window, work area and layout

* All layout is computed against a **safe area**: the monitor work area, minus the strip an auto-hide
  taskbar pops up from (`SHAppBarMessage`), minus the touch keyboard or handwriting panel occlusion
  (`InputPane`). It is recomputed on `WM_SETTINGCHANGE(SPI_SETWORKAREA)`, `WM_DISPLAYCHANGE`, DPI
  changes and InputPane show/hide.
* Window placement is remembered **per monitor**, relative to that monitor's work area, so a window
  never comes back under a taskbar that moved.
* The toolbar docks top, bottom, left or right, and the sidebar can sit on either side. Left/right
  docking keeps tools away from the writing hand. When docked at the top, the toolbar sits inside the
  title bar, so it costs no page height. `ResponsiveLayout` makes the layout decisions: on narrow or
  portrait windows the sidebar overlays the page instead of pushing it, and a top toolbar that no
  longer fits moves to the bottom edge. It is a set of pure functions, unit-tested for 3:2, 16:10,
  16:9, 4:3 and ultrawide, in portrait and landscape.
* On a resize, the ink thread resizes the swap chain and redraws from cached tiles in the same frame,
  so no blank areas appear. Zoom presets are page width, 100% and fit page. Focus mode uses the
  full-screen presenter.
* `app.manifest` declares PerMonitorV2 DPI awareness, so monitor moves are never bitmap-stretched.

## 9. Performance instrumentation

* `NibEventSource` ("Nib-Performance") emits ETW events for input→present, frames (interval and
  render time), UI stalls, page switches, startup milestones, autosaves and tile rasterization.
  `perf/Nib.wprp` records them alongside CPU and GPU stacks for Windows Performance Analyzer.
* `PerfCounters` keeps allocation-free histograms in the app. `PerfVerdict` turns them into pass/fail
  against `perf/perf-targets.json`.
* Pen-to-pixel can't be measured from software alone, because the display scan-out happens after
  Present. The verdict therefore uses

  $$t_{\text{pen→pixel}} \approx p_{95}(t_{\text{input→present}}) + 1.5\,T_{\text{refresh}} - t_{\text{prediction}} \le 20\ \text{ms}$$

  and the camera procedure in `TEST-CHECKLIST.md` checks it on real hardware.
* The debug overlay (Ctrl+Alt+Shift+F12, or `--overlay`) shows frame time (and p99), refresh rate,
  dropped frames, render p95, input→present (last and p95), UI stalls, page-switch p95, the stroke
  count, the graphics device, the prediction in use, the performance-mode state and the safe area. It
  is hidden from normal users.

## 10. Hardware tuning

* `DeviceProbe` (app, background thread at startup) collects a `DeviceSnapshot`:
  * the pen's HID usages from `GetPointerDevices` + `GetPointerDeviceProperties` (pressure logical
    range, X/Y tilt with physical range, twist, eraser/invert, barrel switches),
  * CPU sets from `GetSystemCpuSetInformation` (efficiency class, parked, allocated),
  * the SMBIOS model strings from the registry, the monitor size and the refresh rate,
  * `SM_CONVERTIBLESLATEMODE` (tablet posture) and `GetSystemPowerStatus`.
* `Nib.Core.Device` turns the snapshot into decisions, as pure, unit-tested functions:
  `PenCapabilities.FromHidUsages`, `CpuTopology.PerformanceCores`, `KnownDevices.Match`,
  `DeviceTuner.AutoPredictionMs` (budget + 1.5 refresh periods − 8 ms, clamped to 0–15 ms),
  `DeviceTuner.ApplyFirstRunDefaults` and `DeviceTuner.ToolbarButtonSize`.
* `PerformanceMode` runs on the ink thread and is driven by pen hover. When the pen comes near, it calls
  `DCompositionBoostCompositorClock(TRUE)` and `SetThreadSelectedCpuSets` with the performance cores.
  On pen-down it re-asserts the boost at most once a second. 1.5 s after the pen leaves, it releases
  both.
* At pen-down the engine re-reads the compositor refresh rate (`DwmGetCompositionTimingInfo`), so the
  prediction horizon and dropped-frame accounting follow refresh-rate changes.
* `WindowFeedback` turns off Windows' pen tap, press-and-hold, right-tap and barrel visuals
  (`SetWindowFeedbackSetting`) for the window and its child windows.
* `WindowManager` raises `PostureChanged` on `WM_SETTINGCHANGE("ConvertibleSlateMode")` and
  `DisplayModeChanged` on `WM_DISPLAYCHANGE`.
