using System.Globalization;
using MSUIClient.Engine;
using MSUIClient.Formats;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private double _spellFamilyEvidenceStarted;
    private readonly HashSet<uint> _spellFamilyCandidates = [];

    private void BeginSpellFamilyEvidence(uint rootSpell)
    {
        _spellFamilyEvidenceStarted = NowSeconds();
        _spellFamilyCandidates.Clear();
        var pending = new Queue<uint>();
        pending.Enqueue(rootSpell);
        while (pending.TryDequeue(out uint id))
        {
            if (!_spellFamilyCandidates.Add(id) || _spellCatalog?.TryGet(id, out SpellInfo spell) != true) continue;
            foreach (uint child in spell.EffectTriggerSpells ?? [])
                if (child != 0 && !_spellFamilyCandidates.Contains(child)) pending.Enqueue(child);
        }
    }

    private void SampleSpellFamilyEvidence(SpellInfo rootSpell, string frame)
    {
        if (_spellCatalog is null || _spellVisualCatalog is null) return;
        // DBC reachability is only a candidate relation. Include children only after
        // an actual GO in this attempt; never count merely referenced effects as seen.
        var observed = _verdicts.Snapshot("spell-sweep").OfType<SpellSweepVerdict>()
            .Where(v => v.Time >= _spellFamilyEvidenceStarted && v.Result == "SMSG_SPELL_GO" &&
                _spellFamilyCandidates.Contains(v.SpellId)).Select(v => v.SpellId).ToHashSet();
        observed.Add(rootSpell.Id);
        double now = NowSeconds();
        string path = Path.Combine(ResolveLiveOutputDirectory(), "spell-family-stages.csv");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!File.Exists(path)) File.WriteAllText(path,
            "time,root_spell,spell_id,cell,frame,stage,expected_models,instances,submitted_quads,mesh_seen,ribbon_seen,evidence\n");
        var lines = new List<string>();
        foreach (uint spellId in observed.Order())
        {
            if (!_spellCatalog.TryGet(spellId, out SpellInfo spell) ||
                !_spellVisualCatalog.TryGetStages(spell.VisualId, out SpellVisualStages stages)) continue;
            var instances = _spellEffects?.Snapshot(spellId, now, SpellEffectUnitPose) ?? [];
            foreach (string stage in new[] { "PRECAST", "CAST", "MISSILE", "IMPACT", "CHANNEL", "STATE", "AREA", "AREA_SHARD" })
            {
                IEnumerable<string> expected;
                if (stage == "MISSILE") expected = _spellVisualCatalog.MissilePath(stages) is { } missile ? [missile] : [];
                else if (stage.StartsWith("AREA", StringComparison.Ordinal))
                    expected = _spellVisualCatalog.TryGetAreaVisual(spell.VisualId, out var area)
                        ? area.Emitters.Select(x => x.ModelPath).Concat(area.LoopingModelPath is { } loop ? [loop] : []) : [];
                else
                {
                    uint kitId = stage switch { "PRECAST" => stages.Precast, "CAST" => stages.Cast,
                        "IMPACT" => stages.Impact, "CHANNEL" => stages.Channel, "STATE" => stages.State, _ => 0 };
                    expected = kitId != 0 && _spellVisualCatalog.TryGetKit(kitId, out var kit)
                        ? kit.Effects.Select(x => x.ModelPath) : [];
                }
                string[] models = expected.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                var live = instances.Where(x => x.Stage == stage).ToArray();
                if (models.Length == 0 && live.Length == 0) continue;
                int submitted = live.Sum(x => _spellParticles?.VisualState($"spell:{x.Path}#{x.Id}").DrawnParticles ?? 0);
                bool mesh = live.Any(x => _spellEffectMeshes?.WasDrawn(x.Path) == true);
                bool ribbon = live.Any(x => _spellRibbons?.WasDrawn(x.Path) == true);
                // This is telemetry, not a pixel oracle. Mesh/ribbon draws can be
                // shared by equal model paths; frame review remains required.
                string[] row = [now.ToString("F3", CultureInfo.InvariantCulture), rootSpell.Id.ToString(), spellId.ToString(),
                    _animationSequenceCell, frame, stage, string.Join('|', models), live.Length.ToString(),
                    submitted.ToString(), mesh.ToString(), ribbon.ToString(), "OBSERVED_REQUIRES_REVIEW"];
                lines.Add(string.Join(',', row.Select(value => '"' + value.Replace("\"", "\"\"") + '"')));
            }
        }
        File.AppendAllLines(path, lines);
    }
}
