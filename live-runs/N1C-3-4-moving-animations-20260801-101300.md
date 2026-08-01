# NIGHT_01C 3-4 — moving spell legality and animation blending

Status: `CLOSED-FINDING`

## Instrument-before-fix finding

The first seven-school moving sweep proved the spell renderer was resolving the
right action IDs, but `RestartCombatAction*` cleared `_clip`, `_previousClip`,
and the active blend before the mixer could transition. Moving spell poses
therefore hard-cut from locomotion; the requested cross-fade had no source pose.

## Increment

- Preserved the outgoing locomotion clip when starting spell holds/releases.
  The existing `ChooseClip`/`SwitchClip` path now owns the transition, retaining
  its phase and cross-fade law instead of receiving a cleared mixer.
- Extended `spell-animation` rows with moving state, DBC legality,
  movement-interrupt flags, base/previous/action/hold clips, and exact blend
  weight from the renderer.
- Added a post-tick sampler so the transition is measured after the renderer
  advances, not at the presentation request boundary.

## Mechanical result

- A protected post-fix probe passed 30/30. Arcane Explosion transitioned
  `Run -> Anim52` at blend weight `0.2344`; its release transitioned
  `Anim52 -> Anim54` at `0.7065`. Fireball release likewise measured
  `Anim54 -> Anim53` at `0.6939`.
- The final seven-school moving capture passed 66/66 with every sampled row
  `moving=True` and all authored/requested/played IDs equal. Arcane Explosion
  (instant) is `legal_while_moving=True`; Slam, Fireball, Frostbolt, Holy Light,
  Healing Touch, and Shadow Bolt are cast-time and
  `legal_while_moving=False` under their interrupt flags.
- The GM-off moving Battle Shout leg passed the mechanical pre-send gate and
  flushed while the renderer reported `Run`; it received no server spell event
  in four seconds and has one `BLOCKED-BY:F-SILENT-INTERACT` row. No new server
  root cause is registered.
- Item 3-2's GM-off Slam leg supplies the complementary real movement-cancel
  evidence: `CANCEL_SEND/MOVEMENT_CAST` followed by the server's named
  `SPELL_FAILED_INTERRUPTED` response.

## Perceptual boundary

The final post-fix seven-school contact sheet is queued as Q17. It is not used
to self-approve visual quality.

## Primary evidence

- `live-runs/runner-20260801-100259.csv` and adjacent spell/animation CSVs
- `live-runs/runner-20260801-100757.csv` and adjacent animation CSV
- `live-runs/runner-20260801-100921.csv` and adjacent animation CSV
- `live-runs/N1C-3-4-moving-animation-fixed-contact-sheet-20260801-101100.png`
- `live-runs/N1C-3-4-moving-animation-fixed-contact-sheet-20260801-101100.txt`

## Boundary gates

- Debug build: PASS, 0 warnings / 0 errors on final incremental build.
- combat-wire: PASS.
- portrait-camera: PASS, 10,534 / 1,224 / 1,289 / 56.
- move-audit-check: PASS.
