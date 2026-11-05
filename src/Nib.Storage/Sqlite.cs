using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Nib.Storage;

/// <summary>Raw SQLite C API (the subset Nib needs).</summary>
internal static unsafe partial class SqliteNative
{
    private const string Lib = "sqlite3";

    public const int SQLITE_OK = 0;
    public const int SQLITE_ROW = 100;
    public const int SQLITE_DONE = 101;
    public const int SQLITE_OPEN_READONLY = 0x1;
    public const int SQLITE_OPEN_READWRITE = 0x2;
    public const int SQLITE_OPEN_CREATE = 0x4;
    public const int SQLITE_OPEN_NOMUTEX = 0x8000;
    public const int SQLITE_NULL = 5;
    public static readonly IntPtr SQLITE_TRANSIENT = new(-1);

    static SqliteNative()
    {
        NativeLibrary.SetDllImportResolver(typeof(SqliteNative).Assembly, Resolve);
    }

    private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? path)
    {
        if (name != Lib) return IntPtr.Zero;
        string[] candidates = OperatingSystem.IsWindows()
            ? new[] { "winsqlite3", "sqlite3", "e_sqlite3" }
            : OperatingSystem.IsMacOS()
                ? new[] { "libsqlite3.dylib", "libsqlite3" }
                : new[] { "libsqlite3.so.0", "libsqlite3.so", "sqlite3" };
        foreach (var c in candidates)
            if (NativeLibrary.TryLoad(c, assembly, DllImportSearchPath.System32 | DllImportSearchPath.SafeDirectories, out var h)) return h;
        return IntPtr.Zero;
    }

    [LibraryImport(Lib, EntryPoint = "sqlite3_open_v2")]
    public static partial int open_v2(byte* filename, out IntPtr db, int flags, IntPtr vfs);
    [LibraryImport(Lib, EntryPoint = "sqlite3_close_v2")]
    public static partial int close_v2(IntPtr db);
    [LibraryImport(Lib, EntryPoint = "sqlite3_errmsg")]
    public static partial IntPtr errmsg(IntPtr db);
    [LibraryImport(Lib, EntryPoint = "sqlite3_busy_timeout")]
    public static partial int busy_timeout(IntPtr db, int ms);
    [LibraryImport(Lib, EntryPoint = "sqlite3_exec")]
    public static partial int exec(IntPtr db, byte* sql, IntPtr callback, IntPtr arg, out IntPtr errmsg);
    [LibraryImport(Lib, EntryPoint = "sqlite3_free")]
    public static partial void free(IntPtr p);
    [LibraryImport(Lib, EntryPoint = "sqlite3_prepare_v2")]
    public static partial int prepare_v2(IntPtr db, byte* sql, int nByte, out IntPtr stmt, IntPtr tail);
    [LibraryImport(Lib, EntryPoint = "sqlite3_step")]
    public static partial int step(IntPtr stmt);
    [LibraryImport(Lib, EntryPoint = "sqlite3_reset")]
    public static partial int reset(IntPtr stmt);
    [LibraryImport(Lib, EntryPoint = "sqlite3_clear_bindings")]
    public static partial int clear_bindings(IntPtr stmt);
    [LibraryImport(Lib, EntryPoint = "sqlite3_finalize")]
    public static partial int finalize(IntPtr stmt);
    [LibraryImport(Lib, EntryPoint = "sqlite3_bind_int64")]
    public static partial int bind_int64(IntPtr stmt, int index, long value);
    [LibraryImport(Lib, EntryPoint = "sqlite3_bind_double")]
    public static partial int bind_double(IntPtr stmt, int index, double value);
    [LibraryImport(Lib, EntryPoint = "sqlite3_bind_null")]
    public static partial int bind_null(IntPtr stmt, int index);
    [LibraryImport(Lib, EntryPoint = "sqlite3_bind_text")]
    public static partial int bind_text(IntPtr stmt, int index, byte* text, int n, IntPtr destructor);
    [LibraryImport(Lib, EntryPoint = "sqlite3_bind_blob")]
    public static partial int bind_blob(IntPtr stmt, int index, byte* data, int n, IntPtr destructor);
    [LibraryImport(Lib, EntryPoint = "sqlite3_column_int64")]
    public static partial long column_int64(IntPtr stmt, int col);
    [LibraryImport(Lib, EntryPoint = "sqlite3_column_double")]
    public static partial double column_double(IntPtr stmt, int col);
    [LibraryImport(Lib, EntryPoint = "sqlite3_column_text")]
    public static partial byte* column_text(IntPtr stmt, int col);
    [LibraryImport(Lib, EntryPoint = "sqlite3_column_blob")]
    public static partial byte* column_blob(IntPtr stmt, int col);
    [LibraryImport(Lib, EntryPoint = "sqlite3_column_bytes")]
    public static partial int column_bytes(IntPtr stmt, int col);
    [LibraryImport(Lib, EntryPoint = "sqlite3_column_type")]
    public static partial int column_type(IntPtr stmt, int col);
    [LibraryImport(Lib, EntryPoint = "sqlite3_changes")]
    public static partial int changes(IntPtr db);
}

