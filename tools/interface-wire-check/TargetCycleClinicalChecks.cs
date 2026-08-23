using System.Numerics;
using MSUIClient;
using MSUIClient.Engine.UI;

internal static class TargetCycleClinicalChecks
{
    public static void Run()
    {
        CheckScoringAndTiering();
        CheckHistorySelection();
        CheckRuntimeSourceFence();
    }

    private static void CheckScoringAndTiering()
    {
        Vector2 viewport = new(1000, 800);
        Check(TargetCycleLaw.ScreenOffCenter(new Vector2(500, 400), viewport) == 0f &&
              TargetCycleLaw.ScreenOffCenter(new Vector2(100, 80), viewport).HasValue &&
              !TargetCycleLaw.ScreenOffCenter(new Vector2(99, 80), viewport).HasValue &&
              !TargetCycleLaw.ScreenOffCenter(new Vector2(500, 721), viewport).HasValue,
            "target cycle pulled-in viewport law drifted");

        float peaceful = TargetCycleLaw.PriorityScore(.5f, 20.5f, false);
        float fighting = TargetCycleLaw.PriorityScore(.5f, 20.5f, true);
        Check(MathF.Abs(peaceful - 1f) < .0001f &&
              MathF.Abs(fighting + 2f) < .0001f,
            "target cycle screen/distance/combat weighting drifted");

        List<TargetCycleLaw.Candidate> pool = TargetCycleLaw.SortedPool([
            new(1, false, -2.5f),
            new(2, true, 1.2f),
            new(3, true, .2f),
        ]);
        Check(pool.Select(candidate => candidate.Guid).SequenceEqual([3ul, 2ul]),
            "target cycle on-screen tier no longer excludes fallback candidates");
        List<TargetCycleLaw.Candidate> fallback = TargetCycleLaw.SortedPool([
            new(4, false, .8f), new(5, false, .2f),
        ]);
        Check(fallback.Select(candidate => candidate.Guid).SequenceEqual([5ul, 4ul]),
            "target cycle fallback score order drifted");
    }

    private static void CheckHistorySelection()
    {
        List<TargetCycleLaw.Candidate> pool = [
            new(10, true, 0), new(20, true, 1), new(30, true, 2),
        ];
        Check(TargetCycleLaw.Select(pool, [10ul], 10, reverse: false) ==
                  new TargetCycleLaw.Pick(20, false) &&
              TargetCycleLaw.Select(pool, [10ul, 20ul, 30ul], 30, reverse: false) ==
                  new TargetCycleLaw.Pick(10, true) &&
              TargetCycleLaw.Select(pool, [10ul, 20ul, 30ul], 30, reverse: true) ==
                  new TargetCycleLaw.Pick(20, false) &&
              TargetCycleLaw.Select(pool, [10ul], 30, reverse: true) ==
                  new TargetCycleLaw.Pick(10, false),
            "target cycle forward/wrap/reverse selection drifted");

        var history = new TargetCycleHistory();
        history.Push(10, 1);
        history.Push(20, 2);
        history.Push(10, 3);
        Check(history.Guids.SequenceEqual([20ul, 10ul]),
            "target cycle revisit no longer becomes most recent");
        history.Prune(6.0);
        Check(history.Guids.SequenceEqual([10ul]),
            "target cycle four-second history pruning drifted");
        history.Clear();
        Check(history.Guids.Count == 0, "target cycle history clear drifted");
    }

    private static void CheckRuntimeSourceFence()
    {
        string root = ClientConfig.FindRepoRoot();
        string bindings = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop",
            "Panels", "GameLoop.Bindings.cs"));
        string targeting = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop",
            "Combat", "GameLoop.Targeting.cs"));
        Check(bindings.Contains(
                  "GameBinding.TargetPreviousEnemy, \"Target Previous Enemy\", Key.Tab",
                  StringComparison.Ordinal) &&
              bindings.Contains("new BindingChord(Key.Tab, Shift: true)",
                  StringComparison.Ordinal) &&
              bindings.Contains("CycleEnemyTarget(reverse: true)", StringComparison.Ordinal) &&
              bindings.Contains("TargetCycleLaw.SortedPool(candidates)", StringComparison.Ordinal) &&
              bindings.Contains("unit.Position + new Vector3(0f, 0f, 1f)",
                  StringComparison.Ordinal) &&
              bindings.Contains("query?.CreatureType == 8", StringComparison.Ordinal) &&
              bindings.Contains("unit.Fields.Target == ControlledGuid && unit.Fields.InCombat",
                  StringComparison.Ordinal) &&
              targeting.Contains("_targetCycleHistory.Clear();", StringComparison.Ordinal),
            "target cycle runtime escaped its scored history law or reset boundary");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
