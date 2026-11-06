using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;
using Nib.App.Ink;
using Nib.Core.Export;
using Nib.Core.Ink;
using Nib.Core.Model;
using Nib.Core.Rendering;
using Windows.Foundation;

namespace Nib.App.Services;

/// <summary>
/// Renders one page sheet to a PNG image at a chosen resolution, using the same stroke geometry, paper patterns and
/// text layout as the screen (white paper, real colors; the dark-page display option never applies to exports).
/// </summary>
internal static class PngExporter
{
    /// <summary>Largest image edge Direct2D renders reliably.</summary>
    private const int MaxEdge = 16000;

    public static async Task WriteSheetAsync(ExportPage page, PdfExporter.Sheet sheet, bool includeBackground, int dpi,
        IReadOnlyDictionary<int, TagDefinition> tags, string path)
    {
        // Page units are 1/96 inch. Pixels per unit = (dpi / 96) * the sheet's own scale (infinite canvases are scaled to the paper).
        float pxPerUnit = dpi / 96f * sheet.Scale;
        int w = Math.Max(1, (int)MathF.Round(sheet.WidthPt / 72f * dpi));
        int h = Math.Max(1, (int)MathF.Round(sheet.HeightPt / 72f * dpi));
        if (Math.Max(w, h) > MaxEdge)
        {
            float shrink = MaxEdge / (float)Math.Max(w, h);
            pxPerUnit *= shrink;
            w = Math.Max(1, (int)(w * shrink)); h = Math.Max(1, (int)(h * shrink));
        }
        float originX = sheet.Origin.Left - sheet.MarginUnits, originY = sheet.Origin.Top - sheet.MarginUnits;

        var device = CanvasDevice.GetSharedDevice();
        using var target = new CanvasRenderTarget(device, w, h, 96f); // 96 dpi: one DIP is one pixel
        using (var ds = target.CreateDrawingSession())
        {
            ds.Clear(Colors2D.From(0xFFFFFFFF));
            ds.Transform = Matrix3x2.CreateTranslation(-originX, -originY) * Matrix3x2.CreateScale(pxPerUnit);
            var o = sheet.Origin;
            using var layer = ds.CreateLayer(1f, new Rect(o.Left, o.Top, o.Width, o.Height));

            if (includeBackground)
            {
                var bg = new Win2DBackgroundSink { Session = ds, MinLineWidth = 0.7f / pxPerUnit };
                BackgroundPattern.Emit(page.Layout, sheet.Origin, pxPerUnit, bg);
            }

            var builder = new StrokeGeometryBuilder();
            foreach (var s in page.Strokes)
                if (s.Kind.IsBackgroundInk() && s.Bounds.Intersects(sheet.Origin)) DrawStroke(ds, device, builder, s);

            var shapes = new Win2DShapeCanvas { Session = ds };
            foreach (var t in page.Texts)
            {
                if (!t.Bounds.Intersects(sheet.Origin)) continue;
                using var visual = new TextVisual(device, t);
                visual.Draw(ds, static c => c, shapes, tags);
            }

            foreach (var s in page.Strokes)
                if (!s.Kind.IsBackgroundInk() && s.Bounds.Intersects(sheet.Origin)) DrawStroke(ds, device, builder, s);
        }
        string tmp = path + ".tmp";
        await target.SaveAsync(tmp, CanvasBitmapFileFormat.Png);
        System.IO.File.Move(tmp, path, overwrite: true);
    }

    private static void DrawStroke(CanvasDrawingSession ds, CanvasDevice device, StrokeGeometryBuilder builder, Stroke s)
    {
        var sink = new Win2DRunSink(device);
        builder.Build(s, sink);
        using var visual = sink.TakeVisual();
        for (int i = 0; i < visual.Geometries.Length; i++)
        {
            var run = visual.Runs[i];
            if (run.Multiply)
            {
                ds.Blend = CanvasBlend.Min; // darken: translucent ink never stacks on itself
                ds.FillGeometry(visual.Geometries[i], Colors2D.From(run.FillColor));
                ds.Blend = CanvasBlend.SourceOver;
            }
            else ds.FillGeometry(visual.Geometries[i], Colors2D.From(run.FillColor));
        }
    }
}
