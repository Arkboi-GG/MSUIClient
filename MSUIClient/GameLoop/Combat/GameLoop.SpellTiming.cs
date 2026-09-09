using MSUIClient.Engine.UI;
using MSUIClient.Formats;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private float ActorCastSpeed(ulong actor) => _entities.TryGet(actor, out WorldEntity body)
        ? body.Fields.CastSpeedMultiplier : 1;

    private void StartActorGlobalCooldown(PlayerActions store, ulong actor, in SpellInfo spell, double now)
    {
        if (spell.StartRecoveryCategory == 0 && spell.StartRecoveryMs == 0) return;
        var adjusted = spell with { StartRecoveryMs = (uint)Math.Clamp(
            ActorSpellModifiers(actor, spell, SpellModifierStore.GlobalCooldown).ApplyInteger(spell.StartRecoveryMs), 0, uint.MaxValue) };
        uint duration = _entities.TryGet(actor, out WorldEntity body) && body.IsPlayer
            ? SpellTimingLaw.GlobalCooldownMilliseconds(adjusted, body.Fields.CastSpeedMultiplier)
            : spell.StartRecoveryMs;
        store.StartGlobalCooldown(spell.Id, spell, now, duration);
        if (actor == ControlledGuid && duration > 0) _globalCooldownUntil = now + duration / 1000.0;
    }
}
