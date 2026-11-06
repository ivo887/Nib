using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;
using Nib.App.Ink;
using Nib.Core.Export;
using Nib.Core.Model;
using Nib.Storage;
using Windows.Storage.Pickers;

namespace Nib.App.Services;

/// <summary>
/// Exports pages, sections and notebooks as PDF, PNG or SVG, and backs notebooks up as a .nibpkg. The format, resolution and
/// default folder come from Settings. All rendering and file I/O run on a background thread.
/// </summary>
internal sealed class ExportService
{
    private readonly StorageService _storage;
    private readonly IntPtr _hwnd;
    private readonly Func<IReadOnlyDictionary<int, TagDefinition>> _tags;
    private readonly Func<AppSettings> _settings;

    public ExportService(StorageService storage, IntPtr hwnd, Func<IReadOnlyDictionary<int, TagDefinition>> tags, Func<AppSettings> settings)
    {
        _storage = storage;
        _hwnd = hwnd;
        _tags = tags;
        _settings = settings;
    }

    /// <summary>The folder exports go to when the user does not want to be asked each time.</summary>
    public string DefaultFolder => _settings().ExportFolder is { Length: > 0 } f && Directory.Exists(f)
        ? f
        : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

    // ---------------------------------------------------------------- page / section / notebook

    public async Task<string?> ExportPage(InkEngine engine, ExportFormat? format = null)
    {
        var fmt = format ?? _settings().ExportFormat;
        var page = await engine.SnapshotCurrentPage();
        if (page is null) return null;
        return await Export(new[] { page }, Safe(page.Title, "Page"), page.Title, fmt);
    }

    public async Task<string?> ExportSection(InkEngine engine, SectionInfo section, ExportFormat? format = null)
    {
        var fmt = format ?? _settings().ExportFormat;
        await engine.SnapshotCurrentPage(); // flushes pending autosave first
        // Reads go through the writer queue so they see every save queued before them.
        var pages = await _storage.Write(s => LoadSection(s, section.Id));
        return await Export(pages, Safe(section.Name, "Section"), section.Name, fmt);
    }

