using System.Numerics;
using MSUIClient.Net;
using MSUIClient.World.Encounters;

namespace EncounterLabCheck;

// ─────────────────────────────────────────────────────────────────────────────
// Headless verification for the Encounter Lab subsystem.
//
//   dotnet run --project tools/encounter-lab-check
//
// Exits non-zero on the first failed assertion. Everything here is pure: no GL,
// no MPQ, no network — which is itself a check that the subsystem stayed free of
// those dependencies.
// ─────────────────────────────────────────────────────────────────────────────

internal static class Program
{
    private static int _passed, _failed;

    private static int Main(string[] args)
    {
        string repoRoot = args.Length > 0
            ? args[0]
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

        Console.WriteLine($"encounter-lab-check  (repo root: {repoRoot})");
        Console.WriteLine(new string('-', 70));

        GeometryChecks();
        SimulatorChecks();
        PositioningSlotChecks();
        ProbeChecks();
        TranslatorChecks();
        LibraryChecks(repoRoot);
        OnyxiaChecks(repoRoot);

        // Opt-in: hits MangosSuperUI's read-only CSV export and validates the parsers
        // against REAL rows. This is where unit conversions and column names actually
        // get proven — a parser that is self-consistent but wrong about the schema
        // passes every offline test.
        int liveAt = Array.FindIndex(args, a => a.Equals("--live", StringComparison.OrdinalIgnoreCase));
        if (liveAt >= 0)
        {
            string baseUrl = liveAt + 1 < args.Length ? args[liveAt + 1] : "http://192.168.0.2:5000";
            LiveDataChecks(repoRoot, baseUrl).GetAwaiter().GetResult();
        }
        else Console.WriteLine("\n(skipping live DB checks — pass --live [baseUrl] to run them)");

        Console.WriteLine(new string('-', 70));
        Console.WriteLine($"{_passed} passed, {_failed} failed");
        return _failed == 0 ? 0 : 1;
    }

    // ── geometry ─────────────────────────────────────────────────────────────

    private static void GeometryChecks()
    {
        Section("geometry");

        // A rear cone must cover what is BEHIND the caster and nothing in front.
        // Tail Sweep's -120 is the canonical case and the sign is load-bearing.
        var tailSweep = Ability("tail_sweep", 15847, FootprintKind.Cone, coneDegrees: -120f, radius: 15f);
        Vector3 boss = new(0f, 0f, 0f);
        float facingNorth = 0f;   // +X

        Footprint rear = EncounterGeometryLaw.Resolve(tailSweep, boss, facingNorth, null, null);
        Check("rear cone hits directly behind",
            EncounterGeometryLaw.Test(rear, BodyCapsule.At(new Vector3(-10f, 0f, 0f))).Covered);
        Check("rear cone misses directly in front",
            !EncounterGeometryLaw.Test(rear, BodyCapsule.At(new Vector3(10f, 0f, 0f))).Covered);
        Check("rear cone misses at 90 degrees",
            !EncounterGeometryLaw.Test(rear, BodyCapsule.At(new Vector3(0f, 10f, 0f))).Covered);
        Check("rear cone misses beyond its radius",
            !EncounterGeometryLaw.Test(rear, BodyCapsule.At(new Vector3(-40f, 0f, 0f))).Covered);

        // A front cone is the same maths with the sign flipped.
        var frontCone = Ability("front", 1, FootprintKind.Cone, coneDegrees: 90f, radius: 15f);
        Footprint front = EncounterGeometryLaw.Resolve(frontCone, boss, facingNorth, null, null);
        Check("front cone hits in front",
            EncounterGeometryLaw.Test(front, BodyCapsule.At(new Vector3(10f, 0f, 0f))).Covered);
        Check("front cone misses behind",
            !EncounterGeometryLaw.Test(front, BodyCapsule.At(new Vector3(-10f, 0f, 0f))).Covered);

        // A body is not a point: its width buys angular slack, so a capsule that
        // straddles the arc edge is covered even though its centre is outside.
        Footprint narrow = EncounterGeometryLaw.Resolve(
            Ability("narrow", 2, FootprintKind.Cone, coneDegrees: 20f, radius: 20f),
            boss, facingNorth, null, null);
        FootprintHit grazed = EncounterGeometryLaw.Test(
            narrow, BodyCapsule.At(new Vector3(5f, 1.0f, 0f), radius: 1.5f));
        FootprintHit pointish = EncounterGeometryLaw.Test(
            narrow, BodyCapsule.At(new Vector3(5f, 1.0f, 0f), radius: 0.01f));
        Check("wide body grazes an arc a point body misses",
            grazed.Covered && !pointish.Covered);

        // Height matters: Onyxia hovering 22 yd up is exactly why.
        Footprint circle = EncounterGeometryLaw.Resolve(
            Ability("circle", 3, FootprintKind.Circle, radius: 20f), boss, 0f, boss, null);
        Check("circle misses a body far above the effect plane",
            EncounterGeometryLaw.Test(circle, BodyCapsule.At(new Vector3(0f, 0f, 40f))).Verdict
                == FootprintVerdict.WrongHeight);
        Check("circle hits a body on the plane",
            EncounterGeometryLaw.Test(circle, BodyCapsule.At(new Vector3(5f, 0f, 0f))).Covered);

        // Point chains: a body on any sphere is covered.
        var lane = Ability("lane", 4, FootprintKind.PointChain, radius: 6f);
        lane = lane with
        {
            Geometry = lane.Geometry with
            {
                Points = new[] { new Vector3(0, 0, 0), new Vector3(10, 0, 0), new Vector3(20, 0, 0) },
            },
        };
        Footprint chain = EncounterGeometryLaw.Resolve(lane, boss, 0f, null, null);
        Check("chain covers a body beside a middle point",
            EncounterGeometryLaw.Test(chain, BodyCapsule.At(new Vector3(11f, 3f, 0f))).Covered);
        Check("chain misses a body off the lane",
            !EncounterGeometryLaw.Test(chain, BodyCapsule.At(new Vector3(10f, 25f, 0f))).Covered);

        // The miss report has to carry usable numbers, not just "no".
        FootprintHit missed = EncounterGeometryLaw.Test(chain, BodyCapsule.At(new Vector3(10f, 12f, 0f)));
        Check("near miss reports how much room was left",
            !missed.Covered && missed.ClearanceYards is > 5f and < 7f);

        // Line/capsule sweep.
        var charge = Ability("charge", 5, FootprintKind.Line, width: 6f, radius: 30f);
        Footprint line = EncounterGeometryLaw.Resolve(
            charge, boss, 0f, new Vector3(30f, 0f, 0f), null);
        Check("line covers a body on the path",
            EncounterGeometryLaw.Test(line, BodyCapsule.At(new Vector3(15f, 2f, 0f))).Covered);
        Check("line misses a body beside the path",
            !EncounterGeometryLaw.Test(line, BodyCapsule.At(new Vector3(15f, 12f, 0f))).Covered);
    }

    // ── simulator ────────────────────────────────────────────────────────────

