# July 30, 2026 — S6 creature pipeline handoff

## Step 0 attribution table

| Cohort | Packet arrival | Display resolve / enqueue | Model ready / first draw | Verdict |
|---|---:|---:|---:|---|
| Units already in the initial object stream | Before or during the curtain | Deferred until world rendering became eligible | Drawn after the curtain in the pre-fix run | Client backlog existed for the initial cohort. |
| Six synchronous-burst units from `hitch-31-43-11.json` | Approximately +38 s after clear in the Step-0 rolling lifecycle record | Immediately after arrival, in the same update/render interval | The same interval; the trigger frame was 117.7 ms | Server-side arrival gap, not 38 s sitting in a client queue. |
| Later streamed units in the automated Northshire runs | Distinct server bursts at roughly +12 s, +42 s and +55 s depending on run | 7–16 ms after each packet | Immediate when already cached and eligible | The client pipeline follows arrival; server visibility cadence remains a separate protocol finding. |

The original convicted frame is `dumps/hitch-31-43-11.json`: 117.6872 ms,
`dominantPhase: creature-model-load`, 107.1768 ms creature load, six loads, 48 cache entries,
61.53 MB allocated, one gen-2 collection, plus 64.62 ms HUD work in the same frame. The event ring
shows repeated `DwarfMale.m2` parses by appearance, one attachment shader compile per NPC, and
repeated identical weapon loads.

The +38 s gap is server-side. During the quiet interval the client emitted pings but no stationary
movement heartbeat and no zone/update packet. That is a named protocol follow-up; it was not folded
into S6. The later Northshire automation also shows that the server streams broad, off-camera cohorts
in waves. At +60 s the closest retained unit was 122.1 yards away and outside the frustum; the rest
were outside the 130-yard admission radius. No radius widening was made.

## Landed work and A/B evidence

1. Attribution instrumentation
   - `CreatureLifecycleTracker` records packet receipt, display resolution, enqueue, ready, first
     draw, reason, admission result/distance, and the entity/camera/target coordinates.
   - Load and hitch records carry lifecycle and outgoing-protocol snapshots.
   - Every curtain clear schedules `dumps/creature-load-<map>-<n>.json` at +60 s, so a quiet run is
     measurable even when no hitch is tripped.

2. Cache split
   - Mesh, skeleton, animator and GPU buffers are keyed by model path; visible geosets and the type-1
     skin are keyed by appearance suffix.
   - The A/B new-character run reduced the model cache from 48 appearance-shaped entries to seven
     model-path entries (`load-azeroth-s6-1.json` / `hitch-s6-1-cache.json`).

3. Lazy animation bake
   - Creature animators use the existing `M2Animator.FindOrBake` path instead of eagerly building all
     28 requested clips.
   - The convicted creature-load allocation fell from 61.53 MB to 12.1 MB in the S6.2 A/B.

4. Async prepare/upload and budgeted admission
   - MPQ read, M2 parse, interleave, geosets and BLP decode run on `AssetWorkerPool`; uploads go through
     `GpuUploadWorker`; VAO finalization stays on the main thread.
   - Finalization has a measured 2 ms/frame budget. Units are ordered by camera distance, then held
     until inside the radius and frustum. `TryGetModel` never loads; all HUD/portrait callers tolerate
     a miss. The original HUD number was overlap in the global hitch frame, not a HUD-initiated load.
   - `load-azeroth-s6-3.json` reduced creature-load time from 36.9–107.2 ms to 0–0.1 ms in the watched
     frames. No later automated run produced a creature-dominant hitch.

5. Shared attachments
   - `AttachedItemRenderer` now owns process-wide shader/model/texture resources per GL context.
     Creature GUIDs retain only lightweight mount sets and retire after 30 seconds absent.
   - `cycle-s6-5-final.log`, `cycle-s6-6-final.log`, and `cycle-s6-signoff-1.log` each contain exactly
     one `[shader] attached compiled and linked` line.

6. Miss-storm removal
   - Doodad and foliage variant iteration is `.m2` first; character and attachment candidates already
     had that order and retain it.
   - When `MpqMount` is installed it is authoritative: a miss does not reopen all archives.
     Negative paths are memoized in the mount.
   - I2 now reports `mpqArchiveOpensDuringLoad`. `load-azeroth-13.json`, `-14.json`, and `-15.json`
     report zero.

