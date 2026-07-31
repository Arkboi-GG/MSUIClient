# SPEC TOOLKIT 07 — Character-variant regression: diagnosis + the variant pipeline

You are a FRESH session. Your memory of this project is zero; the repository's is
not. Read, in this order, before any edit:

1. `SPEC_TOOLKIT_00_ORDERS.md` — **binding for everything below.** Symbol
   verification by grep before use; additive-only edits; no drive-by fixes; the
   three gates after every stage; `implemented ≠ verified`; STOP points are hard.
2. `SPEC_TOOLKIT_REPORT_2026-07-30.md` — the cross-session memory. Establish from
   it (do not assume): whether Stage 1G (panel affordance fixes) and the NPC
   extra-display diagnosis were completed after the Slice 2 section, and the
   current canonical batch baseline path.
3. `GAMEPLAY_FOUNDATION_PLAN.md` — why instruments precede fixes here.
4. `SPEC_TOOLKIT_03_BATCH_BAKE.md` + `MSUIClient/Program.PortraitBatch.cs` — the
   pattern and scaffolding you will extend (do NOT build a parallel one).

Report protocol: append a "SPEC 07" section to the same report file; same tables
(stages, files, symbol verification, deviations, findings, console evidence, live
checks). Stage-boundary commits: `toolkit: variants-<stage> <summary>`.

---

## 0. The defect (live evidence, 2026-07-30)

- Player characters show **wrong appearance variants** ("people have the wrong
  characters") — skin/face/hair mapping wrong.
- **Head items (helms) and capes render incorrectly** — wrong or missing BLPs.
- Standing earlier finding, folded in: humanoid NPCs lose their authored
  dressing — Deputy Willem (Northshire) is helmeted in real 1.12, bald/generic
  in MSUI (`CreatureDisplayInfoExtra` pipeline).

This is a regression claim plus a correctness claim. Your job is FIRST a
diagnosis with named evidence, THEN an instrument that measures the whole
variant space, THEN — only on explicit go — fixes verified through that
instrument. Never fix ahead of the instrument: a fix you cannot sweep is a
guess.

## 1. Ranked hypotheses (falsifiable, test in this order)

**H1 — archive-precedence fallout (test first; cheapest; currently most
likely).** Commit `16a2c14` (Stage 4D) corrected MPQ priority to the real 1.12
rule. Its regression gate was the portrait batch — which **skips players
entirely** (`Skipped/unsupported-v1`) and never touches `CharSections.dbc`,
`CharHairGeosets.dbc`, `CharacterFacialHairStyles.dbc`, `ItemDisplayInfo.dbc`
texture fields, or `Character\*`/`Item\*` BLPs. The accepted "2 rows changed"
diff was blind to this plane. Test WITHOUT running the game: `OrderArchives` is
a pure function — compute the old (reverse-lexical) and new (1.12) orders,
enumerate every file whose supplying archive differs between them, and filter to
`*.dbc`, `Character\*`, `Item\*`, `Textures\*`. Build this as a small mode in
the camera-check tool (it already hosts provenance printing). Deliverable: the
changed-supplier list. If `CharSections.dbc` or character BLPs appear, H1 is
essentially confirmed — then determine which copy is CORRECT (the 1.12 order is
law; if the newly-resolving copy renders wrong, the defect is in Nico's patch
content or in code that was tuned against the wrong copy — report, don't
decide).

**H2 — variant-mapping code regression.** `git log` since 2026-07-27 over the
character-customization path: section/geoset resolution, texture compositing,
helm/cape attachment code. List every commit touching them with file:line;
correlate with when Nico observed the break.

**H3 — never-correct (the camera-parser pattern).** The docs have already been
caught once claiming verification that never ran. Treat any doc claim about
variant mapping as a requirement, not history. If neither H1 nor H2 explains a
symptom, assume the mapping was always wrong for that case and diagnose against
the reference law directly.

**Reference law for all of it:** Benilla at `C:\Users\nico\Desktop\benilla-main`
— its char-section resolution, hair/facial-hair geoset selection, NPC-extra
dressing, helm model/suffix selection, and cape texture path. Cite file:line for
every rule you rely on. MSUI screenshots are defect reports, never authority.

## 2. Stage 7A — diagnosis (report-only, then STOP)

Deliver: the H1 changed-supplier list; the H2 commit table; for three concrete
cases — (a) one wrong player variant Nico names, (b) Deputy Willem's helm,
(c) one wrong cape — the full resolution trace as a table: input ids → DBC rows
consulted (with values) → geosets chosen → BLP paths resolved → supplying
archive → what Benilla would have chosen, with citations. State the root cause
per symptom (may differ per symptom) and the smallest fix. **STOP for go.**

## 3. Stage 7B — the instrument: `--variant-batch`

Extend the in-client batch host (`Program.PortraitBatch.cs` pattern; shared
flags/output conventions; new mode name `--variant-batch`). Same architecture
decision as SPEC 03 §0, not revisitable: in-client, real renderers, no parallel
pipeline. Sweep axes:

1. **Player variants:** every race × gender × valid (skin, face, hairStyle,
   hairColor, facialHair) combination — validity from the same DBC-driven
   bounds char-create uses. Full-body bakes via the paper-doll render target
   (466×448 exists in `Program.Portraits.cs`). This is large (tens of
   thousands); support `--limit`/`--list`, chunked cache release as SPEC 03,
   and a default *reduced* sweep (all races/genders × each axis varied
   independently while others hold 0) with `--exhaustive` for the full cross.
2. **NPC extras:** every `CreatureDisplayInfoExtra` row, dressed per the
   (fixed) pipeline.
3. **Head + back items:** every ItemDisplayInfo referenced as helm or cape by
   any item template the client can enumerate offline (fallback: all
   ItemDisplayInfo rows with the relevant fields set), rendered on one fixed
   humanoid (HumanMale) full-body.

CSV verdict per specimen — the decisive columns are **strings, not pixels**:
every resolved section/composite BLP path + its supplying archive, geoset ids
chosen, helm model + suffix, cape texture path, plus the usual
outcome/subjectPx/meanLuma. Contact sheets as SPEC 03. Gates: G1 blanks
(allowlist/known-blank files reused), plus `G3 resolution`: any specimen whose
CSV row contains an empty/missing BLP where the DBC demanded one. `--diff`
compares resolved-path columns too — a future precedence or mapping change
shows up as path diffs even when pixels are subtle.

## 4. Stage 7C — fixes (only after go, one commit per root cause)

Each fix's gate: targeted `--variant-batch --list` of the affected cohort
before/after, plus the full standard gates, plus the reduced sweep with
`--diff` against the 7B baseline showing only intended rows changed. Then a
live-check list for Nico (Willem, one named player variant, one cape — at most
five items, each pasteable via the panel or F10).

## 5. Boundaries

- Portrait/creature framing, MPQ ordering itself (4D stands), and everything
  green in CHECKS_GAMEPLAY are out of scope — touch nothing there.
- If 1G or the NPC diagnosis turn out NOT to be done (report check, §above),
  fold the NPC diagnosis into 7A; leave 1G for its own order unless Nico says
  otherwise.
- The wrongness rulings are Nico's: your output is traces, tables, sheets, and
  diffs, never "this looks right to me."