    private static void SimulatorChecks()
    {
        Section("simulator");

        EncounterDefinition definition = TwoPhaseTestEncounter();
        List<EncounterActorSpec> scenario = Scenario();

        var a = new EncounterSim(definition, scenario, new EncounterSimOptions { Seed = 12345 });
        var b = new EncounterSim(definition, scenario, new EncounterSimOptions { Seed = 12345 });
        a.AdvanceTo(60_000);
        b.AdvanceTo(60_000);

        // Determinism is the whole premise: same seed, same fight, forever.
        Check("same seed produces the same event count", a.Events.Count == b.Events.Count);
        Check("same seed produces identical event stream",
            a.Events.Zip(b.Events).All(pair =>
                pair.First.TimeMs == pair.Second.TimeMs &&
                pair.First.Kind == pair.Second.Kind &&
                pair.First.Text == pair.Second.Text));

        var c = new EncounterSim(definition, scenario, new EncounterSimOptions { Seed = 999 });
        c.AdvanceTo(60_000);
        Check("a different seed produces a different fight",
            !a.Events.Select(e => (e.TimeMs, e.Text))
                .SequenceEqual(c.Events.Select(e => (e.TimeMs, e.Text))));

        // Rewind: restoring a snapshot must reproduce that instant exactly.
        int midpoint = a.Timeline.Count / 2;
        SimSnapshot expected = a.Timeline[midpoint];
        a.RestoreTo(midpoint);
        Check("rewind restores the clock", a.TimeMs == expected.TimeMs);
        Check("rewind restores the phase", a.PhaseKey == expected.PhaseKey);
        Check("rewind restores every actor position",
            a.Actors.All(actor =>
            {
                SimActorState state = expected.Actors.First(s => s.Key == actor.Key);
                return Vector3.Distance(actor.Position, state.Position) < 1e-4f &&
                       actor.Health == state.Health;
            }));

        // A snapshot per step is what makes scrubbing an index rather than a re-run.
        Check("timeline has one snapshot per step plus the initial capture",
            a.Timeline.Count == a.Timeline[^1].Step + 1);

        // Ordered-route render state is part of the snapshot too. The overlay draws only
        // unfired entries plus ActiveOrderedMoveIndex, so a completed first leg must remain
        // retired when the user scrubs while the second leg is underway.
        List<EncounterActorSpec> routeScenario = Scenario();
        routeScenario[1] = routeScenario[1] with
        {
            PlayerRules = new EncounterPlayerRules(AlwaysFaceBoss: true),
            Moves =
            [
                new TimedMove(0, new Vector3(13f, 0f, 0f)),
                new TimedMove(0, new Vector3(20f, 0f, 0f), MoveAnchor.AfterPrevious),
            ],
        };
        var routed = new EncounterSim(definition, routeScenario,
            new EncounterSimOptions { Seed = 17, StepMs = 100 });
        routed.AdvanceTo(5_000);
        int secondLegIndex = routed.Timeline.ToList().FindIndex(snapshot =>
            snapshot.Actors.First(state => state.Key == "tank").ActiveOrderedMoveIndex == 1);
        Check("ordered route reaches its second leg", secondLegIndex >= 0);
        if (secondLegIndex >= 0)
        {
            SimActorState secondLeg = routed.Timeline[secondLegIndex].Actors
                .First(state => state.Key == "tank");
            Check("completed route leg is snapshotted as retired",
                secondLeg.FiredOrderedMoves is [true, true] &&
                secondLeg.ActiveOrderedMoveIndex == 1);
            SimActorState bossOnSecondLeg = routed.Timeline[secondLegIndex].Actors
                .First(state => state.Key == "boss");
            float faceBoss = EncounterGeometryLaw.Facing(secondLeg.Position, bossOnSecondLeg.Position);
            float facingError = MathF.Abs(MathF.Atan2(
                MathF.Sin(secondLeg.Facing - faceBoss), MathF.Cos(secondLeg.Facing - faceBoss)));
            Check("always-face-boss rule holds while running", facingError < 1e-4f);
            routed.RestoreTo(secondLegIndex);
            SimActor restoredTank = routed.Actors.First(actor => actor.Key == "tank");
            Check("rewind restores ordered-route render state",
                restoredTank.ActiveOrderedMoveIndex == 1 &&
                restoredTank.FiredOrderedMoves is [true, true] &&
                restoredTank.MoveTarget == new Vector3(20f, 0f, 0f));
        }

        // Reusable combat plans resolve semantic subjects by actor key, never by
        // scenario insertion order. This scenario deliberately lists tank-z first.
        var doctrinePlan = new CombatPlan(
            Name: "tank healer",
            Movement: new CombatMovementPlan(
                CombatMovementMode.Follow, CombatSubject.Tank(2), 5f, 10f),
            SupportPriorities:
            [
                new CombatSupportPriority(CombatSubject.Tank(1), 50f),
                new CombatSupportPriority(CombatSubject.Tank(2), 90f),
            ],
            EnemyPriorities:
            [
                new CombatEnemyPriority(CombatEnemyKind.AnyAdd),
                new CombatEnemyPriority(CombatEnemyKind.CurrentEnemy),
                new CombatEnemyPriority(CombatEnemyKind.PrimaryEnemy),
            ]);
        List<EncounterActorSpec> doctrineScenario =
        [
            new("boss", "Boss", 1, EncounterActorRole.Boss, Vector3.Zero,
                MaxHealth: 100_000),
            new("tank-z", "second tank", 0, EncounterActorRole.Friendly,
                new Vector3(12f, 0f, 0f), Job: RaidJob.Tank),
            new("tank-a", "first tank", 0, EncounterActorRole.Friendly,
                new Vector3(10f, 0f, 0f), Job: RaidJob.Tank),
            new("healer", "healer", 0, EncounterActorRole.Friendly,
                new Vector3(32f, 0f, 0f), Job: RaidJob.Healer,
                PlayerRules: new EncounterPlayerRules(Plan: doctrinePlan)),
            new("add-z", "second add", 2, EncounterActorRole.Add,
                new Vector3(4f, 4f, 0f)),
            new("add-a", "first add", 2, EncounterActorRole.Add,
                new Vector3(4f, -4f, 0f)),
        ];
        var doctrine = new EncounterSim(definition, doctrineScenario,
            new EncounterSimOptions
            {
                Seed = 18,
                StepMs = 100,
                RaidDpsFraction = 0f,
                PullRangeYards = 100f,
            });
        SimActor doctrineHealer = doctrine.Actors.First(actor => actor.Key == "healer");
        Check("tank ordinals resolve by actor key, not insertion order",
            doctrineHealer.CurrentFollowTargetKey == "tank-z");
        Check("add priority resolves the first alive add by actor key",
            doctrineHealer.CurrentEnemyTargetKey == "add-a");

        SimActor firstTank = doctrine.Actors.First(actor => actor.Key == "tank-a");
        SimActor secondTank = doctrine.Actors.First(actor => actor.Key == "tank-z");
        firstTank.Health = 800;   // first row requires below 50%, so it falls through
        secondTank.Health = 700;  // second row requires below 90%
        int protectedHealth = secondTank.Health;
        doctrine.Advance();
        Check("support priorities fall through to the first applicable subject",
            doctrineHealer.CurrentProtectTargetKey == "tank-z");
        Check("support intent does not invent healing",
            secondTank.Health == protectedHealth);
        int intentSnapshot = doctrine.Timeline.Count - 1;
        Check("combat intent keys are captured in the timeline",
            doctrine.Timeline[intentSnapshot].Actors.First(state => state.Key == "healer") is
            {
                CurrentFollowTargetKey: "tank-z",
                CurrentProtectTargetKey: "tank-z",
                CurrentEnemyTargetKey: "add-a",
            });

        foreach (SimActor add in doctrine.Actors.Where(actor =>
                     actor.Spec.Role == EncounterActorRole.Add))
            add.Alive = false;
        doctrine.Advance();
        Check("enemy priority falls back from adds/current to the primary enemy",
            doctrineHealer.CurrentEnemyTargetKey == "boss");
        doctrine.RestoreTo(intentSnapshot);
        Check("rewind restores resolved combat intent",
            doctrineHealer.CurrentEnemyTargetKey == "add-a" &&
            doctrineHealer.CurrentProtectTargetKey == "tank-z");

        // A planned body's existing DPS follows its portable hostile buckets.
        // The global fraction remains boss pressure even while that body handles
        // adds, and deaths reroute only after the fixed step resolves.
        var addFirstRules = new EncounterPlayerRules(Plan: new CombatPlan(
            EnemyPriorities:
            [
                new CombatEnemyPriority(CombatEnemyKind.AnyAdd),
            ],
            Fallback: CombatFallback.ClassDefaults));
        List<EncounterActorSpec> routedDamageScenario =
        [
            new("boss", "Boss", 1, EncounterActorRole.Boss, Vector3.Zero,
                MaxHealth: 1_000),
            new("add-b", "second add", 2, EncounterActorRole.Add,
                new Vector3(4f, -3f, 0f), MaxHealth: 100),
            new("add-a", "first add", 2, EncounterActorRole.Add,
                new Vector3(4f, -4f, 0f), MaxHealth: 100),
            new("planned-melee", "planned melee", 0, EncounterActorRole.Friendly,
                new Vector3(4f, -4f, 0f), Dps: 100f, Job: RaidJob.Melee,
                PlayerRules: addFirstRules),
        ];
        var routedDamage = new EncounterSim(definition, routedDamageScenario,
            new EncounterSimOptions
            {
                Seed = 21,
                StepMs = 1_000,
                RaidDpsFraction = 0.01f,
                PullRangeYards = 100f,
            });
        routedDamage.Advance(); // pull; combat damage begins on the next tick
        SimActor routedBoss = routedDamage.Actors.First(actor => actor.Key == "boss");
        SimActor routedPlayer = routedDamage.Actors
            .First(actor => actor.Key == "planned-melee");
        routedDamage.Advance(); // add-a dies; only the global dial touches boss
        Check("add-first routes that character's DPS away from the boss",
            routedBoss.Health == 990 &&
            routedDamage.Actors.First(actor => actor.Key == "add-a") is
                { Alive: false, Health: 0 } &&
            routedPlayer.CurrentEnemyTargetKey == "add-b");
        routedDamage.Advance(); // add-b dies; next resolution selects class default
        Check("planned melee reach is tested against its chosen add",
            routedDamage.Actors.First(actor => actor.Key == "add-b") is
                { Alive: false, Health: 0 });
        Check("class-default fallback resolves the primary enemy after adds",
            routedBoss.Health == 980 && routedPlayer.CurrentEnemyTargetKey == "boss");
        Check("add death events are emitted once in deterministic key order",
            routedDamage.Events
                .Where(e => e.Kind == SimEventKind.Death && e.ActorKey.StartsWith("add-"))
                .Select(e => e.ActorKey)
                .SequenceEqual(["add-a", "add-b"]));
        routedDamage.Advance();
        Check("per-character DPS resumes on the fallback primary enemy",
            routedBoss.Health == 870);

        // A NoAction fallback is an actual zero-DPS decision for the planned
        // character. The unplanned body beside it keeps the legacy boss route.
        var noActionRules = new EncounterPlayerRules(Plan: new CombatPlan(
            Fallback: CombatFallback.NoActionThisTick));
        List<EncounterActorSpec> noActionScenario =
        [
            new("boss", "Boss", 1, EncounterActorRole.Boss, Vector3.Zero,
                MaxHealth: 1_000),
            new("legacy", "legacy ranged", 0, EncounterActorRole.Friendly,
                new Vector3(10f, 0f, 0f), Dps: 40f, Job: RaidJob.Ranged),
            new("planned-idle", "planned idle", 0, EncounterActorRole.Friendly,
                new Vector3(11f, 0f, 0f), Dps: 250f, Job: RaidJob.Ranged,
                PlayerRules: noActionRules),
        ];
        var noAction = new EncounterSim(definition, noActionScenario,
            new EncounterSimOptions
            {
                Seed = 22,
                StepMs = 1_000,
                RaidDpsFraction = 0f,
                PullRangeYards = 100f,
            });
        noAction.Advance();
        noAction.Advance();
        Check("NoAction contributes no planned per-character damage",
            noAction.Actors.First(actor => actor.Key == "boss").Health == 960 &&
            noAction.Actors.First(actor => actor.Key == "planned-idle")
                .CurrentEnemyTargetKey is null);
        Check("a character without a plan retains legacy boss DPS",
            noAction.Actors.First(actor => actor.Key == "legacy").Spec.Dps == 40f);

        // A scripted despawn must retire both the visible actor and the key-index
        // entry used by sticky CurrentEnemy, otherwise the plan attacks a ghost.
        EncounterDefinition despawnDefinition = definition with
        {
            Key = "test-despawn-target",
            Name = "Test Despawn Target",
            Phases =
            [
                new EncounterPhase("only", "only", OnEnter:
                [
                    new EncounterStep(EncounterStepKind.Summon,
                        Entry: 2, Count: 1, Point: new Vector3(4f, 0f, 0f)),
                    new EncounterStep(EncounterStepKind.Wait, DurationMs: 1_000),
                    new EncounterStep(EncounterStepKind.DespawnSummons),
                ]),
            ],
            Abilities = [],
        };
        var despawnRules = new EncounterPlayerRules(Plan: new CombatPlan(
            EnemyPriorities:
            [
                new CombatEnemyPriority(CombatEnemyKind.AnyAdd),
                new CombatEnemyPriority(CombatEnemyKind.CurrentEnemy),
            ],
            Fallback: CombatFallback.ClassDefaults));
        List<EncounterActorSpec> despawnScenario =
        [
            new("boss", "Boss", 1, EncounterActorRole.Boss, Vector3.Zero,
                MaxHealth: 1_000),
            new("planner", "planner", 0, EncounterActorRole.Friendly,
                new Vector3(6f, 0f, 0f), Job: RaidJob.Ranged,
                PlayerRules: despawnRules),
        ];
        var despawn = new EncounterSim(despawnDefinition, despawnScenario,
            new EncounterSimOptions
            {
                Seed = 23,
                StepMs = 1_000,
                RaidDpsFraction = 0f,
                PullRangeYards = 100f,
            });
        despawn.Advance(); // pull and install the on-enter sequence
        despawn.Advance(); // summon, then wait
        SimActor despawnPlanner = despawn.Actors.First(actor => actor.Key == "planner");
        Check("a live summon can become the current enemy",
            despawnPlanner.CurrentEnemyTargetKey == "summon:1");
        despawn.Advance(); // wait completes, then the sequence despawns summons
        Check("despawn invalidates sticky current enemy and falls back to primary",
            despawnPlanner.CurrentEnemyTargetKey == "boss" &&
            despawn.Actors.All(actor =>
                !actor.Key.StartsWith("summon:", StringComparison.Ordinal)));

        // Once a CombatPlan exists, its facing flag is authoritative. This keeps
        // a migrated legacy true bit from silently defeating an explicit false.
        var planFacingOff = new EncounterPlayerRules(
            AlwaysFaceBoss: true,
            Plan: new CombatPlan(Movement: new CombatMovementPlan(
                FacePrimaryEnemy: false)));
        var planFacingOn = new EncounterPlayerRules(
            AlwaysFaceBoss: false,
            Plan: new CombatPlan(Movement: new CombatMovementPlan(
                FacePrimaryEnemy: true)));
        List<EncounterActorSpec> facingScenario =
        [
            new("boss", "Boss", 1, EncounterActorRole.Boss, Vector3.Zero,
                MaxHealth: 1_000),
            new("legacy-face", "legacy face", 0, EncounterActorRole.Friendly,
                new Vector3(10f, 0f, 0f), Facing: 0f,
                PlayerRules: new EncounterPlayerRules(AlwaysFaceBoss: true)),
            new("plan-face-off", "plan face off", 0, EncounterActorRole.Friendly,
                new Vector3(0f, 10f, 0f), Facing: 0f,
                PlayerRules: planFacingOff),
            new("plan-face-on", "plan face on", 0, EncounterActorRole.Friendly,
                new Vector3(0f, -10f, 0f), Facing: 0f,
                PlayerRules: planFacingOn),
        ];
        var facing = new EncounterSim(definition, facingScenario,
            new EncounterSimOptions
            {
                Seed = 24,
                StepMs = 100,
                RaidDpsFraction = 0f,
                PullRangeYards = 100f,
            });
        facing.Advance();
        SimActor legacyFace = facing.Actors.First(actor => actor.Key == "legacy-face");
        SimActor planFaceOff = facing.Actors.First(actor => actor.Key == "plan-face-off");
        SimActor planFaceOn = facing.Actors.First(actor => actor.Key == "plan-face-on");
        float legacyFaceError = MathF.Abs(MathF.Atan2(
            MathF.Sin(legacyFace.Facing - MathF.PI),
            MathF.Cos(legacyFace.Facing - MathF.PI)));
        float planFaceOnError = MathF.Abs(MathF.Atan2(
            MathF.Sin(planFaceOn.Facing - MathF.PI / 2f),
            MathF.Cos(planFaceOn.Facing - MathF.PI / 2f)));
        Check("legacy AlwaysFaceBoss still applies when no plan exists",
            legacyFaceError < 1e-4f);
        Check("plan facing false overrides a stale legacy true flag",
            MathF.Abs(planFaceOff.Facing) < 1e-4f);
        Check("plan facing true applies without the legacy flag",
            planFaceOnError < 1e-4f);

        // Follow corrects only far enough to enter its range band. A waypoint is
        // a higher-precedence body order; follow remains visible as intent and
        // resumes on the step after the route arrives.
        var followRules = new EncounterPlayerRules(Plan: new CombatPlan(
            Movement: new CombatMovementPlan(
                CombatMovementMode.Follow, CombatSubject.Tank(), 5f, 10f)));
        List<EncounterActorSpec> followScenario =
        [
            new("boss", "Boss", 1, EncounterActorRole.Boss, Vector3.Zero,
                MaxHealth: 100_000),
            new("anchor", "anchor tank", 0, EncounterActorRole.Friendly,
                new Vector3(10f, 0f, 0f), Job: RaidJob.Tank),
            new("follower", "follower", 0, EncounterActorRole.Friendly,
                new Vector3(30f, 0f, 0f), Job: RaidJob.Healer,
                PlayerRules: followRules),
        ];
        var following = new EncounterSim(definition, followScenario,
            new EncounterSimOptions
            {
                Seed = 19,
                StepMs = 1_000,
                RaidDpsFraction = 0f,
                PullRangeYards = 100f,
            });
        following.Advance();
        following.Advance();
        SimActor follower = following.Actors.First(actor => actor.Key == "follower");
        SimActor anchor = following.Actors.First(actor => actor.Key == "anchor");
        Check("follow movement stops at the configured range band",
            MathF.Abs(EncounterGeometryLaw.GroundDistance(
                follower.Position, anchor.Position) - 10f) < 1e-4f);

        Vector3 waypoint = new(30f, 14f, 0f);
        followScenario[2] = followScenario[2] with
        {
            Position = new Vector3(30f, 0f, 0f),
            Moves = [new TimedMove(0, waypoint)],
        };
        var waypointFollowing = new EncounterSim(definition, followScenario,
            new EncounterSimOptions
            {
                Seed = 20,
                StepMs = 1_000,
                RaidDpsFraction = 0f,
                PullRangeYards = 100f,
            });
        waypointFollowing.Advance();
        SimActor waypointFollower = waypointFollowing.Actors
            .First(actor => actor.Key == "follower");
        Check("an explicit waypoint overrides follow movement",
            MathF.Abs(waypointFollower.Position.X - 30f) < 1e-4f &&
            waypointFollower.Position.Y > 0f &&
            waypointFollower.CurrentFollowTargetKey == "anchor");
        waypointFollowing.Advance();              // arrive at the waypoint
        Vector3 arrived = waypointFollower.Position;
        waypointFollowing.Advance();              // standing doctrine resumes
        Check("follow resumes after the explicit waypoint completes",
            EncounterGeometryLaw.GroundDistance(waypointFollower.Position,
                followScenario[1].Position) <
            EncounterGeometryLaw.GroundDistance(arrived, followScenario[1].Position));

        // Phase gating: a health-gated transition must actually fire.
        var phased = new EncounterSim(definition, scenario,
            new EncounterSimOptions { Seed = 7, RaidDpsFraction = 0.02f });
        phased.AdvanceTo(120_000);
        Check("health-gated phase transition fires",
            phased.Events.Any(e => e.Kind == SimEventKind.PhaseEnter && e.Text.Contains("air")));
        Check("phase-2 ability never fires during phase 1",
            phased.Events
                .Where(e => e.Kind == SimEventKind.CastLand && e.AbilityKey == "p2only")
                .All(e => e.TimeMs >= phased.Events
                    .First(x => x.Kind == SimEventKind.PhaseEnter && x.Text.Contains("air")).TimeMs));

        // Reset must be total — a re-run after Reset is the same run.
        var reset = new EncounterSim(definition, scenario, new EncounterSimOptions { Seed = 4242 });
        reset.AdvanceTo(30_000);
        List<(int, string)> first = reset.Events.Select(e => (e.TimeMs, e.Text)).ToList();
        reset.Reset();
        reset.AdvanceTo(30_000);
        Check("Reset re-arms the seed and reproduces the run",
            first.SequenceEqual(reset.Events.Select(e => (e.TimeMs, e.Text))));

        // Choreography must block ability timers, the way m_bTransition does.
        Check("no cast lands while a transition sequence is running",
            NoCastsDuringTransition(phased));
    }

