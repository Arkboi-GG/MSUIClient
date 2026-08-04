# Plan 01 — Vantages (reproducible viewpoints)

**Foundation build step 1** (`FOUNDATION_PLAN.md` §3.1, §7). Save and restore a
named snapshot of *everything that determines what's on screen except the
geometry* — position, camera, time-of-day and every toggle — so a viewpoint can
be reproduced exactly, by you, by me (via the console echo, later the full dump),
and lined up against the real 1.12 client.

This is written from the actual source (`Program.cs`, `Engine/Camera.cs`,
`Player/CharacterController.cs`, `World/WorldAtmosphere.cs`, `ClientConfig.cs`), so
it names real fields and methods and can be coded straight from. It is a plan, not
code — the C# comes next turn on your go.

> **Template note.** This follows the §5 per-step template. For a *tooling* step
> the "Hypotheses" field doesn't apply, so it's replaced by "Key design
> decisions." Everything else is the template.

---

## 1. Problem

There is no way to return to an exact viewpoint. "Near Stormwind's gate, looking
at the keep, around noon" is not reproducible: the position drifts, the camera
angle is freehand, the time-of-day may be cycling, and a dozen visibility/lighting
toggles are in whatever state we left them. So every comparison is
apples-to-oranges, and every "it changed / it didn't" observation is polluted by
things we didn't mean to vary. This is the substrate the whole test loop stands
on — without it, the scene dump (step 2) has no fixed frame to describe and the
real-client side-by-side (our definition of done, §2) is impossible.

## 2. Class

**Foundation tooling.** Not an emulation-core visual feature and not an addition
— it's the instrument that later makes both measurable. "Done" is therefore
mechanical (§9): the viewpoint reproduces exactly. It carries no real-client
reference of its own; it's the thing that *enables* real-client references.

## 3. Target

A `Save vantage <name>` control captures the current scene state to a
human-readable `vantages.json`. A `Load vantage <name>` control restores it so the
frame is, within floating-point, identical — same spot, same facing, same camera,
same time-of-day, same toggles. Saved vantages survive restart and are
hand-editable. On save and load the client prints a `[vantage]` line echoing the
captured values, so even before the full dump exists (step 2) I can read exactly
what state we're in.

## 4. Key design decisions

**4.1 Where the state lives — capture/apply mirrors the HUD, no central store yet.**
The tunables are currently scattered across their owning objects and the HUD reads/
writes each one live every frame (e.g. `_wmo.Enabled = showWmo` at `Program.cs:1470`,
`_atmosphere.FogEnd` around `:1332`, `cam.FieldOfViewDegrees`). There is no single
settings object. Two ways to capture "every toggle":

- **Option A (recommended for v1) — a `VantageState` DTO plus `CaptureVantage()` /
  `ApplyVantage()` on `GameLoop` that read and write the same owner objects the HUD
  already binds.** Fastest to a working loop; touches nothing else; low risk.
- **Option B — first refactor all tunables into a `TuningState` object that both the
  HUD and the vantage system bind to.** Cleaner, and it also serves step 2's dump
  Toggles block and step 5's HUD reorg — but it's a broad edit to a 93 KB file and
  would stall step 1.

**Decision: ship Option A now; let step 5 (HUD reorg) introduce `TuningState` and
have Vantages adopt it then.** This keeps the foundation incremental and unblocked,
consistent with the project's "ship a working increment" habit. The DTO we design
in A is the same shape `TuningState` will take, so nothing is wasted.

**4.2 Loading across tiles — re-home the world, mirroring startup.**
A vantage may sit outside the resident 3×3 block. The runtime crossing path
(`UpdateWorldResidency`, `Program.cs:442`) only handles *adjacent* incremental
steps and waits on preload readiness, so it won't serve a multi-tile jump. Instead
`ApplyVantage` reuses the **startup sequence** verbatim (`Program.cs:220–340`):
`_terrain.LoadAround(x,y,TileRadius,_adts)` → set `_residentCentre = TileAt(x,y)` →
`_wmo.ResetPlacements()` + `LoadForTiles` → `_doodads.ResetPlacements()` +
`PopulateDoodads` → `_adts.Retain(ring)` → `LoadCollision()` → `Teleport` onto
sampled ground. Guard it: if `TileAt(target) == _residentCentre`, skip the re-home
entirely and just move the player + camera. The synchronous `LoadAround` causes a
brief hitch on a far jump — acceptable for a debug teleport; noted, not fixed here.

**4.3 Persistence — a `vantages.json` next to the config, same conventions.**
Reuse `ClientConfig`'s `System.Text.Json` setup (indented, comment-tolerant,
trailing commas) and `FindRepoRoot()` so the file lives at the repo root, is
hand-editable, and resolves the same way regardless of launch method. A
`VantageStore` owns load/save and a `List<Vantage>`.

