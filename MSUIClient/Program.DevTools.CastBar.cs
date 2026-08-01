using MSUIClient.Engine;
using MSUIClient.Formats;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private uint _castBarPushbackTotalMs;

    private void EmitCastBarVerdict(string evt, uint spellId, uint serverDurationMs = 0,
        string cancelSource = "NONE")
    {
        SpellInfo? info = _spellCatalog?.TryGet(spellId, out SpellInfo found) == true ? found : null;
        var verdict = new CastBarVerdict(NowSeconds(), _net?.PlayerName ?? "", spellId,
            info?.Name ?? $"Spell {spellId}", evt, info?.CastClassification ?? "UNKNOWN",
            serverDurationMs, info?.CastTimeMs ?? 0, info?.DurationMs ?? 0,
            _castBarPhase.ToString().ToUpperInvariant(), _castBarStarted, _castBarEnds,
            _castBarPushbackTotalMs, cancelSource, _character?.CurrentAnimation ?? "none");
        _verdicts.Add(verdict);
        Console.WriteLine($"[verdict:cast-bar] {verdict.ToLine()}");
    }
}
