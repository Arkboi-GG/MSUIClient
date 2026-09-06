using MSUIClient.Net;

namespace MSUIClient;

/// <summary>
/// The spell wire decoder and the spell presentation router deliberately meet at this FIFO.
/// Network pumping only produces immutable facts; the game-thread spell phase consumes them in
/// packet order.  That makes START, GO, impact, failure, channel and pushed-kit edges explicit
/// instead of letting parsing side effects interleave with an object-update batch.
/// </summary>
public sealed partial class GameLoop
{
    private abstract record SpellPresentationEvent;
    private sealed record SpellStartEvent(SpellStartPacket Packet) : SpellPresentationEvent;
    private sealed record SpellGoEvent(SpellGoPacket Packet) : SpellPresentationEvent;
    private sealed record SpellCastResultEvent(ulong Caster, uint SpellId, byte Reason) : SpellPresentationEvent
    { public SpellCastFailureContext? Context { get; init; } }
    private sealed record SpellFailedOtherEvent(ulong Caster, uint SpellId) : SpellPresentationEvent;
    private sealed record SpellDelayedEvent(ulong Caster, uint DelayMs) : SpellPresentationEvent;
    private sealed record SpellChannelStartEvent(ulong Caster, uint SpellId, uint DurationMs) : SpellPresentationEvent;
    private sealed record SpellChannelUpdateEvent(ulong Caster, uint RemainingMs) : SpellPresentationEvent;
    private sealed record SpellChainTargetsEvent(SpellChainTargetsPacket Packet) : SpellPresentationEvent;
    private sealed record SpellKitPushEvent(ulong Unit, uint KitId) : SpellPresentationEvent;
    private sealed record SpellAutoRepeatCancelledEvent(ulong Caster) : SpellPresentationEvent;
    private sealed record SpellFeignDeathResistedEvent(ulong Caster) : SpellPresentationEvent;
    private sealed record SpellCombatCancelledEvent(ulong Caster) : SpellPresentationEvent;

    private readonly Queue<(long Sequence, SpellPresentationEvent Event)> _spellPresentationEvents = new();
    private long _nextSpellPresentationSequence;
    private long _lastSpellPresentationSequence;

    private void EnqueueSpellPresentation(SpellPresentationEvent spellEvent)
        => _spellPresentationEvents.Enqueue((++_nextSpellPresentationSequence, spellEvent));

    private void EnqueueFeignDeathResisted(ulong caster, byte[] body)
    {
        if (body.Length != 0) throw new InvalidDataException("SMSG_FEIGN_DEATH_RESISTED expected empty body");
        EnqueueSpellPresentation(new SpellFeignDeathResistedEvent(caster));
    }

    private void DrainSpellPresentationEvents()
    {
        while (_spellPresentationEvents.TryDequeue(out var queued))
        {
            _lastSpellPresentationSequence = queued.Sequence;
            switch (queued.Event)
            {
                case SpellStartEvent e:
                    ApplySpellStart(e.Packet);
                    break;
                case SpellGoEvent e:
                    ApplySpellGo(e.Packet);
                    break;
                case SpellCastResultEvent e:
                    ApplySpellCastFailureResult(e.SpellId, e.Reason, e.Caster, e.Context);
                    break;
                case SpellFailedOtherEvent e:
                    if (e.Caster == _net?.PlayerGuid)
                        EmitSpellServerResult(e.SpellId, "SMSG_SPELL_FAILED_OTHER");
                    ApplySpellFailure(e.Caster, e.SpellId, "INTERRUPTED");
                    break;
                case SpellDelayedEvent e:
                    DelayRealPortalCastPrewarm(e.Caster, e.DelayMs);
                    if (e.Caster == ControlledGuid)
                        DelayCastBar(e.DelayMs);
                    break;
                case SpellChannelStartEvent e when e.Caster == ControlledGuid:
                    EmitSpellServerResult(e.SpellId, "MSG_CHANNEL_START");
                    BeginChannel(e.SpellId, e.DurationMs);
                    break;
                case SpellChannelUpdateEvent e when e.Caster == ControlledGuid:
                    UpdateChannel(e.RemainingMs);
                    break;
                case SpellChainTargetsEvent e:
                    ApplySpellChainTargets(e.Packet);
                    break;
                case SpellKitPushEvent e:
                    ApplyPushedVisual(e.Unit, e.KitId);
                    break;
                case SpellAutoRepeatCancelledEvent e when e.Caster == ControlledGuid:
                    ApplyAutoRepeatCancelled();
                    break;
                case SpellFeignDeathResistedEvent e when e.Caster == ControlledGuid:
                    ShowUiError(InventoryGlobalString("ERR_FEIGN_DEATH_RESISTED", "Resisted"));
                    break;
                case SpellCombatCancelledEvent e:
                    ApplyServerCombatCancelled(e.Caster);
                    break;
            }
        }
    }
}
