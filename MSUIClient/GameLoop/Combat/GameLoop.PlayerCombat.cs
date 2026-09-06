using MSUIClient.Engine.UI;
using MSUIClient.Formats;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private bool TryPlayerCombatReaction(WorldEntity from, WorldEntity to, out FactionReaction reaction)
    {
        reaction = default;
        if ((from.Fields.UnitFlags & to.Fields.UnitFlags & 8) == 0) return false;
        ulong a = ReactionPlayerOwner(from), b = ReactionPlayerOwner(to);
        if (a == 0 || b == 0 || !_entities.TryGet(a, out var actor) || !_entities.TryGet(b, out var target))
            return false;
        FactionReaction? result = PlayerCombatLaw.Relationship(actor, target, InKnownGroup(a) && InKnownGroup(b));
        if (result is not { } value) return false;
        reaction = value;
        return true;
    }

    private bool CanActorAttack(WorldEntity target, ulong actorGuid)
    {
        if (_net is null || !target.IsUnit || target.Guid == actorGuid || target.IsDead ||
            (target.Fields.UnitFlags & AttackDisqualifiers) != 0 ||
            !_entities.TryGet(actorGuid, out WorldEntity actor)) return false;
        if (ReactionBetween(actor, target) == FactionReaction.Friendly ||
            ReactionBetween(target, actor) == FactionReaction.Friendly) return false;
        ulong a = ReactionPlayerOwner(actor), b = ReactionPlayerOwner(target);
        if (a == 0 || b == 0) return !target.IsPlayer;
        // A missing owner's public flags cannot prove that its pet is PvP-enabled.
        return _entities.TryGet(a, out var ownerA) && _entities.TryGet(b, out var ownerB) &&
            a != b && PlayerCombatLaw.AttackEnabled(ownerA.Fields, ownerB.Fields);
    }

    private FactionReaction ReactionBetween(WorldEntity from, WorldEntity to)
    {
        if (_factions is null ||
            !_factions.TryGet(from.Fields.FactionTemplate, out FactionTemplateRow own) ||
            !_factions.TryGet(to.Fields.FactionTemplate, out FactionTemplateRow other))
            return FactionReaction.Neutral;
        return TryForcedReaction(from, own, to, other, out FactionReaction forced) ||
            TryPlayerCombatReaction(from, to, out forced) ||
            TryReputationReaction(from, own, to, other, out forced)
            ? forced : own.ReactionToward(other);
    }
}
