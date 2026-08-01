# NIGHT_01 tier-1 doc 3 — spells

Parent: `TIER0_MASTER.md` item 0-3. All Tier-0 rules apply. The interface
loop from doc 2 applies; additionally every cast claim uses a Z0-style
mechanical pre-send gate (target/self validity, GCD state, resource state)
so send-precondition drift can never contaminate evidence again.

- **3-1 Cast correctness (wire).** CMSG_CAST_SPELL construction, cast
  result/failed opcodes decoded to named enums in a verdict channel, GCD
  and cooldown bookkeeping vs server packets, resource (mana/rage/energy)
  deltas, target-type matrix (self, friendly, hostile, ground where the
  class set allows). Batch over the test roster's known spellbooks; CSV
  verdicts with STRING columns.
- **3-2 Cast bar + cast-time behavior.** Cast bar start/duration vs
  server cast time, pushback display, cancel (move/escape), instant vs
  cast-time vs channeled classification from DBC (read-only).
- **3-3 Animations — standing.** Cast/channel animation triggers while
  stationary: instrument the animation-state channel (report=act from the
  same mixer state the renderer uses — the single-column-instrument
  lesson applies; extend the instrument before concluding "absent").
  Batch screenshot/contact sheets per spell school + queue for Nico's
  perceptual pass.
- **3-4 Animations — moving.** The stated rule matrix: which casts are
  legal while moving (instants) vs interrupted by movement; animation
  blending with locomotion (cross-fade mixer instrumentation from SPEC-13
  is the base); verify interrupt packets fire when they should.
- **3-5 Channeled spells.** Start/update/stop opcodes, tick timing,
  movement interrupt, animation loop.
- **3-6 Auras/buffs/debuffs.** Aura apply/remove wire flow, duration
  display, stack counts, right-click cancel, debuff display on target
  plate (ties to NEXT_02).
- **3-7 Visual effect presence sweep.** Batch sweep: for each castable
  spell, does a visual/projectile/impact effect supplier resolve
  (STRING verdict: resolved path/supplier), plus contact sheets.
  "Renders correctly" is perceptual → queue; "resolves and draws
  something" is yours.
- **3-8 Spell errors.** Error text display for cast failures (out of
  range, LOS, resources) — same client-display authority as item 1-3.
