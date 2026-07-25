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

> **Superseded in part by §5A (2026-07-25).** The `present` numbers above are a
> mis-attribution — on this hardware the driver does not block in the swap. Read
> §5A before acting on anything in §5.

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
   **RUN 2026-07-25 — see §5A. Stutter vanished: "night and day, not close."**
2. **Untick GPU instancing.** A/B the doodad path directly — 6,300 instances is
   the largest thing submitted. **Not run — H1 is refuted by `gpu.totalMs` 0.6–0.9,
   so the doodad submission cost is not a live suspect. Skip unless §5A.4 turns up
   nothing.**

---

## 5A. The vsync A/B — read this before believing any number in §3 or §5

Untick VSync, walk the same `[32,48] → [32,49]` crossing. Felt result, Nico's
words: **"night and day. not close."** Measured diff, same crossing, same tiles:

| | vsync ON | vsync OFF |
|---|---|---|
| `[stream] terrain` | 13.2 ms | **1.9 ms** |
| `[stream] doodads` | 8.4 | 6.5 |
| `duskwood_inn` preload | 0.23 s | **0.04 s** |
| `[stream-budget] finalize` lines | ~35, all 14–17 ms | **zero** |
| crossing hitch | 65 ms (`model-finalize`) | **none** |
| survivor | 34 ms (`hud-imgui`) | 28 ms (`hud-imgui`) |

### 5A.1 Every `[stream-budget] ... took 16ms` line is an artefact

`WmoRenderer.WarmNextPreload` steps **one** model per frame, so the eleven
`duskwood_inn` lines are eleven separate frames, not one 175 ms stall. Each
16 ms is the vblank wait landing inside whatever GL call that step happened to
make. Vsync off, same models, same order: **the lines do not appear at all.**

This is §1.2's lesson in a new shape. Not a bracket that fails to cover its
phase — a bracket charged for a **wait it does not own**. A timer around a GL
call on a vsync-throttled driver measures the throttle, not the work.

**Do not read any `[stream-budget]` number, or the 65 ms `model-finalize` hitch,
as a cost.** Re-measure with vsync off before optimizing anything they point at.
This also puts §3's `terrain 13.2` under suspicion: uncapped it is 1.9.

### 5A.2 H1 is refuted by measurement

`gpu.totalMs` is **0.6–0.9 ms** on every record, hitch frames included. The
Iris Xe is nowhere near the 16.7 ms vblank. The GPU frame is not the problem.

### 5A.3 The wait is not in `present`, and the bucket name claimed it was

`present` reads 0.05–0.9 ms on 26 ms frames. The driver does **not** block in
the swap on this hardware — it blocks in the next GL call that needs a buffer,
which is whichever of update, render or the ImGui draw comes first. In the ring
you can watch the ~16 ms move between `update`, `render` and `gui` frame to
frame while the frame period stays pinned at 16.7:

```
i     frame   update  render  gui     present
-11   16.66   0.03    0.32    16.19   0.08
-10   16.86   0.03    16.49   0.24    0.07   <- moved to render
-9    16.98   16.26   0.36    0.24    0.05   <- moved to update
```

`present-swap-driver` is renamed `swap-and-events`. A bucket name is not a place
to store a conclusion.

### 5A.4 What survives vsync off — the real remaining bug

```
vsync OFF:  hitch-32-49-2: 28 ms -> hud-imgui
            update 0.0   render 0.4   present 0.1   gui 27.1
            GPU (delayed): total 0.7
```

No vblank to wait for, GPU idle, our own work under 1 ms. And `gui` is not the
HUD: on every neighbouring frame where the wait landed elsewhere, `gui` reads
**0.24 ms**. That is the HUD's real cost. The 27 ms is the driver's implicit
flush, which lands in `_imgui.Render()` because it is the frame's last GL
submission.

It fires on the **crossing frame**, beside `[gpu-upload] ... completed in 9ms
off-thread`. That points at **H2** — shared-context uploads serializing the
render context — which stayed invisible because it lands in `gui` while everyone
watched `present`.

### 5A.5 The instrument change this bought

`gui` is now two numbers that cannot be confused:

- `hud` — `OnGui`, our HUD code. Pure CPU, ~0.25 ms, flat.
- `imguiFlush` — `_imgui.Render()`, the frame's last GL call, where a driver
  stall lands. Large here with `gpu.totalMs` small means the GPU was idle and
  the CPU was blocked in the driver.

