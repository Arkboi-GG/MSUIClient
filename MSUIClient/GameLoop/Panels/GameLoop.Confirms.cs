using System.Numerics;
using ImGuiNET;
using MSUIClient.Engine;
using MSUIClient.Engine.UI;
using MSUIClient.Net;

namespace MSUIClient;

/// <summary>
/// The server-asked confirmations and world broadcasts that had no client half (2026-09-01):
/// summon requests, escort-quest confirmations, the master-loot candidate list, server
/// shutdown/restart messages, zone-under-attack and defense channel lines, chat restriction.
/// </summary>
public sealed partial class GameLoop
{
    private ulong _summonRequester;
    private uint _summonZone;
    private uint _questConfirmId;
    private string _questConfirmTitle = "";
    private ulong _questConfirmStarter;
    /// <summary>SMSG_LOOT_MASTER_LIST: who the master looter may assign the open loot to.</summary>
    private readonly List<ulong> _lootMasterCandidates = [];
    /// <summary>The wire slot whose master-loot assignment menu is open, or -1.</summary>
    private int _lootMasterMenuSlot = -1;

    private void ResetConfirms()
    {
        _summonRequester = 0; _summonZone = 0;
        _questConfirmId = 0; _questConfirmTitle = ""; _questConfirmStarter = 0;
        _lootMasterCandidates.Clear(); _lootMasterMenuSlot = -1;
    }

    // ── Summon ───────────────────────────────────────────────────────────────────────────────

    /// <summary>SMSG_SUMMON_REQUEST: u64 summoner, u32 zone, u32 timeoutMs.</summary>
    private void ApplySummonRequest(byte[] body)
    {
        if (body.Length < 16) return;
        var r = new PacketReader(body);
        _summonRequester = r.ReadU64();
        _summonZone = r.ReadU32();
        uint timeoutMs = r.ReadU32();
        if (!_playerNames.ContainsKey(_summonRequester)) _net?.NameQuery(_summonRequester);
        EnsureAreaTableForMinimap();
        bool dead = _entities.TryGet(ControlledGuid, out WorldEntity player) && player.IsDead;
        // The reference refuses to raise the dialog for a dead/ghost player — the server would
        // refuse the response anyway (HandleSummonResponseOpcode: !IsAlive || IsInCombat).
        ExecuteStaticPopupPlan(StaticPopupCoordinatorLaw.Show(
            _staticPopupSlots, ConfirmPopupUiLaw.SummonDefinition, dead,
            dataToken: _summonRequester.ToString()));
        EmitInterface("summon", "request", dead ? "REFUSED_DEAD" : "SHOWN", _summonRequester,
            $"zone={_summonZone};timeoutMs={timeoutMs}");
    }

    private string SummonPromptText()
    {
        string who = _playerNames.GetValueOrDefault(_summonRequester, "");
        string zone = _areas?.ZoneName(_summonZone) ?? "";
        return ConfirmPopupUiLaw.SummonText(
            InventoryGlobalString("CONFIRM_SUMMON", "%s wants to summon you to %s. Do you accept?"), who, zone);
    }

    // ── Escort / party-accept quest ──────────────────────────────────────────────────────────

    /// <summary>SMSG_QUEST_CONFIRM_ACCEPT: u32 quest, cstring title, u64 starter.</summary>
    private void ApplyQuestConfirmAccept(byte[] body)
    {
        var r = new PacketReader(body);
        _questConfirmId = r.ReadU32();
        _questConfirmTitle = r.ReadCString();
        _questConfirmStarter = r.Remaining >= 8 ? r.ReadU64() : 0;
        if (_questConfirmStarter != 0 && !_playerNames.ContainsKey(_questConfirmStarter))
            _net?.NameQuery(_questConfirmStarter);
        _questTitles[_questConfirmId] = _questConfirmTitle;
        bool dead = _entities.TryGet(ControlledGuid, out WorldEntity player) && player.IsDead;
        ExecuteStaticPopupPlan(StaticPopupCoordinatorLaw.Show(
            _staticPopupSlots, ConfirmPopupUiLaw.QuestAcceptDefinition, dead,
            dataToken: _questConfirmId.ToString()));
        EmitInterface("quest", "confirm-accept", "SHOWN", _questConfirmStarter,
            $"quest={_questConfirmId};title={SanitizeEvidence(_questConfirmTitle)}");
    }