    private static bool NoCastsDuringTransition(EncounterSim sim)
    {
        // The test encounter's transition is a 4 s wait; nothing may land inside it.
        SimEvent? say = sim.Events.FirstOrDefault(e => e.Kind == SimEventKind.Say);
        SimEvent? enter = sim.Events.FirstOrDefault(e =>
            e.Kind == SimEventKind.PhaseEnter && e.Text.Contains("air"));
        if (say is null || enter is null) return true;
        return !sim.Events.Any(e => e.Kind == SimEventKind.CastLand &&
                                    e.TimeMs > say.TimeMs && e.TimeMs < enter.TimeMs);
    }

    // ── probe ────────────────────────────────────────────────────────────────

    // ── positioning slot ───────────────────────────────────────────────────────

    private static void PositioningSlotChecks()
    {
        Section("positioning slot");

        EncounterDefinition definition = TwoPhaseTestEncounter();
        var sideSpot = new Vector3(25f, 15f, 0f);

        // A ranged body assigned a positioning script that sends it to a side spot in
        // the opening phase; a tank with no script that still obeys the job playbook.
        var leftRanged = new PositioningScript(
            Id: "left-ranged", Name: "left ranged", Role: RaidJob.Ranged, Side: RaidSide.Left,
            Movement: new CombatMovementPlan(),
            Phases: [new PositioningPhaseStep("ground", RaidDirectiveKind.MoveToSpot, sideSpot)],
            EncounterKey: definition.Key);

        List<EncounterActorSpec> scenario =
        [
            new("boss", "Boss", 1, EncounterActorRole.Boss, Vector3.Zero, MaxHealth: 100_000),
            new("tank", "tank", 0, EncounterActorRole.Friendly, new Vector3(6f, 0f, 0f),
                Job: RaidJob.Tank),
            new("ranged", "ranged", 0, EncounterActorRole.Friendly, new Vector3(25f, 0f, 0f),
                Job: RaidJob.Ranged, Side: RaidSide.Left,
                PlayerRules: new EncounterPlayerRules(PositioningId: "left-ranged")),
        ];

        var positioningMap = new Dictionary<string, PositioningScript> { ["ranged"] = leftRanged };
        var sim = new EncounterSim(definition, scenario, new EncounterSimOptions
        {
            Seed = 7,
            StepMs = 100,
            RaidDpsFraction = 0f,      // boss never drops below the phase gate — stays "ground"
            PullRangeYards = 100f,
            Playbook = [new RaidPhaseDirective("ground", RaidJob.Tank, RaidDirectiveKind.ChaseBoss)],
            Positioning = positioningMap,
        });

        SimActor ranged = sim.Actors.First(a => a.Key == "ranged");
        SimActor tank = sim.Actors.First(a => a.Key == "tank");

        sim.Advance();   // pull at t=0 re-enters "ground": positioning + playbook apply
        Check("assigned positioning drives the body to its authored spot",
            ranged.MoveTarget is { } target && Vector3.Distance(target, sideSpot) < 0.01f);
        Check("a body with no script still obeys the job playbook (no regression)",
            tank.AutoChase);

        sim.AdvanceTo(6_000);   // 15 yd at 7 yd/s ≈ 2.1 s
        Check("the positioned body actually walks to its spot",
            Vector3.Distance(ranged.Position, sideSpot) < 1.0f);

        // Precedence: an assigned script overrides the job playbook for ITS body. A
        // ranged Chase row must not win over the script's Move-to-spot.
        var sim2 = new EncounterSim(definition, scenario, new EncounterSimOptions
        {
            Seed = 7,
            StepMs = 100,
            RaidDpsFraction = 0f,
            PullRangeYards = 100f,
            Playbook = [new RaidPhaseDirective("ground", RaidJob.Ranged, RaidDirectiveKind.ChaseBoss)],
            Positioning = positioningMap,
        });
        SimActor ranged2 = sim2.Actors.First(a => a.Key == "ranged");
        sim2.Advance();
        Check("the positioning slot overrides the job playbook for its own body",
            !ranged2.AutoChase &&
            ranged2.MoveTarget is { } t2 && Vector3.Distance(t2, sideSpot) < 0.01f);

        // Two bodies sharing one script land on ONE spot fanned apart, not stacked.
        List<EncounterActorSpec> pair =
        [
            new("boss", "Boss", 1, EncounterActorRole.Boss, Vector3.Zero, MaxHealth: 100_000),
            new("h1", "healer 1", 0, EncounterActorRole.Friendly, new Vector3(20f, 2f, 0f),
                Job: RaidJob.Healer, PlayerRules: new EncounterPlayerRules(PositioningId: "left-ranged")),
            new("h2", "healer 2", 0, EncounterActorRole.Friendly, new Vector3(20f, -2f, 0f),
                Job: RaidJob.Healer, PlayerRules: new EncounterPlayerRules(PositioningId: "left-ranged")),
        ];
        var shared = new EncounterSim(definition, pair, new EncounterSimOptions
        {
            Seed = 7, StepMs = 100, RaidDpsFraction = 0f, PullRangeYards = 100f,
            Positioning = new Dictionary<string, PositioningScript>
            { ["h1"] = leftRanged, ["h2"] = leftRanged },
        });
        SimActor h1 = shared.Actors.First(a => a.Key == "h1");
        SimActor h2 = shared.Actors.First(a => a.Key == "h2");
        shared.Advance();
        Check("bodies sharing one spot are fanned apart, not stacked",
            h1.MoveTarget is { } m1 && h2.MoveTarget is { } m2 &&
            Vector3.Distance(m1, m2) > 0.5f);

        // Model: the mirror mapping is symmetric and Center/None are their own mirror.
        Check("RaidSide.Mirror is a symmetric Left⇄Right swap",
            RaidSide.Left.Mirror() == RaidSide.Right &&
            RaidSide.Right.Mirror() == RaidSide.Left &&
            RaidSide.Center.Mirror() == RaidSide.Center &&
            RaidSide.None.Mirror() == RaidSide.None);
    }

