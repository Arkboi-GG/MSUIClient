# MSUI Client — Project Handbook

**A native C# client for World of Warcraft 1.12.1 (build 5875), talking to a private VMaNGOS server.**

Version: Draft 24 — 2026-07-25
Supersedes: Draft 23 (same day; a documentation-sync pass. Draft 23 was written
before the exterior-lighting session and before the streaming investigation ran
to ground, so its stop point still said "GPU timings have not been read yet" and
"run the two A/B tests" — both are done, and six further measured runs are
recorded in SYSTEM_STREAMING.md §5A. Draft 24 also registers the fifth system
doc, SYSTEM_EXTERIOR_LIGHTING.md, adds PLAN_09 and PLAN_10 to the §1.2 map,
corrects the git-state block, and brings §1, §4, §5.2 and §5.3 back in line with
the files actually on disk. **No new engineering claims are made here — every
number in this draft is carried over from a system doc or a plan.**)
Supersedes: Draft 22 (previous day; the streaming/performance session — the hitch
recorder was built (PLAN_07), the tile-crossing freeze was measured at 187 ms and
eliminated, and SYSTEM_STREAMING.md was extracted under the §1.2 rule. The felt
micro-stutter is NOT solved; §3.27 and SYSTEM_STREAMING.md §5 state the open
problem honestly)
Supersedes: Draft 21 (same day; §0, §1, §4, §5.2, §5.3 and §7.1 had fallen behind four systems that shipped after it — water Draft 2, WMO interior lighting, doodad lighting and foliage — and behind the foundation/DevTools layer that is now code. Draft 22 reconciles them and moves the per-system detail out under the §1.2 rule)
Supersedes: Draft 20 (same-ish day; the first water/liquid system landed — see SYSTEM_WATER.md — and the docs began splitting one-system-per-file per §1.2, so system detail now lives in SYSTEM_*.md and this handbook is trending toward a lean index of cross-cutting truth)
Supersedes: Draft 19 (same day; warm lighting retune, ALWAYS_DRAW-interior impostor classification, per-group inside test, collision-BVH occlusion cull, portal-chunk parsing, and the live in-game tuning HUD + middle-click group picker)
Supersedes: Draft 18 (same day; reconciles the handoff/status sections with all runtime-tested renderer, streaming, teardown and Stormwind LOD work)
Supersedes: Draft 17 (same day; superseded by Stormwind distance-shell classification and near/far swapping)
Supersedes: Draft 16 (same day; superseded by antiportal rejection, camera-invariant two-sided lighting and context-safe GPU teardown)
Supersedes: Draft 15 (same day; superseded by the demand-streaming and Intel upload-fence pass)
Supersedes: Draft 14 (previous day; superseded by the atmosphere/visibility test pass and truthful draw-submission diagnostics)
Supersedes: Draft 13 (same day; superseded by the end-of-session runtime verdict and cold-start handoff)
Supersedes: Draft 12 (same day; superseded by shared-context GPU uploads and asynchronous terrain residency)
Supersedes: Draft 11 (same day; superseded by true worker preparation and asynchronous collision BVHs)
Supersedes: Draft 10 (same day; superseded by draw/preload separation and staged M2 textures)
Supersedes: Draft 9 (same day; superseded by outer-ring M2 and WMO-interior preloading)
Supersedes: Draft 8 (same day; superseded by staged WMO groups and corrected MOGP visibility data)
Supersedes: Draft 7 (same day; superseded by WMO preload residency and MOMT blend handling)
Supersedes: Draft 6 (same day; superseded by moving world residency and locomotion-speed work)
Supersedes: Draft 5 (same day; superseded by complete startup profiling and display pacing work)
Supersedes: Draft 4 (same day; superseded by player-renderer v1 and grounded locomotion work)
Supersedes: Draft 3 (same day; superseded by characters, animation, gear and the DBC layer)
Supersedes: `MSUI_CLIENT_DESIGN.md` (browser-era architecture, abandoned — see §2)

---

> **If you are a fresh assistant picking this project up cold, read §0, §1, §3, §4 and §10 before touching anything.**
> §3 lists facts established empirically that cost real hours. Several are counter-intuitive and several were got wrong once — sometimes twice — before being got right. Re-deriving them will waste a session.
> §10 lists exactly what to ask Nico for, and why.
> **§1.2 is the documentation map.** Detail for a specific system (water, terrain, WMO, …) increasingly lives in its own `SYSTEM_<NAME>.md`. When you are working on one system, read *this handbook's* cross-cutting ground truth (§3) plus that system's doc — not every section here. Loading the whole handbook to change one system is the context waste §1.2 exists to stop.

---

## 0. Orientation — what this is in one page

Nico runs a private VMaNGOS server (WoW 1.12.1 vanilla) plus **MangosSuperUI**, an ASP.NET Core admin and content-creation web app he built for it. This project is a **separate, standalone game client** — a real playable client, not a viewer — written in C# on Silk.NET/OpenGL.

**Why it exists.** Two goals converged:

1. A long-standing wish to see WoW 1.12 rendered in a painterly, hand-painted art style. Owning the renderer makes that a shader variant instead of a fight with a platform.
2. A working multiplayer client for his own realm — quest, kill, dungeon, raid, craft — where his AiBot fleet, real 1.12 clients, and this client all coexist.

**Core design stance.** The client reads WoW's own files directly (MPQ → BLP/ADT/M2/WMO/DBC) and will speak the genuine 1.12.1 network protocol. No asset server, no bake step, no format conversion, no coordinate conversion. The server is unmodified.

### Current state (2026-07-25)

Elwynn Forest renders and is walkable, entirely from the client's own MPQs, with no server and **no vmaps**. On top of Draft 3's world, there is now a **character**:

- A skinned Human Male M2 walks, runs, strafes, jumps and stands, driven by real vanilla animation clips
- Gear works: Tier 1 warrior plate paints into the body atlas, switches geosets, and mounts helm, pauldrons, Quel'Serrar and a shield on skeleton attachment points
- `ItemDisplayInfo.dbc` and `CharSections.dbc` are read straight out of the MPQs
- Third-person camera with the vanilla left/right mouse split

**Six systems have landed since Draft 3's world, and each owns its own doc
(§1.2). Do not read the summary here and act on it — open the doc.**

- **Water/liquid** — open-world MCLQ lakes, rivers, ocean, slime and magma,
  surfaced with the client's own animated liquid BLP frames, per-type routing and
  a live tuning window. Drawn after the character, depth-tested but not
  depth-written, so submersion and the underwater overlay read correctly.
  **SYSTEM_WATER.md** (Draft 2). Note the Draft 1 → Draft 2 reversal: 1.12 water
  is a dark, near-opaque *textured* surface whose motion is the texture scrolling.
  The Gerstner geometry waves of Draft 1 were flattened deliberately.
- **WMO interior lighting** — MOCV read back faithfully, `FixVertexColors`
  reproduced, `VertexColorScale = 2.0`, the `0x2000 / 0x48` interior gate.
  **SYSTEM_WMO_INTERIOR_LIGHTING.md**. *Signed off — do not re-open casually; it
  is the reference the doodad system was built to match.*
- **Doodad lighting** — `MODD.color` established as Blizzard's baked
  per-placement interior light (not the tint the wiki claims), gated through
  MODR, plus the M2 Unlit flag so lanterns stay lit in dark rooms.
  **SYSTEM_DOODAD_LIGHTING.md**. Its one invariant: *a barrel matches the floor
  it stands on.*
- **Foliage / ground effects** — the authored `MCLY.EffectId` →
  `GroundEffectTexture` → `GroundEffectDoodad` chain, driven by the MCNK cell
  layer map and the no-doodad mask rather than by alpha sampling.
  **SYSTEM_FOLIAGE.md**. Its one test: *grass must not creep onto the Northshire
  cobblestone.*
- **Streaming, residency and frame performance** — the moving ring, tile
  crossings, the worker pools, the GPU upload context, the hitch recorder, and
  the full frame-time breakdown. **SYSTEM_STREAMING.md** (Draft 1). The
  tile-crossing freeze is dead (187 ms → not measurable) and the doodad cull is
  down from 55.8 ms to 0.3 ms, but a **pacing bug survives every elimination**
  — read §5A before believing any older number, including §3.27's.
- **Exterior lighting (sky, fog, ambient, sun)** — `Light.dbc` →
  `LightParams` → `LightIntBand`/`LightFloatBand` resolved by a light probe and
  applied; a five-band screen-space sky pass replaces the flat clear.
  **SYSTEM_EXTERIOR_LIGHTING.md** (Draft 1). Its headline: *the by-eye ambient
  retune of 2026-07-23 rejected `(0.42, 0.50, 0.60)` and the authored value at
  noon is `(0.408, 0.510, 0.604)`* — §3.28 and §3.35's constants are superseded
  by data.

- **Settings UI / the Escape menu** — `GameMenuFrame` and a Video Options
  frame drawn from Blizzard's own `Interface\` art at Blizzard's own layout
  numbers, backed by `settings.json` with presets and live apply. **This is
  the first thing that works with `DevTools: false`**, and the preference
  half of the HUD moved into it — the Water and Foliage tuning windows are
  gone. **SYSTEM_SETTINGS_UI.md** (Draft 1). Its headline: *the real 1.12 UI
  ships as FrameXML inside interface.MPQ, so a UI question is a read, not a
  guess* — two rounds of plausible recall lost to one extraction.

**The foundation/DevTools layer is no longer a proposal — it is code.**
`FOUNDATION_PLAN.md` and `PLAN_01`–`PLAN_06` specified a shared-language layer;
vantages, visibility reason codes, the scene dump and the visibility override
database now exist in `Engine/Vantage.cs`, `Engine/VisibilityOverrides.cs`,
`World/Wmo/WmoRenderer.cs` and `Program.DevTools.cs`. `vantages.json` holds two
saved viewpoints and `dumps/` holds a real captured dump. **Use it — the paired
artifact (vantage + screenshot + dump) is the working agreement now, see §5.3.**

**Not started:** networking, painterly pass. WMO liquid (canals, fountains,
indoor pools) is also not done — see SYSTEM_WATER.md §5.

**Tile streaming is implemented and partially runtime-validated:** a moving 3×3
terrain ring follows the player; WMO/doodad placement lists and collision are
rebuilt on tile transitions while parsed models, textures and GPU buffers stay
cached. Doodad residency is distance-bounded so far-away furniture inside a
large WMO no longer dominates Northshire startup. Runtime WMO/M2 reads, parses
and BLP decodes now run on workers; collision BVHs are built off-thread and
swapped in only when ready. Terrain, WMO and M2 GPU transfers now run through a
dedicated shared OpenGL context rather than blocking the render context.
Demand-streamed M2s, worker-side outer-ADT parsing and upload-context fences
removed the worst stalls, and the doodad cull that dominated crossing frames is
fixed. **The remaining jitter is a frame-pacing bug, not a workload problem** —
GPU, uploads, first-touch, driver flush, GC and every one of our own phases have
each been eliminated by measurement. **SYSTEM_STREAMING.md §5A is the only
current account; do not re-derive it and do not trust §3.27's older numbers.**

**Player renderer v1 is complete:** the character, appearance, animation, gear,
attachments and render-state handling all render correctly. The earlier texture
flicker is resolved; §9 records the closed investigation.

**Atmosphere/visibility and profiling are runtime-tested:** terrain, WMOs,
doodads, the character, attached gear, the sky clear
colour and fog now share one evaluated time-of-day environment. The HUD exposes
noon/sunset/night presets, automatic time cycling, sun and ambient strength,
fog start/end, fog-bound draw rejection and far-plane coupling. Terrain, WMO
and doodad diagnostics now report CPU submission time, actual draw calls and
submitted triangles; WMO diagnostics also report visible spatial groups. The
panel separates CPU submission measurements from delayed non-blocking GPU
timer-query results.

**Doodad instancing and true 1x MSAA are runtime-tested:** the
legacy path submitted one indexed draw for every material batch of every visible
M2 instance. The new default path uploads the camera-relative matrices for all
visible copies of one model to an instance buffer and issues one instanced draw
per model batch. The HUD `GPU instancing` checkbox switches between the two
paths live. Multisampling was the only first-pass graphics control with a clear
cost on Iris Xe: roughly 5–7 FPS in Trade District. The default is now a true
1x framebuffer; the live switch remains useful only when the startup allocation
has multiple samples.

### Stop point — read this first in the next session

> ## STOP POINT, 2026-07-25 (end of the exterior-lighting session)
>
> **First thing: `git log --oneline -3` and `git status`.** Head should be
> `5699c44 cont` on `main`, eight commits, with `PLAN_10_WMO_PORTALS.md` the
> only modified file. If the tree looks empty of the lighting work, you are on
> the wrong machine or the wrong branch.
>
> ### Two threads are open. Do not braid them.
>
> **Thread A — exterior lighting. Shipped, numerically verified, photographically
> unverified.** `Light.dbc` and its three band tables are read, resolved by
> position and time, and applied; the sky is a real five-band gradient. Every
> value in SYSTEM_EXTERIOR_LIGHTING.md was read out of Nico's own MPQs by the
> probe. **What is still ours and still a guess: the three sky band *heights*
> and the sun *direction*** (§4 of that doc). Those are the most likely reason
> the sky still reads slightly off, and **they cannot be settled without a
> `refs/` capture** — `refs/` still holds only a README. Not done at all:
> skyboxes, clouds, weather, and the ocean/river colours (bands 13–16) that
> `SYSTEM_WATER.md` currently invents.
>
> **Thread B — the streaming pacing bug. Isolated, not fixed.** Six measured
> runs are in SYSTEM_STREAMING.md §5A and every hypothesis died by measurement:
> GPU frame (0.8 ms), concurrent shared-context uploads (0 in flight),
> first-touch (0), driver flush at the last GL call (`imguiFlush 0.1`), GC
> (2.8 ms of 86), `glBufferData` orphaning (0.0), draw submission (0.1). The
> doodad cull *was* real — 55.8 ms → 0.3 ms, 41–46 ns/instance — but §5A.15
> records honestly that **the flat-bounds change is not yet proven to be the
> cause**; model count fell at the same time. The clean A/B is both toggle
> states at the same spot, back to back.
>
> What survives is a 34 ms frame with **zero work, zero allocation, zero
> collections, zero uploads and an idle GPU** (§5A.16). The question is no longer
> "what were we doing" but **"were we running at all"**, and
> `threadMCyclesPerMs` is already instrumented to answer it: **~4–5 M/ms means a
> driver busy-wait; <1 M/ms means descheduled.** Those have opposite fixes.
> **Read that one number before writing any streaming code.** And note §5A.17:
> vsync off is a diagnostic, not the fix.
>
> ### One defect found by this doc sync, not yet fixed
>
> **Authored exterior lighting is gated behind `_config.DevTools`.**
> `UpdateLightProbe` early-returns when DevTools is off, and it holds the only
> call to `WorldAtmosphere.SetAuthored` — so a non-DevTools run silently reverts
> to the invented constants Thread A just replaced. It is a FOUNDATION_PLAN §12
> seam violation and it is small: move the resolve into core and leave the probe
> observing. §4 has the detail.
>
> ### Two traps this session set
>
> - **`[stream-budget]` numbers and the 65 ms `model-finalize` hitch are
>   artefacts of the vsync throttle** (§5A.1). A timer around a GL call on a
>   throttled driver measures the throttle. Re-measure uncapped before
>   optimizing anything they point at — including §3.27's `terrain 13.2`, which
>   is 1.9 uncapped.
> - **PLAN_10 D1 is built and traversal is not.** `Program.Portals.cs`, the
>   camera-group readout and `DumpPortalGraph` exist; nothing culls by portals
>   yet and the 120-yard interior rule is still in place (§3.26).
>
> Read **SYSTEM_STREAMING.md** before touching streaming, residency or
> performance, and **SYSTEM_EXTERIOR_LIGHTING.md** before touching anything
> atmospheric. They have every number.

