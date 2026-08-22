namespace MSUIClient.Net;

public readonly record struct TransferPendingPacket(
    uint MapId, uint? TransportEntry, uint? OldMapId)
{
    public bool RidingTransport => TransportEntry is not null;
}

public readonly record struct LoginTimeSpeedPacket(uint PackedDateTime, float Timescale);

public static class SessionTransferPackets
{
    /// <summary>
    /// SMSG_TRANSFER_PENDING: u32 destination map alone, or exactly two more
    /// u32s (transport template entry and old map) for a transport world seam.
    /// </summary>
    public static TransferPendingPacket ParsePending(byte[] body)
    {
        ArgumentNullException.ThrowIfNull(body);
        var reader = new PacketReader(body);
        uint map = reader.ReadU32();
        if (reader.Remaining == 0) return new(map, null, null);
        if (reader.Remaining != 8)
            throw new InvalidDataException(
                $"SMSG_TRANSFER_PENDING expected 0 or 8 tail bytes, got {reader.Remaining}");
        uint entry = reader.ReadU32();
        uint oldMap = reader.ReadU32();
        return new(map, entry, oldMap);
    }

    /// <summary>SMSG_TRANSFER_ABORTED: one transfer-abort reason byte.</summary>
    public static byte ParseAborted(byte[] body)
    {
        ArgumentNullException.ThrowIfNull(body);
        var reader = new PacketReader(body);
        byte reason = reader.ReadU8();
        if (reader.Remaining != 0)
            throw new InvalidDataException(
                $"SMSG_TRANSFER_ABORTED has {reader.Remaining} trailing byte(s)");
        return reason;
    }


    /// <summary>SMSG_LOGIN_SETTIMESPEED: packed vanilla datetime plus f32 timescale.</summary>
    public static LoginTimeSpeedPacket ParseTimeSpeed(byte[] body)
    {
        ArgumentNullException.ThrowIfNull(body);
        var reader = new PacketReader(body);
        var packet = new LoginTimeSpeedPacket(reader.ReadU32(), reader.ReadF32());
        if (reader.Remaining != 0)
            throw new InvalidDataException(
                $"SMSG_LOGIN_SETTIMESPEED has {reader.Remaining} trailing byte(s)");
        return packet;
    }
}
