# Nib test checklist

Run every section on at least three machines and record the results in `artifacts/`:

| Class | Example | Why |
|---|---|---|
| Low-end | Surface Go 2/3, Pentium/Celeron or Snapdragon 7c, **on battery with battery saver on** | Worst case for CPU, GPU and power throttling. |
| Mainstream | Surface Pro 7/8 or a Core i5 2-in-1, 60 Hz | Typical user. |
| High refresh | Surface Pro 9/10/11 or Snapdragon X, 120 Hz | Shows the latency ceiling. |

Run on both x64 and ARM64 (the Snapdragon devices cover ARM64). Run with a Microsoft Pen Protocol pen
(Surface Pen or Slim Pen 2) and a Wacom AES or EMR pen. Use a tilt-capable pen for the tilt rows.

## 1. Automated suite

```powershell
.\perf\run-perf.ps1 -Label "Surface Go 3, battery saver"        # x64
.\perf\run-perf.ps1 -Platform ARM64 -Label "Surface Pro 11" -Trace
```

The script runs the unit tests (127, including the CPU budget tests), publishes the Native AOT build,
and then runs `Nib.PerfRunner`. The runner does a cold start with a scenario (pen strokes with pressure
and tilt, two-finger pans and pinches with fling, page flips, resizes), followed by a warm start. It
writes `report.json` and exits **0** when every target passes, **1** when any target is missed, and
**2** when it can't run. `-Trace` also records `nib.etl` for Windows Performance Analyzer.

| Target (`perf/perf-targets.json`) | Pass when | Measured by |
|---|---|---|
| Input → present p95 | ≤ one refresh period + 4 ms | Ink thread timestamps, from pointer event to after `Present` |
| Effective pen-to-pixel p95 | ≤ 20 ms, using the formula below | Computed. Confirm with the camera procedure in §2 |
| Pan/zoom dropped frames | ≤ 1% | Frame intervals > 1.5 × refresh period during gestures and fling |
| UI thread longest stall | ≤ 8 ms | Watchdog thread pinging the dispatcher |
| Cold start → writable page | ≤ 1500 ms | Process creation → first ink frame presented (`ready.json`) |
| Warm start → writable page | ≤ 500 ms | Same, second launch |
| Page switch p95 | ≤ 100 ms | Request → first frame of the new page |
| Tool change → next stroke | ≤ 0.5 ms | `ToolState` swap → visible to the ink thread at pointer-down |
| Resize / rotation reflow | ≤ 1 frame | Size change → first frame at the new size with no blank tiles |

$$t_{\text{pen→pixel}} \approx p_{95}(t_{\text{input→present}}) + 1.5\,T_{\text{refresh}} - t_{\text{prediction}}$$

Here $1.5\,T_{\text{refresh}}$ is the average wait for the next vblank plus one scan-out, and
$t_{\text{prediction}}$ is the `PointerPredictor` horizon (15 ms by default). At 60 Hz this is
$4 + 25 - 15 = 14$ ms, and without prediction it is 29 ms, so **at 60 Hz the 20 ms target depends on
prediction**. At 120 Hz it is about 16.5 ms without prediction.

Manual checks for the automated targets:
- [ ] Open the debug overlay (Ctrl+Alt+Shift+F12) and write continuously for 60 s. The input→present
  p95 stays within target, and the UI stall count stays at 0.
- [ ] Write while the sidebar is scrolling, an export is running and autosave is flushing. No drop in
  smoothness.
- [ ] Build a page with 10,000+ strokes (lasso-copy a full page and paste it repeatedly; the debug overlay
  shows the count). Pan, pinch and write: frame time matches an empty page.
- [ ] A section with 1,000 pages: the sidebar scrolls smoothly, and switching pages stays ≤ 100 ms.
- [ ] Change pen, color and width rapidly between strokes. The very next stroke always uses the new tool.

## 2. Pen-to-pixel latency (camera)

