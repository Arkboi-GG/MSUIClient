# Plan 17 — Cold start: NPCs in seconds, not a minute, and no hitches you can't name

Date: 2026-07-30. Method: four parallel full readings of the staged tree (loading/ordering,
unit pipeline, IO/texture layer, frame loop + instruments), findings cross-checked against
`BENILLA_VS_MSUI_LOADING.md`, `BENILLA_VS_MSUI_PERF.md`, `PLAN_07`, `PLAN_08` and the
handbook. Every claim is `file:line` from the current bytes. This plan supersedes the
"current implementation status" note at the top of `BENILLA_VS_MSUI_LOADING.md` where they
conflict.

## 1. Problem

At a fresh character spawn (Northshire-class starting zone), the loading curtain holds for
**50–60 s before any NPC or creature is visible**, and both the curtain phase and the first
minute after it are full of multi-hundred-millisecond hitches. Paired artifact owed: a
`dumps/load-*.json` from instrument I2 (below) — the instrument that captures this end-to-end
does not exist yet, which under the working agreement makes building it task one.

The operating theory was "MSUI loads localized-first (nearest the player first)." **That
theory is false for three of the five load queues** (§4, H3) — but it is also not the main
cause. The main cause is that NPCs are structurally forbidden from existing until the curtain
fades (H1), and the curtain duration is governed by stacked 30 s watchdogs (H2).

## 2. Class

**Addition** (streaming architecture, measured against our intent and against Benilla as the
engineering reference), with one **emulation-core** edge: the presentation contract that the
world reveals populated, the way the 1.12 client's loading screen drops you into a zone that
already has its NPCs.

## 3. Target

Written intent, in measurable terms:

- Curtain clears on a *minimal* condition (player ring + collision underfoot), not the whole
  5×5 preload set, and **never by watchdog** in a healthy run.
- First NPC visible **≤ 2 s after curtain clear** (they should already be resident behind it).
- No frame over **40 ms** during the curtain or the first 60 s after clear (PLAN_08 §3's
  number, now applied to cold start), with the hitch recorder armed at its default 25 ms
  threshold and every trip's `dominantPhase` naming a real bucket — no `unmeasured` trips.
- Every load queue drains **nearest-first from the player**, verifiably (I2 logs the first
  ten dequeues per queue with distances).

## 4. Hypotheses — ranked, each falsifiable by a dump

These were hypotheses; the code reading has already confirmed all of them at the cited lines.
The dumps in §7 exist to *prove the fixes moved them*, per the template rule.

### H1 — CONFIRMED, CRITICAL: the network pump is dead for the entire load; NPCs cannot exist until the curtain fades.

```
Program.cs:1097   if (_worldLoading) { StepWorldLoad(dt); return; }
Program.cs:1099   PumpNet(dt);        // unreachable while loading
```

`PumpNet` (`Program.Net.cs:215`) is the **only** drain of the packet queue and the only
handler of `SMSG_UPDATE_OBJECT` / `SMSG_COMPRESSED_UPDATE_OBJECT` (`Program.Net.cs:222-239`).
While `_worldLoading` is true — set at `Program.Loading.cs:126` by `BeginWorldLoad`, cleared
only when the fade completes at `Program.Loading.cs:436` — zero packets are applied, so
`entities.Units` is empty and `CreatureRenderer.Render` (`CreatureRenderer.cs:258`) iterates
nothing. Creatures are last **by omission, not by design**: they are not a phase in
`WorldLoadPhase` (`Program.Loading.cs:78-90`) at all.

The code's own contract is violated twice over:
- `Program.Net.cs:150`: *"Called near the top of Update(dt), before the world-load guard."*
  It is called after.
- `Program.Loading.cs:98`: *"This curtain deliberately does not set `_worldLoading`: Update
  must keep pumping the socket."* — engineered for `ArmEnterWorldCurtain`, then destroyed by
  `BeginWorldLoad` (`:126`), which is itself invoked *from inside `PumpNet`*
  (`Program.Net.cs:197`).

Second-order effect — **the reveal hitch**: when the curtain lifts, the first `PumpNet`
drains 50–60 s of backlog in one unbounded `while` (`Program.Net.cs:215-360`) in one frame,
then the resulting entity flood hits the synchronous creature loader (H5). Also, no movement
heartbeats are sent for the entire load (`_movementSender.Update` at `Program.cs:1318` is
behind the same guard) — the server sees a silent client for a minute.

