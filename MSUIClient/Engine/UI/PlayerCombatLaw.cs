using MSUIClient.Formats;
using MSUIClient.Net;

namespace MSUIClient.Engine.UI;

/// <summary>Player-owner rules from the inspected Core Object.cpp. Duel teams are set at
/// server start, not acceptance/countdown, and cleared at completion.</summary>
public static class PlayerCombatLaw
{
    public static bool ActiveDuel(ObjectFields a, ObjectFields b)
    {
        ulong arbiter = a.GetGuid(ObjectFields.PLAYER_DUEL_ARBITER) ?? 0;
        uint teamA = a.GetU32(ObjectFields.PLAYER_DUEL_TEAM) ?? 0;
        uint teamB = b.GetU32(ObjectFields.PLAYER_DUEL_TEAM) ?? 0;
        return arbiter != 0 && arbiter == (b.GetGuid(ObjectFields.PLAYER_DUEL_ARBITER) ?? 0) &&
            teamA is 1 or 2 && teamB is 1 or 2 && teamA != teamB;
    }

    public static bool BothFreeForAll(ObjectFields a, ObjectFields b) =>
        (a.PlayerFlags & 0x80) != 0 && (b.PlayerFlags & 0x80) != 0;

    public static FactionReaction? Relationship(WorldEntity a, WorldEntity b, bool sameGroup)
    {
        if (a.Guid == b.Guid) return FactionReaction.Friendly;
        if (ActiveDuel(a.Fields, b.Fields)) return FactionReaction.Hostile;
        if (sameGroup) return FactionReaction.Friendly;
        if (BothFreeForAll(a.Fields, b.Fields)) return FactionReaction.Hostile;
        return null;
    }

    public static bool AttackEnabled(ObjectFields attacker, ObjectFields target)
    {
        if ((target.PlayerFlags & 8) != 0) return false; // GM target
        uint a = (attacker.GetU32(ObjectFields.UNIT_BYTES_2) ?? 0) >> 8;
        uint b = (target.GetU32(ObjectFields.UNIT_BYTES_2) ?? 0) >> 8;
        return ActiveDuel(attacker, target) || (target.UnitFlags & 0x1000) != 0 ||
            BothFreeForAll(attacker, target) || (a & b & 4) != 0 || ((a | b) & 1) != 0;
    }
}
