# Benilla vs MSUI — frame cost and stream-in latency

Two separate problems with two separate answers. Reference points: WoW 1.12 at max settings
holds ~80% of an Iris Xe on the same scene; MSUI pegs 100% at Low. 1.12 shows its immediate
surroundings essentially on curtain-up; MSUI takes another 5–30 s.

Companion to `BENILLA_VS_MSUI_LOADING.md`, which covered the *pre*-curtain work. That fix landed
and it worked — the near world genuinely is resident when the screen lifts. What you are watching
now is a different pipeline, described in §2.

---

# Part 1 — Why the GPU is pegged

## 1.0 The finding that explains your actual question

**No quality preset moves a single fill-rate knob.** `GameSettings.ApplyQuality`
(GameSettings.cs:458-494) sets view distance, doodad distance, clutter density/radius, MSAA and
anisotropy. It does not touch `ForceTwoSided`, `AppearFade`, particle density, `TileRadius`,
terrain density (no such knob exists), or resolution.

So Low and Ultra run **the same shaders over the same per-pixel work at the same resolution**.
The only thing a preset changes is how far away geometry stops existing. That is why dropping to
Low doesn't help: on a fill-bound integrated GPU the presets have nothing to pull.

Your own measurements (SYSTEM_STREAMING.md §5A.20, stationary in Stormwind) put GPU total at
13.3 ms — **WMO 9.6–21.6 ms, terrain 2.8–3.1 ms, doodads 0.3–0.6 ms**. Everything below is
weighted by that: this is a WMO fill problem first and a terrain problem second.

## 1.1 Ranked findings

### 1. `ForceTwoSided = true` disables backface culling for every building — est. 3–8 ms

`WmoRenderer.cs:2822`: `bool twoSided = batch.TwoSided || ForceTwoSided;`
Default `true` at WmoRenderer.cs:895, GameSettings.cs:117, settings.json:43. No preset touches it.

The expression is unconditionally true, so `glDisable(GL_CULL_FACE)` fires on the first batch and
stays off for the whole pass. Every wall in the city pays 2× triangle setup and 2× rasterized
fragments. Back faces only get rejected if the front face happened to draw first — and batch order
is MOBA file order, not view order, so roughly half of them shade fully and are then overwritten.

**On the pass that is 72–86% of your frame, this is a one-line flip.** It is the single biggest
lever in this document.

### 2. No depth prepass and no front-to-back ordering anywhere — est. 2–5 ms

Draw order is sky (full-screen, depth-write off) → terrain (fills ~100% of the screen) → WMO over
it → doodads → foliage → particles → character → water. Terrain is fully painted before a single
building occludes it, and the buildings are painted in arbitrary depth order among themselves.

- TerrainRenderer.cs:540 iterates `_tiles.Values` — dictionary insertion order.
- WmoRenderer.cs:2654 iterates `_instances` in placement order.
- DoodadRenderer.cs:1773 iterates `_byModel` in dictionary order.

In a city view that is easily 3–5× shaded fragments per pixel on a chip whose entire design
assumption is that you don't do that. Iris Xe has no hardware hidden-surface removal.

Cheapest fix that fits the current architecture: sort WMO instances *and* each instance's
`visibleGroups` by distance before the draw loop. `ClassifyGroup` already computes
`groupDistance` at WmoRenderer.cs:1391 and throws it away.

Worth knowing: **benilla doesn't sort either.** It gets away with it because its fragment shaders
are ~10 instructions with one texture fetch. Yours are heavier, so you can't.

### 3. Terrain is culled per 533-yard ADT tile, with no LOD and no distant representation

TerrainRenderer.cs:540-551. The culling unit is the whole tile (`tile.BoundsMin/Max`). One corner
in the frustum submits the entire mesh. `QuadGridSide = 128` (line 59) → ~32.7k triangles per
tile; at `TileRadius: 1` that's ~295k triangles submitted, a large fraction of them sub-pixel.

Sub-pixel triangles are the worst case for a tiled binner — each still costs a 2×2 quad, so quad
overshading approaches 4×.

Vanilla drew per-MCNK with per-chunk frustum rejection. A per-chunk (16×16 sub-box) test alone
typically cuts submitted terrain triangles 50–70% at a 59° FOV. There is also **no WDL distant
terrain at all** — see §1.3.

### 4. Terrain fragment: 5 unconditional fetches amplified by 4.75× anisotropy — ~1–1.5 ms

Shaders/terrain.frag:63-80 — 1 alpha atlas + 4 tileset array fetches, every pixel, all mixed even
where the splat weight is zero. With `Anisotropy: 4.75` on ground at a grazing angle each of those
can expand to 4+ texel groups: up to ~20 filtered taps per terrain pixel over a full-screen
surface, ramping to 16× aniso at Ultra.

