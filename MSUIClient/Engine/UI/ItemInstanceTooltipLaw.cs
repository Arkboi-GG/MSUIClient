using MSUIClient.Net;
using MSUIClient.Formats;

namespace MSUIClient.Engine.UI;

public static class ItemInstanceTooltipLaw
{
    public const uint Bound = 0x1; // ITEM_DYNFLAG_BOUND, ItemDefines.h

    public static string? Binding(uint bonding, uint instanceFlags) => (instanceFlags & Bound) != 0
        ? "Soulbound"
        : bonding switch
        {
            1 => "Binds when picked up", 2 => "Binds when equipped", 3 => "Binds when used",
            4 or 5 => "Quest Item", _ => null,
        };

    // A tooltip-only descriptor carrier: GUID zero, never inserted into EntityStore or used
    // for gameplay/ownership. Remote packets expose first-slot charges and permanent enchant;
    // the three random-property enchants use the same DBC slots as Item::SetItemRandomProperties.
    public static WorldEntity Remote(int property, uint enchant, int? charges, bool wrapped,
        ItemRandomPropertyCatalog? properties)
    {
        var item = new WorldEntity { Type = ObjectTypeId.Item };
        item.Fields.SetU32(ObjectFields.ITEM_RANDOM_PROPERTIES, unchecked((uint)property));
        item.Fields.SetU32(ObjectFields.ITEM_FLAGS, wrapped ? InventoryUiLaw.ItemDynamicWrapped : 0);
        if (wrapped) return item;
        item.Fields.SetU32(ObjectFields.ITEM_FIELD_ENCHANTMENT, enchant);
        if (charges is int count) item.Fields.SetU32(ObjectFields.ITEM_SPELL_CHARGES, unchecked((uint)count));
        if (properties?.TryGet(property, out var row) == true)
            for (int slot = 0; slot < row.Enchantments.Length; slot++)
                item.Fields.SetU32((ushort)(ObjectFields.ITEM_FIELD_ENCHANTMENT + (slot + 3) * 3), row.Enchantments[slot]);
        return item;
    }

    public static int? Charges(ItemTemplate template, ObjectFields? instance)
    {
        for (int slot = 0; slot < template.Spells.Length; slot++)
        {
            ItemSpellTemplate spell = template.Spells[slot];
            if (spell.SpellId == 0 || spell.Charges is 0 or -1) continue;
            // The signed instance value belongs to this exact spell slot. A missing live
            // field is unknown, not proof that the item still has its template maximum.
            int? value = instance is null ? spell.Charges : instance.ItemSpellCharges(slot);
            return value is int known ? (int)Math.Min(int.MaxValue, Math.Abs((long)known)) : null;
        }
        return null;
    }
}
