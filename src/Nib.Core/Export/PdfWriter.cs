using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Numerics;
using System.Text;
using Nib.Core.Geometry;
using Nib.Core.Ink;
using Nib.Core.Rendering;

namespace Nib.Core.Export;

/// <summary>
/// Minimal PDF 1.7 writer: objects, Flate-compressed streams, transparency groups, outlines, xref.
/// Purpose-built for vector ink: every stroke becomes filled path geometry (no rasterization), with
/// knockout groups and Multiply blending for pencil and highlighter so translucent ink never stacks.
/// </summary>
public sealed class PdfDocumentBuilder : IDisposable
{
    private readonly Stream _out;
    private readonly bool _ownsStream;
    private readonly List<long> _offsets = new() { 0 };
    private long _pos;
    private readonly int _catalogId, _pagesId, _infoId;
    private readonly List<int> _pageIds = new();
    private readonly List<(string Title, int PageIndex)> _outlines = new();
    private readonly string _title;
    private bool _finished;

    public int PageCount => _pageIds.Count;

    public PdfDocumentBuilder(Stream output, string title, bool ownsStream = false)
    {
        _out = output;
        _ownsStream = ownsStream;
        _title = title;
        WriteAscii("%PDF-1.7\n%âãÏÓ\n");
        _catalogId = Reserve();
        _pagesId = Reserve();
        _infoId = Reserve();
    }

    public int Reserve()
    {
        _offsets.Add(-1);
        return _offsets.Count - 1;
    }

    /// <summary>Starts a page of the given size in PDF points (1/72 inch).</summary>
    public PdfPageCanvas BeginPage(float widthPt, float heightPt) => new(this, widthPt, heightPt);

    internal void EndPage(PdfPageCanvas page)
    {
        // Form XObjects (knockout groups) first.
        var xobjectNames = new StringBuilder();
        foreach (var form in page.Forms)
        {
            int id = Reserve();
            var dict = $"/Type /XObject /Subtype /Form /BBox [{F(form.BBox.Left)} {F(form.BBox.Top)} {F(form.BBox.Right)} {F(form.BBox.Bottom)}] " +
                       "/Group << /S /Transparency /K true /I true >> /Resources << >>";
            WriteStream(id, dict, form.Content.WrittenSpan);
            xobjectNames.Append('/').Append(form.Name).Append(' ').Append(id).Append(" 0 R ");
        }
        var gs = new StringBuilder();
        foreach (var (name, alpha, multiply) in page.GStates)
        {
            gs.Append('/').Append(name).Append(" << /Type /ExtGState /ca ").Append(F(alpha)).Append(" /CA ").Append(F(alpha));
            if (multiply) gs.Append(" /BM /Multiply");
            gs.Append(" >> ");
        }
        int contentId = Reserve();
        WriteStream(contentId, "", page.Content.WrittenSpan);

        int pageId = Reserve();
        var res = new StringBuilder("<< ");
        if (gs.Length > 0) res.Append("/ExtGState << ").Append(gs).Append(">> ");
        if (xobjectNames.Length > 0) res.Append("/XObject << ").Append(xobjectNames).Append(">> ");
        res.Append(">>");
        BeginObject(pageId);
        WriteAscii($"<< /Type /Page /Parent {_pagesId} 0 R /MediaBox [0 0 {F(page.WidthPt)} {F(page.HeightPt)}] " +
                   $"/Resources {res} /Contents {contentId} 0 R " +
                   "/Group << /S /Transparency /CS /DeviceRGB >> >>\n");
        EndObject();
        _pageIds.Add(pageId);
    }

    public void AddOutline(string title, int pageIndex) => _outlines.Add((title, pageIndex));

