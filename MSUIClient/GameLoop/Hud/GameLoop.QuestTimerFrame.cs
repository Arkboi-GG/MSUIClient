using System.Numerics;
using ImGuiNET;
using MSUIClient.Engine.UI;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private readonly record struct QuestTimerDisplay(uint QuestId, string Title, long Seconds);

    // Logical height accrued by the managed under-minimap stack this frame. Zero means hidden.
    private float _questTimerFrameHeight;

    private void DrawQuestTimerFrame()
    {
        _questTimerFrameHeight = 0f;
        if (_net is null || _gameplayArt is null || _skin is null ||
            !_entities.TryGet(_net.PlayerGuid, out WorldEntity player)) return;

        EnsureQuestServerTime();
        var timers = new List<QuestTimerDisplay>(QuestTimerFrameUiLaw.MaxTimers);
        foreach (var quest in player.Fields.QuestLog())
        {
            long? seconds = QuestSignedSecondsLeft(quest.Timer, (byte)(quest.Counters >> 24));
            if (seconds is null || seconds.Value < 0) continue;
            timers.Add(new(quest.QuestId,
                _questTitles.GetValueOrDefault(quest.QuestId, $"Quest {quest.QuestId}"),
                seconds.Value));
            if (timers.Count == QuestTimerFrameUiLaw.MaxTimers) break;
        }
        if (timers.Count == 0) return;

        float scale = GameplayUiScale();
        float height = QuestTimerFrameUiLaw.FrameHeight(timers.Count);
        _questTimerFrameHeight = height;
        Vector2 origin = QuestTimerFrameUiLaw.FrameOrigin(ImGui.GetIO().DisplaySize, scale);
        Vector2 size = QuestTimerFrameUiLaw.FrameSize(height, scale);

        ImGui.SetNextWindowPos(origin, ImGuiCond.Always);
        ImGui.SetNextWindowSize(size, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0f);
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBackground |
            ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse;
        if (!ImGui.Begin("##quest-timer-frame", flags))
        {
            ImGui.End();
            return;
        }

        ImDrawListPtr draw = ImGui.GetBackgroundDrawList();
        _skin.DrawBackdrop(draw, origin, origin + size, WowSkin.Dialog);
        DrawArt(draw, @"Interface\DialogFrame\UI-DialogBox-Header",
            origin + QuestTimerFrameUiLaw.HeaderMin * scale,
            QuestTimerFrameUiLaw.HeaderSize, scale);
        GameText.DrawCentered(draw, QuestTimerFrameUiLaw.TitleFont, "Quest Timers",
            origin + QuestTimerFrameUiLaw.TitleCenter * scale, scale);

        for (int i = 0; i < timers.Count; i++)
        {
            QuestTimerDisplay timer = timers[i];
            QuestTimerFrameUiLaw.ScreenRect row = QuestTimerFrameUiLaw.RowScreen(
                origin, i, scale);
            ImGui.SetCursorScreenPos(row.Min);
            ImGui.InvisibleButton($"##quest-timer-{i}", row.Size);
            bool hovered = ImGui.IsItemHovered();
            bool clicked = ImGui.IsItemClicked(ImGuiMouseButton.Left);
            GameText.DrawCentered(draw, QuestTimerFrameUiLaw.RowFont,
                QuestFrameUiLaw.SecondsToTime(timer.Seconds),
                QuestTimerFrameUiLaw.RowTextCenter(row,
                    GameText.EmPixels(QuestTimerFrameUiLaw.RowFont, scale)), scale);
            if (hovered)
            {
                QuestTimerFrameUiLaw.TooltipSeat tooltipSeat =
                    QuestTimerFrameUiLaw.RowTooltipSeat(row);
                OfferOwnerAnchoredSharedGameTooltip(new("quest-timer-row", (ulong)i),
                    [new(timer.Title, GameTooltipTextTone.White)],
                    tooltipSeat.Anchor, tooltipSeat.Pivot);
            }
            if (clicked)
            {
                _questLogSelectedQuestId = timer.QuestId;
                _questLogDetailScroll = 0f;
                _questLogOpen = true;
            }
        }
        ImGui.End();
    }
}