**Git state as of Draft 24, verified 2026-07-25.** Branch `main`, **8 commits**:

```
5699c44 cont                                              <- HEAD
721ceac Lighting work continued (ext)
d7ce1fb interior lighting + render optimization work
dcb27f0 Streaming: kill the tile-crossing freeze; build the hitch recorder
b0837a9 WMO interior lighting, doodad lighting, foliage + handbook Draft 22
1292c91 proper water
af00fc4 water, more render work, etc
ac0b071 intiail engine work
```

The Draft 22/23 note listing a large uncommitted diff is **resolved** — that
work is in `b0837a9` and later. The working tree now carries exactly one
modification:

```
 M PLAN_10_WMO_PORTALS.md
```

which is the §3/D4 rewrite recorded in that plan. **Do not reset, checkout,
clean, or replace broad files when starting cold.** `_to_delete/` is untracked
and must never be swept into a `git add -A`.

- Last build: **success, 0 warnings, 0 errors**.
- Iris Xe testing is substantially improved: Trade District is roughly 54–60
  FPS with true 1x MSAA and the former total streaming freezes have become small
  intermittent stutters. VSync can cap the observed result at 60 FPS.
- The fast-start/demand-streaming path is runtime-proven. A measured startup was
  about 22 seconds before the final no-drain M2 policy; capture a fresh
  `[game] ready in` value before making another startup change.
- Shared-context uploads, bounded CPU preparation, worker-side outer ADT parsing,
  collision BVHs and fence/flush publication all run in game. Do not reintroduce
  render-thread parsing, `glFinish`, or WMO-wide embedded-doodad fanout.
- Antiportal groups are rejected before upload/draw/collision. World lighting is
  camera-invariant via `gl_FrontFacing`; MSUI still has no cast-shadow pass.
- `GameLoop` now tears down through `ClientWindow.OnClosing` while the main GL
  context is current, fixing the `GpuFrameProfiler.Dispose` NoContext exception.
- Stormwind's Cathedral and entrance silhouette groups are now classified from
  MOGN/MOGI and swapped at 196 yards. Runtime-check the default-on `Swap
  distance-only city shells` control: visible on approach, absent inside.
- **Exterior lighting is data-driven as of 2026-07-25.** The invented
  `DayFog`/`NightAmbient`/`NoonSun` constants and the invented `FogStart 350 /
  FogEnd 777` are replaced by `Light.dbc` values resolved per position and time,
  and a five-band screen-space sky pass draws before the world. The Draft 20
  by-eye warm retune below is **superseded** — see SYSTEM_EXTERIOR_LIGHTING.md
  §5.3 for why it was wrong and why it was not careless. Still ours and still a
  guess: the three sky band heights and the sun direction.
- **Draft 20 visibility/lighting session (§3.35), lighting part superseded.**
  The warmer/dimmer §3.28 retune is now overridden by the authored data (see the
  bullet above); the visibility work below stands. The impostor classification now catches
  ALWAYS_DRAW *interior*-flagged low-poly groups — the real Stormwind impostors
  (`Cathedral of Light` v=96, `Old Town`, `Taventrance16`) — which the old
  `!indoor` guards discarded, so the cathedral now shows on approach. Shell
  classification runs live (HUD `Impostor max verts` + `ReclassifyShells`). The
  inside/outside test is per-group containment with a HUD `Inside margin`;
  interior cull and shell near-guard are HUD sliders too. A collision-BVH
  occlusion cull (`OcclusionCulling`, off by default, 8-corner test) hides
  exterior groups fully behind geometry. MOPV/MOPT/MOPR + group portal refs are
  parsed, and PLAN_10 D1's instrument (camera-group readout, portal quad draw,
  `DumpPortalGraph`) is built — **but no traversal consumes them yet.**
- **Known ceiling, do not re-litigate blindly.** The SW entrance keep
  (`stormwind.wmo [46] 'thief01'`, exterior, 17.5k verts) stays visible across an
  *open* courtyard because nothing blocks it — occlusion only culls when every
  corner is blocked, and WoWee disables portal culling outdoors on purpose.
  Matching the real client's aggressive cell-based hiding of visible-but-far
  exterior geometry needs portal traversal applied OUTDOORS (which WoWee avoids
  for popout). That is the only remaining lever and it is experimental; occlusion
  is the correct tool for genuinely-blocked cases.
- **In-game tools exist now — use them before theorising (§5.3).** Middle-click
  does triangle-accurate group picking (`[pick]` + HUD); the whole impostor/
  occlusion panel is live sliders; `[wmo-vis]`/`[doodad-cull]` are HUD readouts
  (console traces off by default). Since Draft 23 there are three more:
  the **hitch recorder** (`Program.Hitch.cs`, ring buffer + auto-vantage +
  per-phase/GPU/GC/thread-cycle breakdown), the **light probe** (`data` vs
  `applied` deltas, all 18 colour bands, convention scoring) and the **portal
  panel** (`Program.Portals.cs`, camera group + portal quad draw).
- **`_to_delete/` is still on disk and is still growing** — it now holds
  `program_current.txt`, `wmorenderer_current.txt`, `_auth_Program.cs`,
  `_auth_FoliageRenderer.cs` and three PNGs. Device tools cannot delete; Nico
  removes the folder by hand. Nothing in it is needed. It is also untracked, so
  it must not be swept into a `git add -A`.
- **`Program.cs` reverted once and was restored (Draft 21).** The disk copy had
  rolled back to `class GameLoop` (not `partial`) with no `_liquid`, DevTools,
  vantage or override wiring, while the new partial files were present — a
  combination that does not compile. **If `GameLoop` ever shows as non-partial
  again, the file has been reverted**; the good copy is the one with
  `partial class GameLoop` and the liquid / DevTools / vantage / override wiring.
  This is a cheap thing to check first when a build breaks inexplicably.

---

## 1. Repository layout

Fully standalone. No project reference to MangosSuperUI.

```
MSUIClient/                          <- repo root, open MSUIClient.sln here
├── MSUIClient.sln
├── SETUP.md
├── PROJECT_HANDBOOK.md              this file
├── setup-gamedata.ps1               creates GameData/, copies MPQs (robocopy)
├── setup-vmaps.ps1                  optional; -Wsl mode for the travel machine
├── .gitignore                       MUST contain GameData/ — see §8.6
├── .gitattributes                   CRLF for C#/shaders/config, LF for markdown
├── SYSTEM_*.md                      one system, one doc — see §1.2
├── FOUNDATION_PLAN.md / PLAN_0x_*.md / PLAN_TEMPLATE.md
├── tools/mpqpeek/                   read the client's own MPQs (Python, stdlib, read-only)
│                                    find/cat/stat/png/cells - SYSTEM_SETTINGS_UI.md §7
├── vantages.json                    saved reproducible viewpoints (§5.3)
├── dumps/                           scene dumps, one per vantage capture
├── refs/                            real 1.12 client captures, same vantage names
├── _to_delete/                      scratch; UNTRACKED, delete by hand, never add
├── GameData/                        GITIGNORED, several GB
│   ├── Data/                        the WoW 1.12.1 .MPQ archives
│   └── vmaps/                       optional; collision comes from client geometry
└── MSUIClient/                      project folder
    ├── Program.cs                   entry point + partial GameLoop + ImGui HUD
    ├── Program.DevTools.cs          partial GameLoop: vantages, dump, overrides,
    │                                TuningState, the water tuning window
    ├── Program.Hitch.cs             partial GameLoop: the hitch recorder's HUD,
    │                                ring readout and record writing (PLAN_07)
    ├── Program.LightProbe.cs        partial GameLoop: "what the DBCs say" panel
    │                                <- SYSTEM_EXTERIOR_LIGHTING.md §6
    ├── Program.Portals.cs           partial GameLoop: camera-group readout and
    │                                portal quad draw (PLAN_10 D1) — TOOLING ONLY
    ├── ClientConfig.cs
    │
    ├── Engine/
    │   ├── ClientWindow.cs          window, GL, main loop, INPUT (see §3.13)
    │   ├── Camera.cs                orbit camera, Yaw vs OrbitYaw (see §3.12)
    │   ├── AssetWorkerPool.cs       bounded 2–8 worker CPU preparation pool
    │   ├── GpuUploadWorker.cs       hidden shared GL context + upload queue
    │   ├── GpuFrameProfiler.cs      non-blocking GL_TIME_ELAPSED rings (§3.30)
    │   ├── HitchRecorder.cs         frame-spike ring + record writer + console
    │   │                            tee <- SYSTEM_STREAMING.md §1
    │   ├── Vantage.cs               capture/restore a named viewpoint (§5.3)
    │   ├── VisibilityOverrides.cs   hand-authored show/hide DB, consulted first
    │   ├── Shader.cs                compile/link/uniform cache + SetVec4Array
    │   └── Texture.cs
    │
    ├── Player/
    │   └── CharacterController.cs   movement, gravity, sweep/slide/step-up
    │
    ├── World/
    │   ├── AdtCache.cs
    │   ├── TerrainTile.cs / TerrainTextures.cs / TerrainRenderer.cs
    │   ├── WorldAtmosphere.cs       the one evaluated sun/ambient/fog/sky —
    │   │                            evaluate + apply <- SYSTEM_EXTERIOR_LIGHTING.md
    │   ├── ExteriorLighting.cs      Light.dbc chain resolve, zone blending, the
    │   │                            dbc->world convention <- same doc §1-§3
    │   ├── SkyRenderer.cs           five-band screen-space sky <- same doc §4.1
    │   ├── LiquidRenderer.cs        <- SYSTEM_WATER.md
    │   ├── FoliageRenderer.cs       <- SYSTEM_FOLIAGE.md
    │   ├── Wmo/WmoRenderer.cs       <- SYSTEM_WMO_INTERIOR_LIGHTING.md
    │   ├── Doodads/DoodadRenderer.cs <- SYSTEM_DOODAD_LIGHTING.md
    │   ├── Collision/{CollisionWorld,CollisionBatch,CollisionDebugRenderer,
    │   │              VmapCollisionLoader}.cs
    │   └── Units/                   <- ALL CHARACTER WORK LIVES HERE
    │       ├── M2Animator.cs        clip baking, bone matrices, the two strafe yaws
    │       ├── CharacterRenderer.cs skinned draw, geosets, texture slots, appearance
    │       ├── CharacterEquipment.cs body atlas composite + geoset rules
    │       └── AttachedItemRenderer.cs helms, shoulders, weapons, shields
    │
    ├── Formats/
    │   ├── Mpq/{MpqCrypto,MpqArchive,PkwareExplode,MpqArchiveWriter}.cs
    │   ├── MpqMount.cs              opens all archives once — §3.20, critical
    │   ├── BlpDecoder.cs
    │   ├── AdtTerrainReader.cs      ADT + MCLQ + the general MPQ file read
    │   ├── WmoReader.cs / M2Reader.cs / M2TextureParser.cs
    │   ├── DbcReader.cs             WDBC + ItemDisplayInfo + CharSections
    │   │                            + GroundEffectTexture/GroundEffectDoodad
    │   │                            + Light/LightParams/LightIntBand/LightFloatBand
    │   └── VmapFormat.cs
    │
    └── Shaders/                     ALL PURE ASCII, NO BOM — see §8.5
        ├── terrain.vert / terrain.frag
        ├── wmo.vert / wmo.frag          WmoRenderer ONLY — see §5.2
        ├── doodad.vert / doodad.frag    forked from wmo.*; DO NOT re-merge
        ├── grass.vert / grass.frag      foliage: wind sway, distance fade
        ├── sky.vert / sky.frag          fullscreen triangle, 5-band gradient
        ├── water.vert / water.frag
        ├── underwater.vert / underwater.frag
        ├── character.vert / character.frag   skinned
        ├── attached.vert                pairs with character.frag
        └── collision.vert / collision.frag
```

**Every renderer now owns its own shader pair.** That was not true through
Draft 21 and the sharing that used to exist has been deliberately broken; §5.2
says why, and SYSTEM_DOODAD_LIGHTING.md §5 says why the doodad fork in
particular must stay a fork.

**Three `Program.*.cs` partials are developer tooling, not core** —
`Program.DevTools.cs`, `Program.Hitch.cs`, `Program.LightProbe.cs` and
`Program.Portals.cs` all sit behind the FOUNDATION_PLAN §12 seam: *core decides,
the dev layer observes.* Nothing in them culls, lights or streams anything. If a
change to one of these files alters what is on screen, the seam has been broken.

### 1.1 Where each file's responsibility ends

| File | Owns | Does NOT own |
|---|---|---|
| `Program.cs` | Startup order, game loop, HUD, cross-system diagnostics | Rendering internals, parsing |
| `Program.DevTools.cs` | Vantage save/load, scene dump, override editing, `TuningState`, tuning windows | Any visibility or lighting *decision* — it reads and reports them |
| `Vantage.cs` | Serializing/restoring position, camera, atmosphere and every toggle | Deciding what any toggle means |
| `VisibilityOverrides.cs` | The curated show/hide DB and its reason code | The heuristics it overrides |
| `ClientWindow.cs` | GL context, loop, raw input, mouse capture | What gets drawn |
| `AssetWorkerPool.cs` | Bounded CPU concurrency and render-thread headroom | Asset parsing rules, GL |
| `GpuUploadWorker.cs` | Dedicated shared GL context, ordered upload queue, completion barrier | Visibility, residency policy |
| `Camera.cs` | View/projection, frustum, the Yaw/OrbitYaw split | Input handling, movement rules |
| `CharacterController.cs` | Movement, gravity, ground resolution | What the world is made of |
| `WorldAtmosphere.cs` | The single evaluated sun/ambient/fog/sky every renderer reads, and applying it | Resolving *which* light applies, per-system lighting rules (MOCV, MODD) |
| `ExteriorLighting.cs` | Resolving the `Light.dbc` chain for a position + time, zone falloff blending, the dbc→world convention | Drawing anything; deciding how a renderer uses the values |
| `SkyRenderer.cs` | The screen-space sky gradient pass | Fog, ambient, sun — those are `WorldAtmosphere`'s |
| `HitchRecorder.cs` | The frame ring, the spike trigger, writing a record | Deciding what a phase *means* — phase brackets live at their call sites |
| `LiquidRenderer.cs` | Open-world liquid surfacing and the underwater pass | Terrain heights, MCLQ parsing |
| `FoliageRenderer.cs` | Ground-effect scatter, curation, grass draw | Terrain heights, DBC table layout |
| `M2Animator.cs` | Clip baking, bone matrices, leg and torso yaw | GL, textures, which clip to play |
| `CharacterRenderer.cs` | Skinned draw, geoset visibility, texture slots, appearance, clip choice | Item data, attachment placement |
| `CharacterEquipment.cs` | Body-atlas composite, geoset rules from ItemDisplayInfo | GL, drawing |
| `AttachedItemRenderer.cs` | Item M2s on attachment points | The character's own mesh |
| `Formats/*` | Pure parsing, no GL, no game logic | Rendering, gameplay |

