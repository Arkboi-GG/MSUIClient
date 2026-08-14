> Approved implementation plan, 2026-08-12. Status updated 2026-08-14: R1 code is
> landed on both sides and present in the owner-started server. The current boot is
> verified vanilla/inert; an isolated RTS-save activation and R1 gameplay validation
> are still pending. R2-R5 are plans, not delivered features. Companion docs:
> CRPG_RTS_WIP.md (session records, owner decisions), box
> docs/SUI_WIRE_PROTOCOL.md (wire + ruleset key appendix).

# RTS Worldstate â€” Phased Implementation Plan (tier-2 match layer)

## Context

The commander map (tier-1) and the worldstate scaffold shipped 2026-08-12. The owner
has now designed the tier-2 RTS match layer (CRPG_RTS_WIP.md Parts 3â€“5) and asked for
a code implementation of everything discussed. Decisions binding this plan:

- Two tiers: tier-1 (possession/free view/orders/commander map) always on; tier-2
  inert unless `SuiPossess::RtsWorldState()` (existing scaffold) â€” module rule:
  config absent = module inert even in RTS mode. Ruleset = boot-time-only DATA in
  the swapped characters DB.
- Loop: Fightâ†’Honor(faction pool)â†’Heroes; Hold(hubsâ†’zones)â†’Capacity(hero slots,
  AoE-housing semantics; standing-supply ore/skins/herbs allotments); accelerated
  world (30â€“40x, ~10 h L1â†’60) = progression engine; dungeons = faction-buff
  objectives with controller lockout + one-run-per-faction; bots die/respawn the
  vanilla way with zone-control-aware graveyards; heroes revive for an Honor fee,
  keep their slot.
- Phased delivery, play-testing between phases (owner choice).

### Authoritative database ownership

The active schema boundary is fixed by the landed server source:

- **VMaNGOS `CharacterDatabase`** owns the true-RTS save header, rules, and match
  state. This deployment's active configured schema is `characters`; it is not
  `vmangos_admin`. `superui_worldstate` holds `mode` plus scalar settings;
  `superui_rules_zone`, `superui_rules_hub`, `superui_rules_hero`, and
  `superui_rules_dungeon` hold list-shaped configuration; `superui_faction`,
  `superui_heroes`, `superui_zone_control`, and `superui_dungeon_control` hold
  runtime match state. These rows travel with the characters/worldstate save.
- **VMaNGOS `WorldDatabase`** owns shared authored world content needed by later
  phases, such as banner templates/spawns, game events, spell rows, and loot data.
  Those are prerequisites, not the active match save's authoritative counters or
  controller state.
- **`vmangos_admin`** is MangosSuperUI's administrative database. Future R5 web
  metadata such as editor presets and audit records may live there, but the web
  app must edit the RTS rows through its Characters connection. It must never
  become a second authoritative copy of the live rules or match state.

Recon verdicts this plan builds on: `OPvPCapturePoint`/`ZoneScript` chassis (unused,
purpose-built, already wired into SMSG_INIT_WORLD_STATES); GOOBER banner GOs (not
FLAGSTAND â€” BG-only); `sGameEventMgr` event pairs for guard swaps; virgin
`CreatureAI::FillLoot` virtual for boss multi-drops; public `sWorld.setConfig` for
boot rate overrides; `Player::RewardHonorOnDeath` (pre-DR, damage-attributed) as the
honor-pool seam; AV's `GetClosestGraveYard` as the retreat-line model; the web app's
ChatSettings registry pattern for the ruleset editor and BackupController for the
worldstate swap (two gaps to fix: no atomic multi-group restore; RestoreCore drops
mangosd.conf).

## Phase overview

