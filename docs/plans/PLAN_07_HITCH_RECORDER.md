# Plan 07 — The hitch recorder (making the stutter reproducible)

**First feature plan written under the `PLAN_TEMPLATE.md` agreement**, and the
instrument the streaming work has been missing since Draft 14. It exists to turn
"it freezes for a second at certain coords" into a vantage, a dump, and a named
phase.

Grounded in the real source: `Engine/ClientWindow.cs:140–153, 428–465`,
`Program.cs:109–117, 855–1019, 1207–1281, 1380–1440`, `GameLoop/Dev/GameLoop.DevTools.cs:103,
196`, `Engine/Vantage.cs`, `Engine/GpuFrameProfiler.cs`.

> **Build-order note.** This is a tooling step and it blocks the rest of the
> streaming work. Handbook §7.1 item 7 says to measure before changing
> architecture; §8.7 ends with "add a rolling frame-time spike record with phase
> timings." This is that record. Nothing in items 7's fix list should be
> attempted before it exists.

---

## 1. Problem

Nico, this session, in his words:

> "there are simply certain coords and moments where it stutters and freezes for
> a second before continuing smooth."

Three things in that sentence are new information and all three matter:

- **"certain coords"** — it is *location-triggered and reproducible*, not random
  jitter. That is the most useful property a bug can have and we are currently
  throwing it away, because nothing records where it happened.
- **"a second"** — this is not a 16 ms frame-pacing hitch. A ~1000 ms stall is
  three orders of magnitude above a missed vsync. Whatever causes it is a single
  blocking operation on the main thread, not accumulated overhead.
- **"before continuing smooth"** — one-shot per location, then fine. That is the
  signature of *first-time* work: an asset resolved, a residency published, a BVH
  swapped, a shader or texture created on first sight. It is **not** steady-state
  draw cost, which would degrade continuously rather than spike once.

**There is no vantage attached to this problem, and that is the problem.**
Every other item in the backlog can be stated as "at `<vantage>`, X looks wrong."
This one cannot, because by the time Nico notices the freeze, registers it, and
looks at the HUD, the frame is gone and the character has walked on. The bug
report and the evidence are separated by human reaction time.

## 2. Class

**Tooling** — this plan builds an instrument and changes no rendering behaviour.

The defect it will be used on is **emulation-core**: the real 1.12 client walks
the same route on the same machine without a 1-second stall, so there is an
external right answer and we are not inventing a target.

## 3. Target

For the instrument: a `dumps/hitch_*.json` file that arrives without Nico doing
anything except playing, and that names the responsible phase without further
questions being asked of him.

For the defect behind it: walk a route through Elwynn and Stormwind that
previously produced felt freezes, and produce no frame over ~100 ms. The
yardstick is the real client on the same hardware over the same route.

## 4. Key design decisions

Ranked by how much each one buys.

**D1 — Record automatically on a threshold; never rely on the user noticing.**
The trigger is `dt > HitchThresholdMs` (default 100 ms, HUD slider). Human
reaction time is the thing being designed out.

**D2 — Keep a ring buffer of the *preceding* frames, not just the bad one.**
The cause usually starts before the frame that stalls: a queue fills, a residency
change is requested, an upload is issued. Default ring is 600 frames (~10 s).
Writing only the stalled frame would capture the symptom and discard the setup.

**D3 — Capture the per-frame phase split, which currently exists but is
destroyed every frame.** `_updateMilliseconds`, `_movementMilliseconds`,
`_residencyMilliseconds`, `_preloadMilliseconds`, `_characterUpdateMilliseconds`,
`_cameraCollisionMilliseconds`, `_worldRenderMilliseconds`,
`_characterRenderMilliseconds`, `_creatureRenderMilliseconds`,
`CreatureLoadMs`, `CreatureLoadsThisFrame`, `CreatureCacheEntries`,
`_selectionRenderMilliseconds`, `_spellEffectRenderMilliseconds`, and
`_debugRenderMilliseconds`
(`Program.cs:109–117`) are all overwritten on the next frame and only ever read
by the HUD (`Program.cs:1434`). They are exactly the right fields; they simply
have no memory. The ring gives them one. Creature model work is classified as
`creature-model-load`; residual creature draw time is `creature-render`.

