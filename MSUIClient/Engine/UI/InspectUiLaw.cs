namespace MSUIClient.Engine.UI;

public enum InspectTokenKind
{
    Target,
    Party,
}

/// <summary>The FrameXML unit token whose identity must be re-resolved on UI events.</summary>
public readonly record struct InspectBinding(InspectTokenKind Kind, int PartyIndex)
{
    public static InspectBinding Target => new(InspectTokenKind.Target, -1);
    public static InspectBinding Party(int index) => new(InspectTokenKind.Party, index);
}

public enum InspectEnchantTone
{
    Green,
    Red,
    White,
}

/// <summary>Observable build-5875 inspect gates and model rotation constants.</summary>
public static class InspectUiLaw
{
    public readonly record struct LogicalRect(float X, float Y, float Width, float Height);

    public const float FrameWidth = 384f;
    public const float FrameHeight = 512f;
    public const float HitWidth = 354f;
    public const float HitHeight = 467f;
    public const int EquipmentSlotCount = 19;
    public const float SlotSize = 37f;
    public const float SlotRingSize = 64f;
    public const float WeaponRowTop = 385f;
    public static readonly LogicalRect PortraitRect = new(7, 6, 60, 60);
    public static readonly LogicalRect ModelRect = new(65, 78, 233, 300);
    public static readonly LogicalRect RotateLeftRect = new(65, 78, 35, 35);
    public static readonly LogicalRect RotateRightRect = new(100, 78, 35, 35);
    public static readonly LogicalRect CloseRect = new(324, 9, 32, 32);

    public const float MaxDistance = 10f;
    public const float DefaultFacing = 0.61f;
    public const float TapRadians = 0.03f;
    public const float RotationsPerSecond = 0.5f;
    public const string OpenSound = "igCharacterInfoOpen";
    public const string CloseSound = "igCharacterInfoClose";
    public const string RotateSound = "igInventoryRotateCharacter";
    public const string SoundCategory = "ui.inspect";
    public const int PhysicalTapSoundCount = 2;

    public static bool CanInspect(bool isPlayer, bool isSelf, bool attackable, float distanceSquared)
        => isPlayer && !isSelf && !attackable && distanceSquared <= MaxDistance * MaxDistance;

    /// <summary>
    /// UnitPopup's `CheckInteractDistance(unit, 1)` gate is strict at ten yards. Tokens omitted
    /// from the reference distance table retain the popup template's enabled default.
    /// </summary>
    public static bool PopupRowEnabled(bool isPlayer, bool isSelf, bool attackable,
        float distanceSquared)
        => !isPlayer || isSelf || attackable || distanceSquared < MaxDistance * MaxDistance;

    public static bool RefreshForEvent(InspectBinding binding, bool targetChanged,
        bool partyRosterChanged)
    {
        _ = binding; // both registered events re-run InspectUnit(frame.unit), whatever its token
        return targetChanged || partyRosterChanged;
    }

    public static InspectEnchantTone VisibleEnchantTone(int enchantSlot, bool negative) =>
        enchantSlot >= 2 ? InspectEnchantTone.White :
        negative ? InspectEnchantTone.Red : InspectEnchantTone.Green;

    public static bool VisibleEnchantsAllowed(uint itemFlags) => (itemFlags & 0x2000) == 0;

    public static float ClickFacing(float facing, bool left)
        => facing + (left ? -TapRadians : TapRadians);

    /// <summary>Both registered physical click edges run the same FrameXML OnClick handler.</summary>
    public static float PhysicalTapFacing(float facing, bool left)
        => ClickFacing(ClickFacing(facing, left), left);

    public static float HeldFacing(float facing, bool left, float elapsed)
    {
        float step = Math.Max(0f, elapsed) * 2f * MathF.PI * RotationsPerSecond;
        // This sign reversal between tap and hold is the reference behavior.
        return Wrap(facing + (left ? step : -step));
    }

    public static float Wrap(float facing)
    {
        float turn = 2f * MathF.PI;
        facing %= turn;
        return facing < 0f ? facing + turn : facing;
    }
}