| Phase | Where | Delivers | Current status / playable check |
|---|---|---|---|
| R1 | server+wire+client | ruleset loader, rate overrides, per-faction bot caps, state snapshot/header | **Deployed foundation; current boot vanilla/inert.** Owner-run RTS-save checks pending: show the XP override and prove fresh admissions respect both faction caps. |
| R2 | server+wire+client | faction Honor pool, hero declare/upgrade/revive (fixed slot scalar), scale+damage | **Not built.** Declare a hero and watch it grow. |
| R3 | server+data+client | hub capture, zone control, guard swap, graveyards, supply calc, zone-derived hero cap | **Not built.** Capture a pilot hub and see the map flip. |
| R4 | server+data | dungeon objectives: entry gates, clear detection, faction buffs, 10x boss loot | **Not built.** Clear Deadmines and hold the buff. |
| R5 | web app+brain | ruleset editor, worldstate swap orchestration, RTS planner + capture orders, faction fleet UI | **Not built.** Swap into an RTS save and order a capture. |
| Unphased | server+data+client | capital rules, four non-respawning faction commanders, victory/defeat state | **Design gap.** The stated win condition is not yet assigned to R1-R5. |

## Server module layout & laws (all phases)

R1 landed `src/game/SuperUiBots/SuiRts.h/.cpp` (singleton: ruleset boot-load,
KV accessors, module-enabled flags, the persisted faction Honor-pool scaffold,
main-thread `Tick`, write-behind persistence, wire assembly, GM cores) plus
`src/game/Server/Packets/SuiRts.h` (SuiControl.h conventions). Later phases add
`SuiHonor.cpp`, `SuiHero.cpp`, `SuiTerritory.cpp/.h`, and `SuiDungeon.cpp`; none of
those later-phase modules exists yet. R1 has no Honor-accrual caller; resource,
hero, and dungeon fields in the state packet remain zero/empty placeholders.

**Two-gate pattern on every tier-2 hook**: `if (!SuiPossess::RtsWorldState()) return;`
then `if (!SuiRts::X::Enabled()) return;` (Enabled = module config present at boot).

**Threading law (verified)**: kill hooks / loot fill / GO use / repop / login run on
PARALLEL MAP THREADS; ZoneScriptMgr::Update, PACKET_PROCESS_WORLD handlers, and
GameEvent start/stop run on MAIN. So: honor/resource counters = `std::atomic`
relaxed adds; ALL structural mutations (zone flips, guard events, buffs, hero
roster, dungeon control) happen only in `SuiRts::Tick(diff)` hooked into
`World::Update` beside `sZoneScriptMgr.Update` (World.cpp:2065); map-thread hooks
enqueue into a mutex-guarded action queue. DB writes: async, main thread,
write-behind (`state.flush_ms`=30000 + shutdown flush in CleanupsBeforeStop).

**Boot-order fix (R1 prereq)**: move `SuiPossess::LoadWorldState()` from
World.cpp:1852 to just before `sZoneScriptMgr.InitZoneScripts()` (:1818); add
`SuiRts::LoadRuleset()` right after it (order: LoadConfigSettings :1311 â†’ ... â†’
worldstate+ruleset â†’ InitZoneScripts :1818 â†’ sPlayerBotMgr.Load :1850 sees caps).

**Persistence convention**: all RTS tables use `CharacterDatabase`; none uses
`vmangos_admin`. Core-owned idempotent DDL runs at boot and a vanilla characters DB
boots clean. `superui_worldstate` is the save header/scalar configuration. Config
tables whose rows ship in the RTS save: `superui_rules_zone` (zone_id, ore, skins, herbs),
`superui_rules_hub` (hub_id, zone_id, name, banner_go_guid, event_alliance,
event_horde, capture_ms, initial_controller), `superui_rules_hero` (hero_level 1-5,
declare_cost, revive_fee, spell_id), `superui_rules_dungeon` (map_id,
final_boss_entry, buff_spell_id, loot_items). State tables (core-written):
`superui_faction` (team, honor_pool), `superui_heroes` (guid, team, hero_level,
dead, declared_at), `superui_zone_control` (zone_id, controller),
`superui_dungeon_control` (map_id, controller). Resources are DERIVED (sum of
allotments over controlled zones), never persisted; contest timers/live runs never
persisted.

Table existence alone never enables the match: DDL and the two faction seed rows
also exist under vanilla. In RTS mode the current loader sets the honor bit when any
`honor.weight.*` scalar exists, heroes when `superui_rules_hero` has a row,
territory when `superui_rules_hub` has a row, and dungeons when
`superui_rules_dungeon` has a row. These are presence gates, not proof that the
later-phase mechanic is implemented or valid.

