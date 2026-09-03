using System.Numerics;
using ImGuiNET;
using MSUIClient.Engine.UI;
using MSUIClient.Net;

namespace MSUIClient;

/// <summary>
/// Companions window (COMPANIONS v1). Lists the account's characters — name, level,
/// race and class, and a status word — with Summon / Dismiss per row, plus Refresh and
/// Close. Opened with /companions (alias /comp) or the addon-style Companions button
/// on the minimap (GameLoop/Hud/GameLoop.Minimap.cs, DrawMinimapCompanionsButton:
/// on the ring in normal view, on the square map's right edge in Command View). The
/// console no longer carries a Companions button.
///
/// Rendered the SuperUI way — DrawVanillaPanelChrome + GameText on the window draw
/// list + VanillaButton. No ImGui widgets, so it enrolls in GameplayImguiPolicyLaw's
/// clean list. State and wire handling live in GameLoop/Scene/GameLoop.CompanionRoster.cs.
/// </summary>
public sealed partial class GameLoop
{
    private bool _companionsOpen;

    /// <summary>First visible row when the list is taller than the window.</summary>
    private int _companionsScroll;

    private const float CompanionsRowHeight = 22f;
    private const float CompanionsNameColumnWidth = 104f;
    private const float CompanionsButtonWidth = 70f;
    private const float CompanionsStatusColumnWidth = 78f;

    private void ToggleCompanionsPanel()
    {
        if (_companionsOpen) { _companionsOpen = false; return; }
        OpenCompanionsPanel();
    }

    /// <summary>Open the window; a capable server is asked for a fresh list at once.</summary>
    private void OpenCompanionsPanel()
    {
        _companionsOpen = true;
        _companionsScroll = 0;
        if (_companionsAvailable) RequestCompanionList();
    }

