using System;
using System.Collections.Generic;
using Nib.Core.Editing;
using Nib.Core.Ink;
using Nib.Core.Model;
using Nib.Core.Serialization;

namespace Nib.Storage;

/// <summary>Everything that changed on one page since the last save. Immutable; built on the ink thread, written on the storage thread.</summary>
public sealed class PageChangeSet
{
    public long PageId { get; init; }
    public IReadOnlyList<Stroke> UpsertStrokes { get; init; } = Array.Empty<Stroke>();
    public IReadOnlyList<long> DeleteStrokes { get; init; } = Array.Empty<long>();
    public IReadOnlyList<TextContainer> UpsertTexts { get; init; } = Array.Empty<TextContainer>();
    public IReadOnlyList<long> DeleteTexts { get; init; } = Array.Empty<long>();
    public PageLayout? Layout { get; init; }
    public string? Title { get; init; }
    public int ChangeCount => UpsertStrokes.Count + DeleteStrokes.Count + UpsertTexts.Count + DeleteTexts.Count + (Layout is null ? 0 : 1) + (Title is null ? 0 : 1);
}

/// <summary>
/// SQLite-backed notebook store. Metadata in normalized tables, one row per stroke and per text
/// container (so saves are incremental), WAL journaling so the reader connection never waits on the writer.
/// Deleted notebooks/sections/pages are soft-deleted (recycle bin, purged after 60 days, from v1).
/// </summary>
public sealed class NoteStore : IDisposable
{
    public const int SchemaVersion = 1;
    private readonly SqliteDb _db;

    public string Path { get; }

    public NoteStore(string path, bool readOnly = false)
    {
        Path = path;
        _db = new SqliteDb(path, readOnly);
        _db.Execute("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON; PRAGMA temp_store=MEMORY; PRAGMA cache_size=-16000;");
        if (!readOnly) Migrate();
    }

    internal SqliteDb Db => _db;

    private void Migrate()
    {
        long version = _db.ScalarInt64("PRAGMA user_version");
        if (version >= SchemaVersion) return;
        _db.InTransaction(() =>
        {
            _db.Execute("""
                CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY, value TEXT);
                CREATE TABLE IF NOT EXISTS notebooks(
                    id INTEGER PRIMARY KEY, name TEXT NOT NULL, color INTEGER NOT NULL, sort REAL NOT NULL,
                    defaults TEXT, created INTEGER NOT NULL, modified INTEGER NOT NULL, deleted INTEGER);
                CREATE TABLE IF NOT EXISTS section_groups(
                    id INTEGER PRIMARY KEY, notebook_id INTEGER NOT NULL REFERENCES notebooks(id) ON DELETE CASCADE,
                    parent_group_id INTEGER REFERENCES section_groups(id) ON DELETE CASCADE,
                    name TEXT NOT NULL, sort REAL NOT NULL, deleted INTEGER);
                CREATE TABLE IF NOT EXISTS sections(
                    id INTEGER PRIMARY KEY, notebook_id INTEGER NOT NULL REFERENCES notebooks(id) ON DELETE CASCADE,
                    group_id INTEGER REFERENCES section_groups(id) ON DELETE SET NULL,
                    name TEXT NOT NULL, color INTEGER NOT NULL, sort REAL NOT NULL, defaults TEXT,
                    created INTEGER NOT NULL, modified INTEGER NOT NULL, deleted INTEGER);
                CREATE TABLE IF NOT EXISTS pages(
                    id INTEGER PRIMARY KEY, section_id INTEGER NOT NULL REFERENCES sections(id) ON DELETE CASCADE,
                    title TEXT NOT NULL DEFAULT '', sort REAL NOT NULL, level INTEGER NOT NULL DEFAULT 0,
                    layout TEXT NOT NULL, favorite INTEGER NOT NULL DEFAULT 0,
                    created INTEGER NOT NULL, modified INTEGER NOT NULL, opened INTEGER, deleted INTEGER);
                CREATE INDEX IF NOT EXISTS ix_pages_section ON pages(section_id, sort);
                CREATE INDEX IF NOT EXISTS ix_pages_opened ON pages(opened);
                CREATE TABLE IF NOT EXISTS strokes(
                    page_id INTEGER NOT NULL REFERENCES pages(id) ON DELETE CASCADE,
                    id INTEGER NOT NULL, z INTEGER NOT NULL, data BLOB NOT NULL,
                    PRIMARY KEY(page_id, id)) WITHOUT ROWID;
                CREATE TABLE IF NOT EXISTS texts(
                    page_id INTEGER NOT NULL REFERENCES pages(id) ON DELETE CASCADE,
                    id INTEGER NOT NULL, z INTEGER NOT NULL, data BLOB NOT NULL, plain TEXT NOT NULL,
                    PRIMARY KEY(page_id, id)) WITHOUT ROWID;
                CREATE TABLE IF NOT EXISTS tag_defs(
                    id INTEGER PRIMARY KEY, name TEXT NOT NULL, symbol INTEGER NOT NULL, color INTEGER NOT NULL,
                    is_checkbox INTEGER NOT NULL, shortcut INTEGER NOT NULL);
                CREATE TABLE IF NOT EXISTS tag_items(
                    page_id INTEGER NOT NULL REFERENCES pages(id) ON DELETE CASCADE,
                    text_id INTEGER NOT NULL, paragraph INTEGER NOT NULL, tag_id INTEGER NOT NULL,
                    checked INTEGER NOT NULL, snippet TEXT NOT NULL,
                    PRIMARY KEY(page_id, text_id, paragraph, tag_id)) WITHOUT ROWID;
                CREATE INDEX IF NOT EXISTS ix_tag_items_tag ON tag_items(tag_id);
                """);
            foreach (var t in TagDefinition.BuiltIn) UpsertTagDefinition(t);
            _db.Execute($"PRAGMA user_version={SchemaVersion}");
        });
    }

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private static DateTimeOffset FromMs(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms);

