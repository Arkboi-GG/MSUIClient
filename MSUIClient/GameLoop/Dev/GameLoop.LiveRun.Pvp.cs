using MSUIClient.Engine.UI;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private bool RunLivePvpState(string line)
    {
        string[] p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (p.Length < 2 || !_entities.TryGet(ControlledGuid, out WorldEntity actor)) return false;
        bool flagged = (actor.Fields.UnitFlags & UnitFrameUiLaw.UnitFlagPvp) != 0;
        bool desired = (actor.Fields.PlayerFlags & 0x200u) != 0;
        Console.WriteLine($"[live-pvp] actor=0x{ControlledGuid:X};flagged={flagged};desired={desired};playerFlags=0x{actor.Fields.PlayerFlags:X8};unitFlags=0x{actor.Fields.UnitFlags:X8}");
        if (p[1] == "inspect") return true;
        if (p.Length != 3 || !bool.TryParse(p[2], out bool expected)) return false;
        if (p[1] == "assert-flag") return flagged == expected;
        if (p[1] == "assert-desired") return desired == expected;
        // The current Core toggle handler is session-owned. Do not use it as a
        // possessed-body fixture until that separate server route is corrected.
        if (p[1] == "desire" && ControlledGuid == LocalPlayerGuid)
            return desired == expected || TrySubmitClientSlashCommand("/pvp");
        return false;
    }
}
