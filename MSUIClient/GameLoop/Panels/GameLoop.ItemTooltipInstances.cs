using MSUIClient.Engine.UI;
using MSUIClient.Formats;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private ItemRandomPropertyCatalog? _itemRandomProperties;

    private WorldEntity RemoteTooltipInstance(int property, uint enchant = 0, int? charges = null,
        bool wrapped = false) => ItemInstanceTooltipLaw.Remote(property, enchant, charges, wrapped, _itemRandomProperties);

    private string ItemTooltipInstanceName(ItemTemplate item, WorldEntity? instance) =>
        instance is not null && (instance.Fields.ItemFlags & InventoryUiLaw.ItemDynamicWrapped) == 0
            ? _itemRandomProperties?.ItemName(item.Name, instance.Fields.ItemRandomProperty) ?? item.Name : item.Name;
}
