using System.Collections.Generic;
using System.Numerics;
using ImGuiNET;
using MSUIClient.Engine;
using MSUIClient.Net;
using Silk.NET.Input;

namespace MSUIClient;

public sealed partial class GameLoop
{
    /// <summary>
    /// CRPG/RTS control model. The session always belongs to the logged-in character
    /// (<see cref="LocalPlayerGuid"/>); <see cref="ControlledGuid"/> is the unit the player is
    /// actually driving — their own character, or a possessed party bot granted by the server
    /// (SMSG_SUI_CONTROL_ACK). All gameplay UI (bars, bags, talents, unit frames, cast feedback)
    /// keys off ControlledGuid; wire identity, session services (mail, quests, trainer), and
    /// telemetry stay on the session character.
    /// </summary>
    internal enum ControlState : byte
    {
        /// <summary>Driving the logged-in character (default).</summary>
        OwnChar,
        /// <summary>CMSG_SUI_CONTROL_REQUEST sent; input parked until the ACK resolves.</summary>
        PossessPending,
        /// <summary>Server granted control of <see cref="_controlTargetGuid"/>.</summary>
        Possessing,
        /// <summary>CMSG_SUI_CONTROL_RELEASE sent; input parked until the ACK resolves.</summary>
        ReleasePending,
        /// <summary>Detached fly camera; nobody is driven, the whole party runs on AI.</summary>
        FreeCam,
    }

    private ControlState _controlState = ControlState.OwnChar;
    private ulong _controlTargetGuid;

    /// <summary>The unit whose data the gameplay UI shows and whose movement input drives.</summary>
    internal ulong ControlledGuid =>
        _controlState is ControlState.Possessing or ControlState.ReleasePending && _controlTargetGuid != 0
            ? _controlTargetGuid
            : LocalPlayerGuid;

    /// <summary>
    /// Guid excluded from the streamed-player render pass because the first-person
    /// CharacterRenderer body stands in for it. 0 in FreeCam: everyone (own character included)
    /// renders as a streamed player.
    /// </summary>
    internal ulong RenderSelfGuid => _controlState == ControlState.FreeCam ? 0UL : ControlledGuid;

    // ── Per-unit action stores ────────────────────────────────────────────────────────────────
    // The session socket only ever streams the logged-in character's spellbook/bars/cooldowns;
    // a possessed bot's arrive wrapped in SMSG_SUI_PROXY and land in its own store. Same pattern
    // as the pet bar's separate cooldown store (Program.Pet.cs).

    private readonly Dictionary<ulong, PlayerActions> _actionsByGuid = new();

    private PlayerActions ActionsFor(ulong guid)
    {
        if (!_actionsByGuid.TryGetValue(guid, out PlayerActions? actions))
        {
            actions = new PlayerActions();
            _actionsByGuid[guid] = actions;
        }
        return actions;
    }

    /// <summary>The logged-in character's store — the target of un-proxied wire feeds.</summary>
    private PlayerActions OwnActions => ActionsFor(LocalPlayerGuid);

    /// <summary>
    /// The store the action-bar/spellbook/talent UI reads. Deliberately keeps the historical
    /// field name: every existing read of the single-character store now follows possession.
    /// </summary>
    private PlayerActions _actions => ActionsFor(ControlledGuid);

    /// <summary>Enter-world reset: drop every per-unit store (replaces `_actions.Clear()`).</summary>
    private void ResetActionStores() => _actionsByGuid.Clear();

    // ── SMSG_SUI_CONTROL_ACK result codes (SuperUI-Core SuiPossess.h) ─────────────────────────
    private const byte SuiAckOk = 0;
    private const byte SuiAckFirstRelease = 16;    // 16.. are releases, solicited or forced
    private const byte SuiAckReleasedFreecam = 17;

    // ── Control session state ─────────────────────────────────────────────────────────────────
    private readonly List<(ulong Guid, byte Flags)> _suiRoster = [];
    private double _controlPendingSince;
    private ControlState _controlPendingReturn = ControlState.OwnChar;   // watchdog fallback
    private ulong _controlSwitchQueued;      // cycle target waiting for the in-flight release ACK
    private bool _controlCycleWasDown;
    private bool _freecamKeyWasDown;
    private bool _freecamRequested;          // the in-flight release asked for the free view
    private const byte SuiRosterControllable = 0x01;
    private const byte SuiRosterPossessed = 0x02;
    private const double ControlAckTimeoutSeconds = 3.0;

