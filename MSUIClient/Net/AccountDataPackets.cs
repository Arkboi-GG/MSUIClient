using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;

namespace MSUIClient.Net;

public readonly record struct AccountDataPayload(uint Type, byte[] Data);

/// <summary>1.12 account cache transport: eight MD5 digests, never timestamps.</summary>
public static class AccountDataPackets
{
    public const int Count = 8;
    public const int MaximumSize = 0xFFFF;
    public static bool IsGlobal(uint type) => type is 0 or 2 or 4;

    public static byte[][] ParseDigests(ReadOnlySpan<byte> body)
    {
        if (body.Length != Count * 16) throw new InvalidDataException("account digest snapshot must contain eight MD5 values");
        byte[][] values = new byte[Count][];
        for (int i = 0; i < Count; i++) values[i] = body.Slice(i * 16, 16).ToArray();
        return values;
    }

    public static byte[] Request(uint type)
    {
        if (type >= Count) throw new ArgumentOutOfRangeException(nameof(type));
        byte[] body = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(body, type);
        return body;
    }

    public static byte[] Digest(ReadOnlySpan<byte> data) => data.IsEmpty ? new byte[16] : MD5.HashData(data);

    public static AccountDataPayload ParseUpdate(ReadOnlySpan<byte> body)
    {
        if (body.Length < 8) throw new InvalidDataException("truncated account data header");
        uint type = BinaryPrimitives.ReadUInt32LittleEndian(body);
        uint size = BinaryPrimitives.ReadUInt32LittleEndian(body[4..]);
        if (type >= Count || size > MaximumSize) throw new InvalidDataException("account type or inflated size out of range");
        if (size == 0)
        {
            if (body.Length != 8) throw new InvalidDataException("empty account data has trailing payload");
            return new(type, []);
        }
        ReadOnlySpan<byte> compressed = body[8..];
        if (compressed.Length < 6 || (compressed[0] & 15) != 8 || (compressed[0] >> 4) > 7 ||
            ((compressed[0] << 8) | compressed[1]) % 31 != 0 || (compressed[1] & 0x20) != 0)
            throw new InvalidDataException("invalid account zlib header");
        byte[] inflated = new byte[(int)size];
        try
        {
            using var input = new MemoryStream(compressed.ToArray(), writable: false);
            using var zlib = new ZLibStream(input, CompressionMode.Decompress);
            zlib.ReadExactly(inflated);
            if (zlib.ReadByte() != -1) throw new InvalidDataException("account data exceeds declared size");
        }
        catch (EndOfStreamException e) { throw new InvalidDataException("truncated compressed account data", e); }
        // Require the server's zlib trailer even on runtimes that tolerate a truncated stream.
        uint a = 1, b = 0;
        foreach (byte value in inflated) { a = (a + value) % 65521; b = (b + a) % 65521; }
        if (BinaryPrimitives.ReadUInt32BigEndian(compressed[^4..]) != ((b << 16) | a))
            throw new InvalidDataException("account data checksum mismatch");
        return new(type, inflated);
    }
}
