namespace MSUIClient;

public sealed partial class GameLoop
{
    private bool RunLiveControlAudit(string line)
    {
        string[] p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (p.Length == 3 && p[1] == "portal" && int.TryParse(p[2], out int portalId))
        {
            var portal = _areaTriggers?.All.FirstOrDefault(x => x.Id == portalId);
            if (portal is null) return false;
            Console.WriteLine(FormattableString.Invariant(
                $"[live-portal-row] id={portal.Id};map={portal.MapId};position=({portal.X:R},{portal.Y:R},{portal.Z:R});radius={portal.Radius:R};box={portal.BoxLength:R},{portal.BoxWidth:R},{portal.BoxHeight:R};yaw={portal.BoxYaw:R}"));
            return true;
        }
        if (p.Length == 2 && p[1] == "inspect")
        {
            Console.WriteLine($"[live-control-state] actor=0x{ControlledGuid:X};freeView={_freeView};freeze={TacticalFreezeBlocksLiveCommands};ownedLock={OwnedActiveTacticalLock?.LockId ?? 0}");
            foreach (PartyMember member in _partyMembers)
                Console.WriteLine($"[live-chain-state] name={member.Name};guid=0x{member.Guid:X};state={PartyChainState(member)};anchor=0x{PartyChainAnchor(member):X}");
            return true;
        }
        if (p.Length == 2 && p[1] == "view") { ToggleFreeView(); return true; }
        if (p.Length == 2 && p[1] == "freeze")
        {
            if (!_freeView) return false;
            RequestTacticalFreezeToggle();
            return true;
        }
        if (p.Length == 3 && p[1] is "link" or "unlink" or "follow")
        {
            PartyMember? member = _partyMembers.FirstOrDefault(x => x.Name.Equals(p[2], StringComparison.OrdinalIgnoreCase));
            if (member is null || TacticalFreezeBlocksLiveCommands) return false;
            if (p[1] == "follow") SetPartyChainAnchor(member, ControlledGuid);
            else SetPartyLink(member, p[1] == "link");
            return true;
        }
        return false;
    }
}
