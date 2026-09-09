using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private void RunLiveCompanionStep(string line)
    {
        string[] args = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        string verb = args.Length > 1 ? args[1].ToLowerInvariant() : "";
        CompanionRow row = args.Length > 2 ? _companionRows.FirstOrDefault(x =>
            x.Name.Equals(args[2], StringComparison.OrdinalIgnoreCase)) : default;
        bool pass = false;
        switch (verb)
        {
            case "list": pass = RequestCompanionList(); break;
            case "open": OpenCompanionsPanel(); pass = _companionsOpen; break;
            case "inspect":
                foreach (CompanionRow member in _companionRows)
                    Log(true, $"companion-row name={member.Name};guid=0x{member.Guid:X16};" +
                        $"level={member.Level};state={member.State};inWorld={_entities.TryGet(member.Guid, out _)}");
                pass = _companionsEverListed;
                break;
            case "summon": pass = row.Summonable && RequestCompanionSummon(row.Guid); break;
            case "dismiss": pass = row.IsCompanion && RequestCompanionDismiss(row.Guid); break;
            case "possess":
                if (row.IsCompanion) { CommitSelection(row.Guid, false); RequestPossess(row.Guid); pass = true; }
                break;
            case "assert-controlled": pass = row.Guid != 0 && ControlledGuid == row.Guid; break;
            case "assert-summoned": pass = row.IsCompanion && _entities.TryGet(row.Guid, out _); break;
            case "assert-offline": pass = row.Guid != 0 && row.Summonable; break;
            case "assert-status-summoned":
                pass = row.IsCompanion && !_companionsStatusIsError &&
                    _companionsStatus == $"{row.Name} summoned.";
                break;
            case "attack":
                ulong[] subjects = _companionRows.Where(x => x.IsCompanion &&
                    _entities.TryGet(x.Guid, out _)).Select(x => x.Guid).ToArray();
                pass = subjects.Length > 0 && _selectionGuid != 0 &&
                    TrySendLiveSuiOrder(1, subjects, _selectionGuid, 0, 0, 0);
                break;
            case "cleanup-legacy-bot":
                // Only used to undo an explicitly named mistaken test bot. Never
                // creates a VMaNGOS partybot or falls back to another selection.
                ulong legacy = args.Length > 2 ? KnownPlayerGuid(args[2]) : 0;
                if (legacy != 0 && legacy != LocalPlayerGuid && _entities.TryGet(legacy, out var unit) && unit.IsPlayer)
                {
                    CommitSelection(legacy, false);
                    pass = SendGmCommand(".partybot remove", "explicit-test-bot-cleanup");
                }
                else { Log(true, $"{line} not-present-in-world"); return; }
                break;
        }
        Log(pass, $"{line} guid=0x{row.Guid:X16};state={row.State};actor=0x{ControlledGuid:X16};" +
            "source=production-companion-handler;serverReplyRequired=true");
    }
}
