using MSUIClient.Engine.UI;
using MSUIClient.Formats;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private readonly Dictionary<ulong, Dictionary<uint, uint>> _companionItemProficiencies = [];
    private static readonly IReadOnlyDictionary<uint, uint> EmptyItemProficiencies = new Dictionary<uint, uint>();

    private IReadOnlyDictionary<uint, uint> ItemProficienciesFor(ulong owner) =>
        owner == LocalPlayerGuid ? _itemProficiencies :
        _companionItemProficiencies.TryGetValue(owner, out var masks) ? masks : EmptyItemProficiencies;

    private void ApplyItemProficiency(byte[] body, ulong owner)
    {
        ProficiencyPacket packet = ProficiencyPackets.Parse(body);
        if (owner == 0 || owner != LocalPlayerGuid && owner != ControlledGuid) return;
        Dictionary<uint, uint> masks;
        if (owner == LocalPlayerGuid) masks = _itemProficiencies;
        else if (!_companionItemProficiencies.TryGetValue(owner, out masks!))
            _companionItemProficiencies[owner] = masks = [];
        // This is the complete current mask for one class, including removal and an empty mask.
        masks[packet.ItemClass] = packet.SubclassMask;
    }

    private InventoryUiLaw.ProficiencyColors ItemOwnerProficiencyColors(
        ItemTemplate item, ulong owner, uint? alternative)
    {
        if (owner == 0) owner = ControlledGuid;
        var masks = ItemProficienciesFor(owner);
        if (owner != LocalPlayerGuid && masks.Count == 0) return default;
        bool dual = _spellCatalog is not null && ActionsFor(owner).KnownSpells.Any(id =>
            _spellCatalog.TryGet(id, out SpellInfo spell) && spell.EffectIds?.FirstOrDefault() == 40);
        return InventoryUiLaw.ItemProficiencyColors(item.Class, item.Subclass, item.InventoryType,
            masks, alternative, dual);
    }
}
