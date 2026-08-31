using System.Numerics;
using MSUIClient.Formats;

namespace MSUIClient.Engine.UI;

public enum QuestHelperPinKind { Kill, Loot, Object, Available, TurnIn }

/// <summary>Pure objective decoding, projection, and styling for the native map-only helper.</summary>
public static class QuestHelperUiLaw
{
    // ImGui packed ABGR.
    public const uint KillColor = 0xff4444e8;
    public const uint LootColor = 0xffffb84a;
    public const uint ObjectColor = 0xff48b8ff;
    public const uint AvailableColor = 0xff00d8ff;
    public const uint TurnInColor = 0xff35d8ff;
    public const uint BorderColor = 0xee101010;
    // Questie-compatible defaults: its 16px source frame is scaled to .6 on the world map
    // and .7 on the minimap. Keeping those logical sizes avoids the oversized punctuation that
    // previously dominated the parchment map at ordinary UI scales.
    public const float WorldMapPinSize = 9.6f;
    public const float MinimapPinSize = 11.2f;
    public const float WorldMapClusterPixels = 24f;
    public const float MinimapClusterPixels = 13f;

    // Punctuation is intentionally 20% larger than objective sacks, matching the familiar
    // addon hierarchy. Both draw surfaces and the visual verifier consume these exact values.
    public static float WorldMapMarkerSize(QuestHelperPinKind kind) =>
        WorldMapPinSize * PunctuationScale(kind);

    public static float MinimapMarkerSize(QuestHelperPinKind kind) =>
        MinimapPinSize * PunctuationScale(kind);

    private static float PunctuationScale(QuestHelperPinKind kind) =>
        kind is QuestHelperPinKind.Available or QuestHelperPinKind.TurnIn ? 1.2f : 1f;

    public static bool QuestComplete(uint packedCounters) =>
        ((packedCounters >> 24) & 1u) != 0;

    public static uint ObjectiveProgress(uint packedCounters, int index, uint required) =>
        Math.Min((packedCounters >> (6 * Math.Clamp(index, 0, 3))) & 0x3fu, required);

    public static bool ObjectiveIsObject(uint creatureOrGo) =>
        (creatureOrGo & 0x8000_0000u) != 0;

    public static uint ObjectiveEntry(uint creatureOrGo) => ObjectiveIsObject(creatureOrGo)
        ? unchecked((uint)-unchecked((int)creatureOrGo))
        : creatureOrGo;

    public static bool MatchesMask(uint mask, byte id) =>
        mask == 0 || id is >= 1 and <= 32 && (mask & (1u << (id - 1))) != 0;

    public static bool LevelAppropriate(byte playerLevel, byte minimumLevel, byte questLevel) =>
        playerLevel >= minimumLevel && (questLevel == 0 || questLevel + 5 >= playerLevel);

    public static Vector3 WorldPosition(in WorldMapAreaInfo area,
        in QuestHelperSpawn spawn) => new(
            area.Top + spawn.YPercent / 100f * (area.Bottom - area.Top),
            area.Left + spawn.XPercent / 100f * (area.Right - area.Left), 0f);

    public static uint Color(QuestHelperPinKind kind) => kind switch
    {
        QuestHelperPinKind.Kill => KillColor,
        QuestHelperPinKind.Loot => LootColor,
        QuestHelperPinKind.Object => ObjectColor,
        QuestHelperPinKind.Available => AvailableColor,
        QuestHelperPinKind.TurnIn => TurnInColor,
        _ => TurnInColor,
    };
}