    // ---------------- meta ----------------

    public string? GetMeta(string key)
    {
        var st = _db.Prepare("SELECT value FROM meta WHERE key=?1");
        st.Bind(1, key);
        string? v = st.Step() ? st.GetString(0) : null;
        st.Reset();
        return v;
    }

    public void SetMeta(string key, string? value) =>
        _db.Prepare("INSERT OR REPLACE INTO meta(key, value) VALUES(?1, ?2)").Bind(1, key).Bind(2, value).Run();

    // ---------------- notebooks ----------------

    public List<NotebookInfo> ListNotebooks()
    {
        var list = new List<NotebookInfo>();
        var st = _db.Prepare("SELECT id, name, color, sort, defaults FROM notebooks WHERE deleted IS NULL ORDER BY sort");
        while (st.Step())
            list.Add(new NotebookInfo(st.GetInt64(0), st.GetString(1) ?? "", (uint)st.GetInt64(2), st.GetDouble(3), NibJson.DeserializePageDefaults(st.GetString(4))));
        st.Reset();
        return list;
    }

    public NotebookInfo CreateNotebook(string name, uint color)
    {
        long id = IdGenerator.Next();
        double sort = _db.ScalarInt64("SELECT IFNULL(MAX(sort),0) FROM notebooks") + 1;
        long now = Now();
        _db.Prepare("INSERT INTO notebooks(id, name, color, sort, created, modified) VALUES(?1,?2,?3,?4,?5,?5)")
            .Bind(1, id).Bind(2, name).Bind(3, (long)color).Bind(4, sort).Bind(5, now).Run();
        return new NotebookInfo(id, name, color, sort, null);
    }

    public void RenameNotebook(long id, string name) =>
        _db.Prepare("UPDATE notebooks SET name=?2, modified=?3 WHERE id=?1").Bind(1, id).Bind(2, name).Bind(3, Now()).Run();

    public void SetNotebookDefaults(long id, PageDefaults? d) =>
        _db.Prepare("UPDATE notebooks SET defaults=?2 WHERE id=?1").Bind(1, id).Bind(2, NibJson.Serialize(d)).Run();

    public void DeleteNotebook(long id) =>
        _db.Prepare("UPDATE notebooks SET deleted=?2 WHERE id=?1").Bind(1, id).Bind(2, Now()).Run();

    public void SetNotebookColor(long id, uint color) =>
        _db.Prepare("UPDATE notebooks SET color=?2, modified=?3 WHERE id=?1").Bind(1, id).Bind(2, (long)color).Bind(3, Now()).Run();

