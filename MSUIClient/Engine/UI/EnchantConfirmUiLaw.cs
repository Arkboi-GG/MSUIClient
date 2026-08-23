using System.Numerics;
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
    public const string BindPopupType = "BIND_ENCHANT";
    public const string ReplacePopupType = "REPLACE_ENCHANT";
    public const uint EnchantPermanentEffect = 53;
    public const uint EnchantTemporaryEffect = 54;

    // Both entries set showAlert: current Benilla's shared popup widens from 320 to 420 and
    // shows the 64px alert icon. Height remains content-driven with a 72px one-line floor.
    public const float FrameWidth = StaticPopupCoordinatorLaw.WideDialogWidth;
    public const float FrameHeight = StaticPopupCoordinatorLaw.BaseHeight;
    public const float FrameTop = StaticPopupCoordinatorLaw.ScreenTop;
    public const float MessageCenterX = FrameWidth * .5f;
    public const float MessageTop = StaticPopupCoordinatorLaw.TextTop;
    public const float MessageWrapWidth = StaticPopupCoordinatorLaw.TextWidth;
    public const string BindMessage = "Enchanting this item will bind it to you.";
    public const string ReplaceMessageFormat = "Do you want to replace \"{0}\" with \"{1}\"?";

    public readonly record struct LogicalRect(float X, float Y, float Width, float Height)
    {
        public Vector2 Min => new(X, Y);
        public Vector2 Size => new(Width, Height);
    }

    public readonly record struct ScreenRect(Vector2 Min, Vector2 Size)
    {
        public Vector2 Max => Min + Size;
    }

    public static readonly Vector2 ButtonUvMin = Vector2.Zero;
    public static readonly Vector2 ButtonUvMax = new(1f, .625f);
    public static readonly Vector2 PlainButtonSize = new(128f, 20f);
    public static readonly Vector2 BackdropFillInset = new(11f, 12f);
    public static readonly Vector2 BackdropFillFarInset = new(12f, 11f);

    public readonly record struct PopupLayout(
        float Width, float Height, LogicalRect Text, LogicalRect Alert,
        LogicalRect AcceptButton, LogicalRect DeclineButton)
    {
        public Vector2 Size => new(Width, Height);
    }

    public static readonly StaticPopupCoordinatorLaw.Definition BindDefinition = new(
        BindPopupType, HideOnEscape: true, HasAccept: true,
        ShowAlert: true);

    public static readonly StaticPopupCoordinatorLaw.Definition ReplaceDefinition = new(
        ReplacePopupType, HideOnEscape: true, HasAccept: true,
        ShowAlert: true);

    public static PopupLayout Layout(float textHeight)
    {
        float safeTextHeight = Math.Max(0, textHeight);
        float height = Math.Max(FrameHeight, StaticPopupCoordinatorLaw.Height(
            safeTextHeight, StaticPopupCoordinatorLaw.ButtonHeight));
        float buttonTop = MessageTop + safeTextHeight + 8;
        return new(FrameWidth, height,
            new((FrameWidth - MessageWrapWidth) * .5f, MessageTop,
                MessageWrapWidth, safeTextHeight),
            new(12, (height - 64) * .5f, 64, 64),
            new(FrameWidth * .5f - 134, buttonTop, 128, 20),
            new(FrameWidth * .5f + 7, buttonTop, 128, 20));
    }

    public static (int Slot, StaticPopupCoordinatorLaw.Instance Instance)? Visible(
        StaticPopupCoordinatorLaw.Slots slots)
    {
        for (int slot = 1; slot <= StaticPopupCoordinatorLaw.SlotCount; slot++)
        {
            StaticPopupCoordinatorLaw.Instance? instance = slot == 1 ? slots.First : slots.Second;
            if (instance is { } visible &&
                visible.Definition.Type is BindPopupType or ReplacePopupType)
                return (slot, visible);
        }
        return null;
    }

    public static ScreenRect ScaledRect(Vector2 origin, LogicalRect logical, float scale) =>
        new(origin + logical.Min * scale, logical.Size * scale);

    public static ScreenRect ScaledFrame(Vector2 origin, PopupLayout layout, float scale) =>
        new(origin, layout.Size * scale);

    public static Vector4 ClipRect(ScreenRect rect) =>
        new(rect.Min.X, rect.Min.Y, rect.Max.X, rect.Max.Y);

    public static ScreenRect BackdropFillRect(ScreenRect frame, float backdropScale)
    {
        Vector2 min = frame.Min + BackdropFillInset * backdropScale;
        Vector2 max = frame.Max - BackdropFillFarInset * backdropScale;
        return new(min, Vector2.Max(Vector2.Zero, max - min));
    }

    public static Vector2 MessageLineCenter(Vector2 origin, float scale,
        float pitch, int lineIndex) =>
        new(origin.X + MessageCenterX * scale,
            origin.Y + MessageTop * scale + pitch * (lineIndex + .5f));

    public static Vector2 MeasuredSize(float width, float height) => new(width, height);

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
