# SYSTEM_LOAD — the loading screen and the incremental first-zone load

Written 2026-07-26. This documents the load path as it is actually built after the
benilla-style rework. It implements the fixes proposed in `BENILLA_VS_MSUI_LOADING.md`
(read that first for the *why* and the benilla file:line proof). This doc is the *what*.

Files that make up the system:

- `MSUIClient/Program.cs` — `Load()` is now a fast "shell" that builds empty renderers and hands off.
- `MSUIClient/Program.Loading.cs` — the per-frame world-build phase machine (new partial of `GameLoop`).
- `MSUIClient/Engine/LoadingScreen.cs` — the loading curtain (new, self-contained GL).
- `MSUIClient/Formats/MpqMount.cs` — archive reads now run in parallel.
- `MSUIClient/World/Doodads/DoodadRenderer.cs` — the doodad preloader now decodes many models at once.

## The problem this replaced

The old `Load(GL gl)` built the entire resident zone — terrain, every building, and the
collision BVH — synchronously, inside the GL load callback, before the render loop ever
presented a frame. So the window was frozen (OS "not responding") for the whole multi-second
build, with no loading screen. And the doodads streamed in *afterward*, one model at a time,
which was another ~25 seconds of props visibly popping into the world. Entering a zone, and
`.tele`-ing between them, both felt nothing like 1.12 or benilla.

The rework does three things: it puts a loading screen in front, it moves the build off the
one blocking call and into a budgeted per-frame machine, and it makes the asset streaming
actually parallel so the world fills in fast enough to hide behind the curtain.

## How the load runs now

`Load()` no longer builds the world. It does only the cheap shell setup — mount the MPQs,
create the GPU upload worker and asset worker pool, construct every renderer *empty* (each is
just a shader compile plus a couple of GL objects), create the character and the controller,
load the settings UI — and then calls `BeginWorldLoad(gl)` and returns. Because it returns
quickly, the render loop starts and the first frame it presents is the loading screen, not a
frozen window.

From then on the build is driven one frame at a time. `Update()` has a single new line right
after the controller null-check:

    if (_worldLoading) { StepWorldLoad(dt); return; }

While the curtain is up, that runs the build and skips all gameplay (movement, residency,
portals) for the frame. `Render()` draws the (partial) world as usual and then, at the very
end, draws the curtain over it:

    if (_loadScreen is not null) DrawLoadingScreen();

Silk raises `Update` and `Render` separately every frame, so the window keeps presenting at
~60 fps with an animating progress bar the whole time.

## The phase machine (Program.Loading.cs)

`StepWorldLoad` advances a small state machine. Each phase does a bounded slice of work, or
waits on the async streamers, then moves on. Every phase reuses the streaming methods the
client already had for tile crossings — it does **not** call the old blocking `LoadAround` /
`LoadForTiles` / `LoadCollision`.

1. **Terrain** — `QueuePreload` the resident tile ring (parse + mesh + upload all run off the
   main thread on the worker pool). Wait until `PreloadReady`, then `SetResidency` adopts the
   prepared tiles (just cheap VAO creation on the main thread).
2. **WarmBuildings** — drain the WMO model warm queue (`WarmNextPreload`) so the placement pass
   is a cache hit instead of a blocking resolve.
3. **PlaceBuildings** — `ResetPlacements` + `LoadForTiles` (fast now, warm), then queue the
   outer WMO ring for the first crossings.
4. **Liquid** — build water for the resident tiles, and queue the near doodads (outdoor MDDF +
   resident WMO interiors) so the next phase can drain them.
5. **WarmDoodads** — drain the doodad warm queue behind the curtain. This is the phase that
   used to be missing: the old cut cleared the screen here with zero doodads. Now it holds
   until the near doodads are resident.
6. **PlaceDoodads** — `PopulateDoodads` once, placing every warmed model's instances in one
   shot, behind the curtain, so the reveal is fully populated.
7. **Collision** — `BeginCollisionBuild` (snapshot on the main thread, BVH on a worker). Runs
   *after* doodad placement, so trees, fences and props are solid the moment you can walk.
