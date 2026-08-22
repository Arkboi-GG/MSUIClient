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
        Vector2 size = new(GameTimeUiLaw.Size * scale);
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

        QuestLogicalRect hit = GameTimeUiLaw.HitRect;
        Vector2 hitMin = origin + new Vector2(hit.X, hit.Y) * scale;
        Vector2 hitSize = new Vector2(hit.Width, hit.Height) * scale;
        ImGui.SetCursorScreenPos(hitMin);
        ImGui.InvisibleButton("##game-time-frame", hitSize);
        if (ImGui.IsItemHovered())
        {
            string prepared = GameTimeUiLaw.ClockText(hour, minute);
            OfferPreservedSharedGameTooltipRenderer(new("game-time-frame", 1),
                () => ImGui.SetTooltip(prepared));
        }
    }
}
