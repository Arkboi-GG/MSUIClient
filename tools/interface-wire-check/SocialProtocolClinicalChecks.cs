using MSUIClient;
using MSUIClient.Engine.UI;
using MSUIClient.Net;

internal static class SocialProtocolClinicalChecks
{
    public static void Run()
    {
        SocialPackets.FriendStatus online = SocialPackets.ParseFriendStatus(
            Convert.FromHexString("06887766554433221102EF0500002A00000008000000"));
        Check(online.Result == SocialPackets.FriendAddedOnline &&
              online.Guid == 0x1122334455667788 &&
              online.Presence == new SocialPackets.Online(2, 1519, 42, 8),
            "SMSG_FRIEND_STATUS online-tail bytes drift");
        SocialPackets.FriendStatus offline = SocialPackets.ParseFriendStatus(
            Convert.FromHexString("038877665544332211"));
        Check(offline.Result == SocialPackets.FriendOffline && offline.Presence is null,
            "SMSG_FRIEND_STATUS bare result/guid bytes drift");

        var friends = new List<SocialPackets.FriendEntry>();
        var ignores = new List<ulong>();
        SocialPackets.ApplyStatus(friends, ignores, online);
        Check(friends.Count == 1 && friends[0].Area == 1519 && friends[0].Level == 42,
            "friend-added-online did not create the local row");
        SocialPackets.ApplyStatus(friends, ignores, offline);
        Check(friends[0] == new SocialPackets.FriendEntry(online.Guid, 0, 0, 0, 0),
            "friend-offline retained a stale presence tail");
        SocialPackets.ApplyStatus(friends, ignores,
            new(SocialPackets.IgnoreAdded, online.Guid, null));
        SocialPackets.ApplyStatus(friends, ignores,
            new(SocialPackets.IgnoreAdded, online.Guid, null));
        Check(ignores.SequenceEqual([online.Guid]),
            "ignore-added did not de-duplicate the local list");
        SocialPackets.ApplyStatus(friends, ignores,
            new(SocialPackets.IgnoreRemoved, online.Guid, null));
        SocialPackets.ApplyStatus(friends, ignores,
            new(SocialPackets.FriendRemoved, online.Guid, null));
        Check(ignores.Count == 0 && friends.Count == 0,
            "friend/ignore removal result did not update local state");

        Check(FriendStatusUiLaw.Template(0x01) ==
                  "You don't have room for any more friends." &&
              FriendStatusUiLaw.Compose(FriendStatusUiLaw.Template(0x02)!, "Nico") ==
                  "|Hplayer:Nico|h[Nico]|h has come online." &&
              FriendStatusUiLaw.Template(0x11) ==
                  "That name is ambiguous, type more of the player's server name" &&
              FriendStatusUiLaw.Template(0x12) is null,
            "friend/ignore GlobalStrings result vocabulary drift");

        SocialPackets.WhoRequest query = SocialPackets.ParseWhoFilter(
            "n-bob g-\"Legacy of Steel\" z-\"Elwynn Forest\" c-mage c-warlock " +
            "r-\"night elf\" 10-20 one two three four five",
            name => name.Equals("Elwynn Forest", StringComparison.OrdinalIgnoreCase)
                ? 12u : null);
        Check(query.PlayerName == "bob" && query.GuildName == "Legacy of Steel" &&
              query.LevelMin == 10 && query.LevelMax == 20 &&
              query.ClassMask == ((1u << 8) | (1u << 9)) &&
              query.RaceMask == 1u << 4 && query.Zones.SequenceEqual([12u]) &&
              query.SearchTerms.SequenceEqual(["one", "two", "three", "four", "five"]),
            "quoted/tagged /who filter grammar drift");
        SocialPackets.WhoRequest fallback = SocialPackets.ParseWhoFilter(
            "z-Nowhere c-necromancer 60");
        Check(fallback.LevelMin == 60 && fallback.LevelMax == 60 &&
              fallback.ClassMask == uint.MaxValue &&
              fallback.SearchTerms.SequenceEqual(["Nowhere", "necromancer"]),
            "unresolved /who tags did not widen into search terms");

        var capped = new SocialPackets.WhoRequest(1, 100, "", "", uint.MaxValue,
            uint.MaxValue, Enumerable.Range(1, 12).Select(i => (uint)i).ToArray(),
            Enumerable.Range(1, 6).Select(i => $"term{i}").ToArray());
        var whoReader = new PacketReader(SocialPackets.BuildWhoBody(capped));
        Check(whoReader.ReadU32() == 1 && whoReader.ReadU32() == 100 &&
              whoReader.ReadCString() == "" && whoReader.ReadCString() == "" &&
              whoReader.ReadU32() == uint.MaxValue &&
              whoReader.ReadU32() == uint.MaxValue && whoReader.ReadU32() == 10,
            "CMSG_WHO fixed prefix or 10-zone cap drift");
        for (uint i = 1; i <= 10; i++) Check(whoReader.ReadU32() == i,
            "CMSG_WHO zone ordering drift");
        Check(whoReader.ReadU32() == 4 && whoReader.ReadCString() == "term1" &&
              whoReader.ReadCString() == "term2" && whoReader.ReadCString() == "term3" &&
              whoReader.ReadCString() == "term4" && whoReader.Remaining == 0,
            "CMSG_WHO four-term cap/body drift");
        CheckThrows(() => SocialPackets.ParseFriendStatus(new byte[8]));
        CheckThrows(() => SocialPackets.ParseFriendStatus(new byte[10]));
        CheckThrows(() => SocialPackets.ParseFriendStatus(
            Convert.FromHexString("060000000000000000")));

        string root = ClientConfig.FindRepoRoot();
        string social = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Panels",
            "GameLoop.Social.cs"));
        string net = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Scene",
            "GameLoop.Net.cs"));
        string session = SourceText.Read(Path.Combine(root, "MSUIClient", "Net",
            "WorldSession.cs"));
        Check(social.Contains("SocialPackets.ParseFriendStatus(body)", StringComparison.Ordinal) &&
              social.Contains("SocialPackets.ApplyStatus(_friends, _ignored", StringComparison.Ordinal) &&
              social.Contains("AddChatMessage(FriendStatusUiLaw.Compose", StringComparison.Ordinal) &&
              !social.Contains("if (body.Length >= 9) _net?.FriendList()", StringComparison.Ordinal),
            "production friend-status route still degrades to list refresh");
        Check(net.Contains("FlushPendingFriendStatus(response.Guid)", StringComparison.Ordinal),
            "deferred friend-status name line is not flushed by NAME_QUERY_RESPONSE");
        Check(social.Contains("SendWhoFilter(ReadBuffer(_whoInput))", StringComparison.Ordinal) &&
              social.Contains("_areas?.IdForName(name)", StringComparison.Ordinal) &&
              session.Contains("SocialPackets.BuildWhoBody(request)", StringComparison.Ordinal),
            "advanced /who UI filter is not routed through the exact body builder");
    }

    private static void CheckThrows(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException) { return; }
        throw new InvalidDataException("malformed friend-status packet was accepted");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
