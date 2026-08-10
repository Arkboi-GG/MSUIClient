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

    /// <summary>UnitPopup_ShowMenu: refuses cancel-only menus, queries an unknown name.</summary>
    private void OpenUnitPopup(ulong guid, UnitPopupWhich which, Vector2 physicalPosition,
        InspectBinding binding)
    {
        if (guid == 0 || !UnitPopupUiLaw.ShouldOpen(UnitPopupVisibleRows(guid, which))) return;
        _unitPopupGuid = guid;
        _unitPopupWhich = which;
        _unitPopupPosition = physicalPosition;
        _unitPopupInspectBinding = binding;
        if (!_playerNames.ContainsKey(guid)) _net?.NameQuery(guid);
    }

    private UnitPopupRow[] UnitPopupVisibleRows(ulong guid, UnitPopupWhich which)
    {
        bool inParty = _partyInGroup;
        bool isLeader = inParty && _net is not null && _partyLeaderGuid == _net.PlayerGuid;
        // UnitCanCooperate: a party member always cooperates even out of visual range;
        // anyone else must be a live-tracked, non-attackable player entity.
        bool canCooperate = which == UnitPopupWhich.Party ||
            _entities.TryGet(guid, out WorldEntity unit) && unit.IsPlayer && !CanAttack(unit);
        bool unitInParty = _partyMembers.Any(member => member.Guid == guid);
        return UnitPopupUiLaw.VisibleRows(which, inParty, isLeader, canCooperate, unitInParty);
    }

    private void DrawUnitPopup()
    {
        if (_unitPopupGuid == 0 || _gameplayArt is null) return;
        UnitPopupRow[] rows = UnitPopupVisibleRows(_unitPopupGuid, _unitPopupWhich);
        // The roster can change under an open card (kicked, leader swap): a menu reduced to
        // Cancel closes rather than lingering empty.
        if (!UnitPopupUiLaw.ShouldOpen(rows)) { _unitPopupGuid = 0; return; }
        float s = GameplayUiScale();
        Vector2 logical = _unitPopupPosition / s;
        Vector2 size = new(UnitPopupUiLaw.CardWidth, UnitPopupUiLaw.CardHeight(rows.Length));
        if (!BeginVanillaWindow("##unit-popup", logical, size,
                out ImDrawListPtr dl, out Vector2 origin, out s)) { ImGui.End(); return; }
        dl.AddRectFilled(origin, origin + size * s, 0xee080808, 4 * s);
        dl.AddRect(origin, origin + size * s, 0xffb08040, 4 * s, ImDrawFlags.None, s);
        DrawCenteredText(dl, origin + new Vector2(UnitPopupUiLaw.CardWidth / 2f, 16) * s,
            _playerNames.GetValueOrDefault(_unitPopupGuid, "Player"), 10f * s, VanillaGold);
        for (int i = 0; i < rows.Length; i++)
        {
            UnitPopupRow row = rows[i];
            if (VanillaButton(dl, $"##unit-popup-{row}", UnitPopupUiLaw.RowText(row),
                    origin + UnitPopupUiLaw.RowOrigin(i) * s, UnitPopupUiLaw.RowSize, s,
                    UnitPopupRowEnabled(row)))
                RunUnitPopupRow(row);
        }
        ImGui.End();
    }

    /// <summary>UnitPopup_OnUpdate: the per-frame enable pass over the open card.</summary>
    private bool UnitPopupRowEnabled(UnitPopupRow row)
    {
        bool inParty = _partyInGroup;
        bool isLeader = inParty && _net is not null && _partyLeaderGuid == _net.PlayerGuid;
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
        return UnitPopupUiLaw.RowEnabled(row, inParty, isLeader, connected, distanceSquared);
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
