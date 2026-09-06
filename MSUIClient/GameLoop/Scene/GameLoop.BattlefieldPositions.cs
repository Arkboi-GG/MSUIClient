using MSUIClient.Formats;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private BattlefieldScope? _battlefieldPositionsScope;
    private BattlefieldPositionsPacket? _battlefieldPositions;
    private bool _battlefieldPositionsPending;
    private double _battlefieldPositionsDeadline, _battlefieldPositionsReceived, _battlefieldPositionsNextRequest;
    private readonly HashSet<ulong> _battlefieldDeparted = [];
    private RaceTeamCatalog? _raceTeams;
    private bool _raceTeamsLoaded;
    private readonly List<(ulong Guid, bool Joined, BattlefieldScope Scope, double Deadline)> _battlefieldRosterNotices = [];

    private void ResetBattlefieldPositions()
    {
        _battlefieldPositionsScope = null; _battlefieldPositions = null;
        _battlefieldPositionsPending = false; _battlefieldPositionsNextRequest = 0;
        _battlefieldRosterNotices?.Clear(); _battlefieldDeparted?.Clear();
    }

    private RaceTeam? BattlefieldRaceTeam(ulong guid)
    {
        if (!_raceTeamsLoaded && _mpq is not null) { _raceTeams = RaceTeamCatalog.Load(_mpq); _raceTeamsLoaded = true; }
        uint race = _entities.TryGet(guid, out WorldEntity unit) ? unit.Fields.Bytes0.Race : 0u;
        if (race == 0 && _playerTraits.TryGetValue(guid, out PlayerTraits traits)) race = traits.Race;
        return _raceTeams?.Team(race);
    }

    private bool BattlefieldPositionsCurrent() => _battlefieldPositions is not null &&
        _battlefieldPositionsScope is { } scope && CurrentBattlefieldScope() == scope &&
        NowSeconds() - _battlefieldPositionsReceived < 15;

    private bool RequestBattlefieldPositions()
    {
        if (CurrentBattlefieldScope() is not { } scope) return false;
        if (_battlefieldPositionsScope != scope) ResetBattlefieldPositions();
        if (_battlefieldPositionsPending && NowSeconds() < _battlefieldPositionsDeadline) return false;
        if (_net?.RequestBattlefieldPositions() != true) return false;
        _battlefieldPositionsScope = scope; _battlefieldPositionsPending = true;
        _battlefieldPositionsDeadline = NowSeconds() + 10; _battlefieldPositionsNextRequest = NowSeconds() + 5;
        return true;
    }

    private void UpdateBattlefieldPositions()
    {
        if (CurrentBattlefieldScope() is not { } scope)
        { ResetBattlefieldPositions(); return; }
        if (_battlefieldPositionsScope is { } old && old != scope) ResetBattlefieldPositions();
        if (_battlefieldPositionsPending && NowSeconds() >= _battlefieldPositionsDeadline) _battlefieldPositionsPending = false;
        if (_battlefieldPositions is not null && !BattlefieldPositionsCurrent()) _battlefieldPositions = null;
        if (_worldMapOpen && NowSeconds() >= _battlefieldPositionsNextRequest) RequestBattlefieldPositions();
        if (!_worldMapOpen) _battlefieldPositionsNextRequest = 0;
        FlushBattlefieldRosterNotices();
    }

    private void QueryBattlefieldPlayerName(ulong guid)
    {
        if (!_playerNames.ContainsKey(guid) && !_queriedPlayerNames.Contains(guid) && _net?.TryNameQuery(guid) == true)
            _queriedPlayerNames.Add(guid);
    }

    private void ApplyBattlefieldPositions(byte[] body)
    {
        BattlefieldPositionsPacket packet = BattlefieldPositionsPacket.Parse(body);
        if (!_battlefieldPositionsPending || NowSeconds() >= _battlefieldPositionsDeadline ||
            _battlefieldPositionsScope is not { } scope || CurrentBattlefieldScope() != scope) return;
        _battlefieldPositions = new(Array.AsReadOnly(packet.Teammates.Where(p => !_battlefieldDeparted.Contains(p.Guid)).ToArray()),
            packet.FriendlyFlagCarrier is { } flag && !_battlefieldDeparted.Contains(flag.Guid) ? flag : null);
        _battlefieldPositionsPending = false; _battlefieldPositionsReceived = NowSeconds();
        foreach (var teammate in packet.Teammates) QueryBattlefieldPlayerName(teammate.Guid);
        if (packet.FriendlyFlagCarrier is { } carrier) QueryBattlefieldPlayerName(carrier.Guid);
        // Map observations never relocate streamed entities or change targetability.
    }

    private void ApplyBattlefieldRosterNotice(byte[] body, bool joined)
    {
        if (body.Length != 8) throw new InvalidDataException("Invalid battleground roster notice");
        ulong guid = new PacketReader(body).ReadU64();
        if (guid == 0 || CurrentBattlefieldScope() is not { } scope) return;
        if (_battlefieldPositionsScope != scope) ResetBattlefieldPositions();
        _battlefieldPositionsScope = scope;
        if (joined) _battlefieldDeparted.Remove(guid); else _battlefieldDeparted.Add(guid);
        if (_battlefieldRosterNotices.Count >= 128) _battlefieldRosterNotices.RemoveAt(0);
        _battlefieldRosterNotices.Add((guid, joined, scope, NowSeconds() + 10));
        if (!joined && _battlefieldPositions is { } positions)
            _battlefieldPositions = new(Array.AsReadOnly(positions.Teammates.Where(p => p.Guid != guid).ToArray()),
                positions.FriendlyFlagCarrier?.Guid == guid ? null : positions.FriendlyFlagCarrier);
        QueryBattlefieldPlayerName(guid); FlushBattlefieldRosterNotices();
    }

    private void FlushBattlefieldRosterNotices()
    {
        // Preserve receive order while waiting for names, with a bounded fallback.
        while (_battlefieldRosterNotices.Count > 0)
        {
            var notice = _battlefieldRosterNotices[0];
            if (CurrentBattlefieldScope() != notice.Scope) { _battlefieldRosterNotices.RemoveAt(0); continue; }
            bool known = _playerNames.TryGetValue(notice.Guid, out string? name);
            if (!known && NowSeconds() < notice.Deadline) break;
            _battlefieldRosterNotices.RemoveAt(0);
            AddChatMessage($"{(string.IsNullOrEmpty(name) ? "A player" : name)} has {(notice.Joined ? "joined" : "left")} the battle.");
        }
    }
}
