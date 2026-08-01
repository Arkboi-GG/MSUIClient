# NIGHT_03 tier-1 doc 4 — racials and race-dependent animation

Parent: `TIER0_MASTER.md` item 0-4. Harness (doc 2) required.

Ruling R1 governs this doc: a Gnome Mage's Frostbolt is the same spell ID and
the same ranks as a Troll Mage's, so the SPELL does not vary by race. Two
things do: **racial abilities**, and **animation** — every race has its own
skeleton and animation set. Sweep those two, not 36 full spellbooks.

- **4-1 Racials per race.** For each of the 8 races, the full racial ability
  set at 60: every cell from doc 3 (standing, moving, channel where
  applicable, resource/timing, does-what-it-says, error path). Cohort
  key-list committed from read-only DBC before the sweep.
- **4-2 Race-dependent animation replay.** For every VALID race/class
  combination, replay the ANIMATION legs only (doc 2 verdicts: `ANIM-*` and
  `BLEND-*`) across a pre-declared representative spell set per class — one
  instant, one cast-time, one channel, one self-buff, minimum. This is where
  a missing or wrong per-race animation actually surfaces. Commit the
  representative set as a key-list file BEFORE the replay runs.
- **4-3 Race/class combo matrix.** Materialize the valid combination list
  from read-only DBC (`ChrClasses` / `ChrRaces` / `CharBaseInfo`), not from
  assumption or memory. Invalid combinations are recorded as `N/A`, never
  silently absent — an unenumerated combo is indistinguishable from a skipped
  one.
- **4-4 Gender.** If the animation set differs by gender for a race, that
  doubles the animation cohort for that race. Determine mechanically from the
  model data; if it does differ, sweep both and say so. Do not assume either
  way.
- **4-5 Per-race findings roll-up.** One CSV: race → class → animation
  verdict counts → the specific spell-ranks that fell back or were static.
  This is the artifact that answers "which races look wrong."
