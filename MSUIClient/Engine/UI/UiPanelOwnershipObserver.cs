namespace MSUIClient.Engine.UI;

/// <summary>
/// Confidence carried by the read-only UI-panel ownership shadow. Unknown is deliberately
/// sticky: a later single edge cannot reconstruct which legacy host transition was missed.
/// </summary>
public enum UiPanelObservationConfidence
{
    Known,
    Unknown,
}

/// <summary>
/// One read-only census of registered host panels. Producers must leave unregistered and
/// special-purpose frames out of <see cref="VisibleRegistered"/> and report ambiguous registered
/// provenance through <see cref="UnresolvedReason"/> instead of guessing a descriptor.
/// </summary>
public sealed record UiPanelOwnershipSample(
    IReadOnlyList<UiPanelOwnershipLaw.Panel> VisibleRegistered,
    bool PlayerDeadOrGhost,
    string? UnresolvedReason = null);

/// <summary>
/// Result of observing a host census. Effects are advisory descriptions from the pure ownership
/// law; the observer never dispatches them. When confidence is Unknown, Seats is the last confirmed
/// seat state and must not be interpreted as the current host state.
/// </summary>
public sealed record UiPanelOwnershipObservation(
    UiPanelObservationConfidence Confidence,
    UiPanelOwnershipLaw.Seats Seats,
    IReadOnlyList<UiPanelOwnershipLaw.Effect> AdvisoryEffects,
    IReadOnlyList<string> VisibleIds,
    string Reason);

/// <summary>
/// Observation-only adapter between legacy MSUI visibility predicates and the frozen UI-panel law.
/// It reconstructs ownership only when exactly one registered visibility edge agrees with that law.
/// Ambiguity is latched until an independently observed all-closed census provides a safe baseline.
/// </summary>
public sealed class UiPanelOwnershipObserver
{
    private readonly Dictionary<string, UiPanelOwnershipLaw.Panel> _descriptors =
        new(StringComparer.Ordinal);
    private Dictionary<string, UiPanelOwnershipLaw.Panel> _visible =
        new(StringComparer.Ordinal);
    private UiPanelObservationConfidence _confidence;
    private UiPanelOwnershipLaw.Seats _seats;
    private string _unknownReason = "";

    public UiPanelOwnershipObserver() => _ = Reset();

    public UiPanelOwnershipObservation Reset()
    {
        _descriptors.Clear();
        _visible = new(StringComparer.Ordinal);
        _confidence = UiPanelObservationConfidence.Known;
        _seats = UiPanelOwnershipLaw.Seats.Empty;
        _unknownReason = "";
        return Known(_seats, [], [], "reset");
    }

    public UiPanelOwnershipObservation Observe(UiPanelOwnershipSample sample)
    {
        if (sample is null)
            return EnterUnknown([], "sample-null");
        if (sample.VisibleRegistered is null)
            return EnterUnknown([], "visible-registry-null");

        UiPanelOwnershipLaw.Panel[] currentRows = sample.VisibleRegistered.ToArray();
        string[] currentIds = currentRows.Select(row => row.Id).ToArray();

        if (sample.UnresolvedReason is not null)
            return EnterUnknown(currentIds, $"unresolved:{sample.UnresolvedReason}");

        // An all-closed census is independently authoritative. It needs no reconstruction of the
        // number or order of removals and is therefore the one safe recovery baseline.
        if (currentRows.Length == 0)
        {
            bool recovered = _confidence == UiPanelObservationConfidence.Unknown ||
                _visible.Count != 0;
            _visible = new(StringComparer.Ordinal);
            _seats = UiPanelOwnershipLaw.Seats.Empty;
            _confidence = UiPanelObservationConfidence.Known;
            _unknownReason = "";
            return Known(_seats, [], [], recovered ? "all-closed-recovery" : "unchanged");
        }

        if (_confidence == UiPanelObservationConfidence.Unknown)
            return Unknown(currentIds, _unknownReason);

        if (!TryValidateRows(currentRows, out Dictionary<string, UiPanelOwnershipLaw.Panel> current,
                out string validationReason))
            return EnterUnknown(currentIds, validationReason);

        string[] removed = _visible.Keys.Except(current.Keys, StringComparer.Ordinal).ToArray();
        string[] added = current.Keys.Except(_visible.Keys, StringComparer.Ordinal).ToArray();
        int edgeCount = removed.Length + added.Length;

        if (edgeCount == 0)
        {
            if (!SeatsMatchVisible(_seats, current))
                return EnterUnknown(currentIds, "seat-visible-inconsistency");
            _visible = current;
            return Known(_seats, [], currentIds, "unchanged");
        }

        if (edgeCount != 1)
            return EnterUnknown(currentIds, $"multiple-visibility-edges:{edgeCount}");

        UiPanelOwnershipLaw.Transition transition;
        if (added.Length == 1)
        {
            UiPanelOwnershipLaw.Panel panel = current[added[0]];
            transition = UiPanelOwnershipLaw.Show(_seats, panel,
                playerDeadOrGhost: sample.PlayerDeadOrGhost);
            if (transition.Outcome != UiPanelOwnershipLaw.Outcome.Opened)
                return EnterUnknown(currentIds, $"show-refused:{transition.Outcome}");
        }
        else
        {
            UiPanelOwnershipLaw.Panel panel = _visible[removed[0]];
            transition = UiPanelOwnershipLaw.Hide(_seats, panel);
            if (transition.Outcome != UiPanelOwnershipLaw.Outcome.Hidden)
                return EnterUnknown(currentIds, $"hide-refused:{transition.Outcome}");
        }

        if (!SeatsMatchVisible(transition.Seats, current))
            return EnterUnknown(currentIds, "law-seat-visible-inconsistency");

        _visible = current;
        _seats = transition.Seats;
        return Known(_seats, transition.Effects.ToArray(), currentIds,
            added.Length == 1 ? $"observed-show:{added[0]}" : $"observed-hide:{removed[0]}");
    }

