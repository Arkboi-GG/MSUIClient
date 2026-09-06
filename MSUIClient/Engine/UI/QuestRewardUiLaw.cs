using MSUIClient.Net;

namespace MSUIClient.Engine.UI;

public static class QuestRewardUiLaw
{
    public static uint RequiredMoney(QuestTemplate quest) => quest.Money < 0 ? (uint)-(long)quest.Money : 0;

    public static uint RewardMoney(QuestTemplate quest, uint playerLevel, uint maximumLevel = 60, float moneyRate = 1f)
    {
        if ((quest.Flags & 0x200) != 0) return 0; // hidden rewards remain hidden in the log
        long baseReward = Math.Max(0, quest.Money); // already scaled by Core in the query
        float rate = float.IsFinite(moneyRate) && moneyRate >= 0 ? moneyRate : 1f;
        double bonus = maximumLevel > 0 && playerLevel >= maximumLevel ? Math.Truncate(quest.MoneyAtMaxLevel * rate) : 0;
        return (uint)Math.Clamp(baseReward + bonus, 0, uint.MaxValue);
    }
}
