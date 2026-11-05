using System;
using System.Collections.Generic;
using System.Numerics;
using Nib.Core.Geometry;
using Nib.Core.Ink;
using Nib.Core.Model;

namespace Nib.Core.Editing;

/// <summary>Strokes and text containers picked with the lasso.</summary>
public sealed class Selection
{
    public List<Stroke> Strokes { get; } = new();
    public List<TextContainer> Texts { get; } = new();
    public bool IsEmpty => Strokes.Count == 0 && Texts.Count == 0;

    public RectF Bounds
    {
        get
        {
            var r = RectF.Empty;
            foreach (var s in Strokes) r = r.Union(s.Bounds);
            foreach (var t in Texts) r = r.Union(t.Bounds);
            return r;
        }
    }

    public void Clear() { Strokes.Clear(); Texts.Clear(); }

    /// <summary>Re-resolves selected items against the page after an edit (same ids, new instances).</summary>
    public void Refresh(Page page)
    {
        for (int i = Strokes.Count - 1; i >= 0; i--)
        {
            var s = page.FindStroke(Strokes[i].Id);
            if (s is null) Strokes.RemoveAt(i); else Strokes[i] = s;
        }
        for (int i = Texts.Count - 1; i >= 0; i--)
        {
            var t = page.FindText(Texts[i].Id);
            if (t is null) Texts.RemoveAt(i); else Texts[i] = t;
        }
    }
}

/// <summary>Lasso hit testing and the selection operations (move, resize, rotate, recolor, delete, insert space).</summary>
public static class SelectionOps
{
    /// <summary>A stroke is selected when at least this share of its points lies inside the lasso (OneNote-like).</summary>
    public const float InsideShare = 0.5f;

    public static void Select(Page page, ReadOnlySpan<Vector2> lasso, Selection into)
    {
        into.Clear();
        if (lasso.Length < 3) return;
        var bounds = RectF.Empty;
        foreach (var v in lasso) bounds = bounds.Union(v.X, v.Y);

        var strokes = new List<Stroke>();
        page.StrokeIndex.Query(bounds, strokes);
        foreach (var s in strokes)
        {
            if (!bounds.Contains(s.Bounds) && !bounds.Intersects(s.Bounds)) continue;
            var pts = s.Points;
            int inside = 0;
            int step = Math.Max(1, pts.Length / 64); // sample long strokes
            int tested = 0;
            for (int i = 0; i < pts.Length; i += step)
            {
                tested++;
                if (PointInPolygon(lasso, pts[i].X, pts[i].Y)) inside++;
            }
            if (tested > 0 && inside >= tested * InsideShare) into.Strokes.Add(s);
        }
        var texts = new List<TextContainer>();
        page.TextIndex.Query(bounds, texts);
        foreach (var t in texts)
        {
            var b = t.Bounds;
            if (PointInPolygon(lasso, b.CenterX, b.CenterY)) into.Texts.Add(t);
        }
        // Keep paint order stable for copy/paste.
        into.Strokes.Sort(static (a, b) => a.Z.CompareTo(b.Z));
    }

