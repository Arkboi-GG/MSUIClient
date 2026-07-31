# Gameplay Foundation Plan 2 — the movement/animation and panes/keybinds planes

Status: Draft 1 — 2026-07-31 — planned against `GAMEPLAY_FOUNDATION_PLAN.md` (I1–I10,
built and gate-green), `BENILLA_VS_MSUI_MOVEMENT.md` (the movement gap analysis with
benilla file:line proof), `SYSTEM_GAMEPLAY_UI.md`, and the 7C acceptance doctrine
proven in `SPEC_TOOLKIT_07`–`12` + report W-sections.

Nico's rulings folded in (2026-07-31): movement/animation plane builds FIRST;
keybinds get instruments AND the missing feature (default set + menu); benilla is
runnable as a golden source in principle (PowerShell launch — invocation to be
captured as part of the work, treated as opportunistic until proven).

## 0. Thesis

Plan 1 said: a gameplay bug is located by *what state the entities and UI are in*.
Two planes still have no instruments at all:

- **Kinematics/animation.** Nothing today can put a number on jump height, measure a
  turn rate, or detect a stall mechanically. `BENILLA_VS_MSUI_MOVEMENT.md` names the
  defects (displacement-driven animation with ~83 ms lag, zero cross-fade with phase
  reset pops, no body-heading pipeline, hardcoded speeds, turn rate 2.8 vs π·[1|0.75],
  step 1.0 vs 0.7, slope 55° vs 50°, missing swim/landing/turn-shuffle) — but every
  one of those claims is currently verified only by reading source. None is *measured
  in our client*, so no fix for them is acceptable under standing law 1.
- **Panes/keybinds.** Character, skills, talents, spellbook, quest log, bags:
  formatting defects are described in prose, keybinds are ad-hoc, there is no
  keybinds menu, and no default-binding law exists as data.

The translation is the same one that worked twice already:

| Established pattern | Movement twin | Panes twin |
|---|---|---|
| Vantage / scenario | **Input script** (deterministic keypress timeline) | **Pane state** (which pane, resolution, UI scale) |
| Verdict enums | `[verdict:move]` transitions, stall/latency events | `[verdict:layout]` overflow/clamp, `[verdict:keybind]` resolution |
| F9/F10 dump | **Per-tick movement trace** (CSV) | **Layout table** (authored rect → screen rect per widget) |
| Batch + CSV + diff | **move-audit** over traces vs expectation bands | **pane-sweep** over panes × resolutions vs committed baseline |
| refs/ goldens | analytic constants + benilla traces (+1.12 eyeball) | real-1.12 pane screenshots + shot-diff |
| Acceptance | trace-diff cohorts, materialized key lists, hard stops | layout-diff cohorts, same law |

