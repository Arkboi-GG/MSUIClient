using System.Numerics;
using ImGuiNET;
using MSUIClient.Engine;
using Silk.NET.Input;
using MSUIClient.World.Encounters;

namespace MSUIClient;

// ─────────────────────────────────────────────────────────────────────────────
// Encounter Lab raid probe: a scripted end-to-end proof of the owner's flow,
// in the REAL client against the REAL code paths — no headless shortcuts.
// Activated by the MSUI_ENCLAB_PROBE environment variable; the client boots
// into the creator world at the persisted location (the owner's walkway spot
// in Onyxia's Lair), opens the Lab exactly like Ctrl+E, loads onyxia.json,
// presses the real "Place raid (10)" path, and then MEASURES:
//
//   - every raid body's distance from the controller ("at my feet" or not)
//   - the nearest body's distance from her ring (staged outside, or not)
//   - the outcome line (never pulled until ordered in, or not)
//   - her drift off spawn on the pre-simulated timeline (roaming, or not)
//   - ToggleFreeView() offline (Ctrl+F works, or not)
//
// Two screenshots (dumps/gameplay-enclab-probe-*.png) capture the visual
// truth, and the verdict prints PASS/FAIL per claim. Instrumentation-hazard
// rule as everywhere: this whole file no-ops unless the env var is set.
// ─────────────────────────────────────────────────────────────────────────────

public sealed partial class GameLoop
{
    private static readonly bool EncLabProbeArmed =
        Environment.GetEnvironmentVariable("MSUI_ENCLAB_PROBE") is { Length: > 0 };

    private int _encLabProbeStage;          // 0 boot, 1 world-wait+load, 2 live-motion, 3 act+measure, 4 dump, 5 freeview+dump, 6 verdict, 7 done
    private Vector3 _encLabProbeBossAtLoad;
    private double _encLabProbeAt;
    private int _encLabProbeFailures;

    private void EncLabProbeCheck(string name, bool ok, string detail = "")
    {
        Console.WriteLine($"[enclab-probe] {(ok ? "PASS" : "FAIL")}  {name}" +
                          (detail.Length > 0 ? $"  [{detail}]" : ""));
        if (!ok) _encLabProbeFailures++;
    }

