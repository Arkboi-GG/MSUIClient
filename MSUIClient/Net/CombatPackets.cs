namespace MSUIClient.Net;

// Build-5875 combat wire. Layouts are ported from the current benilla
// benilla-protocol messages/{attack,combat_log,progression}.rs and its golden
// byte tests. Keep packet decoding here; UI and animation consume typed events
// and must never reinterpret packet bodies independently.

public abstract record CombatEvent;

public sealed record CombatAttackStarted(ulong Attacker, ulong Victim) : CombatEvent;
public sealed record CombatAttackStopped(ulong Attacker, ulong Victim, bool VictimDied) : CombatEvent;

public sealed record CombatMeleeSwing(
    ulong Attacker,
    ulong Victim,
    uint HitInfo,
    uint Damage,
    uint VictimState,
    uint Absorb,
    int Resist,
    uint Blocked) : CombatEvent;

public sealed record CombatSpellDamage(
    ulong Attacker,
    ulong Target,
    uint SpellId,
    uint Damage,
    byte School,
    uint Absorb,
    int Resist,
    bool Periodic,
    uint Blocked,
    uint HitInfo) : CombatEvent;

public enum CombatPeriodicKind
{
    Damage,
    Heal,
    Energize,
    ManaLeech,
}

public readonly record struct CombatPeriodicTick(
    CombatPeriodicKind Kind,
    uint Amount,
    uint SchoolOrPower = 0,
    uint Absorb = 0,
    int Resist = 0,
    float Multiplier = 0f);

public sealed record CombatPeriodicAura(
    ulong Caster,
    ulong Target,
    uint SpellId,
    IReadOnlyList<CombatPeriodicTick> Ticks) : CombatEvent;

public sealed record CombatHeal(
    ulong Healer,
    ulong Target,
    uint SpellId,
    uint Amount,
    bool Critical) : CombatEvent;

public sealed record CombatEnergize(
    ulong Caster,
    ulong Target,
    uint SpellId,
    uint PowerType,
    uint Amount) : CombatEvent;

/// <summary>The shield bearer is Victim; Attacker is the unit receiving reflected damage.</summary>
public sealed record CombatDamageShield(
    ulong Victim,
    ulong Attacker,
    uint Damage,
    uint School) : CombatEvent;

public sealed record CombatEnvironmentalDamage(
    ulong Victim,
    byte DamageType,
    uint Damage,
    uint Absorb,
    int Resist) : CombatEvent;

public readonly record struct CombatMiss(ulong Target, byte MissInfo);

public sealed record CombatSpellMiss(
    ulong Caster,
    uint SpellId,
    IReadOnlyList<CombatMiss> Misses) : CombatEvent;

public sealed record CombatXpGain(
    ulong Victim,
    uint Total,
    uint Base,
    bool Kill) : CombatEvent;

public static class CombatPacketParser
{
    private const uint AuraPeriodicDamage = 3;
    private const uint AuraPeriodicHeal = 8;
    private const uint AuraObservedHealth = 20;
    private const uint AuraObservedMana = 21;
    private const uint AuraPeriodicEnergize = 24;
    private const uint AuraPeriodicManaLeech = 64;
    private const uint AuraPeriodicDamagePercent = 89;

    public static CombatEvent Parse(Op opcode, byte[] body)
    {
        var r = new PacketReader(body);
        CombatEvent result = opcode switch
        {
            Op.SMSG_ATTACKSTART => ReadAttackStart(r),
            Op.SMSG_ATTACKSTOP => ReadAttackStop(r),
            Op.SMSG_ATTACKERSTATEUPDATE => ReadMeleeSwing(r),
            Op.SMSG_SPELLNONMELEEDAMAGELOG => ReadSpellDamage(r),
            Op.SMSG_PERIODICAURALOG => ReadPeriodicAura(r),
            Op.SMSG_SPELLHEALLOG => ReadHeal(r),
            Op.SMSG_SPELLENERGIZELOG => ReadEnergize(r),
            Op.SMSG_SPELLDAMAGESHIELD => ReadDamageShield(r),
            Op.SMSG_ENVIRONMENTALDAMAGELOG => ReadEnvironmentalDamage(r),
            Op.SMSG_SPELLLOGMISS => ReadSpellMiss(r),
            Op.SMSG_LOG_XPGAIN => ReadXpGain(r),
            _ => throw new ArgumentOutOfRangeException(nameof(opcode), opcode, "not a combat packet"),
        };
        if (r.Remaining != 0)
            throw new InvalidDataException($"{opcode}: {r.Remaining} trailing byte(s)");
        return result;
    }

    private static CombatAttackStarted ReadAttackStart(PacketReader r)
        // Current benilla golden test: both GUIDs are raw/full.
        => new(r.ReadU64(), r.ReadU64());

    private static CombatAttackStopped ReadAttackStop(PacketReader r)
    {
        ulong attacker = r.ReadPackedGuid();
        ulong victim = r.ReadPackedGuid();
        return new CombatAttackStopped(attacker, victim, r.ReadU32() != 0);
    }

    private static CombatMeleeSwing ReadMeleeSwing(PacketReader r)
    {
        uint hitInfo = r.ReadU32();
        ulong attacker = r.ReadPackedGuid();
        ulong victim = r.ReadPackedGuid();
        uint damage = r.ReadU32();
        byte subCount = r.ReadU8();
        uint absorb = 0;
        int resist = 0;
        for (int i = 0; i < subCount; i++)
        {
            r.ReadU32(); // school
            r.ReadF32(); // damage as float
            r.ReadU32(); // damage as integer; TotalDamage above is authoritative
            absorb = unchecked(absorb + r.ReadU32());
            resist = unchecked(resist + r.ReadI32());
        }
        uint victimState = r.ReadU32();
        r.ReadU32(); // unused zero
        r.ReadU32(); // spell id (e.g. heroic strike); not surfaced by Benilla's AttackerState
        uint blocked = r.ReadU32();
        return new CombatMeleeSwing(attacker, victim, hitInfo, damage, victimState, absorb, resist, blocked);
    }

