using System.Numerics;
using ImGuiNET;
using MSUIClient.Engine.UI;
using MSUIClient.Net;

namespace MSUIClient;

/// <summary>
/// Raid Info window (spec P1). Lists every instance this character is saved to
/// with a live reset countdown, and offers a reset of the instances it is not
/// saved to. Opened with /raidinfo (aliases /raid, /saved).
///
/// Drawn the SuperUI panel way — the riveted DrawVanillaPanelChrome frame with
/// content painted by GameText on the window draw list and a VanillaButton for the
/// action, matching the party quest log. No ImGui widgets: this reads as a WoW
/// frame, not a debug window.
///
/// All rows come from SMSG_RAID_INSTANCE_INFO via <see cref="_raidLockouts"/>; the
/// panel only reads that state and re-pulls (rate-limited) while it is open, so the
/// countdown stays honest without holding a socket open.
/// </summary>
public sealed partial class GameLoop
{
    private bool _raidInfoOpen;

    private const float RaidInfoRowHeight = 20f;
    private const float RaidInfoResetColumnWidth = 118f;

    private void ToggleRaidInfoPanel()
    {
        if (_raidInfoOpen) { _raidInfoOpen = false; return; }
        _raidInfoOpen = true;
        RequestRaidLockouts("raid info opened", force: true);
    }

    private void DrawRaidInfoPanel()
    {
        if (!_raidInfoOpen || _net is null || _gameplayArt is null) return;
        // Keep the countdown live: the pull is rate-limited internally, so calling it
        // every frame the panel is open just refreshes it every few seconds.
        RequestRaidLockouts("raid info watching");
        float scale = GameplayUiScale();

        ImGui.SetNextWindowSize(new Vector2(340f, 300f) * scale, ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(
            new Vector2(300f, 200f) * scale, new Vector2(float.MaxValue, float.MaxValue));
        ImGui.SetNextWindowBgAlpha(0f);
        if (!ImGui.Begin("###raid-info", ref _raidInfoOpen,
                ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoBackground |
                ImGuiWindowFlags.NoCollapse))
        {
            ImGui.End();
            return;
        }
        DrawVanillaPanelChrome("Raid Info", scale, ref _raidInfoOpen);

        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        Vector2 wMin = ImGui.GetWindowPos();
        Vector2 wMax = wMin + ImGui.GetWindowSize();
        float edge = 11f * scale;

        // A denser scrim so dim text lifts off a bright world, kept inside the border
        // and clear of the plaque (same treatment as the party quest log).
        float plaqueBottom = wMin.Y + 52f * scale;
        dl.AddRectFilled(
            new Vector2(wMin.X + edge, plaqueBottom + 2f * scale),
            new Vector2(wMax.X - edge, wMax.Y - edge), 0xc00b0e12);

        // Explicit-position layout under one captured origin.
        Vector2 c0 = new(wMin.X + edge + 8f * scale, plaqueBottom + 10f * scale);
        float innerWidth = (wMax.X - edge - 8f * scale) - c0.X;
        float y = 0f;

        y = DrawRaidInfoContent(dl, c0, y, scale, innerWidth);
        DrawRaidInfoResetButton(dl, wMin, wMax, edge, scale);
        ImGui.End();
    }

    private float DrawRaidInfoContent(ImDrawListPtr dl, Vector2 c0, float y, float scale, float innerWidth)
    {
        IReadOnlyList<RaidLockout> lockouts = RaidLockouts;
        if (lockouts.Count == 0)
        {
            GameText.Draw(dl, "GameFontNormalSmall",
                RaidInfoEverReceived
                    ? "You are not saved to any instances."
                    : "Requesting saved instances…",
                c0 + new Vector2(0, y) * scale, scale, 0xff9aa4ab);
            return y + RaidInfoRowHeight;
        }

        GameText.Draw(dl, "GameFontNormal", "Saved instances",
            c0 + new Vector2(0, y) * scale, scale, VanillaGold);
        y += 20f;
        float lineRight = c0.X + innerWidth;
        dl.AddLine(c0 + new Vector2(0, y) * scale,
            new Vector2(lineRight, c0.Y + y * scale), 0xff2a343d, MathF.Max(1f, scale));
        y += 4f;

        double now = NowSeconds();
        float nameBoxWidth = innerWidth - RaidInfoResetColumnWidth;
        foreach (RaidLockout lockout in lockouts)
        {
            string name = GameText.EllipsizeToBox("GameFontNormalSmall",
                RaidMapName(lockout.MapId), nameBoxWidth, RaidInfoRowHeight, scale);
            GameText.Draw(dl, "GameFontNormalSmall", name,
                c0 + new Vector2(0, y) * scale, scale, 0xffd8e0e6);

            long left = lockout.SecondsLeft(now);
            string when = left <= 0 ? "expired"
                : QuestFrameUiLaw.SecondsToTime(left, noSeconds: true);
            // Amber as a lock nears reset (< 1h), otherwise soft grey.
            uint color = left > 0 && left < 3600 ? 0xff2fd0ff : 0xff9aa4ab;
            GameText.DrawRightAligned(dl, "GameFontNormalSmall", when,
                new Vector2(lineRight, c0.Y + y * scale), scale, color);
            y += RaidInfoRowHeight;
        }
        return y;
    }

    private void DrawRaidInfoResetButton(ImDrawListPtr dl, Vector2 wMin, Vector2 wMax,
        float edge, float scale)
    {
        var buttonSize = new Vector2(132f, 22f);
        var min = new Vector2(wMin.X + edge + 8f * scale,
            wMax.Y - edge - buttonSize.Y * scale - 4f * scale);

        // The constraint hint is drawn as game text rather than an ImGui tooltip:
        // gameplay windows render through GameText, not ImGui widgets (see
        // GameplayImguiPolicyLaw). Sits just above the button, dim.
        GameText.Draw(dl, "GameFontDisableSmall",
            "Resets instances you are not saved to.",
            new Vector2(min.X, min.Y - 15f * scale), scale, 0xff7f888f);

        bool inWorld = _net is { IsInWorld: true };
        if (VanillaButton(dl, "##raid-info-reset", "Reset Dungeons", min, buttonSize, scale, inWorld)
            && inWorld && !RefuseTacticalFreezeLiveCommand("resetting dungeon instances"))
            _net?.ResetInstances();
    }
}
