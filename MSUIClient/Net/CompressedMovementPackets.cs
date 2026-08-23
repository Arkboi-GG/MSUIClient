using System.IO.Compression;

namespace MSUIClient.Net;

public readonly record struct CompressedMovementRelay(Op Opcode, MovementRelay Relay);

/// <summary>
/// SMSG_COMPRESSED_MOVES is a zlib envelope of [u8 size][u16 opcode][body] records. Size includes
/// the two-byte opcode. vmangos only batches the ordinary MSG_MOVE_* observer relay family here.
/// </summary>
public static class CompressedMovementPackets
{
    public static IReadOnlyList<CompressedMovementRelay> Parse(byte[] body)
    {
        var outer = new PacketReader(body);
        uint announcedSize = outer.ReadU32();
        using var compressed = new MemoryStream(body, outer.Position, outer.Remaining, writable: false);
        using var zlib = new ZLibStream(compressed, CompressionMode.Decompress);
        using var inflated = new MemoryStream((int)Math.Min(announcedSize, 64 * 1024u));
        zlib.CopyTo(inflated);
        byte[] records = inflated.ToArray();

        var result = new List<CompressedMovementRelay>();
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
            if (!MovementRelayPackets.IsRelayOpcode(opcode))
                throw new InvalidDataException(
                    $"compressed-moves record {opcode} is not a movement relay");

            byte[] recordBody = records.AsSpan(offset, bodyLength).ToArray();
            offset += bodyLength;
            result.Add(new(opcode, MovementRelayPackets.Parse(opcode, recordBody)));
        }
        return result;
    }
}