    public void Finish()
    {
        if (_finished) return;
        _finished = true;

        int outlinesId = 0;
        if (_outlines.Count > 0)
        {
            outlinesId = Reserve();
            var itemIds = new int[_outlines.Count];
            for (int i = 0; i < itemIds.Length; i++) itemIds[i] = Reserve();
            for (int i = 0; i < itemIds.Length; i++)
            {
                var (title, pageIndex) = _outlines[i];
                int target = _pageIds[Math.Clamp(pageIndex, 0, _pageIds.Count - 1)];
                var sb = new StringBuilder();
                sb.Append($"<< /Title {TextString(title)} /Parent {outlinesId} 0 R /Dest [{target} 0 R /XYZ null null null]");
                if (i > 0) sb.Append($" /Prev {itemIds[i - 1]} 0 R");
                if (i < itemIds.Length - 1) sb.Append($" /Next {itemIds[i + 1]} 0 R");
                sb.Append(" >>\n");
                BeginObject(itemIds[i]);
                WriteAscii(sb.ToString());
                EndObject();
            }
            BeginObject(outlinesId);
            WriteAscii($"<< /Type /Outlines /First {itemIds[0]} 0 R /Last {itemIds[^1]} 0 R /Count {itemIds.Length} >>\n");
            EndObject();
        }

        BeginObject(_pagesId);
        var kids = new StringBuilder();
        foreach (var id in _pageIds) kids.Append(id).Append(" 0 R ");
        WriteAscii($"<< /Type /Pages /Kids [{kids}] /Count {_pageIds.Count} >>\n");
        EndObject();

        BeginObject(_catalogId);
        WriteAscii(outlinesId != 0
            ? $"<< /Type /Catalog /Pages {_pagesId} 0 R /Outlines {outlinesId} 0 R /PageMode /UseOutlines >>\n"
            : $"<< /Type /Catalog /Pages {_pagesId} 0 R >>\n");
        EndObject();

        BeginObject(_infoId);
        string date = DateTimeOffset.Now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        WriteAscii($"<< /Title {TextString(_title)} /Producer (Nib) /Creator (Nib) /CreationDate (D:{date}) >>\n");
        EndObject();

        long xref = _pos;
        var x = new StringBuilder();
        x.Append("xref\n0 ").Append(_offsets.Count).Append('\n');
        x.Append("0000000000 65535 f \n");
        for (int i = 1; i < _offsets.Count; i++)
        {
            long off = _offsets[i];
            if (off < 0) x.Append("0000000000 65535 f \n");
            else x.Append(off.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        }
        x.Append($"trailer\n<< /Size {_offsets.Count} /Root {_catalogId} 0 R /Info {_infoId} 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        WriteAscii(x.ToString());
        _out.Flush();
    }

    private void BeginObject(int id)
    {
        _offsets[id] = _pos;
        WriteAscii($"{id} 0 obj\n");
    }

    private void EndObject() => WriteAscii("endobj\n");

    private void WriteStream(int id, string dictEntries, ReadOnlySpan<byte> data)
    {
        byte[] compressed;
        using (var ms = new MemoryStream(Math.Max(64, data.Length / 3)))
        {
            using (var z = new ZLibStream(ms, CompressionLevel.Fastest, leaveOpen: true)) z.Write(data);
            compressed = ms.ToArray();
        }
        BeginObject(id);
        WriteAscii($"<< {dictEntries} /Length {compressed.Length} /Filter /FlateDecode >>\nstream\n");
        _out.Write(compressed);
        _pos += compressed.Length;
        WriteAscii("\nendstream\n");
        EndObject();
    }

    private void WriteAscii(string s)
    {
        var bytes = Encoding.Latin1.GetBytes(s);
        _out.Write(bytes);
        _pos += bytes.Length;
    }

    /// <summary>PDF text string: ASCII literal when possible, else UTF-16BE hex with BOM.</summary>
    internal static string TextString(string s)
    {
        bool ascii = true;
        foreach (char c in s) if (c < 32 || c > 126) { ascii = false; break; }
        if (ascii) return "(" + s.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)") + ")";
        var sb = new StringBuilder("<FEFF");
        foreach (char c in s) sb.Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
        return sb.Append('>').ToString();
    }

    internal static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    public void Dispose()
    {
        if (_ownsStream) _out.Dispose();
    }
}

/// <summary>Byte buffer for content streams with compact invariant number formatting.</summary>
public sealed class PdfContent
{
    private byte[] _buf = new byte[8192];
    private int _len;

    public ReadOnlySpan<byte> WrittenSpan => _buf.AsSpan(0, _len);
    public int Length => _len;

    private void Ensure(int n)
    {
        if (_len + n > _buf.Length) Array.Resize(ref _buf, Math.Max(_buf.Length * 2, _len + n));
    }

    public PdfContent Num(float v)
    {
        Ensure(24);
        if (MathF.Abs(v) < 0.0005f) v = 0;
        v.TryFormat(_buf.AsSpan(_len), out int n, "0.###", CultureInfo.InvariantCulture);
        _len += n;
        _buf[_len++] = (byte)' ';
        return this;
    }