    private string QuestConfirmPromptText()
    {
        string who = _playerNames.GetValueOrDefault(_questConfirmStarter, "");
        return ConfirmPopupUiLaw.QuestAcceptText(
            InventoryGlobalString("QUEST_ACCEPT", "%s has asked you to accept %s."), who, _questConfirmTitle);
    }

    // ── Ready check ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// MSG_RAID_READY_CHECK: an empty body is the leader's START (vmangos broadcasts it bare);
    /// u64 + u8 is one member's answer. The start used to be parsed and thrown away, so a
    /// ready check never asked anyone anything.
    /// </summary>
    private void ApplyReadyCheck(byte[] body)
    {
        PartyReadyCheckWire wire = PartyFramePacketLaw.ParseReadyCheck(body);
        if (wire.Started)
        {
            bool dead = _entities.TryGet(ControlledGuid, out WorldEntity player) && player.IsDead;
            ExecuteStaticPopupPlan(StaticPopupCoordinatorLaw.Show(
                _staticPopupSlots, ConfirmPopupUiLaw.ReadyCheckDefinition, dead));
            EmitInterface("party", "ready-check", "STARTED", _partyLeaderGuid, "popup=READY_CHECK");
            return;
        }
        if (wire.Guid == LocalPlayerGuid) return;
        if (wire.Ready == 0)
            AddChatMessage(InventoryGlobalString("READY_CHECK_NOT_READY", "%s is not ready.")
                .Replace("%s", ResolveUnitName(wire.Guid), StringComparison.Ordinal), ChatFrameLaw.MsgType.System);
        EmitInterface("party", "ready-check", wire.Ready != 0 ? "READY" : "NOT_READY", wire.Guid, "");
    }

    private string ReadyCheckPromptText() => ConfirmPopupUiLaw.ReadyCheckText(
        InventoryGlobalString("READY_CHECK_MESSAGE", "%s has requested a ready check. Are you ready?"),
        _playerNames.GetValueOrDefault(_partyLeaderGuid, ""));

    // ── Minimap ping ─────────────────────────────────────────────────────────────────────────

    private readonly List<(ulong Guid, Vector2 World, double Until)> _minimapPings = [];
    private const double MinimapPingSeconds = 5.0;

    /// <summary>MSG_MINIMAP_PING (in): u64 pinger, f32 x, f32 y — a party member pinged the map.</summary>
    private void ApplyMinimapPing(byte[] body)
    {
        PartyMinimapPingWire wire = PartyFramePacketLaw.ParseMinimapPing(body);
        AddMinimapPing(wire.Guid, new Vector2(wire.X, wire.Y));
        EmitInterface("party", "minimap-ping", "RECEIVED", wire.Guid, $"x={wire.X:R};y={wire.Y:R}");
    }

    private void AddMinimapPing(ulong guid, Vector2 world)
    {
        _minimapPings.RemoveAll(p => p.Guid == guid);
        _minimapPings.Add((guid, world, NowSeconds() + MinimapPingSeconds));
        PlayUiSound("MapPing", "ui.minimap");
    }

    /// <summary>A left-click on the minimap while grouped pings it for the party (and locally at once).</summary>
    private void SendMinimapPing(Vector2 mouse, Vector2 playerXy, Vector2 mapCenter, float mapSide, float radiusYards)
    {
        if (_partyMembers.Count == 0 || mapSide <= 0 || radiusYards <= 0) return;
        // Invert MinimapUiLaw.PartyBlip: screen = center + (-dy, -dx) × pixelsPerYard.
        float pixelsPerYard = mapSide / (radiusYards * 2f);
        Vector2 screenDelta = (mouse - mapCenter) / pixelsPerYard;
        Vector2 world = new(playerXy.X - screenDelta.Y, playerXy.Y - screenDelta.X);
        if (_net?.GroupMinimapPing(world.X, world.Y) == true)
        {
            AddMinimapPing(LocalPlayerGuid, world);
            EmitInterface("party", "minimap-ping", "SENT", LocalPlayerGuid, $"x={world.X:R};y={world.Y:R}");
        }
    }

