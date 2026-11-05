# OneNote feature parity

Status: **done** = in this MVP · **v1** / **v2** = planned phase (see `ROADMAP.md`) · **excluded** = not
planned (reason given). "Partial" notes explain what part of a feature is done and what remains.

## Pen and ink

| OneNote feature | Status | Notes |
|---|---|---|
| Pens with color and thickness | done | Up to 8 favorite pens on the toolbar, with a live preview in the pen flyout. |
| Pressure sensitivity | done | Full device range, per-pen curve (linear/soft/firm), thinnest-line ratio, pressure off switch. |
| Tilt | done | Fountain nib angle, pencil shading and highlighter shape follow tilt. Per-pen sensitivity, 0 = off. |
| Pencil | done | Pressure → opacity, tilt → wider and lighter. Exported to PDF as vector multiply ink. |
| Highlighter | done | Chisel tip, always shows behind ink, and passes over the same spot don't build up darker. |
| Custom pressure curve / calibration | v1 | Custom curves and the calibration range are already in the data model and file format. |
| Stroke smoothing | done | One-Euro stabilizer per pen; 0 passes raw input through. |
| Stroke eraser, point eraser (sizes) | done | Stroke and pixel erasers; the pixel eraser splits strokes and keeps their dynamics. |
| Pen tail eraser, barrel button | done | Barrel button can be eraser, lasso, right-click or nothing. |
| Lasso select: move, resize, rotate | done | Also recolor, copy, cut, paste and delete. |
| Lasso → convert to text (Ink to Text) | v1 | Uses Windows handwriting recognition, offline. |
| Ink to Shape | v1 | Hold at the end of a stroke to snap it to a line, ellipse, rectangle, triangle or arrow. |
| Ink to Math (equation + LaTeX/linear) | v1 | |
| Math assistant (solve, graph) | excluded | Excluded by the brief (math solving/graphing assistant). |
| Ruler | v1 | |
| Protractor | excluded | Excluded by the brief. |
| Insert Space | done | Both directions: push down/right, pull up/left. |
| Undo/redo | done | Unlimited per page. Two-finger tap undoes, three-finger tap redoes. |
| Draw with touch toggle | done | "Draw with finger" in Settings. |
| Palm rejection | done | Touch is ignored while the pen hovers, for 450 ms after it leaves, and for palm-sized contacts. |
| Galaxy/rainbow/glitter pens | excluded | Excluded by the brief (novelty pen effects). |
| Brush/calligraphy pens | excluded | Excluded by the brief. The fountain pen covers thick/thin writing. |
| Ink replay | excluded | Excluded by the brief. |
| Scratch-out and other ink gestures | excluded | Excluded by the brief (cause accidental edits). |
| Hover cursor | done | Sized and shaped like the current nib. Shows an eraser circle when erasing. |
| Low-latency inking | done | Built to beat OneNote, but not yet measured on hardware. Use the camera procedure in `TEST-CHECKLIST.md` §2 to compare the two side by side. |

## Organization

| OneNote feature | Status | Notes |
|---|---|---|
| Notebooks, sections, section colors | done | |
| Section groups | v1 | Storage and model are done in the MVP; the sidebar UI comes in v1. |
| Pages, subpages, promote/demote | done | Moving a page carries its subpages. |
| Drag-and-drop reorder | done | Within a list. |
| Drag pages/sections between sections and notebooks | v1 | The MVP has **Move to section…** and **Move to notebook…** commands. |
| Navigation pane, collapsible | done | Can sit on either side. Overlays the page on narrow or portrait windows. |
| Page thumbnails | v1 | |
| Recent pages | done | |
| Favorites | done | Star any page; the list is in the sidebar. |
| Tags: To Do, Important, Question, … with Ctrl+1–9 | done | Five built-in tags on Ctrl+1–5; Ctrl+6–9 are free for custom tags. |
| Custom tags | v1 | Storage is done; the editor UI comes in v1. |
| Tag summary (Find Tags) | done | Across all notebooks, grouped by tag. Tapping an item opens its page. |
| Search: typed text and titles | v1 | Plain text is already stored per text container for indexing. |
| Search: handwriting | v1 | Background recognition with InkAnalyzer. |
| Search: text in images/PDFs (OCR) | v1 | |
| Search scopes (page/section/notebook/all) | v1 | |
| Links to notebooks/sections/pages/paragraphs | v1 | Includes Ctrl+K and `[[page]]` wiki links. |
| Quick Notes (global shortcut, Unfiled section) | v1 | The Unfiled section already exists in storage. |
| Recycle bin (60 days) | v1 | Deletes are already soft in the MVP, so nothing is lost before the UI ships. |
| Page versions | v2 | |
| Password-protected sections | v2 | AES-GCM with an Argon2id key, auto-lock on idle. |
| Sync (OneDrive) | v2 | Through any sync folder, with per-object conflict handling. |
| Real-time co-authoring, author marks | excluded | Needs a server. Local-first is a core goal; v2 folder sync covers one person on several devices. |

## Text and page content

