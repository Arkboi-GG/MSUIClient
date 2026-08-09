namespace MSUIClient.Engine.UI;

/// <summary>
/// Frozen build-5875 group-state laws that are observable outside the PartyFrame renderer:
/// roster-diff system lines, packet-specific notices, command feedback, and raid-target state.
/// </summary>
public static class GroupUiLaw
{
    public const uint OperationInvite = 0;
    public const uint OperationLeave = 2;

    public const uint ResultOk = 0;
    public const uint ResultBadPlayerName = 1;
    public const uint ResultTargetNotInGroup = 2;
    public const uint ResultGroupFull = 3;
    public const uint ResultAlreadyInGroup = 4;
    public const uint ResultNotInGroup = 5;
    public const uint ResultNotLeader = 6;
    public const uint ResultWrongFaction = 7;
    public const uint ResultIgnoringYou = 8;

    public static string[] RosterLines(byte previousGroupType,
        IReadOnlyList<PartyRosterWireMember> previousMembers, PartyRosterWire current)
    {
        bool leaving = current.LeaderGuid == 0;
        bool raidWording = current.GroupType == 1 || leaving && previousGroupType == 1;
        string joined = raidWording ? "{0} has joined the raid group" : "{0} joins the party.";
        string left = raidWording ? "{0} has left the raid group" : "{0} leaves the party.";
        var lines = new List<string>();

        foreach (PartyRosterWireMember member in current.Members)
            if (!previousMembers.Any(old => old.Guid == member.Guid))
                lines.Add(string.Format(joined, member.Name));

        foreach (PartyRosterWireMember old in previousMembers)
            if (!current.Members.Any(member => member.Guid == old.Guid))
                lines.Add(string.Format(left, old.Name));

        if (!leaving && current.GroupType == 1 && previousGroupType != 1)
            lines.Add("You have joined a raid group");
        return lines.ToArray();
    }

    public static string InvitedLine(string inviter) =>
        $"{inviter} has invited you to join a group.";

    public static string DeclinedLine(string name) =>
        $"{name} declines your group invitation.";

    public static string UninvitedLine => "You have been removed from the group.";

    public static string[] DestroyedLines(bool inGroup) => inGroup
        ? ["Your group has been disbanded."]
        : [];

    public static string LeaderChangedLine(string name, string? ownName) =>
        string.Equals(name, ownName, StringComparison.Ordinal)
            ? "You are now the group leader."
            : $"{name} is now the group leader.";

    public static string[] CommandResultLines(PartyCommandResultWire wire)
    {
        if (wire.Result == ResultOk)
        {
            return wire.Operation switch
            {
                OperationInvite => [$"You have invited {wire.Member} to join your group."],
                OperationLeave => ["You leave the group."],
                _ => [],
            };
        }

        string line = wire.Result switch
        {
            ResultBadPlayerName => $"Cannot find '{wire.Member}'.",
            ResultTargetNotInGroup => $"{wire.Member} is not in your party.",
            ResultGroupFull => "Your party is full.",
            ResultAlreadyInGroup => $"{wire.Member} is already in a group.",
            ResultNotInGroup => "You aren't in a party.",
            ResultNotLeader => "You are not the party leader.",
            ResultWrongFaction => "Target is not part of your alliance.",
            ResultIgnoringYou => $"{wire.Member} is ignoring you.",
            _ => $"Party command failed ({wire.Result}).",
        };
        return [line];
    }

    public static void ApplyRaidTarget(ulong[] board, byte icon, ulong guid)
    {
        ArgumentNullException.ThrowIfNull(board);
        if (icon < board.Length) board[icon] = guid;
    }

    public static void ApplyRaidTargetList(ulong[] board,
        IReadOnlyList<PartyRaidTargetEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(board);
        Array.Clear(board);
        foreach (PartyRaidTargetEntry entry in entries)
            ApplyRaidTarget(board, entry.Icon, entry.Guid);
    }

    /// <summary>Lua-facing raid mark scale: 1..8, or zero when unmarked.</summary>
    public static byte RaidTargetIndex(IReadOnlyList<ulong> board, ulong guid)
    {
        if (guid == 0) return 0;
        for (int i = 0; i < board.Count; i++)
            if (board[i] == guid) return checked((byte)(i + 1));
        return 0;
    }
}
