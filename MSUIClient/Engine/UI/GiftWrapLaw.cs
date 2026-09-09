using MSUIClient.Net;

namespace MSUIClient.Engine.UI;

public static class GiftWrapLaw
{
    // WrappedGift is server-only template data. A wrapper flag can arm the cursor;
    // Core validates the paper-to-gift mapping when the request arrives.
    public static bool IsPaper(ItemTemplate item, uint flags, ulong giftCreator) =>
        (item.Flags & InventoryUiLaw.ItemFlagWrapper) != 0 &&
        (flags & InventoryUiLaw.ItemDynamicWrapped) == 0 && giftCreator == 0;

    public static string? Refusal(bool sameItem, bool equipped, ulong giftCreator,
        ItemTemplate target, uint flags, bool casting)
    {
        if (sameItem) return "You can't wrap that item.";
        if (equipped) return "Equipped items can't be wrapped.";
        if (giftCreator != 0 || (flags & InventoryUiLaw.ItemDynamicWrapped) != 0)
            return "That item is already wrapped.";
        if (target.InventoryType == 18) return "Bags can't be wrapped.";
        if ((flags & 1) != 0) return "Soulbound items can't be wrapped.";
        if (target.Stackable != 1) return "Stackable items can't be wrapped.";
        if (target.MaxCount > 0) return "Unique items can't be wrapped.";
        if (casting) return "You can't wrap an item while casting.";
        return null;
    }
}
