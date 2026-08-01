# NIGHT_01C 3-3 — standing spell animations

Status: `CLOSED-FINDING`

## Increment

- Added a `spell-animation` verdict channel whose row binds spell, school,
  visual stage, SpellVisualKit, authored animation ID, renderer-requested ID,
  renderer-played ID, resolution kind, and the renderer's active composited
  presentation state.
- Corrected the earlier single-column instrument: base locomotion
  `CurrentAnimation` is not authoritative for spell presentation. The renderer
  now exposes base, action, hold, and effective presentation states; spell
  verdicts read track 2 for precast/channel and track 1 for release/cast.
- Server start/go/channel paths emit the same verdict. A bounded synthetic DBC
  presentation command exercises the identical CharacterRenderer methods for
  repeatable screenshots without claiming a server cast.
- Added a reusable Skia contact-sheet tool and produced a labeled two-stage,
  seven-school sheet plus machine-readable index.

## Batch result

- The corrected run passed 62/62 protocol steps with delivered `.gps` controls
  and GM off.
- Physical, Fire, Frost, Arcane, Holy, Nature, and Shadow each produced precast
  and cast rows and screenshots: 14/14 authored IDs equaled requested and
  played IDs. Resolution was `Exact` or `BakedOnDemand`; renderer states were
  the matching `Anim51`, `Anim52`, `Anim53`, `Anim54`, `Anim56`, or `Anim58`.
- The initial run is preserved because it caught the instrumentation defect:
  it read track 0 and falsely reported `renderer=none`. No conclusion uses
  those stale rows.
- Live server-driven start remains unpromoted because item 3-2's admitted Slam
  send received no `SMSG_SPELL_START`. The server hooks are instrumented; this
  item closes on renderer-mechanical evidence with perceptual judgment queued.

## Perceptual boundary

The contact sheet is evidence only. Whether each pose looks correct is queued
as Q16; no visual-quality claim is made here.

## Primary evidence

- `live-runs/runner-20260801-095739.csv`
- `live-runs/verdicts-20260801-095739.txt`
- `live-runs/spell-animation-20260801-095739.csv`
- `live-runs/N1C-3-3-standing-animation-contact-sheet-20260801-100100.png`
- `live-runs/N1C-3-3-standing-animation-contact-sheet-20260801-100100.txt`

## Boundary gates

- Debug build: PASS, 0 warnings / 0 errors on final incremental build.
- combat-wire: PASS.
- portrait-camera: PASS, 10,534 / 1,224 / 1,289 / 56.
- move-audit-check: PASS.
