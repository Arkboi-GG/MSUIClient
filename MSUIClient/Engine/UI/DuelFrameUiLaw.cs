using System.Numerics;
using MSUIClient.Formats;

namespace MSUIClient.Engine.UI;

/// <summary>Current 1.12 duel StaticPopup, countdown, outcome, and UnitPopup laws.</summary>
public static class DuelFrameUiLaw
{
    public const string RequestedPopupType = "DUEL_REQUESTED";
    public const string OutOfBoundsPopupType = "DUEL_OUTOFBOUNDS";
    public const string InviteSound = "igPlayerInvite";
    public const string AcceptText = "Accept";
    public const string DeclineText = "Decline";
    public const string RequestedSuffix = " has challenged you to a duel.";
    public const string CancelledLine = "Duel cancelled.";
    public const string OwnRequestLine = "You have requested a duel.";
    public const uint DuelSpellEffect = 83;
    public const float DuelDistanceSq = 100;
    public const float PopupWidth = 320;
    public const float PopupTextWidth = 290;
    public const float PopupTextTop = 16;
    public const float PopupButtonWidth = 128;
    public const float PopupButtonHeight = 20;
    public const float PopupButtonOneX = 26;
    public const float PopupButtonTwoX = 167;

    public static readonly StaticPopupCoordinatorLaw.Definition RequestedDefinition = new(
        RequestedPopupType, HideOnEscape: true, HasAccept: true, HasCancel: true,
        TimeoutSeconds: 60, EntrySound: InviteSound);

    public static readonly StaticPopupCoordinatorLaw.Definition OutOfBoundsDefinition = new(
        OutOfBoundsPopupType, UsesTimeoutText: true, TimeoutSeconds: 10);

    public static string RequestedText(string name) =>
        $"{(string.IsNullOrWhiteSpace(name) ? "Another player" : name.Trim())}{RequestedSuffix}";

    public static string CountdownLine(uint seconds) => $"Duel starting: {seconds}";

    public static string WinnerLine(bool fled, string winner, string loser) => fled
        ? $"{loser} has fled from {winner} in a duel"
        : $"{winner} has defeated {loser} in a duel";

    public static string OutOfBoundsText(double timeLeft)
    {
        int seconds = Math.Max(0, (int)Math.Ceiling(timeLeft));
        return $"Exiting duel area, you will forfeit in {seconds} " +
            (seconds == 1 ? "second." : "seconds.");
    }

    public static bool IsDuelSpell(in SpellInfo spell) =>
        spell.EffectIds is { Length: > 0 } && spell.EffectIds[0] == DuelSpellEffect;

    public static bool DuelRowEnabled(bool playerDead, bool targetDead, bool fullControl,
        float distanceSquared) => !playerDead && !targetDead && fullControl &&
        distanceSquared < DuelDistanceSq;

    public static float PopupHeight(float textHeight, bool buttons) =>
        StaticPopupCoordinatorLaw.Height(textHeight, buttons ? PopupButtonHeight : 0);

    public static float PopupButtonTop(float textHeight) =>
        PopupTextTop + Math.Max(0, textHeight) + 8;

    public static Vector2 PopupSize(float textHeight, bool buttons) =>
        new(PopupWidth, PopupHeight(textHeight, buttons));

    public static Vector2 TextLineCenter(int lineIndex) =>
        new(PopupWidth * .5f,
            PopupTextTop + (lineIndex + .5f) * GameText.LinePitch("GameFontHighlight", 1));

    public static (int Slot, StaticPopupCoordinatorLaw.Instance Instance)? Visible(
        StaticPopupCoordinatorLaw.Slots slots, string type)
    {
        if (slots.First is { } first &&
            string.Equals(first.Definition.Type, type, StringComparison.Ordinal))
            return (1, first);
        if (slots.Second is { } second &&
            string.Equals(second.Definition.Type, type, StringComparison.Ordinal))
            return (2, second);
        return null;
    }

    public static Vector2 ButtonMin(int buttonIndex, float textHeight) => buttonIndex switch
    {
        1 => new(PopupButtonOneX, PopupButtonTop(textHeight)),
        2 => new(PopupButtonTwoX, PopupButtonTop(textHeight)),
        _ => throw new ArgumentOutOfRangeException(nameof(buttonIndex)),
    };
}
