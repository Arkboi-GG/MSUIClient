using MSUIClient.Formats;

namespace MSUIClient.Net;

public enum CastTargetKind { SelfImplicit, Unit, Refused }

public enum CastTargetReason
{
    ImplicitSelf,
    UnsupportedTargetShape,
    SelectedUnit,
    SelfFallback,
    NoValidUnit,
    UnavailableOrPassive,
    AlreadyQueued,
    CooldownOrGlobalCooldown,
    PendingCast,
    Mounted,
    TooClose,
    OutOfRange,
}

public readonly record struct CastTargetCandidate(
    ulong Guid, bool IsSelf, bool Friendly, bool Attackable, bool Dead);

public readonly record struct CastTargetVerdict(
    CastTargetKind Kind, CastTargetReason Reason, ulong Guid = 0);

/// <summary>
/// Pure build-5875 ArmCast/BindTarget law, transcribed from Benilla ui_action/cast_target.rs.
/// Non-unit cursor targets are refused until their separate item/GO/ground binders exist.
/// </summary>
public static class CastTargetLaw
{
    private const ushort Unit = 0x0002, Raid = 0x0004, Party = 0x0008;
    private const ushort Enemy = 0x0080, Assist = 0x0100, CorpseEnemy = 0x0200;
    private const ushort ExplicitGate = 0x0400, CorpseAlly = 0x8000;
    private const ushort UnitBits = Unit | Raid | Party | Enemy | Assist | CorpseEnemy | ExplicitGate | CorpseAlly;

    public static ushort TargetMask(in SpellInfo spell)
    {
        ushort word = (ushort)spell.Targets;
        switch (spell.ImplicitTarget)
        {
            case 1: word &= unchecked((ushort)~ExplicitGate); break;
            case 5: word &= unchecked((ushort)~CorpseAlly); break;
            case 6 or 53: word |= Enemy; break;
            case 21 or 45: word |= Assist; break;
            case 23: word |= 0x0800; break;
            case 25 or 63: word |= Unit; break;
            case 26: word |= 0x4000; break;
            case 35: word |= Party; break;
            case 57 or 61: word |= Raid; break;
        }
        return word;
    }

    public static CastTargetVerdict Resolve(in SpellInfo spell,
        CastTargetCandidate? selection, CastTargetCandidate? self, bool autoSelfCast = true)
    {
        ushort word = TargetMask(spell);
        if (word == 0) return new(CastTargetKind.SelfImplicit, CastTargetReason.ImplicitSelf);
        if ((word & ~UnitBits) != 0)
            return new(CastTargetKind.Refused, CastTargetReason.UnsupportedTargetShape);
        if (selection is { } selected && ClearSatisfied(word, selected) == 0)
            return new(CastTargetKind.Unit, CastTargetReason.SelectedUnit, selected.Guid);
        if (autoSelfCast && self is { } player && ClearSatisfied(word, player) == 0)
            return new(CastTargetKind.Unit, CastTargetReason.SelfFallback, player.Guid);
        return new(CastTargetKind.Refused, CastTargetReason.NoValidUnit);
    }

    private static ushort ClearSatisfied(ushort word, in CastTargetCandidate candidate)
    {
        bool assist = candidate.IsSelf || candidate.Friendly;
        if ((word & Party) != 0 && candidate.IsSelf) word &= unchecked((ushort)~Party);
        if ((word & Raid) != 0 && candidate.IsSelf) word &= unchecked((ushort)~Raid);
        if ((word & Assist) != 0 && assist) word &= unchecked((ushort)~Assist);
        if ((word & Enemy) != 0 && !candidate.IsSelf && candidate.Attackable) word &= unchecked((ushort)~Enemy);
        if ((word & Unit) != 0) word &= unchecked((ushort)~Unit);
        if ((word & ExplicitGate) != 0 && !candidate.IsSelf) word &= unchecked((ushort)~ExplicitGate);
        if ((word & CorpseAlly) != 0 && assist && candidate.Dead) word &= unchecked((ushort)~CorpseAlly);
        if ((word & CorpseEnemy) != 0 && !candidate.IsSelf && candidate.Dead) word &= unchecked((ushort)~CorpseEnemy);
        return word;
    }
}
