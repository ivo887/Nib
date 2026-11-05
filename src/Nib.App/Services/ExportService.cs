using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;
using Nib.App.Ink;
using Nib.Core.Export;
using Nib.Core.Model;
using Nib.Storage;
using Windows.Storage.Pickers;

namespace Nib.App.Services;

/// <summary>PDF export of a page, a section or a whole notebook. All rendering and file I/O run on a background thread.</summary>
internal sealed class ExportService
{
    private readonly StorageService _storage;
    private readonly IntPtr _hwnd;
    private readonly Func<IReadOnlyDictionary<int, TagDefinition>> _tags;

    public ExportService(StorageService storage, IntPtr hwnd, Func<IReadOnlyDictionary<int, TagDefinition>> tags)
    {
        _storage = storage;
        _hwnd = hwnd;
        _tags = tags;
    }

    public async Task<string?> ExportPage(InkEngine engine, bool includeBackground)
    {
        var page = await engine.SnapshotCurrentPage();
        if (page is null) return null;
        var path = await PickPath(Safe(page.Title, "Page"));
        if (path is null) return null;
        await Task.Run(() => Write(new[] { page }, path, page.Title, includeBackground));
        return path;
    }

    public async Task<string?> ExportSection(InkEngine engine, SectionInfo section, bool includeBackground)
    {
        await engine.SnapshotCurrentPage(); // flushes pending autosave first
        var path = await PickPath(Safe(section.Name, "Section"));
        if (path is null) return null;
        // Reads go through the writer queue so they see every save queued before them.
        var pages = await _storage.Write(s => LoadSection(s, section.Id));
        await Task.Run(() => Write(pages, path, section.Name, includeBackground));
        return path;
    }

    public async Task<string?> ExportNotebook(InkEngine engine, NotebookInfo notebook, bool includeBackground)
    {
        await engine.SnapshotCurrentPage();
        var path = await PickPath(Safe(notebook.Name, "Notebook"));
        if (path is null) return null;
        var pages = await _storage.Write(s =>
        {
            var all = new List<ExportPage>();
            foreach (var sec in s.ListSections(notebook.Id)) all.AddRange(LoadSection(s, sec.Id));
            return all;
        });
        await Task.Run(() => Write(pages, path, notebook.Name, includeBackground));
        return path;
    }

    private static List<ExportPage> LoadSection(NoteStore s, long sectionId)
    {
        var list = new List<ExportPage>();
        foreach (var info in s.ListPages(sectionId))
        {
            var page = s.LoadPage(info.Id);
            if (page is not null) list.Add(new ExportPage(info.DisplayTitle, page.Layout, page.Strokes.ToArray(), page.Texts.ToArray()));
        }
        return list;
    }

    private void Write(IReadOnlyList<ExportPage> pages, string path, string title, bool includeBackground)
    {
        var outliner = new GlyphOutliner(CanvasDevice.GetSharedDevice(), _tags());
        string tmp = path + ".tmp";
        using (var f = File.Create(tmp))
            PdfExporter.Export(pages, f, new PdfExportOptions { Title = title, IncludeBackground = includeBackground }, outliner);
        File.Move(tmp, path, overwrite: true);
    }

    private async Task<string?> PickPath(string suggested)
    {
        var picker = new FileSavePicker { SuggestedFileName = suggested, SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeChoices.Add("PDF document", new List<string> { ".pdf" });
        WinRT.Interop.InitializeWithWindow.Initialize(picker, _hwnd);
        var file = await picker.PickSaveFileAsync();
        return file?.Path;
    }

    private static string Safe(string name, string fallback)
    {
        if (string.IsNullOrWhiteSpace(name)) return fallback;
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name.Length > 80 ? name[..80] : name;
    }
}
