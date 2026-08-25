namespace MSUIClient.Engine.UI;

/// <summary>PaperDollFrame equipment, ammo, and model-rotation rules from CharacterFrame.xml.</summary>
public static class PaperDollUiLaw
{
    public enum SlotClickAction { None, PickupOrPlace, Use }

    public readonly record struct LogicalRect(float X, float Y, float Width, float Height);
    public readonly record struct SoundTransition(string Cue, int Count);
    public readonly record struct TooltipAnchor(string Point, string RelativePoint,
        float PivotX, float PivotY);
    public readonly record struct DamageTooltipData(string Damage, float AttackSpeed, float Dps);

    public const uint White = 0xffffffff;
    public const uint Locked = 0xff666666;
    public const uint Broken = 0xff0000e6;
    public const uint Fits = 0xff00ff00;
    public const float ClickRadians = .12f;
    public const float RotationsPerSecond = .5f;
    public const int FrameWidth = 384;
    public const int FrameHeight = 512;
    public const int MaxContainerSlots = 36;
    public const float TooltipWrapWidth = 260f;
    public const float LiveAnimationMaxStep = .05f;

    public static readonly LogicalRect PortraitRect = new(7, 6, 60, 60);
    public static readonly LogicalRect ModelRect = new(65, 78, 233, 224);
    public static readonly LogicalRect AmmoHitRect = new(258, 390, 27, 27);
    public static readonly LogicalRect AmmoBackgroundRect = new(251, 383, 41, 41);
    public static readonly LogicalRect AmmoOverlayRect = new(238, 383, 23, 41);
    public static readonly SoundTransition OpenSound = new("igCharacterInfoOpen", 1);
    public static readonly SoundTransition CloseSound = new("igCharacterInfoClose", 1);
    // CharacterFrameTab_OnClick calls ToggleCharacter (which sounds on a page change) and then
    // plays the same cue itself. Preserve that authored two-cue count on an actual page switch.
    public static readonly SoundTransition TabSwitchSound = new("igCharacterInfoTab", 2);
    // The 1.12 rotate buttons register both LeftButtonDown and LeftButtonUp. MSUI keeps its own
    // 0.12-radian tap law, but retains the reference's two sound edges per physical tap.
    public static readonly SoundTransition RotateTapSound = new("igInventoryRotateCharacter", 2);
    public static readonly string[] StatNames =
        ["Strength", "Agility", "Stamina", "Intellect", "Spirit"];
    public static readonly string[] StatSubtexts =
    [
        "Increases attack power with melee weapons.",
        "Increases attack power with ranged weapons.\nImproves chance to score a critical hit with all weapons.\nIncreases armor and chance to dodge attacks.",
        "Increases health points.",
        "Increases the rate at which weapon skills improve.",
        "Increases health and mana regeneration rates.",
    ];
    public static readonly string[] ResistanceNames =
        ["Arcane Resistance", "Fire Resistance", "Nature Resistance", "Frost Resistance", "Shadow Resistance"];
    public static readonly string[] ResistanceTypes = ["arcane", "fire", "nature", "frost", "shadow"];

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

    /// <summary>Advance only while the character page is visibly rendering, without
    /// replaying wall time accumulated while it was closed or on another tab.</summary>
    public static float LiveAnimationStep(double now, double previous) =>
        previous > 0 && now > previous
            ? (float)Math.Clamp(now - previous, 0, LiveAnimationMaxStep)
            : 0f;

    public static bool IsAmmo(uint inventoryType) => inventoryType == 24;
    public static bool IsBroken(uint itemFlags, uint durability, uint maxDurability) =>
        (itemFlags & 0x08) == 0 &&
        ((itemFlags & 0x10) != 0 || maxDurability > 0 && durability == 0);
    public static int ContainerSlotScanCount(uint advertisedSlots) =>
        (int)Math.Clamp(advertisedSlots, 0u, MaxContainerSlots);

    public static string ModifierTooltip(string name, int effective, int positive, int negative)
    {
        if (positive == 0 && negative == 0) return $"{name} {effective}";
        int baseValue = effective - positive - negative;
        string positivePart = positive > 0 ? $"+{positive}" : "";
        string negativePart = negative < 0 ? $" {negative}" : "";
        return $"{name} {effective} ({baseValue}{positivePart}{negativePart})";
    }