You need a phone camera at **240 fps** or faster (960 fps is better), a ruler with mm marks taped to the
screen bezel, and the screen at max brightness.

**Moving-gap method (main measurement):**
1. Set the stabilizer to 0 and choose the ballpoint, 0.6 mm, black, on a blank page.
2. Film from above, square to the screen, with the ruler in frame.
3. Draw fast, steady straight lines along the ruler (about 200–400 mm/s) 10 times.
4. In 10 frames from the steady middle part of each line, measure the gap $g$ between the physical nib
   and the end of the drawn ink, and the nib speed $v = \Delta x / \Delta t_{\text{frame}}$ from the
   nib's movement between frames.
5. For each frame, latency is $t = g / v$. Report the median and p95 over all frames.
6. Repeat with prediction at 0 ms (Settings), at the default 15 ms, and at 30 ms.

**Touchdown method (first-contact latency):** tap a dot 20 times and count frames from nib contact to
the dot appearing ($t = n_{\text{frames}} / f_{\text{camera}}$). This includes the digitizer's
touchdown detection, which is often slower than in-stroke reporting, so record it separately.

- [ ] The median moving-gap latency with default prediction is ≤ 20 ms on the 120 Hz device. Record
  the 60 Hz devices too.
- [ ] Compare with OneNote, using the same pen, device and procedure. Nib is not slower.
- [ ] Prediction doesn't overshoot visibly at sharp corners or stroke ends (watch the slow-motion clip
  frame by frame). The predicted tail disappears in the frame the pen lifts.

## 3. Pressure

- [ ] **Full range:** a feather-light touch starts a stroke with a thin line, and the firmest press
  reaches full width. There's no plateau in the upper third. Check with the pen settings preview and on
  the page.
- [ ] **Native rate:** export a fast stroke and check the timestamps. The point count per second
  matches the digitizer's report rate (for example 133 Hz MPP, 180–240 Hz Wacom), with no gaps or
  duplicates.
- [ ] Curves: **soft** gets thick quickly, **firm** needs more force, **linear** sits between them. Each
  preview matches what appears on the page.
- [ ] Pressure **off** gives constant width. "Thinnest line" at 0% vs 100% gives tapered vs uniform
  strokes.
- [ ] Width and opacity changes look continuous, with no visible steps in long pressure ramps. Zoom in
  to 800%.
- [ ] A pen without pressure (mouse, touch, passive stylus) gets speed-based width: slow is thicker and
  fast is thinner, and nothing jitters.
- [ ] Editing a pen never changes strokes that are already written. Reload the page after editing to
  check.

## 4. Tilt (repeat for each pen)

| Pen | Expected |
|---|---|
| Ballpoint | Tilt has no effect. |
| Fountain | Strokes are thick and thin by direction. Rotating the pen's tilt direction rotates the nib angle. Twist is used when the pen reports it. |
| Pencil | Laying the pen flat widens the stroke and makes it lighter (shading). Upright gives a fine line. |
| Highlighter | Upright gives a narrow chisel. Tilted gives a broader one. It always stays behind ink. |

- [ ] Tilt sensitivity at 0 turns tilt off completely, for each pen.
- [ ] A pen without tilt support behaves as if held upright, with no wobble.
- [ ] Each pen's footprint matches its hover cursor shape and size.

## 5. Pen hardware, tools and editing

- [ ] Tail eraser erases with the current eraser mode, and releasing it returns to the previous tool.
- [ ] Barrel button set to **eraser**, **lasso**, **right-click** (opens the context menu) and **none**
  each works.
- [ ] The hover cursor appears within the hover range, hides when the pen leaves, and follows without lag.
- [ ] Stroke eraser removes whole strokes. The pixel eraser cuts cleanly at every size, even when moved
  fast (nothing gets skipped).
- [ ] Lasso: move, scale (corner handles), rotate, recolor, copy/cut/paste and delete each undo and redo
  correctly.
