# SPEC TOOLKIT 08 — Unattended 7C run (pre-ruled, auto-proceed)

Nico is away for several hours. This order chains the ruled 7C work WITHOUT
per-stage human rulings, replacing them with **mechanical acceptance criteria
computed from the committed baselines**. SPEC 00 remains binding. The one
override of prior process: a stage whose acceptance matches EXACTLY is
self-ruled ACCEPTED — commit and continue. ANY mismatch: hard-stop that chain,
record everything, leave the tree buildable, and continue only with stages
independent of the failed one. Never adjust a law, list, or expected cohort to
make an acceptance pass — that is the one unforgivable move.

Baselines (committed, canonical): `variant-batch/baseline/npc-extras/` (6,939
specimens; 7,677 type-6 miss rows across 5,114 specimens; 3,535 not-mounted;
protocol rows Willem 2072/675 b12 and 3340/54 b15/b18) and
`variant-batch/baseline/items/` (3,944; 50 unmounted NONE-model helms; 26 G3
UNBOUND demands incl. customs 67218–67221). Portrait baseline unchanged.

Work order, in sequence:

## W0 — report reconciliation (15 min)
Establish from the report whether Stage 1G (Portrait Lab copy affordances; wire
recorder toggle placement; portraits.target `latest` dump nit) ever shipped.
If not, implement it now exactly per the "1G — panel affordance fixes" order in
the report/conversation record; one commit. If shipped, state where.

## W1 — items-axis failure classification (report-only, no fixes)
Classify all 76 failing items rows (50 unmounted + 26 G3; note overlaps) into:
(a) vanilla junk — no model / misfiled texture stems (Wand/Cape/Shoulder under
Head\), (b) plausibly-real vanilla rows (the Helmet_AhnQiraj_* family —
determine whether those BLPs exist anywhere in the archives under any path),
(c) **Nico custom content** — displays 67218–67221: determine whether the
demanded Custom_*.blp files are absent from his patches or the sweep's demanded
-path derivation is wrong for them. Attempt the vmangos read-only connection
once for item_template references (SETUP.md may carry details by now); if
unreachable, record zero-queries and classify without reference data, marking
reference-status unknown. Output: `variant-items-known-issues.txt` (same
tolerant format family), G3 gate counts only rows in no list, summary gains the
bucket counts. Correct the prior summary's "no custom rows among G3" claim in
the report with the UNBOUND-supplier explanation. One commit.

## W2 — 7C-1: mount authored NPC helm/shoulder equipment
Implement per the recorded protocol: feed `ExtEquipment[0]`/`[1]` through the
shared attached-item renderer with race/sex suffix resolution, alongside
virtual weapons, sync and async paths both.
**Acceptance (auto-proceed):** first materialize and commit
`variant-batch/baseline/npc-extras/cohort-7c1.keys` from the baseline query
`Ready AND (helmDisplayId != 0 OR shoulderDisplayId != 0)`, and verify it is
element-wise identical to the distinct specimen set having any
`attachmentStatus=not-mounted` row. Then run a full NPC-extras re-sweep +
`--diff` vs baseline: changed specimens == exactly `cohort-7c1.keys`; their
`attachmentStatus` all become mounted; Willem 2072/675 renders helmeted (his
batch-12 scalp row may legitimately change subject/luma — the STRING columns
must change only as predicted, i.e., not at all for texture bindings in this
stage); zero changes outside the cohort; items re-sweep `changedRows=0`;
standard gates. Exact → commit → W3. Else STOP chain.

## W3 — 7C-2b: bind type-6 hair from CharSections
Per protocol: treat type 6 as the character hair slot for character-model NPCs;
resolve race/sex/hairStyle/hairColor with the literal-1 fallback.
**Acceptance:** first materialize and commit the exact baseline type-6 miss
row-key set as `cohort-7c2b.keys` using the stated baseline CSV query. Re-sweep:
all rows in that key list now have
resolved==demanded (real CharSections paths, correct suppliers); zero UNBOUND
type-6 rows; NPC G3 → 0; protocol row 3340/54 b15 shows exactly
`Character\Human\Hair02_09.blp`; zero non-type-6 string changes beyond W2's
accepted set; gates. Exact → commit → W4. Else STOP chain.

## W4 — 7C-2a: npc-bare composite for type-1 head regions
Per protocol: build the equipment-free CharSections head/body composite from
the extra row; bind it for type-1 hair/scalp/ear region batches; ordinary
body/clothing type-1 batches keep the baked dressed atlas.
**Acceptance:** first materialize and commit the exact baseline type-1
hair/scalp/ear row-key set as `cohort-7c2a.keys` using the stated baseline CSV
query. Re-sweep: every row in that key list flips
from `Textures\BakedNpcTextures\*` (or UNBOUND) to the composite identifier;
protocol rows b18 (3340/54) and b12 (2072/675) match their `predicted7C2`
strings; ALL other type-1 rows byte-identical; gates. Exact → commit → W5.
Else STOP chain.

## W5 — re-baseline + review artifacts
Regenerate and commit new canonical baselines for npc-extras and items
(previous baselines move to `variant-batch/history/<date>-pre7C/`). Regenerate
contact sheets. Write a REVIEW.md in the baseline dir listing: per-stage
changed-row counts vs predictions, the new G3 totals, and the 10 most visually
changed specimens (by meanLuma delta) as a viewing guide for Nico's sheet
review. One commit.

## W6 — reduced player sweep (independent; run even if W2–W4 stopped)
The sequenced player axis: all races × sexes × each customization axis varied
independently (others held 0), `charSectionsDupKey`/`charSectionsWinnerRow`
populated. Report-only: collisions found → file as the 7C-3 candidate with the
rows as its test protocol; zero collisions → Flags hardening stays a known-gap
note. Commit the baseline under `variant-batch/baseline/players/`.

## W7 — close-out
Update the report (every stage: status, acceptance numbers vs predicted,
deviations). Append to `CHECKS_GAMEPLAY.md` a short "Session 2 — variant fixes"
live section for Nico: Willem helmeted in Northshire; merchants have real hair
(no clothes-texture, no black chunks); one `]`-cycle through Lab specimens;
plus any stage that stopped, stated plainly with its mismatch. Final commit.

## Rails
- Stage-boundary commits only; tree must build at every boundary.
- Every acceptance cohort is a committed key-list file generated by a stated
  query against the committed baseline CSV. Element-wise key-set equality, not
  a chat-transcribed count, is acceptance authority. Numeric descriptions in
  this order are informational and must be regenerated into the stage key list
  before acceptance runs. Never edit a query, key list, or expected set to make
  a candidate pass.
- A failed acceptance is a RESULT, not a problem to make go away — record the
  actual vs predicted diff verbatim and stop that chain.
- No scope beyond this file. No portrait/framing/MPQ/wire changes. Findings
  observed en route go to the report's FINDINGS table untouched.
- If time/context runs short: finish the current stage cleanly, update the
  report, stop. A half-stage is worse than a missing one.