**Rule:** nothing in `Formats/` may reference Silk.NET or GL. `M2Animator` holds no GL either, and could move there if it ever needs to.

### 1.2 Documentation map — one system, one doc

**The rule (pinned).** Each major system gets its own `SYSTEM_<NAME>.md` at the
repo root. That file is the single source of truth for how that system works: its
goal, what is implemented, its ground-truth facts, its files, how to tune and test
it, and what is not done yet. **This handbook stays a lean index of cross-cutting
truth** — the coordinate system, conventions, startup order, working agreements —
plus this map. System detail belongs in the system doc, not here.

**Why.** This handbook is already large enough that loading it to change one
system wastes a whole context window on sections that change is unrelated to. A
water fix should pull in `SYSTEM_WATER.md` and §3's invariants, nothing else.
Splitting keeps each read small and each doc owned by exactly one concern — the
same "where each responsibility ends" discipline §1.1 applies to code.

**Conventions.**
- File name: `SYSTEM_<NAME>.md` at the repo root (matches the flat `PLAN_*.md`
  layout already here). `docs/` can come later if the root gets crowded; the rule
  is one-system-one-file, not the exact folder.
- Scope: one system per doc. If a doc starts describing two systems, split it.
- Cross-cutting invariants that many systems depend on (coordinates, camera-relative
  rendering, the shader ASCII rule, streaming residency) stay in the handbook §3
  and are *referenced* by the system docs, not copied into them.
- Foundation / dev-tooling docs keep their existing names (`FOUNDATION_PLAN.md`,
  `PLAN_0x_*.md`); they already follow the spirit of this rule.

**The map.**

| Doc | Covers | Status |
|---|---|---|
| `PROJECT_HANDBOOK.md` (this) | Cross-cutting ground truth, repo layout, startup order, history, working agreements, this map | Living index |
| `SYSTEM_WATER.md` | Open-world liquid: MCLQ lakes/rivers/ocean/slime/magma, the client's own animated liquid BLPs, per-type routing, underwater overlay, the water tuning window | **Written (Draft 2)** — Draft 1's procedural Gerstner surface was **reversed**; read the doc before assuming waves |
| `SYSTEM_WMO_INTERIOR_LIGHTING.md` | Interior walls/floors/ceilings: MOCV, `FixVertexColors`, the `x2` scale, the interior gate | **Written — signed off, do not re-open casually** |
| `SYSTEM_DOODAD_LIGHTING.md` | WMO furniture: `MODD.color` as a baked light, MODR interior gate, Unlit materials, the instance-light path | **Written** |
| `SYSTEM_FOLIAGE.md` | Ground effects: the MCLY -> GroundEffectTexture -> GroundEffectDoodad chain, cell layer map, no-doodad mask, holes, per-kind curation | **Written** |
| `SYSTEM_EXTERIOR_LIGHTING.md` | Sky, fog, ambient, sun from `Light.dbc` + `LightParams` + the two band tables; the light probe; the screen-space sky pass | **Written (Draft 1)** — 2026-07-25. **Supersedes §3.28's and §3.35's invented constants.** Numerically verified, photographically unverified |
| `SYSTEM_STREAMING.md` | Moving residency ring, tile crossings, worker pools, GPU upload context, the hitch recorder, and the frame-time breakdown | **Written (Draft 1)** — extracted 2026-07-25, then extended through §5A with six measured runs. Carries the fixed defects AND the still-open pacing bug. **Read §5A before trusting any older number, including §3.27's** |
| `SYSTEM_SETTINGS_UI.md` | The Escape menu: the Game Menu and Video Options frames, the `settings.json` model with presets and composites, and the Blizzard-art skin layer (`WowSkin`/`UiFont`) all three are drawn with | **Written (Draft 1)** — 2026-07-25. Carries the 1.12 frame geometry read out of the FrameXML that ships in `interface.MPQ`, and the five ImGui traps this cost. **§1.1's `.blp` rule and §2.3's clip-rect rule are the two that will bite the next UI** |
| `PLAN_07_HITCH_RECORDER.md` | The automatic frame-spike recorder: ring buffer, console tee, auto-vantage | **Built and proven** — caught the freeze on the first walk. Superset now in `SYSTEM_STREAMING.md` §1 |
| `PLAN_08_INCREMENTAL_RESIDENCY.md` | Per-tile ownership, budgeted adoption, and WoWee's five mechanisms quoted from source | D1 done; **D2/D3 outstanding and still the structural answer to `resid`** (SYSTEM_STREAMING §5A.14); D4/D5 dropped with reasons |
| `PLAN_09_EXTERIOR_LIGHTING.md` | The reasoning, test protocol and verified 1.12 schemas behind exterior lighting | **Built.** The system doc is the current truth; this plan keeps the schemas (§11) and the argument |
| `PLAN_10_WMO_PORTALS.md` | Portal traversal from MOPV/MOPT/MOPR: the doorway-chain rule, the `side`-bit convention, the approach case | **D1 (the instrument) is BUILT** — `Program.Portals.cs`, camera-group readout, `DumpPortalGraph`. **Traversal is NOT built**; the 120-yard rule still stands (§3.26). §3/D4 were rewritten 2026-07-25 |
| `PLAN_13_INSTANCES.md` | Loading in and out of dungeons: `Map.dbc`, the WDT, and the two structurally different kinds of instance map | **Stage 1 (the readers) is BUILT** — `Formats/WdtReader.cs`, `MapTable`, `Program.Instances.cs`. **Stage 2 (travel to terrain maps) is BUILT and unrun** — `AdtCache.SetMap`, `TerrainRenderer.UnloadAll`, `LiquidRenderer.UnloadAll`, `WmoRenderer.ResetForMapChange`, `TravelTo`. Stage 3 (global-WMO maps) specified, not built. §1's headline: **Deadmines is a TERRAIN map, not one WMO** |
| `PLAN_14_PARTICLES.md` | M2 particle emitters: the layout derived from the bytes, and what the dungeon portal actually is | **Stage 1 (parse + panel) BUILT** — `M2ParticleEmitter`, `Program.Particles.cs`. §3's headline: **the emitter stride is 504, not the 476 every reference quotes**, and 18% of the archives' 15,214 M2s carry emitters. Stages 2–3 not built |
| `FOUNDATION_PLAN.md` + `PLAN_01`–`PLAN_06` | Shared-language layer: vantages, scene dump, reason codes, override DB, DevTools seam, HUD/TuningState | **Written AND built.** Plans 01–04 and 06 are code; 05's `TuningState` exists in `Program.DevTools.cs` but the HUD is not fully reorganized. Its own §11 index is correct |
| `SYSTEM_WMO_PORTALS.md` | Traversal algorithm and the `side`-bit convention as ground truth | Not written — PLAN_10 §8 makes extracting it part of that plan's definition of done |
| `SYSTEM_TERRAIN.md` | ADT terrain: MCNK/MCVT tessellation, texturing/splat, tile placement | Planned extraction from §1.1/§3 |
| `SYSTEM_WMO.md` | WMO buildings: groups, visibility, impostors, occlusion (**lighting is split out into the two lighting docs; portals will be their own doc**) | Planned extraction from §3.24–3.35 |
| `SYSTEM_CHARACTER.md` | M2 skinning, animation, gear, attachments, appearance | Planned extraction from §3.4–3.19 |
| `SYSTEM_COLLISION.md` | Client-geometry collision, BVH, sweep/slide/step-up | Planned extraction |
| ~~`SYSTEM_ATMOSPHERE.md`~~ | Time-of-day light, fog, sky, visibility coupling | **Cancelled — delivered as `SYSTEM_EXTERIOR_LIGHTING.md`.** Do not create a second atmosphere doc; §3.28's *visibility/draw-count* half still belongs in this handbook |

"Planned extraction" means the content still lives in this handbook's §3 for now;
pull it into its own doc the next time that system is worked on, and replace the
§3 subsection with a one-line pointer. Do not do a big-bang split — extract a
system's doc when you next touch that system, so each split is verified by real
work.

---

## 2. History — why it is native

The project began as a **browser client** (TypeScript, three.js, WebGL). It was abandoned on 2026-07-21 for one reason: **almost none of what it forced us to build was game code.**

```
Browser:  ADT -> C# parse -> GLB write -> HTTP -> JS parse -> GPU
Native:   ADT -> C# parse -> GPU
```

All format knowledge survived and is still exactly correct. The format readers were **copied** from SuperUI with namespaces changed; they are independent and will drift, so a genuine parser bug must be fixed in both places.

---

## 3. Ground truth — established facts, do not re-derive

### 3.1 Coordinate system — there is no conversion anywhere

WoW world space throughout: **+X north, +Y west, +Z up**, orientation in radians CCW about +Z from +X.

`System.Numerics.Matrix4x4` is **row-vector**. `Shader.Set` uploads with `transpose: false`. System.Numerics is row-major in memory and GL reads those bytes as column-major, which *is* the flip GLSL needs. Transposing in C# first double-flips it and the screen shows only the clear colour while draw calls and culling all look healthy.

### 3.2 Tile indexing — the axes are swapped

```
col = floor(32 - worldY / 533.33333)     first number, from Y
row = floor(32 - worldX / 533.33333)     second number, from X
```

Northshire is tile **[col 32, row 48]**. Both `000_32_48` and `000_48_32` exist on disk. `AdtTerrainReader.ReadFromMpq` takes **(gridX = row, gridY = col)** — inverted from the filename.

### 3.3 ADT placement space — MODF and MDDF are NOT world coordinates

```
worldX = C - posZ,  worldY = C - posX,  worldZ = posY
C = 32 * 533.33333 = 17066.67
```

Linear part determinant +1, so it is a rotation, not a mirror.

### 3.4 Model vertex conventions — three arrays, two conventions

| Data | Convention | Basis needed |
|---|---|---|
| **WMO vertices (MOVT)** | Z-up | `(x,y,z) -> (x,z,-y)` |
| **M2 render vertices** | **Y-up after M2Reader** | none for a doodad |
| **M2 collision hull** | Z-up | `(x,y,z) -> (x,z,-y)` |

**A bounding-box score is structurally blind to a 180° heading error.** Calibration settles which axis is up; only looking at the screen settles which way something faces.

### 3.5 M2Reader converts EVERYTHING to Y-up at parse

Not just vertices. Normals, **bone pivots**, **translation keys**, **rotation keys** `(qx, qz, -qy, qw)`, **scale keys** `(x, z, y)`. So vertices, pivots and animation tracks all live in one consistent space and **skinning needs no basis anywhere**.

The rotation mapping deliberately diverges from WMV's `(-qx,-qz,qy,qw)`, which assembles the body correctly and then rotates every joint the wrong way.

### 3.6 The skinning maths — free inverse bind

```
rest local     = T(pivot - parent.pivot)
animated local = S(scale) * R(rot) * T(rest + translationKey)      row-vector
global         = local * parentGlobal
```

Rest translations accumulate to exactly `T(pivot)`, so **`inverseBind = T(-pivot)`** — no matrix inversion, no error.

**Consequence worth keeping:** with no clip playing, every skin matrix is the identity and the model draws in bind pose, byte-identical to a static mesh. A placement bug and an animation bug can therefore never be the same bug, and the HUD has a `Bind pose` checkbox to split them in one click.

### 3.7 A character needs the model-to-world basis explicitly

`(x,y,z) -> (-z,-x,y)` — the **linear part of `DoodadRenderer.PlacementToWorld`**. Doodads look basis-free only because ADT placement space is itself Y-up and carries the flip; a character has no ADT placement.

**Heading = Yaw + 90°.** Model +X forward maps to world `(sin h, -cos h, 0)`. Confirmed on screen. Do not revisit.

### 3.8 Bone budget — 119, not 50

**HumanMale.m2 has 119 bones.** Vanilla characters carry a full set of finger and facial joints. `M2Animator.MaxBones` is **160** and `MAX_BONES` in `character.vert` must **always move with it** — 160 × 3 vec4 = 1920 float components.

The failure mode when it was 80 is worth knowing: bones past the limit were clamped onto the last valid one, which is **invisible in bind pose** (every skin matrix is the identity there) and grotesque the moment anything animates. Bind-pose-perfect plus animation-broken means a capacity failure, not a transform failure. `BoneOverflow` now refuses to animate rather than deform.

For many units in Phase 2, the answer is a uniform buffer object: GL 3.3 guarantees 16 KB, which is 341 bones.

### 3.9 Animation clip looping is NOT flag 0x20

`M2Sequence.IsLooping` reads bit 0x20 and **that bit is not a loop flag** — it is clear on Stand, Walk and Run. Trusting it made every clip a one-shot that clamped and held: a character who walks a few steps and freezes mid-stride, still correctly posed.

Real looping lives in the repetition fields at +24/+28, which `M2Reader` skips. `M2Animator.OneShotAnimations = {37 JumpStart, 39 JumpEnd}`; everything else loops.

**Clip key selection must use the sequence's absolute timestamp window**, never `Ranges[seqIdx]` — vanilla character M2s leave Ranges as `(0, count-1)` for every sequence.

### 3.10 Strafing is a SPLIT — legs and torso at different angles

Not "turn the body" and not "turn the legs". **Both, at different angles.** Measured against the real client at roughly 90° on the legs and 60° on the torso.

WoWee's `character_renderer.hpp` carries the matching hook: `setInstanceTorsoYaw(id, deltaYawRad)` with a per-instance `torsoYawOverrideRad`. A **delta** on the torso, over whatever the body is already doing.

```
angle phi = atan2(-sideness, forwardness)      relative to facing, + is his left
model heading += phi                            legs take all of it
TorsoYaw = (TorsoFollow - 1) * phi              torso keeps TorsoFollow of it
```

`TorsoFollow` defaults to 0.66. **1.0 reproduces whole-body, 0.0 reproduces lower-body-only** — one slider spans every mode we tried.

The angle derivation is not a guess: facing = `(cos Y, sin Y)`, right = `(sin Y, -cos Y)`, so a direction at world yaw `Yaw + phi` gives `forwardness = cos phi` and `sideness = -sin phi`.

**It does not touch `state.Yaw`.** That stays the character's facing, the camera stays behind it, and a movement packet wants it in Phase 2. Only the drawn model turns.

WoWee confirms there are **no strafe clips on land**: their `LocomotionFSM::resolve` computes strafe booleans and uses them only in the SWIM case.

### 3.11 Subtree yaw — how both halves are applied

