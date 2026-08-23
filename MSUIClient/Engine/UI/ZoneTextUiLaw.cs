using System.Numerics;

namespace MSUIClient.Engine.UI;

public enum ZoneChangeKind { None, Changed, Indoors, NewArea }

public readonly record struct ZoneTextIdentity(
    uint ZoneId, string ZoneText, string SubZoneText, bool Indoor);

/// <summary>Current Benilla ZoneText event, fade, color, and authored-seat laws.</summary>
public static class ZoneTextUiLaw
{
    public const double FadeInSeconds = .5;
    public const double HoldSeconds = 1.0;
    public const double FadeOutSeconds = 2.0;
    public const float FrameBottomOffset = 512f;
    public const float FrameSize = 128f;

    public static readonly Vector4 UnheldTint = new(1f, .9294f, .7607f, 1f);
    public static readonly Vector4 ArenaTint = new(1f, .1f, .1f, 1f);

    public static ZoneChangeKind Elect(ZoneTextIdentity? previous, ZoneTextIdentity next)
    {
        if (previous is not ZoneTextIdentity old || old.ZoneId != next.ZoneId)
            return ZoneChangeKind.NewArea;
        if (old.ZoneText != next.ZoneText || old.SubZoneText != next.SubZoneText)
            return next.Indoor ? ZoneChangeKind.Indoors : ZoneChangeKind.Changed;
        return ZoneChangeKind.None;
    }

    public static bool ShowZone(ZoneTextIdentity? previous, ZoneTextIdentity next,
        ZoneChangeKind change) =>
        change is ZoneChangeKind.Indoors or ZoneChangeKind.NewArea &&
        (previous is not ZoneTextIdentity old || old.ZoneText != next.ZoneText);

    public static bool ShowSubZone(ZoneTextIdentity? previous, ZoneTextIdentity next,
        ZoneChangeKind change) => change != ZoneChangeKind.None &&
        (previous is not ZoneTextIdentity old || old.SubZoneText != next.SubZoneText);

    public static float Alpha(double elapsedSeconds)
    {
        if (elapsedSeconds < 0) return 0;
        if (elapsedSeconds < FadeInSeconds) return (float)(elapsedSeconds / FadeInSeconds);
        if (elapsedSeconds < FadeInSeconds + HoldSeconds) return 1f;
        if (elapsedSeconds < FadeInSeconds + HoldSeconds + FadeOutSeconds)
            return (float)(1 - (elapsedSeconds - FadeInSeconds - HoldSeconds) / FadeOutSeconds);
        return 0;
    }

    public static Vector4 ZoneTint(MinimapZonePvpInfo pvp) =>
        pvp.Type == MinimapZonePvpType.Unknown ? UnheldTint : pvp.Tint;

    public static Vector4 SubZoneTint(MinimapZonePvpInfo pvp) =>
        pvp.IsArena ? ArenaTint : ZoneTint(pvp);

    public static bool HasTerritorySeat(MinimapZonePvpInfo pvp) =>
        pvp.Type != MinimapZonePvpType.Unknown;

    public static Vector2 FrameCenter(Vector2 displayPixels, float scale) =>
        new(displayPixels.X * .5f,
            displayPixels.Y - (FrameBottomOffset + FrameSize * .5f) * scale);

    public static Vector2 ZoneExtraCenter(Vector2 center, float zoneEm, float subZoneEm) =>
        new(center.X, center.Y + (zoneEm + subZoneEm) * .5f);

    public static Vector2 SubZoneCenter(Vector2 center, float zoneEm, float subZoneEm,
        bool territorySeat) => new(center.X,
            center.Y + zoneEm * .5f + subZoneEm * (territorySeat ? 1.5f : .5f));

    public static Vector2 SubZoneExtraCenter(Vector2 subZoneCenter, float subZoneEm) =>
        new(subZoneCenter.X, subZoneCenter.Y + subZoneEm);
}
