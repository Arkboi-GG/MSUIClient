# NIGHT_03 tier-1 doc 2 — the animation acceptance harness (build before fanning out)

Parent: `TIER0_MASTER.md` item 0-2. This is the instrument the whole run is
accepted on. Build it, prove it on ONE spell end to end, then fan out.

Instrument-truth law applies to every column here: **derive, never declare**
(Tier 0). Any column that cannot be read from the acting path is emitted
EMPTY and diffs as a DELTA.

- **2-1 Expected-animation reference table.** From read-only DBC, build the
  committed expected table per spell-rank: SpellVisual → SpellVisualKit →
  animation ID(s) and model/effect paths for precast, cast, impact, state,
  and channel. STRING columns; cite `MPQ:file` for every model resolved and
  `MISSING:` for every one that is not. This is the reference side — it is
  extracted, never transcribed.
- **2-2 Frame-sequence capture (the core).** Capture a SEQUENCE, not a
  frame. Per cell, sample the renderer's OWN mixer state across
  precast → cast → impact → settle: frame index, timestamp, active animation
  ID, blend weights, locomotion state, and whether a spell effect model is
  currently bound. Minimum 14 samples per cell (NIGHT_01C's standing-sheet
  shape), spanning the full cast plus 0.5 s either side. Output one CSV per
  cell plus the captured frames.
- **2-3 Fallback detector.** Compare the animation ID actually played against
  2-1's expected ID. Verdict enum, one per cell:
  `ANIM-EXACT` (expected ID played),
  `ANIM-FALLBACK` (a generic/default cast played instead — **FINDING, not
  PASS**),
  `ANIM-STATIC` (no state change across the sequence — the spell did not
  animate),
  `ANIM-NOT-INSTRUMENTED` (instrument debt — close it, do not count it),
  `ANIM-ASSET-MISSING` (expected model absent from MPQ — shelve with exact
  path, never substitute).
  A cell PASSES only on `ANIM-EXACT` with a proven state change across the
  sequence.
- **2-4 Blend verdict (moving cells).** At movement onset during a cast,
  record whether the mixer cross-faded or hard-cut. NIGHT_01C found hard-cuts
  and fixed them; this verdict proves the fix holds per class and per race.
  `BLEND-CROSSFADE` / `BLEND-HARDCUT` / `BLEND-NOT-INSTRUMENTED`.
- **2-5 Sequence contact sheets.** Per spell-rank per cell, a labeled,
  run-dated strip of the captured frames in order with the animation ID and
  timestamp burned into each frame, so a human can see motion at a glance.
  Batched per school/class for Nico's single perceptual pass. Queued, never
  blocking.
- **2-6 End-to-end proof.** Run 2-1..2-5 on ONE spell that NIGHT_01C already
  proved answers the server (Drain Life or Power Word: Fortitude), standing
  and moving, and commit the full artifact set. If the mixer state cannot be
  read from the renderer's own path at all, that is the run's one legitimate
  early hard finding — record it, file the queue entry, shelve dependents.
  **Do not fabricate a sequence to keep moving.**