    public void ReorderNotebooks(IReadOnlyList<long> idsInOrder) => _db.InTransaction(() =>
    {
        var st = _db.Prepare("UPDATE notebooks SET sort=?2 WHERE id=?1");
        for (int i = 0; i < idsInOrder.Count; i++) { st.Bind(1, idsInOrder[i]).Bind(2, (double)(i + 1)); st.Run(); }
    });

    public void ReorderNotebook(long id, double sort) =>
        _db.Prepare("UPDATE notebooks SET sort=?2 WHERE id=?1").Bind(1, id).Bind(2, sort).Run();

    // ---------------- section groups & sections ----------------

    public List<SectionGroupInfo> ListSectionGroups(long notebookId)
    {
        var list = new List<SectionGroupInfo>();
        var st = _db.Prepare("SELECT id, parent_group_id, name, sort FROM section_groups WHERE notebook_id=?1 AND deleted IS NULL ORDER BY sort");
        st.Bind(1, notebookId);
        while (st.Step())
            list.Add(new SectionGroupInfo(st.GetInt64(0), notebookId, st.IsNull(1) ? null : st.GetInt64(1), st.GetString(2) ?? "", st.GetDouble(3)));
        st.Reset();
        return list;
    }

    public SectionGroupInfo CreateSectionGroup(long notebookId, long? parentGroupId, string name)
    {
        long id = IdGenerator.Next();
        var maxSt = _db.Prepare("SELECT IFNULL(MAX(sort),0) FROM section_groups WHERE notebook_id=?1");
        maxSt.Bind(1, notebookId);
        double sort = (maxSt.Step() ? maxSt.GetDouble(0) : 0) + 1;
        maxSt.Reset();
        var ins = _db.Prepare("INSERT INTO section_groups(id, notebook_id, parent_group_id, name, sort) VALUES(?1,?2,?3,?4,?5)");
        ins.Bind(1, id).Bind(2, notebookId);
        if (parentGroupId is { } pg) ins.Bind(3, pg); else ins.BindNull(3);
        ins.Bind(4, name).Bind(5, sort).Run();
        return new SectionGroupInfo(id, notebookId, parentGroupId, name, sort);
    }

    public List<SectionInfo> ListSections(long notebookId)
    {
        var list = new List<SectionInfo>();
        var st = _db.Prepare("SELECT id, group_id, name, color, sort, defaults FROM sections WHERE notebook_id=?1 AND deleted IS NULL ORDER BY sort");
        st.Bind(1, notebookId);
        while (st.Step())
            list.Add(new SectionInfo(st.GetInt64(0), notebookId, st.IsNull(1) ? null : st.GetInt64(1), st.GetString(2) ?? "",
                (uint)st.GetInt64(3), st.GetDouble(4), NibJson.DeserializePageDefaults(st.GetString(5))));
        st.Reset();
        return list;
    }

    public SectionInfo? GetSection(long id)
    {
        var st = _db.Prepare("SELECT notebook_id, group_id, name, color, sort, defaults FROM sections WHERE id=?1");
        st.Bind(1, id);
        SectionInfo? s = null;
        if (st.Step())
            s = new SectionInfo(id, st.GetInt64(0), st.IsNull(1) ? null : st.GetInt64(1), st.GetString(2) ?? "", (uint)st.GetInt64(3), st.GetDouble(4), NibJson.DeserializePageDefaults(st.GetString(5)));
        st.Reset();
        return s;
    }

    public SectionInfo CreateSection(long notebookId, long? groupId, string name, uint color)
    {
        long id = IdGenerator.Next();
        var maxSt = _db.Prepare("SELECT IFNULL(MAX(sort),0) FROM sections WHERE notebook_id=?1");
        maxSt.Bind(1, notebookId);
        double sort = (maxSt.Step() ? maxSt.GetDouble(0) : 0) + 1;
        maxSt.Reset();
        long now = Now();
        var ins = _db.Prepare("INSERT INTO sections(id, notebook_id, group_id, name, color, sort, created, modified) VALUES(?1,?2,?3,?4,?5,?6,?7,?7)");
        ins.Bind(1, id).Bind(2, notebookId);
        if (groupId is { } g) ins.Bind(3, g); else ins.BindNull(3);
        ins.Bind(4, name).Bind(5, (long)color).Bind(6, sort).Bind(7, now).Run();
        return new SectionInfo(id, notebookId, groupId, name, color, sort, null);
    }

