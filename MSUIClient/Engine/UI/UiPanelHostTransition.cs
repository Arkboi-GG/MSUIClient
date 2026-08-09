namespace MSUIClient.Engine.UI;

/// <summary>
/// Host-confirmed authority for one bounded pair of ordinary, zero-push left panels. The
/// coordinator owns no visibility flag: every state advance comes from an observed host census
/// after a caller-supplied preflight or mutation callback.
/// </summary>
public static class UiPanelHostTransition
{
    public enum Outcome
    {
        Opened,
        AlreadyVisible,
        ObservationUnknown,
        RefusedByNativeCenter,
        RefusedByFullscreen,
        RefusedWhileDead,
        UnsupportedShape,
        IncomingPreflightRefused,
        DisplacedPreflightRefused,
        CensusChangedDuringPreflight,
        DisplacementCallbackFailed,
        DisplacementNotConfirmed,
        OpenCallbackFailed,
        OpenNotConfirmed,
    }

    /// <summary>
    /// Existing host behavior for one panel. Preflights must be read-only. Show and Displace keep
    /// ownership of the native flag, sound, payload, and any surface-specific side effects.
    /// </summary>
    public sealed record Host(
        UiPanelOwnershipLaw.Panel Panel,
        Func<bool> PreflightShow,
        Func<bool> Show,
        Func<bool> PreflightDisplace,
        Func<bool> Displace);

    public readonly record struct Result(
        Outcome Outcome,
        UiPanelOwnershipObservation Observation)
    {
        public bool Succeeded => Outcome is Outcome.Opened or Outcome.AlreadyVisible;
    }

    /// <summary>
    /// Shows <paramref name="incoming"/> through the frozen ownership law, but never treats the
    /// law's planned seats as committed. Only censuses confirmed by <paramref name="observer"/>
    /// advance authority. A failed host callback is reported truthfully and is never rolled back.
    /// </summary>
    public static Result Show(
        UiPanelOwnershipObserver observer,
        Func<UiPanelOwnershipSample> capture,
        Host incoming,
        IReadOnlyList<Host> supportedHosts)
    {
        ArgumentNullException.ThrowIfNull(observer);
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(incoming);
        ArgumentNullException.ThrowIfNull(supportedHosts);

        UiPanelOwnershipSample initialSample = capture();
        UiPanelOwnershipObservation observation = observer.Observe(initialSample);
        if (observation.Confidence != UiPanelObservationConfidence.Known)
            return new(Outcome.ObservationUnknown, observation);

        if (!TryBuildSupportedMap(supportedHosts, incoming,
                out Dictionary<string, Host> supported))
            return new(Outcome.UnsupportedShape, observation);

        bool alreadyVisible = observation.VisibleIds.Contains(incoming.Panel.Id,
            StringComparer.Ordinal);
        UiPanelOwnershipLaw.Seats initialSeats = observation.Seats;
        UiPanelOwnershipLaw.Transition planned = UiPanelOwnershipLaw.Show(
            initialSeats, incoming.Panel, alreadyVisible: alreadyVisible, force: false,
            playerDeadOrGhost: initialSample.PlayerDeadOrGhost);

        Outcome? refusal = planned.Outcome switch
        {
            UiPanelOwnershipLaw.Outcome.RefusedByNativeCenter => Outcome.RefusedByNativeCenter,
            UiPanelOwnershipLaw.Outcome.RefusedByFullscreen => Outcome.RefusedByFullscreen,
            UiPanelOwnershipLaw.Outcome.RefusedWhileDead => Outcome.RefusedWhileDead,
            _ => null,
        };
        if (refusal is { } refused)
            return new(refused, observation);

        if (!TryResolveSingleLeftShape(observation, planned, incoming, supported,
                out Host? displaced))
            return new(Outcome.UnsupportedShape, observation);
        if (planned.Outcome == UiPanelOwnershipLaw.Outcome.AlreadyVisible)
            return new(Outcome.AlreadyVisible, observation);

        if (!incoming.PreflightShow())
            return new(Outcome.IncomingPreflightRefused, observation);
        if (displaced is not null && !displaced.PreflightDisplace())
            return new(Outcome.DisplacedPreflightRefused, observation);

        UiPanelOwnershipSample afterPreflight = capture();
        observation = observer.Observe(afterPreflight);
        if (!SamplesEqual(initialSample, afterPreflight) ||
            !Confirmed(observation, afterPreflight, initialSample, initialSeats))
            return new(Outcome.CensusChangedDuringPreflight, observation);

        if (displaced is not null)
        {
            bool callbackAccepted = displaced.Displace();
            UiPanelOwnershipSample afterDisplacement = capture();
            observation = observer.Observe(afterDisplacement);
            if (!callbackAccepted)
                return new(Outcome.DisplacementCallbackFailed, observation);

            UiPanelOwnershipLaw.Seats expectedAfterDisplacement =
                UiPanelOwnershipLaw.Hide(initialSeats, displaced.Panel).Seats;
            if (!Confirmed(observation, afterDisplacement, initialSample,
                    expectedAfterDisplacement))
                return new(Outcome.DisplacementNotConfirmed, observation);
        }

        bool openAccepted = incoming.Show();
        UiPanelOwnershipSample afterOpen = capture();
        observation = observer.Observe(afterOpen);
        if (!openAccepted)
            return new(Outcome.OpenCallbackFailed, observation);
        if (!Confirmed(observation, afterOpen, initialSample, planned.Seats))
            return new(Outcome.OpenNotConfirmed, observation);

        return new(Outcome.Opened, observation);
    }

