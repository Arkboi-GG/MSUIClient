using MSUIClient.Net;

namespace MSUIClient.Engine.UI;

public static class TrainerServiceUiLaw
{
    public const string PopupType = "MSUI_TRAIN_PRIMARY_PROFESSION";
    public static readonly StaticPopupCoordinatorLaw.Definition Confirmation = new(
        PopupType, HideOnEscape: true, HasAccept: true, HasCancel: true);
    public readonly record struct Requirement(string Text, bool? Satisfied)
    {
        public uint Color => Satisfied == false ? 0xff2020ff : 0xffffffff;
    }
    public static bool CanPurchase(TrainerSpell row, uint money) =>
        row.ServiceSpellId != 0 && row.State == TrainerFrameUiLaw.AvailableState && money >= row.Cost &&
        (!row.PrimaryProfessionDialog || row.PrimaryProfessionLearnable);

    public static IReadOnlyList<Requirement> Requirements(TrainerSpell row, uint? level,
        Func<uint, uint?> skillValue, Func<uint, bool?> knowsSpell,
        Func<uint, string?> skillName, Func<uint, string?> spellName)
    {
        List<Requirement> result = [];
        if (row.RequiredLevel > 0)
            result.Add(new($"Requires level {row.RequiredLevel}", level.HasValue ? level >= row.RequiredLevel : null));
        if (row.RequiredSkill != 0)
        {
            uint? value = skillValue(row.RequiredSkill);
            string name = skillName(row.RequiredSkill) ?? "the required skill";
            result.Add(new($"Requires {name} ({row.RequiredSkillValue})", value.HasValue ? value >= row.RequiredSkillValue : null));
        }
        foreach (uint id in new[] { row.RequiredSpell1, row.RequiredSpell2, row.RequiredSpell3 }.Where(x => x != 0).Distinct())
            result.Add(new($"Requires {spellName(id) ?? "a prerequisite spell"}", knowsSpell(id)));
        if (row.PrimaryProfessionDialog && !row.PrimaryProfessionLearnable)
            result.Add(new("You can only learn two primary professions.", false));
        return result;
    }
}
