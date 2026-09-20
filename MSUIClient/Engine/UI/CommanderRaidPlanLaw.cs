using System.Numerics;
using MSUIClient.World.Encounters;

namespace MSUIClient.Engine.UI;

public static class CommanderRaidPlanLaw
{
    public static string RoleName(CommanderRaidRole role) => role switch
    {
        CommanderRaidRole.MainTank => "Main tank", CommanderRaidRole.AddTank => "Add tank",
        CommanderRaidRole.Healer => "Healer", CommanderRaidRole.Melee => "Melee",
        CommanderRaidRole.Ranged => "Ranged", _ => "Unassigned",
    };

    public static CommanderRaidPlan AutoAssign(IReadOnlyList<CommanderRaidMember> roster,
        CommanderEncounterDefinition? encounter = null, ulong mainGuid = 0,
        CommanderRaidRole mainRole = CommanderRaidRole.Unassigned)
    {
        encounter ??= CommanderEncounterCatalog.Default;
        var plan = CommanderRaidPlan.ForEncounter(encounter) with { MainGuid = mainGuid, MainRole = mainRole };
        var main = roster.FirstOrDefault(m => m.Guid == mainGuid);
        if (main is null || mainRole == CommanderRaidRole.Unassigned) return plan;
        var rows = new List<CommanderRaidAssignment>();
        var remaining = roster.Where(m => m.Guid != mainGuid && m.Commandable && m.Alive && m.FactsReady)
            .OrderBy(m => m.Guid).ToDictionary(m => m.Guid);
        int TeamLoad(int team) => rows.Count(a => (int)a.Team == team);
        int LeastTeam() => Enumerable.Range(0, encounter.Teams.Length).OrderBy(TeamLoad).ThenBy(t => t).First();
        void Assign(CommanderRaidMember member, CommanderRaidRole role, int team, bool manual = false)
        {
            uint focus = role == CommanderRaidRole.MainTank
                ? encounter.ObjectiveEntries[Math.Min(rows.Count(a => a.Role == role), encounter.ObjectiveEntries.Length - 1)]
                : role == CommanderRaidRole.AddTank ? encounter.AddEntries.FirstOrDefault() : encounter.BossEntry;
            rows.Add(new(member.Guid, member.Name, role, (CommanderRaidTeam)team, default, default,
                member.PreferredHeal, member.PreferredDamage, manual, 0, focus,
                member.InterruptSpell, member.DispelSpell, member.TauntSpell, member.DefensiveSpell));
            remaining.Remove(member.Guid);
        }
        CommanderRaidMember? Tank() => remaining.Values.Where(m => m.CanTank || m.ClassId == 1)
            .OrderByDescending(CommanderRaidCapabilityLaw.TankScore).ThenBy(m => m.Guid).FirstOrDefault();
        CommanderRaidMember? Healer() => remaining.Values.Where(m => m.PreferredHeal != 0 && m.Spells.Contains(m.PreferredHeal))
            .OrderByDescending(CommanderRaidCapabilityLaw.HealingScore).ThenBy(m => m.Guid).FirstOrDefault();
        Assign(main, mainRole, 0, true);
        while (rows.Count(a => a.Role == CommanderRaidRole.MainTank) < encounter.ObjectiveEntries.Length && Tank() is { } tank)
            Assign(tank, CommanderRaidRole.MainTank, LeastTeam());
        for (int team = 0; team < encounter.Teams.Length; team++)
            while (rows.Count(a => (int)a.Team == team && a.Role == CommanderRaidRole.AddTank) < encounter.AddTanksPerTeam && Tank() is { } addTank)
                Assign(addTank, CommanderRaidRole.AddTank, team);
        for (int team = 0; team < encounter.Teams.Length; team++)
            while (rows.Count(a => (int)a.Team == team && a.Role == CommanderRaidRole.Healer) < encounter.HealersPerTeam && Healer() is { } healer)
                Assign(healer, CommanderRaidRole.Healer, team);
        while (rows.Count(a => a.Role == CommanderRaidRole.Healer) < encounter.HealerCount && Healer() is { } healer)
        {
            int team = Enumerable.Range(0, encounter.Teams.Length)
                .OrderBy(t => rows.Count(a => (int)a.Team == t && a.Role == CommanderRaidRole.Healer)).ThenBy(TeamLoad).ThenBy(t => t).First();
            Assign(healer, CommanderRaidRole.Healer, team);
        }
        foreach (var member in remaining.Values.OrderBy(m => m.Guid).ToArray())
        {
            var role = member.ClassId is 1 or 4 or 2 ? CommanderRaidRole.Melee
                : member.PreferredDamage != 0 && member.Spells.Contains(member.PreferredDamage) ? CommanderRaidRole.Ranged
                : member.PreferredHeal != 0 ? CommanderRaidRole.Healer : CommanderRaidRole.Unassigned;
            Assign(member, role, LeastTeam());
        }
        return AssignHealing(Layout(plan with { Assignments = rows }));
    }

