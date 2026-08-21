using System.Numerics;
using System.Text.Json;
using ImGuiNET;
using MSUIClient.Formats;
using MSUIClient.Net;
using MSUIClient.World.Encounters;

namespace MSUIClient;

// ─────────────────────────────────────────────────────────────────────────────
// THE TAPE — passive recording of a real fight, and the diff that keeps the
// simulator honest.
//
// This is the cheapest high-value half of the Lab and it needs NO server work at
// all. SMSG_SPELL_GO already carries the caster, the spell, the destination and
// the full hit/miss lists; SMSG_MONSTER_MOVE already carries splines. Both are
// parsed by the client today. Recording them is a few hundred lines and gives
// ground truth for every creature in the game — including the 725 bound to
// compiled C++ that the simulator will never model.
//
// A simulator with no tape drifts into fiction. The diff below is what stops it:
// it names spells the model invented and spells the model missed, per encounter.
//
// INSTRUMENTATION-HAZARD RULE (same as the NPC dev window's observed-path tap):
// the recorder no-ops unless the window is open AND recording is armed. It must
// never cost anything in ordinary play.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>One observed wire event, normalised to milliseconds since the tape started.</summary>
public sealed record TapeEvent(
    int TimeMs,
    string Kind,                // "cast" | "move"
    ulong CasterGuid,
    uint CasterEntry,
    uint SpellId,
    string SpellName,
    float[]? Destination,       // arrays, not Vector3: this is a file format
    int HitCount,
    int MissCount);

/// <summary>A recorded fight. Deliberately a dumb list — the value is that it is
/// observed truth, so nothing here is allowed to be clever.</summary>
public sealed class EncounterTape
{
    public string StartedUtc { get; set; } = DateTime.UtcNow.ToString("O");
    public string? Character { get; set; }
    public int MapId { get; set; }
    public string? EncounterKey { get; set; }
    public List<TapeEvent> Events { get; set; } = [];
}

public sealed partial class GameLoop
{
    private EncounterTape? _encounterTape;
    private double _encounterTapeStartedAt;
    private string? _encounterTapeSavedPath;

    /// <summary>
    /// SMSG_SPELL_GO tap. Called from the spell-go handler; no-ops unless the Lab is
    /// open and recording is armed.
    /// </summary>
    private void RecordEncounterTapeCast(in SpellGoPacket packet)
    {
        if (!_encounterLabOpen || !Settings.EncounterLab.RecordTape) return;
        _encounterTape ??= StartEncounterTape();

        uint entry = 0;
        if (_entities.TryGet(packet.Caster, out WorldEntity caster)) entry = caster.Entry;

        float[]? destination = packet.Targets.Destination is { } point
            ? [point.X, point.Y, point.Z]
            : null;

        _encounterTape.Events.Add(new TapeEvent(
            TimeMs: (int)((NowSeconds() - _encounterTapeStartedAt) * 1000.0),
            Kind: "cast",
            CasterGuid: packet.Caster,
            CasterEntry: entry,
            SpellId: packet.SpellId,
            SpellName: _spellCatalog?.TryGet(packet.SpellId, out SpellInfo spell) == true
                ? spell.Name : $"spell {packet.SpellId}",
            Destination: destination,
            HitCount: packet.Hits.Length,
            MissCount: packet.Misses.Length));
    }

    private EncounterTape StartEncounterTape()
    {
        _encounterTapeStartedAt = NowSeconds();
        _encounterTapeSavedPath = null;
        return new EncounterTape
        {
            Character = _net?.PlayerName is { Length: > 0 } name ? name : "offline",
            MapId = _config.Start.Map,
            EncounterKey = _encounterDefinition?.Key,
        };
    }

    private void SaveEncounterTape()
    {
        if (_encounterTape is not { Events.Count: > 0 } tape) return;
        string directory = Path.Combine(_config.RepoRoot, "encounter-tapes");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory,
            $"{DateTime.Now:yyyyMMdd-HHmmss}-{tape.EncounterKey ?? "unnamed"}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(tape, EncounterLibrary.JsonOptions));
        _encounterTapeSavedPath = path;
        Console.WriteLine($"[encounter-tape] wrote {tape.Events.Count} events to {path}");
    }

    // ── predicted vs observed ────────────────────────────────────────────────

