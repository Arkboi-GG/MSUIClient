# System — Streaming, Residency and Frame Performance

**How the world becomes resident as you walk, and where the frame time goes.**
One of the per-system docs the handbook indexes (see PROJECT_HANDBOOK.md §1.2).
Read this plus the handbook's cross-cutting ground truth (§3.1 coordinates, §5.4
thread/ownership rules, §11 working agreements) before touching streaming,
residency or anything performance-related. You should not need the rest of the
handbook.

Version: Draft 1 — 2026-07-25. Written during the session that built the hitch
recorder and killed the tile-crossing freeze. **Every number in this doc is
measured, not estimated**, and the ones that are still open are marked as open.

Owner files: `Program.cs` (`UpdateWorldResidency`, `PopulateDoodads`,
`QueueVisibleDoodadDemand`, `BeginCollisionBuild`, the `Update`/`Render` phase
timers), `Program.Hitch.cs`, `Engine/HitchRecorder.cs`, `Engine/ClientWindow.cs`
(frame boundary timers), `Engine/AssetWorkerPool.cs`, `Engine/GpuUploadWorker.cs`,
`Engine/GpuFrameProfiler.cs`, `World/TerrainRenderer.cs` (`QueuePreload`,
`SetResidency`, `PumpPreloads`), `World/AdtCache.cs`,
`World/Collision/CollisionBatch.cs`.

---

## 0. The bar

The real 1.12 client walks Elwynn and Stormwind on this hardware without a
stutter. That is the target and it is an objective one — this is emulation-core
work by FOUNDATION_PLAN §2, not a matter of taste.

Nico's hardware, which matters for reading everything below: **i7-12800H (20
logical cores), 48 GB DDR5, Iris Xe integrated graphics.** The integrated GPU is
the important detail: it shares memory bandwidth with the CPU, so "off the main
thread" is not the same as "free".

His acceptance criterion, in his words: *"I don't care if it eats 5-8 GB system
RAM — I care that it's buttery smooth."* Memory is cheap here. Main-thread work
and frame pacing are not.

---

## 1. The instrument — read this before adding any timer

`Engine/HitchRecorder.cs` (PLAN_07). A preallocated ring of per-frame samples
plus a ring of tagged console lines. Any frame over `ThresholdMs` (**default
25 ms** — at 60 Hz vsync anything over 16.7 has already dropped a frame) writes
`dumps/hitch-<col>-<row>-<n>.json` **and saves a matching vantage**, so the spot
is reloadable. Nobody has to notice anything.

### 1.1 The frame boundary is Update-entry to Update-entry, and that is load-bearing

Silk runs `Update → Render → swap`. A period measured render-entry to
render-entry contains `Render N` but `Update N+1`, so closing the sample with
`Update N`'s number reports the wrong frame's work. The first version of the
recorder did exactly that and blamed a 171 ms stall on "outside update and
render" while the residency publication that caused it sat in the Update it had
excluded. Measured from Update entry, every phase falls inside the period it is
attributed to. See `HitchRecorder.FrameBoundary`.

### 1.2 A phase timer that does not cover everything lies quietly

This happened **three times** in one session and is the single most repeatable
lesson here:

| Timer | The lie | Truth |
|---|---|---|
| `[stream] ready: 0.06s` | started *after* the readiness gate | real crossing was 0.17 s |
| `update 100.3 (move 0.1 resid 0.0 …)` | `PumpPreloads`, `AcceptReadyCollision` and the doodad collision rebuild had no bracket | 100 ms was inside Update, unnamed |
| `render 3.7` → "GPU is fine" | CPU submission time only | GPU execution never measured at all (§5) |

Every breakdown now carries an **unaccounted residual** (`UpdateUnaccountedMs`,
`UnaccountedMs`). If a breakdown does not sum, it is not a breakdown. Report the
residual rather than reading past it.

### 1.3 The console tee

`HitchRecorder.InstallConsoleTee` wraps `Console.Out` so every line starting
with `[` lands in the event ring with its frame index. No call sites were
changed, and it picks up tags in files nobody has opened. Each record carries
those lines as offsets from the stalled frame — negative offsets are the run-up,
which is usually where the cause is.

---

## 2. What a tile crossing does

```
UpdateWorldResidency()                     Program.cs
  TileAt(player) != _residentCentre ?
  QueuePreload(lead ring, radius+1)        <- async, non-blocking (§3.1)
  PreloadReady(desired ring) ? else return
  --- the crossing block ---
  terrain.SetResidency(...)                adopt ready tiles, drop departed
  wmo.ResetPlacements(); wmo.LoadForTiles(...)
  liquid.LoadForTiles(...)
  wmo.QueuePreloadForTiles(preload ring)   <- non-blocking (§3.1)
  doodads.ResetPlacements(); PopulateDoodads(...)
  adts.Retain(preload ring)
  BeginCollisionBuild()                    <- snapshot only (§3.2)
```

