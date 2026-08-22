using System.Numerics;

namespace MSUIClient.Engine.UI;

public enum QuestNpcPanel { None, Greeting, Detail, Progress, Reward }
public enum QuestGreetingPool { Active, Available }
public enum QuestGreetingAction { Query, Complete }
public readonly record struct QuestCoin(int Denomination, uint Value);
public readonly record struct QuestLogicalRect(float X, float Y, float Width, float Height);
public readonly record struct QuestLogHeaderGroup(string Header, IReadOnlyList<int> QuestIndexes);

/// <summary>Authored QuestFrame.xml geometry and bounded panel behavior.</summary>
public static class QuestFrameUiLaw
{
    public const float Width = 384f;
    public const float Height = 512f;
    public const float ScrollX = 23f;
    public const float ScrollY = 81f;
    public const float ScrollWidth = 300f;
    public const float ScrollHeight = 334f;
    public const float ScrollStep = 20f;
    public const float ScrollTrackHeight = 302f;
    public const float ScrollThumbHeight = 16f;
    public const float ContentWidth = 300f;
    public const int MaxTitles = 10;
    public const int MaxItems = 6;
    public const float ItemWidth = 147f;
    public const float ItemHeight = 41f;
    public const float ItemIcon = 39f;
    public const int QuestLogRows = 6;
    public const float QuestLogListX = 19f;
    public const float QuestLogListY = 75f;
    public const float QuestLogListWidth = 300f;
    public const float QuestLogRowHeight = 16f;
    public const float QuestLogRowPitch = 15f;
    public static readonly QuestLogicalRect QuestLogDetailRect = new(20, 175, 305, 250);
    public static readonly QuestLogicalRect AbandonPopupRect = new(0, 128, 320, 72);
    public static readonly QuestLogicalRect AbandonPopupTextRect = new(15, 16, 290, 12);
    public static readonly QuestLogicalRect AbandonPopupAcceptRect = new(26, 36, 128, 20);
    public static readonly QuestLogicalRect AbandonPopupCancelRect = new(167, 36, 128, 20);
    public const int MaxQuestWatches = 5;
    public const double AutoQuestWatchSeconds = 300;
    public const int MaxQuestWatchLines = 30;
    public const float QuestWatchNominalWidth = 280f;
    public const float QuestWatchSpacerHeight = 13f;
    public const float QuestWatchInitialGap = 1f;
    public const float QuestWatchTitleGap = 5f;
    public const float QuestWatchObjectiveGap = 1f;

    public static Vector2 WindowOrigin(float scale) => new(0f, 104f * scale);
    public static Vector2 WindowSize(float scale) => new(Width * scale, Height * scale);
    public static Vector2 AbandonPopupOrigin(Vector2 display, float scale)
    {
        float width = AbandonPopupRect.Width * scale;
        return new((display.X - width) * .5f, AbandonPopupRect.Y * scale);
    }
    /// <summary>Managed right stack: tracker follows the minimap, then the shown armor guy.</summary>
    public static Vector2 QuestWatchTopRight(Vector2 display, float scale,
        bool durabilityShown = false) =>
        QuestWatchTopRight(display, scale, 0f, durabilityShown);
    public static Vector2 QuestWatchTopRight(Vector2 display, float scale,
        float questTimerHeight, bool durabilityShown) =>
        new(display.X, (192f + Math.Max(0f, questTimerHeight) +
            (durabilityShown ? DurabilityFrameUiLaw.Height : 0f)) * scale);
    public static float QuestWatchLineTop(float previousBottom, bool title, bool first) =>
        first ? QuestWatchSpacerHeight + QuestWatchInitialGap
            : previousBottom + (title ? QuestWatchTitleGap : QuestWatchObjectiveGap);
    public static uint AutoWatchEvictionCandidate(IReadOnlyDictionary<uint, double> expiries) =>
        expiries.Count == 0 ? 0 : expiries.MinBy(pair => pair.Value).Key;
    public static int ClampQuestLogOffset(int offset, int questCount) =>
        Math.Clamp(offset, 0, Math.Max(0, questCount - QuestLogRows));
    public static float ClampQuestLogDetailScroll(float offset, float contentHeight) =>
        Math.Clamp(offset, 0, Math.Max(0, contentHeight - QuestLogDetailRect.Height));
    public static Vector2 QuestLogRowMin(int visibleRow) =>
        new(QuestLogListX, QuestLogListY + Math.Clamp(visibleRow, 0, QuestLogRows - 1) * QuestLogRowPitch);
    public static Vector2 QuestLogFoldIconMin(int visibleRow) =>
        QuestLogRowMin(visibleRow) + new Vector2(3, 0);