    public void RenameSection(long id, string name) =>
        _db.Prepare("UPDATE sections SET name=?2, modified=?3 WHERE id=?1").Bind(1, id).Bind(2, name).Bind(3, Now()).Run();

    public void SetSectionColor(long id, uint color) =>
        _db.Prepare("UPDATE sections SET color=?2 WHERE id=?1").Bind(1, id).Bind(2, (long)color).Run();

    public void SetSectionDefaults(long id, PageDefaults? d) =>
        _db.Prepare("UPDATE sections SET defaults=?2 WHERE id=?1").Bind(1, id).Bind(2, NibJson.Serialize(d)).Run();

    /// <summary>Moves a section within or between notebooks (drag and drop).</summary>
    public void MoveSection(long id, long notebookId, long? groupId, double sort)
    {
        var st = _db.Prepare("UPDATE sections SET notebook_id=?2, group_id=?3, sort=?4, modified=?5 WHERE id=?1");
        st.Bind(1, id).Bind(2, notebookId);
        if (groupId is { } g) st.Bind(3, g); else st.BindNull(3);
        st.Bind(4, sort).Bind(5, Now()).Run();
    }

    public void DeleteSection(long id) =>
        _db.Prepare("UPDATE sections SET deleted=?2 WHERE id=?1").Bind(1, id).Bind(2, Now()).Run();

    // ---------------- pages ----------------

    private const string PageColumns = "id, section_id, title, sort, level, layout, created, modified, favorite";

    private static PageInfo ReadPage(SqliteStatement st) => new(
        st.GetInt64(0), st.GetInt64(1), st.GetString(2) ?? "", st.GetDouble(3), st.GetInt32(4),
        NibJson.DeserializePageLayout(st.GetString(5)) ?? new PageLayout(),
        FromMs(st.GetInt64(6)), FromMs(st.GetInt64(7)), st.GetInt64(8) != 0);

    public List<PageInfo> ListPages(long sectionId)
    {
        var list = new List<PageInfo>();
        var st = _db.Prepare($"SELECT {PageColumns} FROM pages WHERE section_id=?1 AND deleted IS NULL ORDER BY sort");
        st.Bind(1, sectionId);
        while (st.Step()) list.Add(ReadPage(st));
        st.Reset();
        return list;
    }

    public PageInfo? GetPageInfo(long id)
    {
        var st = _db.Prepare($"SELECT {PageColumns} FROM pages WHERE id=?1 AND deleted IS NULL");
        st.Bind(1, id);
        PageInfo? p = st.Step() ? ReadPage(st) : null;
        st.Reset();
        return p;
    }

    /// <summary>Creates a page. With <paramref name="afterPageId"/> it is inserted right after that page (and its subpages).</summary>
    public PageInfo CreatePage(long sectionId, string title, PageLayout layout, int level = 0, long? afterPageId = null)
    {
        long id = IdGenerator.Next();
        double sort = NextSort(sectionId, afterPageId);
        long now = Now();
        _db.Prepare("INSERT INTO pages(id, section_id, title, sort, level, layout, created, modified) VALUES(?1,?2,?3,?4,?5,?6,?7,?7)")
            .Bind(1, id).Bind(2, sectionId).Bind(3, title).Bind(4, sort).Bind(5, Math.Clamp(level, 0, PageInfo.MaxLevel))
            .Bind(6, NibJson.Serialize(layout)).Bind(7, now).Run();
        return new PageInfo(id, sectionId, title, sort, level, layout, FromMs(now), FromMs(now), false);
    }

    private double NextSort(long sectionId, long? afterPageId)
    {
        var pages = ListPages(sectionId);
        if (pages.Count == 0) return 1;
        if (afterPageId is null) return pages[^1].Sort + 1;
        int idx = pages.FindIndex(p => p.Id == afterPageId);
        if (idx < 0) return pages[^1].Sort + 1;
        // Skip the page's subpages so the new page follows its whole group.
        int end = idx;
        while (end + 1 < pages.Count && pages[end + 1].Level > pages[idx].Level) end++;
        return end + 1 < pages.Count ? (pages[end].Sort + pages[end + 1].Sort) / 2 : pages[end].Sort + 1;
    }

    public void RenamePage(long id, string title) =>
        _db.Prepare("UPDATE pages SET title=?2, modified=?3 WHERE id=?1").Bind(1, id).Bind(2, title).Bind(3, Now()).Run();