    private void UpdateEncounterLabProbe()
    {
        if (!EncLabProbeArmed || _encLabProbeStage >= 99) return;
        double now = NowSeconds();

        switch (_encLabProbeStage)
        {
            case 0:   // boot into the creator world at the persisted (lair) location
                if (_gl is null || _worldLoadStarted) return;
                if (_encLabProbeAt == 0) { _encLabProbeAt = now; return; }
                if (now - _encLabProbeAt < 1.0) return;
                _config.DevTools = true;   // the probe's screenshots ride the gameplay-dump machinery
                Console.WriteLine($"[enclab-probe] entering creator world at persisted loc: " +
                    $"map {Settings.Creator.LocMap} ({Settings.Creator.LocX:0.0}, " +
                    $"{Settings.Creator.LocY:0.0}, {Settings.Creator.LocZ:0.0})");
                EnterOfflineWorld();
                _encLabProbeStage = 1; _encLabProbeAt = now;
                return;

            case 1:   // world ready → open the Lab (the Ctrl+E path) and load the doc
                if (_worldLoading || !_creatorWorldRequested || _controller is null) return;
                if (now - _encLabProbeAt < 3.0) return;
                if (!_encounterLabOpen) ToggleEncounterLab();
                EncounterLibraryRef.Reload();
                if (EncounterLibraryRef.Get("onyxia") is not { } doc)
                {
                    EncLabProbeCheck("onyxia.json loads", false, "document missing");
                    _encLabProbeStage = 6; _encLabProbeAt = now;
                    return;
                }
                LoadEncounterDocument(doc);
                EncLabProbeCheck("onyxia.json loads", true,
                    $"{doc.Phases.Count} phases, {doc.Abilities.Count} abilities");
                _encLabProbeBossAtLoad = _encounterSim?.Boss?.Position ?? Vector3.Zero;
                _encLabProbeStage = 2; _encLabProbeAt = now;
                return;

            case 2:   // PRE-PULL HOLD: stand still, touch nothing — the CLOCK must not run.
            {
                // The owner's rule: the fight timer does not start until the pull. With
                // no body in her ring there is no pull, so playback HOLDS at t=0 - the
                // clock stays put and she does not move on her own. (Her roam still lives
                // in the pre-simulated timeline; it is scrubbable, just not auto-played.)
                if (now - _encLabProbeAt < 4.0) return;
                Vector3 bossNow = _encounterSim?.Boss?.Position ?? Vector3.Zero;
                float moved = Vector3.Distance(bossNow, _encLabProbeBossAtLoad);
                bool held = _encounterViewMs < 200 && (_encounterSim?.EngagedAtMs ?? -1) < 0;
                EncLabProbeCheck("clock HOLDS before the pull (no clicks, no scrub)",
                    held,
                    $"engaged={_encounterSim?.EngagedAtMs}, view at {_encounterViewMs / 1000f:0.0}s, " +
                    $"she has drifted {moved:0.0} yd");
                _encLabProbeStage = 3; _encLabProbeAt = now;
                return;
            }

            case 3:   // THE ACT: the real Place raid path, then measure everything
            {
                Vector3 me = _controller!.Position;
                PlaceScenarioRaid();

                List<EncounterActorSpec> raid = _encounterScenario
                    .Where(a => a.Role == EncounterActorRole.Friendly && a.Job != RaidJob.None)
                    .ToList();
                EncounterActorSpec boss = _encounterScenario
                    .First(a => a.Role == EncounterActorRole.Boss);

                EncLabProbeCheck("raid is 10 bodies with jobs", raid.Count == 10,
                    $"{raid.Count} bodies: " +
                    $"{raid.Count(a => a.Job == RaidJob.Tank)}T/" +
                    $"{raid.Count(a => a.Job == RaidJob.Healer)}H/" +
                    $"{raid.Count(a => a.Job == RaidJob.Melee)}M/" +
                    $"{raid.Count(a => a.Job == RaidJob.Ranged)}R");

                if (raid.Count > 0)
                {
                    float maxFromMe = raid.Max(a => Vector3.Distance(a.Position, me));
                    float minFromBoss = raid.Min(a =>
                        new Vector2(a.Position.X - boss.Position.X,
                                    a.Position.Y - boss.Position.Y).Length());
                    EncLabProbeCheck("raid forms AT MY FEET", maxFromMe < 12f,
                        $"controller at ({me.X:0.0}, {me.Y:0.0}, {me.Z:0.0}); " +
                        $"farthest body {maxFromMe:0.0} yd from me");
                    EncLabProbeCheck("raid is staged OUTSIDE her pull ring",
                        minFromBoss > Settings.EncounterLab.PullRangeYards,
                        $"nearest body {minFromBoss:0.0} yd from her " +
                        $"(ring {Settings.EncounterLab.PullRangeYards:0} yd)");
                    float worstZ = raid.Max(a => MathF.Abs(a.Position.Z - me.Z));
                    EncLabProbeCheck("raid stands ON the floor (collision ground)",
                        worstZ < 3f, $"worst Z offset from my feet {worstZ:0.00} yd");
                    foreach (EncounterActorSpec a in raid)
                        Console.WriteLine($"[enclab-probe]   {a.Name,-9} at " +
                            $"({a.Position.X:0.0}, {a.Position.Y:0.0}, {a.Position.Z:0.0})  " +
                            $"{Vector3.Distance(a.Position, me):0.0} yd from me");
                }

                EncLabProbeCheck("fight waits for the walk-in (never pulled)",
                    _encounterSim is { EngagedAtMs: < 0 }, _encounterOutcome);

                // Pre-pull she STANDS at spawn (exact-db movement_type 0): read her drift
                // straight off the snapshot timeline and assert it is ~zero before the pull.
                float drift = 0f;
                if (_encounterSim is { } sim)
                    foreach (SimSnapshot snap in sim.Timeline)
                    {
                        if (snap.TimeMs > 60_000) break;
                        foreach (SimActorState s in snap.Actors)
                            if (s.Key == boss.Key)
                                drift = MathF.Max(drift, new Vector2(
                                    s.Position.X - boss.Position.X,
                                    s.Position.Y - boss.Position.Y).Length());
                    }
                EncLabProbeCheck("Onyxia STANDS at spawn pre-pull (no invented roam)", drift < 1f,
                    $"max {drift:0.0} yd off spawn in the first simulated minute");

                // Puppets spawn on the NEXT frame after a rebuild; the staging
                // test rides its own stage so it never races the spawn.
                _encLabProbeStage = 9; _encLabProbeAt = now;
                return;
            }

            case 9:   // STAGE-then-GO: the owner's queue-commands flow, as clicked
            {
                if (now - _encLabProbeAt < 1.0) return;
                EncounterActorSpec boss = _encounterScenario
                    .First(a => a.Role == EncounterActorRole.Boss);
                // Select tank 1's puppet, shift-click the floor ahead of it,
                // verify the waypoint QUEUED (nothing moved), GO, verify the walk.
                if (_encounterPuppets.TryGetValue("raid-tank1", out ulong tankGuid))
                {
                    _freecamSelection.Clear();
                    _freecamSelection.Add(tankGuid);
                    EncounterActorSpec tank = _encounterScenario.First(a => a.Key == "raid-tank1");
                    Vector3 toBoss = boss.Position - tank.Position;
                    Vector3 ahead = tank.Position +
                        toBoss / MathF.Max(toBoss.Length(), 1f) * 12f;
                    bool projected = _window.Camera.TryWorldToScreen(
                        ahead, ImGui.GetIO().DisplaySize, out Vector2 screen);
                    bool routed = projected && HandleEncounterRtsOrder(
                        new WorldMouseClick(MouseButton.Right, screen, ShiftDown: true));
                    bool queuedOnly =
                        _encounterStagedOrders.GetValueOrDefault("raid-tank1") is { Count: 1 } &&
                        (_encounterScenario.First(a => a.Key == "raid-tank1").Moves?.Count ?? 0) == 0;
                    EncLabProbeCheck("shift-click STAGES a waypoint (queued, nothing moves)",
                        routed && queuedOnly, $"projected={projected}, routed={routed}");

                    EncounterGoStagedOrders();
                    EncounterActorSpec tankAfter =
                        _encounterScenario.First(a => a.Key == "raid-tank1");
                    float walked = 0f;
                    if (_encounterSim is { } sim2)
                    {
                        int index = Math.Clamp(8_000 / sim2.Options.StepMs, 0,
                            sim2.Timeline.Count - 1);
                        foreach (SimActorState s in sim2.Timeline[index].Actors)
                            if (s.Key == "raid-tank1")
                                walked = Vector3.Distance(s.Position, tankAfter.Position);
                    }
                    EncLabProbeCheck("GO commits the plan and the body WALKS it",
                        tankAfter.Moves is { Count: 1 } && walked > 5f,
                        $"moves={tankAfter.Moves?.Count ?? 0}, walked {walked:0.0} yd by 8s");
                    _freecamSelection.Clear();
                }
                else
                {
                    EncLabProbeCheck("tank 1 puppet exists for the staging test", false);
                }

                // Park the view mid-roam so the screenshot shows her off spawn.
                ScrubTo(20_000);
                _encounterPlaying = false;
                _encLabProbeStage = 4; _encLabProbeAt = now;
                return;
            }

            case 4:   // let the puppets sync to the scrub head, then screenshot
                if (now - _encLabProbeAt < 1.5) return;
                _currentVantage = "enclab-probe-raid-at-feet";
                ArmGameplayDump();
                _encLabProbeStage = 5; _encLabProbeAt = now;
                return;

            case 5:   // Ctrl+F, the real path, offline — then screenshot the sky rig
                if (now - _encLabProbeAt < 2.0) return;
                ToggleFreeView();
                EncLabProbeCheck("Ctrl+F raises the free view OFFLINE", _freeView,
                    $"_freeView={_freeView}");
                _currentVantage = "enclab-probe-freeview";
                ArmGameplayDump();
                _encLabProbeStage = 6; _encLabProbeAt = now;
                return;

            case 6:   // verdict, flush, quit
                if (now - _encLabProbeAt < 2.5) return;
                Console.WriteLine(_encLabProbeFailures == 0
                    ? "[enclab-probe] VERDICT: ALL CHECKS PASSED"
                    : $"[enclab-probe] VERDICT: {_encLabProbeFailures} CHECK(S) FAILED");
                Console.Out.Flush();
                _quitRequested = true;
                _encLabProbeStage = 99;
                return;
        }
    }
}
