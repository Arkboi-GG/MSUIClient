using System.Numerics;

namespace MSUIClient.Engine.UI;

/// <summary>
/// Frozen BinderConfirm.xml/ui_binder.rs policy. The bind prompt is a StaticPopup dialog:
/// its screen seat, size, text, buttons, and service-range lifetime belong here rather than
/// in the ImGui adapter.
/// </summary>
public static class BinderConfirmUiLaw
{
    public const float ServiceRange = NpcSessionUiLaw.ServiceRange;
    public const float PopupWidth = 320f;
    public const float TextWidth = 290f;
    public const float TextTop = 16f;
    public const float ButtonWidth = 128f;
    public const float ButtonHeight = 20f;
    public const float ButtonOneX = 26f;
    public const float ButtonTwoX = 167f;
    public const float PopupTop = 128f;
    public const uint BoundSoundId = 1141;
    public const string AcceptText = "Accept";
    public const string CancelText = "Cancel";
    public const string FallbackAreaName = "your inn";

    public readonly record struct ScreenRect(Vector2 Min, Vector2 Size);

    public static string Prompt(string? areaName) =>
        $"Do you want to make {ResolvedAreaName(areaName)} your new home?";

    public static string ResolvedAreaName(string? areaName) =>
        string.IsNullOrWhiteSpace(areaName) ? FallbackAreaName : areaName.Trim();

    public static string? PlayerBoundText(string? areaName) =>
        string.IsNullOrWhiteSpace(areaName) ? null : $"{areaName.Trim()} is now your home.";

    public static float PopupHeight(float textHeight) =>
        StaticPopupCoordinatorLaw.Height(textHeight, ButtonHeight);

    public static ScreenRect PopupRect(Vector2 displayPixels, float scale, float textHeight)
    {
        float width = PopupWidth * scale;
        float height = PopupHeight(textHeight) * scale;
        return new(new Vector2((displayPixels.X - width) * .5f, PopupTop * scale),
            new Vector2(width, height));
    }

    public static Vector2 TextCenter(float textLineCenterY) =>
        new(PopupWidth * .5f, TextTop + textLineCenterY);

    public static float ButtonTop(float textHeight) => 16f + Math.Max(0, textHeight) + 8f;

    public static Vector2 ButtonMin(int buttonIndex, float textHeight) => buttonIndex switch
    {
        1 => new(ButtonOneX, ButtonTop(textHeight)),
        2 => new(ButtonTwoX, ButtonTop(textHeight)),
        _ => throw new ArgumentOutOfRangeException(nameof(buttonIndex)),
    };

    public static bool ShouldRemainOpen(
        bool playerAvailable,
        bool binderAvailable,
        bool binderIsCreature,
        bool binderIsDead,
        float distance) =>
        playerAvailable && binderAvailable && binderIsCreature && !binderIsDead &&
        distance * distance <= NpcSessionUiLaw.ServiceRangeSquared;
}