### H2 — CONFIRMED, CRITICAL: the 50–60 s is the watchdogs, and a phase-gate bug both corrupts progress and routes work to the worst path.

Each phase gets `LoadPhaseWatchdogSeconds = 30f` (`Program.Loading.cs:76`, applied `:157`).
`WarmBuildings` exits on `_wmo.PendingPreloads == 0` (`:296`); `WarmDoodads` on
`_doodads.PendingPreloads == 0` (`:350`). **30 + 30 = the reported 50–60 s** when either
queue can't drain in time (or never registers, next paragraph).

The gate itself is broken: `WmoRenderer.PendingPreloads` (`WmoRenderer.cs:427`) counts only
`_preloadQueue` + the single in-flight job, **not** `_deferredRingTiles` (`:1295`). At
`BeginWorldLoad` time the ADT cache is empty, every tile defers, and `PendingPreloads` reads
0 — so `WarmBuildings` can exit at frame 1, `_wmoWarmTotal` computes as `Math.Max(1, 0) == 1`
(`Program.Loading.cs:144`, pinning the progress bar via `:295`), and `PlaceBuildings` then
resolves every un-warmed building through the fully-blocking `ResolveModel` spin —
`WmoRenderer.cs:1716-1717`: `job.Worker.GetAwaiter().GetResult()` then
`while (!StepModelLoad(job, waitForUpload: true)) { }` — root + all groups + all textures +
all fenced uploads, on the GL thread. `BENILLA_VS_MSUI_LOADING §5` called this "the line to
kill"; it is still there and the cold path can still reach it.

### H3 — CONFIRMED: "nearest-first" is true for exactly one queue out of five.

| Queue | Ordering | Evidence |
|---|---|---|
| Terrain preload ring | **None — effectively farthest-corner-first.** `TileRing` builds a `HashSet` from `dc=-radius,dr=-radius` upward (`TerrainRenderer.cs:169-181`); `QueuePreload` bare-`foreach`es it (`:300-314`). The NW corner tile gets a worker slot before the tile under the player's feet. The only distance sort (`SetResidency`, `:240`) is adoption order, after the all-or-nothing gate `PreloadReady(ring)` (`Program.Loading.cs:281`) already waited for everything. | |
| WMO buildings | **None — FIFO in raw MODF record order.** `Queue<string>` (`WmoRenderer.cs:383`), enqueued per tile in ADT order (`:1304-1314`). `QueuePreloadForTiles` (`:1278`) has no `streamCentre` parameter at all — unlike the doodad equivalent. | |
| Doodads, outdoor MDDF | **Sorted — the one place it's right.** `QueuePreloadModels(paths.OrderBy(p => p.DistanceSq)...)` (`DoodadRenderer.cs:741`). | |
| Doodads, WMO interiors at cold start | **None.** `Program.Loading.cs:328-331` enqueues `_wmo.EnumerateDoodads(...)` with `.Distinct()` but **no `OrderBy`** — the runtime path at `Program.cs:1468-1472` has the sort; the cold-start loader omitted it. The FIFO freezes order at enqueue (`DoodadRenderer.cs:317`, `:754`), so the far side of the zone warms before the room you spawned in. In a WMO-heavy start this is the phase most likely to burn its full 30 s. | |
| Creature model loads | **None — spawn/insertion order.** `foreach (var e in entities.Units)` (`CreatureRenderer.cs:258`), no sort, no frustum test before spending a load slot. | |

### H4 — CONFIRMED: the per-frame load budget is dead code; curtain frames are unbounded.

`LoadWarmBudgetMs = 12f` (`Program.Loading.cs:70`) has **zero references** in the tree.
`DrainWarm` (`Program.Loading.cs:452-456`) is `for (int i = 0; i < 48; i++)` with no clock;
each doodad call can finalize up to 12 models (`DoodadRenderer.cs:768`, loop `:789-808`), so
the worst case is ~576 main-thread GL model builds in one frame. The code already knows single
builds exceed 8 ms (`DoodadRenderer.cs:805`, `WmoRenderer.cs:1360` warnings) and does nothing
about it. Meanwhile the **full world render pass runs every curtain frame and is overpainted
by an opaque quad** — world pass `Program.cs:1651-1656` (including `FoliageRenderer.Scatter`),
curtain drawn last at `Program.cs:1783`. Pure waste competing with the loader.