    /// <summary>The pings still alive: a pulsing ring at the pinged spot, drawn with the party blips.</summary>
    private void DrawMinimapPings(ImDrawListPtr dl, Vector3 playerPosition, Vector2 mapMin, Vector2 mapMax,
        float s, float? radiusOverride)
    {
        double now = NowSeconds();
        _minimapPings.RemoveAll(p => p.Until <= now);
        if (_minimapPings.Count == 0) return;
        float radiusYards = radiusOverride ?? MinimapUiLaw.OutdoorRadius(_minimapZoom);
        float side = mapMax.X - mapMin.X;
        Vector2 center = (mapMin + mapMax) * .5f;
        Vector2 player = new(playerPosition.X, playerPosition.Y);
        dl.PushClipRect(mapMin, mapMax, true);
        foreach ((ulong _, Vector2 world, double until) in _minimapPings)
        {
            MinimapPartyBlip blip = MinimapUiLaw.PartyBlip(player, world, center, side, radiusYards);
            if (blip.IsArrow) continue;
            float phase = (float)((until - now) % 1.0);
            float ring = MathF.Max(3f, 10f * s) * (0.4f + 0.6f * phase);
            dl.AddCircle(blip.Center, ring, 0xff40c0ff, 24, MathF.Max(1f, 1.5f * s));
            dl.AddCircleFilled(blip.Center, MathF.Max(1.5f, 2.5f * s), 0xff40c0ff);
        }
        dl.PopClipRect();
    }

    /// <summary>The coordinator's effects for the confirm popups.</summary>
    private void ApplyConfirmPopupEffect(StaticPopupCoordinatorLaw.Effect effect)
    {
        if (effect.Type == ConfirmPopupUiLaw.GiverChoicePopupType)
        {
            ApplyCommandViewNpcChoice(effect.Kind);
            return;
        }
        if (effect.Type == ConfirmPopupUiLaw.DisableControlGuidePopupType)
        {
            // Only an explicit Disable turns the guide off; Escape, Cancel and an override all
            // leave it as it was.
            if (effect.Kind == StaticPopupCoordinatorLaw.EffectKind.Accept)
            {
                _enableControlGuide = false;
                _showControlGuide = false;
            }
            return;
        }
        if (effect.Type == ConfirmPopupUiLaw.PartyFlightPopupType)
        {
            ApplyPartyFlightPopupEffect(effect.Kind);
            return;
        }
        bool summon = effect.Type == ConfirmPopupUiLaw.SummonPopupType;
        if (effect.Type == ConfirmPopupUiLaw.ReadyCheckPopupType)
        {
            switch (effect.Kind)
            {
                case StaticPopupCoordinatorLaw.EffectKind.Accept:
                    _net?.AnswerReadyCheck(true);
                    EmitInterface("party", "ready-check", "ANSWERED", LocalPlayerGuid, "ready=true");
                    break;
                case StaticPopupCoordinatorLaw.EffectKind.CancelClicked:
                case StaticPopupCoordinatorLaw.EffectKind.CancelOverride:
                case StaticPopupCoordinatorLaw.EffectKind.CancelWithoutReason:
                    _net?.AnswerReadyCheck(false);
                    EmitInterface("party", "ready-check", "ANSWERED", LocalPlayerGuid, "ready=false");
                    break;
                // A timeout answers nothing: the server's own timer counts silence as not ready.
            }
            return;
        }
        switch (effect.Kind)
        {
            case StaticPopupCoordinatorLaw.EffectKind.Accept:
                if (summon)
                {
                    if (RefuseTacticalFreezeLiveCommand("accepting a summon")) return;
                    if (RefuseTacticalFrozenActor(_summonRequester,
                            "accept a summon from them")) return;
                    bool sent = _summonRequester != 0 && _net?.SummonResponse(_summonRequester) == true;
                    EmitInterface("summon", "answer", sent ? "ACCEPTED" : "SEND_FAILED", _summonRequester, "wire=CMSG_SUMMON_RESPONSE");
                    _summonRequester = 0;
                }
                else
                {
                    if (RefuseTacticalFreezeLiveCommand("accepting a quest confirmation")) return;
                    if (RefuseTacticalFrozenActor(_questConfirmStarter,
                            "accept a quest confirmation from them")) return;
                    bool sent = _questConfirmId != 0 && _net?.QuestConfirmAccept(_questConfirmId) == true;
                    EmitInterface("quest", "confirm-accept", sent ? "ACCEPTED" : "SEND_FAILED", _questConfirmStarter,
                        $"quest={_questConfirmId};wire=CMSG_QUEST_CONFIRM_ACCEPT");
                    _questConfirmId = 0;
                }
                break;
            case StaticPopupCoordinatorLaw.EffectKind.CancelWithoutReason:
            case StaticPopupCoordinatorLaw.EffectKind.CancelOverride:
            case StaticPopupCoordinatorLaw.EffectKind.CancelClicked:
            case StaticPopupCoordinatorLaw.EffectKind.CancelTimeout:
                // A decline sends nothing: the server times the summon out itself, and an
                // unanswered escort confirm simply never joins.
                if (summon) { EmitInterface("summon", "answer", "DECLINED", _summonRequester, "wire=none"); _summonRequester = 0; }
                else { EmitInterface("quest", "confirm-accept", "DECLINED", _questConfirmStarter, "wire=none"); _questConfirmId = 0; }
                break;
        }
    }