    public void SetPageLevel(long id, int level) =>
        _db.Prepare("UPDATE pages SET level=?2 WHERE id=?1").Bind(1, id).Bind(2, Math.Clamp(level, 0, PageInfo.MaxLevel)).Run();

    public void MovePage(long id, long sectionId, double sort) =>
        _db.Prepare("UPDATE pages SET section_id=?2, sort=?3, modified=?4 WHERE id=?1").Bind(1, id).Bind(2, sectionId).Bind(3, sort).Bind(4, Now()).Run();

    /// <summary>Persists a drag-and-drop order (sort = position).</summary>
    public void ReorderPages(IReadOnlyList<long> idsInOrder) => _db.InTransaction(() =>
    {
        var st = _db.Prepare("UPDATE pages SET sort=?2 WHERE id=?1");
        for (int i = 0; i < idsInOrder.Count; i++) { st.Bind(1, idsInOrder[i]).Bind(2, (double)(i + 1)); st.Run(); }
    });

    public void ReorderSections(IReadOnlyList<long> idsInOrder) => _db.InTransaction(() =>
    {
        var st = _db.Prepare("UPDATE sections SET sort=?2 WHERE id=?1");
        for (int i = 0; i < idsInOrder.Count; i++) { st.Bind(1, idsInOrder[i]).Bind(2, (double)(i + 1)); st.Run(); }
    });

    /// <summary>Moves a page (with its subpages) to the end of another section.</summary>
    public void MovePageToSection(long pageId, long sectionId)
    {
        var info = GetPageInfo(pageId);
        if (info is null) return;
        var source = ListPages(info.SectionId);
        int idx = source.FindIndex(p => p.Id == pageId);
        var group = new List<PageInfo> { info };
        for (int i = idx + 1; i < source.Count && source[i].Level > info.Level; i++) group.Add(source[i]);
        var target = ListPages(sectionId);
        double sort = target.Count == 0 ? 1 : target[^1].Sort + 1;
        _db.InTransaction(() =>
        {
            foreach (var p in group)
            {
                MovePage(p.Id, sectionId, sort++);
                SetPageLevel(p.Id, p.Level - info.Level); // keep the subpage structure, rooted at level 0
            }
        });
    }

    public void SetFavorite(long id, bool favorite) =>
        _db.Prepare("UPDATE pages SET favorite=?2 WHERE id=?1").Bind(1, id).Bind(2, favorite).Run();

    public void DeletePage(long id) =>
        _db.Prepare("UPDATE pages SET deleted=?2 WHERE id=?1").Bind(1, id).Bind(2, Now()).Run();

    public void RestorePage(long id) =>
        _db.Prepare("UPDATE pages SET deleted=NULL WHERE id=?1").Bind(1, id).Run();

    public void MarkOpened(long id) =>
        _db.Prepare("UPDATE pages SET opened=?2 WHERE id=?1").Bind(1, id).Bind(2, Now()).Run();

    public List<PageInfo> RecentPages(int count)
    {
        var list = new List<PageInfo>();
        var st = _db.Prepare($"SELECT {PageColumns} FROM pages WHERE deleted IS NULL AND opened IS NOT NULL ORDER BY opened DESC LIMIT ?1");
        st.Bind(1, count);
        while (st.Step()) list.Add(ReadPage(st));
        st.Reset();
        return list;
    }

    public List<PageInfo> FavoritePages()
    {
        var list = new List<PageInfo>();
        var st = _db.Prepare($"SELECT {PageColumns} FROM pages WHERE deleted IS NULL AND favorite=1 ORDER BY title");
        while (st.Step()) list.Add(ReadPage(st));
        st.Reset();
        return list;
    }

    // ---------------- page content ----------------