**Wire (allocated once in R1)**: `CMSG_SUI_RTS_STATE`=838, `SMSG_SUI_RTS_STATE`=839,
`CMSG_SUI_RTS_ACTION`=840 (u8 action 1=declare 2=upgrade 3=revive, u64 subject),
`SMSG_SUI_RTS_ACTION_RESULT`=841 (u8 action, u8 result, u64 subject, i64
poolAfter); NUM_MSG_TYPES 838â†’842. SMSG_SUI_RTS_STATE: u8 mode, u8 moduleFlags,
stride-versioned blocks â€” faction rows (i64 honorPool, i32 ore/skins/herbs, u16
controlledZones/heroesFielded/heroSlotCap), hero rows (u64 guid, u8 team/level/
dead/pad), dungeon rows (u32 mapId, u8 controller/liveRunFlags/pad). Handlers
mirror HandleZoneIntel (SetSuiCapable, reply-to-asker). Zone-intel zone-row stride
bumps 8â†’9 (+u8 controller, 0x80=contested) in R1 with controller always 0.

## R1 â€” Foundation & ruleset

1. Boot-order move + `SuiRts::LoadRuleset()` (DDL, KV load, module flags).
2. Rate overrides: for each present KV (`rate.xp_kill`, `rate.xp_kill_elite`,
   `rate.drop_item_referenced`, `rate.drop_money`, ...) call `sWorld.setConfig`
   (public, World.h:790; XP/drop/money read live â€” verified). Creature HP/damage
   rates are spawn-time â†’ conf only, documented.
3. Per-faction bot caps: admission gate in `PlayerBotMgr::AddBot`; the landed R1
   code scans non-offline manager entries by team for each fresh add. It does not
   evict already-online bots after a rules reload. KV `bots.cap.alliance/horde`
   (absent = uncapped).
4. Full wire allocation (above); 839 works both modes (vanilla: mode=0 + empty
   blocks); 840 answers UNSUPPORTED until R2.
5. GM: `.sui rts status`, `.sui rts reload` (suiCommandTable, Chat.cpp:95).
   Reload is a development diagnostic, not the production save-transition path:
   it does not reread the persisted `mode`, and removing a rate key or flipping the
   in-memory mode to vanilla does not restore the original config rate. A clean
   owner-operated boot remains authoritative.

### Owner-operated R1 validation checkpoint

R1 is deployed but is not yet validated in RTS mode. This checkpoint deliberately
uses the smallest possible disposable characters save; R5.1 is a future convenience,
not a prerequisite. Codex may prepare the checklist, inspect source and redacted
evidence read-only, and walk Nico through the run. Nico alone creates/restores the
save, writes its database rows, and controls installation, deployment, or runtime.

1. Record the server and client commits/binary identities, date, selected test
   faction(s), normal-mob baseline, and the known-good vanilla save to restore.
2. Owner boots the known-good vanilla save first. Expected evidence: the worldstate
   and RTS logs say vanilla/inert, `.sui rts status` reports no active modules, the
   839 state packet reports mode 0, and the commander map has no `RTS CAMPAIGN` tag.
3. Owner makes an isolated test copy of the characters save. Its minimal RTS data is
   exact lowercase `mode=rts`, one conspicuous but sane positive `rate.xp_kill`, and
   at least one explicit `bots.cap.alliance` or `bots.cap.horde` value for the first
   attempt. Both cap keys must eventually be exercised before the cap feature is
   signed off. Leave every `honor.weight.*` key absent and all four `superui_rules_*`
   tables empty so unfinished R2-R4 module bits remain zero. Omit `state.flush_ms`.
4. After the owner's clean boot of that save, confirm the RTS boot/status output
   reports exactly the chosen rate and cap with module flags zero. The commander map
   must show `RTS CAMPAIGN` without an Honor value while the Honor module is disabled;
   the 839 packet must remain well formed. If a
   diagnostic exercises an 840 action request, UNSUPPORTED is the expected R1
   result; no R2 action UI is required for this checkpoint.
5. Kill a normal, non-elite mob under controlled conditions and compare awarded XP
   with the recorded baseline. Elite kills are not the first proof because they
   combine the kill and elite multipliers.
