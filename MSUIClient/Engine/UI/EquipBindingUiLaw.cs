using System.Numerics;

namespace MSUIClient.Engine.UI;

/// <summary>Build-5875 equip binding predicates and StaticPopup geometry.</summary>
public static class EquipBindingUiLaw
{
    public const string EquipType = "EQUIP_BIND";
    public const string AutoEquipType = "AUTOEQUIP_BIND";
    public const string Text = "Equipping this item will bind it to you.";
    public const float Width = StaticPopupCoordinatorLaw.BaseWidth;
    public const float TextWidth = StaticPopupCoordinatorLaw.TextWidth;
    public static bool IsType(string type) => type is EquipType or AutoEquipType;
    public static bool NeedsConfirmation(uint bonding, bool bound, bool usable) =>
        bonding == 2 && !bound && usable;
    public static bool IsEquipPosition(InventoryUiLaw.WirePosition position) =>
        position.Bag == 255 && (position.Slot <= 22 || position.Slot is >= 63 and <= 68);

    public static (int Container, int Slot)? FromWire(byte bag, byte slot) => bag switch
    {
        255 when slot <= 22 => (InventoryUiLaw.EquipmentContainer, slot),
        255 when slot <= 38 => (0, slot - 23),
        255 when slot <= 62 => (InventoryUiLaw.BankContainer, slot - 39),
        255 when slot <= 68 => (InventoryUiLaw.BankBagEquipmentContainer, slot - 63),
        255 when slot is >= 81 and <= 96 => (InventoryUiLaw.KeyringContainer, slot - 81),
        >= 19 and <= 22 when slot < 36 => (bag - 18, slot),
        >= 63 and <= 68 when slot < 36 => (bag - 63 + 5, slot),
        _ => null,
    };

    public static StaticPopupCoordinatorLaw.Definition Definition(bool autoEquip) => new(
        autoEquip ? AutoEquipType : EquipType, WhileDead: true, HideOnEscape: true,
        HasAccept: true, HasCancel: true, HasOnHide: true, Exclusive: true);

    public readonly record struct PopupLayout(Vector2 Size, Vector2 TextCenter,
        StaticPopupCoordinatorLaw.Rect Accept, StaticPopupCoordinatorLaw.Rect Cancel);
    public static PopupLayout Layout(float textHeight) => new(
        new(Width, StaticPopupCoordinatorLaw.Height(textHeight, 20)),
        new(Width / 2, StaticPopupCoordinatorLaw.TextTop),
        new(Width / 2 - 134, StaticPopupCoordinatorLaw.TextTop + textHeight + 8, 128, 20),
        new(Width / 2 + 7, StaticPopupCoordinatorLaw.TextTop + textHeight + 8, 128, 20));

    public static (int Slot, StaticPopupCoordinatorLaw.Instance Instance)? Visible(
        StaticPopupCoordinatorLaw.Slots slots)
    {
        if (slots.First is { } first && IsType(first.Definition.Type)) return (1, first);
        if (slots.Second is { } second && IsType(second.Definition.Type)) return (2, second);
        return null;
    }
}
