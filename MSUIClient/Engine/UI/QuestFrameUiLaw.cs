using System.Numerics;

namespace MSUIClient.Engine.UI;

public enum QuestNpcPanel { None, Greeting, Detail, Progress, Reward }
public enum QuestGreetingPool { Active, Available }
public enum QuestGreetingAction { Query, Complete }
public readonly record struct QuestCoin(int Denomination, uint Value);

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
