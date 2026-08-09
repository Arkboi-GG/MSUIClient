# Spells + character rendering session handoff — 2026-08-06

Repository: `C:\Users\nico\source\repos\MSUIClient`

> Snapshot-parity continuation: read
> [`SNAPSHOT_PARITY_STATUS_2026-08-07.md`](SNAPSHOT_PARITY_STATUS_2026-08-07.md) and
> [`SNAPSHOT_PARITY_WORKFLOW.md`](SNAPSHOT_PARITY_WORKFLOW.md) before resuming the Benilla-versus-MSUI
> packet work. The status page contains the 2026-08-08 pause boundary, invalid-evidence corrections,
> current validator baseline, and exact next packet. This document remains the underlying spells and
> character-rendering context.

This is the current-session entry point. Read it before continuing spell behavior, aura UI,
cooldown presentation, or local/remote character animation work. The working tree is intentionally
dirty and contains accumulated user work. Preserve unrelated changes, especially `vantages.json`;
do not reset or mass-format the tree.

## User-validated outcomes

- Fire Ward and Frost Ward use billboard-joint pose **B** by default. The ward particles/ribbons
  are centered on the player's body instead of staying body-right. The A/B remains in the Spell FX
  inspector; other sampled spells were reported normal with B enabled.
- Blink is movement-castable. Spell `InterruptFlags` bit `0x01`, not bit `0x08`, is the movement
  interruption law. The repeated "Interrupted" behavior is fixed.
- Ice Block is not treated as an ordinary root or stun. While its aura is present it freezes the
  exact cast pose, animation clocks, facing, movement, jumping, steering, and actions; aura removal
  releases the freeze.
- Player buffs expose tooltip data and remaining-duration text and can be right-click cancelled
  when the aura is cancelable. Buff icons do **not** draw the action-button radial cooldown clock.
- Action-button cooldowns use the square 1.12 quadrant wipe plus the finish flash. Clicking a spell
  that is still cooling down reports "Spell is not ready yet" without restarting locomotion or the
  character animation.
- The input-reset cause was ImGui keyboard capture from a focused/clicked button. Movement is now
  blocked by actual text entry, settings modal, or keybinding capture—not generic focused UI.
- Remote players enter the networked unit rendering path as character models rather than missing or
  placeholder creature geometry.
- Character model load begins directly in Stand, eliminating the one-frame arms-out bind pose.
- Human `ShuffleLeft`/`ShuffleRight` contain only 17 keyed bones. They now layer their absent
  shoulder/hand channels over the live Stand pose instead of restoring bind-pose arms. The feet
  still use the authored turn shuffle.
- Stationary A/D release catch-up is deliberately slower: default `StationaryChaseRate = 0.8`, so a
  full 90-degree offset closes in about 625 ms rather than about 63 ms. Held-turn ceiling behavior is
  unchanged. DevTools exposes `Turn: release catch-up rate` for live tuning.
- Nico explicitly approved the final ward, cooldown-click, arms, and slower-release-turn behavior.

## Important boundaries

- Billboard pose B is applied only at the spell particle/ribbon/mesh billboard-joint evaluation
  boundary. It must not become a generic camera-facing transform or change root attachment.
- `M2Animator.TurnBasePose` is supplied only by `CharacterRenderer`. Spell-effect and creature
  animators leave it null, so sparse-turn layering cannot alter spell M2 playback.
- Ice Block's exact pose freeze is separate from `_movementRooted`. Roots may still allow casting
  and animation; Ice Block may not.
- Cooldown readiness and cooldown presentation are separate. A rejected cooldown cast must not tear
  down or restart character presentation.
- Aura duration is server aura-duration state, not the cast cooldown. Do not put action-button
  cooldown sweeps back onto buff icons.
- The stationary body/aim chase is whole-body heading lag. `TorsoCounterYaw` remains for moving
  split-strafe (and the explicit force-angle diagnostic), not stationary chase.

## Code map

