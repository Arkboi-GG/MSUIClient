namespace MSUIClient.Net;

/// <summary>Strict build-5875 decoders for the server cooldown follow-up family.</summary>
public static class CooldownPackets
{
    public readonly record struct ItemCooldown(ulong ItemGuid, uint SpellId);
    public readonly record struct AddressedSpell(uint SpellId, ulong CasterGuid);

    public static ItemCooldown ParseItem(byte[] body)
    {
        RequireLength(body, 12, "SMSG_ITEM_COOLDOWN");
        var r = new PacketReader(body);
        return new ItemCooldown(r.ReadU64(), r.ReadU32());
    }

    public static AddressedSpell ParseAddressedSpell(byte[] body, string packet)
    {
        RequireLength(body, 12, packet);
        var r = new PacketReader(body);
        return new AddressedSpell(r.ReadU32(), r.ReadU64());
    }

    public static ulong ParseCheat(byte[] body)
    {
        RequireLength(body, 8, "SMSG_COOLDOWN_CHEAT");
        return new PacketReader(body).ReadU64();
    }

    private static void RequireLength(byte[] body, int expected, string packet)
    {
        if (body.Length != expected)
            throw new InvalidDataException($"{packet} body is {body.Length} bytes, expected {expected}");
    }
}
