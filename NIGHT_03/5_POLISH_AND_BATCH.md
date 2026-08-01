# NIGHT_03 tier-1 doc 5 — consolidation, contact batch, end packet

Parent: `TIER0_MASTER.md` item 0-5. Harness (doc 2) required.

- **5-1 Missing-visual consolidation.** ONE table of every spell-rank whose
  expected model or animation asset was absent: spell id, name, rank, class,
  the exact missing `MPQ:file` path, and the DBC chain that pointed at it
  (SpellVisual → SpellVisualKit → model). This is the run's single
  `SHELVED-BLOCKED` cohort and the one consolidated queue entry the art law
  requires. **Nothing in this table was substituted, approximated, or
  redrawn.**
- **5-2 Fallback-animation roll-up.** Every `ANIM-FALLBACK` cell: which spell
  played which generic animation instead of which expected ID. Ranked by
  frequency of the substituted ID, because one missing fallback source
  usually explains many rows.
- **5-3 Instrument-debt close-out.** Every `NOT-INSTRUMENTED` /
  `ANIM-NOT-INSTRUMENTED` row from the whole run: close it or state exactly
  why it cannot be read from the acting path. Instrument debt carried
  forward silently is the NIGHT_02 defect repeating; this item exists to stop
  that. Carry forward the NIGHT_02 UI instrument debt note as an explicit
  cross-reference.
- **5-4 Timing and rule-matrix audit.** Across all classes: cast times vs
  DBC, GCD honored, cooldowns honored, channel tick counts and intervals, and
  the moving-cast legality matrix. One CSV, deltas only, so systematic timing
  drift surfaces as a pattern rather than 2,000 individual rows.
- **5-5 Final contact batch.** The run's headline deliverable to Nico: one
  labeled, run-dated set of sequence contact sheets covering every class and
  every race-animation replay, organized per class per school. ONE queue
  entry referencing all sheets, for a SINGLE perceptual sign-off pass.
- **5-6 Hygiene + end packet.** Manifests recompute (immutable-artifact scope
  only; record both worktree and committed-blob hashes). No stray raw art
  dumps. All gates green. End-of-run packet per Tier 0, including classes
  closed vs remaining so the next night resumes cleanly at the first OPEN
  class.
