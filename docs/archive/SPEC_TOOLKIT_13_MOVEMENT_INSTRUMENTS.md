# SPEC_TOOLKIT_13 — Slice M: movement trace, input scripts, move-audit (HARD STOP)

Binding rules: `SPEC_TOOLKIT_00_ORDERS.md`. Plan authority:
`GAMEPLAY_FOUNDATION_PLAN_2.md` §I11–I13. This order is INSTRUMENTS-ONLY:
additive DevTools/tools work in the `Program.DevTools.*` pattern. **Zero
behavior changes to physics, animation selection, playback, input handling, or
the wire** — the point is to measure the current tree exactly as it is. The
final stage is a report-only measured-vs-expected table ending at HARD STOP for
Nico's fix-order ruling. Scope fence: none of the nine movement fixes from
`BENILLA_VS_MSUI_MOVEMENT.md` may be started, including the login-facing-yaw
bug; en-route findings go to the report FINDINGS table.

## M0 — trace recorder (I11)

Per-tick CSV `dumps/movetrace-<name>.csv` with the column set from Plan 2 §I11,
sampled from the live `CharacterController`, `M2Animator`,
`LocalMovementSender` state (report=act; no recomputation). DevTools toggle +
auto-on during script runs. `[verdict:move]` ring entries on gait/air/clip
transitions, including from→to clip ids and clipTime at the cut. Shown ⇒
copyable applies to every new panel affordance.

Gate: build + existing three checks; a manual WASD run produces a trace whose
row count ≈ tick count and whose clip transitions match what was played.

## M1 — scripted input player (I12)

`movement-scripts/` format: optional `fixed-dt <ms>` header, then
`<t> press|release <key>` lines. The player substitutes the input source with
the timeline through the SAME `MovementInput` path as the keyboard. Commit a
flat-arena vantage (`movement-arena` — pick flat terrain, record in
`vantages.json`) and these eight scripts:

- `run-start-stop.txt` — idle 1 s, W 3 s, release, idle 1 s
- `jump-flat.txt` — idle 0.5 s, W 1 s, SPACE (tap) while W held, W 2 s more
- `jump-standing.txt` — idle 0.5 s, SPACE tap, idle 2 s
- `backpedal.txt` — S 3 s
- `diagonal.txt` — W+strafe-right 3 s, then S+strafe-right 3 s
- `turn-standing.txt` — turn-left key 2 s, release, idle 0.5 s
- `turn-moving.txt` — W held, turn-left 2 s during it
- `strafe-pure.txt` — strafe-left 2 s, strafe-right 2 s

Gate: same script twice at fixed dt ⇒ traces identical in all kinematic
columns (byte-comparable after the timestamp column is dropped). If any
nondeterminism remains, name its source in the report — do not paper over it.

## M2 — move-audit + expectations + baseline (I13)

`tools/move-audit <trace> <expected.csv>` → verdicts CSV, one row per measured
quantity: name, measured, expected, band, PASS/FAIL. Commit
`movement-scenarios/expected/*.csv` with these laws (benilla/vanilla citations
in comments; bands explicit):

```text
jump apex height        1.6405 yd   ±0.03      (v0²/2g; v0=7.9558 g=19.2911)
jump apex time          0.4124 s    ±0.017     (one 60Hz tick)
jump airtime (flat)     0.8249 s    ±0.034
run speed               7.00 yd/s   ±0.05      (config fallback; server law later)
backpedal speed         4.50 yd/s   ±0.05
diagonal magnitude      == pure-axis speed ±0.05 (no √2 inflation; S+strafe
                        must obey the backpedal component law — measure both)
stop distance           0.00 yd     ±0.05      (instant stop)
standing turn rate      2.8 rad/s   ±0.05      EXPECTED-CURRENT (vanilla π —
                        dual row: current-tree band AND vanilla-law band)
moving turn rate        2.8 rad/s   ±0.05      EXPECTED-CURRENT (vanilla π×0.75)
start latency (displacement)  ≤ 1 tick
start latency (clip change)   measure; EXPECTED-CURRENT ≈ 83 ms lag band,
                        vanilla-law band ≤ 1 tick
stall windows           0          (intent-moving ∧ Stand/rate≈0 > 150 ms)
phase resets            count them (clipTime→0, gait unchanged) — no pass band
                        yet; this is the cross-fade diagnosis number
Substituted events      0
```

The DUAL-BAND columns (current-tree vs vanilla-law) are the deliverable: the
audit must show which rows the current tree passes against itself and fails
against the law. Never edit a law band to match the tree (standing law 6);
`EXPECTED-CURRENT` rows exist precisely so the gap is a recorded number, not a
failure to explain away.

Run all eight scripts; commit traces to `movement-scenarios/baseline/` and the
audit verdicts beside them. `tools/move-audit` in diff mode becomes gate
**move-audit-check** (against EXPECTED-CURRENT bands only, so the gate is green
on the unfixed tree and every future fix changes bands deliberately, by
editing the expectation file in the fix's own commit with the law citation).

## M3 — benilla launch capture (I14 stage A, report-only)

Record in `SETUP.md`: Nico's PowerShell launch line for benilla (ask via the
report if not already supplied), working directory, build command if any.
Assess and report: can it take scripted input / be built with a trace dumper?
NO benilla modifications in this order — assessment only.

## M4 — HARD STOP: measured-vs-expected packet

Append the full audit table (all scenarios × both band sets) to the report,
plus the phase-reset and stall counts, plus the M3 assessment. End with the
fix-order ruling packet for Nico: the nine-item chain from
`BENILLA_VS_MSUI_MOVEMENT.md` §"Suggested order of attack", each annotated
with which audit rows would prove it, so Nico can reorder or strike items with
the numbers in front of him.

Standard gates at every stage boundary; one commit per stage; report appends
per stage with actual-versus-predicted blocks.
