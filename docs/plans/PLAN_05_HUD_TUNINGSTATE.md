# Plan 05 — HUD cockpit + `TuningState`

**Foundation build step 5** (`FOUNDATION_PLAN.md` §4). Reorganize the crude,
sprawling HUD into a sectioned cockpit, and introduce the single `TuningState`
object that Plans 01 and 02 deferred to here — the consolidation that makes vantage
capture and the dump's toggle block trivial and keeps them from drifting.

Grounded in the current HUD: one `ImGui.Begin("MSUI Client")` window (`Program.cs:1233`)
with ~760 lines of controls (`1233–1990`), each reading/writing its owning object
live (`_wmo.Enabled = …`, `_atmosphere.FogEnd`, `cam.FieldOfViewDegrees`, …).

> **Build-order note.** After the mechanisms exist (01–04), so we reorganize a known
> set and migrate a known list of tunables. Order: 01 → 03 → 02 → 04 → **05** → 06.

---

## 1. Problem

The HUD is one long window of live-bound locals with no grouping, so finding the
right control is slow and half of them are unlabeled by concern — which taxes the
test loop we're building (every extra second hunting a toggle is a second not
comparing to the real client). Underneath, the tunables are **scattered across their
owning objects**, so "capture every toggle" (Plan 01) and "serialize the toggle
state" (Plan 02) each have to reach into a dozen places and will silently miss new
controls as they're added.

## 2. Class

Foundation tooling. Mechanically done when every control has a labelled home, the
reason readout is live, and `TuningState` round-trips through vantages unchanged.

## 3. Target

Two things. **(a)** A `TuningState` object owning every tunable, bound by the HUD and
read by the world/atmosphere/camera each frame through one `ApplyTuning()` — so
capture (Plan 01) and dump (Plan 02) just copy/serialize it. **(b)** The HUD split
into stable, collapsible sections, with a live visibility **reason readout** and the
pick / override controls grouped where the visibility work happens.

## 4. Key design decisions

**4.1 `TuningState` as the single store; `ApplyTuning()` as the single sink.**
Today the HUD both *holds* the value (as a local `ref`) and *pushes* it to the owner
(`_wmo.Enabled = showWmo`) inline, scattered across 760 lines. Instead:

- `TuningState` holds every tunable field (the Plan 01 §5 inventory + the character-
  debug toggles: bind pose, geoset solo, dressed, heading offset, torso follow, …).
- The HUD binds widgets to `TuningState` fields only.
- One `ApplyTuning()` per frame pushes `TuningState` into `_atmosphere`, `_wmo`,
  `_doodads`, `cam`, `_controller` — the *single* place those writes happen.

Consequence: `CaptureVantage()` becomes "copy the scene subset of `TuningState`";
the dump's toggle block becomes "serialize `TuningState`"; a newly added control is
automatically captured and dumped because it's a `TuningState` field. This is the
reconciliation that retires Plan 01 §4.1's interim "mirror each owner" approach.

**4.2 Sections (stable order, remembered open/closed):**

| Section | Holds |
|---|---|
| **Scene & Vantage** *(top)* | vantage name input, Save, saved-list load, Dump scene (F9), time-of-day + presets |
| **Visibility** | WMO + doodad culls; the live **reason readout**; Pick under crosshair; Hide/Show picked (Plan 04) |
| **Lighting / Atmosphere** | sun/ambient/fog, dynamic-lighting, cycle |
| **Streaming & Perf** | FPS/frame ms, per-pass CPU/GPU timers, queue depths, `[stream]` state |
| **Character & Gear** | the existing solo/bind-pose/geoset/attachment instruments |
| **Debug Draw** | collision, capsule, isolate-blocker |

**4.3 The reason readout (the HUD-native dump).** In the Visibility section, always
show: per-`ReasonCode` counts for the largest WMO this frame (from the existing
counters + Plan 03), the picked group's `ReasonCode`, and the `NOT_RESIDENT`-nearby
count. This makes the HUD a live view of what the dump captures.

**4.4 Design rules (`FOUNDATION_PLAN` §4, from the handbook's own method §6):**
every control drives its mechanism directly; an isolation control must be able to
isolate its suspect (the "solo beats hide" / "an instrument that can't isolate isn't
an instrument" rules). New controls are held to that bar.

**4.5 Scope discipline.** This is layout + a state refactor, not new rendering
behaviour. No visual output changes; the only new *controls* are the ones Plans 01/04
introduced.

## 5. Files touched

| File | Change |
|---|---|
| `Engine/TuningState.cs` *(new)* | the field bag + `ApplyTuning(atmosphere, wmo, doodads, cam, controller)` |
| `Program.cs` | migrate the `1233–1990` HUD to bind `TuningState` and split into `CollapsingHeader` sections; call `ApplyTuning()` once per frame; move pick/override/vantage/dump controls into their sections |
| `Engine/Vantage.cs` (Plan 01) | `CaptureVantage`/`ApplyVantage` now read/write `TuningState` instead of each owner |

## 6. Resources

- `Program.cs` HUD `1233–1990` (every current control + its owner); input/echo
  patterns already used.
- The tunable inventory table in `PLAN_01_VANTAGES.md` §5 (the migration list).
- Handbook §6 (working method — the instrument rules), §5.3 (existing debug tools to
  preserve).

## 7. Reconciliation with other plans

- **Retires Plan 01 §4.1 Option A:** capture/apply migrate onto `TuningState` here.
  Plan 01 ships first with mirroring; this step swaps the backing store. The DTO
  shape doesn't change, so vantages saved before the migration still load.
- **Simplifies Plan 02:** toggle block = serialized `TuningState`.
- **Hosts Plan 04's** Hide/Show-picked and Plan 03's reason readout in the Visibility
  section.

## 8. Test protocol and definition of done

- Every control is under a labelled section; sections remember open/closed.
- Move any slider → still works (through `ApplyTuning`), proving the migration is
  behaviour-preserving.
- Save a vantage, change things, load it → round-trips through `TuningState`
  unchanged (the Plan 01 done-test, now on the consolidated store).
- The Visibility reason readout shows live per-reason counts and the picked group's
  code.
- **Done:** the cockpit is legible, `TuningState` is the one source of tunables, and
  capture/dump read it rather than reaching into owners.

## 9. Fallback

If the full `TuningState` migration is heavy, do it in two moves: **(1)** reorganize
the HUD into `CollapsingHeader` sections (pure layout, low risk, immediate legibility
win); **(2)** migrate tunables into `TuningState` incrementally, section by section,
with Plan 01's capture keeping its interim mirroring until each field moves. Neither
move changes visual output.
