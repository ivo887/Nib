using System;
using System.Buffers.Binary;
using System.Text;

namespace Nib.Core.Serialization;

/// <summary>Growable byte buffer with LEB128 varints and zig-zag signed encoding.</summary>
public sealed class VarintWriter
{
    private byte[] _buf;
    private int _len;

    public VarintWriter(int capacity = 1024) => _buf = new byte[capacity];

    public int Length => _len;
    public ReadOnlySpan<byte> WrittenSpan => _buf.AsSpan(0, _len);
    public byte[] ToArray() => _buf.AsSpan(0, _len).ToArray();
    public void Reset() => _len = 0;

    private void Ensure(int extra)
    {
        if (_len + extra <= _buf.Length) return;
        Array.Resize(ref _buf, Math.Max(_buf.Length * 2, _len + extra));
    }

    public void Byte(byte b) { Ensure(1); _buf[_len++] = b; }

    public void UVar(ulong v)
    {
        Ensure(10);
        while (v >= 0x80) { _buf[_len++] = (byte)(v | 0x80); v >>= 7; }
        _buf[_len++] = (byte)v;
    }

    public void SVar(long v) => UVar((ulong)((v << 1) ^ (v >> 63)));

    public void UInt32(uint v) { Ensure(4); BinaryPrimitives.WriteUInt32LittleEndian(_buf.AsSpan(_len), v); _len += 4; }
    public void Int32(int v) { Ensure(4); BinaryPrimitives.WriteInt32LittleEndian(_buf.AsSpan(_len), v); _len += 4; }
    public void UInt16(ushort v) { Ensure(2); BinaryPrimitives.WriteUInt16LittleEndian(_buf.AsSpan(_len), v); _len += 2; }
    public void Single(float v) { Ensure(4); BinaryPrimitives.WriteSingleLittleEndian(_buf.AsSpan(_len), v); _len += 4; }

    public void String(string? s)
    {
        if (string.IsNullOrEmpty(s)) { UVar(0); return; }
        int n = Encoding.UTF8.GetByteCount(s);
        UVar((ulong)n);
        Ensure(n);
        _len += Encoding.UTF8.GetBytes(s, _buf.AsSpan(_len, n));
    }
}

/// <summary>Reader for <see cref="VarintWriter"/> output. Throws <see cref="FormatException"/> on truncated data.</summary>
public ref struct VarintReader
{
    private readonly ReadOnlySpan<byte> _data;
    private int _pos;

    public VarintReader(ReadOnlySpan<byte> data) { _data = data; _pos = 0; }

    public readonly int Position => _pos;
    public readonly bool End => _pos >= _data.Length;

    public byte Byte()
    {
        if (_pos >= _data.Length) throw new FormatException("Unexpected end of data.");
        return _data[_pos++];
    }

    public ulong UVar()
    {
        ulong result = 0;
        int shift = 0;
        while (true)
        {
            byte b = Byte();
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return result;
            shift += 7;
            if (shift > 63) throw new FormatException("Varint too long.");
        }
    }

    public long SVar()
    {
        ulong v = UVar();
        return (long)(v >> 1) ^ -(long)(v & 1);
    }

    private ReadOnlySpan<byte> Take(int n)
    {
        if (_pos + n > _data.Length) throw new FormatException("Unexpected end of data.");
        var s = _data.Slice(_pos, n);
        _pos += n;
        return s;
    }

    public uint UInt32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
    public int Int32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));
    public ushort UInt16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
    public float Single() => BinaryPrimitives.ReadSingleLittleEndian(Take(4));

    public string String()
    {
        int n = checked((int)UVar());
        return n == 0 ? string.Empty : Encoding.UTF8.GetString(Take(n));
    }
}
