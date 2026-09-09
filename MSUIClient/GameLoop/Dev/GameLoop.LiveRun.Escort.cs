using System.Globalization;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private ulong _liveEscortGuid;
    private double _liveEscortDeadline, _liveEscortNext, _liveEscortCapture;
    private bool _liveEscortCastNext;

    // Scripted-quest fixture: real objectives/combat, GM placement alongside the
    // moving NPC. This cannot certify pathfinding or player travel.
    private bool AdvanceLiveEscort(string line)
    {
        string[] p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (p.Length != 4 || !uint.TryParse(p[1], out uint questId) ||
            !uint.TryParse(p[2], out uint protectionSpell) ||
            !double.TryParse(p[3], CultureInfo.InvariantCulture, out double seconds) || seconds <= 0)
        { Log(false, line + " invalid arguments"); return true; }
        double now = NowSeconds();
        if (_liveEscortDeadline == 0)
        {
            _liveEscortGuid = _selectionGuid;
            _liveEscortDeadline = now + seconds;
            _liveEscortNext = _liveEscortCapture = now;
            _liveEscortCastNext = false;
        }
        if (!_entities.TryGet(ControlledGuid, out WorldEntity actor)) return false;
        var quest = actor.Fields.QuestLog().FirstOrDefault(q => q.QuestId == questId);
        bool complete = (quest.Counters & 0x0100_0000u) != 0 ||
            _questProgress.GetValueOrDefault((ControlledGuid, questId)) == "complete";
        bool failed = quest.QuestId == 0 || (quest.Counters & 0x0200_0000u) != 0;
        if (complete || failed || now >= _liveEscortDeadline)
        {
            Log(complete && !failed, $"{line} complete={complete};failed={failed};counters=0x{quest.Counters:X8}");
            _liveEscortDeadline = 0;
            return true;
        }
        if (now < _liveEscortNext) return false;
        if (_liveEscortCastNext)
        {
            if (protectionSpell != 0) TryCast(protectionSpell);
            _liveEscortCastNext = false;
            _liveEscortNext = now + 2.5;
        }
        else if (_entities.TryGet(_liveEscortGuid, out WorldEntity escort) && !escort.IsDead)
        {
            SendGmCommand(string.Create(CultureInfo.InvariantCulture,
                $".go xyz {escort.Position.X:R} {escort.Position.Y:R} {escort.Position.Z:R} {_config.Start.Map}"),
                "protocol-escort-placement");
            _liveEscortCastNext = true;
            _liveEscortNext = now + .5;
        }
        if (now >= _liveEscortCapture)
        {
            _currentVantage = $"escort-{questId}-{(int)now}";
            ArmGameplayDump();
            _liveEscortCapture = now + 30;
        }
        return false;
    }
}
