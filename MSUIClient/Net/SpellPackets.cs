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
public readonly record struct SpellGoPacket(ulong ItemCaster, ulong Caster, uint SpellId,
    ushort CastFlags, ulong[] Hits, (ulong Guid, byte Reason)[] Misses, SpellTargets Targets,
    uint? AmmoDisplayId, uint? AmmoInventoryType);

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
        var misses = new (ulong, byte)[r.ReadU8()];
        for (int i = 0; i < misses.Length; i++)
        {
            ulong guid = r.ReadU64(); byte reason = r.ReadU8();
            if (reason == 11) r.ReadU8();
            misses[i] = (guid, reason);
        }
        SpellTargets targets = ReadTargets(r);
        (uint Display, uint Inventory)? ammo = (flags & 0x20) != 0 ? ReadAmmo(r) : null;
        return new SpellGoPacket(itemCaster, caster, spell, flags, hits, misses, targets,
            ammo?.Display, ammo?.Inventory);
    }

    public static (uint SpellId, byte Status, byte Reason) ParseResult(byte[] body)
    {
        var r = new PacketReader(body);
        uint spell = r.ReadU32(); byte status = r.ReadU8();
        return (spell, status, status == 2 && r.HasMore ? r.ReadU8() : (byte)0);
    }

    private static (uint Display, uint Inventory) ReadAmmo(PacketReader r)
        => (r.ReadU32(), r.ReadU32());

    private static SpellTargets ReadTargets(PacketReader r)
    {
        ushort mask = r.ReadU16(); ulong? unit = null;
        if ((mask & UnitBits) != 0) unit = r.ReadPackedGuid();
        ulong? item = (mask & ItemBits) != 0 ? r.ReadPackedGuid() : null;
        ulong? sourceTransport = null;
        Vector3? source = null;
        if ((mask & 0x0020) != 0)
        {
            sourceTransport = r.ReadPackedGuid();
            source = r.ReadVector3();
        }
        Vector3? destination = (mask & 0x0040) != 0 ? r.ReadVector3() : null;
        string? text = (mask & 0x2000) != 0 ? r.ReadCString() : null;
        return new SpellTargets(mask, unit, item, sourceTransport, source, destination, text);
    }
}
