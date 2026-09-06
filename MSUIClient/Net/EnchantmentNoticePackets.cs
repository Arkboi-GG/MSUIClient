namespace MSUIClient.Net;

public sealed record CombatEnchantment(ulong Owner, ulong Caster, uint ItemEntry,
    uint EnchantmentId, byte Affiliation) : CombatEvent;

public static class EnchantmentNoticePackets
{
    public static CombatEnchantment Parse(byte[] body)
    {
        // Player::BuildEnchantmentLog uses full GUIDs. Its misleading spellId
        // parameter is populated by Item with a SpellItemEnchantment ID.
        if(body.Length != 25) throw new InvalidDataException("bad enchantment notice length");
        var r = new PacketReader(body);
        return new(r.ReadU64(),r.ReadU64(),r.ReadU32(),r.ReadU32(),r.ReadU8());
    }
}
