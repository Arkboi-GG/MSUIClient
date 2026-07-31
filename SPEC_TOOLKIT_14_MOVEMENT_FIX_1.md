# SPEC_TOOLKIT_14 — Slice M fixes, part 1: law correction, doc reconcile, F1 cross-fade, F2 JumpStart

Binding rules: `SPEC_TOOLKIT_00_ORDERS.md`. Plan authority
`GAMEPLAY_FOUNDATION_PLAN_2.md`. Nico's ruling (2026-07-31): revised chain
approved — this order covers the expectation-law correction, the stale-doc
addendum, and fixes F1 + F2. F3–F6 (speed opcodes, step/slope, swim, capsule
sweep) are LATER ORDERS — do not start them; they need new scenario coverage.
One commit per root cause; standard gates (build, combat-wire,
portrait-camera, move-audit-check) at every stage boundary; HARD STOP at end.

Pilot verification underlying this order (recomputed from committed baseline
traces): turn rates exactly π / 0.75π; regressed g=19.2911, v0=7.9558; strafe
body−aim offset exactly ±90°; landing selects 39 standing / 187 moving; run
clip starts same-frame as input with no rate ramp; ALL 12 clip transitions
across the eight scripts are hard cuts starting at clipTime ≈ one tick;
liftoff enters Jump 38 directly (no JumpStart 37); measured jump apex
1.57473 yd @ 0.400 s equals the symplectic-Euler@60Hz prediction from the
exact constants to five decimals.

## S1 — jump expectation-law correction (no behavior change)

The four vanilla-law jump FAILs are discretization artifacts: the law bands
were authored from continuum formulas, but a fixed-dt symplectic integrator
with the CORRECT constants lands measurably below the continuum apex
(v0·dt/2 ≈ 0.066 yd at 60 Hz). Per the W2-correction precedent (a mis-derived
authority is corrected transparently; standing law 6 forbids bending a
correct law, not fixing a wrong one):

1. Re-author the jump law rows integrator-aware. The law check becomes
   dt-independent: regress g from airborne velZ slope and recover
   v0 = velZ(first airborne tick) + g·dt; law bands
   `g = 19.2911 ±0.001`, `v0 = 7.9558 ±0.001` (citations: benilla
   state.rs constants, matching MSUI's). Keep apexHeight/apexTime/airtime
   rows but derive their law bands from the symplectic prediction at the
   trace's dt (state the formula in the citation column).
2. Record in the file header and the report WHY the bands changed, with the
   before/after rows. The current-tree bands are untouched.
3. Re-run move-audit on the committed baseline traces: expect 44/44
   current-tree PASS and vanilla-law FAILs = 0.

## S2 — BENILLA_VS_MSUI_MOVEMENT.md reconcile addendum (doc only)

Append a dated section: for each of the original nine items, its MEASURED
status with the trace/audit row that proves it (items 2, 3, 4, 5:
implemented, cite the numbers above; 1: absent — 12/12 hard cuts; JumpStart
handoff: absent; 6, 7, 8, 9: untested by current scripts). Do not rewrite
the historical analysis; the addendum supersedes it. Also correct the
phase-reset metric definition in move-audit: it now counts EVERY clip
transition whose incoming clipTime < blend window as a hard cut
(`hardCuts` column), so F1's acceptance is measurable. Re-baseline verdicts
accordingly (traces unchanged).

## S3 — F1: cross-fade + phase preservation

Implement the two-clip mixer per benilla's law (driver.rs:358 blend over the
incoming clip's own blend_time; phase preservation on unchanged gait,
driver.rs:1009-1018; landing does not re-pick gait on bracket-less step-off,
driver.rs:893-899 decision 0187). Trace recorder gains columns:
`clipB, clipBTime, blendWeight` (additive; empty when not blending) — land
this instrument delta in the SAME commit chain BEFORE the mixer so the
mixer's first sweep is fully observable.

Acceptance (all eight scripts re-run, fixed dt):

```text
hardCuts: 0 across all scripts (every transition shows a blend ramp:
  blendWeight strictly increasing over the incoming clip's blend window)
kinematic columns byte-identical to baseline traces (pos/vel/yaw/flags/
  grounded/speeds) — the mixer may not change physics
current-tree audit rows: all PASS with unchanged bands except hardCuts
gait phase preserved: in run-start-stop, Run resumed after a same-gait
  interruption keeps clipTime continuity (no reset to ~0); in jump-flat the
  landing 187 one-shot still starts at 0 (one-shots legitimately start at 0)
no Substituted / MissingClip events
```

Any kinematic drift or surviving hard cut: revert, report, HARD STOP.

## S4 — F2: JumpStart 37 → Jump 38 handoff

Per benilla select.rs:210-220: 37 armed at jump initiation; 38 only after
37's own window (~833 ms clip duration governs; cite the exact measured clip
duration from AnimationData at implementation time). Acceptance: re-run both
jump scripts — liftoff sequence is JumpStart(37) → Jump(38, only if still
airborne past 37's window) → Fall(40 per existing latch) → 39/187 landing;
short standing jump (airtime 0.8 s < window ⇒ 37 → landing pick directly if
37 hasn't elapsed — match benilla's law, cite the branch); all other audit
rows unchanged; kinematics byte-identical.

## S5 — rebaseline + close-out (HARD STOP)

Move pre-fix baseline traces/verdicts to `movement-scenarios/history/`
(dated); commit post-F1/F2 traces as the new baseline; move-audit-check gate
now enforces hardCuts=0. Append the full before/after audit table and end at
HARD STOP: packet for Nico listing F3–F6 with the scenario coverage each
needs (F3: wire/GM speed change + ack observation; F4: stair/slope course
vantage + scripts; F5: water vantage + swim ladder scripts; F6: collision
course), so the next order can be scoped with his input on available test
terrain. CHECKS_GAMEPLAY gains Session 3: movement feel items (start/stop,
turn, strafe body, jump arc, landing pop gone) with paste slots.