Plus `uploadsInFlight` / `uploadsCompleted` from `GpuUploadWorker`, per frame,
in the record and in the ring. **That pair is the H2 verdict**: a large
`imguiFlush` beside a non-zero upload count confirms it; a large `imguiFlush` on
a frame with no uploads refutes it and sends the search to the swap chain.

`hud + imguiFlush == gui` by construction in `ClientWindow.HandleRender`, so the
split cannot drift the way the single bucket did.

### 5A.6 The split fired, and it refuted H2 as well (2026-07-25, second run)

First record from the split instrument, vsync **on**, same crossing:

```
[hitch] hitch-32-49-2: 33 ms frame at [32,49] -> swap-and-events
[hitch]   update 0.0 (all zero)  render 0.4  present 16.5
          hud 16.4  imguiFlush 0.1  input 0.0  unaccounted 0.0
[hitch]   GPU (delayed): total 0.9
[hitch]   uploads: 0 in flight, 0 completed during the frame
```

Two clean vblanks — 16.5 in `present`, 16.4 in `hud` — so the frame dropped one.
And every GL-flavoured explanation dies on this record at once:

- `imguiFlush 0.1` — the frame's last GL call returned instantly. **The block is
  not in a GL call.**
- `uploads 0 in flight, 0 completed` — **H2 refuted.** No shared-context upload
  was anywhere near this frame.
- `gpu 0.9` — H1 was already dead; it stays dead.
- The 16.4 ms is in `hud`, which is `Gui()` in Program.cs: `ImGui.Text`,
  `ImGui.Checkbox`, `ImGui.Button`. **No GL, no file I/O, no syscall.** Its
  baseline on every other frame is 0.25 ms.

Managed code doing nothing but building strings and widgets does not block for a
vblank. Something stopped the thread from outside.

### 5A.7 The hypothesis that fits everything: GC pauses

A collection stops **every** thread, wherever each one happens to be. That single
property explains the entire shape of this investigation:

| Observation | Under the GC hypothesis |
|---|---|
| The stall wanders between `update`, `render`, `gui`, `hud`, `present` | a pause lands wherever the thread was; it has no home phase |
| Uncorrelated with uploads, GPU, draw counts | correct — it is not graphics at all |
| **Four correct off-thread fixes did not help** | moving an allocation to a worker does not stop it triggering a collection, and the pause still stops the render thread |
| "Always the same spots" | a crossing allocates the same enormous arrays every time |
| "No matter how much time I SIT before it" | the allocation happens at the crossing, not before it |
| Far worse under vsync | a 16 ms pause against a 16.7 ms budget guarantees a dropped frame; uncapped at ~300 fps it is one blip among many cheap frames |

The suspects are already measured and already in this doc — we just never read
them as allocations:

- **505,037 triangles expanded** per collision rebuild → multi-megabyte
  `Vector3[]`, straight to the **Large Object Heap**. LOH allocation forces gen2.
- **194,861-node BVH**, built from scratch each time → same.
- **1,166,289 WMO triangles**, **37,120 verts × 9 terrain tiles**, BLP decodes on
  8 worker threads.

`MSUIClient.csproj` sets no GC properties, so this runs on workstation background
GC. That is the right latency choice already — which means the fix is to allocate
less and reuse buffers, not to flip a switch.

**Status: hypothesis, not verdict.** `gcPauseMs`, `gen0/1/2` and
`allocatedBytes` are now measured per frame and written to every record and every
ring row, with `dominantPhase` returning `gc-pause` / `gc-pause-gen2` when a
pause holds a third of the frame. GC is checked *before* the phase ranking and
never competes in it — a pause is already inside whichever phase was running, so
ranking it as a peer would double-count it.

One line decides it:

```
[hitch]   GC: pause 16.4 ms of 33 ms frame  gen0 2 gen1 1 gen2 1  allocated 12.40 MB this frame
```

If the pause holds the frame, the phase split above it is naming the unlucky
bucket, not the cause — and no renderer or streaming change can fix it.

**If GC comes back clean**, the next measurement is thread CPU time
(`QueryThreadCycleTime`) across the frame, which separates "the OS descheduled
us" from "the driver is spinning in a busy-wait". Do that before anything else.

### 5A.8 GC refuted, and the real number appears (2026-07-25, third run)

