using System.Numerics;

namespace MSUIClient.Engine.UI;

public enum DeathDialogKind
{
    None,
    Release,
    RecoverCorpse,
    RecoverCorpseInInstance,
    Resurrect,
    ResurrectNoSickness,
    ResurrectNoTimer,
    XpLoss,
    XpLossNoSickness,
}

/// <summary>
/// DeathFrame.xml's StaticPopup policy. This owns every death-window seat, size, child rectangle,
/// countdown string, sickness variant, and range threshold; the renderer supplies only live state.
/// </summary>
public static class DeathFrameUiLaw
{
    public const float PopupWidth = 320f;
    public const float AlertPopupWidth = 420f;
    public const float TextWidth = 290f;
    public const float PopupTop = 128f;
    public const float ButtonWidth = 128f;
    public const float ButtonHeight = 20f;
    public const float TextTop = 16f;
    public const float AlertIconSize = 64f;
    public const float AlertIconLeft = 12f;
    public const float CorpseRange = 40f;
    public const float CorpseRangeSquared = CorpseRange * CorpseRange;
    public const float SpiritHealerRange = 5.5556f;
    public const float SpiritHealerRangeSquared = SpiritHealerRange * SpiritHealerRange;
    public const double ReleaseWindowSeconds = 360;
    public const double ResurrectOfferSeconds = 60;
    public const string AlertIconPath = @"Interface\DialogFrame\DialogAlertIcon";
    public const string CorpseMarkerPath = @"Interface\Minimap\POIIcons";
    public const string CorpseTooltip = "Corpse";
    public const float WorldMapCorpseSize = 16f;
    public const float MinimapCorpseFraction = .11f;
    public const float MinimapCorpseAperture = .8f;
    public static readonly Vector2 CorpseUvMin = new(.875f, 0f);
    public static readonly Vector2 CorpseUvMax = new(1f, .125f);
    public const string ReleaseButton = "Release Spirit";
    public const string AcceptButton = "Accept";
    public const string DeclineButton = "Decline";
    public const string CancelButton = "Cancel";

    public readonly record struct ScreenRect(Vector2 Min, Vector2 Size);

    public static string Countdown(double seconds)
    {
        string[] pair = StaticPopupCoordinatorLaw.CountdownTextUnit(seconds).Split('|');
        return $"{pair[0]} {pair[1]}";
    }

    public static string ReleaseText(bool timerRunning, double remainingSeconds) => timerRunning
        ? $"{Countdown(remainingSeconds)} until release"
        : "You have died. Release to the nearest graveyard?";

    public static string RecoverText(double remainingSeconds) => remainingSeconds > 0
        ? $"{Countdown(remainingSeconds)} until resurrection"
        : "Resurrect now?";

    public static string ResurrectText(DeathDialogKind kind, string name,
        double recoverySeconds) => kind switch
    {
        DeathDialogKind.Resurrect when recoverySeconds > 0 =>
            $"{name} wants to resurrect you and will be able to in {Countdown(recoverySeconds)}.  " +
            "You will be afflicted with resurrection sickness.",
        DeathDialogKind.Resurrect =>
            $"{name} wants to resurrect you.  You will be afflicted with resurrection sickness.",
        DeathDialogKind.ResurrectNoSickness when recoverySeconds > 0 =>
            $"{name} wants to resurrect you and will be able to in {Countdown(recoverySeconds)}",
        _ => $"{name} wants to resurrect you",
    };

    public static string? SicknessDuration(uint level) => level switch
    {
        <= 10 => null,
        <= 19 => level - 10 == 1 ? "1 minute" : $"{level - 10} minutes",
        _ => "10 minutes",
    };