    /// <summary>Loads and decodes a page. Safe to call on a background thread (reader connection).</summary>
    public Page? LoadPage(long pageId)
    {
        var info = GetPageInfo(pageId);
        if (info is null) return null;
        var strokes = new List<Stroke>(256);
        var st = _db.Prepare("SELECT id, z, data FROM strokes WHERE page_id=?1");
        st.Bind(1, pageId);
        while (st.Step())
        {
            try { strokes.Add(InkCodec.Decode(st.GetInt64(0), st.GetInt64(1), st.GetBlob(2))); }
            catch (FormatException) { /* skip a corrupt stroke rather than losing the page */ }
        }
        st.Reset();
        var texts = new List<TextContainer>();
        var tt = _db.Prepare("SELECT id, z, data FROM texts WHERE page_id=?1");
        tt.Bind(1, pageId);
        while (tt.Step())
        {
            try { texts.Add(TextCodec.Decode(tt.GetInt64(0), tt.GetInt64(1), tt.GetBlob(2))); }
            catch (FormatException) { }
        }
        tt.Reset();
        return Page.Load(info.Id, info.SectionId, info.Title, info.Layout, strokes, texts);
    }

    /// <summary>Writes one page's changes atomically.</summary>
    public void Save(PageChangeSet c)
    {
        if (c.ChangeCount == 0) return;
        _db.InTransaction(() =>
        {
            if (c.UpsertStrokes.Count > 0)
            {
                var up = _db.Prepare("INSERT OR REPLACE INTO strokes(page_id, id, z, data) VALUES(?1,?2,?3,?4)");
                foreach (var s in c.UpsertStrokes)
                {
                    up.Bind(1, c.PageId).Bind(2, s.Id).Bind(3, s.Z).Bind(4, InkCodec.Encode(s));
                    up.Run();
                }
            }
            if (c.DeleteStrokes.Count > 0)
            {
                var del = _db.Prepare("DELETE FROM strokes WHERE page_id=?1 AND id=?2");
                foreach (var id in c.DeleteStrokes) { del.Bind(1, c.PageId).Bind(2, id); del.Run(); }
            }
            if (c.UpsertTexts.Count > 0 || c.DeleteTexts.Count > 0)
            {
                var upT = _db.Prepare("INSERT OR REPLACE INTO texts(page_id, id, z, data, plain) VALUES(?1,?2,?3,?4,?5)");
                var clearTags = _db.Prepare("DELETE FROM tag_items WHERE page_id=?1 AND text_id=?2");
                var addTag = _db.Prepare("INSERT OR REPLACE INTO tag_items(page_id, text_id, paragraph, tag_id, checked, snippet) VALUES(?1,?2,?3,?4,?5,?6)");
                foreach (var t in c.UpsertTexts)
                {
                    upT.Bind(1, c.PageId).Bind(2, t.Id).Bind(3, t.Z).Bind(4, TextCodec.Encode(t)).Bind(5, t.Text.PlainText);
                    upT.Run();
                    clearTags.Bind(1, c.PageId).Bind(2, t.Id); clearTags.Run();
                    foreach (var (para, tag, isChecked, snippet) in TagOps.Extract(t))
                    {
                        addTag.Bind(1, c.PageId).Bind(2, t.Id).Bind(3, para).Bind(4, tag).Bind(5, isChecked).Bind(6, snippet);
                        addTag.Run();
                    }
                }
                var delT = _db.Prepare("DELETE FROM texts WHERE page_id=?1 AND id=?2");
                foreach (var id in c.DeleteTexts)
                {
                    delT.Bind(1, c.PageId).Bind(2, id); delT.Run();
                    clearTags.Bind(1, c.PageId).Bind(2, id); clearTags.Run();
                }
            }
            if (c.Layout is not null)
                _db.Prepare("UPDATE pages SET layout=?2 WHERE id=?1").Bind(1, c.PageId).Bind(2, NibJson.Serialize(c.Layout)).Run();
            if (c.Title is not null)
                _db.Prepare("UPDATE pages SET title=?2 WHERE id=?1").Bind(1, c.PageId).Bind(2, c.Title).Run();
            _db.Prepare("UPDATE pages SET modified=?2 WHERE id=?1").Bind(1, c.PageId).Bind(2, Now()).Run();
        });
    }

    // ---------------- tags ----------------

    public void UpsertTagDefinition(TagDefinition t) =>
        _db.Prepare("INSERT OR REPLACE INTO tag_defs(id, name, symbol, color, is_checkbox, shortcut) VALUES(?1,?2,?3,?4,?5,?6)")
            .Bind(1, t.Id).Bind(2, t.Name).Bind(3, (int)t.Symbol).Bind(4, (long)t.Color).Bind(5, t.IsCheckbox).Bind(6, t.Shortcut).Run();