    /// <summary>Even-odd point-in-polygon test.</summary>
    public static bool PointInPolygon(ReadOnlySpan<Vector2> poly, float x, float y)
    {
        bool inside = false;
        for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
        {
            var a = poly[i];
            var b = poly[j];
            if ((a.Y > y) != (b.Y > y) && x < (b.X - a.X) * (y - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
        }
        return inside;
    }

    /// <summary>Builds the command that applies a similarity transform to the selection.</summary>
    public static IUndoCommand Transform(Selection sel, Matrix3x2 m, string name)
    {
        var cmd = new CompositeCommand(name);
        if (sel.Strokes.Count > 0)
        {
            var moved = new Stroke[sel.Strokes.Count];
            for (int i = 0; i < moved.Length; i++) moved[i] = sel.Strokes[i].Transformed(m);
            cmd.Add(new StrokesCommand(name, sel.Strokes.ToArray(), moved));
        }
        float scale = MathF.Sqrt(MathF.Abs(m.M11 * m.M22 - m.M12 * m.M21));
        foreach (var t in sel.Texts)
        {
            // Text containers move with the selection; resize scales their wrap width. Text is not rotated.
            var corner = Vector2.Transform(new Vector2(t.X, t.Y), m);
            var moved = new TextContainer(t.Id, t.Z, corner.X, corner.Y, t.Width * scale, t.Text, 0);
            cmd.Add(new TextCommand(name, t, moved));
        }
        return cmd;
    }

    public static IUndoCommand Recolor(Selection sel, uint color)
    {
        var recolored = new Stroke[sel.Strokes.Count];
        for (int i = 0; i < recolored.Length; i++) recolored[i] = sel.Strokes[i].WithColor(color);
        return new StrokesCommand("Recolor", sel.Strokes.ToArray(), recolored);
    }

    public static IUndoCommand Delete(Selection sel)
    {
        var cmd = new CompositeCommand("Delete");
        if (sel.Strokes.Count > 0) cmd.Add(new StrokesCommand("Delete", sel.Strokes.ToArray(), null));
        foreach (var t in sel.Texts) cmd.Add(new TextCommand("Delete", t, null));
        return cmd;
    }

    /// <summary>Copies of the selection with fresh ids, new Z on top, offset by (dx, dy). Used by paste and duplicate.</summary>
    public static IUndoCommand PasteCopies(Page page, IReadOnlyList<Stroke> strokes, IReadOnlyList<TextContainer> texts, float dx, float dy)
    {
        var cmd = new CompositeCommand("Paste");
        var m = Matrix3x2.CreateTranslation(dx, dy);
        if (strokes.Count > 0)
        {
            var copies = new Stroke[strokes.Count];
            for (int i = 0; i < copies.Length; i++)
                copies[i] = strokes[i].Transformed(m).WithZ(page.NextZ(), IdGenerator.Next());
            cmd.Add(new StrokesCommand("Paste", null, copies));
        }
        foreach (var t in texts)
            cmd.Add(new TextCommand("Paste", null, new TextContainer(IdGenerator.Next(), page.NextZ(), t.X + dx, t.Y + dy, t.Width, t.Text, t.MeasuredHeight)));
        return cmd;
    }

    /// <summary>
    /// Insert Space: everything whose top (or left, for horizontal) is past <paramref name="at"/> moves by
    /// <paramref name="delta"/>. Negative delta pulls content back (never past the line).
    /// </summary>
    public static IUndoCommand? InsertSpace(Page page, float at, float delta, bool vertical)
    {
        if (MathF.Abs(delta) < 0.5f) return null;
        var movedOld = new List<Stroke>();
        var movedNew = new List<Stroke>();
        var m = vertical ? Matrix3x2.CreateTranslation(0, delta) : Matrix3x2.CreateTranslation(delta, 0);
        foreach (var s in page.Strokes)
        {
            float start = vertical ? s.Bounds.Top : s.Bounds.Left;
            if (start < at) continue;
            movedOld.Add(s);
            movedNew.Add(s.Transformed(m));
        }
        var cmd = new CompositeCommand("Insert space");
        if (movedOld.Count > 0) cmd.Add(new StrokesCommand("Insert space", movedOld, movedNew));
        foreach (var t in page.Texts)
        {
            float start = vertical ? t.Y : t.X;
            if (start < at) continue;
            cmd.Add(new TextCommand("Insert space", t, vertical ? t.MovedTo(t.X, t.Y + delta) : t.MovedTo(t.X + delta, t.Y)));
        }
        return cmd.Count > 0 ? cmd : null;
    }

    /// <summary>Uniform scale around an anchor, as produced by dragging a corner handle.</summary>
    public static Matrix3x2 ScaleAround(Vector2 anchor, float scale) =>
        Matrix3x2.CreateTranslation(-anchor) * Matrix3x2.CreateScale(scale) * Matrix3x2.CreateTranslation(anchor);

    public static Matrix3x2 RotateAround(Vector2 center, float radians) =>
        Matrix3x2.CreateTranslation(-center) * Matrix3x2.CreateRotation(radians) * Matrix3x2.CreateTranslation(center);
}
