using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Nib.App.Ink;
using Nib.App.Services;
using Nib.App.Shell;
using Nib.Core.Device;
using Nib.Core.Layout;
using Nib.Core.Model;
using Windows.System;

namespace Nib.App;

/// <summary>
/// The Settings window: one tab per topic (pen, pointer, touch, toolbar, appearance, shortcuts, device).
/// Nothing is applied until Save, except the pointer preview, which shows the choice right in the tab.
/// </summary>
public sealed partial class MainWindow
{
    private static readonly string[] SettingsTabs = { "Pen", "Pointer", "Touch", "Toolbar", "Appearance", "Saving", "Shortcuts", "Device" };

    // ---- small control helpers -----------------------------------------------------------------

    private static ComboBox Combo(string header, IEnumerable<string> items, int selected)
    {
        var c = new ComboBox { Header = header, HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var i in items) c.Items.Add(i);
        c.SelectedIndex = Math.Max(0, selected);
        return c;
    }

    private static Slider Sl(string header, double min, double max, double step, double value) =>
        new() { Header = header, Minimum = min, Maximum = max, StepFrequency = step, Value = Math.Clamp(value, min, max) };

    private static ToggleSwitch Tg(string header, bool on, string? onText = null, string? offText = null) =>
        new() { Header = header, IsOn = on, OnContent = onText ?? "On", OffContent = offText ?? "Off" };

    private static StackPanel Tab() => new() { Spacing = 10, Padding = new Thickness(0, 8, 12, 8) };

    private static uint[] Swatches => new uint[]
    {
        0xFF000000, 0xFFFFFFFF, 0xFFE53935, 0xFFFB8C00, 0xFFFDD835, 0xFF43A047,
        0xFF00ACC1, 0xFF2B6CB0, 0xFF5E35B1, 0xFFD81B60, 0xFF8D6E63, 0xFF757575,
    };

    private static string Hex(uint argb) => "#" + (argb & 0xFFFFFF).ToString("X6");

    private static bool TryHex(string text, out uint argb)
    {
        argb = 0;
        var t = text.Trim().TrimStart('#');
        if (t.Length != 6 || !uint.TryParse(t, System.Globalization.NumberStyles.HexNumber, null, out var v)) return false;
        argb = 0xFF000000 | v;
        return true;
    }

    private static SolidColorBrush Brush(uint argb) => new(Colors2D.From(argb));

    // ---- the dialog ----------------------------------------------------------------------------

