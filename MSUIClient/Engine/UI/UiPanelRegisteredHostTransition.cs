namespace MSUIClient.Engine.UI;

/// <summary>Opens an ordinary left host alongside other registered panel owners.</summary>
public static class UiPanelRegisteredHostTransition
{
    public static UiPanelHostTransition.Result Show(
        UiPanelOwnershipObserver observer,
        Func<UiPanelOwnershipSample> capture,
        UiPanelHostTransition.Host incoming,
        Func<string, bool> closeRegistered)
    {
        UiPanelOwnershipSample initial = capture();
        UiPanelOwnershipObservation observation = observer.Observe(initial);
        if (observation.Confidence != UiPanelObservationConfidence.Known)
            return new(UiPanelHostTransition.Outcome.ObservationUnknown, observation);
        if (incoming.Panel.Area != UiPanelOwnershipLaw.Area.Left || incoming.Panel.Pushable != 0)
            return new(UiPanelHostTransition.Outcome.UnsupportedShape, observation);

        UiPanelOwnershipLaw.Transition planned = UiPanelOwnershipLaw.Show(observation.Seats,
            incoming.Panel, alreadyVisible: initial.VisibleRegistered.Contains(incoming.Panel),
            playerDeadOrGhost: initial.PlayerDeadOrGhost);
        UiPanelHostTransition.Outcome? stopped = planned.Outcome switch
        {
            UiPanelOwnershipLaw.Outcome.AlreadyVisible => UiPanelHostTransition.Outcome.AlreadyVisible,
            UiPanelOwnershipLaw.Outcome.RefusedByNativeCenter => UiPanelHostTransition.Outcome.RefusedByNativeCenter,
            UiPanelOwnershipLaw.Outcome.RefusedByFullscreen => UiPanelHostTransition.Outcome.RefusedByFullscreen,
            UiPanelOwnershipLaw.Outcome.RefusedWhileDead => UiPanelHostTransition.Outcome.RefusedWhileDead,
            _ => null,
        };
        if (stopped is { } outcome) return new(outcome, observation);
        if (!incoming.PreflightShow())
            return new(UiPanelHostTransition.Outcome.IncomingPreflightRefused, observation);
        UiPanelOwnershipSample preflight = capture();
        if (!SameCensus(initial, preflight))
            return new(UiPanelHostTransition.Outcome.CensusChangedDuringPreflight,
                observer.Observe(preflight));

        var remaining = initial.VisibleRegistered.ToList();
        foreach (UiPanelOwnershipLaw.Effect effect in planned.Effects)
        {
            if (effect.Kind != UiPanelOwnershipLaw.EffectKind.Hide || effect.PanelId is not { } id)
                continue;
            bool accepted = closeRegistered(id);
            UiPanelOwnershipSample afterClose = capture();
            observation = observer.Observe(afterClose);
            if (!accepted)
                return new(UiPanelHostTransition.Outcome.DisplacementCallbackFailed, observation);
            remaining.RemoveAll(panel => panel.Id == id);
            if (!SameCensus(new(remaining, initial.PlayerDeadOrGhost), afterClose))
                return new(UiPanelHostTransition.Outcome.DisplacementNotConfirmed, observation);
        }

        bool opened = incoming.Show();
        UiPanelOwnershipSample afterOpen = capture();
        // Confirm the actual final census against the plan, including a retained pushable host.
        // The observer validates descriptors and visible seats; a failed callback is never undone.
        observation = observer.ConfirmPlannedTransition(afterOpen, planned.Seats, planned.Effects,
            $"host-registered-show:{incoming.Panel.Id}");
        if (!opened)
            return new(UiPanelHostTransition.Outcome.OpenCallbackFailed, observation);
        if (observation.Confidence != UiPanelObservationConfidence.Known ||
            afterOpen.PlayerDeadOrGhost != initial.PlayerDeadOrGhost)
            return new(UiPanelHostTransition.Outcome.OpenNotConfirmed, observation);
        return new(UiPanelHostTransition.Outcome.Opened, observation);
    }

    private static bool SameCensus(UiPanelOwnershipSample expected, UiPanelOwnershipSample actual) =>
        expected.PlayerDeadOrGhost == actual.PlayerDeadOrGhost &&
        expected.UnresolvedReason == actual.UnresolvedReason &&
        expected.VisibleRegistered.Count == actual.VisibleRegistered.Count &&
        expected.VisibleRegistered.All(actual.VisibleRegistered.Contains);
}
