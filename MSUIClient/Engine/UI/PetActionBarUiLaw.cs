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
    public const float BaseBottom = 97f;
    public const float BottomMultiBarStep = 43f;
    public const float ButtonSize = 30f;
    public const float ButtonY = 2f;
    public const float ButtonStep = 38f;
    public const float MiddleStep = 37f;
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
    public static bool Autocasting(uint packed) => (packed & AutocastEnabled) != 0;
    public static bool PickupAllowed(uint unitFlags) => (unitFlags & UnitFlagPossessed) == 0;
    public static bool Usable(uint state, uint unitFlags) =>
        (state & BarDisabled) == 0 && (unitFlags & UnitFlagUnusable) == 0;
    public static byte Reaction(uint state) => (byte)state;
    public static uint Command(uint state) => state >> 8;

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
}