    private static bool TryBuildSupportedMap(
        IReadOnlyList<Host> hosts,
        Host incoming,
        out Dictionary<string, Host> supported)
    {
        supported = new(StringComparer.Ordinal);
        if (hosts.Count is < 1 or > 2)
            return false;

        foreach (Host host in hosts)
        {
            if (!IsOrdinaryLeft(host.Panel) || host.PreflightShow is null || host.Show is null ||
                host.PreflightDisplace is null || host.Displace is null ||
                !supported.TryAdd(host.Panel.Id, host))
                return false;
        }

        return IsOrdinaryLeft(incoming.Panel) &&
            supported.TryGetValue(incoming.Panel.Id, out Host? registered) &&
            registered.Panel == incoming.Panel;
    }

    private static bool TryResolveSingleLeftShape(
        UiPanelOwnershipObservation observation,
        UiPanelOwnershipLaw.Transition planned,
        Host incoming,
        IReadOnlyDictionary<string, Host> supported,
        out Host? displaced)
    {
        displaced = null;
        if (observation.Seats.Center is not null || observation.Seats.Fullscreen is not null)
            return false;

        if (observation.Seats.Left is { } left)
        {
            if (!IsOrdinaryLeft(left) ||
                !supported.TryGetValue(left.Id, out displaced) || displaced.Panel != left)
                return false;
        }

        if (planned.Outcome == UiPanelOwnershipLaw.Outcome.AlreadyVisible)
            return displaced is not null && displaced.Panel == incoming.Panel &&
                planned.Seats == observation.Seats && planned.Effects.Count == 0;
        if (planned.Outcome != UiPanelOwnershipLaw.Outcome.Opened ||
            planned.Seats != new UiPanelOwnershipLaw.Seats(incoming.Panel, null, null))
            return false;

        UiPanelOwnershipLaw.Effect[] expected = displaced is null
            ? [
                new(UiPanelOwnershipLaw.EffectKind.AnchorLeft, incoming.Panel.Id),
                new(UiPanelOwnershipLaw.EffectKind.Show, incoming.Panel.Id),
            ]
            : [
                new(UiPanelOwnershipLaw.EffectKind.Hide, displaced.Panel.Id),
                new(UiPanelOwnershipLaw.EffectKind.AnchorLeft, incoming.Panel.Id),
                new(UiPanelOwnershipLaw.EffectKind.Show, incoming.Panel.Id),
            ];
        return planned.Effects.SequenceEqual(expected);
    }

    private static bool Confirmed(
        UiPanelOwnershipObservation observation,
        UiPanelOwnershipSample sample,
        UiPanelOwnershipSample initialSample,
        UiPanelOwnershipLaw.Seats expectedSeats)
    {
        if (observation.Confidence != UiPanelObservationConfidence.Known ||
            observation.Seats != expectedSeats || sample.PlayerDeadOrGhost !=
                initialSample.PlayerDeadOrGhost || sample.UnresolvedReason is not null)
            return false;

        UiPanelOwnershipLaw.Panel[] expectedVisible =
        [
            .. SeatRows(expectedSeats),
        ];
        return sample.VisibleRegistered.Count == expectedVisible.Length &&
            expectedVisible.All(expected => sample.VisibleRegistered.Contains(expected));
    }

    private static bool SamplesEqual(
        UiPanelOwnershipSample left,
        UiPanelOwnershipSample right) =>
        left.PlayerDeadOrGhost == right.PlayerDeadOrGhost &&
        string.Equals(left.UnresolvedReason, right.UnresolvedReason,
            StringComparison.Ordinal) &&
        left.VisibleRegistered.SequenceEqual(right.VisibleRegistered);

    private static IEnumerable<UiPanelOwnershipLaw.Panel> SeatRows(
        UiPanelOwnershipLaw.Seats seats)
    {
        if (seats.Left is { } left) yield return left;
        if (seats.Center is { } center) yield return center;
        if (seats.Fullscreen is { } fullscreen) yield return fullscreen;
    }

    private static bool IsOrdinaryLeft(UiPanelOwnershipLaw.Panel panel) =>
        !string.IsNullOrWhiteSpace(panel.Id) && panel.Area == UiPanelOwnershipLaw.Area.Left &&
        panel.Pushable == 0 && !panel.WhileDead;
}