### H5 — CONFIRMED: creature loading is 100% synchronous on the render thread and shares almost nothing; this is the post-curtain lag-spike engine.

- No worker pool, no upload worker: `_creatures = new CreatureRenderer(gl, _mpq, _config);`
  (`Program.Net.cs:88`) — compare terrain/WMO/foliage/doodads which all receive `_uploads` +
  `_assetWorkers` (`Program.cs:404,428,449,489`).
- `LoadModel` (`CreatureRenderer.cs:653-770`) runs inside `Render()`: MPQ read (`:658`), full
  M2 parse (`:660`), **eager 28-clip animation bake** (`:665` → `M2Animator.Build`,
  `M2Animator.cs:297-311`, `Bake` `:541-630` — per humanoid ≈ 28 clips × ~130 bones × 3
  channels of iterator+List+array churn, the single dominant cost), vertex interleave
  (`:687-720`), synchronous VAO/VBO/EBO upload (`:722-733`), per-batch BLP decode +
  `TexImage2D` + `GenerateMipmap` (`:749-751` → `:828-850`, `Texture.cs:74`).
- Budget is a **count**, not time: `LoadsPerFrame = 4` (`CreatureRenderer.cs:102`, `:267`).
  Four humanoid loads is easily a 300–800 ms frame. And `TryGetModel` (`:543-555`) bypasses
  the cap entirely — its callers are the portrait/nameplate/selection paths.
- **The cache key defeats sharing for humanoids**: `CacheKey` (`CreatureRenderer.cs:632-635`)
  bakes NPC appearance (skin/hair/facial/equipment) into the key, so two Stormwind guards
  re-parse and re-bake the byte-identical `HumanMale.m2` and duplicate its VAO/VBO/EBO/animator.
  Only `VisibleGeosets` (`:675-685`) and the type-1 body texture (`:791,803-819`) actually vary.
- A fresh `AttachedItemRenderer` — **with its own GL shader compile+link and its own private
  model cache** — is constructed per creature GUID (`CreatureRenderer.cs:386-392`,
  `AttachedItemRenderer.cs:168-173`) and disposed after one frame of absence (`:627-628`).
  Twenty guards with the same sword parse and upload it twenty times.
- Item/character model misses take the **full-archive-rescan** path: candidate lists try
  `.mdx` first (`DoodadRenderer.cs:1077-1092` documents the same shape), and a `MpqMount` miss
  falls through `AdtTerrainReader.cs:154-165` into the War3Net fallback (`:170-196`) —
  `Directory.GetFiles` + `MpqArchive.Open` on **every** archive, re-reading and re-decrypting
  hash/block tables each time (`MpqArchive.cs:118-140`). ~15 archive opens per missed
  candidate, on the render thread. `MpqMount.cs:10-23`'s own header describes this as the
  fixed 27-second bug; the attachment/character/creature paths still take it.

### H6 — CONFIRMED: creature work is invisible to the hitch recorder — why the spikes have resisted diagnosis.

`DrawCreatures()` runs at `Program.cs:1699`, **after** `_worldRenderMilliseconds` closes
(`:1688`) and after `_characterRenderMilliseconds` closes (`:1695`). No bracket. `FramePhases`
(`HitchRecorder.cs:644-666`) has no creature field; `DominantPhase()` (`:575-640`) has no
creature `Consider`. A 600 ms creature-bake frame is charged to `UnaccountedMs` and reported
as **`unmeasured`**. Any past investigation that ended at `unmeasured` or `driver-flush`
during populated-zone play is suspect until re-run after I1.

### H7 — CONFIRMED, supporting: the IO/texture layer multiplies everything.

Status of the 2026-07-26 audit items in today's bytes:
- MpqMount global lock — **fixed** (`ReaderWriterLockSlim`, `MpqMount.cs:52,91,114`;
  `MpqArchive` positioned I/O `MpqArchive.cs:314`). Genuinely parallel now.
