using MSUIClient.Net;

namespace MSUIClient;

/// <summary>
/// Companions state (COMPANIONS v1). A player summons their OWN other characters
/// (alts on the same account) into the world as AI-driven party members that the
/// Command View / CRPG mode can possess and order, and dismisses them again.
///
/// The server owns every decision: the client only sends CMSG_SUI_COMPANION acts
/// (list / summon / dismiss) once capability bit 7 has been observed on the control
/// ACK, and mirrors what SMSG_SUI_COMPANION says back — a kind-1 verdict for each act
/// and a kind-2 list that the server re-pushes after every result, whenever a
/// companion finishes entering the world or leaves, and in answer to the list act.
/// Summoned companions also arrive on SMSG_SUI_CONTROL_ROSTER with the 0x08 "your
/// companion" flag (plus 0x01 controllable), so possession and orders work unchanged.
///
/// The window lives in GameLoop/Panels/GameLoop.Companions.cs.
/// </summary>
public sealed partial class GameLoop
{
    /// <summary>Capability bit 7 observed on the SMSG_SUI_CONTROL_ACK trailer.</summary>
    private bool _companionsAvailable;

    /// <summary>The latest kind-2 list, replaced atomically so the window never flickers.</summary>
    private CompanionRow[] _companionRows = [];

    /// <summary>True once any kind-2 list has arrived this session.</summary>
    private bool _companionsEverListed;

    /// <summary>Last verdict text for the window's status line ("" = nothing yet).</summary>
    private string _companionsStatus = "";
    private bool _companionsStatusIsError;

    /// <summary>
    /// The character with an in-flight summon/dismiss; its button stays disabled until
    /// the verdict (or the next list) lands, so a double-click cannot send twice.
    /// </summary>
    private ulong _companionsPendingGuid;
    private double _companionsPendingSince;
    private const double CompanionsPendingTimeoutSeconds = 5.0;

    /// <summary>Called by the shared control-ACK capability parser.</summary>
    private void ApplyCompanionsCapability(uint capabilities)
    {
        bool available = (capabilities & SuiCapabilityWire.CompanionsV1) != 0;
        if (available != _companionsAvailable)
            Console.WriteLine(available
                ? "[companions] server advertised companions-v1"
                : "[companions] server has no companions-v1 advertisement");
        _companionsAvailable = available;
    }

    /// <summary>Action 3: ask for a fresh kind-2 list.</summary>
    private bool RequestCompanionList() => SendCompanionAction(CompanionWire.ActionList, 0);

    /// <summary>Action 1: summon one of the account's characters as a companion.</summary>
    private bool RequestCompanionSummon(ulong guid)
    {
        if (guid == 0) return false;
        if (!SendCompanionAction(CompanionWire.ActionSummon, guid)) return false;
        _companionsPendingGuid = guid;
        _companionsPendingSince = NowSeconds();
        return true;
    }

    /// <summary>Action 2: dismiss one of the summoned companions.</summary>
    private bool RequestCompanionDismiss(ulong guid)
    {
        if (guid == 0) return false;
        if (!SendCompanionAction(CompanionWire.ActionDismiss, guid)) return false;
        _companionsPendingGuid = guid;
        _companionsPendingSince = NowSeconds();
        return true;
    }

    /// <summary>
    /// The one send site. Never emits the opcode before the capability bit has been
    /// observed: older cores close the socket on an opcode beyond their table.
    /// </summary>
    private bool SendCompanionAction(byte action, ulong guid)
    {
        if (_net is not { IsInWorld: true }) return false;
        if (!_companionsAvailable)
        {
            EmitInterface("companions", "send", "NO_CAPABILITY", guid, $"action={action}");
            return false;
        }
        // LIST is read-only and remains useful while frozen. Summon/dismiss mutate the live
        // party and therefore follow the same lock gate as orders, possession and follow state.
        if (action != CompanionWire.ActionList && TacticalFreezeBlocksLiveCommands)
        {
            RefuseTacticalFreezeLiveCommand("changing the companion roster");
            EmitInterface("companions", "send", "TACTICAL_FREEZE", guid, $"action={action}");
            return false;
        }
        if (action != CompanionWire.ActionList &&
            RefuseTacticalFrozenActor(guid, "change its companion state"))
            return false;
        bool sent = _net.SuiCompanion(action, guid);
        EmitInterface("companions", "send", sent ? "SENT" : "REFUSED", guid, $"action={action}");
        return sent;
    }

