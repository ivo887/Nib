using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Nib.Core.Ink;
using Nib.Core.Model;

namespace Nib.Core.Serialization;

/// <summary>
/// Compact lossless-at-device-precision stroke format ("NIB1"), one blob per stroke so autosave writes
/// only changed strokes. Per point: zig-zag varint deltas of position (1/64 page unit ≈ 4 µm),
/// pressure (16-bit, exact for 4096- and 8192-level pens), tilt (0.1°), twist (0.1°) and time (ms).
/// Typical size: 6–9 bytes per point. Stores the pen dynamics too, so strokes re-render identically.
/// </summary>
public static class InkCodec
{
    public const byte Version = 1;
    private const float PosScale = 64f;
    private const float PressureScale = 65535f;
    private const float AngleScale = 10f;

    [ThreadStatic] private static VarintWriter? t_writer;

    public static byte[] Encode(Stroke s)
    {
        var w = t_writer ??= new VarintWriter(4096);
        w.Reset();
        w.Byte(Version);
        w.Byte((byte)s.Kind);
        w.Byte((byte)s.Flags);
        w.UInt32(s.Color);
        w.Single(s.Width);
        WriteDynamics(w, s.Dynamics);

        var pts = s.Points;
        w.UVar((ulong)pts.Length);
        bool tilt = (s.Flags & StrokeFlags.HasTilt) != 0;
        bool twist = (s.Flags & StrokeFlags.HasTwist) != 0;
        long px = 0, py = 0, pp = 0, ptx = 0, pty = 0, ptw = 0, pt = 0;
        for (int i = 0; i < pts.Length; i++)
        {
            ref readonly var p = ref pts[i];
            long x = Q(p.X, PosScale), y = Q(p.Y, PosScale);
            long pr = Q(Math.Clamp(p.Pressure, 0f, 1f), PressureScale);
            w.SVar(x - px); w.SVar(y - py); w.SVar(pr - pp);
            px = x; py = y; pp = pr;
            if (tilt)
            {
                long tx = Q(p.TiltX, AngleScale), ty = Q(p.TiltY, AngleScale);
                w.SVar(tx - ptx); w.SVar(ty - pty);
                ptx = tx; pty = ty;
            }
            if (twist)
            {
                long tw = p.Twist < 0 ? -1 : Q(p.Twist, AngleScale);
                w.SVar(tw - ptw);
                ptw = tw;
            }
            long t = Math.Max(p.TimeMs, pt);
            w.UVar((ulong)(t - pt));
            pt = t;
        }
        return w.ToArray();
    }

    public static Stroke Decode(long id, long z, ReadOnlySpan<byte> data)
    {
        var r = new VarintReader(data);
        byte version = r.Byte();
        if (version != Version) throw new FormatException($"Unsupported ink format version {version}.");
        var kind = (PenKind)r.Byte();
        var flags = (StrokeFlags)r.Byte();
        uint color = r.UInt32();
        float width = r.Single();
        var dyn = ReadDynamics(ref r);

        int n = checked((int)r.UVar());
        if (n <= 0 || n > 1_000_000) throw new FormatException("Invalid point count.");
        bool tilt = (flags & StrokeFlags.HasTilt) != 0;
        bool twist = (flags & StrokeFlags.HasTwist) != 0;
        var pts = new InkPoint[n];
        long px = 0, py = 0, pp = 0, ptx = 0, pty = 0, ptw = 0, pt = 0;
        for (int i = 0; i < n; i++)
        {
            px += r.SVar(); py += r.SVar(); pp += r.SVar();
            if (tilt) { ptx += r.SVar(); pty += r.SVar(); }
            if (twist) ptw += r.SVar();
            pt += (long)r.UVar();
            pts[i] = new InkPoint(
                px / PosScale, py / PosScale, pp / PressureScale,
                tilt ? ptx / AngleScale : 0f, tilt ? pty / AngleScale : 0f,
                twist ? (ptw < 0 ? -1f : ptw / AngleScale) : -1f,
                (int)pt);
        }
        return new Stroke(id, z, kind, color, width, dyn, flags, pts);
    }

    private static long Q(float v, float scale) => (long)MathF.Round(v * scale);

    internal static void WriteDynamics(VarintWriter w, PenDynamics d)
    {
        var c = d.Curve;
        w.Byte((byte)c.Kind);
        if (c.Kind == PressureCurveKind.Custom)
        {
            w.Byte((byte)c.Points.Count);
            foreach (var (x, y) in c.Points) { w.UInt16(U16(x)); w.UInt16(U16(y)); }
        }
        w.UInt16(U16(c.InputMin));
        w.UInt16(U16(c.InputMax));
        w.Byte(d.PressureEnabled ? (byte)1 : (byte)0);
        w.UInt16(U16(d.MinWidthRatio));
        w.UInt16(U16(d.TiltSensitivity));
        w.UInt16(U16(d.SpeedSensitivity));
    }

