# Plan 08 — Incremental residency (killing the tile-crossing freeze)

**The structural fix the hitch recorder earned.** PLAN_07 built the instrument;
this spends what it measured. The short version: MSUI treats a tile crossing as
an *event* that rebuilds the world in one frame. WoWee never has such an event at
all, and the difference is why they stream smoothly and we freeze at the same
coordinates every time.

Grounded in measurement (`[hitch]` / `[stream]` output, 2026-07-24) and in
WoWee's real source at `C:\Users\nico\Desktop\WoWee-master`:
`include/rendering/terrain_manager.hpp` and `src/rendering/terrain_manager.cpp`.

---

## 1. Problem

Nico, after three instrumented runs:

> "Its always the same spots. So its some type of boundary that we cross, and no
> matter how much time i SIT before it, it happens. Its a fundamental flaw in how
> we process it."

He is right, and the instrument agrees. Measured at the Northshire → [32,49]
crossing:

```
[hitch] hitch-32-49-2: 187 ms frame at [32,49] (-9067,-44,88) -> residency
[hitch]   update 119.4 (move 0.5 resid 118.8 preload 0.0)  render 66.4
          present 0.3  gui 0.5  input 0.0  unaccounted 0.1
[stream]  terrain 2.5 ms  wmo 0.4  liquid 1.2  wmoQueue 61.2
          doodads 12.1  retain 0.0  collisionSnapshot 36.0
```

| Cost | ms | What it actually is |
|---|---|---|
| `wmoQueue` | 61.2 | `WmoRenderer.QueuePreloadForTiles` calling `adts.Get()` for 25 tiles **on the main thread**, purely to read each ADT's MODF list |
| `render` | 66.4 | the crossing frame adopting everything at once (2 ms on any other frame) |
| `collisionSnapshot` | 36.0 | re-walking 1,166,289 triangles to rebuild the collision snapshot |
| everything else | 16.2 | terrain 2.5 + doodads 12.1 + liquid 1.2 + wmo 0.4 |

**Note what is NOT the problem.** The full placement rebuild — the thing that
looks most wasteful — costs 12.5 ms. The cost is *synchronous asset access and
one-frame adoption*, not the rebuilding itself.

**Why sitting still does not help, precisely.** The preload ring is computed from
`next` — the tile you have just entered. Its ADTs are therefore first requested
at the instant of the crossing. There is no wait that can front-run a request
that does not exist until the boundary is crossed.

## 2. Class

**Emulation-core.** The real 1.12 client walks this route on this hardware with
no 187 ms stall, so there is an external right answer. It is also the one place
the handbook already concedes MSUI is "visibly behind the real client" (§3.27).

## 3. Target

