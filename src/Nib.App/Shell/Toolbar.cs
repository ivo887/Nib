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
    private readonly List<ToggleButton> _penButtons = new();
    private ToggleButton? _highlighter, _eraser, _lasso, _text;
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

    public Toolbar()
    {
        Content = _panel;
        AutomationProperties.SetName(this, "Tools");
    }

    public Orientation Orientation
    {
        get => _panel.Orientation;
        set
        {
            _panel.Orientation = value;
            Overflow.Placement = value == Orientation.Horizontal ? FlyoutPlacementMode.Bottom : FlyoutPlacementMode.Right;
        }
    }

    public void Build(IReadOnlyList<PenPreset> pens, PenPreset highlighter, ToolKind kind, int activePen)
    {
        _pens = pens;
        _highlighterPreset = highlighter;
        _panel.Children.Clear();
        _penButtons.Clear();
        for (int i = 0; i < pens.Count; i++)
        {
            int idx = i;
            var b = ToolButton(PenGlyph(pens[i].Kind), pens[i].Color, $"{pens[i].Kind} pen {i + 1}");
            b.Click += (_, _) => OnToolClick(ToolKind.Pen, idx, b);
            _penButtons.Add(b);
            _panel.Children.Add(b);
        }
        var add = ActionButton("\uE710", "Add pen", small: true);
        add.Click += (_, _) => AddPenClicked?.Invoke();
        _panel.Children.Add(add);

        _panel.Children.Add(Separator());
        _highlighter = ToolButton("\uE7E6", highlighter.Color, "Highlighter");
        _highlighter.Click += (_, _) => OnToolClick(ToolKind.Highlighter, 0, _highlighter);
        _eraser = ToolButton("\uE75C", null, "Eraser");
        _eraser.Click += (_, _) => OnToolClick(ToolKind.Eraser, 0, _eraser);
        _lasso = ToolButton("\uE8B3", null, "Lasso select");
        _lasso.Click += (_, _) => OnToolClick(ToolKind.Lasso, 0, _lasso);
        _text = ToolButton("\uE8D2", null, "Type text");
        _text.Click += (_, _) => OnToolClick(ToolKind.Text, 0, _text);
        _panel.Children.Add(_highlighter);
        _panel.Children.Add(_eraser);
        _panel.Children.Add(_lasso);
        _panel.Children.Add(_text);

        _panel.Children.Add(Separator());
        _undo = ActionButton("\uE7A7", "Undo (Ctrl+Z)");
        _undo.Click += (_, _) => UndoClicked?.Invoke();
        _redo = ActionButton("\uE7A6", "Redo (Ctrl+Y)");
        _redo.Click += (_, _) => RedoClicked?.Invoke();
        _undo.IsEnabled = _redo.IsEnabled = false;
        _panel.Children.Add(_undo);
        _panel.Children.Add(_redo);

        var more = ActionButton("\uE712", "More");
        more.Flyout = Overflow;
        _panel.Children.Add(more);
        ApplyButtonSize();
        SetActive(kind, activePen);
    }

    /// <summary>Button edge in DIPs: 40 normally, 48 (touch-sized) when a convertible is in tablet posture.</summary>
    public double ButtonSize { get; set; } = 40;

    private void ApplyButtonSize()
    {
        if (ButtonSize == 40) return; // the styles' default
        foreach (var child in _panel.Children)
        {
            if (child is not ButtonBase b) continue;
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
        _updating = false;
    }

    private void OnToolClick(ToolKind kind, int index, ToggleButton button)
    {
        if (_updating) return;
        bool wasActive = ActiveKind == kind && (kind != ToolKind.Pen || ActivePen == index);
        SetActive(kind, index);
        if (wasActive && kind != ToolKind.Lasso && kind != ToolKind.Text) ToolSettingsRequested?.Invoke(kind, index, button);
        else ToolSelected?.Invoke(kind, index);
    }

    public static string PenGlyph(PenKind k) => k switch
    {
        PenKind.Fountain => "\uEDFB",
        PenKind.Pencil => "\uE70F",
        PenKind.Highlighter => "\uE7E6",
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
        b.CornerRadius = new CornerRadius(8); b.BorderThickness = new Thickness(0);
        b.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
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