    /// <summary>SMSG_SUI_CONTROL_ROSTER: which group members are possessable bots.</summary>
    private void ApplySuiControlRoster(byte[] body)
    {
        var r = new PacketReader(body);
        int count = r.ReadU8();
        _suiRoster.Clear();
        for (int i = 0; i < count && r.Remaining >= 9; i++)
            _suiRoster.Add((r.ReadU64(), r.ReadU8()));
    }

    /// <summary>
    /// SMSG_SUI_CONTROL_ACK. Grants and denials answer our requests; release codes (16+) can
    /// also arrive UNSOLICITED (bot died, group broke, teleport) and must be honoured in any
    /// state. The carried position is authoritative for whichever unit we drive next.
    /// </summary>
    private void ApplySuiControlAck(byte[] body)
    {
        var r = new PacketReader(body);
        ulong guid = r.ReadU64();
        byte result = r.ReadU8();
        float x = r.ReadF32(), y = r.ReadF32(), z = r.ReadF32(), o = r.ReadF32();

        if (result == SuiAckOk)
        {
            _controlTargetGuid = guid;
            _controlState = ControlState.Possessing;
            SeatControllerOnControlled(x, y, z, o);
            _net?.SetActiveMover(guid);
            EnterPlayerAuraWorld(guid);
            ApplyControlledCharacter();
            AddChatMessage($"You take control of {ResolveUnitName(guid)}.");
            return;
        }

        if (result == SuiAckReleasedFreecam && _freecamRequested)
        {
            // Enter the free view: nobody is driven, the whole party (own character
            // included) runs on AI. The controller becomes the fly rig where it stands;
            // RenderSelfGuid goes 0 so everyone renders from the entity stream.
            _freecamRequested = false;
            _controlTargetGuid = 0;
            _controlState = ControlState.FreeCam;
            _movementSender.Parked = false;       // the Flying branch parks it from here
            if (_controller is not null) _controller.Flying = true;
            if (_character is not null) _character.Enabled = false;
            EnterPlayerAuraWorld(LocalPlayerGuid);
            PurgeSuiSnapshot();
            AddChatMessage("Free view: Ctrl+RightClick orders the party, Ctrl+F returns.");
            return;
        }

        if (result >= SuiAckFirstRelease)
        {
            bool wasPossessing = _controlState is ControlState.Possessing or ControlState.ReleasePending;
            _controlTargetGuid = 0;
            _controlState = ControlState.OwnChar;
            SeatControllerOnControlled(x, y, z, o);
            _net?.SetActiveMover(LocalPlayerGuid);
            EnterPlayerAuraWorld(LocalPlayerGuid);
            PurgeSuiSnapshot();
            ApplyControlledCharacter();
            if (wasPossessing)
                AddChatMessage(result switch
                {
                    18 => "Control lost: your character died... no wait — the bot died.",
                    19 => "Control released: teleport.",
                    20 => "Control released: group changed.",
                    21 => "Control released: logout.",
                    _ => "Control returned to your character.",
                });
            // A queued cycle switch survives the voluntary release that preceded it.
            if (_controlSwitchQueued != 0)
            {
                ulong next = _controlSwitchQueued;
                _controlSwitchQueued = 0;
                if (result is >= SuiAckFirstRelease and <= SuiAckReleasedFreecam)
                    RequestPossess(next);
            }
            return;
        }

        // Denial: fall back to whatever we were before the request.
        if (_controlState == ControlState.PossessPending)
        {
            _controlState = ControlState.OwnChar;
            _movementSender.Parked = false;
        }
        _controlSwitchQueued = 0;
        ShowUiError(result switch
        {
            2 => "That party member is not a controllable bot.",
            3 => "Not in your group.",
            4 => "Someone is already controlling that bot.",
            5 => "That bot cannot be controlled right now.",
            6 => "You cannot take control right now.",
            _ => "Cannot take control.",
        });
    }

