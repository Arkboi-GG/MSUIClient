using MSUIClient.Formats;

namespace MSUIClient.Engine.UI;

/// <summary>
/// The zone-channel auto-join — the client half of a handshake the server does not perform.
/// vmangos's Player::UpdateLocalChannels is an empty function ("Updated client-side"): a client
/// that never sends CMSG_JOIN_CHANNEL sits in no channel at all, silently. Until 2026-09-01 that
/// was MSUI: "/join" worked and General / Trade / LocalDefense simply never existed.
///
/// The walk is ChatChannels.dbc's INITIAL rows composed against the player's current ZONE and
/// sent as ordinary joins; it re-runs whenever the zone (or the in-a-capital answer) changes.
/// Zone-dependent rows carry the zone name inside the channel name, so crossing a border is
/// genuinely leaving one channel and joining another — LEAVE(old) then JOIN(new), per row.
/// </summary>
public static class ZoneChannelLaw
{
    /// <summary>AreaTable.dbc 0x08 (vmangos AREA_FLAG_SLAVE_CAPITAL, "Allow trade channel"): the six capitals.</summary>
    public const uint AreaFlagTradeChannel = 0x08;
    /// <summary>AreaTable.dbc 0x200: the sentinel row whose name is the shared city word ("City").</summary>
    public const uint AreaFlagCityNameRow = 0x200;

    /// <summary>The composed names to hold, in DBC row order. Unknown zone or missing city word joins nothing half-named.</summary>
    public static List<string> Wanted(ChatChannelsCatalog catalog, string zoneName, bool inCity, string? cityWord)
    {
        var wanted = new List<string>();
        foreach (ChatChannelsCatalog.Row row in catalog.Rows)
        {
            if (!row.IsInitial) continue;
            if (row.IsCityOnly && !inCity) continue;
            if (row.TakesCityName && string.IsNullOrEmpty(cityWord)) continue;
            if (row.IsZoneDependent && string.IsNullOrEmpty(zoneName)) continue;
            wanted.Add(row.JoinableName(zoneName, cityWord ?? ""));
        }
        return wanted;
    }

    /// <summary>The wire edits from held → wanted: a leave for every held name not wanted, a join for every wanted name not held.</summary>
    public static (List<string> Leave, List<string> Join) Diff(IReadOnlyList<string> held, IReadOnlyList<string> wanted)
    {
        var leave = new List<string>();
        var join = new List<string>();
        foreach (string name in held)
            if (!wanted.Contains(name, StringComparer.OrdinalIgnoreCase)) leave.Add(name);
        foreach (string name in wanted)
            if (!held.Contains(name, StringComparer.OrdinalIgnoreCase)) join.Add(name);
        return (leave, join);
    }
}
