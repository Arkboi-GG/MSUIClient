# NIGHT_03 tier-1 doc 3 — the spell matrix (flagship)

Parent: `TIER0_MASTER.md` item 0-3. Harness (doc 2) required. Roster (doc 1)
required. **Depth-first by class (R2)** — a fully-closed class beats nine
half-classes.

## Committed class order

3-1 Mage · 3-2 Warrior · 3-3 Priest · 3-4 Rogue · 3-5 Warlock ·
3-6 Druid · 3-7 Hunter · 3-8 Paladin · 3-9 Shaman

Mage first: widest instant/cast-time/channel spread with the cleanest
self-target and hostile split, so it exercises every cell type early. Warrior
second because NIGHT_01C's only prior data is Warrior — it is the regression
check on the new instrument.

## Cohort rule (pre-declared, per law 5 and Tier-0 instrument law 4)

For each class: **every non-passive spell, every rank, known at level 60,
untalented** (R5). Materialize the cohort as a committed key-list file from a
read-only DBC/spellbook query BEFORE the sweep runs. Never an enumeration of
what the client happens to handle. Passives are recorded as a separate
`PASSIVE` cohort and excluded from cast cells, not silently dropped.

## Cells per spell-rank (each is one verdict row, not one row per spell)

- **a. Cast standing.** Result enum (named, never generic), animation verdict
  per doc 2, cast time vs DBC/server, GCD start, cooldown start.
- **b. Cast while moving.** The stated rule matrix: instants MUST cast;
  cast-time MUST refuse or interrupt. Verdict per cell, plus the blend
  verdict (2-4). An instant that fails while moving is a FINDING; a cast-time
  spell that completes while moving is a FINDING.
- **c. Channel (channeled spells only).** Start, tick count and tick timing,
  stop, movement interrupt, animation loop continuity across ticks.
- **d. Resource and timing.** Resource type, before/cost/after deltas vs
  server packets; GCD honored; cooldown honored on immediate re-cast.
- **e. Does what it says.** The mechanical effect landed, verified against
  read-only DBC/DB expectation — aura applied (descriptor delta),
  damage/heal landed (health delta on self or spawned target), summon/pet
  appeared (object create), teleport moved (position delta), stat modified
  (descriptor delta). Tooltip text is NOT the expectation source.
- **f. Error path.** Where a refusal is expected (range, LOS, resources,
  target validity), the named error displays — reusing the 3-8 family that
  closed PASS in NIGHT_01C.

## Target types

Self and friendly legs are the priority and are fully testable. Hostile and
ground-target legs run against disposable spawned creatures. Any leg that
receives no server response after doc 1 is a single
`BLOCKED-BY:F-SILENT-INTERACT` row (Amendment 10) — no per-spell
re-diagnosis — and the cell still closes on its client increment plus
byte-checked wire evidence. **Positive-control law stands: every live leg
carries a delivered `.gps` control** so "opcode silent" stays distinguishable
from "session dead."

## Per-class close-out

A class closes only with: the committed cohort key-list; the full sweep CSV
with STRING verdict columns and the three-state coverage column; the
animation verdict counts (`ANIM-EXACT` / `ANIM-FALLBACK` / `ANIM-STATIC` /
`ANIM-ASSET-MISSING`); the coverage ratio stated BEFORE any PASS count; the
per-school sequence contact sheets; and a run-dated manifest. Then append
PROGRESS and take the next class the same working turn.