```
[hitch] hitch-32-49-2: 91 ms frame at [32,49] -> doodad-render
[hitch]   update 28.7 (resid 26.7 finalize 2.0)  render 60.6
          present 1.3  hud 0.6  imguiFlush 0.1  unaccounted 0.0
[hitch]   render split: world 60.5 = terrain 0.0 + wmo 0.1 + doodad 60.3 + foliage 0.0
[hitch]   GPU (delayed): total 0.9 = terrain 0.7 + wmo 0.1 + doodad 0.1
[hitch]   uploads: 0 in flight, 0 completed during the frame
[hitch]   GC: pause 1.7 ms of 91 ms frame  gen0 1 gen1 1 gen2 0  allocated 40.45 MB
```

**The GC hypothesis (§5A.7) is refuted: 1.7 ms of a 91 ms frame, no gen2.** It
cost one run to kill and that was worth it — it was the best available theory and
it was wrong. Leave §5A.7 standing as a record of the reasoning; do not re-derive
it.

**`allocated 40.45 MB in one frame` is real and stays on the list**, just not as
the cause of this hitch. It is process-wide, so most of it is the off-thread
507,873-triangle expansion and the 195,943-node BVH. It is the pressure behind
whatever gen2 eventually lands; it is not what stalled this frame.

**What actually stalled: `doodad 60.3 ms` of CPU while the GPU drew that same
pass in 0.1 ms.** Nothing else is close. `hud 0.6` and `imguiFlush 0.1` are back
to baseline, `present 1.3`, `unaccounted 0.0`. For the first time the frame is
fully accounted for and one bucket holds two thirds of it.

`RenderInstanced` does three unrelated jobs in one loop over `_byModel`, and one
timer could not tell them apart:

| Job | What it is | Fails because |
|---|---|---|
| cull | distance + frustum over every placement (6,695 at the crossing) | our arithmetic, scales with placement count |
| instance upload | one `glBufferData` per visible model, `StreamDraw` | driver call |
| draw submit | texture binds, uniform sets, `DrawElementsInstanced` | driver call |

They are now three separate timers plus two counts, summing to `DoodadRenderMs`
with an explicit `unmeasured` residual, and `dominantPhase` reports
`doodad-cull-cpu` / `doodad-instance-upload` / `doodad-draw-submit` instead of
the useless `doodad-render`.

**`firstTouchModels` is the field to watch.** The uploads counter added in §5A.5
answers "was an upload in flight *during* this frame" — and it reads 0 here. But
the models being drawn at a crossing were uploaded on the shared context frames
*earlier*, and on this Intel driver the first bind of such an object by the
render context can force a synchronization. **First-touch is a different failure
from concurrent-upload**, and the earlier counter is structurally blind to it.
If the cost tracks `firstTouchModels` rather than placement count, the fix is a
warm-up pass, not a cheaper cull.

The three outcomes and what each means:

- **cull holds it** → our own code, 6,695 placements re-culled from scratch every
  frame. Fix is PLAN_08 D3 (per-tile ownership) plus a spatial index.
- **instanceUpload holds it** → `glBufferData` orphaning per model per frame.
  Fix is persistent-mapped or double-buffered instance storage.
- **drawSubmit holds it, with firstTouch high** → shared-context first-bind
  stall. Fix is warming new models before they are drawn.

### 5A.9 The split lands: it is our own cull (2026-07-25, fourth run)

```
[hitch] hitch-32-49-2: 86 ms frame at [32,49] -> doodad-cull-cpu
[hitch]   update 29.6 (resid 29.5)  render 56.3  present 0.1  hud 0.3  imguiFlush 0.1
[hitch]   render split: world 56.1 = terrain 0.0 + wmo 0.1 + doodad 56.0 + foliage 0.0
[hitch]   doodad 56.0 = cull 55.8 + instanceUpload 0.0 + drawSubmit 0.1 + unmeasured 0.0
                        (41 model(s) uploaded, 0 first-touch)
[hitch]   GPU total 0.8   uploads 0 in flight, 0 completed
[hitch]   GC: pause 2.8 ms of 86 ms  gen0 2 gen1 0 gen2 0  allocated 37.42 MB
```

Every driver hypothesis is now dead, by measurement rather than by argument:

| Hypothesis | Killed by |
|---|---|
| H1 GPU exceeds vblank | `gpu.total 0.8` |
| H2 concurrent shared-context upload | `uploads 0 in flight, 0 completed` |
| First-touch of shared-context objects | `0 first-touch` |
| Driver flush at the last GL call | `imguiFlush 0.1` |
| GC pause | `2.8 ms of 86` |
| `glBufferData` orphaning | `instanceUpload 0.0` |
| Draw submission | `drawSubmit 0.1` |