`start.tileRadius = 1` (3×3 terrain), `WmoPreloadRadius = 2` (5×5 assets).
`ObjectResidencyRadius = doodad draw distance + half a tile diagonal + 50 yd`
= **300 + 377 + 50 = 727 yd**.

---

## 3. What was fixed, with the numbers

Baseline, Northshire → `[32,49]`, 2026-07-24:

```
[hitch] hitch-32-49-2: 187 ms frame at [32,49] -> residency
[hitch]   update 119.4 (move 0.5 resid 118.8 preload 0.0)  render 66.4
[stream]  terrain 2.5  wmo 0.4  liquid 1.2  wmoQueue 61.2  doodads 12.1
          retain 0.0  collisionSnapshot 36.0
```

### 3.1 `AdtCache.Get` blocks — never call it from a speculative path

`AdtCache.Get` waits on a pending parse:

```csharp
if (pending is not null) return pending.GetAwaiter().GetResult();
```

`WmoRenderer.QueuePreloadForTiles` called it for all 25 ring tiles on the render
thread, purely to read each ADT's MODF list, with a worker pool busy behind it.

**Measured: 61.2 ms → 0.0 ms.**

The fix is `AdtCache.TryPeek`, which never parses and never waits. Unparsed ring
tiles are deferred into `WmoRenderer._deferredRingTiles` and retried each frame
from `WarmNextPreload`; `[stream]` reports `(ring deferred N)`.

> **`TryPeek` returns true for a cached null.** "Known to be absent" (an ocean
> tile with no ADT) is an answer; "not looked at yet" is not. Get this backwards
> and missing tiles retry forever.

**Still open:** `DoodadRenderer` (351, 434), `FoliageRenderer` (270) and
`LiquidRenderer` (304) all still call `adts.Get` from the main thread. They are
cache hits today, so they measure ~0 — but they are the same latent bug.

### 3.2 Collision expansion belongs on the worker, and the ownership rule does not forbid it

`BeginCollisionBuild` expanded ~509,000 triangles on the render thread — three
`Vector3.Transform` calls each — then built the BVH off-thread.

**Measured: 92.9 ms on the main thread, fired on a timer every few seconds while
doodads streamed**, to add a handful of props. Half a million transforms to
append a rounding error, while nothing on screen changed.

Handbook §5.4 forbids a worker reading live renderer placement collections while
they mutate. **That applies to the list, not to the geometry.**
`Model.CollisionTriangles` is immutable once loaded. So the main thread now
snapshots references + transforms (`World/Collision/CollisionBatch.cs`, a few
thousand tiny structs) and the worker does every transform plus the BVH.

**Measured: 92.9 ms → 0.3 ms.**

### 3.3 The crossing freeze is gone

```
[stream] tile [32,48] ready ... 0.03s   collisionSnapshot 0.4
[stream] tile [32,49] ready ... 0.02s   collisionSnapshot 0.3
[stream] tile [32,48] ready ... 0.01s   collisionSnapshot 0.3
```

Three consecutive crossings, **none tripping the 40 ms threshold**. Crossing
cost 0.26 s → 0.01–0.03 s. Original symptom (187 ms freeze at the same
coordinates every time) no longer reproduces.

### 3.4 The client re-derived the whole world four times a second, forever

`QueueVisibleDoodadDemand` runs on a 250 ms timer and called `PopulateDoodads` —
the **full 7,562-placement re-derivation** — plus a LINQ `OrderBy` over every
MODD placement in radius. Moving or not, changed or not.

**Measured: `demand 32.1 ms`, permanently, on a 250 ms cycle.**

This is why "I cross the same spot, nothing changes visually, and it still
hitches" was true *and* not about the crossing. The work was simply always
running.

The scan now returns immediately when no model is in flight and the player has
not moved `DemandRescanDistance` (24 yd), and backs its interval off to 1 s
while idle.

### 3.5 Collision rebuilds now wait for streaming to settle

Each rebuild is a ~500,000 triangle expansion plus a from-scratch BVH: **35–142
ms expansion, 0.4–1.2 s BVH**, and four of them fired in ~20 seconds of ordinary
streaming, back to back. On an integrated GPU sharing the memory bus with the
render thread, that is not free even though it is off the main thread.

Gate is now `streamingSettled && pending > 0`, with a 15 s defer timer as the
safety valve.

---

## 4. Ground truth — do not re-derive

| Fact | Value |
|---|---|
| Worker pool | `Clamp(ProcessorCount - 2, 2, 8)` = **8** on the i7-12800H |
| `AdtCache.Get` | **blocks** on a pending parse; `TryPeek` does not |
| Residency radius | draw + half tile diagonal + 50 = **727 yd** at 300 yd draw |
| Placements vs drawn | `placed 7562, drawn 396` — **19:1** |
| Collision world | ~509,000 triangles from 41 WMOs + ~3,600 doodads |
| Collision expansion | 35–142 ms; BVH 0.4–1.2 s (both off-thread now) |
| Detail triangles excluded | 772,166 of 1,166,289 (MOPY F_DETAIL) |
| Terrain tile | 37,120 verts, ~65,536 tris; 9 resident |
| vblank at 60 Hz | **16.7 ms** — present values of 27/30/37 are 2–3× that |
| `[stream] ready` timer | starts at the top of the method now, not after the gate |