    internal static PenDynamics ReadDynamics(ref VarintReader r)
    {
        var kind = (PressureCurveKind)r.Byte();
        PressureCurve curve;
        if (kind == PressureCurveKind.Custom)
        {
            int n = r.Byte();
            var pts = new (float, float)[n];
            for (int i = 0; i < n; i++) pts[i] = (F16(r.UInt16()), F16(r.UInt16()));
            float inMin = F16(r.UInt16()), inMax = F16(r.UInt16());
            curve = new PressureCurve(PressureCurveKind.Custom, pts, inMin, inMax);
        }
        else
        {
            float inMin = F16(r.UInt16()), inMax = F16(r.UInt16());
            curve = PressureCurve.FromKind(kind);
            if (inMin != 0f || inMax != 1f) curve = curve.WithCalibration(inMin, inMax);
        }
        bool enabled = r.Byte() != 0;
        float minW = F16(r.UInt16()), tiltS = F16(r.UInt16()), speedS = F16(r.UInt16());
        // Share the default instance where possible: thousands of strokes, one object.
        var d = new PenDynamics { Curve = curve, PressureEnabled = enabled, MinWidthRatio = minW, TiltSensitivity = tiltS, SpeedSensitivity = speedS };
        return DynamicsCache.Intern(d);
    }

    private static ushort U16(float v) => (ushort)MathF.Round(Math.Clamp(v, 0f, 1f) * 65535f);
    private static float F16(ushort v) => v / 65535f;

    /// <summary>Interns identical dynamics so a 10,000-stroke page holds a handful of objects.</summary>
    private static class DynamicsCache
    {
        [ThreadStatic] private static Dictionary<PenDynamics, PenDynamics>? t_cache;

        public static PenDynamics Intern(PenDynamics d)
        {
            var cache = t_cache ??= new Dictionary<PenDynamics, PenDynamics>();
            if (cache.TryGetValue(d, out var existing)) return existing;
            if (cache.Count > 256) cache.Clear();
            cache[d] = d;
            return d;
        }
    }
}

/// <summary>Binary format for text containers (position, size, rich text, tags).</summary>
public static class TextCodec
{
    public const byte Version = 1;

    [ThreadStatic] private static VarintWriter? t_writer;

    public static byte[] Encode(TextContainer c)
    {
        var w = t_writer ??= new VarintWriter(1024);
        w.Reset();
        w.Byte(Version);
        w.Single(c.X); w.Single(c.Y); w.Single(c.Width); w.Single(c.MeasuredHeight);
        var paras = c.Text.Paragraphs;
        w.UVar((ulong)paras.Length);
        foreach (var p in paras)
        {
            w.Byte((byte)p.Kind);
            w.Byte((byte)Math.Clamp(p.Indent, 0, 8));
            w.Byte(p.Checked ? (byte)1 : (byte)0);
            w.UVar((ulong)p.TagIds.Length);
            foreach (int t in p.TagIds) w.UVar((ulong)t);
            w.UVar((ulong)p.Runs.Length);
            foreach (var run in p.Runs)
            {
                w.String(run.Text);
                w.Byte((byte)run.Style.Flags);
                w.Single(run.Style.SizePt);
                w.UInt32(run.Style.Color);
                w.UInt32(run.Style.Highlight);
                w.String(run.Style.Font);
            }
        }
        return w.ToArray();
    }

    public static TextContainer Decode(long id, long z, ReadOnlySpan<byte> data)
    {
        var r = new VarintReader(data);
        byte v = r.Byte();
        if (v != Version) throw new FormatException($"Unsupported text format version {v}.");
        float x = r.Single(), y = r.Single(), width = r.Single(), measured = r.Single();
        int pc = checked((int)r.UVar());
        var paras = ImmutableArray.CreateBuilder<Paragraph>(pc);
        for (int i = 0; i < pc; i++)
        {
            var kind = (ParagraphKind)r.Byte();
            int indent = r.Byte();
            bool isChecked = r.Byte() != 0;
            int tc = checked((int)r.UVar());
            var tags = ImmutableArray.CreateBuilder<int>(tc);
            for (int k = 0; k < tc; k++) tags.Add((int)r.UVar());
            int rc = checked((int)r.UVar());
            var runs = ImmutableArray.CreateBuilder<TextRun>(rc);
            for (int k = 0; k < rc; k++)
            {
                string text = r.String();
                var flags = (TextStyleFlags)r.Byte();
                float size = r.Single();
                uint color = r.UInt32(), highlight = r.UInt32();
                string font = r.String();
                runs.Add(new TextRun(text, new TextStyle(flags, size, color, highlight, font.Length == 0 ? null : font)));
            }
            paras.Add(new Paragraph { Kind = kind, Indent = indent, Checked = isChecked, TagIds = tags.MoveToImmutable(), Runs = runs.MoveToImmutable() });
        }
        return new TextContainer(id, z, x, y, width, new RichText { Paragraphs = paras.MoveToImmutable() }, measured);
    }
}
