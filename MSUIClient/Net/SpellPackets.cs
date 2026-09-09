using System.Numerics;

namespace MSUIClient.Net;

/// <summary>
/// The complete 1.12 spell-target block.  Keeping the source and item/string branches is
/// important even when the renderer does not consume them: the wire decoder must not erase
/// information before presentation/gameplay policy has had a chance to inspect it.
/// </summary>
public readonly record struct SpellTargets(
    ushort Mask,
    ulong? Unit,
    ulong? Item,
    ulong? SourceTransport,
    Vector3? Source,
    Vector3? Destination,
    string? String);

public readonly record struct SpellStartPacket(ulong ItemCaster, ulong Caster, uint SpellId,
    ushort CastFlags, uint CastTimeMs, SpellTargets Targets, uint? AmmoDisplayId,
    uint? AmmoInventoryType);
public readonly record struct SpellGoMiss(ulong Guid, byte Reason, byte? ReflectionReason = null);
public readonly record struct SpellGoPacket(ulong ItemCaster, ulong Caster, uint SpellId,
    ushort CastFlags, ulong[] Hits, SpellGoMiss[] Misses, SpellTargets Targets,
    uint? AmmoDisplayId, uint? AmmoInventoryType);
public readonly record struct SpellDelayedPacket(ulong Caster, uint DelayMs);
public readonly record struct SpellChannelStartPacket(uint SpellId, uint DurationMs);
public readonly record struct SpellChainTargetsPacket(ulong Caster, uint SpellId, ulong[] Targets);

/// <summary>
/// The three compact, self-cast lifecycle packets that drive pushback and channel timing.
/// Keeping their byte shapes here makes the runtime route share the golden-vector-tested decoder.
/// </summary>
public static class SpellLifecyclePacketParser
{
    public static SpellChainTargetsPacket ParseChainTargets(byte[] body)
    {
        var r = new PacketReader(body);
        ulong caster = r.ReadU64();
        uint spell = r.ReadU32();
        uint count = r.ReadU32();
        // The wire count is a u32. Bound it by the bytes actually present before allocating so a
        // malformed packet cannot turn a small body into a giant allocation request.
        if (count > (uint)(r.Remaining / sizeof(ulong)))
            throw new InvalidDataException(
                $"SMSG_SPELL_UPDATE_CHAIN_TARGETS count {count} exceeds {r.Remaining} payload bytes");
        int targetCount = checked((int)count);
        var targets = new ulong[targetCount];
        for (int i = 0; i < targets.Length; i++) targets[i] = r.ReadU64();
        if (r.Remaining != 0)
            throw new InvalidDataException(
                $"SMSG_SPELL_UPDATE_CHAIN_TARGETS has {r.Remaining} trailing byte(s)");
        return new SpellChainTargetsPacket(caster, spell, targets);
    }

    public static SpellDelayedPacket ParseDelayed(byte[] body)
    {
        var r = new PacketReader(body);
        return new SpellDelayedPacket(r.ReadU64(), r.ReadU32());
    }

    public static SpellChannelStartPacket ParseChannelStart(byte[] body)
    {
        if (body.Length != 8) throw new InvalidDataException("Channel start requires spell and duration");
        var r = new PacketReader(body);
        return new SpellChannelStartPacket(r.ReadU32(), r.ReadU32());
    }

    public static uint ParseChannelUpdate(byte[] body)
    {
        if (body.Length != 4) throw new InvalidDataException("Channel update requires remaining duration");
        return new PacketReader(body).ReadU32();
    }
}

public static class SpellPacketParser
{
    private const ushort UnitBits = 0x0002 | 0x0800 | 0x0200 | 0x8000;
    private const ushort ItemBits = 0x0010 | 0x1000;

    public static SpellStartPacket ParseStart(byte[] body)
    {
        var r = new PacketReader(body);
        ulong itemCaster = r.ReadPackedGuid();
        ulong caster = r.ReadPackedGuid();
        uint spell = r.ReadU32();
        ushort flags = r.ReadU16();
        uint castMs = r.ReadU32();
        SpellTargets targets = ReadTargets(r);
        (uint Display, uint Inventory)? ammo = (flags & 0x20) != 0 ? ReadAmmo(r) : null;
        return new SpellStartPacket(itemCaster, caster, spell, flags, castMs, targets,
            ammo?.Display, ammo?.Inventory);
    }

    public static SpellGoPacket ParseGo(byte[] body)
    {
        var r = new PacketReader(body);
        ulong itemCaster = r.ReadPackedGuid();
        ulong caster = r.ReadPackedGuid();
        uint spell = r.ReadU32();
        ushort flags = r.ReadU16();
        var hits = new ulong[r.ReadU8()];
        for (int i = 0; i < hits.Length; i++) hits[i] = r.ReadU64();
        var misses = new SpellGoMiss[r.ReadU8()];
        for (int i = 0; i < misses.Length; i++)
        {
            ulong guid = r.ReadU64(); byte reason = r.ReadU8();
            byte? reflectionReason = reason == 11 ? r.ReadU8() : null;
            misses[i] = new(guid, reason, reflectionReason);
        }
        SpellTargets targets = ReadTargets(r);
        (uint Display, uint Inventory)? ammo = (flags & 0x20) != 0 ? ReadAmmo(r) : null;
        return new SpellGoPacket(itemCaster, caster, spell, flags, hits, misses, targets,
            ammo?.Display, ammo?.Inventory);
    }

    public static SpellCastResultPacket ParseResult(byte[] body) => SpellCastResultPackets.Parse(body);

    private static (uint Display, uint Inventory) ReadAmmo(PacketReader r)
        => (r.ReadU32(), r.ReadU32());

    private static SpellTargets ReadTargets(PacketReader r)
    {
        ushort mask = r.ReadU16(); ulong? unit = null;
        if ((mask & UnitBits) != 0) unit = r.ReadPackedGuid();
        ulong? item = (mask & ItemBits) != 0 ? r.ReadPackedGuid() : null;
        // TARGET_FLAG_SOURCE_LOCATION in 1.12 is three floats only — no transport guid
        // (vmangos SpellCastTargetsInfo.cpp write side; benilla messages/spells.rs:110-112).
        // Reading a packed guid here desyncs the stream and corrupts every self-centred
        // AoE SPELL_GO (Arcane Explosion, Frost Nova).
        ulong? sourceTransport = null;
        Vector3? source = (mask & 0x0020) != 0 ? r.ReadVector3() : null;
        Vector3? destination = (mask & 0x0040) != 0 ? r.ReadVector3() : null;
        string? text = (mask & 0x2000) != 0 ? r.ReadCString() : null;
        return new SpellTargets(mask, unit, item, sourceTransport, source, destination, text);
    }
}
