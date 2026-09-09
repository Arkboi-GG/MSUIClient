using MSUIClient.Net;

namespace MSUIClient;

/// <summary>
/// Dungeon/raid lockouts + timers (spec P1). The client half of the vanilla Raid
/// Info window: which instances this character is saved to and the live reset
/// countdown per lock, plus the "you are now saved" moment and the periodic reset
/// warning. All state is server-pushed — a bound-instance list is not derivable
/// client-side, which is why this is a wire and not a local computation.
///
/// The list is refreshed by an explicit pull (CMSG_REQUEST_RAID_INFO) when the
/// panel opens and after a reset; the save/warning/reset packets arrive unsolicited
/// and keep it honest between pulls.
/// </summary>
public sealed partial class GameLoop
{
    /// <summary>Every instance this character is currently bound to. Each row is
    /// stamped with its receive time so the panel counts down without another pull.</summary>
    private RaidLockout[] _raidLockouts = [];
    private readonly InstanceOwnershipState _instanceOwnership = new();

    /// <summary>NowSeconds() of the last SMSG_RAID_INSTANCE_INFO; 0 = never told.</summary>
    private double _raidInfoReceivedAt;

    /// <summary>NowSeconds() of the last pull, to rate-limit a panel that polls while open.</summary>
    private double _raidInfoRequestedAt;

    /// <summary>Minimum spacing between raid-info pulls (a panel redraw must not flood the socket).</summary>
    private const double RaidInfoPullCooldown = 3.0;

    private IReadOnlyList<RaidLockout> RaidLockouts => _raidLockouts;
    private bool RaidInfoEverReceived => _raidInfoReceivedAt > 0;

    /// <summary>
    /// Ask the server for the lockout list. Rate-limited unless forced (a reset
    /// outcome forces one so the panel reflects the change immediately).
    /// </summary>
    private bool RequestRaidLockouts(string reason, bool force = false)
    {
        if (_net is not { IsInWorld: true }) return false;
        double now = NowSeconds();
        if (!force && now - _raidInfoRequestedAt < RaidInfoPullCooldown) return false;
        if (!_net.RequestRaidInfo()) return false;
        _raidInfoRequestedAt = now;
        EmitInterface("raid-info", "pull", "SENT", LocalPlayerGuid, reason);
        return true;
    }

    /// <summary>SMSG_RAID_INSTANCE_INFO: the complete bound-instance list + reset timers.</summary>
    private void ApplyRaidInstanceInfo(byte[] body)
    {
        if (!RaidInfoWire.TryParseRaidInfo(body, NowSeconds(), out RaidLockout[] lockouts))
        {
            EmitInterface("raid-info", "info", "MALFORMED", LocalPlayerGuid, $"bytes={body.Length}");
            return;
        }
        _raidLockouts = lockouts;
        _instanceOwnership.ApplyDetails(LocalPlayerGuid, lockouts);
        _raidInfoReceivedAt = NowSeconds();
        Console.WriteLine($"[raid-info] {lockouts.Length} saved instance(s)");
        EmitInterface("raid-info", "info", "APPLIED", LocalPlayerGuid, $"count={lockouts.Length}");
    }

    /// <summary>SMSG_INSTANCE_SAVE_CREATED: the character just became bound. The body
    /// is a single unused u32; the event itself is the payload, so re-pull the list.</summary>
    private void ApplyInstanceSaveCreated(byte[] body)
    {
        EmitInterface("raid-info", "save", "CREATED", LocalPlayerGuid, $"bytes={body.Length}");
        ShowUiInfo("You are now saved to this instance.");
        // The save packet does not name the instance; the authoritative list does.
        RequestRaidLockouts("instance save created", force: true);
    }

    /// <summary>SMSG_RAID_INSTANCE_MESSAGE: the periodic "resets in N" warning.</summary>
    private void ApplyRaidInstanceMessage(byte[] body)
    {
        if (!RaidInfoWire.TryParseInstanceMessage(body, out RaidInstanceMessage message))
        {
            EmitInterface("raid-info", "message", "MALFORMED", LocalPlayerGuid, $"bytes={body.Length}");
            return;
        }
        ShowUiInfo(RaidInstanceMessageText(message));
        EmitInterface("raid-info", "message", "APPLIED", LocalPlayerGuid,
            $"type={message.Type};map={message.MapId};secs={message.SecondsUntilReset}");
    }

