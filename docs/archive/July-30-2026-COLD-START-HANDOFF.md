# July 30, 2026 — cold-start regression handoff

## Purpose and authority

This handoff records the PLAN_17 cold-start pass on the current dirty tree. The pass followed
`PLAN_17_COLD_START.md` in order, with `PLAN_TEMPLATE.md`, `PLAN_07_HITCH_RECORDER.md`, and the
July-30 gameplay handoff as the verification discipline. Existing unrelated changes were kept.
No S7 work was started.

## What landed

### I1 — creature hitch visibility

- Creature draw, model-load time, loads-per-frame, cache size, selection ring, and spell-effect
  meshes now reach the frame ring and JSON (`Program.Hitch.cs:208`,
  `Engine/HitchRecorder.cs:388`).
- `DominantPhase()` distinguishes `creature-model-load` and `creature-render`
  (`Engine/HitchRecorder.cs:638-639`).
- The DevTools `Reload creature models` button is at `Program.Hitch.cs:653`.

Self-test artifact: `dumps/hitch-32-48-3.json`. It recorded a 248.1 ms frame with
`dominantPhase: creature-model-load`, `creatureLoadMs: 246.33`, 19 loads, and 19 cache entries.

### I2 — load timeline

`Program.LoadTimeline.cs` records every BeginWorldLoad-to-clear cycle. The retained dumps contain
phase durations/reasons, entry/exit queue depths, first-ten queue dequeues and distances, packets
pumped, units known, first creature draw, curtained maximum frame, GC/thread data, and events.

### S1 through S4

- S1 pumps the network before the loading guard, limits each drain to 256 packets or 2 ms
  (`Program.Net.cs:26-27`, `:229`), runs the movement sender during load (`Program.cs:1116`), and
  guards BeginWorldLoad re-entry by active map.
- S2 counts deferred WMO tiles in the phase gate, keeps progress totals synchronized, makes the
  load placement path cache-only (`Program.Loading.cs:334`), and emits `[load] WATCHDOG <phase>`
  alarms (`Program.Loading.cs:170`).
- S3 references the real 12 ms warm budget (`Program.Loading.cs:492`), yields instead of spinning
  on unfinished WMO workers/uploads, skips the hidden world pass until Fade
  (`Program.cs:1658`), and uses the allocation-free single-tile readiness overload.
- S4 uses nearest-first terrain, WMO, outdoor-doodad and cold-start interior-doodad queues. WMO
  roots use a distance priority queue (`World/Wmo/WmoRenderer.cs:383`). The approved player-tile
  plus eight-neighbour gate is at `Program.Loading.cs:302`.

The unused synchronous `TerrainRenderer.LoadAround`, WMO/Doodad `DrainPreloads`,
`TakeNewDoodadModelPaths`, and `DoodadCollisionRebuildThreshold` were deleted. The stale loading
and hitch comments/docs were reconciled.

## Measurements

### Instrument-only baseline — three cold starts

| Dump | Curtain ms | Packets pumped | Units at clear | First creature ms | Max curtained frame ms |
|---|---:|---:|---:|---:|---:|
| `load-azeroth-baseline-1.json` | 3286.2 | 0 | 41 | 0 | 238.0 |
| `load-azeroth-baseline-2.json` | 3152.6 | 0 | 39 | 0 | 258.5 |
| `load-azeroth-baseline-3.json` | 3218.9 | 0 | 38 | 0 | 231.9 |

The first pre-baseline attempt counted packets remaining in the same PumpNet call that entered
BeginWorldLoad. It is retained as `prebaseline-boundary-invalid.json`; the recorder boundary was
corrected before the three baselines.

### Step A/B

| Dump | Named result |
|---|---|
| `load-azeroth-s1.json` | packets pumped moved 0 → 35; 39 units known; first draw 0 ms |
| `load-azeroth-s2.json` | WarmBuildings entry WMO depth 9; every phase condition-met |
| `load-azeroth-s3.json` | max curtained frame moved 146.1 → 99.2 ms; total rose to 10.29 s under the real budget |
| `load-azeroth-s4-sorted.json` | all three required first-ten distance lists monotone; total 10.31 s |
| `load-azeroth-s4-minimal.json` | total 9.86 s, about 450 ms below sorting-only; Terrain itself moved only 2 ms |

`s3-busyspin-invalid.json` is retained as a rejected intermediate. It proved that treating an
unfinished WMO worker as progress starved that worker under the wall-clock loop; the contract was
fixed before the accepted S3 dump.

### Final post-reconciliation runs

| Dump | Curtain ms | Packets | Units | First creature ms | Max curtained frame ms | Exit reasons |
|---|---:|---:|---:|---:|---:|---|
| `load-azeroth-final-1.json` | 9891.6 | 69 | 42 | 0 | 94.3 | all condition-met |
| `load-azeroth-final-2.json` | 9957.8 | 79 | 40 | 0 | 97.0 | all condition-met |
| `load-azeroth-final-3.json` | 10157.8 | 78 | 39 | 0 | 91.0 | all condition-met |

Terrain, WMO, and interior-doodad first-ten distances are monotone in all three final dumps. GC and
thread blocks are present in all three.

## Verification completed

- Release solution build passed. The sole warning is the pre-existing CA2014 in
  `Engine/UI/GlueAdditive.cs:141`; no new warning was introduced.
- Combat/movement/targeting/wire targeted checks passed.
- Portrait camera and MPQ ordering targeted checks passed.
- `git diff --check` passed.
- I1 live self-test passed at Hmnpala, Elwynn Forest, tile `[32,48]`.
- Three baseline and three final cold-start timeline records were retained.

S5 and S6 were not started. PLAN_17 makes them conditional on steps 1–7 landing clean, and the
final curtained maximum remains 91–97 ms rather than the required 40 ms. S7 remains out of scope.

## Required live sign-off

Run a fresh character at the Hmnpala/Elwynn `[32,48]` vantage with DevTools on and stock settings,
then verify all of the following:

1. Keep the client running for 60 seconds after curtain clear. Check the matching
   `load-*.json.maxFrameMsDuringCurtain` and every `hitch-*.json` in the clear-plus-60-second
   window. Required result: no frame above 40 ms and no `dominantPhase: unmeasured`. The automated
   runs do **not** satisfy this yet: their curtained maxima are 91–97 ms.
2. Check `unitsKnownAtClear > 0` and `timeToFirstCreatureDrawMs <= 2000`, then visually confirm the
   first NPC/creature is actually visible within two seconds. The final dumps report 39–42 units
   and 0 ms, but visual timing still needs human sign-off.
3. Click Enter World and watch the whole transition. Required result: immediate full-screen cover,
   no dialog/action bar/HUD leak, and the first revealed frame is populated and already faded in;
   no black or half-scene flash at Fade entry.
4. Repeat PLAN_08's Elwynn/Stormwind tile-crossing `[stream]` protocol and inspect the matching
   hitch record. Required result: no crossing frame above 40 ms.

Until items 1 and 4 pass, PLAN_17's definition of done is not met and S5/S6 should remain gated.
