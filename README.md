# Nib

A pen-first note-taking app for Windows, built to beat OneNote on the two things that matter most when
you write by hand: **ink that keeps up with the pen**, and **a UI that stays out of the way**.

C# · WinUI 3 (Windows App SDK 2.6.5) · .NET 10 · Win2D · SQLite · Native AOT

This repository is the **MVP**: low-latency inking with pressure and tilt, four pen models, notebooks,
sections and pages, backgrounds, text containers, tags, vector PDF export, work-area-correct layout,
one compact toolbar, and an automated performance suite.

## Documents

| | |
|---|---|
| [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) | Stack choices (and where they deviate from the brief), projects, threading, rendering pipeline, data model, file format |
| [docs/ROADMAP.md](docs/ROADMAP.md) | MVP (this repo) → v1 → v2 |
| [docs/ONENOTE-PARITY.md](docs/ONENOTE-PARITY.md) | Each OneNote feature: done / v1 / v2 / excluded, with reasons |
| [docs/TEST-CHECKLIST.md](docs/TEST-CHECKLIST.md) | Perf targets, camera latency procedure, pen, palm, taskbar, DPI, monitor and export tests |
| [docs/DEVICE-YOGA7.md](docs/DEVICE-YOGA7.md) | Tuning for the Lenovo Yoga 7 2-in-1 14ILL10 (Core Ultra 7 256V, OLED, Yoga Pen): what changes, Windows settings, tuned build, on-device checks |

## Build and run

You need Windows 10 2004 (19041) or later (Windows 11 recommended), the .NET 10 SDK, and Visual Studio
2026 with the *WinUI application development* workload. The command line alone also works.

```powershell
# Debug run (x64; use -p:Platform=ARM64 on ARM devices)
dotnet build src\Nib.App -c Debug -p:Platform=x64
.\src\Nib.App\bin\x64\Debug\net10.0-windows10.0.22621.0\win-x64\Nib.exe

# Release: Native AOT, self-contained, xcopy-deployable folder
dotnet publish src\Nib.App -c Release -r win-x64 -p:Platform=x64 -o artifacts\app-win-x64

# Release tuned for a Core Ultra 200V (Lunar Lake) such as the Yoga 7 2-in-1's 256V (AVX2/FMA/BMI2)
dotnet publish src\Nib.App -c Release -r win-x64 -p:Platform=x64 -p:NibCpu=lunarlake -o artifacts\app-yoga7

# Unit tests (platform-independent core, storage and export)
dotnet test tests\Nib.Core.Tests -c Release

# Full performance gate: unit tests, AOT publish, automated perf run; exit code 0 = all targets met
.\perf\run-perf.ps1 -Label "my device"
```

In Visual Studio, open `Nib.sln`, choose **x64** or **ARM64**, set **Nib.App** as the startup project,
and press F5.

Command-line options: `--data <folder>` uses a different data folder, `--overlay` shows the debug
overlay, and `--perf --perf-out <folder> --perf-scenario <name>` runs the harness used by the perf runner.