| Concern | Primary files |
|---|---|
| Ward billboard B + inspector | `Program.SpellFxInspector.cs`, `Program.cs`, `SpellMeshSkinningLaw.cs`, `SpellRibbonRenderer.cs`, `SpellEffectSource.cs` |
| Blink interrupt semantics | `Formats/SpellCatalog.cs`, `tools/interface-wire-check/Program.cs` |
| Aura state, duration, cancellation, Ice Block | `Program.DevTools.Auras.cs`, `Program.UnitFrames.cs`, `Program.cs`, `Net/WorldSession.cs`, `Net/NetworkClient.cs`, `World/Units/CharacterRenderer.cs` |
| Cooldown square wipe/flash | `Engine/UI/CooldownVisualLaw.cs`, `Program.ActionBars.cs`, `Net/PlayerActions.cs` |
| Cooldown-click locomotion guard | `Engine/UI/GameplayInputLaw.cs`, `Program.cs` |
| Remote character rendering | `World/Units/CreatureRenderer.cs`, character/display DBC and object-field readers |
| Turn pose and catch-up | `World/Units/M2Animator.cs`, `World/Units/CharacterRenderer.cs`, `World/Units/CharacterPoseLaw.cs` |
| Empty gameplay-text hover guard | `Engine/UI/GameTextLaw.cs` |

The earlier hover crash stopped at `GameTextLaw.Draw` with an empty string passed into ImGui text.
The empty-text guard fixes that path. The later Silk.NET `Reset inside render loop` exception was the
shutdown consequence seen after the unhandled draw exception; if it ever occurs without the text
exception, treat it as a separate window-lifecycle investigation.

## Current automated evidence

- `dotnet build MSUIClient.sln --no-restore`: pass, 0 errors. The known unrelated CA2014 warning in
  `Engine/UI/GlueAdditive.cs` may appear on a cold rebuild.
- `tools/spell-animation-lifecycle-check`: **PASS (5,810 checks)**. The added HumanMale fixture
  samples both sparse turn clips at eleven phases and keeps hand span within `0.01` of Stand.
- `tools/spell-frame-law-check`: pass, including gameplay-input capture, cooldown wipe/flash,
  stationary release-rate cap, and held-turn ceiling laws.
- `tools/spell-particle-motion-check`: **PASS (100 checks)**.
- `tools/spell-mesh-skinning-check`: **PASS (84,393 checks)**.
- `tools/interface-wire-check`: pass.
- `git diff --check`: pass; line-ending conversion warnings are pre-existing workspace policy noise.

## First live smoke test in the next session

Do this before changing another shared rendering or animation law:

1. Relog and confirm there is no arms-out pulse before movement.
2. Stand still, hold/release A and D: shoulders remain in Stand, feet shuffle, and a 90-degree body
   lag closes over roughly half a second. Tap versus hold should both look coherent.
3. Cast Fire Ward and Frost Ward from front and side cameras; confirm body-centered B behavior.
4. Cast Blink while moving; retry it during cooldown. The retry should show the not-ready text with
   no animation or locomotion reset.
5. Cast Ice Block while moving/turning; confirm the exact cast pose and facing are frozen, actions are
   blocked, and aura cancellation cleanly restores control.
6. Hover timed and untimed buffs, then right-click a cancelable buff. Confirm duration text but no
   radial cooldown sweep.
7. Observe at least one remote player after login/appearance load.

## Where to continue

- Spell-FX structural work still follows
  `docs/current/spells/SPELL_FX_SEMANTIC_PARITY_NEXT_AGENT_PROMPT.md`; its next independent lane is
  D-001 WMO-floor decal projection. Do not absorb that lane into character work.
- Historical Benilla/MSUI movement research remains in
  `docs/current/research/BENILLA_VS_MSUI_MOVEMENT.md`. Its source-reading record is useful, but the
  dated 2026-08-06 correction at the end governs sparse turns and release catch-up feel.
- Gameplay live acceptance remains in `docs/current/project-context/CHECKS_GAMEPLAY.md`; use its M2
  standing/moving-turn check together with the seven-point smoke test above.
