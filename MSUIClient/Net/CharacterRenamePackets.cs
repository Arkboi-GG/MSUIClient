using System.Buffers.Binary;
using System.Text;

namespace MSUIClient.Net;

public readonly record struct CharacterRenameResult(byte Code, ulong Guid, string Name)
{
    public bool Succeeded => Code == 0; // RESPONSE_SUCCESS, not CHAR_CREATE_SUCCESS
}

public static class CharacterRenamePackets
{
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static bool ValidRequestName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Contains('\0') || name.EnumerateRunes().Count() > 12) return false;
        try { _ = Utf8.GetByteCount(name); return true; }
        catch (EncoderFallbackException) { return false; }
    }

    public static byte[] Build(ulong guid, string name)
    {
        if (guid == 0 || !ValidRequestName(name))
            throw new ArgumentException("A character and a nonempty name of at most 12 characters are required.");
        byte[] encoded = Utf8.GetBytes(name);
        byte[] body = new byte[9 + encoded.Length];
        BinaryPrimitives.WriteUInt64LittleEndian(body, guid);
        encoded.CopyTo(body, 8);
        return body;
    }

    public static CharacterRenameResult Parse(ReadOnlySpan<byte> body)
    {
        if (body.IsEmpty) throw new InvalidDataException("empty rename reply");
        if (body[0] != 0)
        {
            if (body.Length != 1) throw new InvalidDataException("rename failure has unexpected fields");
            return new(body[0], 0, "");
        }
        if (body.Length < 11 || body[^1] != 0 || body[9..^1].IndexOf((byte)0) >= 0)
            throw new InvalidDataException("invalid rename success payload");
        ulong guid = BinaryPrimitives.ReadUInt64LittleEndian(body[1..]);
        string name;
        try { name = Utf8.GetString(body[9..^1]); }
        catch (DecoderFallbackException e) { throw new InvalidDataException("invalid rename UTF-8", e); }
        if (guid == 0 || !ValidRequestName(name)) throw new InvalidDataException("invalid rename identity");
        return new(0, guid, name);
    }
}
