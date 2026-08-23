using System.Numerics;
using ImGuiNET;
using MSUIClient.Engine.UI;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private void ToggleGuildControlFrame()
    {
        if (_guildControlOpen)
        {
            _guildControlOpen = false;
            _guildControlDropDownOpen = false;
            return;
        }
        if (CurrentGuildRank() != 0 || _guildRankRights.Count == 0) return;
        LoadGuildControlRank(0);
        _guildInfoOpen = false;
        _guildMemberDetailOpen = false;
        _guildControlOpen = true;
    }

    private void LoadGuildControlRank(int rank)
    {
        int count = Math.Min(_guildRankRights.Count, _guildRankNames.Length);
        _guildControlRank = count == 0 ? 0 : Math.Clamp(rank, 0, count - 1);
        _guildControlRights = _guildControlRank < _guildRankRights.Count
            ? _guildRankRights[_guildControlRank] : 0;
        _guildControlRankName = _guildControlRank < _guildRankNames.Length
            ? _guildRankNames[_guildControlRank] : "";
        _guildControlDirty = false;
        _guildControlDropDownOpen = false;
    }

    private int GuildBottomRankMembers()
    {
        if (_guildRankRights.Count == 0) return 0;
        uint bottom = (uint)_guildRankRights.Count - 1;
        return _guildMembers.Count(member => member.Rank == bottom);
    }

    private void SaveGuildControlRank()
    {
        if (!_guildControlDirty) return;
        _guildControlRankName = _guildControlRankName.Length <=
            GuildFrameUiLaw.RankNameMaxLetters
            ? _guildControlRankName
            : _guildControlRankName[..GuildFrameUiLaw.RankNameMaxLetters];
        _net?.GuildRank((uint)_guildControlRank, _guildControlRights,
            _guildControlRankName);
        if (_guildControlRank < _guildRankNames.Length)
            _guildRankNames[_guildControlRank] = _guildControlRankName;
        _guildControlDirty = false;
        _guildControlOpen = false;
    }

    private void DrawGuildControlFrame()
    {
        if (!_guildControlOpen) return;
        if (!_guildOpen || _gameplayArt is null || CurrentGuildRank() != 0)
        {
            _guildControlOpen = false;
            return;
        }
        int rankCount = Math.Min(_guildRankRights.Count, _guildRankNames.Length);
        if (rankCount == 0)
        {
            _guildControlOpen = false;
            return;
        }

        float scale = GameplayUiScale();
        Vector2 guildOrigin = UiPanelFrameOrigin(UiPanelOwnershipRegistry[14], scale);
        Vector2 origin = GuildFrameUiLaw.ControlFrameOrigin(guildOrigin, scale);
        Vector2 size = GuildFrameUiLaw.ControlFrame.Size * scale;
        ImGui.SetNextWindowPos(origin, ImGuiCond.Always);
        ImGui.SetNextWindowSize(size, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0);
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBackground |
            ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse;
        if (!ImGui.Begin("##guild-control-frame", flags))
        {
            ImGui.End();
            return;
        }

        ImDrawListPtr draw = ImGui.GetWindowDrawList();
        DrawGuildControlShell(draw, origin, scale);
        GameText.DrawCentered(draw, "GameFontNormal", "Select guild rank to modify:",
            origin + GuildFrameUiLaw.ControlSelectLabel.Center * scale, scale);
        GameText.DrawCentered(draw, "GameFontHighlightSmall", "Allow this rank to:",
            origin + GuildFrameUiLaw.ControlAllowLabel.Center * scale, scale);

        DrawGuildControlRankButtons(draw, origin, scale, rankCount);

        GameText.DrawRightAligned(draw, "GameFontNormal", "Rank Label:",
            origin + GuildFrameUiLaw.ControlRankNameLabelRight * scale, scale);
        GuildFrameUiLaw.LogicalRect edit = GuildFrameUiLaw.ControlRankName;
        Vector2 editMin = origin + edit.Min * scale;
        DrawStaticPopupEditBoxBorder(draw, editMin, scale);
        ImGui.SetCursorScreenPos(editMin + GuildFrameUiLaw.ControlRankNameTextOffset * scale);
        ImGui.SetNextItemWidth(edit.Width * scale);
        ImGui.PushStyleColor(ImGuiCol.FrameBg, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.FrameBgActive, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.Border, Vector4.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, Vector2.Zero);
        if (ImGui.InputText("##guild-control-rank-name", ref _guildControlRankName, 128,
                ImGuiInputTextFlags.EnterReturnsTrue) || ImGui.IsItemActivated())
            _guildControlDirty = true;
        if (_guildControlRankName.Length > GuildFrameUiLaw.RankNameMaxLetters)
            _guildControlRankName = _guildControlRankName[..GuildFrameUiLaw.RankNameMaxLetters];
        ImGui.PopStyleVar();
        ImGui.PopStyleColor(4);

        for (int i = 0; i < GuildFrameUiLaw.RankRightOrder.Length; i++)
            DrawGuildControlCheckbox(draw, origin, scale, i);

        GuildFrameUiLaw.LogicalRect accept = GuildFrameUiLaw.ControlAccept;
        if (VanillaButton(draw, "##guild-control-accept", "Accept",
                origin + accept.Min * scale, accept.Size, scale, _guildControlDirty))
            SaveGuildControlRank();
        GuildFrameUiLaw.LogicalRect cancel = GuildFrameUiLaw.ControlCancel;
        if (VanillaButton(draw, "##guild-control-cancel", "Cancel",
                origin + cancel.Min * scale, cancel.Size, scale))
        {
            _guildControlOpen = false;
            _guildControlDropDownOpen = false;
        }

        // The dropdown list is a top-level overlay in the reference; draw it after the form.
        DrawGuildControlDropDown(draw, origin, scale, rankCount);

        ImGui.End();
    }

    private void DrawGuildControlShell(ImDrawListPtr draw, Vector2 origin, float scale)
    {
        void Slice(string texture, GuildFrameUiLaw.LogicalRect rect) =>
            DrawArt(draw, texture, origin + rect.Min * scale, rect.Size, scale);
        Slice(@"Interface\MacroFrame\MacroPopup-TopLeft",
            GuildFrameUiLaw.ControlShellTopLeft);
        Slice(@"Interface\MacroFrame\MacroPopup-TopRight",
            GuildFrameUiLaw.ControlShellTopRight);
        Slice(@"Interface\MacroFrame\MacroPopup-BotLeft",
            GuildFrameUiLaw.ControlShellBottomLeft);
        Slice(@"Interface\MacroFrame\MacroPopup-BotRight",
            GuildFrameUiLaw.ControlShellBottomRight);
    }

    private void DrawGuildControlDropDown(ImDrawListPtr draw, Vector2 origin, float scale,
        int rankCount)
    {
        GuildFrameUiLaw.LogicalRect box = GuildFrameUiLaw.ControlDropDown;
        Vector2 min = origin + box.Min * scale;
        _skin?.DrawBackdrop(draw, min, min + box.Size * scale, WowSkin.Tooltip,
            UnitPopupUiLaw.MenuBackdropFillTint, UnitPopupUiLaw.MenuBackdropEdgeTint);
        GameText.Draw(draw, "GameFontHighlightSmall", _guildControlRankName,
            min + GuildFrameUiLaw.ControlDropDownTextOffset * scale, scale);
        GuildFrameUiLaw.LogicalRect arrowRect = GuildFrameUiLaw.ControlDropDownArrow;
        Vector2 arrowMin = origin + arrowRect.Min * scale;
        ImGui.SetCursorScreenPos(min);
        if (ImGui.InvisibleButton("##guild-control-dropdown", box.Size * scale))
            _guildControlDropDownOpen = !_guildControlDropDownOpen;
        uint arrow = _gameplayArt?.Handle(@"Interface\ChatFrame\UI-ChatIcon-ScrollDown-Up") ?? 0;
        if (arrow != 0) draw.AddImage((nint)arrow, arrowMin,
            arrowMin + arrowRect.Size * scale);
        if (!_guildControlDropDownOpen) return;

        GuildFrameUiLaw.LogicalRect list = GuildFrameUiLaw.ControlDropDownList(rankCount);
        Vector2 listMin = origin + list.Min * scale;
        _skin?.DrawBackdrop(draw, listMin, listMin + list.Size * scale, WowSkin.Tooltip,
            UnitPopupUiLaw.MenuBackdropFillTint, UnitPopupUiLaw.MenuBackdropEdgeTint);
        for (int i = 0; i < rankCount; i++)
        {
            GuildFrameUiLaw.LogicalRect row = GuildFrameUiLaw.ControlDropDownRow(i);
            Vector2 rowMin = origin + row.Min * scale;
            ImGui.SetCursorScreenPos(rowMin);
            bool clicked = ImGui.InvisibleButton($"##guild-control-rank-{i}", row.Size * scale);
            if (ImGui.IsItemHovered())
            {
                uint hi = _gameplayArt?.AdditiveHandle(
                    @"Interface\QuestFrame\UI-QuestTitleHighlight") ?? 0;
                if (hi != 0) draw.AddImage((nint)hi, rowMin, rowMin + row.Size * scale);
            }
            GameText.Draw(draw, "GameFontHighlightSmall", _guildRankNames[i],
                rowMin + GuildFrameUiLaw.ControlDropDownRowTextOffset * scale, scale);
            if (clicked) LoadGuildControlRank(i);
        }
    }

    private void DrawGuildControlRankButtons(ImDrawListPtr draw, Vector2 origin, float scale,
        int rankCount)
    {
        if (DrawGuildControlMiniButton(draw, "##guild-control-add-rank",
                origin, GuildFrameUiLaw.ControlAddRank, scale, plus: true,
                enabled: rankCount < 10))
            ShowGuildAddRankPopup();

        bool showRemove = GuildFrameUiLaw.ShowRemoveRank(_guildControlRank, rankCount);
        bool canRemove = GuildFrameUiLaw.CanRemoveRank(_guildControlRank, rankCount,
            GuildBottomRankMembers());
        if (showRemove && DrawGuildControlMiniButton(draw, "##guild-control-remove-rank",
                origin, GuildFrameUiLaw.ControlRemoveRank, scale, plus: false,
                enabled: canRemove))
        {
            _net?.GuildDeleteRank();
            LoadGuildControlRank(0);
        }
    }

    private bool DrawGuildControlMiniButton(ImDrawListPtr draw, string id, Vector2 origin,
        GuildFrameUiLaw.LogicalRect rect, float scale, bool plus, bool enabled)
    {
        Vector2 min = origin + rect.Min * scale;
        Vector2 size = rect.Size * scale;
        ImGui.SetCursorScreenPos(min);
        if (!enabled) ImGui.BeginDisabled();
        bool clicked = ImGui.InvisibleButton(id, size);
        bool held = enabled && ImGui.IsItemActive();
        bool hovered = enabled && ImGui.IsItemHovered();
        if (!enabled) ImGui.EndDisabled();
        string stem = plus ? "Plus" : "Minus";
        string state = !enabled ? "Disabled" : held ? "Down" : "Up";
        uint art = _gameplayArt?.Handle($@"Interface\Buttons\UI-{stem}Button-{state}") ?? 0;
        if (art != 0) draw.AddImage((nint)art, min, min + size);
        if (hovered)
        {
            uint hi = _gameplayArt?.AdditiveHandle(
                @"Interface\Buttons\UI-PlusButton-Hilight") ?? 0;
            if (hi != 0) draw.AddImage((nint)hi, min, min + size);
            GuildFrameUiLaw.TooltipSeat tooltipSeat =
                GuildFrameUiLaw.RightTooltipSeat(min, size);
            string tooltip = plus ? GuildFrameUiLaw.ControlAddRankTooltip :
                GuildFrameUiLaw.ControlRemoveRankTooltip;
            OfferPreservedSharedGameTooltipRenderer(
                new(plus ? "guild-control-add-rank" : "guild-control-remove-rank", 0),
                () =>
                {
                    ImGui.SetNextWindowPos(tooltipSeat.Anchor, ImGuiCond.Always,
                        tooltipSeat.Pivot);
                    ImGui.BeginTooltip();
                    ImGui.TextUnformatted(tooltip);
                    ImGui.EndTooltip();
                });
        }
        return enabled && clicked;
    }

    private void DrawGuildControlCheckbox(ImDrawListPtr draw, Vector2 origin, float scale,
        int zeroBasedIndex)
    {
        GuildFrameUiLaw.LogicalRect rect = GuildFrameUiLaw.ControlCheckbox(zeroBasedIndex + 1);
        Vector2 min = origin + rect.Min * scale;
        ImGui.SetCursorScreenPos(min);
        bool clicked = ImGui.InvisibleButton($"##guild-right-{zeroBasedIndex}",
            rect.Size * scale);
        bool held = ImGui.IsItemActive();
        bool hovered = ImGui.IsItemHovered();
        uint bit = GuildFrameUiLaw.RankRightOrder[zeroBasedIndex];
        bool checkedNow = (_guildControlRights & bit) != 0;
        string boxState = held ? "Down" : "Up";
        uint box = _gameplayArt?.Handle($@"Interface\Buttons\UI-CheckBox-{boxState}") ?? 0;
        if (box != 0) draw.AddImage((nint)box, min, min + rect.Size * scale);
        if (checkedNow)
        {
            uint mark = _gameplayArt?.Handle(@"Interface\Buttons\UI-CheckBox-Check") ?? 0;
            if (mark != 0) draw.AddImage((nint)mark, min, min + rect.Size * scale);
        }
        if (hovered)
        {
            uint hi = _gameplayArt?.AdditiveHandle(
                @"Interface\Buttons\UI-CheckBox-Highlight") ?? 0;
            if (hi != 0) draw.AddImage((nint)hi, min, min + rect.Size * scale);
        }
        GameText.Draw(draw, "GameFontNormalSmall",
            GuildFrameUiLaw.RankRightLabels[zeroBasedIndex],
            min + GuildFrameUiLaw.ControlCheckboxLabelOffset * scale, scale);
        if (!clicked) return;
        checkedNow = !checkedNow;
        if (checkedNow) _guildControlRights |= bit;
        else _guildControlRights &= ~bit;
        _guildControlDirty = true;
        PlayUiSound(checkedNow ? "igMainMenuOptionCheckBoxOff" :
            "igMainMenuOptionCheckBoxOn", "ui.guild");
    }
}
