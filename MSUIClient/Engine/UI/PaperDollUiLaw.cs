namespace MSUIClient.Engine.UI;

/// <summary>PaperDollFrame equipment, ammo, and model-rotation rules from CharacterFrame.xml.</summary>
public static class PaperDollUiLaw
{
    public enum SlotClickAction { None, PickupOrPlace, Use }

    public const uint White = 0xffffffff;
    public const uint Locked = 0xff666666;
    public const uint Broken = 0xff0000e6;
    public const uint Fits = 0xff00ff00;
    public const float ClickRadians = .12f;
    public const float RotationsPerSecond = .5f;

    public static SlotClickAction ClickAction(bool left, bool right, bool shift, bool control,
        bool ignoreModifiers = false) =>
        left && (ignoreModifiers || !shift && !control) ? SlotClickAction.PickupOrPlace :
        right ? SlotClickAction.Use : SlotClickAction.None;

    public static bool FitsEquipmentSlot(uint inventoryType, int zeroBasedSlot) => inventoryType switch
    {
        1 => zeroBasedSlot == 0,
        2 => zeroBasedSlot == 1,
        3 => zeroBasedSlot == 2,
        4 => zeroBasedSlot == 3,
        5 or 20 => zeroBasedSlot == 4,
        6 => zeroBasedSlot == 5,
        7 => zeroBasedSlot == 6,
        8 => zeroBasedSlot == 7,
        9 => zeroBasedSlot == 8,
        10 => zeroBasedSlot == 9,
        11 => zeroBasedSlot is 10 or 11,
        12 => zeroBasedSlot is 12 or 13,
        13 => zeroBasedSlot is 15 or 16,
        14 or 22 or 23 => zeroBasedSlot == 16,
        15 or 25 or 26 => zeroBasedSlot == 17,
        16 => zeroBasedSlot == 14,
        17 or 21 => zeroBasedSlot == 15,
        18 => zeroBasedSlot is >= 19 and <= 22,
        19 => zeroBasedSlot == 18,
        _ => false,
    };

    public static bool IsAmmo(uint inventoryType) => inventoryType == 24;
    public static uint IconTint(bool locked, bool broken) => locked ? Locked : broken ? Broken : White;
    public static uint RingTint(bool cursorFits, bool broken) => cursorFits ? Fits : broken ? Broken : White;
    public static uint? ModifierTextColor(int positive, int negative) => negative < 0
        ? Broken : positive > 0 ? 0xff20ff20u : null;

    public static float ClickFacing(float facing, bool left) => facing + (left ? -ClickRadians : ClickRadians);
    public static float HeldFacing(float facing, bool left, float elapsed) =>
        facing + (left ? 1f : -1f) * elapsed * 2f * MathF.PI * RotationsPerSecond;
}