    /// <summary>
    /// SMSG_SUI_PROXY: an owner-only packet of the possessed bot (bars, spellbook,
    /// cooldowns, cast results), re-wrapped by the server with the bot's guid. Routed
    /// into the bot's per-guid store via the normal parsers; stragglers whose source
    /// is not the currently controlled unit are dropped (possession-boundary races).
    /// </summary>
    private void ApplySuiProxy(byte[] body)
    {
        var r = new PacketReader(body);
        if (r.Remaining < 10) return;
        ulong source = r.ReadU64();
        var innerOp = (Op)r.ReadU16();
        byte[] inner = r.ReadBytes(r.Remaining);
        if (source == 0 || source == LocalPlayerGuid || source != ControlledGuid) return;

        PlayerActions store = ActionsFor(source);
        switch (innerOp)
        {
            case Op.SMSG_ACTION_BUTTONS:
                store.ApplyButtons(inner);
                break;
            case Op.SMSG_INITIAL_SPELLS:
                store.ApplyInitialSpells(inner, MovementInfo.ClientUptimeMs() / 1000.0);
                break;
            case Op.SMSG_LEARNED_SPELL:
                store.Learn(new PacketReader(inner).ReadU16());
                break;
            case Op.SMSG_SUPERCEDED_SPELL:
                {
                    var ir = new PacketReader(inner);
                    store.Supercede(ir.ReadU16(), ir.ReadU16());
                }
                break;
            case Op.SMSG_REMOVED_SPELL:
                store.Remove(new PacketReader(inner).ReadU16());
                break;
            case Op.SMSG_SPELL_COOLDOWN:
                // Addressed packet — self-routes to the right store by embedded caster guid.
                ApplyAddressedSpellCooldowns(inner);
                break;
            case Op.SMSG_CAST_RESULT:
                {
                    var result = SpellPacketParser.ParseResult(inner);
                    if (result.Status == 2)
                        EnqueueSpellPresentation(new SpellCastResultEvent(result.SpellId, result.Reason));
                }
                break;
            default:
                // COOLDOWN_EVENT / CLEAR_COOLDOWN and future whitelist growth: ignored in v1.0.
                break;
        }
    }

    // ── SMSG_SUI_SNAPSHOT: read-only bags/talents for the possessed bot (M4) ──────────────────
    // The wire never streams owner-only fields to a non-owner; the snapshot is injected into
    // the bot's local WorldEntity fields + synthetic item entities so the already-parameterized
    // inventory/talent UI renders it unchanged. Purged on release.

    private readonly List<ulong> _suiSnapshotItemGuids = [];

    private void ApplySuiSnapshot(byte[] body)
    {
        var r = new PacketReader(body);
        if (r.Remaining < 18) return;
        ulong source = r.ReadU64();
        uint talentPoints = r.ReadU32();
        uint coinage = r.ReadU32();
        int count = r.ReadU16();
        if (source != ControlledGuid || !_entities.TryGet(source, out WorldEntity bot)) return;

        PurgeSuiSnapshot();
        bot.Fields.SetU32(ObjectFields.PLAYER_CHARACTER_POINTS1, talentPoints);
        bot.Fields.SetU32(ObjectFields.PLAYER_COINAGE, coinage);

        for (int i = 0; i < count && r.Remaining >= 19; i++)
        {
            byte bag = r.ReadU8();
            byte slot = r.ReadU8();
            ulong itemGuid = r.ReadU64();
            uint entry = r.ReadU32();
            uint stack = r.ReadU32();
            byte bagSlots = r.ReadU8();

            var fields = new ObjectFields().AsCreated();
            fields.SetU32(ObjectFields.OBJECT_ENTRY, entry);
            fields.SetU32(ObjectFields.ITEM_STACK_COUNT, stack);
            _entities.AddSynthetic(new WorldEntity
            {
                Guid = itemGuid,
                Type = bagSlots > 0 ? ObjectTypeId.Container : ObjectTypeId.Item,
                Fields = fields,
            });
            _suiSnapshotItemGuids.Add(itemGuid);

            if (bag == 255)
            {
                // Character-held: equipment 0-18, bag slots 19-22, backpack 23-38, keyring 81+ —
                // one contiguous guid array from PLAYER_INV_SLOT_HEAD.
                bot.Fields.SetGuid((ushort)(ObjectFields.PLAYER_INV_SLOT_HEAD + slot * 2), itemGuid);
            }
            else
            {
                // Contents of an equipped bag; the bag row itself came earlier in the stream.
                ulong bagGuid = bot.Fields.PlayerInventorySlot(bag);
                if (bagGuid != 0 && _entities.TryGet(bagGuid, out WorldEntity bagEntity))
                    bagEntity.Fields.SetGuid((ushort)(ObjectFields.CONTAINER_SLOT_1 + slot * 2), itemGuid);
            }
            if (_net is not null) _items?.Require(entry, itemGuid, _net);
        }

        // Gear templates may have just resolved — rebuild the possessed body once more.
        ApplyControlledCharacter();
    }

    private void PurgeSuiSnapshot()
    {
        foreach (ulong guid in _suiSnapshotItemGuids)
            _entities.RemoveSynthetic(guid);
        _suiSnapshotItemGuids.Clear();
    }

