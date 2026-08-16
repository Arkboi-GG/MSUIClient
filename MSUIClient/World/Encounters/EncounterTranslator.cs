using MSUIClient.Net;

namespace MSUIClient.World.Encounters;

// ─────────────────────────────────────────────────────────────────────────────
// World DB rows → EncounterDefinition.
//
// This is the repeatability engine. Written once, it covers every creature whose
// behaviour lives in data — roughly 2 750 creature_spells entries and 3 570
// EventAI creatures on this world DB — instead of one hand-ported boss. That
// ratio is the entire argument for authoring NEW encounters as data: the tool
// understands them for free, forever, with no per-boss work.
//
// The 725 creatures bound to compiled C++ get an honest hole instead of a guess:
// a declared UnknownUnmodeled marker and the coverage flag that makes the window
// say "scripted behaviour exists; this encounter is not fully modeled."
//
// Enum values below are the core's own (ScriptCommands.h, CreatureEventAI.h) and
// are quoted with their numeric value so a drift shows up as a diff, not as
// silently wrong behaviour.
// ─────────────────────────────────────────────────────────────────────────────

public static class EncounterTranslator
{
    // enum EventAI_Type (CreatureEventAI.h)
    private const int EventTimerInCombat = 0;
    private const int EventTimerOutOfCombat = 1;
    private const int EventHealth = 2;
    private const int EventMana = 3;
    private const int EventAggro = 4;
    private const int EventDeath = 6;
    private const int EventRange = 9;
    private const int EventSpawned = 11;
    private const int EventMovementInform = 29;

    // enum ScriptCommands (ScriptCommands.h)
    private const int CommandTalk = 0;
    private const int CommandEmote = 1;
    private const int CommandMoveTo = 3;
    private const int CommandTempSummonCreature = 10;
    private const int CommandCastSpell = 15;
    private const int CommandDespawnCreature = 18;
    private const int CommandSetPhase = 44;

    // enum ScriptTarget (ScriptCommands.h)
    private const int TargetProvided = 0;
    private const int TargetHostile = 1;
    private const int TargetHostileSecondAggro = 2;
    private const int TargetHostileLastAggro = 3;
    private const int TargetHostileRandom = 4;
    private const int TargetHostileRandomNotTop = 5;
    private const int TargetHostileNearest = 6;
    private const int TargetHostileFarthest = 7;
    private const int TargetOwnerOrSelf = 8;
    private const int TargetOwner = 9;
    private const int TargetFriendly = 16;

    /// <summary>
    /// Build a definition for one creature straight from the world DB. Returns a
    /// definition even when nothing is known — an encounter with no modeled
    /// behaviour is a legitimate, and visible, answer.
    /// </summary>
    public static EncounterDefinition FromDatabase(
        uint entry,
        string creatureName,
        uint spellListId,
        string? scriptName,
        string? aiName,
        EncounterWorldData? data,
        IEncounterSpellFacts? facts,
        uint maxHealth = 100000)
    {
        List<EncounterAbility> abilities = [];
        EncounterCoverage coverage = EncounterCoverage.Template;
        var phaseKeys = new SortedSet<int>();

        // ── tier 1: creature_spells (exact, and the cleanest data there is) ──
        CreatureSpellList? spellList = spellListId != 0 ? data?.SpellList(spellListId) : null;
        if (spellList is not null)
        {
            coverage |= EncounterCoverage.CreatureSpells;
            foreach (CreatureSpellSlot slot in spellList.Slots)
                abilities.Add(FromSpellSlot(slot, facts));
        }

        // ── tier 2: EventAI ──────────────────────────────────────────────────
        IReadOnlyList<EventAiEvent> events = data?.EventsFor(entry) ?? [];
        if (events.Count > 0)
        {
            coverage |= EncounterCoverage.EventAi;
            foreach (EventAiEvent aiEvent in events)
            {
                CollectPhases(aiEvent.InversePhaseMask, phaseKeys);
                abilities.AddRange(FromEventAiEvent(aiEvent, data, facts, phaseKeys));
            }
        }

        // ── tier 3: compiled C++ — declared as a hole, never guessed ─────────
        if (!string.IsNullOrWhiteSpace(scriptName))
        {
            coverage |= EncounterCoverage.CppCreatureScript;
            abilities.Add(new EncounterAbility(
                Key: $"cpp:{scriptName}",
                Name: $"compiled script '{scriptName}'",
                SpellId: 0,
                Trigger: EncounterTriggerSpec.Manual,
                Timing: EncounterTiming.Never,
                Target: EncounterTargetSpec.Caster,
                Geometry: EncounterGeometrySpec.None,
                Fidelity: EncounterFidelity.UnknownUnmodeled,
                Sources: [new EncounterSourceRef("db-table", "creature_template.script_name", scriptName)],
                Note: "Scripted behaviour exists in compiled C++; this encounter is not " +
                      "fully modeled. Author a manifest to make it simulate."));
        }

        IReadOnlyList<EncounterPhase> phases = BuildPhases(phaseKeys);
        return new EncounterDefinition(
            Key: $"db:{entry}",
            Name: creatureName,
            PrimaryEntry: entry,
            Phases: phases,
            Abilities: abilities,
            Provenance: new EncounterProvenance(
                Source: "world-db",
                DbRevision: data?.FetchedUtc.ToString("O"),
                ContentPatch: EncounterDataClient.ContentBuild,
                CapturedUtc: DateTime.UtcNow),
            Coverage: coverage,
            MemberEntries: [entry],
            Actors:
            [
                new EncounterActorSpec("boss", creatureName, entry, EncounterActorRole.Boss,
                    default, 0f, 2f, 1.5f, 60, maxHealth),
            ],
            Note: string.IsNullOrWhiteSpace(aiName) ? null : $"ai_name = {aiName}");
    }