6. From a fresh bot-manager state, admit bots of each selected faction up to its cap.
   The next add must be refused with the `[SUI-RTS] bot cap` diagnostic. Also inspect
   manager/status output after refusal and repeat the rejected add once: the refused
   provisional entry must not remain counted or poison later admission. Exercising
   only one faction is a useful first attempt, but not full two-key cap validation.
7. Owner restores the known-good vanilla save and performs a clean boot. Reconfirm
   the inert logs/header and baseline XP. Runtime `.sui worldstate` or `.sui rts
   reload` is useful for diagnosis only; it is not proof of activation or rollback
   because mode is not reread and removed rate keys do not restore config values.
8. Preserve a redacted evidence bundle: identities, selected rows/keys, boot/status
   lines, client header, XP before/after, cap/refusal result, rollback proof, and any
   deviation. Mark R1 validated only when every item above passes, including both
   faction-cap mappings; otherwise record the run accurately as a partial R1 attempt.

## R2 â€” Honor pool & heroes (fixed slots)

1. `SuiHonor.cpp`: ONE choke point in `Unit::Kill` (Unit.cpp:957, map thread) â€”
   two boolean loads before any work; classify victim (enemy player/bot/NPC/elite),
   weight from KV `honor.weight.player/bot/npc/npc_elite` (10/5/1/3), atomic add to
   killer-team pool. Single site = no solo-vs-group double count.
2. Bot-HK write suppression: in `Player::RewardHonorOnDeath` (Player.cpp:21917), if
   RTS âˆ§ both sides bots âˆ§ KV `honor.suppress_bot_hk` (default 1) â†’ skip vanilla
   HonorMgr recording (kills character_honor_cp write amplification). Human-involved
   kills keep vanilla honor.
3. `SuiHero.cpp`: roster from `superui_heroes`; declare/upgrade/revive as
   main-thread actions from the 840 handler + GM; checks: pool â‰¥ cost, `fielded <
   slotCap` (R2: KV `hero.slots_fixed` default 4, declare-time only, never
   demotes), subject in-world same-faction Player. Hero death via the same
   Unit::Kill choke â†’ `dead=1`, keeps slot; revive pays `revive_fee`.
4. Data: 5 world-DB `spell_template` rows (e.g. 51001-51005): SPELL_AURA_MOD_SCALE
   +20..100% + SPELL_AURA_MOD_DAMAGE_PERCENT_DONE school-mask 127 same factor
   (covers both SpellCaster Done funnels; scale recomputes reach via
   UpdateModelData).
5. Login re-apply: deterministic from roster (strip stale, cast level spell) via
   the `m_Events.AddLambdaEventAtOffset(...,1)` idiom in
   SendInitialPacketsAfterAddToMap (Player.cpp:19251) â€” shared
   `SuiRts::OnPlayerWorldEnter` (R4 reuses for buffs). Bots included.
6. Wire/GM: 839 hero block + 840 live; `.sui rts honor`, `.sui rts hero
   declare|upgrade|revive|list`.

Verify: bot-vs-bot kill ticks pool with NO character_honor_cp growth; human kill
keeps vanilla honor; declared hero grows + hits harder by factor; 5th declare at
cap 4 refused; killed hero keeps slot, revive fee works; restart survives (pool,
roster, auras).

## R3 â€” Territory

1. `SuiTerritoryZone : ZoneScript` per hub row, registered in InitZoneScripts
   (Register.cpp pattern, double-gated); AB state model (neutralâ†’contested+timerâ†’
   occupied, copied semantics from BattleGroundAB.cpp:338) over the ZoneScript
   chassis: FillInitialWorldStates (already wired for any zone, Player.cpp:8311) +
   SendUpdateWorldState for contest UI; timers in Update (main).
2. Banner = custom GAMEOBJECT_TYPE_GOOBER template; gated call
   `SuiTerritory::OnBannerUse` in GameObject::Use's GOOBER branch (map thread â†’
   enqueue only). FLAGSTAND is BG-locked, off-limits.
