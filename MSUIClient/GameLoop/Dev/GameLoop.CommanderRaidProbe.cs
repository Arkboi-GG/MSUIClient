using MSUIClient.Engine.UI;
using System.Numerics;
using MSUIClient.World.Encounters;

namespace MSUIClient;

/// <summary>Offline render QA only. Synthetic roster never enters party state or the wire.</summary>
public sealed partial class GameLoop
{
    private static readonly bool CommanderRaidProbeArmed =
        Environment.GetEnvironmentVariable("MSUI_COMMANDER_RAID_PROBE") == "1";
    private int _commanderRaidProbeStage;
    private double _commanderRaidProbeAt;
    private IReadOnlyList<CommanderRaidMember>? _commanderRaidProbeRoster;

    private void UpdateCommanderRaidProbe()
    {
        if (!CommanderRaidProbeArmed || _commanderRaidProbeStage == 99 || _net is { IsInWorld: true }) return;
        double now = NowSeconds();
        if (_commanderRaidProbeAt == 0) { _commanderRaidProbeAt = now; return; }
        if (now - _commanderRaidProbeAt > 120)
        { Console.WriteLine("[commander-raid-probe] FAIL world/render timeout"); _quitRequested = true; _commanderRaidProbeStage = 99; return; }
        switch (_commanderRaidProbeStage)
        {
            case 0:
                if (_gl is null || now - _commanderRaidProbeAt < 1) return;
                _config.DevTools = true;
                EnterOfflineWorld();
                _commanderRaidProbeStage = 1;
                break;
            case 1:
                if (_worldLoading || !_creatorWorldRequested || _controller is null || now - _commanderRaidProbeAt < 5) return;
                _commanderRaidProbeRoster = Enumerable.Range(1, 39).Select(i => new CommanderRaidMember(
                    (ulong)i, "Preview" + i, i <= 3 ? 1u : i <= 13 ? 5u : 8u, true, true,
                    new HashSet<uint>(i > 3 && i <= 13 ? [2061u] : [116u]), i > 3 && i <= 13 ? 2061u : 0u, 116u)).ToArray();
                _commanderRaidProbeRoster = new[] { new CommanderRaidMember(1000, "MainPreview", 1, false, true, new HashSet<uint> { 116 }) }
                    .Concat(_commanderRaidProbeRoster).ToArray();
                _commanderRaidDraft = CommanderRaidPlan.Empty with { MainGuid = 1000, MainRole = CommanderRaidRole.MainTank };
                AutoAssignCommanderRaid(_commanderRaidProbeRoster, 1000);
                _commanderRaidLoaded = true;
                _partyTacticsOpen = true;
                _partyTacticsGuid = 4;
                _commanderRaidAvailable = false;
                _commanderRaidProbeAt = now;
                _commanderRaidProbeStage = 2;
                Console.WriteLine("[commander-raid-probe] offline 39-bot presentation fixture; network disabled");
                break;
            case 2:
                if (now - _commanderRaidProbeAt < 3) return;
                _currentVantage = "commander-raid-ground";
                ArmGameplayDump();
                _commanderRaidProbeAt = now; _commanderRaidProbeStage = 3;
                break;
            case 3:
                if (now - _commanderRaidProbeAt < 3) return;
                _commanderRaidAirMap = true;
                _commanderRaidPage = 2;
                _partyTacticsGuid = 39;
                _commanderRaidProbeAt = now; _commanderRaidProbeStage = 4;
                break;
            case 4:
                if (now - _commanderRaidProbeAt < 2) return;
                _currentVantage = "commander-raid-air-last-page";
                ArmGameplayDump();
                _commanderRaidProbeAt = now; _commanderRaidProbeStage = 5;
                break;
            case 5:
                if (now - _commanderRaidProbeAt < 3) return;
                Console.WriteLine("[commander-raid-probe] rendered ground and air/last-page views; inspect screenshots. No live raid was run.");
                _quitRequested = true; _commanderRaidProbeStage = 99;
                break;
        }
    }

    private void DrawCommanderRaidProbe()
    {
        if (CommanderRaidProbeArmed && _commanderRaidProbeRoster is not null && _net is not { IsInWorld: true })
            DrawCommanderRaidPlanner(_commanderRaidProbeRoster);
    }
}
