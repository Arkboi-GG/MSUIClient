using System.Numerics;

namespace MSUIClient.Engine.UI;

public readonly record struct RaidMarkerUv(Vector2 Min, Vector2 Max);
public readonly record struct RaidMarkerRect(Vector2 Min, Vector2 Max);

/// <summary>Current Benilla raid-target atlas, world-billboard and V-plate geometry.</summary>
public static class RaidMarkerUiLaw
{
    public const string Texture = @"Interface\TargetingFrame\UI-RaidTargetingIcons";
    public const float WorldSize = 1f;
    public const float NameplateIconGx = .02f;

    /// <summary>Lua-facing mark 1..8 on the atlas' first two 4-column rows.</summary>
    public static RaidMarkerUv AtlasUv(byte mark)
    {
        int index = Math.Clamp(mark, (byte)1, (byte)8) - 1;
        int column = index & 3;
        int row = index >> 2;
        return new(new Vector2(column * .25f, row * .25f),
            new Vector2((column + 1) * .25f, (row + 1) * .25f));
    }

    /// <summary>RIGHT of the 0.02gx square is seated at the plate's LEFT, vertically centered.</summary>
    public static RaidMarkerRect NameplateRect(float plateLeft, float plateTop,
        float plateBottom, float gxBasis)
    {
        float size = MathF.Round(NameplateIconGx * gxBasis);
        float centerY = (plateTop + plateBottom) * .5f;
        return new(new Vector2(plateLeft - size, centerY - size * .5f),
            new Vector2(plateLeft, centerY + size * .5f));
    }

    /// <summary>The overhead mark is a fixed one-world-unit, bottom-seated square.</summary>
    public static RaidMarkerRect OverheadRect(Vector2 bottomCenter, float projectedWorldSize)
    {
        float size = MathF.Max(0f, projectedWorldSize);
        return new(bottomCenter - new Vector2(size * .5f, size),
            bottomCenter + new Vector2(size * .5f, 0f));
    }
}