    public PdfContent Op(string op)
    {
        Ensure(op.Length + 1);
        for (int i = 0; i < op.Length; i++) _buf[_len++] = (byte)op[i];
        _buf[_len++] = (byte)'\n';
        return this;
    }

    public PdfContent Raw(string s)
    {
        Ensure(s.Length);
        for (int i = 0; i < s.Length; i++) _buf[_len++] = (byte)s[i];
        return this;
    }
}

/// <summary>
/// Drawing surface for one PDF page. Implements the shared drawing contracts so the same code paths
/// that feed the screen renderer (backgrounds, stroke geometry, tag glyphs) produce the PDF.
/// Coordinates are page units, y down, once <see cref="SetPageTransform"/> has been called.
/// </summary>
public sealed class PdfPageCanvas : IPathSink, IStrokeRunSink, IBackgroundSink, IShapeCanvas
{
    private const float Kappa = 0.5522847498f;
    private readonly PdfDocumentBuilder _doc;
    private PdfContent _target;
    private readonly PdfContent _page = new();
    private readonly Dictionary<(float, bool), string> _gstateNames = new();
    private PdfContent? _group;
    private RectF _groupBounds;
    private float _curX, _curY, _startX, _startY;
    private bool _inMultiplyRun;

    internal readonly List<(string Name, float Alpha, bool Multiply)> GStates = new();
    internal readonly List<(string Name, RectF BBox, PdfContent Content)> Forms = new();

    public float WidthPt { get; }
    public float HeightPt { get; }
    internal PdfContent Content => _page;

    internal PdfPageCanvas(PdfDocumentBuilder doc, float widthPt, float heightPt)
    {
        _doc = doc;
        WidthPt = widthPt;
        HeightPt = heightPt;
        _target = _page;
    }

    /// <summary>
    /// Sets the mapping from page units to PDF space: page point (originX, originY) lands at the page's
    /// top-left plus (offsetPtX, offsetPtY), scaled by <paramref name="scale"/>; y is flipped to point down.
    /// </summary>
    public void SetPageTransform(float originX, float originY, float scale, float offsetPtX = 0, float offsetPtY = 0)
    {
        float k = PageUnits.PdfPointsPerUnit * scale;
        _page.Num(k).Num(0).Num(0).Num(-k).Num(offsetPtX - originX * k).Num(HeightPt - offsetPtY + originY * k).Op("cm");
    }

    public void Save() => _target.Op("q");
    public void Restore() => _target.Op("Q");

    public void ClipRect(RectF r) => _target.Num(r.Left).Num(r.Top).Num(r.Width).Num(r.Height).Op("re W n");

    public void Finish() => _doc.EndPage(this);

    // ---- colors and graphics states ----

    private void SetFill(uint c) =>
        _target.Num(ColorUtil.R(c) / 255f).Num(ColorUtil.G(c) / 255f).Num(ColorUtil.B(c) / 255f).Op("rg");

    private void SetStroke(uint c) =>
        _target.Num(ColorUtil.R(c) / 255f).Num(ColorUtil.G(c) / 255f).Num(ColorUtil.B(c) / 255f).Op("RG");

    private string GState(float alpha, bool multiply)
    {
        alpha = MathF.Round(alpha * 100) / 100f;
        if (!_gstateNames.TryGetValue((alpha, multiply), out var name))
        {
            name = "GS" + _gstateNames.Count;
            _gstateNames[(alpha, multiply)] = name;
            GStates.Add((name, alpha, multiply));
        }
        return name;
    }

    // ---- IPathSink (stroke geometry: all figures same orientation, filled non-zero) ----

    public void BeginFigure(float x, float y) { _target.Num(x).Num(y).Op("m"); _startX = _curX = x; _startY = _curY = y; }
    public void LineTo(float x, float y) { _target.Num(x).Num(y).Op("l"); _curX = x; _curY = y; }
    public void EndFigure() { _target.Op("h"); _curX = _startX; _curY = _startY; }

    public void AddCircle(float cx, float cy, float r)
    {
        // Four cubic arcs with increasing angle (matches the polygon orientation).
        float k = r * Kappa;
        _target.Num(cx + r).Num(cy).Op("m");
        _target.Num(cx + r).Num(cy + k).Num(cx + k).Num(cy + r).Num(cx).Num(cy + r).Op("c");
        _target.Num(cx - k).Num(cy + r).Num(cx - r).Num(cy + k).Num(cx - r).Num(cy).Op("c");
        _target.Num(cx - r).Num(cy - k).Num(cx - k).Num(cy - r).Num(cx).Num(cy - r).Op("c");
        _target.Num(cx + k).Num(cy - r).Num(cx + r).Num(cy - k).Num(cx + r).Num(cy).Op("c");
        _target.Op("h");
    }