Also terrain.frag:83 normalizes `uSunDirection` — a **uniform** — per pixel. wmo.frag:81 and
doodad.frag:85 already do this correctly on the CPU.

### 5. WMO vertex format is 48 bytes — est. 1–2 ms of pure bandwidth

WmoRenderer.cs:85, layout at 2030-2040: pos float3 + normal float3 + uv float2 + colour **float4**.
The vertex colour is authored as 4 bytes and expanded to 16 for the GPU (BuildGroupVertexArray:2183
divides by 255 to get there). Normal as `GL_INT_2_10_10_10_REV` (4B), UV as half float (4B), colour
as `GL_UNSIGNED_BYTE` normalized (4B) → 24 bytes. An exact 2× cut on the vertex-fetch traffic of
the pass that dominates the frame, on shared DDR5 where that is real time.

### 6. Water has no culling of any kind — est. 1–4 ms when on screen

LiquidRenderer.cs:810-816 submits every loaded liquid tile every frame with no frustum test, no
distance test — unique among the renderers here. Plus `glDisable(GL_CULL_FACE)` at 808 and
`DepthMask(false)` at 805, so on-screen water is double-sided blended fill with no early-Z from
itself, and an ocean tile behind the camera still pays full vertex cost.

`water.frag` textured path is 3 fetches (`FrameBlend: 0.4375` means the cross-fade's second fetch
is always taken) plus a `pow(x,5)`. Setting `FrameBlend` to 0 halves water texture fetches for a
change nobody sees in motion.

**Latent cliff worth defusing:** water.frag:412-508 is a procedural fallback that runs when any
liquid BLP fails to load. Tally: ~84 transcendentals per fragment (36 `sin` in the cellular foam
alone) plus `pow(x, 200.0)`. It is one missing texture away from being your entire frame budget,
with no diagnostic that would tell you which path you're on. Put it behind a `#ifdef` or a separate
program.

### 7. Doodads: blending on for the whole pass, alpha-test on all geometry, no LOD

- DoodadRenderer.cs:1767-1771 — `AppearFade: true` enables `GL_BLEND` for every doodad every
  frame, though steady-state doodads all output `alpha == 1.0` (doodad.frag:122-133). Every doodad
  fragment pays a colour-buffer read-modify-write. The WMO path already tracks per-instance fade
  state (WmoRenderer.cs:2680); mirror it.
