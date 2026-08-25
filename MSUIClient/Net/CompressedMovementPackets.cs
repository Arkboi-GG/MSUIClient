using System.IO.Compression;

namespace MSUIClient.Net;

/// <summary>One record of a compressed-moves batch. Relay is parsed for the MSG_MOVE_*
/// observer family; every other opcode carries its raw body for the dispatcher.</summary>
public readonly record struct CompressedMovementRecord(Op Opcode, MovementRelay? Relay, byte[] Body);

/// <summary>
/// SMSG_COMPRESSED_MOVES is a zlib envelope of [u8 size][u16 opcode][body] records. Size includes
/// the two-byte opcode. vmangos batches the ordinary MSG_MOVE_* observer relay family here, and
/// under mass bot movement also SMSG_MONSTER_MOVE splines and SMSG_SPLINE_* speed records. The
/// records are length-prefixed, so an opcode without a handler is the DISPATCHER's decision to
/// skip — a foreign record must never abort the rest of the batch (the old fatal treatment
/// dropped every spline behind it whenever twenty bots were ordered to move at once).
/// </summary>
public static class CompressedMovementPackets
{
    public static IReadOnlyList<CompressedMovementRecord> Parse(byte[] body)
    {
        var outer = new PacketReader(body);
        uint announcedSize = outer.ReadU32();
        using var compressed = new MemoryStream(body, outer.Position, outer.Remaining, writable: false);
        using var zlib = new ZLibStream(compressed, CompressionMode.Decompress);
        using var inflated = new MemoryStream((int)Math.Min(announcedSize, 64 * 1024u));
        zlib.CopyTo(inflated);
        byte[] records = inflated.ToArray();

        var result = new List<CompressedMovementRecord>();
        int offset = 0;
        while (offset < records.Length)
        {
            int size = records[offset++];
            if (size < 2)
                throw new InvalidDataException($"compressed-moves record size {size} < 2");
            if (records.Length - offset < 2)
                throw new EndOfStreamException("compressed-moves record ended before opcode");
            Op opcode = (Op)(records[offset] | records[offset + 1] << 8);
            offset += 2;
            int bodyLength = size - 2;
            if (records.Length - offset < bodyLength)
                throw new EndOfStreamException(
                    $"compressed-moves record {opcode} wants {bodyLength} byte(s), " +
                    $"{records.Length - offset} left");
            if (opcode == Op.SMSG_COMPRESSED_MOVES)
                throw new InvalidDataException("compressed-moves cannot nest");

            byte[] recordBody = records.AsSpan(offset, bodyLength).ToArray();
            offset += bodyLength;
            MovementRelay? relay = MovementRelayPackets.IsRelayOpcode(opcode)
                ? MovementRelayPackets.Parse(opcode, recordBody)
                : null;
            result.Add(new(opcode, relay, recordBody));
        }
        return result;
    }
}
