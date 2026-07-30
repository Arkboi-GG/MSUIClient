using System.Numerics;

namespace MSUIClient.Net;

public readonly record struct SpellTargets(ushort Mask, ulong? Unit, Vector3? Destination);
public readonly record struct SpellStartPacket(ulong Caster, uint SpellId, ushort CastFlags,
    uint CastTimeMs, SpellTargets Targets, uint? AmmoDisplayId);
public readonly record struct SpellGoPacket(ulong Caster, uint SpellId, ushort CastFlags,
    ulong[] Hits, (ulong Guid, byte Reason)[] Misses, SpellTargets Targets, uint? AmmoDisplayId);

public static class SpellPacketParser
{
    private const ushort UnitBits = 0x0002 | 0x0800 | 0x0200 | 0x8000;
    private const ushort ItemBits = 0x0010 | 0x1000;

    public static SpellStartPacket ParseStart(byte[] body)
    {
        var r = new PacketReader(body);
        r.ReadPackedGuid();
        ulong caster = r.ReadPackedGuid();
        uint spell = r.ReadU32();
        ushort flags = r.ReadU16();
        uint castMs = r.ReadU32();
        SpellTargets targets = ReadTargets(r);
        uint? ammo = (flags & 0x20) != 0 ? ReadAmmo(r) : null;
        return new SpellStartPacket(caster, spell, flags, castMs, targets, ammo);
    }

    public static SpellGoPacket ParseGo(byte[] body)
    {
        var r = new PacketReader(body);
        r.ReadPackedGuid();
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
        uint? ammo = (flags & 0x20) != 0 ? ReadAmmo(r) : null;
        return new SpellGoPacket(caster, spell, flags, hits, misses, targets, ammo);
    }

    public static (uint SpellId, byte Status, byte Reason) ParseResult(byte[] body)
    {
        var r = new PacketReader(body);
        uint spell = r.ReadU32(); byte status = r.ReadU8();
        return (spell, status, status == 2 && r.HasMore ? r.ReadU8() : (byte)0);
    }

    private static uint ReadAmmo(PacketReader r) { uint display = r.ReadU32(); r.ReadU32(); return display; }

    private static SpellTargets ReadTargets(PacketReader r)
    {
        ushort mask = r.ReadU16(); ulong? unit = null;
        if ((mask & UnitBits) != 0) unit = r.ReadPackedGuid();
        if ((mask & ItemBits) != 0) r.ReadPackedGuid();
        if ((mask & 0x0020) != 0) r.ReadVector3();
        Vector3? destination = (mask & 0x0040) != 0 ? r.ReadVector3() : null;
        if ((mask & 0x2000) != 0) r.ReadCString();
        return new SpellTargets(mask, unit, destination);
    }
}
