using MSUIClient.Formats;

namespace MSUIClient.Engine.UI;

/// <summary>Current Benilla's confirmed acquire/spend audio call sites.</summary>
public static class AcquisitionSoundLaw
{
    public const string CoinCue = "LOOTWINDOWCOINSOUND";
    public const string CoinCategory = "sfx.money";
    public const string ItemPickupCategory = "sfx.item-pickup";
    public const string ItemPutDownCategory = "sfx.item-putdown";

    /// <summary>First observation is a seed; every later purse delta plays in either direction.</summary>
    public static bool PlayCoin(uint? previous, uint? current) =>
        previous.HasValue && current.HasValue && previous.Value != current.Value;

    public static uint? PickupKit(uint displayInfoId, ItemDisplayTable? displays,
        ItemGroupSoundsCatalog? sounds) =>
        GestureKit(displayInfoId, displays, sounds, ItemSoundGesture.Pickup);

    public static uint? GestureKit(uint displayInfoId, ItemDisplayTable? displays,
        ItemGroupSoundsCatalog? sounds, ItemSoundGesture gesture)
    {
        // Unknown displays must not accidentally borrow group zero's authored sound.
        return displays?.Find(displayInfoId) is { } display
            ? sounds?.Kit(display.GroupSoundIndex, gesture) : null;
    }
}