    private async Task SettingsDialogAsync(string? openTab = null)
    {
        var s = Settings;

        // ===== Pen & eraser =====
        var penTab = Tab();
        penTab.Children.Add(Header("Eraser"));
        var eraserMode = Combo("Eraser type", new[] { "Stroke (removes whole strokes)", "Pixel (erases part of a stroke)" }, (int)s.EraserMode);
        var eraserSize = Sl("Eraser size (DIPs)", 4, 60, 1, s.EraserSize);
        penTab.Children.Add(eraserMode);
        penTab.Children.Add(eraserSize);
        penTab.Children.Add(Header("Shape and line snapping"));
        var snapOn = Tg("Snap to shapes and lines", s.ShapeSnap, "On: hold the pen still at the end of a stroke", "Off");
        var snapHold = Sl("Hold time before it snaps (ms)", 300, 1500, 50, s.ShapeSnapHoldMs);
        snapHold.IsEnabled = s.ShapeSnap;
        snapOn.Toggled += (_, _) => snapHold.IsEnabled = snapOn.IsOn;
        var snapAngles = Tg("Lock lines and boxes to straight angles", s.SnapLineAngles, "On: horizontal, vertical and 45 degrees when close", "Off: keep the angle you drew");
        penTab.Children.Add(snapOn);
        penTab.Children.Add(snapHold);
        penTab.Children.Add(snapAngles);
        penTab.Children.Add(new TextBlock { Text = "Draw a line, circle, ellipse, triangle or rectangle and keep the pen still for a moment: it turns into the clean shape. A snapped line follows the pen until you lift.", Opacity = 0.7, TextWrapping = TextWrapping.Wrap });
        penTab.Children.Add(Header("Pen tilt"));
        var tiltCfg = s.Tilt;
        var tiltOn = Tg("Use pen tilt", tiltCfg.Enabled, "On: brush, pencil, calligraphy, marker and watercolor respond to how you hold the pen", "Off: every pen acts as if held upright");
        var tiltDead = Sl("Upright dead zone (degrees)", 0, 20, 1, tiltCfg.DeadZoneDeg);
        var tiltFlipX = Tg("Flip tilt left / right", tiltCfg.InvertX, "On", "Off");
        var tiltFlipY = Tg("Flip tilt up / down", tiltCfg.InvertY, "On", "Off");
        void TiltEnable() { tiltDead.IsEnabled = tiltFlipX.IsEnabled = tiltFlipY.IsEnabled = tiltOn.IsOn; }
        tiltOn.Toggled += (_, _) => TiltEnable();
        TiltEnable();
        penTab.Children.Add(tiltOn);
        penTab.Children.Add(tiltDead);
        penTab.Children.Add(tiltFlipX);
        penTab.Children.Add(tiltFlipY);
        penTab.Children.Add(new TextBlock { Text = "Each pen's own tilt strength is in its flyout (tap the active pen). If the brush leans the wrong way on your pen, flip the axis here.", Opacity = 0.7, TextWrapping = TextWrapping.Wrap });
        penTab.Children.Add(Header("Pen hardware"));
        var barrel = Combo("Pen barrel button", new[] { "Eraser", "Lasso select", "Right-click", "Nothing" }, (int)s.BarrelButton);
        penTab.Children.Add(barrel);
        var autoPrediction = Tg("Stroke prediction", s.AutoPrediction,
            $"Automatic from the refresh rate ({DeviceTuner.AutoPredictionMs(_device.RefreshHz):0} ms at {_device.RefreshHz:0} Hz)", "Fixed");
        var prediction = Sl("Fixed prediction (ms, 0 = off)", 0, 30, 1, s.PredictionMs);
        prediction.IsEnabled = !s.AutoPrediction;
        autoPrediction.Toggled += (_, _) => prediction.IsEnabled = !autoPrediction.IsOn;
        penTab.Children.Add(autoPrediction);
        penTab.Children.Add(prediction);
        var perfMode = Tg("Performance while writing", s.PerformanceModeWhileWriting,
            "On: high refresh rate and performance cores while the pen is near", "Off: let Windows decide");
        penTab.Children.Add(perfMode);
        penTab.Children.Add(new TextBlock
        {
            Text = "Pen color, width, pressure curve, tilt and stabilizer are per pen: tap the active pen on the toolbar.",
            Opacity = 0.7, TextWrapping = TextWrapping.Wrap,
        });

        // ===== Pointer =====
        var p = s.Pointer.Sanitized();
        var pointerTab = Tab();
        var preview = new Canvas { Width = 150, Height = 90 };
        var previewBox = new Border
        {
            Width = 150, Height = 90, CornerRadius = new CornerRadius(8), HorizontalAlignment = HorizontalAlignment.Left,
            Background = new SolidColorBrush(Colors2D.From(0xFFFAFAF7)), Child = preview,
        };
        var style = Combo("Pointer style", new[] { "Nib outline", "Dot", "Crosshair", "Ring", "None" }, (int)p.Style);
        var sizeMode = Combo("Pointer size", new[] { "Follow the pen width", "Fixed size" }, (int)p.SizeMode);
        var sysCursor = Combo("Windows cursor (the cross)", new[] { "Automatic (hidden when Nib draws its own pointer)", "Cross", "Arrow", "Hidden" }, (int)p.SystemCursor);
        var fixedSize = Sl("Fixed size (DIPs)", 4, 80, 1, p.FixedSize);
        fixedSize.IsEnabled = p.SizeMode == PointerSizeMode.Fixed;
        var colorMode = Combo("Pointer color", new[] { "Same as the pen", "Custom color" }, (int)p.ColorMode);
        uint custom = p.CustomColor;
        var hexBox = new TextBox { Header = "Custom color (hex)", Text = Hex(custom), Width = 140, HorizontalAlignment = HorizontalAlignment.Left };
        var swatchRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        var opacity = Sl("Opacity", 10, 100, 5, p.Opacity * 100);
        var lineWidth = Sl("Line thickness (DIPs)", 0.5, 4, 0.5, p.LineWidth);
        var centerDot = Tg("Dot at the exact contact point", p.CenterDot);
        var showWhileDrawing = Tg("Pointer while drawing", p.ShowWhileDrawing, "Shown while hovering and drawing", "Shown only while hovering");

        PointerSettings ReadPointer() => new PointerSettings()
        {
            Style = (PointerStyle)Math.Max(0, style.SelectedIndex),
            SystemCursor = (SystemCursorMode)Math.Max(0, sysCursor.SelectedIndex),
            SizeMode = (PointerSizeMode)Math.Max(0, sizeMode.SelectedIndex),
            FixedSize = (float)fixedSize.Value,
            ColorMode = (PointerColorMode)Math.Max(0, colorMode.SelectedIndex),
            CustomColor = custom,
            Opacity = (float)(opacity.Value / 100.0),
            LineWidth = (float)lineWidth.Value,
            CenterDot = centerDot.IsOn,
            ShowWhileDrawing = showWhileDrawing.IsOn,
        }.Sanitized();

        void DrawPreview()
        {
            var cur = ReadPointer();
            preview.Children.Clear();
            fixedSize.IsEnabled = cur.SizeMode == PointerSizeMode.Fixed;
            // The pen the preview pretends to be: blue ink, a 5 DIP wide nib.
            uint argb = cur.ColorMode == PointerColorMode.Custom ? cur.CustomColor : 0xFF1A3A8A;
            var color = Colors2D.From(argb);
            color.A = (byte)(color.A * cur.Opacity);
            var brush = new SolidColorBrush(color);
            double cx = 75, cy = 45;
            double r = cur.SizeMode == PointerSizeMode.Fixed ? cur.FixedSize / 2 : 5;
            // A short stroke behind the pointer shows how large it is next to real ink.
            preview.Children.Add(new Rectangle { Width = 70, Height = 5, RadiusX = 2.5, RadiusY = 2.5, Fill = new SolidColorBrush(Colors2D.From(0xFF1A3A8A)), Margin = new Thickness(0, 0, 0, 0) });
            Canvas.SetLeft(preview.Children[0], 4); Canvas.SetTop(preview.Children[0], cy - 2.5);
            void Add(UIElement e, double x, double y) { preview.Children.Add(e); Canvas.SetLeft(e, x); Canvas.SetTop(e, y); }
            switch (cur.Style)
            {
                case PointerStyle.Dot:
                    Add(new Ellipse { Width = r * 2, Height = r * 2, Fill = brush }, cx - r, cy - r);
                    break;
                case PointerStyle.Ring:
                    Add(new Ellipse { Width = r * 2, Height = r * 2, Stroke = brush, StrokeThickness = cur.LineWidth }, cx - r, cy - r);
                    break;
                case PointerStyle.Crosshair:
                {
                    double arm = Math.Max(r, 5) + 3, gap = 2;
                    Add(new Rectangle { Width = arm - gap, Height = cur.LineWidth, Fill = brush }, cx - arm, cy - cur.LineWidth / 2);
                    Add(new Rectangle { Width = arm - gap, Height = cur.LineWidth, Fill = brush }, cx + gap, cy - cur.LineWidth / 2);
                    Add(new Rectangle { Width = cur.LineWidth, Height = arm - gap, Fill = brush }, cx - cur.LineWidth / 2, cy - arm);
                    Add(new Rectangle { Width = cur.LineWidth, Height = arm - gap, Fill = brush }, cx - cur.LineWidth / 2, cy + gap);
                    break;
                }
                case PointerStyle.None:
                    Add(new TextBlock { Text = "no pointer", Opacity = 0.5, FontSize = 11 }, 80, 70);
                    break;
                default:
                    Add(new Ellipse { Width = r * 2, Height = cur.SizeMode == PointerSizeMode.Fixed ? r * 2 : r * 1.4, Stroke = brush, StrokeThickness = cur.LineWidth }, cx - r, cy - (cur.SizeMode == PointerSizeMode.Fixed ? r : r * 0.7));
                    break;
            }
            if (cur.CenterDot) Add(new Ellipse { Width = 3, Height = 3, Fill = brush }, cx - 1.5, cy - 1.5);
        }

        foreach (uint sw in Swatches)
        {
            uint c = sw;
            var b = new Button
            {
                Width = 26, Height = 26, Padding = new Thickness(0), Background = Brush(c),
                BorderBrush = Toolbar.Divider(), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(13),
            };
            AutomationProperties_SetName(b, Hex(c));
            b.Click += (_, _) => { custom = c; hexBox.Text = Hex(c); colorMode.SelectedIndex = 1; DrawPreview(); };
            swatchRow.Children.Add(b);
        }
        hexBox.TextChanged += (_, _) => { if (TryHex(hexBox.Text, out var v)) { custom = v; colorMode.SelectedIndex = 1; DrawPreview(); } };
        foreach (var c in new Control[] { style, sizeMode, colorMode })
            if (c is ComboBox cb) cb.SelectionChanged += (_, _) => DrawPreview();
        foreach (var sl in new[] { fixedSize, opacity, lineWidth }) sl.ValueChanged += (_, _) => DrawPreview();
        centerDot.Toggled += (_, _) => DrawPreview();

        pointerTab.Children.Add(new TextBlock { Text = "Preview (a 5 DIP pen, ink shown beside it)", Opacity = 0.7 });
        pointerTab.Children.Add(previewBox);
        pointerTab.Children.Add(style);
        pointerTab.Children.Add(sysCursor);
        pointerTab.Children.Add(sizeMode);
        pointerTab.Children.Add(fixedSize);
        pointerTab.Children.Add(colorMode);
        pointerTab.Children.Add(swatchRow);
        pointerTab.Children.Add(hexBox);
        pointerTab.Children.Add(opacity);
        pointerTab.Children.Add(lineWidth);
        pointerTab.Children.Add(centerDot);
        pointerTab.Children.Add(showWhileDrawing);
        DrawPreview();

        // ===== Touch =====
        var touchTab = Tab();
        var touchDraws = Tg("Draw with finger", s.TouchDraws, "On: two fingers scroll", "Off: one finger scrolls");
        var palmMs = Sl("Palm rejection delay after the pen leaves (ms)", 0, 1500, 50, s.PalmRejectionMs);
        var palmSize = Sl("Treat touches wider than this as a palm (DIPs)", 20, 80, 1, s.PalmSizeDips);
        var twoTap = Tg("Two-finger tap undoes", s.TwoFingerTapUndo);
        var threeTap = Tg("Three-finger tap redoes", s.ThreeFingerTapRedo);
        touchTab.Children.Add(touchDraws);
        touchTab.Children.Add(Header("Palm rejection"));
        touchTab.Children.Add(palmMs);
        touchTab.Children.Add(palmSize);
        touchTab.Children.Add(Header("Gestures"));
        touchTab.Children.Add(twoTap);
        touchTab.Children.Add(threeTap);

        // ===== Toolbar =====
        var toolbarTab = Tab();
        var dock = Combo("Toolbar position (left/right keeps tools away from your writing hand)", new[] { "Top", "Bottom", "Left", "Right" }, (int)s.ToolbarDock);
        int[] sizes = { 0, 32, 40, 48, 56 };
        var btnSize = Combo("Button size", new[] { "Automatic (larger in tablet mode)", "Small (32)", "Medium (40)", "Large (48)", "Extra large (56)" },
            Math.Max(0, Array.IndexOf(sizes, s.ToolbarButtonSize)));
        var sidebarRight = Tg("Swap sides (for left-handed use)", s.SidebarOnRight, "On: notebooks on the right, pages on the left", "Off: notebooks on the left, pages on the right");
        var railCompact = Tg("Notebook list", s.RailCompact, "Badges only (names open on tap)", "Names beside the badges when there is room");
        var pagesHidden = Tg("Page list", s.PagesHidden, "Hidden (open it from the section bar)", "Shown beside the page when there is room");
        toolbarTab.Children.Add(dock);
        toolbarTab.Children.Add(btnSize);
        toolbarTab.Children.Add(sidebarRight);
        toolbarTab.Children.Add(railCompact);
        toolbarTab.Children.Add(pagesHidden);
        toolbarTab.Children.Add(Header("Buttons on the toolbar"));
        toolbarTab.Children.Add(new TextBlock { Text = "Tick what you want, then move it up or down. Everything else stays in the ⋯ menu.", Opacity = 0.7, TextWrapping = TextWrapping.Wrap });

        var rows = new List<(string Id, bool On)>();
        void ResetRows(IEnumerable<string> visible)
        {
            rows.Clear();
            var vis = ToolbarLayout.Normalize(visible);
            foreach (var id in vis) rows.Add((id, true));
            foreach (var id in ToolbarLayout.AllIds) if (!vis.Contains(id)) rows.Add((id, false));
        }
        ResetRows(s.ToolbarItems);
        var rowHost = new StackPanel { Spacing = 2 };
        void RenderRows()
        {
            rowHost.Children.Clear();
            for (int i = 0; i < rows.Count; i++)
            {
                int idx = i;
                var (id, on) = rows[i];
                bool divider = id == ToolbarLayout.Separator;
                var g = new Grid { ColumnSpacing = 4 };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });

                if (divider)
                {
                    g.Children.Add(new FontIcon { Glyph = "", FontSize = 14, Opacity = 0.6 });
                    var t = new TextBlock { Text = "── divider ──", Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center };
                    Grid.SetColumn(t, 1); g.Children.Add(t);
                }
                else
                {
                    var cb = new CheckBox { IsChecked = on, MinWidth = 0, IsEnabled = id != "more" };
                    cb.Checked += (_, _) => rows[idx] = (id, true);
                    cb.Unchecked += (_, _) => rows[idx] = (id, false);
                    g.Children.Add(cb);
                    var info = Toolbar.Catalog.TryGetValue(id, out var ii) ? ii : null;
                    var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center, Opacity = on ? 1 : 0.55 };
                    label.Children.Add(new FontIcon { Glyph = info?.Glyph ?? "", FontSize = 14 });
                    label.Children.Add(new TextBlock { Text = info?.Label ?? id });
                    Grid.SetColumn(label, 1); g.Children.Add(label);
                }