    /// <summary>Snap the local, client-authoritative controller onto the ACK position.</summary>
    private void SeatControllerOnControlled(float x, float y, float z, float o)
    {
        _movementSender.Parked = false;
        _freecamRequested = false;
        if (_controller is not null) _controller.Flying = false;   // exits the free-view fly rig
        if (_character is not null) _character.Enabled = true;
        _controller?.Teleport(x, y, z);
        if (_controller is not null) _controller.Yaw = o;
        _window.Camera.Yaw = o;
        _window.Camera.OrbitYaw = 0f;
        if (_controller is not null) _window.Camera.Target = _controller.Position;
        _movementSender.Reset(o);
        _playerPortraitDirty = true;
        _paperDollDirty = true;
    }

    /// <summary>Ask the server for control of a party bot (portrait Alt+click / cycle key).</summary>
    internal void RequestPossess(ulong guid)
    {
        if (_net is not { IsInWorld: true } || _controller is null) return;
        if (guid == 0 || guid == LocalPlayerGuid) return;
        if (_controlState != ControlState.OwnChar) return;
        // Flush a MSG_MOVE_STOP at the own character's position BEFORE the request so no
        // in-flight movement straggles into the mover swap, then park the stream.
        _movementSender.ParkForRoot(_net, _controller);
        _movementSender.Parked = true;
        _controlState = ControlState.PossessPending;
        _controlPendingReturn = ControlState.OwnChar;
        _controlPendingSince = NowSeconds();
        _net.SuiControlRequest(guid);
    }

    /// <summary>Give control back (cycle to self). The bot's AI resumes server-side.</summary>
    internal void RequestControlRelease(bool toFreecam)
    {
        if (_net is null || _controller is null) return;
        if (_controlState != ControlState.Possessing) return;
        _movementSender.ParkForRoot(_net, _controller);   // stops the bot dead at its position
        _movementSender.Parked = true;
        _controlState = ControlState.ReleasePending;
        _controlPendingReturn = ControlState.Possessing;
        _controlPendingSince = NowSeconds();
        _freecamRequested = toFreecam;
        _net.SuiControlRelease(toFreecam ? (byte)1 : (byte)0);
    }

    /// <summary>
    /// Ctrl+F: enter/leave the CRPG free view. From the own character or a possessed bot
    /// the server keeps/attaches the unattended AI (release mode 1); from the free view a
    /// plain release (mode 0) returns to manual control of the own character.
    /// </summary>
    private void ToggleFreeView()
    {
        if (_net is not { IsInWorld: true } || _controller is null) return;
        switch (_controlState)
        {
            case ControlState.OwnChar:
                _movementSender.ParkForRoot(_net, _controller);
                _movementSender.Parked = true;
                _controlState = ControlState.ReleasePending;
                _controlPendingReturn = ControlState.OwnChar;
                _controlPendingSince = NowSeconds();
                _freecamRequested = true;
                _net.SuiControlRelease(1);
                break;
            case ControlState.Possessing:
                RequestControlRelease(toFreecam: true);
                break;
            case ControlState.FreeCam:
                // No pending state: stay in the fly rig until the ACK teleports us home.
                _net.SuiControlRelease(0);
                break;
        }
    }

    /// <summary>
    /// Ctrl+Tab / Ctrl+Shift+Tab cycles control across [own character + controllable party
    /// bots], hard-coded like the F fly toggle. Runs every frame from the input chain.
    /// Also watches the pending-state ACK timeout (server without SUI support, packet loss).
    /// </summary>
    private void UpdateControlInput(bool typing)
    {
        // Pending-state watchdog: never strand the movement stream parked.
        if (_controlState is ControlState.PossessPending or ControlState.ReleasePending &&
            NowSeconds() - _controlPendingSince > ControlAckTimeoutSeconds)
        {
            _controlState = _controlPendingReturn;
            _movementSender.Parked = false;
            _controlSwitchQueued = 0;
            _freecamRequested = false;
            ShowUiError("No answer from the server (SUI control).");
        }

        bool ctrl = InputKeyDown(Key.ControlLeft) || InputKeyDown(Key.ControlRight);
        bool tab = InputKeyDown(Key.Tab);
        bool cyclePressed = ctrl && tab;
        if (cyclePressed && !_controlCycleWasDown && !typing && _net is { IsInWorld: true })
            CycleControl(ShiftHeld() ? -1 : +1);
        _controlCycleWasDown = cyclePressed;

        // Ctrl+F: free view toggle (plain F stays the local fly toggle).
        bool freecamPressed = ctrl && InputKeyDown(Key.F);
        if (freecamPressed && !_freecamKeyWasDown && !typing && _net is { IsInWorld: true })
            ToggleFreeView();
        _freecamKeyWasDown = freecamPressed;
    }