public sealed class SqliteException : Exception
{
    public int Code { get; }
    public SqliteException(int code, string message) : base($"SQLite error {code}: {message}") => Code = code;
}

/// <summary>
/// Thin, allocation-light SQLite connection. Not thread-safe: each storage worker thread owns one.
/// Prepared statements are cached per SQL string.
/// </summary>
public sealed unsafe class SqliteDb : IDisposable
{
    private IntPtr _db;
    private readonly Dictionary<string, SqliteStatement> _cache = new();

    public SqliteDb(string path, bool readOnly = false)
    {
        var bytes = Utf8Z(path);
        int flags = (readOnly ? SqliteNative.SQLITE_OPEN_READONLY : SqliteNative.SQLITE_OPEN_READWRITE | SqliteNative.SQLITE_OPEN_CREATE) | SqliteNative.SQLITE_OPEN_NOMUTEX;
        fixed (byte* p = bytes)
        {
            int rc = SqliteNative.open_v2(p, out _db, flags, IntPtr.Zero);
            if (rc != SqliteNative.SQLITE_OK)
            {
                string msg = _db != IntPtr.Zero ? ErrorMessage() : "open failed";
                if (_db != IntPtr.Zero) SqliteNative.close_v2(_db);
                _db = IntPtr.Zero;
                throw new SqliteException(rc, msg);
            }
        }
        SqliteNative.busy_timeout(_db, 5000);
    }

    internal IntPtr Handle => _db;

    public void Execute(string sql)
    {
        var bytes = Utf8Z(sql);
        fixed (byte* p = bytes)
        {
            int rc = SqliteNative.exec(_db, p, IntPtr.Zero, IntPtr.Zero, out var err);
            if (rc != SqliteNative.SQLITE_OK)
            {
                string msg = err != IntPtr.Zero ? Marshal.PtrToStringUTF8(err) ?? "" : ErrorMessage();
                if (err != IntPtr.Zero) SqliteNative.free(err);
                throw new SqliteException(rc, msg + " in: " + sql);
            }
        }
    }

    /// <summary>Returns a cached, reset statement for the SQL.</summary>
    public SqliteStatement Prepare(string sql)
    {
        if (_cache.TryGetValue(sql, out var st))
        {
            st.Reset();
            return st;
        }
        var bytes = Utf8Z(sql);
        IntPtr handle;
        fixed (byte* p = bytes)
        {
            int rc = SqliteNative.prepare_v2(_db, p, bytes.Length, out handle, IntPtr.Zero);
            if (rc != SqliteNative.SQLITE_OK) throw new SqliteException(rc, ErrorMessage() + " in: " + sql);
        }
        st = new SqliteStatement(this, handle);
        _cache[sql] = st;
        return st;
    }

    public long ScalarInt64(string sql)
    {
        var st = Prepare(sql);
        long v = st.Step() ? st.GetInt64(0) : 0;
        // Always reset: an un-reset statement keeps its read transaction (and its old WAL snapshot) open.
        st.Reset();
        return v;
    }

    public int Changes => SqliteNative.changes(_db);

    public void InTransaction(Action body)
    {
        Execute("BEGIN IMMEDIATE");
        try
        {
            body();
            Execute("COMMIT");
        }
        catch
        {
            try { Execute("ROLLBACK"); } catch { /* keep original exception */ }
            throw;
        }
    }

