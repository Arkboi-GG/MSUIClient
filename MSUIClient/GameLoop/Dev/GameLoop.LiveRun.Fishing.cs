using System.Globalization;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private ulong _liveFishActor, _liveFishGuid;
    private double _liveFishDeadline, _liveFishCaptureAt;
    private bool _liveFishSawWaiting;

    // Observe an already-started real cast, then use only that actor's bobber
    // after its server state changes to ACTIVE. Loot remains a separate result.
    private bool AdvanceLiveFishing(string line)
    {
        string[] args = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (args.Length != 2 || !double.TryParse(args[1], CultureInfo.InvariantCulture,
                out double seconds) || !double.IsFinite(seconds) || seconds <= 0 || seconds > 60)
        { Log(false, line + " invalid timeout"); return true; }
        double now = NowSeconds();
        if (_liveFishDeadline == 0)
        {
            if (!_entities.TryGet(ControlledGuid, out WorldEntity caster) ||
                caster.Fields.ChannelObject is not > 0)
            { Log(false, line + " requires an active fishing channel"); return true; }
            _liveFishActor = ControlledGuid;
            _liveFishGuid = caster.Fields.ChannelObject.Value;
            _liveFishDeadline = now + seconds;
            _liveFishCaptureAt = now;
            _liveFishSawWaiting = false;
        }
        if (ControlledGuid != _liveFishActor ||
            !_entities.TryGet(_liveFishActor, out WorldEntity actor) || actor.IsDead ||
            actor.Fields.ChannelObject != _liveFishGuid ||
            !_entities.TryGet(_liveFishGuid, out WorldEntity bobber) ||
            !bobber.IsGameObject || bobber.GameObjectType != 17 || now >= _liveFishDeadline)
        {
            Log(false, line + " channel/actor/bobber ended or timed out");
            _liveFishDeadline = 0;
            return true;
        }
        uint state = bobber.Fields.GameObjectState;
        _liveFishSawWaiting |= state == 1;
        bool bite = _liveFishSawWaiting && state == 0;
        if (now >= _liveFishCaptureAt || bite)
        {
            EmitInterface("gathering", "fishing-observer", bite ? "BITE" : "WAITING", bobber.Guid,
                $"state={state};actor=0x{_liveFishActor:X};owner=0x{bobber.Fields.GameObjectCreatedBy:X}");
            _currentVantage = $"fishing-{(bite ? "bite" : "waiting")}-{(int)now}";
            ArmGameplayDump();
            _liveFishCaptureAt = now + 3;
        }
        if (!bite) return false;
        bool sent = UseGameObject(_liveFishGuid);
        Log(sent, $"{line} observedReadyTransition=True;useSent={sent};lootNotYetVerified=True");
        _liveFishDeadline = 0;
        return true;
    }
}
