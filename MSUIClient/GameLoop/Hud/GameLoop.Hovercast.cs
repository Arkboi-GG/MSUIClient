using MSUIClient.Engine.UI;
using MSUIClient.Formats;
using MSUIClient.Net;

namespace MSUIClient;

/// <summary>
/// Hovercast — cast on the unit under the cursor without changing target.
/// The rule itself is <see cref="HovercastLaw"/>; this half owns the frame state it needs.
///
/// The hover is published by the unit-frame surfaces while they draw (Render) and read by
/// <c>UseAction</c> on the following binding poll (Update), so it is double-buffered: the
/// surfaces accumulate into <c>_hovercastFrameHoverPending</c> and the frame boundary in
/// <c>Update</c> promotes it. A single field would be cleared by whichever surface drew
/// last and read back as "nothing hovered" every other frame.
/// </summary>
public sealed partial class GameLoop
{
    /// <summary>Unit whose frame the cursor rested on as of the last completed draw.</summary>
    private ulong _hovercastFrameHover;

    /// <summary>Accumulator for the draw in progress. Promoted at the frame boundary.</summary>
    private ulong _hovercastFrameHoverPending;

    private bool HovercastEnabled => Settings.AddOns?.Hovercast == true;

    private bool HovercastWorldUnitsEnabled => Settings.AddOns?.HovercastWorldUnits == true;

    /// <summary>Frame boundary: last draw's hover becomes the one presses resolve against.</summary>
    private void BeginHovercastFrame()
    {
        _hovercastFrameHover = _hovercastFrameHoverPending;
        _hovercastFrameHoverPending = 0;
    }

    /// <summary>
    /// A unit frame reports the cursor is over it. Called from the frame surfaces
    /// (player, target, party) right where they already compute their hover for art state,
    /// so hovercast never runs its own hit test and cannot disagree with the one on screen.
    /// </summary>
    private void NoteHovercastFrameHover(ulong guid, bool hovered)
    {
        if (hovered && guid != 0) _hovercastFrameHoverPending = guid;
    }

    /// <summary>
    /// The unit an action press should be redirected onto, or 0 to leave the press alone.
    /// Everything the redirect must not disturb is checked here: the switch, the slot kind,
    /// an armed targeting cursor, and whether the hovered unit can actually take the spell.
    /// </summary>
    private ulong HovercastTarget(in ActionSlot slot)
    {
        // Cheap exits first so a disabled feature costs one bool read per press.
        if (!HovercastEnabled || _net is null || _spellCatalog is null) return 0;

        bool armed = _groundCastSpell != 0 || _tacticalGroundSpellId != 0 ||
            _giftWrap is not null || _itemCastSpell != 0 || _rtsUnitCastSpellId != 0;
        uint spellId = slot.ActionId;

        HovercastVerdict verdict = HovercastLaw.Resolve(
            enabled: true,
            allowWorldUnits: HovercastWorldUnitsEnabled,
            slotCastsASpell: slot.Kind == ActionSlot.Spell,
            targetingAlreadyArmed: armed,
            unitFrameHoverGuid: _hovercastFrameHover,
            worldHoverGuid: _hoveredGuid,
            hoveredUnitAcceptsSpell: guid => HovercastUnitAcceptsSpell(spellId, guid));

        if (verdict.Redirects)
            EmitCombat("HovercastRedirect", "cast-acting-path", verdict.Guid,
                $"spell={spellId};source={verdict.Source};reason={verdict.Reason}");

        return verdict.Guid;
    }

    /// <summary>
    /// Whether the hovered unit is a live entity this spell can legally bind. Answered by
    /// the same <see cref="CastTargetLaw"/> the ordinary cast path uses, so a redirect can
    /// never reach a target the normal press would have refused.
    ///
    /// autoSelfCast is false on purpose: the question is strictly "can THIS unit take it",
    /// and a self-cast fallback here would silently cast on the player when the cursor was
    /// resting on someone the spell cannot touch.
    /// </summary>
    private bool HovercastUnitAcceptsSpell(uint spellId, ulong guid)
    {
        if (_spellCatalog is null || !_spellCatalog.TryGet(spellId, out SpellInfo spell))
            return false;
        if (!_entities.TryGet(guid, out WorldEntity unit) || !unit.IsUnit) return false;

        CastTargetCandidate candidate = CastCandidate(unit, guid == ControlledGuid);
        return CastTargetLaw.Resolve(spell, candidate, self: null, autoSelfCast: false)
            .Kind == CastTargetKind.Unit;
    }
}
