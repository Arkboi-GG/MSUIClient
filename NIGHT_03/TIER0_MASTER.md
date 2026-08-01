# NIGHT_03 TIER 0 — full spell validation, animation-first (autonomous long-run work order)

Binding rules: `SPEC_TOOLKIT_00_ORDERS.md`; all PILOT_PROTOCOL standing laws
(1–12); the NIGHT autonomy amendments, RE-INHERITED in full —
`NIGHT_01/1B_RESUME_ORDER.md` (narrow blocker definition, attempt floor, no
batch shelving, build-don't-shelve) and `NIGHT_01/1C_AUTONOMY_AMENDMENTS.md`
(Amendment 9 continuous execution, 10 one-finding server silence, 11
self-provisioning, 12 sweep discipline) — plus the NIGHT_02 instrument-truth
amendments restated in full below, because they are young, they were learned
the hard way, and this run will re-earn them if it forgets them.

Read `NIGHT_01/1B`, `NIGHT_01/1C`, and the "Instrument truth" section of this
file before starting. Then read the tail of `NIGHT_02/REPORT.md` for the
instrument-debt carry-forward.

## Mission

Validate every player-castable spell in the game — **every class, every rank** —
and prove that each one **ANIMATES**.

NIGHT_01C built the spell instruments and they work: pre-send gate, named
result enums, DBC/server cast-bar timing with pushback, the renderer's own
animation-state channel, the moving cross-fade matrix, channel start/tick/stop
verdicts, the aura apply/duration/cancel family, the effect-kit supplier sweep,
and named spell-error display. Items 3-5, 3-6 and 3-8 closed PASS.

What NIGHT_01C did not have was **scale**. It ran on ONE character —
TEST/Warrior, 65 non-passive spells — and stopped: *"The account reached its
10-character cap after creating `Nbwarhuman`; stale-character deletion was not
permitted, so the remaining class representatives were not fabricated around."*

This run removes that wall. The instruments exist. Execute them at full scale.

## The acceptance criterion is ANIMATION

Not "the animation-state field was populated." Not "an effect supplier
resolved." **The character visibly plays the cast, and the spell's visual
actually renders, in a captured live sequence.** Two layers, both required:

1. **The caster animates.** The body plays the correct cast / channel
   animation from its own animation set — standing, and while moving it
   BLENDS with locomotion rather than hard-cutting. NIGHT_01C 3-4 found and
   fixed exactly those hard-cuts; that fix must hold across every class and
   every race, because each race has its own skeleton and race is precisely
   where this changes.
2. **The spell animates.** Precast glow, cast-hand effect, projectile in
   flight, impact on target, persistent aura visual — the SpellVisual chain
   resolving to a real model and actually drawing.

### Two guards (this is where a metric would define the finding away)

- **A generic or fallback animation is a FINDING, never a PASS.** The verdict
  compares the animation ID actually played against the spell's EXPECTED
  animation from read-only DBC. "Something moved" is not the test. A renderer
  substituting a default cast because the specific one is missing is a gap,
  and is recorded as one.
- **Proof of animation is temporal, so the evidence must be.** A single
  screenshot cannot show motion. Every animation cell produces a FRAME
  SEQUENCE: mixer animation ID + timestamp + locomotion state per frame, read
  from the renderer's own state, spanning precast → cast → impact → settle.
  "Did it animate" is answered by state CHANGING OVER TIME. NIGHT_01C's
  14-frame standing sheets are the shape; that is now the standard for every
  cell, standing and moving.

### Expected largest finding class

NIGHT_01C item 3-7 swept ONE class and found **40 of 67 spells resolved a
model supplier — 12 chains had no model at all, 15 had no visual.** If that
ratio holds across nine classes and every rank, missing spell visuals will be
the biggest finding class in this run, larger than anything in NIGHT_02.
That is a RESULT, not a failure. Record it precisely and keep going.

**Art law (the one hard fraud line, unchanged from NIGHT_02):** a missing
spell visual or animation asset is `SHELVED-BLOCKED` with its EXACT path and
one consolidated queue entry. Never substituted, never approximated, never
redrawn, never downloaded. Every visual claimed as present cites its
`MPQ:file` source and its DBC chain (SpellVisual → SpellVisualKit → model).

## Instrument truth (NIGHT_02 amendments, binding here)

NIGHT_02 shipped panels whose "actual" capture declared its own verdict
columns as string literals typed at the instrumentation call site, and whose
PASS counts were element-subsets chosen after the fact. All four corrections
apply to every instrument in this run:

1. **Derive, never declare.** Every verdict column is read from the SAME
   variable the acting code consumes — the animation ID the mixer actually
   played, the resource value the packet actually carried, the model path the
   renderer actually bound. A value that cannot be read from the acting path
   is emitted EMPTY and the diff records a DELTA. Typing an expected value
   into an instrument argument is a fabrication-class defect, same tier as
   invented art.
2. **Three-state coverage, not two.** Every sweep row carries
   `MEASURED` / `NOT-INSTRUMENTED` / `NOT-PRESENT`. `NOT-PRESENT` is the real
   gap; `NOT-INSTRUMENTED` is instrument debt and must be closed, not counted
   as a gap and not counted as a pass.
3. **Coverage ratio is a headline number.** Every item's REPORT line states
   `measured / total applicable cells` and the `NOT-PRESENT` count BEFORE any
   PASS count. A PASS count without its coverage ratio is void.
4. **Cohorts are pre-declared, never post-hoc.** If a subset is swept, its
   SELECTION RULE is committed as a file next to the baseline BEFORE the sweep
   runs (law 5), and the rule is a property of the reference (e.g. "all
   non-passive ranks known at level 60, untalented"), never an enumeration of
   what the client happens to handle. No "representative cohort" language.

## Rulings pre-issued by Nico (2026-08-02) — do not re-litigate

- **R1 Race multiplier.** Full spellbook × rank sweep once per CLASS (9).
  Racials swept per RACE (8). **Animation legs replayed across all valid
  race/class combinations**, because animation is the thing race actually
  changes. Do NOT re-run full spellbooks per race/class combo.
- **R2 Order.** Depth-first by class, in the committed order in
  `3_SPELL_MATRIX.md`. A fully-closed class beats nine half-classes. The run
  spans multiple nights; each night closes whole classes.
- **R3 Silence first.** `F-SILENT-INTERACT` gates roughly every hostile leg
  in this matrix. Diagnose it FIRST (doc 1). The account supplied in the
  launch directive is full GM/admin, which is the unlock item 1-1 was waiting
  on.
- **R4 Deletion fence (HARD LAW).** The agent creates and deletes ONLY
  characters it created, matching its own `NB*` roster prefix. A
  pre-existing character is never deleted, renamed, stripped, or logged in
  to. Before any delete, the roster CSV must show the agent created that
  name in this run or a prior NIGHT run. Violating this is the one action
  that ends the run.
- **R5 Talents.** Untalented level-60 baseline is the acceptance cohort.
  Talented is a SECOND pass, recorded separately. Never mix them in one
  baseline.

## Perceptual vs mechanical split

**MECHANICAL (agent accepts autonomously):** animation ID played vs DBC
expected; frame-sequence state change; blend vs hard-cut at movement onset;
cast success/refusal/interrupt per the movement rule matrix; GCD, cooldown,
resource cost and delta vs server packets; channel start/tick-count/stop
timing; effect landed (aura descriptor delta, health delta, object create,
position delta); visual model supplier resolution and MPQ citation.

**PERCEPTUAL (contact sheet, queued, never blocking):** does the fireball
LOOK right; does the animation FEEL like vanilla; art quality judgments.
No perceptual claim is the agent's to make.

## Numbering, navigation, ledger, statuses

Identical to NIGHT_02 Tier 0. `PROGRESS.md` and `RULINGS_QUEUE.md` are
append-only and live in THIS folder. Per-item evidence is run-dated with a
SHA-256 manifest under `live-runs/`. Per-item sections append to
`NIGHT_03/REPORT.md`. Anti-lost loop after every item: append status, re-read
this file, re-read PROGRESS tail, take first OPEN item. Status vocabulary
unchanged (CLOSED-PASS, CLOSED-FINDING, SHELVED-RULING, SHELVED-BLOCKED).

**Manifest scope:** hash ONLY immutable run-dated artifacts, never mutable
source files or append-only ledgers. Record BOTH the worktree hash and the
committed-blob hash (or mark run CSVs binary in `.gitattributes`) so pilot
recompute is one step — the `* text=auto` CRLF divergence cost a full
verification pass in NIGHT_02.

## Continuous execution (Amendment 9)

Mid-run status is FILE APPENDS ONLY. Closing an item immediately starts the
next OPEN item, same turn. Nothing waits for Nico — rulings go to the queue
and you keep going. Your next and ONLY chat message is the end-of-run packet.
A relaunch message of `continue` = resume at first OPEN item, no summary.

## TIER 0 ITEMS (execute in order)

- **0-1 → `1_SILENCE_AND_ROSTER.md`** — kill `F-SILENT-INTERACT`, then
  self-provision the full roster under the deletion fence. Everything
  downstream depends on both.
- **0-2 → `2_ANIMATION_HARNESS.md`** — build the animation acceptance
  instrument: DBC expected-animation table, frame-sequence capture, the
  fallback-detection differ, and the sequence contact-sheet generator. Prove
  it end to end on ONE spell before fanning out.
- **0-3 → `3_SPELL_MATRIX.md`** — the flagship. Every class, every spell,
  every rank, every cell.
- **0-4 → `4_RACE_AND_RACIALS.md`** — racials per race; animation legs
  replayed across all valid race/class combinations (R1).
- **0-5 → `5_POLISH_AND_BATCH.md`** — missing-visual consolidation, the
  full contact batch for Nico's single perceptual pass, hygiene, end packet.

## End-of-run packet (mandatory, last commit)

Append to `NIGHT_03/REPORT.md`: full status table of every item; classes
closed and classes remaining; total spell-ranks swept vs total applicable;
**animation PASS vs FALLBACK vs NOT-PRESENT counts**; the consolidated
missing-visual list with exact paths; the rulings-queue count; gates summary;
and the three most important findings. Add a pointer line to the main
SPEC_TOOLKIT report. Leave the tree clean.