**The decisive advantage on movement: most expectations are already exact numbers.**
v0 = 7.9558, g = 19.2911 ⇒ apex = v0²/2g = **1.6405 yd** at **0.4124 s**, full airtime
≈ 0.8249 s on flat ground. Turn rate π rad/s standing, ×0.75 translating. Run 7.0,
backpedal 4.5 (never applied to a diagonal's whole magnitude), walk gate 2.0×walk.
Stop distance 0 (instant). Start latency (intent→displacement) ≤ 1 tick; today's
displacement-driven animator adds ~83 ms (measurable!) of clip lag on starts. Step
0.7, slope 50°. Every one of these becomes a sweep row with an explicit tolerance
band — a committed expectations file, not reviewer prose (standing law 5).

**Stalls become a detector, not an anecdote:** any trace window where intent flags
say moving but the playing clip is Stand (or rate ≈ 0) beyond a threshold is a stall
row — the exact class of the post-cast freeze, found mechanically forever after.

## 1. New instruments (numbering continues Plan 1)

### I11 — Movement trace recorder  **[SLICE M]**

DevTools toggle (and auto-on while a script runs): every simulation tick appends one
row to `dumps/movetrace-<name>.csv`:

`frame, t, dt, pos.xyz, vel.xyz, horizSpeed, aimYaw, bodyYaw(when split), inputFlags
(fwd/back/strafeL/R/turnL/R/jump/autorun), grounded, verticalVel, fallTimeMs,
clipId, clipName, clipTime, playbackRate, lastAnimChoice, wireSentThisTick
(MSG_MOVE_* opcode names)`

Report=act: sampled from the live `CharacterController` / `M2Animator` /
`LocalMovementSender` state, never recomputed. Plus `[verdict:move]` ring entries on
transitions (gait change, air/ground, clip change with from→to and clipTime-at-cut).
Additive; zero behavior change.

### I12 — Scripted input player  **[SLICE M]**

`movement-scripts/*.txt`: timestamped input edges (`0.000 press W`, `2.000 press
SPACE`, `2.100 release W`, …) plus an optional `fixed-dt 16.667ms` header. A DevTools
"run script" substitutes the input source with the timeline — the SAME `MovementInput`
path the keyboard feeds (forcing inputs, not outputs, keeps report=act honest; the
I5 doctrine). Paired with a committed flat-arena vantage (and later a synthetic
stair/slope course) so runs are reproducible. Every script run auto-records a trace.

### I13 — move-audit  **[SLICE M]**

`tools/move-audit`: consumes trace + expectation file → verdicts CSV. Measures per
scenario: jump apex height/time/airtime; run/walk/backpedal displacement per second;
diagonal magnitude; stop distance; standing and moving turn rate (deg/s); start
latency input-edge→first-displacement and input-edge→clip-change; stall windows
(intent-moving ∧ Stand/rate≈0 > threshold); phase resets (clipTime→0 while gait
unchanged — the pop detector); Substituted/MissingClip events (I1's `AnimChoice`).
Expectations live in `movement-scenarios/expected/*.csv` — committed, explicit
tolerance bands, sourced from the constants above with benilla file:line citations.
Diff mode against committed baseline traces: `movement-scenarios/baseline/`.
This becomes the fourth standing gate: **move-audit-check** joins build,
combat-wire-check, portrait-camera-check.

### I14 — Benilla golden traces (opportunistic)

Stage A: capture how benilla launches (Nico's PowerShell invocation, recorded in
SETUP.md) and whether it can accept scripted input / dump per-tick state; a small
benilla-side trace dumper is authorized IF its tree builds locally. Stage B: record
benilla traces for the same `movement-scripts/` and add a golden-diff column to
move-audit. NOT a gate until proven; the analytic constants remain primary law. If
benilla can't be scripted, downgrade to eyeball reference without blocking anything.

### I15 — Animation transition law audit

Plan 1's I6 (Animation Lab + anim-audit) grown one step: trace-derived transition
laws as mechanical checks — landing preserves gait phase (no re-pick; the
driver.rs:893-899 law), JumpStart→Jump handoff honors clip 37's own window,
JumpEnd 39 / JumpLandRun 187 selected by the standing/moving split, no Substituted
in shipped fallback rules. These are the acceptance instruments for the fix chain's
animation items; they extend move-audit rather than being a separate tool.

### I16 — Pane layout verdicts

Every pane (character, skills, spellbook, talents, quest log, social, bags, loot,
vendor, and the keybinds menu once it exists) registers a layout table: widget id,
authored rect, screen rect, font, color, text metrics, overflow/clamp flag. The F10
dump gains a `panes` block covering whatever is open. `[verdict:layout]` fires on
overflow/clamp — formatting bugs become named rows.

### I17 — pane-sweep

Batch tool: open each pane at a matrix of resolutions × UI scales (windowed,
automated), write per-pane screenshot + layout CSV + contact sheets; committed
baseline + diff acceptance, exactly the variant-batch pattern. A formatting fix's
cohort is a materialized list of (pane, widget, resolution) keys — never a prose
description of what should move.

### I18 — Keybind law + matrix + menu  **[feature work authorized]**

1. **Store:** declarative binding file (action id → chord), loaded at startup,
   written by the menu. `[verdict:keybind]` line per dispatch: chord, resolved
   action, consumer (pane/bar/system), or `Unbound`/`Shadowed(by)`.
2. **Defaults:** the vanilla 1.12 default binding set materialized as committed
   data (sourced from benilla assets / the real client), the acceptance baseline.
3. **Matrix sweep:** injector fires every binding in the store and asserts the
   expected verdict/action fired via the verdict ring → matrix CSV vs the default
   set. Runs headless-windowed like pane-sweep; joins the gate set once green.
4. **Menu:** the keybinds pane itself — view/rebind/conflict-detect/save — built
   only after 1–3 exist so the menu is verifiable from day one (its own layout
   table + its edits observable as store diffs).

### I19 — refs/gameplay pane set

One real-1.12 capture session fills `refs/gameplay/pane-<name>-<res>.png` per a
one-page checklist (extends Plan 1 I9); `tools/shot-diff` strips for geometry.
Scheduled once I16/I17 exist so each capture has a layout CSV twin.

## 2. Build order

```
SLICE M (movement instruments):  I11 trace → I12 scripts → I13 audit+baseline  [SPEC 13]
   → HARD STOP: measured-vs-expected table; Nico rules the fix order
SLICE M-fix: benilla-doc's chain, one root cause per order, trace-diff acceptance:
   1 cross-fade+phase preservation   2 intent-driven animation (kills 83ms lag,
   wall-slide misfires, rate jitter)  3 body-heading pipeline (frozen chase,
   turn shuffle)                      4 turn rates π / ×0.75
   5 landing anims 39/187             6 SMSG speed opcodes + ack
   7 step 0.7 / slope 50°             8 swim ladder    9 capsule sweep
   (login-facing-yaw bug from BENILLA_VS_MSUI_MOVEMENT §"outright bug" rides with 3)
I14 benilla goldens: parallel, opportunistic, never blocking.
SLICE P (panes):  I16 layout verdicts → I17 pane-sweep + baseline
   → HARD STOP: formatting cohort ruling → fix orders per pane
SLICE K (keybinds): I18.1 store → I18.2 defaults → I18.3 matrix → I18.4 menu
SLICE P and K spec AFTER Slice M instruments land (M-fix and P/K can then
interleave — disjoint code).  I19 refs: first 1.12 session after I16.
```

Rationale: instruments before fixes (standing law 1) — the M harness must exist
before any of the nine movement fixes, because each fix's acceptance is a trace
cohort. Movement outranks panes by Nico's ruling; keybinds feature work is
authorized but sequenced behind its own instruments so the menu ships verifiable.

## 3. Acceptance doctrine (carried from 7C, now standing)

Every fix order in these planes follows the SPEC-08/12 pattern: query-derived,
materialized expectation files committed next to the baseline; acceptance =
exact-set / in-band equality against them; deviations hard-stop and revert; never
adjust an expectation to pass; one commit per root cause; gates at every boundary
(build, combat-wire, portrait-camera, and move-audit-check once I13 lands, then
pane-sweep-check and keybind-matrix-check as they arrive). Implemented ≠ verified:
Nico's live runs remain the only path to verified, and CHECKS_GAMEPLAY grows a
Session 3 (movement feel + panes) as the instruments come up.

## 4. Nico's asks

1. Paste your exact benilla PowerShell launch line (and working directory) into
   SETUP.md or the chat — I14 stage A starts from it.
2. Session 2 (V1–V4, V2b) is still the open live gate from the 7C chain; it can
   share a sitting with the first movement-script live run.
3. One real-1.12 session later for I19 pane refs — not needed yet.
