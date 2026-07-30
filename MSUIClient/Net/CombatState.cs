namespace MSUIClient.Net;

/// <summary>
/// Game-thread combat seam. It owns engagement edges and a bounded event queue;
/// animation, floating text, unit frames and the combat log will consume the
/// same normalized events in later port slices.
/// </summary>
public sealed class CombatState
{
    private const int MaxBufferedEvents = 256;
    private readonly Queue<CombatEvent> _events = new();
    private readonly HashSet<ulong> _engaged = new();
    private readonly Dictionary<ulong, ulong> _attackTargets = new();

    public long ReceivedCount { get; private set; }
    public int BufferedCount => _events.Count;
    public int EngagedCount => _engaged.Count;
    public CombatEvent? LastEvent { get; private set; }
    public long AttackRevision { get; private set; }

    public bool IsEngaged(ulong guid) => _engaged.Contains(guid);
    public bool TryGetAttackTarget(ulong attacker, out ulong victim)
        => _attackTargets.TryGetValue(attacker, out victim);

    public CombatEvent Apply(CombatEvent combatEvent, EntityStore entities)
    {
        // Benilla's full-block synthesis: the trailing blocked amount is the
        // only indication when a known victim took zero damage.
        if (combatEvent is CombatMeleeSwing swing &&
            swing.Damage == 0 && swing.Blocked != 0 && entities.TryGet(swing.Victim, out _))
            combatEvent = swing with { VictimState = 5 };

        switch (combatEvent)
        {
            case CombatAttackStarted start:
                _engaged.Add(start.Attacker);
                _attackTargets[start.Attacker] = start.Victim;
                AttackRevision++;
                entities.SetEngaged(start.Attacker, true, start.Victim);
                break;
            case CombatAttackStopped stop:
                _engaged.Remove(stop.Attacker);
                _attackTargets.Remove(stop.Attacker);
                AttackRevision++;
                entities.SetEngaged(stop.Attacker, false);
                if (stop.VictimDied) entities.StopMovement(stop.Victim);
                break;
        }

        ReceivedCount++;
        LastEvent = combatEvent;
        if (_events.Count == MaxBufferedEvents) _events.Dequeue();
        _events.Enqueue(combatEvent);
        return combatEvent;
    }

    public bool TryDequeue(out CombatEvent combatEvent)
    {
        if (_events.TryDequeue(out var next)) { combatEvent = next; return true; }
        combatEvent = null!;
        return false;
    }

    public void Clear()
    {
        _events.Clear();
        _engaged.Clear();
        _attackTargets.Clear();
        ReceivedCount = 0;
        AttackRevision = 0;
        LastEvent = null;
    }
}
