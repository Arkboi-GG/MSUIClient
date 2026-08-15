using System.Numerics;

namespace MSUIClient.Net;

/// <summary>One fixed-order faction row in SMSG_SUI_RTS_STATE.</summary>
public readonly record struct RtsFactionWire(
    long HonorPool,
    int Ore,
    int Skins,
    int Herbs,
    ushort ControlledZones,
    ushort HeroesFielded,
    ushort HeroSlotCap);

/// <summary>A full player GUID and its persisted hero state.</summary>
public readonly record struct RtsHeroWire(
    ulong Guid,
    byte Team,
    byte HeroLevel,
    bool Dead);

public readonly record struct RtsDungeonWire(
    uint MapId,
    byte Controller,
    byte LiveRunFlags);

/// <summary>
/// One complete, validated RTS snapshot. The two faction rows are always ordered
/// Alliance (0), Horde (1), matching the server packet contract.
/// </summary>
public sealed record RtsStateSnapshot(
    byte Mode,
    byte Modules,
    RtsFactionWire[] Factions,
    RtsHeroWire[] Heroes,
    RtsDungeonWire[] Dungeons);

public readonly record struct RtsActionResultWire(
    byte Action,
    byte Result,
    ulong SubjectGuid,
    long PoolAfter);

[Flags]
public enum RtsForceFlags : byte
{
    None = 0,
    Alive = 0x01,
    Busy = 0x02,
    ControlEligibleNow = 0x04,
    SameMapAndInstance = 0x08,
    DeclaredHero = 0x10,
    HeroDead = 0x20,
    InstanceableMap = 0x40,
}

/// <summary>One in-world AiBot belonging to the requesting commander's faction.</summary>
public readonly record struct RtsForceUnitWire(
    ulong Guid,
    uint MapId,
    uint ZoneId,
    Vector3 Position,
    byte Race,
    byte Class,
    byte Level,
    RtsForceFlags Flags)
{
    public bool Alive => (Flags & RtsForceFlags.Alive) != 0;
    public bool Busy => (Flags & RtsForceFlags.Busy) != 0;
    public bool ControlEligibleNow => (Flags & RtsForceFlags.ControlEligibleNow) != 0;
    public bool SameMapAndInstance => (Flags & RtsForceFlags.SameMapAndInstance) != 0;
    public bool DeclaredHero => (Flags & RtsForceFlags.DeclaredHero) != 0;
    public bool HeroDead => (Flags & RtsForceFlags.HeroDead) != 0;
    public bool InstanceableMap => (Flags & RtsForceFlags.InstanceableMap) != 0;
}

/// <summary>One strictly validated page from SMSG_SUI_FORCE_ROSTER.</summary>
public sealed record RtsForceRosterPage(
    uint RequestId,
    uint ZoneId,
    uint NextGuidLow,
    ushort Total,
    RtsForceUnitWire[] Units);

/// <summary>
/// Exact wire law for the tier-2 RTS packet block allocated in R1. Every block
/// has an explicit row stride: known prefixes are parsed, future tail bytes are
/// skipped, undersized rows and trailing packet data are rejected.
/// </summary>
public static class RtsWire
{
    public const byte FactionRowBytes = 26;
    public const byte HeroRowBytes = 12;
    public const byte DungeonRowBytes = 7;
    public const int FactionCount = 2;
    public const int ActionResultBytes = 18;
    public const byte ForceRowBytes = 32;
    public const byte MaximumForcePageSize = 200;
    public const int ForceRequestBytes = 14;

    public static RtsStateSnapshot ParseState(byte[] body)
    {
        ArgumentNullException.ThrowIfNull(body);
        var r = new PacketReader(body);
        byte mode = r.ReadU8();
        byte modules = r.ReadU8();
        if (mode > 1)
            throw new InvalidDataException($"SMSG_SUI_RTS_STATE has invalid mode {mode}");

        byte factionStride = r.ReadU8();
        RequireStride("faction", factionStride, FactionRowBytes);
        var factions = new RtsFactionWire[FactionCount];
        for (int team = 0; team < factions.Length; team++)
        {
            factions[team] = new RtsFactionWire(
                unchecked((long)r.ReadU64()),
                r.ReadI32(),
                r.ReadI32(),
                r.ReadI32(),
                r.ReadU16(),
                r.ReadU16(),
                r.ReadU16());
            r.Skip(factionStride - FactionRowBytes);
        }

        byte heroCount = r.ReadU8();
        byte heroStride = r.ReadU8();
        RequireStride("hero", heroStride, HeroRowBytes);
        var heroes = new RtsHeroWire[heroCount];
        for (int i = 0; i < heroes.Length; i++)
        {
            ulong guid = r.ReadU64();
            byte team = r.ReadU8();
            byte level = r.ReadU8();
            byte deadValue = r.ReadU8();
            r.Skip(1); // R1/R2 reserved byte
            r.Skip(heroStride - HeroRowBytes);
            if (guid == 0)
                throw new InvalidDataException("RTS hero row has a zero player GUID");
            if (team > 1)
                throw new InvalidDataException($"RTS hero row has invalid team {team}");
            if (level is < 1 or > 5)
                throw new InvalidDataException($"RTS hero row has invalid level {level}");
            if (deadValue > 1)
                throw new InvalidDataException($"RTS hero row has invalid dead value {deadValue}");
            heroes[i] = new RtsHeroWire(guid, team, level, deadValue != 0);
        }

        byte dungeonCount = r.ReadU8();
        byte dungeonStride = r.ReadU8();
        RequireStride("dungeon", dungeonStride, DungeonRowBytes);
        var dungeons = new RtsDungeonWire[dungeonCount];
        for (int i = 0; i < dungeons.Length; i++)
        {
            uint mapId = r.ReadU32();
            byte controller = r.ReadU8();
            byte liveRunFlags = r.ReadU8();
            r.Skip(1); // R1/R2 reserved byte
            r.Skip(dungeonStride - DungeonRowBytes);
            dungeons[i] = new RtsDungeonWire(mapId, controller, liveRunFlags);
        }

        RequireFullyConsumed(r, "SMSG_SUI_RTS_STATE");
        return new RtsStateSnapshot(mode, modules, factions, heroes, dungeons);
    }

