using System.Numerics;

namespace MSUIClient.Engine.UI;

public static class AreaSpiritHealerUiLaw
{
    public const string PopupType = "AREA_SPIRIT_HEAL";
    public const uint WaitingAura = 2584;
    public static StaticPopupCoordinatorLaw.Definition Definition(uint remainingMilliseconds) => new(
        PopupType, WhileDead: true, HideOnEscape: true, HasAccept: true,
        HasOnShow: true, HasOnHide: true, TimeoutSeconds: remainingMilliseconds / 1000d);

    // The active shipped dialog has only button1=Cancel; the old two-button entry is commented out.
    public static Vector2 ButtonMin(float textHeight) => new(
        (StaticPopupCoordinatorLaw.BaseWidth - StaticPopupCoordinatorLaw.ButtonWidth) / 2,
        DuelFrameUiLaw.PopupButtonTop(textHeight));

    public static (int Amount, bool Minutes) Countdown(double seconds) => seconds < 60
        ? ((int)Math.Ceiling(Math.Max(0, seconds)), false)
        : ((int)Math.Ceiling(seconds / 60), true);
}
