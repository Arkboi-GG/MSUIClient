# SPEC_TOOLKIT_11 — W10: A-vs-B discriminating evidence (report-only, HARD STOP)

Binding rules: `SPEC_TOOLKIT_00_ORDERS.md`. REPORT-ONLY. No renderer change
lands in the accepted tree; permitted commits are diagnosis artifacts under
`variant-batch/diagnosis/7c2a-inherit/` and report appends. Ends at HARD STOP
for Nico's re-ruling.

## Context (pilot findings, verified from committed artifacts)

The Option A ruling is VOID: it rested on non-discriminating evidence.

- The committed W8-3 diagnosis CSVs show `48c16dc` changed all 32 sampled
  type-1 authority rows and 0 of the 30 sampled inherit-cohort type-8 rows.
  `48c16dc` is therefore the carry-isolated CORRECTION authored during the
  original W4 violation — an Option-B-shaped implementation — not the first
  candidate that produced the original 8,889+689 run. W8-3 step 1 identified
  the wrong commit; the W9-2 outcome (8,889 exact, 0/689) is consistent
  behavior, not a new defect.
- Every visible improvement in the W8-3 pairs (Tauren horns) comes from
  type-1 rows present in BOTH options. The incremental visual effect of the
  689 type-8 rows has never been rendered.
- The entire inherit cohort is race 6: all 689 rows / 410 specimens are
  Tauren; row geosets are the 1xx/2xx facial-hair groups.

## W10-1 — forensic commit identification

Enumerate the W4-era history precisely. Report, with hashes and subjects:
the first-candidate commit (the implementation that produced the ORIGINAL
W4 acceptance run with 8,889 + 689 changes), the correction commit
(expected: `48c16dc` or its source), and exactly what `be31ac6` reverted.
If the first candidate was amended/squashed and no longer exists as a
distinct commit, identify the nearest historical tree that contains the
uncorrected inheritance behavior (e.g. the state the first full sweep was
run from) and justify the identification from commit metadata and diffs —
not from memory. If no such tree exists in history, say so and HARD STOP:
reconstruction would be new implementation and needs its own order.

## W10-2 — discriminating paired render

1. Build the identified first-candidate tree in an isolated worktree/clone
   (never the accepted tree).
2. Rerun the deterministic 16-specimen sweep from SPEC-09 W8-3 in that
   build. GATE: in its CSV, the sample's 30 inherit-cohort type-8 rows must
   ALL change effectiveTexture relative to accepted, inheriting each
   specimen's preceding head row's composite value, and the 32 type-1 rows
   must match `predicted7C2Texture`. If the inherit rows do not change, the
   commit identification is wrong — return to W10-1 or HARD STOP.
3. Produce a THREE-WAY contact sheet, head-cropped, one row per specimen:
   accepted (`ed37e8f`) | pinned (`48c16dc`, reuse committed W8-3 renders) |
   inherited (first candidate). Add a fourth column: per-pixel difference
   heatmap between pinned and inherited, so subtle facial-hair texel
   changes are visible even where the geoset is small.
4. Extend the sample deterministically for coverage of every facial-hair
   geoset id present in the inherit cohort: for each distinct geosetId
   (expected set includes 102-107, 202-206), take the first specimen in
   sorted key order bearing it that is not already sampled. Render those
   specimens in all three builds and append them to the sheet, labelled by
   geosetId.
5. Commit CSVs and sheets under `variant-batch/diagnosis/7c2a-inherit/`,
   subdirectory `three-way/`. Discard temporary builds; confirm the
   accepted tree is untouched.

## W10-3 — HARD STOP re-ruling packet

Append a packet restating the two options with the new evidence linked:

- **Option A — inheritance:** accept the first candidate against the frozen
  9,578-row union (SPEC-10 W9-2 criteria, reinstatement target corrected to
  the W10-1 commit).
- **Option B — pinned:** accept `48c16dc` against `cohort-7c2a.keys` alone:
  exactly 8,889 rows change, the 689 inherit rows byte-identical, all other
  SPEC-10 W9-2 criteria unchanged. Note plainly that W9-2 already ran this
  exact acceptance shape and passed everything except the union count.

No recommendation weighting. W5 and 7C-3 remain untouched. Standard three
gates after commits; actual-versus-predicted blocks per stage.
