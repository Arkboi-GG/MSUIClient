using System.Numerics;
using ImGuiNET;
using MSUIClient.Engine.UI;
using MSUIClient.Formats;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private bool IsBeastTraining => _professionPanelKind == ProfessionPanelKind.Craft &&
        _professionCraftType == ProfessionFrameUiLaw.CraftTypeBeastTraining;
    private readonly record struct PetTrainingOffer(uint RequiredLevel, int Cost, bool Known, bool Ready);

    private WorldEntity? TrainingPet => _entities.TryGet(_petGuid, out WorldEntity pet) &&
        pet.Fields.SummonedBy == ControlledGuid ? pet : null;

    private PetTrainingOffer PetTraining(ProfessionRecipe recipe)
    {
        if (_skillLines is null || _spellCatalog?.TryGet(recipe.SpellId, out SpellInfo teacher) != true)
            return default;
        uint ability = PetTrainingUiLaw.TaughtAbility(teacher.EffectIds, teacher.EffectTriggerSpells);
        if (ability == 0) return new(teacher.SpellLevel, 0, false, false);
        uint chain = _skillLines.AbilityChainRoot(ability);
        uint spent = 0;
        bool known = false;
        foreach (uint packed in _petBookSpells)
        {
            uint existing = PetSpellBookUiLaw.SpellId(packed);
            if (_skillLines.AbilityChainRoot(existing) != chain) continue;
            spent = Math.Max(spent, _skillLines.TrainingPointCost(existing));
            known |= existing == ability || (_spellCatalog.TryGet(existing, out SpellInfo learned) &&
                _spellCatalog.TryGet(ability, out SpellInfo requested) && learned.SpellLevel >= requested.SpellLevel);
        }
        int cost = (int)_skillLines.TrainingPointCost(ability) - (int)spent;
        WorldEntity? pet = TrainingPet;
        int points = PetPaperDollUiLaw.AvailableTrainingPoints(pet?.Fields.PetTrainingPoints ?? 0);
        bool ready = PetTrainingUiLaw.CanTrain(pet is { IsDead: false }, pet?.Fields.Level ?? 0,
            teacher.SpellLevel, points, cost, known);
        return new(teacher.SpellLevel, cost, known, ready);
    }

    private void DrawPetTrainingPoints(ImDrawListPtr draw, Vector2 origin, float scale)
    {
        WorldEntity? pet = TrainingPet;
        if (PetPaperDollUiLaw.TotalTrainingPoints(pet?.Fields.Level ?? 0,
                pet?.Fields.PetLoyaltyLevel ?? 0) == 0) return;
        string value = PetPaperDollUiLaw.AvailableTrainingPoints(pet?.Fields.PetTrainingPoints ?? 0).ToString();
        float valueWidth = GameText.MeasureWidth("GameFontHighlightSmall", value, scale);
        float pitch = GameText.LinePitch("GameFontHighlightSmall", scale);
        Vector2 at = PetTrainingUiLaw.PointsPosition(origin, scale, valueWidth, pitch);
        GameText.Draw(draw, "GameFontHighlightSmall", value, at, scale);
        string label = InventoryGlobalString("TRAINING_POINTS", "Training Points:");
        GameText.Draw(draw, "GameFontNormalSmall", label, PetTrainingUiLaw.PointsLabelPosition(at,
            GameText.MeasureWidth("GameFontNormalSmall", label, scale), scale), scale, VanillaGold);
    }
}
