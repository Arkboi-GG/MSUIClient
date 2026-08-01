using MSUIClient.Engine;
using MSUIClient.Formats;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private bool PresentSpellAnimation(uint spellId, string stage, string source)
    {
        if (_spellCatalog?.TryGet(spellId, out SpellInfo info) != true || _character is null)
            return false;
        if (_spellVisualCatalog?.TryGetStages(info.VisualId, out SpellVisualStages stages) != true)
            return false;
        uint kitId = stage.ToLowerInvariant() switch
        {
            "precast" => stages.Precast,
            "cast" => stages.Cast,
            "channel" => stages.Channel,
            _ => 0,
        };
        if (kitId == 0 || _spellVisualCatalog.TryGetKit(kitId, out SpellVisualKitInfo kit) != true)
            return false;
        if (stage.Equals("precast", StringComparison.OrdinalIgnoreCase) ||
            stage.Equals("channel", StringComparison.OrdinalIgnoreCase))
            _character.BeginSpellVisual(kit.AnimationId);
        else
            _character.ReleaseSpellVisual(kit.AnimationId);
        EmitSpellAnimation(info, stage.ToUpperInvariant(), kitId, kit.AnimationId, source);
        return true;
    }

    private void EmitSpellAnimation(in SpellInfo info, string stage, uint kitId,
        ushort? authoredAnimation, string source)
    {
        int track = stage is "PRECAST" or "CHANNEL" ? 2 : 1;
        (int Requested, int Played, AnimChoiceKind Kind) state =
            _lastAnimChoices.TryGetValue(("player", track), out var found)
                ? found : (-1, -1, AnimChoiceKind.Missing);
        var verdict = new SpellAnimationVerdict(NowSeconds(), _net?.PlayerName ?? "",
            info.Id, info.Name, SchoolName(info.School), stage, kitId,
            authoredAnimation ?? -1, state.Requested, state.Played, state.Kind,
            _character?.CurrentPresentationAnimation ?? "none", source);
        _verdicts.Add(verdict);
        Console.WriteLine($"[verdict:spell-animation] {verdict.ToLine()}");
    }
}
