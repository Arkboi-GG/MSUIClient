using System.Numerics;

namespace MSUIClient.Engine.UI;

public readonly record struct PetActionAssignment(int RelocationSlot, uint DisplacedWord)
{
    public bool Relocated => RelocationSlot >= 0;
    public bool HasDisplacedWord => DisplacedWord != 0;
}

/// <summary>Build-5875 PetActionBar.xml geometry, packed-word, state and rearrangement laws.</summary>
public static class PetActionBarUiLaw
{
    public const int SlotCount = 10;
    public const float FrameWidth = 509f;
    public const float FrameHeight = 43f;
    public const float BaseX = 36f;
    // PETACTIONBAR_YPOS is the frame TOP's distance above the main bar's bottom anchor.
    public const float BaseTopOffset = 97f;
    public const float BottomMultiBarStep = 43f;
    public const float ButtonSize = 30f;
    // FrameXML seats button 1 BOTTOMLEFT +2. ImGui rectangles are top-left based, so the
    // authored 30 px button begins 43 - 2 - 30 = 11 px below the frame's top edge.
    public const float ButtonBottomOffset = 2f;
    public const float ButtonTop = FrameHeight - ButtonBottomOffset - ButtonSize;
    public const float ButtonStep = 38f;
    public const float MiddleStep = 37f;
    public const float NormalTextureSize = 54f;
    public static readonly Vector2 NormalTextureOffset = new(0f, -1f);
    public const float CooldownSize = 33f;
    public static readonly Vector2 CooldownOffset = new(-2f, -1f);
    public const float AutoCastOverlaySize = 58f;
    public const uint AutocastAllowed = 0x8000_0000u;
    public const uint AutocastEnabled = 0x4000_0000u;
    public const uint BarDisabled = 0x0800_0000u;
    public const uint UnitFlagPossessed = 0x0100_0000u;
    public const uint UnitFlagUnusable = 0x0004_0000u | 0x0040_0000u | 0x0080_0000u;

    public static float ButtonX(int index)
    {
        index = Math.Clamp(index, 0, SlotCount - 1);
        return index <= 5 ? BaseX + ButtonStep * index :
            BaseX + ButtonStep * 5 + MiddleStep + ButtonStep * (index - 6);
    }

    public static uint Action(uint packed) => packed & 0x0000_FFFFu;
    public static byte Kind(uint packed) => (byte)((packed >> 24) & 0x3Fu);
    public static bool IsSpell(uint packed) => Kind(packed) is >= 1 and <= 5;
    public static bool IsToken(uint packed) => Kind(packed) is 6 or 7;
    public static bool HasPayload(uint packed) => Kind(packed) is >= 1 and <= 7;
    public static bool Autocastable(uint packed) => (packed & AutocastAllowed) != 0;
    public static bool Autocastable(uint packed, bool spellResolved) =>
        spellResolved && IsSpell(packed) && Autocastable(packed);
    public static bool Autocasting(uint packed) => (packed & AutocastEnabled) != 0;
    public static uint StripPermanentCooldownMarker(uint durationMs) =>
        durationMs & ~0x0800_0000u;
    public static bool PickupAllowed(uint unitFlags) => (unitFlags & UnitFlagPossessed) == 0;
    public static bool Usable(uint state, uint unitFlags) =>
        (state & BarDisabled) == 0 && (unitFlags & UnitFlagUnusable) == 0;
    public static byte Reaction(uint state) => (byte)state;
    public static uint Command(uint state) => state >> 8;

    /// <summary>An unnamed slot has no mouse target until a pet payload raises the drop grid.</summary>
    public static bool InteractiveSlot(bool named, bool cursorPayloadHeld) =>
        named || cursorPayloadHeld;

    /// <summary>
    /// A pet spell becomes the cancel-aura verb only when the same predicate that swaps its icon
    /// holds: a resolved spell word, a non-zero ActiveIconID, and a live cancelable matching aura.
    /// </summary>
    public static bool ActiveAuraPress(uint packed, uint activeIconId,
        bool matchingCancelableAura) =>
        IsSpell(packed) && Action(packed) != 0 && activeIconId != 0 && matchingCancelableAura;

    /// <summary>CMSG_PET_ACTION always carries the current selection, for every slot class.</summary>
    public static ulong ActionTarget(ulong selectionGuid) => selectionGuid;

    /// <summary>The old-target clear runs only when a selection existed and is replaced/dropped.</summary>
    public static bool StopsAttackOnSelectionChange(bool attacking, ulong previous, ulong current) =>
        attacking && previous != 0 && previous != current;