    // ---- general paths (glyph outlines) ----

    public void MoveTo(float x, float y) => BeginFigure(x, y);

    public void CubicTo(float x1, float y1, float x2, float y2, float x3, float y3)
    {
        _target.Num(x1).Num(y1).Num(x2).Num(y2).Num(x3).Num(y3).Op("c");
        _curX = x3; _curY = y3;
    }

    public void QuadTo(float x1, float y1, float x2, float y2)
    {
        // Exact quadratic → cubic elevation.
        float c1x = _curX + 2f / 3f * (x1 - _curX), c1y = _curY + 2f / 3f * (y1 - _curY);
        float c2x = x2 + 2f / 3f * (x1 - x2), c2y = y2 + 2f / 3f * (y1 - y2);
        CubicTo(c1x, c1y, c2x, c2y, x2, y2);
    }

    public void ClosePath() => EndFigure();

    /// <summary>Starts a filled path: call path methods, then <see cref="EndFill"/>.</summary>
    public void BeginFill(uint color) => SetFill(color);
    public void EndFill(bool evenOdd = false) => _target.Op(evenOdd ? "f*" : "f");

    // ---- IStrokeRunSink ----

    /// <summary>
    /// Wraps a multi-run translucent stroke in a knockout transparency group composited with Multiply,
    /// so the runs never darken each other where they overlap (same result as the screen's darken blend).
    /// </summary>
    public void BeginTranslucentStroke(RectF bounds)
    {
        _group = new PdfContent();
        _groupBounds = bounds;
        _target = _group;
    }

    public void EndTranslucentStroke()
    {
        if (_group is null) return;
        string name = "Fm" + Forms.Count;
        Forms.Add((name, _groupBounds, _group));
        _group = null;
        _target = _page;
        _page.Op("q").Raw("/" + GState(1f, true) + " ").Op("gs").Raw("/" + name + " ").Op("Do").Op("Q");
    }

    public IPathSink BeginRun(in StrokeRun run)
    {
        _inMultiplyRun = run.Multiply && _group is null;
        if (_inMultiplyRun) _target.Op("q").Raw("/" + GState(1f, true) + " ").Op("gs");
        SetFill(run.FillColor);
        return this;
    }

    public void EndRun()
    {
        _target.Op("f");
        if (_inMultiplyRun) _target.Op("Q");
        _inMultiplyRun = false;
    }

    // ---- IBackgroundSink ----

    public void FillRect(RectF r, uint color)
    {
        SetFill(color);
        _target.Num(r.Left).Num(r.Top).Num(r.Width).Num(r.Height).Op("re f");
    }

    public void Line(float x0, float y0, float x1, float y1, float width, uint color)
    {
        SetStroke(color);
        _target.Num(width).Op("w").Op("0 J").Num(x0).Num(y0).Op("m").Num(x1).Num(y1).Op("l S");
    }

    public void Dot(float cx, float cy, float radius, uint color)
    {
        SetFill(color);
        AddCircle(cx, cy, radius);
        _target.Op("f");
    }

    // ---- IShapeCanvas (tag glyphs) ----

    public void FillPolygon(ReadOnlySpan<Vector2> pts, uint color)
    {
        if (pts.Length < 3) return;
        SetFill(color);
        _target.Num(pts[0].X).Num(pts[0].Y).Op("m");
        for (int i = 1; i < pts.Length; i++) _target.Num(pts[i].X).Num(pts[i].Y).Op("l");
        _target.Op("h f");
    }

    public void FillCircle(float cx, float cy, float r, uint color) => Dot(cx, cy, r, color);

    public void StrokePolyline(ReadOnlySpan<Vector2> pts, bool closed, float width, uint color)
    {
        if (pts.Length < 2) return;
        SetStroke(color);
        _target.Num(width).Op("w").Op("1 J 1 j");
        _target.Num(pts[0].X).Num(pts[0].Y).Op("m");
        for (int i = 1; i < pts.Length; i++) _target.Num(pts[i].X).Num(pts[i].Y).Op("l");
        _target.Op(closed ? "h S" : "S");
    }
}