    public async Task<string?> ExportNotebook(InkEngine engine, NotebookInfo notebook, ExportFormat? format = null)
    {
        var fmt = format ?? _settings().ExportFormat;
        await engine.SnapshotCurrentPage();
        var pages = await _storage.Write(s =>
        {
            var all = new List<ExportPage>();
            foreach (var sec in s.ListSections(notebook.Id)) all.AddRange(LoadSection(s, sec.Id));
            return all;
        });
        return await Export(pages, Safe(notebook.Name, "Notebook"), notebook.Name, fmt);
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

    /// <summary>Returns the file (or folder, for several images) that was written, or null if cancelled.</summary>
    private async Task<string?> Export(IReadOnlyList<ExportPage> pages, string baseName, string title, ExportFormat format)
    {
        var s = _settings();
        if (format == ExportFormat.Pdf)
        {
            var path = await ChooseFile(baseName, ".pdf", "PDF document");
            if (path is null) return null;
            await Task.Run(() => WritePdf(pages, path, title, s.ExportBackground));
            return path;
        }

        string ext = format == ExportFormat.Png ? ".png" : ".svg";
        int sheetCount = pages.Sum(p => PdfExporter.Paginate(p, new PdfExportOptions()).Count);
        if (sheetCount == 1)
        {
            var path = await ChooseFile(baseName, ext, format == ExportFormat.Png ? "PNG image" : "SVG drawing");
            if (path is null) return null;
            await WriteImages(pages, format, s, _ => path);
            return path;
        }

        // Several images: they go into a folder named after the export.
        var parent = await ChooseFolder();
        if (parent is null) return null;
        string folder = UniquePath(Path.Combine(parent, baseName), "");
        Directory.CreateDirectory(folder);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int pageNo = 0;
        await WriteImages(pages, format, s, (key) =>
        {
            var (pageIndex, sheetIndex, sheetsInPage) = key;
            string name = Safe(pages[pageIndex].Title, "Page " + (pageIndex + 1));
            if (sheetsInPage > 1) name += $" - {sheetIndex + 1}";
            string file = name + ext;
            for (int n = 2; !used.Add(file); n++) file = $"{name} ({n}){ext}";
            pageNo++;
            return Path.Combine(folder, file);
        });
        return folder;
    }

    private Task WriteImages(IReadOnlyList<ExportPage> pages, ExportFormat format, AppSettings s, Func<(int Page, int Sheet, int Sheets), string> pathFor)
        => format == ExportFormat.Png ? WritePngs(pages, s, pathFor) : Task.Run(() => WriteSvgs(pages, s, pathFor));

    private async Task WritePngs(IReadOnlyList<ExportPage> pages, AppSettings s, Func<(int Page, int Sheet, int Sheets), string> pathFor)
    {
        var tags = _tags();
        for (int p = 0; p < pages.Count; p++)
        {
            var sheets = PdfExporter.Paginate(pages[p], new PdfExportOptions());
            for (int i = 0; i < sheets.Count; i++)
                await PngExporter.WriteSheetAsync(pages[p], sheets[i], s.ExportBackground, Math.Clamp(s.PngDpi, 48, 600), tags, pathFor((p, i, sheets.Count)));
        }
    }

    private void WriteSvgs(IReadOnlyList<ExportPage> pages, AppSettings s, Func<(int Page, int Sheet, int Sheets), string> pathFor)
    {
        var outliner = new GlyphOutliner(CanvasDevice.GetSharedDevice(), _tags());
        for (int p = 0; p < pages.Count; p++)
        {
            var sheets = PdfExporter.Paginate(pages[p], new PdfExportOptions());
            for (int i = 0; i < sheets.Count; i++)
            {
                string path = pathFor((p, i, sheets.Count));
                string tmp = path + ".tmp";
                using (var w = new StreamWriter(tmp, false, new System.Text.UTF8Encoding(false)))
                    SvgExporter.WriteSheet(pages[p], sheets[i], s.ExportBackground, outliner, w, pages[p].Title);
                File.Move(tmp, path, overwrite: true);
            }
        }
    }

    private void WritePdf(IReadOnlyList<ExportPage> pages, string path, string title, bool includeBackground)
    {
        var outliner = new GlyphOutliner(CanvasDevice.GetSharedDevice(), _tags());
        string tmp = path + ".tmp";
        using (var f = File.Create(tmp))
            PdfExporter.Export(pages, f, new PdfExportOptions { Title = title, IncludeBackground = includeBackground }, outliner);
        File.Move(tmp, path, overwrite: true);
    }

    // ---------------------------------------------------------------- backup

    /// <summary>Writes every notebook to a single .nibpkg (a zip holding a consistent database snapshot).</summary>
    public async Task<string?> BackUpAll(InkEngine engine)
    {
        await engine.SnapshotCurrentPage(); // flush the page being edited
        var path = await ChooseFile($"Nib backup {DateTime.Now:yyyy-MM-dd}", ".nibpkg", "Nib backup");
        if (path is null) return null;
        string snapshot = Path.Combine(Path.GetTempPath(), $"nib-snapshot-{Guid.NewGuid():N}.db");
        try
        {
            await _storage.Write(s => s.BackupTo(snapshot));
            await Task.Run(() =>
            {
                string tmp = path + ".tmp";
                if (File.Exists(tmp)) File.Delete(tmp);
                using (var zip = ZipFile.Open(tmp, ZipArchiveMode.Create))
                {
                    zip.CreateEntryFromFile(snapshot, "notebooks.db", CompressionLevel.Optimal);
                    var manifest = zip.CreateEntry("manifest.txt");
                    using var w = new StreamWriter(manifest.Open());
                    w.Write($"Nib backup\ncreated={DateTimeOffset.Now:O}\napp=Nib\n");
                }
                File.Move(tmp, path, overwrite: true);
            });
        }
        finally { try { File.Delete(snapshot); } catch (IOException) { } }
        return path;
    }

    // ---------------------------------------------------------------- pickers and names

    private async Task<string?> ChooseFile(string suggested, string extension, string description)
    {
        var s = _settings();
        if (!s.AskExportLocation)
            return UniquePath(Path.Combine(DefaultFolder, suggested), extension);
        var picker = new FileSavePicker { SuggestedFileName = suggested, SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeChoices.Add(description, new List<string> { extension });
        WinRT.Interop.InitializeWithWindow.Initialize(picker, _hwnd);
        var file = await picker.PickSaveFileAsync();
        return file?.Path;
    }

    private async Task<string?> ChooseFolder()
    {
        if (!_settings().AskExportLocation) return DefaultFolder;
        return await PickFolder(_hwnd);
    }

    public static async Task<string?> PickFolder(IntPtr hwnd)
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        var folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }

    /// <summary>"name.ext", or "name (2).ext" when that exists, so exporting twice never overwrites silently.</summary>
    internal static string UniquePath(string withoutExtension, string extension)
    {
        string path = withoutExtension + extension;
        for (int n = 2; File.Exists(path) || Directory.Exists(path); n++) path = $"{withoutExtension} ({n}){extension}";
        return path;
    }

    private static string Safe(string name, string fallback)
    {
        if (string.IsNullOrWhiteSpace(name)) return fallback;
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name.Length > 80 ? name[..80] : name;
    }
}
