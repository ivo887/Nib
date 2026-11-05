using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Nib.Core.Diagnostics;
using Nib.Core.Ink;
using Nib.Core.Model;

namespace Nib.Storage;

/// <summary>
/// Collects page changes on the ink thread (as an <see cref="IPageObserver"/>) and hands out compact
/// change sets: only strokes and text boxes that changed, last write wins per id.
/// Costs a dictionary insert per change on the ink thread; encoding and disk I/O happen elsewhere.
/// </summary>
public sealed class AutosaveTracker : IPageObserver
{
    private readonly Dictionary<long, Stroke?> _strokes = new();   // null = deleted
    private readonly Dictionary<long, TextContainer?> _texts = new();
    private PageLayout? _layout;
    private string? _title;

    public long PageId { get; }
    public bool HasChanges => _strokes.Count > 0 || _texts.Count > 0 || _layout is not null || _title is not null;
    /// <summary>Raised on the ink thread after each change (the app debounces the flush).</summary>
    public event Action<AutosaveTracker>? Changed;

    public AutosaveTracker(long pageId) => PageId = pageId;

    public void OnStrokesChanged(Page page, IReadOnlyList<Stroke> removed, IReadOnlyList<Stroke> added)
    {
        // Last write wins per id. Deleting a stroke that never reached the disk is a harmless no-op,
        // which keeps this correct through any sequence of edits, undos and redos.
        for (int i = 0; i < removed.Count; i++) _strokes[removed[i].Id] = null;
        for (int i = 0; i < added.Count; i++) _strokes[added[i].Id] = added[i];
        Changed?.Invoke(this);
    }

    public void OnTextChanged(Page page, TextContainer? oldText, TextContainer? newText)
    {
        if (newText is not null) _texts[newText.Id] = newText;
        else if (oldText is not null) _texts[oldText.Id] = null;
        Changed?.Invoke(this);
    }

    public void OnLayoutChanged(Page page, PageLayout oldLayout)
    {
        _layout = page.Layout;
        Changed?.Invoke(this);
    }

    public void OnTitleChanged(Page page)
    {
        _title = page.Title;
        Changed?.Invoke(this);
    }

    /// <summary>Returns the pending changes and clears the tracker. Ink thread only.</summary>
    public PageChangeSet TakeChanges()
    {
        var up = new List<Stroke>();
        var del = new List<long>();
        foreach (var (id, s) in _strokes)
        {
            if (s is null) del.Add(id); else up.Add(s);
        }
        var upT = new List<TextContainer>();
        var delT = new List<long>();
        foreach (var (id, t) in _texts)
        {
            if (t is null) delT.Add(id); else upT.Add(t);
        }
        var set = new PageChangeSet
        {
            PageId = PageId,
            UpsertStrokes = up,
            DeleteStrokes = del,
            UpsertTexts = upT,
            DeleteTexts = delT,
            Layout = _layout,
            Title = _title,
        };
        _strokes.Clear();
        _texts.Clear();
        _layout = null;
        _title = null;
        return set;
    }
}

/// <summary>
/// Two dedicated storage threads, each with its own SQLite connection: a writer (saves, structure edits)
/// and a reader (page loads, lists, tag summary). WAL mode lets them run in parallel, so preloading the
/// next page never waits behind an autosave. Nothing here ever runs on the UI or ink thread.
/// </summary>
public sealed class StorageService : IDisposable
{
    private readonly Worker _writer;
    private readonly Worker _reader;

    public string DatabasePath { get; }

    public StorageService(string databasePath)
    {
        DatabasePath = databasePath;
        // The writer creates/migrates the schema before the reader opens.
        _writer = new Worker("Nib storage writer", () => new NoteStore(databasePath));
        _writer.WaitReady();
        _reader = new Worker("Nib storage reader", () => new NoteStore(databasePath));
    }

    public Task<T> Read<T>(Func<NoteStore, T> work, CancellationToken ct = default) => _reader.Enqueue(work, ct);
    public Task<T> Write<T>(Func<NoteStore, T> work, CancellationToken ct = default) => _writer.Enqueue(work, ct);
    public Task Write(Action<NoteStore> work) => _writer.Enqueue<object?>(s => { work(s); return null; }, default);

    public Task Save(PageChangeSet changes)
    {
        if (changes.ChangeCount == 0) return Task.CompletedTask;
        return Write(s =>
        {
            var sw = Stopwatch.StartNew();
            s.Save(changes);
            NibEventSource.Log.Autosave(changes.ChangeCount, sw.Elapsed.TotalMilliseconds);
        });
    }

    /// <summary>Blocks until all queued writes are on disk (app exit, suspend).</summary>
    public void Flush(TimeSpan timeout) => _writer.Enqueue<object?>(s => { s.Checkpoint(); return null; }, default).Wait(timeout);

    public void Dispose()
    {
        _reader.Dispose();
        _writer.Dispose();
    }

    private sealed class Worker : IDisposable
    {
        private readonly BlockingCollection<Action<NoteStore>> _queue = new(new ConcurrentQueue<Action<NoteStore>>());
        private readonly Thread _thread;
        private readonly ManualResetEventSlim _ready = new(false);
        private Exception? _openError;

        public Worker(string name, Func<NoteStore> open)
        {
            _thread = new Thread(() => Run(open)) { IsBackground = true, Name = name, Priority = ThreadPriority.BelowNormal };
            _thread.Start();
        }

        public void WaitReady()
        {
            _ready.Wait();
            if (_openError is not null) throw new InvalidOperationException("Could not open the notebook database.", _openError);
        }

        private void Run(Func<NoteStore> open)
        {
            NoteStore store;
            try { store = open(); }
            catch (Exception e) { _openError = e; _ready.Set(); return; }
            _ready.Set();
            using (store)
            {
                foreach (var work in _queue.GetConsumingEnumerable()) work(store);
            }
        }

        public Task<T> Enqueue<T>(Func<NoteStore, T> work, CancellationToken ct)
        {
            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (_openError is not null) { tcs.SetException(_openError); return tcs.Task; }
            _queue.Add(store =>
            {
                if (ct.IsCancellationRequested) { tcs.TrySetCanceled(ct); return; }
                try { tcs.TrySetResult(work(store)); }
                catch (Exception e) { tcs.TrySetException(e); }
            });
            return tcs.Task;
        }

        public void Dispose()
        {
            _queue.CompleteAdding();
            _thread.Join(TimeSpan.FromSeconds(5));
        }
    }
}
