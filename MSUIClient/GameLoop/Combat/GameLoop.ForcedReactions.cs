using MSUIClient.Formats;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private readonly ForcedReactionStore _forcedReactions = new();

    private ulong ReactionPlayerOwner(WorldEntity entity)
    {
        // Follow charmer before owner. A missing player entity can still be identified by GUID.
        for (int depth = 0; depth < 8; ++depth)
        {
            ulong controller = entity.Fields.CharmedBy ?? entity.Fields.SummonedBy ?? entity.Guid;
            if (controller == entity.Guid) return entity.IsPlayer ? entity.Guid : 0;
            if (GuidInfo.IsPlayer(controller)) return controller;
            if (!_entities.TryGet(controller, out entity)) return 0;
        }
        return 0;
    }

    private bool TryForcedReaction(WorldEntity from, FactionTemplateRow fromFaction,
        WorldEntity to, FactionTemplateRow toFaction, out FactionReaction reaction)
    {
        ulong fromController = from.Fields.CharmedBy ?? from.Fields.SummonedBy ?? from.Guid;
        ulong toController = to.Fields.CharmedBy ?? to.Fields.SummonedBy ?? to.Guid;
        if (from.Guid == to.Guid || fromController == toController)
        {
            reaction = FactionReaction.Friendly;
            return true;
        }
        ulong fromOwner = ReactionPlayerOwner(from);
        if (fromOwner != 0) return _forcedReactions.TryGet(fromOwner, toFaction.Faction, out reaction);
        return _forcedReactions.TryGet(ReactionPlayerOwner(to), fromFaction.Faction, out reaction);
    }
}