- No process-wide texture/pixel cache — **still present**. Terrain re-decodes every tile's
  MTEX list with no cache (`TerrainTextures.cs:108-133`, `ReadBlpPixels`
  `AdtTerrainReader.cs:834-848` memoizes nothing); five private per-renderer
  `Dictionary<string, Texture?>` caches share nothing (`WmoRenderer.cs:354`,
  `DoodadRenderer.cs:265`, `FoliageRenderer.cs:113`, `CreatureRenderer.cs:827`,
  `AttachedItemRenderer.cs:108`). ~9× decode of shared tileset BLPs stands.
- Mip 0 + `GenerateMipmap`, no `CompressedTexImage2D` anywhere — **still present**
  (`BlpDecoder.cs:49-53,65`; `Texture.cs:69-74,95-110`). 8× upload bandwidth vs DXT
  passthrough, authored mips discarded.
- One shared 2–8-slot `AssetWorkerPool` (`AssetWorkerPool.cs:16`) serves terrain + WMO +
  doodads (cap 12, `DoodadRenderer.cs:325`) + foliage (cap 6, `FoliageRenderer.cs:127`) with
  no priority classes — residency-critical terrain parses queue behind cosmetic doodads, and
  the render thread can block on exactly that tile (`AdtCache.cs:129` via
  `TerrainRenderer.cs:271`).

## 5. Resources

- `Program.Loading.cs` (phase machine, 78-456), `Program.cs` Update body (1064-1405), Render
  body (1617-1796), `Program.Net.cs` pump (215-360) and `BeginWorldLoad` call (197).
- `WmoRenderer.cs` 383, 427, 1278-1345, 1702-1817, 1910-1952; `DoodadRenderer.cs` 317-325,
  714-808, 1077-1092, 1202-1257; `TerrainRenderer.cs` 169-181, 240-314;
  `CreatureRenderer.cs` 89-115, 258-292, 543-555, 626-770, 827-850; `M2Animator.cs` 297-311,
  488-494 (`FindOrBake` — already exists, use it), 541-630; `AttachedItemRenderer.cs` 168-173,
  262-314; `AdtTerrainReader.cs` 139-235; `MpqMount.cs`, `MpqArchive.cs`, `Texture.cs`,
  `GpuUploadWorker.cs` 103-114, `HitchRecorder.cs` 113-229, 334-666; `Program.Hitch.cs`
  106-127, 168-244, 255-586.
- Prior art in-repo: PLAN_07 (instrument discipline, self-test convention), PLAN_08 D1-D3
  (budgeted resumable adoption — **D2 was never built**; `BENILLA_VS_MSUI_PERF §2.2`
  confirmed and this reading re-confirmed), `BENILLA_VS_MSUI_LOADING §5` fix path (items 2,
  3, 5 remain undone), handbook §7.1 item 8.
- Benilla mechanisms to mirror: minimal clear condition (`loading_screen.rs:85-86,278`),
  4 ms/frame spawn budget (`terrain_stream.rs:62`), appear-fade (`model_fade.rs:124,149`),
  off-thread colliders (`terrain_stream.rs:458-471`).

## 6. Tools / instrument

Existing: hitch recorder (default-armed, 25 ms threshold, `dumps/hitch-*.json` + auto-vantage),
F9 scene dump, F8 vantage reload, GPU frame profiler, `[stream]` console lines, console tee.

**Missing — and per the template, building these is task one:**

- **I1 — Creature visibility in the recorder.** Bracket `DrawCreatures()` with a new
  `_creatureRenderMilliseconds`; add `CreatureRenderMs` and `CreatureLoadMs` (time spent
  inside `LoadModel`/`TryGetModel` this frame) plus counts `CreatureLoadsThisFrame`,
  `CreatureCacheEntries` to `FramePhases`/`FrameSample`; add `creature-render` and
  `creature-model-load` to `DominantPhase()`. Also bracket `DrawCreatures`' friends
  (`DrawSelectionRing`, spell meshes, `Program.cs:1699-1703`) so nothing in that span stays
  unaccounted.
- **I2 — Load timeline record.** On every `BeginWorldLoad` → curtain-clear cycle, write
  `dumps/load-<map>-<n>.json`: per-phase wall-clock, exit reason (**condition-met vs
  watchdog** — a boolean per phase), queue depths at phase entry/exit
  (`PendingPreloads` for WMO/doodads/terrain/foliage — after S2 fixes what that counts),
  the first 10 dequeues per queue with their distance-to-player at dequeue time, packets
  pumped during load (0 today; nonzero after S1), units known at clear, time from clear to
  first creature draw, max frame ms during curtain, and the same GC/thread columns the hitch
  record carries. Console tee already captures `[stream]`/`[wmo-vis]` lines into the event
  ring; the timeline record snapshots them. This is the artifact §1 owes and the yardstick
  for every step below.