    // ── creature_spells ──────────────────────────────────────────────────────

    private static EncounterAbility FromSpellSlot(CreatureSpellSlot slot, IEncounterSpellFacts? facts)
    {
        (EncounterTargetKind target, bool exactTarget) = MapCastTarget(slot.CastTarget);
        EncounterGeometrySpec geometry = InferGeometry(slot.SpellId, facts);

        // Timing and chance are straight out of the table; the target mapping may be
        // an approximation (there is no threat table here), and that alone is enough
        // to drop the whole ability's fidelity. Never average confidences.
        EncounterFidelity fidelity = exactTarget
            ? EncounterFidelity.ExactDb
            : EncounterFidelity.Heuristic;

        return new EncounterAbility(
            Key: $"spells:{slot.Index}:{slot.SpellId}",
            Name: facts?.SpellName(slot.SpellId) ?? $"spell {slot.SpellId}",
            SpellId: slot.SpellId,
            Trigger: new EncounterTriggerSpec(EncounterTriggerKind.Timer),
            Timing: new EncounterTiming(
                slot.InitialMinMs, slot.InitialMaxMs, slot.RepeatMinMs, slot.RepeatMaxMs),
            Target: new EncounterTargetSpec(target, default, slot.TargetParam1),
            Geometry: geometry,
            Fidelity: fidelity,
            ChancePercent: slot.Probability,
            Sources:
            [
                new EncounterSourceRef("db-table", $"creature_spells.spellId_{slot.Index}"),
                new EncounterSourceRef("db-table", $"creature_spells.delayRepeatMin_{slot.Index}",
                    "seconds in DB, x1000 at load"),
            ],
            Note: exactTarget ? null : $"castTarget {slot.CastTarget} approximated (no threat model)");
    }

    // ── EventAI ──────────────────────────────────────────────────────────────