**The 727 yd residency radius is a consequence of rebuilding only at crossings.**
A doodad must be resident now in case it comes into range before the *next*
crossing. Maintain placements continuously and the margin collapses toward draw
distance — that is the 19:1 ratio's real cause.

---

## 5. NOT SOLVED — the open problem, stated honestly

**Nico still feels micro-stutter, and no change in this session removed it.**
Four real, measured defects were fixed and the felt problem survived all four.
That pattern means the model of the problem was wrong, not that the next fix
needs to be bigger.

Every surviving hitch looks like this:

```
[hitch] hitch-32-49-8: 43 ms at [32,49] -> present-swap-driver
[hitch]   update 0.1 (everything 0.0)  render 3.7  present 37.9  gui 0.7
[hitch] hitch-32-49-9: 35 ms   render 3.8  present 30.7
[hitch] hitch-32-49-10: 33 ms  render 5.2  present 27.0
```

Our code does **nothing** in these frames. Same spot, no `[stream] crossing`
nearby.

### 5.1 The blind spot that lasted the whole session

**Only the CPU was ever measured.** `GpuFrameProfiler` (`GL_TIME_ELAPSED` rings,
per pass) existed the entire time and was never read. "render 3.7 ms, so the GPU
is not the bottleneck" was an assumption, not a measurement — CPU submission
time says nothing about how long the GPU then took.

Worse, the bucket was *named* `present-swap-driver`, which smuggled a conclusion
into a field that only measures *end of render → start of next update*.

**GPU timings are now wired into the record** (`gpu.totalMs` and per pass, plus a
`[hitch] GPU (delayed)` console line). They are delayed by a frame or two —
inherent to non-blocking queries — which is fine for telling a 5 ms GPU frame
from a 25 ms one.

### 5.2 The two live hypotheses, and how to kill one

- **H1 — the GPU frame exceeds the vblank interval.** 27/30/37 ms are ~2× and
  ~3× of 16.7. That is the signature of missing vblank and waiting for the next
  one. Iris Xe pushing 6,300 doodad instances and 1.17 M WMO triangles makes it
  plausible. **Confirmed if `gpu.totalMs` approaches or exceeds 16 ms.**
- **H2 — shared-context uploads serialize the render context.** Handbook §3.24
  already warns Intel's driver can do this. `[gpu-upload] duskwood_blacksmith.wmo
  completed in 38ms` sits directly beside hitch #8; several others line up too.
  **Suspected if `gpu.totalMs` stays ~3–5 ms while `present` spikes**, with
  `[gpu-upload]` lines nearby in the record's event list.

### 5.3 Two one-click A/B tests, to run before writing any more code

1. **Untick VSync.** If the stutter vanishes, it is frame pacing / vblank misses
   and `present` was never a stall. If unchanged, vblank is exonerated.
2. **Untick GPU instancing.** A/B the doodad path directly — 6,300 instances is
   the largest thing submitted.

---

## 6. Not done — the honest ceiling

- **Per-tile placement and collision ownership (PLAN_08 D3).** Placements are
  still rebuilt wholesale; collision is still re-expanded wholesale. Both are
  off the critical path now but both still re-derive data they already have.
  WoWee's `TerrainTile` holds `wmoInstanceIds` / `m2InstanceIds` /
  `doodadUniqueIds` and `unloadTile` removes exactly those. There is no
  `ResetPlacements` anywhere in WoWee.
- **Budgeted resumable adoption (PLAN_08 D2).** WoWee's `FinalizingTile` is a
  cursor advanced under an 8 ms wall-clock budget (16 ms while taxiing), so a
  crossing structurally cannot exceed it. MSUI has no equivalent.
- **Startup foliage scatter: 2,687 ms**, inside `Render`, loading 10 grass M2
  models synchronously. Measured, untouched, and larger than any crossing ever
  was.
- **Hysteresis was considered and rejected** for MSUI — see PLAN_08 §5 D4. Our
  residency radius is 1, so an unload radius of 2 means 25 terrain tiles instead
  of 9 (~2.8× terrain VRAM) on the machine where VRAM is the constraint. Do not
  port WoWee's 6/9 split without re-checking it.
- **Residency throttling was considered and rejected** — D5. `UpdateWorldResidency`
  early-returns after one `TileAt` and a tuple compare; WoWee throttles because
  their `streamTiles()` sweeps 169 tiles per call.

---

## 7. Lineage

- WoWee `include/rendering/terrain_manager.hpp` + `src/rendering/terrain_manager.cpp`
  at `C:\Users\nico\Desktop\WoWee-master` — the five mechanisms quoted in
  PLAN_08 §4. Four transfer; two did not survive contact with our measurements,
  which is handbook §10's warning in action.
- PLAN_07 (the recorder) and PLAN_08 (incremental residency) carry the full
  reasoning; this doc is the settled result plus the open problem.