- [ ] Insert space pushes down and right, and pulls up and left. Text containers move with the ink.
- [ ] Undo/redo is unlimited per page and kept separately for each page. Visit more than 6 other pages
  (so the page is evicted from memory and reloaded), come back, and undo all the way to empty.
- [ ] The highlighter over ink: ink stays black, and overlapping passes of one highlighter stroke don't
  darken.
- [ ] Shapes, text and ink look identical after a restart (the round trip through storage is lossless).

## 6. Palm rejection and touch

- [ ] Rest the palm on the screen, then write. The palm leaves no marks, and the page doesn't pan or zoom.
- [ ] Put the palm down *before* the pen comes into range (the hardest case). No marks.
- [ ] A finger touch within 450 ms of lifting the pen is ignored. After that, two-finger pan works.
- [ ] "Draw with finger" (Settings) off: one finger scrolls. On: one finger draws and two fingers scroll.
- [ ] Two-finger pan and pinch stay locked to the refresh rate, inertia decays smoothly, and the view
  settles crisp.
- [ ] Two-finger tap undoes and three-finger tap redoes, with no accidental triggers while pinching.
- [ ] Mouse wheel scrolls, Shift+wheel scrolls horizontally, Ctrl+wheel zooms around the cursor.

## 7. Taskbar, work area and on-screen panels

For each taskbar position (**bottom, top, left, right**), with **auto-hide off and on**:
- [ ] Maximized, snapped and restored: the toolbar, sidebar and page edges are fully visible.
- [ ] With auto-hide on, nothing interactive sits in the reveal strip, and moving the pen to the edge
  doesn't fight the taskbar.
- [ ] Moving the taskbar or toggling auto-hide while Nib is open re-lays out the window immediately
  (`WM_SETTINGCHANGE`).
- [ ] Restarting puts the window back in the same place relative to the work area, never under the
  taskbar.

Touch keyboard and handwriting panel:
- [ ] Docked touch keyboard: the text being edited and the toolbar move above it. When it hides, the
  layout returns.
- [ ] Floating keyboard and handwriting panel: nothing important stays covered.
- [ ] **InputPane coordinate check:** Nib assumes `OccludedRect` is in screen-space DIPs and converts
  it with the window's rasterization scale. At 100%, 150% and 200% scaling, and on a secondary monitor
  with *different* scaling, the bottom toolbar must sit exactly on the keyboard's top edge, with no gap
  and no overlap. If it's off by the scale factor, fix `WindowManager.ToPhysical`.

## 8. Screens, rotation, DPI, monitors

- [ ] Aspect ratios **3:2, 16:10, 16:9, 4:3, 21:9 ultrawide**, each in landscape and portrait. The layout
  is sensible, and on portrait or narrow windows the sidebar overlays the page.
- [ ] Rotating the device reflows within one frame, with no blank tiles, and the page doesn't lose its
  position.
- [ ] Scaling at **100, 125, 150, 175, 200, 250 and 300%**: crisp text, icons and ink, with no
  blurriness.
- [ ] Moving the window between monitors with different DPI: no blur, no wrong-size window, and ink
  stays aligned with the pen.
- [ ] Unplugging the monitor Nib is on: the window lands inside the remaining monitor's work area.
- [ ] Snap layouts (Win+Z and dragging), split screen with another app, and F11 focus mode all work.
- [ ] Each toolbar dock (top/bottom/left/right) and each sidebar side, including left-handed use with
  the toolbar on the left.
- [ ] Zoom presets: page width, 100% and fit page are exact. 100% equals real paper size at the
  monitor's DPI.

## 9. Organization and content

- [ ] Create, rename, recolor, reorder and delete notebooks, sections and pages. Subpages promote and
  demote. Moving a page carries its subpages.
- [ ] Move a page to another section, and a section to another notebook.
- [ ] Recent pages and favorites update immediately.
- [ ] Tags Ctrl+1–5 on text, To Do checkboxes toggle, and the tag summary lists items across notebooks
  and opens each one.
