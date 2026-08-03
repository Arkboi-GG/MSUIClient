| Evidence run | Loader phase at maximum | Maximum | Update / render | dominantPhase | Timed bucket 1 | Timed bucket 2 |
|---|---:|---:|---:|---|---:|---:|
| `load-azeroth-attribution-1.json` | Finish | 101.35 ms | 0.00 / 84.59 ms | `creature-model-load` | creature-model-load 55.87 ms | unaccounted 16.49 ms |
| `load-azeroth-attribution-2.json` | Finish | 93.84 ms | 0.00 / 72.72 ms | `creature-model-load` | creature-model-load 50.76 ms | unaccounted 20.57 ms |
| `load-azeroth-attribution-3.json` | Fade | 102.20 ms | 0.00 / 101.83 ms | `creature-model-load` | creature-model-load 85.94 ms | creature-render 6.54 ms |

# 2026-07-30 curtain-frame handoff

## Step 1 — frame attribution

The retained `load-azeroth-final-1..3.json` files contain the scalar maximum but no `frames[]`
ring, and process-local hitch numbering overwrote the hitch files from the first two final runs.
The one surviving final-run hitch did not contain the maximum. Attribution from those files
would therefore have been invented. An allocation-free 1,024-entry value-type curtain ring was
added to I2, startup hitch suppression now ends at `BeginWorldLoad`, and three otherwise unchanged
cold starts were captured as `load-azeroth-attribution-1..3.json`.

The maximum is consistent: the first render after Finish advances to Fade synchronously loads
creature models. The neighbouring Fade frames continue doing the same work:

- run 1: 101.35 ms Finish, then 75.08 / 65.32 ms Fade; creature loads 55.87 / 59.46 / 56.55 ms.
- run 2: 93.84 ms Finish, then 51.86 / 83.26 ms Fade; creature loads 50.76 / 39.06 / 73.80 ms.
- run 3: 97.11 ms Finish immediately before the 102.20 ms Fade maximum, then 65.80 / 80.32 ms;
  creature loads 57.11 / 85.94 / 57.63 / 70.88 ms.

The hitch recorder did capture threshold trips during each curtain, but its existing cooldown
means it does not promise one file per trip; the maximum-frame evidence is the new I2 ring. No
maximum is a gen2 pause, WMO/model-finalize frame, placement phase, or collision phase.

## Fixes landed after attribution

The table convicted the first creature adoption, not placement, collision, WMO finalize, or gen2.
The curtained creature path now adopts at most one cold model per frame and returns immediately
after adoption; drawing that model begins on the following frame (`CreatureRenderer.cs:232-280`).
The first fully opaque Fade render also omits creature drawing (`Program.cs:1769`), so Finish and
the first world render cannot stack with that adoption.

Two additional named allocations appeared only after the creature spike was removed:

- Entering the world rebuilt the selected player although character select already owned the same
  renderer. `GlueBooth.TakeCharacter` transfers it (`GlueBooth.cs:95`), the receiver preserves live
  tuning (`CharacterRenderer.cs:579`), and the network transition adopts it (`Program.Net.cs:211`).
- The first equipment packet rebuilt the character atlas even when its display IDs and inventory
  types matched the selected character. `EquipmentVisuallyMatches` now advances the live signature
  without recomposition (`Program.Inventory.cs:76-112`).
- `BeginWorldLoad` no longer decodes the same loading-screen art a second time when the authoritative
  map matches the already-armed curtain (`Program.Loading.cs:39,107,131-135`).

I2 was tightened while diagnosing the split: its fixed 1,024-entry value-type ring now retains the
loader phase and complete frame sample (`Program.LoadTimeline.cs:15,48-71`), and Update separately
names the curtain network pump and loader step (`HitchRecorder.cs:354-355,617-618`). The recorder
path remains fixed-capacity and allocation-free per frame.

## Step 3 — `unitsKnownAtClear` correction

The unit count is sampled before every frame's network pump (`Program.cs:1112`,
`Program.LoadTimeline.cs:182-185`) and the clear record consumes that snapshot
(`Program.LoadTimeline.cs:265`). Old baselines that say units were known while
`packetsPumpedDuringLoad` was zero measured the clear-frame backlog drain and must not be used as
evidence for S1. The corrected final runs report 39/40/40 units with 71/74/68 packets pumped.

## Forty-millisecond acceptance

The final three pre-S5 cold starts are:

| Dump | Curtain total | Maximum curtained frame | Units at clear | Exit reasons |
|---|---:|---:|---:|---|
| `load-azeroth-adoption-only.json` | 10,073.3 ms | 37.8 ms | 39 | condition-met |
| `load-azeroth-acceptance-2.json` | 10,158.1 ms | 32.6 ms | 40 | condition-met |
| `load-azeroth-acceptance-3.json` | 9,774.2 ms | 30.6 ms | 40 | condition-met |

