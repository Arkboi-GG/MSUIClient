namespace MSUIClient.Net;

/// <summary>
/// One instance the character is saved (bound) to, as one row of
/// SMSG_RAID_INSTANCE_INFO. A permanent raid bind counts down to its scheduled
/// weekly/however reset; the server sends the seconds remaining, not an absolute
/// time, so a live panel just subtracts elapsed frames from <see cref="CapturedAt"/>.
/// </summary>
/// <param name="MapId">Map.dbc id of the bound instance.</param>
/// <param name="SecondsUntilReset">Seconds until the lock resets, at capture time.</param>
/// <param name="InstanceId">The server-side instance id the character is bound to.</param>
/// <param name="CapturedAt">NowSeconds() when this row was received (for the live countdown).</param>
public readonly record struct RaidLockout(
    uint MapId, uint SecondsUntilReset, uint InstanceId, double CapturedAt = 0)
{
    /// <summary>Seconds left now, given a current NowSeconds() clock. Never negative.</summary>
    public long SecondsLeft(double now)
    {
        double left = SecondsUntilReset - (now - CapturedAt);
        return left <= 0 ? 0 : (long)left;
    }
}

/// <summary>
/// A raid-instance system message (SMSG_RAID_INSTANCE_MESSAGE): the periodic
/// "this instance resets in N" warning the server pushes as a reset approaches.
/// </summary>
/// <param name="Type">RAID_INSTANCE_WARNING_* / _WELCOME / _EXPIRED (see the constants).</param>
/// <param name="MapId">Map.dbc id the warning is about.</param>
/// <param name="SecondsUntilReset">Seconds until the reset the warning announces.</param>
public readonly record struct RaidInstanceMessage(uint Type, uint MapId, uint SecondsUntilReset);

/// <summary>
/// Outcome of a CMSG_RESET_INSTANCES request for one map: either SMSG_INSTANCE_RESET
/// (success, <see cref="Failed"/> = false) or SMSG_INSTANCE_RESET_FAILED (with a reason).
/// </summary>
/// <param name="MapId">Map.dbc id the reset targeted.</param>
/// <param name="Failed">True when this came from SMSG_INSTANCE_RESET_FAILED.</param>
/// <param name="Reason">INSTANCE_RESET_* failure reason (0 on success).</param>
public readonly record struct InstanceResetOutcome(uint MapId, bool Failed, uint Reason);

/// <summary>
/// Dungeon/raid lockout wire (spec P1). Parses the instance-save family the
/// vanilla 1.12.1 client uses for its Raid Info window: the bound-instance list
/// with reset timers, the periodic reset warning, and the reset request outcome.
///
/// Both requests in this family — CMSG_REQUEST_RAID_INFO and CMSG_RESET_INSTANCES —
/// carry an empty body, so this class only ever PARSES; there is nothing to build.
///
/// Exact-length parsing by wire law: a body one byte off is a different packet.
/// </summary>
public static class RaidInfoWire
{
    /// <summary>u32 count header, then <c>count</c> fixed rows.</summary>
    public const int InfoHeaderBytes = 4;

    /// <summary>u32 mapId, u32 secondsUntilReset, u32 instanceId.</summary>
    public const int InfoRowBytes = 12;

    /// <summary>u32 type, u32 mapId, u32 secondsUntilReset.</summary>
    public const int MessageBytes = 12;

    /// <summary>SMSG_INSTANCE_RESET body: u32 mapId.</summary>
    public const int ResetBytes = 4;

    /// <summary>SMSG_INSTANCE_RESET_FAILED body: u32 reason, u32 mapId.</summary>
    public const int ResetFailedBytes = 8;

    // RAID_INSTANCE_WARNING_* (Player.h). Kept so the panel can phrase the warning
    // rather than print a raw number.
    public const uint WarningHours = 1;
    public const uint WarningMinutes = 2;
    public const uint WarningMinutesSoon = 3;
    public const uint Welcome = 4;
    public const uint Expired = 5;

    /// <summary>
    /// SMSG_RAID_INSTANCE_INFO: u32 count, then count × { u32 mapId, u32 secondsUntilReset,
    /// u32 instanceId }. Every row is stamped with <paramref name="capturedAt"/> so the
    /// panel can run a live countdown without another round trip.
    /// </summary>
    public static bool TryParseRaidInfo(byte[] body, double capturedAt, out RaidLockout[] lockouts)
    {
        lockouts = [];
        if (body.Length < InfoHeaderBytes) return false;
        var r = new PacketReader(body);
        uint count = r.ReadU32();
        if (body.Length != InfoHeaderBytes + (long)count * InfoRowBytes) return false;

        var rows = new RaidLockout[count];
        for (uint i = 0; i < count; i++)
        {
            uint mapId = r.ReadU32();
            uint seconds = r.ReadU32();
            uint instanceId = r.ReadU32();
            rows[i] = new RaidLockout(mapId, seconds, instanceId, capturedAt);
        }
        lockouts = rows;
        return true;
    }

    /// <summary>SMSG_RAID_INSTANCE_MESSAGE: u32 type, u32 mapId, u32 seconds. Exact 12 bytes.</summary>
    public static bool TryParseInstanceMessage(byte[] body, out RaidInstanceMessage message)
    {
        message = default;
        if (body.Length != MessageBytes) return false;
        var r = new PacketReader(body);
        message = new RaidInstanceMessage(r.ReadU32(), r.ReadU32(), r.ReadU32());
        return true;
    }

    /// <summary>SMSG_INSTANCE_RESET: u32 mapId. Exact 4 bytes.</summary>
    public static bool TryParseInstanceReset(byte[] body, out InstanceResetOutcome outcome)
    {
        outcome = default;
        if (body.Length != ResetBytes) return false;
        outcome = new InstanceResetOutcome(new PacketReader(body).ReadU32(), Failed: false, Reason: 0);
        return true;
    }

    /// <summary>SMSG_INSTANCE_RESET_FAILED: u32 reason, u32 mapId. Exact 8 bytes.</summary>
    public static bool TryParseInstanceResetFailed(byte[] body, out InstanceResetOutcome outcome)
    {
        outcome = default;
        if (body.Length != ResetFailedBytes) return false;
        var r = new PacketReader(body);
        uint reason = r.ReadU32();
        uint mapId = r.ReadU32();
        outcome = new InstanceResetOutcome(mapId, Failed: true, reason);
        return true;
    }
}