    private static IEnumerable<EncounterAbility> FromEventAiEvent(
        EventAiEvent aiEvent, EncounterWorldData? data, IEncounterSpellFacts? facts,
        SortedSet<int> phaseKeys)
    {
        (EncounterTriggerSpec trigger, EncounterTiming timing, bool modeled) = MapEventType(aiEvent);
        IReadOnlyList<string>? phases = PhaseListFor(aiEvent.InversePhaseMask, phaseKeys);

        uint[] scripts = [aiEvent.Action1Script, aiEvent.Action2Script, aiEvent.Action3Script];
        int emitted = 0;

        for (int slot = 0; slot < scripts.Length; slot++)
        {
            uint scriptId = scripts[slot];
            if (scriptId == 0) continue;
            IReadOnlyList<AiScriptCommand> commands = data?.Script(scriptId) ?? [];
            if (commands.Count == 0) continue;

            // The interesting command in an EventAI action is almost always the cast;
            // everything else becomes choreography attached to it.
            AiScriptCommand? cast = commands.FirstOrDefault(c => c.Command == CommandCastSpell);
            List<EncounterStep> steps = commands
                .Where(c => c.Command != CommandCastSpell)
                .Select(MapCommandToStep)
                .Where(s => s is not null)
                .Select(s => s!)
                .ToList();

            uint spellId = cast?.Datalong ?? 0;
            (EncounterTargetKind target, bool exactTarget) =
                MapCastTarget(cast?.TargetType ?? TargetHostile);

            EncounterFidelity fidelity =
                !modeled ? EncounterFidelity.UnknownUnmodeled
                : aiEvent.ConditionId != 0 ? EncounterFidelity.Heuristic  // conditions unevaluated
                : exactTarget ? EncounterFidelity.ExactDb
                : EncounterFidelity.Heuristic;

            string name = spellId != 0
                ? facts?.SpellName(spellId) ?? $"spell {spellId}"
                : DescribeCommands(commands, aiEvent.Comment);

            yield return new EncounterAbility(
                Key: $"eventai:{aiEvent.Id}:{slot}",
                Name: name,
                SpellId: spellId,
                Trigger: trigger,
                Timing: timing,
                Target: new EncounterTargetSpec(target),
                Geometry: spellId != 0 ? InferGeometry(spellId, facts) : EncounterGeometrySpec.None,
                Fidelity: fidelity,
                Phases: phases,
                ChancePercent: aiEvent.Chance <= 0 ? 100 : aiEvent.Chance,
                Steps: steps.Count > 0 ? steps : null,
                Sources:
                [
                    new EncounterSourceRef("db-table", $"creature_ai_events.id={aiEvent.Id}",
                        aiEvent.Comment),
                    new EncounterSourceRef("db-table", $"creature_ai_scripts.id={scriptId}"),
                ],
                Note: !modeled
                    ? $"event_type {aiEvent.EventType} is not modeled"
                    : aiEvent.ConditionId != 0
                        ? $"gated by condition_id {aiEvent.ConditionId}, not evaluated here"
                        : null);
            emitted++;
        }

        // An event whose actions resolved to nothing is still a fact about the
        // creature. Report it rather than dropping it silently.
        if (emitted == 0)
            yield return new EncounterAbility(
                Key: $"eventai:{aiEvent.Id}:empty",
                Name: string.IsNullOrWhiteSpace(aiEvent.Comment)
                    ? $"event {aiEvent.EventType}" : aiEvent.Comment,
                SpellId: 0,
                Trigger: trigger,
                Timing: timing,
                Target: EncounterTargetSpec.Caster,
                Geometry: EncounterGeometrySpec.None,
                Fidelity: EncounterFidelity.UnknownUnmodeled,
                Phases: phases,
                Sources: [new EncounterSourceRef("db-table", $"creature_ai_events.id={aiEvent.Id}")],
                Note: "action scripts missing or empty");
    }

    private static EncounterStep? MapCommandToStep(AiScriptCommand command) => command.Command switch
    {
        CommandTalk => new EncounterStep(EncounterStepKind.Say,
            Note: string.IsNullOrWhiteSpace(command.Comment) ? "says something" : command.Comment),
        CommandMoveTo => new EncounterStep(EncounterStepKind.MoveTo, Point: command.Position),
        CommandTempSummonCreature => new EncounterStep(EncounterStepKind.Summon,
            Point: command.Position, Entry: command.Datalong, Count: 1),
        CommandDespawnCreature => new EncounterStep(EncounterStepKind.DespawnSummons),
        CommandSetPhase => new EncounterStep(EncounterStepKind.SetPhase,
            PhaseKey: $"p{command.Datalong}"),
        CommandEmote => null,
        _ => new EncounterStep(EncounterStepKind.Unmodeled,
            Note: $"SCRIPT_COMMAND {command.Command}" +
                  (string.IsNullOrWhiteSpace(command.Comment) ? "" : $" ({command.Comment})")),
    };

    private static (EncounterTriggerSpec, EncounterTiming, bool Modeled) MapEventType(EventAiEvent e)
    {
        switch (e.EventType)
        {
            case EventTimerInCombat:
                return (new EncounterTriggerSpec(EncounterTriggerKind.Timer),
                    new EncounterTiming(e.Param1, e.Param2, e.Param3, e.Param4), true);

            case EventTimerOutOfCombat:
                // Out-of-combat timers do not belong in a combat timeline; keep the
                // fact, refuse to fire it.
                return (EncounterTriggerSpec.Manual, EncounterTiming.Never, false);

            case EventHealth:
                // params are HPMax%, HPMin%, RepeatMin, RepeatMax — a BAND, not a point.
                return (new EncounterTriggerSpec(EncounterTriggerKind.HealthBelow, e.Param1 / 100f),
                    new EncounterTiming(0, 0, e.Param3, e.Param4), true);

            case EventMana:
                return (new EncounterTriggerSpec(EncounterTriggerKind.ManaBelow, e.Param1 / 100f),
                    EncounterTiming.Never, false);

            case EventAggro:
            case EventSpawned:
                return (new EncounterTriggerSpec(EncounterTriggerKind.OnPhaseEnter),
                    new EncounterTiming(0, 0, 0, 0), true);

            case EventDeath:
                return (new EncounterTriggerSpec(EncounterTriggerKind.OnDeath), EncounterTiming.Never, true);

            case EventRange:
                return (new EncounterTriggerSpec(EncounterTriggerKind.TargetInRange, e.Param2),
                    new EncounterTiming(0, 0, e.Param3, e.Param4), true);

            case EventMovementInform:
                return (new EncounterTriggerSpec(EncounterTriggerKind.OnMovementDone),
                    new EncounterTiming(0, 0, e.Param3, e.Param4), true);

            default:
                return (EncounterTriggerSpec.Manual, EncounterTiming.Never, false);
        }
    }

