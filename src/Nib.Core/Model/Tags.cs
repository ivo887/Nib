using System.Collections.Generic;

namespace Nib.Core.Model;

public enum TagSymbol : byte
{
    Checkbox = 0,
    Star = 1,
    Question = 2,
    Flag = 3,
    Bookmark = 4,
    Lightbulb = 5,
    Exclamation = 6,
    Dot = 7,
}

/// <summary>A tag type. Built-ins match OneNote's Ctrl+1..5; users can add custom tags on 6..9.</summary>
public sealed record TagDefinition(int Id, string Name, TagSymbol Symbol, uint Color, bool IsCheckbox, int Shortcut)
{
    public const int ToDo = 1;
    public const int Important = 2;
    public const int Question = 3;
    public const int Remember = 4;
    public const int Definition = 5;

    public static IReadOnlyList<TagDefinition> BuiltIn { get; } = new[]
    {
        new TagDefinition(ToDo, "To Do", TagSymbol.Checkbox, 0xFF2B6CB0, true, 1),
        new TagDefinition(Important, "Important", TagSymbol.Star, 0xFFE0A100, false, 2),
        new TagDefinition(Question, "Question", TagSymbol.Question, 0xFF7B4FB8, false, 3),
        new TagDefinition(Remember, "Remember for later", TagSymbol.Bookmark, 0xFF2E8B57, false, 4),
        new TagDefinition(Definition, "Definition", TagSymbol.Flag, 0xFFD35400, false, 5),
    };
}

/// <summary>One tagged paragraph, as listed in the tag summary across all notebooks.</summary>
public sealed record TagItem(
    long PageId, long TextId, int Paragraph, int TagId, bool Checked, string Snippet,
    string PageTitle, string SectionName, string NotebookName);
