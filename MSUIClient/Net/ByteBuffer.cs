using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace MSUIClient.Net;

// Bounds-checked reader/writer over a world-packet body. Every multi-byte
// integer is little-endian on the wire unless the method name says BE. Ported
// from benilla-bytes/src/lib.rs + benilla-protocol/src/wire.rs (packed guid,
// packed spline point) — the exact field encodings the 1.12.1 client uses.

/// <summary>Reader over a decrypted packet body. Throws on underrun (mirrors read_exact).</summary>
public sealed class PacketReader
{
    private readonly byte[] _data;
    private int _pos;
    private readonly int _end;

    public PacketReader(byte[] data) : this(data, 0, data.Length) { }

    public PacketReader(byte[] data, int offset, int length)
    {
        _data = data;
        _pos = offset;
        _end = offset + length;
    }

    public int Position => _pos;
    public int Remaining => _end - _pos;
    public bool HasMore => _pos < _end;

    private ReadOnlySpan<byte> Take(int n)
    {
        if (_pos + n > _end)
            throw new EndOfStreamException($"packet underrun: needed {n} byte(s) at {_pos}, end {_end}");
        var s = _data.AsSpan(_pos, n);
        _pos += n;
        return s;
    }

    public byte ReadU8() => Take(1)[0];
    public sbyte ReadI8() => (sbyte)Take(1)[0];
    public ushort ReadU16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
    public ushort ReadU16BE() => BinaryPrimitives.ReadUInt16BigEndian(Take(2));
    public uint ReadU32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
    public uint ReadU32BE() => BinaryPrimitives.ReadUInt32BigEndian(Take(4));
    public int ReadI32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));
    public ulong ReadU64() => BinaryPrimitives.ReadUInt64LittleEndian(Take(8));
    public float ReadF32() => BinaryPrimitives.ReadSingleLittleEndian(Take(4));
    public byte[] ReadBytes(int n) => Take(n).ToArray();
    public void Skip(int n) => Take(n);

    /// <summary>NUL-terminated string (the NUL is consumed). Invalid UTF-8 is replaced.</summary>
    public string ReadCString()
    {
        int start = _pos;
        while (_pos < _end && _data[_pos] != 0) _pos++;
        string s = Encoding.UTF8.GetString(_data, start, _pos - start);
        if (_pos < _end) _pos++; // consume the NUL
        return s;
    }

    /// <summary>WoW packed GUID: a 1-byte mask, then only the non-zero bytes, low to high.</summary>
    public ulong ReadPackedGuid()
    {
        byte mask = ReadU8();
        ulong guid = 0;
        for (int i = 0; i < 8; i++)
            if ((mask & (1 << i)) != 0)
                guid |= (ulong)ReadU8() << (i * 8);
        return guid;
    }

    /// <summary>Full 8-byte GUID (CMSG_PLAYER_LOGIN, CMSG_ATTACKSWING, etc. use this, not packed).</summary>
    public ulong ReadFullGuid() => ReadU64();

    public Vector3 ReadVector3() => new(ReadF32(), ReadF32(), ReadF32());

    /// <summary>
    /// Decode a SMSG_MONSTER_MOVE packed spline point: signed two's-complement
    /// 11 bits x, 11 bits y, 10 bits z, in quarter-yard units. The vector is the
    /// offset from the spline's DESTINATION back to this waypoint (destination - waypoint),
    /// not an absolute position (vmangos PacketBuilder::WriteLinearPath).
    /// </summary>
    public static Vector3 DecodePackedSplinePoint(int p) => new(
        ((p << 21) >> 21) * 0.25f,
        ((p << 10) >> 21) * 0.25f,
        (p >> 22) * 0.25f);
}

/// <summary>Growable little-endian writer for building CMSG bodies.</summary>
public sealed class PacketWriter
{
    private byte[] _buf;
    private int _len;

    public PacketWriter(int capacity = 64) => _buf = new byte[capacity];

    public int Length => _len;

    private Span<byte> Reserve(int n)
    {
        if (_len + n > _buf.Length)
            Array.Resize(ref _buf, Math.Max(_buf.Length * 2, _len + n));
        var s = _buf.AsSpan(_len, n);
        _len += n;
        return s;
    }

    public void WriteU8(byte v) => Reserve(1)[0] = v;
    public void WriteI8(sbyte v) => Reserve(1)[0] = (byte)v;
    public void WriteU16(ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(Reserve(2), v);
    public void WriteU16BE(ushort v) => BinaryPrimitives.WriteUInt16BigEndian(Reserve(2), v);
    public void WriteU32(uint v) => BinaryPrimitives.WriteUInt32LittleEndian(Reserve(4), v);
    public void WriteI32(int v) => BinaryPrimitives.WriteInt32LittleEndian(Reserve(4), v);
    public void WriteU64(ulong v) => BinaryPrimitives.WriteUInt64LittleEndian(Reserve(8), v);
    public void WriteF32(float v) => BinaryPrimitives.WriteSingleLittleEndian(Reserve(4), v);
    public void WriteBytes(ReadOnlySpan<byte> b) => b.CopyTo(Reserve(b.Length));

    /// <summary>NUL-terminated string.</summary>
    public void WriteCString(string s)
    {
        int n = Encoding.UTF8.GetByteCount(s);
        var span = Reserve(n + 1);
        Encoding.UTF8.GetBytes(s, span);
        span[n] = 0;
    }

    public void WriteFullGuid(ulong guid) => WriteU64(guid);

    /// <summary>Packed GUID (see PacketReader.ReadPackedGuid).</summary>
    public void WritePackedGuid(ulong guid)
    {
        Span<byte> le = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(le, guid);
        byte mask = 0;
        Span<byte> outb = stackalloc byte[8];
        int idx = 0;
        for (int i = 0; i < 8; i++)
            if (le[i] != 0) { mask |= (byte)(1 << i); outb[idx++] = le[i]; }
        WriteU8(mask);
        WriteBytes(outb.Slice(0, idx));
    }

    public void WriteVector3(Vector3 v) { WriteF32(v.X); WriteF32(v.Y); WriteF32(v.Z); }

    public byte[] ToArray() => _buf.AsSpan(0, _len).ToArray();
    public ReadOnlySpan<byte> AsSpan() => _buf.AsSpan(0, _len);
}
