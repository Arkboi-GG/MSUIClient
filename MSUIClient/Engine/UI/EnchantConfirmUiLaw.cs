using MSUIClient.Formats;

namespace MSUIClient.Engine.UI;

public enum EnchantBindKind { Bind, Refuse, ConfirmBind, ConfirmReplace }

public readonly record struct EnchantBindVerdict(
    EnchantBindKind Kind, uint ExistingEnchant = 0, uint NewEnchant = 0);

public readonly record struct EnchantClickedItem(
    uint Class, uint Subclass, uint InventoryType, bool AlreadyBound,
    uint PermanentEnchant = 0, uint TemporaryEnchant = 0);

/// <summary>
/// Pure transcription of the build-5875 item-target enchant gate at 0x495d60.
/// It deliberately does not send packets or clear targeting: every non-Bind exit occurs before
/// BindTarget in the reference, so refusals and dismissed confirmations leave the cursor armed.
/// </summary>
public static class EnchantConfirmUiLaw
{
    public const uint EnchantPermanentEffect = 53;
    public const uint EnchantTemporaryEffect = 54;

    public static EnchantBindVerdict Decide(in SpellInfo spell, in EnchantClickedItem item,
        EnchantCatalog? enchants, bool bindConfirmed)
    {
        uint[] effects = spell.EffectIds ?? [];
        int[] misc = spell.EffectMiscValues ?? [];
        int count = Math.Min(3, effects.Length);
        for (int i = 0; i < count; i++)
        {
            uint effect = effects[i];
            if (effect is not (EnchantPermanentEffect or EnchantTemporaryEffect)) continue;

            if (spell.EquippedItemSubclassMask != 0)
            {
                bool classOk = item.Class == unchecked((uint)spell.EquippedItemClass);
                bool subclassOk = item.Subclass < 32 &&
                    (spell.EquippedItemSubclassMask & (1u << (int)item.Subclass)) != 0;
                if (!classOk || !subclassOk) return new(EnchantBindKind.Refuse);
            }
            if (spell.EquippedItemInventoryTypeMask != 0 &&
                (item.InventoryType >= 32 ||
                 (spell.EquippedItemInventoryTypeMask & (1u << (int)item.InventoryType)) == 0))
                return new(EnchantBindKind.Refuse);

            uint newId = i < misc.Length && misc[i] > 0 ? (uint)misc[i] : 0;
            if (newId == 0 || enchants?.TryGet(newId, out EnchantInfo fresh) != true) continue;

            if (fresh.BindsItem && !item.AlreadyBound && !bindConfirmed && item.InventoryType != 0)
                return new(EnchantBindKind.ConfirmBind, NewEnchant: newId);

            uint oldId = effect == EnchantPermanentEffect
                ? item.PermanentEnchant : item.TemporaryEnchant;
            if (oldId != 0 && enchants.TryGet(oldId, out _))
                return new(EnchantBindKind.ConfirmReplace, oldId, newId);
        }
        return new(EnchantBindKind.Bind);
    }
}
