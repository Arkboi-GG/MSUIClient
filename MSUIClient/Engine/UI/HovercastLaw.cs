namespace MSUIClient.Engine.UI;

/// <summary>Where a hovercast redirect found its unit.</summary>
public enum HovercastSource
{
    None,
    UnitFrame,
    WorldUnit,
}

/// <summary>Why a hovercast redirect did or did not happen. Reported on the cast verdict
/// line so a redirect that silently did nothing can be told apart from one that never ran.</summary>
public enum HovercastReason
{
    /// <summary>The AddOns-page switch is off. Every press behaves exactly as it always did.</summary>
    Disabled,

    /// <summary>Items, macros and empty slots are passed through untouched. Only a spell slot
    /// has a target for the redirect to rebind.</summary>
    NotASpell,

    /// <summary>A ground, item or commander unit cursor is already armed and owns the next
    /// click. Redirecting here would steal the pick the player is part-way through.</summary>
    TargetingArmed,

    /// <summary>Cursor is over neither a unit frame nor a world unit: nothing to redirect to.</summary>
    NoHover,

    /// <summary>A world unit is under the cursor but "Include world units" is off, so the
    /// press keeps its ordinary target. Frames-only is the default.</summary>
    WorldHoverNotEnabled,

    /// <summary>The hovered unit cannot receive this spell (a heal over an enemy, a bolt over
    /// a party frame). The press falls through to ordinary targeting rather than being refused.</summary>
    UnitRejectsSpell,

    /// <summary>Redirected onto the unit frame under the cursor.</summary>
    UnitFrame,

    /// <summary>Redirected onto the 3D world unit under the cursor.</summary>
    WorldUnit,
}

/// <summary>The outcome. <see cref="Guid"/> is non-zero only when the press should be
/// rebound onto that unit.</summary>
public readonly record struct HovercastVerdict(
    ulong Guid, HovercastSource Source, HovercastReason Reason)
{
    public bool Redirects => Guid != 0;
}

/// <summary>
/// Hovercast: while the cursor rests on a unit, an action bar press casts on THAT unit
/// instead of the current target, and the target is never disturbed. Ported from the
/// MSUI_Hovercast 1.12 addon, which hooked the Lua <c>UseAction</c> global; the native
/// equivalent is <c>GameLoop.UseAction</c>, the one funnel every bar, key and click
/// already goes through.
///
/// Only the DECISION lives here. The addon's machinery around it does not survive the
/// port and must not be reintroduced:
///
///   - Its target juggling (ClearTarget, CastSpellByName, SpellTargetUnit, TargetLastTarget)
///     existed because 1.12 Lua cannot aim a cast at an arbitrary unit. This client sends
///     CMSG_CAST_SPELL with a guid, so the redirect is one argument and the player's real
///     target is never touched at all.
///   - Its hand-maintained HELPFUL_SPELLS and DUAL_SPELLS name lists stood in for target
///     flags Lua could not read. <see cref="MSUIClient.Net.CastTargetLaw"/> reads the real
///     Spell.dbc target word, so eligibility is answered from data, for every spell, with
///     no list to maintain and nothing class-specific to miss.
///   - Its tooltip-scanning GetActionSpell existed because 1.12 has no GetActionInfo. The
///     action slot here already carries its spell id.
///
/// Precedence matches the addon: a unit frame under the cursor beats a world unit, because
/// a frame is an unambiguous statement of intent and a body under the crosshair is not.
/// </summary>
public static class HovercastLaw
{
    /// <param name="enabled">The AddOns-page master switch.</param>
    /// <param name="allowWorldUnits">Whether 3D bodies count, not just frames.</param>
    /// <param name="slotCastsASpell">False for item, macro and empty slots.</param>
    /// <param name="targetingAlreadyArmed">A ground/item/commander cursor owns the next pick.</param>
    /// <param name="unitFrameHoverGuid">Unit whose frame is under the cursor, or 0.</param>
    /// <param name="worldHoverGuid">Unit whose body is under the cursor, or 0. Already
    /// mutually exclusive with the frame hover: the world pick is cleared whenever ImGui
    /// owns the mouse, which is exactly when a frame is hovered.</param>
    /// <param name="hoveredUnitAcceptsSpell">Whether the hovered unit can legally receive
    /// this spell. Supplied by the caller from <c>CastTargetLaw</c> rather than guessed here.</param>
    public static HovercastVerdict Resolve(
        bool enabled,
        bool allowWorldUnits,
        bool slotCastsASpell,
        bool targetingAlreadyArmed,
        ulong unitFrameHoverGuid,
        ulong worldHoverGuid,
        Func<ulong, bool> hoveredUnitAcceptsSpell)
    {
        ArgumentNullException.ThrowIfNull(hoveredUnitAcceptsSpell);

        if (!enabled) return new(0, HovercastSource.None, HovercastReason.Disabled);
        if (!slotCastsASpell) return new(0, HovercastSource.None, HovercastReason.NotASpell);
        if (targetingAlreadyArmed)
            return new(0, HovercastSource.None, HovercastReason.TargetingArmed);

        ulong guid;
        HovercastSource source;

        if (unitFrameHoverGuid != 0)
        {
            guid = unitFrameHoverGuid;
            source = HovercastSource.UnitFrame;
        }
        else if (worldHoverGuid == 0)
        {
            return new(0, HovercastSource.None, HovercastReason.NoHover);
        }
        else if (!allowWorldUnits)
        {
            return new(0, HovercastSource.None, HovercastReason.WorldHoverNotEnabled);
        }
        else
        {
            guid = worldHoverGuid;
            source = HovercastSource.WorldUnit;
        }

        // A hovered unit that cannot receive this spell FALLS THROUGH to ordinary targeting
        // instead of refusing the cast. Hovering a party frame must never make Frostbolt
        // fail; the addon's redirect had no such escape and produced "Invalid target" there.
        if (!hoveredUnitAcceptsSpell(guid))
            return new(0, HovercastSource.None, HovercastReason.UnitRejectsSpell);

        return new(guid, source,
            source == HovercastSource.UnitFrame
                ? HovercastReason.UnitFrame
                : HovercastReason.WorldUnit);
    }
}
