using System.Numerics;
using MSUIClient.World.Encounters;

namespace MSUIClient.Engine.UI;

/// <summary>Shared encounter vocabulary. Unsupported mechanics fail validation instead of being ignored.</summary>
public static class CommanderEncounterLaw
{
    public static readonly string[] Actions = ["avoidCones", "avoidPoints", "isolate", "spread", "stack", "move", "stopDamage", "cast"];
    public static readonly string[] Triggers = ["always", "castStart", "castGo", "bossAura", "selfAura", "memberAura", "bossNear"];
    public static Vector3 Point(float[] p) => new(p[0], p[1], p[2]);
    public static bool Inside(CommanderEncounterDefinition d, Vector3 p) =>
        float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z) &&
        p.X >= d.Bounds.Min[0] && p.X <= d.Bounds.Max[0] && p.Y >= d.Bounds.Min[1] &&
        p.Y <= d.Bounds.Max[1] && p.Z >= d.Bounds.Min[2] && p.Z <= d.Bounds.Max[2];
    public static IReadOnlyList<string> Validate(CommanderEncounterDefinition d)
    {
        var errors = new List<string>();
        if (!CommanderRaidTuningLaw.Valid(d.Tuning)) errors.Add("Unknown or out-of-range tuning field.");
        bool PointOk(float[]? p) => p is { Length: 3 } && p.All(float.IsFinite);
        if (d.Schema is not (1 or 2) || string.IsNullOrWhiteSpace(d.Id) || d.Id.Length > 64 ||
            !d.Id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_') ||
            string.IsNullOrWhiteSpace(d.Name) || d.Name.Length > 96 || d.BossEntry == 0)
            errors.Add("Invalid encounter identity or schema.");
        if (d.Objectives is null || d.ObjectiveEntries.Length > 8 || d.ObjectiveEntries.Any(id => id == 0) ||
            d.ObjectiveEntries.Distinct().Count() != d.ObjectiveEntries.Length || !d.ObjectiveEntries.Contains(d.BossEntry) ||
            d.ImmuneSchools > 127 || d.Coverage is not ("authored" or "basic")) errors.Add("Invalid objectives, immunity mask or coverage.");
        if (d.Bounds is null || !PointOk(d.Bounds.Min) || !PointOk(d.Bounds.Max) ||
            Enumerable.Range(0, 3).Any(i => d.Bounds.Max[i] <= d.Bounds.Min[i] || d.Bounds.Max[i] - d.Bounds.Min[i] > 400))
        { errors.Add("Encounter needs finite room bounds, at most 400 yards per axis."); return errors; }
        if (!PointOk(d.TankAnchor) || !Inside(d, Point(d.TankAnchor))) errors.Add("Tank anchor is outside the room.");
        if (d.Teams is null || d.Teams.Length is < 1 or > 8 || d.Teams.Any(t => t is null ||
            string.IsNullOrWhiteSpace(t.Name) || t.Name.Length > 24 || !PointOk(t.Anchor) || !Inside(d, Point(t.Anchor))))
            errors.Add("Define one to eight named teams with valid anchors.");
        if (d.AddTanksPerTeam is < 0 or > 4 || d.HealersPerTeam is < 0 or > 8 || d.HealerCount is < 0 or > 40)
            errors.Add("Invalid role coverage requirements.");
        if (d.AddEntries is null || d.AddEntries.Length > 32 || d.AddEntries.Any(e => e == 0)) errors.Add("Invalid add entries.");
        if (d.AddPolicy is not ("split" or "focus" or "balance" or "hold") || d.RequiredAdds is null || d.RequiredAdds.Length > 32 ||
            d.RequiredAdds.Any(r => r is null || r.Count is < 0 or > 64 || d.AddEntries is null || !d.AddEntries.Contains(r.Entry)) ||
            d.RequiredAdds.Select(r => r.Entry).Distinct().Count() != d.RequiredAdds.Length)
            errors.Add("Invalid required add counts or damage policy.");
        if (d.Phases is null || d.Phases.Length is < 1 or > 16 || d.Phases.Any(p => p is null || p.Id == 0 ||
            string.IsNullOrWhiteSpace(p.Name) || p.Name.Length > 64 || !float.IsFinite(p.HealthMin) || !float.IsFinite(p.HealthMax) ||
            p.HealthMin < 0 || p.HealthMax > 100 || p.HealthMin >= p.HealthMax || p.Airborne is < -1 or > 1 || p.Priority is < -10000 or > 10000 ||
            !float.IsFinite(p.ThreatRatio) || p.ThreatRatio is < 0 or > 1) || d.Phases.Select(p => p.Id).Distinct().Count() != d.Phases.Length)
        { errors.Add("Invalid or duplicate phase definitions."); return errors; }
        if (!d.Phases.Any(p => p.HealthMin == 0 && p.HealthMax == 100 && p.Airborne == -1 && p.Aura == 0 && p.ElapsedMinMs == 0 && p.ElapsedMaxMs == 0 && p.MinLivingAdds == -1 && p.MaxLivingAdds == -1 && !p.EndWhenAddsDead))
            errors.Add("Define an unconditional fallback phase.");
        foreach (var p in d.Phases)
            if (p.ElapsedMinMs > 3600000 || p.ElapsedMaxMs > 3600000 || p.PeriodMs > 3600000 ||
                p.ElapsedMaxMs != 0 && p.ElapsedMaxMs <= p.ElapsedMinMs ||
                p.PeriodMs != 0 && (p.ElapsedMinMs >= p.PeriodMs || p.ElapsedMaxMs > p.PeriodMs) ||
                p.MinLivingAdds is < -1 or > 256 || p.MaxLivingAdds is < -1 or > 256 ||
                p.MinLivingAdds >= 0 && p.MaxLivingAdds >= 0 && p.MinLivingAdds > p.MaxLivingAdds)
                errors.Add("Invalid timed or counted-add phase.");
        var m = d.Mechanics;
        bool Ids(uint[]? ids, int maximum) => ids is not null && ids.Length <= maximum && ids.All(id => id > 0 && id <= int.MaxValue);
        bool Roles(uint roles) => roles != 0 && (roles & ~62u) == 0;
        if (m is null || m.Completion is not ("death" or "friendlySurrender") || m.CompletionStableMs > 10000 || m.ThreatSettleMs > 10000 ||
            m.CrowdControl is null || m.CrowdControl.Length > 32 || m.Hazards is null || m.Hazards.Length > 32 ||
            m.Waves is null || m.Waves.Length > 8 || m.AddDistances is null || m.AddDistances.Length > 32 || m.DamagePolicies is null || m.DamagePolicies.Length > 32 || m.Interactions is null || m.Interactions.Length > 32)
            errors.Add("Invalid mechanic primitive configuration.");
        else
        {
            foreach (var c in m.CrowdControl)
                if (c is null || !Ids(c.Entries,32) || c.Entries.Length == 0 || !Ids(c.Spells,32) || c.Spells.Length == 0 || !Roles(c.Roles) || c.MaxTargets is < 1 or > 32 || c.Phase != 0 && !d.Phases.Any(p => p.Id == c.Phase)) errors.Add("Invalid crowd-control policy.");
            foreach (var h in m.Hazards)
                if (h is null || !Ids(h.Entries,32) || !Ids(h.Spells,64) || h.Objects is null || h.Objects.Length > 32 || h.Entries.Length + h.Spells.Length + h.Objects.Length == 0 || !float.IsFinite(h.Radius) || h.Radius is < .1f or > 100 || h.DurationMs is 0 or > 3600000 || h.Aura > int.MaxValue || !Roles(h.Roles) || h.Priority is < -10000 or > 10000) errors.Add("Invalid observed-hazard policy.");
            foreach (var h in m.Hazards.Where(h => h is not null && h.Objects is not null && h.Entries is not null && h.Spells is not null))
                if (h.Objects.Any(o => o is null || o.Entry is 0 or > int.MaxValue || o.CreationSpell is 0 or > int.MaxValue) ||
                    h.Objects.Distinct().Count() != h.Objects.Length ||
                    h.Objects.Length > 0 && (h.Entries.Length > 0 || h.Spells.Length > 0 || h.FollowSource || h.OnDeath || h.Aura != 0))
                    errors.Add("Invalid summoned-object hazard source.");
            foreach (var p in m.DamagePolicies)
                if (p is null || !Ids(p.Entries,32) || !Ids(p.Auras,64) || p.Schools is 0 or > 127 || !Roles(p.Roles) || !p.Always && p.Auras.Length == 0) errors.Add("Invalid target damage policy.");
            foreach (var p in m.AddDistances)
                if (p is null || !Ids(p.Entries,32) || p.Entries.Length == 0 || p.Entries.Distinct().Count() != p.Entries.Length ||
                    !Ids(p.References,32) || p.References.Length == 0 || p.References.Distinct().Count() != p.References.Length ||
                    !float.IsFinite(p.Minimum) || !float.IsFinite(p.Maximum) || p.Minimum is < 0 or > 400 || p.Maximum is < 0 or > 400 ||
                    p.Minimum == 0 && p.Maximum == 0 || p.Maximum > 0 && p.Minimum >= p.Maximum || p.ReferenceAura > int.MaxValue ||
                    p.Entries.Any(e => !(d.AddEntries ?? []).Contains(e)) || p.References.Any(e => !(d.AddEntries ?? []).Contains(e) && !(d.Objectives is not null && d.ObjectiveEntries.Contains(e))))
                    errors.Add("Invalid add-distance policy.");
            foreach (var p in m.Waves)
                if (p is null || p.Entry is 0 or > int.MaxValue || p.Count is 0 or > 64 || p.Aura is 0 or > int.MaxValue ||
                    p.ActiveMs > 3600000 || p.RecoveryMs > 3600000 || !(d.AddEntries ?? []).Contains(p.Entry))
                    errors.Add("Invalid observed-wave policy.");
            if (m.Waves.Where(p => p is not null).Select(p => (p.Entry,p.Aura)).Distinct().Count() != m.Waves.Length)
                errors.Add("Duplicate observed-wave policy.");
            foreach (var p in m.Interactions)
                if (p is null || string.IsNullOrWhiteSpace(p.Id) || p.Id.Length > 64 || p.Entry == 0 || p.Entry > int.MaxValue || p.RequiredItem > int.MaxValue || !float.IsFinite(p.Range) || p.Range is < .1f or > 5) errors.Add("Invalid gameobject interaction policy.");
            if (m.Interactions.Where(p => p is not null).Select(p => p.Id).Distinct().Count() != m.Interactions.Length) errors.Add("Duplicate interaction policy.");
        }
        if (d.Rules is null || d.Rules.Length > 64) { errors.Add("At most 64 mechanic rules are supported."); return errors; }
        if (d.Rules.Any(r => r is null)) { errors.Add("Null mechanic rule."); return errors; }
        if (d.Rules.Select(r => r.Id).Distinct().Count() != d.Rules.Length) errors.Add("Duplicate mechanic rule id.");
        foreach (var r in d.Rules)
        {
            if (string.IsNullOrWhiteSpace(r.Id) || r.Id.Length > 64 || r.Priority is < -10000 or > 10000 || !Actions.Contains(r.Action) || !Triggers.Contains(r.Trigger) ||
                !new[] { "self", "boss", "tank", "marked", "tankHealer" }.Contains(r.Target) || r.Roles == 0 || (r.Roles & ~62u) != 0 ||
                r.Phase != 0 && !d.Phases.Any(p => p.Id == r.Phase) || !float.IsFinite(r.Radius) || r.Radius is < 0 or > 100 ||
                r.DurationMs > 60000 || !float.IsFinite(r.TriggerRadius) || r.TriggerRadius is < 0 or > 100 || r.Toggle is not (0 or 1 or 2 or 4) || r.Spells is null || r.Spells.Length > 64 ||
                r.PointSpells is null || r.PointSpells.Length > 64 || !PointOk(r.Station))
            { errors.Add($"Invalid mechanic: {r.Id}."); continue; }
            if (r.Trigger is not ("always" or "bossNear") && (r.Spells.Length == 0 || r.Spells.Any(s => s == 0))) errors.Add($"{r.Id}: trigger needs spell identities.");
            if (r.Trigger is "castStart" or "castGo" && r.DurationMs == 0) errors.Add($"{r.Id}: cast trigger needs a duration.");
            if (r.Trigger == "bossNear" && (r.Action != "avoidPoints" || r.TriggerRadius < 1)) errors.Add($"{r.Id}: boss proximity requires an authored footprint and a positive trigger radius.");
            if (r.Target == "tankHealer" && (r.Action != "cast" || r.Trigger != "always")) errors.Add($"{r.Id}: tank healer is an always support target.");
            if (r.ReserveCasters is < 0 or > 8 || r.ReserveCasters > 0 &&
                (r.Action != "cast" || r.Trigger != "always" || r.Target is "self" or "marked"))
                errors.Add($"{r.Id}: reserve one to eight casters only for always support of a shared target.");
            if (r.Action == "isolate" && (r.Trigger is not ("castStart" or "castGo" or "memberAura") || r.Target != "marked" || r.Radius < 1 || r.MissingAura && r.Spell == 0)) errors.Add($"{r.Id}: isolation needs an observed marked cast or member aura, positive radius and an identified optional impact aura.");
            if (r.Trigger == "memberAura" && (r.Action is not ("spread" or "isolate") || r.Target != "marked" || r.Radius < 1)) errors.Add($"{r.Id}: member aura requires marked spread or isolation with a positive radius.");
            if (r.Action == "avoidCones" && (r.Spells.Length == 0 || r.Spells.Any(id => id == 0) || r.Trigger != "always" || r.Target != "boss" || r.Radius is < 1 or > 10))
                errors.Add($"{r.Id}: cone avoidance needs boss spell identities, an always trigger and one-to-ten-yard clearance.");
            if (r.Action == "avoidPoints" && (r.PointSpells.Length == 0 || r.Radius < 1)) errors.Add($"{r.Id}: avoidance needs spell points and clearance.");
            if (r.Action == "cast" && r.Spell == 0) errors.Add($"{r.Id}: select a spell to cast.");
            if (r.Action == "move" && !Inside(d, Point(r.Station))) errors.Add($"{r.Id}: station outside room.");
            if (r.Target == "marked" && r.Trigger is not ("castStart" or "castGo" or "memberAura")) errors.Add($"{r.Id}: marked target needs a cast event or member aura.");
        }
        return errors;
    }
    public static CommanderEncounterPhase SelectPhase(CommanderEncounterDefinition d, float health, bool airborne, IReadOnlySet<uint> auras) =>
        d.Phases.Where(p => health >= p.HealthMin && health <= p.HealthMax &&
            (p.Airborne == -1 || (p.Airborne == 1) == airborne) && (p.Aura == 0 || auras.Contains(p.Aura)))
            .OrderByDescending(p => p.Priority).ThenBy(p => p.Id).First();
}
