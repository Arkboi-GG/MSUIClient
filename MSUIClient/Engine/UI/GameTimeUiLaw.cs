using System.Numerics;

namespace MSUIClient.Engine.UI;

/// <summary>Frozen 1.12 GameTimeFrame geometry, clock, and sun/moon atlas laws.</summary>
public static class GameTimeUiLaw
{
    public readonly record struct ScreenRect(Vector2 Min, Vector2 Size);
    public readonly record struct TooltipSeat(Vector2 Anchor, Vector2 Pivot);
    public const float Size = 50f;
    public const int DawnMinute = 5 * 60 + 30;
    public const int DuskMinute = 21 * 60;
    public static readonly Vector2 DayUvMin = new(0f, 0f);
    public static readonly Vector2 DayUvMax = new(50f / 128f, 50f / 64f);
    public static readonly Vector2 NightUvMin = new(.5f, 0f);
    public static readonly Vector2 NightUvMax = new(.5f + 50f / 128f, 50f / 64f);

    // TOPRIGHT of MinimapCluster, offset (+4,-19) in FrameXML's y-up coordinates.
    public static Vector2 FrameOrigin(Vector2 display, float scale) =>
        new(display.X - 46f * scale, 19f * scale);

    public static QuestLogicalRect HitRect => new(6f, 5f, 44f, 35f);

    public static Vector2 FrameSize(float scale) => Vector2.One * (Size * scale);

    // GameTime.xml: GameTooltip:SetOwner(this, "ANCHOR_BOTTOMLEFT"). Benilla maps the
    // tooltip's TOPRIGHT to the owner's BOTTOMLEFT.
    public static TooltipSeat BottomLeftTooltipSeat(Vector2 frameOrigin, float scale) =>
        new(frameOrigin + new Vector2(0, Size * scale), Vector2.UnitX);

    public static ScreenRect HitScreen(Vector2 origin, float scale)
    {
        QuestLogicalRect hit = HitRect;
        return new(new(origin.X + hit.X * scale, origin.Y + hit.Y * scale),
            new(hit.Width * scale, hit.Height * scale));
    }

    public static (int Hour, int Minute) TimeParts(float hours)
    {
        float wrapped = hours % 24f;
        if (wrapped < 0f) wrapped += 24f;
        int total = Math.Clamp((int)MathF.Floor(wrapped * 60f), 0, 1439);
        return (total / 60, total % 60);
    }

    public static bool IsNight(int hour, int minute)
    {
        int minuteOfDay = Math.Clamp(hour, 0, 23) * 60 + Math.Clamp(minute, 0, 59);
        return minuteOfDay < DawnMinute || minuteOfDay >= DuskMinute;
    }

    public static string ClockText(int hour, int minute, bool twentyFourHour = true)
    {
        hour = Math.Clamp(hour, 0, 23);
        minute = Math.Clamp(minute, 0, 59);
        if (twentyFourHour) return $"{hour}:{minute:D2}";
        string suffix = hour >= 12 ? "PM" : "AM";
        int twelveHour = hour % 12;
        if (twelveHour == 0) twelveHour = 12;
        return $"{twelveHour}:{minute:D2} {suffix}";
    }
}
