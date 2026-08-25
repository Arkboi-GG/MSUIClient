using System.Numerics;

namespace MSUIClient.Engine.UI;

public readonly record struct RaidMarkerUv(Vector2 Min, Vector2 Max);
public readonly record struct RaidMarkerRect(Vector2 Min, Vector2 Max);

/// <summary>Current Benilla raid-target atlas, world-billboard and V-plate geometry.</summary>
public static class RaidMarkerUiLaw
{
    // The extension is load-bearing. GameplayArt.Get appends ".blp" when it is missing, but the
    // world-billboard path (SpellEffectMeshRenderer.ResolveTexture) hands the string straight to
    // MpqMount.ReadFile, which only knows the real archive key —
    // patch.MPQ Interface\TargetingFrame\UI-RaidTargetingIcons.blp. Without it every overhead
    // raid mark silently resolved to a null texture and was never drawn.
    public const string Texture = @"Interface\TargetingFrame\UI-RaidTargetingIcons.blp";
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

    /// <summary>
    /// TargetRaidTargetIcon (TargetFrame.xml): a 26x26 square whose CENTER is anchored to the
    /// TOPRIGHT of the setAllPoints TargetFrameTextureFrame at offset (-73, -14). The root frame
    /// is 232x100, so the centre lands at (159, 14) and the square's top-left at (146, 1) in
    /// authored units. There is deliberately no party-frame equivalent: PartyFrameTemplates.xml
    /// carries LeaderIcon, MasterIcon, PVPIcon and Disconnect, and no raid mark at all.
    /// </summary>
    public const float TargetFrameSize = 26f;

    public static RaidMarkerRect TargetFrameRect(Vector2 frameTopLeft, float scale)
    {
        Vector2 min = frameTopLeft + new Vector2(146f, 1f) * scale;
        return new(min, min + new Vector2(TargetFrameSize) * scale);
    }

    /// <summary>The overhead mark is a fixed one-world-unit, bottom-seated square.</summary>
    public static RaidMarkerRect OverheadRect(Vector2 bottomCenter, float projectedWorldSize)
    {
        float size = MathF.Max(0f, projectedWorldSize);
        return new(bottomCenter - new Vector2(size * .5f, size),
            bottomCenter + new Vector2(size * .5f, 0f));
    }
}
