# SPEC_TOOLKIT_12 — W11: W4 acceptance under ruled Option B (pinned)

Binding rules: `SPEC_TOOLKIT_00_ORDERS.md`. Unattended execution per the
SPEC-08 pattern: pre-ruled mechanical acceptance, auto-proceed on exact
match, HARD STOP on any deviation.

## Ruling record

Nico ruled **Option B** (2026-07-31), superseding the voided Option A ruling
of SPEC-10, after SPEC-11/W10-1 established that the original inheritance
implementation existed only in an unrecoverable dirty working tree
(`306e030-dirty`) and that evidencing Option A would require new code.

- W4 acceptance authority is `variant-batch/baseline/npc-extras/
  cohort-7c2a.keys` ALONE: exactly 8,889 rows.
- `cohort-7c2a-inherit.keys` is DEMOTED back to diagnostic evidence. Its 689
  rows are a forbidden-change cohort: they must remain byte-identical. Do
  not edit either file; this spec is the ruling record.
- Deferred question, parked not closed: whether unbound Tauren facial-hair
  type-8 geosets should instead inherit the npc-bare composite. Reopening
  requires a new implementation order plus discriminating three-way
  evidence; trigger is Nico's live Session 2 verdict on Tauren heads.

## W11-1 — reinstate and accept

Reinstate `48c16dc` exactly (revert of `be31ac6` or equivalent). Zero new
implementation deltas; any source edit beyond mechanical conflict
resolution is a HARD STOP. Then full NPC sweep (6,939 / 64,650 keys) and
full items sweep (3,944). Accept only on ALL of:

```text
changed row keys == cohort-7c2a.keys: 8889, authority-only 0, candidate-only 0
every authority row's new texture == its baseline predicted7C2Texture
cohort-7c2a-inherit rows changed: 0 of 689 (byte-identical)
changes outside authority: 0
type-6 rows: all 7,677 W3 bindings unchanged, UNBOUND type-6 count 0
attachment cohort: all 3,535 remain mounted; Willem row still mounted with
  Helm_Plate_B_01Stormwind_HuM.m2 / patch.MPQ
control npc-extra:54:display:3340:batch:15 still binds
  Character\Human\Hair02_09.blp / texture.MPQ
unexpected blanks 0; NPC G3 0; items changedRows 0, gated G3 0
```

Any deviation: revert, report actual-versus-predicted, HARD STOP. One
commit for the W4 root cause.

## W11-2 — W5 rebaseline (unblocked)

Only after W11-1 accepts. Original SPEC-08 W5 scope: current canonical
NPC-extras and items baselines move to `variant-batch/history/` (dated);
post-7C sweeps become the new canonical baselines; regenerate
baseline-derived summaries. The cohort files and the diagnosis directory
remain committed, frozen, as the 7C acceptance and forensic record; do not
regenerate them against the new baseline.

## W11-3 — close-out

Update the SPEC-08 close-out matrix (W4 accepted-as-Option-B with this
spec cited; W5 done) and refresh `CHECKS_GAMEPLAY.md` Session 2:

- V2 collapses to a full-PASS expectation on scalp/ears (type-1 composite
  present in accepted form).
- Add V2b: several Tauren NPCs with facial-hair/horn variants — do chin and
  horn geosets look correct? A FAIL here is the trigger to reopen the
  parked inheritance question via a new order, with the pasted line as
  evidence. It is NOT a regression of this acceptance.

7C-3 remains queued and untouched. Standard three gates at every stage
boundary; append one report section per stage with actual-versus-predicted
blocks; end with the refreshed close-out matrix.
