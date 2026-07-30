# July 30, 20206 — 9 AM handoff

> The filename and heading preserve the requested `20206` spelling. The actual handoff date is
> **2026-07-30**.

## Purpose and authority

This handoff records the full correction cycle for the 1.12 gameplay-UI parity work. The `NEXT_*`
documents supplied the starting backlog. For behavior and presentation, the only implementation
reference trusted during this pass was the Benilla source tree at
`C:\Users\nico\Desktop\benilla-main`. MSUI observations and screenshots were treated as defect
reports, not as authority for inventing replacement behavior.

The repository already contained unrelated and in-progress changes. They were preserved. The work
below was made on top of that dirty tree and must not be interpreted as a clean, isolated patch.

## What the conversation established

### First live review

- The bottom bar being flush with the screen was accepted.
- Reaction-colored overhead names were wanted, but the text was too large. Floating target plates
  were explicitly rejected and must not be restored merely because `NEXT_02_TARGET_PLATE.md`
  originally requested them.
- The purple XP bar was accepted.
- Action-bar page buttons were visible but displaced and inert.
- The ping meter was displaced.
- Portrait framing was mostly wrong. In particular, changing between creatures appeared to cycle
  unrelated PNG/sample portraits. That behavior was rejected; portraits must come from the actual
  selected model and Benilla framing law.

### Second live review

- Live portraits began appearing, but unit-frame names needed the same gold, faint-outline and
  shadow treatment used by the 1.12 glue screens, at the correct HUD scale.
- Overhead unit names were correctly white where applicable, but needed Benilla's stronger black
  shadow and no outline.
- The action-bar arrows were in the correct area, but hover exposed an incorrect black/red-arrow
  state and clicks did not page the bar.
- The Character micro-menu portrait was missing.
- The action-page number and player-frame level number were incorrectly blue; they should be gold.
- The backpack escaped its authored slot under UI scaling; bag placement must remain relative to
  the authored bar geometry at every supported scale.

### Third live review

- The Character micro-menu portrait was still missing.
- The backpack had no proper hover response; its behavior needed to be copied from Benilla.
- Spell-button hover darkened icons until they were unreadable.
- The cast-bar moving highlight had an incorrect thick black shadow.
- Casting Holy Light with a hostile wolf selected incorrectly targeted the wolf. A friendly spell
  must not heal a hostile creature; in that case the client should cast on the player.
- Movement animation appeared frozen after casting.
- Character selection was not remembered after logging out or exiting character select.
- Clicking Enter World sometimes exposed the gameplay skill bar before loading. The loading screen
  must cover the transition immediately, before any dialog or gameplay HUD can appear.
- The request expanded to a Benilla-by-section combat review, with no invented behavior and with
  proof of what changed, why, and where the behavior came from.

## Corrections implemented in the latest pass

### Additive button and cast-bar art

The dark/unreadable hover and black-backed spark shared one cause: art authored for additive
composition was being drawn as ordinary alpha imagery. `GameplayArt.AdditiveHandle` now provides
the correct additive-safe path. Action buttons, micro-menu buttons, bag buttons, loot buttons,
equipped-item borders, the cast-bar spark and cast flash use that path where Benilla marks the
texture `ADD`.

Benilla evidence:

- `assets/ui/ActionBar.xml`, approximately lines 588–641: action-button highlight/checked art and
  additive blend declarations.
- `assets/ui/BagFrame.xml`, approximately lines 1233–1424: bag-button highlight/check states.
- `assets/ui/CastingBarFrame.xml`, approximately lines 215–225: additive spark/flash behavior.
- `assets/ui/MicroMenu.xml`: micro-button pushed/highlight/disabled layers.

### Character micro-menu portrait and bag behavior

The Character portrait was being drawn under its surrounding button art. Its draw order is now
inside the authored button face and before the additive overlay, so the portrait remains visible.
Bag buttons now expose Benilla-style hover/open states and tooltips, and their layout remains tied
to the scaled authored slot rather than an independent screen offset.

Benilla evidence:

- `assets/ui/MicroMenu.xml`, approximately lines 161–199 and 244–257: Character button portrait,
  frame, pushed and highlight layers.
- `assets/ui/BagFrame.xml`, approximately lines 643–680 and 1233–1424: checkbutton state textures,
  anchoring and tooltip behavior.