Self-test (PLAN_07 §7.1 convention — an instrument that has never fired is not evidence):
I1 — with DevTools on, stand in a populated vantage and clear one creature's cache entry via
a new dev button (`Reload creature models`); the next frame must trip or near-trip with
`dominantPhase: creature-model-load` and `CreatureLoadsThisFrame ≥ 1`. I2 — run one cold
start; the file must exist, every phase must carry an exit reason, and `packetsPumpedDuringLoad`
must equal 0 **before** S1 lands (the instrument proving the defect is the instrument
proving the fix).

## 7. Implementation + test protocol

Steps are ordered; each has its own falsifiable check against I2/hitch output. **Run the
baseline first**: with I1+I2 built and nothing else changed, do three cold starts (new
character, DevTools on, stock `TileRadius`), keep the three `load-*.json` — that is the
"before" against which every later diff is read. A change that does not move its own named
field is a change that did nothing — back it out (PLAN_08 §7 rule, kept).

### S1 — Pump the network during load. *(H1 — the NPC fix)*

Move `PumpNet(dt)` above the `_worldLoading` guard:

```
if (_worldLoading) { PumpNet(dt); StepWorldLoad(dt); return; }
PumpNet(dt);
```

and budget the drain loop (`Program.Net.cs:215`) to **N packets or 2 ms per frame,
whichever first** (constant, referenced, not a dead field), so neither the curtain frames
nor the reveal frame eat an unbounded backlog. Keep `_movementSender.Update` running during
load too (it sits at `Program.cs:1318` behind the same guard) so the server never sees a
silent client. Guard interactions to check while here: `BeginWorldLoad` is called from
inside `PumpNet` (`Program.Net.cs:197`) — re-entrancy is now real; make `BeginWorldLoad`
idempotent per map and make the pump tolerate `_worldLoading` flipping mid-drain.

*Test:* I2 shows `packetsPumpedDuringLoad > 0` and `unitsKnownAtClear > 0`; time from
curtain-clear to first creature draw drops from "seconds, after a spike train" to ≤ 2 s.
Hitch recorder over the reveal: no trip with `dominantPhase: unmeasured` (I1 renames them)
and no single frame > 40 ms attributable to the drain (the 2 ms budget is the moved field).

### S2 — Fix the phase gates and the progress math. *(H2)*

- `WmoRenderer.PendingPreloads` must count `_deferredRingTiles` (`WmoRenderer.cs:427` +
  `:1295`), so `WarmBuildings` cannot exit before the queue has even formed.
- Recompute `_wmoWarmTotal` after deferred tiles drain (`Program.Loading.cs:141-144`), or
  compute it from the post-drain queue depth, so the bar moves.
- The blocking `ResolveModel` spin (`WmoRenderer.cs:1716-1717`) must be unreachable from the
  load path: `PlaceBuildings` places what is warm and leaves the rest queued to stream in
  post-curtain (they are behind the curtain or the appear-fade either way). Delete the
  `waitForUpload: true` spin from this path; `BENILLA_VS_MSUI_LOADING §5.3` already ordered
  this kill.
