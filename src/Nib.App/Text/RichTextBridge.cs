using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml.Controls;
using Nib.App.Ink;
using Nib.Core.Model;
using Windows.UI;

namespace Nib.App.Text;

/// <summary>
/// Converts between Nib's rich text model and a RichEditBox document. Runs on the UI thread, only when an
/// edit starts or the draft is refreshed, walking maximal runs of identical formatting (not characters).
/// </summary>
internal static class RichTextBridge
{
    private const float PointsPerUnit = 72f / 96f;

    public static float HeadingPt(ParagraphKind k) => k switch
    {
        ParagraphKind.Heading1 => 20f,
        ParagraphKind.Heading2 => 16f,
        ParagraphKind.Heading3 => 13f,
        _ => TextStyle.Default.SizePt,
    };

    public static void Load(RichEditBox box, RichText text, int caretParagraph)
    {
        var doc = box.Document;
        var sb = new StringBuilder();
        for (int i = 0; i < text.Paragraphs.Length; i++)
        {
            if (i > 0) sb.Append('\r');
            sb.Append(text.Paragraphs[i].PlainText);
        }
        doc.SetText(TextSetOptions.None, sb.ToString());

        int pos = 0, caret = 0;
        for (int i = 0; i < text.Paragraphs.Length; i++)
        {
            var p = text.Paragraphs[i];
            int start = pos;
            foreach (var run in p.Runs)
            {
                int n = run.Text.Length;
                if (n == 0) continue;
                var cf = doc.GetRange(pos, pos + n).CharacterFormat;
                var s = run.Style;
                bool heading = p.Kind is ParagraphKind.Heading1 or ParagraphKind.Heading2 or ParagraphKind.Heading3;
                cf.Bold = s.Bold || heading ? FormatEffect.On : FormatEffect.Off;
                cf.Italic = s.Italic ? FormatEffect.On : FormatEffect.Off;
                cf.Underline = s.Underline ? UnderlineType.Single : UnderlineType.None;
                cf.Strikethrough = s.Strikethrough ? FormatEffect.On : FormatEffect.Off;
                cf.Size = heading ? MathF.Max(s.SizePt, HeadingPt(p.Kind)) : s.SizePt;
                cf.ForegroundColor = Colors2D.From(s.Color);
                if (s.Highlight != 0) cf.BackgroundColor = Colors2D.From(s.Highlight);
                if (s.Font is not null) cf.Name = s.Font;
                pos += n;
            }
            var pf = doc.GetRange(start, Math.Max(start, pos)).ParagraphFormat;
            pf.ListType = p.Kind switch
            {
                ParagraphKind.Bullet => MarkerType.Bullet,
                ParagraphKind.Numbered => MarkerType.Arabic,
                _ => MarkerType.None,
            };
            if (p.Indent > 0) pf.SetIndents(0, p.Indent * TextVisual.IndentStep * PointsPerUnit, 0);
            if (i == caretParagraph) caret = pos;
            pos += 1; // paragraph mark
        }
        doc.Selection.SetRange(caret, caret);
        doc.ClearUndoRedoHistory();
    }