    private static void ProbeChecks()
    {
        Section("position probe");

        EncounterDefinition definition = TwoPhaseTestEncounter();
        var sim = new EncounterSim(definition, Scenario(), new EncounterSimOptions { Seed = 31337 });
        sim.AdvanceTo(60_000);

        // Standing where the tank stands is inside the cleave arc: the cone turns to
        // face the current victim, so the victim's own spot is the worst spot there is.
        ProbeReport onTop = EncounterProbeLaw.Scan(sim, ProbeTrajectory.Stationary(new Vector3(6.5f, 0f, 0f)));
        Check("a body in the tank's spot is hit repeatedly", onTop.HitCount > 0);
        Check("the first hit is reported with a time", onTop.FirstHit is { TimeMs: > 0 });

        // ...and stepping out of the arc but staying close is the interesting case:
        // still in range, no longer in the sector.
        ProbeReport besideBoss = EncounterProbeLaw.Scan(sim, ProbeTrajectory.Stationary(new Vector3(0f, 2f, 0f)));
        Check("a body perpendicular to the victim escapes the frontal arc",
            besideBoss.Threats.Any(t => !t.Covered && t.Hit.Verdict == FootprintVerdict.OutsideArc));

        // Far away is safe — and the report says so without pretending certainty.
        ProbeReport faraway = EncounterProbeLaw.Scan(sim, ProbeTrajectory.Stationary(new Vector3(400f, 400f, 0f)));
        Check("a body across the zone is never hit", faraway.HitCount == 0);

        // A trajectory is tested at the moment of impact, not at cast time. A body
        // that walks out before the landing must come back clean.
        var walkOut = new ProbeTrajectory();
        walkOut.Add(0, new Vector3(0f, 2f, 0f));
        walkOut.Add(4000, new Vector3(300f, 300f, 0f));
        ProbeReport walked = EncounterProbeLaw.Scan(sim, walkOut, fromMs: 10_000);
        Check("a body that walked away is not hit by later effects", walked.HitCount == 0);

        Check("trajectory interpolates between waypoints",
            Vector3.Distance(walkOut.PositionAt(2000), new Vector3(150f, 151f, 0f)) < 1f);
        Check("trajectory clamps before the first waypoint",
            walkOut.PositionAt(-500) == new Vector3(0f, 2f, 0f));
        Check("trajectory clamps after the last waypoint",
            walkOut.PositionAt(99_999) == new Vector3(300f, 300f, 0f));

        // "Why not" has to be usable: near misses are reported with clearance.
        ProbeReport grazing = EncounterProbeLaw.Scan(sim, ProbeTrajectory.Stationary(new Vector3(14f, 0f, 0f)));
        Check("near misses are collected with a clearance figure",
            grazing.Threats.Any(t => !t.Covered && t.Hit.ClearanceYards > 0f));

        // Structural reachability ignores timing — "can this ever hit me here".
        IReadOnlyList<string> structural = EncounterProbeLaw.StructuralThreats(
            definition, Vector3.Zero, BodyCapsule.At(new Vector3(6f, 0f, 0f)), null);
        Check("structural scan finds abilities that could reach the spot", structural.Count > 0);
    }

