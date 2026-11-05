using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Nib.Core.Model;

namespace Nib.Core.Editing;

/// <summary>Tag operations on text containers (Ctrl+1..9 toggles, checkbox clicks) and tag extraction for the summary index.</summary>
public static class TagOps
{
    /// <summary>Toggles a tag on one paragraph. Returns the undoable command, or null if nothing changed.</summary>
    public static IUndoCommand? Toggle(TextContainer container, int paragraph, int tagId)
    {
        if (paragraph < 0 || paragraph >= container.Text.Paragraphs.Length) return null;
        var p = container.Text.Paragraphs[paragraph];
        bool on = !p.TagIds.Contains(tagId);
        var updated = container.WithText(container.Text.ReplaceParagraph(paragraph, p.WithTag(tagId, on)), container.MeasuredHeight);
        return new TextCommand(on ? "Add tag" : "Remove tag", container, updated);
    }

    /// <summary>Removes every tag from a paragraph (Ctrl+0).</summary>
    public static IUndoCommand? ClearTags(TextContainer container, int paragraph)
    {
        if (paragraph < 0 || paragraph >= container.Text.Paragraphs.Length) return null;
        var p = container.Text.Paragraphs[paragraph];
        if (p.TagIds.Length == 0) return null;
        var updated = container.WithText(container.Text.ReplaceParagraph(paragraph, p with { TagIds = p.TagIds.Clear(), Checked = false }), container.MeasuredHeight);
        return new TextCommand("Remove tags", container, updated);
    }

    public static IUndoCommand? SetChecked(TextContainer container, int paragraph, bool isChecked)
    {
        if (paragraph < 0 || paragraph >= container.Text.Paragraphs.Length) return null;
        var p = container.Text.Paragraphs[paragraph];
        if (p.Checked == isChecked) return null;
        var updated = container.WithText(container.Text.ReplaceParagraph(paragraph, p with { Checked = isChecked }), container.MeasuredHeight);
        return new TextCommand(isChecked ? "Check" : "Uncheck", container, updated);
    }

    /// <summary>Rows for the tag index of one container (one per paragraph per tag).</summary>
    public static IEnumerable<(int Paragraph, int TagId, bool Checked, string Snippet)> Extract(TextContainer c)
    {
        for (int i = 0; i < c.Text.Paragraphs.Length; i++)
        {
            var p = c.Text.Paragraphs[i];
            if (p.TagIds.Length == 0) continue;
            string text = p.PlainText;
            string snippet = text.Length > 160 ? text[..160] + "…" : text;
            foreach (int tag in p.TagIds) yield return (i, tag, p.Checked, snippet);
        }
    }

    /// <summary>
    /// Tags are not part of an editor's text, so after free-form editing they are carried over by paragraph:
    /// unchanged paragraphs keep theirs (matched by text, nearest position first); edited paragraphs keep the
    /// tags of the paragraph that was at the same position; new paragraphs get none.
    /// </summary>
    public static (ImmutableArray<int> Tags, bool Checked)[] CarryTags(RichText previous, string[] texts)
    {
        var result = new (ImmutableArray<int>, bool)[texts.Length];
        var old = previous.Paragraphs;
        var used = new bool[old.Length];
        var matched = new bool[texts.Length];
        for (int i = 0; i < texts.Length; i++)
        {
            result[i] = (ImmutableArray<int>.Empty, false);
            int best = -1, bestDist = int.MaxValue;
            for (int j = 0; j < old.Length; j++)
            {
                if (used[j] || old[j].PlainText != texts[i]) continue;
                int d = Math.Abs(j - i);
                if (d < bestDist) { best = j; bestDist = d; }
            }
            if (best >= 0)
            {
                used[best] = true;
                matched[i] = true;
                result[i] = (old[best].TagIds, old[best].Checked);
            }
        }
        for (int i = 0; i < texts.Length; i++)
        {
            if (matched[i] || i >= old.Length || used[i]) continue;
            used[i] = true;
            result[i] = (old[i].TagIds, old[i].Checked);
        }
        return result;
    }
}