**4.4 Console echo now, full dump later.** Save/load each print one `[vantage]`
line (name, map, x/y/z, yaw, pitch, distance, time, compact toggle summary). This
is the bootstrap of "coherent data output for me" until step 2's `F9` dump lands,
and it doubles as the load-matches-save check in the test protocol.

## 5. Captured state — the inventory (build-ready)

Every field, its owner, how to read it, how to apply it. This is the DTO.

| Field | Owner / read | Apply |
|---|---|---|
| player X,Y,Z | `_controller.Position` | `_controller.Teleport(x,y, SampleHeight(x,y) ?? z)`, then `_window.Camera.Target = _controller.Position` |
| facing yaw | `_window.Camera.Yaw` (controller reads it via `input.Yaw`) | `_window.Camera.Yaw = yaw` |
| flying | `_controller.Flying` | `_controller.Flying = v` |
| orbit yaw | `cam.OrbitYaw` | set directly |
| pitch | `cam.Pitch` | set directly |
| zoom distance | `cam.Distance` | set `cam.Distance` **and** `cam.EffectiveDistance = Distance` |
| field of view | `cam.FieldOfViewDegrees` | set directly |
| far plane | `cam.FarPlane` | set directly (see caveat 7.3) |
| time of day | `_atmosphere.TimeOfDayHours` | set directly |
| dynamic lighting | `_atmosphere.DynamicLighting` | set directly |
| fog on / cull-at-fog | `_atmosphere.FogEnabled`, `.CullAtFogEnd` | set directly |
| fog start / end | `_atmosphere.FogStart`, `.FogEnd` | set directly |
| sun / ambient strength | `_atmosphere.SunStrength`, `.AmbientStrength` | set directly |
| cycle time / far-plane couple / hours-per-min | `_cycleTimeOfDay`, `_coupleFarPlaneToFog`, `_gameHoursPerMinute` (GameLoop) | set directly |
| WMO visibility set | properties on `_wmo` the HUD binds at `Program.cs:1470–1539` — Enabled, frustum, distance-shell swap, ForceTwoSided, alpha cutoff, Building distance, Inside margin, Interior cull, Shell near-guard, Impostor max verts, Occlusion, Occlusion min dist, vis-trace, dump-groups | mirror those exact bindings |
| doodad set | `_doodads` bindings at `Program.cs:1573–1588` — Enabled, frustum, GPU instancing, alpha cut, DrawDistance — plus `_demandStreamDoodads` | mirror those bindings |
| map | `_config.Start.MapName` (drives `_adts`) | v1: compare only; cross-map is §8 |

**Environment toggles (VSync, Multisampling) are captured for the record but not
force-applied on load** — they're display settings, not viewpoint, and MSAA needs a
framebuffer rebuild. **Character-debug toggles** (bind pose, geoset solo, dressed,
etc.) are out of v1 scope: they don't frame the scene. Both are easy to fold in
later once `TuningState` exists (4.1).

## 6. Files touched

| File | Change |
|---|---|
| `Engine/Vantage.cs` *(new)* | `Vantage` record (the DTO above) + `VantageStore` (load/save `vantages.json`, `List<Vantage>`, find-by-name) |
| `Program.cs` | `CaptureVantage()` / `ApplyVantage(Vantage)` on `GameLoop`; a "Scene & Vantage" HUD section at the top of the panel; the `[vantage]` console echo; hold a `VantageStore` and the current-vantage name |
| `ClientConfig.cs` | none required (reuse `FindRepoRoot`/JSON options); optionally expose them |
| `vantages.json` *(new, repo root)* | the saved list; git-committed so vantages are shared, unlike `GameData/` |

No renderer, no format, no collision changes. This is additive instrumentation.

## 7. Apply sequence and caveats

**Order (in `ApplyVantage`):** re-home world if needed (4.2) → `Teleport` onto
sampled ground → `Camera.Target = Position` → set `Camera.Yaw`, `OrbitYaw`,
`Pitch`, `Distance`/`EffectiveDistance`, `FieldOfViewDegrees` → set all
`_atmosphere` fields + GameLoop cycle/couple flags → set `_wmo` / `_doodads`
bindings → set `_controller.Flying` → print `[vantage] loaded …`.

- **7.1 Facing.** `Update` copies `_window.Camera.Yaw` into `input.Yaw` every frame,
  so setting `Camera.Yaw` is sufficient; the controller follows.
- **7.2 Time cycling.** If `_cycleTimeOfDay` is true, `TimeOfDayHours` advances after
  load. For a stable comparison, save with cycling off. We capture the flag either
  way, so the vantage reproduces whatever was set.