    private void DrawConfirmPopups()
    {
        DrawConfirmPopup(ConfirmPopupUiLaw.SummonPopupType);
        DrawConfirmPopup(ConfirmPopupUiLaw.QuestAcceptPopupType);
        DrawConfirmPopup(ConfirmPopupUiLaw.ReadyCheckPopupType);
        DrawConfirmPopup(ConfirmPopupUiLaw.GiverChoicePopupType);
        DrawConfirmPopup(ConfirmPopupUiLaw.DisableControlGuidePopupType);
        DrawConfirmPopup(ConfirmPopupUiLaw.PartyFlightPopupType);
        DrawLootMasterMenu();
    }

    /// <summary>One yes/no StaticPopup on the dialog skin — the duel request's twin.</summary>
    private void DrawConfirmPopup(string type)
    {
        (int Slot, StaticPopupCoordinatorLaw.Instance Instance)? popup =
            ConfirmPopupUiLaw.Visible(_staticPopupSlots, type);
        if (popup is not { } visible || _skin is null) return;
        float scale = GameplayUiScale();
        // The Command View NPC chooser is the one popup with N buttons (one per thing the NPC
        // offers); the rest are the stock accept/decline pair.
        bool chooser = type == ConfirmPopupUiLaw.GiverChoicePopupType;
        string text = type switch
        {
            ConfirmPopupUiLaw.SummonPopupType => SummonPromptText(),
            ConfirmPopupUiLaw.ReadyCheckPopupType => ReadyCheckPromptText(),
            ConfirmPopupUiLaw.GiverChoicePopupType => ConfirmPopupUiLaw.GiverChoiceText(
                ResolveWorldUnitName(_cvGiverChoiceGuid), _cvGiverChoiceOptions),
            ConfirmPopupUiLaw.DisableControlGuidePopupType => ConfirmPopupUiLaw.DisableControlGuideText,
            ConfirmPopupUiLaw.PartyFlightPopupType => PartyFlightPromptText(),
            _ => QuestConfirmPromptText(),
        };
        (string acceptCaption, string declineCaption) = ConfirmPopupUiLaw.Captions(type);
        string[] lines = WrapTooltipText(text, "GameFontHighlight", scale,
            DuelFrameUiLaw.PopupTextWidth * scale).ToArray();
        float logicalTextHeight = lines.Length * GameText.LinePitch("GameFontHighlight", 1);
        Vector2 origin = StaticPopupOrigin(visible.Slot, DuelFrameUiLaw.PopupWidth, scale);
        Vector2 size = (chooser
            ? ConfirmPopupUiLaw.GiverChoicePopupSize(logicalTextHeight, _cvGiverChoiceOptions.Count)
            : DuelFrameUiLaw.PopupSize(logicalTextHeight, buttons: true)) * scale;
        ImGui.SetNextWindowPos(origin, ImGuiCond.Always);
        ImGui.SetNextWindowSize(size, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0);
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoNav;
        bool begun = ImGui.Begin($"##confirm-popup-{visible.Slot}-{type}", flags);
        ImGui.PopStyleVar(2);
        if (!begun) { ImGui.End(); return; }
        ImDrawListPtr draw = ImGui.GetWindowDrawList();
        draw.PushClipRectFullScreen();
        _skin.DrawBackdrop(draw, origin, origin + size, WowSkin.Dialog);
        for (int i = 0; i < lines.Length; i++)
            GameText.DrawCentered(draw, "GameFontHighlight", lines[i],
                origin + DuelFrameUiLaw.TextLineCenter(i) * scale, scale);
        int clicked = 0;   // 1-based button, 0 = none
        if (chooser)
        {
            for (int i = 0; i < _cvGiverChoiceOptions.Count; i++)
                if (DrawPartyInviteButton(draw, $"StaticPopup{visible.Slot}Button{i + 1}",
                        _cvGiverChoiceOptions[i].Caption,
                        origin + ConfirmPopupUiLaw.GiverChoiceButtonMin(i, logicalTextHeight) * scale,
                        scale, capture: false, clip: Vector4.Zero))
                    clicked = i + 1;
        }
        else
        {
            if (DrawPartyInviteButton(draw, $"StaticPopup{visible.Slot}Button1", acceptCaption,
                    origin + DuelFrameUiLaw.ButtonMin(1, logicalTextHeight) * scale,
                    scale, capture: false, clip: Vector4.Zero))
                clicked = 1;
            else if (DrawPartyInviteButton(draw, $"StaticPopup{visible.Slot}Button2", declineCaption,
                    origin + DuelFrameUiLaw.ButtonMin(2, logicalTextHeight) * scale,
                    scale, capture: false, clip: Vector4.Zero))
                clicked = 2;
        }
        draw.PopClipRect();
        ImGui.End();
        if (clicked == 0) return;
        if (chooser)
        {
            // Every chooser button is the popup's ACCEPT: the coordinator only knows two
            // buttons, so the picked option is recorded first and the Accept effect dispatches it.
            _cvGiverChoicePicked = clicked - 1;
            ExecuteStaticPopupPlan(StaticPopupCoordinatorLaw.Click(_staticPopupSlots, visible.Slot, buttonIndex: 1));
        }
        else
            ExecuteStaticPopupPlan(StaticPopupCoordinatorLaw.Click(_staticPopupSlots, visible.Slot, buttonIndex: clicked));
    }