**D4 — Record `unaccounted = dt - (update + render)` as a first-class field.**
This is the single highest-value number in the record and nothing currently
computes it. If a 1000 ms frame shows update ≈ 4 ms and render ≈ 6 ms, then
990 ms was spent outside both — in swap, driver, or a stalling GL call — and
handbook §3.30's third case is confirmed in one line. If instead residency or
preload holds the time, we have the phase immediately. **This field decides
which half of the backlog we work on**, so it is not a nice-to-have.

**D5 — Route the existing log tags into the ring as structured events.**
`[stream]`, `[stream-budget]`, `[gpu-upload]`, `[wmo-preload]`,
`[doodad-preload]`, `[collision-async]`, `[wmo-lod]` currently go to the console
and are correlated by hand against a felt moment — the manual step §8.7 keeps
asking for and nobody enjoys. A `HitchLog.Event(tag, text)` that both prints
(unchanged) and appends `(frameIndex, tag, text)` to the ring makes the
correlation automatic and exact. **This is the difference between a log and an
instrument.**

**D6 — Auto-save a vantage at every hitch.** This is what converts "certain
coords" into something reproducible. On trigger, capture the current state via
the existing `CaptureVantage()` (Plan 01, `GameLoop/Dev/GameLoop.DevTools.cs:103`) and append
it to `vantages.json` as `hitch-<tile>-<n>`. Nico can then reload the exact spot
and walk into it again on demand, and every later A/B has a fixed viewpoint. The
whole foundation layer was built for this and has never been used in anger.

**D7 — Cooldown and cap.** Minimum 3 s between records and a session cap
(default 40) so one bad street does not write hundreds of files. Any suppression
must be *counted and reported* in the record — handbook §7.1's "no silent caps"
discipline applies to instruments too.

**D8 — Store the ring in preallocated structs, and write the file off-thread.**
An instrument that allocates per frame, or that writes JSON on the render thread,
would create the hitches it is meant to measure. The ring is a fixed array of a
value type; serialization happens on the asset worker pool.

## 5. Resources

**Check these before writing anything** — handbook §10 records writing from
scratch what already existed, twice.

| Resource | Why |
|---|---|
| `GameLoop/Dev/GameLoop.DevTools.cs:196` `DumpScene()` | The hitch record should reuse the dump's blocks, not invent a second format. Ideally the hitch file *is* a scene dump plus a `hitch` section |
| `GameLoop/Dev/GameLoop.DevTools.cs:103` `ApplyVantage` / `CaptureVantage` | D6 auto-vantage; do not re-derive the capture list |
| `Engine/Vantage.cs` | The serialization already handles every toggle |
| `Engine/GpuFrameProfiler.cs` | Delayed GPU per-pass ms. **Results arrive late by design** — the record must timestamp them by the frame they belong to, not the frame they were read on, or the GPU column will be attributed to the wrong frame |
| Handbook §3.27 | The three streaming states. Do not read old `[stream-budget]` spam as current behaviour |
| Handbook §3.24, §3.31, §3.32 | What residency, fast-start and demand streaming actually do at a boundary |
| Handbook §8.7 | The existing triage table — the record's fields should make each of its rows decidable |
| WoWee `src/rendering/terrain_manager.cpp` | `processReadyTiles()` 8 ms budget, ready-queue backpressure. If our stall is adoption, this is the shape of the answer |

## 6. Tools / instrument

**None of the existing instruments can isolate this suspect, which is why this
plan is a tooling step.** Concretely, the two the HUD offers both fail:

- **`ClientWindow.FrameMs` is a 0.5-second average** (`ClientWindow.cs:445–453`:
  accumulate `dt`, divide by frame count every half second). It is built to be a
  stable readout, which is the opposite of what spike detection needs, and it
  keeps no history at all.
- **The phase timers are single-frame scalars overwritten before anyone reads
  them** (D3). They are live-bound to ImGui text and nothing else.

So the honest statement of where we are: the client currently *cannot* answer
"what was it doing during that freeze," and every attempt to answer it so far has
been Nico eyeballing console output against a memory of a moment. Per the
template's first hard rule — *if you can't fill field 7, you're missing an
instrument, and building it is the real next task* — this is that task.

## 7. Test protocol

**Testing the instrument** (must pass before it is trusted):

1. Add a debug-only `Force 800 ms hitch` HUD button that sleeps the render
   thread. Press it. A record must appear, its `unaccounted` field must hold
   ~800 ms, and the auto-vantage must reload to the spot it was pressed.
   *An instrument that has never been shown to fire is not evidence.*
2. Press it three times in two seconds. Exactly one record, and the suppression
   count must read 2 (D7).
3. Walk normally for two minutes with no hitch. Zero files, and no measurable
   FPS difference with recording on versus off — check the HUD both ways.

**Testing the defect:**

4. Nico walks — **not flies** — his usual route, including the coords where he
   knows it freezes, until at least three records exist.
5. For each record, read in this order: `unaccounted` → the largest phase →
   the events in the preceding 60 frames → queue depths.
6. Classify each hitch into exactly one of: **outside update+render**
   (driver/swap/present), **residency publication**, **preload adoption**,
   **collision swap**, **first-sight asset work**, **steady-state**.

The classification in step 6 is the deliverable of this plan. It selects which
of handbook §7.1's fixes is the real one, and it replaces the five ranked guesses
in §3.27 with a measurement.

## 8. Definition of done

**The instrument:** steps 1–3 pass, and a hitch Nico *felt* has a file he did not
have to do anything to produce.

**The plan's real output:** three or more classified hitches, and a one-line
verdict written into handbook §3.27 replacing the current "likely remaining
causes, in evidence order rather than certainty" list with what was measured.

Explicitly **not** in scope: fixing the hitch. If the classification points at
whole-WMO upload granularity, that is Plan 08 and it gets its own template.
Diagnosing and fixing in one step is how this project previously burned build
cycles on well-argued changes that did nothing.

## 9. Fallback

If the ring buffer or off-thread write proves fiddly, the smallest partial win is
**D4 alone**: compute `unaccounted` per frame, and on threshold print one console
line with position, tile, and the phase split. That single line already splits
the backlog in half, and it is perhaps twenty lines of code. Ship that first if
time is short; the ring, the events and the auto-vantage are the amplifier, not
the mechanism.

If even that is ambiguous — say `unaccounted` is large but variable — the next
fallback is to bracket `SwapBuffers` and the ImGui render separately, which
turns the "unmeasured boundary" of §3.30 into two named numbers.

## 10. Reconciliation

- **Plan 02 (scene dump).** The hitch record should be a scene dump plus a
  `hitch` block, not a parallel format. If that forces the dump to be callable
  with a "reason" argument, change Plan 02's signature rather than forking it.
- **Plan 01 (vantages).** D6 makes vantages get *written by the client* rather
  than only by Nico. `vantages.json` will grow automatically, so it needs a
  distinguishable name prefix (`hitch-`) and, eventually, pruning.
- **Plan 05 (HUD/TuningState).** Adds a "Hitch log" panel: last N hitches with
  position, dominant phase and a load-vantage button. `TuningState` gains
  `HitchThresholdMs`, `HitchRingFrames`, `HitchRecordingEnabled`.
- **Handbook §8.7** gains a first row: "check `dumps/hitch_*.json` before reading
  console tags by hand." **§3.27** gets rewritten once step 6 produces verdicts —
  that rewrite is the point of the plan.
