using System.Text.Json;
using System.Text.Json.Serialization;

namespace MSUIClient.World.Encounters;

// ─────────────────────────────────────────────────────────────────────────────
// The raid plan document — PLAN_19 M-A.
//
// ONE portable JSON file bundling everything two executors need to run the same
// fight: the raid-wide doctrine (with its class-gated rules), every body's
// encounter-local rules (per-phase targets, avoidance, macro group), and the
// referenced rotation library entries inlined so the document stands alone. The
// Lab exports/imports it; MangosSuperUI stores and assigns it; SuperUI-Core
// ingests it over the brain bridge as LOAD_RAID_PLAN. Version everything: a
// future field is additive, an unknown version is refused loudly, never guessed.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>One body's slice of the plan: identity plus its encounter-local rules.
/// InlinePlan carries the body's resolved rotation so the document is complete
/// even when a library id is missing on the receiving side.</summary>
public sealed record RaidPlanBody(
    string Key,
    string Name,
    RaidJob Job,
    RaidSide Side,
    uint ClassId,
    string? RotationId = null,
    string? PositioningId = null,
    IReadOnlyList<PhaseTargetOverride>? PhaseTargets = null,
    IReadOnlyList<string>? AvoidAbilityKeys = null,
    bool AlwaysFaceBoss = false,
    CombatPlan? InlinePlan = null);

public sealed record RaidPlanDocument(
    int SchemaVersion,
    string Name,
    /// <summary>The encounter this plan was authored against. Advisory — phase keys
    /// and ability keys inside only mean anything on this encounter.</summary>
    string? EncounterKey,
    RaidDoctrine Doctrine,
    IReadOnlyList<RaidPlanBody> Bodies,
    /// <summary>Referenced rotation library entries, keyed by id, inlined so the
    /// document travels whole.</summary>
    IReadOnlyDictionary<string, CombatPlan>? Rotations = null)
{
    public const int CurrentSchemaVersion = 1;
}

/// <summary>Read/write the document. Deliberately tolerant on read (a malformed
/// file reports an error string, never throws into the UI loop) and strict on
/// version (an unknown schema is refused, not guessed at).</summary>
public static class RaidPlanFile
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Assemble the document from a live scenario + doctrine + rotation
    /// library. Boss and adds are not part of a raid plan; only friendlies travel.</summary>
    public static RaidPlanDocument Build(
        string name,
        string? encounterKey,
        RaidDoctrine doctrine,
        IEnumerable<EncounterActorSpec> scenario,
        IReadOnlyDictionary<string, CombatPlan> rotationLibrary)
    {
        List<RaidPlanBody> bodies = [];
        Dictionary<string, CombatPlan> rotations = new(StringComparer.Ordinal);
        foreach (EncounterActorSpec actor in scenario)
        {
            if (actor.Role != EncounterActorRole.Friendly) continue;
            EncounterPlayerRules? rules = actor.PlayerRules;
            string? rotationId = rules?.RotationId;
            if (rotationId is { Length: > 0 } &&
                rotationLibrary.TryGetValue(rotationId, out CombatPlan? library))
                rotations[rotationId] = library;
            bodies.Add(new RaidPlanBody(
                actor.Key, actor.Name, actor.Job, actor.Side, actor.ClassId,
                rotationId, rules?.PositioningId, rules?.PhaseTargets,
                rules?.AvoidAbilityKeys, rules?.AlwaysFaceBoss ?? false,
                rules?.Plan));
        }
        return new RaidPlanDocument(RaidPlanDocument.CurrentSchemaVersion,
            name, encounterKey, doctrine, bodies,
            rotations.Count > 0 ? rotations : null);
    }

    public static bool Save(RaidPlanDocument document, string path, out string? error)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
            File.WriteAllText(path, JsonSerializer.Serialize(document, Options));
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static RaidPlanDocument? Load(string path, out string? error)
    {
        try
        {
            RaidPlanDocument? document = JsonSerializer.Deserialize<RaidPlanDocument>(
                File.ReadAllText(path), Options);
            if (document is null) { error = "empty document"; return null; }
            if (document.SchemaVersion > RaidPlanDocument.CurrentSchemaVersion)
            {
                error = $"schema {document.SchemaVersion} is newer than this build " +
                        $"({RaidPlanDocument.CurrentSchemaVersion})";
                return null;
            }
            error = null;
            return document;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    /// <summary>Apply a document onto a live scenario: each body entry lands on the
    /// actor with the matching key (missing keys are reported, never invented), the
    /// bundled rotations upsert into the library. Returns how many bodies took.</summary>
    public static int Apply(
        RaidPlanDocument document,
        IList<EncounterActorSpec> scenario,
        Action<string, CombatPlan> upsertRotation,
        out List<string> missingKeys)
    {
        missingKeys = [];
        if (document.Rotations is { } rotations)
            foreach ((string id, CombatPlan plan) in rotations)
                upsertRotation(id, plan);

        int applied = 0;
        foreach (RaidPlanBody body in document.Bodies)
        {
            int index = -1;
            for (int i = 0; i < scenario.Count; i++)
                if (string.Equals(scenario[i].Key, body.Key, StringComparison.Ordinal))
                { index = i; break; }
            if (index < 0) { missingKeys.Add(body.Key); continue; }

            EncounterActorSpec actor = scenario[index];
            scenario[index] = actor with
            {
                Job = body.Job,
                Side = body.Side,
                ClassId = body.ClassId,
                PlayerRules = new EncounterPlayerRules(
                    AlwaysFaceBoss: body.AlwaysFaceBoss,
                    Plan: body.InlinePlan,
                    RotationId: body.RotationId,
                    PositioningId: body.PositioningId,
                    PhaseTargets: body.PhaseTargets,
                    AvoidAbilityKeys: body.AvoidAbilityKeys),
            };
            applied++;
        }
        return applied;
    }
}
