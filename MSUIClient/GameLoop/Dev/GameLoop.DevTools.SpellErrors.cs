using MSUIClient.Engine;
using MSUIClient.Formats;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private void ShowSpellError(uint spellId, string reason, string text, string source)
    {
        bool displayed = text.Length > 0;
        if (displayed) PushCenterText(text, CenterCombatTextStyle.Damage);
        SpellInfo? info = _spellCatalog?.TryGet(spellId, out SpellInfo found) == true ? found : null;
        var verdict = new SpellErrorVerdict(NowSeconds(), _net?.PlayerName ?? "", spellId,
            info?.Name ?? $"Spell {spellId}", reason, text, displayed, source);
        _verdicts.Add(verdict);
        Console.WriteLine($"[verdict:spell-error] {verdict.ToLine()}");
    }

    private void ApplySpellCastFailureResult(uint spellId, byte reason)
    {
        FailRealPortalCastPrewarmResult(spellId);
        string name = SpellCastResultNames.Name(reason);
        EmitSpellServerResult(spellId, name);
        string power = _spellCatalog?.TryGet(spellId, out SpellInfo spell) == true
            ? PowerName((byte)spell.PowerType) : "POWER";
        string text = SpellCastResultNames.Text(reason, power);
        ShowSpellError(spellId, name, text, "SMSG_CAST_RESULT");
        ObserveProfessionSpellFailure(spellId, name);
        ApplySpellFailure(_net?.PlayerGuid ?? 0, spellId,
            reason is 0x23 or 0x24 ? "INTERRUPTED" : text.Length > 0 ? text : "FAILED");
    }
}
