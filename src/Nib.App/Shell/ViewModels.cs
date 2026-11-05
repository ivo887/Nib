using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Nib.App.Ink;
using Nib.Core.Model;

namespace Nib.App.Shell;

/// <summary>Sidebar row for a section.</summary>
public sealed class SectionItem : INotifyPropertyChanged
{
    private SectionInfo _info;
    public SectionItem(SectionInfo info) { _info = info; }
    public SectionInfo Info
    {
        get => _info;
        set { _info = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null)); }
    }
    public long Id => _info.Id;
    public string Name => _info.Name;
    public SolidColorBrush ColorBrush => new(Colors2D.From(_info.Color));
    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>Sidebar row for a page (indented for subpages).</summary>
public sealed class PageItem : INotifyPropertyChanged
{
    private PageInfo _info;
    public PageItem(PageInfo info) { _info = info; }
    public PageInfo Info
    {
        get => _info;
        set { _info = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null)); }
    }
    public long Id => _info.Id;
    public string Title => _info.DisplayTitle;
    public Thickness Indent => new(12 + _info.Level * 18, 0, 8, 0);
    public double TitleOpacity => string.IsNullOrWhiteSpace(_info.Title) ? 0.6 : 1.0;
    public Visibility FavoriteVisibility => _info.Favorite ? Visibility.Visible : Visibility.Collapsed;
    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>Row in the tag summary.</summary>
public sealed class TagRow
{
    public required TagItem Item { get; init; }
    public required string TagName { get; init; }
    public string Snippet => string.IsNullOrWhiteSpace(Item.Snippet) ? "(empty)" : Item.Snippet;
    public string Where => $"{Item.NotebookName} › {Item.SectionName} › {(string.IsNullOrWhiteSpace(Item.PageTitle) ? "Untitled page" : Item.PageTitle)}";
    public string CheckGlyph => Item.Checked ? "\uE73A" : "\uE739";
}
