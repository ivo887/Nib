using System;
using System.Text.Json.Serialization;
using Nib.Core.Geometry;

namespace Nib.Core.Model;

public enum BackgroundKind : byte
{
    Blank = 0,
    Lined = 1,
    Grid = 2,
    Dots = 3,
    Graph = 4,
    Cornell = 5,
    /// <summary>v1: custom image.</summary>
    Image = 6,
    /// <summary>v1: PDF page (printouts).</summary>
    PdfPage = 7,
}

/// <summary>Ruling presets for lined paper (US standards).</summary>
public enum LineRule : byte { Narrow = 0, College = 1, Wide = 2, Custom = 3 }

/// <summary>Paper pattern and colors. Immutable; edits create a new instance.</summary>
public sealed record BackgroundSpec
{
    public BackgroundKind Kind { get; init; } = BackgroundKind.Blank;
    /// <summary>Distance between lines / grid cells / dots, page units.</summary>
    public float Spacing { get; init; } = Rules.College;
    public LineRule Rule { get; init; } = LineRule.College;
    /// <summary>Graph paper: a major line every N minor lines.</summary>
    public int MajorEvery { get; init; } = 5;
    public uint PaperColor { get; init; } = 0xFFFFFFFF;
    public uint LineColor { get; init; } = 0xFFB9D3EA;
    public uint MajorLineColor { get; init; } = 0xFF8FB4DA;
    public bool MarginLine { get; init; }
    public uint MarginColor { get; init; } = 0xFFE8A3A3;
    /// <summary>v1 (Image / PdfPage backgrounds): attachment id of the source.</summary>
    public string? SourceRef { get; init; }

    public static class Rules
    {
        /// <summary>1/4 inch.</summary>
        public const float Narrow = 24f;
        /// <summary>9/32 inch.</summary>
        public const float College = 27f;
        /// <summary>11/32 inch.</summary>
        public const float Wide = 33f;

        public static float For(LineRule r, float custom) => r switch
        {
            LineRule.Narrow => Narrow,
            LineRule.College => College,
            LineRule.Wide => Wide,
            _ => custom,
        };
    }

    public static readonly BackgroundSpec Blank = new();
    public static readonly BackgroundSpec CollegeRuled = new() { Kind = BackgroundKind.Lined, Rule = LineRule.College, Spacing = Rules.College, MarginLine = true };
    public static readonly BackgroundSpec Squares5mm = new() { Kind = BackgroundKind.Grid, Spacing = PageUnits.FromMillimeters(5), LineColor = 0xFFD4E2EE };
    public static readonly BackgroundSpec DotGrid5mm = new() { Kind = BackgroundKind.Dots, Spacing = PageUnits.FromMillimeters(5), LineColor = 0xFFA9B9C9 };
    public static readonly BackgroundSpec Graph1mm = new() { Kind = BackgroundKind.Graph, Spacing = PageUnits.FromMillimeters(1), MajorEvery = 10, LineColor = 0xFFE3ECF4, MajorLineColor = 0xFFB4CBE0 };
    public static readonly BackgroundSpec Cornell = new() { Kind = BackgroundKind.Cornell, Rule = LineRule.College, Spacing = Rules.College };
}

/// <summary>A paper size in millimeters (portrait orientation).</summary>
public sealed record PaperSize(string Name, float WidthMm, float HeightMm)
{
    public static readonly PaperSize A4 = new("A4", 210f, 297f);
    public static readonly PaperSize A5 = new("A5", 148f, 210f);
    public static readonly PaperSize Letter = new("Letter", 215.9f, 279.4f);

    public static PaperSize Custom(float widthMm, float heightMm) => new("Custom", widthMm, heightMm);

    public static PaperSize ByName(string? name) => name switch
    {
        "A5" => A5,
        "Letter" => Letter,
        _ => A4,
    };
}

public enum PageMode : byte
{
    /// <summary>Stack of sheets of a fixed paper size; a new sheet is added when you write near the end.</summary>
    Fixed = 0,
    /// <summary>Unbounded canvas in every direction.</summary>
    Infinite = 1,
}

/// <summary>Page geometry and background. Null members inherit from section, then notebook, then app defaults.</summary>
public sealed record PageLayout
{
    public PageMode Mode { get; init; } = PageMode.Fixed;
    public PaperSize Paper { get; init; } = PaperSize.A4;
    public bool Landscape { get; init; }
    public int SheetCount { get; init; } = 1;
    public BackgroundSpec Background { get; init; } = BackgroundSpec.Blank;

    [JsonIgnore] public float SheetWidth => PageUnits.FromMillimeters(Landscape ? Paper.HeightMm : Paper.WidthMm);
    [JsonIgnore] public float SheetHeight => PageUnits.FromMillimeters(Landscape ? Paper.WidthMm : Paper.HeightMm);

    /// <summary>Bounds of all sheets for fixed pages; <see cref="RectF.Empty"/> for infinite canvases.</summary>
    [JsonIgnore] public RectF Bounds => Mode == PageMode.Fixed ? new RectF(0, 0, SheetWidth, SheetHeight * Math.Max(1, SheetCount)) : RectF.Empty;

    public RectF SheetRect(int index) => new(0, index * SheetHeight, SheetWidth, (index + 1) * SheetHeight);

    /// <summary>Returns a layout with enough sheets for content reaching <paramref name="contentBottom"/>,
    /// keeping a fresh sheet available once writing gets into the last 20% of the final sheet.</summary>
    public PageLayout EnsureSheetsFor(float contentBottom)
    {
        if (Mode != PageMode.Fixed) return this;
        float h = SheetHeight;
        int needed = Math.Max(1, (int)MathF.Ceiling((contentBottom + h * 0.2f) / h));
        return needed > SheetCount ? this with { SheetCount = needed } : this;
    }
}

/// <summary>Defaults stored on a notebook or section. Null = inherit from the level above.</summary>
public sealed record PageDefaults
{
    public PageMode? Mode { get; init; }
    public string? Paper { get; init; }
    public float? CustomWidthMm { get; init; }
    public float? CustomHeightMm { get; init; }
    public bool? Landscape { get; init; }
    public BackgroundSpec? Background { get; init; }

    public static readonly PageDefaults AppDefaults = new()
    {
        Mode = PageMode.Fixed,
        Paper = "A4",
        Landscape = false,
        Background = BackgroundSpec.Blank,
    };

    /// <summary>Resolves the layout for a new page: section defaults, then notebook defaults, then app defaults.</summary>
    public static PageLayout ResolveNewPage(PageDefaults? notebook, PageDefaults? section)
    {
        PageMode mode = section?.Mode ?? notebook?.Mode ?? AppDefaults.Mode!.Value;
        string paperName = section?.Paper ?? notebook?.Paper ?? AppDefaults.Paper!;
        PaperSize paper = paperName == "Custom"
            ? PaperSize.Custom(section?.CustomWidthMm ?? notebook?.CustomWidthMm ?? 210, section?.CustomHeightMm ?? notebook?.CustomHeightMm ?? 297)
            : PaperSize.ByName(paperName);
        return new PageLayout
        {
            Mode = mode,
            Paper = paper,
            Landscape = section?.Landscape ?? notebook?.Landscape ?? false,
            SheetCount = 1,
            Background = section?.Background ?? notebook?.Background ?? AppDefaults.Background!,
        };
    }
}