    /// <summary>
    /// Commits a host transition that a coordinator already planned through
    /// <see cref="UiPanelOwnershipLaw"/> and then executed. This is the only
    /// multi-edge entrance: it still validates every descriptor and requires
    /// the final visible set to equal the planned seats exactly.
    /// </summary>
    public UiPanelOwnershipObservation ConfirmPlannedTransition(
        UiPanelOwnershipSample sample,
        UiPanelOwnershipLaw.Seats plannedSeats,
        IReadOnlyList<UiPanelOwnershipLaw.Effect> effects,
        string reason)
    {
        ArgumentNullException.ThrowIfNull(sample);
        ArgumentNullException.ThrowIfNull(effects);
        if (sample.VisibleRegistered is null)
            return EnterUnknown([], "planned-visible-registry-null");

        UiPanelOwnershipLaw.Panel[] rows = sample.VisibleRegistered.ToArray();
        string[] ids = rows.Select(row => row.Id).ToArray();
        if (sample.UnresolvedReason is not null)
            return EnterUnknown(ids, $"planned-unresolved:{sample.UnresolvedReason}");
        if (!TryValidateRows(rows, out Dictionary<string, UiPanelOwnershipLaw.Panel> current,
                out string validationReason))
            return EnterUnknown(ids, $"planned-{validationReason}");
        if (!SeatsMatchVisible(plannedSeats, current))
            return EnterUnknown(ids, "planned-seat-visible-inconsistency");

        _visible = current;
        _seats = plannedSeats;
        _confidence = UiPanelObservationConfidence.Known;
        _unknownReason = "";
        return Known(_seats, effects.ToArray(), ids, reason);
    }

    private bool TryValidateRows(
        IReadOnlyList<UiPanelOwnershipLaw.Panel> rows,
        out Dictionary<string, UiPanelOwnershipLaw.Panel> current,
        out string reason)
    {
        current = new(StringComparer.Ordinal);
        reason = "";
        foreach (UiPanelOwnershipLaw.Panel row in rows)
        {
            if (string.IsNullOrWhiteSpace(row.Id) ||
                row.Area == UiPanelOwnershipLaw.Area.Unregistered || row.Pushable < 0)
            {
                reason = $"invalid-registered-descriptor:{row.Id}";
                return false;
            }
            if (!current.TryAdd(row.Id, row))
            {
                reason = $"duplicate-visible-id:{row.Id}";
                return false;
            }
            if (_descriptors.TryGetValue(row.Id, out UiPanelOwnershipLaw.Panel known) && known != row)
            {
                reason = $"descriptor-inconsistency:{row.Id}";
                return false;
            }
        }

        foreach ((string id, UiPanelOwnershipLaw.Panel row) in current)
            _descriptors.TryAdd(id, row);
        return true;
    }

    private static bool SeatsMatchVisible(
        UiPanelOwnershipLaw.Seats seats,
        IReadOnlyDictionary<string, UiPanelOwnershipLaw.Panel> visible)
    {
        UiPanelOwnershipLaw.Panel[] occupied =
            [.. SeatRows(seats)];
        if (occupied.Select(panel => panel.Id).Distinct(StringComparer.Ordinal).Count() !=
            occupied.Length)
            return false;
        if (seats.Left is { Area: not UiPanelOwnershipLaw.Area.Left } ||
            seats.Center is { Area: UiPanelOwnershipLaw.Area.Unregistered or
                UiPanelOwnershipLaw.Area.Fullscreen } ||
            seats.Fullscreen is { Area: not UiPanelOwnershipLaw.Area.Fullscreen })
            return false;
        if (occupied.Length != visible.Count)
            return false;
        return occupied.All(panel => visible.TryGetValue(panel.Id, out var row) && row == panel);
    }

    private static IEnumerable<UiPanelOwnershipLaw.Panel> SeatRows(
        UiPanelOwnershipLaw.Seats seats)
    {
        if (seats.Left is { } left) yield return left;
        if (seats.Center is { } center) yield return center;
        if (seats.Fullscreen is { } fullscreen) yield return fullscreen;
    }

    private UiPanelOwnershipObservation EnterUnknown(
        IReadOnlyList<string> visibleIds,
        string reason)
    {
        _confidence = UiPanelObservationConfidence.Unknown;
        _unknownReason = reason;
        return Unknown(visibleIds, reason);
    }

    private UiPanelOwnershipObservation Unknown(
        IReadOnlyList<string> visibleIds,
        string reason) =>
        new(UiPanelObservationConfidence.Unknown, _seats, [], visibleIds.ToArray(), reason);

    private static UiPanelOwnershipObservation Known(
        UiPanelOwnershipLaw.Seats seats,
        IReadOnlyList<UiPanelOwnershipLaw.Effect> effects,
        IReadOnlyList<string> visibleIds,
        string reason) =>
        new(UiPanelObservationConfidence.Known, seats, effects, visibleIds.ToArray(), reason);
}
