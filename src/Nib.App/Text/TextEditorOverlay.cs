using System;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using DispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Nib.App.Ink;
using Nib.Core.Editing;
using Nib.Core.Input;
using Nib.Core.Model;
using Windows.System;

namespace Nib.App.Text;

/// <summary>
/// The one live text editor: a RichEditBox laid over the ink surface exactly where the container is drawn,
/// scaled with the page. While it is open the ink thread hides that container's text and keeps drawing its
/// tag symbols from the live draft, so Ctrl+1 shows a checkbox immediately.
/// </summary>
internal sealed class TextEditorOverlay
{
    private readonly Canvas _host;
    private readonly InkEngine _engine;
    private readonly DispatcherQueueTimer _draftTimer;
    private RichEditBox? _box;
    private TextContainer? _container;
    private RichText _model = RichText.Empty;
    private bool _committing;

    public bool IsEditing => _box is not null;
    public bool DarkPages { get; set; }

    public TextEditorOverlay(Canvas host, InkEngine engine, DispatcherQueue ui)
    {
        _host = host;
        _engine = engine;
        _draftTimer = ui.CreateTimer();
        _draftTimer.Interval = TimeSpan.FromMilliseconds(150);
        _draftTimer.IsRepeating = false;
        _draftTimer.Tick += (_, _) => PushDraft();
    }

    public void Begin(TextContainer container, ViewState view, int caretParagraph)
    {
        if (_box is not null) Commit();
        _container = container;
        _model = container.Text;
        var box = new RichEditBox
        {
            Width = container.Width,
            MinHeight = 22,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            TextWrapping = TextWrapping.Wrap,
            IsSpellCheckEnabled = true,
            FontFamily = new FontFamily(TextVisual.DefaultFont),
            FontSize = TextStyle.Default.SizeUnits,
            RenderTransformOrigin = new Windows.Foundation.Point(0, 0),
        };
        // Lightweight styling: no chrome, no focus background, so the text sits exactly on the page.
        var clear = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        Brush card = DarkPages ? new SolidColorBrush(Microsoft.UI.Colors.White) : clear;
        foreach (var key in new[] { "TextControlBackground", "TextControlBackgroundPointerOver", "TextControlBackgroundFocused" })
            box.Resources[key] = card;
        foreach (var key in new[] { "TextControlBorderBrush", "TextControlBorderBrushPointerOver", "TextControlBorderBrushFocused" })
            box.Resources[key] = clear;
        box.Resources["TextControlThemeMinHeight"] = 22.0;
        box.Resources["TextControlThemePadding"] = new Thickness(0);
        AutomationProperties_SetName(box, "Note text");

        box.TextChanged += (_, _) => { _draftTimer.Stop(); _draftTimer.Start(); };
        box.LostFocus += (_, _) => Commit();
        box.PreviewKeyDown += OnPreviewKeyDown;

        _box = box;
        _host.Children.Add(box);
        UpdateView(view);
        RichTextBridge.Load(box, container.Text, caretParagraph);
        box.Focus(FocusState.Programmatic);
    }

    private static void AutomationProperties_SetName(DependencyObject o, string name) =>
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(o, name);

    public void UpdateView(ViewState v)
    {
        if (_box is null || _container is null) return;
        _box.RenderTransform = new CompositeTransform
        {
            ScaleX = v.Zoom,
            ScaleY = v.Zoom,
            TranslateX = _container.X * v.Zoom + v.OffsetX,
            TranslateY = _container.Y * v.Zoom + v.OffsetY,
        };
    }

    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            Commit();
            return;
        }
        if (!ctrl) return;
        // Ctrl+1..9 are tags (RichEdit would use them for line spacing); Ctrl+0 clears tags.
        if (e.Key >= VirtualKey.Number0 && e.Key <= VirtualKey.Number9)
        {
            int n = e.Key - VirtualKey.Number0;
            e.Handled = true;
            if (n == 0) ClearTags();
            else ToggleTag(n);
        }
    }

    /// <summary>Applies tag <paramref name="shortcut"/> (1..9) to the paragraph with the caret.</summary>
    public void ToggleTag(int tagId)
    {
        if (_box is null) return;
        _model = RichTextBridge.Read(_box, _model);
        int para = Math.Clamp(RichTextBridge.CaretParagraph(_box), 0, _model.Paragraphs.Length - 1);
        var p = _model.Paragraphs[para];
        _model = _model.ReplaceParagraph(para, p.WithTag(tagId, !p.TagIds.Contains(tagId)));
        _engine.UpdateTextDraft(_container!.Id, _model);
    }

    private void ClearTags()
    {
        if (_box is null) return;
        _model = RichTextBridge.Read(_box, _model);
        int para = Math.Clamp(RichTextBridge.CaretParagraph(_box), 0, _model.Paragraphs.Length - 1);
        var p = _model.Paragraphs[para];
        _model = _model.ReplaceParagraph(para, p with { TagIds = p.TagIds.Clear(), Checked = false });
        _engine.UpdateTextDraft(_container!.Id, _model);
    }

    /// <summary>Formatting from the overflow menu: applies a paragraph style to the caret's paragraph.</summary>
    public void SetParagraphKind(ParagraphKind kind)
    {
        if (_box is null) return;
        var doc = _box.Document;
        var range = doc.Selection;
        range.Expand(Microsoft.UI.Text.TextRangeUnit.Paragraph);
        range.ParagraphFormat.ListType = kind switch
        {
            ParagraphKind.Bullet => Microsoft.UI.Text.MarkerType.Bullet,
            ParagraphKind.Numbered => Microsoft.UI.Text.MarkerType.Arabic,
            _ => Microsoft.UI.Text.MarkerType.None,
        };
        bool heading = kind is ParagraphKind.Heading1 or ParagraphKind.Heading2 or ParagraphKind.Heading3;
        range.CharacterFormat.Bold = heading ? Microsoft.UI.Text.FormatEffect.On : Microsoft.UI.Text.FormatEffect.Off;
        range.CharacterFormat.Size = RichTextBridge.HeadingPt(kind);
        PushDraft();
    }

    private void PushDraft()
    {
        if (_box is null || _container is null) return;
        _model = RichTextBridge.Read(_box, _model);
        _engine.UpdateTextDraft(_container.Id, _model);
    }

    public void Commit()
    {
        if (_box is null || _container is null || _committing) return;
        _committing = true;
        try
        {
            _draftTimer.Stop();
            _model = RichTextBridge.Read(_box, _model);
            _engine.CommitTextEdit(_container.Id, _model, _container.X, _container.Y, _container.Width);
            Close();
        }
        finally
        {
            _committing = false;
        }
    }

    /// <summary>Closes without committing (the engine already ended the edit).</summary>
    public void Close()
    {
        if (_box is null) return;
        var box = _box;
        _box = null;
        _container = null;
        _host.Children.Remove(box);
    }
}