8. **Finish** — wait for the off-thread BVH to be adopted (the "collision under the player is
   real" gate, like benilla's player-settling), load the maps/portals DBCs, snap the player
   onto the ground, hand the outer ring to the background discovery streamer, and print
   `[game] world ready in Ns`.
9. **Fade** — fade the curtain's opacity from 1 to 0 over half a second, then dispose it.

So the curtain clears on a *minimal but complete* condition: the near world (terrain +
buildings + doodads) resident, and collision under the player built. Not the whole map — the
outer rings and anything past view distance keep streaming after the curtain lifts.

## The loading curtain (Engine/LoadingScreen.cs)

Self-contained: a dark full-screen quad plus a progress bar, drawn from `gl_VertexID` against
an empty VAO with a tiny inlined shader (the same trick `SkyRenderer` uses). No shader files,
no textures, no external assets. It saves and restores the depth/blend state it touches, and
draws last in the frame so it covers whatever the world has managed to build so far. The bar
colour is the client's sky/fog accent so it reads as part of the same world.

## Why the world now fills in fast: two throughput fixes

Putting the build behind a curtain is only half of it. If the assets still stream in slowly,
the curtain just stays up for 30 seconds. Two changes made the streaming actually parallel.

**1. MpqMount reads in parallel.** `MpqArchive.ReadFile` is fully thread-safe — it reads only
immutable tables, allocates every working buffer per call, and uses positioned `RandomAccess`
I/O with no shared file cursor (its own header says so). But `MpqMount` was wrapping every
read in one global lock, so the eight worker threads all funnelled through it and read one
file at a time. The lock is now a `ReaderWriterLockSlim`: reads take the read lock
(concurrent), only `Dispose` takes the write lock. Counters are `Interlocked`.

This helped, but on its own it did **not** fix the doodad pop-in — which pointed straight at
the second problem.

**2. The doodad preloader decodes many models at once.** `DoodadRenderer` used a single
`_preloadJob`: `while (_preloadJob is null …)` prepared exactly one model, waited for it,
then started the next. So 245 Stormwind doodads at ~0.10s each = ~25 seconds, *serial*, no
matter how parallel the MPQ reads were. That single job is now a pool
(`_preloadJobs`, cap `MaxConcurrentPreloads = 12`). `WarmNextPreload` keeps the pool topped
up — up to twelve `PrepareModel` tasks in flight on the worker pool — then finalizes each one
whose CPU prepare has finished (enqueue its GPU upload, then build and cache the model once
the upload lands). The worker pool caps real concurrency at roughly cores-minus-two; the pool
just keeps it saturated. Result: the 245 models decode in parallel (~3s) instead of one at a
time (~25s), which fixes both the initial load *and* `.tele`.

## Tuning knobs

- `Start.TileRadius` (config) — resident terrain ring; default 1 (a 3×3 block).
- `DoodadDemandRadius` (Program.cs, = draw distance + 100 yd) — how far out the curtain waits
  for doodads. Smaller = shorter hold, fewer props on the reveal.
- `MaxConcurrentPreloads = 12` (DoodadRenderer.cs) — how many models decode at once.
- `LoadPhaseWatchdogSeconds = 30f` (Program.Loading.cs) — a phase force-advances after this so
  a hang can never leave the curtain stuck forever.
- `LoadFadeSeconds = 0.5f` — curtain fade-out length.
- The `DrainWarm` per-frame pass count (48) in Program.Loading.cs.

## Before / after

- Before: window frozen for the full terrain+building+collision build, then ~25s of doodads
  popping in serially. `.tele` the same.
- After: a loading screen from the first frame; curtain up roughly 5–8s (terrain + buildings
  ~2.4s, doodads now ~3–4s, collision ~0.4s); it lifts onto a populated zone. `.tele` streams
  its doodads in ~3s.

## Still to do

- **Per-object appear-fade.** As you *walk*, newly streamed doodads still pop in rather than
  fading. benilla fades every streamed object in over 2s (cubic). That is the last piece to
  make movement-time streaming feel benilla-smooth; the loading curtain already fades, but
  individual objects do not yet.
- **WMO preload pool.** `WmoRenderer`'s preloader is still single-job like the doodad one used
  to be. It has few models so it is rarely the bottleneck, but the giant `stormwind.wmo`
  (~1.2M triangles, one model, ~1.3s) can't be split by pooling — that one is inherent. If WMO
  warm feels slow on a dense zone, apply the identical pool pattern.
