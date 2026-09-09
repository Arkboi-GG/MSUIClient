using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private readonly record struct BattlefieldScope(ulong Owner, uint Map, uint Instance);
    private BattlefieldScope? _battlefieldScoreScope;
    private BattlefieldScorePacket? _battlefieldScore;
    private bool _battlefieldScoreOpen, _battlefieldScorePending;
    private int _battlefieldScoreScroll;
    private double _battlefieldScoreDeadline, _battlefieldScoreLeaveDeadline;
    private string _battlefieldScoreError = "";

    private BattlefieldQueueState.Entry? ActiveBattlefield()
    {
        if (!CanAuthorBattlefield || _worldLoading) return null;
        for (int slot = 0; slot < 3; slot++)
            if (_battlefieldQueues?[slot] is { Packet.Status: BattlefieldStatus.Active } entry &&
                entry.Packet.Map == _config.Start.Map) return entry;
        return null;
    }

    private BattlefieldScope? CurrentBattlefieldScope() => ActiveBattlefield() is { } entry
        ? new(ControlledGuid, entry.Packet.Map, entry.Packet.Instance) : null;

    private void ResetBattlefieldScores()
    {
        _battlefieldScoreOpen = _battlefieldScorePending = false;
        _battlefieldScoreScope = null; _battlefieldScore = null; _battlefieldScoreScroll = 0;
        _battlefieldScoreDeadline = _battlefieldScoreLeaveDeadline = 0; _battlefieldScoreError = "";
    }

    private void UpdateBattlefieldScores()
    {
        if (_battlefieldScoreScope is { } scope && CurrentBattlefieldScope() != scope)
            ResetBattlefieldScores();
        if (_battlefieldScorePending && NowSeconds() >= _battlefieldScoreDeadline)
        { _battlefieldScorePending = false; _battlefieldScoreError = "Scores are unavailable. Try refreshing."; }
    }

    private bool RequestBattlefieldScores()
    {
        if (CurrentBattlefieldScope() is not { } scope) return false;
        if (_battlefieldScoreScope != scope) ResetBattlefieldScores();
        _battlefieldScoreScope = scope; _battlefieldScoreOpen = true; _battlefieldQueueMenu = false;
        if (_battlefieldScorePending && NowSeconds() < _battlefieldScoreDeadline) return false;
        if (_net?.RequestBattlefieldScores() != true)
        { _battlefieldScoreError = "Unable to request scores."; return false; }
        _battlefieldScorePending = true; _battlefieldScoreDeadline = NowSeconds() + 10; _battlefieldScoreError = "";
        return true;
    }

    private void ApplyBattlefieldScores(byte[] body)
    {
        BattlefieldScorePacket packet = BattlefieldScorePacket.Parse(body);
        // Unaddressed native reply: only the currently active main/instance may consume it.
        // A final result can arrive unsolicited when the match ends.
        if (CurrentBattlefieldScope() is not { } scope) return;
        if (!packet.Ended && (!_battlefieldScorePending || _battlefieldScoreScope != scope ||
            NowSeconds() >= _battlefieldScoreDeadline)) return;
        if (_battlefieldScoreScope != scope) ResetBattlefieldScores();
        _battlefieldScoreScope = scope; _battlefieldScore = packet;
        _battlefieldScorePending = false; _battlefieldScoreError = "";
        if (packet.Ended) _battlefieldScoreOpen = true;
        foreach (BattlefieldScoreRow row in packet.Rows)
            QueryBattlefieldPlayerName(row.Guid);
    }

    private bool ReturnFromFinishedBattlefield()
    {
        if (CurrentBattlefieldScope() is not { } scope || _battlefieldScoreScope != scope ||
            _battlefieldScore is not { Ended: true } || NowSeconds() < _battlefieldScoreLeaveDeadline ||
            RefuseTacticalFreezeLiveCommand("leaving a battleground")) return false;
        if (_net?.LeaveBattlefield(scope.Map) != true) return false;
        _battlefieldScoreLeaveDeadline = NowSeconds() + 10;
        // Wait for the actual status clear/world transfer. Never fabricate an exit.
        return true;
    }
}
