# Nib roadmap

The phases follow the brief: **MVP** proves that responsiveness and pen feel beat OneNote, **v1** reaches
everyday parity, and **v2** adds the extras. Every phase has to pass the same perf gate
(`perf/run-perf.ps1`, targets in `perf/perf-targets.json`) before it ships. A feature that breaks a
budget doesn't ship until it fits the budget.

## MVP: built in this repository

**Ink and pens**
- Own low-latency ink engine: independent input thread, Win2D swap chain with frame latency 1, pointer
  prediction, incremental wet ink, tile cache. See `ARCHITECTURE.md`.
- Ballpoint, fountain, pencil and highlighter, with full pressure, tilt and twist models.
  Speed-based width for devices without pressure.
- Per pen: color, width, thinnest-line ratio, pressure on/off, pressure curve (linear/soft/firm), tilt
  sensitivity (0 = off), and a stabilizer.
- Up to 8 favorite pens and a highlighter on the toolbar. The pen flyout shows a live preview stroke.
- Stroke eraser and pixel eraser (with sizes). The pen's tail eraser works automatically. The barrel
  button is configurable (eraser, lasso, or none).
- Lasso: move, scale, rotate, recolor, copy/cut/paste, delete, plus undo for each of these.
- Insert space: drag down/right to push ink and text away, or up/left to pull it back.
- Unlimited per-page undo/redo.
- Hover cursor shaped and sized like the nib.

**Touch and input**
- Palm rejection: touch is ignored while the pen is in range, for 450 ms after it leaves, and for
  contacts larger than palm size.
- Two-finger pan and pinch-zoom with inertia. Two-finger tap is undo and three-finger tap is redo.
  Mouse wheel scrolls and Ctrl+wheel zooms. "Draw with finger" is a toggle in Settings.

**Pages and organization**
- Notebooks, sections (with color), pages, subpages (promote/demote), drag reorder, and move to another
  section or notebook.
- Fixed pages (A4, A5, Letter, portrait or landscape) that add sheets as you write, or an infinite canvas.
- Backgrounds: blank, lined (narrow, college, wide), squares (any size in mm), dot grid, graph paper,
  Cornell. Paper color, line color and margin line are adjustable. Defaults can be set per page, section or notebook.
- Text containers with paragraph styles (Heading 1–3, bullets, numbering), B/I/U shortcuts and spell
  check.
- Tags: To Do (checkbox), Important, Question, Remember for later, Definition, on Ctrl+1–5 (Ctrl+6–9
  are reserved for custom tags). Tag summary across notebooks.
- Recent pages and favorites.

**Window, layout, export, reliability**
- Work-area aware layout: every taskbar edge, auto-hide strip, touch keyboard and handwriting panel
  occlusion, per-monitor placement memory, PerMonitorV2 DPI.
- Toolbar docks top (in the title bar), bottom, left or right. The sidebar can sit on either side.
  Compact layouts adapt to narrow and portrait windows. Focus mode (F11).
- Light/dark theme, with optional dark pages (display only).
- Vector PDF export of a page, section or notebook, with or without the background. Pages break at
  white space for infinite pages. Text is exported as outlines.
- SQLite autosave (300 ms debounce, per-stroke rows, WAL), soft delete, crash log.
- Perf instrumentation (ETW, histograms, debug overlay, harness), automated perf runner, WPR profile.

**Hardware tuning** (details in `DEVICE-YOGA7.md`)
- Device probe at startup: the pen's HID descriptor (pressure levels, tilt, buttons), hybrid CPU core
  classes, SMBIOS model, refresh rate, tablet posture, power state. Shown in Settings → This device.
- Automatic stroke prediction from the live refresh rate (follows Dynamic refresh rate).
- Performance while writing: a high-refresh request and performance-core preference while the pen
  hovers, released 1.5 s after it leaves.
- Opaque swap chain (overlay-plane eligible), Windows pen feedback visuals off on the page, touch-sized
  toolbar in tablet posture, pure black dark pages for OLED.
