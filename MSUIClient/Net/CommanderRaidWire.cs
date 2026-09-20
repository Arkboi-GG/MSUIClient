using System.Text;
using System.Numerics;
using MSUIClient.World.Encounters;

namespace MSUIClient.Net;

public enum CommanderRaidOperation : byte { Inspect, Apply, Arm, Pause, Clear, Interact }
public sealed record CommanderRaidStatus(uint RequestId, uint Revision, byte Result, byte State,
    byte Phase, ulong BossGuid, IReadOnlyList<CommanderRaidActorStatus> Actors)
{
    public uint BossHealth { get; init; }
    public uint BossMaxHealth { get; init; }
    public byte BossFlags { get; init; }
    public bool BossKnown => (BossFlags & 1) != 0;
    public bool BossAlive => (BossFlags & 2) != 0;
    public bool BossInCombat => (BossFlags & 4) != 0;
    public bool BossFriendly => (BossFlags & 8) != 0;
    public bool BossNonAttackable => (BossFlags & 16) != 0;
}
public sealed record CommanderRaidActorStatus(ulong Guid, byte Duty, ulong TargetGuid)
{
    public byte Flags { get; init; }
    public bool Known => (Flags & 1) != 0;
    public bool Alive => (Flags & 2) != 0;
    public bool InCombat => (Flags & 4) != 0;
    public CommanderRaidGuidance Guidance { get; init; } = new(0, 0, 0, default);
}
public sealed record CommanderRaidGuidance(byte State, ushort RuleIndex, uint ImpactMs, Vector3 Waypoint);

