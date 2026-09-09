using System.Numerics;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private readonly record struct GroundItemUse(ulong Actor, ulong Item, uint Entry, uint Spell);
    private GroundItemUse? _groundItemUse;

    private void CancelGroundTargeting()
    {
        _groundCastSpell = 0;
        _groundCursorPoint = null;
        _groundItemUse = null;
    }

    // A cursor owns an item instance, not a bag coordinate. Resolve that same copy again
    // when the point is chosen; inventory moves and possession cannot redirect consumption.
    private bool TryCommitGroundItemUse(uint spellId, Vector3 destination)
    {
        if (_groundItemUse is not { } intent) return false;
        CancelGroundTargeting();
        if (!CanAuthorControlledOrSelf || ControlledGuid != intent.Actor || spellId != intent.Spell ||
            !float.IsFinite(destination.X) || !float.IsFinite(destination.Y) || !float.IsFinite(destination.Z) ||
            !_entities.TryGet(ControlledGuid, out WorldEntity actor) ||
            _items?.TryGet(intent.Entry, out ItemTemplate? template) != true || template is null)
            return true;
        foreach (var copy in EnumerateActionItemCopies(actor, intent.Entry))
        {
            if (copy.Item.Guid != intent.Item) continue;
            SendItemUse(copy.Bag, copy.Slot, copy.Item, template, destination);
            return true;
        }
        ShowSpellError(spellId, "LOCAL_ITEM_MISSING", "That item is no longer available.", "LOCAL_GATE");
        return true;
    }
}