| OneNote feature | Status | Notes |
|---|---|---|
| Type anywhere, movable note containers | done | Tap or click anywhere with the text tool. Tapping existing text edits it. |
| Bold / italic / underline | done | Ctrl+B / I / U. |
| Font, size, color, highlight, strikethrough | v1 | Partial: stored, rendered and exported already, and pasted formatting is kept. The format bar comes in v1. |
| Heading styles | done | Heading 1–3 in the overflow menu. |
| Bullets and numbering | done | |
| Indent, clear formatting | v1 | Indent levels are stored and rendered already. |
| Format painter | excluded | Excluded by the brief. |
| Tables (insert, resize, rows/columns) | v1 | |
| Table sorting/shading | excluded | Excluded by the brief. |
| Spell check | done | Windows spell checker in the text editor. |
| Find and replace on page | v1 | |
| Date/time stamp | excluded | Excluded by the brief. |
| Images and pasted screenshots | v1 | |
| File attachments (icons) | v1 | |
| File printouts (PDF/Office pages as backgrounds) | v1 | PDF in v1. Office files print to PDF first. |
| Audio recording synced with notes | v1 | Tapping a stroke plays the recording from that moment. |
| Video recording | excluded | Excluded by the brief. |
| Screen clipping | excluded | Excluded by the brief: pasting from Snipping Tool is enough (paste in v1). |
| Dictate | excluded | Excluded by the brief: Windows Win+H already works in text containers. |
| Read Aloud / Immersive Reader | excluded | Excluded by the brief. |
| Embedded Excel/Visio/Loop components | excluded | Needs the Office runtime inside the page, which costs responsiveness. Attach the file instead (v1). |
| Page templates (save page as template) | v1 | |
| OneNote template packs | excluded | Excluded by the brief. |
| Stickers | excluded | Clutter; the same kind of feature as the brief's novelty exclusions. |

## Backgrounds and view

| OneNote feature | Status | Notes |
|---|---|---|
| Page color | done | Plus a separate line color. |
| Rule lines: narrow/college/wide | done | |
| Grid / squares | done | Any size in mm. |
| Dot grid, graph paper (major/minor), Cornell | done | |
| Margin line on/off | done | |
| Custom image or PDF page as background | v1 | |
| Music staff, calligraphy backgrounds | excluded | Excluded by the brief. |
| Paper size A4/A5/Letter, portrait/landscape | done | Fixed pages add sheets as you write. |
| Custom paper size | v1 | Supported by the model; the setup UI comes in v1. |
| Infinite canvas | done | |
| Defaults per notebook/section/page | done | Page setup applies to the page, section or notebook. |
| Zoom: page width, 100%, fit page | done | Pinch zoom with inertia, settling to crisp tile levels. |
| Full page / focus mode | done | F11. |
| Dark mode with inverted page | done | Display only; exports keep the real colors. |
| Rotate view | excluded | Excluded by the brief. |
| Docked window / side note | excluded | Excluded by the brief. |
| Toolbar position, left-handed mode | done | Toolbar docks top, bottom, left or right; the sidebar goes left or right. |

## Export, import, print

| OneNote feature | Status | Notes |
|---|---|---|
| Export PDF | done | Vector ink with pressure and tilt variation, background optional, page/section/notebook, white-space page breaks for infinite pages. Text is exported as outlines (not selectable) until v1. |
| Export native backup format (.onepkg) | v1 | `.nibpkg`: lossless SQLite snapshot plus assets. |
| Export PNG, SVG | v1 | |
| Export Markdown (handwriting → text) | v1 | |
| Export JPG, DOCX | excluded | Excluded by the brief. PNG and PDF cover these uses. |
| Print | v1 | Windows print dialog, rendered from the vector pipeline. |
| Import PDF / images | v1 | |
| Import OneNote (.one/.onepkg, PDF/DOCX export) | v1 | |

## App and workflow

| OneNote feature | Status | Notes |
|---|---|---|
| Autosave | done | Every change, 300 ms debounce, per-stroke rows, WAL journal. |
| Crash recovery | done | A crash loses at most the debounce window. Unhandled exceptions go to `crash.log`. |
| OneNote keyboard shortcuts | done | Ctrl+N, Ctrl+Alt+N, Ctrl+T, Ctrl+1–9, Ctrl+Alt+[ ], Ctrl+PgUp/PgDn and more. Ctrl+K comes with links in v1. |
| Remappable shortcuts | done | Partial: remaps are read from `settings.json` today; the settings UI comes in v1. |
| Snap layouts, split screen | done | Standard window; layout reflows within one frame. |
| Remember window per monitor | done | Placement is stored relative to each monitor's work area. |
| Taskbar/keyboard-safe layout | done | Every taskbar edge, auto-hide, touch keyboard and handwriting panel. |
| Screen reader labels, keyboard navigation | done | Partial: toolbar, sidebar and dialogs are labeled and keyboard-navigable. Page content for Narrator comes in v1. |
| High contrast | v1 | The XAML shell follows high contrast already; page rendering follows in v1. |
| Copilot / AI features | excluded | Not requested, and would need a cloud service in a local-first app. |
| Email page / Send to OneNote | excluded | Export to PDF and attach the file. The Windows share sheet is enough. |
