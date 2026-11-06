using System;
using System.Collections.Generic;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Nib.App.Ink;
using Nib.Core.Ink;
using Nib.Core.Model;

namespace Nib.App.Shell;

/// <summary>Settings flyouts for pens, highlighter and eraser. Every change applies to the next stroke.</summary>
internal static class ToolFlyouts
{
    public static readonly uint[] PenColors =
    {
        0xFF1F1F1F, 0xFF5F6368, 0xFF1957C9, 0xFF0F9D58, 0xFFC62828, 0xFFE8710A,
        0xFF8E24AA, 0xFF00838F, 0xFF795548, 0xFF14213D, 0xFFD81B60, 0xFF7CB342,
    };

    public static readonly uint[] HighlighterColors =
    {
        0xFFFFE135, 0xFF7CF57C, 0xFF6EC6FF, 0xFFFF8AD8, 0xFFFFA94D, 0xFFB388FF,
    };

    /// <summary>Pen or highlighter settings with a live preview stroke drawn by the real ink engine.</summary>
    public static Flyout ForPen(PenPreset preset, bool highlighter, Action<PenPreset> changed, Action? remove)
    {
        var current = preset;
        var root = new StackPanel { Spacing = 10, Width = 280 };
        var preview = new CanvasControl { Height = 64, ClearColor = Colors2D.From(0xFFFFFFFF) };
        preview.Draw += (s, e) =>
        {
            var pts = new InkPoint[90];
            for (int i = 0; i < pts.Length; i++)
            {
                float t = i / (float)(pts.Length - 1);
                float x = 14 + t * ((float)s.ActualWidth - 28);
                float y = 32 + MathF.Sin(t * MathF.PI * 2.2f) * 14;
                float p = 0.1f + 0.9f * MathF.Sin(t * MathF.PI);
                // Lean the pen over to one side and sweep its direction, so every tilt-sensitive nib shows what it does.
                pts[i] = new InkPoint(x, y, p, 55f * t, 30f * MathF.Sin(t * MathF.PI), -1, i * 6);
            }
            var sink = new Win2DRunSink(e.DrawingSession);
            new StrokeGeometryBuilder().Build(current.Kind, current.Color, current.Width, current.Dynamics, StrokeFlags.HasPressure | StrokeFlags.HasTilt, pts, sink);
            var visual = sink.TakeVisual();
            for (int i = 0; i < visual.Geometries.Length; i++)
            {
                var run = visual.Runs[i];
                if (run.Multiply) e.DrawingSession.Blend = Microsoft.Graphics.Canvas.CanvasBlend.Min;
                e.DrawingSession.FillGeometry(visual.Geometries[i], Colors2D.From(run.FillColor));
                e.DrawingSession.Blend = Microsoft.Graphics.Canvas.CanvasBlend.SourceOver;
            }
            visual.Dispose();
        };
        root.Children.Add(new Border { Child = preview, CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), BorderBrush = Toolbar.Divider() });

        bool syncing = false; // true while the flyout itself moves the sliders (a nib change), so they don't echo back
        Slider? widthSlider = null, thinSlider = null, tiltSlider = null;
        ToggleSwitch? pressureSwitch = null;
        void Update(PenPreset p)
        {
            current = p;
            preview.Invalidate();
            changed(p);
        }
        void SyncControls()
        {
            syncing = true;
            if (widthSlider is not null)
            {
                var (lo, hi) = WidthRange(current.Kind);
                widthSlider.Minimum = lo; widthSlider.Maximum = hi;
                widthSlider.Value = Math.Clamp(current.Width, lo, hi);
            }
            if (thinSlider is not null) thinSlider.Value = current.Dynamics.MinWidthRatio * 100;
            if (tiltSlider is not null) tiltSlider.Value = current.Dynamics.TiltSensitivity * 100;
            if (pressureSwitch is not null) pressureSwitch.IsOn = current.Dynamics.PressureEnabled;
            syncing = false;
        }

