using MSUIClient.Engine;
using MSUIClient.Formats;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private double _channelObservedStarted;
    private double _channelLastTick;
    private int _channelTickIndex;
    private uint _channelInitialDurationMs;

    private void EmitChannelVerdict(string @event, uint durationMs = 0, uint remainingMs = 0,
        string tickKind = "NONE", uint amount = 0, string source = "SERVER")
    {
        double now = NowSeconds();
        double delta = @event == "TICK" && _channelLastTick > 0 ? (now - _channelLastTick) * 1000.0 : 0;
        if (@event == "START")
        {
            _channelObservedStarted = now;
            _channelLastTick = 0;
            _channelTickIndex = 0;
            _channelInitialDurationMs = durationMs;
        }
        else if (@event == "TICK")
        {
            _channelLastTick = now;
            _channelTickIndex++;
        }
        SpellInfo? info = _spellCatalog?.TryGet(_castBarSpell, out SpellInfo found) == true ? found : null;
        var verdict = new ChannelSpellVerdict(now, _net?.PlayerName ?? "", _castBarSpell,
            info?.Name ?? $"Spell {_castBarSpell}", @event,
            durationMs == 0 ? _channelInitialDurationMs : durationMs, remainingMs,
            _channelTickIndex, delta, tickKind, amount,
            (_character?.GroundSpeed ?? 0) > 0.3f,
            _character?.CurrentPresentationAnimation ?? "none", source);
        _verdicts.Add(verdict);
        Console.WriteLine($"[verdict:spell-channel] {verdict.ToLine()}");
    }

    private void ObserveChannelCombat(CombatEvent value)
    {
        if (_castBarPhase != CastBarPhase.Channel || _net is null) return;
        uint remaining = (uint)Math.Max(0, (_castBarEnds - NowSeconds()) * 1000.0);
        switch (value)
        {
            case CombatPeriodicAura aura when aura.SpellId == _castBarSpell && aura.Caster == _net.PlayerGuid:
                foreach (CombatPeriodicTick tick in aura.Ticks)
                    EmitChannelVerdict("TICK", remainingMs: remaining,
                        tickKind: tick.Kind.ToString().ToUpperInvariant(), amount: tick.Amount,
                        source: "SMSG_PERIODICAURALOG");
                break;
            // The 5875 server reports Drain Life's periodic damage through
            // SMSG_SPELLNONMELEEDAMAGELOG with its periodic bit set, rather than
            // through SMSG_PERIODICAURALOG.  Both are authoritative tick wires.
            case CombatSpellDamage damage when damage.SpellId == _castBarSpell &&
                                                damage.Attacker == _net.PlayerGuid && damage.Periodic:
                EmitChannelVerdict("TICK", remainingMs: remaining, tickKind: "DAMAGE",
                    amount: damage.Damage, source: "SMSG_SPELLNONMELEEDAMAGELOG");
                break;
        }
    }
}