    /// <summary>Benilla's current section model: ordinal header sort, descriptor order in group.</summary>
    public static IReadOnlyList<QuestLogHeaderGroup> GroupQuestLogHeaders(
        IReadOnlyList<string> questHeaders)
    {
        var groups = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (int i = 0; i < questHeaders.Count; i++)
        {
            string header = string.IsNullOrEmpty(questHeaders[i]) ? "Quests" : questHeaders[i];
            if (!groups.TryGetValue(header, out List<int>? indexes))
                groups[header] = indexes = [];
            indexes.Add(i);
        }
        return groups.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new QuestLogHeaderGroup(pair.Key, pair.Value))
            .ToArray();
    }

    /// <summary>Frozen SecondsToTime: strict day/hour thresholds and at most two units.</summary>
    public static string SecondsToTime(long seconds, bool noSeconds = false)
    {
        seconds = Math.Max(0, seconds);
        var parts = new List<string>(2);
        void Add(long value, string singular, string plural) =>
            parts.Add($"{value} {(value == 1 ? singular : plural)}");
        if (seconds > 86400)
        {
            long days = seconds / 86400;
            Add(days, "Day", "Days");
            seconds %= 86400;
        }
        if (seconds > 3600)
        {
            long hours = seconds / 3600;
            Add(hours, "Hr", "Hrs");
            seconds %= 3600;
        }
        if (parts.Count < 2 && seconds >= 60)
        {
            long minutes = seconds / 60;
            Add(minutes, "Min", "Mins");
            seconds %= 60;
        }
        if (parts.Count < 2 && seconds > 0 && !noSeconds)
            Add(seconds, "Sec", "Secs");
        return parts.Count == 0 ? "" : string.Join(' ', parts) + " ";
    }

    /// <summary>GetDifficultyColor: red/orange/yellow/green/grey with the shared grey band.</summary>
    public static Vector4 QuestDifficultyColor(uint playerLevel, uint questLevel)
    {
        long difference = (long)questLevel - playerLevel;
        if (difference >= 5) return new(1f, .1f, .1f, 1f);
        if (difference >= 3) return new(1f, .5f, .25f, 1f);
        if (difference >= -2) return new(1f, 1f, 0f, 1f);
        ReadOnlySpan<uint> bands =
            [4, 4, 5, 5, 6, 6, 7, 7, 8, 9, 10, 11, 12, 12, 12, 12, 12, 12, 12, 12];
        uint band = bands[(int)Math.Min(playerLevel / 5, (uint)bands.Length - 1)];
        return playerLevel - questLevel <= band
            ? new(.25f, .75f, .25f, 1f) : new(.5f, .5f, .5f, 1f);
    }

    public static Vector2 CloseMin => new(326, 15); // center at TOPRIGHT(-42,-31)
    public static Vector2 ItemGridOffset(int index) =>
        new((index & 1) == 0 ? 0 : ItemWidth + 1f, (index / 2) * (ItemHeight + 2f));
    public static float ClampScroll(float value, float contentHeight) =>
        Math.Clamp(value, 0, Math.Max(0, contentHeight - ScrollHeight));
    public static float ScrollThumbY(float value, float contentHeight)
    {
        float range = Math.Max(0, contentHeight - ScrollHeight);
        return range <= 0 ? 0 : value / range * (ScrollTrackHeight - ScrollThumbHeight);
    }
    public static bool RewardCompleteEnabled(int choiceCount, int selectedChoice) =>
        choiceCount == 0 || selectedChoice >= 0 && selectedChoice < choiceCount;

    /// <summary>The frozen client splits greeting and gossip rows solely by the wire icon.</summary>
    public static QuestGreetingPool GreetingPool(uint icon) => icon is 3 or 4
        ? QuestGreetingPool.Active : QuestGreetingPool.Available;

    /// <summary>Active rows always complete; available icon 0 is the one-click complete path.</summary>
    public static QuestGreetingAction GreetingAction(uint icon) =>
        GreetingPool(icon) == QuestGreetingPool.Active || icon == 0
            ? QuestGreetingAction.Complete : QuestGreetingAction.Query;

    public static IReadOnlyList<QuestCoin> Money(uint copper)
    {
        uint[] values = [copper / 10000, copper / 100 % 100, copper % 100];
        var result = new List<QuestCoin>(3);
        for (int denomination = 0; denomination < values.Length; denomination++)
            if (values[denomination] != 0) result.Add(new QuestCoin(denomination, values[denomination]));
        return result;
    }

    public static string InvalidReasonKey(uint reason) => reason switch
    {
        1 => "ERR_QUEST_FAILED_LOW_LEVEL",
        6 => "ERR_QUEST_FAILED_WRONG_RACE",
        12 => "ERR_QUEST_ONLY_ONE_TIMED",
        13 => "ERR_QUEST_ALREADY_ON",
        20 => "ERR_QUEST_FAILED_MISSING_ITEMS",
        22 => "ERR_QUEST_FAILED_NOT_ENOUGH_MONEY",
        _ => "ERR_QUEST_NEED_PREREQS",
    };

    public static string GiverFailureKey(uint reason) => reason switch
    {
        4 or 50 => "ERR_QUEST_FAILED_BAG_FULL_S",
        17 => "ERR_QUEST_FAILED_MAX_COUNT_S",
        _ => "ERR_QUEST_FAILED_S",
    };
}

/// <summary>Frozen world-state init semantics used by quest/NPC text macros.</summary>
public static class QuestWorldStateLaw
{
    public static void ApplyInit(IDictionary<uint, uint> target,
        IEnumerable<(uint Id, uint Value)> received)
    {
        // The frozen snapshot intentionally does not invent a table clear for the reference's
        // still-unresolved map/zone setup call. Every wire pair, including (0,0), is an ordinary
        // upsert and keys omitted by the new init survive.
        foreach ((uint id, uint value) in received) target[id] = value;
    }
}