        if (!highlighter)
        {
            var kinds = new VariableSizedWrapGrid { Orientation = Orientation.Horizontal, MaximumRowsOrColumns = 4, ItemWidth = 68, ItemHeight = 58 };
            foreach (var k in new[] { PenKind.Ballpoint, PenKind.Fountain, PenKind.Pencil, PenKind.Brush, PenKind.Calligraphy, PenKind.Marker, PenKind.Watercolor })
            {
                var face = new StackPanel { Spacing = 2, HorizontalAlignment = HorizontalAlignment.Center };
                face.Children.Add(new FontIcon { Glyph = Toolbar.PenGlyph(k), FontSize = 16, HorizontalAlignment = HorizontalAlignment.Center });
                face.Children.Add(new TextBlock { Text = NibLabel(k), FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center });
                var tb = new ToggleButton { Content = face, IsChecked = current.Kind == k, Width = 64, Height = 54, Padding = new Thickness(0) };
                ToolTipService.SetToolTip(tb, NibHint(k));
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(tb, k.ToString());
                tb.Click += (_, _) =>
                {
                    for (int ci = 0; ci < kinds.Children.Count; ci++) if (kinds.Children[ci] is ToggleButton other) other.IsChecked = ReferenceEquals(other, tb);
                    Update(current.WithKind(k)); // the new nib arrives with its own width and feel
                    SyncControls();
                };
                kinds.Children.Add(tb);
            }
            root.Children.Add(kinds);
        }

        var palette = new VariableSizedWrapGrid { Orientation = Orientation.Horizontal, MaximumRowsOrColumns = 6, ItemWidth = 44, ItemHeight = 36 };
        var swatches = new List<(Button Button, uint Color, FontIcon Check)>();
        var selectedRing = new SolidColorBrush(Colors2D.From(0xFF2B6CB0));
        var idleRing = new SolidColorBrush(Colors2D.From(0x33808080));
        void MarkSelectedColor()
        {
            foreach (var (button, c, check) in swatches)
            {
                bool on = c == (current.Color | 0xFF000000);
                check.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
                button.BorderBrush = on ? selectedRing : idleRing;
                button.BorderThickness = new Thickness(on ? 2 : 1);
            }
        }
        foreach (uint c in highlighter ? HighlighterColors : PenColors)
        {
            uint color = c;
            // A tick on the chosen swatch, dark or light so it reads on any color.
            float lum = (((c >> 16) & 0xFF) * 0.299f + ((c >> 8) & 0xFF) * 0.587f + (c & 0xFF) * 0.114f) / 255f;
            var check = new FontIcon { Glyph = "\uE73E", FontSize = 12, Foreground = new SolidColorBrush(Colors2D.From(lum > 0.6f ? 0xFF000000 : 0xFFFFFFFF)), Visibility = Visibility.Collapsed };
            var face = new Grid();
            face.Children.Add(new Microsoft.UI.Xaml.Shapes.Ellipse { Width = 20, Height = 20, Fill = new SolidColorBrush(Colors2D.From(c)) });
            face.Children.Add(check);
            var b = new Button { Width = 38, Height = 30, Padding = new Thickness(0), Content = face };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(b, Nib.Core.Rendering.ColorUtil.ToHex(c));
            b.Click += (_, _) => { Update(current with { Color = color }); MarkSelectedColor(); };
            swatches.Add((b, c, check));
            palette.Children.Add(b);
        }
        MarkSelectedColor();
        root.Children.Add(palette);

        var (widthLo, widthHi) = highlighter ? (6f, 36f) : WidthRange(current.Kind);
        widthSlider = LabeledSlider("Width", widthLo, widthHi, Math.Clamp(current.Width, widthLo, widthHi), 0.1,
            v => { if (!syncing) Update(current with { Width = (float)v }); });
        root.Children.Add(widthSlider);

        var pressure = new ToggleSwitch { Header = "Pressure", IsOn = current.Dynamics.PressureEnabled, OnContent = "On", OffContent = "Off" };
        pressureSwitch = pressure;
        pressure.Toggled += (_, _) => { if (!syncing) Update(current with { Dynamics = Clone(current.Dynamics, pressureEnabled: pressure.IsOn) }); };
        root.Children.Add(pressure);

        var curve = new ComboBox { Header = "Pressure curve", HorizontalAlignment = HorizontalAlignment.Stretch };
        curve.Items.Add("Linear"); curve.Items.Add("Soft (light touch)"); curve.Items.Add("Firm");
        curve.SelectedIndex = current.Dynamics.Curve.Kind switch { PressureCurveKind.Soft => 1, PressureCurveKind.Firm => 2, _ => 0 };
        curve.SelectionChanged += (_, _) =>
        {
            var kind = curve.SelectedIndex switch { 1 => PressureCurveKind.Soft, 2 => PressureCurveKind.Firm, _ => PressureCurveKind.Linear };
            var c = PressureCurve.FromKind(kind).WithCalibration(current.Dynamics.Curve.InputMin, current.Dynamics.Curve.InputMax);
            Update(current with { Dynamics = Clone(current.Dynamics, curve: c) });
        };
        root.Children.Add(curve);

