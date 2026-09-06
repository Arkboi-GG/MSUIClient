using System.Numerics;
using ImGuiNET;
using MSUIClient.Engine;
using MSUIClient.Engine.UI;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private sealed record PetUnlearnContext(ulong Actor, ulong Trainer, ulong Pet);
    private PetUnlearnContext? _petUnlearnSelection, _petUnlearnCompleted, _petUnlearnConfirmation;
    private uint _petUnlearnCost;

    private void ResetPetUnlearn()
    {
        _petUnlearnSelection = _petUnlearnCompleted = _petUnlearnConfirmation = null;
        _petUnlearnCost = 0;
        ExecuteStaticPopupPlan(StaticPopupCoordinatorLaw.HideByType(_staticPopupSlots, PetUnlearnUiLaw.PopupType));
    }

    private void RememberPetUnlearnSelection(ulong trainer)
    {
        ResetPetUnlearn();
        if (_entities.TryGet(ControlledGuid, out WorldEntity actor) && actor.IsPlayer &&
            actor.Fields.Bytes0.Class == 3 && actor.Fields.Summon is ulong pet && pet != 0)
            _petUnlearnSelection = new(ControlledGuid, trainer, pet);
    }

    // Core closes gossip immediately before its pet quote. Preserve only the selected
    // conversation across that close; ordinary resets/new menus invalidate it.
    private void CompleteGossip()
    {
        PetUnlearnContext? selected = _petUnlearnSelection;
        bool current = selected is not null && selected.Actor == _gossipOwnerGuid &&
            selected.Trainer == _gossipMenu?.SourceGuid;
        ResetGossip();
        if (current) _petUnlearnCompleted = selected;
    }

    private bool PetUnlearnContextCurrent(PetUnlearnContext context)
    {
        return context.Actor == ControlledGuid &&
            _entities.TryGet(context.Actor, out WorldEntity actor) && actor.IsPlayer && !actor.IsDead &&
            actor.Fields.Bytes0.Class == 3 && actor.Fields.Summon == context.Pet &&
            _entities.TryGet(context.Pet, out WorldEntity pet) && pet.IsUnit && pet.Fields.PetNumber != 0 &&
            TryGetInteractionBodyPose(out WorldBodyPose pose) &&
            _entities.TryGet(context.Trainer, out WorldEntity trainer) && trainer.IsCreature && !trainer.IsDead &&
            NpcSessionUiLaw.InRange(Vector3.DistanceSquared(pose.Position, trainer.Position));
    }

    private void ApplyPetUnlearnConfirm(byte[] body, ulong owner)
    {
        if (owner == 0 || owner != ControlledGuid ||
            !PetUnlearnPacket.TryParse(body, out PetUnlearnPacket quote)) return;
        PetUnlearnContext? context = _petUnlearnCompleted;
        _petUnlearnCompleted = null;
        if (context is null || context.Actor != owner || context.Pet != quote.Pet ||
            !PetUnlearnContextCurrent(context)) return;
        ResetPetUnlearn();
        _petUnlearnConfirmation = context;
        _petUnlearnCost = quote.Cost;
        ExecuteStaticPopupPlan(StaticPopupCoordinatorLaw.Show(_staticPopupSlots,
            PetUnlearnUiLaw.Definition, false, dataToken: quote.Pet.ToString()));
        EmitInterface("pet", "unlearn-quote", "SHOWN", quote.Pet, $"cost={quote.Cost};actor={owner};trainer={context.Trainer}");
    }

    private void UpdatePetUnlearnConfirmation()
    {
        PetUnlearnContext? context = _petUnlearnConfirmation ?? _petUnlearnCompleted ?? _petUnlearnSelection;
        if (context is not null && !PetUnlearnContextCurrent(context)) ResetPetUnlearn();
    }

    private void ApplyPetUnlearnPopupEffect(StaticPopupCoordinatorLaw.Effect effect)
    {
        if (effect.Kind == StaticPopupCoordinatorLaw.EffectKind.Accept)
        {
            PetUnlearnContext? context = _petUnlearnConfirmation;
            _petUnlearnConfirmation = null;
            bool sent = context is not null && PetUnlearnContextCurrent(context) &&
                !RefuseTacticalFreezeLiveCommand("unlearning pet skills") &&
                !RefuseTacticalFrozenActor(context.Pet, "unlearn its skills") &&
                _net?.PetUnlearn(context.Pet) == true;
            EmitInterface("pet", "unlearn", sent ? "SENT" : "REFUSED", context?.Pet ?? 0, "wire=CMSG_PET_UNLEARN");
        }
        else if (effect.Kind == StaticPopupCoordinatorLaw.EffectKind.OnHide)
        {
            _petUnlearnConfirmation = null;
            _petUnlearnCost = 0;
        }
    }

    private string PetUnlearnPromptText() => InventoryGlobalString(PetUnlearnUiLaw.PopupType, PetUnlearnUiLaw.Prompt);

    private void DrawPetUnlearnMoney(ImDrawListPtr draw, Vector2 origin, float textHeight, float scale)
    {
        var denominations = MailUiLaw.Money(_petUnlearnCost);
        float width = denominations.Sum(x => GameText.MeasureWidth("NumberFontNormal", x.Value.ToString(), scale) +
            TrainerFrameUiLaw.MoneyIconSize * scale) + Math.Max(0, denominations.Count - 1) * TrainerFrameUiLaw.MoneyGap * scale;
        DrawTrainerMoney(draw, _petUnlearnCost, origin + PetUnlearnUiLaw.MoneyCenterTop(textHeight) * scale -
            new Vector2(width * .5f, 0), scale, 0xffffffff, rightAligned: false);
    }
}
