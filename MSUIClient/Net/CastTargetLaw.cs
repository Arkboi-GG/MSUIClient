using MSUIClient.Formats;

namespace MSUIClient.Net;

public enum CastTargetKind { SelfImplicit, Unit, Ground, Item, GameObject, ItemOrGameObject, Refused }

public enum CastTargetReason
{
    ImplicitSelf,
    UnsupportedTargetShape,
    SelectedUnit,
    SelfFallback,
    GroundTargeting,
    ItemTargeting,
    GameObjectTargeting,
    InvalidItemTarget,
    NoValidUnit,
    UnavailableOrPassive,
    AlreadyQueued,
    CooldownOrGlobalCooldown,
    PendingCast,
    Mounted,
    TooClose,
    OutOfRange,
    NotEnoughPower,
    UnknownSpell,
    ProfessionWindow,
    MissingReagent,
    MissingTool,
    MissingSpellFocus,
    WrongForm,
    CasterAuraState,
    TargetAuraState,
    ComboTarget,
}

public readonly record struct CastTargetCandidate(
    ulong Guid, bool IsSelf, bool Friendly, bool Attackable, bool Dead, bool PartyEligible = false);

public readonly record struct CastTargetVerdict(
    CastTargetKind Kind, CastTargetReason Reason, ulong Guid = 0);

/// <summary>
/// Pure build-5875 ArmCast/BindTarget law, transcribed from Benilla ui_action/cast_target.rs.
/// Item, gameobject/locked and location words bind through separate cursor consumers.
/// </summary>
public static class CastTargetLaw
{
    private const ushort Unit = 0x0002, Raid = 0x0004, Party = 0x0008;
    private const ushort Enemy = 0x0080, Assist = 0x0100, CorpseEnemy = 0x0200;
    private const ushort ExplicitGate = 0x0400, CorpseAlly = 0x8000;
    private const ushort UnitBits = Unit | Raid | Party | Enemy | Assist | CorpseEnemy | ExplicitGate | CorpseAlly;
    // TARGET_FLAG_SOURCE_LOCATION / TARGET_FLAG_DEST_LOCATION — the ground-AoE location bits.
    // The 1.12 client answers these with its targeting cursor (SpellIsTargeting = word != 0,
    // click to bind a terrain point), not with a unit; vmangos SpellCastTargets::read consumes
    // the bound point as three raw floats (SpellCastTargetsInfo.cpp:169-174).
    private const ushort SourceLocation = 0x0020, DestLocation = 0x0040;
    private const ushort LocationBits = SourceLocation | DestLocation;
    private const ushort ItemBits = 0x0010 | 0x1000;
    private const ushort Object = 0x0800, Locked = 0x4000;

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
        // Ground-target AoE (Blizzard, Flamestrike, Rain of Fire...): the location bits enter
        // the client's targeting-cursor mode — the caller arms a ground pick and the cast is
        // committed later with the bound point. Words mixing location bits with shapes we still
        // can't bind (item/string) keep refusing below.
        if ((word & LocationBits) != 0 && (word & ~(UnitBits | LocationBits)) == 0)
            return new(CastTargetKind.Ground, CastTargetReason.GroundTargeting);
        // Core Spell::CheckTargetMode accepts ITEM or GAMEOBJECT for LOCKED.
        // An explicit GAMEOBJECT requirement (e.g. Disarm Trap implicit23) still excludes items.
        if ((word & (Object | Locked)) != 0 && (word & ~(Object | Locked)) == 0)
            return (word & Object) != 0
                ? new(CastTargetKind.GameObject, CastTargetReason.GameObjectTargeting)
                : new(CastTargetKind.ItemOrGameObject, CastTargetReason.ItemTargeting);
        if ((word & ItemBits) != 0 && (word & ~(UnitBits | ItemBits)) == 0)
            return new(CastTargetKind.Item, CastTargetReason.ItemTargeting);
        if ((word & ~UnitBits) != 0)
            return new(CastTargetKind.Refused, CastTargetReason.UnsupportedTargetShape);
        if (selection is { } selected && ClearSatisfied(word, selected) == 0)
            return new(CastTargetKind.Unit, CastTargetReason.SelectedUnit, selected.Guid);
        if (autoSelfCast && self is { } player && ClearSatisfied(word, player) == 0)
            return new(CastTargetKind.Unit, CastTargetReason.SelfFallback, player.Guid);
        return new(CastTargetKind.Refused, CastTargetReason.NoValidUnit);
    }

    public static bool AcceptsItem(in SpellInfo spell) =>
        Resolve(spell, null, null).Kind is CastTargetKind.Item or CastTargetKind.ItemOrGameObject;

    public static bool AcceptsGameObject(in SpellInfo spell) =>
        Resolve(spell, null, null).Kind is CastTargetKind.GameObject or CastTargetKind.ItemOrGameObject;

    /// <summary>
    /// Whether this spell can bind an explicitly chosen friendly unit other than the caster,
    /// but cannot bind a hostile unit. The second half keeps dual-purpose and generic unit spells
    /// on their existing combat-target path instead of misclassifying them as heals/buffs.
    /// Commander multi-selection uses this to distinguish heals/buffs/resurrections from
    /// self-only, hostile, ground, item, and party-wide spells before opening a unit cursor.
    /// Both living and dead candidates are tried so ally-corpse spells keep their target step.
    /// </summary>
    public static bool AcceptsExplicitFriendlyUnit(in SpellInfo spell)
    {
        var living = new CastTargetCandidate(1, IsSelf: false, Friendly: true,
            Attackable: false, Dead: false, PartyEligible: true);
        var dead = living with { Dead = true };
        var hostile = new CastTargetCandidate(2, IsSelf: false, Friendly: false,
            Attackable: true, Dead: false);
        var hostileDead = hostile with { Dead = true };
        bool acceptsFriendly = Resolve(spell, living, self: null,
                autoSelfCast: false).Kind == CastTargetKind.Unit ||
            Resolve(spell, dead, self: null,
                autoSelfCast: false).Kind == CastTargetKind.Unit;
        bool acceptsHostile = Resolve(spell, hostile, self: null,
                autoSelfCast: false).Kind == CastTargetKind.Unit ||
            Resolve(spell, hostileDead, self: null,
                autoSelfCast: false).Kind == CastTargetKind.Unit;
        return acceptsFriendly && !acceptsHostile;
    }

    private static ushort ClearSatisfied(ushort word, in CastTargetCandidate candidate)
    {
        bool assist = candidate.IsSelf || candidate.Friendly;
        if ((word & Party) != 0 && !candidate.IsSelf && assist && candidate.PartyEligible) word &= unchecked((ushort)~Party);
        // Core TARGET_UNIT_RAID selects the explicit friendly target; group membership
        // controls later raid/class expansion, not whether the seed unit can be selected.
        if ((word & Raid) != 0 && assist) word &= unchecked((ushort)~Raid);
        if ((word & Assist) != 0 && assist) word &= unchecked((ushort)~Assist);
        if ((word & Enemy) != 0 && !candidate.IsSelf && candidate.Attackable) word &= unchecked((ushort)~Enemy);
        if ((word & Unit) != 0) word &= unchecked((ushort)~Unit);
        if ((word & ExplicitGate) != 0 && !candidate.IsSelf) word &= unchecked((ushort)~ExplicitGate);
        if ((word & CorpseAlly) != 0 && assist && candidate.Dead) word &= unchecked((ushort)~CorpseAlly);
        if ((word & CorpseEnemy) != 0 && !candidate.IsSelf && candidate.Dead) word &= unchecked((ushort)~CorpseEnemy);
        return word;
    }
}