    // CharacterFrame's five-primary-stat loop is not PaperDollFormatStat: after UnitStat has
    // supplied its first (already inverse-derived) value, the authored Lua subtracts both buff
    // halves from that value once more when composing the parenthesized tooltip segment. Preserve
    // the frozen reference's literal result rather than silently normalizing the oddity.
    public static string PrimaryStatTooltip(string name, int effective, int positive, int negative)
    {
        if (positive == 0 && negative == 0) return $"{name} {effective}";
        int unitStatFirstReturn = effective - positive - negative;
        int tooltipBase = unitStatFirstReturn - positive - negative;
        string positivePart = positive > 0 ? $"+{positive}" : "";
        string negativePart = negative < 0 ? $" {negative}" : "";
        return $"{name} {effective} ({tooltipBase}{positivePart}{negativePart})";
    }

    // PaperDollFrame's resistance tooltip intentionally has spaces just inside the
    // parentheses; it is not the same formatter used by the five primary statistics.
    public static string ResistanceTooltip(string name, int effective, int positive, int negative)
    {
        if (positive == 0 && negative == 0) return $"{name} {effective}";
        int baseValue = effective - positive - negative;
        string positivePart = positive > 0 ? $" +{positive}" : "";
        string negativePart = negative < 0 ? $" {negative}" : "";
        return $"{name} {effective} ( {baseValue}{positivePart}{negativePart} )";
    }

    public static float ArmorReductionPercent(int effectiveArmor, uint playerLevel)
    {
        float denominator = 85f * playerLevel + 400f;
        float ratio = effectiveArmor / denominator;
        return 100f * ratio / (ratio + 1f);
    }

    public static string ArmorTooltipSubtext(int effectiveArmor, uint playerLevel) =>
        FormattableString.Invariant(
            $"Decreases the amount of damage taken from physical attacks.  The amount of reduction is influenced by the level of the attacker.\nDamage reduction against a level {playerLevel} attacker: {ArmorReductionPercent(effectiveArmor, playerLevel):0.0}%");

    public static string ResistanceRating(int resistance, uint playerLevel)
    {
        float normalized = resistance / Math.Max(20f, playerLevel);
        return normalized > 5 ? "Excellent" : normalized > 3.75f ? "Very Good" :
            normalized > 2.5f ? "Good" : normalized > 1.25f ? "Fair" :
            normalized > 0 ? "Poor" : "None";
    }

    public static string ResistanceTooltipSubtext(string resistanceType, int resistance,
        uint playerLevel)
    {
        uint effectiveLevel = Math.Max(playerLevel, 20u);
        return $"Increases the ability to resist {resistanceType}-based attacks, spells and abilities.\n" +
            $"Resistance against level {effectiveLevel}: {ResistanceRating(resistance, playerLevel)}";
    }

    /// <summary>
    /// Exact CharacterFrame SetDamage/SetRangedDamage inverse decomposition. The update fields
    /// carry effective min/max values; the tooltip first unwinds the school-zero percent and flat
    /// modifiers, then appends those modifiers while DPS remains based on effective full damage.
    /// </summary>
    public static DamageTooltipData DamageTooltip(float effectiveMin, float effectiveMax,
        int positive, int negative, float percent, float attackSpeed)
    {
        // The server initializes the field family to 1.0. Retain a div-safe identity for a
        // malformed/transitional zero without changing any valid reference input.
        if (!float.IsFinite(percent) || percent == 0f) percent = 1f;
        if (!float.IsFinite(attackSpeed) || attackSpeed <= 0f) attackSpeed = 2f;
        float baseMin = effectiveMin / percent - positive - negative;
        float baseMax = effectiveMax / percent - positive - negative;
        float baseDamage = (baseMin + baseMax) * .5f;
        float fullDamage = (baseDamage + positive + negative) * percent;
        float totalBonus = fullDamage - baseDamage;
        string damage = $"{Math.Max((int)MathF.Floor(baseMin), 1)} - " +
            $"{Math.Max((int)MathF.Ceiling(baseMax), 1)}";
        if (totalBonus != 0f)
        {
            if (positive > 0) damage += $" +{positive}";
            if (negative < 0) damage += $" {negative}";
            if (percent > 1f || percent < 1f)
                damage += $" x{MathF.Floor(percent * 100f + .5f):0}%";
        }
        return new(damage, attackSpeed, Math.Max(fullDamage, 1f) / attackSpeed);
    }

