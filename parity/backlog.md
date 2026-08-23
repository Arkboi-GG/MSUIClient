# MSUI ⇄ Benilla implementation backlog

_Generated 2026-08-23 01:15 UTC by tools/rebuild_backlog.py — do not edit; edit registry/*.json instead._

## 1. Divergences awaiting Nico's ruling

MSUI differs from Benilla. Each needs one of: port the Benilla behavior, or record a
decision in `decisions/` preserving MSUI (allowed only when `deviationPolicy: ui-allowed`).

- **systems/ui_party** (must-match): MSUI diverges from Benilla — decide: port Benilla behavior, or record a decision preserving MSUI (only allowed if UI/graphics). MSUI intentionally keeps /partytest and Benilla's synthetic local roster sandbox absent.  ← `claim-group-synthetic-preserve-001`
- **ui/buffframe** (ui-allowed): MSUI diverges from Benilla — decide: port Benilla behavior, or record a decision preserving MSUI (only allowed if UI/graphics). Both clients have complete aura-bar presentation; MSUI's established anchor, 35-pixel row spacing, compact timer style, tooltip placement, and stack typography are preserved because they are present differences, not gaps.  ← `claim-buffframe-layout-001`
- **ui/castingbar** (ui-allowed): MSUI diverges from Benilla — decide: port Benilla behavior, or record a decision preserving MSUI (only allowed if UI/graphics). MSUI's existing centered custom cast-label typography is complete and intentionally preserved instead of being replaced by Benilla's GameFontHighlight declaration.  ← `claim-castingbar-text-001`
- **ui/characterframe** (ui-allowed): MSUI diverges from Benilla — decide: port Benilla behavior, or record a decision preserving MSUI (only allowed if UI/graphics). MSUI already had complete character statistics, resistances, and tooltips; its broad panels, Attack/Ranged labels and values, damage format, and no-ranged semantics are preserved instead of adopting Benilla's different presentation.  ← `claim-characterframe-stats-001`
- **ui/characterframe** (ui-allowed): MSUI diverges from Benilla — decide: port Benilla behavior, or record a decision preserving MSUI (only allowed if UI/graphics). Held rotation, release sounds, and model-pane auto-equip are present; MSUI's established zero facing and 0.12-radian tap are deliberately preserved instead of Benilla's different constants.  ← `claim-characterframe-model-001`
- **ui/characterframe** (ui-allowed): MSUI diverges from Benilla — decide: port Benilla behavior, or record a decision preserving MSUI (only allowed if UI/graphics). MSUI's complete native character-frame composition is preserved; no Benilla coordinate, tab, background-slice, or header difference is treated as authorization to replace it.  ← `claim-characterframe-layout-001`
- **ui/fonts** (ui-allowed): MSUI diverges from Benilla — decide: port Benilla behavior, or record a decision preserving MSUI (only allowed if UI/graphics). The object, face, size, color, and THICK outline are preserved, but MSUI intentionally retains anti-aliased rather than frozen monochrome glyph rasterization.  ← `claim-fonts-number-small-monochrome-001`
- **ui/gamemenuframe** (ui-allowed): MSUI diverges from Benilla — decide: port Benilla behavior, or record a decision preserving MSUI (only allowed if UI/graphics). MSUI's complete existing game-menu composition is preserved; only missing behavior was added, and Benilla's different Era ladder did not replace present MSUI controls.  ← `claim-gamemenu-layout-001`
- **ui/gamemenuframe** (ui-allowed): MSUI diverges from Benilla — decide: port Benilla behavior, or record a decision preserving MSUI (only allowed if UI/graphics). MSUI's modal ownership, pushed micro-menu state, and blocked bag input are equivalent; its existing bag-bar presentation is preserved instead of adding Benilla's temporary disabled tint.  ← `claim-gamemenu-lifecycle-001`
- **ui/gametooltip** (ui-allowed): MSUI diverges from Benilla — decide: port Benilla behavior, or record a decision preserving MSUI (only allowed if UI/graphics). Existing MSUI tooltip presentations retain their current shell, Thicken treatment, and pixel choices under the preserve-present-differences decision.  ← `claim-gametooltip-existing-presentations-001`
- **ui/gametooltip** (ui-allowed): MSUI diverges from Benilla — decide: port Benilla behavior, or record a decision preserving MSUI (only allowed if UI/graphics). MSUI preserves its present equipment-comparison presentation rather than reproducing the frozen two-frame ShoppingTooltip XML shell.  ← `claim-gametooltip-shopping-pair-001`
- **ui/gametooltip** (ui-allowed): MSUI diverges from Benilla — decide: port Benilla behavior, or record a decision preserving MSUI (only allowed if UI/graphics). MSUI preserves its current minimap tooltip presentation and anchoring rather than normalizing it to the frozen adapter.  ← `claim-tooltip-minimap-presentation-001`
- **ui/inspectframe** (ui-allowed): MSUI diverges from Benilla — decide: port Benilla behavior, or record a decision preserving MSUI (only allowed if UI/graphics). The reference-visible InspectFrame composition is present while MSUI-native rendering and left-panel plumbing are deliberately preserved.  ← `claim-inspect-frame-composition-001`
- **ui/partyframe** (ui-allowed): MSUI diverges from Benilla — decide: port Benilla behavior, or record a decision preserving MSUI (only allowed if UI/graphics). The user-directed target presentation difference is intentionally preserved while missing containment, state, and lifecycle work is added, but that preserved presentation remains unverified.  ← `claim-partyframe-template-001`
- **ui/spellbookframe** (ui-allowed): MSUI diverges from Benilla — decide: port Benilla behavior, or record a decision preserving MSUI (only allowed if UI/graphics). Spell buttons and skill-line tabs publish through the shared GameTooltip owner arbitration, but MSUI deliberately retains its existing rich spell tooltip and right-column rank presentation.  ← `claim-spellbookframe-tooltip-preserved-difference-001`

## 2. Known gaps — implement these

- **protocol/opcode**: Composite opcode-table inventory remains open and needs a fresh recount after this parity pass. Channel moderation, AUTOSTORE_BAG_ITEM and PET_SPELL_AUTOCAST are now present alongside the complete force-speed, base movement relay, observer MOVE_SET/SPLINE_SET speed, granted move-mode, server-scripted sound, weather, cooldown-extra, and implemented raid/group opcodes; several miscellaneous server-event families remain to inventory.  ← `triage-2026-08-09/batch-protocol-2.json`
- **protocol/update_object**: Rider TransportPose (MOVEFLAG_ON_TRANSPORT tail in the LIVING block) is discarded - an observed unit standing on a boat/zeppelin/elevator cannot be re-anchored through the transport frame  ← `triage-2026-08-09/batch-protocol-3.json`
- **systems/bindings**: Rebindable command coverage remains narrower than the reference's engine-wide registry. Common host commands now include chat open/slash/reply/page scrolling, autorun, camera/minimap zoom, physical movement secondaries, sit/stand, follow, attack target and enemy/friendly/all nameplates. Remaining work includes Benilla's scored recent-history TARGETPREVIOUSENEMY/TAB cycling and the long tail of reference edge/action commands.  ← `triage-2026-08-09/batch-systems-1.json`
- **systems/go_templates**: MO_TRANSPORT (type 15) data0..2 taxiPathId/moveSpeed/accelRate is parsed into Data[] but never consumed - no transport timetable exists  ← `triage-2026-08-09/batch-systems-2.json`
- **systems/player**: Transport riding is parse-only: the ONTRANSPORT pose tail is decoded but there is no deck-ride state, no boat-local pose streaming, and no ride-through-worldport handling  ← `triage-2026-08-09/batch-systems-2.json`
- **systems/sound**: No water/liquid loops, underwater muffling, or interior reverb (sound/liquid_loop.rs, water.rs, reverb.rs).  ← `triage-2026-08-09/batch-systems-3.json`
- **systems/sound**: No glue-screen (login/character-select) music or UI sounds outside the world session (sound/glue.rs).  ← `triage-2026-08-09/batch-systems-3.json`
- **systems/sound**: Backend is Windows-only MCI; other platforms are observable-but-silent by design (Benilla's mixer plays everywhere a device exists). Listener model is a raw position handed per Play call, not the reference listener-at-character pose/orientation law with pan.  ← `triage-2026-08-09/batch-systems-3.json`
- **systems/transport**: Type-15 MO_TRANSPORT boats/zeppelins never move: no TaxiPathNode.dbc timetable build from the template's (taxiPathId, moveSpeed, accelRate) tuple, no (anchor + elapsed) % period sampling - a streamed boat renders frozen at its create pose (if rendered at all).  ← `triage-2026-08-09/batch-systems-3.json`
- **systems/transport**: Type-11 TRANSPORT elevators/lifts never move: no TransportAnimation.dbc keyframe path keyed by template entry, no spawn-quat composed sampling.  ← `triage-2026-08-09/batch-systems-3.json`
- **systems/transport**: Observed riders are not carried: the on-transport local pose from create blocks and MSG_MOVE_* relays now reaches WorldEntity, but it is not yet composed through a live transport matrix, so a deck NPC or fellow passenger still renders at the packet's fallback world pose.  ← `triage-2026-08-09/batch-systems-3.json`
- **systems/transport**: No dock/depart behavior, no off-map hiding of a transport sailing another continent's leg.  ← `triage-2026-08-09/batch-systems-3.json`
- **ui/colorpickerframe**: If MSUI gains addon/Lua compatibility, implement ColorPickerFrame as one rule-owned centered 305x200 or 365x200 modal with exact named methods/fields, HSV color selection, 32x32 swatch, optional 16x128 opacity slider, live callbacks, Okay/Cancel ordering, previousValues restoration and Escape cancellation. Until a consumer exists, do not add a disconnected ImGui color editor and call it parity.  ← `benilla-b57ee0e9e2c8822b/ColorPickerFrame.xml`
- **ui/merchantframe**: Request/list/buy basics exist, but frozen left-click PickupMerchantItem and shared cursor-item authority remain incomplete.  ← `claim-merchantframe-row-click-001`
- **ui/merchantframe**: The complete template-source item projection and exact row-owned GameText/WowSkin presentation are implemented. Shared carried-item cursor arbitration and authenticated live verification remain incomplete.  ← `claim-merchantframe-row-hover-001`
- **ui/petpaperdollframe**: Attack and Defense currently use the level-derived base when MSUI has no separate streamed pet weapon-skill/defense-skill modifier surface. Tooltip presence and all other visible descriptor rows are implemented; this remains a data-source calculation gap rather than a window-layout gap.  ← `benilla-b57ee0e9e2c8822b/ui_pet_stats.rs`

## 3. Verification debt — blocked on a live authenticated session

185 claims across 65 entries are implemented but nonterminal until live verification runs.

- **ui/bagframe** — 12 claims to verify
- **ui/uipanels** — 12 claims to verify
- **systems/net** — 11 claims to verify
- **ui/merchantframe** — 9 claims to verify
- **ui/friendsframe** — 7 claims to verify
- **ui/gametooltip** — 7 claims to verify
- **ui/mailframe** — 7 claims to verify
- **ui/spellbookframe** — 7 claims to verify
- **systems/ui_party** — 6 claims to verify
- **ui/questframe** — 6 claims to verify
- **protocol/group** — 5 claims to verify
- **ui/inspectframe** — 5 claims to verify
- **ui/petactionbar** — 5 claims to verify
- **systems/bindings** — 4 claims to verify
- **systems/entities** — 4 claims to verify
- **ui/characterframe** — 4 claims to verify
- **ui/fonts** — 4 claims to verify
- **ui/uiparent** — 4 claims to verify
- **systems/player** — 3 claims to verify
- **ui/buffframe** — 3 claims to verify
- **ui/macroframe** — 3 claims to verify
- **ui/multibars** — 3 claims to verify
- **ui/partyframe** — 3 claims to verify
- **protocol/client** — 2 claims to verify
- **protocol/movement** — 2 claims to verify
- **systems/char_create** — 2 claims to verify
- **systems/nameplates** — 2 claims to verify
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
- **systems/area** — 1 claims to verify
- **systems/area_trigger** — 1 claims to verify
- **systems/aura_visual** — 1 claims to verify
- **systems/chat_bubble** — 1 claims to verify
- **systems/cooldowns** — 1 claims to verify
- **systems/cursor** — 1 claims to verify
- **systems/minimap** — 1 claims to verify
- **systems/quest_markers** — 1 claims to verify
- **systems/raid_marks** — 1 claims to verify
- **systems/terrain** — 1 claims to verify
- **systems/ui_follow** — 1 claims to verify
- **systems/ui_hide** — 1 claims to verify
- **ui/binderconfirm** — 1 claims to verify
- **ui/combattext** — 1 claims to verify
- **ui/dressupframe** — 1 claims to verify
- **ui/durabilityframe** — 1 claims to verify
- **ui/gametime** — 1 claims to verify
- **ui/minimapcluster** — 1 claims to verify
- **ui/optionsframe** — 1 claims to verify
- **ui/petpaperdollframe** — 1 claims to verify
- **ui/questtimerframe** — 1 claims to verify
- **ui/reputationframe** — 1 claims to verify
- **ui/screenshotstatus** — 1 claims to verify
- **ui/worldmapframe** — 1 claims to verify
- **ui/zonetext** — 1 claims to verify

## 4. Not yet reviewed — triage frontier

No claims cover these yet. Review each against MSUI: classify as equivalent / missing /
divergent, then promote gaps into section 2.

## 5. Deliberate MSUI preferences (preserved)

- none recorded yet