    // ── Master loot ──────────────────────────────────────────────────────────────────────────

    /// <summary>SMSG_LOOT_MASTER_LIST: u8 count, count × u64 — sent to the master looter on loot open.</summary>
    private void ApplyLootMasterList(byte[] body)
    {
        var r = new PacketReader(body);
        byte count = r.ReadU8();
        _lootMasterCandidates.Clear();
        for (int i = 0; i < count && r.Remaining >= 8; i++)
        {
            ulong guid = r.ReadU64();
            _lootMasterCandidates.Add(guid);
            if (!_playerNames.ContainsKey(guid)) _net?.NameQuery(guid);
        }
        EmitInterface("loot", "master-list", "DECODED", _loot.Source, $"count={_lootMasterCandidates.Count}");
    }

    /// <summary>The candidate menu for a master-loot row: pick who receives the item.</summary>
    private void DrawLootMasterMenu()
    {
        if (_lootMasterMenuSlot < 0 || _skin is null) return;
        if (!_loot.IsOpen || _lootMasterCandidates.Count == 0) { _lootMasterMenuSlot = -1; return; }
        float s = GameplayUiScale();
        float rowHeight = 16 * s, width = 160 * s;
        Vector2 origin = _lootMasterMenuOrigin;
        Vector2 size = new(width, rowHeight * _lootMasterCandidates.Count + 8 * s);
        ImGui.SetNextWindowPos(origin, ImGuiCond.Always);
        ImGui.SetNextWindowSize(size, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0);
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoNav;
        if (!ImGui.Begin("##loot-master-menu", flags)) { ImGui.End(); return; }
        ImDrawListPtr draw = ImGui.GetWindowDrawList();
        _skin.DrawBackdrop(draw, origin, origin + size, WowSkin.Dialog);
        for (int i = 0; i < _lootMasterCandidates.Count; i++)
        {
            ulong candidate = _lootMasterCandidates[i];
            Vector2 min = origin + new Vector2(4 * s, 4 * s + i * rowHeight);
            ImGui.SetCursorScreenPos(min);
            bool clicked = ImGui.InvisibleButton($"##loot-master-{i}", new Vector2(width - 8 * s, rowHeight));
            if (ImGui.IsItemHovered())
            {
                uint highlight = _gameplayArt?.AdditiveHandle(DropdownCapsuleUiLaw.RowHighlight) ?? 0;
                if (highlight != 0) draw.AddImage((nint)highlight, min, min + new Vector2(width - 8 * s, rowHeight));
            }
            GameText.Draw(draw, "GameFontHighlightSmall",
                _playerNames.GetValueOrDefault(candidate, $"Player-{candidate & 0xffff:X4}"),
                min + new Vector2(4 * s, 2 * s), s);
            if (clicked && !RefuseTacticalFreezeLiveCommand("assigning loot") &&
                !RefuseTacticalFrozenActor(_loot.Source, "assign its loot") &&
                !RefuseTacticalFrozenActor(candidate, "assign loot to them"))
            {
                bool sent = _net?.LootMasterGive(_loot.Source, (byte)_lootMasterMenuSlot, candidate) == true;
                EmitInterface("loot", "master-give", sent ? "SENT" : "SEND_FAILED", _loot.Source,
                    $"slot={_lootMasterMenuSlot};target=0x{candidate:X16}");
                _lootMasterMenuSlot = -1;
            }
        }
        ImGui.End();
        if (ImGui.IsMouseClicked(ImGuiMouseButton.Left) && !ImGui.IsMouseHoveringRect(origin, origin + size))
            _lootMasterMenuSlot = -1;
    }

