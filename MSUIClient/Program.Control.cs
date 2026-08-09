using System.Collections.Generic;
using MSUIClient.Net;

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
}
