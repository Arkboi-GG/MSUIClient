namespace MSUIClient.Engine.UI;

/// <summary>
/// Benilla's explicit /partytest developer fixture. The fake GUIDs remain outside streamed
/// world state; callers project these rows through the ordinary party roster/stats renderer.
/// </summary>
public static class PartyTestSandboxLaw
{
    public const ulong AliceGuid = 0xF001;
    public const ulong BobGuid = 0xF002;
    public const ulong CarolGuid = 0xF003;
    public const ulong DaveGuid = 0xF004;

    public readonly record struct FixtureMember(
        string Name,
        ulong Guid,
        byte Status,
        PartyMemberStatsSnapshot Stats);

    public static FixtureMember[] Roster(short? playerX = null, short? playerY = null)
    {
        (short? X, short? Y) Seat(int dx, int dy) =>
            playerX is short x && playerY is short y
                ? (unchecked((short)(x + dx)), unchecked((short)(y + dy)))
                : (null, null);

        (short? X, short? Y) alice = Seat(30, 0);
        (short? X, short? Y) bob = Seat(0, 80);
        (short? X, short? Y) carol = Seat(-300, 0);
        return
        [
            new("Alice", AliceGuid, PartyFrameUiLaw.Online,
                new(Health: 820, MaxHealth: 1240, PowerType: 0, Power: 300,
                    MaxPower: 410, Level: 32, PositionX: alice.X, PositionY: alice.Y)),
            new("Bob", BobGuid, PartyFrameUiLaw.Online | PartyFrameUiLaw.Afk,
                new(Health: 455, MaxHealth: 980, PowerType: 3, Power: 300,
                    MaxPower: 410, Level: 30, PositionX: bob.X, PositionY: bob.Y)),
            new("Carol", CarolGuid, PartyFrameUiLaw.Online | PartyFrameUiLaw.Dead,
                new(Health: 0, MaxHealth: 1105, PowerType: 0, Power: 300,
                    MaxPower: 410, Level: 31, PositionX: carol.X, PositionY: carol.Y)),
            new("Dave", DaveGuid, 0,
                new(Health: 0, MaxHealth: 0, PowerType: 0, Power: 300,
                    MaxPower: 410, Level: 0)),
        ];
    }

    /// <summary>Apply the server's one-mark-per-unit behavior to the local sandbox board.</summary>
    public static void ApplyRaidTarget(ulong[] board, ulong guid, byte requested)
    {
        ArgumentNullException.ThrowIfNull(board);
        if (guid == 0 || requested > board.Length) return;
        for (int i = 0; i < board.Length; i++)
            if (board[i] == guid) board[i] = 0;
        if (requested > 0) board[requested - 1] = guid;
    }
}
