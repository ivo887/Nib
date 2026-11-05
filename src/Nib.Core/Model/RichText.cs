using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text;
using Nib.Core.Geometry;
using Nib.Core.Spatial;

namespace Nib.Core.Model;

[Flags]
public enum TextStyleFlags : byte
{
    None = 0,
    Bold = 1,
    Italic = 2,
    Underline = 4,
    Strikethrough = 8,
}

/// <summary>Character formatting of a run.</summary>
public readonly record struct TextStyle(TextStyleFlags Flags, float SizePt, uint Color, uint Highlight, string? Font)
{
    public static readonly TextStyle Default = new(TextStyleFlags.None, 11f, 0xFF1F1F1F, 0, null);
    public bool Bold => (Flags & TextStyleFlags.Bold) != 0;
    public bool Italic => (Flags & TextStyleFlags.Italic) != 0;
    public bool Underline => (Flags & TextStyleFlags.Underline) != 0;
    public bool Strikethrough => (Flags & TextStyleFlags.Strikethrough) != 0;
    /// <summary>Font size in page units (points × 96/72).</summary>
    public float SizeUnits => SizePt * 96f / 72f;
}

public sealed record TextRun(string Text, TextStyle Style);

public enum ParagraphKind : byte
{
    Normal = 0,
    Heading1 = 1,
    Heading2 = 2,
    Heading3 = 3,
    Bullet = 4,
    Numbered = 5,
}

/// <summary>A paragraph with its tags. To-Do state is stored per paragraph.</summary>
public sealed record Paragraph
{
    public ParagraphKind Kind { get; init; }
    /// <summary>Indent level 0..8.</summary>
    public int Indent { get; init; }
    public ImmutableArray<TextRun> Runs { get; init; } = ImmutableArray<TextRun>.Empty;
    public ImmutableArray<int> TagIds { get; init; } = ImmutableArray<int>.Empty;
    /// <summary>Checked state for checkbox tags (To Do).</summary>
    public bool Checked { get; init; }

    public string PlainText
    {
        get
        {
            if (Runs.Length == 1) return Runs[0].Text;
            var sb = new StringBuilder();
            foreach (var r in Runs) sb.Append(r.Text);
            return sb.ToString();
        }
    }

    public static Paragraph Plain(string text, TextStyle? style = null) =>
        new() { Runs = ImmutableArray.Create(new TextRun(text, style ?? TextStyle.Default)) };

    public Paragraph WithTag(int tagId, bool on)
    {
        bool has = TagIds.Contains(tagId);
        if (has == on) return this;
        return this with { TagIds = on ? TagIds.Add(tagId) : TagIds.Remove(tagId), Checked = on && Checked };
    }
}

/// <summary>Rich text content of a note container. Immutable.</summary>
public sealed record RichText
{
    public ImmutableArray<Paragraph> Paragraphs { get; init; } = ImmutableArray<Paragraph>.Empty;

    public static readonly RichText Empty = new() { Paragraphs = ImmutableArray.Create(Paragraph.Plain(string.Empty)) };

    public static RichText FromPlain(string text, TextStyle? style = null)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var b = ImmutableArray.CreateBuilder<Paragraph>(lines.Length);
        foreach (var l in lines) b.Add(Paragraph.Plain(l, style));
        return new RichText { Paragraphs = b.MoveToImmutable() };
    }

    public string PlainText
    {
        get
        {
            var sb = new StringBuilder();
            for (int i = 0; i < Paragraphs.Length; i++)
            {
                if (i > 0) sb.Append('\n');
                sb.Append(Paragraphs[i].PlainText);
            }
            return sb.ToString();
        }
    }

    public bool IsBlank
    {
        get
        {
            foreach (var p in Paragraphs)
                if (!string.IsNullOrWhiteSpace(p.PlainText) || p.TagIds.Length > 0) return false;
            return true;
        }
    }

    public RichText ReplaceParagraph(int index, Paragraph p) => this with { Paragraphs = Paragraphs.SetItem(index, p) };
}

/// <summary>A movable text box on the page. Immutable; edits produce a new instance with the same id.</summary>
public sealed class TextContainer : ISpatialItem
{
    public long Id { get; }
    public long Z { get; }
    public float X { get; }
    public float Y { get; }
    /// <summary>Wrap width in page units.</summary>
    public float Width { get; }
    public RichText Text { get; }
    /// <summary>Layout height measured by the renderer (0 until first layout). Persisted so bounds are known before layout.</summary>
    public float MeasuredHeight { get; }

    /// <summary>Renderer cache (text layout). Ink-thread owned.</summary>
    public object? RenderCache { get; set; }

    int ISpatialItem.QueryStamp { get; set; }

    public RectF Bounds => RectF.FromXYWH(X - TagGutter, Y, Width + TagGutter, MathF.Max(MeasuredHeight, EstimatedHeight));
    RectF ISpatialItem.Bounds => Bounds;

    /// <summary>Space left of the text for tag symbols.</summary>
    public const float TagGutter = 22f;
    public const float DefaultWidth = 480f;

    public TextContainer(long id, long z, float x, float y, float width, RichText text, float measuredHeight = 0)
    {
        Id = id; Z = z; X = x; Y = y; Width = MathF.Max(width, 40f); Text = text; MeasuredHeight = measuredHeight;
    }

    /// <summary>Rough height before the renderer has measured the layout.</summary>
    public float EstimatedHeight
    {
        get
        {
            float h = 0;
            foreach (var p in Text.Paragraphs)
            {
                float size = p.Runs.Length > 0 ? p.Runs[0].Style.SizeUnits : TextStyle.Default.SizeUnits;
                int chars = p.PlainText.Length;
                int lines = Math.Max(1, (int)MathF.Ceiling(chars * size * 0.5f / Width));
                h += lines * size * 1.35f;
            }
            return MathF.Max(h, 20f);
        }
    }

    public TextContainer WithText(RichText text, float measuredHeight = 0) => new(Id, Z, X, Y, Width, text, measuredHeight);
    public TextContainer MovedTo(float x, float y) => new(Id, Z, x, y, Width, Text, MeasuredHeight);
    public TextContainer WithWidth(float w) => new(Id, Z, X, Y, w, Text, 0);
    public TextContainer WithMeasuredHeight(float h) => new(Id, Z, X, Y, Width, Text, h);
}