    /// <summary>True while a summon/dismiss for this character is awaiting its verdict.</summary>
    private bool IsCompanionActionPending(ulong guid)
    {
        if (_companionsPendingGuid == 0 || _companionsPendingGuid != guid) return false;
        if (NowSeconds() - _companionsPendingSince > CompanionsPendingTimeoutSeconds)
        {
            _companionsPendingGuid = 0;   // the server never answered; let the button live again
            return false;
        }
        return true;
    }

    /// <summary>SMSG_SUI_COMPANION: a kind-1 verdict or a kind-2 list.</summary>
    private void ApplySuiCompanion(byte[] body)
    {
        if (!CompanionWire.TryReadKind(body, out byte kind))
        {
            EmitInterface("companions", "reply", "MALFORMED", 0, "bytes=0");
            return;
        }
        switch (kind)
        {
            case CompanionWire.KindResult:
                ApplyCompanionResult(body);
                break;
            case CompanionWire.KindList:
                ApplyCompanionList(body);
                break;
            default:
                EmitInterface("companions", "reply", "UNKNOWN_KIND", 0, $"kind={kind};bytes={body.Length}");
                break;
        }
    }

    private void ApplyCompanionResult(byte[] body)
    {
        if (!CompanionWire.TryParseResult(body, out CompanionResult result))
        {
            EmitInterface("companions", "result", "MALFORMED", 0, $"bytes={body.Length}");
            return;
        }
        if (_companionsPendingGuid == result.Guid || result.Action == CompanionWire.ActionList)
            _companionsPendingGuid = 0;

        bool ok = CompanionWire.IsSuccess(result.Result);
        string text = CompanionWire.DescribeResult(result.Action, result.Result,
            CompanionName(result.Guid));
        // The list act's own OK is silent: the arriving list is the answer.
        bool silent = ok && result.Action == CompanionWire.ActionList;
        if (!silent)
        {
            _companionsStatus = text;
            _companionsStatusIsError = !ok;
            if (ok) ShowUiInfo(text);
            else ShowUiError(text);
        }
        EmitInterface("companions", "result", ok ? "OK" : "FAIL", result.Guid,
            $"action={result.Action};code={result.Result}");
    }

    private void ApplyCompanionList(byte[] body)
    {
        if (!CompanionWire.TryParseList(body, out CompanionRow[] rows))
        {
            EmitInterface("companions", "list", "MALFORMED", 0, $"bytes={body.Length}");
            return;
        }
        // Stable presentation order regardless of how the server enumerated: the
        // character being played first, then by name. Replaced in one assignment so
        // the open window redraws from a complete list, never an empty one.
        Array.Sort(rows, (a, b) =>
        {
            int playing = b.IsPlaying.CompareTo(a.IsPlaying);
            return playing != 0 ? playing
                : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });
        _companionRows = rows;
        _companionsEverListed = true;
        // A pending act whose subject is no longer in flux is settled by this list.
        if (_companionsPendingGuid != 0)
        {
            foreach (CompanionRow row in rows)
                if (row.Guid == _companionsPendingGuid && !row.IsLoading)
                { _companionsPendingGuid = 0; break; }
        }
        Console.WriteLine($"[companions] {rows.Length} character(s), " +
            $"{rows.Count(r => r.IsCompanion)} summoned");
        EmitInterface("companions", "list", "APPLIED", 0,
            $"rows={rows.Length};summoned={rows.Count(r => r.IsCompanion)}");
    }

    /// <summary>Name for a result line: the list row, else the world's name table.</summary>
    private string CompanionName(ulong guid)
    {
        foreach (CompanionRow row in _companionRows)
            if (row.Guid == guid) return row.Name;
        return _playerNames.TryGetValue(guid, out string? name) ? name : "";
    }

    /// <summary>Clear companion state on world-leave / character swap.</summary>
    private void ResetCompanions()
    {
        _companionsOpen = false;
        _companionsAvailable = false;
        _companionRows = [];
        _companionsEverListed = false;
        _companionsStatus = "";
        _companionsStatusIsError = false;
        _companionsPendingGuid = 0;
        _companionsScroll = 0;
    }
}
