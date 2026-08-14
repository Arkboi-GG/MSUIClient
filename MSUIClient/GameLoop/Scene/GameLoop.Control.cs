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

    /// <summary>
    /// The RTS camera is up: detached fly rig, marquee selection, order clicks, ground FX.
    ///
    /// DELIBERATELY INDEPENDENT of <see cref="_controlState"/>. Clicking a party toon from
    /// the sky takes control of it — its bars, bags and spells become the live HUD — but the
    /// camera STAYS in the sky, because possession is a control decision and the free view is
    /// a camera decision. Ctrl+F is the only thing that puts the camera down. (Before this,
    /// <see cref="ControlState.FreeCam"/> conflated the two and every possess dropped you
    /// out of the free view.)
    /// </summary>
    private bool _freeView;

    /// <summary>
    /// Raise or lower the free view, TELLING THE SERVER about the transition.
    ///
    /// The server has to know, because the free view is not just a client camera to it: while
    /// it is up the server keeps a streaming eye under the camera, and treats a possessed bot
    /// as commanded-remotely — which deliberately runs that bot's OWN AI, on the reasoning
    /// that the client's movement stream is parked and nothing else would move it.
    ///
    /// Landing without saying so leaves both switches on. The bot's AI then keeps driving it
    /// while you believe you are: your movement is local-only prediction, your swings never
    /// land, nobody follows you, and the moment control is released the server's position wins
    /// and you snap back to the group. Every transition goes through here for that reason.
    /// </summary>
    private void SetFreeView(bool up)
    {
        if (_freeView == up) return;
        _freeView = up;
        _freeViewExitRequested = false;
        // A vanilla world map left open would hijack the whole HUD via its
        // fullscreen early-return; the free view has its own map (M → commander).
        if (up) _worldMapOpen = false;
        // Force the next heartbeat: on the way up it rebuilds the eye the possess tore down,
        // and on the way down this IS the notification.
        _freecamCamSentAt = 0;
        if (!up) _net?.SuiCam(0f, 0f, 0f, active: false);
    }

    /// <summary>The unit whose data the gameplay UI shows and whose movement input drives.</summary>
    internal ulong ControlledGuid =>
        _controlState is ControlState.Possessing or ControlState.ReleasePending && _controlTargetGuid != 0
            ? _controlTargetGuid
            : LocalPlayerGuid;

    /// <summary>
    /// Guid excluded from the streamed-player render pass because the first-person
    /// CharacterRenderer body stands in for it. 0 in the free view: the rig is not standing
    /// anywhere, so everyone — own character and any possessed bot alike — renders from the
    /// entity stream where their body actually is.
    /// </summary>
    internal ulong RenderSelfGuid => _freeView ? 0UL : ControlledGuid;

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
        // Read-only inspection is the fallback, NOT a competitor: while you are commanding a
        // toon its bars stay live and castable no matter what the marquee is highlighting,
        // because commanding a character from the sky has to give you the same character you
        // would have driving it directly. Selecting someone else picks who your right-click
        // ORDERS — it does not quietly demote the character you are playing to a spectator.
        // Inspection therefore only applies when nobody is commanded.
        _freeView && _controlState != ControlState.Possessing &&
        _freecamSelection.Count == 1 && _freecamSelection[0] != ControlledGuid
            ? _freecamSelection[0]
            : ControlledGuid;

    /// <summary>Free-view inspection shows another unit's bars; they must never act.</summary>
    private bool BarsReadOnly => BarsGuid != ControlledGuid;

    /// <summary>
    /// Divinity-style cutaway subject: the commanded toon's position (eye height
    /// added for the cell drop test) while the free view is up and the Settings
    /// checkbox is on; null otherwise. Fed to WmoRenderer.SetCutawaySubject once
    /// per frame — the single integration point for the whole feature.
    /// </summary>
    private Vector3? FreeViewCutawaySubject()
    {
        if (!_freeView || !Settings.Controls.FreeViewCutaway) return null;
        if (_controlState is not ControlState.Possessing || _controlTargetGuid == 0) return null;
        if (!_entities.TryGet(_controlTargetGuid, out WorldEntity bot)) return null;
        return bot.Position + new Vector3(0f, 0f, 1.5f);
    }

    /// <summary>
    /// Which renderer owns the CONTROLLED unit's skeleton. Normally the first-person
    /// CharacterRenderer; in the free view that body is not drawn at all — the driven unit
    /// streams in like any other player and CreatureRenderer owns it. Body animations
    /// (cast, channel, one-shots, wound reactions) have to follow, or they play on a body
    /// nobody is looking at and the commanded toon casts without moving a muscle.
    /// </summary>
    private bool ControlledBodyIsStreamed => _freeView;

    /// <summary>
    /// The store the action-bar/spellbook/talent UI reads. Deliberately keeps the historical
    /// field name: every existing read of the single-character store now follows possession
    /// (and, in the free view, the inspected selection).
    /// </summary>
    private PlayerActions _actions => ActionsFor(BarsGuid);

    /// <summary>Enter-world reset: drop every per-unit store (replaces `_actions.Clear()`).</summary>
    private void ResetActionStores() => _actionsByGuid.Clear();

    /// <summary>
    /// Session-loss reset. Possession and the free view both normally end on a server ACK;
    /// with the socket gone that ACK is never coming, so the client returns itself to its
    /// own character on the ground rather than stranding the fly rig with a parked stream.
    /// </summary>
    private void ResetSuiControl()
    {
        if (!_freeView && _controlState == ControlState.OwnChar) return;
        SetFreeView(false);
        _controlState = ControlState.OwnChar;
        _controlTargetGuid = 0;
        _controlSwitchQueued = 0;
        _controlledBodyPending = false;
        _freecamRequested = false;
        _freeViewExitRequested = false;
        _freecamSelection.Clear();
        ClearRtsWaypointChain();
        _suiRoster.Clear();
        PurgeSuiSnapshot();
        _movementSender.Parked = false;
        _walkToggled = false;
        if (_controller is not null) _controller.Flying = false;
        if (_character is not null) _character.Enabled = true;
        _window.FreeSelectMode = false;
    }

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
    private readonly List<Vector3> _rtsWaypointChain = [];   // Shift+RightClick chain dots
    private readonly List<ulong> _rtsWaypointSubjects = [];  // who the chain was issued to
    private double _rtsWaypointProgressAt;                   // last leg consumed / chain issued
    private const float RtsWaypointReachedYards = 3.5f;
    private const double RtsWaypointStaleSeconds = 45.0;
    private Vector3 _freecamCamSentPosition;                 // last CMSG_SUI_CAM position
    private double _freecamCamSentAt;                        // and when it went out
    private static readonly Vector3 RtsFriendlyTint = new(0.30f, 0.95f, 0.45f);
    private static readonly Vector3 RtsHostileTint = new(0.95f, 0.30f, 0.22f);
    private static readonly Vector3 RtsNeutralTint = new(0.95f, 0.85f, 0.25f);
    private bool _freecamKeyWasDown;
    private bool _freecamRequested;          // the in-flight release asked for the free view
    private bool _freeViewExitRequested;     // ...and this one asks to LEAVE it
    private double _freecamPanAt;            // last edge-pan tick, for its own dt
    private const float FreecamEdgePanMargin = 14f;   // px from a screen edge that starts a pan
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

        // A zero-guid request is the backwards-compatible feature probe used by
        // REAL_PORTALS. Old cores return an ordinary denial; new cores append a
        // capability trailer. Neither response is a possession attempt or a UI
        // error, so consume it before the control state machine below.
        if (ApplyRealPortalCapabilityAck(guid, r)) return;

        if (result == SuiAckOk)
        {
            // Whoever we were driving is about to start drawing from the entity stream instead
            // of from the controller — file where they actually stand first.
            SyncDrivenEntityToController();
            _controlTargetGuid = guid;
            _controlState = ControlState.Possessing;
            // No-op on the camera while the free view is up (see SeatControllerOnControlled).
            SeatControllerOnControlled(x, y, z, o);
            _net?.SetActiveMover(guid);
            EnterPlayerAuraWorld(guid);
            ApplyControlledCharacter();
            AddChatMessage(_freeView
                ? $"Commanding {ResolveUnitName(guid)} — bars and bags are live, camera stays up."
                : $"You take control of {ResolveUnitName(guid)}.");
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
            // Same hand-off as a possess: RenderSelfGuid goes 0, so the driven body stops being
            // drawn from the controller and starts being drawn from its (stale) streamed entity.
            SyncDrivenEntityToController();
            _freecamRequested = false;
            _controlTargetGuid = 0;
            SetFreeView(true);
            _controlState = ControlState.FreeCam;
            _movementSender.Parked = false;       // the Flying branch parks it from here
            if (_controller is not null) _controller.Flying = true;
            EnterPlayerAuraWorld(LocalPlayerGuid);
            PurgeSuiSnapshot();
            AddChatMessage("Free view: drag-select the party, click a toon to command it, " +
                "RightClick to move/attack, Shift+RightClick chains waypoints, Ctrl+F returns.");
            return;
        }

        if (result >= SuiAckFirstRelease)
        {
            bool wasPossessing = _controlState is ControlState.Possessing or ControlState.ReleasePending;
            // The bot we were driving reverts to a streamed body on this line; file where we
            // actually walked it to, or it snaps back to where we picked it up.
            SyncDrivenEntityToController();
            _controlTargetGuid = 0;

            // A SOLICITED release (16/17) inside the free view is a control change only —
            // clicking your own toon, or the release half of a bot-to-bot switch. The camera
            // stays in the sky. Only the FORCED codes (18+: death, teleport, group change)
            // mean the server has put you back in your body, which ends the free view.
            //
            // ...UNLESS the release WAS the Ctrl+F that asked to leave. Both arrive as 16, so
            // the reason code cannot tell them apart — only the client knows which it sent,
            // and without that flag Ctrl+F answered its own exit by staying put.
            bool staysInFreeView = _freeView && !_freeViewExitRequested &&
                result <= SuiAckReleasedFreecam;
            if (!staysInFreeView) SetFreeView(false);
            _freeViewExitRequested = false;
            _controlState = staysInFreeView ? ControlState.FreeCam : ControlState.OwnChar;
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
                    _ when staysInFreeView => "Commanding no one — the party runs itself.",
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
        // The snapshot is the ONLY way a possessed bot's bags/gold/talents can exist client
        // side (the wire never streams owner-only fields to a non-owner), so a silent drop
        // here is indistinguishable from "the bot has nothing". Say which it was.
        if (source != ControlledGuid || !_entities.TryGet(source, out WorldEntity bot))
        {
            Console.WriteLine($"[sui] snapshot DROPPED for 0x{source:X} " +
                $"(controlled=0x{ControlledGuid:X}, resident={_entities.TryGet(source, out _)}), " +
                $"{count} items, {coinage} copper");
            return;
        }
        Console.WriteLine($"[sui] snapshot for {ResolveUnitName(source)}: {count} items, " +
            $"{coinage / 10000}g{coinage % 10000 / 100}s{coinage % 100}c, {talentPoints} talent pts");

        PurgeSuiSnapshot();
        bot.Fields.SetU32(ObjectFields.PLAYER_CHARACTER_POINTS1, talentPoints);
        bot.Fields.SetU32(ObjectFields.PLAYER_COINAGE, coinage);
        int containersSized = 0;
        bool statsApplied = false;

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
            // The bag UI sizes windows from CONTAINER_NUM_SLOTS and enumerates
            // contents with Math.Min(numSlots, 36) — a synthetic container
            // without the field reads 0 slots, so the items filed into its
            // CONTAINER_SLOT fields were never even looked at.
            if (bagSlots > 0)
            {
                fields.SetU32(ObjectFields.CONTAINER_NUM_SLOTS, bagSlots);
                containersSized++;
            }
            _entities.AddSynthetic(new WorldEntity
            {
                Guid = itemGuid,
                Type = bagSlots > 0 ? ObjectTypeId.Container : ObjectTypeId.Item,
                // Entry is a plain field the UPDATE_OBJECT parser fills for streamed
                // entities — nothing derives it from OBJECT_ENTRY in Fields. Left at 0,
                // every template consumer (names, icons, tooltips, bag portraits) reads
                // "no item" while the field-driven stack counts render fine, and
                // Require(0, ...) is a silent no-op so nothing ever logged a failure.
                Entry = entry,
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

        // ── Snapshot v2: the paper-doll stat block (optional trailing bytes). ──
        // UNIT_FIELD stats/resists/AP/damage are owner-only on the vanilla wire —
        // never streamed for another player — so a possessed bot's character
        // sheet rendered all zeros until the snapshot carried the raw values.
        // Injected verbatim into the same fields the sheet already reads.
        if (r.Remaining >= 19 * 4 + 6 * 4)
        {
            for (int i = 0; i < 5; i++)
                bot.Fields.SetU32((ushort)(ObjectFields.UNIT_STAT0 + i), r.ReadU32());
            for (int i = 0; i < 7; i++)
                bot.Fields.SetU32((ushort)(ObjectFields.UNIT_RESISTANCES + i), r.ReadU32());
            bot.Fields.SetU32(ObjectFields.UNIT_ATTACK_POWER, r.ReadU32());
            bot.Fields.SetU32(ObjectFields.UNIT_ATTACK_POWER_MODS, r.ReadU32());
            bot.Fields.SetU32(ObjectFields.UNIT_RANGED_ATTACK_POWER, r.ReadU32());
            bot.Fields.SetU32(ObjectFields.UNIT_RANGED_ATTACK_POWER_MODS, r.ReadU32());
            bot.Fields.SetU32(ObjectFields.UNIT_BASEATTACKTIME, r.ReadU32());
            bot.Fields.SetU32((ushort)(ObjectFields.UNIT_BASEATTACKTIME + 1), r.ReadU32());
            bot.Fields.SetU32(ObjectFields.UNIT_RANGEDATTACKTIME, r.ReadU32());
            bot.Fields.SetU32(ObjectFields.UNIT_MINDAMAGE, BitConverter.SingleToUInt32Bits(r.ReadF32()));
            bot.Fields.SetU32(ObjectFields.UNIT_MAXDAMAGE, BitConverter.SingleToUInt32Bits(r.ReadF32()));
            bot.Fields.SetU32(ObjectFields.UNIT_MINOFFHANDDAMAGE, BitConverter.SingleToUInt32Bits(r.ReadF32()));
            bot.Fields.SetU32(ObjectFields.UNIT_MAXOFFHANDDAMAGE, BitConverter.SingleToUInt32Bits(r.ReadF32()));
            bot.Fields.SetU32(ObjectFields.UNIT_MINRANGEDDAMAGE, BitConverter.SingleToUInt32Bits(r.ReadF32()));
            bot.Fields.SetU32(ObjectFields.UNIT_MAXRANGEDDAMAGE, BitConverter.SingleToUInt32Bits(r.ReadF32()));
            statsApplied = true;
        }

        // Version marker: this line existing at all proves the round-13 client is
        // running; its numbers say whether the wire carried the v2 payloads.
        Console.WriteLine($"[sui] snapshot v2: stats={(statsApplied ? "applied" : "ABSENT")}, " +
            $"containers sized={containersSized}");

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
        SetFreeView(false);
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

    /// <summary>
    /// Publish the controller's position into the entity of whichever unit it is DRIVING.
    ///
    /// Movement is client-authoritative for the unit you drive: the controller is the
    /// continuously updated truth, and that unit's object-store entity is only the last SERVER
    /// snapshot — the server does not echo your own mover's movement back to you, so it stays
    /// frozen at wherever the unit stood when you took control. It never shows while you are
    /// driving, because RenderSelfGuid excludes that unit from the streamed pass and the
    /// controller draws its body. The moment control hands off, it starts drawing from the
    /// stale entity and snaps back to the spot you picked it up at, then jumps forward again
    /// as soon as its AI emits a real move.
    ///
    /// Applies to ANY driven unit, not just the own character — releasing a bot you walked
    /// across the zone strands it the same way. Called at every hand-off, BEFORE the controller
    /// is re-seated onto someone else.
    /// </summary>
    private void SyncDrivenEntityToController()
    {
        // In the free view the controller is a CAMERA and drives nobody. Writing its position
        // into a character would file that character in the sky — worse than the staleness.
        if (_freeView || _controller is null) return;
        // ONLY while the movement stream is actually live. Parked or flying means the server is
        // hearing nothing from us, so ITS position is the truth and ours is local fiction —
        // publishing anyway would paint the fiction over the fact and hide a desync behind a
        // selection ring that follows you perfectly while the character has not moved at all.
        if (_movementSender.Parked || _controller.Flying) return;
        if (!_entities.TryGet(ControlledGuid, out WorldEntity driven)) return;
        driven.Position = _controller.Position;
        driven.Orientation = _controller.Yaw;
    }

    /// <summary>
    /// Snap the local, client-authoritative controller onto the ACK position.
    ///
    /// NO-OP ON THE CAMERA IN THE FREE VIEW, and that is the whole mechanism behind
    /// "possessing from the sky does not land you": every possess/release path funnels
    /// through here, so guarding it once keeps the fly rig, the parked movement stream
    /// and the hidden first-person body intact no matter which of them fired. The
    /// portrait bakes still have to be invalidated — the HUD identity did change.
    /// </summary>
    private void SeatControllerOnControlled(float x, float y, float z, float o)
    {
        if (_freeView)
        {
            _freecamRequested = false;
            // The server tears the streaming eye down on possess and rebuilds it on the next
            // CMSG_SUI_CAM. Force that heartbeat out on the very next frame rather than
            // waiting up to 2 s for the idle cadence, so the gap where nothing streams the
            // world in around the camera closes immediately.
            _freecamCamSentAt = 0;
            _playerPortraitDirty = true;
            _paperDollDirty = true;
            return;
        }
        _movementSender.Parked = false;
        _freecamRequested = false;
        // A control hand-off always seats you RUNNING. The walk toggle is easy
        // to flip unnoticed around control chords (Slash sits next to the
        // Shift/Ctrl cluster in use during control jumping), and a toggle left
        // on sticks invisibly to the next body.
        _walkToggled = false;
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
        // Mode 1 means "I am NOT taking my body back". Releasing while the free view is up is
        // exactly that, so it must go out as 1 even though the caller only asked to release:
        // SuiPossess::DoRelease answers mode 0 by running DetachUnattendedAI on the own
        // character and RemoveFreecamEye on the session. That is what left the abandoned
        // character with no AI to obey RTS orders — the client said "move to", the server had
        // nobody listening — and killed the streaming eye out from under the camera.
        _net.SuiControlRelease(toFreecam || _freeView ? (byte)1 : (byte)0);
    }

    /// <summary>
    /// Ctrl+F: raise/lower the CRPG free view. From the own character or a possessed bot
    /// the server keeps/attaches the unattended AI (release mode 1); from the free view a
    /// plain release (mode 0) returns to manual control of the own character.
    /// </summary>
    private void ToggleFreeView()
    {
        if (_net is not { IsInWorld: true } || _controller is null) return;

        // Already commanding a toon from the sky: Ctrl+F is purely a camera decision, so it
        // just lands on the unit whose bars are already live. No release, no server round
        // trip — you keep driving what you were already driving.
        if (_freeView && _controlState == ControlState.Possessing)
        {
            SetFreeView(false);
            if (_entities.TryGet(_controlTargetGuid, out WorldEntity bot))
                SeatControllerOnControlled(bot.Position.X, bot.Position.Y, bot.Position.Z,
                    bot.Orientation);
            else
            {
                if (_controller is not null) _controller.Flying = false;
                if (_character is not null) _character.Enabled = true;
                _movementSender.Parked = false;
            }
            ApplyControlledCharacter();
            AddChatMessage($"You take control of {ResolveUnitName(_controlTargetGuid)}.");
            return;
        }

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
                _freeViewExitRequested = true;
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

        // Ctrl+N: the NPC dev window (spawn/pathing/aggro overlays). Same edge
        // pattern; no in-world gate so it also opens in creator mode.
        UpdateDevWindowInput(typing);
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
        _window.FreeSelectMode = _freeView;
        if (!_freeView)
        {
            _window.TakeFreeFlightScroll();   // discard a leftover final-frame wheel tick
            _freecamDragOrigin = null;
            _freecamDragActive = false;
            _freecamMarqueeConsumedClick = false;
            _freecamSelection.Clear();
            ClearRtsWaypointChain();
            _commanderMapOpen = false;      // the commander map is a free-view surface only
            _freecamPanAt = 0;
            if (_controller is not null)
            {
                _controller.FlyFloorClearance = null;
                _controller.FlyCollide = false;
            }
            return;
        }

        // Re-assert the rig every frame rather than only on entry: a possess from the sky
        // runs ApplyControlledCharacter, which puts the controller back on the ground.
        // _character.Enabled is deliberately NOT touched — the world pass skips the body on
        // _freeView instead, so the portrait booth (same Render method) keeps working.
        if (_controller is not null)
        {
            _controller.Flying = true;
            // The RTS camera never sinks beneath the map; plain F fly stays unclamped.
            _controller.FlyFloorClearance = 2f;
            // The round-15 floating-body camera (walls/ceilings stop the rig) is OFF:
            // detaching under any WMO geometry — indoors, gate arches, city overhangs —
            // hit an invisible "fake ceiling" (owner, 2026-08-11 evening). The rig is a
            // ghost again. Hard false, not the saved setting: the old default persisted
            // `true` into settings files, and honoring it would keep the ceiling.
            _controller.FlyCollide = false;
        }

        // RTS altitude on the wheel: fly the RIG along the camera look direction
        // (wheel up = toward what you look at, wheel down = pull back and away).
        // Unlimited height — the orbit boom's MaxDistance no longer caps the view.
        // Steps scale with altitude like the edge pan, and go through FlyMove so
        // the wheel cannot ghost through what the keys cannot.
        // While the commander map is up the mouse belongs to the map: no rig
        // wheel-fly, no edge pan, no marquee. The heartbeat below keeps running —
        // the map's click-to-fly depends on it (TakeFreeFlightScroll still runs
        // so a wheel tick over the map is consumed, not banked for landing).
        float wheel = _window.TakeFreeFlightScroll();
        if (wheel != 0f && !_commanderMapOpen && _controller is not null)
        {
            float altitude = 10f;
            if (_terrain?.SampleHeight(_controller.Position.X, _controller.Position.Y) is float floor)
                altitude = MathF.Max(2f, _controller.Position.Z - floor);
            float step = Math.Clamp(altitude * 0.30f, 2.5f, 40f);
            _controller.FlyMove(_window.Camera.Forward * (wheel * step));
        }

        if (!_commanderMapOpen) UpdateFreeCamEdgePan();
        UpdateRtsWaypointProgress();

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

        if (leftDown && _freecamDragOrigin is null && !_commanderMapOpen &&
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

    /// <summary>
    /// Retire chain legs as the party walks them. The route is drawn from
    /// <see cref="_rtsWaypointChain"/>, and the server consumes its own copy on arrival, so
    /// without this the dots and the dashed line stayed on screen over a party standing at
    /// the end of the route it had already finished. The client cannot see the server's
    /// arrival callback, so it watches the same thing the eye does: the leading leg is spent
    /// once any ordered subject stands on it.
    /// </summary>
    private void UpdateRtsWaypointProgress()
    {
        if (_rtsWaypointChain.Count == 0) return;

        while (_rtsWaypointChain.Count > 0)
        {
            Vector3 leg = _rtsWaypointChain[0];
            bool reached = false;
            foreach (ulong guid in _rtsWaypointSubjects.Count > 0
                         ? _rtsWaypointSubjects : FreeCamSelectableGuids())
            {
                if (!_entities.TryGet(guid, out WorldEntity unit)) continue;
                // Horizontal only: a member on the abbey steps is at the waypoint even
                // though its Z is a storey off.
                float dx = unit.Position.X - leg.X, dy = unit.Position.Y - leg.Y;
                if (dx * dx + dy * dy <= RtsWaypointReachedYards * RtsWaypointReachedYards)
                { reached = true; break; }
            }
            if (!reached) break;
            _rtsWaypointChain.RemoveAt(0);
            _rtsWaypointProgressAt = NowSeconds();
        }

        // Nobody is coming (stuck path, dead subject, order overridden server-side). Drop the
        // route rather than leaving a permanent decoration on the ground.
        if (_rtsWaypointChain.Count > 0 &&
            NowSeconds() - _rtsWaypointProgressAt > RtsWaypointStaleSeconds)
            ClearRtsWaypointChain();
    }

    private void ClearRtsWaypointChain()
    {
        _rtsWaypointChain.Clear();
        _rtsWaypointSubjects.Clear();
        _rtsWaypointProgressAt = 0;
    }

    /// <summary>
    /// RTS edge scroll: the pointer within a few pixels of a screen edge slides the fly rig
    /// that way, camera-relative, so the free view is drivable without touching the keyboard.
    ///
    /// Speed scales with altitude above the ground the way every RTS does it — a map-level
    /// camera has to cover map-level distances, and the same yards/second that reads as a
    /// nudge at 5 yards up reads as a crawl at 60. Suppressed while a marquee drag is live
    /// (the rectangle is anchored in SCREEN space, so panning under it selects a lie) and
    /// whenever ImGui owns the pointer, which keeps the bottom edge usable as a UI strip.
    /// </summary>
    private void UpdateFreeCamEdgePan()
    {
        if (_controller is null) return;

        double now = NowSeconds();
        double previous = _freecamPanAt;
        _freecamPanAt = now;
        if (previous <= 0) return;                       // first frame in the view: no dt yet
        float dt = (float)Math.Clamp(now - previous, 0.0, 0.1);
        if (dt <= 0f) return;

        if (_freecamDragActive || _window.MouseCaptured || ImGui.GetIO().WantCaptureMouse) return;

        Vector2 display = ImGui.GetIO().DisplaySize;
        Vector2 mouse = _window.MousePosition;
        if (display.X < 1f || display.Y < 1f) return;
        // Pointer outside the window (alt-tabbed, or dragged onto a second monitor): no pan.
        if (mouse.X < 0f || mouse.Y < 0f || mouse.X > display.X || mouse.Y > display.Y) return;

        float x = (mouse.X <= FreecamEdgePanMargin ? -1f : 0f) +
                  (mouse.X >= display.X - FreecamEdgePanMargin ? 1f : 0f);
        float y = (mouse.Y <= FreecamEdgePanMargin ? 1f : 0f) +
                  (mouse.Y >= display.Y - FreecamEdgePanMargin ? -1f : 0f);
        if (x == 0f && y == 0f) return;

        float yaw = _window.Camera.Yaw;
        // Same basis the controller uses, so a pan and a WASD fly agree on which way is which.
        var forward = new Vector3(MathF.Cos(yaw), MathF.Sin(yaw), 0f);
        var right = new Vector3(MathF.Sin(yaw), -MathF.Cos(yaw), 0f);

        // Off-map or unloaded terrain returns null; fall back to a mid-altitude rate rather
        // than crawling because the height sample happened to miss.
        float altitude = 10f;
        if (_terrain?.SampleHeight(_controller.Position.X, _controller.Position.Y) is float ground)
            altitude = MathF.Max(2f, _controller.Position.Z - ground);
        float speed = _config.Movement.FlySpeed * Math.Clamp(altitude / 12f, 0.45f, 3.0f);

        Vector3 pan = forward * y + right * x;
        if (pan.LengthSquared() > 1e-6f)
            // Through the same wall test as WASD flight — a direct Position write
            // here would let the edge pan ghost through what the keys cannot.
            _controller.FlyMove(Vector3.Normalize(pan) * speed * dt);
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
            AddChatMessage($"Selected {_freecamSelection.Count}: RightClick the ground to move, " +
                "a hostile to attack, Shift+RightClick to chain waypoints.");
    }

    /// <summary>
    /// Free-view world click (routed from the targeting click queue). Left selects (a
    /// party member joins the highlighted set; empty ground clears it). RightClick
    /// orders the HIGHLIGHTED set: hostile under cursor → attack, ground → move.
    /// Shift+RightClick keeps ordering the whole party regardless of the selection.
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
            // NPC dev window focus set: Ctrl+LeftClick multi-selects creatures for the
            // "Selected only" overlay scope and consumes the click (ahead of the
            // take-command and marquee-clear behaviour below).
            if (HandleDevFocusClick(pickedUnit)) return;
            // CRPG rule: clicking a party toon in the free view IS taking command of it —
            // its bars, bags and spells become the live HUD, the same as Ctrl+Tab. The
            // CAMERA does not move: you stay in the sky until Ctrl+F says otherwise.
            if (pickedUnit != 0)
                foreach (ulong guid in FreeCamSelectableGuids())
                    if (guid == pickedUnit)
                    {
                        SwitchControlTo(guid);
                        // Ringed as well as commanded: one click gives you the halo, the bars
                        // and the order target, which is the whole point of the free view.
                        _freecamSelection.Clear();
                        _freecamSelection.Add(guid);
                        EnsureBotBarForViewing(guid);
                        return;
                    }
            CommitSelection(pickedUnit, beginAttack: false);
            _freecamSelection.Clear();
            return;
        }
        if (click.Button != MouseButton.Right) return;

        // SHIFT, not Ctrl, queues waypoints: Ctrl is the control-chord modifier (Ctrl+F,
        // Ctrl+Tab), so entering the free view with Ctrl still down turned the very first
        // right-click into a chained waypoint instead of a move. Shift is also what every
        // RTS uses for queue-this-order, so the collision fix is the conventional binding.
        bool queue = ShiftHeld();
        // The commanded toon is ordered like any other member — no filtering. In the free view
        // a possessed bot IS orderable: SuiPossess::orderBot waives its IsPossessed() bail when
        // the possessor holds a freecam eye, because the conflict that bail guards (a server
        // MOVE_TO fighting the client's movement stream) cannot arise when the client's
        // controller is a detached camera and its stream is parked. Commanding a character
        // from the sky gives you the same character you would have driving it directly.
        List<ulong> subjects = [.. _freecamSelection];

        ulong picked = PickUnit(click.Position);
        if (picked != 0 && _entities.TryGet(picked, out WorldEntity target) &&
            !target.IsDead && CanAttack(target))
        {
            if (subjects.Count == 0 && !queue) return;   // nothing highlighted, no accidental orders
            _net?.SuiOrder(1, subjects, picked, 0, 0, 0);
            _rtsMoveMarkers.Add((target.Position, NowSeconds(), RtsHostileTint));
            ClearRtsWaypointChain();
            AddChatMessage($"{OrderSubjectLabel(subjects)}: attack {ResolveWorldUnitName(picked)}!");
        }
        else if (TryPickGround(click.Position, out System.Numerics.Vector3 point))
        {
            if (queue)
            {
                // Shift+RightClick chains a waypoint (whole party when nothing is highlighted).
                _net?.SuiOrder(3, subjects, 0, point.X, point.Y, point.Z);
                // Re-anchor the chain's ownership on every leg: the highlighted set can change
                // between clicks, and the route belongs to whoever was last told to walk it.
                _rtsWaypointSubjects.Clear();
                _rtsWaypointSubjects.AddRange(subjects);
                _rtsWaypointProgressAt = NowSeconds();
                _rtsWaypointChain.Add(point);
                _rtsMoveMarkers.Add((point, NowSeconds(), RtsNeutralTint));
                AddChatMessage($"{OrderSubjectLabel(subjects)}: waypoint {_rtsWaypointChain.Count} " +
                    $"({point.X:F0}, {point.Y:F0}).");
            }
            else
            {
                if (subjects.Count == 0) return;   // plain move needs a highlighted set
                BeginRtsMovePresentation(subjects, point);
                _net?.SuiOrder(0, subjects, 0, point.X, point.Y, point.Z);
                _rtsMoveMarkers.Add((point, NowSeconds(), RtsFriendlyTint));
                ClearRtsWaypointChain();
                AddChatMessage($"{OrderSubjectLabel(subjects)}: move to ({point.X:F0}, {point.Y:F0}).");
            }
        }
    }

    /// <summary>
    /// Apply the immediate, presentation-only half of a direct movement order. The server still
    /// owns pathfinding and every position update; locally we only cancel stale action poses,
    /// restart locomotion phase, and face each subject toward the clicked destination. Queued
    /// waypoints deliberately do not call this: a future leg must not turn a body off its current
    /// path before the server starts that leg.
    /// </summary>
    private void BeginRtsMovePresentation(IReadOnlyList<ulong> subjects, Vector3 destination)
    {
        IEnumerable<ulong> ordered = subjects.Count == 0 ? FreeCamSelectableGuids() : subjects;
        var seen = new HashSet<ulong>();
        foreach (ulong guid in ordered)
        {
            if (!seen.Add(guid)) continue;
            _entities.PredictServerMoveFacing(guid, destination);
            _creatures?.InterruptActionForMovement(guid);
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
        if (guid == ControlledGuid) return;
        switch (_controlState)
        {
            case ControlState.OwnChar when guid != LocalPlayerGuid:
            case ControlState.FreeCam when guid != LocalPlayerGuid:
                RequestPossess(guid);
                break;
            case ControlState.FreeCam:
                // Clicked the own character with nobody possessed. In the free view that is
                // already the state; landing on it is Ctrl+F's job, not a click's.
                break;
            case ControlState.Possessing when guid == LocalPlayerGuid:
                // Hand the bot back. From the sky this drops to commanding nobody and the
                // camera stays up; on the ground it returns you to your own body as before.
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
        if (!_freeView) return;
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
        if (!_freeView ||
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
        _spellEffectMeshes.GatherGround ??= GatherGroundEffectTriangles;
        double now = NowSeconds();
        _rtsMoveMarkers.RemoveAll(m => now - m.Born > 0.9);

        if (_freeView)
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
        // The free view is a camera mode, so it prefixes rather than replaces: you can be
        // in the sky AND commanding a toon, and the banner has to say both.
        string text;
        if (_freeView)
        {
            string who = _controlState == ControlState.Possessing
                ? $"commanding {ResolveUnitName(_controlTargetGuid)}"
                : BarsReadOnly
                    ? $"{ResolveUnitName(BarsGuid)}'s bars (read-only)"
                    : "drag: select · click a toon to command it";
            text = $"Free view — {who} · RightClick: move/attack · " +
                "Shift+RightClick: chain waypoints · Ctrl+F: land";
        }
        else text = _controlState switch
        {
            ControlState.Possessing =>
                $"Controlling {ResolveUnitName(_controlTargetGuid)} — Ctrl+Tab to switch, Ctrl+F free view",
            ControlState.PossessPending => "Taking control…",
            ControlState.ReleasePending => "Releasing control…",
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
