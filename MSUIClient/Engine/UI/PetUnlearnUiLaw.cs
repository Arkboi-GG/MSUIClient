using System.Numerics;
namespace MSUIClient.Engine.UI;

public static class PetUnlearnUiLaw
{
    public const string PopupType = "CONFIRM_PET_UNLEARN";
    public const string Prompt = "Do you want to unlearn all of your pet's skills? The cost will increase each time you do it.";
    public static readonly StaticPopupCoordinatorLaw.Definition Definition = new(
        PopupType, HideOnEscape: true, HasAccept: true, HasCancel: true, HasOnHide: true);
    // Shipped StaticPopup.lua: money shifts the buttons down 16; XML centers coins
    // five pixels below the text. The regular bottom margin remains intact.
    public const float MoneyExtraHeight = 16;
    public static Vector2 MoneyCenterTop(float textHeight) =>
        new(DuelFrameUiLaw.PopupWidth * .5f, DuelFrameUiLaw.PopupTextTop + textHeight + 5);
}
