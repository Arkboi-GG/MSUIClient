using MSUIClient.Engine;
using MSUIClient.Formats;
using MSUIClient.Net;
using System.Numerics;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private uint _animationSequenceSpell;
    private string _animationSequenceCell = "";
    private string _animationSequenceStage = "SETTLE";
    private readonly List<SpellAnimationSequenceVerdict> _animationSequenceSamples = [];
    private readonly record struct LayerVisualSample(string Precast, string Cast, string Missile, string Impact,
        string Instances, Vector3[] MissilePositions);
    private readonly List<LayerVisualSample> _animationVisualSamples = [];

    private bool BeginAnimationSequence(uint spellId, string cell)
    {
        if (_spellCatalog?.TryGet(spellId, out _) != true || _character is null) return false;
        _animationSequenceSpell = spellId;
        _animationSequenceCell = cell.ToUpperInvariant();
        _animationSequenceStage = "SETTLE";
        _animationSequenceSamples.Clear();
        _animationVisualSamples.Clear();
        return true;
    }

    private void MarkAnimationSequenceStage(uint spellId, string stage)
    {
        if (_animationSequenceSpell == spellId) _animationSequenceStage = stage;
    }

    private bool SampleAnimationSequence(string frame)
    {
        if (_animationSequenceSpell == 0 || _character is null || _spellCatalog is null ||
            !_spellCatalog.TryGet(_animationSequenceSpell, out SpellInfo spell)) return false;

        int track = _animationSequenceStage is "PRECAST" or "CHANNEL" ? 2 : 1;
        (int Requested, int Played, AnimChoiceKind Kind) state =
            _lastAnimChoices.TryGetValue(("player", track), out var actual)
                ? actual : (-1, -1, AnimChoiceKind.Missing);
        (uint kitId, ushort? expected, IReadOnlyList<SpellVisualKitEffect> effects) =
            ExpectedStage(spell, _animationSequenceStage);
        var sources = new List<string>();
        bool assetMissing = false;
        foreach (SpellVisualKitEffect effect in effects)
        {
            string rawPath = effect.ModelPath;
            string path = NormalizeModelPath(rawPath);
            var supplier = _mpq?.ReadFileWithSupplier(path);
            if (supplier is null) { sources.Add($"MISSING:{path}"); assetMissing = true; }
            else sources.Add($"{supplier.Value.Supplier}:{path}");
        }
        sources = sources.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        LayerVisualSample visual = SampleSpellVisualLayer(spell);
        _animationVisualSamples.Add(visual);
        IReadOnlyList<string> active = _spellEffects?.ActiveModelPaths(_animationSequenceSpell) ?? [];
        _entities.TryGet(_net?.PlayerGuid ?? 0, out var player);
        _entities.TryGet(_selectionGuid, out var selection);
        string AuraFingerprint(WorldEntity? unit) => string.Join('|', SnapshotAuras(unit).Values
            .OrderBy(aura => aura.Slot).Select(aura => $"{aura.Slot}:{aura.SpellId}:{aura.Stacks}"));
        string inventory = player is null ? "" : string.Join('|', Enumerable.Range(0, 23)
            .Select(slot => player.Fields.PlayerInventorySlot(slot)).Concat(Enumerable.Range(0, 16)
                .Select(slot => player.Fields.PlayerBackpackSlot(slot))).Where(guid => guid != 0)
            .Select(guid => $"{guid:X16}"));
        string verdict = assetMissing ? "ANIM-ASSET-MISSING" :
            expected is { } expectedId && state.Played == expectedId &&
                state.Kind is AnimChoiceKind.Exact or AnimChoiceKind.BakedOnDemand ? "ANIM-EXACT" :
            state.Kind is AnimChoiceKind.Fallback or AnimChoiceKind.Substituted ||
                (expected is { } authored && state.Played >= 0 && state.Played != authored) ? "ANIM-FALLBACK" :
            "ANIM-STATIC";
        var row = new SpellAnimationSequenceVerdict(NowSeconds(), _net?.PlayerName ?? "",
            spell.Id, _animationSequenceCell, "SAMPLE", "MEASURED", _animationSequenceSamples.Count,
            frame, _animationSequenceStage, expected ?? -1, state.Requested, state.Played, state.Kind,
            _character.CurrentPresentationAnimation, _character.CurrentBaseAnimation,
            _character.PreviousBaseAnimation, _character.CurrentActionAnimation,
            _character.CurrentSpellHoldAnimation, _character.CurrentBlendWeight,
            _character.GroundSpeed > .3f, player?.Fields.Health ?? 0, player?.Fields.ActivePower ?? 0,
            selection?.Fields.Health ?? 0, _controller?.Position.X ?? 0, _controller?.Position.Y ?? 0, _controller?.Position.Z ?? 0,
            _entities.Units.Count(), AuraFingerprint(player), AuraFingerprint(selection), inventory,
            false, false, false, false, false, false, false, visual.Precast, visual.Cast, visual.Missile, visual.Impact,
            visual.Instances, "PENDING", string.Join('|', active), string.Join('|', sources), verdict,
            "PENDING", _serverGmMode.HasValue ? (_serverGmMode.Value ? "ON" : "OFF") : "UNMEASURED",
            "RENDERER_MIXER_POST_TICK");
        _animationSequenceSamples.Add(row);
        _verdicts.Add(row);
        Console.WriteLine($"[verdict:spell-animation-sequence] {row.ToLine()}");
        string framePath = Path.Combine(ResolveLiveOutputDirectory(), $"frame-{frame}.png");
        TrySaveAnimationSequenceFrame(framePath);
        return true;
    }

    private bool EndAnimationSequence()
    {
        if (_animationSequenceSpell == 0 || _animationSequenceSamples.Count == 0) return false;
        int count = _animationSequenceSamples.Count;
        bool changed = _animationSequenceSamples.Select(row =>
            (row.PlayedAnimationId, row.RendererState, row.ActionAnimation, row.HoldAnimation)).Distinct().Count() > 1;
        bool missing = _animationSequenceSamples.Any(row => row.AnimationVerdict == "ANIM-ASSET-MISSING");
        bool fallback = _animationSequenceSamples.Any(row => row.AnimationVerdict == "ANIM-FALLBACK");
        bool exact = _animationSequenceSamples.Any(row => row.AnimationVerdict == "ANIM-EXACT");
        string coverage = count >= 14 ? "MEASURED" : "NOT-INSTRUMENTED";
        string verdict = coverage == "NOT-INSTRUMENTED" ? "ANIM-NOT-INSTRUMENTED" : missing ?
            "ANIM-ASSET-MISSING" : fallback ? "ANIM-FALLBACK" : exact && changed ? "ANIM-EXACT" : "ANIM-STATIC";
        bool moving = _animationSequenceSamples.Any(row => row.Moving);
        bool crossfade = _animationSequenceSamples.Any(row => row.BlendWeight > .001f && row.BlendWeight < .999f);
        string blend = !moving ? "N/A" : crossfade ? "BLEND-CROSSFADE" : "BLEND-HARDCUT";
        _spellCatalog!.TryGet(_animationSequenceSpell, out SpellInfo visualSpell);
        string Aggregate(string stage, Func<LayerVisualSample, string> value)
        {
            string[] expectedPaths = ExpectedVisualPaths(visualSpell, stage);
            if (expectedPaths.Length == 0) return "ABSENT";
            string[] values = _animationVisualSamples.Select(value).ToArray();
            if (values.Contains("ASSET-MISSING")) return "ASSET-MISSING";
            if (!values.Contains("PRESENT")) return "ABSENT";
            if (stage != "MISSILE") return "PRESENT";
            int positions = _animationVisualSamples.SelectMany(sample => sample.MissilePositions)
                .Select(position => ($"{position.X:F2}", $"{position.Y:F2}", $"{position.Z:F2}")).Distinct().Count();
            return positions >= 2 ? "PRESENT" : "ABSENT";
        }
        string precastVisual = Aggregate("PRECAST", sample => sample.Precast);
        string castVisual = Aggregate("CAST", sample => sample.Cast);
        string missileVisual = Aggregate("MISSILE", sample => sample.Missile);
        string impactVisual = Aggregate("IMPACT", sample => sample.Impact);
        if (coverage == "NOT-INSTRUMENTED")
            precastVisual = castVisual = missileVisual = impactVisual = "NOT-INSTRUMENTED";
        string[] required = [precastVisual, castVisual, missileVisual, impactVisual];
        bool assetVisualMissing = required.Contains("ASSET-MISSING");
        bool anyExpectedVisual = new[] { "PRECAST", "CAST", "MISSILE", "IMPACT" }
            .Any(stage => ExpectedVisualPaths(visualSpell, stage).Length > 0);
        bool resolvedNotDrawn = new[] { "PRECAST", "CAST", "MISSILE", "IMPACT" }.Select((stage, index) =>
            (Expected: ExpectedVisualPaths(visualSpell, stage).Length > 0, Status: required[index]))
            .Any(item => item.Expected && item.Status != "PRESENT");
        string spellVisualVerdict = coverage == "NOT-INSTRUMENTED" ? "SPELL-VISUAL-NOT-INSTRUMENTED" :
            !anyExpectedVisual ? "SPELL-VISUAL-ABSENT" : assetVisualMissing ? "SPELL-VISUAL-ASSET-MISSING" :
            resolvedNotDrawn ? "VISUAL-RESOLVED-NOT-DRAWN" : "SPELL-VISUAL-PRESENT";
        SpellAnimationSequenceVerdict last = _animationSequenceSamples[^1];
        SpellAnimationSequenceVerdict first = _animationSequenceSamples[0];
        string activeModels = string.Join('|', _animationSequenceSamples.SelectMany(row =>
                row.ActiveModels.Split('|', StringSplitOptions.RemoveEmptyEntries))
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(path => path, StringComparer.OrdinalIgnoreCase));
        string assetSources = string.Join('|', new[] { "PRECAST", "CAST", "MISSILE", "IMPACT" }
            .SelectMany(stage => ExpectedVisualPaths(visualSpell, stage))
            .Select(path => _mpq?.ReadFileWithSupplier(path) is { } source ?
                $"{source.Supplier}:{path}" : $"MISSING:{path}")
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(path => path, StringComparer.OrdinalIgnoreCase));
        var summary = last with { Time = NowSeconds(), RowKind = "CELL", Coverage = coverage,
            SampleIndex = count, Frame = "ALL", AnimationVerdict = verdict, BlendVerdict = blend,
            HealthChanged = _animationSequenceSamples.Any(row => row.PlayerHealth != first.PlayerHealth),
            TargetHealthChanged = _animationSequenceSamples.Any(row => row.SelectionHealth != first.SelectionHealth),
            PositionChanged = _animationSequenceSamples.Any(row => Math.Abs(row.PlayerX - first.PlayerX) > .01f ||
                Math.Abs(row.PlayerY - first.PlayerY) > .01f || Math.Abs(row.PlayerZ - first.PlayerZ) > .01f),
            UnitCountChanged = _animationSequenceSamples.Any(row => row.UnitCount != first.UnitCount),
            AuraChanged = _animationSequenceSamples.Any(row => row.PlayerAuras != first.PlayerAuras ||
                row.SelectionAuras != first.SelectionAuras),
            InventoryChanged = _animationSequenceSamples.Any(row => row.InventoryFingerprint != first.InventoryFingerprint),
            PowerChanged = _animationSequenceSamples.Any(row => row.PlayerPower != first.PlayerPower),
            PrecastVisual = precastVisual, CastVisual = castVisual, MissileVisual = missileVisual,
            ImpactVisual = impactVisual, VisualInstances = string.Join("||", _animationVisualSamples
                .Select(sample => sample.Instances).Where(value => value.Length > 0).Distinct()),
            SpellVisualVerdict = spellVisualVerdict,
            ActiveModels = activeModels, AssetSources = assetSources,
            Source = "DERIVED_FROM_SEQUENCE_SAMPLES" };
        _verdicts.Add(summary);
        Console.WriteLine($"[verdict:spell-animation-sequence] {summary.ToLine()}");
        _animationSequenceSpell = 0;
        _animationSequenceSamples.Clear();
        return true;
    }

    private LayerVisualSample SampleSpellVisualLayer(SpellInfo spell)
    {
        double now = NowSeconds();
        var instances = _spellEffects?.Snapshot(spell.Id, now, SpellEffectUnitPose) ?? [];
        string Status(string stage)
        {
            string[] expected = ExpectedVisualPaths(spell, stage);
            if (expected.Length == 0) return "ABSENT";
            if (expected.Any(path => _mpq?.ReadFileWithSupplier(path) is null)) return "ASSET-MISSING";
            foreach (var instance in instances.Where(instance => instance.Stage == stage))
            {
                var particles = _particles?.VisualState($"spell:{instance.Path}#{instance.Id}") ?? default;
                bool drawn = particles.DrawnParticles > 0 || (_spellEffectMeshes?.WasDrawn(instance.Path) ?? false) ||
                    (_spellRibbons?.WasDrawn(instance.Path) ?? false);
                if (drawn) return "PRESENT";
            }
            return "ABSENT";
        }
        string details = string.Join('|', instances.Select(instance =>
        {
            var particles = _particles?.VisualState($"spell:{instance.Path}#{instance.Id}") ?? default;
            bool mesh = _spellEffectMeshes?.WasDrawn(instance.Path) ?? false;
            bool ribbon = _spellRibbons?.WasDrawn(instance.Path) ?? false;
            return $"{instance.Stage}:{instance.Path}#{instance.Id}@{instance.Position.X:F2}/{instance.Position.Y:F2}/{instance.Position.Z:F2}" +
                $":progress={instance.Progress:F3}:pools={particles.Pools}:live={particles.LiveParticles}:drawn={particles.DrawnParticles}:mesh={mesh}:ribbon={ribbon}";
        }));
        return new(Status("PRECAST"), Status("CAST"), Status("MISSILE"), Status("IMPACT"), details,
            instances.Where(instance => instance.Missile).Select(instance => instance.Position).ToArray());
    }

    private string[] ExpectedVisualPaths(SpellInfo spell, string stage)
    {
        if (_spellVisualCatalog?.TryGetStages(spell.VisualId, out SpellVisualStages stages) != true) return [];
        if (stage == "MISSILE") return _spellVisualCatalog.MissilePath(stages) is { } missile ?
            [NormalizeModelPath(missile)] : [];
        uint kitId = stage switch { "PRECAST" => stages.Precast, "CAST" => stages.Cast,
            "IMPACT" => stages.Impact, _ => 0 };
        return kitId != 0 && _spellVisualCatalog.TryGetKit(kitId, out SpellVisualKitInfo kit) ?
            kit.Effects.Select(effect => NormalizeModelPath(effect.ModelPath)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() : [];
    }

    private (uint KitId, ushort? Animation, IReadOnlyList<SpellVisualKitEffect> Effects)
        ExpectedStage(in SpellInfo spell, string stage)
    {
        if (_spellVisualCatalog?.TryGetStages(spell.VisualId, out SpellVisualStages stages) != true)
            return (0, null, []);
        uint kitId = stage switch { "PRECAST" => stages.Precast, "CHANNEL" => stages.Channel,
            "IMPACT" => stages.Impact, "STATE" => stages.State, _ => stages.Cast };
        return kitId != 0 && _spellVisualCatalog.TryGetKit(kitId, out SpellVisualKitInfo kit)
            ? (kitId, kit.AnimationId, kit.Effects) : (kitId, null, []);
    }

    private static string NormalizeModelPath(string rawPath)
    {
        string path = rawPath.Replace('/', '\\');
        return path.EndsWith(".mdx", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase) ? path[..^4] + ".m2" : path;
    }

    private string ResolveLiveOutputDirectory() => _liveRunOptions is null ?
        Path.Combine(_config.RepoRoot, "dumps") :
        Path.GetFullPath(Path.IsPathRooted(_liveRunOptions.OutputDirectory) ? _liveRunOptions.OutputDirectory :
            Path.Combine(_config.RepoRoot, _liveRunOptions.OutputDirectory));
}
