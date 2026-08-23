# MSUI ⇄ Benilla implementation backlog

_Generated 2026-08-23 17:18 UTC by tools/rebuild_backlog.py — do not edit; edit registry/*.json instead._

## 1. Divergences awaiting Nico's ruling

MSUI differs from Benilla. Each needs one of: port the Benilla behavior, or record a
decision in `decisions/` preserving MSUI (allowed only when `deviationPolicy: ui-allowed`).

- **ui/chatframe** (ui-allowed): Current Benilla intentionally does not construct ChatFrame2Tab until its complete combat-event pipeline exists, while MSUI exposes a functioning Combat Log tab with Money and XP routing. Decide whether desktop parity should hide that working MSUI surface or preserve it as an explicit MSUI behavior.  ← `benilla-b57ee0e9e2c8822b/ChatFrame.xml`
- **ui/gametooltip** (ui-allowed): MSUI diverges from Benilla — decide: port Benilla behavior, or record a decision preserving MSUI (only allowed if UI/graphics). Existing MSUI tooltip presentations retain their current shell, Thicken treatment, and pixel choices under the preserve-present-differences decision.  ← `claim-gametooltip-existing-presentations-001`
- **ui/gametooltip** (ui-allowed): MSUI diverges from Benilla — decide: port Benilla behavior, or record a decision preserving MSUI (only allowed if UI/graphics). MSUI preserves its present equipment-comparison presentation rather than reproducing the frozen two-frame ShoppingTooltip XML shell.  ← `claim-gametooltip-shopping-pair-001`
- **ui/gametooltip** (ui-allowed): MSUI diverges from Benilla — decide: port Benilla behavior, or record a decision preserving MSUI (only allowed if UI/graphics). MSUI preserves its current minimap tooltip presentation and anchoring rather than normalizing it to the frozen adapter.  ← `claim-tooltip-minimap-presentation-001`
- **ui/spellbookframe** (ui-allowed): MSUI diverges from Benilla — decide: port Benilla behavior, or record a decision preserving MSUI (only allowed if UI/graphics). Spell buttons and skill-line tabs publish through the shared GameTooltip owner arbitration, but MSUI deliberately retains its existing rich spell tooltip and right-column rank presentation.  ← `claim-spellbookframe-tooltip-preserved-difference-001`

## 2. Known gaps — implement these

- **systems/char_select**: If MSUI gains real addon/Lua compatibility, port current Benilla's rule-owned character-select AddOns modal, installed manifest discovery, per-character tri-state enable staging, version gate, scrollbar/tooltips, and Okay/Cancel persistence. Until that consumer exists, do not add a disconnected modal or filesystem subsystem.  ← `benilla-b57ee0e9e2c8822b/char_select/addons.rs`
- **systems/sound**: Interior reverb remains absent (sound/reverb.rs). Above-water SoundWaterType beds are implemented: a distinct unowned sound scan finds the nearest retained wet ADT/MLIQ surface per class within 9 yards of the player, resolves class plus fluid speed through the real 12-row SoundWaterType.dbc, admits at most two classes in River/Ocean/Magma/Slime priority, near-clamps and slews positional emitters, crossfades changes over five seconds, and hard-stops only for water/ocean submersion with instant resurface gain. Interaction queries remain owner-scoped and unchanged. Current Benilla does not apply a separate master underwater-muffling filter.  ← `triage-2026-08-09/batch-systems-3.json`
- **systems/sound**: Backend is Windows-only waveOut PCM; other platforms are observable-but-silent by design (Benilla's mixer plays everywhere a device exists). The reference listener-at-character position/orientation, live positional gain, and stereo-pan behavior are now present on the Windows backend.  ← `triage-2026-08-09/batch-systems-3.json`
- **ui/colorpickerframe**: If MSUI gains addon/Lua compatibility, implement ColorPickerFrame as one rule-owned centered 305x200 or 365x200 modal with exact named methods/fields, HSV color selection, 32x32 swatch, optional 16x128 opacity slider, live callbacks, Okay/Cancel ordering, previousValues restoration and Escape cancellation. Until a consumer exists, do not add a disconnected ImGui color editor and call it parity.  ← `benilla-b57ee0e9e2c8822b/ColorPickerFrame.xml`
- **ui/merchantframe**: Request/list/buy basics exist, but frozen left-click PickupMerchantItem and shared cursor-item authority remain incomplete.  ← `claim-merchantframe-row-click-001`
- **ui/merchantframe**: The complete template-source item projection and exact row-owned GameText/WowSkin presentation are implemented. Shared carried-item cursor arbitration and authenticated live verification remain incomplete.  ← `claim-merchantframe-row-hover-001`

## 3. Verification debt — blocked on a live authenticated session

222 claims across 75 entries are implemented but nonterminal until live verification runs.

- **ui/bagframe** — 12 claims to verify
- **ui/uipanels** — 12 claims to verify
- **systems/net** — 11 claims to verify
- **systems/bindings** — 10 claims to verify
- **ui/merchantframe** — 9 claims to verify
- **systems/entities** — 8 claims to verify
- **systems/player** — 8 claims to verify
- **ui/friendsframe** — 7 claims to verify
- **ui/gametooltip** — 7 claims to verify
- **ui/mailframe** — 7 claims to verify
- **ui/spellbookframe** — 7 claims to verify
- **systems/ui_party** — 6 claims to verify
- **ui/questframe** — 6 claims to verify
- **protocol/group** — 5 claims to verify
- **ui/inspectframe** — 5 claims to verify
- **ui/petactionbar** — 5 claims to verify
- **systems/sound** — 4 claims to verify
- **systems/targeting** — 4 claims to verify
- **ui/characterframe** — 4 claims to verify
- **ui/fonts** — 4 claims to verify
- **ui/uiparent** — 4 claims to verify
- **ui/buffframe** — 3 claims to verify
- **ui/macroframe** — 3 claims to verify
- **ui/multibars** — 3 claims to verify
- **ui/optionsframe** — 3 claims to verify
- **ui/partyframe** — 3 claims to verify
- **protocol/auth** — 2 claims to verify
- **protocol/client** — 2 claims to verify
- **protocol/movement** — 2 claims to verify
- **systems/char_create** — 2 claims to verify
- **systems/cursor** — 2 claims to verify
- **systems/nameplates** — 2 claims to verify
- **systems/transport** — 2 claims to verify
- **ui/dressupframe** — 2 claims to verify
- **ui/gamemenuframe** — 2 claims to verify
- **ui/gossipframe** — 2 claims to verify
- **ui/keybindingspage** — 2 claims to verify
- **ui/questlogframe** — 2 claims to verify
- **ui/unitpopup** — 2 claims to verify
- **protocol/area_trigger** — 1 claims to verify
- **protocol/binder** — 1 claims to verify
- **protocol/chat** — 1 claims to verify
- **protocol/lib** — 1 claims to verify
- **protocol/parse** — 1 claims to verify
- **protocol/social** — 1 claims to verify
- **protocol/spellbook** — 1 claims to verify
- **protocol/trade** — 1 claims to verify
- **protocol/update_object** — 1 claims to verify
- **systems/area** — 1 claims to verify
- **systems/area_trigger** — 1 claims to verify
- **systems/aura_visual** — 1 claims to verify
- **systems/chat_bubble** — 1 claims to verify
- **systems/cooldowns** — 1 claims to verify
- **systems/fishing_line** — 1 claims to verify
- **systems/minimap** — 1 claims to verify
- **systems/quest_markers** — 1 claims to verify
- **systems/raid_marks** — 1 claims to verify
- **systems/terrain** — 1 claims to verify
- **systems/ui_follow** — 1 claims to verify
- **systems/ui_hide** — 1 claims to verify
- **systems/ui_net** — 1 claims to verify
- **ui/actionbar** — 1 claims to verify
- **ui/binderconfirm** — 1 claims to verify
- **ui/chatframe** — 1 claims to verify
- **ui/combattext** — 1 claims to verify
- **ui/durabilityframe** — 1 claims to verify
- **ui/gametime** — 1 claims to verify
- **ui/minimapcluster** — 1 claims to verify
- **ui/petpaperdollframe** — 1 claims to verify
- **ui/questtimerframe** — 1 claims to verify
- **ui/reputationframe** — 1 claims to verify
- **ui/screenshotstatus** — 1 claims to verify
- **ui/talentframe** — 1 claims to verify
- **ui/worldmapframe** — 1 claims to verify
- **ui/zonetext** — 1 claims to verify

## 4. Not yet reviewed — triage frontier

No claims cover these yet. Review each against MSUI: classify as equivalent / missing /
divergent, then promote gaps into section 2.

## 5. Deliberate MSUI preferences (preserved)

- none recorded yet
