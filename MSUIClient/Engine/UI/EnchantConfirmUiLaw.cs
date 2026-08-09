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

    // Existing MSUI presentation contract. Benilla's current shared StaticPopup implementation
    // accepts showAlert/exclusive but deliberately leaves both inert; that is not a reason to
    // erase MSUI's already-functional alert treatment or compact it to Benilla's 320x72 host.
    public const float FrameWidth = 360f;
    public const float FrameHeight = 96f;
    public const float FrameTop = 128f;
    public const float MessageCenterX = 212f;
    public const float MessageTop = 15f;
    public const float MessageWrapWidth = 260f;
    public const string BindMessage = "Enchanting this item will bind it to you.";
    public const string ReplaceMessageFormat = "Do you want to replace \"{0}\" with \"{1}\"?";

    public readonly record struct LogicalRect(float X, float Y, float Width, float Height);
    public static readonly LogicalRect AlertRect = new(12f, 8f, 64f, 64f);
    public static readonly LogicalRect AcceptButtonRect = new(62f, 68f, 128f, 20f);
    public static readonly LogicalRect DeclineButtonRect = new(198f, 68f, 128f, 20f);

    /// <summary>
    /// StaticPopup_OnShow/OnHide sound cardinality for the enchant-confirm slice. A bind answer
    /// can synchronously open REPLACE_ENCHANT before the original BIND_ENCHANT instance hides,
    /// so that transition is deliberately open-then-close rather than a silent in-place swap.
    /// </summary>
    public static IReadOnlyList<string> PopupSoundCues(bool wasOpen, bool willOpen,
        bool chainedPopup = false)
    {
        if (!wasOpen) return willOpen ? ["igMainMenuOpen"] : [];
        if (!willOpen) return ["igMainMenuClose"];
        return chainedPopup
            ? ["igMainMenuOpen", "igMainMenuClose"]
            : ["igMainMenuClose", "igMainMenuOpen"];
    }

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
