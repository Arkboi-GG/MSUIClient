namespace MSUIClient.Net;

public readonly record struct ShowTaxiNodesPacket(
    uint Gate, ulong FlightMasterGuid, uint NearestNode, uint[] KnownMask);
public readonly record struct TaxiNodeStatusPacket(ulong FlightMasterGuid, bool Known);

public static class TaxiPackets
{
    public const int MaskWords = 8;

    public static ShowTaxiNodesPacket ParseShowNodes(ReadOnlySpan<byte> body)
    {
        var reader = new PacketReader(body.ToArray());
        uint gate = reader.ReadU32();
        if (gate == 0)
        {
            RequireConsumed(reader, nameof(ShowTaxiNodesPacket));
            return new(0, 0, 0, new uint[MaskWords]);
        }

        ulong guid = reader.ReadU64();
        uint nearest = reader.ReadU32();
        var mask = new uint[MaskWords];
        for (int i = 0; i < mask.Length; i++) mask[i] = reader.ReadU32();
        RequireConsumed(reader, nameof(ShowTaxiNodesPacket));
        return new(gate, guid, nearest, mask);
    }

    public static void RequireNewPathBody(ReadOnlySpan<byte> body)
    {
        if (!body.IsEmpty)
            throw new InvalidDataException($"SMSG_NEW_TAXI_PATH has {body.Length} unexpected byte(s)");
    }

    public static TaxiNodeStatusPacket ParseNodeStatus(ReadOnlySpan<byte> body)
    {
        var reader = new PacketReader(body.ToArray());
        ulong guid = reader.ReadU64();
        bool known = reader.ReadU8() != 0;
        RequireConsumed(reader, nameof(TaxiNodeStatusPacket));
        return new(guid, known);
    }

    public static uint ParseActivateReply(ReadOnlySpan<byte> body)
    {
        var reader = new PacketReader(body.ToArray());
        uint code = reader.ReadU32();
        RequireConsumed(reader, "SMSG_ACTIVATETAXIREPLY");
        return code;
    }

    public static byte[] BuildActivateExpressBody(
        ulong flightMasterGuid, uint totalCost, IReadOnlyList<uint> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        var writer = new PacketWriter(16 + nodes.Count * 4);
        writer.WriteU64(flightMasterGuid);
        writer.WriteU32(totalCost);
        writer.WriteU32((uint)nodes.Count);
        foreach (uint node in nodes) writer.WriteU32(node);
        return writer.ToArray();
    }

    private static void RequireConsumed(PacketReader reader, string packet)
    {
        if (reader.Remaining != 0)
            throw new InvalidDataException($"{packet} has {reader.Remaining} trailing byte(s)");
    }
}