    // ── translator ───────────────────────────────────────────────────────────

    private static void TranslatorChecks()
    {
        Section("translator");

        var data = new TestWorldData();
        EncounterDefinition fromSpells = EncounterTranslator.FromDatabase(
            entry: 12129, creatureName: "Onyxian Warder", spellListId: 121290,
            scriptName: null, aiName: null, data: data.Build(), facts: null);

        Check("creature_spells slots become abilities",
            fromSpells.Abilities.Count(a => a.Key.StartsWith("spells:")) == 2);
        EncounterAbility? first = fromSpells.Abilities.FirstOrDefault(a => a.SpellId == 20203);
        Check("spell slot survives translation", first is not null);
        // The units trap: the DB stores SECONDS and the core multiplies by 1000.
        Check("creature_spells delays are milliseconds after parse",
            first is { Timing.RepeatMinMs: 12000, Timing.RepeatMaxMs: 12000 });
        Check("probability carries through", first is { ChancePercent: 100 });

        // A C++-bound creature must produce a declared hole, never a guess.
        EncounterDefinition scripted = EncounterTranslator.FromDatabase(
            entry: 10184, creatureName: "Onyxia", spellListId: 0,
            scriptName: "boss_onyxia", aiName: null, data: data.Build(), facts: null);
        Check("a compiled script becomes a declared hole",
            scripted.Abilities.Any(a => a.Fidelity == EncounterFidelity.UnknownUnmodeled &&
                                        a.Key == "cpp:boss_onyxia"));
        Check("coverage records the C++ source",
            scripted.Coverage.HasFlag(EncounterCoverage.CppCreatureScript));
        Check("the hole is reported by Holes()", scripted.Holes().Any());
        Check("worst fidelity of a scripted creature is unknown-unmodeled",
            scripted.WorstFidelity() == EncounterFidelity.UnknownUnmodeled);

        // EventAI: a timer event with a cast action becomes a timed ability.
        EncounterDefinition eventAi = EncounterTranslator.FromDatabase(
            entry: 1842, creatureName: "Test Elite", spellListId: 0,
            scriptName: null, aiName: "EventAI", data: data.Build(), facts: null);
        EncounterAbility? timed = eventAi.Abilities.FirstOrDefault(a => a.SpellId == 11976);
        Check("EventAI timer event becomes a timed ability",
            timed is { Trigger.Kind: EncounterTriggerKind.Timer });
        Check("EventAI params are already milliseconds",
            timed is { Timing.RepeatMinMs: 8000 });
        Check("EventAI HP event becomes a health trigger",
            eventAi.Abilities.Any(a => a.Trigger.Kind == EncounterTriggerKind.HealthBelow &&
                                       Math.Abs(a.Trigger.Threshold - 0.3f) < 0.01f));
        Check("coverage records EventAI", eventAi.Coverage.HasFlag(EncounterCoverage.EventAi));

        // A translated definition has to actually simulate — that is the point.
        var sim = new EncounterSim(eventAi,
            [
                eventAi.Actors![0] with { Position = Vector3.Zero, MaxHealth = 10000 },
                new EncounterActorSpec("t", "target", 0, EncounterActorRole.Friendly, new Vector3(3, 0, 0)),
            ],
            new EncounterSimOptions { Seed = 5, RaidDpsFraction = 0f });
        sim.AdvanceTo(60_000);
        Check("a DB-derived definition simulates and casts",
            sim.Events.Any(e => e.Kind == SimEventKind.CastLand));
    }

    // ── file format ──────────────────────────────────────────────────────────

