using System.Numerics;
using ImGuiNET;
using MSUIClient.Engine.UI;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private uint _rtsTerritoryMapId;
    private uint _rtsTerritoryZoneId;
    private RtsTerritoryCaptureState? _rtsTerritoryCapture;
    private double _rtsTerritoryCaptureAt;

    private void ResetRtsTerritoryCaptureContext(uint mapId, uint zoneId)
    {
        _rtsTerritoryMapId = mapId;
        _rtsTerritoryZoneId = zoneId;
        _rtsTerritoryCapture = null;
        _rtsTerritoryCaptureAt = 0;
    }

    private void ClearRtsTerritoryCapture()
    {
        _rtsTerritoryMapId = 0;
        _rtsTerritoryZoneId = 0;
        _rtsTerritoryCapture = null;
        _rtsTerritoryCaptureAt = 0;
    }

    private void ApplyRtsTerritoryWorldState(uint id, uint value)
    {
        if (id != RtsWire.TerritoryCaptureWorldStateId) return;
        if (!RtsWire.TryDecodeTerritoryCaptureState(value, out RtsTerritoryCaptureState state) ||
            !state.Visible)
        {
            _rtsTerritoryCapture = null;
            _rtsTerritoryCaptureAt = 0;
            return;
        }

        _rtsTerritoryCapture = state;
        _rtsTerritoryCaptureAt = NowSeconds();
    }

    private void DrawRtsTerritoryCapture()
    {
        if (_rtsTerritoryCapture is not RtsTerritoryCaptureState state ||
            !CommanderMapUiLaw.ShowTerritory(_rtsMode, _rtsModules) ||
            _rtsTerritoryZoneId == 0 || PlayerPanelOpen || SettingsModalOpen)
        {
            // No capture running: still give the editor a rect to place the strip by.
            if (_hudEditMode && _freeView)
                HudFrame("territory-strip", "Territory", HudPlacement.At(HudAnchor.Top, 0f, 72f),
                    new Vector2(400f, 70f));
            return;
        }

        Vector2 display = ImGui.GetIO().DisplaySize;
        float s = Math.Clamp(GameplayUiScale(), 0.7f, 2.25f);
        float width = Math.Clamp(display.X * 0.34f, 300f * s, 520f * s);
        float height = 70f * s;
        // Sized in device pixels above (its own clamped scale); the registry wants logical.
        HudFrameResult strip = HudFrame("territory-strip", "Territory", HudPlacement.At(HudAnchor.Top, 0f, 72f),
            new Vector2(width, height) / GameplayUiScale());
        if (strip.Hidden) return;   // hidden in the active HUD layout (Edit Mode's Hide)
        Vector2 min = strip.ScreenMin;
        Vector2 max = min + new Vector2(width, height);
        ImDrawListPtr dl = ImGui.GetForegroundDrawList();

        dl.AddRectFilled(min, max, 0xE8181410u, 5f * s);
        dl.AddRect(min, max, 0x806E655Bu, 5f * s, ImDrawFlags.None, 1f * s);

        string zone = _areas?.ZoneName(_rtsTerritoryZoneId) is { Length: > 0 } name
            ? name : $"Zone {_rtsTerritoryZoneId}";
        DrawCenteredText(dl, new Vector2(display.X * 0.5f, min.Y + 13f * s),
            zone.ToUpperInvariant(), 11f * s, VanillaGold);

        double elapsed = Math.Max(0, NowSeconds() - _rtsTerritoryCaptureAt);
        int remaining = Math.Max(0, state.RemainingSeconds - (int)Math.Floor(elapsed));
        bool awaiting = state.RemainingSeconds > 0 && remaining == 0 &&
            state.Phase is RtsTerritoryCapturePhase.Contested or RtsTerritoryCapturePhase.Cooldown;
        string status = state.Phase switch
        {
            RtsTerritoryCapturePhase.Stable => $"Controlled by {TerritoryOwnerName(state.Owner)}",
            RtsTerritoryCapturePhase.Contested => awaiting
                ? $"{TerritoryOwnerName(state.Attacker)} assault - Awaiting server"
                : $"{TerritoryOwnerName(state.Attacker)} assault - {remaining}s",
            RtsTerritoryCapturePhase.Cooldown => awaiting
                ? $"{TerritoryOwnerName(state.Owner)} control - Awaiting server"
                : $"{TerritoryOwnerName(state.Owner)} control secured - {remaining}s",
            _ => string.Empty,
        };
        DrawCenteredText(dl, new Vector2(display.X * 0.5f, min.Y + 31f * s),
            status, 12f * s, 0xFFF0E6D2u);

        Vector2 barMin = new(min.X + 18f * s, min.Y + 51f * s);
        Vector2 barMax = new(max.X - 18f * s, min.Y + 61f * s);
        dl.AddRectFilled(barMin, barMax, 0xB02A2826u, 2f * s);
        float fraction = state.Phase == RtsTerritoryCapturePhase.Contested
            ? state.ProgressPermille / 1000f : state.Phase == RtsTerritoryCapturePhase.Stable ? 1f : 0f;
        if (fraction > 0)
        {
            uint fill = state.Attacker == RtsTerritoryOwner.Horde ||
                        (state.Attacker == RtsTerritoryOwner.Neutral && state.Owner == RtsTerritoryOwner.Horde)
                ? 0xE0444ED8u : 0xE0DC8746u;
            dl.AddRectFilled(barMin, new Vector2(
                barMin.X + (barMax.X - barMin.X) * fraction, barMax.Y), fill, 2f * s);
        }
    }

    private static string TerritoryOwnerName(RtsTerritoryOwner owner) => owner switch
    {
        RtsTerritoryOwner.Alliance => "Alliance",
        RtsTerritoryOwner.Horde => "Horde",
        _ => "Neutral",
    };
}