    /// <summary>
    /// Reads the editor back. Tags and To-Do state are not part of the editor's text, so they are carried over
    /// from <paramref name="previous"/>: unchanged paragraphs keep theirs, edited paragraphs keep the tags of
    /// the paragraph that was at the same position.
    /// </summary>
    public static RichText Read(RichEditBox box, RichText previous)
    {
        var doc = box.Document;
        doc.GetText(TextGetOptions.None, out string all);
        if (all.EndsWith('\r')) all = all[..^1];
        var texts = all.Split('\r');
        var carried = Nib.Core.Editing.TagOps.CarryTags(previous, texts);

        var paras = ImmutableArray.CreateBuilder<Paragraph>(texts.Length);
        int pos = 0;
        for (int i = 0; i < texts.Length; i++)
        {
            string t = texts[i];
            int start = pos, end = pos + t.Length;
            var runs = ImmutableArray.CreateBuilder<TextRun>();
            bool allBold = t.Length > 0;
            float maxSize = 0;
            int p = start;
            while (p < end)
            {
                var r = doc.GetRange(p, p + 1);
                r.Expand(TextRangeUnit.CharacterFormat);
                int runEnd = Math.Clamp(r.EndPosition, p + 1, end);
                var cf = r.CharacterFormat;
                var flags = TextStyleFlags.None;
                if (cf.Bold == FormatEffect.On) flags |= TextStyleFlags.Bold; else allBold = false;
                if (cf.Italic == FormatEffect.On) flags |= TextStyleFlags.Italic;
                if (cf.Underline != UnderlineType.None && cf.Underline != UnderlineType.Undefined) flags |= TextStyleFlags.Underline;
                if (cf.Strikethrough == FormatEffect.On) flags |= TextStyleFlags.Strikethrough;
                float size = cf.Size > 0 ? cf.Size : TextStyle.Default.SizePt;
                maxSize = MathF.Max(maxSize, size);
                uint fg = Argb(cf.ForegroundColor);
                uint bg = Argb(cf.BackgroundColor);
                if (ColorIsDefaultBackground(cf.BackgroundColor)) bg = 0;
                string? font = string.IsNullOrEmpty(cf.Name) || cf.Name == TextVisual.DefaultFont ? null : cf.Name;
                var style = new TextStyle(flags, size, fg == 0 ? TextStyle.Default.Color : fg, bg, font);
                string runText = all.Substring(p, runEnd - p);
                if (runs.Count > 0 && runs[^1].Style == style) runs[^1] = new TextRun(runs[^1].Text + runText, style);
                else runs.Add(new TextRun(runText, style));
                p = runEnd;
            }
            if (runs.Count == 0) runs.Add(new TextRun(string.Empty, TextStyle.Default));

            var pf = doc.GetRange(start, Math.Max(start, end)).ParagraphFormat;
            var kind = pf.ListType switch
            {
                MarkerType.Bullet => ParagraphKind.Bullet,
                MarkerType.Arabic or MarkerType.LowercaseEnglishLetter or MarkerType.UppercaseEnglishLetter
                    or MarkerType.LowercaseRoman or MarkerType.UppercaseRoman => ParagraphKind.Numbered,
                _ => ParagraphKind.Normal,
            };
            if (kind == ParagraphKind.Normal && allBold)
            {
                if (maxSize >= 19.5f) kind = ParagraphKind.Heading1;
                else if (maxSize >= 15.5f) kind = ParagraphKind.Heading2;
                else if (maxSize >= 12.75f) kind = ParagraphKind.Heading3;
            }
            int indent = (int)MathF.Round(pf.LeftIndent / (TextVisual.IndentStep * PointsPerUnit));
            var (tags, isChecked) = carried[i];
            paras.Add(new Paragraph { Kind = kind, Indent = Math.Clamp(indent, 0, 8), Runs = runs.ToImmutable(), TagIds = tags, Checked = isChecked });
            pos = end + 1;
        }
        return new RichText { Paragraphs = paras.MoveToImmutable() };
    }

    /// <summary>Paragraph index of the caret (for Ctrl+1..9 while typing).</summary>
    public static int CaretParagraph(RichEditBox box)
    {
        var doc = box.Document;
        doc.GetText(TextGetOptions.None, out string all);
        int caret = Math.Clamp(doc.Selection.StartPosition, 0, all.Length);
        int n = 0;
        for (int i = 0; i < caret; i++) if (all[i] == '\r') n++;
        return n;
    }

    private static uint Argb(Color c) => (uint)(c.A << 24 | c.R << 16 | c.G << 8 | c.B);

    private static bool ColorIsDefaultBackground(Color c) => c.A == 0 || (c.R == 255 && c.G == 255 && c.B == 255);
}
