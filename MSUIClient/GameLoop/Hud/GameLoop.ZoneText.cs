using System.Numerics;
using ImGuiNET;
using MSUIClient.Engine.UI;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private ZoneTextIdentity? _zoneTextIdentity;
    private ZoneSplashState? _zoneTextSplash;
    private ZoneSplashState? _subZoneTextSplash;

    private readonly record struct ZoneSplashState(
        double StartedAt, string Text, string Extra, Vector4 Tint, bool TerritorySeat);

    private void UpdateZoneTextIdentity(ZoneTextIdentity next, MinimapZonePvpInfo pvp)
    {
        ZoneTextIdentity? previous = _zoneTextIdentity;
        ZoneChangeKind change = ZoneTextUiLaw.Elect(previous, next);
        if (change == ZoneChangeKind.None) return;

        double now = NowSeconds();
        if (ZoneTextUiLaw.ShowZone(previous, next, change))
        {
            string territory = change == ZoneChangeKind.NewArea
                ? pvp.TerritoryLine ?? "" : "";
            _zoneTextSplash = new(now, next.ZoneText, territory,
                ZoneTextUiLaw.ZoneTint(pvp), TerritorySeat: territory.Length > 0);
        }
        if (ZoneTextUiLaw.ShowSubZone(previous, next, change))
        {
            _subZoneTextSplash = new(now, next.SubZoneText,
                pvp.IsArena ? MinimapUiLaw.ArenaText : "",
                ZoneTextUiLaw.SubZoneTint(pvp), ZoneTextUiLaw.HasTerritorySeat(pvp));
        }
        _zoneTextIdentity = next;
    }

    private void DrawZoneTextSplash()
    {
        if (_zoneTextSplash is null && _subZoneTextSplash is null) return;
        double now = NowSeconds();
        float scale = GameplayUiScale();
        Vector2 center = ZoneTextUiLaw.FrameCenter(ImGui.GetIO().DisplaySize, scale);
        ImDrawListPtr draw = ImGui.GetBackgroundDrawList();
        int zoneEm = GameText.EmPixels("ZoneTextFont", scale);
        int subEm = GameText.EmPixels("SubZoneTextFont", scale);

        if (_zoneTextSplash is ZoneSplashState zone)
        {
            float alpha = ZoneTextUiLaw.Alpha(now - zone.StartedAt);
            if (alpha <= 0) _zoneTextSplash = null;
            else
            {
                GameText.DrawCentered(draw, "ZoneTextFont", zone.Text, center, scale,
                    WithAlpha(zone.Tint, alpha));
                if (zone.Extra.Length > 0)
                    GameText.DrawCentered(draw, "SubZoneTextFont", zone.Extra,
                        ZoneTextUiLaw.ZoneExtraCenter(center, zoneEm, subEm), scale,
                        WithAlpha(zone.Tint, alpha));
            }
        }

        if (_subZoneTextSplash is ZoneSplashState sub)
        {
            float alpha = ZoneTextUiLaw.Alpha(now - sub.StartedAt);
            if (alpha <= 0) _subZoneTextSplash = null;
            else
            {
                Vector2 subCenter = ZoneTextUiLaw.SubZoneCenter(center,
                    zoneEm, subEm, sub.TerritorySeat);
                if (sub.Text.Length > 0)
                    GameText.DrawCentered(draw, "SubZoneTextFont", sub.Text, subCenter, scale,
                        WithAlpha(sub.Tint, alpha));
                if (sub.Extra.Length > 0)
                    GameText.DrawCentered(draw, "SubZoneTextFont", sub.Extra,
                        ZoneTextUiLaw.SubZoneExtraCenter(subCenter, subEm), scale,
                        WithAlpha(ZoneTextUiLaw.ArenaTint, alpha));
            }
        }
    }

    private static uint WithAlpha(Vector4 color, float alpha) =>
        ImGui.ColorConvertFloat4ToU32(color with { W = color.W * alpha });
}
