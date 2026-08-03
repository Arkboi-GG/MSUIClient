# SPEC_TOOLKIT_10 — W4 resume under ruled extended authority (Option A)

Binding rules: `SPEC_TOOLKIT_00_ORDERS.md`. Unattended execution per the
SPEC-08 pattern: pre-ruled mechanical acceptance, auto-proceed on exact
match, HARD STOP on any deviation. Nico has ruled **Option A** on the
SPEC-09/W8-4 packet (2026-07-31): the type-8 facial-hair inheritance is
correct behavior; the 689 rows are part of the fix.

## Authority (ruled)

W4 acceptance authority is the union of two committed, byte-frozen files:

- `variant-batch/baseline/npc-extras/cohort-7c2a.keys` — 8,889 type-1 rows
- `variant-batch/baseline/npc-extras/cohort-7c2a-inherit.keys` — 689 type-8
  rows, hereby PROMOTED from diagnostic evidence to acceptance authority by
  this ruling. Do NOT edit either file — not even the header comment; this
  spec is the promotion record. Union: **9,578 row keys, zero overlap**
  (verify the zero-overlap claim; a nonzero intersection is a deviation).

## W9-1 — reinstate the candidate

Reinstate the exact historical implementation `48c16dc` (revert of `be31ac6`
or equivalent cherry-pick). Zero new implementation deltas are authorized: if
reinstatement requires any source edit beyond mechanical conflict resolution,
report the conflict and HARD STOP.

## W9-2 — full acceptance sweep

Full NPC sweep (6,939 specimens / 64,650 stable row keys) and full items
sweep (3,944). Accept only on ALL of:

```text
changed row keys == union authority: 9578, authority-only 0, candidate-only 0
every cohort-7c2a row's new texture == its baseline predicted7C2Texture
every cohort-7c2a-inherit row: effectiveTexture-only change, new value ==
  nearest preceding cohort-7c2a row's new composite value
changes outside the union: 0
type-6 rows: all 7,677 W3 bindings unchanged, UNBOUND type-6 count 0
attachment cohort: all 3,535 remain mounted; Willem row still mounted with
  Helm_Plate_B_01Stormwind_HuM.m2 / patch.MPQ
control npc-extra:54:display:3340:batch:15 still binds
  Character\Human\Hair02_09.blp / texture.MPQ
unexpected blanks 0; NPC G3 0; items changedRows 0, gated G3 0
```

Any deviation: revert the reinstatement, report actual-versus-predicted, and
HARD STOP. One commit for the accepted W4 root cause.

## W9-3 — W5 rebaseline (unblocked)

Only after W9-2 accepts. Execute the original SPEC-08 W5 scope: move the
current canonical NPC-extras and items baselines to `variant-batch/history/`
(dated), commit the post-7C sweeps as the new canonical baselines, and
regenerate baseline-derived summaries. The four cohort files remain committed
where they are as the frozen 7C acceptance record; do not regenerate them
against the new baseline.

## W9-4 — close-out

Update the SPEC-08 close-out matrix (W4/W5 rows) and refresh
`CHECKS_GAMEPLAY.md` Session 2: V2's split verdict collapses to a full-PASS
expectation (real hair AND no clothing-atlas bleed on scalp/ears/horns);
add a V2b line for Tauren-horn NPCs referencing the contact-sheet fix.
7C-3 remains queued and untouched. Standard three gates (build, combat/wire,
portrait-camera 1,224/1,289/56) at every stage boundary; append one report
section per stage with actual-versus-predicted blocks.
