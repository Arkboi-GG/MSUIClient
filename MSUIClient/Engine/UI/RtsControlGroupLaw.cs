namespace MSUIClient.Engine.UI;

public readonly record struct RtsFormationUnit(int MemberCount, bool IsRaid)
{
    public int SubgroupCount => (MemberCount + RtsControlGroupLaw.PartySize - 1) /
        RtsControlGroupLaw.PartySize;
}

/// <summary>
/// Pure rules for the Free View numbered groups. These are client-session
/// selections, not World of Warcraft groups; only the explicit Auto-group
/// command asks the server to create real parties/raids.
/// </summary>
public static class RtsControlGroupLaw
{
    public const int GroupCount = 10;
    public const int PartySize = 5;
    public const int RaidSize = 40;
    public const int MaximumWireSubjects = byte.MaxValue;

    public static string DisplayNumber(int index) => index switch
    {
        >= 0 and <= 8 => (index + 1).ToString(),
        9 => "0",
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    /// <summary>
    /// Snapshot a selection for the u8-count SUI order wire: zero GUIDs and
    /// duplicates disappear, first-seen order wins, and the packet limit is
    /// never exceeded.
    /// </summary>
    public static ulong[] NormalizeMembers(IEnumerable<ulong> members)
    {
        ArgumentNullException.ThrowIfNull(members);
        var seen = new HashSet<ulong>();
        var result = new List<ulong>();
        foreach (ulong guid in members)
        {
            if (guid == 0 || !seen.Add(guid)) continue;
            result.Add(guid);
            if (result.Count == MaximumWireSubjects) break;
        }
        return [.. result];
    }

    /// <summary>
    /// Up to five selected bots form one ordinary party. Six or more form raid
    /// units of at most forty; a remainder above forty is another raid even
    /// when that final raid currently contains fewer than six members.
    /// </summary>
    public static RtsFormationUnit[] PlanFormation(int memberCount)
    {
        int remaining = Math.Clamp(memberCount, 0, MaximumWireSubjects);
        if (remaining == 0) return [];
        bool raids = remaining > PartySize;
        var result = new List<RtsFormationUnit>();
        int capacity = raids ? RaidSize : PartySize;
        while (remaining > 0)
        {
            int take = Math.Min(capacity, remaining);
            result.Add(new RtsFormationUnit(take, raids));
            remaining -= take;
        }
        return [.. result];
    }

    public static string FormationSummary(int memberCount)
    {
        RtsFormationUnit[] units = PlanFormation(memberCount);
        if (units.Length == 0) return "no groups";
        if (!units[0].IsRaid) return $"1 party ({units[0].MemberCount}/5)";
        int subgroups = units.Sum(unit => unit.SubgroupCount);
        return units.Length == 1
            ? $"1 raid ({subgroups} parties)"
            : $"{units.Length} raids ({subgroups} parties)";
    }
}
