# SPEC TOOLKIT 04 — Batch calibration, blank allowlist, dark-bake diagnosis, MPQ precedence audit

Slice 2, part 1. Four small stages driven by evidence from the first full sweep
(`portrait-batch/codex-full/`, 10,534 specimens, 2026-07-30). SPEC 00's orders remain
binding. Stages 4A–4C are toolkit; 4D is a core fix with the batch tool as its
regression gate — keep its commit separate.

Evidence this spec is calibrated against (reviewer-computed from `verdicts.csv`):
Ready subjectPx distribution p1=23,793 / p50=45,116 / p99=59,165; min=250, max=65,536;
only 244 of 10,505 Ready rows fall inside the old [800, 30000] band — the band was
guessed and is wrong. 47 Ready rows sit below 8,000 (totems, Wyvern, battle
standards); 13 sit at/above 63,000 (EyeOfKathune at the full 65,536, SteamTonk,
ArcheryTarget, Hakkar). All 17 Blanks are bounds-camera zero-subject; 10 are
`AncientProtector` (a giant — the WINDOW_MAX/giant-framing case), the rest are
invisible/effect models. Contact-sheet review also shows a cohort of near-black
"glowing eyes" bakes that count Ready.

---

## Stage 4A — recalibrate the informational gate + add a luminance column

1. Replace G2's [800, 30000] band with two **informational** lists computed at the
   end of the sweep: `tiny` = Ready with `subjectPx < 8000`; `full` = Ready with
   `subjectPx >= 63000`. Summary prints counts and the 20 most extreme keys of each.
   Constants stay at the top of `Program.PortraitBatch.cs`, commented as derived
   from the 2026-07-30 codex-full distribution (p1≈24k, p50≈45k, p99≈59k).
2. Add one CSV column `meanLuma` — the mean of (r+g+b)/3 over **subject** pixels
   (pixels already classified non-clear by `Analyze`; compute in the same readback
   pass, no second readback). This makes the dark-bake cohort queryable
   (`meanLuma < 25` finds the silhouettes). Column order: insert after `alphaHi`.
   Bump nothing else; `--diff` compares outcome/subjectPx as before and ignores the
   new column when the baseline lacks it.
3. G1 semantics unchanged in this stage.

**Test:** `--limit 200` run shows the new column populated and the two lists in
summary; a `--diff` against the old-format baseline still works.

## Stage 4B — expected-blank allowlist (make G1 meaningful)

New committed file at repo root, `portrait-expected-blank.txt`:

```
# Specimens that are BLANK by design (invisible/utility models).
# One key per line, mandatory trailing comment stating the reason.
# G1 counts only blanks NOT listed here; allowlisted blanks are reported
# separately in summary as "expected-blank: N".
creature:15435   # InvisibleMan — invisible by design
creature:16925   # InvisibleStalkerNoName — invisible by design
```

Seed with exactly those two entries. Do NOT add the Kathune mouth/portal rows or any
`AncientProtector` row — those need a human Lab/1.12 verdict first; they remain
honest G1 failures until Nico rules on them. Loader: tolerant (blank lines, comment
lines, unknown keys warn but don't fail). Summary distinguishes
`G1 blanks (unexpected): N` from `expected-blank: M`.

**Test:** full-sweep summary shows 15 unexpected blanks, 2 expected, exit still 3.

## Stage 4C — dark-bake diagnosis (DIAGNOSIS ONLY — no fix without go)

Hypothesis to test: the specimen booth may bake the base model **without applying
the display row's texture variations** (`CreatureDisplayInfo` texture fields), so
displays that differ only by skin bake identical or untextured-dark. The near-black
glowing-eyes cohort on sheet 1 and visually identical human rows are the evidence
for suspicion; both could also be legitimate (dark models / same-face displays).

1. Read how the live world path applies display textures: from `CreatureDbc.cs`
   (`CreatureModelInfo` folds texture/extended-NPC data) into wherever
   `CreatureRenderer` binds skins. Cite file:line.
2. Read what the specimen/synthetic path (`TryBakeCreaturePortrait` with the
   `ObjectFields` synthetic factory) does with those same fields. Cite file:line.
3. Pick three dark specimens from sheet 1's cohort (report their display ids from
   the CSV) plus two same-model different-skin display pairs. State, from code
   alone, whether the booth resolves their textures identically to the live path.
4. Report: confirmed defect (with the exact divergence) or exonerated (the models
   are genuinely dark/identical), plus the smallest fix if confirmed. **Wait for
   go.** Nico will separately eyeball one of the three in the Lab vs a live spawn.

## Stage 4D — MPQ patch precedence audit (core fix, separate commit, batch-gated)

Current shared order (from the 2026-07-30 diagnosis, verbatim):
`patch.MPQ > patch-4.MPQ > patch-2.MPQ > …` — reverse-lexical, contradicting
`MpqMount`'s own comment that numbered patches beat the base patch. Authority is
the real 1.12 client rule: base archives lowest; `patch.MPQ` above them;
numbered `patch-N.MPQ` above `patch.MPQ` in ascending numeric order (higher N =
higher priority); locale archives follow the same pattern within their tier if
present. Nico's customizations live in numbered patches, so today they can be
shadowed by stock `patch.MPQ` content.

1. Extract the priority computation in `MpqMount.cs:125-149` into a pure function
   `static IReadOnlyList<string> OrderArchives(IEnumerable<string> names)` (names
   in, priority-ordered names out, no I/O) and implement the 1.12 rule there.
   Add unit-style checks to the camera-check tool (it already hosts toolkit
   assertions): feed it name lists like
   `[base.MPQ, patch.MPQ, patch-2.MPQ, patch-4.MPQ, patch-10.MPQ]` and assert the
   exact expected order, including numeric (not lexical) ordering of N and the
   `patch-10 > patch-9` case.
2. At mount time, print the final chain once: `[mpq] priority: …` (both client and
   tools — it's one shared line in `MpqMount`).
3. **Regression gate:** run the full portrait batch with
   `--diff portrait-batch/codex-full/verdicts.csv` after the change. Deliver
   `diff.txt` in the report. Expected outcomes: zero rows changed, or changed rows
   that are explained by previously-shadowed patch content now resolving — list
   each with the supplying archive before/after (extend the tool's provenance
   printer to answer that for any named file). Do not classify the changes as
   good/bad yourself; that ruling is Nico's, made from the diff.
4. Also re-run all three standard gates. The camera counts (1224/1289/56) must
   still hold — those models resolve from `patch.MPQ` today; if a numbered patch
   starts supplying any of them, the counts may legitimately move — if so, STOP
   and report before committing (that would mean Nico's patches contain modified
   character/wolf M2s, and he decides).

**Commit only after Nico reviews diff.txt.**

---

## Definition of done

4A/4B: full sweep reruns green on gates, summary reads
`G1 blanks (unexpected): 15 | expected-blank: 2 | tiny: ~47 | full: ~13`,
meanLuma queryable. 4C: a written verdict with citations, no code change. 4D:
pure-function ordering with passing assertions, the printed chain, and a delivered
diff.txt awaiting Nico's ruling.

## Live checks for Nico (copy into report verbatim)

1. Lab-check the AncientProtector (any of its 10 display ids) and the Kathune
   mouth/portal: are they visible creatures in 1.12? Rule each: framing worklist
   (needs an override / giant-framing law) or expected-blank (allowlist it).
2. Lab-check one dark-cohort specimen against a live GM-spawned one of the same
   display id: same appearance? (Feeds 4C's verdict.)
3. Review 4D's diff.txt: every changed row is your ruling — expected custom
   content surfacing, or a problem.