/// <summary>Capability 13; exact-length binary records. No brain bridge, credentials, or SQL.</summary>
/// <remarks>
/// Version 4 is what the deployed Core parses (SuiCommanderRaid.cpp: <c>packet.version != 4</c> rejects, and its
/// definition parser rejects any word outside schema 1). The client plans on the richer schema-2 definition but
/// applies its schema-1 projection (<see cref="CommanderEncounterWire"/>). The Interact operation belongs to the
/// staged protocol-5 candidate and is never sent to a version-4 Core.
/// </remarks>
public static class CommanderRaidWire
{
    public const uint Capability = 1u << 13;
    public const byte Version = 4;
    public const int HeaderBytes = 24;
    public const int RowBytes = 71;
    public static byte[] Build(uint request, uint revision, CommanderRaidOperation operation, CommanderRaidPlan plan)
    {
        if (!Enum.IsDefined(operation) || operation == CommanderRaidOperation.Interact) throw new ArgumentOutOfRangeException(nameof(operation));
        IReadOnlyList<CommanderRaidAssignment> rows = operation == CommanderRaidOperation.Apply ? plan.Assignments : [];
        if (rows.Count > 40) throw new ArgumentOutOfRangeException(nameof(plan));
        byte[] definition = operation == CommanderRaidOperation.Apply ? CommanderEncounterWire.Schema1Utf8(plan.Encounter) : [];
        if (definition.Length > 32768) throw new ArgumentOutOfRangeException(nameof(plan));
        var w = new PacketWriter(HeaderBytes + rows.Count * RowBytes + definition.Length);
        w.WriteU8(Version); w.WriteU32(request); w.WriteU32(revision); w.WriteU8((byte)operation);
        w.WriteU32(plan.BossEntry);
        w.WriteU8((byte)((plan.AvoidBreath ? 1 : 0) | (plan.SpreadFireballs ? 2 : 0) | (plan.MaintainFearWard ? 4 : 0)));
        w.WriteU8((byte)rows.Count);
        w.WriteU32(plan.MapId); w.WriteU32((uint)definition.Length);
        foreach (var a in rows)
        {
            w.WriteU64(a.Guid); w.WriteU8((byte)a.Role); w.WriteU8((byte)a.Team); w.WriteU8(a.Manual ? (byte)1 : (byte)0);
            w.WriteF32(a.Ground.X); w.WriteF32(a.Ground.Y); w.WriteF32(a.Ground.Z);
            w.WriteF32(a.Air.X); w.WriteF32(a.Air.Y); w.WriteF32(a.Air.Z);
            w.WriteU32(a.HealSpell); w.WriteU32(a.DamageSpell);
            w.WriteU64(a.HealPrimary); w.WriteU32(a.FocusEntry);
            w.WriteU32(a.InterruptSpell); w.WriteU32(a.DispelSpell); w.WriteU32(a.TauntSpell); w.WriteU32(a.DefensiveSpell);
        }
        foreach (byte value in definition) w.WriteU8(value);
        return w.ToArray();
    }
    // QA supplies a verified ten-pull acceptance count; a packet alone is not evidence.
    public static byte[] BuildInteraction(uint request, uint revision, CommanderRaidPlan plan, ulong objectGuid, uint policyIndex, uint measuredSuccesses)
    {
        if (objectGuid == 0 || policyIndex >= plan.Encounter.Mechanics.Interactions.Length || measuredSuccesses is < 8 or > 10)
            throw new ArgumentOutOfRangeException(nameof(objectGuid));
        var w = new PacketWriter(HeaderBytes + RowBytes);
        w.WriteU8(Version); w.WriteU32(request); w.WriteU32(revision); w.WriteU8((byte)CommanderRaidOperation.Interact);
        w.WriteU32(plan.BossEntry); w.WriteU8(0); w.WriteU8(1); w.WriteU32(plan.MapId); w.WriteU32(0);
        w.WriteU64(objectGuid); w.WriteU8(0); w.WriteU8(0); w.WriteU8(0);
        for (int i = 0; i < 6; i++) w.WriteF32(0);
        w.WriteU32(0); w.WriteU32(measuredSuccesses); w.WriteU64(0); w.WriteU32(policyIndex);
        for (int i = 0; i < 4; i++) w.WriteU32(0);
        return w.ToArray();
    }
    public static bool TryParse(byte[] bytes, out CommanderRaidStatus? status)
    {
        status = null;
        if (bytes.Length < 30) return false;
        var r = new PacketReader(bytes);
        if (r.ReadU8() != Version) return false;
        uint request = r.ReadU32(), revision = r.ReadU32();
        byte result = r.ReadU8(), state = r.ReadU8(), phase = r.ReadU8();
        ulong boss = r.ReadU64(); byte count = r.ReadU8();
        if (result > 10 || state > 4 || count > 40 || r.Remaining != 9 + count * 37) return false;
        uint health = r.ReadU32(), maxHealth = r.ReadU32(); byte bossFlags = r.ReadU8();
        if (bossFlags > 31 || health > maxHealth || (bossFlags & 6) != 0 && (bossFlags & 1) == 0 ||
            (bossFlags & 2) != 0 && (health == 0 || maxHealth == 0) ||
            (bossFlags & 1) == 0 && (health != 0 || maxHealth != 0)) return false;
        var rows = new List<CommanderRaidActorStatus>();
        var seen = new HashSet<ulong>();
        for (int i = 0; i < count; i++)
        {
            ulong guid = r.ReadU64(); byte duty = r.ReadU8(); ulong target = r.ReadU64();
            if (guid == 0 || !seen.Add(guid) || duty > 16) return false;
            byte flags = r.ReadU8();
            if (flags > 7 || (flags & 6) != 0 && (flags & 1) == 0) return false;
            byte guidance = r.ReadU8(); ushort ruleIndex = r.ReadU16(); uint impact = r.ReadU32();
            var point = new Vector3(r.ReadF32(), r.ReadF32(), r.ReadF32());
            if (guidance > 4 || (ruleIndex >= 96 && !(ruleIndex == ushort.MaxValue && guidance == 1 && impact == 0)) || impact > 60000 ||
                !float.IsFinite(point.X) || !float.IsFinite(point.Y) || !float.IsFinite(point.Z)) return false;
            rows.Add(new(guid, duty, target) { Flags = flags, Guidance = new(guidance, ruleIndex, impact, point) });
        }
        status = new(request, revision, result, state, phase, boss, rows) { BossHealth = health, BossMaxHealth = maxHealth, BossFlags = bossFlags };
        return true;
    }
    public static string ResultText(byte result) => result switch
    {
        0 => "Accepted", 1 => "Malformed plan", 2 => "Only the raid commander may apply this plan",
        3 => "Enter the selected encounter with your group", 4 => "A member cannot be commanded",
        5 => "A selected spell is not learned or cannot fulfill its duty", 6 => "Plan changed; refresh and retry",
        7 => "Finish Tactical Freeze before changing the plan", 8 => "Apply a valid plan first",
        9 => "Assignments do not cover required teams and the main tank", 10 => "Cannot replace an active plan; pause first",
        _ => "Unknown response",
    };
    public static string DutyText(byte duty) => duty switch
    {
        0 => "Waiting", 1 => "Moving to station", 2 => "Tanking", 3 => "Collecting adds",
        4 => "Healing", 5 => "Attacking", 6 => "Avoiding hazard", 7 => "Spreading",
        8 => "Waiting for tank threat", 9 => "Under manual control", 10 => "Unavailable",
        11 => "Holding for pull", 12 => "No safe route", 13 => "Interrupting", 14 => "Dispelling", 15 => "Waiting for mana", 16 => "Waiting on heal", _ => "Unknown",
    };
}