    public static LogicalRect EquipmentSlotRect(int zeroBasedSlot) => zeroBasedSlot switch
    {
        0 => new(21, 74, 37, 37),
        1 => new(21, 115, 37, 37),
        2 => new(21, 156, 37, 37),
        14 => new(21, 197, 37, 37),
        4 => new(21, 238, 37, 37),
        3 => new(21, 279, 37, 37),
        18 => new(21, 320, 37, 37),
        8 => new(21, 361, 37, 37),
        9 => new(305, 74, 37, 37),
        5 => new(305, 115, 37, 37),
        6 => new(305, 156, 37, 37),
        7 => new(305, 197, 37, 37),
        10 => new(305, 238, 37, 37),
        11 => new(305, 279, 37, 37),
        12 => new(305, 320, 37, 37),
        13 => new(305, 361, 37, 37),
        15 => new(122, 385, 37, 37),
        16 => new(164, 385, 37, 37),
        17 => new(206, 385, 37, 37),
        _ => default,
    };

    public static string EquipmentSlotLabel(int zeroBasedSlot) => zeroBasedSlot switch
    {
        0 => "Head",
        1 => "Neck",
        2 => "Shoulders",
        3 => "Shirt",
        4 => "Chest",
        5 => "Waist",
        6 => "Legs",
        7 => "Feet",
        8 => "Wrist",
        9 => "Hands",
        10 or 11 => "Finger",
        12 or 13 => "Trinket",
        14 => "Back",
        15 => "Main Hand",
        16 => "Off Hand",
        17 => "Ranged",
        18 => "Tabard",
        _ => "Equipment",
    };

    /// <summary>
    /// Doll slots which answer SHOW_COMPARE_TOOLTIP for a hovered bag item. Ammo and bags have no
    /// listener on CharacterFrame. This is deliberately not derived from FitsEquipmentSlot:
    /// two-handed weapons compare against both hands (they displace the off hand) while their
    /// actual pickup/drop target remains the main-hand slot.
    /// </summary>
    public static int ComparisonSlotCount(uint inventoryType) => inventoryType switch
    {
        11 or 12 or 13 or 17 => 2,
        1 or 2 or 3 or 4 or 5 or 6 or 7 or 8 or 9 or 10 or 14 or 15 or 16 or 19 or 20 or 21 or
            22 or 23 or 25 or 26 or 28 => 1,
        _ => 0,
    };

    public static int ComparisonSlot(uint inventoryType, int ordinal) => (inventoryType, ordinal) switch
    {
        (1, 0) => 0,
        (2, 0) => 1,
        (3, 0) => 2,
        (4, 0) => 3,
        (5 or 20, 0) => 4,
        (6, 0) => 5,
        (7, 0) => 6,
        (8, 0) => 7,
        (9, 0) => 8,
        (10, 0) => 9,
        (11, 0) => 10,
        (11, 1) => 11,
        (12, 0) => 12,
        (12, 1) => 13,
        (16, 0) => 14,
        (13 or 17 or 21, 0) => 15,
        (13 or 17, 1) => 16,
        (14 or 22 or 23, 0) => 16,
        (15 or 25 or 26 or 28, 0) => 17,
        (19, 0) => 18,
        _ => -1,
    };

    public static bool ShowBagItemComparison(bool characterOpen, int characterTab, bool shiftHeld,
        bool sourceIsEquipped) =>
        characterOpen && characterTab == 0 && shiftHeld && !sourceIsEquipped;
    public static TooltipAnchor ShoppingTooltipAnchor(int zeroBasedOrdinal) => zeroBasedOrdinal <= 0
        ? new("BOTTOMLEFT", "TOPRIGHT", 0, 1)
        : new("TOPLEFT", "BOTTOMRIGHT", 0, 0);
    public static uint IconTint(bool locked, bool broken) => locked ? Locked : broken ? Broken : White;
    public static uint RingTint(bool cursorFits, bool broken) => cursorFits ? Fits : broken ? Broken : White;
    public static uint? ModifierTextColor(int positive, int negative) => negative < 0
        ? Broken : positive > 0 ? 0xff20ff20u : null;
    public static uint? ResistanceTextColor(int positive, int negative)
    {
        int negativeMagnitude = Math.Abs(negative);
        return negativeMagnitude > positive ? Broken : negativeMagnitude < positive
            ? 0xff20ff20u : null;
    }

    public static float ClickFacing(float facing, bool left) => facing + (left ? -ClickRadians : ClickRadians);
    public static float HeldFacing(float facing, bool left, float elapsed) =>
        facing + (left ? 1f : -1f) * elapsed * 2f * MathF.PI * RotationsPerSecond;
}
