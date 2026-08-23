using MSUIClient.Formats;

namespace MSUIClient.Engine.UI;

/// <summary>The build-5875 DropItemOnUnit("pet") ownership/provenance gates.</summary>
public static class FeedPetLaw
{
    public const uint FeedPetEffect = 0x65;

    // The original client latches only Effect[0], not any matching secondary lane.
    public static bool IsFeedPetEffects(IReadOnlyList<uint>? effects) =>
        effects is { Count: > 0 } && effects[0] == FeedPetEffect;

    public static bool IsFeedPetSpell(in SpellInfo spell) =>
        IsFeedPetEffects(spell.EffectIds);

    public static bool CanFeed(ulong pickedGuid, ulong petGuid, uint createdBySpell,
        ulong? createdBy, ulong selfGuid, uint feedPetSpell, ulong heldItemGuid) =>
        pickedGuid != 0 && pickedGuid == petGuid && createdBySpell != 0 &&
        selfGuid != 0 && createdBy == selfGuid && feedPetSpell != 0 && heldItemGuid != 0;
}
