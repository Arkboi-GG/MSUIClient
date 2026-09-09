using MSUIClient.Engine.UI;
using MSUIClient.Formats;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private bool? _combatTextInCombat;
    private bool _combatTextLowHealth;
    private bool _combatTextLowMana;
    private bool _combatTextAuraBaselineReady;

    private void ResetCombatTextState()
    {
        _combatTextInCombat = null;
        _combatTextLowHealth = false;
        _combatTextLowMana = false;
        _combatTextAuraBaselineReady = false;
    }

    private void ObservePlayerCombatTextState(WorldEntity player)
    {
        CombatTextStateCue? combat = CombatTextStateUiLaw.CombatState(
            _combatTextInCombat, player.InCombat);
        _combatTextInCombat = player.InCombat;
        if (Settings.Controls.ShowCombatStateText && combat is { } combatCue)
            QueueCombatTextState(combatCue);

        CombatTextResourceTransition health = CombatTextStateUiLaw.Resource(
            _combatTextLowHealth, player.Fields.Health, player.Fields.MaxHealth);
        _combatTextLowHealth = health.Latched;
        if (health.Warn)
            QueueCombatTextState(new("Health Low", CombatTextStateTone.Red));

        bool mana = player.Fields.PowerType == 0;
        CombatTextResourceTransition power = CombatTextStateUiLaw.Resource(
            _combatTextLowMana, player.Fields.ActivePower, player.Fields.ActiveMaxPower, mana);
        _combatTextLowMana = power.Latched;
        if (power.Warn)
            QueueCombatTextState(new("Mana Low", CombatTextStateTone.Red));
    }

    private void ObservePlayerAuraCombatText(AuraSnapshot aura, bool applied)
    {
        if (!_combatTextAuraBaselineReady) return;
        SpellInfo? spell = _spellCatalog?.TryGet(aura.SpellId, out SpellInfo found) == true
            ? found : null;
        string name = spell?.Name ?? $"Spell {aura.SpellId}";
        CombatTextStateCue? cue = CombatTextStateUiLaw.Aura(
            name, aura.Helpful, applied, hidden: spell?.HiddenClientSide == true);
        if (cue is { } visible) QueueCombatTextState(visible);
    }

    private void CompletePlayerAuraCombatTextBaseline() => _combatTextAuraBaselineReady = true;

    private void QueueCombatTextState(CombatTextStateCue cue) =>
        QueueCenterCombatText(cue.Text,
            cue.Tone == CombatTextStateTone.Green
                ? CenterCombatTextStyle.Heal : CenterCombatTextStyle.Damage);
}