Terrain, WMO, and interior-doodad first-ten distances are monotone in all three. No threshold
(>=25 ms) frame in any curtain has `dominantPhase: unmeasured`. This satisfies the automated
PLAN_17 section 8 frame ceiling and unlocks S5.

## S5 — parallel WMO preparation

`WmoRenderer` now keeps up to four root preparations in flight (`WmoRenderer.cs:394,1353`) and
round-robins only ready jobs through the existing one-group budgeted finalizer. `AssetWorkerPool`
reserves two of its normal eight slots from general model work (`AssetWorkerPool.cs:14-25`), while
ADT parsing uses the critical path (`AssetWorkerPool.cs:47`, `AdtCache.cs:194`).

Baseline `WarmBuildings` was 8,173.5 / 8,156.9 ms. Three matched character-select S5 runs were:

| Dump | Curtain total | WarmBuildings | Maximum | Exit reasons |
|---|---:|---:|---:|---|
| `load-azeroth-s5-normal-1.json` | 9,340.5 ms | 7,376.4 ms | 32.1 ms | condition-met |
| `load-azeroth-s5-normal-2.json` | 7,372.5 ms | 5,410.7 ms | 32.7 ms | condition-met |
| `load-azeroth-s5-normal-3.json` | 9,040.3 ms | 7,377.2 ms | 33.0 ms | condition-met |

The named field moves by 10-34%, but not the several-fold estimate. This vantage is usually
dominated by serial Stormwind group finalization: the existing over-budget alarm repeatedly records
16-17 ms for a group. Per the pass rule, groups were logged and were not sliced internally. All
three S5 runs retain monotone first-ten queues and have zero threshold `unmeasured` frames.

Two `load-azeroth-s5-auto-*` diagnostic dumps are deliberately excluded: naming a character in
config bypasses character select, so there is no booth renderer to reuse and the first network pump
performs a 109-127 MB player-atlas rebuild. That is not the accepted vantage.

## Verification completed

- Release solution build: passed; only the pre-existing CA2014 warning at
  `Engine/UI/GlueAdditive.cs:141`.
- `MSUICombatWireCheck`: passed.
- `MSUIPortraitCameraCheck`: passed.
- `git diff --check`: no whitespace errors (only line-ending notices).
- Three pre-S5 and three post-S5 matched cold starts: automated invariants above are green.

S6 was not started. It remains gated on S5's controlled tile-crossing regression check. S7 remains
out of scope.

## Required live sign-off

1. At the Hmnpala/Elwynn `[32,48]` vantage, keep the client running for 60 seconds after curtain
   clear. Visually confirm the first nearby NPC/creature appears within 2 seconds. In the matching
   `load-*.json`, check `timeToFirstCreatureDrawMs`, `unitsKnownAtClear`, and
   `packetsPumpedDuringLoad`; in any hitch dump check `creature-model-load` and `creature-render`.
2. Repeat PLAN_08's exact saved tile crossing before and after S5 with stock settings. Diff the
   `[stream]` line and hitch record field by field—especially `frameMs`, `dominantPhase`,
   `update.preloadMs`, `update.terrainAdoptMs`, `gc`, queue depths, and uploads. This is the remaining
   S5 regression gate and therefore the gate on S6.
3. Visually confirm the transferred selected player has identical appearance/equipment and the
   loading-screen art does not flash or change during the authoritative `BeginWorldLoad` transition.

## New-character live capture — 2026-07-30 16:07

User-created character, normal account/character-create/Enter World flow. Retained as
`dumps/load-azeroth-new-character-1.json` at the `[31,43]` start.

- Curtain total: 3,035.9 ms; every phase exited condition-met.
- 25 packets were pumped and 8 units were known before the clear-frame pump.
- The recorded curtained maximum was 74.4 ms in WarmBuildings: 57.8 ms
  `swap-and-events`/Present plus 16.5 ms `load-phase-step`. The largest loader-owned frame was
  28.0 ms. This run therefore fails the literal 40 ms total-frame ceiling because of a swap stall,
  not because a loader phase exceeded the budget.
- `timeToFirstCreatureDrawMs` remained 0: no creature drew before curtain clear despite the eight
  known units.
- About 38 seconds after the 16:07:03 clear, `hitch-31-43-11.json` recorded a 117.7 ms
  `creature-model-load` frame. Six models were adopted together, the cache jumped 42 -> 48,
  61.5 MB was allocated, and GC paused for 5.9 ms. This reproduces the delayed-NPC complaint on the
  new-character path and is the next falsifiable creature-pipeline defect; it is separate from the
  now-fixed curtain phase work.