- [ ] Page setup for each background kind, line spacing, grid size, paper and line colors, margin line,
  A4/A5/Letter, portrait/landscape and infinite canvas. Applying to section or notebook changes the
  defaults for new pages.
- [ ] Fixed pages add a sheet when you write near the bottom of the last sheet.
- [ ] Text containers: type, B/I/U, Heading 1–3, bullets, numbering, spell check, and moving the
  container. Pasting formatted text keeps fonts, colors and highlights.
- [ ] Dark theme with dark pages: ink is readable and colored ink is recognizable. Exports keep the
  original colors.

## 10. PDF export accuracy

- [ ] Export one page, one section and one notebook. Each opens in Edge, Chrome, Acrobat Reader and
  SumatraPDF. `qpdf --check` reports no errors.
- [ ] **Vector:** zoom in to 6400% and edges stay sharp. `pdfimages -list out.pdf` lists no images.
- [ ] Pressure and tilt variation match the screen (fountain thick/thin, pencil shading, chisel
  highlighter).
- [ ] The highlighter multiplies behind ink. Pencil overlaps don't stack darker.
- [ ] Paper sizes are exact: A4 is 595.28 × 841.89 pt, Letter is 612 × 792 pt, and landscape swaps them.
- [ ] Backgrounds on and off both work (overflow menu → Export PDF → Include page background).
- [ ] Infinite pages break at white space, with no handwriting line cut in half.
- [ ] Text matches the screen in position, wrapping, styles and colors (exported as outlines, so it is
  not selectable in the MVP).
- [ ] A notebook export with hundreds of pages runs in the background. The UI never freezes, and writing
  stays smooth while it runs.

## 11. Reliability

- [ ] Kill Nib in Task Manager while writing. On reopen, everything up to about 300 ms before the kill
  is there.
- [ ] Close the window during heavy writing. Every stroke is saved (the app flushes on close).
- [ ] Undo/redo storms (hold Ctrl+Z, then Ctrl+Y) followed by a restart: the page matches what was on
  screen.
- [ ] Read-only data folder: Nib shows "Could not open your notebooks" and doesn't crash. Full disk
  while writing: no crash, the ink stays on screen, and saving resumes once there is space.
- [ ] Start with `--data <empty folder>`: starter notebook created, and cold start target still met.
- [ ] A forced exception is written to `crash.log` with a stack trace.
- [ ] Run for 8 hours with the device sleeping and waking in between. Memory stays flat (tile cache
  stays within budget), and nothing is lost after a GPU device loss (the device is recreated and tiles
  redrawn).

## 12. Accessibility

- [ ] Narrator reads every toolbar button, sidebar item and dialog control.
- [ ] Keyboard only: Tab, arrow keys and Enter reach every command. The OneNote shortcuts work
  (Ctrl+N, Ctrl+Alt+N, Ctrl+T, Ctrl+PgUp/PgDn, Ctrl+Alt+[ ], F11).
- [ ] A shortcut remapped in `settings.json` (for example `"KeyMap": {"NewPage": "Ctrl+Shift+N"}`)
  works after a restart.
- [ ] The high-contrast theme applies to the shell (page rendering comes in v1).

## 13. Hardware tuning

The device-specific checks for the Lenovo Yoga 7 2-in-1 14ILL10 are in `DEVICE-YOGA7.md` §5. On any
other machine:
- [ ] **Settings → This device** lists the pen's real capabilities (compare with the manufacturer's
  spec), the CPU core classes and the current refresh rate.
- [ ] The debug overlay shows the performance mode turning on while the pen hovers and going idle about
  1.5 s after it leaves. With the setting off, it shows *performance mode off*.
- [ ] Automatic prediction changes when the refresh rate changes (switch it in Windows display
  settings, then draw a stroke).
- [ ] On a convertible, tablet posture makes the toolbar touch-sized, and laptop posture restores it.
- [ ] No Windows pen ripple or press-and-hold ring appears on the page.