- First-run defaults for the Lenovo Yoga 7 2-in-1 14ILL10, and an optional CPU-tuned AOT build
  (`-p:NibCpu=lunarlake`).

**Deferred from the brief's MVP list:** custom-curve editor and calibration screen (the data model and
file format already support both), the text format bar (font, size, color, highlight, strikethrough,
indent, clear formatting; these are stored and rendered already, and pasting keeps them), section
groups in the sidebar (storage supports them), custom tag editor (storage supports it), ruler, and page
thumbnails.

## v1: everyday parity

**Pens and ink**
- Custom pressure-curve editor (drag points) and a calibration screen that records the lightest and
  firmest press.
- Shape recognition (hold at stroke end, or toggle): line, rectangle, ellipse, triangle, arrow, kept as
  ink with the pen's dynamics.
- Ruler: two-finger rotate, snaps ink to its edge.
- Ink-to-text (Windows handwriting recognition, offline) on a lasso selection.
- Ink-to-math: handwriting → LaTeX (stored as an equation object, editable as LaTeX).
- Delegated ink trail (Windows 11 24H2+) as an optional "zero frame" wet-ink path, if measurements show
  it beats the swap chain path with `SwapChainPanel`.

**Text**
- Format bar: font, size, color, highlight, strikethrough, indent, clear formatting.
- Tables (insert, add/remove rows and columns, Tab navigation).
- Find on page (Ctrl+F) and find & replace.
- Links: web URLs (auto-detected), links to notebooks, sections, pages and paragraphs (Copy link),
  `[[page]]` wiki links, Ctrl+K.

**Content**
- Images: paste, drag in, insert. Move, resize, crop, and pen-annotate on top.
- File attachments, and PDF printouts (each PDF page becomes a page background, so you can write on it).
- Audio recording synced to ink: tap a stroke to play from that moment.
- Page templates: save a page as a template, then use it per section or notebook.
- Backgrounds from a custom image or a PDF page. Custom paper sizes.

**Organization**
- Section groups in the sidebar. Drag pages and sections between sections and notebooks.
- Page thumbnails in the page list (rendered off-thread from the tile cache).
- Search (Ctrl+E) with page, section, notebook or all scopes: titles, typed text, handwriting
  (InkAnalyzer, recognized in the background on idle cores), and OCR of images and PDF printouts.
  Ranked results with highlight-in-page.
- Custom tags (name, symbol, color, shortcut). Tag summary filters.
- Quick Note: a global shortcut opens a small always-on-top page in the Unfiled section.
- Recycle bin (pages and sections, kept for 60 days; soft delete is already in the MVP schema).

**Export and import**
- Export PNG (per page, chosen DPI), SVG, and Markdown (text only, ink as embedded SVG).
- Print (via the Windows print dialog, rendered from the same vector pipeline).
- Nib package export/import (`.nibpkg`: SQLite snapshot plus assets), for backup and moving notebooks.
- Import: PDF (to annotate), images, `.nibpkg`, and OneNote: `.one`/`.onepkg` for the common cases
  (hierarchy, text, ink as ink, images, tags, attachments; the file format is documented in
  [MS-ONESTORE]/[MS-ONE]), with OneNote's own PDF/DOCX exports as a fallback.

**App**
- Settings UI for every shortcut (the MVP already reads remaps from `settings.json`).
- High-contrast page rendering, and Narrator support for page content (text containers, ink count, tags).
- MSIX package with optional auto-update. The unpackaged xcopy build stays available.

## v2: extras

- Sync through a folder (OneDrive, Dropbox, Syncthing, NAS). Per-stroke change log with
  last-writer-wins per object, so two devices never corrupt each other. No server required.
- Page version history (snapshots on idle, browse and restore).
- Password-protected sections (AES-GCM, key from Argon2id, locked on idle).
- PDF annotation polish: open any PDF as a notebook section, export it with annotations, outline
  navigation.
