using System.Numerics;
using ImGuiNET;
using MSUIClient.Engine.UI;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private void DrawGameTimeFrame()
    {
        if (_gameplayArt is null || (_net is null && !HudPreview)) return;
        float scale = GameplayUiScale();
        Vector2 origin = GameTimeUiLaw.FrameOrigin(ImGui.GetIO().DisplaySize, scale);
        Vector2 size = GameTimeUiLaw.FrameSize(scale);
        (int hour, int minute) = GameTimeUiLaw.TimeParts(_worldClock.CurrentHours);
        bool night = GameTimeUiLaw.IsNight(hour, minute);
        uint texture = _gameplayArt.Handle(@"Interface\Minimap\UI-TOD-Indicator");
        if (texture != 0)
        {
            ImDrawListPtr draw = ImGui.GetBackgroundDrawList();
            draw.AddImage((nint)texture, origin, origin + size,
                night ? GameTimeUiLaw.NightUvMin : GameTimeUiLaw.DayUvMin,
                night ? GameTimeUiLaw.NightUvMax : GameTimeUiLaw.DayUvMax);
        }

        GameTimeUiLaw.ScreenRect hit = GameTimeUiLaw.HitScreen(origin, scale);
        // InvisibleButton still needs an authored ImGui window. Submitting it on
        // the implicit Debug##Default window makes Dear ImGui expose that fallback
        // as a large empty "Debug" panel over the world. Give the clock's small
        // hover target its own transparent host instead.
        ImGui.SetNextWindowPos(hit.Min, ImGuiCond.Always);
        ImGui.SetNextWindowSize(hit.Size, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        // GameTimeFrame is the clock beside the minimap - standing furniture, not a UIPanel - so
        // it keeps the display-back flag that VanillaWindowFlags no longer implies. It is the one
        // consumer of that constant which is not a frame the player opens.
        bool hostOpen = ImGui.Begin("##game-time-frame-host",
            VanillaWindowFlags | ImGuiWindowFlags.NoBringToFrontOnFocus);
        ImGui.PopStyleVar();
        if (!hostOpen)
        {
            ImGui.End();
            return;
        }
        ImGui.SetCursorScreenPos(hit.Min);
        ImGui.InvisibleButton("##game-time-frame", hit.Size);
        if (ImGui.IsItemHovered())
        {
            string prepared = GameTimeUiLaw.ClockText(hour, minute);
            GameTimeUiLaw.TooltipSeat tooltipSeat =
                GameTimeUiLaw.BottomLeftTooltipSeat(origin, scale);
            OfferOwnerAnchoredSharedGameTooltip(new("game-time-frame", 1),
                [new(prepared, GameTooltipTextTone.White)],
                tooltipSeat.Anchor, tooltipSeat.Pivot);
        }
        ImGui.End();
    }
}