- **7.3 Far-plane coupling.** `Render` overwrites `Camera.FarPlane` each frame when
  `_coupleFarPlaneToFog && _atmosphere.CullAtFogEnd` (`Program.cs:1167`). Restoring
  `FarPlane` only "sticks" when coupling is off — which is fine, because coupling is
  itself captured, so the far plane is reproduced by reproducing the coupling+fog
  state rather than the raw number.
- **7.4 Collision after re-home.** Reuse `LoadCollision()` / `BeginCollisionBuild`;
  the character may momentarily lack building collision on a far jump — acceptable
  for a debug teleport.

## 8. Scope boundary (v1 vs later)

- **v1:** vantages within the current map (`Azeroth` / Eastern Kingdoms). Save/load,
  persistence, HUD section, console echo, same-block fast path + far-jump re-home.
- **Later:** cross-continent vantages (Kalimdor) — needs rebuilding `_adts = new
  AdtCache(dataPath, savedMapName)` and everything downstream; guard v1 by warning
  and skipping the re-home if `saved.MapName != _config.Start.MapName`. Character-
  debug toggles and environment toggles folded in via `TuningState` (step 5).

## 9. Test protocol and definition of done

**Test (the done-test from `FOUNDATION_PLAN.md` §7.1):**

1. Walk to a spot, frame the camera on a building, set noon, set some toggles.
   `Save vantage sw-front-gate`.
2. Walk several tiles away (or teleport elsewhere) and change the camera/time.
3. `Load vantage sw-front-gate`.
4. **Pass =** player position, facing, pitch, zoom, time-of-day and every captured
   toggle read back identical (check the existing player-pos/camera HUD readouts),
   and the `[vantage] loaded` console line matches the `[vantage] saved` line
   field-for-field.
5. Restart the client, `Load vantage sw-front-gate` from the persisted file → same
   result.

**Definition of done:** the viewpoint reproduces exactly across a walk-away and
across a restart, and the console echo is legible enough that I can tell you what
state a vantage holds without a screenshot. (This is the mechanical bar; the
real-client side-by-side it unlocks is exercised starting in step 3's building
work, not here.)

## 10. Fallback (so this can't hard-block)

If the far-jump world re-home (4.2) proves fiddly, **v1 ships same-resident-block
vantages only** — position within the loaded 3×3, plus camera, time and toggles —
and still delivers most of the value (framing a specific building from a specific
angle at a specific time within the current area). The re-home upgrade then lands
as a fast follow. Nothing about the DTO or persistence changes.

## 11. One decision for you

Hotkeys. Save needs a typed name, so saving stays in the HUD. For *loading*, do
you want quick keys (e.g. `F6`/`F7` to cycle saved vantages, `F8` to reload the
current one), or is HUD-button loading enough for now? `F9` is reserved for the
step-2 scene dump either way. I'll default to **HUD buttons + `F8` reload-current**
unless you'd rather have the cycle keys. (The input pattern is the existing edge-
detected `_window.IsDown(Key.F8)` against a `_reloadKeyDown` bool, same as
`_flyKeyDown`.)

## 12. Resources

- `Engine/Camera.cs` — `Yaw`/`OrbitYaw`/`Pitch`/`Distance`/`EffectiveDistance`/
  `FieldOfViewDegrees`/`FarPlane`; handbook §3.12.
- `Player/CharacterController.cs` — `Teleport(x,y,z)`, `Position`, `Flying`, `Yaw`.
- `World/WorldAtmosphere.cs` — time/fog/sun/ambient; handbook §3.28, §3.35.
- `ClientConfig.cs` — JSON options + `FindRepoRoot()`/`RepoRoot` to reuse.
- `Program.cs` — startup world-load `220–340`; residency `442–486`; input API
  `818–882` (`_window.IsDown(Key.*)`, `Axis`, `MouseMiddleDown`, `MousePosition`,
  edge-detect bools); HUD toggle owners `1233–1990`.
- **WoWee / SuperUI: not needed for this step** — it's our own instrument, so no
  reference files to request (recording this so we don't ask out of habit).

---

## 13. Reconciliation (after planning steps 02–06)

Planning the whole foundation as a set changed two things here:

- **`CaptureVantage()` must return a serializable, embeddable object.** The Scene
  Dump (Plan 02) reuses it as its reproducible half — a dump is
  `{ vantage: CaptureVantage(), decisions: … }`. Design the DTO for reuse, not just
  internal capture.
- **The per-owner capture in §4.1 (Option A) is interim.** Plan 05 introduces
  `TuningState` as the single store for every tunable; when it lands,
  `CaptureVantage`/`ApplyVantage` read and write `TuningState` and the scattered
  mirroring is retired. The DTO here is a subset of `TuningState`, so nothing is
  wasted and vantages saved before the migration still load. Ship the mirroring now
  to get the loop working; migrate at Plan 05.
- **Build order:** Vantages still comes first and depends on nothing else. The only
  change downstream is that reason codes (Plan 03) now precede the dump (Plan 02).
