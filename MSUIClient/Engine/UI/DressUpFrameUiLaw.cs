using System.Numerics;

namespace MSUIClient.Engine.UI;

/// <summary>Build-5875 DressUpFrame geometry and try-on slot laws.</summary>
public static class DressUpFrameUiLaw
{
    public readonly record struct LogicalRect(float X, float Y, float Width, float Height)
    {
        public Vector2 Min => new(X, Y);
        public Vector2 Size => new(Width, Height);
    }

    public const float Width = 384;
    public const float Height = 512;
    public const float Top = 104;
    public const float InitialFacing = .61f;
    public const float ClickEdgeRadians = .03f;
    public const float HeldRadiansPerSecond = MathF.Tau * .5f;
    public const float LiveAnimationMaxStep = .05f;

    public static readonly Vector2 FrameSize = new(Width, Height);
    public static readonly Vector2 PortraitUvMin = new(0, 1);
    public static readonly Vector2 PortraitUvMax = new(1, 0);
    public static readonly LogicalRect Portrait = new(7, 6, 60, 60);
    public static readonly LogicalRect Description = new(62, 37, 260, 36);
    public static readonly Vector2 TitleCenter = new(192, 17);
    public static readonly Vector2 DescriptionLineOneCenter = new(192, 43);
    public static readonly Vector2 DescriptionLineTwoCenter = new(192, 57);
    public static readonly LogicalRect BackdropTopLeft = new(22, 76, 256, 255);
    public static readonly LogicalRect BackdropTopRight = new(278, 76, 62, 255);
    public static readonly LogicalRect BackdropBottomLeft = new(22, 331, 256, 128);
    public static readonly LogicalRect BackdropBottomRight = new(278, 331, 62, 128);
    public static readonly LogicalRect Model = new(23, 76, 316, 351);
    public static readonly LogicalRect RotateLeft = new(21, 75, 35, 35);
    public static readonly LogicalRect RotateRight = new(56, 75, 35, 35);
    public static readonly LogicalRect CloseX = new(322, 9, 32, 32);
    public static readonly LogicalRect Reset = new(185, 411, 80, 22);
    public static readonly LogicalRect Close = new(265, 411, 80, 22);

    /// <summary>Reference equip_slot mapping, expressed as zero-based player equipment slots.</summary>
    public static int EquipmentSlot(uint inventoryType) => inventoryType switch
    {
        1 => 0,
        2 => 1,
        3 => 2,
        4 => 3,
        5 or 20 => 4,
        6 => 5,
        7 => 6,
        8 => 7,
        9 => 8,
        10 => 9,
        11 => 10,
        12 => 12,
        13 or 17 or 21 => 15,
        14 or 22 or 23 => 16,
        15 or 25 or 26 => 17,
        16 => 14,
        18 => 19,
        19 => 18,
        _ => -1,
    };

    public static bool HeldSlot(int slot) => slot is 15 or 16 or 17;

    /// <summary>The widget's two held lanes coexist only for a one-hand main plus legal off-hand.</summary>
    public static bool HeldLanesCoexist(uint mainInventoryType, uint offInventoryType) =>
        mainInventoryType is 13 or 21 && offInventoryType is 13 or 14 or 22 or 23;

    public static bool RangedUsesOffLane(uint inventoryType) => inventoryType == 15;

    public static float ClickFacing(float facing, bool left) =>
        facing + (left ? -ClickEdgeRadians : ClickEdgeRadians);

    /// <summary>
    /// Reference Model_OnUpdate sign quirk: the held direction is opposite the click direction.
    /// Its one-sided wrap conditions are retained exactly rather than normalized symmetrically.
    /// </summary>
    public static float HeldFacing(float facing, bool left, float elapsed)
    {
        float next = facing + (left ? 1f : -1f) *
            HeldRadiansPerSecond * MathF.Max(0, elapsed);
        if (left && next < 0f) next += MathF.Tau;
        else if (!left && next > MathF.Tau) next -= MathF.Tau;
        return next;
    }

    /// <summary>
    /// The DressUpModel is a live PlayerModel widget. Clamp a resumed/stalled frame rather than
    /// advancing its Stand loop by wall time accumulated while the window was not drawing.
    /// </summary>
    public static float LiveAnimationStep(double now, double previous) =>
        previous > 0 && double.IsFinite(now) && double.IsFinite(previous)
            ? (float)Math.Clamp(now - previous, 0, LiveAnimationMaxStep)
            : 0f;

    public static string BackgroundRace(string race) => race.ToUpperInvariant() switch
    {
        "GNOME" => "Dwarf",
        "TROLL" => "Orc",
        "HUMAN" => "Human",
        "ORC" => "Orc",
        "DWARF" => "Dwarf",
        "NIGHTELF" or "NIGHT ELF" => "NightElf",
        "SCOURGE" or "UNDEAD" => "Scourge",
        "TAUREN" => "Tauren",
        _ => "Orc",
    };
}
