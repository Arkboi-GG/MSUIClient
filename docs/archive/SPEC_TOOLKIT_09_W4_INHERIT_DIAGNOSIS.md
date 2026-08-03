# SPEC_TOOLKIT_09 — W8: W4 inheritance diagnosis (report-only, HARD STOP)

Binding rules: `SPEC_TOOLKIT_00_ORDERS.md`. This is a DIAGNOSIS order. No
renderer change lands in the accepted tree. The only commits permitted are:
one diagnostic key-list file, diagnosis artifacts under
`variant-batch/diagnosis/`, and the report append. The spec ends at HARD STOP
for Nico's ruling between two pre-framed options. Do not implement either.

## Context

W4 (7C-2a npc-bare head composite) hard-stopped correctly: all 8,889 authority
rows changed exactly, but 689 rows outside the authority also changed —
`textureType=8, region=facial-hair, effectiveTexture only`. The pilot has
independently recomputed, from the committed
`variant-batch/baseline/npc-extras/verdicts.csv`, that this outside set is
fully determined by the baseline:

- Query: `textureType=8 AND region=facial-hair AND (exists a row of the same
  specimen in cohort-7c2a.keys with a strictly lower batchIndex)`.
- Pilot-predicted result: **689 rows across 410 specimens**.
- Invariant A: every such row has `resolvedTexture=NONE`.
- Invariant B: every such row's baseline `effectiveTexture` equals the
  `effectiveTexture` of its nearest preceding cohort-7c2a row (zero
  mismatches) — i.e. unbound facial-hair type-8 inherits the head slot's
  effective binding, which is why W4's composite leaked into them.
- Invariant C: the remaining 241 type-8 facial-hair rows (930 total) all live
  in specimens whose cohort-7c2a rows sit only at HIGHER batch indices, so
  they inherit from something earlier than the head slot and did not change.
- Invariant D: the nearest preceding cohort-7c2a row of every one of the 689
  carries `predicted7C2Texture` beginning `composite://npc-bare/` (zero
  exceptions).

These numbers are PREDICTIONS to be confirmed by your own queries against the
same committed baseline; per standing law 5, the materialized key list you
generate — not any transcribed count — becomes the diagnostic authority.

## W8-1 — accepted-tree integrity check

The close-out claims the accepted tree is byte-equivalent to the W3 renderer
state. Prove it mechanically: diff `7829bdb..ed37e8f` and report the complete
changed-file list verbatim. Expected: documentation, checklists, committed
authority/key lists, and report appends only — zero production source files.
Any production source file in that diff is itself a HARD STOP finding;
report it and stop the spec immediately.

## W8-2 — materialize the diagnostic cohort

From the committed baseline `verdicts.csv` ONLY (not from any sweep rerun):

1. Generate `variant-batch/baseline/npc-extras/cohort-7c2a-inherit.keys`
   using the query above, same comment-tolerant format as the other cohort
   files, sorted, with the query recorded in the header. Label it in the
   header as DIAGNOSTIC EVIDENCE — it is not acceptance authority for
   anything until Nico rules.
2. Verify invariants A–D and report actual-versus-predicted counts
   (689 rows / 410 specimens / 241 remainder / zero mismatches on B and D).
3. If the rejected W4 run's changed-row artifact still exists, set-compare it
   against this key list and report the three-way counts (both / only-list /
   only-run). If it was not preserved, state that plainly — do not
   reconstruct it.
4. Commit the key list. This is the one baseline-adjacent commit of the spec.

Any actual-versus-predicted deviation in step 2: report it and HARD STOP
without proceeding to W8-3.

## W8-3 — visual ruling evidence from history (no tree change)

Nico must rule on what unbound facial-hair type-8 SHOULD show. Produce
side-by-side evidence without touching the accepted tree:

1. Identify the exact W4 implementation commit that `be31ac6` reverted (read
   the revert commit's referenced hash; report it).
2. In a temporary git worktree (or a separate throwaway clone directory —
   never the working tree), check out that commit and build it there.
3. Run a focused batch in BOTH builds (accepted `ed37e8f` and the historical
   candidate) over a deterministic sample: the first 16 specimens of
   `cohort-7c2a-inherit.keys` in sorted key order. Verdict CSVs must come
   from the normal batch code path (report must equal act).
4. Deliver under `variant-batch/diagnosis/7c2a-inherit/`: the two CSVs, and
   paired contact sheets cropped to the head/face region, one row per
   specimen, accepted-left / candidate-right, filenames carrying the
   specimen key.
5. Discard the temporary worktree/clone. Confirm in the report that the
   accepted tree was never modified (git status clean apart from the
   artifacts and key list above).

If the historical commit does not build in isolation, report the exact
failure and stop — do not patch it.

## W8-4 — HARD STOP ruling packet

End the report append with this decision framed for Nico, no recommendation
weighting beyond the evidence:

- **Option A — inheritance is correct.** Unbound facial-hair type-8 should
  follow the head slot; the 689 changes are a legitimate consequence of
  7C-2a. Consequence if ruled: `cohort-7c2a-inherit.keys` is promoted to
  acceptance authority alongside `cohort-7c2a.keys`, and W4 reruns against
  the union (8,889 + 689 = 9,578 rows; any other change still rejects).
- **Option B — inheritance must be pinned.** Unbound facial-hair type-8 must
  keep the dressed baked atlas; the candidate needs an implementation change
  so exactly the 8,889 authority rows change and the 689 stay byte-identical.

Do not implement either option. Do not edit `cohort-7c2a.keys`,
`variant-items-known-issues.txt`, or any expected list. W5 remains stopped.
7C-3 remains queued and untouched pending its own ruling.

## Gates and report

Standard three gates (build, combat/wire, portrait-camera 1,224/1,289/56) on
the accepted tree after your commits. Append one section per stage to
`SPEC_TOOLKIT_REPORT_2026-07-30.md` with actual-versus-predicted blocks.
En-route findings go to the FINDINGS table only.