        thinSlider = LabeledSlider("Thinnest line (% of width)", 0, 100, current.Dynamics.MinWidthRatio * 100, 1,
            v => { if (!syncing) Update(current with { Dynamics = Clone(current.Dynamics, minWidth: (float)(v / 100)) }); });
        root.Children.Add(thinSlider);
        tiltSlider = LabeledSlider("Tilt sensitivity (0 = off)", 0, 100, current.Dynamics.TiltSensitivity * 100, 1,
            v => { if (!syncing) Update(current with { Dynamics = Clone(current.Dynamics, tilt: (float)(v / 100)) }); });
        root.Children.Add(tiltSlider);
        root.Children.Add(LabeledSlider("Stabilizer", 0, 100, current.Stabilizer * 100, 1,
            v => Update(current with { Stabilizer = (float)(v / 100) })));

        if (remove is not null)
        {
            var del = new Button { Content = "Remove this pen", HorizontalAlignment = HorizontalAlignment.Left };
            del.Click += (_, _) => remove();
            root.Children.Add(del);
        }
        return new Flyout { Content = new ScrollViewer { Content = root, MaxHeight = 640 } };
    }

    public static Flyout ForEraser(EraserMode mode, float size, Action<EraserMode, float> changed)
    {
        var root = new StackPanel { Spacing = 10, Width = 240 };
        var radio = new RadioButtons { Header = "Eraser" };
        radio.Items.Add("Whole strokes");
        radio.Items.Add("Precise (pixel)");
        radio.SelectedIndex = mode == EraserMode.Pixel ? 1 : 0;
        float currentSize = size;
        radio.SelectionChanged += (_, _) => changed(radio.SelectedIndex == 1 ? EraserMode.Pixel : EraserMode.Stroke, currentSize);
        root.Children.Add(radio);
        root.Children.Add(LabeledSlider("Size", 4, 80, size, 1, v =>
        {
            currentSize = (float)v;
            changed(radio.SelectedIndex == 1 ? EraserMode.Pixel : EraserMode.Stroke, currentSize);
        }));
        return new Flyout { Content = root };
    }

    /// <summary>Sensible width limits per nib (page units).</summary>
    private static (float Min, float Max) WidthRange(PenKind kind) => kind switch
    {
        PenKind.Watercolor => (4f, 40f),
        PenKind.Marker => (1.5f, 24f),
        PenKind.Brush => (0.8f, 24f),
        PenKind.Calligraphy => (1f, 16f),
        PenKind.Highlighter => (6f, 36f),
        _ => (0.4f, 14f),
    };

    private static string NibLabel(PenKind k) => k switch
    {
        PenKind.Calligraphy => "Calligraphy",
        PenKind.Watercolor => "Watercolor",
        _ => k.ToString(),
    };

    private static string NibHint(PenKind k) => k switch
    {
        PenKind.Ballpoint => "Ballpoint: even line (ignores tilt)",
        PenKind.Fountain => "Fountain: flat nib, thick and thin by direction; tilt swings the nib",
        PenKind.Pencil => "Pencil: lean it to shade with the side of the lead",
        PenKind.Brush => "Brush: press to spread the bristles, lean to lay them flat",
        PenKind.Calligraphy => "Calligraphy: crisp chisel edge; tilt or twist turns it",
        PenKind.Marker => "Marker: translucent felt tip; lean to broaden it",
        PenKind.Watercolor => "Watercolor: pale wash under your ink; press for pigment, lean to spread",
        _ => k.ToString(),
    };

    private static PenDynamics Clone(PenDynamics d, bool? pressureEnabled = null, PressureCurve? curve = null, float? minWidth = null, float? tilt = null) => new()
    {
        Curve = curve ?? d.Curve,
        PressureEnabled = pressureEnabled ?? d.PressureEnabled,
        MinWidthRatio = minWidth ?? d.MinWidthRatio,
        TiltSensitivity = tilt ?? d.TiltSensitivity,
        SpeedSensitivity = d.SpeedSensitivity,
    };

    private static Slider LabeledSlider(string header, double min, double max, double value, double step, Action<double> changed)
    {
        var s = new Slider { Header = header, Minimum = min, Maximum = max, StepFrequency = step, Value = value };
        s.ValueChanged += (_, e) => changed(e.NewValue);
        return s;
    }
}
