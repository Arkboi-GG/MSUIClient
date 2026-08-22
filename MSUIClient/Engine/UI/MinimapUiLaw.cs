using System.Numerics;

namespace MSUIClient.Engine.UI;

public enum MinimapZonePvpType { Unknown, Friendly, Hostile, Contested }

public readonly record struct MinimapZonePvpInfo(
    MinimapZonePvpType Type, string FactionName, bool IsArena)
{
    public Vector4 Tint => Type switch
    {
        MinimapZonePvpType.Friendly => new(.1f, 1f, .1f, 1f),
        MinimapZonePvpType.Hostile => new(1f, .1f, .1f, 1f),
        MinimapZonePvpType.Contested => new(1f, .7f, 0f, 1f),
        _ => new(1f, .82f, 0f, 1f),
    };

    public string? TerritoryLine => Type switch
    {
        MinimapZonePvpType.Friendly or MinimapZonePvpType.Hostile
            when FactionName.Length > 0 => $"{FactionName} Territory",
        MinimapZonePvpType.Contested => "Contested Territory",
        _ => null,
    };
}

/// <summary>Frozen minimap PvP label, zoom-state, tooltip, and sound laws.</summary>
public static class MinimapUiLaw
{
    public const float BlipBasisPixels = 140.8f;
    public const float LandmarkIconPixels = 16f;
    public const float LandmarkArrowPixels = 38.4f;
    public const float LandmarkEdgeRatio = .8f;
    public const string OpenSound = "igMiniMapOpen";
    public const string CloseSound = "igMiniMapClose";
    public const string ZoomInSound = "igMiniMapZoomIn";
    public const string ZoomOutSound = "igMiniMapZoomOut";
    public const string UnreadMailText = "You have unread mail";
    public const string ArenaText = "PvP Area";
    public const uint QuestRewardStatus = 7;
    public static readonly Vector2 QuestDotUvMin = new(.75f, 0f);
    public static readonly Vector2 QuestDotUvMax = new(1f, .25f);
    public static readonly Vector2 TrackedCreatureDotUvMin = new(.25f, 0f);
    public static readonly Vector2 TrackedCreatureDotUvMax = new(.5f, .25f);
    public const uint TrackUnitDynamicFlag = 0x2;
    public const uint CrossInteriorTint = 0xffb0b0b0;
    // ZOOM_CHUNKS {14,12,10,8,6,4} * .5 * one 33.3333yd ADT chunk.
    public static readonly float[] OutdoorRadiusYards =
        [233.33333f, 200f, 166.66667f, 133.33333f, 100f, 66.66667f];

    public static MinimapZonePvpInfo ZonePvp(uint leafFlags, uint zoneFactionMask,
        uint friendGroupMask, uint enemyGroupMask, bool inputsKnown = true)
    {
        if (!inputsKnown) return new(MinimapZonePvpType.Unknown, "", false);
        MinimapZonePvpType type = (zoneFactionMask & friendGroupMask) != 0
            ? MinimapZonePvpType.Friendly
            : (zoneFactionMask & enemyGroupMask) != 0
                ? MinimapZonePvpType.Hostile : MinimapZonePvpType.Contested;
        string faction = zoneFactionMask switch
        {
            2 => "Alliance",
            4 => "Horde",
            _ => "",
        };
        return new(type, faction, (leafFlags & 0x80) != 0);
    }

    public static bool ZoomInEnabled(int zoom, int levels = 6) => zoom < levels - 1;
    public static bool ZoomOutEnabled(int zoom) => zoom > 0;
    public static int StepZoom(int zoom, bool zoomIn, int levels = 6) =>
        Math.Clamp(zoom + (zoomIn ? 1 : -1), 0, levels - 1);
    public static float OutdoorRadius(int zoom) =>
        OutdoorRadiusYards[Math.Clamp(zoom, 0, OutdoorRadiusYards.Length - 1)];
    public static bool ShowQuestDot(uint dialogStatus) => dialogStatus == QuestRewardStatus;

    public static bool ShowTrackedCreatureDot(uint trackMask, uint creatureType,
        uint dynamicFlags) =>
        (dynamicFlags & TrackUnitDynamicFlag) != 0 ||
        creatureType is >= 1 and <= 32 && (trackMask & (1u << (int)(creatureType - 1))) != 0;

    public static uint BlipTint(bool playerIndoors, bool candidateIndoors) =>
        playerIndoors == candidateIndoors ? 0xffffffff : CrossInteriorTint;

    public static float LandmarkIconSize(float minimapSide) =>
        minimapSide * (LandmarkIconPixels / BlipBasisPixels);

    public static float LandmarkArrowSize(float minimapSide) =>
        minimapSide * (LandmarkArrowPixels / BlipBasisPixels);

    public static Vector2 LandmarkArrowCenter(Vector2 minimapCenter, Vector2 direction,
        float minimapSide)
    {
        if (direction.LengthSquared() <= float.Epsilon) return minimapCenter;
        return minimapCenter + Vector2.Normalize(direction) *
            (minimapSide * .5f * LandmarkEdgeRatio);
    }

    /// <summary>
    /// Reference outdoor-tile MODULATE from LightIntBand diffuse (0) and ambient (1), evaluated
    /// in gamma-space. Interior tiles deliberately remain white.
    /// </summary>
    public static Vector3 OutdoorDayTint(Vector3 ambient, Vector3 diffuse)
    {
        ambient = Vector3.Clamp(ambient, Vector3.Zero, Vector3.One);
        diffuse = Vector3.Clamp(diffuse, Vector3.Zero, Vector3.One);
        float lumaByte = 255f *
            (ambient.X * 77f + ambient.Y * 151f + ambient.Z * 28f) / 256f;
        float towardWhite = MathF.Min(lumaByte + 96f, 255f) / 256f;
        Vector3 liftedAmbient = Vector3.Lerp(ambient, Vector3.One, towardWhite);
        return Vector3.Lerp(diffuse, liftedAmbient, .75f);
    }
}
