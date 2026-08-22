# MSUI ⇄ Benilla implementation backlog

_Generated 2026-08-22 15:28 UTC by tools/rebuild_backlog.py — do not edit; edit registry/*.json instead._

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

- **protocol/channel**: CMSG_CHANNEL_* moderation family (LIST, PASSWORD, OWNER, SET_OWNER, MODERATOR, UNMODERATOR, MUTE, UNMUTE, INVITE, KICK, BAN, UNBAN, ANNOUNCEMENTS, MODERATE): no senders  ← `triage-2026-08-09/batch-protocol-1.json`
- **protocol/client**: CMSG_CHANNEL_* moderation builder family remains absent; JOIN_CHANNEL, LEAVE_CHANNEL and CHANNEL_LIST are implemented  ← `triage-2026-08-09/batch-protocol-1.json`
- **protocol/client**: CMSG_MOVE_SPLINE_DONE: opcode-table-only (Net/Opcodes.cs:269), never built/sent after server-driven splines  ← `triage-2026-08-09/batch-protocol-1.json`
- **protocol/events**: SMSG_SPELL_UPDATE_CHAIN_TARGETS remains unhandled. SMSG_CHANNEL_LIST and SMSG_SET_PROFICIENCY are strict-decoded and dispatched; proficiency drives item-tooltip colors plus vendor usability, while SMSG_EXPLORATION_EXPERIENCE and SMSG_LEVELUP_INFO are decoded and applied.  ← `triage-2026-08-09/batch-protocol-1.json`
- **protocol/events**: SMSG_SPLINE_SET_* observer speed family remains unhandled. The complete six-kind SMSG/CMSG_FORCE_* speed-change/ack family is now implemented and focused-wire checked.  ← `triage-2026-08-09/batch-protocol-1.json`
- **protocol/events**: The remaining aggregate event gap is the channel moderation/list surface tracked under protocol/channel and protocol/client. Duel, played time, random roll, corpse query, area-trigger message, chat player-not-found/wrong-faction, spirit-healer confirm, and durability-damage-death are dispatched.  ← `triage-2026-08-09/batch-protocol-1.json`
- **protocol/items**: CMSG_AUTOSTORE_BAG_ITEM: absent (bag-to-bag auto-store; MSUI only has explicit swap/split)  ← `triage-2026-08-09/batch-protocol-1.json`
- **protocol/lib**: Ambiguous-B redial: Benilla redials the logon challenge (up to 8 times) when the server's SRP6 public key B is not width-stable (~1 in 137 dials), because those handshakes can fail proof verification despite a correct password; MSUI's RealmClient.Logon dials exactly once, so roughly 0.7% of logons can spuriously reject a correct password  ← `triage-2026-08-09/batch-protocol-1.json`
- **protocol/movement**: SMSG_SPLINE_SET_* speed packets (non-controlled movers) not handled  ← `triage-2026-08-09/batch-protocol-2.json`
- **protocol/movement**: Inbound MSG_MOVE_* relays for other players have no dispatch case in Program.Net.cs - Entities.ApplyRemotePlayerMove exists but has no caller  ← `triage-2026-08-09/batch-protocol-2.json`
- **protocol/opcode**: Composite opcode-table inventory remains open and needs a fresh recount after this parity pass. The complete force-speed, granted move-mode, server-scripted sound, and weather opcodes are now present; known remaining families include channel moderation, MSG_MOVE_SET_* relays, SMSG_SPLINE_SET_*, cooldown extras, raid/subgroup administration, PET_SPELL_AUTOCAST, and several miscellaneous server events.  ← `triage-2026-08-09/batch-protocol-2.json`
- **protocol/parse**: Spline/relay observer speed families remain unhandled: SMSG_SPLINE_SET_* (6), MSG_MOVE_SET_*_SPEED relays (6). The six SMSG_FORCE_*_SPEED_CHANGE controller packets are implemented.  ← `triage-2026-08-09/batch-protocol-2.json`
- **protocol/parse**: SMSG_COMPRESSED_MOVES remains unhandled. SMSG_WEATHER and SMSG_PLAY_SOUND/MUSIC/OBJECT_SOUND now use exact strict decoders and world-audio dispatch.  ← `triage-2026-08-09/batch-protocol-2.json`
- **protocol/parse**: The remaining misc parse gap is SMSG_SPELL_UPDATE_CHAIN_TARGETS. SMSG_CHANNEL_LIST is now strict-decoded and dispatched, alongside SET_PROFICIENCY, transfer-aborted, exploration-experience, pet-name response, login time-speed, item-enchant-time, mount-special, played-time, random-roll, area-trigger, durability-death, inventory-change-failure, mount/dismount-result, new-taxi-path, spirit-healer, chat-error, and corpse-query events.  ← `triage-2026-08-09/batch-protocol-2.json`
- **protocol/quest**: CMSG_QUESTLOG_SWAP_QUEST (two u8 log slots) has no sender - quest log entries cannot be reordered  ← `triage-2026-08-09/batch-protocol-2.json`
- **protocol/self_movement**: CMSG_MOVE_SPLINE_DONE never sent - a server-authored spline addressed to the player (taxi end, charge, knockback) is never acked, so vmangos SplineDonePending relocation/rebroadcast is left to its timeout fallback  ← `triage-2026-08-09/batch-protocol-3.json`
- **protocol/spells**: SMSG_SPELL_UPDATE_CHAIN_TARGETS has no handler - the channeled-beam hop list (Drain Life / Mind Flay style, vmangos sends it from SendChannelStart) is dropped, so packet-delivered chain visuals have no data source  ← `triage-2026-08-09/batch-protocol-3.json`
- **protocol/update_object**: Create-time live spline (MOVEFLAG_SPLINE_ENABLED tail) is read and discarded - a creature already mid-walk when it streams into view stands frozen at its create pose until the next SMSG_MONSTER_MOVE (the exact bug Benilla decision 0708 fixed), and the spline id/time_passed/duration/flying/cyclic never reach the entity layer  ← `triage-2026-08-09/batch-protocol-3.json`
- **protocol/update_object**: Rider TransportPose (MOVEFLAG_ON_TRANSPORT tail in the LIVING block) is discarded - an observed unit standing on a boat/zeppelin/elevator cannot be re-anchored through the transport frame  ← `triage-2026-08-09/batch-protocol-3.json`
- **protocol/update_object**: UPDATE_FLAG_TRANSPORT path-progress u32 is discarded - the transport GameObject cycle anchor (Benilla decision 0438) is unavailable to any elevator/ship evaluator  ← `triage-2026-08-09/batch-protocol-3.json`
- **systems/aura_visual**: Proc 14 translucency: no aura-driven body alpha at all (no stealth/invisibility/ghost fade; grep for stealth/translucency/CreatureModelAlpha in the render stack finds nothing).  ← `triage-2026-08-09/batch-systems-1.json`
- **systems/aura_visual**: Proc 1 tint: no aura-driven body color modulation.  ← `triage-2026-08-09/batch-systems-1.json`
- **systems/aura_visual**: Proc 11 anim-rate: no freeze of the unit's animation clocks under Ice Block/Petrify-family auras.  ← `triage-2026-08-09/batch-systems-1.json`
- **systems/aura_visual**: Base display alpha: CreatureDisplayInfo's CreatureModelAlpha is not applied, so authored-translucent displays (Ghost Wolf) render opaque.  ← `triage-2026-08-09/batch-systems-1.json`
- **systems/bindings**: Rebindable command coverage is narrower than the reference's engine-wide set (e.g. chat open, autorun, camera zoom are not in the BindingRows table).  ← `triage-2026-08-09/batch-systems-1.json`
- **systems/char_create**: The per-race dial-5 label rename (ChrRaces HAIR_/FACIAL_HAIR_ tokens - 'Markings'/'Horns'/'Tusks' per race) is self-documented as deferred; a generic label shows instead.  ← `triage-2026-08-09/batch-systems-1.json`
- **systems/char_create**: Glue click sounds are absent (self-documented: no glue audio subsystem on this screen).  ← `triage-2026-08-09/batch-systems-1.json`
- **systems/entities**: SMSG_SPELL_UPDATE_CHAIN_TARGETS unhandled: chain spells (Chain Lightning/Chain Heal) draw no jump beams between targets.  ← `triage-2026-08-09/batch-systems-1.json`
- **systems/entities**: No enchant item glow: an enchanted weapon's glow effect (item_glow) has no counterpart.  ← `triage-2026-08-09/batch-systems-1.json`
- **systems/entities**: No carried-light: point lights attached to carried items (lanterns/torches on NPCs) are not spawned.  ← `triage-2026-08-09/batch-systems-1.json`
- **systems/go_templates**: MO_TRANSPORT (type 15) data0..2 taxiPathId/moveSpeed/accelRate is parsed into Data[] but never consumed - no transport timetable exists  ← `triage-2026-08-09/batch-systems-2.json`
- **systems/nameplates**: Names are drawn on the ImGui background draw list, not depth-tested world geometry - walls do not occlude overhead names, where the reference's name batch is depth-test ON  ← `triage-2026-08-09/batch-systems-2.json`
- **systems/player**: Swimming is entirely absent: no swim state/pitch, MSG_MOVE_START_SWIM/STOP_SWIM are never sent, and the SWIMMING flag never joins the outbound stream  ← `triage-2026-08-09/batch-systems-2.json`
- **systems/player**: SMSG_MOVE_KNOCK_BACK is not handled - no knockback arc or ack  ← `triage-2026-08-09/batch-systems-2.json`
- **systems/player**: A bare self-addressed MSG_MOVE_* from the server (.go forward-style GM move) is not applied to the local mover  ← `triage-2026-08-09/batch-systems-2.json`
- **systems/player**: Transport riding is parse-only: the ONTRANSPORT pose tail is decoded but there is no deck-ride state, no boat-local pose streaming, and no ride-through-worldport handling  ← `triage-2026-08-09/batch-systems-2.json`
- **systems/player**: The drunk system (DRUNKENSTATE screen effect/gait) has no counterpart  ← `triage-2026-08-09/batch-systems-2.json`
- **systems/portrait**: No pet portrait slot: the pet frame has no baked portrait  ← `triage-2026-08-09/batch-systems-2.json`
- **systems/portrait**: Party frames bake no live portraits - PartyFrameUiLaw's source enum only offers the TemporaryPortrait circular stand-in or empty, vs Benilla's party1-4 booth slots  ← `triage-2026-08-09/batch-systems-2.json`
- **systems/sound**: No water/liquid loops, underwater muffling, or interior reverb (sound/liquid_loop.rs, water.rs, reverb.rs).  ← `triage-2026-08-09/batch-systems-3.json`
- **systems/sound**: No glue-screen (login/character-select) music or UI sounds outside the world session (sound/glue.rs).  ← `triage-2026-08-09/batch-systems-3.json`
- **systems/sound**: Backend is Windows-only MCI; other platforms are observable-but-silent by design (Benilla's mixer plays everywhere a device exists). Listener model is a raw position handed per Play call, not the reference listener-at-character pose/orientation law with pan.  ← `triage-2026-08-09/batch-systems-3.json`
- **systems/terrain**: SMSG_WEATHER visual presentation remains: exact dual ~10-second effect/sky ramps, storm lighting/fog blend, outdoor/interior visibility gate, and rain/snow/sand precipitation/mist pools. Packet decode and outdoor-only weather ambience are implemented.  ← `benilla-current-2026-08-22/weather`
- **systems/transport**: Type-15 MO_TRANSPORT boats/zeppelins never move: no TaxiPathNode.dbc timetable build from the template's (taxiPathId, moveSpeed, accelRate) tuple, no (anchor + elapsed) % period sampling - a streamed boat renders frozen at its create pose (if rendered at all).  ← `triage-2026-08-09/batch-systems-3.json`
- **systems/transport**: Type-11 TRANSPORT elevators/lifts never move: no TransportAnimation.dbc keyframe path keyed by template entry, no spawn-quat composed sampling.  ← `triage-2026-08-09/batch-systems-3.json`
- **systems/transport**: Observed riders are not carried: the on-transport local pose from create blocks and MSG_MOVE_* relays is discarded, so a deck NPC or fellow passenger renders at a stale world pose instead of being composed through the transport's live matrix.  ← `triage-2026-08-09/batch-systems-3.json`
- **systems/transport**: No dock/depart behavior, no off-map hiding of a transport sailing another continent's leg.  ← `triage-2026-08-09/batch-systems-3.json`
- **systems/ui_text**: No general FontString overflow/ellipsize law (Benilla's layout/overflow.rs ellipsize_to_fit) - truncation behavior outside chat wrap is per-panel ad hoc.  ← `triage-2026-08-09/batch-systems-3.json`
- **ui/itemref**: Shift-click insertion is implemented for player/item links, but Ctrl-click item dressing-room preview remains absent because MSUI has no dressing-room subsystem  ← `triage-2026-08-09/batch-ui-2.json`
- **ui/keybindingspage**: No mouse button (3/4/5) or mouse-wheel binding capture  ← `triage-2026-08-09/batch-ui-2.json`
- **ui/keybindingspage**: Category headers are plain labels - no collapse/expand sections and no binding search  ← `triage-2026-08-09/batch-ui-2.json`
- **ui/macroframe**: No General vs Character-specific tab split (reference: two tabbed macro sets; MSUI has one flat 18-slot set)  ← `triage-2026-08-09/batch-ui-2.json`
- **ui/macroframe**: No scrollbar on the body editor (reference MacroFrameScrollFrame)  ← `triage-2026-08-09/batch-ui-2.json`
- **ui/macroframe**: Macro execution supports only a small command subset, with 'Unsupported macro command' chat fallback (engine scope, noted for honesty)  ← `triage-2026-08-09/batch-ui-2.json`
- **ui/merchantframe**: Request/list/buy basics exist, but frozen left-click PickupMerchantItem and shared cursor-item authority remain incomplete.  ← `claim-merchantframe-row-click-001`
- **ui/merchantframe**: The complete template-source item projection and exact row-owned GameText/WowSkin presentation are implemented. Shared carried-item cursor arbitration and authenticated live verification remain incomplete.  ← `claim-merchantframe-row-hover-001`
- **ui/merchantframe**: Buyback commands and partial presentation support exist, but the complete twelve-slot server snapshot and page projection are not implemented.  ← `claim-merchantframe-buyback-page-001`
- **ui/merchantframe**: Recent buyback remains dependent on the missing authenticated complete buyback snapshot.  ← `claim-merchantframe-recent-buyback-001`
- **ui/merchantframe**: Merchant show/update/close and money refresh exist, but shared UIPanel left-slot authority and replacement semantics remain incomplete.  ← `claim-merchantframe-lifecycle-001`
- **ui/minimapcluster**: No minimap ping or battlefield/meeting-stone buttons (current Benilla also scopes these out). The day/night indicator is now implemented and tracked separately under ui/gametime.  ← `triage-2026-08-09/batch-ui-2.json`
- **ui/optionsframe**: No per-page Defaults button (only whole-quality presets)  ← `triage-2026-08-09/batch-ui-2.json`
- **ui/questlogframe**: Share Quest button is permanently disabled (no quest push) - matches Benilla's own v1 stub, noted for completeness  ← `triage-2026-08-09/batch-ui-2.json`
- **ui/uipanels**: The frozen center/fullscreen seat setters, getters, and left-center movement semantics remain outside the bounded Character/SpellBook host adapter.  ← `claim-uipanels-center-fullscreen-authority-gap-001`
- **ui/uipanels**: MSUI retains surface-owned Escape and close paths, but there is no authoritative frozen global CloseWindows/CloseAllWindows coordinator with the full center-ignore and ordered closure contract.  ← `claim-uipanels-close-windows-gap-001`

## 3. Verification debt — blocked on a live authenticated session

141 claims across 50 entries are implemented but nonterminal until live verification runs.

- **ui/bagframe** — 12 claims to verify
- **systems/net** — 11 claims to verify
- **ui/uipanels** — 9 claims to verify
- **ui/merchantframe** — 8 claims to verify
- **ui/gametooltip** — 7 claims to verify
- **ui/mailframe** — 7 claims to verify
- **systems/ui_party** — 6 claims to verify
- **ui/questframe** — 6 claims to verify
- **ui/spellbookframe** — 6 claims to verify
- **protocol/group** — 5 claims to verify
- **ui/inspectframe** — 5 claims to verify
- **ui/characterframe** — 4 claims to verify
- **ui/fonts** — 4 claims to verify
- **ui/petactionbar** — 4 claims to verify
- **ui/uiparent** — 4 claims to verify
- **ui/buffframe** — 3 claims to verify
- **ui/multibars** — 3 claims to verify
- **ui/partyframe** — 3 claims to verify
- **ui/gamemenuframe** — 2 claims to verify
- **ui/keybindingspage** — 2 claims to verify
- **protocol/area_trigger** — 1 claims to verify
- **protocol/binder** — 1 claims to verify
- **protocol/chat** — 1 claims to verify
- **protocol/social** — 1 claims to verify
- **protocol/spellbook** — 1 claims to verify
- **protocol/trade** — 1 claims to verify
- **systems/area** — 1 claims to verify
- **systems/area_trigger** — 1 claims to verify
- **systems/bindings** — 1 claims to verify
- **systems/chat_bubble** — 1 claims to verify
- **systems/cooldowns** — 1 claims to verify
- **systems/cursor** — 1 claims to verify
- **systems/entities** — 1 claims to verify
- **systems/minimap** — 1 claims to verify
- **systems/quest_markers** — 1 claims to verify
- **systems/raid_marks** — 1 claims to verify
- **systems/ui_follow** — 1 claims to verify
- **systems/ui_hide** — 1 claims to verify
- **ui/binderconfirm** — 1 claims to verify
- **ui/combattext** — 1 claims to verify
- **ui/durabilityframe** — 1 claims to verify
- **ui/friendsframe** — 1 claims to verify
- **ui/gametime** — 1 claims to verify
- **ui/gossipframe** — 1 claims to verify
- **ui/macroframe** — 1 claims to verify
- **ui/minimapcluster** — 1 claims to verify
- **ui/questtimerframe** — 1 claims to verify
- **ui/unitpopup** — 1 claims to verify
- **ui/worldmapframe** — 1 claims to verify
- **ui/zonetext** — 1 claims to verify

## 4. Not yet reviewed — triage frontier

No claims cover these yet. Review each against MSUI: classify as equivalent / missing /
divergent, then promote gaps into section 2.

## 5. Deliberate MSUI preferences (preserved)

- none recorded yet
