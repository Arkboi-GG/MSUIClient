using MSUIClient.Engine.UI;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    /// <summary>
    /// Party questgiver status (PLAN_20 P5). Vanilla's SMSG_QUESTGIVER_STATUS
    /// answers for the asking session and nobody else, so the world markers can
    /// only ever speak for our own character. This asks the server the same
    /// question on behalf of every party member at once, which is what lets the
    /// marker wear an honest "(4)".
    ///
    /// The pull is driven by what is actually on screen: the guids we are already
    /// drawing markers for, and nothing else.
    /// </summary>
    private bool _partyGiverStatusAvailable;

    private const double GiverStatusPullMinIntervalSeconds = 2.0;

    /// <summary>How often to re-ask when neither the marker set nor the roster
    /// moved. A companion levelling up or finishing an objective changes the
    /// answer without changing either, so standing at a hub still refreshes.</summary>
    private const double GiverStatusIdleRefreshSeconds = 6.0;

    private double _giverStatusPulledAt;

    /// <summary>The set of givers our last pull asked about, so a roster or
    /// marker change is what re-asks rather than the frame clock.</summary>
    private ulong _giverStatusRequestHash;

    /// <summary>Per giver, per member, the server's dialog verdict. Never cleared
    /// on a miss: an absent member means "not told", which the marker law counts
    /// as zero rather than asserting anything.</summary>
    private readonly Dictionary<ulong, Dictionary<ulong, byte>> _giverMemberStatuses = [];

    private void ApplyPartyGiverStatusCapability(uint capabilities)
    {
        bool available = (capabilities & SuiCapabilityWire.PartyGiverStatusV1) != 0;
        if (available != _partyGiverStatusAvailable)
            Console.WriteLine(available
                ? "[giver-status] server advertised party-giver-status-v1"
                : "[giver-status] server has no party-giver-status-v1 advertisement");
        _partyGiverStatusAvailable = available;
    }

    /// <summary>Forget the pull throttle so the next frame re-asks immediately —
    /// called on control hand-offs, where the party's business at every marked
    /// giver may read differently the moment a different body is driven.</summary>
    private void PokePartyGiverStatus()
    {
        _giverStatusPulledAt = 0;
        _giverStatusRequestHash = 0;
    }

    private void ResetPartyGiverStatus()
    {
        _partyGiverStatusAvailable = false;
        _giverStatusPulledAt = 0;
        _giverStatusRequestHash = 0;
        _giverMemberStatuses.Clear();
    }

    /// <summary>
    /// Ask about the questgivers we are drawing markers for. Re-asks when that
    /// set or the roster changes, and otherwise on a slow refresh so a companion
    /// levelling up or finishing an objective is picked up while you stand there.
    /// </summary>
    private void UpdatePartyGiverStatus(IReadOnlyList<ulong> markedGivers)
    {
        if (!_partyGiverStatusAvailable || _net is not { IsInWorld: true }) return;
        if (markedGivers.Count == 0 || _partyMembers.Count == 0) return;

        ulong hash = 14695981039346656037UL;      // FNV-1a offset basis
        foreach (ulong guid in markedGivers)
        {
            hash ^= guid;
            hash *= 1099511628211UL;
        }
        foreach (PartyMember member in _partyMembers)
        {
            hash ^= member.Guid;
            hash *= 1099511628211UL;
        }

        double now = NowSeconds();
        if (now - _giverStatusPulledAt < GiverStatusPullMinIntervalSeconds) return;
        // A changed marker set or roster asks immediately; standing still asks
        // slowly, because a companion levelling or finishing an objective changes
        // the answer without changing either.
        if (hash == _giverStatusRequestHash &&
            now - _giverStatusPulledAt < GiverStatusIdleRefreshSeconds) return;

        var givers = new List<ulong>(Math.Min(markedGivers.Count, GiverStatusWire.MaximumGivers));
        foreach (ulong guid in markedGivers)
        {
            if (givers.Count >= GiverStatusWire.MaximumGivers) break;
            givers.Add(guid);
        }
        if (givers.Count == 0) return;

        if (_net.SuiGiverStatus(givers))
        {
            _giverStatusPulledAt = now;
            _giverStatusRequestHash = hash;
        }
    }

    /// <summary>
    /// SMSG_SUI_GIVER_STATUS. The server answers for every (giver, member) pair
    /// it was asked about; a giver it answers for replaces that giver's map
    /// wholesale, so a member who stops having business there stops being counted
    /// instead of lingering at a stale verdict.
    /// </summary>
    private void ApplySuiGiverStatus(byte[] body)
    {
        if (!GiverStatusWire.TryParseGiverStatus(body, out GiverMemberStatus[] entries))
        {
            EmitInterface("giver-status", "status", "MALFORMED", 0, $"bytes={body.Length}");
            return;
        }

        var touched = new HashSet<ulong>();
        foreach (GiverMemberStatus entry in entries)
        {
            if (!touched.Add(entry.Giver)) continue;
            _giverMemberStatuses[entry.Giver] = [];
        }
        foreach (GiverMemberStatus entry in entries)
            _giverMemberStatuses[entry.Giver][entry.Member] = entry.Status;

        EmitInterface("giver-status", "status", "APPLIED", 0,
            $"givers={touched.Count};entries={entries.Length}");
    }

    /// <summary>
    /// How many of the group have this kind of business at this NPC. Counts our
    /// own character from the vanilla status we already hold, so the numeral and
    /// the marker under it can never disagree about us.
    /// </summary>
    private int CountGiverFamily(ulong giver, QuestMarkerFamily family)
    {
        if (family == QuestMarkerFamily.None) return 0;
        int count = QuestMarkerUiLaw.FamilyOf(_questStatuses.GetValueOrDefault(giver)) == family ? 1 : 0;
        if (!_giverMemberStatuses.TryGetValue(giver, out Dictionary<ulong, byte>? members))
            return count;
        foreach ((ulong guid, byte status) in members)
        {
            if (guid == LocalPlayerGuid) continue;   // ours came from _questStatuses
            if (QuestMarkerUiLaw.FamilyOf(status) == family) count++;
        }
        return count;
    }

    /// <summary>The numeral to hang over this NPC, or null when there is nothing
    /// worth saying that the vanilla marker does not already say.</summary>
    private string? GiverNumeralFor(ulong giver)
    {
        if (!_partyGiverStatusAvailable) return null;
        uint own = _questStatuses.GetValueOrDefault(giver);
        int takers = CountGiverFamily(giver, QuestMarkerFamily.Take);
        int finishers = CountGiverFamily(giver, QuestMarkerFamily.TurnIn);
        QuestMarkerFamily family = QuestMarkerUiLaw.NumeralFamily(own, takers, finishers);
        int count = family == QuestMarkerFamily.Take ? takers : finishers;
        return QuestMarkerUiLaw.ShowNumeral(own, family, count)
            ? QuestMarkerUiLaw.NumeralText(count) : null;
    }
}
