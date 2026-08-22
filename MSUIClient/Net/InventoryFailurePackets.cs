namespace MSUIClient.Net;

public readonly record struct InventoryChangeFailurePacket(
    byte Reason, uint? RequiredLevel, ulong ItemGuid, ulong OtherItemGuid, byte BagSlot);

/// <summary>Strict build-5875 SMSG_INVENTORY_CHANGE_FAILURE parsing.</summary>
public static class InventoryFailurePackets
{
    public static InventoryChangeFailurePacket Parse(byte[] body)
    {
        if (body.Length < 1)
            throw new InvalidDataException("SMSG_INVENTORY_CHANGE_FAILURE has no reason byte");

        var reader = new PacketReader(body);
        byte reason = reader.ReadU8();
        if (reason == 0)
        {
            if (reader.Remaining != 0)
                throw new InvalidDataException("SMSG_INVENTORY_CHANGE_FAILURE success body has a tail");
            return new(0, null, 0, 0, 0);
        }

        int expected = reason == 1 ? 21 : 17;
        if (reader.Remaining != expected)
            throw new InvalidDataException(
                $"SMSG_INVENTORY_CHANGE_FAILURE reason {reason} expected {expected} tail bytes, got {reader.Remaining}");

        uint? requiredLevel = reason == 1 ? reader.ReadU32() : null;
        var result = new InventoryChangeFailurePacket(
            reason, requiredLevel, reader.ReadU64(), reader.ReadU64(), reader.ReadU8());
        if (reader.Remaining != 0)
            throw new InvalidDataException("SMSG_INVENTORY_CHANGE_FAILURE has trailing bytes");
        return result;
    }
}