3. Flip pipeline (main): guard swap = `sGameEventMgr.StopEvent(old,false)` +
   `StartEvent(new,false)` (game_event_creature GUID lists, no DB writes);
   persist zone_control async; recompute zone counts + supplies; feed zone-intel
   controller byte. KV `territory.flip_cooldown_ms` (30000) debounces.
4. Boot restore: read zone_control (fallback initial_controller); first Tick does
   an idempotent desired-guard-events sync (IsActiveEvent check) â€” never start
   events during world load.
5. Graveyards: shared `SuiTerritory::GetRtsGraveyard` (nearest safe-loc in a
   team-controlled zone, AV model, vanilla fallback; never cross-map) hooked at
   ALL THREE repop sites: Player::RepopAtGraveyard (Player.cpp:5075),
   AiBotAIBridge.cpp:2838-2988, AiBotAIMain.cpp:1095-1148.
6. Hero cap flips to `floor(controlledZones / territory.zones_per_hero_slot)`
   (default 2); `hero.slots_fixed` stays as the fallback when territory inert.
7. Wire/GM: controller byte live (+contested bit); 839 carries real supplies.
   `.sui rts zone <id> <faction>` (debug path IS the real pipeline),
   `.sui rts territory status`.

Data authoring (pilot 3 hubs, both continents): Sentinel Hill (Westfall), 
Crossroads (Barrens), Tarren Mill (Hillsbrad). 1 banner GO template + 3 spawns;
6 custom game_event ids (never-scheduled dates) + game_event_creature guard lists
(entries from GuardMgr's per-area pairs; fresh creature rows); 3 hub rows + zone
allotments.

Verify: banner click â†’ contested â†’ flip after capture_ms; guards swap; supplies
change; attacker respawn moves forward after flip and falls back after loss; hero
cap grows with zones, never demotes; restart mid-contest â†’ contest gone,
controller + guards correct after first tick.

## R4 â€” Dungeons

1. Entry gate `SuiDungeon::CanEnter` hooked in HandleAreaTriggerOpcode after
   trigger resolve (MiscHandler.cpp:704, BEFORE corpse-recovery special-casing) â€”
   deny controller faction; one live run per faction (runtime registry {mapId,
   team}â†’{instanceId, groupLow, startedAt}; closes on boss kill or instance
   unload + `dungeon.run_timeout_min` 120 safety net). Bot programmatic entry
   routes through the same check in the bridge travel path.
2. Clear detection: same Unit::Kill choke â€” victim entry == final_boss_entry on
   configured map â†’ enqueue; main-thread OnFinalBossClear: killerTeam â‰  controller
   â†’ flip + persist + buff swap; close run either way.
3. Faction buff: world-scope TeamCastSpell idiom (negative id = remove) over
   GetPlayers() under ReadGuard, ONLY in the main tick window; login apply via the
   shared OnPlayerWorldEnter hook.
4. Boss loot: gated pre-branch at Creature::GenerateLootForBody (Creature.cpp:1570)
   â€” NOT per-boss AI overrides (scripted bosses own their AI): roll
   `LootTemplate::Process(loot, store, rate, groupId)` once per item for
   `loot_items` (default 10, hard cap MAX_NR_LOOT_ITEMS 16) from the existing
   (Lootifier-enriched) pools; mark items FFA/under-threshold to avoid
   SMSG_LOOT_START_ROLL fan-out to the raid.
5. Wire/GM: 839 dungeon block live; `.sui rts dungeon <mapId> <faction|none>`,
   `.sui rts dungeon status`.

Data authoring (pilot 2): Deadmines (map 36, VanCleef 639) + Wailing Caverns (map
43, Mutanus 3654); buff spells = existing world-buff rows or 2 custom
spell_template rows.

Verify: A clears â†’ A buffed (bot + human + fresh login, both continents), A denied
at entrance, B enters, second B group denied while run live; boss corpse ~10 FFA
items from its real pool; B clears â†’ buff swaps; restart â†’ control/lockout/buffs
survive.

## R5 â€” MangosSuperUI: ruleset editor, worldstate swap, faction fleet, RTS orders

Build order R5.1â†’R5.4. R5.1 can follow the owner-operated R1 checkpoint to make
later save authoring less manual; it is not required to validate the landed R1.

**R5.1 Ruleset editor.** New `BotLogic/Core/Rts/RtsRulesetRegistry.cs` (compiled-in
key defs: type/default/min/max/description/ConsumedByPhase â€” kept in lockstep with
core's DDL seed list; unregistered DB keys shown read-only) +
`RtsTableRegistry.cs` (whitelisted column descriptors for `superui_rules_zone`,
`superui_rules_hub`, `superui_rules_hero`, and `superui_rules_dungeon` â€” the
SQL-injection guard AND the generic grid renderer; dungeon rows use `loot_items`).
Honor weights remain scalar `honor.weight.*` rows in `superui_worldstate`, not a
fifth rules table. `Services/WorldstateRulesetService.cs` uses the Characters
connection; its existence probe produces a "core hasn't booted RTS yet" banner,
writes are whitelist-bound, and reads may be cached for the brain. New
`Controllers/RtsRulesetController.cs` +
`Views/RtsRuleset/Index.cshtml` (ChatSettings-shaped endpoints; phase chips;
preset save/apply via a new web-owned `rts_preset` metadata table in
`vmangos_admin`/BotBrainDbInit, with apply explicitly writing the selected
characters save through the Characters connection rather than creating a second
authoritative rules copy;
`AppliedState` pending-restart indicator â€” banner: edits are LIVE-DB, read at next
boot; stowed saves are edited by loading them first). Extract InstancesController's
INSTANCES list to shared `Models/InstanceCatalog.cs` for the dungeon picker.
Sidebar entries + audit logging throughout.

**R5.2 Worldstate swap (BackupController).** (a) Refactor restores into DB-only
internals; (b) FIX RestoreCore dropping mangosd.conf (two-pass tar extract);
(c) manifest gains `mode:"worldstate"` + `worldstateName` + browsable
`rulesetSnapshot`; (d) `POST SaveWorldstate` (players+world+core all-or-nothing) and
`POST LoadWorldstate`: pre-flight file validation BEFORE stopping anything â†’ full
safety snapshot (`*_pre-worldstate-load`) â†’ single stop â†’ restore players+world+
core(conf) sequentially â†’ on failure DO NOT auto-start (name the safety snapshot) â†’
start â†’ prompt `.bot add_all` (existing Bots/AddAll); guardrail: plain single-group
Restore refuses worldstate saves without force. UI: Worldstates tab with two-step
confirm. Check free disk in pre-flight.

**R5.3 Faction population.** Surface `BotIdentity.Faction`/`bot_registry.faction`
(persisted, never consumed today): LiveFleet/FleetDiagnostics payloads + filters,
`GET /Bots/FactionCounts` (registered/live/cap from ruleset keys
`bots.cap.alliance` and `bots.cap.horde`), map dot colors, spawn UI faction toggle with
count-vs-cap bar (client warns; server-side cap is authoritative).

**R5.4 Brain RTS v1 â€” manual orders only.** New `BotLogic/Core/Rts/RtsOrder.cs`
(record struct, GroupOrder-shaped), `BotLogic/Brain/RtsCommandCenter.cs` (singleton
squadâ†’hub assignment store + per-tick `Stamp` after GroupCoordinator.Update; the
future autonomous director's seam â€” autonomy explicitly OUT of scope),
`BotLogic/Planners/RtsPlanner.cs` (new `Goal.Rts`; Capture = travel + CAPTURE_HUB
on arrival, gated fallback to MOVE_TO+hold until the C++ handler exists; Defend =
GroupDefend semantics), `Controllers/RtsOpsController.cs` + view (groups Ã— hubs Ã—
Capture/Defend/Clear buttons). Modify: Objective (append Capture=5/Defend=6, never
renumber), BotContext (+RtsOrder), GoalSelector (branch ABOVE the group branch),
BotBridgeService (SendCaptureHubAsync/SendDefendPointAsync, snake_case), 
BotBrainService (Stamp call), Program.cs (DI).

**R5 risks:** destructive restore (mitigations: pre-flight, safety snapshot,
no-auto-start on failure, force gate); live-vs-stowed edit confusion (banner +
read-only snapshot browsing); RA throughput for 500-bot spawns (keep 200/batch +
progress; `.bot add_all` is the fast re-field path; background queue = follow-up);
registry drift vs core DDL (one shared key list; both sides tolerate drift);
append-only enums.

## Client surfaces (MSUIClient; distributed into R1â€“R4 as each server phase lands)

Wire (matches the server allocation): client enum gains `CMSG_SUI_RTS_STATE`
0x0346, `SMSG_SUI_RTS_STATE` 0x0347, `CMSG_SUI_RTS_ACTION` 0x0348,
`SMSG_SUI_RTS_ACTION_RESULT` 0x0349 (= server 838â€“841; deploy-together rule per
phase that touches wire). The proven 5-step plumbing checklist applies
(`MSUIClient/Net/Opcodes.cs` â†’ WorldSession sender â†’ NetworkClient facade â†’ the
pump case in `MSUIClient/GameLoop/Scene/GameLoop.Net.cs` â†’ apply handler in
`MSUIClient/GameLoop/Scene/GameLoop.CommanderMap.cs`).

- **R1 client**: send CMSG_SUI_RTS_STATE on the commander map's existing 5 s
  cadence (piggyback the zone-intel throttle); parse SMSG_SUI_RTS_STATE
  stride-blocks into `_rtsMode`, `_rtsModules`, `_rtsFactions`, `_rtsHeroes`, and
  `_rtsDungeons`; the header shows the RTS campaign state and adds the Honor pool
  only when the Honor module is enabled. Zone-intel parser
  reads the 9-byte zone row (controller byte, always 0 until R3). The fullscreen
  overview shows both continents; stock ZMP ownership and shaped highlight art
  drive zone hover/click, and drill-in maps reveal every stock overlay without
  character exploration fog.
- **R2 client**: side-panel "HEROES" section (roster rows from the hero block:
  name via guid resolution, HL, dead flag) with Declare/Upgrade/Revive buttons on
  selected/party units â†’ CMSG_SUI_RTS_ACTION; results (841) surface in the
  commander notice line. Hero scale renders free for streamed units
  (OBJECT_SCALE_X); fix the known possessed-body gap: feed
  `CharacterRenderer.ModelScale` from entity Scale in `ApplyControlledCharacter`
  (`MSUIClient/GameLoop/Scene/GameLoop.Net.cs`) + per-frame refresh.
- **R3 client**: zone rect tint by controller (blue/red wash, contested pulse via
  the 0x80 bit), pill shows control; side panel gains the ore/skins/herbs supply
  row and controlled-zone counts from the faction rows.
- **R4 client**: side-panel "OBJECTIVES" rows from the dungeon block (dungeon name
  via map id, controller, live-run flags).

All client presentation remains in
`MSUIClient/GameLoop/Scene/GameLoop.CommanderMap.cs` plus the wire-plumbing files;
no new windows.

## Execution order & bookkeeping

Interleaved by play-test value: **owner-guided R1 validation â†’ R5.1 editor â†’ R2
(server+wire+client together) â†’ owner play-test â†’ R5.2 swap + R5.3 factions â†’ R3
(server+data+client) â†’ owner play-test â†’ R5.4 brain orders â†’ R4 â†’ final loop
test.** Each server phase: agents stop after the build; Nico alone writes/restores
worldstate saves, installs, deploys, and controls the runtime. Wire-touching phases
are handed to Nico as a paired client/server deployment. The
R5/R1 shared KV key list lives in `docs/SUI_WIRE_PROTOCOL.md`'s new ruleset
appendix (single source both sides read). After each phase: update
`CRPG_RTS_WIP.md` (session record + verification outcomes) and project memory.

## Verification (cross-phase)

Each phase ends with a compile/build result and an owner handoff. Nico alone installs
or deploys artifacts, writes/restores/swaps the characters/worldstate save, and
starts, stops, or restarts the runtime. The authoritative validation path is the
owner loading the intended save and performing a clean boot, then running the
phase's in-play checklist; wire phases use the matching client/server pair.
`.sui worldstate` and `.sui rts reload` may assist diagnosis but never replace the
clean-boot activation and rollback evidence. The vanilla regression gate must show
every tier-2 mechanic inert before a phase is marked validated.