    private Vector2 _lootMasterMenuOrigin;

    // ── Broadcasts ───────────────────────────────────────────────────────────────────────────

    /// <summary>SMSG_SERVER_MESSAGE: u32 type, cstring text (1 shutdown in, 2 restart in, 3 text, 4/5 cancelled).</summary>
    private void ApplyServerMessage(byte[] body)
    {
        var r = new PacketReader(body);
        uint type = r.ReadU32();
        string text = r.Remaining > 0 ? r.ReadCString() : "";
        string line = type switch
        {
            1 => InventoryGlobalString("SERVER_MESSAGE_SHUTDOWN_TIME", "[SERVER] Shutdown in %s").Replace("%s", text, StringComparison.Ordinal),
            2 => InventoryGlobalString("SERVER_MESSAGE_RESTART_TIME", "[SERVER] Restart in %s").Replace("%s", text, StringComparison.Ordinal),
            4 => InventoryGlobalString("SERVER_MESSAGE_SHUTDOWN_CANCELLED", "[SERVER] Shutdown cancelled."),
            5 => InventoryGlobalString("SERVER_MESSAGE_RESTART_CANCELLED", "[SERVER] Restart cancelled."),
            _ => text,
        };
        if (line.Length > 0) AddChatMessage(line, ChatFrameLaw.MsgType.System);
    }

    /// <summary>SMSG_ZONE_UNDER_ATTACK: u32 areaId. The reference bails on an area it cannot name.</summary>
    private void ApplyZoneUnderAttack(byte[] body)
    {
        if (body.Length < 4) return;
        uint areaId = BitConverter.ToUInt32(body, 0);
        EnsureAreaTableForMinimap();
        string area = _areas?.ZoneName(areaId) ?? _areas?.AreaName(areaId) ?? "";
        if (area.Length == 0) return;
        AddChatMessage(InventoryGlobalString("ZONE_UNDER_ATTACK", "%s is under attack!")
            .Replace("%s", area, StringComparison.Ordinal), ChatFrameLaw.MsgType.System);
    }

    /// <summary>SMSG_DEFENSE_MESSAGE: u32 zoneId, u32 len, cstring text — the defense channel line.</summary>
    private void ApplyDefenseMessage(byte[] body)
    {
        var r = new PacketReader(body);
        if (r.Remaining < 8) return;
        r.ReadU32(); r.ReadU32();
        string text = r.Remaining > 0 ? r.ReadCString() : "";
        if (text.Length > 0) AddChatMessage(text, ChatFrameLaw.MsgType.System);
    }
}
