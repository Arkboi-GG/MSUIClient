using System.Numerics;

namespace MSUIClient.Engine.UI;

public enum DurabilityGlyphKind
{
    Head, Shoulders, Chest, Waist, Legs, Feet, Wrists, Hands, Weapon, Shield, OffWeapon, Ranged,
}

public readonly record struct DurabilityGlyph(
    DurabilityGlyphKind Kind, Vector2 Min, Vector2 Size, Vector2 UvMin, Vector2 UvMax,
    int AlertIndex, bool Body);

/// <summary>Frozen 1.12 DurabilityFrame alert, atlas, and managed-HUD seating laws.</summary>
public static class DurabilityFrameUiLaw
{
    public const float Width = 60;
    public const float Height = 65;
    public const float MinimapClusterHeight = 192;
    public static readonly Vector4 Damaged = new(1f, .82f, .18f, 1f);
    public static readonly Vector4 Broken = new(.93f, .07f, .07f, 1f);
    public static readonly Vector4 Faded = new(1f, 1f, 1f, .5f);
    public static readonly int[] EquipmentSlots = [0, 2, 4, 5, 6, 7, 8, 9, 15, 16, 17];

    public static readonly DurabilityGlyph[] Glyphs =
    [
        new(DurabilityGlyphKind.Head, new(21,0), new(18,22), new(0,0), new(.140625f,.171875f), 0, true),
        new(DurabilityGlyphKind.Shoulders, new(6,6), new(48,22), new(.140625f,0), new(.515625f,.171875f), 1, true),
        new(DurabilityGlyphKind.Chest, new(20,13), new(20,22), new(.515625f,0), new(.6640625f,.171875f), 2, true),
        new(DurabilityGlyphKind.Waist, new(22,29), new(16,5), new(.328125f,.171875f), new(.46875f,.203125f), 3, true),
        new(DurabilityGlyphKind.Legs, new(15.5f,32), new(29,20), new(.46875f,.171875f), new(.6875f,.3203125f), 4, true),
        new(DurabilityGlyphKind.Feet, new(9.5f,44), new(41,32), new(.6875f,.171875f), new(1,.4140625f), 5, true),
        new(DurabilityGlyphKind.Wrists, new(8,15), new(44,22), new(.6640625f,0), new(1,.171875f), 6, true),
        new(DurabilityGlyphKind.Hands, new(9,22), new(42,18), new(0,.171875f), new(.328125f,.3046875f), 7, true),
        new(DurabilityGlyphKind.Weapon, new(-12,9.5f), new(20,45), new(0,.3203125f), new(.140625f,.6640625f), 8, false),
        new(DurabilityGlyphKind.Shield, new(52,.5f), new(25,31), new(.1875f,.3203125f), new(.375f,.5546875f), 9, false),
        new(DurabilityGlyphKind.OffWeapon, new(52,9.5f), new(20,45), new(0,.3203125f), new(.140625f,.6640625f), 9, false),
        new(DurabilityGlyphKind.Ranged, new(50.5f,26.5f), new(28,38), new(.1875f,.5546875f), new(.3984375f,.84375f), 10, false),
    ];

    public static byte AlertStatus(uint itemFlags, uint durability, uint maxDurability)
    {
        if ((itemFlags & 0x10) != 0) return 4;
        if ((itemFlags & 0x08) != 0 || maxDurability == 0) return 0;
        if (durability == 0) return 4;
        return durability <= 5 ? (byte)3 : (byte)0;
    }

    public static Vector4 Color(byte status) => status switch
    {
        3 => Damaged,
        4 => Broken,
        _ => Faded,
    };

    public static bool BodyShown(IReadOnlyList<byte> statuses) =>
        statuses.Take(8).Any(status => status is 3 or 4);
    public static bool SideShown(IReadOnlyList<byte> statuses) =>
        statuses.Count >= 11 && (statuses[9] is 3 or 4 || statuses[10] is 3 or 4);
    public static bool FrameShown(IReadOnlyList<byte> statuses) =>
        statuses.Any(status => status is 3 or 4);

    public static Vector2 FrameOrigin(Vector2 display, float scale, bool rightSideGlyph,
        float questTimerHeight = 0f) =>
        new Vector2(display.X / scale - Width - (rightSideGlyph ? 20f : 0f),
            MinimapClusterHeight + Math.Max(0f, questTimerHeight)) * scale;
}
