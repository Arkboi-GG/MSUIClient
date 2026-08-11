using System.Collections.Generic;
using System.Numerics;
using ImGuiNET;
using MSUIClient.Engine;
using MSUIClient.Formats;
using MSUIClient.Net;
using MSUIClient.World.Units;
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
    /// The guid whose bars/spellbook/talents the HUD displays. In the free view a single
    /// selected party bot swaps its bars in (read-only inspection); otherwise the
    /// controlled unit's, exactly as before.
    /// </summary>
    internal ulong BarsGuid =>
        _controlState == ControlState.FreeCam && _freecamSelection.Count == 1 &&
        _freecamSelection[0] != LocalPlayerGuid
            ? _freecamSelection[0]
            : ControlledGuid;

    /// <summary>Free-view inspection shows another unit's bars; they must never act.</summary>
    private bool BarsReadOnly => BarsGuid != ControlledGuid;

    /// <summary>
    /// The store the action-bar/spellbook/talent UI reads. Deliberately keeps the historical
    /// field name: every existing read of the single-character store now follows possession
    /// (and, in the free view, the inspected selection).
    /// </summary>
    private PlayerActions _actions => ActionsFor(BarsGuid);

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
    private bool _controlledBodyPending;     // possessed body rebuild waiting on entity stream-in
    private bool _controlCycleWasDown;

    // ── Free-view marquee selection (party only, v1) ──────────────────────────────────────────
    private Vector2? _freecamDragOrigin;         // left button went down here, over the world
    private bool _freecamDragActive;             // travel exceeded the click threshold
    private bool _freecamMarqueeConsumedClick;   // swallow the release's queued world click
    private readonly List<ulong> _freecamSelection = [];
    private const float FreecamDragThresholdPixels = 6f;
    private readonly List<(Vector3 Pos, double Born, Vector3 Tint)> _rtsMoveMarkers = [];
    private readonly List<Vector3> _rtsWaypointChain = [];   // Ctrl+RightClick chain dots
    private Vector3 _freecamCamSentPosition;                 // last CMSG_SUI_CAM position
    private double _freecamCamSentAt;                        // and when it went out
    private static readonly Vector3 RtsFriendlyTint = new(0.30f, 0.95f, 0.45f);
    private static readonly Vector3 RtsHostileTint = new(0.95f, 0.30f, 0.22f);
    private static readonly Vector3 RtsNeutralTint = new(0.95f, 0.85f, 0.25f);
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

        // Both solicited release codes honour a pending free-view request; only the
        // forced codes (18+: death, teleport, group change) override it. A server
        // answering 16 to a mode-1 release must not snap the camera back to the
        // character — that read as a momentary floor-drop.
        if (_freecamRequested && result is SuiAckFirstRelease or SuiAckReleasedFreecam)
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
            AddChatMessage("Free view: drag-select the party, RightClick to move/attack, " +
                "Ctrl+RightClick chains waypoints, Ctrl+F returns.");
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

        // Denial: fall back to whatever we were before the request (a free-view
        // possess click drops back into the free view, not onto the own character).
        if (_controlState == ControlState.PossessPending)
        {
            _controlState = _controlPendingReturn == ControlState.FreeCam
                ? ControlState.FreeCam
                : ControlState.OwnChar;
            _movementSender.Parked = false;
        }
        else if (_controlState == ControlState.FreeCam)
        {
            // The server refused the free-view exit (it no longer holds any control
            // session for us). Land locally on the own character rather than staying
            // stuck in the fly rig with no way back.
            ExitFreeCamLocally();
            return;
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
                // Bots own no server-side buttons; an empty wire bar hands the
                // slots to the layered client bars (base / class / per-bot).
                if (store.OccupiedCount == 0 && store.KnownSpells.Count > 0)
                    PopulateBotBar(source);
                break;
            case Op.SMSG_INITIAL_SPELLS:
                store.ApplyInitialSpells(inner, MovementInfo.ClientUptimeMs() / 1000.0);
                if (store.OccupiedCount == 0 && store.KnownSpells.Count > 0)
                    PopulateBotBar(source);
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

    /// <summary>
    /// Leave the free view without a server ACK position: seat on the own character's
    /// streamed entity (or just drop the rig in place when it isn't resident).
    /// </summary>
    private void ExitFreeCamLocally()
    {
        _controlState = ControlState.OwnChar;
        _controlTargetGuid = 0;
        if (_entities.TryGet(LocalPlayerGuid, out WorldEntity self))
            SeatControllerOnControlled(self.Position.X, self.Position.Y, self.Position.Z, self.Orientation);
        else
        {
            if (_controller is not null) _controller.Flying = false;
            if (_character is not null) _character.Enabled = true;
            _movementSender.Parked = false;
        }
        _net?.SetActiveMover(LocalPlayerGuid);
        EnterPlayerAuraWorld(LocalPlayerGuid);
        ApplyControlledCharacter();
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

    /// <summary>
    /// Ask the server for control of a party bot (portrait Alt+click / cycle key / a
    /// free-view click on a party toon — CRPG mode: click a character, you drive it).
    /// </summary>
    internal void RequestPossess(ulong guid)
    {
        if (_net is not { IsInWorld: true } || _controller is null) return;
        if (guid == 0 || guid == LocalPlayerGuid) return;
        if (_controlState is not (ControlState.OwnChar or ControlState.FreeCam)) return;
        // Flush a MSG_MOVE_STOP at the own character's position BEFORE the request so no
        // in-flight movement straggles into the mover swap, then park the stream. (From
        // the free view the stream is already silent — flags are clear, nothing flushes.)
        _movementSender.ParkForRoot(_net, _controller);
        _movementSender.Parked = true;
        _controlPendingReturn = _controlState;
        _controlState = ControlState.PossessPending;
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

        // A possess granted before the bot's entity streamed in leaves the body
        // rebuild pending; retry until the fields are resident.
        if (_controlledBodyPending && _controlState == ControlState.Possessing)
            ApplyControlledCharacter();

        UpdateFreeCamSelection();

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
    /// Free-view marquee lifecycle, run every frame from the input chain. The window's
    /// FreeSelectMode keeps the LEFT button out of camera look while the free view is up,
    /// so a left-drag over the world becomes the RTS selection rectangle. The queued
    /// world click that the release still produces is swallowed via
    /// <see cref="_freecamMarqueeConsumedClick"/> (drag travel never accumulates when the
    /// mouse isn't captured, so the window classifies the release as a click).
    /// </summary>
    private void UpdateFreeCamSelection()
    {
        bool inFreeCam = _controlState == ControlState.FreeCam;
        _window.FreeSelectMode = inFreeCam;
        if (!inFreeCam)
        {
            _freecamDragOrigin = null;
            _freecamDragActive = false;
            _freecamMarqueeConsumedClick = false;
            _freecamSelection.Clear();
            _rtsWaypointChain.Clear();
            return;
        }

        // Keep the server's streaming eye under the camera: heartbeat every 2 s, and
        // whenever the rig has flown more than a few yards since the last send.
        if (_controller is not null)
        {
            double now = NowSeconds();
            Vector3 rig = _controller.Position;
            if (now - _freecamCamSentAt > 2.0 ||
                Vector3.DistanceSquared(rig, _freecamCamSentPosition) > 5f * 5f)
            {
                if (_net?.SuiCam(rig.X, rig.Y, rig.Z) == true)
                {
                    _freecamCamSentPosition = rig;
                    _freecamCamSentAt = now;
                }
            }
        }

        bool leftDown = _window.MouseLeftDown;
        Vector2 mouse = _window.MousePosition;

        if (leftDown && _freecamDragOrigin is null &&
            !_window.MouseCaptured && !ImGui.GetIO().WantCaptureMouse)
            _freecamDragOrigin = mouse;

        if (leftDown && !_freecamDragActive && _freecamDragOrigin is Vector2 origin &&
            (mouse - origin).Length() > FreecamDragThresholdPixels)
            _freecamDragActive = true;

        if (!leftDown && _freecamDragOrigin is Vector2 anchor)
        {
            if (_freecamDragActive)
            {
                CommitMarqueeSelection(anchor, mouse);
                _freecamMarqueeConsumedClick = true;
            }
            _freecamDragOrigin = null;
            _freecamDragActive = false;
        }
    }

    /// <summary>Party members (own character + group) — the v1 selectable set.</summary>
    private IEnumerable<ulong> FreeCamSelectableGuids()
    {
        yield return LocalPlayerGuid;
        foreach (PartyMember member in _partyMembers)
            if (member.Guid != LocalPlayerGuid)
                yield return member.Guid;
    }

    private void CommitMarqueeSelection(Vector2 a, Vector2 b)
    {
        _freecamSelection.Clear();
        Vector2 min = Vector2.Min(a, b);
        Vector2 max = Vector2.Max(a, b);
        Vector2 display = ImGui.GetIO().DisplaySize;
        foreach (ulong guid in FreeCamSelectableGuids())
        {
            if (!_entities.TryGet(guid, out WorldEntity unit) || unit.IsDead) continue;
            if (!_window.Camera.TryWorldToScreen(unit.Position, display, out Vector2 screen)) continue;
            if (screen.X >= min.X && screen.X <= max.X && screen.Y >= min.Y && screen.Y <= max.Y)
                _freecamSelection.Add(guid);
        }
        if (_freecamSelection.Count == 1)
            EnsureBotBarForViewing(_freecamSelection[0]);
        if (_freecamSelection.Count > 0)
            AddChatMessage($"Selected {_freecamSelection.Count}: RightClick the ground to move, a hostile to attack.");
    }

    /// <summary>
    /// Free-view world click (routed from the targeting click queue). Left selects (a
    /// party member joins the highlighted set; empty ground clears it). RightClick
    /// orders the HIGHLIGHTED set: hostile under cursor → attack, ground → move.
    /// Ctrl+RightClick keeps ordering the whole party regardless of the selection.
    /// </summary>
    private void HandleFreeCamWorldClick(WorldMouseClick click)
    {
        if (click.Button == MouseButton.Left)
        {
            if (_freecamMarqueeConsumedClick)
            {
                _freecamMarqueeConsumedClick = false;
                return;
            }
            ulong pickedUnit = PickUnit(click.Position);
            // CRPG rule: clicking a party toon in the free view IS taking control of it —
            // the same jump as Ctrl+Tab / Alt+clicking its portrait. Bars go live.
            if (pickedUnit != 0)
                foreach (ulong guid in FreeCamSelectableGuids())
                    if (guid == pickedUnit)
                    {
                        SwitchControlTo(guid);
                        return;
                    }
            CommitSelection(pickedUnit, beginAttack: false);
            _freecamSelection.Clear();
            return;
        }
        if (click.Button != MouseButton.Right) return;

        bool ctrl = InputKeyDown(Key.ControlLeft) || InputKeyDown(Key.ControlRight);
        List<ulong> subjects = [.. _freecamSelection];

        ulong picked = PickUnit(click.Position);
        if (picked != 0 && _entities.TryGet(picked, out WorldEntity target) &&
            !target.IsDead && CanAttack(target))
        {
            if (subjects.Count == 0 && !ctrl) return;   // nothing highlighted, no accidental orders
            _net?.SuiOrder(1, subjects, picked, 0, 0, 0);
            _rtsMoveMarkers.Add((target.Position, NowSeconds(), RtsHostileTint));
            _rtsWaypointChain.Clear();
            AddChatMessage($"{OrderSubjectLabel(subjects)}: attack {ResolveWorldUnitName(picked)}!");
        }
        else if (TryPickGround(click.Position, out System.Numerics.Vector3 point))
        {
            if (ctrl)
            {
                // Ctrl+RightClick chains a waypoint (whole party when nothing is highlighted).
                _net?.SuiOrder(3, subjects, 0, point.X, point.Y, point.Z);
                _rtsWaypointChain.Add(point);
                _rtsMoveMarkers.Add((point, NowSeconds(), RtsNeutralTint));
                AddChatMessage($"{OrderSubjectLabel(subjects)}: waypoint {_rtsWaypointChain.Count} " +
                    $"({point.X:F0}, {point.Y:F0}).");
            }
            else
            {
                if (subjects.Count == 0) return;   // plain move needs a highlighted set
                _net?.SuiOrder(0, subjects, 0, point.X, point.Y, point.Z);
                _rtsMoveMarkers.Add((point, NowSeconds(), RtsFriendlyTint));
                _rtsWaypointChain.Clear();
                AddChatMessage($"{OrderSubjectLabel(subjects)}: move to ({point.X:F0}, {point.Y:F0}).");
            }
        }
    }

    private string OrderSubjectLabel(List<ulong> subjects) => subjects.Count switch
    {
        0 => "Party",
        1 => ResolveUnitName(subjects[0]),
        _ => $"Party ({subjects.Count})",
    };

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
        if (guid == ControlledGuid && _controlState != ControlState.FreeCam) return;
        switch (_controlState)
        {
            case ControlState.OwnChar when guid != LocalPlayerGuid:
            case ControlState.FreeCam when guid != LocalPlayerGuid:
                RequestPossess(guid);
                break;
            case ControlState.FreeCam:
                ToggleFreeView();   // clicked the own character: back to driving it
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

    /// <summary>
    /// Free-view selection overlay — only the live marquee rectangle lives in screen
    /// space now. Selection rings are world-space ground decals (RenderRtsGroundFx) and
    /// the members inside the rectangle light up through the renderer highlight.
    /// </summary>
    private void DrawFreeCamSelectionOverlay()
    {
        if (_controlState != ControlState.FreeCam) return;
        var draw = ImGui.GetForegroundDrawList();
        Vector2 display = ImGui.GetIO().DisplaySize;

        // Dashed connector through the waypoint chain, so the route reads at a glance.
        if (_rtsWaypointChain.Count > 1)
        {
            Vector2? previous = null;
            foreach (Vector3 waypoint in _rtsWaypointChain)
            {
                if (!_window.Camera.TryWorldToScreen(waypoint, display, out Vector2 screen))
                { previous = null; continue; }
                if (previous is Vector2 from) DrawDashedLine(draw, from, screen, 0xAA55D8F0, 10f, 7f);
                previous = screen;
            }
        }

        if (!_freecamDragActive || _freecamDragOrigin is not Vector2 origin) return;
        Vector2 mouse = _window.MousePosition;
        Vector2 min = Vector2.Min(origin, mouse);
        Vector2 max = Vector2.Max(origin, mouse);
        draw.AddRectFilled(min, max, 0x2240E080);
        draw.AddRect(min, max, 0xCC40E080);
    }

    private static void DrawDashedLine(ImDrawListPtr draw, Vector2 from, Vector2 to,
        uint color, float dash, float gap)
    {
        Vector2 delta = to - from;
        float length = delta.Length();
        if (length < 1f) return;
        Vector2 dir = delta / length;
        for (float at = 0f; at < length; at += dash + gap)
        {
            float end = MathF.Min(at + dash, length);
            draw.AddLine(from + dir * at, from + dir * end, color, 2f);
        }
    }

    /// <summary>Party members the live marquee rectangle currently covers (drag preview).</summary>
    private void AddMarqueePreview(ISet<ulong> set)
    {
        if (_controlState != ControlState.FreeCam ||
            !_freecamDragActive || _freecamDragOrigin is not Vector2 origin) return;
        Vector2 mouse = _window.MousePosition;
        Vector2 min = Vector2.Min(origin, mouse);
        Vector2 max = Vector2.Max(origin, mouse);
        Vector2 display = ImGui.GetIO().DisplaySize;
        foreach (ulong guid in FreeCamSelectableGuids())
        {
            if (!_entities.TryGet(guid, out WorldEntity unit) || unit.IsDead) continue;
            if (!_window.Camera.TryWorldToScreen(unit.Position, display, out Vector2 feet)) continue;
            if (feet.X >= min.X && feet.X <= max.X && feet.Y >= min.Y && feet.Y <= max.Y)
                set.Add(guid);
        }
    }

    /// <summary>
    /// World-space RTS ground FX: depth-tested selection rings under every selected unit
    /// (the model occludes the far arc) and the animated move-confirm markers. Runs in the
    /// 3-D render pass after units have populated depth — never from the HUD.
    /// </summary>
    private void RenderRtsGroundFx()
    {
        if (_spellEffectMeshes is null) return;
        _spellEffectMeshes.GatherGround ??= _terrain is not null
            ? _terrain.GatherGroundTriangles : null;
        double now = NowSeconds();
        _rtsMoveMarkers.RemoveAll(m => now - m.Born > 0.9);

        if (_controlState == ControlState.FreeCam)
        {
            List<SpellEffectMeshRenderer.UnitRing> rings = [];
            float pulse = 0.80f + 0.15f * MathF.Sin((float)(now * 3.0));
            foreach (ulong guid in _freecamSelection)
            {
                if (!_entities.TryGet(guid, out WorldEntity unit) || unit.IsDead) continue;
                float radius = 1.05f * MathF.Max(0.5f, unit.Scale <= 0f ? 1f : unit.Scale);
                rings.Add(new(unit.Position, radius, RtsFriendlyTint, pulse));
            }
            // A non-party pick (mob clicked from the sky) rings in its reaction colour.
            if (_selectionGuid != 0 && !_freecamSelection.Contains(_selectionGuid) &&
                _entities.TryGet(_selectionGuid, out WorldEntity target) && !target.IsDead)
            {
                Vector3 tint = ReactionTargetTowardPlayer(target) switch
                {
                    FactionReaction.Hostile => RtsHostileTint,
                    FactionReaction.Friendly => RtsFriendlyTint,
                    _ => RtsNeutralTint,
                };
                float radius = 1.05f * MathF.Max(0.5f, target.Scale <= 0f ? 1f : target.Scale);
                rings.Add(new(target.Position, radius, tint, pulse));
            }
            // Waypoint-chain dots: small persistent rings until a plain move/stop replaces them.
            foreach (Vector3 waypoint in _rtsWaypointChain)
                rings.Add(new(waypoint, 0.40f, RtsNeutralTint, 0.55f));

            if (rings.Count > 0)
                _spellEffectMeshes.RenderSelectionRings(_window.Camera, rings);
        }

        if (_rtsMoveMarkers.Count > 0)
        {
            List<SpellEffectMeshRenderer.MoveMarker> markers = [];
            foreach ((Vector3 pos, double born, Vector3 tint) in _rtsMoveMarkers)
                markers.Add(new(pos, (float)(now - born), tint));
            _spellEffectMeshes.RenderMoveMarkers(_window.Camera, markers);
        }
    }

    /// <summary>Small HUD line while controlling a bot or waiting on the server.</summary>
    private void DrawControlBanner()
    {
        string text = _controlState switch
        {
            ControlState.Possessing =>
                $"Controlling {ResolveUnitName(_controlTargetGuid)} — Ctrl+Tab to switch, Ctrl+F free view",
            ControlState.PossessPending => "Taking control…",
            ControlState.ReleasePending => "Releasing control…",
            ControlState.FreeCam => _freecamSelection.Count == 1 && _freecamSelection[0] != LocalPlayerGuid
                ? $"Free view — {ResolveUnitName(_freecamSelection[0])}'s bars (read-only) · " +
                  "RightClick: move/attack · Ctrl+RightClick: waypoints · Ctrl+F: return"
                : "Free view — drag: select · RightClick: move/attack · Ctrl+RightClick: chain waypoints · Ctrl+F: return",
            _ => "",
        };
        if (text.Length == 0) return;

        var io = ImGui.GetIO();
        var draw = ImGui.GetForegroundDrawList();
        Vector2 size = ImGui.CalcTextSize(text);
        var pos = new Vector2((io.DisplaySize.X - size.X) * 0.5f, 24f);
        draw.AddRectFilled(pos - new Vector2(8, 4), pos + size + new Vector2(8, 4), 0x99000000u, 4f);
        draw.AddText(pos, 0xFF40D0FFu, text);

        if (_controlState == ControlState.Possessing)
            DrawBotBarLayerToggle(pos.Y + size.Y + 12f);
    }

    /// <summary>
    /// While driving a bot, bar edits are client-persisted; this picks the layer they land
    /// on — the named bot's override map, or the customization shared by its whole class.
    /// </summary>
    private void DrawBotBarLayerToggle(float y)
    {
        var io = ImGui.GetIO();
        string className = BotClassName(ControlledGuid, ResolveUnitName(ControlledGuid));
        ImGui.SetNextWindowPos(new Vector2(io.DisplaySize.X * 0.5f, y), ImGuiCond.Always,
            new Vector2(0.5f, 0f));
        ImGui.SetNextWindowBgAlpha(0.55f);
        if (ImGui.Begin("##botbar-layer", ImGuiWindowFlags.NoDecoration |
            ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoFocusOnAppearing))
        {
            ImGui.TextUnformatted("Bar edits save to:");
            ImGui.SameLine();
            if (ImGui.RadioButton("this bot", !_botBarSaveToClass)) _botBarSaveToClass = false;
            ImGui.SameLine();
            if (ImGui.RadioButton(className.Length != 0 ? $"all {className}s" : "class",
                    _botBarSaveToClass))
                _botBarSaveToClass = true;
        }
        ImGui.End();
    }
}
