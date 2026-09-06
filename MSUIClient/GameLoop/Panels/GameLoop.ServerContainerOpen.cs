using MSUIClient.Engine.UI;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private readonly Dictionary<ulong, (ulong Actor, double Expires)> _serverContainerOpens = [];

    private void ApplyServerContainerOpen(byte[] body)
    {
        ulong guid = ObjectNoticePackets.ParseGuid(body, Op.SMSG_OPEN_CONTAINER);
        if (guid == 0 || ControlledGuid == 0) return;
        // The current core's auto-equip path sends this on the session socket,
        // even for a driven bot's new bag. Validate the addressed item against
        // the driven inventory after the following object/snapshot updates.
        _serverContainerOpens[guid] = (ControlledGuid, NowSeconds() + 3);
    }

    private void UpdateServerContainerOpens()
    {
        foreach (var (guid, pending) in _serverContainerOpens.ToArray())
        {
            if (pending.Actor != ControlledGuid || NowSeconds() >= pending.Expires)
            { _serverContainerOpens.Remove(guid); continue; }
            if (!_entities.TryGet(pending.Actor, out WorldEntity actor)) continue;
            int container = -1;
            if (guid == pending.Actor) container = 0;
            else if (_entities.TryGet(guid, out WorldEntity bag) && bag.Type == ObjectTypeId.Container)
            {
                for (int slot = 0; slot < 4; slot++)
                    if (actor.Fields.PlayerInventorySlot(19 + slot) == guid) container = slot + 1;
                if (_bankOpen)
                    for (int slot = 0; slot < InventoryUiLaw.BankBagCount; slot++)
                        if (actor.Fields.PlayerBankBagSlot(slot) == guid)
                            container = InventoryUiLaw.BankBagContainerFirst + slot;
            }
            if (container < 0) continue;
            SetBagWindowOpen(container, true);
            _serverContainerOpens.Remove(guid);
        }
    }
}
