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
    private sealed record SpellCastResultEvent(uint SpellId, byte Reason) : SpellPresentationEvent;
    private sealed record SpellFailedOtherEvent(ulong Caster, uint SpellId) : SpellPresentationEvent;
    private sealed record SpellDelayedEvent(ulong Caster, uint DelayMs) : SpellPresentationEvent;
    private sealed record SpellChannelStartEvent(uint SpellId, uint DurationMs) : SpellPresentationEvent;
    private sealed record SpellChannelUpdateEvent(uint RemainingMs) : SpellPresentationEvent;
    private sealed record SpellKitPushEvent(ulong Unit, uint KitId) : SpellPresentationEvent;
    private sealed record SpellAutoRepeatCancelledEvent : SpellPresentationEvent;

    private readonly Queue<(long Sequence, SpellPresentationEvent Event)> _spellPresentationEvents = new();
    private long _nextSpellPresentationSequence;
    private long _lastSpellPresentationSequence;

    private void EnqueueSpellPresentation(SpellPresentationEvent spellEvent)
        => _spellPresentationEvents.Enqueue((++_nextSpellPresentationSequence, spellEvent));

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
                    ApplySpellCastFailureResult(e.SpellId, e.Reason);
                    break;
                case SpellFailedOtherEvent e:
                    if (e.Caster == _net?.PlayerGuid)
                        EmitSpellServerResult(e.SpellId, "SMSG_SPELL_FAILED_OTHER");
                    ApplySpellFailure(e.Caster, e.SpellId, "INTERRUPTED");
                    break;
                case SpellDelayedEvent e when e.Caster == _net?.PlayerGuid:
                    DelayCastBar(e.DelayMs);
                    break;
                case SpellChannelStartEvent e:
                    EmitSpellServerResult(e.SpellId, "MSG_CHANNEL_START");
                    BeginChannel(e.SpellId, e.DurationMs);
                    break;
                case SpellChannelUpdateEvent e:
                    UpdateChannel(e.RemainingMs);
                    break;
                case SpellKitPushEvent e:
                    ApplyPushedVisual(e.Unit, e.KitId);
                    break;
                case SpellAutoRepeatCancelledEvent:
                    ApplyAutoRepeatCancelled();
                    break;
            }
        }
    }
}
