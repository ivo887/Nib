using System;
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
                pts[i] = new InkPoint(x, y, p, 35 * t, 0, -1, i * 6);
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

        void Update(PenPreset p)
        {
            current = p;
            preview.Invalidate();
            changed(p);
        }

        if (!highlighter)
        {
            var kinds = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            foreach (var k in new[] { PenKind.Ballpoint, PenKind.Fountain, PenKind.Pencil })
            {
                var tb = new ToggleButton { Content = new FontIcon { Glyph = Toolbar.PenGlyph(k), FontSize = 16 }, IsChecked = current.Kind == k, Width = 44, Height = 36 };
                ToolTipService.SetToolTip(tb, k.ToString());
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(tb, k.ToString());
                tb.Click += (_, _) =>
                {
                    foreach (var c in kinds.Children) if (c is ToggleButton other) other.IsChecked = ReferenceEquals(other, tb);
                    Update(current with { Kind = k });
                };
                kinds.Children.Add(tb);
            }
            root.Children.Add(kinds);
        }

        var palette = new VariableSizedWrapGrid { Orientation = Orientation.Horizontal, MaximumRowsOrColumns = 6, ItemWidth = 44, ItemHeight = 36 };
        foreach (uint c in highlighter ? HighlighterColors : PenColors)
        {
            var b = new Button
            {
                Width = 38, Height = 30, Padding = new Thickness(0),
                Content = new Microsoft.UI.Xaml.Shapes.Ellipse { Width = 20, Height = 20, Fill = new SolidColorBrush(Colors2D.From(c)) },
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(b, Nib.Core.Rendering.ColorUtil.ToHex(c));
            uint color = c;
            b.Click += (_, _) => Update(current with { Color = color });
            palette.Children.Add(b);
        }
        root.Children.Add(palette);

        root.Children.Add(LabeledSlider("Width", highlighter ? 6 : 0.4, highlighter ? 36 : 14, current.Width, 0.1,
            v => Update(current with { Width = (float)v })));

        var pressure = new ToggleSwitch { Header = "Pressure", IsOn = current.Dynamics.PressureEnabled, OnContent = "On", OffContent = "Off" };
        pressure.Toggled += (_, _) => Update(current with { Dynamics = Clone(current.Dynamics, pressureEnabled: pressure.IsOn) });
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

        root.Children.Add(LabeledSlider("Thinnest line (% of width)", 0, 100, current.Dynamics.MinWidthRatio * 100, 1,
            v => Update(current with { Dynamics = Clone(current.Dynamics, minWidth: (float)(v / 100)) })));
        root.Children.Add(LabeledSlider("Tilt sensitivity (0 = off)", 0, 100, current.Dynamics.TiltSensitivity * 100, 1,
            v => Update(current with { Dynamics = Clone(current.Dynamics, tilt: (float)(v / 100)) })));
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

    private static PenDynamics Clone(PenDynamics d, bool? pressureEnabled = null, PressureCurve? curve = null, float? minWidth = null, float? tilt = null) => new()
    {
        Curve = curve ?? d.Curve,
        PressureEnabled = pressureEnabled ?? d.PressureEnabled,
        MinWidthRatio = minWidth ?? d.MinWidthRatio,
        TiltSensitivity = tilt ?? d.TiltSensitivity,
        SpeedSensitivity = d.SpeedSensitivity,
    };

    private static FrameworkElement LabeledSlider(string header, double min, double max, double value, double step, Action<double> changed)
    {
        var s = new Slider { Header = header, Minimum = min, Maximum = max, StepFrequency = step, Value = value };
        s.ValueChanged += (_, e) => changed(e.NewValue);
        return s;
    }
}
