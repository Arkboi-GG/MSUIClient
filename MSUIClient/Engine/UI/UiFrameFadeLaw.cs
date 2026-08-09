namespace MSUIClient.Engine.UI;

/// <summary>
/// Pure UIFrameFade state machine from frozen UIParent.lua/UiPanels.xml. It exposes alpha and
/// callback timing without owning a rendered frame or callback delegate.
/// </summary>
public static class UiFrameFadeLaw
{
    public enum Mode
    {
        In,
        Out,
    }

    public readonly record struct State(
        Mode Mode,
        double DurationSeconds,
        float StartAlpha,
        float EndAlpha,
        double TimerSeconds,
        double HoldSeconds,
        bool HasFinishedCallback);

    public readonly record struct StartPlan(
        State State,
        float Alpha,
        bool ShowFrame,
        bool AddedToRegistry);

    /// <summary>
    /// Starts or replaces a fade. The frozen function assigns the new fadeInfo and start alpha
    /// before checking whether the frame is already in FADEFRAMES. Therefore an already-fading
    /// frame is reset to the new start state but is not shown/inserted a second time.
    /// </summary>
    public static StartPlan Start(
        Mode mode = Mode.In,
        double durationSeconds = 0,
        float? startAlpha = null,
        float? endAlpha = null,
        double holdSeconds = 0,
        bool hasFinishedCallback = false,
        bool alreadyRegistered = false,
        double initialTimerSeconds = 0)
    {
        float start = startAlpha ?? (mode == Mode.In ? 0f : 1f);
        float end = endAlpha ?? (mode == Mode.In ? 1f : 0f);
        var state = new State(
            mode,
            durationSeconds,
            start,
            end,
            initialTimerSeconds,
            holdSeconds,
            hasFinishedCallback);
        return new(state, start, !alreadyRegistered, !alreadyRegistered);
    }

    public readonly record struct Step(
        State? State,
        float Alpha,
        bool RemovedFromRegistry,
        bool InvokeFinishedCallback);

    /// <summary>
    /// Advances one fade. Once the end is reached, a positive hold consumes the full frame elapsed
    /// (not just time beyond the fade boundary) and remains registered until a later tick observes
    /// a non-positive hold. This slightly surprising behavior is the frozen executable law.
    /// </summary>
    public static Step Advance(State state, double elapsedSeconds)
    {
        if (elapsedSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(elapsedSeconds));

        double timer = state.TimerSeconds + elapsedSeconds;
        State timed = state with { TimerSeconds = timer };
        if (timer < state.DurationSeconds)
        {
            double fraction = state.DurationSeconds == 0 ? 1 : timer / state.DurationSeconds;
            float alpha = (float)(state.StartAlpha +
                fraction * (state.EndAlpha - state.StartAlpha));
            return new(timed, alpha, false, false);
        }

        if (state.HoldSeconds > 0)
        {
            // The condition is tested before subtraction, so crossing zero completes next tick.
            State held = timed with { HoldSeconds = state.HoldSeconds - elapsedSeconds };
            return new(held, state.EndAlpha, false, false);
        }

        return new(null, state.EndAlpha, true, state.HasFinishedCallback);
    }

    public readonly record struct RegistryEntry(string Id, State State);
    public readonly record struct RegistryObservation(
        string Id,
        float Alpha,
        bool Removed,
        bool InvokeFinishedCallback);
    public readonly record struct RegistryStep(
        IReadOnlyList<RegistryEntry> Entries,
        IReadOnlyList<RegistryObservation> Observations);

    /// <summary>
    /// Reproduces the frozen while-index registry tick exactly. Removing entry i shifts the next
    /// entry into i, but the loop still increments, so that shifted entry waits until the next
    /// frame. This is observable snapshot behavior and is intentionally not normalized here.
    /// </summary>
    public static RegistryStep AdvanceRegistry(
        IReadOnlyList<RegistryEntry> entries,
        double elapsedSeconds)
    {
        var active = entries.ToList();
        var observations = new List<RegistryObservation>();
        int index = 0;
        while (index < active.Count)
        {
            RegistryEntry entry = active[index];
            Step step = Advance(entry.State, elapsedSeconds);
            observations.Add(new(entry.Id, step.Alpha, step.RemovedFromRegistry,
                step.InvokeFinishedCallback));
            if (step.State is { } next)
                active[index] = entry with { State = next };
            else
                active.RemoveAt(index);
            index++;
        }
        return new(active, observations);
    }

    /// <summary>UIFrameFadeRemoveFrame removes every duplicate occurrence, not only the first.</summary>
    public static IReadOnlyList<RegistryEntry> RemoveFrame(
        IReadOnlyList<RegistryEntry> entries,
        string id) =>
        entries.Where(entry => !string.Equals(entry.Id, id, StringComparison.Ordinal)).ToArray();

    public static bool IsFading(IReadOnlyList<RegistryEntry> entries, string id) =>
        entries.Any(entry => string.Equals(entry.Id, id, StringComparison.Ordinal));
}
