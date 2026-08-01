using MSUIClient.Engine;
using MSUIClient.Formats;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private uint _animationSequenceSpell;
    private string _animationSequenceCell = "";
    private string _animationSequenceStage = "SETTLE";
    private readonly List<SpellAnimationSequenceVerdict> _animationSequenceSamples = [];

    private bool BeginAnimationSequence(uint spellId, string cell)
    {
        if (_spellCatalog?.TryGet(spellId, out _) != true || _character is null) return false;
        _animationSequenceSpell = spellId;
        _animationSequenceCell = cell.ToUpperInvariant();
        _animationSequenceStage = "SETTLE";
        _animationSequenceSamples.Clear();
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
        (uint kitId, ushort? expected, IReadOnlyList<(ushort AttachmentId, string ModelPath)> effects) =
            ExpectedStage(spell, _animationSequenceStage);
        var sources = new List<string>();
        bool assetMissing = false;
        foreach (var (_, rawPath) in effects)
        {
            string path = NormalizeModelPath(rawPath);
            var supplier = _mpq?.ReadFileWithSupplier(path);
            if (supplier is null) { sources.Add($"MISSING:{path}"); assetMissing = true; }
            else sources.Add($"{supplier.Value.Supplier}:{path}");
        }
        IReadOnlyList<string> active = _spellEffects?.ActiveModelPaths(_animationSequenceSpell) ?? [];
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
            _character.GroundSpeed > .3f, string.Join('|', active), string.Join('|', sources), verdict,
            "PENDING", _serverGmMode.HasValue ? (_serverGmMode.Value ? "ON" : "OFF") : "UNMEASURED",
            "RENDERER_MIXER_POST_TICK");
        _animationSequenceSamples.Add(row);
        _verdicts.Add(row);
        Console.WriteLine($"[verdict:spell-animation-sequence] {row.ToLine()}");
        _currentVantage = frame;
        _gameplayDumpDirectoryOverride = ResolveLiveOutputDirectory();
        ArmGameplayDump();
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
        SpellAnimationSequenceVerdict last = _animationSequenceSamples[^1];
        var summary = last with { Time = NowSeconds(), RowKind = "CELL", Coverage = coverage,
            SampleIndex = count, Frame = "ALL", AnimationVerdict = verdict, BlendVerdict = blend,
            Source = "DERIVED_FROM_SEQUENCE_SAMPLES" };
        _verdicts.Add(summary);
        Console.WriteLine($"[verdict:spell-animation-sequence] {summary.ToLine()}");
        _animationSequenceSpell = 0;
        _animationSequenceSamples.Clear();
        return true;
    }

    private (uint KitId, ushort? Animation, IReadOnlyList<(ushort AttachmentId, string ModelPath)> Effects)
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