    public List<TagDefinition> ListTagDefinitions()
    {
        var list = new List<TagDefinition>();
        var st = _db.Prepare("SELECT id, name, symbol, color, is_checkbox, shortcut FROM tag_defs ORDER BY shortcut, id");
        while (st.Step())
            list.Add(new TagDefinition(st.GetInt32(0), st.GetString(1) ?? "", (TagSymbol)st.GetInt32(2), (uint)st.GetInt64(3), st.GetInt64(4) != 0, st.GetInt32(5)));
        st.Reset();
        return list;
    }

    /// <summary>Tag summary across all notebooks. Optional filter by tag and hiding completed To-Dos.</summary>
    public List<TagItem> TagSummary(int? tagId = null, bool includeChecked = true)
    {
        var list = new List<TagItem>();
        string sql = """
            SELECT ti.page_id, ti.text_id, ti.paragraph, ti.tag_id, ti.checked, ti.snippet, p.title, s.name, n.name
            FROM tag_items ti
            JOIN pages p ON p.id = ti.page_id AND p.deleted IS NULL
            JOIN sections s ON s.id = p.section_id AND s.deleted IS NULL
            JOIN notebooks n ON n.id = s.notebook_id AND n.deleted IS NULL
            WHERE (?1 IS NULL OR ti.tag_id = ?1) AND (?2 = 1 OR ti.checked = 0)
            ORDER BY ti.tag_id, n.sort, s.sort, p.sort, ti.text_id, ti.paragraph
            """;
        var st = _db.Prepare(sql);
        if (tagId is { } t) st.Bind(1, t); else st.BindNull(1);
        st.Bind(2, includeChecked);
        while (st.Step())
            list.Add(new TagItem(st.GetInt64(0), st.GetInt64(1), st.GetInt32(2), st.GetInt32(3), st.GetInt64(4) != 0,
                st.GetString(5) ?? "", st.GetString(6) ?? "", st.GetString(7) ?? "", st.GetString(8) ?? ""));
        st.Reset();
        return list;
    }

    // ---------------- first run ----------------

    /// <summary>Creates a starter notebook, section and page if the store is empty. Returns the page to open.</summary>
    public long EnsureStarterContent()
    {
        var notebooks = ListNotebooks();
        if (notebooks.Count > 0)
        {
            foreach (var nb in notebooks)
                foreach (var s in ListSections(nb.Id))
                {
                    var pages = ListPages(s.Id);
                    if (pages.Count > 0) return pages[0].Id;
                }
            var firstSection = ListSections(notebooks[0].Id);
            var sec = firstSection.Count > 0 ? firstSection[0] : CreateSection(notebooks[0].Id, null, "Quick Notes", SectionColors.ForIndex(0));
            return CreatePage(sec.Id, "", PageDefaults.ResolveNewPage(notebooks[0].Defaults, sec.Defaults)).Id;
        }
        long pageId = 0;
        _db.InTransaction(() =>
        {
            var nb = CreateNotebook("My Notebook", 0xFF7A5AF8);
            var sec = CreateSection(nb.Id, null, "Quick Notes", SectionColors.ForIndex(0));
            pageId = CreatePage(sec.Id, "", PageDefaults.ResolveNewPage(null, null)).Id;
        });
        return pageId;
    }

    /// <summary>The "Unfiled notes" section that Quick Notes land in (created on first use).</summary>
    public long EnsureUnfiledSection()
    {
        var idText = GetMeta("unfiled_section");
        if (long.TryParse(idText, out long id) && GetSection(id) is not null) return id;
        var nbs = ListNotebooks();
        var nb = nbs.Count > 0 ? nbs[0] : CreateNotebook("My Notebook", 0xFF7A5AF8);
        var sec = CreateSection(nb.Id, null, "Unfiled notes", 0xFFC0C0C0);
        SetMeta("unfiled_section", sec.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return sec.Id;
    }

    public void Checkpoint() => _db.Execute("PRAGMA wal_checkpoint(PASSIVE)");

    /// <summary>
    /// Writes a consistent, compact copy of the whole database to <paramref name="path"/> (which must not exist),
    /// safe to run while the app is writing: SQLite takes a read snapshot.
    /// </summary>
    public void BackupTo(string path) => _db.Execute("VACUUM INTO '" + path.Replace("'", "''") + "'");

    public void Dispose() => _db.Dispose();
}
