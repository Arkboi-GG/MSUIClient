using MSUIClient.Net;

namespace MSUIClient.Engine.UI;

public static class QuestRewardUiLaw
{
    public static bool HasNpcRewards(int choices, int fixedItems, uint money, uint spell) =>
        choices > 0 || fixedItems > 0 || money > 0 || spell != 0;

    public static string SpellLearnTextKey(bool tradeSkill) =>
        tradeSkill ? "REWARD_TRADESKILL_SPELL" : "REWARD_SPELL";

    public static uint RequiredMoney(QuestTemplate quest) => quest.Money < 0 ? (uint)-(long)quest.Money : 0;

    public static uint RewardMoney(QuestTemplate quest, uint playerLevel, uint maximumLevel = 60, float moneyRate = 1f)
    {
        if ((quest.Flags & 0x200) != 0) return 0; // hidden rewards remain hidden in the log
        return VisibleRewardMoney(quest.Money, quest.MoneyAtMaxLevel, playerLevel, maximumLevel, moneyRate);
    }

    /// <summary>NPC details/offer packets carry the scaled base reward; the query
    /// supplies the unscaled max-level bonus. Call only when rewards are revealed.</summary>
    public static uint VisibleRewardMoney(int wireMoney, uint moneyAtMaxLevel,
        uint playerLevel, uint maximumLevel = 60, float moneyRate = 1f)
    {
        long baseReward = Math.Max(0, wireMoney);
        float rate = float.IsFinite(moneyRate) && moneyRate >= 0 ? moneyRate : 1f;
        double bonus = maximumLevel > 0 && playerLevel >= maximumLevel ? Math.Truncate(moneyAtMaxLevel * rate) : 0;
        return (uint)Math.Clamp(baseReward + bonus, 0, uint.MaxValue);
    }
}
