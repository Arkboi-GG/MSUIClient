namespace MSUIClient.Engine.UI;

public enum EquipmentDisplayPreference
{
    Helm,
    Cloak,
}

/// <summary>
/// Build-5875's server-owned Show Helm / Show Cloak contract. The public PLAYER_FLAGS bits are
/// the rendering truth; the Options binding keeps a separate optimistic belief because the two
/// client packets are empty-bodied flips rather than target-state writes.
/// </summary>
public static class EquipmentDisplayPreferenceLaw
{
    public const uint HideHelm = 0x400;
    public const uint HideCloak = 0x800;
    public const int HeadEquipmentSlot = 0;
    public const int CloakEquipmentSlot = 14;
    public const int HeadInventoryType = 1;
    public const int CloakInventoryType = 16;

    public static bool HelmShown(uint playerFlags) => (playerFlags & HideHelm) == 0;
    public static bool CloakShown(uint playerFlags) => (playerFlags & HideCloak) == 0;

    public static bool EquipmentSlotShown(int slot, uint playerFlags) => slot switch
    {
        HeadEquipmentSlot => HelmShown(playerFlags),
        CloakEquipmentSlot => CloakShown(playerFlags),
        _ => true,
    };

    public static bool InventoryTypeShown(int inventoryType, uint playerFlags) =>
        inventoryType switch
        {
            HeadInventoryType => HelmShown(playerFlags),
            CloakInventoryType => CloakShown(playerFlags),
            _ => true,
        };

    // An explicitly tried-on piece wins over the worn-display preference. Only the base worn
    // piece is suppressed in the Dressing Room.
    public static bool DressUpPieceShown(
        int equipmentSlot, bool explicitlyTriedOn, uint playerFlags) =>
        explicitlyTriedOn || EquipmentSlotShown(equipmentSlot, playerFlags);
}

public sealed class EquipmentDisplayPreferenceController
{
    private uint? _observedPlayerFlags;

    public bool HelmShown { get; private set; } = true;
    public bool CloakShown { get; private set; } = true;

    /// <summary>
    /// Accept descriptor truth only on its first observation or a real PLAYER_FLAGS edge. Repeating
    /// the same wire value must not erase an optimistic checkbox change during the round trip.
    /// </summary>
    public bool Observe(uint playerFlags)
    {
        if (_observedPlayerFlags == playerFlags) return false;
        _observedPlayerFlags = playerFlags;
        HelmShown = EquipmentDisplayPreferenceLaw.HelmShown(playerFlags);
        CloakShown = EquipmentDisplayPreferenceLaw.CloakShown(playerFlags);
        return true;
    }

    /// <summary>Return the empty-bodied flip to send, or null when the requested state is held.</summary>
    public EquipmentDisplayPreference? Request(
        EquipmentDisplayPreference preference, bool shown)
    {
        switch (preference)
        {
            case EquipmentDisplayPreference.Helm:
                if (HelmShown == shown) return null;
                HelmShown = shown;
                return preference;
            case EquipmentDisplayPreference.Cloak:
                if (CloakShown == shown) return null;
                CloakShown = shown;
                return preference;
            default:
                return null;
        }
    }
}