    public static CommanderRaidPlan AssignHealing(CommanderRaidPlan plan)
    {
        var rows = plan.Assignments.ToArray();
        var tanks = rows.Where(a => a.Role is CommanderRaidRole.MainTank or CommanderRaidRole.AddTank).ToArray();
        var load = new Dictionary<ulong, int>();
        for (int i = 0; i < rows.Length; i++)
        {
            if (rows[i].Role != CommanderRaidRole.Healer) { rows[i] = rows[i] with { HealPrimary = 0 }; continue; }
            var healer = rows[i];
            var primary = tanks.Where(t => t.Role == CommanderRaidRole.MainTank || t.Team == healer.Team)
                .OrderBy(t => load.GetValueOrDefault(t.Guid) / (t.Role == CommanderRaidRole.MainTank ? 2.0 : 1.0))
                .ThenBy(t => t.Role).ThenBy(t => t.Guid).FirstOrDefault();
            ulong guid = primary?.Guid ?? healer.Guid;
            rows[i] = healer with { HealPrimary = guid };
            load[guid] = load.GetValueOrDefault(guid) + 1;
        }
        return plan with { Assignments = rows };
    }

    public static CommanderRaidPlan Layout(CommanderRaidPlan plan)
    {
        var rows = new List<CommanderRaidAssignment>();
        foreach (CommanderRaidAssignment row in plan.Assignments)
        {
            CommanderRaidAssignment[] peers = plan.Assignments.Where(a => a.Team == row.Team && a.Role == row.Role)
                .OrderBy(a => a.Guid).ToArray();
            int slot = Array.FindIndex(peers, a => a.Guid == row.Guid);
            var definition = plan.Encounter;
            Vector3 anchor = CommanderEncounterLaw.Point(definition.Teams[(int)row.Team].Anchor);
            Vector3 low = CommanderEncounterLaw.Point(definition.Bounds.Min), high = CommanderEncounterLaw.Point(definition.Bounds.Max);
            float middleY = (low.Y + high.Y) * .5f;
            float sign = anchor.Y >= middleY ? 1 : -1;
            float offset = row.Role == CommanderRaidRole.Ranged ? 8 : row.Role == CommanderRaidRole.AddTank ? -6 : 0;
            // Healers need overlapping patient coverage; damage roles retain the wider grid.
            float spacing = row.Role == CommanderRaidRole.Healer ? 3 : 5;
            Vector3 ground = row.Role == CommanderRaidRole.MainTank ? CommanderEncounterLaw.Point(definition.TankAnchor)
                : Vector3.Clamp(anchor + new Vector3(-(slot % 5) * spacing - (row.Role == CommanderRaidRole.AddTank ? 5 : 0), sign * (offset + slot / 5 * 4), 0), low, high);
            var team = plan.Assignments.Where(a => a.Team == row.Team).OrderBy(a => a.Guid).ToArray();
            int airSlot = Array.FindIndex(team, a => a.Guid == row.Guid);
            Vector3 air = Vector3.Clamp(anchor + new Vector3(-airSlot % 5 * 8, sign * (-6 + airSlot / 5 * 7), 0), low, high);
            // Explicit phase movement data can reserve a tank intercept station.
            // Other roles retain their existing formation grid.
            var alternateStation = definition.Rules.Where(r => r.Action == "move" && r.Trigger == "always" &&
                (r.Roles & (1u << (int)row.Role)) != 0 && definition.Phases.Any(p => p.Id == r.Phase && p.Alternate))
                .OrderByDescending(r => r.Priority).FirstOrDefault();
            if (alternateStation is not null) air = CommanderEncounterLaw.Point(alternateStation.Station);
            rows.Add(row with { Ground = ground, Air = air });
        }
        return plan with { Assignments = rows };
    }