A rotation appended AFTER a bone's global transform is applied in model space, and because every child does `local * parentGlobal`, whatever is appended to a bone becomes **the rightmost factor of that bone's entire subtree**. So one append at the hips rotates hips, thighs, calves and feet and touches nothing above; a second append at the spine does the torso.

`TwistBone` (hips) resolves from per-bone `KeyBoneId == 5` (Waist) — bone 21 on HumanMale. `TorsoBone` from `KeyBoneId == 4` (SpineLow). Both validated by subtree size, and the torso is rejected if it sits *inside* the leg subtree, where the two yaws would compound instead of splitting.

**The earlier version rotated everything and cancelled at one bone. It rotated the UPPER body** — which is how the right answer was found. If you invert something and the wrong half moves, the reading names the fix.

### 3.12 Camera — Yaw is the character, OrbitYaw is the camera

`Camera.Yaw` is the **character's facing**; the controller reads it directly. `Camera.OrbitYaw` is a camera-only offset wrapped to (-π, π]; `ViewYaw = Yaw + OrbitYaw` drives where the camera sits.

- **Left drag** → `RotateView` — swings the camera without turning him, so you can walk north and look at your own face
- **Right drag** → `Rotate` — turns him and the camera together
- **Right button DOWN** → `FoldOrbitIntoFacing` — `Yaw += OrbitYaw; OrbitYaw = 0`. Turns him to where the camera was swung **without the view moving**, because the same angle simply moved from one term to the other
- **Moving** → `EaseOrbitBehind`, unless the left button is held

`FlatForward`/`FlatRight` stay on `Yaw`, so W walks the character forward rather than toward the camera.

**Keys:** A/D turn, Q/E strafe, and **holding right mouse swaps them**. Arrows turn and walk, PgUp/PgDn tilt.

### 3.13 Mouse capture must be POLLED

Deriving capture from MouseDown/MouseUp alone is unreliable three separate ways: `CursorMode.Raw` can be refused or silently stop reporting motion; switching to Raw moves the reported position into another coordinate space so the first delta is nonsense; and a MouseUp delivered while ImGui owns the mouse, or with the pointer outside the window, is never seen.

`ClientWindow.PollMouse` derives capture from `IMouse.IsButtonPressed` every frame. Events supply only the motion delta. First delta after capture discarded, deltas over 300 px dropped, Raw falls back to Hidden with a printed line.

### 3.14 Movement feel — no smoothing on the stop

The measured ground speed is smoothed on the way **up** and taken immediately on the way **down**. WoWee's `LocomotionFSM` holds a grace window past the last motion; it was copied here and Nico's verdict was that it feels awful. **Do not reintroduce it.** The strafe angle snaps home on stop for the same reason.

Ground support uses a different kind of tolerance and must not be confused with
movement-stop smoothing. `CharacterController.ResolveGround` samples collision
at the centre, then expands to eight points at 85% of the capsule radius only
when neither the centre nor terrain gives nearby support. Stair lips, fence
rails and other narrow supports therefore do not depend on one ray, while
ordinary terrain walking stays at one BVH query. A previously grounded
character that did not jump may adhere downward by
`movement.groundSnapDistance` (default 0.5 yd).

Physics becomes airborne immediately when support really disappears. Only the
**visual fall pose** is debounced: positive jump velocity selects JumpStart at
once, while an uncommanded fall must remain airborne for
`movement.fallAnimationDelayMs` (default 180 ms) before selecting Fall. This
does not keep walk/run playing after movement stops.

Movement speeds follow VMaNGOS's vanilla `baseMoveSpeed`: walk 2.5 yd/s, run
7.0 yd/s and run-back 4.5 yd/s. Backpedalling must not reuse forward run speed.
The M2 sequence header's float at `+12` is its authored `moveSpeed`; locomotion
playback divides actual displacement speed by that value (times model scale),
falling back to the controller constants only when the sequence value is absent
or invalid. This ties foot cadence to the selected clip instead of assuming
every Walk/Run/WalkBackwards animation was authored for the same nominal speed.

### 3.15 Gear is THREE mechanisms

1. **Body atlas** — chest, legs, boots, gloves, bracers, belt, tabard have no geometry. They paint into the single 256×256 skin at fixed rectangles, eight texture slots per item.
2. **Geoset variants** — the same items switch which body geosets draw, via `m_geosetGroup`.
3. **Attached models** — helm, shoulders, weapons, shields, capes are separate M2 files on attachment points.

A Tier set is not one feature.