    private readonly record struct TapeDiffRow(
        uint SpellId, string Name, int Observed, int Predicted, string Verdict);

    /// <summary>
    /// Compare what the simulator says should happen against what the server
    /// actually did. Deliberately coarse — per-spell counts, not per-instant
    /// alignment — because timing drifts legitimately (threat, movement, resists)
    /// while a MISSING or INVENTED spell is always a real modelling defect.
    /// </summary>
    private List<TapeDiffRow> BuildTapeDiff()
    {
        List<TapeDiffRow> rows = [];
        if (_encounterTape is not { } tape || _encounterSim is not { } sim) return rows;

        Dictionary<uint, int> observed = [];
        foreach (TapeEvent recorded in tape.Events)
        {
            if (recorded.Kind != "cast" || recorded.SpellId == 0) continue;
            // Only the encounter's own members count: a raid's own casts are noise here.
            if (_encounterDefinition?.MemberEntries is { Count: > 0 } members &&
                recorded.CasterEntry != 0 && !members.Contains(recorded.CasterEntry)) continue;
            observed[recorded.SpellId] = observed.GetValueOrDefault(recorded.SpellId) + 1;
        }

        Dictionary<uint, int> predicted = [];
        foreach (SimEvent simEvent in sim.Events)
        {
            if (simEvent.Kind != SimEventKind.CastLand || simEvent.SpellId == 0) continue;
            predicted[simEvent.SpellId] = predicted.GetValueOrDefault(simEvent.SpellId) + 1;
        }

        foreach (uint spellId in observed.Keys.Union(predicted.Keys).OrderBy(id => id))
        {
            int seen = observed.GetValueOrDefault(spellId);
            int expected = predicted.GetValueOrDefault(spellId);
            string verdict =
                seen > 0 && expected == 0 ? "MISSING FROM MODEL"
                : seen == 0 && expected > 0 ? "model invented it"
                : "both";
            rows.Add(new TapeDiffRow(spellId,
                _spellCatalog?.TryGet(spellId, out SpellInfo spell) == true
                    ? spell.Name : $"spell {spellId}",
                seen, expected, verdict));
        }
        return rows;
    }

    private void DrawEncounterTapeSection()
    {
        if (!EncounterSectionHeader("Tape (record & compare)")) return;

        var settings = Settings.EncounterLab;
        bool recording = settings.RecordTape;
        if (ImGui.Checkbox("record live casts", ref recording))
        {
            settings.RecordTape = recording;
            if (recording) _encounterTape = StartEncounterTape();
            SettingsFile?.Save();
        }
        ImGui.TextDisabled("passive: reads SMSG_SPELL_GO the client already parses. No server work.");

        if (_net is not { IsInWorld: true })
            ImGui.TextDisabled("(no live server — recording needs a world connection)");

        if (_encounterTape is { } tape)
        {
            ImGui.Text($"{tape.Events.Count} events recorded");
            if (EncounterPanelButton("Save tape")) SaveEncounterTape();
            EncounterSameLineForButton("Clear tape");
            if (EncounterPanelButton("Clear tape"))
            { _encounterTape = null; _encounterTapeSavedPath = null; }
            if (_encounterTapeSavedPath is { } path) ImGui.TextDisabled(path);
        }
        else
        {
            ImGui.TextDisabled("nothing recorded yet");
            return;
        }

        ImGui.Separator();
        List<TapeDiffRow> diff = BuildTapeDiff();
        if (diff.Count == 0)
        {
            ImGui.TextDisabled("simulate and record to compare");
            return;
        }

        ImGui.Text("predicted vs observed");
        foreach (TapeDiffRow row in diff)
        {
            Vector4 color = row.Verdict switch
            {
                "MISSING FROM MODEL" => new Vector4(1f, .45f, .38f, 1f),
                "model invented it" => new Vector4(1f, .78f, .45f, 1f),
                _ => new Vector4(.6f, .85f, .65f, 1f),
            };
            ImGui.TextColored(color,
                $"{row.Name,-24} observed {row.Observed,3}  predicted {row.Predicted,3}  {row.Verdict}");
        }

        int missing = diff.Count(r => r.Verdict == "MISSING FROM MODEL");
        if (missing > 0)
            ImGui.TextColored(new Vector4(1f, .45f, .38f, 1f),
                $"{missing} spell(s) the server casts and the model does not know about");
    }
}