    private static void LibraryChecks(string repoRoot)
    {
        Section("file format");

        string temporary = Path.Combine(Path.GetTempPath(), "encounter-lab-check-" + Guid.NewGuid().ToString("N"));
        try
        {
            var library = new EncounterLibrary(temporary);
            EncounterDefinition original = TwoPhaseTestEncounter();
            EncounterActorSpec[] actors = original.Actors!.ToArray();
            actors[1] = actors[1] with
            {
                PlayerRules = new EncounterPlayerRules(
                    AlwaysFaceBoss: true,
                    Plan: new CombatPlan(
                        Name: "portable healer",
                        Movement: new CombatMovementPlan(
                            CombatMovementMode.Follow, CombatSubject.Tank(2),
                            7f, 16f, FacePrimaryEnemy: true),
                        Engagement: CombatEngagementMode.AssistAnchor,
                        SupportPriorities:
                        [
                            new CombatSupportPriority(CombatSubject.Tank(1), 85f),
                            new CombatSupportPriority(CombatSubject.LowestHealth, 40f),
                        ],
                        EnemyPriorities:
                        [
                            new CombatEnemyPriority(CombatEnemyKind.AnyAdd),
                            new CombatEnemyPriority(CombatEnemyKind.PrimaryEnemy),
                        ],
                        Responsibilities:
                        [
                            CombatResponsibility.DispelMagic,
                            CombatResponsibility.Resurrect,
                        ],
                        Resources: new CombatResourcePolicy(30, 20, true),
                        Fallback: CombatFallback.NoActionThisTick)),
            };
            original = original with { Actors = actors };
            library.Save(original, "roundtrip.json");
            Check("save wrote a document", File.Exists(Path.Combine(temporary, "roundtrip.json")));

            var reloaded = new EncounterLibrary(temporary);
            Check("reload finds the document", reloaded.Reload() == 1);
            Check("reload reported no errors", reloaded.Errors.Count == 0);

            EncounterDefinition? round = reloaded.Get(original.Key);
            Check("round trip preserves the key", round?.Key == original.Key);
            Check("round trip preserves ability count",
                round?.Abilities.Count == original.Abilities.Count);
            Check("round trip preserves phases", round?.Phases.Count == original.Phases.Count);
            Check("round trip preserves cone sign",
                round?.Abilities.Any(a => a.Geometry.ConeDegrees < 0f) == true);
            Check("round trip preserves transition steps",
                round?.Phases[0].Transitions?[0].Steps?.Count == original.Phases[0].Transitions![0].Steps!.Count);
            Check("round trip preserves fidelity labels",
                round?.WorstFidelity() == original.WorstFidelity());
            Check("round trip preserves per-player base rules",
                round?.Actors?[1].PlayerRules?.AlwaysFaceBoss == true);
            CombatPlan? roundPlan = round?.Actors?[1].PlayerRules?.Plan;
            Check("round trip preserves combat-plan movement and semantic anchor",
                roundPlan is
                {
                    Name: "portable healer",
                    Movement:
                    {
                        Mode: CombatMovementMode.Follow,
                        Anchor.Kind: CombatSubjectKind.RoleOrdinal,
                        Anchor.Role: RaidJob.Tank,
                        Anchor.Ordinal: 2,
                        MinRangeYards: 7f,
                        MaxRangeYards: 16f,
                        FacePrimaryEnemy: true,
                    },
                    Engagement: CombatEngagementMode.AssistAnchor,
                });
            Check("round trip preserves ordered support priorities",
                roundPlan?.SupportPriorities is { Count: 2 } support &&
                support[0].Target == CombatSubject.Tank(1) &&
                support[0].OnlyWhenBelowHealthPercent == 85f &&
                support[1].Target == CombatSubject.LowestHealth);
            Check("round trip preserves ordered enemy priorities",
                roundPlan?.EnemyPriorities is { Count: 2 } enemies &&
                enemies[0].Kind == CombatEnemyKind.AnyAdd &&
                enemies[1].Kind == CombatEnemyKind.PrimaryEnemy);
            Check("round trip preserves responsibilities and resource policy",
                roundPlan?.Responsibilities is { Count: 2 } responsibilities &&
                responsibilities[0] == CombatResponsibility.DispelMagic &&
                responsibilities[1] == CombatResponsibility.Resurrect &&
                roundPlan.Resources == new CombatResourcePolicy(30, 20, true));
            Check("round trip preserves combat-plan fallback",
                roundPlan?.Fallback == CombatFallback.NoActionThisTick);

            // Reusable character plans live outside encounter documents. The stable
            // character key survives any boss/party reconstruction while the plan
            // itself contains only semantic, portable subjects.
            string planPath = Path.Combine(temporary, "preferences", "combat-plans.json");
            var emptyPlans = new CombatPlanStore(planPath);
            Check("missing combat-plan store loads as empty",
                emptyPlans.Load() == 0 && emptyPlans.Errors.Count == 0);
            CombatPlan durablePlan = roundPlan!;
            Check("combat-plan upsert persists explicitly",
                emptyPlans.Upsert("raid-healer1", durablePlan) && File.Exists(planPath));

            var reloadedPlans = new CombatPlanStore(planPath);
            Check("combat-plan store reloads a stable character profile",
                reloadedPlans.Load() == 1 && reloadedPlans.Errors.Count == 0);
            CombatPlan? storedPlan = reloadedPlans.Find("raid-healer1");
            Check("combat-plan store round trip uses the encounter DTO mapping",
                storedPlan is
                {
                    Name: "portable healer",
                    Movement:
                    {
                        Mode: CombatMovementMode.Follow,
                        Anchor.Kind: CombatSubjectKind.RoleOrdinal,
                        Anchor.Role: RaidJob.Tank,
                        Anchor.Ordinal: 2,
                    },
                    Engagement: CombatEngagementMode.AssistAnchor,
                    Fallback: CombatFallback.NoActionThisTick,
                } && storedPlan.SupportPriorities is { Count: 2 });
            Check("combat-plan lookup is tolerant of empty and unknown keys",
                reloadedPlans.Find("") is null && reloadedPlans.Find("raid-missing") is null);
            Check("combat-plan remove persists",
                reloadedPlans.Remove("raid-healer1") &&
                new CombatPlanStore(planPath) is { } afterRemove &&
                afterRemove.Load() == 0 && afterRemove.Find("raid-healer1") is null);

            File.WriteAllText(planPath, "{ definitely not valid json");
            var malformedPlans = new CombatPlanStore(planPath);
            Check("malformed combat-plan store is reported without throwing",
                malformedPlans.Load() == 0 && malformedPlans.Errors.Count == 1 &&
                malformedPlans.Find("raid-healer1") is null);
            Check("an explicit upsert recovers a malformed preference file",
                malformedPlans.Upsert("raid-healer1", durablePlan) &&
                new CombatPlanStore(planPath) is { } recoveredPlans &&
                recoveredPlans.Load() == 1 && recoveredPlans.Errors.Count == 0);

            // A stale schema version must fail loudly, not parse into nonsense.
            File.WriteAllText(Path.Combine(temporary, "stale.json"),
                """{ "schemaVersion": 999, "key": "stale", "name": "Stale" }""");
            var withStale = new EncounterLibrary(temporary);
            withStale.Reload();
            Check("a future schemaVersion is rejected with an error",
                withStale.Errors.Any(e => e.Contains("schemaVersion")));
            Check("one bad document does not take the library down", withStale.Count == 1);
        }
        finally
        {
            if (Directory.Exists(temporary)) Directory.Delete(temporary, recursive: true);
        }
    }

    // ── the Onyxia document ──────────────────────────────────────────────────

    private static void OnyxiaChecks(string repoRoot)
    {
        Section("onyxia document");

        string directory = Path.Combine(repoRoot, "encounters");
        var library = new EncounterLibrary(directory);
        int loaded = library.Reload();
        if (loaded == 0)
        {
            Check($"encounters directory has documents ({directory})", false);
            foreach (string error in library.Errors) Console.WriteLine($"      {error}");
            return;
        }
        Check("library loaded without errors", library.Errors.Count == 0);
        foreach (string error in library.Errors) Console.WriteLine($"      {error}");

        EncounterDefinition? onyxia = library.Get("onyxia");
        Check("onyxia document loads", onyxia is not null);
        if (onyxia is null) return;

        Check("onyxia resolves by primary entry", library.ForEntry(10184)?.Key == "onyxia");
        Check("onyxia resolves by member entry (whelp)", library.ForEntry(11262)?.Key == "onyxia");
        Check("onyxia has three phases", onyxia.Phases.Count == 3);
        Check("phase 2 is flagged flying", onyxia.Phase("p2")?.CasterFlying == true);
        Check("phase 1 ability controls include Cleave but not Fireball",
            onyxia.AbilitiesIn("p1").Any(a => a.Key == "cleave") &&
            !onyxia.AbilitiesIn("p1").Any(a => a.Key == "fireball"));
        Check("phase 2 ability controls include Fireball but not Cleave",
            onyxia.AbilitiesIn("p2").Any(a => a.Key == "fireball") &&
            !onyxia.AbilitiesIn("p2").Any(a => a.Key == "cleave"));
        Check("Heated Ground is only available in the air phase",
            onyxia.AbilitiesIn("p2").Any(a => a.Key == "unmodeled_heated_ground") &&
            !onyxia.AbilitiesIn("p1").Any(a => a.Key == "unmodeled_heated_ground") &&
            !onyxia.AbilitiesIn("p3").Any(a => a.Key == "unmodeled_heated_ground"));
        Check("phase 3 ability controls include Cleave and Bellowing Roar",
            onyxia.AbilitiesIn("p3").Any(a => a.Key == "cleave") &&
            onyxia.AbilitiesIn("p3").Any(a => a.Key == "bellowing_roar"));
        Check("p1 transitions to p2 on a health gate",
            onyxia.Phase("p1")?.Transitions?[0] is
                { ToPhase: "p2", Trigger.Kind: EncounterTriggerKind.HealthBelow });
        Check("the takeoff choreography has ordered steps",
            onyxia.Phase("p1")?.Transitions?[0].Steps?.Count >= 8);

        Check("tail sweep is a REAR cone",
            onyxia.Abilities.Any(a => a.Key == "tail_sweep" && a.Geometry.ConeDegrees == -120f &&
                                      a.Geometry.IsRearCone));
        Check("all eight breath lanes are catalogued",
            onyxia.Abilities.Count(a => a.Key.StartsWith("lane_")) == 8);
        Check("breath lanes resolve from spell_target_position ids",
            onyxia.Abilities.Where(a => a.Key.StartsWith("lane_"))
                .All(a => a.Geometry.PointSpellIds is { Count: >= 7 }));
        Check("catalogued lanes are never auto-scheduled",
            onyxia.Abilities.Where(a => a.Key.StartsWith("lane_"))
                .All(a => a.Trigger.Kind == EncounterTriggerKind.Manual));
        Check("the north-to-south lane skips the missing 17096 row",
            onyxia.Abilities.First(a => a.Key == "lane_north_to_south")
                .Geometry.PointSpellIds!.Contains(17097u) is true &&
            !onyxia.Abilities.First(a => a.Key == "lane_north_to_south")
                .Geometry.PointSpellIds!.Contains(17096u));

        // The document's real job: naming what the format cannot say.
        List<string> holes = onyxia.Holes().ToList();
        Check("the document declares its unmodeled beats", holes.Count >= 3);
        Check("gap markers are traceable",
            onyxia.Abilities.Any(a => a.Note?.Contains("GAP-") == true));
        Console.WriteLine($"      declared holes: {holes.Count}");
        foreach (string hole in holes.Take(6)) Console.WriteLine($"        - {hole}");

        // It has to run. With no spell facts every geometry falls back to defaults,
        // which is exactly the offline creator-mode case.
        List<EncounterActorSpec> scenario = onyxia.Actors!.ToList();
        var sim = new EncounterSim(onyxia, scenario,
            new EncounterSimOptions { Seed = 2026, RaidDpsFraction = 0.006f });
        sim.AdvanceTo(5 * 60 * 1000);

        Check("onyxia simulates without throwing", sim.Timeline.Count > 100);
        Check("phase 1 abilities fire",
            sim.Events.Any(e => e.Kind == SimEventKind.CastLand && e.AbilityKey == "tail_sweep"));
        Check("the fight reaches phase 2",
            sim.Events.Any(e => e.Kind == SimEventKind.PhaseEnter && e.Text.Contains("air")));
        Check("the fight reaches phase 3",
            sim.Events.Any(e => e.Kind == SimEventKind.PhaseEnter && e.Text.Contains("roar")));
        Check("takeoff choreography emits movement",
            sim.Events.Any(e => e.Kind == SimEventKind.Move && e.Text.Contains("moves to")));
        Check("whelps get summoned",
            sim.Events.Any(e => e.Kind == SimEventKind.Summon));
        Check("unmodeled beats surface as timeline markers",
            sim.Events.Any(e => e.Kind == SimEventKind.Unmodeled));
        Check("bellowing roar never fires during phase 1", BellowingRoarIsLatePhaseOnly(sim));

        Console.WriteLine($"      simulated {sim.TimeMs / 1000}s, {sim.Events.Count} events, " +
                          $"{sim.Timeline.Count} snapshots");
    }