**`cull 55.8` — our own arithmetic, in `DoodadRenderer.RenderInstanced`.** No GL
call is involved in that bracket at all.

### 5A.10 Why 55.8 ms is not a plausible amount of arithmetic

The loop is a distance-squared test and, for survivors, `Camera.BoxInFrustum`.
`BoxInFrustum` was read and is clean: no allocation, no per-call plane
extraction, early-out at the first corner that is inside. `Render` is called once
per frame (`Program.cs:1454`). So this is ~6,163 iterations of cheap vector maths
costing **~9 µs each**, which is two orders of magnitude off what that code
should cost.

**Leading suspect, stated as a hypothesis:** `Instance` is a **`sealed class`**
(`DoodadRenderer.cs:183`), so `List<Instance>` is a list of *pointers*. The cull
dereferences 6,163 scattered heap objects, and on a crossing frame every one of
them was allocated moments earlier by `PopulateDoodads` in the same frame's
`resid 29.5` — part of that 37.42 MB. If that is right, the cost is memory, not
maths, and no cheaper test will touch it.

**It is a hypothesis and it gets measured before anything is rewritten.** The GC
theory in §5A.7 was just as tidy and just as wrong.

### 5A.11 The measurement that decides it

`cullModels`, `cullInstances` and `cullNsPerInstance` are now recorded, and
`doodadCullMs` / `doodadCullInstances` / `doodadCullNsPerInstance` are in **every
ring row**, not just the tripped frame. That placement is the whole design:

> The frames on either side of a crossing cull the **same instances**. Identical
> `doodadCullInstances` with a 100× difference in `doodadCullMs` cannot be
> explained by workload. Only by the state of the memory those instances are in.

Reading the rate:

- **~50–100 ns/instance** — normal arithmetic. The cull is fine, look elsewhere.
- **~1000+ ns/instance** — memory, not maths. Confirms the pointer-chase.
  The fix is contiguous bounds (a flat `struct` array of `WorldMin`/`WorldMax`
  written by `PopulateDoodads` as it builds), **not** a cheaper cull test and not
  a smaller radius.
- **High ns/model with low ns/instance** — the opposite: per-model overhead over
  a `_byModel` with far more entries than expected.

If the flat-bounds change goes in, PLAN_08 §7 step 3 applies without amendment:
`cull` is the named field, and a change that does not move it did nothing.

### 5A.12 The rate confirms it: memory, not maths (2026-07-25, fifth run)

Two crossings, same code, same route:

| | run 4 | run 5 |
|---|---|---|
| `cull` | 55.8 ms | 4.8 ms |
| instances | 6,163 | 6,364 |
| **models** | **512** | **153** |
| **ns/instance** | **9,053** | **751** |
| frame | 86 ms | 32 ms |

Nico, on run 5: *"that FELT and seemed a smaller hitch."*

**Both are far above the 50–100 ns the arithmetic costs** — 7.5× and 90×. And
the rate tracks **model count**, not instance count: 3.3× the models gave 12× the
per-instance cost, on a *smaller* placement set. Instance count barely moved.

That is locality, and nothing else fits it. `Instance` is a `sealed class`
(`DoodadRenderer.cs:183`), so `List<Instance>` is a list of pointers. More models
means more separate lists means a more scattered walk over objects
`PopulateDoodads` allocated moments earlier in the same frame. The cull was never
doing too much work; it was waiting on memory.

### 5A.13 The fix, and how to back it out

`_cullBounds`: a `Dictionary<Model, List<CullBounds>>` parallel to `_byModel`,
where `CullBounds` is a 24-byte readonly struct of `Min`/`Max`. The cull walks
that span; the `Instance` object is dereferenced **only for survivors**.

Measured selectivity: `placed 6694, drawn 195, dist-culled 5616,
frustum-culled 883`. **97% of placements are rejected**, so 97% of the pointer
chases disappear. 6,364 × 24 B ≈ 153 KB, contiguous, L2-resident.

Maintained at exactly four sites — the two `list.Add` paths, `ResetPlacements`
and `Dispose`. If a future placement path forgets, `RebuildCullBounds` repairs it
and prints `[doodad-cull] cull bounds drifted…` **once**. Self-healing because a
short bounds list would silently draw the wrong props, and a wrong picture is
worse than a slow one — but reported, because a silent repair is how a bug
survives a session.