### Friendly-spell target law

The client previously forwarded the selected GUID too freely. A pure `Net/CastTargetLaw.cs` now
resolves the target mask before a cast is sent. A helpful unit spell with a hostile wolf selected
falls back to the player; unsupported target combinations are refused locally rather than encoded
as a fabricated target. Tests cover the target-mask decisions.

Benilla evidence:

- `crates/benilla/src/ui_action/cast_target.rs`: target-shape construction and the separation of
  selected unit, self, ground and item targets.

This pass also added local checks for cooldown/global-cooldown, pending casts, mounted state and
known range before sending a cast. Server decisions remain authoritative.

### Spell animation recovery

The apparent post-cast movement freeze was an animation-selection defect, not movement input being
deliberately locked. Spell one-shots could request an animation that had not yet been baked; the
fallback then selected Stand and left locomotion visually stalled. `M2Animator.FindOrBake` now
obtains the requested clip on demand, and exact player and creature spell-action paths no longer
substitute Stand for a missing spell clip.

Benilla evidence:

- `crates/benilla/src/creature_anim/spell_visual.rs`, approximately lines 420–670: exact spell
  animation selection and return to the movement-driven base state.

### Character selection and immediate loading cover

The most recently selected character GUID is now persisted through `GameSettings.LastCharacterGuid`
and restored when the character list is rebuilt. This is a direct product requirement from the
live review; no Benilla persistence equivalent was found and none is claimed.

`ArmEnterWorldCurtain` now raises the loading cover at the Enter World click, before asynchronous
world verification and before gameplay HUD rendering can leak through. The normal world-ready gate
still controls removal of that cover.

## Earlier parity work retained by this conversation

- Flush bottom action bar and accepted purple XP bar.
- Reaction-colored overhead names, without rejected target plates.
- Gold/outlined/shadowed unit-frame text and shadow-only overhead text.
- Gold action-page and player-level numerals.
- Model-derived player and creature portraits rather than cycling sample images.
- Functional action-page arrows in the authored bar position.
- Ping/performance presentation tied to the authored bottom-bar layout.
- Action usability tint, range state, cooldown/GCD state, attack flash, equipped-item border, stack
  counts and carried-action grid state.
- Cast packets, cancellation and casting-bar presentation.
- Solo corpse-loot flow and the authored loot window.

The detailed implementation history and citations remain in `PORT_GAMEPLAY_UI.md` and
`SYSTEM_GAMEPLAY_UI.md`. The `NEXT_*` files remain useful historical research, but their original
status claims and the target-plate request are superseded by this handoff and later user decisions.

## Verification completed

- The repository's targeted tests, including cast-target-law coverage, pass.
- The solution builds successfully in the available .NET environment.
- The build retains one pre-existing CA2014 warning; this pass did not introduce a new build error.
- Documentation consistency and whitespace are checked after this handoff update.

Compilation and pure-law tests prove wiring and invariants. They do not prove the final visual feel
in a running client.

## Required live sign-off

Run the client at the UI scales used in the supplied screenshots and verify all of the following:

1. Character portrait micro-button is visible; hover/push are additive highlights, never black
   rectangles, and clicking opens the character panel.
2. Spell hover remains readable; pressed, checked, cooldown and unusable states match Benilla.
3. Backpack and equipped bags remain centered in their authored slots at every supported UI scale;
   hover, open/checked state and tooltip all work.
4. Holy Light with a hostile wolf selected casts on the player, never the wolf.
5. Cast spark/flash has no thick black box or shadow.
6. Starting to move after a cast resumes the correct locomotion clip immediately.
7. The last selected character is restored after logout and after returning to character select.
8. Enter World covers the screen immediately; no dialog, action bar or single-frame gameplay HUD
   leak appears before the loading screen.

## Remaining combat-parity boundary

This correction does not honestly constitute all of 1.12 combat. Outstanding sections still
requiring Benilla comparison, implementation and live proof include full spell-ribbon emitters,
complete tooltip rules, aura duration/cancellation behavior, stack splitting, bank/vendor flows,
talents, macros/stances/multiple action bars, remaining spell-usability gates, group-loot rules,
combat-log breadth and chat integration. Work on those sections must continue from Benilla source,
and each section should be marked complete only after both code verification and a live behavior
check.