    /// <summary>
    /// Bellowing Roar is a phase-3 ability, but its FIRST cast is faithfully fired by
    /// the landing choreography inside the phase-2→3 transition — the real script
    /// casts it from MovementInform(LANDING_FLIGHT), before phase 3 formally begins.
    /// So the invariant is not "only after the phase-3 marker"; it is "never while
    /// she is still on the ground in phase 1".
    /// </summary>
    private static bool BellowingRoarIsLatePhaseOnly(EncounterSim sim)
    {
        SimEvent? phaseTwo = sim.Events.FirstOrDefault(e =>
            e.Kind == SimEventKind.PhaseEnter && e.Text.Contains("air"));
        if (phaseTwo is null) return true;
        return sim.Events
            .Where(e => e.AbilityKey == "bellowing_roar" && e.Kind == SimEventKind.CastLand)
            .All(e => e.TimeMs >= phaseTwo.TimeMs);
    }

    // ── live world-DB integration ────────────────────────────────────────────

    /// <summary>
    /// Read-only. Fetches the five behaviour tables from MangosSuperUI's existing CSV
    /// export and checks the parsers against facts verified independently by SQL on
    /// the homeserver. Every assertion here is a schema or unit claim that offline
    /// tests structurally cannot catch.
    /// </summary>
    private static async Task LiveDataChecks(string repoRoot, string baseUrl)
    {
        Section($"live world DB ({baseUrl})");

        var client = new EncounterDataClient(repoRoot);
        client.BeginFetch(baseUrl, forceRefresh: true);
        for (int i = 0; i < 120 && client.Fetching; i++) await Task.Delay(500);

        if (client.Data is not { } data)
        {
            Check("behaviour tables fetched", false);
            return;
        }
        Console.WriteLine($"      {data.Describe()}");
        Check("behaviour tables fetched", true);
        if (data.Error is { Length: > 0 } error) Console.WriteLine($"      note: {error}");

        Check("creature_spells parsed", data.SpellListsByEntry.Count > 1000);
        Check("EventAI creatures parsed", data.EventsByCreature.Count > 1000);
        Check("ai scripts parsed", data.ScriptsById.Count > 100);
        Check("spell_target_position parsed", data.TargetPositions.Count > 100);
        Check("spell_cone parsed", data.ConeDegrees.Count > 10);

        // THE SIGN. spell_cone stores rear arcs as negative degrees; taking the
        // absolute value on parse would silently move Tail Sweep to Onyxia's face.
        Check("Tail Sweep (15847) cone is -120 (rear arc)",
            data.ConeDegrees.TryGetValue(15847u, out float sweep) &&
            Math.Abs(sweep - -120f) < 0.01f);
        Check("a front cone stays positive",
            data.ConeDegrees.TryGetValue(5708u, out float front) && front > 0f);

        // THE UNITS. creature_spells delays are SECONDS in the DB and the core
        // multiplies by IN_MILLISECONDS at load. Onyxian Warder's slot 1 is a
        // 12-second repeat; parsed raw it would read as 12 ms.
        if (data.SpellList(121290u) is { } warder)
        {
            CreatureSpellSlot? slot = warder.Slots.FirstOrDefault(s => s.SpellId == 20203);
            Check("Onyxian Warder spell list found", slot is not null);
            Check("creature_spells seconds converted to ms (12s not 12ms)",
                slot is { RepeatMinMs: 12000, RepeatMaxMs: 12000 });
        }
        else Check("Onyxian Warder spell list found", false);

        // THE COORDINATES. Onyxia's breath lane is exact-db, not hand-drawn.
        Check("breath lane head 17086 is at the north end of the lair",
            data.TargetPositions.TryGetValue(17086u, out SpellTargetPosition? head) &&
            head.Map == 249 &&
            Math.Abs(head.Position.X - 20.73f) < 0.1f &&
            Math.Abs(head.Position.Y - -215.24f) < 0.1f);
        Check("the missing 17096 row really is absent",
            !data.TargetPositions.ContainsKey(17096u));
        Check("breath lane tail 17097 exists",
            data.TargetPositions.ContainsKey(17097u));

        // Now the payoff: resolve the authored Onyxia lanes against real DB rows.
        var library = new EncounterLibrary(Path.Combine(repoRoot, "encounters"));
        library.Reload();
        if (library.Get("onyxia") is not { } onyxia)
        {
            Check("onyxia document available for live resolution", false);
            return;
        }

        var facts = new WorldDataSpellFacts(data);
        int resolvedLanes = 0, totalPoints = 0;
        foreach (EncounterAbility lane in onyxia.Abilities.Where(a => a.Key.StartsWith("lane_")))
        {
            IReadOnlyList<System.Numerics.Vector3> points =
                EncounterGeometryLaw.ResolveChainPoints(lane.Geometry, facts);
            if (points.Count == lane.Geometry.PointSpellIds!.Count) resolvedLanes++;
            totalPoints += points.Count;
        }
        Check("all 8 authored breath lanes resolve fully from the live DB", resolvedLanes == 8);
        Console.WriteLine($"      {totalPoints} lane points resolved from spell_target_position");

        // A resolved lane must be a real lane: long, and roughly straight.
        EncounterAbility north = onyxia.Abilities.First(a => a.Key == "lane_north_to_south");
        IReadOnlyList<System.Numerics.Vector3> lanePoints =
            EncounterGeometryLaw.ResolveChainPoints(north.Geometry, facts);
        float span = EncounterGeometryLaw.GroundDistance(lanePoints[0], lanePoints[^1]);
        Check($"the north-south lane spans the chamber ({span:0.#} yd)", span is > 80f and < 100f);

        // And it must hit a body standing in it, and miss one standing beside it.
        var footprint = EncounterGeometryLaw.Resolve(
            north, lanePoints[0], 0f, null, facts);
        System.Numerics.Vector3 middle = lanePoints[lanePoints.Count / 2];
        Check("a body standing in the lane is covered",
            EncounterGeometryLaw.Test(footprint, BodyCapsule.At(middle)).Covered);
        Check("a body 30 yd off the lane is not",
            !EncounterGeometryLaw.Test(footprint,
                BodyCapsule.At(middle + new System.Numerics.Vector3(0f, 30f, 0f))).Covered);

        // THE TIER TEST. Which of the three behaviour tiers a creature is in has to be
        // read off creature_template, or a compiled-C++ boss looks like a mob with no
        // abilities instead of declaring its hole.
        Check("creature_template bindings parsed", data.Bindings.Count > 5000);
        CreatureBehaviourBinding? onyxiaBinding = data.Binding(10184u);
        Check("Onyxia is bound to compiled C++ (boss_onyxia)",
            onyxiaBinding is { ScriptName: "boss_onyxia", SpellListId: 0 });
        Check("Onyxian Warder is bound to creature_spells 121290",
            data.Binding(12129u) is { SpellListId: 121290 });

        EncounterDefinition derivedOnyxia = EncounterTranslator.FromDatabase(
            10184u, onyxiaBinding?.Name ?? "Onyxia", onyxiaBinding?.SpellListId ?? 0,
            onyxiaBinding?.ScriptName, onyxiaBinding?.AiName, data, facts);
        Check("deriving Onyxia from the DB declares her C++ hole",
            derivedOnyxia.WorstFidelity() == EncounterFidelity.UnknownUnmodeled &&
            derivedOnyxia.Coverage.HasFlag(EncounterCoverage.CppCreatureScript));

        CreatureBehaviourBinding? warderBinding = data.Binding(12129u);
        EncounterDefinition derivedWarder = EncounterTranslator.FromDatabase(
            12129u, warderBinding?.Name ?? "Warder", warderBinding?.SpellListId ?? 0,
            warderBinding?.ScriptName, warderBinding?.AiName, data, facts);
        Check("deriving the Warder picks up its creature_spells abilities",
            derivedWarder.Abilities.Any(a => a.Key.StartsWith("spells:")));

        // Translate a real EventAI creature end to end and simulate it.
        uint sample = data.EventsByCreature
            .Where(kv => kv.Value.Any(e => e.EventType == 0 && e.Action1Script != 0))
            .Select(kv => kv.Key).FirstOrDefault();
        if (sample != 0)
        {
            EncounterDefinition derived = EncounterTranslator.FromDatabase(
                sample, $"creature {sample}", 0, null, "EventAI", data, facts);
            Check($"a real EventAI creature ({sample}) translates", derived.Abilities.Count > 0);
            var sim = new EncounterSim(derived,
                [
                    new EncounterActorSpec("boss", "boss", sample, EncounterActorRole.Boss,
                        System.Numerics.Vector3.Zero, 0f, 2f, 1.5f, 60, 20000),
                    new EncounterActorSpec("t", "target", 0, EncounterActorRole.Friendly,
                        new System.Numerics.Vector3(4f, 0f, 0f)),
                ],
                new EncounterSimOptions { Seed = 11, RaidDpsFraction = 0f }, facts);
            sim.AdvanceTo(120_000);
            Check($"and simulates ({sim.Events.Count} events)", sim.Events.Count > 1);
        }
        else Check("found a real EventAI creature to translate", false);
    }