7. Fresh-character lifecycle tooling and delete repair
   - `tools/cold-start-cycle` owns create, unique naming, exact-character selection, launch,
     observation, shutdown, deletion, roster verification, stale `Cold*` cleanup, reconnect backoff,
     per-label exclusion, and failure-path cleanup.
   - `cycle-harness-recovery.json` proves stale cleanup plus roster 9 → 10 → 9. The S6.6 and sign-off
     cycles also completed with successful create/delete result codes and no leftover test character.
   - MSUI's delete dialog now follows Benilla's `char_select/dialog.rs`: it snapshots the target,
     requires case-insensitive `DELETE`, disables Okay until armed, supports Enter/Escape, and prevents
     the full-scene booth drag catcher from stealing modal input. Protocol deletion is verified by the
     cycle tool; physical mouse interaction remains a live UI check.

## Verification completed

- Release solution build passes. The only warning is the pre-existing CA2014 at
  `Engine/UI/GlueAdditive.cs:141`; no warning was added.
- Combat/movement/targeting/wire targeted checks pass.
- Portrait-camera defaults, archive ordering and HumanMale provenance checks pass.
- `git diff --check` reports no whitespace errors (only existing line-ending notices).
- `load-azeroth-13.json`: all phases condition-met, all first-ten dequeue distances monotone,
  `unitsKnownAtClear = 15`, `mpqArchiveOpensDuringLoad = 0`, one attachment shader compile, and no
  post-clear creature hitch.
- The first attempted final run (`load-azeroth-14.json`) was deliberately not counted: max curtain
  frame was 58.8287 ms, with 42.7898 ms in `load-network-pump`, 29.48 MB allocated and 12.124 ms GC.
- Object-update parsing was then moved off-thread and object application was sliced under the existing
  2 ms network budget while preserving packet order. Its A/B (`load-azeroth-15.json`) moved the maximum
  from 58.8287 to 43.2664 ms, but did not clear the 40 ms ceiling.

## Required live sign-off / remaining automated blockers

1. **40 ms ceiling — blocked, named.** Vantage: fresh Human character at Northshire, stock settings,
   from `BeginWorldLoad` through clear +60 s. In `load-azeroth-15.json`, inspect frame 17:
   43.2664 ms, `dominantPhase: load-network-pump`, 39.7826 ms in the pump. The log brackets this with
   `ApplyServerCharacter`; the auto-login path has no cached booth avatar and synchronously rebuilds
   the player appearance/equipment. Frame 18 is 41.5924 ms with `dominantPhase: gc-pause-gen2`,
   16.131 ms GC and 38.8149 ms unaccounted while the off-thread object parse allocates 20.27 MB.
   These must be split/reused before the three-run counter restarts.

2. **Per-unit ≤2 s SLA — not exercisable at the unattended default facing yet.** Vantage: the same
   fresh Northshire spawn. Inspect `creature-load-azeroth-14.json`: no unit qualified for draw by +60 s;
   the closest retained unit is 122.1 yards away but `OUT_OF_FRUSTUM`, and all others are outside the
   130-yard radius. Rotate toward a visible spawned unit (or add deterministic camera automation), then
   verify `firstDrawMs - spawnPacketMs <= 2000` for every `VISIBLE` lifecycle row.

3. **Delete modal mouse path.** Vantage: character select with any disposable test character. Click
   Delete Character, type `DELETE`, click Okay, and verify the modal closes and the roster row disappears.
   Also verify Escape cancels and Enter confirms only while armed. The underlying network create/delete
   path and exact-target cleanup are already automated and green.

4. **Standing streaming regression from PLAN_08.** At the recorded Elwynn/Stormwind crossing, compare
   the `[stream]` line and hitch dump against the prior handoff. This was not exercised by stationary
   fresh-character automation.

5. **Parked external stall.** The 57.8 ms Present/swap stall from the curtain investigation remains in
   the swap-and-events family and was not chased in S6.

S7 was not started.