    private static CombatSpellDamage ReadSpellDamage(PacketReader r)
    {
        ulong target = r.ReadPackedGuid();
        ulong attacker = r.ReadPackedGuid();
        uint spellId = r.ReadU32();
        uint damage = r.ReadU32();
        byte school = r.ReadU8();
        uint absorb = r.ReadU32();
        int resist = r.ReadI32();
        bool periodic = r.ReadU8() != 0;
        r.ReadU8(); // unused
        uint blocked = r.ReadU32();
        uint hitInfo = r.ReadU32();
        r.ReadU8(); // extended data flag (always zero in 5875)
        return new CombatSpellDamage(attacker, target, spellId, damage, school, absorb, resist, periodic, blocked, hitInfo);
    }

    private static CombatPeriodicAura ReadPeriodicAura(PacketReader r)
    {
        ulong target = r.ReadPackedGuid();
        ulong caster = r.ReadPackedGuid();
        uint spellId = r.ReadU32();
        uint count = r.ReadU32();
        if (count > 1024) throw new InvalidDataException($"SMSG_PERIODICAURALOG: implausible tick count {count}");
        var ticks = new CombatPeriodicTick[count];
        for (int i = 0; i < ticks.Length; i++)
        {
            uint auraType = r.ReadU32();
            ticks[i] = auraType switch
            {
                AuraPeriodicDamage or AuraPeriodicDamagePercent => new(
                    CombatPeriodicKind.Damage,
                    r.ReadU32(), r.ReadU32(), r.ReadU32(), r.ReadI32()),
                AuraPeriodicHeal or AuraObservedHealth => new(
                    CombatPeriodicKind.Heal, r.ReadU32()),
                AuraObservedMana or AuraPeriodicEnergize => new(
                    CombatPeriodicKind.Energize, r.ReadU32(), r.ReadU32()),
                AuraPeriodicManaLeech => new(
                    CombatPeriodicKind.ManaLeech, r.ReadU32(), r.ReadU32(), Multiplier: r.ReadF32()),
                _ => throw new InvalidDataException($"SMSG_PERIODICAURALOG: unknown aura type {auraType}"),
            };
        }
        return new CombatPeriodicAura(caster, target, spellId, ticks);
    }

    private static CombatHeal ReadHeal(PacketReader r)
    {
        ulong target = r.ReadPackedGuid();
        ulong healer = r.ReadPackedGuid();
        return new CombatHeal(healer, target, r.ReadU32(), r.ReadU32(), r.ReadU8() != 0);
    }

    private static CombatEnergize ReadEnergize(PacketReader r)
    {
        ulong target = r.ReadPackedGuid();
        ulong caster = r.ReadPackedGuid();
        return new CombatEnergize(caster, target, r.ReadU32(), r.ReadU32(), r.ReadU32());
    }

    private static CombatDamageShield ReadDamageShield(PacketReader r)
        => new(r.ReadU64(), r.ReadU64(), r.ReadU32(), r.ReadU32());

    private static CombatEnvironmentalDamage ReadEnvironmentalDamage(PacketReader r)
        => new(r.ReadU64(), r.ReadU8(), r.ReadU32(), r.ReadU32(), r.ReadI32());

    private static CombatSpellMiss ReadSpellMiss(PacketReader r)
    {
        uint spellId = r.ReadU32();
        ulong caster = r.ReadU64();
        bool extended = r.ReadU8() != 0;
        uint count = r.ReadU32();
        if (count > 1024) throw new InvalidDataException($"SMSG_SPELLLOGMISS: implausible miss count {count}");
        var misses = new CombatMiss[count];
        for (int i = 0; i < misses.Length; i++)
        {
            misses[i] = new CombatMiss(r.ReadU64(), r.ReadU8());
            if (extended) { r.ReadF32(); r.ReadF32(); }
        }
        return new CombatSpellMiss(caster, spellId, misses);
    }

    private static CombatXpGain ReadXpGain(PacketReader r)
    {
        ulong victim = r.ReadU64();
        uint total = r.ReadU32();
        bool kill = r.ReadU8() == 0;
        uint baseXp = total;
        if (kill)
        {
            baseXp = r.ReadU32();
            r.ReadF32(); // group bonus; retained later when party UI exists
        }
        return new CombatXpGain(victim, total, baseXp, kill);
    }
}

/// <summary>Build-5875 attack refusal opcodes are payload-free; the opcode is the law.</summary>
public static class CombatAttackErrorText
{
    public static string ForOpcode(Op opcode) => opcode switch
    {
        Op.SMSG_ATTACKSWING_NOTINRANGE => "You are too far away!",
        Op.SMSG_ATTACKSWING_BADFACING => "You are facing the wrong way!",
        Op.SMSG_ATTACKSWING_NOTSTANDING => "You must be standing to attack!",
        Op.SMSG_ATTACKSWING_DEADTARGET => "Your target is dead!",
        Op.SMSG_ATTACKSWING_CANT_ATTACK => "You can't attack that target!",
        _ => throw new ArgumentOutOfRangeException(nameof(opcode), opcode,
            "not an attack-error opcode"),
    };
}