    /// <summary>Spell facts backed by the world DB alone — cone arcs and DB landing
    /// positions. No Spell.dbc here, which is exactly the point: these two facts do
    /// not exist in the DBC and a DBC-only resolver would miss them entirely.</summary>
    private sealed class WorldDataSpellFacts(EncounterWorldData data) : IEncounterSpellFacts
    {
        public bool TryGetRadius(uint spellId, out float radius) { radius = 0f; return false; }

        public bool TryGetConeDegrees(uint spellId, out float degrees) =>
            data.ConeDegrees.TryGetValue(spellId, out degrees) && degrees != 0f;

        public bool TryGetSpeed(uint spellId, out float yardsPerSecond)
        { yardsPerSecond = 0f; return false; }

        public bool TryGetCastTimeMs(uint spellId, out int castTimeMs)
        { castTimeMs = 0; return false; }

        public bool TryGetDatabasePosition(uint spellId, out System.Numerics.Vector3 position)
        {
            position = default;
            if (!data.TargetPositions.TryGetValue(spellId, out SpellTargetPosition? row)) return false;
            position = row.Position;
            return true;
        }

        public string? SpellName(uint spellId) => null;
    }

    // ── fixtures ─────────────────────────────────────────────────────────────

    private static EncounterAbility Ability(
        string key, uint spellId, FootprintKind kind,
        float radius = 0f, float coneDegrees = 0f, float width = 0f) => new(
        key, key, spellId,
        EncounterTriggerSpec.Manual, EncounterTiming.Never, EncounterTargetSpec.Caster,
        new EncounterGeometrySpec(kind, radius, coneDegrees, width),
        EncounterFidelity.ExactDb);

    private static List<EncounterActorSpec> Scenario() =>
    [
        new("boss", "Test Boss", 1, EncounterActorRole.Boss, Vector3.Zero, 0f, 4f, 5f, 63, 100000),
        new("tank", "tank", 0, EncounterActorRole.Friendly, new Vector3(6f, 0f, 0f)),
        new("ranged", "ranged", 0, EncounterActorRole.Friendly, new Vector3(25f, 0f, 0f)),
    ];

    private static EncounterDefinition TwoPhaseTestEncounter() => new(
        Key: "test-two-phase",
        Name: "Test Two Phase",
        PrimaryEntry: 1,
        Phases:
        [
            new EncounterPhase("ground", "ground",
                Transitions:
                [
                    new EncounterTransition("air",
                        new EncounterTriggerSpec(EncounterTriggerKind.HealthBelow, 0.65f),
                        EncounterFidelity.DeclaredCppManifest,
                        Steps:
                        [
                            new EncounterStep(EncounterStepKind.Say, Note: "takes off"),
                            new EncounterStep(EncounterStepKind.Wait, DurationMs: 4000),
                            new EncounterStep(EncounterStepKind.SetFlying, Flag: true),
                        ]),
                ]),
            new EncounterPhase("air", "air", CasterFlying: true),
        ],
        Abilities:
        [
            new EncounterAbility("cleave", "Cleave", 100,
                new EncounterTriggerSpec(EncounterTriggerKind.Timer),
                new EncounterTiming(2000, 3000, 3000, 5000),
                EncounterTargetSpec.Victim,
                new EncounterGeometrySpec(FootprintKind.Cone, 12f, 90f),
                EncounterFidelity.ExactDb, Phases: ["ground"]),
            new EncounterAbility("sweep", "Tail Sweep", 15847,
                new EncounterTriggerSpec(EncounterTriggerKind.Timer),
                new EncounterTiming(3000, 3000, 4000, 4000),
                EncounterTargetSpec.Caster,
                new EncounterGeometrySpec(FootprintKind.Cone, 15f, -120f),
                EncounterFidelity.ExactDb),
            new EncounterAbility("p2only", "Fireball", 200,
                new EncounterTriggerSpec(EncounterTriggerKind.Timer),
                new EncounterTiming(1000, 1000, 3000, 3000),
                new EncounterTargetSpec(EncounterTargetKind.RandomHostile),
                new EncounterGeometrySpec(FootprintKind.Circle, 5f),
                EncounterFidelity.ExactDb, Phases: ["air"]),
        ],
        Provenance: new EncounterProvenance("test"),
        Coverage: EncounterCoverage.Template,
        Actors: Scenario());

    /// <summary>A hand-built world-DB snapshot: two creature_spells slots, a couple
    /// of EventAI rows and their action scripts.</summary>
    private sealed class TestWorldData
    {
        public EncounterWorldData Build() => new()
        {
            FetchedUtc = DateTime.UtcNow,
            Source = "test",
            SpellListsByEntry = new Dictionary<uint, CreatureSpellList>
            {
                [121290] = new(121290, "Onyxia's Lair - Onyxian Warder",
                [
                    // delays already in ms, mirroring the parser's x1000 conversion
                    new CreatureSpellSlot(1, 20203, 100, 0, 0, 0, 0, 1000, 1000, 12000, 12000, 0),
                    new CreatureSpellSlot(2, 18958, 100, 1, 0, 0, 0, 0, 0, 20000, 20000, 0),
                ]),
            },
            EventsByCreature = new Dictionary<uint, IReadOnlyList<EventAiEvent>>
            {
                [1842] =
                [
                    new EventAiEvent(184201, 1842, 0, 0, 0, 100, 0, 8000, 8000, 8000, 8000,
                        900001, 0, 0, "timer cast"),
                    new EventAiEvent(184202, 1842, 0, 2, 0, 100, 0, 30, 0, 20000, 20000,
                        900002, 0, 0, "hp gate"),
                ],
            },
            ScriptsById = new Dictionary<uint, IReadOnlyList<AiScriptCommand>>
            {
                [900001] =
                [
                    new AiScriptCommand(900001, 0, 15, 11976, 0, 0, 0, 0, 0, 1, 0, 0,
                        Vector3.Zero, 0f, "cast"),
                ],
                [900002] =
                [
                    new AiScriptCommand(900002, 0, 15, 12471, 0, 0, 0, 0, 0, 4, 0, 0,
                        Vector3.Zero, 0f, "cast at random"),
                ],
            },
            TargetPositions = new Dictionary<uint, SpellTargetPosition>(),
            ConeDegrees = new Dictionary<uint, float>(),
        };
    }

    // ── harness ──────────────────────────────────────────────────────────────

    private static void Section(string name) => Console.WriteLine($"\n[{name}]");

    private static void Check(string what, bool ok)
    {
        if (ok) { _passed++; Console.WriteLine($"  ok   {what}"); }
        else { _failed++; Console.WriteLine($"  FAIL {what}"); }
    }
}
