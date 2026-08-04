# Plan 02 — Scene Dump (coherent data output)

**Foundation build step 2** (`FOUNDATION_PLAN.md` §3.2). A hotkey writes a bounded,
structured report of what the client decided this view and why — keyed to a vantage
— so a screenshot from you always arrives with the machine-readable "why" for me.

Grounded in the real state already computed: `WmoRenderer` counters, `WorldAtmosphere`,
`CharacterController` ground fields, `GpuFrameProfiler`, and the `GameLoop`
`_*Milliseconds` timers.

> **Build-order note.** Build **after Plan 03** — the dump's WMO block calls the
> `ExplainGroup` / `EnumerateExpectedWmoPlacements` that Plan 03 creates, and it
> reuses `CaptureVantage()` from Plan 01.

---

## 1. Problem

The screenshot is the lossy channel we're leaving behind. It shows a building is
missing; it can't say the group was `SHELL_NEAR_SUPPRESSED`, or the placement was
`NOT_RESIDENT`, or the sun was at the wrong angle because time was cycling. Without
a data artifact paired to the picture, I reconstruct the client's state by guessing,
and we burn a build cycle per guess.

## 2. Class

Foundation tooling. "Done" is mechanical: the dump reconstructs the situation well
enough that I can explain the frame without the screenshot.

## 3. Target

`F9` (and a HUD "Dump scene" button) writes `dumps/<name>.json` (name = the loaded
vantage, else a timestamp), and prints a one-line `[dump]` summary to the console.
The file embeds the vantage (so it's reproducible) plus the decision and
performance data (so it's explainable). You send me the JSON with your screenshot;
that pair is the atomic unit of every test from here on.

## 4. Contents (bounded on purpose)

Dumping every group of every instance every time is unbounded in a city. The dump
is **targeted**:

1. **Header + vantage** — reuse `CaptureVantage()` (Plan 01): map, camera, player,
   time-of-day, and every toggle. This *is* the reproducible half.
2. **Atmosphere** — the evaluated `WorldAtmosphere` values (sun dir/colour/intensity,
   ambient, fog start/end, clear colour) so lighting differences are readable.
3. **Ground** — `CharacterController` truth: `GroundSource`, `GroundZ`,
   `TerrainGroundZ`, `CollisionGroundZ`, `GroundTriangle`, `NoGroundBelow`,
   `LastBlockPoint/Normal` — for feel/collision reports.
4. **Crosshair instance** — the WMO instance under the cursor (via `PickGroups`):
   its full group list, each with the `ReasonCode` from `ExplainGroup`, plus any
   `Rejected` groups. **This is the "why is this building doing that" block.**
5. **Instance summary** — every resident WMO instance: name, distance, groups
   drawn / hidden, and a histogram of `ReasonCode` counts. Cheap, whole-scene.
6. **Not-resident nearby** — `EnumerateExpectedWmoPlacements` for resident tiles,
   filtered to camera range, listing placements with no live instance
   (`NOT_RESIDENT`). This catches "it isn't loaded yet," which no group reason can.
7. **Doodads** — mirror the existing `[doodad-cull]` counters (placed / drawn /
   distance-culled / frustum-culled) + `DrawCallsLastFrame`, queue depth.
8. **Terrain** — resident `TileCount`, `LoadedTiles`, pending discovery count.
9. **Perf** — `_window.Fps`/`FrameMs`; the `GameLoop` `_*Milliseconds` phase timers
   (update, movement, residency, preload, character, camera-collision); the
   `GpuFrameProfiler` smoothed per-pass GPU ms; WMO `DrawCalls`/`Triangles`.

## 5. Key design decisions

**5.1 Reuse `CaptureVantage`.** The dump's header/camera/atmosphere/toggles blocks
are exactly a vantage. So the dump = `{ vantage: CaptureVantage(), decisions: … }`.
This is the reconciliation with Plan 01: `CaptureVantage()` must return a
**serializable object the dump can embed**, not just mutate state. (Plan 01 §4.1's
DTO already is that object — this just formalizes that it's shared.)

**5.2 JSON as source of truth, console line for the human.** The dump is *for me*,
so JSON — unambiguous, exactly parseable. On write, also print
`[dump] <file>: inside=<b> crosshair=<wmo>#<grp> reason=<code>; N resident WMO, K not-resident nearby`
so you get an instant human-readable gist and a confirmation it fired. (Text-vs-JSON
is the open call from `FOUNDATION_PLAN` §9; recommending JSON.)

**5.3 Targeted, not exhaustive.** Full per-group reasons only for the crosshair
instance (the thing you're asking about); everything else is summaries and
histograms. Keeps a Stormwind dump to kilobytes and keeps the `F9` cost trivial.

**5.4 Where it writes.** `dumps/` at the repo root (via `ClientConfig.RepoRoot`),
git-ignored (they're transient). Filename = current vantage name if one is loaded,
else a timestamp.

## 6. Files touched

| File | Change |
|---|---|
| `Engine/SceneDump.cs` *(new)* | the DTO tree + `Write(path)` (System.Text.Json, indented) |
| `Program.cs` | `DumpScene()` on `GameLoop` — gathers from the systems, calls `CaptureVantage()`, `PickGroups`+`ExplainGroup`, the enumerators, writes the file, prints `[dump]`; wire `F9` (edge-detected like `_flyKeyDown`) and a "Dump scene" HUD button in the Scene & Vantage section |
| `World/Wmo/WmoRenderer.cs` | (from Plan 03) `ExplainGroup`, `EnumerateExpectedWmoPlacements`, `Rejected` — consumed here |
| `.gitignore` | add `dumps/` |

## 7. Reconciliation with other plans

- **Depends on Plan 03** for the WMO reasons and enumerators — build 03 first.
- **Depends on Plan 01** for `CaptureVantage`; and it constrains Plan 01's DTO to be
  a serializable, embeddable object (noted back into Plan 01).
- **Simplified by Plan 05:** once `TuningState` exists, the toggles block is just
  `TuningState` serialized rather than gathered field-by-field.

## 8. Test protocol and definition of done

- Stand where a building should be and isn't. `F9`. Open the JSON: it names the
  cause — a crosshair group `ReasonCode`, a `Rejected` entry, or a `NOT_RESIDENT`
  placement — with no screenshot needed.
- Send me only the JSON for a vantage I've never seen; I describe back what was on
  screen (what drew, what was hidden and why, the lighting). If I can, it's done.
- A second dump at the same vantage after a change diffs cleanly — the changed
  reason code is the effect of the change.

## 9. Fallback

Ship the dump **without per-group reasons** first — vantage + atmosphere + ground +
counts + `NOT_RESIDENT` + perf. That already beats a screenshot alone and is
independent of Plan 03. Add blocks 4–6's reasons when 03 lands.
