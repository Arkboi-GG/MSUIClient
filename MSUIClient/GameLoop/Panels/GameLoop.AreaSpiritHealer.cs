using System.Numerics;
using MSUIClient.Engine.UI;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private sealed record AreaSpiritHealerContext(BattlefieldScope Scope, ulong Guide);
    private AreaSpiritHealerContext? _areaSpiritHealerPending, _areaSpiritHealerShown;
    private double _areaSpiritHealerDeadline, _areaSpiritHealerAuraDeadline;
    private bool _areaSpiritHealerSawAura;

    private void ResetAreaSpiritHealer()
    {
        _areaSpiritHealerPending = _areaSpiritHealerShown = null;
        _areaSpiritHealerSawAura = false;
        _areaSpiritHealerDeadline = _areaSpiritHealerAuraDeadline = 0;
        ExecuteStaticPopupPlan(StaticPopupCoordinatorLaw.HideByType(_staticPopupSlots, AreaSpiritHealerUiLaw.PopupType));
    }

    private bool AreaSpiritHealerContextCurrent(AreaSpiritHealerContext context) =>
        CurrentBattlefieldScope() == context.Scope &&
        _entities.TryGet(ControlledGuid, out WorldEntity actor) && actor.IsPlayer &&
        (actor.IsDead || actor.Fields.PlayerIsGhost) &&
        TryGetInteractionBodyPose(out WorldBodyPose pose) &&
        _entities.TryGet(context.Guide, out WorldEntity guide) && guide.IsCreature && !guide.IsDead &&
        (guide.NpcFlags & WorldCursorUiLaw.SpiritGuide) != 0 &&
        NpcSessionUiLaw.InRange(Vector3.DistanceSquared(pose.Position, guide.Position));

    private void RememberAreaSpiritHealerGossip(ulong guide, uint flags)
    {
        // Dedicated Core query/queue still use the main; never send them while driving a companion.
        if ((flags & WorldCursorUiLaw.SpiritGuide) == 0 || CurrentBattlefieldScope() is not { } scope) return;
        var context = new AreaSpiritHealerContext(scope, guide);
        if (!AreaSpiritHealerContextCurrent(context)) return;
        _areaSpiritHealerPending = context;
        _areaSpiritHealerDeadline = NowSeconds() + 10;
    }

    private void ApplyAreaSpiritHealerTime(byte[] body)
    {
        AreaSpiritHealerPacket packet = AreaSpiritHealerPacket.Parse(body);
        var context = _areaSpiritHealerPending;
        if (context is null || packet.Guide != context.Guide || NowSeconds() >= _areaSpiritHealerDeadline ||
            !AreaSpiritHealerContextCurrent(context)) return;
        _areaSpiritHealerPending = null; // A duplicate or late reply cannot queue twice or reopen a dismissed dialog.
        _areaSpiritHealerShown = context;
        _areaSpiritHealerSawAura = false;
        _areaSpiritHealerAuraDeadline = NowSeconds() + 10;
        ExecuteStaticPopupPlan(StaticPopupCoordinatorLaw.Show(_staticPopupSlots,
            AreaSpiritHealerUiLaw.Definition(packet.RemainingMilliseconds), true));
        if (ConfirmPopupUiLaw.Visible(_staticPopupSlots, AreaSpiritHealerUiLaw.PopupType) is null)
            ResetAreaSpiritHealer();
    }

    private void UpdateAreaSpiritHealer()
    {
        if (_areaSpiritHealerPending is { } pending &&
            (NowSeconds() >= _areaSpiritHealerDeadline || !AreaSpiritHealerContextCurrent(pending)))
            _areaSpiritHealerPending = null;
        if (_areaSpiritHealerShown is not { } shown) return;
        if (!AreaSpiritHealerContextCurrent(shown)) { ResetAreaSpiritHealer(); return; }
        bool waiting = _entities.TryGet(ControlledGuid, out WorldEntity actor) &&
            actor.Fields.Auras().Any(aura => aura.SpellId == AreaSpiritHealerUiLaw.WaitingAura);
        if (waiting) _areaSpiritHealerSawAura = true;
        else if (_areaSpiritHealerSawAura || NowSeconds() >= _areaSpiritHealerAuraDeadline)
            ResetAreaSpiritHealer();
        // Health, ghost state and resurrection are exclusively server updates, never timer effects.
    }

    private void ApplyAreaSpiritHealerPopupEffect(StaticPopupCoordinatorLaw.Effect effect)
    {
        if (effect.Kind == StaticPopupCoordinatorLaw.EffectKind.OnShow)
        {
            if (_areaSpiritHealerShown is not { } context || !AreaSpiritHealerContextCurrent(context) ||
                RefuseTacticalFreezeLiveCommand("queuing for resurrection") ||
                RefuseTacticalFrozenActor(context.Guide, "queue for resurrection there") ||
                _net?.QueueAreaSpiritHealer(context.Guide) != true) ResetAreaSpiritHealer();
        }
        else if (effect.Kind == StaticPopupCoordinatorLaw.EffectKind.Accept)
        {
            if (_areaSpiritHealerShown is { } context && AreaSpiritHealerContextCurrent(context))
                _net?.CancelAura(AreaSpiritHealerUiLaw.WaitingAura);
        }
        else if (effect.Kind == StaticPopupCoordinatorLaw.EffectKind.OnHide)
        {
            _areaSpiritHealerPending = _areaSpiritHealerShown = null;
            _areaSpiritHealerSawAura = false;
        }
        // Escape/timeout/override have no OnCancel callback and do not cancel the waiting aura.
    }

    private string AreaSpiritHealerPromptText()
    {
        double seconds = ConfirmPopupUiLaw.Visible(_staticPopupSlots, AreaSpiritHealerUiLaw.PopupType)?.Instance.TimeLeft ?? 0;
        var time = AreaSpiritHealerUiLaw.Countdown(seconds);
        return InventoryGlobalString(AreaSpiritHealerUiLaw.PopupType, "Resurrection in %d %s")
            .Replace("%d", time.Amount.ToString(), StringComparison.Ordinal)
            .Replace("%s", InventoryGlobalString(time.Minutes ? "MINUTES" : "SECONDS", time.Minutes ? "Minutes" : "Seconds"), StringComparison.Ordinal);
    }
}
