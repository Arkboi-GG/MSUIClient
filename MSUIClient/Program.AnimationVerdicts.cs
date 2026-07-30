using MSUIClient.Engine;
using MSUIClient.World.Units;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private readonly Dictionary<(string Unit, int Track),
        (int Requested, int Played, AnimChoiceKind Kind)> _lastAnimChoices = [];

    private void CaptureAnimationChoice(string unit, int track, M2Animator.Resolution resolution)
    {
        AnimChoiceKind kind = resolution.Kind switch
        {
            M2Animator.ResolutionKind.Exact => AnimChoiceKind.Exact,
            M2Animator.ResolutionKind.BakedOnDemand => AnimChoiceKind.BakedOnDemand,
            M2Animator.ResolutionKind.Fallback => AnimChoiceKind.Fallback,
            M2Animator.ResolutionKind.Missing => AnimChoiceKind.Missing,
            M2Animator.ResolutionKind.Substituted => AnimChoiceKind.Substituted,
            _ => AnimChoiceKind.Missing,
        };
        var key = (unit, track);
        var state = (resolution.RequestedId, resolution.PlayedId, kind);
        bool changed = !_lastAnimChoices.TryGetValue(key, out var previous) || previous != state;
        _lastAnimChoices[key] = state;
        if (!changed) return;

        var verdict = new AnimChoice(
            NowSeconds(), unit, track, resolution.RequestedId, resolution.PlayedId, kind);
        _verdicts.Add(verdict);
        if (kind is AnimChoiceKind.Fallback or AnimChoiceKind.Missing or
            AnimChoiceKind.Substituted)
            Console.WriteLine($"[verdict:anim] {verdict.ToLine()}");
    }
}
