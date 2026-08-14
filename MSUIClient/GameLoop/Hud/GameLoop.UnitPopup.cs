using System.Numerics;
using ImGuiNET;
using MSUIClient.Engine.UI;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private ulong _unitPopupGuid;
    private Vector2 _unitPopupPosition;
    private InspectBinding _unitPopupInspectBinding = InspectBinding.Target;
    private UnitPopupWhich _unitPopupWhich = UnitPopupWhich.Player;
    private double _unitPopupAutoCloseAt;
    private bool _unitPopupFocusRequested;
    private bool _unitPopupJustOpened;

    /// <summary>UnitPopup_ShowMenu: refuses cancel-only menus, queries an unknown name.</summary>
    private void OpenUnitPopup(ulong guid, UnitPopupWhich which, Vector2 physicalPosition,
        InspectBinding binding)
    {
        if (guid == 0 || !UnitPopupUiLaw.ShouldOpen(UnitPopupVisibleRows(guid, which))) return;
        _unitPopupGuid = guid;
        _unitPopupWhich = which;
        _unitPopupPosition = physicalPosition;
        _unitPopupInspectBinding = binding;
        _unitPopupAutoCloseAt = NowSeconds() + UnitPopupUiLaw.AutoCloseSeconds;
        _unitPopupFocusRequested = true;
        _unitPopupJustOpened = true;
        if (!_playerNames.ContainsKey(guid)) _net?.NameQuery(guid);
    }

    private UnitPopupRow[] UnitPopupVisibleRows(ulong guid, UnitPopupWhich which)
    {
        bool inParty = _partyInGroup;
        bool isLeader = inParty && _net is not null && _partyLeaderGuid == _net.PlayerGuid;
        bool isRaid = inParty && _partyGroupType == 1;
        // UnitCanCooperate: a party member always cooperates even out of visual range;
        // anyone else must be a live-tracked, non-attackable player entity.
        bool canCooperate = which == UnitPopupWhich.Party ||
            _entities.TryGet(guid, out WorldEntity unit) && unit.IsPlayer && !CanAttack(unit);
        bool unitInParty = _partyMembers.Any(member => member.Guid == guid);
        return UnitPopupUiLaw.VisibleRows(which, inParty, isLeader, isRaid,
            canCooperate, unitInParty);
    }

    private void DrawUnitPopup()
    {
        if (_unitPopupGuid == 0 || _gameplayArt is null || _skin is null) return;
        UnitPopupRow[] rows = UnitPopupVisibleRows(_unitPopupGuid, _unitPopupWhich);
        // The roster can change under an open card (kicked, leader swap): a menu reduced to
        // Cancel closes rather than lingering empty.
        if (!UnitPopupUiLaw.ShouldOpen(rows)) { _unitPopupGuid = 0; return; }
        float s = GameplayUiScale();
        string title = _playerNames.GetValueOrDefault(_unitPopupGuid, "Player");
        float widestText = GameText.MeasureWidth("GameFontNormalSmall", title, s) / s;
        foreach (UnitPopupRow row in rows)
            widestText = MathF.Max(widestText,
                GameText.MeasureWidth("GameFontNormalSmall", UnitPopupUiLaw.RowText(row), s) / s);
        float cardWidth = UnitPopupUiLaw.CardWidth(widestText);
        Vector2 size = new(cardWidth, UnitPopupUiLaw.CardHeight(rows.Length));
        Vector2 physicalSize = size * s;
        Vector2 origin = UnitPopupUiLaw.ClampOrigin(
            _unitPopupPosition, physicalSize, ImGui.GetIO().DisplaySize);

        ImGui.SetNextWindowPos(origin, ImGuiCond.Always);
        ImGui.SetNextWindowSize(physicalSize, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0f);
        if (_unitPopupFocusRequested)
        {
            ImGui.SetNextWindowFocus();
            _unitPopupFocusRequested = false;
        }
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBackground |
            ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse;
        if (!ImGui.Begin("##unit-popup", flags)) { ImGui.End(); return; }
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        _skin.DrawBackdrop(dl, origin, origin + physicalSize, WowSkin.Tooltip);
        GameText.Draw(dl, "GameFontNormalSmall", title,
            origin + UnitPopupUiLaw.TitleOrigin * s, s);

        for (int i = 0; i < rows.Length; i++)
        {
            UnitPopupRow row = rows[i];
            Vector2 rowMin = origin + UnitPopupUiLaw.RowOrigin(i) * s;
            Vector2 rowSize = UnitPopupUiLaw.RowSize(cardWidth) * s;
            bool enabled = UnitPopupRowEnabled(row);
            ImGui.SetCursorScreenPos(rowMin);
            if (!enabled) ImGui.BeginDisabled();
            bool clicked = ImGui.InvisibleButton($"##unit-popup-{row}", rowSize);
            bool hovered = enabled && ImGui.IsItemHovered();
            if (!enabled) ImGui.EndDisabled();

            if (hovered)
            {
                uint highlight = _gameplayArt.AdditiveHandle(
                    @"Interface\QuestFrame\UI-QuestTitleHighlight");
                if (highlight != 0)
                    dl.AddImage((nint)highlight, rowMin, rowMin + rowSize);
            }
            string font = hovered ? "GameFontHighlightSmall" : "GameFontNormalSmall";
            uint? textColor = enabled ? null : FontObjectLaw.Get("GameFontDisableSmall").Color;
            GameText.Draw(dl, font, UnitPopupUiLaw.RowText(row),
                origin + UnitPopupUiLaw.RowTextOrigin(i) * s, s, textColor);

            if (enabled && clicked)
            {
                RunUnitPopupRow(row);
                break;
            }
        }

        bool menuHovered = ImGui.IsWindowHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem);
        double now = NowSeconds();
        if (menuHovered)
            _unitPopupAutoCloseAt = now + UnitPopupUiLaw.AutoCloseSeconds;
        bool clickedOutside = !_unitPopupJustOpened && !menuHovered &&
            (ImGui.IsMouseClicked(ImGuiMouseButton.Left) ||
             ImGui.IsMouseClicked(ImGuiMouseButton.Right));
        bool timedOut = !menuHovered && now >= _unitPopupAutoCloseAt;
        _unitPopupJustOpened = false;
        ImGui.End();
        if (_unitPopupGuid != 0 && (clickedOutside || timedOut)) _unitPopupGuid = 0;
    }

    /// <summary>UnitPopup_OnUpdate: the per-frame enable pass over the open card.</summary>
    private bool UnitPopupRowEnabled(UnitPopupRow row)
    {
        bool inParty = _partyInGroup;
        bool isLeader = inParty && _net is not null && _partyLeaderGuid == _net.PlayerGuid;
        bool isRaid = inParty && _partyGroupType == 1;
        // MEMBER_STATUS_ONLINE for a rostered member; a non-party unit in visual range is
        // connected by construction.
        bool connected = true;
        foreach (PartyMember member in _partyMembers)
            if (member.Guid == _unitPopupGuid)
            {
                connected = (member.Status & 1) != 0;
                break;
            }
        float distanceSquared = float.MaxValue;
        bool tracked = _entities.TryGet(_unitPopupGuid, out WorldEntity unit);
        if (tracked && _controller is not null)
            distanceSquared = Vector3.DistanceSquared(_controller.Position, unit.Position);
        // ControlledGuid, not PlayerGuid: while possessing a bot, the controlled unit is
        // "self" for the inspect gate (the CRPG seam the pre-rewrite popup carried).
        if (row == UnitPopupRow.Inspect)
            return _net is null || !tracked || InspectUiLaw.PopupRowEnabled(unit.IsPlayer,
                _unitPopupGuid == ControlledGuid, CanAttack(unit), distanceSquared);
        return UnitPopupUiLaw.RowEnabled(row, inParty, isLeader, isRaid,
            connected, distanceSquared);
    }

    /// <summary>UnitPopup_OnClick.</summary>
    private void RunUnitPopupRow(UnitPopupRow row)
    {
        ulong guid = _unitPopupGuid;
        string name = _playerNames.GetValueOrDefault(guid, "");
        switch (row)
        {
            case UnitPopupRow.Whisper:
                if (name.Length > 0) OpenChatEditWith($"/w {name} ");
                break;
            case UnitPopupRow.Invite:
                if (name.Length > 0) _net?.GroupInvite(name);
                break;
            case UnitPopupRow.Uninvite:
                _net?.GroupUninviteGuid(guid);
                break;
            case UnitPopupRow.Promote:
                _net?.GroupSetLeader(guid);
                break;
            case UnitPopupRow.ConvertToRaid:
                _net?.GroupRaidConvert();
                break;
            case UnitPopupRow.Leave:
                // LeaveParty(): the 1.12 client leaves a group with CMSG_GROUP_DISBAND.
                _net?.GroupDisband();
                break;
            case UnitPopupRow.Trade:
                _tradePartnerGuid = guid;
                _net?.InitiateTrade(guid);
                break;
            case UnitPopupRow.Inspect:
                RequestInspect(guid, _unitPopupInspectBinding);
                break;
        }
        _unitPopupGuid = 0;
    }
}