    public static string XpLossText(string? sicknessDuration, bool secondAsk)
    {
        if (secondAsk)
            return sicknessDuration is null
                ? "Remember, if you find your corpse there is no penalty.  Are you sure you want " +
                  "to have all your items take 25% durability damage?"
                : $"Remember, if you find your corpse there is no penalty.  Are you sure you want " +
                  $"to incur {sicknessDuration} of Resurrection Sickness and have all your items " +
                  "take 25% durability damage?";
        return sicknessDuration is null
            ? "If you find your corpse, you can resurrect for no penalty.  If I resurrect you all " +
              "of your items will take 25% durability damage (equipped and inventory)."
            : $"If you find your corpse, you can resurrect for no penalty.  If I resurrect you all " +
              $"of your items will take 25% durability damage (equipped and inventory) and you " +
              $"will be afflicted by {sicknessDuration} of Resurrection Sickness.";
    }

    public static ScreenRect PopupRect(Vector2 displayPixels, float scale, float textHeight,
        bool alert)
    {
        float logicalWidth = alert ? AlertPopupWidth : PopupWidth;
        float logicalHeight = StaticPopupCoordinatorLaw.Height(textHeight, ButtonHeight);
        Vector2 size = new Vector2(logicalWidth, logicalHeight) * scale;
        return new(new Vector2((displayPixels.X - size.X) * .5f, PopupTop * scale), size);
    }

    public static Vector2 TextCenter(float logicalWidth, float lineCenterY) =>
        new(logicalWidth * .5f, TextTop + lineCenterY);

    public static Vector2 AlertIconMin(float popupHeight) =>
        new(AlertIconLeft, (popupHeight - AlertIconSize) * .5f);

    public static Vector2 ButtonMin(int index, int buttonCount, float logicalWidth,
        float textHeight) => (index, buttonCount) switch
    {
        (1, 1) => new((logicalWidth - ButtonWidth) * .5f, 24f + textHeight),
        (1, 2) => new(logicalWidth * .5f - ButtonWidth - 6f, 24f + textHeight),
        (2, 2) => new(logicalWidth * .5f + 7f, 24f + textHeight),
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    public static bool AcceptEnabled(DeathDialogKind kind, double recoverySeconds) =>
        kind is not (DeathDialogKind.RecoverCorpse or DeathDialogKind.Resurrect or
            DeathDialogKind.ResurrectNoSickness) || recoverySeconds <= 0;

    public static bool HideOnEscape(DeathDialogKind kind) => kind is
        DeathDialogKind.Resurrect or DeathDialogKind.ResurrectNoSickness or
        DeathDialogKind.ResurrectNoTimer or DeathDialogKind.XpLoss or
        DeathDialogKind.XpLossNoSickness;

    public static bool TryWorldMapFraction(int displayMap, uint viewedMap, Vector3 position,
        float left, float right, float top, float bottom, out Vector2 fraction)
    {
        fraction = default;
        if (displayMap < 0 || (uint)displayMap != viewedMap ||
            right == left || bottom == top) return false;
        fraction = new((position.Y - left) / (right - left),
            (position.X - top) / (bottom - top));
        return fraction.X is >= 0f and <= 1f && fraction.Y is >= 0f and <= 1f;
    }

    public static bool TryMinimapCorpseRect(int displayMap, uint currentMap,
        Vector3 player, Vector3 corpse, Vector2 mapMin, Vector2 mapMax, float radiusYards,
        out ScreenRect rect)
    {
        rect = default;
        if (displayMap < 0 || (uint)displayMap != currentMap || radiusYards <= 0) return false;
        Vector2 size = mapMax - mapMin;
        Vector2 center = (mapMin + mapMax) * .5f;
        float pixelsPerYard = size.X / (radiusYards * 2f);
        Vector2 point = center + new Vector2(
            -(corpse.Y - player.Y), -(corpse.X - player.X)) * pixelsPerYard;
        float aperture = size.X * .5f * MinimapCorpseAperture;
        if (Vector2.DistanceSquared(point, center) > aperture * aperture) return false;
        Vector2 markerSize = new(size.X * MinimapCorpseFraction);
        rect = new(point - markerSize * .5f, markerSize);
        return true;
    }
}