    /// <summary>
    /// Free-view world click (routed from the targeting click queue). Left selects;
    /// Ctrl+RightClick issues the RTS order: hostile under cursor → party attack,
    /// otherwise ground point → party move.
    /// </summary>
    private void HandleFreeCamWorldClick(WorldMouseClick click)
    {
        if (click.Button == MouseButton.Left)
        {
            CommitSelection(PickUnit(click.Position), beginAttack: false);
            return;
        }
        if (click.Button != MouseButton.Right) return;
        bool ctrl = InputKeyDown(Key.ControlLeft) || InputKeyDown(Key.ControlRight);
        if (!ctrl) return;

        ulong picked = PickUnit(click.Position);
        if (picked != 0 && _entities.TryGet(picked, out WorldEntity target) &&
            !target.IsDead && CanAttack(target))
        {
            _net?.SuiOrder(1, [], picked, 0, 0, 0);
            AddChatMessage($"Party: attack {ResolveWorldUnitName(picked)}!");
        }
        else if (TryPickGround(click.Position, out System.Numerics.Vector3 point))
        {
            _net?.SuiOrder(0, [], 0, point.X, point.Y, point.Z);
            AddChatMessage($"Party: move to ({point.X:F0}, {point.Y:F0}).");
        }
    }

    private string ResolveWorldUnitName(ulong guid)
    {
        if (_playerNames.TryGetValue(guid, out string? playerName)) return playerName;
        if (_entities.TryGet(guid, out WorldEntity unit) && unit.IsCreature &&
            _creatureNames.TryGetValue(unit.Entry, out string? creatureName)) return creatureName;
        return "target";
    }

    private void CycleControl(int direction)
    {
        // Own character first, then the roster's controllable bots in roster order.
        List<ulong> ring = [LocalPlayerGuid];
        foreach ((ulong guid, byte flags) in _suiRoster)
            if ((flags & SuiRosterControllable) != 0 &&
                ((flags & SuiRosterPossessed) == 0 || guid == _controlTargetGuid))
                ring.Add(guid);
        if (ring.Count <= 1)
        {
            ShowUiError("No controllable party bots.");
            return;
        }

        int index = ring.IndexOf(ControlledGuid);
        if (index < 0) index = 0;
        ulong next = ring[(index + direction + ring.Count) % ring.Count];
        SwitchControlTo(next);
    }

    /// <summary>Jump control to a unit, chaining release→possess when already possessing.</summary>
    internal void SwitchControlTo(ulong guid)
    {
        if (guid == ControlledGuid) return;
        switch (_controlState)
        {
            case ControlState.OwnChar when guid != LocalPlayerGuid:
                RequestPossess(guid);
                break;
            case ControlState.Possessing when guid == LocalPlayerGuid:
                RequestControlRelease(toFreecam: false);
                break;
            case ControlState.Possessing:
                _controlSwitchQueued = guid;      // resumes in ApplySuiControlAck
                RequestControlRelease(toFreecam: false);
                break;
        }
    }

    private string ResolveUnitName(ulong guid) =>
        _playerNames.TryGetValue(guid, out string? name) ? name : $"unit {guid:X}";

    /// <summary>Small HUD line while controlling a bot or waiting on the server.</summary>
    private void DrawControlBanner()
    {
        string text = _controlState switch
        {
            ControlState.Possessing =>
                $"Controlling {ResolveUnitName(_controlTargetGuid)} — Ctrl+Tab to switch, Ctrl+F free view",
            ControlState.PossessPending => "Taking control…",
            ControlState.ReleasePending => "Releasing control…",
            ControlState.FreeCam => "Free view — Ctrl+RightClick: order party · Ctrl+F: return",
            _ => "",
        };
        if (text.Length == 0) return;

        var io = ImGui.GetIO();
        var draw = ImGui.GetForegroundDrawList();
        Vector2 size = ImGui.CalcTextSize(text);
        var pos = new Vector2((io.DisplaySize.X - size.X) * 0.5f, 24f);
        draw.AddRectFilled(pos - new Vector2(8, 4), pos + size + new Vector2(8, 4), 0x99000000u, 4f);
        draw.AddText(pos, 0xFF40D0FFu, text);
    }
}
