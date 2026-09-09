using MSUIClient.Net;

namespace MSUIClient.Engine.UI;

public readonly record struct CastPartyUnit(
    ulong Guid, bool IsPlayer, bool IsPet, ulong Owner, ulong Charmer)
{
    public ulong Controller => Charmer != 0 ? Charmer : Owner;
    public ulong GroupPlayer => Controller != 0
        ? (GuidInfo.IsPlayer(Controller) ? Controller : 0) : IsPlayer ? Guid : 0;

    public static CastPartyUnit From(WorldEntity unit) => new(unit.Guid, unit.IsPlayer,
        unit.IsCreature && (GuidInfo.IsPet(unit.Guid) || unit.Fields.IsPetOrCharm),
        unit.Fields.SummonedBy ?? 0, unit.Fields.CharmedBy ?? 0);
}

/// <summary>Core TARGET_UNIT_PARTY (35): another group member, its pet, or the caster's owner/pet.</summary>
public static class CastPartyTargetLaw
{
    public static bool Accepts(in CastPartyUnit caster, in CastPartyUnit target, bool sameGroup)
    {
        if (caster.Guid == 0 || target.Guid == 0 || caster.Guid == target.Guid) return false;
        if (caster.Controller != 0)
        {
            if (!GuidInfo.IsPlayer(caster.Controller)) return false;
            if (target.Guid == caster.Controller) return true;
        }
        else if (caster.IsPlayer)
        {
            if (target.Controller == caster.Guid && target.IsPet) return true;
        }
        else return false;

        if (!sameGroup) return false;
        // A controlled player or a charmed creature is not another member's owned pet.
        return target.Controller != 0
            ? GuidInfo.IsPlayer(target.Controller) && target.IsPet && target.Owner == target.Controller
            : target.IsPlayer;
    }
}