- Watchdogs stay, but as *alarms*: a watchdog exit must set the phase's `exitReason:
  "watchdog"` in I2 and print `[load] WATCHDOG <phase>` — in a healthy run all phases read
  `condition-met`.

*Test:* I2 from a cold start shows WarmBuildings entry queue depth > 0 (today it can read 0),
every phase `condition-met`, and total curtain time collapses from ~50–60 s to the sum of
real work. The progress bar visibly advances through WarmBuildings (was pinned at 0.22).

### S3 — Make the budget real; stop rendering the world under an opaque curtain. *(H4)*

- Implement `DrainWarm` against a wall clock: loop `warmOne()` until
  `LoadWarmBudgetMs` (12 ms) elapses, not 48 blind iterations
  (`Program.Loading.cs:452-456`). The constant finally gets its first reference.
- While `_worldLoading` and curtain alpha is fully opaque, skip the world pass
  (`Program.cs:1651-1656` and siblings — terrain/WMO/doodads/foliage/particles/glow) and
  give the reclaimed frame time to `DrainWarm`; resume the world pass when the fade begins.
- Fix the low-cost allocation on the Terrain gate while in the file:
  `_terrain.PreloadReady(new[] { t })` per tile per frame (`Program.Loading.cs:277-278`) —
  add a single-tile overload.

*Test:* during the curtain, hitch recorder shows max frame ≤ 40 ms (was unbounded); I2's
`maxFrameMsDuringCurtain` is the moved field. Verify the curtain still paints and the fade
still reveals a fully-drawn world (no one-frame black or half-scene flash — the first
world-pass frame after skip must precede alpha < 1).

### S4 — Nearest-first, everywhere, verifiably. *(H3 — the "localized" promise, made true)*

- Terrain: sort the ring by Chebyshev (or Manhattan, matching `SetResidency`) distance from
  the player tile before `QueuePreload` (`TerrainRenderer.cs:300-314`); the player's own tile
  is index 0.
- WMO: add the `streamCentre` overload to `QueuePreloadForTiles`
  (`WmoRenderer.cs:1278`) mirroring the doodad API, and enqueue MODF entries sorted by
  `DistanceSq(placementOrigin, centre)`.
- Cold-start interior doodads: add the missing
  `.OrderBy(d => Vector2.DistanceSquared(...))` at `Program.Loading.cs:328-331`, copied from
  the runtime path at `Program.cs:1468-1472` (delete one of the two derivations while there —
  `TakeNewDoodadModelPaths` (`WmoRenderer.cs:1383`) is the dead API that was meant to feed
  this; either revive it or remove it).
- Optional but recommended (Benilla's actual trick): let the Terrain phase clear on
  *player tile + 8 neighbours ready* instead of the whole ring (`Program.Loading.cs:281`),
  and let the outer ring finish behind WarmBuildings. With sorting in place this is a
  condition change only.

*Test:* I2's first-10-dequeues-with-distances per queue must be monotone non-decreasing for
terrain, WMO and interior doodads (today: terrain starts at the far corner, WMO is MODF
order). A/B total curtain time — sorting alone shouldn't change totals much (same work), but
the *minimal-clear* option is the field that moves time-to-clear; report both.

### S5 — Parallelize WMO warming. *(H2/H7; BENILLA_VS_MSUI_LOADING §5.2, still owed)*

One root in flight (`_preloadJob` singular, `WmoRenderer.cs:1337`; `PendingPreloads` `:427`)
with sequential group+texture reads (`:1747-1814`) leaves 7 of 8 worker slots idle during
WarmBuildings. Mirror the doodad shape: `MaxConcurrentPreloads = 4` root jobs, each preparing
on the pool, finalized via the budgeted `DrainWarm` (S3). Add a small priority reservation to
`AssetWorkerPool`: 2 slots reserved for the residency-critical class (terrain ADT parses)
so a doodad burst can't starve the tile the render thread will block on (H7,
`AdtCache.cs:129`).

*Test:* I2's WarmBuildings wall-clock vs baseline is the moved field (expect several-fold in
WMO-heavy starts); confirm no regression on tile-crossing hitches (`[stream]` line, PLAN_08's
protocol — same crossing, diff field by field).

### S6 — Creature pipeline: async, shared, budgeted, distance-ordered. *(H5 — the lag-spike fix)*

Interlocking sub-steps; land in this order, each independently shippable:

1. **Split the cache.** Mesh + skeleton + animator + index/vertex buffers keyed on
   `ModelPath` alone; a light per-appearance record (VisibleGeosets + type-1 skin texture)
   keyed on the appearance suffix (`CreatureRenderer.cs:632-635` today). Humanoid NPCs
   collapse to one `HumanMale.m2` parse per zone.
2. **Lazy-bake.** Replace `M2Animator.Build(m2, CreatureAnims)` (28 eager clips,
   `CreatureRenderer.cs:665`) with on-demand `FindOrBake` (`M2Animator.cs:488-494` —
   already exists and is already the pattern the July-30 handoff used for spell clips).
   Most creatures only ever play Stand/Walk/Run.
3. **Off-thread prepare, uploaded via the worker.** Split `LoadModel` into a pool-side
   `Prepare` (MPQ read, M2 parse, geosets, interleave, BLP decode) and a
   `GpuUploadWorker.Enqueue` upload, mirroring `DoodadRenderer.cs:779/:1238`. Give
   `CreatureRenderer` the pool + uploader at construction (`Program.Net.cs:88`).
4. **Budget + order the queue.** Replace `LoadsPerFrame = 4` (count) with a 2 ms/frame
   finalize budget; sort pending loads by distance-to-camera; skip loads for units beyond
   `AnimateDistance`-class radius or outside the frustum until they qualify. `TryGetModel`
   (`CreatureRenderer.cs:543-555`) must **never** load — it returns false on cache miss and
   enqueues; portrait/nameplate callers already tolerate absence for a frame.
5. **Share attachments.** Hoist `AttachedItemRenderer`'s shader and model cache to one
   shared instance owned by `CreatureRenderer` (`CreatureRenderer.cs:386-392`); stop
   disposing per-GUID state after one absent frame (`:627-628`) — use a timed retire.
6. **Kill the miss storm.** Candidate order `.m2` first everywhere
   (`DoodadRenderer.cs:1077-1092` shape, plus `CharacterRenderer.cs:605-607`,
   `AttachedItemRenderer.cs:271-292`); make `MpqMount` authoritative — a miss returns null,
   no War3Net full-archive fallback on hot paths (`AdtTerrainReader.cs:170-196` becomes
   tooling-only), and memoize negative results.

*Test:* stand at a populated vantage (capture `vantage: coldstart-npcs` on first spawn —
F8-reloadable), then use the I1 self-test button to flush and reload all creature models.
Baseline: a spike train of 300–800 ms frames, `dominantPhase: creature-model-load` (post-I1)
or `unmeasured` (pre-I1). After: no frame > 40 ms, `CreatureLoadsThisFrame` spread over many
frames, `CreatureCacheEntries` far below unit count (sharing works — the moved field for
sub-step 1). Diff two F9 dumps at the same vantage to confirm visible geoset/texture parity
(no visual regression from the cache split). Sub-step 6's field: zero `MpqArchive.Open`
console traces (add a counter to I2) during a full cold start.

### S7 — Texture layer (deliberate follow-up, not this pass). *(H7)*

Process-wide `path → decoded pixels` cache in front of all renderers; upload authored BLP
mip chains instead of `GenerateMipmap`; evaluate `CompressedTexImage2D` DXT passthrough
(neither client does it — free bandwidth win when we get there). Kept out of scope so S1–S6
land small; re-plan as PLAN_18 with its own baseline once I2 shows texture decode as the
remaining dominant load cost.

## 8. Definition of done

All from three consecutive fresh-character cold starts, DevTools on, stock settings:

1. `dumps/load-*.json` shows every phase `exitReason: condition-met`; no watchdog fires.
2. First creature draw ≤ 2 s after curtain clear; `unitsKnownAtClear > 0`.
3. No frame > 40 ms from `BeginWorldLoad` through clear + 60 s; zero hitch records with
   `dominantPhase: unmeasured` in that window.
4. First-10-dequeue distances monotone for terrain / WMO / interior-doodad queues.
5. PLAN_08's standing regression: the Elwynn/Stormwind tile-crossing `[stream]` protocol
   still shows no frame over 40 ms.
6. Live sign-off (July-30 handoff item 8 still applies): Enter World covers immediately;
   no dialog or HUD leak before the curtain; reveal shows a populated, already-faded-in
   scene.

## 9. Fallback

Every step is independently revertible; the floor is **I1 + I2 + S1** — instruments plus the
one-line pump fix. That alone makes NPCs appear with the curtain (or ≤ 2 s after) even if the
curtain still takes 50 s, and converts every remaining spike from `unmeasured` to a named
bucket for the next pass. If S2's gate rework fights back, the narrow fallback is counting
`_deferredRingTiles` in `PendingPreloads` and shipping only that. If S6 proves too large for
one pass, sub-steps 1+2 (cache split + lazy bake) are the 80% and touch no threading.

## 10. Reconciliation

- `BENILLA_VS_MSUI_LOADING.md` "current implementation status" (2026-07-29) overstates: the
  curtain exists but the budget behind it is dead code (H4) and the pump is gated (H1). Add a
  pointer to this plan.
- PLAN_08 D2 (budgeted resumable adoption) remains the structural answer for *crossings*;
  S3 builds its cold-start twin. When D2 lands, unify `DrainWarm` and the crossing cursor on
  one budget constant.
- PLAN_07: I1 extends `FramePhases`/`DominantPhase` — update the recorder's field list in
  that doc; the `FrameSample.FrameMs` doc comment at `HitchRecorder.cs:338` is stale
  ("render-entry to render-entry") and should be corrected in the same commit.
- `Program.Loading.cs:98`'s comment becomes true again after S1 — reword it to name the new
  invariant (pump runs in both curtain states) so the next reader can't re-break it.
- Dead code to delete or revive, decided here: `TerrainRenderer.LoadAround` (`:184`),
  `WmoRenderer.DrainPreloads` (`:1368`), `DoodadRenderer.DrainPreloads` (`:813`),
  `TakeNewDoodadModelPaths` (`:1383`) (revive in S4 or delete), dead constant
  `DoodadCollisionRebuildThreshold` (`Program.cs:299`) — same dead-constant shape as
  `LoadWarmBudgetMs`; a dead budget constant is how H4 happened, so the rule going forward:
  **a budget constant with zero references fails review.**

### Reconciliation completed — 2026-07-30

- `BENILLA_VS_MSUI_LOADING.md` now points back here and marks its dead-budget/gated-pump text as
  historical. `LoadWarmBudgetMs` is referenced by the wall-clock `DrainWarm` loop.
- PLAN_07's field list now includes creature render/load/count/cache fields and their two dominant
  phase labels. `FrameSample.FrameMs` now says Update-entry to Update-entry.
- `Program.Loading.cs` now states the invariant directly: the socket pump runs in both curtain
  states, while only `LOGIN_VERIFY_WORLD` starts the guarded world-load cycle.
- Deleted the zero-reference synchronous `TerrainRenderer.LoadAround`, WMO/Doodad
  `DrainPreloads`, `TakeNewDoodadModelPaths`, and `DoodadCollisionRebuildThreshold`. The live paths
  are the incremental queue/budget paths; retaining blocking alternatives would invite regression.
- PLAN_08's future cursor unification remains open. The follow-up curtain-frame pass brought three
  consecutive matched starts below 40 ms, then landed S5's four-root WMO preparation and two-slot
  ADT reservation. S5's cold-start checks are green; its same-crossing PLAN_08 live comparison is
  still required, so S6 remains gated and S7 remains out of scope. See
  `July-30-2026-CURTAIN-FRAME-HANDOFF.md`.
- S6 steps 1-6 are implemented and independently A/B'd. The creature path now separates model and
  appearance caches, bakes animations lazily, prepares and uploads asynchronously, finalizes under
  a 2 ms budget in camera-distance order, shares attachment resources with 30-second retirement,
  and uses `.m2`-first lookups against an authoritative mounted archive chain with memoized misses.
  The load record now reports `mpqArchiveOpensDuringLoad`; the S6.6 run reports zero. A 60-second
  post-clear lifecycle artifact and the unattended create/enter/observe/delete harness were added
  for repeatable fresh-character validation. Final three-run sign-off is not complete: the first
  enter-world frame remains 43.3 ms after packet parsing was moved off-thread, split between the
  synchronous player-avatar rebuild and the following allocation/GC frame. See
  `July-30-2026-S6-CREATURE-HANDOFF.md`.
- The new-character 40-60 s visibility gap was subsequently traced to an unacknowledged vanilla race
  cinematic, not the creature cache: vmangos re-anchors visibility to its flying cinematic camera
  until `CMSG_COMPLETE_CINEMATIC`. MSUI now answers every `SMSG_TRIGGER_CINEMATIC` immediately with
  the empty completion packet, matching Benilla's ESC-skip behavior, and sends
  `CMSG_SET_ACTIVE_MOVER` immediately after `CMSG_PLAYER_LOGIN`. Two new-character cycles reduced
  enter-world to first visible NPC to 8.65 s and 7.03 s, with 45 units known at clear in both; the
  existing-character control reached first visible NPC in 3.11 s with no cinematic packet. See
  `July-30-2026-CINEMATIC-ACK-HANDOFF.md`.