    public static IReadOnlyList<string> Validate(CommanderRaidPlan plan, IReadOnlyList<CommanderRaidMember> roster)
    {
        var errors = new List<string>();
        errors.AddRange(CommanderEncounterLaw.Validate(plan.Encounter));
        if (errors.Count > 0) return errors;
        if (plan.Version != 3 || plan.MapId != plan.Encounter.MapId || plan.BossEntry != plan.Encounter.BossEntry) errors.Add("Encounter identity mismatch.");
        if (plan.Assignments.Count is < 1 or > 40) errors.Add("Assign between 1 and 40 characters.");
        if (plan.Assignments.Select(a => a.Guid).Distinct().Count() != plan.Assignments.Count) errors.Add("A character is assigned twice.");
        if (plan.MainGuid == 0 || plan.MainRole == CommanderRaidRole.Unassigned) errors.Add("Choose your main character's role first.");
        if (plan.Assignments.Count(a => a.Manual) != 1 || !plan.Assignments.Any(a => a.Guid == plan.MainGuid && a.Manual && a.Role == plan.MainRole))
            errors.Add("The main character must be explicitly assigned and remain under manual control.");
        if (plan.Assignments.Count(a => a.Role == CommanderRaidRole.MainTank) != plan.Encounter.ObjectiveEntries.Length)
            errors.Add("Assign a main tank to each encounter objective.");
        foreach (uint entry in plan.Encounter.ObjectiveEntries)
            if (!plan.Assignments.Any(a => a.Role == CommanderRaidRole.MainTank && a.FocusEntry == entry)) errors.Add($"Objective {entry}: assign a tank.");
        if (plan.Assignments.Count(a => a.Role == CommanderRaidRole.Healer) < plan.Encounter.HealerCount)
            errors.Add($"Assign at least {plan.Encounter.HealerCount} healers for this encounter.");
        foreach (var member in roster.Where(m => m.Commandable || m.Guid == plan.MainGuid))
        {
            if (!member.FactsReady) errors.Add($"{member.Name}: waiting for live ability facts.");
            if (!plan.Assignments.Any(a => a.Guid == member.Guid)) errors.Add($"{member.Name}: not assigned.");
        }
        for (int team = 0; team < plan.Encounter.Teams.Length; team++)
        {
            string name = plan.Encounter.Teams[team].Name;
            if (plan.Assignments.Count(a => (int)a.Team == team && a.Role == CommanderRaidRole.AddTank) < plan.Encounter.AddTanksPerTeam) errors.Add($"{name}: assign add tanks.");
            if (plan.Assignments.Count(a => (int)a.Team == team && a.Role == CommanderRaidRole.Healer) < plan.Encounter.HealersPerTeam) errors.Add($"{name}: assign healers.");
        }
        foreach (CommanderRaidAssignment row in plan.Assignments)
        {
            CommanderRaidMember? member = roster.FirstOrDefault(m => m.Guid == row.Guid);
            if (member is null || (!member.Commandable && row.Guid != plan.MainGuid)) { errors.Add($"{row.Name}: not a commandable raid member."); continue; }
            if (!member.Alive) errors.Add($"{row.Name}: unavailable or dead.");
            if (!Enum.IsDefined(row.Role) || row.Role == CommanderRaidRole.Unassigned || (int)row.Team >= plan.Encounter.Teams.Length) errors.Add($"{row.Name}: choose a role and team.");
            if (!InsideRoom(row.Ground, plan.Encounter) || !InsideRoom(row.Air, plan.Encounter)) errors.Add($"{row.Name}: position is outside the encounter room.");
            if (!row.Manual && row.Role == CommanderRaidRole.Healer && (row.HealSpell == 0 || !member.Spells.Contains(row.HealSpell))) errors.Add($"{row.Name}: no learned healing spell selected.");
            if (!row.Manual && row.Role == CommanderRaidRole.Ranged && (row.DamageSpell == 0 || !member.Spells.Contains(row.DamageSpell))) errors.Add($"{row.Name}: no learned ranged spell selected.");
            if (row.Role == CommanderRaidRole.Healer && !plan.Assignments.Any(a => a.Guid == row.HealPrimary)) errors.Add($"{row.Name}: assign a primary patient.");
            if (!row.Manual && new[] { row.InterruptSpell, row.DispelSpell, row.TauntSpell, row.DefensiveSpell }.Any(id => id != 0 && !member.Spells.Contains(id)))
                errors.Add($"{row.Name}: a support ability is not learned.");
        }
        return errors;
    }

    public static bool InsideRoom(Vector3 point, CommanderEncounterDefinition? encounter = null) =>
        CommanderEncounterLaw.Inside(encounter ?? CommanderEncounterCatalog.Default, point);

    public static string Duty(CommanderRaidRole role, bool air) => role switch
    {
        CommanderRaidRole.MainTank => air ? "Save threat tools; prepare to catch landing" : "Hold the tank station; face the boss away from both teams",
        CommanderRaidRole.AddTank => "Collect adds in your team; backup the main tank",
        CommanderRaidRole.Healer => "Keep tanks alive, then heal your team; move for danger",
        CommanderRaidRole.Melee => air ? "Kill adds; do not chase the flying boss" : "Attack the safe flank; avoid front and tail",
        CommanderRaidRole.Ranged => air ? "Spread; damage the boss; escape hazards" : "Damage the boss from your team; respect tank threat",
        _ => "Choose a role",
    };
}