**`ItemDisplayInfo.dbc`, 23 fields, 92 bytes** (from SuperUI's DbcService, established by dumping all fields across robes, plate, cloth, boots and gloves plus a histogram over 29,604 records):

```
[0] ID  [1-2] modelName  [3-4] modelTexture  [5] inventoryIcon
[6-8] geosetGroup  [9] spellVisualID  [10] groundModel  [11] groupSoundIndex
[12-13] helmetGeosetVis  [14-21] texture[0..7]  [22] itemVisual
```

An earlier parser used a −2 texture shift that looked right on chests because the compositor's slot map started at 2 — two errors cancelling — and it hid LegLower and Foot entirely.

**m_texture slot → region:** 0 ArmUpper, 1 ArmLower, 2 Hand, 3 TorsoUpper, 4 TorsoLower, 5 LegUpper, 6 LegLower, 7 Foot.

**Atlas rectangles** (canonical, each column sums to 256): armUpper(0,0,128,64) armLower(0,64,128,64) hand(0,128,128,32) faceUpper(0,160,128,32) faceLower(0,192,128,64) torsoUpper(128,0,128,64) torsoLower(128,64,128,32) legUpper(128,96,128,64) legLower(128,160,128,64) foot(128,224,128,32).

**Composite order is equip order**, because vanilla textures are often overlay strips: a plate belt's LegUpper strip is a buckle band meant to draw over the legplates' thigh texture.

**Geoset rules** carry their confidence from `geoset-rules.js`: boots (cat 5) and gloves (cat 4) are verified against the decompiled `GeosRenderPrep` — the client computes `BASE + geosetGroup[N]`, so `+1` in variant terms. Robes are verified against a real DBC row. Chest, pants, tabard and shoulders are pattern-matched only. **A geosetGroup of zero means "leave the default", not "hide".**

**Helm hair suppression:** `helmetGeosetVis1 != vis2` means a closed helm. Helm of Wrath 248/306 closed; Helm of Might 247/247 open. It matters because the scalp dome is baked into each hair geoset, so hiding hair for an open helm leaves a hollow above the face.

### 3.16 Texture slots are filled BY TYPE, and the types do not share a source

This is the whole of the hair-and-cape problem, in both codebases.

```
type 0  the slot names a BLP - just read it
type 1  CHAR_SKIN         the body atlas
type 2  OBJECT_SKIN       a cape or item texture
type 6  CHAR_HAIR         CharSections section 3
type 7  CHAR_FACIAL_HAIR  CharSections section 2
type 8  SKIN_EXTRA        CharSections section 4 (underwear)
```

**Pointing every empty slot at the body atlas renders plausibly and is wrong everywhere it matters** — that is "hair textures like skin", and it hides every upstream error underneath it.

**`CharSections.dbc`, 10 fields, 40 bytes:** `[0] ID [1] Race [2] Sex [3] BaseSection [4] VariationIndex [5] ColorIndex [6-8] TextureName[0..2] [9] Flags`. Sections: 0 Skin, 1 Face, 2 FacialHair, 3 Hair, 4 Underwear.

**The match keys differ per section** and getting them wrong returns a plausible row for the wrong character:

| Section | Match on |
|---|---|
| Skin | colour (skin tone) |
| Face | variation (face shape) **and** colour (skin tone) |
| Hair | variation (hair style) **and** colour (hair colour) |
| Underwear | colour |

### 3.17 The eyes are not a geoset

Most races' body skin BLP has **no eye detail at all**. Eyes come from compositing the CharSections **Face** row onto the atlas — Texture1 is the lower face, Texture2 the upper, and the upper carries the eyes.

Miss that step and the character renders blank-faced, which reads as "eyes closed" and sends you hunting through geosets for something that was never there. (SuperUI's character viewer has this exact bug, and its own comment contains the proof: Human Female and Troll Female look right only because their base BLPs happen to have eyes baked in.)

**Take the region from the DBC field the texture came out of, never from its dimensions.** Texture1 is the lower face and Texture2 the upper — that is stated, not inferred. Inferring it back from image height paints the face across the eyes.

### 3.18 Attachment points

From SuperUI's `equip.js`, established by eye:

```
 0  LeftWrist    shields mount HERE, not on the palm
 1  HandRight
 2  HandLeft
 5  ShoulderRight   ModelName2, the R file
 6  ShoulderLeft    ModelName1, the L file
11  Helm
```

Placement is free: a rigid point attached to bone *b* transforms by that bone's **skin matrix**, so `T(attachment.Position) * Skin[BoneIndex] * instanceMatrix` is the whole thing and attached models follow the animation with no second bone chain. Item M2s draw unskinned — a sword does not bend.

**Helm models are per race and gender and nothing else is.** A helm must fit the head it sits on, so vanilla ships one file per head shape with a suffix like `Helm_Plate_A_01_HuM`; shoulders and weapons have a single file each. That asymmetry is why the helm was missing while everything else mounted.

Shoulders are **two files** and both are needed.

### 3.19 BLP alpha is not always on a 0..255 scale

Some BLPs decode 1-bit alpha as **0 or 1** rather than 0 or 255. In the shader that is 0.004, which fails any sensible cut on every texel — the surface loads, textures correctly, and renders as nothing at all. Guarded at the point of use in `WmoRenderer`, `CharacterRenderer` and `AttachedItemRenderer`. **The proper fix belongs in `BlpDecoder`** and has not been done.

### 3.20 MPQ access — the startup bottleneck

`AdtTerrainReader.ReadFileFromMpqs` reopens every archive on every call. `MpqMount` opens all 15 once and is hooked in with one line:

```csharp
AdtTerrainReader.StormLibExtractor = _mpq.ReadFile;
```

Historical first-pass gains were terrain 4.7s → 0.4s, buildings 27.2s →
0.9s, doodads 26.9s → 13.1s. **Load order must stay** patches
reverse-alphabetical, then `terrain.MPQ`, `model.MPQ`, then the rest.

`MpqArchive` instances themselves are not thread-safe. `MpqMount.ReadFile` is
now safe to call from asset workers because it serializes archive extraction
and counters behind `_readLock`; `Dispose` takes the same lock. The returned
`byte[]` is independent, so parsing and BLP decoding proceed concurrently after
the short extraction lane. Do not remove the lock or mistake serialized MPQ I/O
for a failure to use multiple CPU cores.

The doodad renderer's original timer covered ADT placements only. The WMO
interior pass — 8,501 placements around Northshire in the measured run — came
after it and was therefore invisible in the reported 11 seconds. Startup now
prints a `[startup]` line for MPQ mount, render setup, terrain, buildings, all
doodads, collision, controller/spawn, character/equipment, debug setup and
alignment checks, plus the total. The interior doodad report has its own time.
ADT `.mdx`/`.mdl` aliases and WMO-interior `.m2` names are canonicalized to one
model-cache key, preventing the two passes from parsing and uploading the same
physical asset twice.

The collision diagnostic used to build and upload its complete debug mesh at
every boot despite being off by default. Its shader is still prepared at boot,
but the large CPU expansion and GPU upload now happen only the first time `C`
or `Show collision` enables it.

### 3.21 Display pacing and tearing

`window.vSync` is passed during window creation and reapplied after the OpenGL
context exists. The second assignment matters on Windows drivers that ignore a
creation-time swap-interval hint. Startup prints both the requested and the
window-reported state, and the HUD has a live `VSync (prevent tearing)` toggle.

The first reported "tearing" screenshot was actually aliasing: stair-stepped
geometry silhouettes plus noisy oblique textures. The window now requests 4x
MSAA and enables multisampling; mipmapped textures use 8x anisotropic filtering
when the driver supports it. Startup prints requested/actual sample counts and
the selected anisotropy. `render.msaaSamples` requires a restart because it is
a framebuffer creation setting; `render.anisotropy` is also applied at load.

Frustum culling uses a direct homogeneous clip-space test of all eight AABB
corners. It is deliberately conservative and rejects only when every corner is
outside the same GPU clip plane. However, live testing with both WMO and doodad
frustum culling disabled did not change the reported popping, so rejection was
empirically ruled out as its cause. The HUD toggles remain as diagnostics.

Terrain, WMOs and doodads render camera-relative, just like the character. At
Northshire's roughly -9,000 world coordinates, multiplying absolute float
positions by a view matrix with the opposite large translation loses precision
through cancellation. Thin foliage cards and nearby architectural surfaces
expose that as pieces changing depth or vanishing under tiny camera motion.
World renderers now subtract `camera.Position` before the GPU transform, use
`RelativeViewProjection`, and perform lighting/fog with the camera at zero.
Retesting the same tree and wooden arch confirmed this fixed the reported
world-geometry popping.

### 3.22 Network protocol (Phase 2 — not started)

Opcodes (1.12.1 build 5875): `SMSG_UPDATE_OBJECT=169`, `SMSG_COMPRESSED_UPDATE_OBJECT=502`, `CMSG_AUTH_SESSION=493`, `SMSG_AUTH_CHALLENGE=492`, `CMSG_PLAYER_LOGIN=61`, `SMSG_LOGIN_VERIFY_WORLD=566`, `MSG_MOVE_HEARTBEAT=238`, `CMSG_CHAR_ENUM=55`. 825 opcodes, `NUM_MSG_TYPES=828`.

UpdateFields: use `UpdateFields_1_12_1.cpp` (flat table, 324 rows), **not** the `.h`. PLAYER=1282 slots, UNIT=188.

Vanilla is **client-authoritative for movement**, which is why `CharacterController` is the real simulation and not a prediction.

**Appearance arrives as four bytes** on the character record: skin, face, hairStyle, hairColor — the exact CharSections lookup keys in §3.16.

### 3.23 Server environment

realmd `0.0.0.0:3724`, world `0.0.0.0:8085`, DataDir `/home/wowvmangos/vmangos/run/data`, client MPQs `/home/wowvmangos/wowclient/Data`. **`Anticheat.Enable=0` and `Warden.*Enabled=0` already.**

Travel machine: project on Windows at `C:\Users\nico\source\repos\MSUIClient`, vmangos fork inside WSL on the same machine.

### 3.24 Moving world residency

`start.tileRadius` is the moving terrain residency radius; 1 means a 3×3 ring.
`TerrainRenderer.SetResidency` disposes departed terrain GPU resources and loads
new edge tiles. `start.wmoPreloadRadius` defaults to 2: WMO assets and parsed
ADTs are retained for a 5×5 outer ring while only the inner 3×3 terrain ring is
uploaded and placed. The larger RAM working set is deliberate.

WMO and doodad renderers separate expensive shared assets from cheap placement
state. `ResetPlacements` clears only active instances; model parses, textures,
VAOs and buffers remain cached across crossings. Active placements are rebuilt
from the resident ADTs, which also handles objects referenced by multiple tiles
without retaining stale ownership.

At startup, every unique WMO referenced by the outer ring is fully warmed while
loading is expected. At runtime, MPQ reads, WMO root/group parsing and BLP
decoding run in a worker task. The finished CPU package then goes to the
dedicated shared-context GPU worker, which creates textures, mipmaps and buffer
objects and completes them before publication. The render thread adopts a ready
package and creates context-local VAOs only. A city root can contain hundreds
of group files, so one whole model on the render thread is unbounded work and
caused multi-second freezes. The newly active inner edge should already be a
cache hit, and expensive buildings are normally prepared at least a full tile
before they can be seen. Logs use `[wmo-preload]` and `[gpu-upload]`.

The outer ring must preload **M2 assets too**, not only WMO geometry. A measured
Stormwind transition took 6.11 seconds even though WMO placement was a 0.0-second
cache hit: outdoor doodads took 0.8 seconds and 4,303 embedded WMO doodads took
4.9 seconds as 30 new M2 models and 36 textures were first resolved. The doodad
renderer now warms MDDF models from outer-ring ADTs plus every unique MODD model
path announced by completed WMO roots. Startup drains the initial M2 queue;
runtime prepares one M2 package at a time on a worker. Logs use
`[doodad-preload]` and the HUD shows both pending queues.

M2 parsing and every BLP decode are worker-only. Render-thread finalization is
followed by one package upload on a dedicated hidden OpenGL context sharing
objects with the render context. Textures, mipmap generation, vertex buffers and
index buffers are completed there. The render thread publishes a completed
package and creates only its VAO, because VAOs are context-local containers.
Uploads over eight milliseconds use `[gpu-upload]`; that elapsed time is on the
upload thread and should no longer be a frame hitch. A remaining
`[stream-budget]` identifies render-context adoption rather than data transfer.

CPU preparation is bounded to 2–8 workers (`logical processors - 2`, clamped),
matching WoWee's worker-count shape while reserving headroom for rendering,
input and the OS. MPQ extraction remains a serialized I/O lane; parsing, mesh
generation and BLP decoding fan back out across the bounded workers afterward.

`MpqMount` serializes archive extraction behind a private lock because archive
instances share file handles and scratch state. Returned byte arrays are owned
by the caller, so parsing and decoding proceed concurrently after extraction.
Renderer disposal joins its preparation worker before the mount is closed.

`GpuUploadWorker` publishes only after a per-upload `GLsync` fence signals on
the upload thread. The former `glFinish` was removed because Intel's shared
context driver could serialize the render context and cause a full-screen
freeze even though the call was issued by the uploader.

Terrain uses the same prepare/upload/publish pipeline. A one-tile lead ring is
decoded and meshed on CPU workers, then its tileset array, alpha atlas and mesh
buffers are created on the upload context. Tiles remain unpublished and
invisible until the moving 3×3 residency ring requests them. At adoption the
render thread only wires the already-resident buffers into a VAO and installs
the precomputed height grid. A tile transition is published atomically only
after every non-missing terrain tile in its desired ring is ready; until then,
the overlapping previous 3×3 ring remains active. This replaces the measured
0.17-second boundary path that synchronously decoded and uploaded three terrain
tiles without introducing partially populated WMO/doodad residency.

Residency and visibility are separate. `render.wmoDistance` defaults to 777
yards, the original unpatched 1.12 farclip ceiling. WMO models, textures and GPU
buffers remain warm throughout the 5×5 preload ring, but each spatial group is
distance-culled before draw submission and WMO fog reaches full opacity at the
same boundary. The HUD “Building distance” slider adjusts both together, so
raising preload memory never makes distant cities visible by itself.

Outdoor and WMO-interior doodads are resolved only inside:

```
doodad draw distance + half a tile diagonal + 50 yd model margin
```

measured from the current tile centre. This guarantees that any doodad capable
of entering draw range before the next tile transition is already resident,
while excluding distant MODD furniture from huge WMOs such as Stormwind.

After a residency change, collision triangles are snapshotted into a new world
and its measured ~0.3-second BVH build runs on a worker. The controller keeps
the previous overlapping 3×3 collision world until the replacement is complete,
then swaps atomically; this prevents both a frame freeze and a temporary loss of
ground/building collision. Completion uses `[collision-async]`. The collision
debug upload is invalidated and rebuilt only after the new BVH is accepted.
Runtime transition timing is printed as `[stream]` and shown in the HUD.

### 3.25 WMO alpha follows MOMT blend mode

A BLP containing alpha does **not** mean its WMO material is an alpha cutout.
WoWee carries `MOMT.blendMode` into each draw batch: mode 0 is opaque, mode 1 is
alpha-key, and modes 2+ are transparent. Applying the global alpha cutoff to
every WMO texture with non-opaque pixels made ordinary walls and roofs look
like torn sheets. The renderer now cuts mode 1 only and renders modes 2+ in a
second blended pass with depth writes disabled.

### 3.26 MOGP header offsets and interior visibility

The vanilla MOGP group header begins with `groupName` at `+0x00` and
`descriptiveGroupName` at `+0x04`. **Flags are at `+0x08`; bounds begin at
`+0x0C`.** The first parser read flags from `+0x00` and bounds from `+0x04`, so
interior/exterior classification was actually based on a string-table offset.
Large city WMOs exposed this dramatically: distant Cathedral interior groups
appeared above Stormwind's Trade District.

> **Status correction, 2026-07-25.** MOPV/MOPT/MOPR **are parsed now**, and
> PLAN_10 D1's instrument is built (`Program.Portals.cs`: which group the camera
> is in, portal quads drawn through walls, `DumpPortalGraph` cross-checking
> MOHD's `NPortals`). **What has not changed is the culling** — the 120-yard
> rule below is still what runs. The wording "until those root chunks are
> parsed" is therefore obsolete; the correct statement is *"until traversal
> consumes them."* **PLAN_10_WMO_PORTALS.md is the live plan**, and its §10
> lists exactly what in this section dies when traversal ships.

Full indoor WMO visibility is portal traversal through MOPV/MOPT/MOPR. Until
traversal consumes those root chunks, the renderer uses per-group frustum culling
and draws ordinary interior groups only within 120 yards of their transformed
AABB.
Stormwind's approach silhouettes are a separate authored distance-LOD system
handled by §3.34, not portal cells. Portal traversal should eventually replace
the ordinary-interior heuristic; it should not replace the near/far shell swap.

**Updated verdict, 2026-07-23:** the corrected MOGP offsets were necessary but
not sufficient. The floating Cathedral was identified as an authored distance
shell and is now handled by MOGN/MOGI classification plus the 196-yard swap in
§3.34. The 120-yard AABB rule remains only a temporary approximation for other
interior groups.

The eventual portal implementation is sketched below. **PLAN_10 supersedes this
sketch where the two differ** — in particular PLAN_10 D4 corrects step 2: a
WMO's *own* exterior groups (Stormwind's streets are groups) do seed traversal,
and only WMO-to-WMO traversal across the open world is out of scope.

1. Parse root portal vertices (`MOPV`), portal descriptors (`MOPT`) and
   portal-to-group relations (`MOPR`), preserving relation side/orientation.
2. Determine the camera's current WMO group/cell when inside a WMO; when
   outside, seed traversal from visible exterior groups.
3. Traverse only portals facing/intersecting the camera view and clip the child
   frustum through each portal polygon.
4. Submit exterior groups normally and interior groups only when reached by
   traversal. Keep per-group frustum and draw-distance tests as secondary
   rejection, not as the visibility authority.
5. Compare WoWee's WMO visibility path before inventing data layouts or portal
   semantics. Do not add a Stormwind model-name exception or lower 120 yards
   until the real traversal exists.

### 3.27 Streaming performance — measured history and current state

The optimization work progressed through three distinct states. Keep them
separate when reading old logs:

1. **Synchronous assets:** a Stormwind transition took 6.11 seconds, dominated
   by first-time M2 and embedded-WMO doodad resolution.
2. **Worker CPU preparation, render-thread GPU finalization:** the boundary
   fell to 0.17 seconds and collision BVH construction moved off-thread, but
   every texture/mesh finalization logged almost exactly 14–17 ms. That
   refresh-interval signature identified the OpenGL context/driver as the
   remaining repeated hitch. A representative collision build was 0.24 seconds
   off-thread over 580,263 triangles.
3. **Current code: demand-streamed M2s + bounded CPU pool + shared GL upload
   context + asynchronous terrain:** Nico reports much better FPS and small
   intermittent stutters instead of full-screen freezes. A captured startup was
   22.6 seconds before the final no-drain M2 policy; the current exact startup
   time still needs a clean measurement.

Do not use the earlier `[stream-budget]` spam to judge the current code. The
next run must correlate visible hitches with:

- frame time or a short rolling frame-time spike log;
- `[gpu-upload]` (upload-thread time, not automatically a frame stall);
- `[stream-budget]` (render-thread adoption, expected to be rare now);
- `[stream]` (atomic residency publication);
- `[collision-async]`;
- WMO and doodad queue depth.

> **§3.27 is superseded by SYSTEM_STREAMING.md (2026-07-25).** That doc is the
> single source of truth for streaming and frame performance now, including the
> parts of this section that are still true. What follows is the first
> measurement pass, kept because its method is worth reading; the outcome and
> the open problem are in the system doc.

**MEASURED, 2026-07-24 — the guesswork above is over.** The hitch recorder
(PLAN_07, `Engine/HitchRecorder.cs`) caught the felt stutter on the first walk.
Two consecutive runs, same tile crossing, same verdict:

```
[hitch] hitch-32-49-2: 172 ms frame at [32,49] (-9067,-43,88) -> residency
[hitch]   update 169.0 (move 0.1 resid 168.8 preload 0.0)  render 2.0
          present 0.3  gui 0.5  input 0.0  unaccounted 0.4
```

**`UpdateWorldResidency` is the whole hitch: 168.8 of 172 ms, on the main
thread.** Render is 2 ms. Present is 0.3 ms. So the four suspects that were
ranked first above are all *cleared* — it is not upload packaging, not driver
contention behind fences, not steady-state draw cost, and not the unmeasured
window/swap boundary. It is one synchronous method.

Two facts inside that number, both of which cost time to see:

1. **`[stream] ... ready: 0.06s` under-reports the crossing by ~3x.** Its
   `Stopwatch` starts at `Program.cs:504`, *after* `QueuePreload` and
   `PreloadReady` have already run. The missing ~109 ms is in
   `TerrainRenderer.QueuePreload` (`TerrainRenderer.cs:263`), whose doc comment
   says it prepares tiles "on CPU workers" — but `adts.Get(col,row)`,
   `BuildHeightGrid` and `BuildHoleGrid` (lines 271-278) all run on the calling
   thread, and only `TerrainTile.Prepare` reaches a worker. A cold ADT on the
   entering edge is a full MPQ read, decompress and parse in the render thread.
   **Do not trust `[stream] ready` as the cost of a crossing.**
2. The remaining ~60 ms is inside the timer: `ResetPlacements` +
   `LoadForTiles` + `PopulateDoodads` (6,360 placements rebuilt) +
   `BeginCollisionBuild`'s main-thread triangle snapshot (~500k triangles).

**Also visible in the same log and not yet a named hitch:** `[collision] from
client geometry: 41 building(s), 394,123 solid triangles, 800,497 detail
excluded` repeats roughly once a second while doodads stream in. Each repetition
re-walks ~1.17M triangles on the main thread to feed an off-thread BVH build.
The BVH is async; **the snapshot that feeds it is not.** That is the most likely
source of the sub-threshold stutter, and it is cheap to test — raise the
recorder's threshold slider down to ~40 ms and walk the same route.

Fix order follows the measurement, not the old ranking: move the ADT
fetch/height/hole work in `QueuePreload` off the render thread first (largest,
most contained), then the placement rebuild and collision snapshot. Re-measure
with the recorder after each — same route, same threshold, diff the numbers.

### 3.28 Atmosphere, visibility and truthful draw counts

> **The lighting half of this section is superseded by
> SYSTEM_EXTERIOR_LIGHTING.md (2026-07-25).** The colours and fog distances
> described here as tuned constants are now read from `Light.dbc` and its band
> tables; `WorldAtmosphere` still *evaluates and applies* them, but it no longer
> *invents* them, and `SkyRenderer` now draws a real gradient where this section
> assumes a flat clear. **The visibility / draw-count half below is still
> current and stays here** — it is cross-cutting, not atmospheric.

`WorldAtmosphere` is the single live source for sun direction/colour/intensity,
ambient colour/intensity, fog colour/start/end and sky clear colour. Every world
renderer and the character/attached-item renderers receive the same evaluated
values before a frame draws. Time-of-day lighting can be disabled independently
from fog.

Fog and visibility are deliberately separate test switches. `Draw distance
fog` changes shader output only. `Stop submitting past fog` also gives terrain,
WMO groups and doodads the fog end as a secondary visibility ceiling. Existing
building and doodad distances remain their own tighter limits. `Match camera far
plane to fog` moves the projection far plane to fog end plus a small guard band.
This separation proves whether an FPS change came from appearance or less draw
submission.

The HUD's renderer timing is **CPU draw-submission time**, measured around the
render calls. It is not GPU execution time. WMO counts are now visible instances,
visible spatial groups, material-batch draw calls and submitted triangles;
doodads report visible instances, draw calls and triangles. If CPU submission
is low while frame time stays high, consult the non-blocking OpenGL timer-query
results before optimizing the CPU path.

### 3.29 Structural renderer gap found in the WoWee comparison

MSUI's old doodad renderer grouped instances by model only to reduce VAO binds;
it still called `DrawElements` for every batch of every instance. WoWee groups
visible opaque M2s by model and LOD, writes transforms/fade/bone offsets to an
instance SSBO and draws the whole group with one instanced indexed draw per
batch. It also has GPU frustum/distance culling, optional temporal Hi-Z
occlusion, adaptive M2 distance, and M2 LOD selection around 40/80/150 yards.

MSUI now implements the first and highest-leverage part for static doodads with
OpenGL instanced attributes. Remaining structural gaps, in priority order after
the A/B runtime result, are WMO portal/material batching, chunk-level terrain
culling, M2 LOD selection, then occlusion culling. WoWee's current WMO portal
path is intentionally conservative for outdoor city groups, so do not assume a
straight copy alone will solve Trade District performance.

### 3.30 Non-blocking GPU and update profiling

After the first Trade District test, disabling 4x multisampling recovered about
5–7 FPS while fog, visibility distance, doodad instancing and the other live
controls barely moved the result. This rules out draw distance and CPU doodad
submission as the primary limiter in that scene. Do not infer the next renderer
rewrite from FPS alone.

The startup default is now a true 1x framebuffer (`render.msaaSamples = 1`).
The live checkbox only disables multisample rasterization on an already-created
4x framebuffer, so it does not remove all allocation/resolve cost and its 5–7
FPS gain is a lower bound. Prefer a later FXAA/SMAA-style post-process pass over
making 4x MSAA the default again.

`GpuFrameProfiler` uses four rings of `GL_TIME_ELAPSED` queries. It polls only
available results and never waits for the GPU. The HUD reports smoothed delayed
GPU milliseconds for terrain, WMO, doodad, character and debug passes. The
measured sum excludes clear, ImGui and presentation. `GameLoop.Update` also
reports movement/collision, residency, preload adoption, character update and
camera-collision CPU time.

Interpretation: GPU time near the full frame time identifies a rendering
bottleneck and the largest pass names it. Low GPU time with high update time is
a simulation/streaming bottleneck. Low GPU and update time at a slow frame rate
points at unmeasured UI/presentation/driver pacing and requires instrumenting
that boundary rather than changing world geometry again.

### 3.31 Fast startup: visible set first, speculative residency later

The old startup intentionally drained every WMO and M2 referenced by the outer
5x5 preload ring before creating the player. It also synchronously parsed the
outer 16 ADTs merely to discover those assets. That protected the first tile
boundary but made assets that might never be seen mandatory boot work.

The default path now loads active 3x3 terrain and WMOs, queues only camera-near
doodads without waiting for them, builds collision and starts the game. The
undiscovered outer tiles are ordered nearest first. After a 0.5-second
first-frame grace period, their ADTs are parsed one at a time on the bounded
worker pool and their terrain/WMO work enters the normal worker/upload queues.
M2 discovery is camera-demand-driven instead of tile-ring-driven. Startup logs
report `background` mode plus queued and undiscovered counts; the HUD shows
remaining discovery tiles beside the M2 queue.

`start.drainPreloadsAtStartup = true` preserves the legacy full outer-ring drain.
Compare both the `[game] ready in` time and the first two walked tile crossings
before declaring the fast path complete. A shorter loading screen that merely
moves a minute-long freeze onto the first frame is not a win; this path avoids
that by deferring discovery itself and pacing it across frames.

### 3.32 Demand-streamed M2s and the 22.6-second startup diagnosis

Nico's first fast-start log exposed that the path was still not fast: 19.6 of
22.6 seconds were spent draining 260 supposedly "visible" doodad models. After
the world appeared, outer WMO discovery then broadcast every embedded MODD
model path into the M2 queue, which grew beyond 500 and included furniture and
assets nowhere near the camera.

Draft 16 changes the policy:

- non-blocking startup no longer drains M2s at all;
- outdoor and WMO-embedded M2 requests are limited to camera position plus the
  doodad draw distance and a 100-yard lead;
- discovering a WMO no longer fans out every embedded model in the root;
- completed M2s acquire their nearby placements on a 250 ms idempotent refresh;
- speculative ADT parsing runs on the bounded asset worker pool rather than in
  `Update`;
- shared-context uploads use a fence plus `glFlush`, not `glFinish`;
- the test panel exposes `Stream only nearby doodads` and the live demand
  radius.

The WMO renderer already draws at group granularity with group AABB frustum and
distance culling. It still uploads a whole WMO model and lacks MOPV/MOPT/MOPR
portal traversal. WoWee's reference implementation only trusts portal traversal
from an interior-only group and always retains outdoor groups; therefore portal
culling is important for interiors but is not the explanation for an outdoor
Trade District result. Also note that VSync was enabled in Nico's log, so
54-60 FPS was the 60 Hz cap, not an uncapped ceiling.

### 3.33 Antiportals are not shadows; lighting must not face the camera

Nico's first high-FPS Trade District screenshot showed large black triangular
slabs across roads whose apparent darkness changed with camera orbit. Two
separate defects were present:

- MOGP group flag `0x04000000` is antiportal/occlusion-only geometry. WoWee
  skips it. MSUI was uploading and drawing it as visible WMO geometry; the
  reader now exposes `IsAntiportal` and preparation rejects those groups before
  upload, draw and collision collection.
- `wmo.frag` and `character.frag` flipped normals toward `uCameraPos` before
  evaluating the fixed sun. Orbiting changes camera position, so this made
  directional lighting camera-dependent. Two-sided lighting now uses
  `gl_FrontFacing`, which selects the rasterized geometric side and leaves the
  world-space sun fixed.

MSUI has no cast-shadow/shadow-map pass yet. Dark shapes in this build are
material/normal/geometry behaviour, not physically cast sun shadows.

The same run exposed shutdown ordering: `GpuFrameProfiler.Dispose` attempted
to delete timer queries after `ClientWindow` had destroyed the OpenGL context.
`ClientWindow.OnClosing` now disposes the game while the render context is
current, before ImGui/input/GL teardown; `GameLoop.Dispose` is idempotent.

### 3.34 Stormwind's floating Cathedral is a distance shell

The Cathedral/upper-city pieces visible above Trade District were not misplaced
copies of the detailed city. They are distance-only exterior silhouette groups:
the vanilla client shows them on approach, then removes them when the detailed
nearby city is resident. Drawing both creates the characteristic floating
Cathedral and incomplete entrance shells seen in Draft 17 screenshots.

MSUI now parses root `MOGN` and `MOGI` so group names remain associated with
their group indices. The renderer ports WoWee's large-WMO LOD classification:
small exterior connectors, low-vertex ALWAYS_DRAW groups, named facade/city
shells, and Stormwind's flagged Cathedral shell. Classified shells draw beyond
196 yards from their transformed group centre and are suppressed nearby.

The test panel exposes `Swap distance-only city shells`; the line below reports
how many shells were hidden that frame. Completed roots log `[wmo-lod]` with the
number classified. Disabling the checkbox is the A/B control and intentionally
restores the floating geometry.

### 3.35 City visibility, occlusion and lighting — the Draft 20 pass

This session chased "the city looks wrong from inside/on approach" and settled
several facts. They are all runtime-observed against Nico's build; none is
re-derivable from the file format alone.

**Warm/dim lighting — SUPERSEDED 2026-07-25, kept as a lesson.**
`WorldAtmosphere`'s day constants were cool and blown out. The daytime ambient
was blue-biased `(0.42, 0.50, 0.60)` — it coloured every shadow cold — and noon
sun+ambient stacked to a ~1.42 multiplier on flat ground, clipping cobblestone to
white. That pass replaced it with warm `(0.50, 0.46, 0.38)`, noon sun golden
`(1.00, 0.90, 0.72)`, sun intensity 1.15→0.90, ambient 0.85→~0.64.

> **The authored value at noon is `(0.408, 0.510, 0.604)`** — very nearly the
> exact colour this pass rejected as wrong. Exterior lighting now reads
> `Light.dbc`, so **none of the constants in this paragraph is live any more**
> and re-tuning them is not a fix for anything. SYSTEM_EXTERIOR_LIGHTING.md §5.3
> records why the pass was reasonable and still wrong: it had no yardstick. The
> HUD `Sun strength` / `Ambient strength` sliders survive as multipliers over the
> data, and **1.0 means "use the data exactly"** — that is the correctness check,
> not a taste setting. The clipping-to-white symptom was real; if it returns, it
> is a *tone-mapping* problem, not a constants problem.

**The real distance impostors are flagged INTERIOR.** This was the key find, and
it came from the in-game group dump (`DumpLargeWmoGroups` → `[wmo-groups]`):

```
[302] 'Cathedral of Light' 0x00012841 INT v=96   (0x10000 ALWAYS_DRAW set)
[303] 'Taventrance16'      0x00012841 INT v=48
[304] 'Old Town'           0x00012841 INT v=48
[305] 'Taventrance15'      0x00002A05 INT v=2368  (detailed, NOT always-draw)
```

Blizzard's "double cathedral" / distant-district silhouettes are low-poly
`ALWAYS_DRAW` (0x10000) groups that are ALSO flagged interior (0x2000). WoWee's
classification (which MSUI copied) requires `!indoor`, so it discarded them and
they were culled on approach. The signal is **ALWAYS_DRAW + low vertex count,
independent of the interior flag** (`alwaysDrawImpostor = alwaysDraw && verts <
ImpostorMaxVertices`). A genuine interior room is never ALWAYS_DRAW. The detailed
versions (`Taventrance15`, not always-draw) are untouched.

**Classification is now live, not baked.** `IsDistanceOnlyLod` takes
`(rootPath, nGroups, flags, verts, name)` and reads `ImpostorMaxVertices`.
`ReclassifyShells()` re-bakes every group's `IsDistanceLod` when the HUD slider
moves, so the whole city retunes without a reload; the draw loop stays
allocation-free by reading the baked flag.

**Inside/outside is per-group containment, and it is fragile.** `Swap`ping the
impostor for detailed geometry keys off `CameraInsideInstance`: transform the
camera into WMO-local space and test it against each non-shell group's local
AABB, expanded by `InsideInstanceMargin` (HUD, negative shrinks). The
whole-instance box was too coarse (swallowed the approach bridge); per-group is
better but still reads `inside=False` in spots you are visibly inside (e.g. the
open entrance courtyard), and one margin cannot satisfy both the deep-cathedral
swap and the entrance. Do not trust it as a hard "am I in the city" oracle.

**Occlusion cull reuses the collision BVH.** There is no depth/Hi-Z pass, so
`OcclusionCulling` (HUD, off by default) shoots collision-BVH rays at a group's
box corners; the group is culled only when **all eight corners** are blocked by
nearer solid geometry. It is NOT gated on inside/outside — occlusion is
view-dependent, and the earlier inside-gate is exactly why the toggle did
nothing. Caveats that are real, not bugs: it is corner-sampled (a partly-visible
roof still draws — correct), it tests *collision* geometry (excludes MOPY
F_DETAIL, so thin railings do not occlude), and it costs raycasts per candidate
group (`OcclusionMinDistance` bounds it; the toggle disables it).

**What occlusion cannot do, and why.** The SW entrance keep (`thief01`,
exterior) stays visible from an open courtyard because it is genuinely unblocked
there — vanilla shows it too. The real client can hide exterior geometry you can
technically see via portal/cell disconnection; WoWee deliberately disables portal
culling outdoors because applying it causes direction-dependent popout. So
neither occlusion nor a faithful WoWee port removes the courtyard keep. The only
remaining lever is portal traversal run OUTDOORS, accepting popout — experimental,
not built.

**Portal chunks are parsed, unused.** `WmoReader` now reads MOPV (portal
vertices), MOPT (portal descriptors: startVertex, count, C4Plane) and MOPR
(portal→group refs: portalIndex, groupIndex, side), plus per-group
`PortalStart`/`PortalCount` from the MOGP header (+0x24/+0x26). Nothing consumes
them yet; they are step 1 of any real traversal.

**The doodad pop-in was placement latency, not a cull.** Raising the doodad
demand/draw distance fixed statues appearing only when close. The
`[doodad-cull]` diagnostic (placed / drawn / dist-culled / frustum-culled)
proved the render cull was not the gate.

---

## 4. Verified vs unverified

### Verified against reality

- Terrain heights match the server exactly (`[verify] PASS delta -0.00`)
- WMO placement, M2 render and collision bases, client-geometry collision
- Character controller: walking, running, jumping, stairs, wall slide
- Skinned character: placement, heading offset 90, 119-bone skeleton, clip playback
- Gear: ItemDisplayInfo layout, atlas rectangles, item texture and model path conventions, attachment points
- The Yaw/OrbitYaw camera split, and the A/D-turn Q/E-strafe binds
- Dedicated shared OpenGL upload context initializes and the world renders with
  resources it created; the bounded CPU pool and ready-package path run in game
- Fast-start demand streaming runs in game without the former absolute freezes;
  true 1x MSAA recovers roughly 5–7 FPS versus multisampling on Iris Xe
- Antiportal geometry rejection removes the black WMO slabs; fixed-sun lighting
  no longer changes with camera orbit
- Context-safe shutdown prevents GPU timer-query deletion after GL destruction
- **WMO interior lighting** — signed off by Nico by eye ("the interior lighting
  looked good") and committed. SYSTEM_WMO_INTERIOR_LIGHTING.md is the reference
- **Doodad lighting** — `MODD.color` correlates 0.824 with sampled floor MOCV
  across 7,428 doodads; the interior gate is measured across 70,228 placements
  with zero orphans. SYSTEM_DOODAD_LIGHTING.md §1, §3
- **Water** — shipped and committed (`proper water`) after Nico's five specific
  complaints were each closed by eye. SYSTEM_WATER.md §0
- **Vantages, reason codes, scene dump and the override DB** run in game — a
  real dump and two saved vantages are on disk
- **Exterior lighting, numerically.** The `LightIntBand`/`LightFloatBand` row
  mapping is proved by arithmetic (`7668 = 426 × 18`, `2556 = 426 × 6`, exactly);
  the dbc→world convention is proved by landing light 77 on Stormwind within
  ~20 yards; and with `Use authored lighting` on at strength 1.0 the probe's
  `data` vs `applied` deltas read 0.000. SYSTEM_EXTERIOR_LIGHTING.md §1–§2
- **The tile-crossing freeze is dead:** 187 ms → not measurable, three
  consecutive crossings under a 40 ms threshold. SYSTEM_STREAMING.md §3
- **The doodad cull is fixed:** 55.8 ms → 0.3 ms at the same crossing,
  41–46 ns/instance, inside the normal-arithmetic band. SYSTEM_STREAMING.md
  §5A.15 — *but see the unverified list for the attribution caveat*

### Not yet verified — expect bugs here

- **Ground support tuning** — the nine-probe footprint, 0.5 yd downward adhesion
  and 180 ms fall-animation debounce are implemented but still need Nico's
  backwards-stair and fence-rail validation.
- **The face composite method.** WoWee stacks CharSections layers full-canvas; SuperUI paints them into face rectangles. Both cannot be right for the same file; the client tries size-appropriate handling and prints which happened.
- **Geoset rules for chest, pants, tabard and shoulders** are pattern-matched, not verified.
- **`TorsoFollow` 0.66** is Nico's read by eye, not their constant.
- `BlpDecoder` alpha scaling (§3.19) and MOPY F_DETAIL filter
- **WMO portal visibility is missing.** The chunks are parsed and PLAN_10 D1's
  instrument is built, but nothing traverses; the 120-yard ordinary-interior cull
  is still what runs (§3.26). The separate Cathedral distance-shell defect does
  have the authored near/far behavior (§3.34).
- **Authored exterior lighting only applies while DevTools is ON — found during
  the Draft 24 doc sync, 2026-07-25, and not yet fixed.** `UpdateLightProbe`
  (`Program.LightProbe.cs`) opens with `if (!_config.DevTools ||
  !_exteriorLight.Ready) return;`, and the *only* call to
  `WorldAtmosphere.SetAuthored` is inside it. With DevTools off, `HasAuthored`
  stays false and `Authored` gates every colour back to the invented constants,
  silently. **This is a FOUNDATION_PLAN §12 seam violation** — core's lighting
  now depends on the dev layer running. The resolve belongs in core
  (`ExteriorLighting` + `WorldAtmosphere`), with the probe left observing it.
  Two smaller stale notes in the same file: `Program.cs`'s comment above
  `InitLightProbe` still says *"nothing applies it yet"*, and the panel is the
  only thing that calls `DetectConvention`.
- **The sky's three band heights and the sun's direction are ours, not the
  data's.** `StopMiddle/StopBand1/StopBand2 = 0.45 / 0.18 / 0.06` and
  `SunDirectionAt`'s six/twelve/eighteen clock are honest inventions —
  `LightIntBand` gives five sky colours and no elevations, and `Light.dbc`
  carries no sun position. SYSTEM_EXTERIOR_LIGHTING.md §4 names these as **the
  single most likely reason the sky still reads off**, and they cannot be settled
  without a `refs/` capture.
- **The doodad cull fix is not proven to be the cause of its own improvement.**
  Run 6 read 41 ns/instance with the SoA toggle *off*, and model count fell 512 →
  169 between the runs. The clean test is both toggle states at the same spot,
  back to back. Per PLAN_08 §7 step 3, if the toggle makes no difference at equal
  model count, the change comes out. SYSTEM_STREAMING.md §5A.15
- **The frame-pacing bug is isolated but unexplained.** A 34 ms frame with zero
  work, zero allocation, zero collections, zero uploads and an idle GPU
  (§5A.16). Every graphics-flavoured hypothesis is dead by measurement.
  `threadMCyclesPerMs` is instrumented and **has not been read yet** — ~4–5 M/ms
  means a driver busy-wait, <1 M/ms means descheduled, and the two have opposite
  fixes.
- **Stormwind shell swap needs final runtime confirmation.** Verify that the
  Cathedral/entrance silhouette is present on approach and absent inside with
  `Swap distance-only city shells` enabled.
- **Streaming smoothness is only partially validated.** The shared-context build
  is substantially better but remains visibly behind the real client. Read
  SYSTEM_STREAMING.md §5A — **not** §3.27 — before choosing the next
  optimization, and treat every `[stream-budget]` figure as an artefact until
  re-measured with vsync off (§5A.1).
- **Foliage has not been checked against a real-client capture.** The Northshire
  road test (SYSTEM_FOLIAGE.md §0) is the pass/fail, and the per-kind curation
  is explicitly a blunt stand-in for retail's hand curation — the reference
  screenshot under `refs/` has not been taken.
- **No system has a `refs/` capture. `refs/` holds only a README.** Water,
  interior lighting, doodad lighting, foliage and exterior lighting are *all*
  emulation-core by FOUNDATION_PLAN §2, so "done" means side-by-side with the
  real client, and every one of them was signed off by eye or by number alone.
  **This is the largest single gap in the verification story** and it has grown,
  not shrunk, since Draft 22 — five systems now depend on a capture that does not
  exist.
- **The foundation's own loop is half-exercised.** Vantages, dumps and reason
  codes exist and only one dump has ever been captured — but the *instrument*
  half of the loop has now proved itself twice in anger: the hitch recorder
  killed six hypotheses in six runs, and the light probe overturned a by-eye
  tuning pass. What remains unexercised is the **`refs/` comparison half**.
- Networking: not written. WMO liquid (MLIQ): not written. Skyboxes, clouds and
  weather: parsed/resolved and applied nowhere (SYSTEM_EXTERIOR_LIGHTING.md §7)

---

## 5. Runtime architecture

### 5.1 Startup order — this order matters

```
ClientConfig.Load
ClientWindow main GL context
MpqMount + StormLibExtractor hook    BEFORE anything reads a file
GpuUploadWorker hidden shared context
AssetWorkerPool                      2–8 bounded CPU workers
TerrainRenderer.LoadShaders
AdtCache
TerrainRenderer.LoadAround / VerifyAgainst      initial inner 3x3
WmoRenderer.LoadShaders + LoadForTiles          buildings BEFORE collision
LiquidRenderer.LoadShaders           water.* + underwater.*
FoliageRenderer.LoadShaders          grass.*; DBC ground-effect tables
DoodadRenderer.LoadShaders           doodad.*, NOT wmo.* — see §5.2
DoodadRenderer queues visible-radius outdoor + WMO interior models without a startup drain
DoodadRenderer.LoadForTiles + nearby WMO interior doodads
adts.Retain(preload 5x5 ring)
LoadCollision()                      synchronous once during startup
CharacterController                  teleported to sampled ground
CharacterRenderer.LoadShaders + Load + Equipment + ApplyEquipment
CollisionDebugRenderer               GPU upload deferred until enabled
```

Default fast-start continuation after the first playable frame:

```
wait 0.5 seconds
discover one outer-ring ADT every 0.05 seconds
queue its terrain preparation plus WMO/M2 asset paths
consume WMO/M2 ready work through the existing per-frame preload budget
```

Runtime tile transition:

```
notice player entered adjacent tile
queue/continue terrain lead preparation
keep previous overlapping terrain + collision while desired terrain is pending
when desired terrain is ready: adopt buffers/VAOs atomically
rebuild cheap WMO/doodad placement state from resident ADTs
queue newly entering outer-ring WMO/M2 packages
snapshot collision triangles
build collision BVH on worker
atomically replace controller collision when BVH completes
```

### 5.2 Shaders — every renderer now owns its own pair

**This changed after Draft 21 and the old sharing is gone.** Current ownership,
read from the `LoadShaders` calls:

| Renderer | Vertex | Fragment |
|---|---|---|
| `TerrainRenderer` | `terrain.vert` | `terrain.frag` |
| `WmoRenderer` | `wmo.vert` | `wmo.frag` |
| `DoodadRenderer` | `doodad.vert` | `doodad.frag` |
| `FoliageRenderer` | `grass.vert` | `grass.frag` |
| `SkyRenderer` | `sky.vert` | `sky.frag` |
| `LiquidRenderer` | `water.vert` + `underwater.vert` | `water.frag` + `underwater.frag` |
| `CharacterRenderer` | `character.vert` | `character.frag` |
| `AttachedItemRenderer` | `attached.vert` | `character.frag` |
| `CollisionDebugRenderer` | `collision.vert` | `collision.frag` |

**Why the forks exist, and why re-merging them is a regression, not a cleanup.**
Doodads and the world used to share `wmo.*`. Once interiors were correctly lit
from MOCV and signed off, any lighting change made for furniture would have
altered wall lighting — the one thing that had just been declared correct. So
`doodad.*` was forked *before* any lighting change landed, and the WMO pair's
md5s are provably unchanged (SYSTEM_WMO_INTERIOR_LIGHTING.md's header records
them). Grass forked for the same reason: wind sway is wanted on a fern and not
on a table. **Do not re-merge any of these to reduce file count.**

The one remaining share is deliberate: `attached.vert` pairs with
`character.frag`, so a sword cannot light differently from the hand holding it.

`sky.*` is the newest and is unlike the others: it takes no geometry at all. The
vertex stage builds a fullscreen triangle from `gl_VertexID` and the fragment
stage evaluates a five-band gradient against the view direction, so it is exact
at any FOV with nothing to cull or get wrong at the poles
(SYSTEM_EXTERIOR_LIGHTING.md §4.1).

Each program is a **separate GL program object** — a uniform set on one does not
apply to another. Forgetting `uAlphaCutoff` on the doodad program turned every
tree into a black rectangle. With nine pairs instead of four, that failure mode
is now twice as easy to hit: when a new uniform is added to one shader, grep for
every renderer that needs the matching `Set` call.

Bones upload as **three vec4 rows per bone**, so skinning is three dot products and there is no mat3x4 column-order question.

### 5.3 Debug tooling — use it before theorising

| Tool | What it answers |
|---|---|
| `Bind pose` | Splits placement bugs from animation bugs — identities everywhere |
| `Force angle (deg)` | Drives the strafe mechanism directly, decoupled from the trigger |
| `Solo one geoset` | Draws one geoset at a time. **Solo beats hide for overlap bugs** |
| `Geosets drawn` | Category and variant of everything currently drawn |
| `Attached items` | Per-piece switches. Attached models are not geosets |
| `Hide hair` | Hair without the body — category 0 holds both |
| `Magenta unbound` | Which geosets have no texture |
| Mouse diagnostics | Buttons, capture, move events, applied events, last delta, cursor mode |
| `C` / Show collision | Green standable, red wall, yellow the exact triangle underfoot |
| Cyan capsule | Where the character actually is, at real radius and height |
| **Middle-click / Pick group** | Triangle-accurate WMO group pick under the cursor. Prints `[pick]` and lists in the HUD: file, group index, name, flags, INT/ext, LOD, verts, distance. **This is how `thief01`, `Cathedral of Light` etc. were identified — click first, do not guess which group.** |
| **Impostor/interior/occlusion sliders** | `Inside margin`, `Interior cull`, `Shell near-guard`, `Impostor max verts` (live reclassify), `Occlusion cull` + `Occlusion min dist`. All live; the readout below shows `inside=`, shells drawn/hidden, groups drawn, and occluded count. |
| `[wmo-vis]` / `[doodad-cull]` | Same numbers as the HUD, but console (off by default — `Console visibility trace`). |
| `Dump groups on load` | Re-emits the `[wmo-groups]` table (name/flags/int-ext/LOD/verts/local-centre) for large WMOs; needs a reload. |
| **Vantages** | Save/load a named viewpoint — position, camera, time of day and *every* toggle. `vantages.json`. Two are saved: `looking at the visible castle`, `looking at green river water`. |
| **Scene dump** | Writes `dumps/<vantage>.json`: camera, player, atmosphere, terrain residency, per-group WMO decisions with reason codes, doodad counts, perf, and the full toggle state. Self-describing — it records what was on when it was taken. |
| **Reason codes** | Every WMO group's draw/skip resolves to exactly one code via the shared `ClassifyGroup` predicate in `WmoRenderer`. "Why is this building missing?" is a lookup, not a guess. |
| **Visibility override DB** | `VisibilityOverrides.cs` — hand-authored show/hide, built by clicking, consulted **before** the heuristics. For cases the heuristics will never get right. |
| **Buildings panel** | Baked interior light (MOCV) + Interior brightness — SYSTEM_WMO_INTERIOR_LIGHTING.md §4 |
| **Doodads panel** | Baked interior light (MODD) + Interior brightness + "N with baked interior light" — SYSTEM_DOODAD_LIGHTING.md §8 |
| **Foliage panel** | Coverage, the three 1.12 authenticity switches, per-kind curation, wind/fade, look — SYSTEM_FOLIAGE.md §4 |
| **Water tuning window** | SYSTEM_WATER.md §4 |
| **Light probe** — *"what the DBCs say"* | Which zone lights reach you and at what blend weight; all 18 colour bands with swatches; all 6 scalars; and a **`data` vs `applied` block with deltas that must all read 0.000** at strength 1.0. Plus a time pin, a raw key dump, `Score all conventions` and `Re-detect from here`. SYSTEM_EXTERIOR_LIGHTING.md §6 |
| **Hitch recorder** | Automatic frame-spike capture: a ring of recent frames plus a written record with the per-phase split (`update`/`render`/`present`/`hud`/`imguiFlush`), the render split (terrain/wmo/doodad/foliage), the doodad split (cull/instanceUpload/drawSubmit + `firstTouchModels`), delayed GPU timings, GC pause and generation counts, upload counts, and `threadMCyclesPerMs`. Auto-saves a vantage at the spike. **`dominantPhase` names a bucket, not a cause** — SYSTEM_STREAMING.md §1 and §5A.3 |
| **Portal panel** (PLAN_10) | Which group the camera is in (index, name, INT/ext, door count, volume, file), portal quads drawn depth-off so a doorway can be checked against its opening, and `DumpPortalGraph` — which cross-checks MOHD's `NPortals` against the parsed count. **Tooling only; it culls nothing** |
| **Instances panel** (PLAN_13) | Every `Map.dbc` row with its kind (`global WMO` / `terrain`), tile count, col/row range, centre tile and world origin, plus the global-WMO path, MODF placement and bounds. `Dump to console` prints the whole table for diffing against PLAN_13 §1. **Read-only — it loads nothing and moves nobody** |
| **Particles panel** (PLAN_14) | Every loaded model with emitters: bone, texture, blend mode, emitter type, cell grid, and all ten tracks with their static values and key counts. Flags a NEGATIVE emission speed, which is what makes a portal pull inward. `Dump to console` prints the set for diffing against PLAN_14 §3.2. **Read-only — nothing is simulated or drawn** |

**The paired-artifact rule is the working agreement now (FOUNDATION_PLAN §3.3):
no visual bug report without its dump, and no dump without its vantage.** One
screenshot + one dump + one vantage name = one testable observation, plus a
`refs/<vantage>.png` real-client capture for anything emulation-core. This is
what replaces "here's a picture, what's wrong" — and it is currently
under-used: only one dump has ever been captured.

Working method note earned earlier: the middle-click picker + the group
dump ended three rounds of guessing which of Stormwind's 306 groups were the
cathedral impostor and the entrance keep. When "which piece of geometry is
that?" comes up, pick it; the answer is one click. The reason codes generalize
exactly that win to every group, every frame.

### 5.4 Thread and ownership rules

| Lane | May do | Must not do |
|---|---|---|
| Render/main thread | Input, movement, placement publication, VAO creation, draw submission, renderer caches | MPQ decompression, BLP decode, large mesh generation, texture/buffer transfer |
| `AssetWorkerPool` | M2/WMO/terrain parsing, BLP decode, CPU mesh preparation | GL calls, mutating renderer dictionaries |
| `MpqMount` locked lane | Archive lookup/extraction into independent byte arrays | Parallel reads through one archive instance |
| `GpuUploadWorker` | Shared-context texture creation, mipmaps, VBO/EBO creation and transfer | VAO creation, visibility decisions, drawing |
| Collision task | `CollisionWorld.Build()` BVH construction on a private snapshot | Reading live renderer placement collections while they mutate |

The handoff types encode the boundary: `Prepared*` objects are CPU-only;
`Uploaded*` objects contain complete shared GL handles; the renderer publishes
them only after upload completion. VAOs stay on the render context because they
are context-local container state even when their buffers are shared.

Shutdown order also matters: join renderer/terrain preparation, drain and stop
the GPU uploader, dispose the bounded CPU pool, then detach and dispose
`MpqMount`. Closing the archives while a worker is extracting is a race.

---

## 6. Working method — what has actually worked

Written down because the same three moves have solved almost every hard bug in this project.

**When something "does nothing", build a control that drives the mechanism directly.** Two rounds were lost to "didn't change anything" on the strafe twist. A slider that applied the twist regardless of the trigger turned it into "that caused strafe on the upper body, not lower" — which contained the fix.

**For overlap bugs, solo beats hide.** Hiding one participant proves a pair stopped fighting but never says which pair. Soloing enumerates participants and the answer is an index.

**An isolation control that cannot isolate the suspect is not an instrument.** Category 0 holds the base body *and* every hairstyle, so the category checkbox could not test hair against a helm without deleting the character.

**Prefer a measurement to another theory.** Printing collision bounds found a 26,000-unit coordinate-space error in one line, after walking around inside it found nothing.

**Do not guess at something you were handed.** CharSections states which texture is the lower face; inferring it back from image height painted the face across the eyes.

**When two references disagree, implement both and print which fired.** The face composite does this.

**Nico's eyes beat a comment in someone else's codebase.** The grace timer was well-argued and felt awful.

---

## 7. Phase plan

| Phase | Content | State |
|---|---|---|
| P0 | Foundations, opcode/updatefield generators | done |
| **P1** | **Northshire offline: terrain, buildings, doodads, collision, movement** | **DONE** |
| **P1.5** | **Character: skinning, animation, camera, gear, DBC** | **substantially done** |
| P2 | Enter world — realmd, world server, SRP6, header crypto, movement packets | not started |
| P3 | Combat | not started |
| P4 | Quests and systems | not started |
| P5 | Dungeons (Deadmines target) | not started |
| P6 | Raids | not started |
| P7 | Painted art pass | parallel from P4 |

### 7.1 Immediate next steps

**Housekeeping, before anything else**

1. **Start safely.** Read the stop-point block in §0, inspect `git status` and
   the existing diff, then build. The only uncommitted file should be
   `PLAN_10_WMO_PORTALS.md`.
2. ~~Commit the lighting + foliage set.~~ **Done** — `b0837a9` and later.
3. ~~Fix `FOUNDATION_PLAN.md`'s status line.~~ **Done** in the Draft 24 doc sync.
4. ~~**Fix the DevTools lighting gate.**~~ **Done, 2026-07-25.** The resolve is
   split out as `UpdateExteriorLighting` in `Program.LightProbe.cs` and runs in
   every build; only `_lightSample`, which feeds the panel and the console dump,
   stays behind the flag. The call site's comment had asserted the opposite of
   the truth — *"Read-only: it feeds the probe panel and nothing else"* — which
   is how the violation survived several readings; it now says what the call
   does. **The switch that decides whether the renderer consumes authored data
   is `WorldAtmosphere.UseAuthoredData`, a setting on the Lighting page. It is
   not the DevTools flag and must never become it again.**

**Close the verification gap (this is still the cheapest real win available)**

5. **Capture `refs/` shots from the real 1.12 client.** `refs/` holds a README
   and nothing else, and **five** emulation-core systems now depend on it —
   water, interior lighting, doodad lighting, foliage and exterior lighting.
   Start with the two saved vantages, then the same shots from MSUI plus their
   dumps. For exterior lighting this is not optional garnish: the three sky band
   heights and the sun direction are the only invented quantities left in that
   system and **nothing but a capture can settle them**
   (SYSTEM_EXTERIOR_LIGHTING.md §4, §7).
6. **Run the Northshire road test for foliage** (SYSTEM_FOLIAGE.md §0) and the
   tavern test for doodad lighting (a barrel must match its floor;
   "N with baked interior light" must be non-zero indoors).
7. **Validate the Stormwind distance-shell swap.** With the default-on test
   toggle, approach Stormwind from outside and confirm its silhouette remains;
   inside Trade District, confirm the Cathedral/entrance shells disappear.
   Record `[wmo-lod]` and `LOD shells hidden nearby`. Toggle it off for A/B.
   The dump's reason codes now answer this directly.

**Then, the open engineering fronts — pick one, do not braid them**

8. **The frame-pacing bug — the number has been read (§5A.18).** Three hitches
   in one run read **0.30–0.43 M/ms**, with a 2.62 M/ms control on a frame that
   was genuinely working. **<1 = the thread was not running**, so the
   driver-busy-wait branch and its whole fix family (adaptive vsync,
   `EXT_swap_control_tear`, pacing to pre-empt a spin) are **refuted**. The
   question is now *why the vblank deadline was missed*, and the swap chain is
   the suspect. **Still owed before writing streaming code:** the same reading on
   a controlled `[32,48] → [32,49]` crossing where GPU is back under 1 ms —
   §5A.19 says exactly what each outcome means. Also owed here: the clean
   SoA A/B at equal model count (§5A.15) and PLAN_08 D2's budgeted resumable
   adoption, which is the structural answer to the `resid` term that is now the
   largest remaining one (§5A.14).
9. **WMO portal visibility — PLAN_10.** The chunks are parsed and D1's
   instrument is built; traversal is not. This is the remaining lever for indoor
   correctness and for the Stormwind courtyard keep. Note D4: a WMO's own
   exterior groups *do* seed traversal; only WMO-to-WMO traversal across the
   open world is out of scope, and §3.35 warns it trades popout.
10. **Finish exterior lighting's honest gaps** — skyboxes
    (`LightParams.lightSkyboxID` is read and applied nowhere), clouds (bands
    9–12), weather (only `ParamsClear` is ever read, so underwater lighting is a
    visible hole), and **the ocean/river colours in bands 13–16, which are the
    authored answer to values `SYSTEM_WATER.md` currently invents.**
    SYSTEM_EXTERIOR_LIGHTING.md §7.
11. **WMO liquid (MLIQ)** — canals, fountains, indoor pools. `MCLQ` open-world
    liquid is done and shipped; `MLIQ` is parsed and drawn nowhere.
    SYSTEM_WATER.md §5.
12. **MFOG, per-interior fog** — offered and not taken during the interior
    lighting pass, and named there as the most likely next real gain for
    interiors. SYSTEM_WMO_INTERIOR_LIGHTING.md §5. PLAN_10 D1 has now made
    "which group am I in" answerable, which MFOG needs.
13. **`BlpDecoder` alpha fix** (§3.19), then remove the three point-of-use guards.
14. **P2 networking.**

**A note on sequencing.** Items 8–14 are genuinely independent and the project
has more open fronts than it has sessions. **Prefer 5–7 over opening another
front.** The gap has widened since Draft 22, not closed: five emulation-core
systems are now signed off without a single real-client capture between them.
That is exactly the failure mode §11's "empirical over documented" exists to
prevent, and exterior lighting just demonstrated the cost of it — a careful
by-eye tune picked a value the data contradicts.

### 7.2 Deliberately out of scope, permanently

- Supporting retail or any client other than 1.12.1 build 5875
- Server modifications
- Reimplementing FrameXML/Lua

### 7.3 Environment and how to run

```powershell
cd C:\Users\nico\source\repos\MSUIClient
dotnet build
dotnet run --project MSUIClient
```

Controls: **W/S walk, A/D turn, Q/E strafe** (holding right mouse swaps A/D to strafe), arrows turn and walk, PgUp/PgDn look, Shift walk, Space jump, F fly, C collision, left mouse orbits the camera, right mouse turns him, wheel zooms, Esc quits.

---

## 8. Troubleshooting playbook

### 8.1 Build errors

- **CS0102 duplicate definition** — a nested type and a method cannot share a name. `Mount` the class and `Mount` the method collided; the method became `AddMount`.
- **CS0111 / CS0103 after a refactor** — from editing by text slice and cutting a neighbour out with the target. **After any excision or cross-file copy, grep the surrounding scope for every identifier the remaining code still references.**
- **Named tuple elements lost in a ternary.** Name the fallback: `(x: 0f, y: 0f, z: 1f)`.
- **Silk.NET.OpenGL has its own `Texture` and `Shader`.** Alias them.

### 8.2 Nothing renders

Double transpose (§3.1) — draw calls and culling counts will all look healthy. Then shader compile errors, then frustum culling.

### 8.3 A surface loads and textures but does not appear

§3.19, alpha. Drag the alpha cutoff to 0.

### 8.4 The model is folded, exploded or flat

Check the bone count against `MaxBones` first (§3.8), then `Bind pose`. Bind pose correct plus animation wrong is a capacity or clip problem, not a transform one.

### 8.5 Non-ASCII kills shaders and PowerShell

One em-dash in a shader comment made Intel's GLSL compiler report "pre-mature EOF" on a complete shader, and the same character made PowerShell report brace mismatches across a whole script. **All `.vert`, `.frag` and `.ps1` files must be pure ASCII with no BOM.**

### 8.6 GameData must never enter git

`git check-ignore -v <path>` prints **nothing** for a file that is already **tracked**. Use `--no-index`. A first attempt committed 5.34 GiB before this was noticed.

### 8.7 Streaming hitch triage

- **Start here: read `dumps/hitch-*.json`, or the two `[hitch]` console lines.**
  The recorder fires on its own at any frame over the threshold and names the
  phase; correlating console tags against a remembered moment by hand is the
  step it exists to delete. `unaccounted` near zero means the split is complete
  and the named phase is the answer. `unaccounted` large means some region still
  has no timer around it — say so rather than reading past it.
- **A phase timer that starts late lies quietly.** `[stream] ready: 0.06s`
  reported a third of its own crossing (§3.27). When a phase number disagrees
  with the frame time, suspect the bracket before suspecting the frame.
- `[gpu-upload] X completed in 16ms off-thread` alone is not a failure. It says
  how long the dedicated context took, not how long the render thread stopped.
- `[stream-budget] ... 16ms` is render-thread adoption and is directly suspect.
- `[stream] tile ... ready: 0.XXs` measures the atomic placement/residency
  publication path, not the preceding background preparation wait.
- `[collision-async] ... off-thread` should not stall movement; the old collision
  remains attached until the new BVH is ready.
- A hitch with none of those lines is probably steady-state rendering, driver
  contention invisible to the current timers, or window/swap pacing. Add a
  rolling frame-time spike record with phase timings.
- If the application fails before `[stream] dedicated shared-context GPU
  uploader ready`, inspect hidden-window/shared-context initialization first.
  Do not disable the upload worker and silently return all uploads to the render
  thread as a permanent fix.

---

## 9. RESOLVED — character texture flicker

The player renderer v1 pass resolved the character texture flicker. M2 render
flags and blend modes are now carried into draw pieces, opaque/alpha-test draws
run before transparent/additive draws, transparent draws keep depth testing but
disable depth writes, and overlapping attached-item effect passes are
suppressed. The overlap detector remains as a regression instrument.

**Ruled out:**

- LOD duplication — `M2Reader` reads view 0 only
- Attached items — present with them off
- A single geoset category — hiding categories individually did not stop it

**Instruments in place:**

- `Solo one geoset` — draws one at a time. A geoset that flickers *alone* is self-overlapping or fighting something outside the geoset list; if none flickers alone, the fight is between two of them
- **Overlapping-draw detector** — at load, any two visible pieces whose index ranges intersect are printed. Two draws sharing triangles are the same surface submitted twice, which is z-fighting by construction. Silence means the geometry is disjoint and the cause is elsewhere

Keep the old instruments. If flicker regresses, first inspect render flags,
`PriorityPlane`, `MaterialLayer`, and whether a newly supported effect pass is
coplanar with an opaque pass.

---

## 10. What to ask Nico for

He has a large adjacent codebase (**MangosSuperUI**) and a cloned reference client
(**WoWee**). The long-lived clone has been
`C:\Users\nico\Desktop\WoWee-master`; this session also cloned the then-current
source read-only to
`C:\Users\nico\AppData\Local\Temp\wowee-reference-20260722`. A temp clone may
not survive cleanup, so locate the Desktop copy or fetch current source if it is
gone. **Check both before writing anything from scratch**—that mistake has been
made twice, once on WMO rendering and once on the DBC layer.

### Already brought over — do not ask again

`MpqCrypto`, `MpqArchive`, `PkwareExplode`, `MpqArchiveWriter`, `BlpDecoder`, `AdtTerrainReader`, `VmapFormat`, `WmoReader`, `M2Reader`, `M2TextureParser`. Plus the layouts and rules lifted from `DbcService`, `geoset-rules.js`, `region-rects.js`, `equip.js` and `SkinnedGlbWriter`.

### Ask for these when the matching work starts

| Work | Ask for | Why |
|---|---|---|
| WMO portal visibility | WoWee WMO renderer/visibility code using MOPV, MOPT and MOPR | Replace the approximate 120-yard ordinary-interior heuristic with real cell traversal; distance shells remain §3.34's separate system |
| WMO liquid (MLIQ) | WoWee's MLIQ path, plus a real-client capture of a fountain/canal | Open-world MCLQ is done; MLIQ is a different chunk with its own per-group placement — SYSTEM_WATER.md §5 |
| Any emulation-core sign-off | A real 1.12 client screenshot from the named vantage, saved to `refs/<vantage>.png` | FOUNDATION_PLAN §2: emulation-core work is measured against the real client, not by eye. Four shipped systems currently lack this |
| Streaming smoothness | A post-Draft-14 console log plus the exact moments Nico felt hitches | Separate upload contention, residency publication and steady-state rendering |
| The flicker (§9) | WoWee `src/rendering/m2_renderer.cpp` render-state setup | Whether they honour NoZWrite, blend mode and priority plane |
| Torso yaw constant | WoWee `src/rendering/character_renderer.cpp`, `setInstanceTorsoYaw` | The real fraction behind §3.10's 0.66 |
| Cape rendering | SuperUI `equip.js` cape path, WoWee `appearance_composer.cpp` cloak slot | Type-2 OBJECT_SKIN handling |
| Dungeons (P5) | SuperUI `WdtReader.cs` | Detects global-WMO instance maps; 13 dungeons with Map.dbc names |
| Particles / spell visuals | SuperUI `M2EmitterParser.cs`, `M2ParticlePatcher.cs` | |
| Anything protocol (P2) | The opcode and UpdateFields generators from the browser era | §3.22 has the values |

### Surveying WoWee

The streaming reference already inspected on 2026-07-22 is
`src/rendering/terrain_manager.cpp`. Its relevant structure:

- 4–8 CPU worker threads;
- nearest-first circular load queue;
- worker-side ADT/M2/WMO parsing, mesh generation and BLP decode;
- ready queue with memory/backpressure limits;
- main-thread `processReadyTiles()` budget of 8 ms, 16 ms while taxiing;
- asynchronous Vulkan upload batch without a render-thread fence wait;
- incremental publication phases for terrain chunks, models, instances and WMO
  doodads;
- a larger unload radius than load radius.

MSUI now mirrors the worker/ready/publication shape and uses upload-context
fences rather than `glFinish`. Demand streaming is runtime-proven, but do not
claim parity until whole-WMO group uploads are incremental and repeat-launch
preparation cost is addressed.

`index-cpp.ps1` on his Desktop builds a symbol index and assembles context packets:

```powershell
.\index-cpp.ps1 -Find strafe
.\index-cpp.ps1 -Packet "torso yaw bone"
.\index-cpp.ps1 -Symbol setInstanceTorsoYaw -Depth 1
```

### Always ask before writing code against a file

**Ask for the relevant files rather than guessing at their API.** Every reader in `Formats/` has surprises in it, and inventing a signature wastes a build cycle. He would rather paste a file than debug a wrong assumption.

### Ask for a console paste, not a description

Almost every hard bug here was resolved by a number in the log — the collision bounds that revealed a 26,000-unit error, the bone count that explained the folded character, the attachment list that explained the missing helm. When something looks wrong, add the measurement, ask him to run it, and read the output.

---

## 11. Working agreements

- **Complete files, not diffs.** Deliver whole replacement files and say plainly where each one goes.
- **CRLF** for `.cs`, `.vert`, `.frag`, `.json`, `.ps1`; **LF** for markdown.
- **Pure ASCII, no BOM** in shaders and PowerShell (§8.5).
- **Never question deployment steps.**
- **Empirical over documented.** If a doc and the bytes disagree, the bytes win.
- **Land an answer.** Exploration-only replies waste his time; every response should produce something he can build, run or read.
- **One system, one doc (§1.2).** A system's detail goes in its own `SYSTEM_<NAME>.md`, not into this handbook. Keep the handbook a lean index of cross-cutting truth. When you work on a system, read its doc plus §3 — not the whole handbook — and if you touch a system whose doc is still a "planned extraction", split it out then.