- DoodadRenderer.cs:1670 sets `uAlphaCutoff` for every textured batch regardless of whether the
  material has an alpha channel, so the `discard` at doodad.frag:76 is live on all doodad geometry.
  WMO does this right with `batch.AlphaTest` (WmoRenderer.cs:2841). *(Fixed 2026-08-12: the cutoff
  now follows the batch's M2 blend mode, and modes 2-6 draw in a deferred blended pass — see the
  "Backed out" section's superseded note below.)*
- No LOD, no impostors, no billboards anywhere in DoodadRenderer. A tree is drawn identically —
  thousands of alpha-tested two-sided leaf triangles — at 3 yards and at 341. Alpha-test discard on
  tiny distant foliage is the classic quad-overshading pathology on this hardware.

### 8. WMO LOD shells only apply to WMOs with more than 50 groups

WmoRenderer.cs:2145: `if (nGroups <= 50) return false;`. So the impostor path covers Stormwind and
Ironforge and nothing else. Every inn, tower, guard house and mine draws at full detail out to
`BuildingDistance: 739`.

### 9. Foliage draws the full 360° scatter disc unculled — est. 0.3–0.8 ms

FoliageRenderer.cs:802-830 scatters into a 37.5-yard disc around the camera and submits all of it
with no frustum test at draw time. At 59° FOV about 16% of that disc is on screen, so ~84% of up to
14,500 instances are vertex-shaded and clipped. The doodad path already does exactly the right
thing at DoodadRenderer.cs:1789-1810 — copy it.

### 10. Particles have no screen-coverage bound

ParticleRenderer.cs:256 `MaxParticles = 40000`, depth-write off at 830, additive and 2×-modulate
blend modes at 1153-1162. With depth-write off there is no self-occlusion, so a portal (~800
sprites in a small volume, per the shader's own comment) is unbounded overdraw of large screen-space
quads. No preset scales it — `ParticleDensity` lives in ClientConfig.cs:193, a different settings
object from the one the presets write.

### 11. Per-frame allocations and re-derivation in the draw path

CPU, not GPU, but it is on the critical path:

- WmoRenderer.cs:581,586 — `ComputeReachableGroups` allocates a `HashSet<int>` **and** a
  `Dictionary<int, GroupMesh>(model.Groups.Count)` per instance per frame. For Stormwind that is a
  several-hundred-entry dictionary rebuilt every frame from data that never changes.
- WmoRenderer.cs:2730 — `new List<GroupMesh>()` per instance per frame.
- WmoRenderer.cs:2736 — `TransformedBounds` recomputed per group per frame from a fixed transform.
- WmoRenderer.cs:2798-2854 — the two-pass loop binds `group.Vao` unconditionally before checking
  whether that group has any batch in this pass. Most groups have no transparent batches, so that's
  N wasted VAO binds per frame.
- ParticleRenderer.cs:792 — `new Dictionary<(string, byte), List<Pool>>()` per frame, string-keyed.
- Every `_shader.Set("uName", ...)` passes a string. If `Engine/Shader.cs` doesn't cache locations,
  the WMO pass alone is thousands of `glGetUniformLocation` calls per frame with string hashing in
  front of each. (Shader.cs wasn't in scope this pass — worth a 30-second check.)

### Checked and clear

Doodad instancing is real and correct (`DrawElementsInstanced`, per-instance frustum + distance
culling over a flat SoA bounds array) — which is why doodads measure 0.3–0.6 ms. WMO portal culling
is genuinely per-group with portal traversal and is done right; being inside one room does not draw
the building. MSAA is off at the current preset. FfxGlow defaults off. No stray render targets or
blits.

## 1.2 What benilla does that keeps its fragments cheap

Not "it's Rust" and not really "it's Bevy". The wins are all shader-shaped and portable:

**No shadow maps at all.** Terrain shadows are the pre-baked MCSH R8 array; unit shadows are a blob
decal. Nothing anywhere sets `shadows_enabled`. On Iris Xe this is worth more than everything else
combined. Worth confirming you don't have a shadow pass you've forgotten about.

**Lighting is per-vertex, not per-fragment.** terrain.wgsl:127-162 runs a selection loop over up to
**512 point lights**, keeping the 3 nearest by insertion sort, plus a Blinn `pow(ndoth, 20)` — all
in the **vertex** shader. The fragment shader (227-322) has no `normalize`, no `pow`, no loop and no
lighting: it is 5–6 fetches, 6 `mix`, one MAD, one fog lerp. Terrain vertex density is 145 verts per
33-yard chunk, so pixels outnumber vertices by orders of magnitude. It's also the faithful choice —
GL fixed-function T&L clamped per vertex.

**Early far-clip discard before any texture fetch.** terrain.wgsl:234-239 and wow_model.wgsl:309-314
discard on planar eye-Z as the first thing in the fragment shader. Costs a mat-vec, saves 6 fetches
plus the whole combine on everything past 777 yd.

**One draw per ADT tile instead of 256.** All 256 chunks' layer textures live in a
`texture_2d_array`; per-chunk alpha maps in another; the merged mesh's vertex COLOR and UV1.x carry
the per-chunk indices. Three texture bindings and **one shared sampler** for a whole tile.

**Alpha MASK, not alpha blend, for all foliage** — and at the vanilla `224/255 ≈ 0.878` key rather
than the Cata 0.5 (model_render.rs:18-23). Mask geometry writes depth and occludes; blend geometry
doesn't. The higher reference also discards roughly 2× more fragments. Only glass and glow cards
ride the transparent pass.

**Depth-write forced ON for transparent model batches** unless the M2 render flag clears it
(terrain.rs:169-180) — Bevy defaults it off; benilla overrides so a model's own transparent cards
occlude each other instead of all shading.

**Gamma lane: no sRGB anywhere.** All albedo is `Rgba8Unorm`, never `Rgba8UnormSrgb`. Saves a
linearize per sample and a conversion per blend, and makes hardware `DST_COLOR/ZERO` exactly equal
the reference's byte multiply. One decode for the whole frame, in the glow combine.

**Authored BLP mips uploaded verbatim, 8 levels, aniso 8** — never CPU-regenerated. Terrain
minification without full mip chains is the classic texture-cache killer on low-bandwidth parts.
(They also note regenerating by averaging in gamma byte space darkens mid-tones — a correctness
argument on top of the perf one.)

**Size-bucketed doodad fade with a hard cull at α=0** (model_fade.rs:56-90). Distance is horizontal
only, to the bounding-sphere centre minus its radius:

| bounding radius | fade band |
|---|---|
| > 7.0 yd | never fades (dies at the 777 yd wall) |
| ≤ 0.5 yd | 40 → 50 yd |
| 0.5 – 2.5 yd | 100 → 125 yd |
| 2.5 – 7.0 yd | 150 → 200 yd |

α ≤ 0 removes the object from the draw list entirely. Small props stop drawing at 50 yards.

**Clutter is CPU-merged, not instanced**: one draw per (chunk × model × submesh), with each tuft's
yaw/position/scale baked into world-space vertex positions and its MCSH tint into the vertex colour.
Geometry exists only inside a 94-yard bubble (built at 94, torn down at 100, 8 chunks/frame),
faded 52.5 → 70. Live grass geometry is bounded regardless of how many tiles are resident.

**Animation parking** (creature_anim/lod.rs): 0.5 s continuously off-frustum and every joint's
`AnimatedBy` is repointed at a dummy entity, so bone sampling early-outs. Clocks keep running so
off-screen combat stays audible; waking snaps to the absolute-clock pose. Second-order win is that
bone transforms stop changing, which quiets the skin-palette GPU upload — real bandwidth on an
integrated GPU.

**One `uint` per instance carries five render states** (mesh_tag.rs): bits 0-15 alpha, 16-23 MCSH
shade, 16-29 interior probe slot, 30 interior fog, 31 highlight, with whole-payload 0 as the
"untagged ⇒ opaque" sentinel. Five orthogonal per-object states without breaking instancing or
adding a uniform buffer. Directly stealable.

## 1.3 The distant-terrain gap

You have no WDL. Benilla does, and the numbers are stark:

- Detailed ring: 5×5 ADT tiles, far-clip wall at ~777 yd, per-fragment discard.
- WDL ring: Chebyshev radius 5 → 11×11 = 121 tiles ≈ 2665 yd, **545 verts per tile**, 8 tiles built
  per frame.
- The WDL shader is unlit, untextured, `base_color: WHITE`, `unlit: true`. Its fragment shader is a
  discard test, a fog lerp and a depth min. Zero texture fetches. **The visible colour is the fog.**
- It writes `frag_depth` clamped just inside the far plane so the 33-yard overlap with detailed
  terrain can never poke through. The overlap is deliberate — a shared clip plane between two
  surfaces leaves sky-coloured holes at ridge crests.

121 tiles × 545 verts ≈ 66k verts with a ~5-instruction fragment shader, in place of what would
otherwise be ~1900 ADT tiles at 37k verts each running a 6-fetch splat shader.

## 1.4 Measure before you tune

You have `GL_TIMESTAMP` / `GL_TIME_ELAPSED` available under GL and `GpuFrameProfiler.cs` already
exists. Before and after each change, get per-pass GPU time for: sky, terrain, WMO opaque, WMO
transparent, doodads, foliage, particles, character, water. The 13.3 ms figure in SYSTEM_STREAMING
is the right kind of number and there should be one per pass, tracked.

Also worth ten minutes: confirm what "100% GPU" is measuring. With vsync on, 100% utilization at a
locked 60 is not the same problem as 100% at 35 fps. Benilla's `perf.rs` keeps a 300-frame window
with p50/p99/max against a 16.7 ms budget and logs hitches over 250 ms, plus an optional CSV journal
of `t, x, y, z, mean_ms, p95_ms, entity counts` with the player position in raw WoW coords so a dip
pastes straight into a `.go xyz` probe. That last idea is cheap and worth copying outright.

---

# Part 2 — Why the world takes 5–30 s to fill in

## 2.0 The headline

**The WMO pipeline is serial three times over**, and it is 50–70% of the post-curtain window.

1. **One model in flight, ever.** `private ModelLoadJob? _preloadJob` (WmoRenderer.cs:355), topped
   up by `while (_preloadJob is null …)` at :1163.
2. **Single-threaded inside that model.** `PrepareWmo` runs on one worker slot for the entire model
   (:1553-1556) — all group files plus all BLPs, serial. A 200-group WMO uses 1 of your 8 cores.
3. **One group finalized per call.** `StepModelLoad` returns after building a single group
   (:1767-1802), and post-curtain the WMO renderer gets ~1.5 calls per frame → **90 groups/s at
   60 fps**.

And `_models[rootPath]` is only assigned after the **last** group (:1804+), so a building is 100%
invisible until it is 100% done. That is exactly the "nothing, nothing, nothing, then a building"
pattern you're seeing.

Arithmetic for Stormwind: 41 resident WMOs; `stormwind.wmo` alone is in the hundreds of groups
(the file's own comment at :238 says so). 600 groups ⇒ **6.7 s of finalization alone**, on top of
41 models × 0.3–1.5 s = **12–60 s of serialized prepare**. That is your 5–30 seconds.

SYSTEM_LOAD.md:143-146 flagged this as "still to do" but judged it "rarely the bottleneck". On a
city it is the entire thing.

## 2.1 The rest of the budget

**~15–20% — the single fenced GPU upload thread.** `GpuUploadWorker` is one thread
(GpuUploadWorker.cs:79-84) and each item blocks on `ClientWaitSync` for GPU completion (:103-114).
A whole WMO — every texture and every group — is enqueued as **one item** (WmoRenderer.cs:1645-1685),
so it head-of-line-blocks every terrain tile and every tree behind it for its entire duration. No
PBO path anywhere (zero `PixelUnpackBuffer` in the tree); uploads are blocking `TexImage2D` from
managed arrays, and `GenerateMipmap` runs per texture with the authored BLP mips discarded.

**~10% — the doodad radius gap.** Warm radius is `min(VisibilityDistance, DrawDistance+100)` ≈ 441
yd (Program.cs:320-322); placement radius is `DrawDistance + 377 + 50` ≈ 768 yd (:313-315).
Everything in that 327-yard band resolves null under demand streaming and retries later. The gap
guarantees a post-curtain tail by construction.

**~10% — `QueueVisibleDoodadDemand` burning the frame budget.** It re-derives every placement
(Program.cs:1352-1359 → `PopulateDoodads` → `LoadForTiles` over 9 ADTs + `EnumerateDoodads` over
every resident WMO's MODD list, 7,562 measured, with a LINQ `OrderBy` on top). Measured 32–71 ms.
Its idle early-out is **disabled whenever `PendingPreloads > 0`** (Program.cs:1339-1345) — i.e.
precisely during the stream-in — so it runs at full cost every 0.25 s for the whole window.

**~5% — self-inflicted duplicate work on the render thread.** Every vertex array is built twice:
once on the upload thread (DoodadRenderer.cs:1244-1245, WmoRenderer.cs:1665) and again on the main
thread in the same frame (:1264-1266, :2005) — where, with `uploaded != null`, the arrays are never
uploaded and only min/max is kept. For `stormwind.wmo` that is a full ~600k-vertex re-pack and
allocation on the render thread, one group per frame.

## 2.2 Three structural bugs worth fixing regardless

**`LoadWarmBudgetMs` is declared and never used.** Program.Loading.cs:68 declares it; nothing in the
tree references it. `DrainWarm` (:404-408) is a bare `for (i = 0; i < 48; i++)` with no clock.
PLAN_08 D2's resumable cursor was never built, and the doc's claim that each phase does a bounded
slice of work isn't true of the code.

**The 6 ms budget starts before the two most expensive jobs.** Program.cs:1241 starts the stopwatch,
then :1244 and :1248 run background discovery and the 32 ms doodad demand scan *inside* it, so by
:1258 the budget is always blown and you get exactly one warm call per frame. Move the stopwatch to
after :1248 and check it *inside* the finalize loops.

**A warmed WMO stays invisible until you cross a tile boundary.** `WmoRenderer.LoadForTiles` is
called from exactly two places — Program.Loading.cs:259 and Program.cs:604. There is no per-frame
WMO equivalent of `QueueVisibleDoodadDemand`.

Also: `_preloadWmoFirst` alternates 50/50 between the two renderers (Program.cs:1256-1264), but one
doodad call finalizes up to 12 **models** and one WMO call finalizes one **group**. The split is
nominally fair and effectively ~100:1 in the doodads' favour. Buildings starve behind trees.

And the WMO queue has **no distance ordering at all** — `TryQueueRingTile` enqueues in raw MODF
order per ADT (:1129-1145). Since only one warms at a time, that arbitrary order directly decides
which building you stare at a hole in for 30 seconds.

Watchdog note: `LoadPhaseWatchdogSeconds = 30f` across nine phases means the curtain can
theoretically hold 4.5 minutes — and a timed-out phase dumps its unfinished work onto the blocking
`ResolveModel` path (WmoRenderer.cs:1528-1546: `GetAwaiter().GetResult()` then a spin on
`StepModelLoad(waitForUpload: true)`). `BENILLA_VS_MSUI_LOADING.md` §5 called that "the line to
kill"; it is still there.

## 2.3 What benilla does instead

**Wall-clock budgets on every main-thread burst, checked *after* the unit of work** so progress is
always guaranteed, with a re-entrancy flag making the loop resumable:

| budget | value | covers |
|---|---|---|
| `SPAWN_BUDGET` | 4 ms | tile spawn, and independently placement spawn (~8 ms/frame combined) |
| `ATTACH_BUDGET` | 2 ms | attaching finished colliders |
| `READY_SCAN_CAP` | 2048 | bound on the poll pass (Stormwind queues ~10,500 pending colliders) |
| clutter | 8 chunks/frame | grass mesh build + upload |
| WDL | 8 tiles/frame | horizon meshes |

`finish_colliders` is an **exclusive system** taking `&mut World` deliberately: the previous version
queued each attach as a deferred command, which buried the whole burst in one opaque block with
nothing able to measure or stop it. Owning the world is what makes a deadline enforceable. Their
measured attach cost is ~0.004 ms/entity + ~1.8e-5 ms/triangle, and one traced flight hitch spent
9.45 ms there in a single frame — which is how they knew to budget it.

**Collision never touches the main thread.** `Collider::trimesh` (parry QBVH, "hundreds of ms") runs
on the async compute pool; the built shape is *parked* in `PendingCollider.built` when the attach is
deferred, because a completed task can't be polled twice — so no finished build is ever dropped.
There's a test asserting both that the budget bites and that nothing is lost.

**The player is frozen until the ground under it is real.** A 1-yard down-probe with a 6 s backstop
(mover.rs:109-113); while settling, gravity is off and both velocity terms are zeroed. Because the
avatar literally cannot move or fall, colliders are allowed to arrive several frames late with no
consequence — which is what makes the loading screen's clear condition trivially safe.

**Resident radius is larger than the far clip.** 5×5 tiles ≈ 1066 yd against a 777 yd wall
(assets/mod.rs:386-390) — chosen so tiles are resident *before* the far clip reveals them. Pop-in
becomes structurally impossible rather than something you fight.

**Three-layer dedup:** decode by path (AssetServer handle cache), material by full render-state
tuple (which is what enables batching), and instance by authored MDDF/MODF `uniqueId`, refcounted
across tiles — so a building straddling four tiles is spawned once.

**One shared light/fog storage buffer** referenced by every material and written in place once per
frame, so a tile streamed this frame is correctly lit on its first drawn frame. No warm-up, no
materials settling in. The predecessor re-pushed per-material uniforms every frame and therefore
re-created every bind group every frame.

**The IO pool is deliberately oversized** — `min 2, max 8, percent 0.5` against a framework default
cap of 4 (main.rs:312-322) — because MPQ decompress and parse are synchronous on that pool, so a
dense-area teleport saturates it and net-driven NPC models queue behind the terrain flood. And
`thread_qos.rs` promotes worker threads above default OS priority, because at default they compete
with the user's compiler for cores. The Windows equivalent is `SetThreadPriority` and avoiding
`PROCESS_MODE_BACKGROUND_BEGIN`.

### What benilla does *worse*, so don't copy it

No nearest-first tile ordering (`desired` is built in raw dx/dy loop order and the spawn loop walks
a `HashMap`, so per-frame spawn order is effectively random). No eviction budget and no hysteresis —
crossing one tile boundary evicts an entire row plus every uniquely-referenced placement in a single
frame, and it's the one unbudgeted path in their system. No derived/preprocessed asset cache: every
run re-parses MPQ from scratch, and their own diagnostic notes ~2 s of post-login terrain latency
they simply spread across frames.

That last one is your biggest opportunity that benilla *doesn't* have. A derived-format cache —
pre-merged tile mesh, pre-packed layer array, pre-serialized collision trimesh, memory-mapped —
turns parse cost into a memcpy. Benilla can't easily do it because its gamma-space fidelity
constraint rules out naive recompression; you'd be caching already-decoded RGBA8 plus the authored
mip chain, which is a straight copy.

---

# Part 3 — What to do, in order

## Frame cost

| # | Change | Effort | Est. win | Status |
|---|---|---|---|---|
| 1 | `ForceTwoSided = false` | one line | 3–8 ms | **DONE** |
| 2 | Sort WMO instances + groups front-to-back using the already-computed `groupDistance` | small | 2–5 ms | **DONE** |
| 3 | Per-MCNK frustum culling in `TerrainRenderer.Render` | small | ~1–2 ms | **DONE** |
| 4 | Frustum-cull the liquid tile list; `FrameBlend` → 0 | small | 1–4 ms | **DONE** (cull only) |
| 5 | Gate doodad blending on actual fade state | small | 0.3–0.8 ms | **DONE** |
| 6 | Frustum-cull foliage instances at draw time | small | 0.3–0.8 ms | **DONE** |
| 7 | Move `normalize(uSunDirection)` to the CPU; skip zero-weight terrain layers | small | ~0.5–1 ms | **DONE** |
| 8 | Pack the WMO vertex to 24 bytes | medium | 1–2 ms | open |
| 9 | Give the presets real fill-rate levers: particle density, terrain aniso, `AppearFade` | medium | preset-dependent | open |
| 10 | Size-bucketed doodad fade + hard cull at α=0, benilla's four buckets | medium | 0.5–1.5 ms | open |
| 11 | WDL distant terrain ring | large | enables a much shorter far clip | open |
| 12 | Cache `ComputeReachableGroups`' dictionary and `TransformedBounds` on the model | small | CPU only, but it's per-frame | open |
| 13 | Split doodad.frag into discard / no-discard variants so opaque batches get early-Z | medium | unmeasured | open — see §5 below |

Items 1–7 landed together and were, by eye, most of the gap.

See **Part 4** for exactly what shipped, including one item that was backed out.

## Stream-in

| # | Change | Effort | Est. win |
|---|---|---|---|
| 1 | Pool the WMO preloader exactly as the doodad one was pooled (list of ~6 jobs, finalize every ready job per call) | medium | the single biggest cut |
| 2 | Publish WMO groups incrementally — place the instance after the first group, append the rest | medium | buildings appear progressively instead of all-at-once-after-nothing |
| 3 | Parallelise `PrepareWmo` internally across the worker pool | medium | 200-group WMO currently uses 1 of 8 cores |
| 4 | Split a WMO's upload into per-group items; batch N items per fence or run 2–3 upload threads | medium | removes head-of-line blocking |
| 5 | Move the 6 ms stopwatch after the two scan jobs; check it *inside* the finalize loops; wire up `LoadWarmBudgetMs` | small | doubles effective warm throughput |
| 6 | Add a per-frame WMO demand pass so a warmed building appears without a tile crossing | small | removes a whole class of "it never showed up" |
| 7 | Order the WMO queue by distance; make both queues re-prioritisable rather than FIFO | small | what fills in first stops being arbitrary |
| 8 | Make `QueueVisibleDoodadDemand` incremental (dirty set, not a 7,562-entry re-walk 4×/s) | medium | ~10% of the window |
| 9 | Delete the duplicate vertex-array builds; return min/max from the upload-side pass | small | pure win, large main-thread allocation per model *and per group* |
| 10 | Close the 327-yard doodad radius gap | one line | removes a tail that exists by construction |
| 11 | Delay `_backgroundDiscovery` until the WMO/doodad queues drain | small | stops 16 tile uploads starting at the fade |
| 12 | Upload authored BLP mips instead of `GenerateMipmap`; add a PBO path | medium | upload thread throughput + texture quality |
| 13 | Derived-asset cache on disk (merged mesh, packed layer array, serialized BVH) | large | turns parse into memcpy |

Items 1, 2 and 5 are the ones that change the felt experience. None of the stream-in work has been done yet.

---

# Part 4 — What actually shipped

All of frame-cost items 1–7, plus the settings plumbing needed to make item 1 reach an
existing install. Nothing from the stream-in list.

## The changes

**1. Backface culling restored for buildings.** `ForceTwoSided` now defaults off in
`WmoRenderer`, in `GameSettings.Detail`, in `Vantage`, and in the shipped `settings.json`.

It needed all four, because `Program.Settings.cs:1430` pushes `s.Detail.ForceTwoSided` onto the
renderer every time settings apply — so the renderer's own default is not authoritative and
changing it alone would have done nothing. A **v2 → v3 settings migration** (`GameSettings.Migrate`)
forces it off once for any existing `settings.json`, which is the codebase's own documented
mechanism for exactly this.

> **Trap: saved vantages still carry the old value.** `Vantage.WmoForceTwoSided` is serialised per
> vantage and every one captured before this change records `true`. Loading one turns backface
> culling off again for the whole WMO pass, so an old vantage will benchmark much worse than live
> play at the same spot. Re-capture before reading anything into a difference.

**2. Front-to-back ordering, at two levels — and a restructure it forced.**

WMO instances are culled into a reusable `_drawOrder` list, sorted by `DistanceToBox`, and each
instance's surviving groups are sorted the same way.

The first attempt left the two passes nested inside the instance loop, which made transparency
*worse* than the arbitrary order it replaced: a near building's transparent geometry (depth-write
off) would be laid down before a far building's opaque walls, and the walls would then paint over
it wherever the near building had not itself written depth — which is exactly where a banner hangs
in open air. Previously the order was arbitrary, so this happened for roughly half of all pairs; a
near-to-far sort made it happen for all of them.

So `Render` now runs culling once, recording an `InstanceSlice` (uniforms + a slice of a flat
`_flatGroups` list) per instance, and the two draw passes run **after** the loop: opaque walks
slices near-to-far, transparent walks them far-to-near, with groups reversed inside the slice too.
One pass of culling, two orders.

Also in that path: per-instance uniforms and VAO binds are now set lazily, on the first batch that
actually draws — most groups have no transparent batches at all — and the two per-instance/per-frame
`List` allocations are gone.

**3. Terrain culls per MCNK.** `TerrainTile.Prepare` records a `ChunkRange` (index start, count,
bounds) per chunk. Chunks were already emitted contiguously into the index buffer, so this was free
and a chunk is a contiguous range. `TerrainTile.Draw(viewProjection, cameraPosition)` frustum-tests
each and merges neighbouring visible chunks into single `glDrawElements` calls; a gap closes the
run rather than being drawn through. `TerrainRenderer.ChunkCulling` switches it off for A/B.

The tile-level test is now only a cheap reject. A 533-yard box with one corner on screen no longer
submits all ~32,700 of its triangles.

**4. Water gets a frustum test.** `TileMesh` records world bounds from its packed vertex array and
`Render` rejects off-screen tiles. This pass previously had no visibility test of any kind — unique
among the renderers.

Backface culling on water was **not** enabled: `Disable(CullFace)` there is deliberate so the
surface reads correctly when the camera dips under it. `FrameBlend` was left alone too — halving
the water texture fetches is real, but it is an appearance change and belongs to whoever is looking
at the water.

**5. Doodad blending is per model, not per pass.** `AppearFade` used to enable `GL_BLEND` for the
whole pass, described as a no-op because steady doodads output alpha 1. It composites identically,
but it is not free: every doodad fragment paid a colour-buffer read-modify-write for a fade nothing
was doing. The instanced path now scans the surviving instances for one still inside its fade
window and toggles blend accordingly.

**6. Foliage culls per instance** — six frustum planes extracted once per frame
(Gribb-Hartmann, normalised), then a sphere test per instance with an early out.

Deliberately **not** `Camera.BoxInFrustum`: that helper transforms all eight corners of a box, which
is right per building and ruinous across ~14,500 grass cards — it would have been six figures of
matrix-vector work per frame on the render thread to save vertex shading on a few triangles each.
The cull radius scales with `Scale` and `ScaleJitter`, so raising the size knob cannot make grass
wink out at the screen edge.

**7. Terrain shader.** `normalize(uSunDirection)` moved to the CPU (`TerrainRenderer.SafeNormalize`)
— it was normalising a uniform per pixel over a surface that covers most of the screen. Overlay
layers whose splat weight is zero are skipped, using `textureGrad` with gradients hoisted above the
branches so the skips are well-defined in divergent control flow.

## Backed out: the doodad alpha-cutoff change

The plan was to stop passing an alpha cutoff to opaque doodad batches, on the theory that a blanket
`discard` was defeating early-Z on all doodad geometry.

**The reasoning was wrong.** Drivers disable early depth rejection on the *static* presence of
`discard` in the shader, not on the uniform's value, and `doodad.frag` discards unconditionally in
two places. Setting the cutoff to zero recovers nothing. What it would have changed is real: a
`BlendingMode == 0` batch whose texture nonetheless carries a cutout alpha would start rendering as
a solid quad.

`Batch.AlphaTest` is still derived (correctly, from M2 `BlendingMode != 0`) and documented as
parsed-but-unused. The real win needs a second shader program with no `discard`, selected per batch
— item 13 on the frame-cost list.

> **Superseded 2026-08-12.** `Batch.AlphaTest` no longer exists; DoodadRenderer now carries the
> full M2 `BlendMode` per batch and splits drawing into an opaque pass (modes 0-1; mode 1 keeps the
> 0.5 cutout cutoff, mode 0 passes cutoff 0) and a deferred BLENDED pass (modes 2-6; depth write
> off, per-mode `glBlendFunc`, cutoff 1/255) in both the instanced and non-instanced paths. This was
> done for correctness — additive lamp/lantern halos (e.g. LampPost.m2's Glow32.blp, blend 4) were
> being alpha-tested to nothing — not for the early-Z win, which still needs the discard-free
> second program described above.

## Verification status

**None of this was compiled.** No .NET SDK is reachable from the environment this was written in, so
it is careful-reading-verified only, twice over, including a dedicated adversarial pass that caught
the transparency ordering regression and the foliage CPU cost before they shipped.

New counters for the HUD: `LiquidRenderer.TilesDrawnLastFrame`,
`FoliageRenderer.InstancesDrawnLastFrame`, and terrain's draw-call and triangle counts now report
what was actually *submitted* rather than what was resident.

Get per-pass `GL_TIME_ELAPSED` numbers before and after judging any of it. If the WMO number does
not move a lot, something did not take.