**A/B without a rebuild:** HUD → `Flat cull bounds (SoA)`. Unticking runs the
original array-of-pointers loop, kept verbatim. Cross `[32,48] → [32,49]` both
ways and diff `cull` and `ns/instance` in the `[hitch] doodad` line.

Per PLAN_08 §7 step 3: **`cull` is the named field. If it does not move, this
change did nothing and comes out.** Expect ~50–100 ns/instance if the diagnosis
is right.

### 5A.14 Still open after this

Even at 32 ms the frame was over budget, and `cull` was only 4.8 of it.
**`resid 25.5` is now the largest remaining term** — `[stream] terrain 14.1 +
doodads 6.8` inside it. Some of that is the vsync artefact of §5A.1 and has to be
re-measured uncapped before anyone optimizes it. That is the next thread, and
PLAN_08 D2 (budgeted resumable adoption) is still unbuilt and still the
structural answer to it.

### 5A.15 The cull is fixed — and the pacing bug is now alone (2026-07-25, sixth run)

Cull, at the same crossing, across runs:

| | run 4 | run 5 | run 6 |
|---|---|---|---|
| `cull` | 55.8 ms | 4.8 ms | **0.3 ms** |
| instances | 6,163 | 6,364 | 6,623 |
| models | 512 | 153 | 169 |
| **ns/instance** | 9,053 | 751 | **41–46** |

**41–46 ns/instance is inside the normal-arithmetic band** for the first time.
The doodad pass at a crossing is now `doodad 0.4 = cull 0.3 + instanceUpload 0.0
+ drawSubmit 0.0`, against 60.3 ms two runs earlier.

> **Caveat on attribution, recorded honestly.** Nico reports the second reading
> was taken with the SoA toggle OFF, and it read 41 ns — the same as ON. Model
> count also fell from 512 to 169 between runs 4 and 6, and the rate tracked
> model count before. So the run-6 numbers do **not** by themselves prove the
> flat-bounds change caused the improvement. The clean test is both toggle
> states at the same spot, back to back, in one session. Until that is done,
> treat the cull as fixed but the *reason* as unconfirmed, and see PLAN_08 §7
> step 3 — if the toggle makes no difference at equal model count, back the
> change out.

### 5A.16 The pacing bug, fully isolated at last

```
[hitch] hitch-32-48-4: 34 ms frame at [32,48] -> swap-and-events
[hitch]   update 0.0 (all zero)  render 0.4  present 17.6  hud 15.8  imguiFlush 0.1
[hitch]   GPU (delayed): total 0.8    uploads: 0 in flight, 0 completed
[hitch]   GC: pause 0.0 ms  gen0 0 gen1 0 gen2 0  allocated 0.00 MB this frame
```

**Zero work, zero allocation, zero collections, zero uploads, idle GPU — and two
vblanks.** This is the micro-stutter with every other variable eliminated, and it
is the same shape §5A.3 first saw: the ~16 ms wait wandering between `present`
and `hud`, neither of which is a GL call of ours.

Nothing about our workload can explain it. The remaining question is not "what
were we doing" but **"were we running at all"**, and it has exactly two answers
with opposite fixes.

`QueryThreadCycleTime` is now sampled per frame, reported as
`threadMCyclesPerMs` in the record and in every ring row:

```
[hitch]   thread: 1.4M cycles over 34 ms = 0.04 M/ms  (~4-5 = running and spinning; <1 = blocked or descheduled)
```

- **~4–5 M/ms on a long frame** — the thread WAS running, burning CPU. A driver
  busy-wait spin, which Intel's GL driver is known to do. Fix is to stop it
  spinning: swap interval, adaptive vsync (`EXT_swap_control_tear`), or our own
  frame pacing.
- **<1 M/ms on a long frame** — the thread was NOT running. Blocked in a kernel
  wait or descheduled. Entirely different fix.

No calibration is needed: the comparison is against the frame's own wall clock
and the two answers are an order of magnitude apart. `GetThreadTimes` cannot
answer this — its resolution is the ~15.6 ms scheduler tick, the same size as the
thing being measured.

The ring carries it per frame on purpose: the 16.7 ms frames *around* a hitch are
also waiting on vsync. If they show the same rate, the hitch is a longer wait of
the same kind rather than a different event — and that distinction decides
whether this is one bug or two.

### 5A.17 Vsync off is not the fix

It tears, and it burns an integrated GPU rendering frames nobody sees. The
finding is not "ship it off" — it is that **the cost is a driver throttle
interacting with our upload pattern, not our workload**. Do not close this by
changing the default.

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