    internal string ErrorMessage() => Marshal.PtrToStringUTF8(SqliteNative.errmsg(_db)) ?? "unknown error";

    internal static byte[] Utf8Z(string s)
    {
        int n = Encoding.UTF8.GetByteCount(s);
        var b = new byte[n + 1];
        Encoding.UTF8.GetBytes(s, b);
        return b;
    }

    public void Dispose()
    {
        foreach (var st in _cache.Values) st.Finalize_();
        _cache.Clear();
        if (_db != IntPtr.Zero)
        {
            SqliteNative.close_v2(_db);
            _db = IntPtr.Zero;
        }
    }
}

public sealed unsafe class SqliteStatement
{
    private readonly SqliteDb _db;
    private IntPtr _st;

    internal SqliteStatement(SqliteDb db, IntPtr st) { _db = db; _st = st; }

    public void Reset()
    {
        SqliteNative.reset(_st);
        SqliteNative.clear_bindings(_st);
    }

    public SqliteStatement Bind(int index, long value) { Check(SqliteNative.bind_int64(_st, index, value)); return this; }
    public SqliteStatement Bind(int index, int value) => Bind(index, (long)value);
    public SqliteStatement Bind(int index, bool value) => Bind(index, value ? 1L : 0L);
    public SqliteStatement Bind(int index, double value) { Check(SqliteNative.bind_double(_st, index, value)); return this; }

    public SqliteStatement Bind(int index, string? value)
    {
        if (value is null) { Check(SqliteNative.bind_null(_st, index)); return this; }
        int n = Encoding.UTF8.GetByteCount(value);
        Span<byte> buf = n <= 512 ? stackalloc byte[n + 1] : new byte[n + 1];
        Encoding.UTF8.GetBytes(value, buf);
        fixed (byte* p = buf) Check(SqliteNative.bind_text(_st, index, p, n, SqliteNative.SQLITE_TRANSIENT));
        return this;
    }

    public SqliteStatement Bind(int index, ReadOnlySpan<byte> blob)
    {
        fixed (byte* p = blob)
        {
            // Empty spans pin to null; SQLite needs a non-null pointer for a zero-length blob.
            byte dummy = 0;
            Check(SqliteNative.bind_blob(_st, index, p == null ? &dummy : p, blob.Length, SqliteNative.SQLITE_TRANSIENT));
        }
        return this;
    }

    public SqliteStatement BindNull(int index) { Check(SqliteNative.bind_null(_st, index)); return this; }

    /// <summary>Advances; true when a row is available.</summary>
    public bool Step()
    {
        int rc = SqliteNative.step(_st);
        if (rc == SqliteNative.SQLITE_ROW) return true;
        if (rc == SqliteNative.SQLITE_DONE) return false;
        throw new SqliteException(rc, _db.ErrorMessage());
    }

    /// <summary>Runs a statement that returns no rows.</summary>
    public void Run()
    {
        while (Step()) { }
        Reset();
    }

    public long GetInt64(int col) => SqliteNative.column_int64(_st, col);
    public int GetInt32(int col) => (int)SqliteNative.column_int64(_st, col);
    public double GetDouble(int col) => SqliteNative.column_double(_st, col);
    public bool IsNull(int col) => SqliteNative.column_type(_st, col) == SqliteNative.SQLITE_NULL;

    public string? GetString(int col)
    {
        byte* p = SqliteNative.column_text(_st, col);
        if (p == null) return null;
        int n = SqliteNative.column_bytes(_st, col);
        return Encoding.UTF8.GetString(p, n);
    }

    /// <summary>Blob view valid until the next Step/Reset.</summary>
    public ReadOnlySpan<byte> GetBlob(int col)
    {
        byte* p = SqliteNative.column_blob(_st, col);
        int n = SqliteNative.column_bytes(_st, col);
        return p == null || n == 0 ? ReadOnlySpan<byte>.Empty : new ReadOnlySpan<byte>(p, n);
    }

    private void Check(int rc)
    {
        if (rc != SqliteNative.SQLITE_OK) throw new SqliteException(rc, _db.ErrorMessage());
    }

    internal void Finalize_()
    {
        if (_st != IntPtr.Zero) { SqliteNative.finalize(_st); _st = IntPtr.Zero; }
    }
}