    private void DrawCompanionsPanel()
    {
        if (!_companionsOpen || _net is null || _gameplayArt is null) return;
        float scale = GameplayUiScale();

        ImGui.SetNextWindowSize(new Vector2(420f, 380f) * scale, ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(
            new Vector2(380f, 220f) * scale, new Vector2(float.MaxValue, float.MaxValue));
        ImGui.SetNextWindowBgAlpha(0f);
        if (!ImGui.Begin("###companions", ref _companionsOpen,
                ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoBackground |
                ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoScrollbar |
                ImGuiWindowFlags.NoScrollWithMouse))
        {
            ImGui.End();
            return;
        }
        DrawVanillaPanelChrome("Companions", scale, ref _companionsOpen);

        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        Vector2 wMin = ImGui.GetWindowPos();
        Vector2 wMax = wMin + ImGui.GetWindowSize();
        float edge = 11f * scale;

        // A denser scrim so dim text lifts off a bright world, kept inside the border
        // and clear of the plaque (same treatment as the raid info / stable panels).
        float plaqueBottom = wMin.Y + 52f * scale;
        dl.AddRectFilled(
            new Vector2(wMin.X + edge, plaqueBottom + 2f * scale),
            new Vector2(wMax.X - edge, wMax.Y - edge), 0xc00b0e12);

        // Explicit-position layout under one captured origin. The footer (status
        // line + Refresh/Close) is reserved from the bottom; the list gets the rest.
        Vector2 c0 = new(wMin.X + edge + 8f * scale, plaqueBottom + 10f * scale);
        float innerWidth = (wMax.X - edge - 8f * scale) - c0.X;
        float footerTop = wMax.Y - edge - 50f * scale;

        if (!_companionsAvailable)
        {
            GameText.Draw(dl, "GameFontNormalSmall", "This server does not support companions.",
                c0, scale, 0xff9aa4ab);
        }
        else
        {
            DrawCompanionsList(dl, c0, scale, innerWidth, footerTop);
        }

        DrawCompanionsFooter(dl, wMin, wMax, edge, scale);
        ImGui.End();
    }

    private void DrawCompanionsList(ImDrawListPtr dl, Vector2 c0, float scale, float innerWidth,
        float footerTop)
    {
        float y = 0f;
        CompanionRow[] rows = _companionRows;
        int summoned = 0;
        foreach (CompanionRow row in rows) if (row.IsCompanion) summoned++;

        GameText.Draw(dl, "GameFontNormal", "Your characters",
            c0 + new Vector2(0, y) * scale, scale, VanillaGold);
        GameText.DrawRightAligned(dl, "GameFontNormalSmall",
            $"Companions: {summoned}/{CompanionWire.MaxCompanions}",
            new Vector2(c0.X + innerWidth, c0.Y + (y + 3f) * scale), scale, 0xff9aa4ab);
        y += 20f;
        float lineRight = c0.X + innerWidth;
        dl.AddLine(c0 + new Vector2(0, y) * scale,
            new Vector2(lineRight, c0.Y + y * scale), 0xff2a343d, MathF.Max(1f, scale));
        y += 4f;

        if (rows.Length == 0)
        {
            GameText.Draw(dl, "GameFontNormalSmall",
                _companionsEverListed
                    ? "No other characters on this account."
                    : "Requesting your characters…",
                c0 + new Vector2(0, y) * scale, scale, 0xff9aa4ab);
            return;
        }

        // Visible window of rows: scroll with the wheel while the list is hovered.
        float listTop = c0.Y + y * scale;
        float listBottom = footerTop - 4f * scale;
        int visible = Math.Max(1, (int)MathF.Floor((listBottom - listTop) / (CompanionsRowHeight * scale)));
        int maxScroll = Math.Max(0, rows.Length - visible);
        _companionsScroll = Math.Clamp(_companionsScroll, 0, maxScroll);
        bool listHovered = ImGui.IsWindowHovered() &&
            ImGui.IsMouseHoveringRect(new Vector2(c0.X, listTop), new Vector2(lineRight, listBottom));
        if (listHovered && maxScroll > 0)
        {
            float wheel = ImGui.GetIO().MouseWheel;
            if (wheel > 0f) _companionsScroll = Math.Max(0, _companionsScroll - 1);
            else if (wheel < 0f) _companionsScroll = Math.Min(maxScroll, _companionsScroll + 1);
        }

        bool inWorld = _net is { IsInWorld: true };
        float classX = CompanionsNameColumnWidth + 6f;
        float buttonX = innerWidth / scale - CompanionsButtonWidth;
        float statusRight = buttonX - 10f;
        float classBoxWidth = MathF.Max(40f, statusRight - CompanionsStatusColumnWidth - classX - 6f);
        int last = Math.Min(rows.Length, _companionsScroll + visible);
        for (int i = _companionsScroll; i < last; i++)
        {
            CompanionRow row = rows[i];
            var rowMin = new Vector2(c0.X, c0.Y + (y - 2f) * scale);
            var rowSize = new Vector2(innerWidth, CompanionsRowHeight * scale);
            if ((i & 1) == 1) dl.AddRectFilled(rowMin, rowMin + rowSize, 0x14ffffff);

            // Name — the played character in gold, live companions in green, the rest plain.
            uint nameColor = row.IsPlaying ? VanillaGold
                : row.IsCompanion ? 0xff40d040u
                : row.State == CompanionWire.StateUnavailable ? 0xff7f888fu
                : 0xffd8e0e6u;
            string name = GameText.EllipsizeToBox("GameFontNormalSmall", row.Name,
                CompanionsNameColumnWidth, CompanionsRowHeight, scale);
            GameText.Draw(dl, "GameFontNormalSmall", name,
                c0 + new Vector2(0, y + 2f) * scale, scale, nameColor);

            // Level, race and class.
            string detail = GameText.EllipsizeToBox("GameFontNormalSmall",
                $"{row.Level} {RaceName(row.Race)} {ClassName(row.Class)}",
                classBoxWidth, CompanionsRowHeight, scale);
            GameText.Draw(dl, "GameFontNormalSmall", detail,
                c0 + new Vector2(classX, y + 2f) * scale, scale, 0xffb8c0c6);

            // Status word, right-aligned before the button column. A summoned companion
            // that the control roster already carries is one the Command View can drive.
            string status = CompanionWire.StateWord(row.State);
            uint statusColor = row.State switch
            {
                CompanionWire.StateCompanion => IsOwnCompanion(row.Guid) ? 0xff40d040u : 0xff9ad0a0u,
                CompanionWire.StateLoading => 0xff2fd0ffu,
                CompanionWire.StatePlaying => VanillaGold,
                CompanionWire.StateUnavailable => 0xff7f888fu,
                _ => 0xff9aa4abu,
            };
            GameText.DrawRightAligned(dl, "GameFontNormalSmall", status,
                new Vector2(c0.X + statusRight * scale, c0.Y + (y + 2f) * scale), scale, statusColor);

            // One button per row: Summon (state 0) / Dismiss (state 1); none otherwise.
            bool pending = IsCompanionActionPending(row.Guid);
            var btnSize = new Vector2(CompanionsButtonWidth, 18f);
            Vector2 btnMin = c0 + new Vector2(buttonX, y) * scale;
            if (row.Summonable)
            {
                if (VanillaButton(dl, $"##companion-summon-{row.Guid:X}", "Summon", btnMin, btnSize,
                        scale, inWorld && !pending, "GameFontNormalSmall", "GameFontHighlightSmall",
                        "GameFontDisableSmall") && inWorld && !pending)
                    RequestCompanionSummon(row.Guid);
            }
            else if (row.IsCompanion)
            {
                if (VanillaButton(dl, $"##companion-dismiss-{row.Guid:X}", "Dismiss", btnMin, btnSize,
                        scale, inWorld && !pending, "GameFontNormalSmall", "GameFontHighlightSmall",
                        "GameFontDisableSmall") && inWorld && !pending)
                    RequestCompanionDismiss(row.Guid);
            }
            y += CompanionsRowHeight;
        }

        if (maxScroll > 0)
        {
            // A dim page marker so the player knows the list continues.
            GameText.DrawRightAligned(dl, "GameFontDisableSmall",
                $"{_companionsScroll + 1}-{last} of {rows.Length}",
                new Vector2(lineRight, listBottom - 14f * scale), scale, 0xff7f888f);
        }
    }

    private void DrawCompanionsFooter(ImDrawListPtr dl, Vector2 wMin, Vector2 wMax, float edge,
        float scale)
    {
        var buttonSize = new Vector2(76f, 20f);
        float rowY = wMax.Y - edge - buttonSize.Y * scale - 4f * scale;
        float left = wMin.X + edge + 8f * scale;
        float right = wMax.X - edge - 8f * scale;

        // Last verdict, drawn as game text above the buttons (never an ImGui tooltip).
        if (_companionsStatus.Length > 0)
        {
            string status = GameText.EllipsizeToBox("GameFontNormalSmall", _companionsStatus,
                (right - left) / scale, 14f, scale);
            GameText.Draw(dl, "GameFontNormalSmall", status,
                new Vector2(left, rowY - 18f * scale), scale,
                _companionsStatusIsError ? 0xff4040ffu : 0xff9ad0a0u);
        }

        bool canAsk = _companionsAvailable && _net is { IsInWorld: true };
        if (VanillaButton(dl, "##companions-refresh", "Refresh", new Vector2(left, rowY), buttonSize,
                scale, canAsk) && canAsk)
            RequestCompanionList();

        if (VanillaButton(dl, "##companions-close", "Close",
                new Vector2(right - buttonSize.X * scale, rowY), buttonSize, scale))
            _companionsOpen = false;
    }
}
