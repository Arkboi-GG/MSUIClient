namespace MSUIClient.Net;

public readonly record struct ItemReadResult(ulong Guid, bool Success, byte Reason);

public static class ItemReadPackets
{
    public static ItemReadResult Parse(Op opcode, byte[] body)
    {
        bool success = opcode == Op.SMSG_READ_ITEM_OK;
        if (!success && opcode != Op.SMSG_READ_ITEM_FAILED)
            throw new InvalidDataException("unsupported item read result");
        int canonicalLength = success ? 8 : 9;
        // This Core appends the item GUID twice. Also admit the canonical single-GUID
        // packet, but never an unrelated suffix or a conflicting duplicate GUID.
        if (body.Length != canonicalLength && body.Length != canonicalLength + 8)
            throw new InvalidDataException("bad item read result length");
        var r = new PacketReader(body);
        ulong guid = r.ReadU64();
        byte reason = success ? (byte)0 : r.ReadU8();
        if (r.Remaining != 0 && r.ReadU64() != guid)
            throw new InvalidDataException("conflicting item read GUIDs");
        return new(guid, success, reason);
    }
}
