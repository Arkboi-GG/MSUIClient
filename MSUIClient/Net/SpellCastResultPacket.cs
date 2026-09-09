namespace MSUIClient.Net;

public sealed record SpellCastFailureContext(uint? RequiredArea = null, uint? RequiredFocus = null,
    int? ItemClass = null, uint SubclassMask = 0, uint InventoryMask = 0,
    bool? PermanentCooldown = null);

public readonly record struct SpellCastResultPacket(uint SpellId, byte Status, byte Reason,
    SpellCastFailureContext? Context);

public static class SpellCastResultPackets
{
    public static SpellCastResultPacket Parse(byte[] body)
    {
        var r = new PacketReader(body);
        uint spell = r.ReadU32(); byte status = r.ReadU8();
        if (status == 0)
        {
            if (r.Remaining != 0) throw new InvalidDataException("cast success has trailing bytes");
            return new(spell, status, 0, null);
        }
        if (status != 2) throw new InvalidDataException("unknown cast result status");
        byte reason = r.ReadU8();
        SpellCastFailureContext? context = reason switch
        {
            0x5D => new(RequiredArea: r.ReadU32()),
            0x5E => new(RequiredFocus: r.ReadU32()),
            0x19 or 0x1A or 0x1B => new(ItemClass: unchecked((int)r.ReadU32()),
                SubclassMask: r.ReadU32(), InventoryMask: r.ReadU32()),
            0x3C when r.Remaining == 4 => new(PermanentCooldown: ReadPermanent(r)),
            _ => null,
        };
        // Passive spells substitute DONT_REPORT but append the original failure's
        // payload. Its type cannot be recovered from this packet; do not display it.
        if (reason == 0x17 && r.Remaining is 4 or 12)
            while (r.Remaining > 0) r.ReadU32();
        if (r.Remaining != 0) throw new InvalidDataException("unexpected cast failure context");
        return new(spell, status, reason, context);
    }

    private static bool ReadPermanent(PacketReader r) => r.ReadU32() switch
    {
        0 => false, 1 => true,
        _ => throw new InvalidDataException("invalid permanent cooldown flag"),
    };
}