    public static RtsActionResultWire ParseActionResult(byte[] body)
    {
        ArgumentNullException.ThrowIfNull(body);
        var r = new PacketReader(body);
        var result = new RtsActionResultWire(
            r.ReadU8(),
            r.ReadU8(),
            r.ReadU64(),
            unchecked((long)r.ReadU64()));
        RequireFullyConsumed(r, "SMSG_SUI_RTS_ACTION_RESULT");
        return result;
    }

    public static byte[] BuildActionBody(byte action, ulong subjectGuid)
    {
        if (action is < 1 or > 3)
            throw new ArgumentOutOfRangeException(nameof(action),
                "RTS hero actions are 1=declare, 2=upgrade, 3=revive.");
        if (subjectGuid == 0)
            throw new ArgumentOutOfRangeException(nameof(subjectGuid),
                "An RTS hero action requires a full player GUID.");
        var w = new PacketWriter(9);
        w.WriteU8(action);
        w.WriteU64(subjectGuid);
        return w.ToArray();
    }

    /// <summary>
    /// CMSG_SUI_FORCE_ROSTER (842): reserved flags, correlation id, exact zone
    /// (zero means all), exclusive GUID-low cursor, and a server-clamped page size.
    /// </summary>
    public static byte[] BuildForceRosterRequestBody(
        uint requestId, uint zoneId, uint afterGuidLow, byte limit = MaximumForcePageSize)
    {
        if (requestId == 0)
            throw new ArgumentOutOfRangeException(nameof(requestId),
                "An RTS force-roster request requires a non-zero correlation id.");
        if (limit > MaximumForcePageSize)
            throw new ArgumentOutOfRangeException(nameof(limit),
                $"An RTS force-roster page cannot exceed {MaximumForcePageSize} rows.");

        var w = new PacketWriter(ForceRequestBytes);
        w.WriteU8(0); // flags, reserved
        w.WriteU32(requestId);
        w.WriteU32(zoneId);
        w.WriteU32(afterGuidLow);
        w.WriteU8(limit); // zero deliberately retains the server's "default 200" law
        return w.ToArray();
    }

    /// <summary>
    /// SMSG_SUI_FORCE_ROSTER (843). Rows must be strictly increasing by player
    /// GUID low. A non-zero next cursor is exactly the last emitted GUID low.
    /// </summary>
    public static RtsForceRosterPage ParseForceRoster(byte[] body)
    {
        ArgumentNullException.ThrowIfNull(body);
        var r = new PacketReader(body);
        uint requestId = r.ReadU32();
        uint zoneId = r.ReadU32();
        uint nextGuidLow = r.ReadU32();
        ushort total = r.ReadU16();
        byte count = r.ReadU8();
        byte stride = r.ReadU8();
        if (requestId == 0)
            throw new InvalidDataException("SMSG_SUI_FORCE_ROSTER has a zero request id");
        if (count > MaximumForcePageSize)
            throw new InvalidDataException(
                $"SMSG_SUI_FORCE_ROSTER count {count} exceeds {MaximumForcePageSize}");
        RequireStride("force", stride, ForceRowBytes);

        var units = new RtsForceUnitWire[count];
        uint previousGuidLow = 0;
        for (int i = 0; i < units.Length; i++)
        {
            ulong guid = r.ReadU64();
            uint mapId = r.ReadU32();
            uint unitZoneId = r.ReadU32();
            Vector3 position = r.ReadVector3();
            byte race = r.ReadU8();
            byte @class = r.ReadU8();
            byte level = r.ReadU8();
            var flags = (RtsForceFlags)r.ReadU8();
            r.Skip(stride - ForceRowBytes);

            uint guidLow = unchecked((uint)guid);
            if (guid == 0 || guidLow == 0)
                throw new InvalidDataException("force roster contains a zero player GUID");
            if (i > 0 && guidLow <= previousGuidLow)
                throw new InvalidDataException("force roster rows are not strictly GUID-sorted");
            if (zoneId != 0 && unitZoneId != zoneId)
                throw new InvalidDataException(
                    $"force roster zone {zoneId} contains row for zone {unitZoneId}");
            if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) ||
                !float.IsFinite(position.Z))
                throw new InvalidDataException("force roster contains a non-finite position");

            previousGuidLow = guidLow;
            units[i] = new RtsForceUnitWire(
                guid, mapId, unitZoneId, position, race, @class, level, flags);
        }

        if (nextGuidLow != 0 && (units.Length == 0 || nextGuidLow != previousGuidLow))
            throw new InvalidDataException(
                "force roster next cursor is not the last emitted GUID low");
        RequireFullyConsumed(r, "SMSG_SUI_FORCE_ROSTER");
        return new RtsForceRosterPage(requestId, zoneId, nextGuidLow, total, units);
    }

    private static void RequireStride(string block, byte actual, byte minimum)
    {
        if (actual < minimum)
            throw new InvalidDataException(
                $"{block} row stride {actual} is smaller than {minimum}");
    }

    private static void RequireFullyConsumed(PacketReader reader, string packet)
    {
        if (reader.Remaining != 0)
            throw new InvalidDataException(
                $"{packet} has {reader.Remaining} unexpected trailing byte(s)");
    }
}
