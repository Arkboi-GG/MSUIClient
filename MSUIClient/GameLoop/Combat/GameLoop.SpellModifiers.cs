using MSUIClient.Formats;
using MSUIClient.Engine.UI;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private readonly Dictionary<ulong, SpellModifierStore> _spellModifiers = [];

    private void ApplySpellModifier(byte[] body, ulong owner, bool percentage)
    {
        SpellModifierPacket packet = SpellModifierPacket.Parse(body);
        if (owner == 0 || owner != LocalPlayerGuid && owner != ControlledGuid) return;
        if (!_spellModifiers.TryGetValue(owner, out var store)) _spellModifiers[owner] = store = new();
        store.Apply(packet, percentage);
    }

    private SpellModifierTotals ActorSpellModifiers(ulong actor, in SpellInfo spell, byte operation)
    {
        if ((spell.AttributesEx3 & 0x20000000) != 0 || _spellCatalog is null || _spellModifiers is not { } stores || !stores.TryGetValue(actor, out var store) ||
            !_entities.TryGet(actor, out WorldEntity body) || !body.IsPlayer) return default;
        return store.Totals(spell, _spellCatalog.ModifierFamilyForClass(body.Fields.Bytes0.Class), operation);
    }
    private SpellTooltipView BuildActorSpellTooltip(in SpellInfo spell, uint level, ulong actor)
    {
        var shown = spell with { CastTimeMs = (int)Math.Clamp(
            ActorSpellModifiers(actor, spell, SpellModifierStore.CastTime).ApplyInteger(spell.CastTimeMs), 0, int.MaxValue) };
        if (_entities.TryGet(actor, out WorldEntity body) && !spell.UsesAllPower)
        {
            ActorCanPaySpell(spell, body, out _, out uint cost);
            shown = shown with { ManaCost = cost, ManaCostPercent = 0 };
        }
        var recovery = PlayerActions.ModifiedRecovery(spell.RecoveryMs, spell.Category, spell.CategoryRecoveryMs,
            ActorSpellModifiers(actor, spell, SpellModifierStore.Cooldown));
        shown = shown with { RecoveryMs = recovery.Spell, CategoryRecoveryMs = recovery.Category };
        SpellRangeRow? range = _spellCatalog!.TryGetRange(spell.RangeIndex, out var rawRange)
            ? ActorSpellRange(spell, actor, rawRange) : null;
        return SpellTooltipLaw.Build(shown, _spellCatalog, level, ActorCastSpeed(actor), range,
            (referencedSpell, operation) => ActorSpellModifiers(actor, referencedSpell, operation));
    }
    private void StartActorSpellCooldown(PlayerActions store, ulong actor, in SpellInfo spell,
        uint rangedAttackTimeMs, double now) => store.StartSpellCooldown(spell.Id, spell, rangedAttackTimeMs, now,
            ActorSpellModifiers(actor, spell, SpellModifierStore.Cooldown));
    private SpellRangeRow ActorSpellRange(in SpellInfo spell, ulong actor, SpellRangeRow range)
    {
        // Melee reach follows a separate rule; range modifiers extend the authored ranged maximum only.
        if (range.Melee || range.Max <= 0) return range;
        float max = ActorSpellModifiers(actor, spell, SpellModifierStore.Range).ApplyFloat(range.Max);
        return float.IsFinite(max) ? range with { Max = Math.Max(0, max) } : range;
    }

    private float ActorSpellTargetingRadius(in SpellInfo spell, ulong actor)
    {
        if (_spellCatalog is null || !_spellCatalog.TryGetTargetingRadius(spell, out float radius))
            return SpellCatalog.MissingTargetRadiusFallback;
        float modified = ActorSpellModifiers(actor, spell, SpellModifierStore.Radius).ApplyFloat(radius);
        return float.IsFinite(modified) ? Math.Max(0, modified) : radius;
    }
}