    /// <summary>SMSG_INSTANCE_RESET / SMSG_INSTANCE_RESET_FAILED: the outcome of a
    /// CMSG_RESET_INSTANCES request for one map.</summary>
    private void ApplyInstanceReset(byte[] body, bool failed)
    {
        bool parsed = failed
            ? RaidInfoWire.TryParseInstanceResetFailed(body, out InstanceResetOutcome outcome)
            : RaidInfoWire.TryParseInstanceReset(body, out outcome);
        if (!parsed)
        {
            EmitInterface("raid-info", "reset", "MALFORMED", LocalPlayerGuid,
                $"failed={failed};bytes={body.Length}");
            return;
        }

        string mapName = RaidMapName(outcome.MapId);
        if (outcome.Failed)
        {
            ShowUiError($"{mapName} could not be reset.");
            EmitInterface("raid-info", "reset", "FAILED", LocalPlayerGuid,
                $"map={outcome.MapId};reason={outcome.Reason}");
        }
        else
        {
            ShowUiInfo($"{mapName} has been reset.");
            EmitInterface("raid-info", "reset", "OK", LocalPlayerGuid, $"map={outcome.MapId}");
            // A successful reset removes a lock; re-pull so the panel matches the server.
            RequestRaidLockouts("instance reset", force: true);
        }
    }

    /// <summary>Human name for a bound map, or a stable fallback when Map.dbc is absent.</summary>
    private string RaidMapName(uint mapId) =>
        _maps?.Get((int)mapId)?.Name is { Length: > 0 } name ? name : $"Map {mapId}";

    private string RaidInstanceMessageText(RaidInstanceMessage message)
    {
        string map = RaidMapName(message.MapId);
        long minutes = (message.SecondsUntilReset + 59) / 60;
        return message.Type switch
        {
            RaidInfoWire.Welcome => $"Welcome to {map}.",
            RaidInfoWire.Expired => $"{map} is now reset.",
            _ => $"{map} will reset in {minutes} minute(s).",
        };
    }

    private void ApplyInstanceOwnership(byte[] body, ulong owner)
    {
        bool hasSaved = _instanceOwnership.ApplyOwnership(owner, body);
        if (owner != LocalPlayerGuid) return;
        _raidLockouts = [];
        _raidInfoReceivedAt = hasSaved ? 0 : NowSeconds();
        if (hasSaved) RequestRaidLockouts("instance ownership snapshot", force: true);
    }

    private void ApplyLastInstance(byte[] body, ulong owner) =>
        _instanceOwnership.ApplyLastInstance(owner, body);

    private string RaidInfoEmptyText()
    {
        if (_instanceOwnership.HasSavedInstances(LocalPlayerGuid) == true)
        {
            string[] names = _instanceOwnership.Maps(LocalPlayerGuid)
                .Select(map => _maps?.Get(unchecked((int)map))?.Name)
                .Where(name => !string.IsNullOrWhiteSpace(name)).Cast<string>().ToArray();
            return names.Length == 0 ? "Requesting saved instance details…"
                : $"Saved to {string.Join(", ", names)}. Requesting reset details…";
        }
        return RaidInfoEverReceived || _instanceOwnership.HasSavedInstances(LocalPlayerGuid) == false
            ? "You are not saved to any instances." : "Requesting saved instances…";
    }

    /// <summary>Drop all lockout state on world-leave / character swap, mirroring the
    /// other party-state resets so a new character never shows a stale list.</summary>
    private void ResetRaidInfo()
    {
        _instanceBoot.Clear();
        _instanceOwnership.Clear();
        ExecuteStaticPopupPlan(MSUIClient.Engine.UI.StaticPopupCoordinatorLaw.HideByType(
            _staticPopupSlots, MSUIClient.Engine.UI.InstanceBootUiLaw.PopupType));
        _raidLockouts = [];
        _raidInfoReceivedAt = 0;
        _raidInfoRequestedAt = 0;
    }
}