                Button Mini(string glyph, string name, int col, Action act, bool enabled = true)
                {
                    var b = new Button { Content = new FontIcon { Glyph = glyph, FontSize = 11 }, Width = 30, Height = 28, Padding = new Thickness(0), IsEnabled = enabled };
                    AutomationProperties_SetName(b, name);
                    b.Click += (_, _) => act();
                    Grid.SetColumn(b, col); g.Children.Add(b);
                    return b;
                }
                Mini("", "Move up", 2, () => { (rows[idx], rows[idx - 1]) = (rows[idx - 1], rows[idx]); RenderRows(); }, idx > 0);
                Mini("", "Move down", 3, () => { (rows[idx], rows[idx + 1]) = (rows[idx + 1], rows[idx]); RenderRows(); }, idx < rows.Count - 1);
                if (divider) Mini("", "Remove divider", 4, () => { rows.RemoveAt(idx); RenderRows(); });
                rowHost.Children.Add(g);
            }
        }
        RenderRows();
        toolbarTab.Children.Add(rowHost);
        var rowButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var addDivider = new Button { Content = "Add divider" };
        addDivider.Click += (_, _) =>
        {
            int firstHidden = rows.FindIndex(r => !r.On && r.Id != ToolbarLayout.Separator);
            rows.Insert(firstHidden < 0 ? rows.Count : firstHidden, (ToolbarLayout.Separator, true));
            RenderRows();
        };
        var resetToolbar = new Button { Content = "Reset to default" };
        resetToolbar.Click += (_, _) => { ResetRows(ToolbarLayout.Default); RenderRows(); };
        rowButtons.Children.Add(addDivider);
        rowButtons.Children.Add(resetToolbar);
        toolbarTab.Children.Add(rowButtons);

        // ===== Appearance =====
        var appearanceTab = Tab();
        var theme = Combo("Theme", new[] { "Use system setting", "Light", "Dark" }, (int)s.Theme);
        var backdrop = Combo("Window material", new[] { "Mica (follows your wallpaper)", "Mica Alt (stronger tint)", "Acrylic (translucent)", "Solid color" }, (int)s.Backdrop);
        var invert = Tg("Dark pages in dark theme", s.InvertPagesInDarkTheme, "On (exports keep the real colors)", "Off: pages stay white");
        var oled = Tg("Pure black dark pages", s.OledBlackPages, "On: best on OLED, black pixels switch off", "Off");
        appearanceTab.Children.Add(theme);
        appearanceTab.Children.Add(backdrop);
        appearanceTab.Children.Add(invert);
        appearanceTab.Children.Add(oled);

        // ===== Saving =====
        var savingTab = Tab();
        ContentDialog? settingsDialog = null;
        bool restartRequested = false;
        string? pendingDescription = null;
        Action? pendingAction = null;

        savingTab.Children.Add(Header("Where your notebooks are stored"));
        var folderText = new TextBlock { Text = AppPaths.DataFolder, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        savingTab.Children.Add(folderText);
        savingTab.Children.Add(new TextBlock
        {
            Text = "Notebooks and settings live together in this folder. Point it at OneDrive, Dropbox or a network folder to keep them in sync between devices (open Nib on one device at a time).",
            Opacity = 0.7, TextWrapping = TextWrapping.Wrap,
        });

        // Changes that need a restart are confirmed inline: a second dialog cannot open on top of this one.
        var confirmBox = new StackPanel { Spacing = 8, Visibility = Visibility.Collapsed, Padding = new Thickness(12), CornerRadius = new CornerRadius(8) };
        confirmBox.Background = new SolidColorBrush(Colors2D.From(0x1A2B6CB0));
        var confirmText = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var confirmButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var confirmYes = new Button { Content = "Restart and apply" };
        var confirmAlt = new Button { Content = "", Visibility = Visibility.Collapsed };
        var confirmNo = new Button { Content = "Cancel" };
        confirmButtons.Children.Add(confirmYes); confirmButtons.Children.Add(confirmAlt); confirmButtons.Children.Add(confirmNo);
        confirmBox.Children.Add(confirmText); confirmBox.Children.Add(confirmButtons);
        Action? altAction = null;
        void AskRestart(string text, Action apply, string? altLabel = null, Action? alt = null)
        {
            confirmText.Text = text;
            pendingAction = apply; altAction = alt;
            confirmAlt.Content = altLabel ?? ""; confirmAlt.Visibility = altLabel is null ? Visibility.Collapsed : Visibility.Visible;
            confirmBox.Visibility = Visibility.Visible;
        }
        void Restart() { restartRequested = true; settingsDialog?.Hide(); }
        confirmYes.Click += (_, _) => { pendingAction?.Invoke(); Restart(); };
        confirmAlt.Click += (_, _) => { altAction?.Invoke(); Restart(); };
        confirmNo.Click += (_, _) => { confirmBox.Visibility = Visibility.Collapsed; pendingAction = null; };

        var folderButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var changeFolder = new Button { Content = "Change folder…" };
        var openFolder = new Button { Content = "Open folder" };
        var defaultFolderBtn = new Button { Content = "Use default" };
        defaultFolderBtn.IsEnabled = DataLocation.Configured() is not null;
        folderButtons.Children.Add(changeFolder); folderButtons.Children.Add(openFolder); folderButtons.Children.Add(defaultFolderBtn);
        savingTab.Children.Add(folderButtons);
        savingTab.Children.Add(confirmBox);

        openFolder.Click += (_, _) => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{AppPaths.DataFolder}\"") { UseShellExecute = true });
        changeFolder.Click += async (_, _) =>
        {
            var to = await ExportService.PickFolder(_windows.Hwnd);
            if (to is null) return;
            string from = AppPaths.DataFolder;
            if (string.Equals(System.IO.Path.GetFullPath(to).TrimEnd('\\'), System.IO.Path.GetFullPath(from).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            { AskRestartClear(); ShowToast("That is already the notebook folder"); return; }
            if (!IsWritableFolder(to)) { AskRestartClear(); ShowToast("Nib cannot write to that folder"); return; }
            if (DataLocation.HasNotebooks(to))
                AskRestart($"{to} already contains Nib notebooks. Switch to them? (Your current notebooks stay untouched in {from}.)",
                    () => DataLocation.ScheduleMove(from, to, copyData: false));
            else
                AskRestart($"Copy your notebooks and settings to {to} and use that folder from now on? Your current copy stays in {from} until you delete it.",
                    () => DataLocation.ScheduleMove(from, to, copyData: true),
                    "Start empty there", () => DataLocation.ScheduleMove(from, to, copyData: false));
        };
        void AskRestartClear() { confirmBox.Visibility = Visibility.Collapsed; }
        defaultFolderBtn.Click += (_, _) =>
        {
            string from = AppPaths.DataFolder, to = DataLocation.DefaultFolder;
            AskRestart(DataLocation.HasNotebooks(to)
                    ? $"Switch back to the notebooks in the default folder ({to})?"
                    : $"Copy your notebooks back to the default folder ({to}) and use it from now on?",
                () => DataLocation.ScheduleMove(from, to, copyData: !DataLocation.HasNotebooks(to)));
        };

        savingTab.Children.Add(Header("Backup"));
        savingTab.Children.Add(new TextBlock { Text = "A backup is a single .nibpkg file holding every notebook. Restoring replaces your notebooks with it and keeps a copy of the current ones.", Opacity = 0.7, TextWrapping = TextWrapping.Wrap });
        var backupButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var backUp = new Button { Content = "Back up all notebooks…" };
        var restore = new Button { Content = "Restore from backup…" };
        backupButtons.Children.Add(backUp); backupButtons.Children.Add(restore);
        savingTab.Children.Add(backupButtons);
        backUp.Click += async (_, _) =>
        {
            if (_export is null || _engine is null) return;
            try
            {
                var path = await _export.BackUpAll(_engine);
                if (path is not null) ShowToast("Backup saved: " + System.IO.Path.GetFileName(path));
            }
            catch (Exception e) { Services.CrashLog.Write(e); ShowToast("Could not back up: " + e.Message); }
        };
        restore.Click += async (_, _) =>
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker { SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary };
            picker.FileTypeFilter.Add(".nibpkg");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, _windows.Hwnd);
            var file = await picker.PickSingleFileAsync();
            if (file is null) return;
            string package = file.Path, data = AppPaths.DataFolder;
            AskRestart($"Replace your notebooks with the backup \"{file.Name}\"? A copy of your current notebooks is kept next to them.",
                () => DataLocation.ScheduleRestore(package, data));
        };

        savingTab.Children.Add(Header("Exporting pages"));
        var exportFormat = Combo("Default export format", new[] { "PDF document (vector)", "PNG image", "SVG drawing (vector)" }, (int)s.ExportFormat);
        int[] dpis = { 96, 150, 200, 300, 600 };
        var pngDpi = Combo("PNG resolution", new[] { "96 dpi (screen)", "150 dpi", "200 dpi", "300 dpi (print)", "600 dpi (very large)" }, Math.Max(0, Array.IndexOf(dpis, s.PngDpi)));
        var exportBg = Tg("Include the paper pattern (lines, grids)", s.ExportBackground);
        var askWhere = Tg("Ask where to save each export", s.AskExportLocation, "On: choose a file name and folder every time", "Off: save straight to the export folder");
        string? exportFolder = s.ExportFolder;
        var exportFolderText = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.8 };
        void ShowExportFolder() => exportFolderText.Text = "Export folder: " + (exportFolder ?? "Documents");
        ShowExportFolder();
        var exportFolderButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var chooseExport = new Button { Content = "Choose export folder…" };
        var clearExport = new Button { Content = "Use Documents" };
        exportFolderButtons.Children.Add(chooseExport); exportFolderButtons.Children.Add(clearExport);
        chooseExport.Click += async (_, _) => { var f = await ExportService.PickFolder(_windows.Hwnd); if (f is not null) { exportFolder = f; ShowExportFolder(); } };
        clearExport.Click += (_, _) => { exportFolder = null; ShowExportFolder(); };
        savingTab.Children.Add(exportFormat);
        savingTab.Children.Add(pngDpi);
        savingTab.Children.Add(exportBg);
        savingTab.Children.Add(askWhere);
        savingTab.Children.Add(exportFolderText);
        savingTab.Children.Add(exportFolderButtons);

        // ===== Shortcuts =====
        var shortcutTab = Tab();
        shortcutTab.Children.Add(new TextBlock
        {
            Text = "Click a box and press the new shortcut. Escape in a box does nothing; use Reset to go back to the default.",
            Opacity = 0.7, TextWrapping = TextWrapping.Wrap,
        });
        var gestureBoxes = new List<(string Action, string Default, TextBox Box)>();
        foreach (var (action, def) in KeyMap.Defaults)
        {
            var current = s.KeyMap.TryGetValue(action, out var ov) && !string.IsNullOrWhiteSpace(ov) ? ov : def;
            var g = new Grid { ColumnSpacing = 6 };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.Children.Add(new TextBlock { Text = ActionLabel(action), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
            var box = new TextBox { Text = current, IsReadOnly = true, Width = 150, PlaceholderText = def };
            string a = action, d = def;
            box.KeyDown += (_, e) =>
            {
                string? gesture = GestureFromKey(e.Key);
                if (gesture is null) return; // a bare modifier: wait for the real key
                e.Handled = true;
                if (e.Key == VirtualKey.Escape) return;
                box.Text = gesture;
            };
            Grid.SetColumn(box, 1); g.Children.Add(box);
            var reset = new Button { Content = new FontIcon { Glyph = "", FontSize = 11 }, Width = 30, Height = 28, Padding = new Thickness(0) };
            AutomationProperties_SetName(reset, "Reset " + ActionLabel(action));
            reset.Click += (_, _) => box.Text = d;
            Grid.SetColumn(reset, 2); g.Children.Add(reset);
            gestureBoxes.Add((a, d, box));
            shortcutTab.Children.Add(g);
        }
        var shortcutError = new TextBlock { Foreground = Brush(0xFFD13438), TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };

        // ===== Device =====
        var deviceTab = Tab();
        deviceTab.Children.Add(new TextBlock
        {
            Text = string.Join("\n", DeviceTuner.Describe(_device, s)),
            TextWrapping = TextWrapping.Wrap, Opacity = 0.8, IsTextSelectionEnabled = true,
        });
        deviceTab.Children.Add(Header("About"));
        deviceTab.Children.Add(new TextBlock
        {
            Text = $"Nib {typeof(MainWindow).Assembly.GetName().Version?.ToString(3)} · data: {AppPaths.DataFolder}",
            TextWrapping = TextWrapping.Wrap, Opacity = 0.7, IsTextSelectionEnabled = true,
        });

        // ===== Assemble =====
        var panels = new[] { penTab, pointerTab, touchTab, toolbarTab, appearanceTab, savingTab, shortcutTab, deviceTab };
        string[] glyphs = { "", "", "", "", "", "", "", "" };

        // Windows 11 Settings layout: a navigation list on the left, the chosen page on the right.
        var content = new ContentPresenter { HorizontalAlignment = HorizontalAlignment.Stretch };
        var nav = new ListView { SelectionMode = ListViewSelectionMode.Single, Width = 170, VerticalAlignment = VerticalAlignment.Top };
        AutomationProperties_SetName(nav, "Settings sections");
        for (int i = 0; i < panels.Length; i++)
        {
            var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Padding = new Thickness(0, 6, 0, 6) };
            label.Children.Add(new FontIcon { Glyph = glyphs[i], FontSize = 16 });
            label.Children.Add(new TextBlock { Text = SettingsTabs[i], VerticalAlignment = VerticalAlignment.Center });
            var navItem = new ListViewItem { Content = label };
            AutomationProperties_SetName(navItem, SettingsTabs[i]);
            nav.Items.Add(navItem);
        }
        var scroller = new ScrollViewer { MaxHeight = 520, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalAlignment = HorizontalAlignment.Stretch };
        content.Content = scroller;
        void ShowTab(int i) { i = Math.Clamp(i, 0, panels.Length - 1); if (nav.SelectedIndex != i) nav.SelectedIndex = i; scroller.Content = panels[i]; scroller.ChangeView(0, 0, null, true); }
        nav.SelectionChanged += (_, _) => { if (nav.SelectedIndex >= 0) scroller.Content = panels[nav.SelectedIndex]; };
        ShowTab(openTab is null ? 0 : Array.IndexOf(SettingsTabs, openTab));

        var split = new Grid { ColumnSpacing = 20, MinWidth = 760 };
        split.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        split.Children.Add(nav);
        Grid.SetColumn(content, 1); split.Children.Add(content);

        var host = new StackPanel { Spacing = 4 };
        host.Children.Add(split);
        host.Children.Add(shortcutError);
        var dialog = new ContentDialog
        {
            Title = "Settings",
            Content = host,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Root.XamlRoot, RequestedTheme = Root.ActualTheme,
        };
        dialog.Resources["ContentDialogMaxWidth"] = 960.0;
        dialog.Resources["ContentDialogMinWidth"] = 760.0;
        dialog.PrimaryButtonClick += (_, e) =>
        {
            string? problem = FindShortcutConflict(gestureBoxes.Select(b => (b.Action, b.Box.Text)));
            shortcutError.Visibility = problem is null ? Visibility.Collapsed : Visibility.Visible;
            shortcutError.Text = problem ?? "";
            if (problem is not null) { e.Cancel = true; ShowTab(Array.IndexOf(SettingsTabs, "Shortcuts")); }
        };
        settingsDialog = dialog;
        var shown = await dialog.ShowAsync();
        if (shown != ContentDialogResult.Primary && !restartRequested) return;

        // ===== Apply =====
        s.EraserMode = (EraserMode)Math.Max(0, eraserMode.SelectedIndex);
        s.EraserSize = (float)eraserSize.Value;
        s.BarrelButton = (BarrelButtonAction)Math.Max(0, barrel.SelectedIndex);
        s.Tilt = new TiltSettings { Enabled = tiltOn.IsOn, DeadZoneDeg = (float)tiltDead.Value, InvertX = tiltFlipX.IsOn, InvertY = tiltFlipY.IsOn };
        s.ShapeSnap = snapOn.IsOn;
        s.ShapeSnapHoldMs = (int)snapHold.Value;
        s.SnapLineAngles = snapAngles.IsOn;
        s.AutoPrediction = autoPrediction.IsOn;
        s.PredictionMs = (float)prediction.Value;
        s.PerformanceModeWhileWriting = perfMode.IsOn;

        s.Pointer = ReadPointer();
        UpdateSystemCursor();

        s.TouchDraws = touchDraws.IsOn;
        s.PalmRejectionMs = (int)palmMs.Value;
        s.PalmSizeDips = (float)palmSize.Value;
        s.TwoFingerTapUndo = twoTap.IsOn;
        s.ThreeFingerTapRedo = threeTap.IsOn;

        s.ToolbarButtonSize = sizes[Math.Clamp(btnSize.SelectedIndex, 0, sizes.Length - 1)];
        s.ToolbarItems = ToolbarLayout.Normalize(rows.Where(r => r.On).Select(r => r.Id));
        s.SidebarOnRight = sidebarRight.IsOn;
        s.RailCompact = railCompact.IsOn;
        s.PagesHidden = pagesHidden.IsOn;

        s.Theme = (ThemeMode)Math.Max(0, theme.SelectedIndex);
        s.Backdrop = (BackdropKind)Math.Max(0, backdrop.SelectedIndex);
        s.InvertPagesInDarkTheme = invert.IsOn;
        s.OledBlackPages = oled.IsOn;

        s.ExportFormat = (ExportFormat)Math.Clamp(exportFormat.SelectedIndex, 0, 2);
        s.PngDpi = dpis[Math.Clamp(pngDpi.SelectedIndex, 0, dpis.Length - 1)];
        s.ExportBackground = exportBg.IsOn;
        s.AskExportLocation = askWhere.IsOn;
        s.ExportFolder = exportFolder;

        s.KeyMap.Clear();
        foreach (var (action, def, box) in gestureBoxes)
            if (!string.Equals(box.Text, def, StringComparison.OrdinalIgnoreCase)) s.KeyMap[action] = box.Text;
        ReRegisterAccelerators();

        ApplyDock((DockSide)Math.Max(0, dock.SelectedIndex)); // rebuilds the toolbar with the new buttons and size
        _lastEffectiveDock = (DockSide)255;
        ApplyBackdrop();
        ApplyResponsiveLayout();
        ApplyDeviceTuning(); // also pushes the tool state (pointer, palm rejection, eraser)
        BuildOverflowMenu(); // its export format and background toggles read these settings
        _settings.SaveSoon();
        if (restartRequested) await RestartNowAsync();
    }

    // ---- shortcuts -----------------------------------------------------------------------------

    private static string ActionLabel(string action) => action switch
    {
        "NewPage" => "New page",
        "NewSubpage" => "New subpage",
        "NewSection" => "New section",
        "Undo" => "Undo",
        "Redo" => "Redo",
        "NextPage" => "Next page",
        "PreviousPage" => "Previous page",
        "DemotePage" => "Make page a subpage",
        "PromotePage" => "Promote subpage",
        "ZoomIn" => "Zoom in",
        "ZoomOut" => "Zoom out",
        "Zoom100" => "Zoom to 100%",
        "FullPage" => "Focus mode",
        "Copy" => "Copy",
        "Cut" => "Cut",
        "Paste" => "Paste",
        "Delete" => "Delete selection",
        "ExportPdf" => "Export page as PDF",
        "ToggleSidebar" => "Show / hide sidebar",
        "DebugOverlay" => "Debug overlay",
        _ when action.StartsWith("Tag", StringComparison.Ordinal) && int.TryParse(action.AsSpan(3), out int n) =>
            TagDefinition.BuiltIn.FirstOrDefault(t => t.Shortcut == n)?.Name is { } name ? $"Tag: {name}" : $"Custom tag {n}",
        _ => action,
    };

    /// <summary>Turns the key just pressed plus the held modifiers into a gesture string ("Ctrl+Shift+N"), or null for a bare modifier.</summary>
    private static string? GestureFromKey(VirtualKey key)
    {
        if (key is VirtualKey.Control or VirtualKey.Shift or VirtualKey.Menu or VirtualKey.LeftControl or VirtualKey.RightControl
            or VirtualKey.LeftShift or VirtualKey.RightShift or VirtualKey.LeftMenu or VirtualKey.RightMenu
            or VirtualKey.LeftWindows or VirtualKey.RightWindows or VirtualKey.None) return null;
        static bool Down(VirtualKey k) => InputKeyboardSource.GetKeyStateForCurrentThread(k).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        var parts = new List<string>();
        if (Down(VirtualKey.Control)) parts.Add("Ctrl");
        if (Down(VirtualKey.Menu)) parts.Add("Alt");
        if (Down(VirtualKey.Shift)) parts.Add("Shift");
        int k = (int)key;
        string name = k switch
        {
            187 => "Plus",
            189 => "Minus",
            219 => "[",
            221 => "]",
            >= 48 and <= 57 => ((char)k).ToString(),
            >= 65 and <= 90 => ((char)k).ToString(),
            _ => key.ToString(),
        };
        parts.Add(name);
        return string.Join("+", parts);
    }

    private static string? FindShortcutConflict(IEnumerable<(string Action, string Gesture)> all)
    {
        var seen = new Dictionary<(VirtualKeyModifiers, VirtualKey), string>();
        foreach (var (action, gesture) in all)
        {
            if (!KeyMap.TryParse(gesture, out var mods, out var key) || key == VirtualKey.None)
                return $"\"{gesture}\" is not a valid shortcut for {ActionLabel(action)}.";
            if (seen.TryGetValue((mods, key), out var other))
                return $"{gesture} is used by both {ActionLabel(other)} and {ActionLabel(action)}.";
            seen[(mods, key)] = action;
        }
        return null;
    }

    private void ReRegisterAccelerators()
    {
        foreach (var (_, acc) in _accelerators) Root.KeyboardAccelerators.Remove(acc);
        _accelerators.Clear();
        RegisterAccelerators();
    }

    // ---- restarting after a location change ----

    private static bool IsWritableFolder(string folder)
    {
        try
        {
            string probe = System.IO.Path.Combine(folder, $".nib-write-test-{Guid.NewGuid():N}");
            System.IO.File.WriteAllText(probe, "x");
            System.IO.File.Delete(probe);
            return true;
        }
        catch (Exception) { return false; }
    }

    /// <summary>Saves everything, starts a new Nib that waits for this one to exit, and closes this window.</summary>
    private async Task RestartNowAsync()
    {
        _settings.SaveNow();
        if (_storage is not null) await Task.Run(() => _storage.Flush(TimeSpan.FromSeconds(5)));
        DataLocation.LaunchSuccessor();
        Close();
    }
}
