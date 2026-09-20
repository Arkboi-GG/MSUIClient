using System.Globalization;
using MSUIClient.Formats;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private ulong _liveFightGuid, _liveFightActor;
    private double _liveFightDeadline, _liveFightCastAt, _liveFightCaptureAt;

    // Bounded combat fixture on an existing selected enemy. Normal cast handlers
    // own gameplay; this observer never creates, kills, moves or heals a unit.
    private bool AdvanceLiveFight(string line)
    {
        string[] args = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (args.Length != 3 || !uint.TryParse(args[1], out uint spell) ||
            !double.TryParse(args[2], CultureInfo.InvariantCulture, out double seconds) ||
            !double.IsFinite(seconds) || seconds <= 0 || seconds > 600)
        { Log(false, line + " invalid arguments"); return true; }
        double now = NowSeconds();
        if (_liveFightDeadline == 0)
        {
            if (_controller is not { Grounded: true })
            { Log(false, line + " fixture requires the actor to be grounded before combat"); return true; }
            if (!_entities.TryGet(_selectionGuid, out WorldEntity enemy) ||
                !enemy.IsCreature || !CanActorAttack(enemy, ControlledGuid))
            { Log(false, line + " requires a living attackable creature"); return true; }
            _liveFightGuid = _selectionGuid;
            _liveFightActor = ControlledGuid;
            _liveFightDeadline = now + seconds;
            _liveFightCastAt = _liveFightCaptureAt = now;
        }
        bool targetPresent = _entities.TryGet(_liveFightGuid, out WorldEntity target);
        bool actorPresent = _entities.TryGet(_liveFightActor, out WorldEntity actor);
        bool dead = targetPresent && target.IsDead;
        bool interrupted = ControlledGuid != _liveFightActor ||
            (_selectionGuid != _liveFightGuid && !dead) ||
            !actorPresent || actor.IsDead || !targetPresent;
        if (dead || interrupted || now >= _liveFightDeadline)
        {
            Log(dead && !interrupted, $"{line} target=0x{_liveFightGuid:X};observedDead={dead};" +
                $"interrupted={interrupted};targetHealth={(targetPresent ? target.Fields.Health : 0)}");
            _liveFightDeadline = 0;
            return true;
        }
        if (now >= _liveFightCaptureAt)
        {
            EmitCombat("LiveFightState", "diagnostic", target.Guid,
                $"entry={target.Entry};health={target.Fields.Health};actorHealth={actor.Fields.Health}");
            _currentVantage = $"fight-{target.Entry}-{(int)now}";
            ArmGameplayDump();
            _liveFightCaptureAt = now + 5;
        }
        if (spell != 0 && now >= _liveFightCastAt)
        {
            TryCast(spell);
            _liveFightCastAt = now + 2.1;
        }
        return false;
    }
}
