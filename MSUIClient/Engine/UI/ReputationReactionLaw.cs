using MSUIClient.Formats;

namespace MSUIClient.Engine.UI;

public static class ReputationReactionLaw
{
    public const uint ContestedGuardFlag = 0x1000, ContestedPlayerFlag = 0x100;

    public static FactionReaction PlayerToward(bool atWar, bool contestedGuard) =>
        atWar || contestedGuard ? FactionReaction.Hostile : FactionReaction.Friendly;

    public static FactionReaction FactionTowardPlayer(long standing, bool atWar, bool contestedGuard)
    {
        if (contestedGuard || standing < -3000) return FactionReaction.Hostile;
        return standing >= 3000 && !atWar ? FactionReaction.Friendly : FactionReaction.Neutral;
    }
}
