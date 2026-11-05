using System;

namespace Nib.Core.Model;

/// <summary>Notebook → (section groups) → sections → pages (with subpage levels). Metadata only; page content loads separately.</summary>
public sealed record NotebookInfo(long Id, string Name, uint Color, double Sort, PageDefaults? Defaults)
{
    /// <summary>Shown by the notebook picker (no XAML template needed, so it stays AOT-safe).</summary>
    public override string ToString() => Name;
}

public sealed record SectionGroupInfo(long Id, long NotebookId, long? ParentGroupId, string Name, double Sort);

public sealed record SectionInfo(long Id, long NotebookId, long? GroupId, string Name, uint Color, double Sort, PageDefaults? Defaults);

public sealed record PageInfo(
    long Id,
    long SectionId,
    string Title,
    double Sort,
    int Level, // 0 = page, 1 = subpage, 2 = sub-subpage (OneNote allows two levels)
    PageLayout Layout,
    DateTimeOffset Created,
    DateTimeOffset Modified,
    bool Favorite)
{
    public const int MaxLevel = 2;
    public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? "Untitled page" : Title;
}

/// <summary>Section colors offered in the UI (OneNote-like palette).</summary>
public static class SectionColors
{
    public static readonly uint[] Palette =
    {
        0xFF8AA8E4, 0xFF91BAAE, 0xFFF5A97F, 0xFFEBAACB, 0xFFB49EDE, 0xFFF3D275,
        0xFF9FDBA6, 0xFFE58F8F, 0xFF9BC3E6, 0xFFC0C0C0, 0xFFD7B89F, 0xFFA5D5D9,
    };

    public static uint ForIndex(int i) => Palette[((i % Palette.Length) + Palette.Length) % Palette.Length];
}
