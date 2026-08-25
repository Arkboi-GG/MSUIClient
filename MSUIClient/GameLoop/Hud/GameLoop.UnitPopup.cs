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
    private UnitPopupSubmenu _unitPopupSubmenu;
    private int _unitPopupSubmenuParentRow = -1;
    private string _unitPopupNameOverride = "";

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
        _unitPopupSubmenu = UnitPopupSubmenu.None;
        _unitPopupSubmenuParentRow = -1;
        _unitPopupNameOverride = "";
        // Pet and Target are creature guids. CMSG_NAME_QUERY on one answers empty and the
        // handler caches that empty answer as a negative entry, so asking would both put junk
        // on the wire and poison _playerNames on every first right-click of every mob.
        if (which is not (UnitPopupWhich.Pet or UnitPopupWhich.Target) &&
            !_playerNames.ContainsKey(guid))
            _net?.NameQuery(guid);
        PlayUiSound("igMainMenuOpen");
    }

    private void OpenFriendPopup(string name, Vector2 physicalPosition)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        ulong guid = name.Equals(_net?.PlayerName, StringComparison.OrdinalIgnoreCase)
            ? LocalPlayerGuid
            : _playerNames.FirstOrDefault(pair =>
                pair.Value.Equals(name, StringComparison.OrdinalIgnoreCase)).Key;
        _unitPopupGuid = guid == 0 ? ulong.MaxValue : guid;
        _unitPopupWhich = UnitPopupWhich.Friend;
        _unitPopupNameOverride = name;
        _unitPopupPosition = physicalPosition;
        _unitPopupInspectBinding = InspectBinding.Target;
        _unitPopupAutoCloseAt = NowSeconds() + UnitPopupUiLaw.AutoCloseSeconds;
        _unitPopupFocusRequested = true;
        _unitPopupJustOpened = true;
        _unitPopupSubmenu = UnitPopupSubmenu.None;
        _unitPopupSubmenuParentRow = -1;
        PlayUiSound("igMainMenuOpen");
    }

    private void OpenGuildPopup(ulong guid, string name, Vector2 physicalPosition)
    {
        if (guid == 0 || string.IsNullOrWhiteSpace(name)) return;
        _unitPopupGuid = guid;
        _unitPopupWhich = UnitPopupWhich.Guild;
        _unitPopupNameOverride = name;
        _unitPopupPosition = physicalPosition;
        _unitPopupInspectBinding = InspectBinding.Target;
        _unitPopupAutoCloseAt = NowSeconds() + UnitPopupUiLaw.AutoCloseSeconds;
        _unitPopupFocusRequested = true;
        _unitPopupJustOpened = true;
        _unitPopupSubmenu = UnitPopupSubmenu.None;
        _unitPopupSubmenuParentRow = -1;
        PlayUiSound("igMainMenuOpen");
    }

    private UnitPopupRow[] UnitPopupVisibleRows(ulong guid, UnitPopupWhich which)
    {
        if (which == UnitPopupWhich.Pet)
        {
            bool tracked = _entities.TryGet(guid, out WorldEntity pet) && pet.IsUnit;
            bool ownedSummon = tracked && pet.Fields.SummonedBy == LocalPlayerGuid;
            (bool canAbandon, bool canRename) = tracked
                ? PetMenuUiLaw.Predicates(pet.Fields.SummonedBy, LocalPlayerGuid,
                    pet.Fields.UnitFlags)
                : (false, false);
            return UnitPopupUiLaw.VisiblePetRows(ownedSummon, canAbandon, canRename);
        }
        if (which == UnitPopupWhich.Guild)
        {
            bool unitInPartyNow = _partyMembers.Any(member => member.Guid == guid);
            return UnitPopupUiLaw.VisibleGuildRows(unitInPartyNow,
                CurrentGuildRank() == 0, guid == LocalPlayerGuid);
        }
        bool inParty = _partyInGroup;
        bool isLeader = inParty && _net is not null && _partyLeaderGuid == _net.PlayerGuid;
        bool isRaid = inParty && _partyGroupType == 1;
        bool isAssistant = inParty && (_partyOwnFlags & 0x80) != 0;
        // UnitCanCooperate: a party member always cooperates even out of visual range;
        // anyone else must be a live-tracked, non-attackable player entity.
        bool canCooperate = which is UnitPopupWhich.Party or UnitPopupWhich.Friend ||
            _entities.TryGet(guid, out WorldEntity unit) && CanAssistFollowTarget(unit);
        bool unitInParty = _partyMembers.Any(member => member.Guid == guid);
        bool unitIsLootMaster = _partyMasterLooterGuid == guid ||
            which == UnitPopupWhich.Self && _partyMasterLooterGuid == 0;
        return UnitPopupUiLaw.VisibleRows(which, inParty, isLeader, isRaid,
            canCooperate, unitInParty, isAssistant, _partyLootMethod, unitIsLootMaster);
    }

    private void DrawUnitPopup()
    {
        if (_unitPopupGuid == 0 || _gameplayArt is null || _skin is null) return;
        UnitPopupRow[] rows = UnitPopupVisibleRows(_unitPopupGuid, _unitPopupWhich);
        // The roster can change under an open card (kicked, leader swap): a menu reduced to
        // Cancel closes rather than lingering empty.
        if (!UnitPopupUiLaw.ShouldOpen(rows)) { _unitPopupGuid = 0; return; }
        float s = GameplayUiScale();
        bool isLeader = _partyInGroup && _net is not null && _partyLeaderGuid == _net.PlayerGuid;
        // A creature has no _playerNames entry, so without the Target arm the card over a mob
        // would be headed with the literal word "Player".
        string title = _unitPopupNameOverride.Length > 0
            ? _unitPopupNameOverride
            : (_unitPopupWhich is UnitPopupWhich.Pet or UnitPopupWhich.Target) &&
                _entities.TryGet(_unitPopupGuid, out WorldEntity popupPet)
                ? ResolveCreatureOrPetName(popupPet,
                    _unitPopupWhich == UnitPopupWhich.Pet ? "Pet" : "Target")
                : _playerNames.GetValueOrDefault(_unitPopupGuid, "Player");
        float cardWidth = MeasureUnitPopupWidth(rows, hasTitle: true, title, isLeader, s);
        Vector2 logicalSize = new(cardWidth, UnitPopupUiLaw.CardHeight(rows.Length));
        Vector2 physicalSize = logicalSize * s;
        Vector2 origin = UnitPopupUiLaw.ClampOrigin(_unitPopupPosition, physicalSize,
            ImGui.GetIO().DisplaySize);

        UnitPopupLevelDraw root = DrawUnitPopupLevel("##unit-popup", origin, logicalSize,
            rows, hasTitle: true, title, isLeader, s, requestFocus: _unitPopupFocusRequested);
        _unitPopupFocusRequested = false;

        if (root.HoveredRow is { } rootHover)
        {
            UnitPopupSubmenu hoverMenu = UnitPopupUiLaw.SubmenuFor(rootHover);
            if (hoverMenu != UnitPopupSubmenu.None && UnitPopupUiLaw.HasArrow(rootHover, isLeader))
            {
                _unitPopupSubmenu = hoverMenu;
                _unitPopupSubmenuParentRow = Array.IndexOf(rows, rootHover);
            }
            else if (hoverMenu == UnitPopupSubmenu.None)
            {
                _unitPopupSubmenu = UnitPopupSubmenu.None;
                _unitPopupSubmenuParentRow = -1;
            }
        }

        if (root.Clicked is { } rootClicked)
        {
            UnitPopupSubmenu clickMenu = UnitPopupUiLaw.SubmenuFor(rootClicked);
            if (clickMenu != UnitPopupSubmenu.None && UnitPopupUiLaw.HasArrow(rootClicked, isLeader))
            {
                _unitPopupSubmenu = clickMenu;
                _unitPopupSubmenuParentRow = Array.IndexOf(rows, rootClicked);
            }
            else
            {
                RunUnitPopupRow(rootClicked);
            }
        }

        bool submenuHovered = false;
        if (_unitPopupGuid != 0 && _unitPopupSubmenu != UnitPopupSubmenu.None &&
            _unitPopupSubmenuParentRow >= 0)
        {
            UnitPopupRow[] submenuRows = UnitPopupUiLaw.SubmenuRows(_unitPopupSubmenu);
            float submenuWidth = MeasureUnitPopupWidth(submenuRows, hasTitle: false,
                "", isLeader, s);
            Vector2 submenuLogicalSize = new(submenuWidth,
                UnitPopupUiLaw.MenuHeight(submenuRows.Length, hasTitle: false));
            Vector2 submenuOrigin = UnitPopupUiLaw.SubmenuOrigin(origin / s, cardWidth,
                _unitPopupSubmenuParentRow, submenuLogicalSize, ImGui.GetIO().DisplaySize / s) * s;
            UnitPopupLevelDraw submenu = DrawUnitPopupLevel("##unit-popup-submenu",
                submenuOrigin, submenuLogicalSize, submenuRows, hasTitle: false, "", isLeader,
                s, requestFocus: false);
            submenuHovered = submenu.Hovered;
            if (submenu.Clicked is { } submenuClicked)
            {
                if (submenuClicked == UnitPopupRow.Cancel)
                {
                    _unitPopupSubmenu = UnitPopupSubmenu.None;
                    _unitPopupSubmenuParentRow = -1;
                }
                else
                {
                    RunUnitPopupSubmenuRow(submenuClicked);
                }
            }
        }

        bool menuHovered = root.Hovered || submenuHovered;
        double now = NowSeconds();
        if (menuHovered)
            _unitPopupAutoCloseAt = now + UnitPopupUiLaw.AutoCloseSeconds;
        bool clickedOutside = !_unitPopupJustOpened && !menuHovered &&
            (ImGui.IsMouseClicked(ImGuiMouseButton.Left) ||
             ImGui.IsMouseClicked(ImGuiMouseButton.Right));
        bool timedOut = !menuHovered && now >= _unitPopupAutoCloseAt;
        _unitPopupJustOpened = false;
        if (_unitPopupGuid != 0 && (clickedOutside || timedOut)) _unitPopupGuid = 0;
    }

    private readonly record struct UnitPopupLevelDraw(
        bool Hovered, UnitPopupRow? HoveredRow, UnitPopupRow? Clicked);

    private float MeasureUnitPopupWidth(UnitPopupRow[] rows, bool hasTitle, string title,
        bool isLeader, float scale)
    {
        var measures = new List<UnitPopupWidthMeasure>();
        if (hasTitle)
            measures.Add(new(GameText.MeasureWidth("GameFontNormalSmall", title, scale) / scale,
                true, false, false));
        foreach (UnitPopupRow row in rows)
        {
            bool checkable = !hasTitle;
            measures.Add(new(GameText.MeasureWidth("GameFontHighlightSmall",
                    UnitPopupUiLaw.RowText(row, _partyLootMethod, _partyLootThreshold), scale) /
                    scale,
                !checkable, UnitPopupUiLaw.HasArrow(row, isLeader),
                UnitPopupUiLaw.HasRaidIcon(row)));
        }
        return UnitPopupUiLaw.CardWidth(measures);
    }

    private UnitPopupLevelDraw DrawUnitPopupLevel(string name, Vector2 origin,
        Vector2 logicalSize, UnitPopupRow[] rows, bool hasTitle, string title, bool isLeader,
        float scale, bool requestFocus)
    {
        Vector2 physicalSize = logicalSize * scale;
        ImGui.SetNextWindowPos(origin, ImGuiCond.Always);
        ImGui.SetNextWindowSize(physicalSize, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0f);
        if (requestFocus) ImGui.SetNextWindowFocus();
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBackground |
            ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse;
        if (!ImGui.Begin(name, flags))
        {
            ImGui.End();
            return default;
        }

        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        _skin!.DrawBackdrop(dl, origin, origin + physicalSize, WowSkin.Tooltip,
            UnitPopupUiLaw.MenuBackdropFillTint, UnitPopupUiLaw.MenuBackdropEdgeTint);
        if (hasTitle)
            GameText.Draw(dl, "GameFontNormalSmall", title,
                origin + UnitPopupUiLaw.TitleOrigin * scale, scale);

        UnitPopupRow? hoveredRow = null;
        UnitPopupRow? clickedRow = null;
        for (int i = 0; i < rows.Length; i++)
        {
            UnitPopupRow row = rows[i];
            bool checkable = !hasTitle;
            Vector2 rowMin = origin + UnitPopupUiLaw.RowOrigin(i, hasTitle, checkable) * scale;
            Vector2 rowSize = UnitPopupUiLaw.RowSize(logicalSize.X, checkable) * scale;
            bool enabled = !hasTitle || UnitPopupRowEnabled(row);
            ImGui.SetCursorScreenPos(rowMin);
            if (!enabled) ImGui.BeginDisabled();
            bool clicked = ImGui.InvisibleButton($"##{name}-{row}", rowSize);
            bool hovered = enabled && ImGui.IsItemHovered();
            if (!enabled) ImGui.EndDisabled();
            if (hovered) hoveredRow = row;
            if (enabled && clicked) clickedRow = row;

            if (hovered)
            {
                uint highlight = _gameplayArt!.AdditiveHandle(
                    @"Interface\QuestFrame\UI-QuestTitleHighlight");
                if (highlight != 0) dl.AddImage((nint)highlight, rowMin, rowMin + rowSize);
            }

            if (checkable && UnitPopupUiLaw.IsChecked(row, _partyLootMethod,
                _partyLootThreshold, GroupUiLaw.RaidTargetIndex(_partyRaidTargets, _unitPopupGuid)))
            {
                uint check = _gameplayArt!.Handle(@"Interface\Buttons\UI-CheckBox-Check");
                Vector2 checkMin = origin + UnitPopupUiLaw.CheckOrigin(i) * scale;
                if (check != 0)
                    dl.AddImage((nint)check, checkMin,
                        checkMin + UnitPopupUiLaw.CheckSize * scale);
            }

            if (UnitPopupUiLaw.HasRaidIcon(row))
            {
                uint icon = _gameplayArt!.Handle(
                    @"Interface\TargetingFrame\UI-RaidTargetingIcons");
                Vector2 iconMin = origin + UnitPopupUiLaw.RightDecorationOrigin(i,
                    logicalSize.X, 15f, hasTitle, 5f) * scale;
                (Vector2 uv0, Vector2 uv1) = UnitPopupUiLaw.RaidIconUv(row);
                if (icon != 0)
                    dl.AddImage((nint)icon, iconMin,
                        iconMin + UnitPopupUiLaw.RaidIconSize * scale,
                        uv0, uv1);
            }
            else if (UnitPopupUiLaw.HasArrow(row, isLeader))
            {
                uint arrow = _gameplayArt!.Handle(@"Interface\ChatFrame\ChatFrameExpandArrow");
                Vector2 arrowMin = origin + UnitPopupUiLaw.RightDecorationOrigin(i,
                    logicalSize.X, 16f, hasTitle) * scale;
                if (arrow != 0)
                    dl.AddImage((nint)arrow, arrowMin,
                        arrowMin + UnitPopupUiLaw.ArrowSize * scale);
            }

            uint? textColor = enabled
                ? UnitPopupUiLaw.RowColor(row, _partyLootThreshold) is { } color
                    ? ImGui.ColorConvertFloat4ToU32(color) : null
                : FontObjectLaw.Get("GameFontDisableSmall").Color;
            GameText.Draw(dl, "GameFontHighlightSmall",
                UnitPopupUiLaw.RowText(row, _partyLootMethod, _partyLootThreshold),
                origin + UnitPopupUiLaw.RowTextOrigin(i, hasTitle, checkable) * scale,
                scale, textColor);
        }

        bool hoveredWindow = ImGui.IsWindowHovered(
            ImGuiHoveredFlags.AllowWhenBlockedByActiveItem);
        ImGui.End();
        return new(hoveredWindow, hoveredRow, clickedRow);
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
        if (tracked && TryGetControlledBodyPose(out WorldBodyPose controlledBody))
            distanceSquared = Vector3.DistanceSquared(controlledBody.Position, unit.Position);
        // ControlledGuid, not PlayerGuid: while possessing a bot, the controlled unit is
        // "self" for the inspect gate (the CRPG seam the pre-rewrite popup carried).
        if (row == UnitPopupRow.Inspect)
            return _net is null || !tracked || CanAuthorControlledGameplay &&
                InspectUiLaw.PopupRowEnabled(unit.IsPlayer,
                    _unitPopupGuid == ControlledGuid, CanAttack(unit), distanceSquared);
        if (row == UnitPopupRow.Duel)
        {
            bool playerDead = _entities.TryGet(ControlledGuid, out WorldEntity player) &&
                player.IsDead;
            return tracked && unit.IsPlayer && DuelFrameUiLaw.DuelRowEnabled(playerDead,
                unit.IsDead, fullControl: CanAuthorControlledGameplay, distanceSquared);
        }
        if (row == UnitPopupRow.Trade && !CanAuthorControlledGameplay) return false;
        if (row is UnitPopupRow.PetRename or UnitPopupRow.PetAbandon or UnitPopupRow.PetDismiss &&
            !CanAuthorControlledGameplay) return false;
        if (row == UnitPopupRow.Follow)
            return tracked && unit.IsPlayer && !_freeView && _controlState == ControlState.OwnChar;
        return UnitPopupUiLaw.RowEnabled(row, inParty, isLeader, isRaid,
            connected, distanceSquared);
    }

    /// <summary>UnitPopup_OnClick.</summary>
    private void RunUnitPopupRow(UnitPopupRow row)
    {
        ulong guid = _unitPopupGuid;
        string name = _unitPopupNameOverride.Length > 0
            ? _unitPopupNameOverride : _playerNames.GetValueOrDefault(guid, "");
        switch (row)
        {
            case UnitPopupRow.PetPaperDoll:
                OpenCharacterPageThroughUiPanel(requestedTab: 1);
                break;
            case UnitPopupRow.PetRename:
                if (CanAuthorControlledGameplay) ShowPetRenamePopup(guid);
                break;
            case UnitPopupRow.PetAbandon:
                if (CanAuthorControlledGameplay) ShowPetAbandonPopup(guid);
                break;
            case UnitPopupRow.PetDismiss:
                if (CanAuthorControlledGameplay)
                    _net?.PetAction(guid, PetMenuUiLaw.DismissWord, 0);
                break;
            case UnitPopupRow.Whisper:
                if (name.Length > 0) OpenChatEditWith($"/w {name} ");
                break;
            case UnitPopupRow.Invite:
                if (name.Length > 0) _net?.GroupInvite(name);
                break;
            case UnitPopupRow.Uninvite:
                if (!TryPartyTestUninvite(guid)) _net?.GroupUninviteGuid(guid);
                break;
            case UnitPopupRow.Promote:
                if (!TryPartyTestPromote(guid)) _net?.GroupSetLeader(guid);
                break;
            case UnitPopupRow.Leave:
                // LeaveParty(): the 1.12 client leaves a group with CMSG_GROUP_DISBAND.
                if (!TryPartyTestLeave()) _net?.GroupDisband();
                break;
            case UnitPopupRow.GuildPromote:
                ShowGuildActionPopup(GuildFrameUiLaw.ConfirmPromoteDefinition, name);
                break;
            case UnitPopupRow.GuildLeave:
                ShowGuildActionPopup(GuildFrameUiLaw.ConfirmLeaveDefinition, _guildName);
                break;
            case UnitPopupRow.Trade:
                if (CanAuthorControlledGameplay && UnitPopupRowEnabled(row))
                {
                    _tradePartnerGuid = guid;
                    _net?.InitiateTrade(guid);
                }
                break;
            case UnitPopupRow.Follow:
                StartAutoFollow(guid, name);
                break;
            case UnitPopupRow.Duel:
                StartDuelWith(guid);
                break;
            case UnitPopupRow.Inspect:
                RequestInspect(guid, _unitPopupInspectBinding);
                break;
            case UnitPopupRow.LootPromote:
                if (!TryPartyTestLoot(2, guid, _partyLootThreshold))
                    _net?.GroupLootMethod(2, guid, _partyLootThreshold);
                break;
        }
        PlayUiSound("UChatScrollButton");
        _unitPopupGuid = 0;
        _unitPopupNameOverride = "";
    }

    /// <summary>Level-2 UnitPopup_OnClick for the backed loot and raid-mark rows.</summary>
    private void RunUnitPopupSubmenuRow(UnitPopupRow row)
    {
        if (row is >= UnitPopupRow.FreeForAll and <= UnitPopupRow.NeedBeforeGreed)
        {
            byte method = UnitPopupUiLaw.LootMethodValue(row);
            ulong master = method == 2 ? _unitPopupGuid : 0;
            if (!TryPartyTestLoot(method, master, _partyLootThreshold))
                _net?.GroupLootMethod(method, master, _partyLootThreshold);
        }
        else if (row is >= UnitPopupRow.Quality2 and <= UnitPopupRow.Quality4)
        {
            byte threshold = UnitPopupUiLaw.QualityValue(row);
            if (!TryPartyTestLoot(_partyLootMethod, _partyMasterLooterGuid, threshold))
                _net?.GroupLootMethod(_partyLootMethod, _partyMasterLooterGuid, threshold);
        }
        else if (row is >= UnitPopupRow.RaidTarget1 and <= UnitPopupRow.RaidTargetNone)
        {
            byte requested = UnitPopupUiLaw.RaidTargetValue(row);
            byte current = GroupUiLaw.RaidTargetIndex(_partyRaidTargets, _unitPopupGuid);
            if (requested == 0)
            {
                if (current > 0 && !TryPartyTestRaidTarget(_unitPopupGuid, 0))
                    _net?.SetRaidTarget(checked((byte)(current - 1)), 0);
            }
            else
            {
                if (!TryPartyTestRaidTarget(_unitPopupGuid, requested))
                    _net?.SetRaidTarget(checked((byte)(requested - 1)), _unitPopupGuid);
            }
        }
        PlayUiSound("UChatScrollButton");
        _unitPopupSubmenu = UnitPopupSubmenu.None;
        _unitPopupSubmenuParentRow = -1;
    }
}
