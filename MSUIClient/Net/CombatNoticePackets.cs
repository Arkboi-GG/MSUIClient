namespace MSUIClient.Net;

public sealed record CombatDispel(ulong Caster, ulong Target, bool Failed, uint[] Spells) : CombatEvent;
public sealed record CombatInstantKill(ulong Target, uint SpellId) : CombatEvent;
public sealed record CombatPartyKill(ulong Killer, ulong Target) : CombatEvent;

/// <summary>Exact producers in the inspected Core SpellEffects.cpp and Unit.cpp.</summary>
public static class CombatNoticePackets
{
    public static CombatEvent Parse(Op opcode, byte[] body)
    {
        var r = new PacketReader(body);
        CombatEvent result;
        if (opcode == Op.SMSG_SPELLDISPELLOG)
        {
            ulong target = r.ReadPackedGuid(), caster = r.ReadPackedGuid();
            uint count = r.ReadU32();
            if (count > r.Remaining / 4) throw new InvalidDataException("Dispel count exceeds payload");
            var spells = new uint[count];
            for (int i = 0; i < spells.Length; i++) spells[i] = r.ReadU32();
            result = new CombatDispel(caster, target, false, spells);
        }
        else if (opcode == Op.SMSG_DISPEL_FAILED)
        {
            ulong caster = r.ReadU64(), target = r.ReadU64();
            if (r.Remaining % 4 != 0) throw new InvalidDataException("Dispel failure has an incomplete spell ID");
            var spells = new uint[r.Remaining / 4];
            for (int i = 0; i < spells.Length; i++) spells[i] = r.ReadU32();
            result = new CombatDispel(caster, target, true, spells);
        }
        else if (opcode == Op.SMSG_SPELLINSTAKILLLOG) result = new CombatInstantKill(r.ReadU64(), r.ReadU32());
        else if (opcode == Op.SMSG_PARTYKILLLOG) result = new CombatPartyKill(r.ReadU64(), r.ReadU64());
        else throw new ArgumentOutOfRangeException(nameof(opcode));
        if (r.Remaining != 0) throw new InvalidDataException($"{opcode}: trailing bytes");
        return result;
    }
}