    /// <summary>The two action-feedback codes shipped by vmangos; unknown codes display nothing.</summary>
    public static string? FeedbackKey(byte reason) => reason switch
    {
        1 => "PET_SPELL_NOPATH",
        2 => "SPELL_FAILED_OUT_OF_RANGE",
        _ => null,
    };

    /// <summary>The attack-order actor gates, in the observable client order.</summary>
    public static string? AttackRefusalKey(uint? health, ulong? charmedBy,
        ulong playerGuid, uint unitFlags, uint mountDisplayId)
    {
        if (health == 0) return "ERR_ATTACK_DEAD";
        if (charmedBy is { } controller && controller != playerGuid) return "ERR_ATTACK_CHARMED";
        if ((unitFlags & 0x0004_0000u) != 0) return "ERR_ATTACK_STUNNED";
        if ((unitFlags & 0x0002_0000u) != 0) return "ERR_ATTACK_PACIFIED";
        if ((unitFlags & 0x0080_0000u) != 0) return "ERR_ATTACK_FLEEING";
        if ((unitFlags & 0x0040_0000u) != 0) return "ERR_ATTACK_CONFUSED";
        return mountDisplayId != 0 ? "ERR_ATTACK_MOUNTED" : null;
    }

    public static bool Active(uint packed, uint state, bool attacking)
    {
        uint action = Action(packed);
        return Kind(packed) switch
        {
            7 => action == 2 ? attacking : Command(state) == action,
            6 => ((state & BarDisabled) != 0 ? 0u : Reaction(state)) == action,
            _ => false,
        };
    }

    public static uint LatchPress(uint state, uint packed)
    {
        uint action = Action(packed);
        return Kind(packed) switch
        {
            7 when action <= 1 => (state & 0x0800_00FFu) | (action << 8),
            6 => (state & 0xFFFF_FF00u) | action,
            _ => state,
        };
    }

    public static uint ToggleAutocast(uint packed) => packed ^ AutocastEnabled;

    /// <summary>The reference assign core. Returns false for no-op/passive/token-without-home.</summary>
    public static bool TryAssign(uint[] slots, int target, uint source, bool passive,
        out PetActionAssignment assignment)
    {
        assignment = default;
        if ((uint)target >= slots.Length || slots[target] == source ||
            (Kind(source) == 1 && passive)) return false;

        uint occupant = slots[target];
        int relocation = -1;
        bool blankedSpell = Kind(source) == 1 && Action(source) == 0;
        if (!blankedSpell)
        {
            for (int i = 0; i < slots.Length; i++)
                if (i != target && (slots[i] & 0x3FFF_FFFFu) == (source & 0x3FFF_FFFFu))
                { relocation = i; break; }
        }
        if (relocation < 0 && IsToken(occupant))
        {
            for (int i = 0; i < slots.Length; i++)
                if (!IsToken(slots[i]) && Action(slots[i]) == 0) { relocation = i; break; }
            if (relocation < 0) return false;
        }

        if (relocation >= 0) slots[relocation] = occupant;
        slots[target] = source;
        assignment = new(relocation,
            relocation < 0 && HasPayload(occupant) ? occupant : 0u);
        return true;
    }

    /// <summary>Four emitters lap the 28.8px square anti-clockwise from bottom-left.</summary>
    public static Vector2 SparklePoint(float phase)
    {
        phase -= MathF.Floor(phase);
        float p = phase * 4f;
        return (int)p switch
        {
            0 => new(0f, 28.8f * (1f - (p - 0f))),
            1 => new(28.8f * (p - 1f), 0f),
            2 => new(28.8f, 28.8f * (p - 2f)),
            _ => new(28.8f * (1f - (p - 3f)), 28.8f),
        };
    }

    /// <summary>Three-key linear M2 particle size ramp at ages 0, .5 and 1.</summary>
    public static float SparkleSize(float age) => Ramp(14.4f, 5.76f, 2.88f, age);

    /// <summary>Three-key linear M2 particle colour/alpha ramp at ages 0, .5 and 1.</summary>
    public static Vector4 SparkleColor(float age)
    {
        Vector4 first = new(.976f, .875f, .192f, 1f);
        Vector4 middle = new(.996f, .945f, .745f, 1f);
        Vector4 last = new(1f, 1f, 1f, 0f);
        age = Math.Clamp(age, 0f, 1f);
        return age <= .5f
            ? Vector4.Lerp(first, middle, age * 2f)
            : Vector4.Lerp(middle, last, (age - .5f) * 2f);
    }

    private static float Ramp(float first, float middle, float last, float age)
    {
        age = Math.Clamp(age, 0f, 1f);
        return age <= .5f
            ? first + (middle - first) * age * 2f
            : middle + (last - middle) * (age - .5f) * 2f;
    }
}
