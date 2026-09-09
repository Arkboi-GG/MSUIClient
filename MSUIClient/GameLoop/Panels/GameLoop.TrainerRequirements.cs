using MSUIClient.Engine.UI;
using MSUIClient.Formats;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private sealed record TrainerConfirmation(ulong Actor, ulong Trainer, uint Spell);
    private TrainerConfirmation? _trainerConfirmation;

    private IReadOnlyList<TrainerServiceUiLaw.Requirement> TrainerRequirements(TrainerSpell row)
    {
        WorldEntity? actor = _entities.TryGet(ControlledGuid, out WorldEntity found) && found.IsPlayer ? found : null;
        var requirements = TrainerServiceUiLaw.Requirements(row, actor?.Level,
            id => actor?.Fields.PlayerSkillValueBase(id),
            id => actor is null ? null : ActionsFor(ControlledGuid).KnownSpells.Contains(id),
            id => _skillLines?.TryGet(id, out SkillLineInfo skill) == true ? skill.Name : null,
            id => _spellCatalog?.TryGet(id, out SpellInfo spell) == true
                ? string.IsNullOrWhiteSpace(spell.Rank) ? spell.Name : $"{spell.Name} ({spell.Rank})" : null);
        if (row.PrimaryProfessionDialog && actor?.Fields.GetU32(ObjectFields.PLAYER_CHARACTER_POINTS2) is uint free)
            return requirements.Append(new TrainerServiceUiLaw.Requirement($"Primary profession slots available: {free}", free > 0)).ToArray();
        return requirements;
    }

    private bool ShowTrainerConfirmation(TrainerSpell spell)
    {
        if (_trainer is null || !_entities.TryGet(ControlledGuid, out WorldEntity actor) || actor.IsDead) return false;
        CancelTrainerConfirmation();
        _trainerConfirmation = new(ControlledGuid, _trainer.TrainerGuid, spell.ServiceSpellId);
        ExecuteStaticPopupPlan(StaticPopupCoordinatorLaw.Show(_staticPopupSlots,
            TrainerServiceUiLaw.Confirmation, actor.IsDead));
        return DuelFrameUiLaw.Visible(_staticPopupSlots, TrainerServiceUiLaw.PopupType) is not null;
    }

    private string TrainerConfirmationText()
    {
        uint id = _trainerConfirmation?.Spell ?? 0;
        string name = _spellCatalog?.TryGet(id, out SpellInfo spell) == true ? spell.Name : "this profession";
        uint? free = _entities.TryGet(_trainerConfirmation?.Actor ?? 0, out WorldEntity actor)
            ? actor.Fields.GetU32(ObjectFields.PLAYER_CHARACTER_POINTS2) : null;
        string format = free is >= 2
            ? InventoryGlobalString("PROFESSION_CONFIRMATION1", "You may only know two professions at any one time. Would you like to learn %s as your first one?")
            : free == 1 ? InventoryGlobalString("PROFESSION_CONFIRMATION2", "You may only know two professions at any one time. Would you like to learn %s as your second one?")
            : "You may only know two professions at any one time. Would you like to learn %s?";
        return format.Replace("|cffffd200", "").Replace("|r", "").Replace("%s", name);
    }

    private void CancelTrainerConfirmation()
    {
        _trainerConfirmation = null;
        ExecuteStaticPopupPlan(StaticPopupCoordinatorLaw.HideByType(_staticPopupSlots, TrainerServiceUiLaw.PopupType));
    }

    private void UpdateTrainerConfirmation()
    {
        if (_trainerConfirmation is not { } pending) return;
        if (pending.Actor != ControlledGuid || _trainer?.TrainerGuid != pending.Trainer ||
            DuelFrameUiLaw.Visible(_staticPopupSlots, TrainerServiceUiLaw.PopupType) is null)
            CancelTrainerConfirmation();
        else UpdateTrainerLifecycle();
    }

    private void AcceptTrainerConfirmation()
    {
        TrainerConfirmation? pending = _trainerConfirmation;
        _trainerConfirmation = null;
        if (pending is not null && pending.Actor == ControlledGuid && _trainer?.TrainerGuid == pending.Trainer)
            BuyTrainerSpell(pending.Spell, confirmed: true);
    }
}
