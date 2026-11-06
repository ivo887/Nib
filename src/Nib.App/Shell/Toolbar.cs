using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Nib.App.Ink;
using Nib.Core.Ink;

namespace Nib.App.Shell;

/// <summary>
/// The only toolbar: favorite pens, highlighter, eraser, lasso, text, undo/redo and one overflow menu.
/// Built in code so it can flip between horizontal and vertical docking without re-templating.
/// Tapping the active tool again opens its settings.
/// </summary>
public sealed partial class Toolbar : UserControl
{
    private readonly StackPanel _panel = new() { Spacing = 0 };
    /// <summary>The rounded "pill" the buttons sit in: a translucent layer with a hairline border (Windows 11 style).</summary>
    private readonly Border _chrome = new() { CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Padding = new Thickness(2, 0, 2, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly List<ToggleButton> _penButtons = new();
    private ToggleButton? _highlighter, _eraser, _lasso, _text, _insertSpace, _snap;
    private Button? _undo, _redo;
    private IReadOnlyList<PenPreset> _pens = Array.Empty<PenPreset>();
    private PenPreset _highlighterPreset = PenPreset.DefaultHighlighter();
    private bool _updating;

    public ToolKind ActiveKind { get; private set; } = ToolKind.Pen;
    public int ActivePen { get; private set; }
    public MenuFlyout Overflow { get; } = new() { Placement = FlyoutPlacementMode.Bottom };

    public event Action<ToolKind, int>? ToolSelected;
    /// <summary>The active tool was tapped again: show its settings anchored to the button.</summary>
    public event Action<ToolKind, int, FrameworkElement>? ToolSettingsRequested;
    public event Action? UndoClicked, RedoClicked, AddPenClicked;
    /// <summary>The shape-snapping switch was toggled on the toolbar.</summary>
    public event Action<bool>? SnapToggled;
    public bool ShapeSnapOn { get; set; } = true;

    /// <summary>Text on the navigator button (the open page).</summary>
    public string NavLabel
    {
        get => _navLabel;
        set { _navLabel = value; if (_navText is not null) _navText.Text = value; }
    }
    private string _navLabel = "Page";
    private TextBlock? _navText;

    /// <summary>True while the toolbar floats over the page as the full-screen dock: a firmer surface so it reads over ink.</summary>
    public bool Floating
    {
        get => _floating;
        set { _floating = value; UpdateChrome(); }
    }
    private bool _floating;

    /// <summary>A command button (zoom, page setup, export, focus, sidebar, settings) was pressed; the anchor is for flyouts.</summary>
    public event Action<string, FrameworkElement>? CommandInvoked;

    /// <summary>Ordered button ids (see <see cref="Nib.Core.Model.ToolbarLayout"/>). Applied on the next <see cref="Build"/>.</summary>
    public IReadOnlyList<string> Items { get; set; } = Nib.Core.Model.ToolbarLayout.Default;

    public Toolbar()
    {
        _chrome.Child = _panel;
        Content = _chrome;
        AutomationProperties.SetName(this, "Tools");
        Loaded += (_, _) => UpdateChrome();
        ActualThemeChanged += (_, _) => UpdateChrome();
    }

    public Orientation Orientation
    {
        get => _panel.Orientation;
        set
        {
            _panel.Orientation = value;
            _chrome.Padding = value == Orientation.Horizontal ? new Thickness(2, 0, 2, 0) : new Thickness(0, 2, 0, 2);
            Overflow.Placement = value == Orientation.Horizontal ? FlyoutPlacementMode.Bottom : FlyoutPlacementMode.Right;
        }
    }

    public void Build(IReadOnlyList<PenPreset> pens, PenPreset highlighter, ToolKind kind, int activePen)
    {
        _pens = pens;
        _highlighterPreset = highlighter;
        _panel.Children.Clear();
        _penButtons.Clear();
        _highlighter = _eraser = _lasso = _text = _insertSpace = _snap = null;
        _navText = null;
        _undo = _redo = null;

        foreach (var id in Items)
        {
            switch (id)
            {
                case Nib.Core.Model.ToolbarLayout.Separator:
                    _panel.Children.Add(Separator());
                    break;
                case "pens":
                    for (int i = 0; i < pens.Count; i++)
                    {
                        int idx = i;
                        var b = ToolButton(PenGlyph(pens[i].Kind), pens[i].Color, $"{pens[i].Kind} pen {i + 1}");
                        b.Click += (_, _) => OnToolClick(ToolKind.Pen, idx, b);
                        _penButtons.Add(b);
                        _panel.Children.Add(b);
                    }
                    break;
                case "add-pen":
                    var add = ActionButton("", "Add pen", small: false);
                    add.Click += (_, _) => AddPenClicked?.Invoke();
                    _panel.Children.Add(add);
                    break;
                case "highlighter":
                    _highlighter = ToolButton("", highlighter.Color, "Highlighter");
                    _highlighter.Click += (_, _) => OnToolClick(ToolKind.Highlighter, 0, _highlighter);
                    _panel.Children.Add(_highlighter);
                    break;
                case "eraser":
                    _eraser = ToolButton("", null, "Eraser");
                    _eraser.Click += (_, _) => OnToolClick(ToolKind.Eraser, 0, _eraser);
                    _panel.Children.Add(_eraser);
                    break;
                case "lasso":
                    _lasso = ToolButton("", null, "Lasso select");
                    _lasso.Click += (_, _) => OnToolClick(ToolKind.Lasso, 0, _lasso);
                    _panel.Children.Add(_lasso);
                    break;
                case "text":
                    _text = ToolButton("", null, "Type text");
                    _text.Click += (_, _) => OnToolClick(ToolKind.Text, 0, _text);
                    _panel.Children.Add(_text);
                    break;
                case "insert-space":
                    _insertSpace = ToolButton("", null, "Insert space");
                    _insertSpace.Click += (_, _) => OnToolClick(ToolKind.InsertSpace, 0, _insertSpace);
                    _panel.Children.Add(_insertSpace);
                    break;
                case "snap":
                    _snap = ToolButton("", null, "Snap to shapes and lines (hold the pen still at the end of a stroke)");
                    _snap.IsChecked = ShapeSnapOn;
                    _snap.Click += (_, _) => { bool on = _snap.IsChecked == true; ShapeSnapOn = on; SnapToggled?.Invoke(on); };
                    _panel.Children.Add(_snap);
                    break;
                case "nav":
                {
                    var face = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                    face.Children.Add(new FontIcon { Glyph = "\uE8F1", FontSize = 15 });
                    if (_panel.Orientation == Orientation.Horizontal)
                    {
                        _navText = new TextBlock { Text = _navLabel, MaxWidth = 170, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
                        face.Children.Add(_navText);
                        face.Children.Add(new FontIcon { Glyph = "\uE70D", FontSize = 10, VerticalAlignment = VerticalAlignment.Center });
                    }
                    var navBtn = new Button { Content = face };
                    StyleToolButton(navBtn);
                    navBtn.Width = double.NaN; navBtn.Padding = new Thickness(10, 0, 10, 0);
                    ToolTipService.SetToolTip(navBtn, "Notebooks, sections and pages");
                    AutomationProperties.SetName(navBtn, "Notebooks, sections and pages");
                    navBtn.Click += (_, _) => CommandInvoked?.Invoke("nav", navBtn);
                    _panel.Children.Add(navBtn);
                    break;
                }
                case "undo":
                    _undo = ActionButton("", "Undo (Ctrl+Z)");
                    _undo.Click += (_, _) => UndoClicked?.Invoke();
                    _undo.IsEnabled = false;
                    _panel.Children.Add(_undo);
                    break;
                case "redo":
                    _redo = ActionButton("", "Redo (Ctrl+Y)");
                    _redo.Click += (_, _) => RedoClicked?.Invoke();
                    _redo.IsEnabled = false;
                    _panel.Children.Add(_redo);
                    break;
                case "more":
                    var more = ActionButton("", "More");
                    more.Flyout = Overflow;
                    _panel.Children.Add(more);
                    break;
                default:
                    if (Catalog.TryGetValue(id, out var info))
                    {
                        string cmd = id;
                        var btn = ActionButton(info.Glyph, info.Label);
                        btn.Click += (_, _) => CommandInvoked?.Invoke(cmd, btn);
                        _panel.Children.Add(btn);
                    }
                    break;
            }
        }
        ApplyButtonSize();
        SetActive(kind, activePen);
    }

    /// <summary>Everything that can sit on the toolbar, for the customise screen.</summary>
    public sealed record ItemInfo(string Id, string Label, string Glyph);

    public static readonly IReadOnlyDictionary<string, ItemInfo> Catalog = new Dictionary<string, ItemInfo>
    {
        ["pens"] = new("pens", "Favorite pens", ""),
        ["add-pen"] = new("add-pen", "Add pen (+)", ""),
        ["highlighter"] = new("highlighter", "Highlighter", ""),
        ["eraser"] = new("eraser", "Eraser", ""),
        ["lasso"] = new("lasso", "Lasso select", ""),
        ["text"] = new("text", "Type text", ""),
        ["snap"] = new("snap", "Snap to shapes and lines", ""),
        ["nav"] = new("nav", "Notebook / page navigator", ""),
        ["exit-focus"] = new("exit-focus", "Exit full screen", ""),
        ["undo"] = new("undo", "Undo", ""),
        ["redo"] = new("redo", "Redo", ""),
        ["zoom"] = new("zoom", "Zoom", ""),
        ["insert-space"] = new("insert-space", "Insert space", ""),
        ["page-setup"] = new("page-setup", "Page setup", ""),
        ["export"] = new("export", "Export PDF", ""),
        ["focus"] = new("focus", "Focus mode", ""),
        ["sidebar"] = new("sidebar", "Notebooks sidebar", ""),
        ["settings"] = new("settings", "Settings", ""),
        ["more"] = new("more", "More menu (always shown)", ""),
    };

    /// <summary>Button edge in DIPs: 40 normally, 48 (touch-sized) when a convertible is in tablet posture.</summary>
    public double ButtonSize { get; set; } = 40;

    private void ApplyButtonSize()
    {
        if (ButtonSize == 40) return; // the default size set by StyleToolButton
        for (int i = 0; i < _panel.Children.Count; i++)
        {
            if (_panel.Children[i] is not ButtonBase b) continue;
            bool small = b.Width == 28; // the "add pen" button stays narrow
            if (!small) b.Width = ButtonSize;
            b.Height = ButtonSize;
        }
    }

    public void SetUndoState(bool canUndo, bool canRedo)
    {
        if (_undo is not null) _undo.IsEnabled = canUndo;
        if (_redo is not null) _redo.IsEnabled = canRedo;
    }

    public void SetActive(ToolKind kind, int pen)
    {
        _updating = true;
        ActiveKind = kind;
        ActivePen = pen;
        for (int i = 0; i < _penButtons.Count; i++) _penButtons[i].IsChecked = kind == ToolKind.Pen && i == pen;
        if (_highlighter is not null) _highlighter.IsChecked = kind == ToolKind.Highlighter;
        if (_eraser is not null) _eraser.IsChecked = kind == ToolKind.Eraser;
        if (_lasso is not null) _lasso.IsChecked = kind == ToolKind.Lasso;
        if (_text is not null) _text.IsChecked = kind == ToolKind.Text;
        if (_insertSpace is not null) _insertSpace.IsChecked = kind == ToolKind.InsertSpace;
        _updating = false;
    }

    private void OnToolClick(ToolKind kind, int index, ToggleButton button)
    {
        if (_updating) return;
        bool wasActive = ActiveKind == kind && (kind != ToolKind.Pen || ActivePen == index);
        SetActive(kind, index);
        if (wasActive && kind is not (ToolKind.Lasso or ToolKind.Text or ToolKind.InsertSpace)) ToolSettingsRequested?.Invoke(kind, index, button);
        else ToolSelected?.Invoke(kind, index);
    }

    public static string PenGlyph(PenKind k) => k switch
    {
        PenKind.Fountain => "\uEDFB",
        PenKind.Pencil => "\uE70F",
        PenKind.Highlighter => "\uE7E6",
        PenKind.Brush => "\uE790",
        PenKind.Calligraphy => "\uEDC6",
        PenKind.Marker => "\uED64",
        PenKind.Watercolor => "\uEDA8",
        _ => "\uE76D",
    };

    private static ToggleButton ToolButton(string glyph, uint? color, string name)
    {
        var grid = new Grid();
        var icon = new FontIcon { Glyph = glyph, FontSize = 18 };
        grid.Children.Add(icon);
        if (color is { } c)
        {
            grid.Children.Add(new Ellipse
            {
                Width = 9, Height = 9,
                Fill = new SolidColorBrush(Colors2D.From(c)),
                Stroke = new SolidColorBrush(Colors2D.From(0x40000000)),
                StrokeThickness = 0.5,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, -6, -6),
            });
        }
        var b = new ToggleButton { Content = grid };
        StyleToolButton(b);
        ToolTipService.SetToolTip(b, name);
        AutomationProperties.SetName(b, name);
        return b;
    }

    // Styles are applied as plain properties: a Style fetched from Application.Resources comes back as a
    // bare DependencyObject under Native AOT, and the (Style) cast throws InvalidCastException.
    internal static void StyleToolButton(ButtonBase b)
    {
        b.Width = 40; b.Height = 40; b.Padding = new Thickness(0); b.Margin = new Thickness(2);
        b.CornerRadius = new CornerRadius(6); b.BorderThickness = new Thickness(0);
        b.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
    }

    private void UpdateChrome()
    {
        bool dark = ActualTheme == ElementTheme.Dark;
        _chrome.Background = new SolidColorBrush(_floating
            ? (dark ? Windows.UI.Color.FromArgb(0xEE, 0x2B, 0x2B, 0x2B) : Windows.UI.Color.FromArgb(0xEE, 0xF9, 0xF9, 0xF9))
            : (dark ? Windows.UI.Color.FromArgb(0x4C, 0x3A, 0x3A, 0x3A) : Windows.UI.Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)));
        _chrome.CornerRadius = new CornerRadius(_floating ? 14 : 10);
        _chrome.BorderBrush = new SolidColorBrush(dark ? Windows.UI.Color.FromArgb(0x19, 0xFF, 0xFF, 0xFF) : Windows.UI.Color.FromArgb(0x1A, 0x00, 0x00, 0x00));
    }

    /// <summary>Hairline color that reads on both light and dark surfaces.</summary>
    internal static Brush Divider() => new SolidColorBrush(Windows.UI.Color.FromArgb(0x40, 0x80, 0x80, 0x80));

    private static Button ActionButton(string glyph, string name, bool small = false)
    {
        var b = new Button
        {
            Content = new FontIcon { Glyph = glyph, FontSize = small ? 12 : 16 },
        };
        StyleToolButton(b);
        if (small) { b.Width = 28; }
        ToolTipService.SetToolTip(b, name);
        AutomationProperties.SetName(b, name);
        return b;
    }

    private UIElement Separator() => new Rectangle
    {
        Width = _panel.Orientation == Orientation.Horizontal ? 1 : 24,
        Height = _panel.Orientation == Orientation.Horizontal ? 24 : 1,
        Margin = new Thickness(4),
        Fill = Divider(),
        VerticalAlignment = VerticalAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center,
    };
}
