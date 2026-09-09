using MSUIClient.Engine.UI;
using MSUIClient.Formats;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private bool TryReputationReaction(WorldEntity from, FactionTemplateRow fromFaction,
        WorldEntity to, FactionTemplateRow toFaction, out FactionReaction reaction)
    {
        reaction = default;
        if (_factionCatalog is null) return false;
        ulong fromOwner = ReactionPlayerOwner(from), toOwner = ReactionPlayerOwner(to);
        if ((from.Fields.UnitFlags & 0x8) != 0 && fromOwner != 0)
        {
            // Each player owner needs its own received standing; pets inherit only that owner's data.
            if (fromOwner == 0 || !_entities.TryGet(fromOwner, out WorldEntity owner) ||
                !_factionCatalog.TryGetById(toFaction.Faction, out FactionInfo info) ||
                ReputationFor(fromOwner)?[info.ReputationIndex] is not { } state) return false;
            bool contested = (toFaction.Flags & ReputationReactionLaw.ContestedGuardFlag) != 0 &&
                (owner.Fields.PlayerFlags & ReputationReactionLaw.ContestedPlayerFlag) != 0;
            reaction = ReputationReactionLaw.PlayerToward((state.Flags & 2) != 0, contested);
            return true;
        }
        if (toOwner == 0 || !_entities.TryGet(toOwner, out WorldEntity recipient)) return false;
        bool guardHostile = (fromFaction.Flags & ReputationReactionLaw.ContestedGuardFlag) != 0 &&
            (recipient.Fields.PlayerFlags & ReputationReactionLaw.ContestedPlayerFlag) != 0;
        if (guardHostile) { reaction = FactionReaction.Hostile; return true; }
        if (!_factionCatalog.TryGetById(fromFaction.Faction, out FactionInfo faction) ||
            ReputationFor(toOwner)?[faction.ReputationIndex] is not { } reputation) return false;
        var identity = recipient.Fields.Bytes0;
        long standing = (long)faction.BaseStanding(identity.Race, identity.Class) + reputation.Standing;
        reaction = ReputationReactionLaw.FactionTowardPlayer(standing, (reputation.Flags & 2) != 0, false);
        return true;
    }
}
