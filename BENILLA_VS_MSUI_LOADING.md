# Why benilla loads instantly and MSUIClient doesn't — full proof

Date: 2026-07-26. Method: seven agents read the two codebases in full; every load-critical
claim below was then re-verified by hand against the staged source. Citations are `file:line`
from the code as it stands today (benilla = Rust/Bevy reference client; MSUIClient = your
C#/Silk.NET client). Nothing here is paraphrased memory — it is the current bytes.

Files analysed: 68 benilla source files (`crates/benilla*`), 46 MSUIClient files
(`MSUIClient/…` + `SYSTEM_STREAMING.md`, `PLAN_07/08`, `PROJECT_HANDBOOK.md`).

## Current implementation status — 2026-07-29

The historical comparison below records the original blocking loader and is retained as the reason
for the redesign. The current tree now has the incremental loader and native loading curtain in
`Program.Loading.cs` / `Engine/LoadingScreen.cs`. The presentation contract is:

- Loading owns the whole screen through the final curtain fade. The ImGui pass returns before drawing
  gameplay unit frames, buffs, action/bag bars, open panels, settings, or developer windows.
- The map backdrop still resolves through `Map.dbc -> LoadingScreens.dbc -> BLP`.
- The backdrop is fitted to the reference 4:3 canvas with black pillar/letterboxing instead of being
  stretched to the host aspect ratio.
- The synthetic blue progress track has been removed. Build 5875 draws exactly two MPQ assets:
  `Interface\Glues\LoadingBar\Loading-BarFill.blp`, followed by
  `Interface\Glues\LoadingBar\Loading-BarBorder.blp`.
- Their verified canvas rectangles are preserved: border `(left=.20, bottom=.05, width=.60,
  height=.05)` and fill `(left=.2375, bottom=.0625, max width=.525, height=.025)`.

---

## 0. The one-sentence answer

benilla is **not** parsing or decoding assets faster than you. Both clients CPU-decode every
BLP to full 32-bit RGBA (proof in §5). benilla feels instant because of **when** and **where**
the work runs, not how fast each file is:

> benilla shows a loading screen, clears it the moment the *ground ring under the player* is
> resident (not the whole zone), and streams the buildings, doodads, props and NPCs in **after**
> that — every heavy build capped to **4 ms per frame**, all decode on **background threads**,
> every late arrival hidden behind a **2-second fade**.
>
> MSUIClient builds the **entire** resident zone — terrain + every building + collision —
> **synchronously inside the GL `Load` callback, before the first frame is ever presented**,
> with **no loading screen at all**. You watch a frozen window until it finishes, then the
> doodads visibly pop in for several more seconds with nothing covering them.

Same streaming idea on both sides. benilla puts a curtain in front of it and a time-budget
around it; you do it in the open, all at once, on the thread that draws.

---

## 1. The two enter-world sequences, traced from code

### benilla (Rust/Bevy) — clear-early, stream-and-fade

Fixed per-frame pipeline `Net → Input → Stream → Present`, `crates/benilla/src/schedule.rs:22-49`.
On startup or a map change:

1. **Loading screen goes up** the instant the focus tile isn't resident —
   `loading_screen.rs:284` (`if !screen.active && !progress.focus_resident`).
2. **Coarse WDL horizon fills the distance immediately** — a whole-map low-detail mesh streamed
   to radius 5 tiles (~2112 yd), 8 tiles/frame — `wdl.rs:33` (`WDL_RADIUS = 5`), `wdl.rs:37`
   (`WDL_LOADS_PER_FRAME = 8`). The horizon is never empty.
3. **Terrain streams in under a 4 ms/frame budget** — `terrain_stream.rs:62`
   (`SPAWN_BUDGET = Duration::from_millis(4)`), loop deadline at `:439`, `break` at `:523`.
   Colliders for each tile are built **off the main thread** — `terrain_stream.rs:458-471`
   (`build_collider_task(...)`), attached later by an independent `finish_colliders` system (`:252-256`).
4. **The screen clears on a minimal condition** — every wanted tile resident
   (`is_ready()` = `ready >= total`, `loading_screen.rs:85-86`) **and** collision under the
   player settled (`player_settling`, `:278`) **and** stable for 3 frames
   (`CLEAR_AFTER_READY_FRAMES = 3`, `:55`; clear at `:291-294`). It never waits for the full zone.
5. **Buildings, doodads, WMO props and NPCs stream in afterward**, registered per tile and
   spawned across later frames as their assets land, each armed with a pending appear-fade.
6. **Late arrivals fade in, they don't pop** — `α = t³` over 2 s — `model_fade.rs:124`
   (`APPEAR_FADE_SECS = 2.0`), curve at `:149` (`from + (to - from) * t * t * t`). The fade is
   *armed* only once the world is actually on-screen, so the first burst completes behind the
   loading screen and anything later eases up in front of you.

### MSUIClient (C#/Silk.NET) — build everything, then present

Silk raises `Load` once, before the render loop starts. `Engine/ClientWindow.cs:277`
(`HandleLoad`) → `:415` (`OnLoad?.Invoke(_gl)`). The render/​swap path `HandleRender`
(`ClientWindow.cs:531`, `OnRender?.Invoke` at `:552`) **is not reached until `Load` returns.**

`OnLoad` is your `GameLoop.Load(GL gl)` — `Program.cs:341` — which runs, one blocking call after
another on the main thread:

```
Program.cs:361  PhaseComplete("MPQ mount")
Program.cs:383  _terrain.LoadAround(...)                → PhaseComplete("terrain")        :396
Program.cs:404  _wmo.LoadForTiles(...)                  → PhaseComplete("buildings")      :422
Program.cs:434  _liquid.LoadForTiles(...)
Program.cs:515  PopulateDoodads(...)                    → PhaseComplete("doodads/…")      :523
Program.cs:536  LoadCollision()                         → PhaseComplete("collision world"):537
Program.cs:566  new CharacterRenderer(...)              → PhaseComplete("character…")     :588
Program.cs:626  Console.WriteLine($"[game] ready in {startup.Elapsed.TotalSeconds:F2}s …")
```

There is **no loading screen**. A tree-wide search finds exactly one mention — an aspirational
comment that promises one that doesn't exist: `World/Wmo/WmoRenderer.cs:949`
`/// <summary>Warm the initial preload ring while the loading screen is expected.</summary>`.
Until `Program.cs:626` prints "ready", the window shows its last-cleared contents / the OS
"not responding" state. Then the update loop starts and the **deferred doodads** stream in — with
nothing covering them and no fade — which is the "loads doodads and stuff" you watch happen.

---

## 2. Head-to-head — the mechanisms that decide it

| Dimension | benilla (fast) | MSUIClient (slow) | Net effect |
|---|---|---|---|
| **Loading screen** | Yes — up when focus tile not resident. `loading_screen.rs:284` | **None.** Only a comment "while the loading screen is expected". `WmoRenderer.cs:949` | benilla hides latency; you expose it |
| **When is the first frame shown?** | After the **ground ring + collision under the feet** are resident (`ready>=total` + `player_settling` false + 3 frames). `loading_screen.rs:85-86, 278, 291-294` | After the **entire** zone (terrain+buildings+collision) is built. `Program.cs:341→626`, `ClientWindow.cs:415→531` | You wait for 10× more work before anything draws |
| **Rest of the world** | Streams in *after* the screen clears, hidden by a 2 s `α=t³` fade. `model_fade.rs:124,149` | Doodads stream after (good) but with **no screen and no fade** — visible pop-in. `DoodadRenderer.cs:925` | Same streaming, but yours is on-camera |
| **Per-frame budget at INITIAL load** | 4 ms/frame for tiles+placements; 8 clutter builds/frame; 8 WDL/frame. `terrain_stream.rs:62`, `clutter.rs:148`, `wdl.rs:37` | **None on the initial build.** The 6 ms budget only runs in `Update`, after `Load`. `Program.cs:1349` | benilla can't hitch >1 frame; you hitch for the whole load |
| **Where decode/parse runs** | Off-thread on Bevy's IO task pool (widened to 8), one task per file. `main.rs:300, 316` | On a worker pool too — **but consumed synchronously** (see next row). `AssetWorkerPool.cs:16,22` | Your threads exist but don't shorten wall-clock at load |
| **WMO building load** | Async asset; groups spawn when the handle lands. | **Blocks the main thread** per building: `GetResult()` + `while(!StepModelLoad(waitForUpload:true))`. `WmoRenderer.cs:1267-1268, 1464-1465` | A city = hundreds of buildings loaded serially, each waited on |
| **Terrain upload** | In the async loader / render thread. | **Inline on the main render GL:** `Adopt(gl, Upload(gl, gl, prepared))`. `TerrainTile.cs:107`, loop `TerrainRenderer.cs:185,196` | Terrain never uses your upload worker at load |
| **Collision build** | Off-thread task, screen held until it's ready under the player. `terrain_stream.rs:458-471, 252-256` | **Synchronous BVH on the main thread:** `AppendCollision` ×2 then `_collision.Build()`. `Program.cs:964-975` | ~0.5–1 s of BVH on the critical path |
| **MPQ read concurrency** | `Chain` reads open a fresh handle per call — genuinely concurrent across the 8 IO threads. `benilla-assets/src/lib.rs:50` | **All reads + zlib/PKWARE decompress serialized under one lock.** `MpqMount.cs:80` | Your worker pool bottlenecks on one read lock |
| **BLP → GPU** | CPU-decode to `Rgba8Unorm`, **authored mips uploaded verbatim** (no GPU regen). `benilla-assets/src/blp.rs:88`, mips note `:4,26` | CPU-decode to `Rgba8`, **mip 0 only, then `GL.GenerateMipmap` per texture.** `Texture.cs:69,74`; callers pass mip 0 `AdtTerrainReader.cs:814,841` | You throw away the on-disk mips and pay to rebuild them |
| **Texture/model cache** | Global handle-dedup via `AssetServer` across the whole world. `benilla-assets/src/lib.rs:1` | **Per-renderer dicts; no cross-renderer cache; terrain re-decodes shared BLPs per tile.** `TerrainTextures.cs:79` | You decode the same grass/rock BLP ~9× |
| **Residency radius vs view** | 5×5 tiles (~1066 yd) **larger** than the 777 yd far-clip, so tiles are resident before revealed. `assets/mod.rs:391`, `view.rs` | 3×3 tiles (`TileRadius=1`). Smaller working set — and still slower. | The architecture, not the workload, is the gap |

---

## 3. Ranked root causes (each proven from both sides, with the fix)

### #1 — The whole zone is built synchronously before the first frame, with no loading screen
**This is the headline. Everything else is a multiplier on it.**

MSUI: `game.Load` (`Program.cs:341`) runs terrain → buildings → liquid → doodads → collision →
character as blocking calls (`:383, :404, :434, :515, :536, :566`) and only then prints
`[game] ready` (`:626`). The window can't present a frame because `HandleRender`
(`ClientWindow.cs:531`) runs strictly after `HandleLoad` (`:415`) returns. No loading screen
exists (`WmoRenderer.cs:949` is the only, false, reference).

benilla: the loading screen clears on `ready >= total` (the small desired ring) + collision under
the player + 3 frames (`loading_screen.rs:85-86, 278, 291-294`) — **not** the whole zone — and
streams the rest behind it.

**Fix:** put a loading screen (even a black quad + your `[game] ready` progress) in front, and
drive the initial build through the *same* budgeted, incremental path you already use for tile
crossings, clearing the screen on a minimal "ground + collision under player" condition. This is
literally your own **PLAN_08 D2** ("budgeted, resumable adoption… the single highest-value
structural change") — written, not yet built (`SYSTEM_STREAMING.md §5A.14`, `PLAN_08 §5`).

### #2 — WMO buildings load serially, main-thread-blocked, despite the worker pool
In a city this dominates. `WmoRenderer.LoadForTiles` loops buildings (`:735`) and `ResolveModel`
blocks on both the worker parse and every fenced GPU upload:

```
WmoRenderer.cs:1267   try { job.Worker.GetAwaiter().GetResult(); } catch { }
WmoRenderer.cs:1268   while (!StepModelLoad(job, waitForUpload: true)) { }
WmoRenderer.cs:1464-65  if (waitForUpload && !job.Upload.IsCompleted) job.Upload.GetAwaiter().GetResult();
```

Your own class comment names the symptom (`WmoRenderer.cs:238`): *"A city WMO can contain hundreds
of group files… caused the multi-second freezes seen while walking into Stormwind."* Your docs
measured buildings at **27.2 s → 0.9 s** after the MPQ-mount fix and a single WMO ring-warm at
**61 ms of a 187 ms** crossing (`SYSTEM_STREAMING.md §3`, `PROJECT_HANDBOOK §3.20`).

benilla never blocks on a building: groups spawn when the async handle resolves, and appear-fade
hides the arrival.

**Fix:** don't `GetResult()` on the load path. Enqueue the building, let the group meshes appear
across frames (your `WarmNextPreload`/`GpuUploadWorker` already do this for the streaming path —
route the *initial* buildings through it too), and fade them in.

### #3 — The initial load has no per-frame time budget
benilla caps every heavy build: 4 ms/frame for tiles+placements (`terrain_stream.rs:62`), 8
clutter builds/frame (`clutter.rs:148`), 8 WDL/frame (`wdl.rs:37`). A cold start therefore spreads
across frames and can never beach-ball. Your 6 ms budget exists but only inside `Update`
(`Program.cs:1349`), which starts *after* `Load`. The initial `LoadAround`/`LoadForTiles`/
`LoadCollision` are un-budgeted. Your `PLAN_08 §4.2` even quotes the reference's
`budgetMs = 8` and notes "MSUI has no equivalent."

**Fix:** the budgeted cursor from PLAN_08 D2, applied from frame 1 rather than only at crossings.

### #4 — Terrain bypasses your upload worker and uploads inline
`TerrainTile.Load` does decode **and** GPU upload on the same `gl` — note both args are the render
context: `TerrainTile.cs:107` `return Adopt(gl, Upload(gl, gl, prepared));`, driven by the
synchronous `LoadAround` loop `TerrainRenderer.cs:185,196`. The off-thread terrain path
(`PreparePreloadAsync` → `_uploads.Enqueue`) exists but is only used for streaming, not the
initial ring.

**Fix:** route the initial tiles through `PreparePreloadAsync`/`GpuUploadWorker` like the
streaming ring already does.

### #5 — Collision BVH is built synchronously at load
`Program.cs:964-975`: `_wmo.AppendCollision` + `_doodads.AppendCollision` (each `Vector3.Transform`
×3 per triangle on the main thread), then `_collision.Build()`. Your docs measure the sibling
expansion at **~509,000 triangles → 92.9 ms** and a from-scratch BVH at **0.4–1.2 s**
(`SYSTEM_STREAMING.md §3.2, §3.5`). You already moved the *runtime* rebuild to `Task.Run`
(`Program.cs:1050`) — the **initial** build is still on the critical path.

benilla builds colliders off-thread and holds the loading screen until the collision under the
player is ready (`terrain_stream.rs:458-471`, `loading_screen.rs:278`).

**Fix:** build the initial BVH on the same worker you already use at runtime; gate "playable" on
just the collision under the spawn point, not the whole world.

### #6 — Every MPQ read + decompress is serialized under one lock
`MpqMount.cs:80` wraps all archive extraction in `lock (_readLock)`, and the zlib/PKWARE
decompression is managed C# run inline inside that lock (`MpqArchive.cs`, `PkwareExplode.cs`). So
even though decode runs on a worker pool, the **read half of every file contends on one lock** —
your 8 workers can't actually read 8 files at once. benilla's `Chain` opens a fresh OS handle per
read (`benilla-assets/src/lib.rs:50`), so its 8 IO threads read concurrently.

**Fix:** `MpqArchive` already uses positioned I/O (its own header says concurrent `ReadFile` is
safe, `MpqArchive.cs:25`). Drop the global lock to a per-archive lock, or lock only the hash-table
lookup and read/decompress outside it.

### #7 — Redundant decode: no shared cache, and mips thrown away then rebuilt
Two compounding wastes that make you decode far more than benilla for the same zone:

- **No cross-renderer / cross-tile texture cache.** WMO, doodad, foliage each keep their own
  `Dictionary<string,Texture?>` (`WmoRenderer.cs:324`, `DoodadRenderer.cs:235`,
  `FoliageRenderer.cs:82`), and terrain has *no* path cache — `TerrainTextures.Prepare` re-decodes
  every tile's MTEX list (`TerrainTextures.cs:79`), so a shared `ElwynnGrass01.blp` is decoded once
  per tile (~9×) and again per renderer. `ReadFileFromMpqs`/`ReadBlpPixels` are themselves uncached
  (`AdtTerrainReader.cs:139, 834`). benilla's `AssetServer` handle-dedup decodes each unique asset
  once for the whole world (`benilla-assets/src/lib.rs:1`).
- **Mip 0 decoded, full chain regenerated on the GPU.** Every caller passes mip 0
  (`AdtTerrainReader.cs:814, 841`), then `Texture.cs:74/110` calls `GL.GenerateMipmap` per texture —
  discarding the BLP's on-disk mip pyramid and paying GPU time + ~33% extra VRAM to rebuild it.
  benilla uploads the authored mips verbatim, no regen (`benilla-assets/src/blp.rs:4,26,88`).

**Fix:** one process-wide `path → Texture` cache in front of all four renderers; upload the BLP's
stored mip levels instead of `GenerateMipmap`.

---

## 4. What is NOT the reason (so you don't chase the wrong thing)

- **BLP decode cost is a wash.** benilla also CPU-decodes every BLP to full RGBA8 — it uses the
  `texpresso` codec to expand DXT1/3 blocks into a `w*h*4` buffer (`benilla-blp/src/lib.rs:254-267`)
  and uploads `Rgba8Unorm`/`Rgba8UnormSrgb` (`benilla-assets/src/blp.rs:88`). Neither client does
  GPU-compressed (BC/DXT) passthrough — a repo-wide search finds **zero** `TextureFormat::Bc` in
  benilla and **zero** `CompressedTexImage` in MSUI. If anything, DXT passthrough is a win *available
  to you* that benilla also left on the table. Do not conclude "rewrite the decoder."
- **You are not missing multithreading.** You have `AssetWorkerPool` (2–8 workers) and a dedicated
  `GpuUploadWorker`. The problem is they're **bypassed or synchronously awaited at the one moment
  that matters** — the initial `Load`. The infrastructure is right; the wiring at cold start is not.
- **The tile-crossing hitch is already fixed** — your docs show 187 ms → not measurable after four
  measured changes, and the doodad cull 55.8 ms → 0.3 ms (`SYSTEM_STREAMING.md §3.3, §5A.15`). The
  "many seconds" the user feels is the **cold zone load**, a different path from crossings.
- **Doodad demand-streaming is already the right call** — `DoodadRenderer.cs:925` returns null under
  `DemandStreaming` so props don't block the load (they cut a ~19.6 s startup drain, per
  `PROJECT_HANDBOOK §3.32`). The remaining problem is only that the pop-in is *visible* because
  there's no screen/fade over it.

---

## 5. The fix path, mapped to benilla as the reference

In priority order (each maps to a benilla mechanism and, where it exists, your own PLAN):

1. **Add a loading screen** driven by a residency fraction, cleared on "ground ring + collision
   under spawn" — benilla `loading_screen.rs` is the whole blueprint. Immediately converts "frozen
   for N seconds" into "curtain for N seconds," and lets 2–4 hide the rest.
2. **Route the initial world build through a budgeted, resumable cursor** (your PLAN_08 D2), 4–8
   ms/frame — benilla `terrain_stream.rs:62`. This is the structural cure; it caps every future
   load cost automatically.
3. **Stop `GetResult()`-blocking on WMO** at load; let buildings appear across frames like your
   streaming path already does — benilla never blocks (`WmoRenderer.cs:1267-1268` is the line to kill).
4. **Add a 2 s appear-fade** on streamed-in geometry so post-clear pop-in reads as "settling in,"
   not "still loading" — benilla `model_fade.rs:124,149`.
5. **Build the initial collision BVH off-thread** (you already do it at runtime, `Program.cs:1050`);
   gate playability on collision under the spawn only — benilla `terrain_stream.rs:458-471`.
6. **One global texture cache + upload authored BLP mips** (drop `GenerateMipmap`) — removes the ~9×
   terrain re-decode and the mip regen — benilla `AssetServer` dedup + authored mips.
7. **De-serialize MPQ reads** (per-archive lock, positioned I/O you already have) so your 8 workers
   actually read in parallel — `MpqMount.cs:80` is the choke.

Doing 1 + 4 alone (a curtain + a fade) would make the load *feel* close to benilla even before the
deeper 2/3/5 work lands, because the user's complaint is as much "I can see it loading" as raw
wall-clock. Doing 2 is what actually makes it *be* fast.

---

## Appendix — verified anchor list

Every line below was read directly from the staged source during this analysis.

benilla:
- `crates/benilla/src/loading_screen.rs:55` `CLEAR_AFTER_READY_FRAMES = 3`; `:85-86` `is_ready = ready>=total`; `:278` `player_settling`; `:284` screen-up; `:291-294` clear.
- `crates/benilla/src/terrain_stream.rs:62` `SPAWN_BUDGET = 4ms`; `:439` deadline; `:523` break; `:252-256` `finish_colliders`; `:458-471` `build_collider_task` off-thread.
- `crates/benilla/src/clutter.rs:148` `CLUTTER_BUILDS_PER_FRAME = 8`.
- `crates/benilla/src/wdl.rs:33` `WDL_RADIUS = 5`; `:37` `WDL_LOADS_PER_FRAME = 8`.
- `crates/benilla/src/main.rs:300` "parses synchronously on Bevy's IO task pool"; `:316` `max_threads: 8`.
- `crates/benilla/src/model_fade.rs:124` `APPEAR_FADE_SECS = 2.0`; `:149` `α = t³`.
- `crates/benilla/src/assets/mod.rs:391` `tile_radius` default 2 (5×5).
- `crates/benilla-assets/src/blp.rs:88` `Rgba8Unorm`; `:4,26` authored mips verbatim.
- `crates/benilla-blp/src/lib.rs:254-267` `texpresso` DXT→RGBA8 CPU decode. No GPU BC anywhere.

MSUIClient:
- `MSUIClient/Program.cs:341` `Load(GL gl)`; `:383,404,434,515,536` synchronous phase calls; `:626` `[game] ready in …`.
- `MSUIClient/Engine/ClientWindow.cs:415` `OnLoad?.Invoke(_gl)`; `:531` `HandleRender`; `:552` `OnRender?.Invoke` (after Load returns).
- `MSUIClient/World/Wmo/WmoRenderer.cs:735` building loop; `:949` "loading screen is expected" (only mention); `:1267-1268, 1464-1465` main-thread block.
- `MSUIClient/World/TerrainTile.cs:107` `Adopt(gl, Upload(gl, gl, prepared))`; `TerrainRenderer.cs:185,196` sync loop.
- `MSUIClient/Program.cs:964-975` `AppendCollision` + `_collision.Build()` synchronous; `:1050` `Task.Run` (runtime rebuild only).
- `MSUIClient/Formats/MpqMount.cs:80` `lock (_readLock)`; `:67` "held open".
- `MSUIClient/Engine/Texture.cs:69` `TexImage2D Rgba8`; `:74,110` `GenerateMipmap`. No `CompressedTexImage` anywhere.
- `MSUIClient/Formats/AdtTerrainReader.cs:814,841` `GetPixels(blpData, 0, …)` (mip 0 only); `:139,834` uncached reads.
- `MSUIClient/World/Doodads/DoodadRenderer.cs:925` `if (DemandStreaming) return null` (deferred — the one thing already right).
- `MSUIClient/Engine/AssetWorkerPool.cs:16` `Clamp(ProcessorCount-2,2,8)`; `GpuUploadWorker.cs:79` upload thread, `:103-114` fence+flush+spin.

Corroborating docs (your own): `SYSTEM_STREAMING.md §3, §5A.14, §6`; `PLAN_08 §4.2, §5`;
`PROJECT_HANDBOOK.md §3.20 (terrain 4.7→0.4s, buildings 27.2→0.9s, doodads 26.9→13.1s), §3.24, §3.32`.
Note: your docs' reference client is "WoWee", not benilla — the two happen to reach the same
budgeted-streaming conclusion.
