> Approved implementation plan, 2026-08-12. Status: R1 (foundation & ruleset) BUILT
> on both sides, deploy pending. Companion docs: CRPG_RTS_WIP.md (session records,
> owner decisions), box docs/SUI_WIRE_PROTOCOL.md (wire + ruleset key appendix).

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

| Phase | Where | Delivers | Playable check |
|---|---|---|---|
| R1 | server | ruleset loader, rate overrides, per-faction bot caps, `.sui ruleset` | 30x XP visible; caps hold |
| R2 | server+wire+client | faction Honor pool, hero declare/upgrade/revive (fixed slot scalar), scale+damage | declare a hero, watch it grow |
| R3 | server+data+client | hub capture, zone control, guard swap, graveyards, supply calc, zone-derived hero cap | capture a pilot hub, see map flip |
| R4 | server+data | dungeon objectives: entry gates, clear detection, faction buffs, 10x boss loot | clear Deadmines, hold the buff |
| R5 | web app+brain | ruleset editor, worldstate swap orchestration, RTS planner + capture orders, faction fleet UI | swap into an RTS save, order a capture |

## Server module layout & laws (all phases)

New files in `src/game/SuperUiBots/`: `SuiRts.h/.cpp` (singleton: ruleset boot-load,
KV accessors, module-enabled flags, per-faction atomics for honor/resources,
main-thread `Tick`, write-behind persistence, wire assembly, GM cores),
`SuiHonor.cpp`, `SuiHero.cpp`, `SuiTerritory.cpp/.h`, `SuiDungeon.cpp`, plus
`src/game/Server/Packets/SuiRts.h` (SuiControl.h conventions).

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

**Persistence convention**: all tables = idempotent core DDL at boot in
LoadRuleset(), characters DB, vanilla DBs boot clean. Config tables (rows ship in
the RTS save): `superui_rules_zone` (zone_id, ore, skins, herbs),
`superui_rules_hub` (hub_id, zone_id, name, banner_go_guid, event_alliance,
event_horde, capture_ms, initial_controller), `superui_rules_hero` (hero_level 1-5,
declare_cost, revive_fee, spell_id), `superui_rules_dungeon` (map_id,
final_boss_entry, buff_spell_id, loot_items). State tables (core-written):
`superui_faction` (team, honor_pool), `superui_heroes` (guid, team, hero_level,
dead, declared_at), `superui_zone_control` (zone_id, controller),
`superui_dungeon_control` (map_id, controller). Resources are DERIVED (sum of
allotments over controlled zones), never persisted; contest timers/live runs never
persisted.

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
3. Per-faction bot caps: gate in `PlayerBotMgr::AddBot` (PlayerBotMgr.cpp:411),
   per-team counters (decrement in bot remove path). KV `bots.cap.alliance/horde`
   (absent = uncapped).
4. Full wire allocation (above); 839 works both modes (vanilla: mode=0 + empty
   blocks); 840 answers UNSUPPORTED until R2.
5. GM: `.sui rts status`, `.sui rts reload` (suiCommandTable, Chat.cpp:95).

Verify: vanilla DB boots inert (log line, status, mode=0 packet); `mode=rts` +
`rate.xp_kill=35` â†’ 35x XP on a kill; `bots.cap.horde=10` holds; client gets
well-formed 839.

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

Build order R5.1â†’R5.4; R5.1 can land right after server R1 (it's how R1 gets tested).

**R5.1 Ruleset editor.** New `BotLogic/Core/Rts/RtsRulesetRegistry.cs` (compiled-in
key defs: type/default/min/max/description/ConsumedByPhase â€” kept in lockstep with
core's DDL seed list; unregistered DB keys shown read-only) +
`RtsTableRegistry.cs` (whitelisted column descriptors for superui_zone_allotments /
superui_hubs / superui_dungeon_objectives / superui_honor_weights â€” the
SQL-injection guard AND the generic grid renderer; dungeon table includes
`drop_roll_count`) + `Services/WorldstateRulesetService.cs` (Dapper over
Characters(); Exists probe â†’ "core hasn't booted RTS yet" banner; REPLACE INTO
writes; cached Get for brain reads). New `Controllers/RtsRulesetController.cs` +
`Views/RtsRuleset/Index.cshtml` (ChatSettings-shaped endpoints; phase chips;
preset save/apply via new web-owned `rts_preset` table in BotBrainDbInit;
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
`GET /Bots/FactionCounts` (registered/live/cap from ruleset key
`rts.population.max_per_faction`), map dot colors, spawn UI faction toggle with
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
phase that touches wire). The proven 5-step plumbing checklist applies (Opcodes.cs
â†’ WorldSession sender â†’ NetworkClient facade â†’ PumpNet case â†’ Apply handler in
Program.CommanderMap.cs).

- **R1 client**: send CMSG_SUI_RTS_STATE on the commander map's existing 5 s
  cadence (piggyback the zone-intel throttle); parse SMSG_SUI_RTS_STATE
  stride-blocks into `_rtsState` (mode, moduleFlags, per-faction rows); header
  shows mode + honor pool when RTS. Zone-intel parser reads the 9-byte zone row
  (controller byte, always 0 until R3).
- **R2 client**: side-panel "HEROES" section (roster rows from the hero block:
  name via guid resolution, HL, dead flag) with Declare/Upgrade/Revive buttons on
  selected/party units â†’ CMSG_SUI_RTS_ACTION; results (841) surface in the
  commander notice line. Hero scale renders free for streamed units
  (OBJECT_SCALE_X); fix the known possessed-body gap: feed
  `CharacterRenderer.ModelScale` from entity Scale in `ApplyControlledCharacter`
  (Program.Net.cs:1212-1267) + per-frame refresh.
- **R3 client**: zone rect tint by controller (blue/red wash, contested pulse via
  the 0x80 bit), pill shows control; side panel gains the ore/skins/herbs supply
  row and controlled-zone counts from the faction rows.
- **R4 client**: side-panel "OBJECTIVES" rows from the dungeon block (dungeon name
  via map id, controller, live-run flags).

All in Program.CommanderMap.cs (+ the 4 wire-plumbing files); no new windows.

## Execution order & bookkeeping

Interleaved by play-test value: **R1 server â†’ R5.1 editor (tests R1) â†’ owner
play-test â†’ R2 (server+wire+client together) â†’ play-test â†’ R5.2 swap + R5.3
factions â†’ R3 (server+data+client) â†’ play-test â†’ R5.4 brain orders â†’ R4 â†’ final
loop test.** Each server phase: agents stop after the build; Nico alone installs,
deploys, and controls the systemd-managed runtime. Wire-touching phases deploy
client and server together. The
R5/R1 shared KV key list lives in `docs/SUI_WIRE_PROTOCOL.md`'s new ruleset
appendix (single source both sides read). After each phase: update
`CRPG_RTS_WIP.md` (session record + verification outcomes) and project memory.

## Verification (cross-phase)

Each phase ends with: server build on the box, followed by Nico's owner-only
installation and service restart (paired client deploy when the phase touches wire),
then the phase's in-play
checklist run in a live session; `.sui worldstate rts` (or the DB row) flips the
test world; `.sui worldstate vanilla` must render every phase mechanic inert
(regression gate before each deploy).