Data is stored in `%LOCALAPPDATA%\Nib\` as `notebooks.db` (SQLite), `settings.json` and `crash.log`.

## Using it

* **Toolbar:** favorite pens, highlighter, eraser, lasso, text, undo/redo, and the overflow menu (⋯),
  which holds insert space, page setup, export and settings. Tap the active tool again to edit it (kind, color, width, pressure curve, tilt,
  stabilizer, with a live preview). **+** adds a favorite pen. The toolbar docks top, bottom, left or
  right (overflow → Toolbar position).
* **Pen:** the tail eraser just works. The barrel button is set in Settings. Hovering shows the nib.
* **Touch:** two fingers pan and pinch. Two-finger tap undoes, three-finger tap redoes. A finger never
  draws unless you turn on *Draw with finger* in Settings.
* **Page setup** (overflow menu): background, line spacing, grid size, colors, margin line, paper
  size, orientation, or infinite canvas. Apply it to the page, the section or the notebook.
* **Shortcuts** (OneNote defaults; remap them in `settings.json` under `"KeyMap"`): Ctrl+N new page,
  Ctrl+Alt+N subpage, Ctrl+T section, Ctrl+Alt+[ / ] promote/demote, Ctrl+PgUp/PgDn previous/next page,
  Ctrl+1–5 tags, Ctrl+Z/Y, Ctrl+Shift+E export PDF, Ctrl+Shift+B sidebar, F11 focus mode.

## Repository layout

```
src/Nib.Core          platform-independent engine: ink, geometry, model, editing, layout, codec, PDF, perf
src/Nib.Storage       SQLite (winsqlite3), schema, background reader/writer, autosave
src/Nib.App           WinUI 3 app: ink engine, Win2D rendering, shell, windowing, diagnostics
tests/Nib.Core.Tests  xUnit tests incl. CPU budgets
tests/Nib.PerfRunner  launches the AOT build, injects pen/touch/keys, enforces perf targets
perf/                 perf-targets.json, run-perf.ps1, Nib.wprp (ETW profile for WPA)
docs/                 architecture, roadmap, parity table, test checklist
```

## Verification status

What was checked before handing this over:

* **Core, storage and export** compile and pass all **127 unit tests** on .NET 10. This includes the
  CPU budget tests (stroke geometry for every pen model stays under 1 ms on realistic handwriting).
* **PDF export:** sample exports were rendered and inspected at 400 dpi (pressure variation, fountain
  thick/thin, pencil tilt shading, highlighter behind ink, no seams), and pass `qpdf --check`.
* **The WinUI app builds on Windows** (x64, Debug and Release) and launches: window opens, starter
  notebook is created, no crash log. Fixes needed on first build: `InputPointerSource.Cursor`, the
  predicted-points array, and no public `NotebookInfo` collection on `Sidebar` (XAML metadata).
* **Native AOT builds with no warnings and runs** (first writable frame about 410 ms cold on the dev machine).
  Under AOT, every class that implements a WinRT interface (or derives from a WinRT type) must be
  `partial`, and styles/brushes can't be read from `Application.Resources` (the cast fails), so the
  toolbar sets them in code. Publishing needs `vswhere` on PATH: add
  `C:\Program Files (x86)\Microsoft Visual Studio\Installer`.
* **Automated perf run (240 Hz Yoga 7, AOT build):** input to present p95 1.2 ms (target 8.2), effective
  pen-to-pixel p95 5.5 ms (target 20, estimate; confirm with the camera procedure), cold start 330-490 ms
  (1500), warm start 300 ms (500), page switch p95 19-36 ms (100) all PASS. UI thread longest stall passed
  at 7.5 ms in the latest run; an earlier run saw one 1 s stall that did not recur. **Two targets still
  fail:** pan/zoom dropped frames 2% (target 1%) and resize reflow 2 frames (target 1). The pan numbers
  need a second look because the recorded frame intervals have a median of 0.15 ms on a 240 Hz panel,
  which suggests the render loop is not paced by vsync during gestures; the 2% is also quantized to
  whole percentiles. Run it from a normal desktop terminal: the runner needs real input injection.
* **No perf target has been measured on real hardware yet.** Run `perf\run-perf.ps1` and the camera
  procedure in `docs/TEST-CHECKLIST.md` §2. The 20 ms pen-to-pixel target at **60 Hz** depends on
  pointer prediction (the math is in the checklist). At 120 Hz the model meets it without prediction.

## Known limitations of the MVP

* Text is exported to PDF as glyph outlines: it looks exact but can't be selected or searched until v1.
* Deferred to v1 (see the roadmap): search, thumbnails, section-group UI, custom tags, text format bar,
  tables, images and printouts, Ink to Text/Shape/Math, ruler, curve editor and calibration, links,
  Quick Note, recycle bin UI, PNG/SVG/Markdown export, print, import.
* Shortcut remapping has no settings UI yet; edit `settings.json`.
* Page content isn't exposed to Narrator yet (the toolbar, sidebar and dialogs are).
