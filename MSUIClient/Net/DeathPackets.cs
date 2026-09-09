using System.Numerics;
using System.Text;

namespace MSUIClient.Net;

public readonly record struct CorpseLocation(
    bool Found, int DisplayMap, Vector3 Position, uint CorpseMap);

public readonly record struct ResurrectRequestPacket(
    ulong Caster, string Name, bool Sickness, bool HasTimer);

/// <summary>Strict build-5875 death/corpse packet parsing.</summary>
public static class DeathPackets
{
    public static CorpseLocation ParseCorpseQuery(byte[] body)
    {
        if (body.Length is not (1 or 21))
            throw new InvalidDataException($"MSG_CORPSE_QUERY expected 1 or 21 bytes, got {body.Length}");
        var r = new PacketReader(body);
        bool found = r.ReadU8() != 0;
        if (!found)
        {
            if (body.Length != 1)
                throw new InvalidDataException("MSG_CORPSE_QUERY not-found body has a tail");
            return new(false, 0, Vector3.Zero, 0);
        }
        if (body.Length != 21)
            throw new InvalidDataException("MSG_CORPSE_QUERY found body is truncated");
        var result = new CorpseLocation(true, r.ReadI32(), r.ReadVector3(), r.ReadU32());
        RequireConsumed(r, nameof(ParseCorpseQuery));
        return result;
    }

    public static byte ParsePlayerSkinned(byte[] body)
    {
        if (body.Length != 1) throw new InvalidDataException("Player skinned requires one byte");
        return body[0]; // Current Core writes zero; do not invent another meaning for its flag.
    }

    public static uint ParseReclaimDelay(byte[] body)
    {
        if (body.Length != 4)
            throw new InvalidDataException($"SMSG_CORPSE_RECLAIM_DELAY expected 4 bytes, got {body.Length}");
        return new PacketReader(body).ReadU32();
    }

    public static ResurrectRequestPacket ParseResurrectRequest(byte[] body)
    {
        if (body.Length < 15)
            throw new InvalidDataException($"SMSG_RESURRECT_REQUEST too short: {body.Length}");
        var r = new PacketReader(body);
        ulong caster = r.ReadU64();
        uint nameLength = r.ReadU32();
        if (nameLength == 0 || nameLength > int.MaxValue || r.Remaining != nameLength + 2)
            throw new InvalidDataException(
                $"SMSG_RESURRECT_REQUEST invalid name length {nameLength} for {r.Remaining} bytes");
        byte[] encoded = r.ReadBytes((int)nameLength);
        if (encoded[^1] != 0 || encoded[..^1].Contains((byte)0))
            throw new InvalidDataException("SMSG_RESURRECT_REQUEST name is not one canonical cstring");
        string name = Encoding.UTF8.GetString(encoded, 0, encoded.Length - 1);
        bool sickness = r.ReadU8() != 0;
        bool hasTimer = r.ReadU8() != 0;
        RequireConsumed(r, nameof(ParseResurrectRequest));
        return new(caster, name, sickness, hasTimer);
    }

    public static ulong ParseSpiritHealerConfirm(byte[] body)
    {
        if (body.Length != 8)
            throw new InvalidDataException($"SMSG_SPIRIT_HEALER_CONFIRM expected 8 bytes, got {body.Length}");
        return new PacketReader(body).ReadU64();
    }

    private static void RequireConsumed(PacketReader reader, string packet)
    {
        if (reader.Remaining != 0)
            throw new InvalidDataException($"{packet} has {reader.Remaining} trailing bytes");
    }
}
