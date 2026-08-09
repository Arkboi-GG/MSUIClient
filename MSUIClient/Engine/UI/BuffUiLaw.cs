using System.Numerics;

namespace MSUIClient.Engine.UI;

/// <summary>Build-5875 BuffFrame.xml geometry and runtime presentation rules.</summary>
public static class BuffUiLaw
{
    public readonly record struct AuraKey(byte Slot, uint SpellId);

    // MSUI deliberately keeps its pre-port horizontal seat under the minimap.  The button
    // geometry below is the shared 1.12 contract: 30px icons, eight columns, and a 15px
    // duration gutter between rows.  Keeping this in one law prevents the draw, hit boxes,
    // telemetry, and deterministic checks from drifting independently.
    public const float FrameWidth = 50f;
    public const float FrameHeight = 50f;
    public const float FrameRightInset = 205f;
    public const float FrameTopInset = 13f;
    public const float ButtonSize = 30f;
    public const float ColumnStep = 35f;
    public const float RowStep = 45f;
    public const float DurationGutter = 15f;
    public const float DurationTextHeight = 10f;
    public const int Columns = 8;
    public const int HelpfulLimit = 16;
    public const int HarmfulLimit = 8;
    public const float DebuffBorderWidth = 33f;
    public const float DebuffBorderHeight = 32f;
    public const float DebuffBorderExpandX = 1.5f;
    public const float DebuffBorderExpandY = 1f;
    public const float AuraWindowWidth = 290f;
    public const float AuraWindowHeight = 150f;
    public const float AuraWindowLeftOfFrame = 230f;
    public const float AuraWindowAboveFrame = 5f;
    public const double DurationSlackSeconds = 1d;
    public static readonly Vector4 DebuffTexCoords =
        new(.296875f, 0f, .5703125f, .515625f);

    public const double WarningSeconds = 31d;
    public const double FlashPeriodSeconds = 0.75d;
    public const float FlashMinimumAlpha = 0.3f;

    public static float WarningAlpha(double nowSeconds, double remainingSeconds)
    {
        if (remainingSeconds >= WarningSeconds) return 1f;
        double phase = Math.Abs(nowSeconds % (FlashPeriodSeconds * 2d));
        double ramp = phase <= FlashPeriodSeconds
            ? phase / FlashPeriodSeconds
            : 2d - phase / FlashPeriodSeconds;
        return FlashMinimumAlpha + (1f - FlashMinimumAlpha) * (float)ramp;
    }

    /// <summary>Spell.dbc Dispel mapped through FrameXML's DebuffTypeColor table.</summary>
    public static Vector4 DebuffColor(uint dispelType) => dispelType switch
    {
        1 => new(.2f, .6f, 1f, 1f),
        2 => new(.6f, 0f, 1f, 1f),
        3 => new(.6f, .4f, 0f, 1f),
        4 => new(0f, .6f, 0f, 1f),
        _ => new(.8f, 0f, 0f, 1f),
    };

    public static Vector2 FrameMin(Vector2 logicalDisplay) =>
        new(logicalDisplay.X - FrameRightInset - FrameWidth, FrameTopInset);

    public static Vector2 AuraWindowMin(Vector2 frameMin) =>
        frameMin - new Vector2(AuraWindowLeftOfFrame, AuraWindowAboveFrame);

    public static Vector2 ButtonMin(Vector2 frameMin, bool harmful, int cohort)
    {
        cohort = Math.Max(0, cohort);
        int col = cohort % Columns;
        int row = harmful ? 2 : Math.Clamp(cohort / Columns, 0, 1);
        return frameMin + new Vector2(FrameWidth - ButtonSize - col * ColumnStep,
            row * RowStep);
    }

    public static bool WithinAuraWindow(Vector2 frameMin, Vector2 min, Vector2 max)
    {
        Vector2 windowMin = AuraWindowMin(frameMin);
        Vector2 windowMax = windowMin + new Vector2(AuraWindowWidth, AuraWindowHeight);
        return min.X >= windowMin.X && min.Y >= windowMin.Y &&
               max.X <= windowMax.X && max.Y <= windowMax.Y;
    }

    /// <summary>
    /// The duration packet is slot-keyed and may lead its descriptor by one frame.  Accept it
    /// only when it is no more than the measured slack older than the aura appearance; this
    /// preserves fresh pre-descriptor stamps while rejecting a recycled slot's stale timer.
    /// </summary>
    public static bool DurationBelongsToAura(double receivedAt, double appearedAt) =>
        receivedAt >= appearedAt - DurationSlackSeconds;

    public static bool PreserveAcrossWorldEnter(ulong ownerGuid, ulong enteringGuid) =>
        enteringGuid != 0 && ownerGuid == enteringGuid;

    /// <summary>Reference player-bar cache law: survivors retain insertion order, removals
    /// repack, and newly occupied slots append in ascending raw-slot order.</summary>
    public static AuraKey[] ReconcileOrder(IEnumerable<AuraKey> existing,
        IEnumerable<AuraKey> live)
    {
        AuraKey[] liveOrder = live.OrderBy(a => a.Slot).ToArray();
        HashSet<AuraKey> liveSet = liveOrder.ToHashSet();
        var result = existing.Where(liveSet.Contains).ToList();
        HashSet<AuraKey> seen = result.ToHashSet();
        foreach (AuraKey aura in liveOrder)
            if (seen.Add(aura)) result.Add(aura);
        return result.ToArray();
    }
}
