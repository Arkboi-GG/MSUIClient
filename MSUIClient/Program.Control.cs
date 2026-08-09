using System.Collections.Generic;
using System.Numerics;
using ImGuiNET;
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
    private ulong _controlSwitchQueued;      // cycle target waiting for the in-flight release ACK
    private bool _controlCycleWasDown;
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
            AddChatMessage($"You take control of {ResolveUnitName(guid)}.");
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

    /// <summary>Snap the local, client-authoritative controller onto the ACK position.</summary>
    private void SeatControllerOnControlled(float x, float y, float z, float o)
    {
        _movementSender.Parked = false;
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
        _controlPendingSince = NowSeconds();
        _net.SuiControlRelease(toFreecam ? (byte)1 : (byte)0);
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
            _controlState = _controlState == ControlState.PossessPending
                ? ControlState.OwnChar : ControlState.Possessing;
            _movementSender.Parked = false;
            _controlSwitchQueued = 0;
            ShowUiError("No answer from the server (SUI control).");
        }

        bool ctrl = InputKeyDown(Key.ControlLeft) || InputKeyDown(Key.ControlRight);
        bool tab = InputKeyDown(Key.Tab);
        bool cyclePressed = ctrl && tab;
        if (cyclePressed && !_controlCycleWasDown && !typing && _net is { IsInWorld: true })
            CycleControl(ShiftHeld() ? -1 : +1);
        _controlCycleWasDown = cyclePressed;
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
                $"Controlling {ResolveUnitName(_controlTargetGuid)} — Ctrl+Tab to switch",
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
    }
}