    /// <summary>ScriptTarget → the sim's target vocabulary. The bool says whether the
    /// mapping is faithful; anything needing a threat table is not.</summary>
    private static (EncounterTargetKind Kind, bool Exact) MapCastTarget(int castTarget) =>
        castTarget switch
        {
            TargetProvided or TargetHostile => (EncounterTargetKind.CurrentVictim, false),
            TargetHostileRandom => (EncounterTargetKind.RandomHostile, true),
            TargetHostileRandomNotTop => (EncounterTargetKind.RandomHostileNotVictim, true),
            TargetHostileNearest => (EncounterTargetKind.NearestHostile, true),
            TargetHostileSecondAggro or TargetHostileLastAggro or TargetHostileFarthest =>
                (EncounterTargetKind.RandomHostileNotVictim, false),
            TargetOwnerOrSelf or TargetOwner => (EncounterTargetKind.Self, true),
            >= TargetFriendly => (EncounterTargetKind.Self, false),
            _ => (EncounterTargetKind.CurrentVictim, false),
        };

    /// <summary>Shape from spell data: a cone if the world DB declares one, otherwise
    /// a disc at the effect radius. Single-target spells still get a small disc — the
    /// spell does land somewhere, and drawing it is more useful than drawing nothing.</summary>
    private static EncounterGeometrySpec InferGeometry(uint spellId, IEncounterSpellFacts? facts)
    {
        if (facts is null) return new EncounterGeometrySpec(FootprintKind.Circle);
        if (facts.TryGetConeDegrees(spellId, out float degrees) && degrees != 0f)
            return new EncounterGeometrySpec(FootprintKind.Cone, ConeDegrees: degrees);
        if (facts.TryGetRadius(spellId, out float radius) && radius > 0f)
            return new EncounterGeometrySpec(FootprintKind.Circle, radius);
        return new EncounterGeometrySpec(FootprintKind.Circle);
    }

    // ── EventAI phases ───────────────────────────────────────────────────────
    //
    // EventAI phases are an INVERSE mask: an event is active in phase p when bit p
    // of event_inverse_phase_mask is CLEAR. Modelling that faithfully is what lets
    // a DB-driven multi-phase creature simulate without a single line of C++.

    private const int MaxEventAiPhases = 8;

    private static void CollectPhases(uint inverseMask, SortedSet<int> phases)
    {
        if (inverseMask == 0) return;
        for (int p = 0; p < MaxEventAiPhases; p++)
            if ((inverseMask & (1u << p)) == 0) phases.Add(p);
    }

    private static IReadOnlyList<string>? PhaseListFor(uint inverseMask, SortedSet<int> known)
    {
        if (inverseMask == 0 || known.Count == 0) return null;   // active everywhere
        List<string> active = [];
        foreach (int p in known)
            if ((inverseMask & (1u << p)) == 0) active.Add($"p{p}");
        return active.Count > 0 ? active : null;
    }

    private static IReadOnlyList<EncounterPhase> BuildPhases(SortedSet<int> phaseKeys)
    {
        if (phaseKeys.Count == 0)
            return [new EncounterPhase("all", "combat")];
        return phaseKeys
            .Select(p => new EncounterPhase($"p{p}", $"phase {p}"))
            .ToArray();
    }

    private static string DescribeCommands(IReadOnlyList<AiScriptCommand> commands, string comment)
    {
        if (!string.IsNullOrWhiteSpace(comment)) return comment;
        return commands.Count switch
        {
            0 => "no action",
            1 => $"script command {commands[0].Command}",
            _ => $"{commands.Count} script commands",
        };
    }
}