Walking any tile boundary in Elwynn or Stormwind produces **no frame over 40 ms**,
with the recorder's threshold set to 40. Secondary: the `[collision] from client
geometry` re-walk stops appearing once per second during streaming.

## 4. How WoWee does it — read before designing anything

Five mechanisms, all present in `terrain_manager.{hpp,cpp}`. MSUI has none of
them. They are listed in the order they matter for our measured numbers.

### 4.1 Per-tile instance ownership — there is no global rebuild

`TerrainTile` (hpp:53) carries the IDs of everything it placed:

```cpp
// Instance IDs for cleanup on unload
std::vector<uint32_t> wmoInstanceIds;
std::vector<uint32_t> wmoUniqueIds;   // For WMO dedup cleanup on unload
std::vector<uint32_t> m2InstanceIds;
std::vector<uint32_t> doodadUniqueIds;
```

`unloadTile` removes exactly those and nothing else:

```cpp
m2Renderer->removeInstances(fit->m2InstanceIds);
wmoRenderer->removeInstances(fit->wmoInstanceIds);
for (uint32_t uid : tile->doodadUniqueIds) placedDoodadIds.erase(uid);
```

**There is no `ResetPlacements()` anywhere in WoWee.** Adding a tile adds its
instances; removing a tile removes its instances. The six unchanged tiles of a
3x3 shift are never touched. MSUI instead clears everything and re-derives all
6,400 placements from all nine resident ADTs on every crossing.

### 4.2 Finalization is resumable and time-budgeted — the key idea

`FinalizingTile` (hpp:155) is a **cursor**, not a job:

```cpp
size_t m2ModelIndex = 0;      // Next M2 model to upload
size_t m2InstanceIndex = 0;   // Next M2 placement to instantiate
size_t wmoModelIndex = 0;
size_t wmoInstanceIndex = 0;
size_t wmoDoodadIndex = 0;
size_t wmoLiquidGroupIndex = 0;
bool terrainPreloaded = false;
int  terrainChunkNext = 0;    // Next chunk index to upload (0-255, row-major)
bool terrainMeshDone = false;
```

and `processReadyTiles()` advances cursors until a wall-clock budget expires:

```cpp
const float budgetMs = taxiStreamingMode_ ? 16.0f : 8.0f;
while (!finalizingTiles_.empty()) {
    bool done = advanceFinalization(ft);
    if (done) finalizingTiles_.pop_front();
    if (elapsed >= budgetMs) break;
}
```

A single tile's publication is spread over as many frames as it needs — even its
**256 terrain chunks are uploaded a few per frame**. So a crossing *cannot* cost
more than 8 ms of main thread, structurally, no matter how heavy the tile.
This one mechanism would cap our 187 ms at 8 ms on its own.

### 4.3 The publish path never touches source assets

`PendingTile` (hpp:122) carries everything the main thread will need, with the
reason written in the comment:

```cpp
// Pre-loaded terrain texture BLP data (loaded on background thread to avoid
// blocking file I/O on the main thread during finalizeTile)
std::unordered_map<std::string, pipeline::BLPImage> preloadedTextures;
std::unordered_map<std::string, pipeline::BLPImage> preloadedM2Textures;
std::unordered_map<std::string, pipeline::BLPImage> preloadedWMOTextures;
std::unordered_map<std::string, pipeline::BLPImage> preloadedWMONormalMaps;
```

Placements, models, textures and even CPU-generated normal maps are extracted by
workers **before** publication. MSUI's publish path calls `adts.Get()` in five
different renderers, and `AdtCache.Get` blocks on a pending parse
(`return pending.GetAwaiter().GetResult();`). That is our 61 ms.

### 4.4 Hysteresis — load and unload radii differ

```cpp
int loadRadius   = 6;   // 13x13
int unloadRadius = 9;   // unload beyond 19x19
```

Walking back and forth across a boundary cannot thrash, because the tile you
just left is nowhere near the unload threshold. MSUI uses one radius for both,
so a boundary step in either direction is a full residency change.

### 4.5 Streaming is throttled, ordered nearest-first, and unloads are budgeted too

- `updateInterval = 0.033f` — the residency check runs ~30x/sec, not per frame.
- `streamTiles()` sorts new tiles by `distSq` and `push_front`s them so close
  tiles preempt distant ones already queued.
- Unloads are queued and drained by `processPendingUnloads()` under its own
  per-frame budget rather than executed inline.
- `failedTiles` records a missing ADT permanently so it is never retried.

## 5. Key design decisions for MSUI

Ordered by measured payoff. Each is independently shippable and independently
measurable with the recorder — **do not do them in one change.**

**D1 — `QueuePreloadForTiles` must never block (61 ms).**
Add `AdtCache.TryPeek(col,row, out adt)` that returns false when the tile is not
already parsed and **never** waits. `QueuePreloadForTiles` skips those tiles and
retries next frame; a parallel `adts.QueueLoad` warms them. Then queue the
*prospective* neighbour rings while walking, so the ring is parsed before the
boundary is reached — this is what removes the "sitting doesn't help" property.
The same fix applies to `DoodadRenderer` (351, 434), `FoliageRenderer` (270) and
`LiquidRenderer` (304); all four call `adts.Get` from the main thread.

**D2 — Budgeted, resumable adoption (66 ms).**
Port §4.2. `PumpPreloads()` already exists and is already called per frame, but
the crossing bypasses it by calling `SetResidency` directly. Give residency a
`FinalizingTile`-style cursor and an 8 ms budget. This is the single highest-value
structural change and it caps every future streaming cost automatically.

**D3 — Per-tile placement and collision ownership (36 ms + the 1/sec re-walk).**
Port §4.1. Each resident tile keeps its own placement list and its own collision
triangle array; a crossing splices the delta instead of re-walking 1.17M
triangles. This also fixes the sub-threshold stutter, because the repeated
`[collision] from client geometry` rebuild during doodad streaming becomes an
append rather than a full snapshot.

**D4 — Hysteresis. DROPPED after reading our own code.** WoWee's 6/9 split is
real and right *for WoWee*, but our residency radius is 1 (a 3x3 ring), so an
unload radius of 2 means holding 25 terrain tiles with their GPU buffers and
tileset arrays instead of 9 — roughly 2.8x terrain VRAM on an Iris Xe that is
already the constraint. The benefit is only avoiding thrash when walking a
boundary back and forth, which is not the reported problem. Revisit if the
residency radius ever grows; do not port the numbers as-is.

**D5 — Throttle the residency check. DROPPED, it would buy nothing.**
`UpdateWorldResidency` already early-returns on `_residentCentre == next` after
one `TileAt` and a tuple compare. WoWee's 33 ms interval matters because their
`streamTiles()` sweeps 169 tiles every call; ours does nothing at all until the
centre tile actually changes. Throttling a no-op is not an optimization.

> **The general point, which is the handbook's §10 warning in action:** four of
> WoWee's five mechanisms transfer directly and two of them do not survive
> contact with our numbers. Cross-check a reference against your own
> measurements before porting it — the two dropped items would have cost VRAM
> and complexity for nothing, and both looked perfectly sensible on paper.

**D6 — Name the 66 ms render before restructuring it.** `_worldRenderMilliseconds`
lumps terrain, WMO, doodads and foliage into one number, so "render 66.4" on the
crossing frame is currently unattributable. Foliage is a live suspect —
`FoliageRenderer.Scatter` runs inside `Render` and a residency change invalidates
its scatter — but `DoodadRenderer.RenderInstanced` rebuilds and re-uploads its
whole visible-instance buffer every frame anyway, so it is not obviously the
crossing that makes it expensive. **Measure first.** (Done: foliage and liquid
now have their own timers and the `[hitch]` line prints the render split.)

## 6. Tools / instrument

Already built and already proven on this exact bug: the hitch recorder
(PLAN_07), the seven `[stream]` sub-phase timers, and the auto-saved
`hitch-<col>-<row>-<n>` vantages. **No new instrument is needed** — which is the
first time that has been true in this project, and is the whole return on
PLAN_07.

## 7. Test protocol

Written before any code changes.

1. Baseline is on record: 187 ms, `resid 118.8`, `wmoQueue 61.2`,
   `collisionSnapshot 36.0`, `render 66.4`.
2. Set the recorder threshold to **40 ms** so sub-threshold stutter becomes
   visible; walk the [32,48] → [32,49] crossing.
3. After **each** of D1, D2, D3: same crossing, same threshold, diff the
   `[stream]` line field by field. A change that does not move its own named
   field is a change that did nothing — back it out.
4. Then walk *into Stormwind*, not just Elwynn, since it is the heaviest tile
   set and the one §3.27 has always been about.
5. Regression watch: ground collision must stay correct across a crossing (D3
   touches the collision snapshot), and the character must not fall through or
   stick. The `[move] NO GROUND` line already appears in the logs at [32,49] —
   **capture whether that predates D3.**

## 8. Definition of done

No frame over 40 ms while walking any Elwynn or Stormwind tile boundary, and the
`[collision] from client geometry` full re-walk no longer appears during ordinary
streaming. Handbook §3.27's measured verdict updated with the new numbers, and
`SYSTEM_STREAMING.md` extracted (§1.2 has listed it as "planned extraction" since
Draft 21; this is the work that earns it).

## 9. Fallback

D2 alone. A budgeted, resumable adoption caps the main-thread cost of a crossing
regardless of how much work it contains, so even if D1 and D3 are never done the
freeze becomes a slightly longer stream-in instead of a stall. If D2 proves
invasive, D5 + D4 are a few lines each and reduce how *often* the cost is paid,
which is worth having while the real fix is built.

## 10. Reconciliation

- **PLAN_07** is complete and its instrument is reused unchanged. Its §8 said the
  classification would become Plan 08; this is that plan.
- **Handbook §3.27** was rewritten with the first measurement and needs a second
  pass after D1-D3.
- **Handbook §5.4 (thread and ownership rules)** gains a row: *the publication
  path may not read source assets* — the rule WoWee states in a comment and MSUI
  breaks in five places.
- **`SYSTEM_STREAMING.md`** should be extracted as part of D2/D3 per §1.2's
  one-system-one-doc rule, and should carry §4 of this plan as its "how the
  reference does it" section.
- **The 2026-07-24 QueuePreload change** (moving terrain's ADT parse to the
  worker pool) stays, but it is only correct once D1 lands: right now it moves
  the parse off-thread and the very next line blocks waiting for it, which is why
  the crossing got *worse* (169 ms → 187 ms) before it got better.
