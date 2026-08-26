# CRPG/RTS Mode — WIP (updated 2026-08-15)

> **2026-08-15 MMO FACTION CONTROL-GROUP UPDATE:** source now includes
> capability-gated same-faction Free View control/orders, ten session-only
> `Ctrl+1` through `Ctrl+0` groups, sticky Free View cards, a command palette,
> per-bot patrol closure, and explicit real party/raid auto-formation (5 per
> party, raids above 5, 40 per raid). The paired authority and verification
> contract is `docs/systems/SYSTEM_CRPG_CONTROL_GROUPS.md`. Dynamic combat rules
> and enemy encounter intelligence remain design-only in
> `docs/plans/DYNAMIC_COMBAT_RULES_AND_ENCOUNTER_INTELLIGENCE.md`. No server
> deployment, live DB/World State mutation, or runtime action occurred.

> **CURRENT TIER-2 STATUS (2026-08-14): R1 PARTIALLY OWNER-VALIDATED; R2
> SOURCE/BUILD COMPLETE, OWNER HANDOFF.** Nico created and loaded a clean RTS World State. RTS
> mode/wire, configured scaling, clean character creation and the Commander
> campaign surface were observed; both faction bot-cap admission/refusal cases
> still need explicit proof before R1 is fully signed off. R2 Honor, bot Heroes
> and faction-wide singular direct control are implemented/build-verified across
> the client, MangosSuperUI World State profile and authoritative VMaNGOS
> Release/scripts build. No install/restart/live DB action occurred; owner
> deployment and live gameplay remain separate pending gates. World
> State create/resume is built and was used; the generic rules editor, faction
> fleet/strategic brain, R3 territory, R4 dungeons, capital commanders and victory
> state remain unbuilt. `docs/systems/SYSTEM_RTS_R2.md` is the exact R2 authority.
> This block supersedes status wording in the dated records retained below.
>
> **TIER-1 STATUS: CORE LOOP OWNER-VERIFIED IN PLAY (2026-08-11).** Possession +
> movement, party follow, bot bags + character sheet, free-view collision camera
> all confirmed working in play after Rounds 11–16 (summary below; sections are
> newest-first). This Tier-1 result is not Tier-2 R1 validation. Owner decisions
> in here are binding — do not redesign them.

## 2026-08-25 (late) — PARTY QUESTING & VENDORING: plan + P1 (quest facts) BUILT

New authority: [`docs/plans/PLAN_20_PARTY_QUESTING_AND_VENDORING.md`](docs/plans/PLAN_20_PARTY_QUESTING_AND_VENDORING.md).

The gap it closes: a bot in your control group was the ONLY bot in the world
that could not quest or vendor. Conscription (order 11) fences the C# brain out
by design, and nothing replaced it — while the client's whole quest/vendor
stack is hard-bound to your own character, so you could not even SEE what your
companions held. Meanwhile the server already has the mechanics: the bridge
handlers `QUEST_INTERACT` (accept/turn-in with spec-aware reward pick +
auto-equip), `SELL_ITEMS`, `REPAIR_AT_NPC`, `TRAIN_AT_NPC` are live and the
fleet runs them daily.

**Owner decisions (binding, 2026-08-25):** (1) vendoring is PER BOT, driven like
your own character — sweeps are a convenience on top, not the mechanism;
(2) quest rewards are per bot, chosen by you, with every member's picker visible
at once on a group turn-in; (3) real per-character quest logs, MERGED in the
view; (4) junk policy exposed, each service auto-complete or you-choose;
(5) world markers keep the vanilla `!` art with a parenthesised numeral over it
— `(4)` when four of five are eligible; (6) remove the 20-quest cap, ~100 held.

**P1 BUILT (client compiled + guard-checked; core compiled on the box, NOT
installed).** `CMSG_SUI_QUEST_FACTS` 854 / `SMSG_SUI_QUEST_LOG` 855,
capability bit 5, `NUM_MSG_TYPES` 856; roster-edge push + rate-limited pull
(limited SEPARATELY from the bag pull); new Party Quest Log panel — one row per
distinct quest across the party, one column per member, `L` in the free view.
Implementation record and file table in the plan §5b.

**The cap probe (this is the useful part for P2).** `MAX_QUEST_LOG_SIZE 20`
governs UPDATE-FIELD SLOTS, not storage: `mQuestStatus` is already a map,
`character_queststatus` is quest-id keyed, and `_LoadQuestStatus` ALREADY
tolerates `slot >= 20` (loads the status, skips the field write) — persistence
needs zero changes. One gate (`SatisfyQuestLog`). The real work is nine loops in
`Player.cpp` that iterate slots instead of the map — `KilledMonsterCredit`,
`ItemAddedQuestCheck`, `CastedCreatureOrGO`, `TalkedToCreature`,
`MoneyChanged`, `ReputationChanged`, `HasQuestForItem`, `HasQuestForGO`,
`ItemRemovedQuestCheck` — and in every one the slot index is used ONLY to fetch
the quest id.

**Correction to the first write-up of this (it said "undefined behaviour"):**
`mQuestStatus` is an ordered `std::map`, so insertion does NOT invalidate
iterators or references — iterating it directly is not UB. The real hazards are
smaller but still real: a key inserted mid-pass that sorts AFTER the cursor gets
visited in the same pass, and `operator[]` on a stale id silently creates a
NONE-status row. Snapshotting the ids is still the right move; the reason is
determinism, not UB.

Two more facts pinned from source, both for P3: `m_rewarded` — not `m_status` —
is what means "turned in" (VMaNGOS leaves the status at COMPLETE forever), and
**no bot AI answers any quest opcode** (`CombatBotBaseAI::OnPacketReceived`
handles seven cases, none of them quest; `AiBotAI` adds three, none of them
quest), so a vanilla shared quest to a companion dies silently today.

**P2 BUILT same session** (core compiled on the box, NOT installed). The cap is
now `Quests.MaxHeld` (config, default 100, floor 20); `MAX_QUEST_LOG_SIZE` stays
20 as the field layout. The mechanism is `Player::m_questsHeld` — the
authoritative list of held quest ids, slotted and slotless alike — which all
nine credit scans now iterate instead of the twenty slots. Slot promotion pulls
the oldest slotless quest up whenever a slot frees (reward, abandon), so the
stock client's twenty-quest view stays full. Persistence needed nothing:
`character_queststatus` is quest-id keyed and `_LoadQuestStatus` already
tolerated slotless rows.

**The real trap was not the one predicted.** `ItemRemovedQuestCheck` has NO
status check of its own — the slot loop's implicit "owns a slot" filter was
doing that job. Iterating held quests without stating the filter would have let
`IncompleteQuest` drag a finished DELIVER quest back to INCOMPLETE every time
one of its items left the bags. Also found and fixed in passing: a
**pre-existing out-of-range field write** in `SendQuestUpdateAddItem`, which
added the objective count to the SLOT index instead of the COUNTER index —
for a quest in a high slot that wrote past `PLAYER_QUEST_LOG_20_1` into
`PLAYER_VISIBLE_ITEM_1_CREATOR`, corrupting a GUID field. Raising the cap makes
high slots common, so it would have gone from rare to routine.

Because the held list is hand-maintained derived state, `ValidateHeldQuests()`
reconciles it against `mQuestStatus` on every save: a missing entry is REPAIRED
and logged at ERROR (`[QUEST-HELD]`), because a quest that silently stops
earning credit is the one failure mode play-testing would never surface. **If
that line ever appears in the log, a mutation path is not calling
`QuestHeldAdd`/`Remove` — find it, do not just enjoy the repair.**

## 2026-08-25 (late) — PLAN_20 P3: PARTY QUEST ACTS + shared quests BUILT

Client compiled + guard green (new `PartyQuestActs` clinical check); core
compiled on the box, NOT installed. Opcodes 856/857, capability bit 6,
`NUM_MSG_TYPES` 858.

**What it does.** Standing at a questgiver with companions, a rail appears
beside the quest frame: tick who comes along, "Accept for party (4)". On a
turn-in the rail becomes a reward BOARD — one column per companion, one row per
choice, every member's picker visible at once (owner decision 2). Members with
no explicit pick go as "auto", which is the fleet's own spec-aware
`ChooseQuestReward`. Refusals are reported per member BY NAME, never as "some of
your party could not do that". Action 3 addressed at yourself is the id-keyed
abandon that closes the P2 overflow gap. And the dead **Share Quest** button is
finally live.

**Design calls worth remembering.** (1) The act wire has NO whole-party
shorthand — the facts wire's empty list means "everyone", but acting on
someone's behalf must always name them. (2) Range: the requester answers to
INTERACTION_DISTANCE (5 yd, they are the one talking to the NPC), companions to
QUEST_SHARE_DISTANCE (14 yd) FROM THE REQUESTER — vanilla's own share rule. Five
bodies cannot stand within 5 yards of one NPC, and the bridge's 15.0f has no
authority. (3) The rail is a separate window at x=390 in the SuperUI skin, so
the 384-wide quest frame's parity tree is untouched and no parity claim is
invited; it does not draw while a parity proof is armed.

**Two of my own premises were wrong, both corrected in the plan.** Bot sessions
DO receive SMSGs — `WorldSession::SendPacket` diverts a socket-less session into
`GetBot()->ai->OnPacketReceived`. And the share hook belongs on
`SMSG_QUESTGIVER_QUEST_DETAILS`, not `SMSG_QUEST_CONFIRM_ACCEPT` (that one only
fires for escort quests). The reply must be QUEUED, not a direct handler call:
`HandlePushQuestToParty` sets the share info AFTER sending, so an inline accept
would kill the sharer's confirmation and strand the bot at BUSY forever.

**Bug found by copying carefully:** the bot bridge's accept path never casts a
quest's SOURCE SPELL. The real handler does. The party accept mirrors the real
handler, so it does too — the bridge path is still wrong and is worth a
follow-up for the unattended fleet.

Also: the client's `QuestTemplate` was discarding the quest flags word, so it
could not tell a sharable quest from any other. It now keeps it — the Share
Quest button owns that gate, because `HandlePushQuestToParty` does NOT check
`QUEST_FLAGS_SHARABLE` and will happily forward a quest it then refuses.

## 2026-08-25 (night) — fleet bots silently killed by Blizzard NAME DBCs (found + fixed)

Owner report after live deploy: 12 bots (all the Mister*/Uber* set,
Wafflesqueen, Keyboardking, and party member Barrensqueen) logged out 3s
after "load all bots". Probe (not theory) found the chain: SuperUI bot
CREATION never runs name validation, but `Player::LoadFromDB` does — and its
name-check branch is the ONLY silent early-return in the function. The names
tripped the client's own NamesReserved/NamesProfanity DBC regexes
(`\<mister`, `\<uber`, `queen\>`, and `king\>` on the profanity list), each
character got stamped CHARACTER_FLAG_RENAME (0x4000 — the forensic
fingerprint that proved the path), and the session was kicked with no log.
First load-from-DB after creation = first death; today's binary was
innocent. The gorilla/bear "missing players as animals" were unrelated: the
new bot-spec code gives spec-less hunters random exotic pets and shapeshifts
feral druids.

FIX (core, compiled on box, deploy = owner): `Player::LoadFromDB` name check
now carries the same `!IsBot()` waiver the account check two lines above
always had (fleet names are owner policy, not realm policy), and the
real-player failure path LOGS before refusing (no more silent kills).
Cleanup: the 12 rows need `character_flags & ~0x4000` (SQL handed to owner).

**Pet/shapeshift follow-up (owner: "that's wrong")**, same build:
1. Spec-less hunters (`spec_tab=255`, every migrated bot) no longer roll the
   17-beast world zoo — they tame RACE-PLAUSIBLE homeland fauna (orc:
   wolf/boar/scorpid/raptor, troll: raptor/cat/spider/bat, tauren:
   strider/wolf/boar, forsaken: spider/bat/wolf, dwarf+gnome:
   bear/boar/wolf/owl, night elf: cat/owl/spider/bear; wolf/cat/boar
   fallback). Spec'd hunters keep their doctrine pets (wolf/cat/boar).
2. Druid bots read as PEOPLE between fights: bear/dire-bear/cat/moonkin
   drop 8s after combat ends (`m_lastCombatMs` grace in
   `AiBotAI::UpdateOutOfCombatAI`); Travel/Aquatic are journey tools and
   stay; the next fight re-shifts per role/spec as before.
3. The `CastSpell: unknown spell id 0` log spam (Feararrow/Ragerogue): the
   bridge QUEST_CAST path cast a raw planner-sent spell id unchecked — now
   refused with a `QUEST_CAST_FAIL reason=bad_spell` event when the id is 0
   or unknown to the DBCs. (Slate/rotation casts were already entry-guarded;
   if the spam survives deploy, the source is elsewhere — re-probe then.)
4. **Ranged-bot pull LIVELOCK** (owner: "sitting in combat pose doing
   nothing for minutes"): every legacy class OOC routine ends with the
   load-bearing bridge `if (GetVictim()) UpdateInCombatAI_X()` — what fires
   a ranged OPENER at an armed victim during the pull window (attack intent
   set, combat flag not yet). The new `UpdateSpecOutOfCombatAI` returned
   true after buffs, short-circuiting BEFORE that bridge, so spec-profile
   casters armed the attack, parked at caster-chase distance, and nothing
   ever started the fight — a silent per-tick rescan/BeginPull/Attack-false
   loop (the "objective rescan hit … every second" signature) until the mob
   wandered into melee reach. FIXED: the same victim-bridge now ends the
   spec OOC path (fires `UpdateSpecCombatAI`). Compiled on box with the
   rest; deploy = owner.

## 2026-08-25 (evening) — RTS layout, panels v3, INSTANT PARTY TRADE (Phase C v1)

Owner feedback round from creator mode; everything client-side built + guard
tools green, server compiled on box (deploy gates unchanged).

1. **Free-view layout** (owner ask): the minimap is SQUARE and docks
   bottom-LEFT (WC3 furniture); the chat frame lifts 124 logical px above it;
   the spell-tooltip default anchor now clears the commander console (134px)
   instead of the moved map. **B in the free view opens Party Inventory**
   (everyone's bags) instead of the hidden body bags.
2. **Party Tactics layout fix** (owner: "ImGui stuff is weird, clipping the
   top"): the raw SmallButton flow (staircasing over the chrome) replaced
   with explicit-position vanilla buttons + GameText labels in the clipped,
   scrolling child; role pick shown as a gold ring.
3. **Party Inventory v3** (owner ask): each member column is now a MINIFIED
   CRPG character sheet — paper-doll rails (vanilla arrangement) around the
   member's baked portrait (the self column uses the own-portrait booth),
   weapons row beneath — then bags in SMALL icons (20px) with per-column
   vertical scroll. Columns are CENTERED with equal widths/gaps/margins
   derived from the live window width (owner: "fill with margin from both
   sides, equal spacing for the number of people") — resize reflows them and
   the bags-per-row count; a 150px logical floor falls back to horizontal
   scroll. DRAG AND DROP: drag a bag item onto any cell, the portrait, or
   the column of another member to hand it over (ImGui native DnD, empty
   typed payload + stamped fields); right-click "Give to …" remains.
4. **INSTANT PARTY TRADE — Phase C v1 (owner ask "move things between party
   members realtime")**: right-click a bag item in Party Inventory → "Give
   to <member>". New wire pair `CMSG_SUI_MEMBER_ITEM_MOVE = 852` /
   `SMSG_SUI_MEMBER_ITEM_MOVE_RESULT = 853` (`NUM_MSG_TYPES = 854`),
   capability bit 4 (party-item-move-v1), exact-length parsing, endpoints =
   own character or party AiBot (party line; faction never suffices), same
   map, no trade window open, binding deliberately not a gate (the CRPG
   shared backpack), conjured items refused. Mechanics mirror the proven
   trade-completion sequence (CanStoreItem → MoveItemFromInventory →
   MoveItemToInventory). After an accepted move the server re-pushes BOTH
   endpoints' snapshots to every real SUI group member — columns update from
   pushes, never optimism. Box `docs/SUI_WIRE_PROTOCOL.md` updated;
   `interface-wire-check` extended (builder/parser round-trips, opcodes
   852/853, capability bit 4, give-menu wire mapping law).

## 2026-08-25 (later still) — Attack icon fix; WC3 console v2 (CREATOR-MODE review)

The owner reviewed the console in CREATOR MODE (offline sandbox) — "Yes that
works" refers to the console shell there. Member-facts live proof (SuperUI
bots on the real server, never-possessed bags/spells) is STILL PENDING the
usual deploy gates; nothing in this entry changes the deploy-state table
below.

1. **Attack icon fix** (owner caught it in creator mode): the console card and party quick
   slots drew spell 6603's raw DBC icon — the internal Temp face. Both now
   route through the existing `ActionIconLaw` (weapon-icon substitution), and
   `ResolveEquippedItemIcon` falls back to PUBLIC visible-item entries so a
   never-possessed bot's weapon still resolves (no inventory-guid wiring
   needed).
2. **Hover tips no longer fight the cursor** (owner report: the hardware
   cursor draws over the stock mouse-anchored ImGui tooltip): new `HoverTip`
   helper (`GameLoop.VanillaUi.cs`) anchors gameplay hover tips ABOVE the
   cursor's space (bottom-left pivot at the hotspot, display-clamped,
   TextUnformatted so literal '%' never hits printf). All 25 player-facing
   sites across the HUD + panels route through it; dev/creator tooling keeps
   stock behavior; the minimap resource-dot tooltip keeps its clinically
   frozen SetTooltip seat (B4/B5 fence — approved anchor + fade lifecycle).
3. **Console card polish**: role medallion on the portrait corner (shared
   `DrawRoleMedallion` with the party rows), `enlisted · <last order>` state
   on the level/class line, and a right-aligned `Route · N pts` readout on
   the scope row when the selection has an authored waypoint chain.
4. **Spell tooltips clear the docked minimap** (owner report: hovering a card
   ability parked the tooltip over the map): the prepared spell-tooltip
   snapshot now carries a bottom clearance — in free view the vanilla
   default bottom-right anchor lifts above the minimap dock (206 logical px);
   normal play keeps the stock anchor. B2 fence extended with the new
   snapshot field (renderer still reads the snapshot alone).
5. **Bags & Tactics live on the console** (the free view hides the bag bar
   and micro menu): a utility row under the squad grid opens Party Inventory
   / Party Tactics for the whole party, and the unit card carries per-member
   Bags/Tactics buttons (party privilege — disabled with the reason for
   non-party units).
6. **Patrol is a mode now** (owner ask): click Patrol → the button arms (gold
   border) and right-clicks chain COLD waypoints — nothing is ordered while
   drafting (gold dashed route + bright dots, "Drafting route · N pts" on the
   scope row); click Patrol again → every leg is queued (order 3) and the
   loop engages (order 4), the draft becoming the standing chain (same
   retirement machinery). Escape cancels the draft ahead of every menu layer
   (pre-gate beside the dev-edit one, per the doc's Escape order); leaving
   the free view or losing the session cancels silently. Subjects freeze at
   arm time; attack picks are swallowed while drafting. The legacy
   Shift+RightClick chain still works and Patrol still engages it directly.
7. **WC3 commander console v2** (`GameLoop.CommandShelf.cs` rewritten): the
   plain auto-sizing shelf became a fixed three-region console — SQUAD grid
   (all ten wells: click recall, Shift+click save, matching the 1-0 keys),
   INFO panel (scope line; portrait unit card — party-frame 3-D bake, level/
   class, vitals, read-only ability row — for one unit; baked-portrait chips
   for a group), and the icon COMMAND CARD carrying the same seven verbs,
   orders/voices/chat lines unchanged. Art per the owner's design language:
   pet-bar idiom from the shipped FrameXML via mpqpeek (Focus=
   Ability_GhoulFrenzy, Regroup=Ability_Tracking, Hold=Spell_Nature_TimeStop),
   Patrol=Ability_Hunter_Pathfinding, Line=INV_Banner_01,
   Circle=Spell_Holy_PrayerofHealing, Sheathe/Draw=Ability_Warrior_Disarm/
   INV_Sword_04 — every path archive-verified before use.

## 2026-08-25 (later) — PARTY MEMBER FACTS BUILT (both sides; deploy pending)

The owner decision below (party = full facts, faction = orders) is now
implemented end to end; the handoff doc
`docs/current/ui/CRPG_PARTY_MEMBER_FACTS_NEXT_AGENT_PROMPT.md` carries the
full build record in its status header.

1. **Server** (`~/vmangos`, compiled clean on box, NOT installed/restarted):
   new opcodes `CMSG_SUI_MEMBER_FACTS = 850` / `SMSG_SUI_MEMBER_SPELLS = 851`
   (848/849 left reserved for the rotation pair; `NUM_MSG_TYPES = 852`),
   capability bit 3 in the control-ACK trailer, roster-edge push
   (`BroadcastRoster → PushMemberFactsTo`: every party AiBot's
   `SendSnapshot` + new `SendMemberSpells` per real SUI member), exact-length
   pull handler with a 1/s per-session rate limit. Authorization: real player
   session + AiBot subject + SAME group/raid; faction authority insufficient.
   Box `docs/SUI_WIRE_PROTOCOL.md` updated.
2. **Client** (compiles clean): `ApplySuiSnapshot` possession-era fence
   lifted — party/raid members accepted (`IsPartyMemberFactsSubject`),
   non-party still dropped honestly, `ApplyControlledCharacter` stays
   controlled-only; new `GameLoop.MemberFacts.cs` (capability latch,
   roster-fingerprint auto-pull, panel-open pulls, member-spells handler
   seeding `ActionsFor` + `PopulateBotBar` + the `botbars.json BotSpells`
   cache); `Net/MemberFactsWire.cs` wire law; "possess once to sync" /
   "?"-well texts retire for party members under the capability.
3. **Verification:** all four guard tools green;
   `interface-wire-check --party-member-facts-only` added (wire round-trips,
   exact-length refusals, opcode/capability constants, snapshot-gate law).
   Inventory dirty-hook re-send deferred by the handoff's own allowance
   (age stamps + pulls cover staleness).

Deploy state unchanged in kind: mangosd compiled on box NOT
installed/restarted; MangosSuperUI built NOT published (deploy the two
TOGETHER — the brain is untouched by member facts but the conscription fence
still requires the pair); client rebuild after closing the running instance.
Live proof is owner-run with SuperUI bots: party with never-possessed bots →
quick slots + Party Inventory columns populate with nobody possessed.

## 2026-08-25 — Phase A UI shell complete + free-view console; NEXT: party member facts

Continuation of the 2026-08-24 session (below). Everything client-side, all
built and guard-tools green; nothing deployed anywhere this round.

1. **Phase A comprehension shell BUILT** (the full ledger lives in
   `docs/current/ui/CRPG_RTS_MMO_PARTY_COMMAND_UI.md` "Build state"): party-row
   quick slots (truthful, honest "?" wells) + corner role medallions +
   order-state chips; command shelf with the full verb set
   (Regroup/Hold/Focus/Patrol/Line/Circle/Sheathe) and SQUAD-aware scope;
   free-view number keys (1-0 pick, Ctrl+1-0 save; bars never eat numerals in
   free view); Party Tactics panel (role + quick-slot AI policy in
   botbars.json `BotSlotPolicies`; stances disabled with reason); Party
   Inventory in BG3 columns; vanilla skin pass (UI-Panel-Button verbs, tooltip
   backdrops, dialog chrome, pet-bar autocast corners); WC3 selection chips;
   mockup HTML re-skinned to the vanilla idiom.
2. **Free View is a costume change now** (owner ask): body chrome — action,
   multi, stance, pet bars, bag bar, micro menu — hides in free view; the
   commander console owns the bottom edge (always present: squads row + hint
   when empty; WC3 unit card with vitals + read-only abilities on single
   selection); the minimap docks to the bottom corner.
3. **Snapshot RETENTION** (owner pushback, correct): possession was always
   full access one-bot-at-a-time; the client was purging the snapshot on
   release. Now kept per-bot with age stamps — sync each companion once and
   the BG3 inventory view shows the whole party, honestly aged.
4. **OWNER DECISION + NEXT WORK ITEM — party = full facts, faction = orders:**
   party/raid members get bags + skills pushed WITHOUT possession; faction
   bots stay command-only; Tier-2 RTS rules come later. Implementation handoff
   (server seams, wire format, triggers, authorization, capability bit, client
   gate to lift, verification):
   `docs/current/ui/CRPG_PARTY_MEMBER_FACTS_NEXT_AGENT_PROMPT.md`.

Deploy state: mangosd compiled on box NOT installed/restarted; MangosSuperUI
built NOT published (deploy the two TOGETHER); client compiles clean (rebuild
after closing the running client — bin lock).

## 2026-08-24 — mass-move fix, companion voice, formations, conscription (client built, core compiled NOT deployed)

Five changes from the owner's 20-bot free-view session:

0. **Conscription — "assign a group = the questing brain is off" (core + client +
   brain, all built, NONE deployed):** control-group membership IS enlistment.
   Assigning bots to a group sends additive order 11 (`ORDER_CONSCRIPT`);
   dropping them from their last group sends 12 (`ORDER_DISMISS`, resume
   questing in place — owner decision). Three walls: STATE `conscripted:1` parks
   the brain planner at `why=conscripted` (objective preserved, wedge/stuck
   clocks kept warm, enlisted deaths never shelve a quest), the C++ bridge fence
   drops planner lines (`CONSCRIPTED_DROP`; PING/COMBAT_DIRECTIVE/LOAD_ROTATION/
   LOAD_RAID_PLAN pass — conscripts keep combat AI and stay raid-plannable), and
   `BotExecutor` refuses planner traffic client-of-the-brain-side. Commander
   logout musters the army out server-side. Roster flag 0x04 + "enlisted" chip
   on the party strip. Contract: `SYSTEM_CRPG_CONTROL_GROUPS.md` §5c.
   **Deploy mangosd and MangosSuperUI together** (an old brain would churn
   against the new fence). MangosSuperUI deploy: `dotnet publish -c Release`
   (linux-x64 profile) → scp to `/tmp/mangossuperui-deploy/` → `~/deploy.sh`.

1. **Mass-move parse spam FIXED (client):** vmangos batches `SMSG_MONSTER_MOVE`
   splines into `SMSG_COMPRESSED_MOVES` under load (rate gate in
   `WorldSession::SendMovementPacket`); the client treated any non-relay record
   as fatal and dropped the REST of the batch — so ordering many bots at once
   spammed `[net] parse error` and lost their walk splines. Records now parse
   individually: monster-moves and spline-speeds route to their standalone
   handlers, unknown opcodes skip with a log-once. `interface-wire-check` updated.
2. **Voice chorus (client):** an order to 4+ bots answers with 2 voices, 10+
   with 3, cascading 0.4 s apart, randomized speakers, distinct race/gender
   voices preferred (`CompanionVoiceLaw.ChorusSize`).
3. **RTS discipline (core, compiled on box, mangosd restart pending):** every
   `CMSG_SUI_ORDER` sets `m_suiRtsHold`; `DoRandomWander`/`DoGrindPatrol` stand
   down until `SuiAbandonJourney`. Hold previously re-armed the stroll.
4. **Formations + sheath (core + client, additive orders 8/9/10; 7 stays
   reserved for auto-group):** LINE ranks-of-five facing the commander, CIRCLE
   outward ring, SHEATH toggle overriding the AI auto-arm until combat draws
   steel. New Free View bottom-center command shelf (Hold/Line/Circle/Sheathe)
   orders the current selection. Contract details:
   `docs/systems/SYSTEM_CRPG_CONTROL_GROUPS.md` §5b. Old server ignores 8–10
   silently, so client and core need not deploy in lockstep — but formations
   only work after the new mangosd is installed+restarted (owner's call:
   `cd ~/vmangos/build && make install && sudo systemctl restart mangosd`).

## 2026-08-11 session summary (read this first)

The day started with "possession doesn't move the character and nobody follows"
and ended with the core loop verified. One line per round, newest first — each
has a full section below:

| Round | What | State |
|---|---|---|
| 18 | SOLO free view: `HandleOrder`'s `if (!group) return;` silently ate every RTS order from a partyless owner → solo now orders the own character (and only it); empty subjects solo = own char. **Compiled on box, mangosd restart pending (owner's call — kicks the live session).** No wire change, no client pairing. | built, deploy pending |
| 17 | Round 15 REVERSED (owner, 2026-08-11 evening): the floating-body camera reads as a "fake ceiling" when detaching under WMO geometry (indoors, gate arches, city overhangs). Free-view rig is a ghost again — `FlyCollide` hard false in `UpdateFreeCamSelection` (not the saved setting: old default persisted `true` into settings files). Same session: solo own toon now clickable for the halo (`PickUnit` ControlledGuid skip lifts in `_freeView`), free-view wheel flies the rig instead of the 40-yd orbit zoom, and **selection rings + move markers get the FlatDiscVertices WMO-floor fallback** — `RenderSelectionRings`/`RenderMoveMarkers` were terrain-projection-only, so the halo/confirm never drew on Stormwind streets or any WMO floor (party tests passed only because they ran on open Elwynn terrain). | in client build, needs eyes-on |
| 16 | Camera-through-walls in NORMAL play (pre-existing): MinDistance clamp overrode the wall ray → vanilla-style first-person dip | fixed, needs eyes-on |
| 15 | Free-view camera is a floating BODY: collides with walls/ceilings, fly through doors (replaces the cutaway) | ~~owner-verified~~ REVERSED by round 17 |
| 14 | Free-view under-map clamp + Divinity cutaway v1 | clamp in; **cutaway REJECTED** (round 15), default OFF |
| 13 | Bot bags empty (FOUR stacked causes; killer: synthetic entities had `Entry = 0`) + stuck cast animation in free view | owner-verified |
| 12 | Cape geoset law, sheet name + stats (SNAPSHOT v2 wire block), walk-toggle clear, free-view cast/melee facing | working; facing needs a repro if still off |
| 11 | **The big one**: stale `SplineDonePending` made the server discard ALL client movement for any AI-walked body | owner-verified |

Open items going into the next session:
1. **Facing while commanded** (round 12): owner "not sure it's properly fixed" —
   needs the specific failing scenario (cast error vs model not turning vs melee
   drift; the last two have known designed limits).
2. **Walk-mode mystery** (round 12): mitigated by clearing the toggle on
   hand-offs; if it recurs, first check whether Shift was held (Shift-while-
   grounded = walk by design and it is in hand during control chords).
3. **Camera diagonal graze** (round 16): thin edges can still slip the single
   centre ray; add near-plane corner rays if seen live.
4. **Cutaway code is parked** behind `Settings.Controls.FreeViewCutaway`
   (default OFF) — delete it or revive it, but don't ship it silently.
5. **Scattered bots**: bots that sat in Solo doctrine get quested far away by
   the brain (pre-round-11 observation: Kaelrunner/Earthadin/Lornkeeper ended up
   across the world). RefreshDoctrine now abandons Solo-era journeys on joining
   a party, but there is no recall/regroup UX for already-scattered bots.
6. **Client TODO (v1.1)**: implement `CMSG_MOVE_SPLINE_DONE` — outside the SUI
   flows a server spline on the own character (charge-type effects) can still
   wedge the movement-blocking flag (round 11's clears cover only SUI paths).

## 2026-08-12 — RTS phase R1 build record (status superseded above)

This section records the build checkpoint as it stood on 2026-08-12. Its pending-
deployment language is historical; the current status at the top of this file is
authoritative.

⚠ **WIRE CHANGE — deploy together.** New opcodes 838–841 (`CMSG/SMSG_SUI_RTS_STATE`,
`CMSG_SUI_RTS_ACTION`/`SMSG_SUI_RTS_ACTION_RESULT`), `NUM_MSG_TYPES` → 842, and the
zone-intel zone row grew 8→9 bytes (+controller, 0 until R3). At this checkpoint,
the pending server binary carried commander map (836/837) + worldstate scaffold +
R1, and the client built to match. Neither had been installed yet. R1 has since
been deployed, while owner-operated RTS-save validation remains pending.

The full phased plan (R1–R5) is approved and recorded (plan file + this doc's
Parts 3–5). R1 delivers: `SuiRts` module (`src/game/SuperUiBots/SuiRts.{h,cpp}`) —
boot-time ruleset through VMaNGOS `CharacterDatabase` (this deployment's active
configured schema is `characters`): `superui_worldstate` KV plus the
`superui_rules_*` and `superui_*` state tables. DDL is idempotent and may exist
under vanilla; the mode gate, not table absence, keeps Tier-2 inert. Rate overrides use
`sWorld.setConfig` (XP/drop/money read live), per-faction bot caps enforced in
`PlayerBotMgr::AddBot`, a faction Honor-pool persistence scaffold (atomics + 30 s
write-behind + shutdown flush, but no R1 accrual caller), the RTS state/action wire
(stride-versioned blocks; actions answer "unsupported" until R2), `.sui rts
status|reload`, and the boot-order fix
(worldstate+ruleset load moved BEFORE InitZoneScripts). Client: parses RTS state,
piggybacks the request on the commander-map 5 s cadence, shows `RTS CAMPAIGN` in
RTS mode and appends Honor only when the Honor module is actually enabled, and reads
the zone-row controller byte. Key list + wire spec:
`docs/SUI_WIRE_PROTOCOL.md` on the box (new ruleset appendix — the single source
the R5.1 web registry must mirror). `.sui rts reload` is a development diagnostic,
not the production save-transition path or proof of boot persistence.

**R1 owner validation: NOT RUN.** The authoritative, isolated owner-operated
checklist is in `RTS_WORLDSTATE_PLAN.md`. In summary, it records a vanilla/inert
boot baseline, an isolated RTS-save boot with a known XP rate and per-faction bot
cap, the matching 839 state packet and (if diagnostically exercised) R1's expected
840 `UNSUPPORTED` result, and a return to the vanilla/inert save. Nico alone
performs save/database changes, deployment, and runtime control. Codex may walk
through the checklist, inspect source/schema/config/logs read-only, and perform a
requested build; it must not mutate a database or control the runtime.

## 2026-08-12 — TWO-TIER RULE (owner, binding) + worldstate scaffold

The mode splits in two, and the boundary is binding:

- **Tier 1 — vanilla-valid CRPG/RTS** (possession, free view, orders, links,
  bot bars, **commander map + zone census**): extensions of rules that are
  legal in the OG MMO world. ALWAYS available, in the normal world too. Never
  gate these on the worldstate.
- **Tier 2 — match mechanics** (XP scaling, hub captures, hero units,
  non-respawning faction commanders, win conditions): fundamentally CHANGE the
  game. One core, no second binary — but tier-2 code must be **inert unless
  the loaded save says otherwise**. The intended R5 owner flow will stow the
  vanilla save and load an RTS worldstate through MangosSuperUI; R5 is not built.
  The rules and match state travel WITH the `CharacterDatabase` save.

**R2 correction to the historical scaffold paragraph below:** the R2-capable
source makes worldstate and rules an immutable boot latch. Runtime
`.sui worldstate rts|vanilla` and `.sui rts reload` requests are refused; they
cannot activate or reconfigure a running process. R2 Honor, bot Heroes and
faction control now consume explicit boot module rows. Production changes only
through Nico's owner-operated World State load/start path. The older runtime
override and "R2 not built" sentences below are retained only as chronology.

Scaffold (deployed): `superui_worldstate` through `CharacterDatabase` (this
deployment's active configured schema is `characters`; the row `mode='rts'`
belongs only in an RTS save) → `SuiPossess::LoadWorldState()` at boot (logs
`[SUI] worldstate: ...`) →
`SuiPossess::RtsWorldState()` predicate. `.sui worldstate [rts|vanilla]` = GM
inspection + runtime-only test override. **BINDING RULE: no tier-2 mechanic
ships without a `RtsWorldState()` gate.** R1 consumes the boot mode for rate
overrides, per-faction bot caps, and the RTS state header/wire; R2–R5 consumers are
not built. Runtime overrides and `.sui rts reload` are diagnostics only. Production
activation and validation use the owner-operated boot path documented in
`RTS_WORLDSTATE_PLAN.md`.

## 2026-08-12 — Commander map v1 BUILT (Part 1 of the future design below)

⚠ **WIRE CHANGE — client and server deploy TOGETHER** (new opcode pair; a new
client against the live pre-836 binary gets kicked on the first M in free view).
At this historical checkpoint the server and client compiled clean but had not yet
been deployed or seen in play. The current R1 deployment status is recorded at the
top of this file; live Tier-2 validation remains pending.

Owner decisions taken this session (interaction model, supersedes "TBD click
actions" for v1): continent view hover = intel; **clicking a zone ZOOMS into a
zone map showing your own units; clicking a unit exits the map and parks the
free-view camera ~25 yd above it** (ground click = fly there at ~60 yd, map
stays open). Standard 1.12 zone granularity. Cross-continent clicks are
DISABLED with a notice — hard server fact: `Camera::SetView` refuses a
viewpoint on another map, so the streaming eye can never leave the character's
continent; phase 1.5 (teleport the character under the view) is designed in the
plan but deferred pending an owner decision on moving the body.

Wire: `CMSG_SUI_ZONE_INTEL` 836 / `SMSG_SUI_ZONE_INTEL` 837 (`NUM_MSG_TYPES`
→ 838). Client polls every ~5 s while the map is open; reply = per-zone
{bots, players} census (all maps, sparse) + the asker's own forces with live
positions (self + group — the client can't see unstreamed members). Both blocks
carry an explicit row stride so future servers can grow rows without a version
bump. Full spec: `docs/SUI_WIRE_PROTOCOL.md` on the box (also gained the
missing CMSG_SUI_CAM section).

Client: new `MSUIClient/GameLoop/Scene/GameLoop.CommanderMap.cs` (state, census cache, fly-to +
terrain-settle latch, all drawing); M routing branches on `_freeView` in
`UpdateWorldMapInput`; draw seam above the world map's in `DrawCombatHud`; Esc
rides the existing WorldMap escape layer; free-view edge pan / wheel-fly /
marquee are gated off while the map is up (cam heartbeat deliberately keeps
running — click-to-fly rides it); leaving the free view force-closes the map,
entering it force-closes a leftover vanilla map. Fly-to = rig `Teleport` +
`_freecamCamSentAt = 0`; the eye follows on the next heartbeat and far same-map
jumps are safe (the eye is an active object — destination grid force-loads).

Current presentation: the Commander surface is fullscreen and its overview shows
Eastern Kingdoms and Kalimdor together. Hover/click ownership comes from the stock
1.12 continent ZMP grid; the shaped, colored `<Zone>Highlight.blp` art brightens
the exact zone and labels it. Clicking enters that zone's full map, where every
`WorldMapOverlay.dbc` row is drawn (Commander has no exploration fog). Population
presence and the free-camera marker remain layered above the map. Known cuts:
GM-invisible characters count as players and faction breakdown is not shown.

**Hero units (Part 2) are NOT started.** Direction + groundwork trace (core
hooks: aura scale + `UpdateModelData`, `DoneTotalMod` multiplier, SpellModifier
hero spells, `Unit::Kill` XP hook, first custom DB table) live in the approved
plan; owner reframed heroes as part of a MATCH ruleset — MangosSuperUI DB/core
swap, ~20x XP, 10–30 h matches, win = kill the enemy faction's 4 non-respawning
commanders.

## What this is

CRPG/RTS hybrid on top of the SuperUI possession stack: possess any party bot
(Ctrl+Tab / Alt+click portrait / **click a toon in the free view**), Ctrl+F for a
free RTS camera with marquee selection and right-click orders, Divinity-style
chain links on the party portraits, layered per-class/per-bot skillbars.

## Future design — Azeroth commander map + hero units (owner direction, 2026-08-12)

This is the next strategic layer: a proper Azeroth-scale commander view plus
Warcraft-style hero units that exist and progress inside the live world. The
requirements below are owner direction; items explicitly marked TBD are open
design decisions, not permission to silently choose a different design.

### Part 1 — strategic commander map in detached mode

- While the player is in `Ctrl+F` / detached free-view mode, pressing `M` must
  open a purpose-built **RTS commander map**, not the standard foggy world map.
- The mental model is **Total War at Azeroth scale**: the world is presented as
  a set of strategic, hoverable zones rather than only as the ordinary MMO map.
- Hovering a zone must expose live strategic intelligence. The baseline required
  information is the number of **bots** and **players** currently in that zone.
  The zone-intel model should leave room for additional information as the
  strategic layer develops.
- This needs to be an information/command surface, not merely a visual reskin of
  the existing map. Exact click actions and cross-zone command capabilities are
  still TBD.
- The normal `M` behavior outside detached mode is unchanged unless separately
  redesigned later.

TBD: zone boundaries/granularity, faction breakdown and information visibility,
fog-of-war rules, refresh cadence, which additional zone facts are shown, and
what selecting/clicking a zone allows the commander to do.

### Part 2 — Warcraft RTS hero units inside the world

Add a hero-unit progression system across both the vmangos server C++ and this
client. Most authority and gameplay logic should live on the server, with the
client providing the required presentation, feedback, and control surfaces.

Core direction:

- Each faction can have up to **N active heroes**. `N` is deliberately TBD.
- Heroes progress through **Hero Level 1–5**. The final in-world name for this
  progression is TBD; **General** is a current naming candidate.
- Hero XP comes from a curated, TBD list of important enemies in the live world.
  Candidates include world bosses, rares, selected NPCs in towns, and major
  figures in faction cities.
- Broader overworld faction warfare also contributes a portion of Hero XP:
  qualifying kills against the opposing faction count whether the defeated
  combatant is an NPC, bot, or player. The contribution/distribution formula is
  TBD.
- A hero's model scales visibly with Hero Level, using this intended ladder:

| Hero Level | Model scale | Ability damage scale |
|---:|---:|---:|
| 1 | 1.2x | 1.2x |
| 2 | 1.4x | 1.4x |
| 3 | 1.6x | 1.6x |
| 4 | 1.8x | 1.8x |
| 5 | 2.0x | 2.0x |

- All damaging abilities receive proportional damage scaling with Hero Level.
- Each supported class also gets distinctive **hero abilities** made by
  modifying existing spells. The supported class list and exact modifications
  are TBD. Illustrative direction: a Hero Level 5 mage's Frost Nova could have
  **2x radius with a guaranteed freeze**.

Accepted hero law: R2 uses `hero.slots_fixed` as its declaration cap; R3 derives
capacity from `territory.zones_per_hero_slot`. Falling below cap never demotes an
existing hero. A dead hero remains in the roster and occupies its slot; revival
costs faction Honor. Still open: exact cap/ratio and fees, final rank terminology,
eligibility/promotion, supported classes, curated XP targets and values, shared-XP
assignment, and the exact hero-spell package and tuning for each class.

### Part 3 — the RTS ruleset is DATA, tunable from SuperUI (owner: CRITICAL; 2026-08-12)

Engineering shape for every tier-2 mechanic — this is what "semi-modular" means:

1. **Database ownership is explicit.** VMaNGOS `CharacterDatabase` owns the RTS
   header, rules, and match state; this deployment's active schema is
   `characters`, never `vmangos_admin`. `WorldDatabase` owns shared authored
   content referenced by the rules (banner templates/spawns, events, spells,
   creatures, and loot). Future MangosSuperUI presets and audit records may live
   in `vmangos_admin`, but applying one writes the authoritative rows through the
   app's Characters connection. The core never reads an admin-DB copy of a rule.
2. **Scalar knobs** are key/value rows in `superui_worldstate`. Landed R1 keys
   include `mode`, `state.flush_ms`, `bots.cap.alliance`, `bots.cap.horde`,
   `rate.xp_kill`, `rate.xp_kill_elite`, `rate.xp_quest`, `rate.drop_money`, and
   `rate.drop_item_poor`, `rate.drop_item_normal`, `rate.drop_item_uncommon`,
   `rate.drop_item_rare`, `rate.drop_item_epic`, `rate.drop_item_legendary`,
   `rate.drop_item_artifact`, and `rate.drop_item_referenced`.
   Accepted later-phase names include `honor.weight.*`, `hero.slots_fixed`, and
   `territory.zones_per_hero_slot`; their consumers are not built. Core reads the
   authoritative rules at boot into in-memory state. Current scalar parsing has
   only minimal numeric validation, so the R1 checkpoint deliberately uses a small,
   known-safe key set and sane values.
3. **List-shaped config** uses the landed CharacterDatabase schema:
   `superui_rules_zone(zone_id, ore, skins, herbs)`,
   `superui_rules_hub(hub_id, zone_id, name, banner_go_guid, event_alliance,
   event_horde, capture_ms, initial_controller)`,
   `superui_rules_hero(hero_level, declare_cost, revive_fee, spell_id)`, and
   `superui_rules_dungeon(map_id, final_boss_entry, buff_spell_id, loot_items)`.
   Runtime match state uses `superui_faction(team, honor_pool)`,
   `superui_heroes(guid, team, hero_level, dead, declared_at)`,
   `superui_zone_control(zone_id, controller)`, and
   `superui_dungeon_control(map_id, controller)`.
4. **MODULE RULE**: each mechanic = config table(s) + loader + core hooks
   gated on `RtsWorldState()` AND config presence. No rows = that module is
   inert even in RTS mode. A match can run any subset of the mechanics.
5. **Bend existing knobs before writing code**: at boot an RTS worldstate
   overrides the stock `Rate.XP.*` / `Rate.Drop.*` world rates from the
   ruleset (`sWorld.setConfig`), so scalar multipliers reuse all existing rate
   plumbing. New code only where semantics are new (e.g. guaranteed
   multi-roll boss loot is a loot-fill hook, not a rate).

Owner examples mapped: accelerated XP uses the relevant `rate.xp_*` rows rather
than a generic XP scalar. "Each Deadmines boss
drops ~10x guaranteed items from the pool" = loot-fill hook rolling the
EXISTING reference loot pool N times with guarantee semantics for creatures
matching a `superui_rules_dungeon` row. Bot caps use `bots.cap.alliance/horde`;
hero capacity uses `hero.slots_fixed` until territory replaces it with
`territory.zones_per_hero_slot`. Zone allotments are accepted standing supply,
derived from `superui_rules_zone` and never banked; their gameplay sinks remain
open design work.

### Part 4 — dungeons as strategic objectives (owner direction, 2026-08-12)

- Different dungeons grant different **faction-wide buffs**; dungeons are
  major map objectives.
- Clearing requires a REAL group run. **First faction to clear possesses the
  buff**; it holds until THE OTHER faction clears the dungeon (no decay). Only
  way to flip it.
- The controlling faction fights at the **entrance, RTS-style, from outside**
  — and is **locked out of entering while it controls** ("you can only be
  inside if you don't actively control the dungeon"). Enforcement point: the
  instance-entry / AreaTrigger check — one clean choke point.
- **One live run per faction at a time** per dungeon; at a neutral start both
  factions may race in parallel.
- Mechanics sketch: clear detection = final-boss kill (config row per dungeon:
  boss entry + buff spell id + loot rule) at the same `Unit::Kill` seam the
  hero XP uses; buff = aura on every faction member, applied on login + on
  control flip; control state persisted in `superui_dungeon_control` (part of
  the save — a match survives restart).
- **Run lock (owner decision 2026-08-12): one live run PER FACTION** per
  dungeon. With nobody controlling (match start), both factions can be inside
  simultaneously racing to the final boss; the controlling faction is locked
  out of ENTERING entirely. Edge to design later: control flips while the
  losing faction has a group mid-run — their run presumably continues (they
  can immediately re-take), but define it explicitly before building.
- **Ruleset tunability (owner decision 2026-08-12): BOOT-TIME AUTHORITATIVE.**
  Production changes travel with the owner-selected CharacterDatabase save and
  take effect on its clean boot. `.sui rts reload` exists only as a development
  diagnostic; it is not a supported production apply path or validation substitute.
- TBD: exact per-dungeon buffs, whether outside control confers anything
  beyond the lockout (guards, visibility).

### Part 5 — the RTS loop & economy (owner notes reduced, 2026-08-12)

**THE LOOP: Fight → Honor → Heroes. Hold → Capacity → Scale. Farm the
accelerated world → Levels & Gear → Strength. Converge on capitals → kill the
enemy's 4 commanders → win.**

Design philosophy (owner): reuse the existing WoW world and systems as the RTS
economy; player focus = expansion, army building, leveling, gearing, heroes,
fighting — NOT resource micromanagement. Macro only: it's about what you
control and what that gives.

Three axes, deliberately different KINDS of things:

| Axis | Kind | Source | Buys |
|---|---|---|---|
| Honor (Tier 0) | currency | ONLY combat vs the enemy faction (bots/NPCs/players; weighted — commanders/bosses worth more, values TBD) | hero declare / upgrade / equip / outfit (maybe ability upgrades, upkeep) |
| Territory | capacity | capturing hubs → zone control | hero SLOTS (every X zones = +1; e.g. one-per-class roster ≈ 14 zones), resource allotments, strategic position |
| Skins / Ores / Herbs | throughput | ZONE ALLOTMENTS — each controlled zone grants a config allotment ("2 herb + 1 ore"); NO node-farming, no worker micro | sinks TBD (see open questions) |
| The world | progression engine | 30–40x XP (target: L1→60 in ~10 h), currency/loot multipliers, 10x lootified dungeon bosses | levels, gear, money — army strength |

- **Capacity/currency split is load-bearing**: territory never generates
  Honor; Honor never buys slots. Combat = hero POWER, territory = hero COUNT.
- Bots: per-faction population cap is match config (500 / 800 / …); all bots
  start L1 and level through the real world; leveling is a meaningful part of
  the campaign, not instant max armies.
- Dungeons double as army-building events (lootified 10x boss drops through
  the existing Lootification system) AND strategic buff objectives (Part 4).
- **Hub capture**: reuse the Battleground capture-point mechanic — flag in
  each eligible hub; capture flips ownership, despawns enemy guards, spawns
  own faction flags + guards; hub becomes occupying faction's territory.
  Applies to non-capital hubs; capitals are their own category with their own
  rules (they house the win-condition commanders).
- **Accepted:** configured hub capture is the source of zone control; hub-less
  zones remain neutral. Territory supplies and hero capacity derive from that
  single controller state.
- **Accepted:** no hero demotion when territory shrinks. Falling below the slot
  threshold blocks new declarations but leaves fielded heroes intact.
- All values are CharacterDatabase ruleset data per Part 3:
  `superui_rules_zone`, `superui_rules_hub`, `superui_rules_hero`,
  `superui_rules_dungeon`, and scalar keys such as `honor.weight.*`,
  `hero.slots_fixed`, and `territory.zones_per_hero_slot`.

**Decided 2026-08-12 (owner):**
- **Allotments = STANDING SUPPLY**: held zones set the faction's live
  throughput; lose the zone, lose the supply instantly. No banking, no
  stockpile UX, no inflation over a long match.
- **Bot death = TIMED RESPAWN, the vanilla way**: bots die and respawn like
  any player (corpse/spirit-healer flow), with the AI piloting that flow
  correctly; scale/modify the existing mechanic rather than inventing
  attrition. The planned faction Honor pool is separate weighted accrual at the
  `Unit::Kill` seam; vanilla honor remains only where human involvement requires it.

- **Hero cap = AoE housing semantics (owner, 2026-08-12)**: territory OPENS
  cap, never kills what exists. Dropping below the threshold leaves fielded
  heroes untouched — you're over cap and simply cannot DECLARE a new hero
  until back above it. One gate at declaration time
  (`fielded < floor(zones / X)`), no ongoing enforcement, no demotion rule.
  Pressure lands on REPLACEMENT: a shrinking faction keeps its veterans but
  cannot rebuild its command without retaking ground.

**Open design questions (the current agenda):**
1. Resource SINKS: what do skins/ores/herbs supply-gate? (Input suggestion:
   map onto existing professions — skins→leatherworking, ores→smithing,
   herbs→alchemy; standing supply gates the faction's crafting/consumable
   throughput.)
2. Capital-city rules, the four faction commanders, and victory/defeat behavior;
   these are not assigned to R1–R5 and are not built.
3. Respawn balance: timer scaling and whether death adds a durability or other
   cost. R3 already accepts nearest controlled-zone graveyard with vanilla fallback.
4. Exact Honor weights, hero declare/revive costs, allotments, and slot ratio —
   balance data, later. Hero death itself is accepted: dead state persists, the
   slot remains occupied, and revival costs faction Honor.

## The three codebases (all three matter — none is optional)

| Piece | Where | Role |
|---|---|---|
| Client | this repo (`MSUIClient/GameLoop/Scene/GameLoop.Control.cs`, `MSUIClient/GameLoop/Hud/GameLoop.BotBars.cs`, `MSUIClient/GameLoop/Hud/GameLoop.PartyFrames.cs`, `MSUIClient/GameLoop/Hud/GameLoop.Portraits.cs`) | all UI/UX, SUI wire opcodes 0x33C–0x349 |
| Server C++ | `wowvmangos@192.168.0.2:~/vmangos`, branch `development` (no feature branches — owner rule: work directly on it) | possession, orders, follow/formation, streaming eye |
| Brain C# | `repos/MangosSuperUI` (deployed at `/opt/mangossuperui` on the box) | fleet goals; STANDS DOWN whenever `pparty=1` |

Server access: `ssh -i ~/.ssh/id_ed25519_msui_vmangos_travel_20260731
wowvmangos@192.168.0.2` — on THIS machine (2026-08-12) the repo
`MSUIClient/local-credentials/vmangos_ed25519` path does NOT exist and the
travel key is the working one; on the desktop the repo key may still apply.
Build: `cd ~/vmangos/build && cmake --build . -j$(nproc)`. Codex stops after a
successful build. Installation, deployment, and all runtime control belong only
to Nico. `mangosd` is managed by `mangosd.service`, which owns its detached
screen session; never launch a second screen manually. Server logs:
`~/vmangos/run/bin/Server.log` (+ Movement.log etc. beside it); grep `[SUI]`,
`[AIBOT-DOCTRINE]`, `[SUI-FOLLOW]`, `[cutaway]` (client console) for this feature.

## ⚠ HARD RULE: client and server deploy TOGETHER on wire changes

Today's "Ctrl+F logs me out" bug was exactly this: the client sent the new
`CMSG_SUI_CAM` (835/0x343) to a server built before that opcode existed → the
unknown-opcode path **kicks the session**. If you add/repurpose opcodes, the
owner must install and restart the matching server before the client is tested.

**Still true as of 2026-08-10 22:0x** — verified on the box: `mangosd` has been
running since **09:37**, but `6ed7716a6` (CMSG_SUI_CAM) landed at **19:32** and
`build/src/mangosd/mangosd` (20:23, matching HEAD `33e15c1f6`) was never
installed. The live binary's `NUM_MSG_TYPES` is 835, so opcode 835 trips
`IsDefinitelyBogusOpcode` and the socket closes — the free view sends its first
cam heartbeat within a frame of entering, hence "Ctrl+F boots me". **The
owner-only install and service restart are the entire fix; nothing is wrong in
the client here. Agents stop after the build.**

## SUI order codes (CMSG_SUI_ORDER)

| Code | Meaning | Notes |
|---|---|---|
| 0 | move | clears any waypoint chain |
| 1 | attack | targetGuid |
| 2 | stop / hold | clears chain + patrol |
| 3 | queue waypoint | **Shift**+RightClick chain; arrival chains next leg (in-callback rule: `MoveToDestination(..., false)`) |
| 4 | patrol | converts chain to a loop (arrival re-queues popped point) |
| 5 | follow | injects `SET_ESCORT` (legacy; superseded by links for the portrait UX) |
| 6 | link/unlink | `x >= 0.5` links; unlink → `m_suiUnlinked`, `DoPartyFollow` early-returns |
| 7 | auto-group | nonempty explicit same-faction bot list → one party up to 5, otherwise raid chunks of 40 |

Current law: an empty subjects list retains the real-party/own-character
meaning. A nonempty explicit list may additionally address server-validated
same-faction AiBots while the requester has a live Free View eye; follow/link
remain real-group-only. A possessed bot is orderable only by its own possessor
from Free View, and the **unattended own character obeys orders too**. See the
later canonical contract in `docs/systems/SYSTEM_CRPG_CONTROL_GROUPS.md`.

## Owner decisions (binding)

- **Free view: the personal party is NEVER autonomous** unless a future explicit
  command makes it so. Enforced by `pparty=1` for the enrolled real character
  (AiBotAIBridge.cpp STATE else-branch) — the brain holds Idle.
- **Clicking a party toon in the free view IS taking control of it** (same as
  Ctrl+Tab), bars live and castable. Not read-only inspection.
- **…and it does NOT leave the free view** (2026-08-10, revised). Possession is a
  CONTROL decision, the free view is a CAMERA decision, and they are independent:
  clicking a toon from the sky hands you its bars/bags/spells while the camera
  stays up. **Ctrl+F is the only thing that lands you.** Implemented as
  `_freeView`, a field separate from `ControlState`; the single enforcement point
  is `SeatControllerOnControlled`, which no-ops on the camera while `_freeView` is
  set, so every possess/release path inherits the rule.
- **Shift, not Ctrl, chains waypoints.** Ctrl is the control-chord modifier
  (Ctrl+F, Ctrl+Tab), so entering the free view with Ctrl still down turned the
  first right-click into a waypoint instead of a move. Shift is also the universal
  RTS queue-order binding.
- **Chain links are Divinity semantics**: linked members follow *whoever is being
  driven*; an unlinked member stands its ground. Not "x follows y".
- **A real character must NEVER be adoptable by the bot fleet** (see incident).

## Landed 2026-08-10 — server (committed on `development`; deployed + verified 08-11)

`6ef0ba6ae` FindEscortBoss possessed-first pre-pass (fixes party-follows-body)
`3059145e8` free-view pparty hold + orders reach unattended own char
`d91c9b2d2` **theft walls** (see incident below)
`6d26b5ecf` ORDER_MOVE_QUEUE waypoint chains
`6ed7716a6` CMSG_SUI_CAM + freecam streaming eye (World Trigger 15384 active-object
            summon rides the player Camera; `s_freecamEyes` in SuiPossess.cpp;
            torn down on possess/release/logout)
`5a17f83c9` ORDER_PATROL loop
`035562171` ORDER_FOLLOW via SET_ESCORT
`33e15c1f6` ORDER_LINK + DoPartyFollow gate

## Landed 2026-08-10 — client (committed as `0169804` "CRPG & RTS Work + Painterly")

- Free-view marquee (window `FreeSelectMode`: left = select, right = look),
  depth-tested ground-decal selection rings + move markers (procedural textures in
  `SpellEffectMeshRenderer`), `GroupSelectedGuids` highlight, waypoint-chain dots +
  dashed route, cam heartbeat (>5 yd or 2 s → `SuiCam`).
- Click-toon-to-possess from free view (`RequestPossess` accepts FreeCam origin;
  denial/watchdog fall back INTO the free view).
- **Layered bot bars** (`MSUIClient/GameLoop/Hud/GameLoop.BotBars.cs` + `botbars.json` repo root): generated
  baseline (active spells, best rank, wire slots 0-11 then 48-59) ← ClassSlots ←
  BotSlots (explicit 0 masks). Banner toggle picks save layer while possessing.
  `BotSpells`/`BotClasses` cache feeds free-view inspection; `BarsGuid`/`BarsReadOnly`
  swap bars for marquee-single selection (read-only, `UseAction` gated).
- **RTS strips** (`Settings.Controls.RtsCommands`, checkbox in Settings→Controls):
  role cycle T/H/D (client-persisted `BotRoles` — rotations hook is FUTURE WORK),
  Hold, Patrol, link chip.
- **Chain links UI**: permanent bead-chain down the party frames
  (`DrawPartyChainLinks`), broken stub when unlinked; drag portrait onto another/
  player frame = link, drag away >60 px into the open = unlink; `BotLinks` map.
- **Real party portraits** (`UpdatePartyPortraits`, `MSUIClient/GameLoop/Hud/GameLoop.Portraits.cs`): per-member
  bake, appearance-hash invalidation, one bake/frame; TemporaryPortrait art is now only
  the out-of-range fallback. **Reworked after the first look (uncommitted):** the bake
  now goes through `TryBakeCreaturePortrait` — the same booth as the target frame
  (authored M2 portrait camera → bounds fallback → blank retry) with the own-character
  portrait's `player:race-gender` tuning — instead of a hand-rolled fov-30 camera; it
  styles-then-`UpdateCircularCopy` like every other bake and hands the party frame the
  round handle. **The party frame was also drawing the bake with flat-art UVs, i.e.
  upside down** (`Vector2.Zero/One` instead of `(0,1)/(1,0)`) — that was the "those
  aren't the bots" symptom.
- Fixes: player-frame name + overhead name follow possession identity; name tag no
  longer rides the freecam rig; plain-F fly toggle ignores Ctrl.

## Round 3 — client, uncommitted (2026-08-10, after the first live free-view session)

1. **Ctrl no longer double-duties.** Waypoint chaining moved to Shift+RightClick
   (`HandleFreeCamWorldClick`). Residual overlap to watch: Shift is also the fly-rig
   `Boost`, so chaining while flying moves the camera faster.
2. **RTS edge scroll** (`UpdateFreeCamEdgePan`): pointer within 14 px of a screen edge
   slides the rig camera-relative, speed scaled by altitude above ground
   (`FlySpeed × clamp(alt/12, 0.45, 3.0)`). Suppressed during a marquee drag (the
   rectangle is screen-anchored), while the right button holds camera look, and while
   ImGui owns the pointer — so the bottom UI strip stays usable.
3. **Free view survives possession** — see the owner decision above. Touches
   `RenderSelfGuid`, `BarsGuid`, both ACK branches, `ToggleFreeView` (now lands on the
   commanded toon with no server round trip), `SwitchControlTo`, the click router in
   `MSUIClient/GameLoop/Combat/GameLoop.Targeting.cs`, the nameplate anchor, and the RTS overlays; all keyed on
   `_freeView` instead of `ControlState.FreeCam`. `UpdateFreeCamSelection` re-asserts
   `Flying`/`_character.Enabled` every frame because `ApplyControlledCharacter`
   re-enables the first-person body on every possess. New `ResetSuiControl()` drops both
   modes on socket loss, where no forced-release ACK can ever arrive.
4. **Selection rings tuck behind the model** (`SpellEffectMeshRenderer`): `RenderGroundQuads`
   never enabled depth TESTING (it inherited whatever the previous pass left) and applied the
   full `GROUND_FX_DEPTH_BIAS` of -8192 units, which at RTS camera range pulls a decal several
   yards toward the eye — so the ring's far arc drew straight through the body standing in it.
   Depth test is now explicit (write still off) and rings use `UnitAwareDepthBias` (-64): enough
   to clear the coplanar terrain, not enough to beat a unit. Spell decals keep the coarse bias.
   **-64 is a tuned guess** — raise it if rings z-fight the ground at distance, lower it if the
   model still fails to occlude.
5. **Party frames follow the driven unit** (`PartyFrameMembers`): the possessed bot leaves
   the party list (it holds the player frame) and the abandoned own character joins it via
   a synthesised row. Before this the bot appeared twice and the real character vanished.
   `UpdatePartyPortraits` bakes the frame set rather than the wire roster. Strict no-op
   while `ControlledGuid == LocalPlayerGuid`, so UI-parity captures are unaffected.

## Round 4 — client, uncommitted (2026-08-10, second free-view session)

All four were consequences of commanding-from-the-sky, which round 3 made reachable:

1. **Spell FX came out of the camera, not the caster.** `SpellEffectUnitPose` served the
   first-person body's pose for `ControlledGuid`, and that pose is the CONTROLLER's — the fly
   rig, i.e. screen centre. Now falls through to the streamed pose while `_freeView`.
2. **Player frame showed the commanded bot's name over the old face.** Round 3 hid the
   first-person body with `_character.Enabled = false`, but `CharacterRenderer.Render`
   early-returns on `!Enabled` and the portrait booth calls that same method — so the bake
   silently froze. The body is now suppressed at its world-pass call site
   (`Program.cs`, gated on `_freeView`) and `Enabled` is left alone.
3. **Ordered units would not move.** Two distinct causes:
   - Releasing while staying in the free view sent mode 0. `SuiPossess::DoRelease` answers
     mode 0 with `DetachUnattendedAI` + `RemoveFreecamEye` — so the abandoned own character
     had no AI left to obey, and the streaming eye died. `RequestControlRelease` now sends
     mode 1 whenever `_freeView`, whatever the caller asked for.
   - The commanded bot itself is unorderable by design (`orderBot` bails on
     `ai->IsPossessed()` — possession makes the CLIENT its mover). It is no longer
     auto-selected on the click that commands it, and it is filtered out of any subject list
     with a real message instead of a false "move to".
4. **Waypoint route outlived the walk.** `UpdateRtsWaypointProgress` retires the leading leg
   once any ordered subject stands within 3.5 yd of it (horizontal only — stairs), mirroring
   the server's own arrival chaining, and drops a chain that has made no progress in 45 s.

## Round 5 — client, uncommitted (2026-08-10, third free-view session)

1. **The commanded toon cast without animating.** `ApplySpellStart` / `ApplySpellGo` /
   `ApplyChannelStart` / `PlayBodyAnimation` all sent the CONTROLLED unit's body animation to
   `_character`. In the free view that body is not drawn — the driven unit streams in like any
   other player and `CreatureRenderer` owns its skeleton — so the animation played on nothing.
   New `ControlledBodyIsStreamed` picks the renderer; the HUD side-effects (cast bar,
   cooldowns, server-result emits) still key on `ControlledGuid` as before.
2. **Ctrl+F would not leave the free view.** Round 3's `staysInFreeView` rule ("a solicited
   release inside the free view is a control change only") also caught the release that Ctrl+F
   sends to LEAVE — both arrive as reason 16, so the code cannot tell them apart. Only the
   client knows which it sent: new `_freeViewExitRequested`, set in `ToggleFreeView`'s FreeCam
   case and cleared on entry, on local exit, and on session reset.

### ATTACK orders are dead on arrival (open, server-side — **pre-existing, not CRPG**)

Proven from `Server.log`, 21 occurrences during the 2026-08-10 session:

```
[AIBOT-BRIDGE] Tesfff:     ATTACK_TARGET creature guid 79945 not found on map
[AIBOT-BRIDGE] Kaelrunner: ATTACK_TARGET creature guid 79945 not found on map
[AIBOT-BRIDGE] Earthadin:  ATTACK_TARGET creature guid 79945 not found on map
```

The wire, the subject list and `orderBot` are all fine — the order reaches every member.
`AiBotAI::BridgeHandleAttackTarget` then rebuilds the target as
`ObjectGuid(HIGHGUID_UNIT, uint32(guidLow))`, i.e. **counter only, entry dropped**. A vmangos
creature guid embeds its entry, so `Map::GetCreature` can never match and the handler logs and
returns. The comment there ("Try with full GUID construction / Search nearby creatures as
fallback") describes a fallback that was never written.

**This is not a CRPG bug** — the same handler serves brain-issued `ATTACK_TARGET`, so fleet
attack commands down this path have always failed the same way. The entry is lost at the JSON
hop (`SuiPossess` ORDER_ATTACK sends `targetGuid.GetCounter()`), so the fix is either to carry
the entry in the payload or to have `SuiPossess` set the attack target directly from the full
`targetGuid` it already holds.

### The abandoned character runs off, and the party chases IT (open, server-side)

Reported 2026-08-10: possess any bot and the real character bolts, with the remaining bots
following the runaway body instead of the possessed toon.

Verified from source + the live `Server.log`:

- Possession does attach the unattended AI (`TryBegin` → `AttachUnattendedAI`); the matching
  `[SUI] Tesfff back under manual control` on every reason-16 release proves it was attached.
- **That AI has no brain.** The theft wall `d91c9b2d2` gates `UpdateBridgeTick` on
  `m_ownedDummyEntry`, so an AI on a real character never opens the bridge socket — by
  design, and it must stay that way. Its commit message assumes "doctrine plus
  CMSG_SUI_ORDER injections are the whole control surface".
- **The non-autonomy decree is not enforced anywhere that AI can see it.** `pparty=1` is
  computed inside the STATE producer in `AiBotAIBridge.cpp` — a message *to the brain*. For a
  real character that message is never sent and the brain would ignore it anyway. So nothing
  holds the body; it runs whatever the C++ doctrine picks by default. **This is the likely
  root cause of "booking it" and it needs a local hold, not an echo.**
- `FindEscortBoss`'s possessed-first pre-pass reads correctly and IS in the deployed build
  (`6ef0ba6ae` 09:02 < build 20:23), so "other bots follow the body" is NOT explained by
  static reading. Either it is downstream of the above (the body bolts, boss resolution
  transiently falls through to the only real session) or the pre-pass is not on the path that
  actually drives fleet-bot follow. **Needs a live diagnostic, not more code reading.**

## Round 16 — camera-through-walls (PRE-EXISTING, not CRPG; owner-reported 2026-08-11)

Pressing the orbit camera against a wall in NORMAL play let you see through it.
`ResolveCameraCollision` pulls the boom in correctly (full collision-world ray +
0.35 clearance) but then clamped `allowed` up to `Camera.MinDistance` (1.5 yd) —
so any wall closer than that behind the character's head simply won: the camera
parked inside the wall. Vanilla's answer is the first-person dip, now
implemented: the collision pass may collapse the boom to `FirstPersonDip`
(0.25 yd; the zoom wheel still stops at MinDistance), and the first-person body
render is suppressed below `FirstPersonBodyHide` (0.6 yd) so the camera doesn't
sit inside the model's head. Watch-for: a diagonal graze past a thin pillar edge
can still slip the single centre ray — add near-plane corner rays if seen live.

## Round 15 — cutaway REJECTED; free camera is a floating body instead (2026-08-11)

Owner saw the Round 14 cutaway live: the open-face dollhouse look is **"no good"**
— `FreeViewCutaway` now defaults OFF (kept as an experiment toggle; code intact
behind it). The clarified intent: in the free view the camera should behave like
a **floating body** — walls and ceilings stop it, so while the party is in room A
you naturally see room A and nothing impossible; to see another room you fly
through the door.

Implemented (client only): `CharacterController.FlyCollide` +
`FlyMove(delta)` — full-3D swept move with slide against the collision world
(like MoveHorizontal minus walkable-slope pass-through and step-up). WASD flight
AND the edge pan route through it; plain F fly stays a ghost. Gated by
`Settings.Controls.FreeViewCameraCollision` (default ON, checkbox in Settings →
Controls → CRPG/RTS).

Watch-fors: a rig that somehow starts INSIDE solid geometry may wedge (untick
the checkbox to escape); single-ray + radius sweep can slip through thin gaps
at extreme speed; the ORBITING camera around the rig has its own collision
(ResolveCameraCollision) which was not touched.

## Round 14 — free-view floor clamp + Divinity cutaway v1 (2026-08-11, client only; cutaway REJECTED — see Round 15)

1. **Fly rig can no longer sink beneath the map** in the free view:
   `CharacterController.FlyFloorClearance` (null = classic unclamped fly; the
   free view sets 2 yd, plain F fly stays a go-anywhere debug tool). Terrain
   only — WMO floors (city streets, bridges) are not sampled.
2. **Divinity-style building cutaway, v1** (owner picked trigger (a): commanding
   an indoor toon; hard hide, fade is future polish). Rides the PLAN_10 portal
   machinery: `WmoRenderer.SetCutawaySubject(worldPos)` resolves the commanded
   toon's cell exactly like the camera's (refusing pure-EXTERIOR 0x08 seed cells
   — "at the gate" must not cut a city), and for that ONE instance the
   authoritative ReachableGroups gate is fed by `ComputeCutawayGroups`: a
   view-independent portal BFS from the toon's cell that never traverses INTO a
   0x08 group. Shell + roof stay unreached → cull; the room and its
   portal-connected interior stay. **Deliberately removable**: one Settings
   checkbox (Controls → "Cut buildings away in the free view",
   `Settings.Controls.FreeViewCutaway`), and in code three marked blocks
   (property + seed resolution + flood override in WmoRenderer,
   `FreeViewCutawaySubject()` in `MSUIClient/GameLoop/Scene/GameLoop.Control.cs`, one feed line in
   Program.cs before UpdateCameraCell).
   Watch-fors on first live test: WMO-owned doodads (furniture) may still draw
   inside hidden groups; multi-storey buildings show every floor portal-reachable
   from the toon's (a "floors above" cut is a future refinement); the cut is
   per-instance, so neighbouring buildings are untouched.

## Round 13 — bot bags + stuck cast animation (2026-08-11, second play report)

**Bags (FOUR stacked causes, all fixed — the fourth found after the third's
deploy still showed empty slots):**
0. Client, the big one: the snapshot's synthetic `WorldEntity`s never set the
   plain `Entry` FIELD (only OBJECT_ENTRY inside `Fields`), and Entry is what
   every template consumer keys on — `Require(0, …)` is a silent no-op, so
   names/icons/tooltips/bag portraits all read "no item" while stack counts
   (field-driven) rendered fine, and nothing ever logged a failure. Owner
   verified sizes fixed but items invisible; `Entry = entry` closed it.
1. Client: `ApplySuiSnapshot` never set `CONTAINER_NUM_SLOTS` on the synthetic
   container entities, and the bag UI both sizes windows and enumerates contents
   from it (`Math.Min(numSlots, 36)` → 0 iterations) — the items were filed into
   CONTAINER_SLOT fields nobody ever read. Now set from the snapshot's bagSlots
   byte.
2. Server: vmangos' anti-datamining gate (`Item.PreventDataMining = 1` +
   `ItemPrototype::Discovered`) answers template queries for undiscovered entries
   with the not-found tombstone. Fabricated bots' gear can be undiscovered after a
   restart, so every name/icon/tooltip/bag-size for bot items came back refused.
   `AppendSnapshotItem` now marks each snapshot item's prototype Discovered.
3. Client: `Items.Apply` caches a tombstone as a permanent null template
   (Require never retries) — one refusal is forever. Kept the cache (query-storm
   guard: DiscoverItemTemplates re-Requires every frame) but it now logs
   `[items] template query for entry N answered EMPTY` so a refusal is visible.

**Stuck cast animation in the free view (client, fixed):** every spell-hold
CANCEL path (`ApplySpellFailure`, escape cancel, auto-repeat cancel, channel
stop) called `_character?.CancelSpellVisual()` — the first-person body — even
when the controlled unit is streamed (`ControlledBodyIsStreamed`). Start/Go
branched correctly (Round 5) but the cancels didn't, so an interrupted cast left
the streamed body looping its cast-state until the next cast overwrote the hold.
New `CancelControlledSpellVisual()` helper picks the renderer; all four sites
converted.

**Facing (Round 12) is deployed** (binary md5-verified running since 10:29) but
owner reports "not sure it's properly fixed" — needs a specific failing scenario:
cast-time facing errors should be gone; melee only faces at swing START (drift
mid-fight relies on the bot's own combat AI); mid-cast target movement is not
re-faced (vanilla behaviour).

## Round 12 — post-fix polish (2026-08-11, after Round 11 verified in play)

Owner confirmed movement is fixed. Four follow-ups from the first real play session:

1. **Cape length changed with the renderer** (client, fixed):
   `CharacterEquipment.ApplyGeosets` hard-coded cloak geoset variant 2 (the long
   1502 cloth) for any textured cloak; the streamed body correctly uses
   `1501 + GeosetGroup[0]` (CharacterGeosets.cs law). The first-person body now
   applies the same law — driving a toon no longer promotes its cape to long.
2. **Character sheet identity + stats** (client + server, fixed — WIRE ADDITION):
   the sheet header always printed the SESSION character's name (`_net.PlayerName`);
   now resolves the driven unit. Stats/armor/AP/damage are owner-only UNIT_FIELDs
   the vanilla wire never streams for another player, so the bot sheet was zeros:
   `SMSG_SUI_SNAPSHOT` now appends a raw stat block (5 stats, 7 resistances,
   AP+mods, RAP+mods, 3 attack times, 6 damage floats) and the client injects it
   verbatim into the bot's fields. Optional trailing bytes — old/new client+server
   mixes degrade gracefully (no new opcode, no kick risk).
3. **"My character goes into walk mode"** (client, mitigated): the Slash walk
   toggle (`_walkToggled`) survived control hand-offs invisibly; every hand-off
   now seats you running. NOTE: holding Shift while grounded is also walk by
   design (`Walking = _walkToggled || shift`), and Shift is in hand during
   control chords (reverse cycle, fly boost) — if walk still appears, check
   whether Shift was held; may deserve its own rethink.
4. **Free-view casts fail facing** (server, fixed): with the camera parked no
   orientation packets ever turn the acting unit, so frontal-arc checks failed
   and the commanded toon could not fight. `HandleCastSpellOpcode` and
   `HandleAttackSwingOpcode` now face the actor at the target when
   `SuiPossess::IsFreeViewUp(_player)` (new query: session player holds a live
   freecam eye — covers both the commanded bot AND the unattended own character
   acting through the bars). The facing spline this launches is safe because
   Round 11's arrival auto-clear covers bot-session players.

**Bot bags** were still broken at the end of this round — diagnosed and fixed in
Round 13 (four stacked causes).

## Round 11 — SplineDonePending: the REAL "I drive but nothing happens" (server, 2026-08-11)

Round 10 was necessary but not sufficient. The definitive signature (owner-reported):
the client predicts movement freely, but Ctrl+F reveals the body never left the
pickup spot — the server discarded every movement packet.

Root cause: `MoveSplineInit::Launch` sets `SetSplineDonePending(true)` for EVERY
spline on a Player, and the flag clears only when the controlling client sends
`CMSG_MOVE_SPLINE_DONE` — the arrival auto-clear in `Unit::UpdateSplineMovement`
explicitly skips Players. Fleet bots are Players with fake sessions that never send
it, so **any bot the AI has ever spline-walked carries the flag for life**. It costs
the fleet nothing (bots send no client movement) — it only bites when a human takes
the body: `HandleMovementOpcodes` drops every packet while the flag is set. Same for
the own character after the unattended AI walked it during the free view. This is why
possession worked right after the 23:48 restart (bots un-splined since boot; the brain
holds pparty bots) and failed for freshly created bots minutes later (spline-quested
through onboarding at creation) and for everything the morning after.

Fixed on the box (`development`, uncommitted; DEPLOYED 2026-08-11 morning and
owner-verified — "seems fixed"):

1. `Unit.cpp UpdateSplineMovement` — arrival auto-clear now also applies to Players
   whose own session is a bot session (a fake client can never confirm).
2. `SuiPossess.cpp TryBegin` — `SetSplineDonePending(false)` AFTER the stop sequence
   (StopMoving itself launches a stop spline that re-sets the flag), plus
   `SuiAbandonJourney()` (new AiBotAI helper: task + stored path + RTS waypoints)
   so the brain-era journey dies when a human takes the body.
3. `SuiPossess.cpp DetachUnattendedAI` — clear the flag on the own character when
   the human gets their body back (the AI era splined it).
4. `SuiPossess.cpp HandleCam(active=false)` — landing while commanding: remove the
   eye FIRST (kills the commanded waiver), then stop the controlled bot and clear
   its flag.
5. `AiBotAIMain.cpp MovementInform(TASK_DEST)` — no journey chaining while
   `m_possessed && !IsCommandedFromFreeView` (Finalize fires this callback from
   TryBegin's own stop; the next chunk would re-open a movespline that outranks the
   human's packets).
6. `AiBotAIMain.cpp RefreshDoctrine` — entering PlayerParty from Solo calls
   `SuiAbandonJourney()`: a Solo-era errand must not keep walking a bot that just
   joined a human's party (live TASK_MOVE_TO also gates DoPartyFollow — the
   "new member marches away instead of forming up" bug).

Client TODO (v1.1): implement `CMSG_MOVE_SPLINE_DONE`. Until then any server spline
on the own character OUTSIDE the SUI flows (charge-type effects) still wedges the
flag with nothing to clear it.

Also deployed in the same binary: the `[SUI-FOLLOW]` per-bot follow diagnostic
(task/unlinked/commanded/boss/dist every 5 s in PlayerParty), which the previous
session had built at 2026-08-11 00:16 but never installed.

## Round 10 — possession must STOP the bot first (server + client)

The "I drive but nothing happens" bug. `HandleMovementOpcodes` drops **every** client movement
packet while the mover has an unfinalized movespline:

```cpp
if (!pMover->movespline->Finalized())
    return;
```

`TryBegin` granted possession without stopping the bot. A bot is almost always mid-follow when
you take it, so its spline kept running: the client's input was discarded wholesale, the bot
walked on to wherever the AI had already sent it, and the human drove a body that ignored them.
Every reported symptom follows — no attacks (you are not where you think), nobody follows you,
and dropping to the free view reveals the character back with the party, which is simply where
the spline finished. `TryBegin` now does `StopMoving()` + `MotionMaster::Clear/MoveIdle` at the
grant, mirroring what `DetachUnattendedAI` already does on the way out.

**Client, same round:** `SyncDrivenEntityToController()` now refuses to publish while the
movement stream is parked or flying. Round 9 published unconditionally, which painted the
client's predicted position over the server's real one — the selection ring tracked you
perfectly while the character had not moved at all, hiding exactly this desync. If the stream
is silent the server's position is the truth and the client must show it.

## Round 9 — the driven unit's entity is published EVERY FRAME (client only)

`SyncDrivenEntityToController()` moved from the control hand-offs into the frame loop, right
after `_controller.Update`. Hand-off-only was treating a symptom: the driven unit is
client-authoritative, so its entity is the one thing the server never updates for us, and it
stays frozen at the pickup spot for the whole time you drive. Everything reading the ENTITY
rather than the controller renders it there — the visible one is `DrawSelectionRing`
(`MSUIClient/GameLoop/Combat/GameLoop.Targeting.cs`, `target.Position`), which leaves the blue player selection circle
sitting on the ground behind you as you run off. The teleport-on-hand-off was the same bug
caught at one instant; this is the general form.

## Round 8 — the free view has to be a SERVER fact (uncommitted, compiled, NOT deployed)

⚠ **WIRE CHANGE — client and server deploy together** (see the hard rule above).

Round 7's waiver keys on "possessor holds a freecam eye". But **landing was purely client-side**:
Ctrl+F while commanding just set `_freeView = false` locally and re-seated the camera. The
server never heard, so the eye stayed up, `IsCommandedFromFreeView` stayed true, and the bot's
own AI kept driving it. The human drove locally at the same time — movement was prediction
only, swings never landed, nobody followed, and the release ACK snapped the character back to
wherever the server actually had it. Round 7 traded one broken direction for the other.

- `CMSG_SUI_CAM` gains a trailing **ACTIVE** byte. Read as optional (`rpos() < size()`), so a
  sender predating it still reads as "up", which is what it meant.
- Client: every `_freeView` transition now goes through `SetFreeView(bool)`, which sends one
  `active: 0` cam packet on the way down and forces an immediate heartbeat on the way up. Five
  assignment sites collapsed into it — the bug was possible because they were scattered.
- `HandleCam(..., bool active)`: `!active` → `RemoveFreecamEye` and return.
- `RemoveFreecamEye` no longer blind-`ResetView()`s. Landing while STILL possessing must hand
  the camera back to the **bot** — a reset would point it at the abandoned body and stop
  streaming the world around the character being driven.

## Round 7 — "commanded == driven", the rest of it (uncommitted, compiled, NOT deployed)

Round 6 relaxed `orderBot`'s `IsPossessed()` bail and called the job done. It was not: that is
the OUTERMOST of **three** gates, and the order still died one level below it. All three are
now waived by one predicate, `SuiPossess::IsCommandedFromFreeView(bot)` — possessed AND the
possessor holds a live freecam eye:

1. `SuiPossess::orderBot` — the order reaches the bot (round 6).
2. **`AiBotAI::BridgeProcessLine`** (AiBotAIBridge.cpp) — dropped EVERY command but PING for a
   possessed bot, so the `MOVE_TO` was thrown away with a `POSSESSED_DROP` event. **This is
   where the move actually died.**
3. **`AiBotAI::UpdateAI`** (AiBotAIMain.cpp) — returned after `UpdateBridgeTick()` for a
   possessed bot, so even a queued task had no tick to walk it.
4. `DoPartyFollow` now early-returns for a commanded bot, so it holds the ground you sent it to
   instead of formation-walking back to the party the moment the leg finishes.

The resulting doctrine is PlayerParty (the possessor's own body resolves as boss): no grind, no
patrol, no wander, combat assist live, task machinery reachable. **Design note / risk:** the
server now drives a bot whose client is nominally its mover (`SetMover` + `SetClientControl`
are still in force). That is safe only because the free view parks the client's movement
stream — the controller is the camera and sends nothing. Needs live verification; if it
desyncs or trips anticheat, the fix is to drop client control for the duration of a free-view
command rather than to re-close these gates.

Client, same round: `SyncLocalPlayerEntityToController()` publishes the controller position into
the local player's ENTITY at both hand-off points (free-view entry, possess grant). Local
movement is client-authoritative, so the own character's entity is only the last SERVER
snapshot — often the spawn point. It never showed while the controller drew the body; the
moment `RenderSelfGuid` stops covering you, your body draws from that stale entity and appears
back where you entered the world until the unattended AI's first move corrects it.

## Round 6 — server, UNCOMMITTED on `development`, compiled clean, NOT deployed

Four fixes, `git diff` on the box to review (61 insertions across 4 files):

1. **`AiBotDoctrine.cpp` + `AiBotAIMain.h` — the runaway body.** Root cause found in the live
   log: `[AIBOT-DOCTRINE] Tesfff: (none) -> Solo` fires the moment the human enters the free
   view. With nobody possessed, `FindPartyBoss` finds no OTHER real member in a group of bots,
   so the ladder fell through to **Solo** — grind, patrol, wander. The non-autonomy decree was
   never enforceable there: `pparty=1` is an echo *to the brain*, and the theft wall keeps this
   AI off the bridge, so the echo has no reader. `ResolveDoctrine` now returns `PlayerParty`
   for any AI with `IsUnattendedRealCharacter()`. That invents no behaviour: formation on
   whoever is driven when someone is, `DoPartyFollow`'s no-boss early return (stand still) when
   nobody is, combat assist either way, and the branch stands the whole task machinery down.
   *This is also the likely fix for "the other bots follow the body" — they were escorting a
   legitimately-resolved boss that had gone grinding.*
2. **`SuiPossess.cpp` `HandleCam` — the streaming eye.** Now `EnsureFreecamEye` rather than
   only teleporting an existing one, so the eye tracks the client's real camera mode instead of
   its possession state. Client companion: `SeatControllerOnControlled` zeroes
   `_freecamCamSentAt` in the free view, forcing the heartbeat out next frame instead of
   waiting up to 2 s.
3. **`SuiPossess.cpp` `orderBot` — the commanded toon is orderable from the sky.** The
   `IsPossessed()` bail now applies only when the possessor has no freecam eye. The conflict
   it guards against (server MOVE_TO fighting the client's movement stream) cannot arise in
   the free view: the client's controller is the camera and its stream is parked.
   **→ the client-side subject filter in `HandleFreeCamWorldClick` has been deleted** (it was
   still refusing the order after the server started accepting it, and it was the thing putting
   "You are commanding X — Ctrl+F to land and drive it" on screen). `BarsGuid` also stops the
   marquee stealing the commanded toon's live bars: **commanding a character from the sky gives
   you the same character you would have driving it directly** — that is the rule, and any gate
   that makes it a spectator is a bug.
4. **`SuiPossess.cpp` + `AiBotAIBridge.cpp` — ATTACK_TARGET.** ORDER_ATTACK now sends
   `entry` alongside `guid`, and `BridgeHandleAttackTarget` rebuilds
   `ObjectGuid(HIGHGUID_UNIT, entry, counter)` — the form every correct caller in
   `AiBotAIMain` already uses. The entry-less path is kept for producers that omit it.

**Still broken, same bug, NOT touched** (out of scope, brain paths, no report against them):
`BridgeHandleInteractNpc` (AiBotAIBridge.cpp:1678) and the lookup at :2559 both use the same
entry-less `ObjectGuid(HIGHGUID_UNIT, counter)` and cannot resolve a creature either.

### Server-side, NOT done — needs a decision

- **The streaming eye dies when you command a toon from the sky.**
  `SuiPossess::HandleRequest` calls `RemoveFreecamEye(requester)` ("possess overrides the
  free-camera view"), which was true before round 3 and is not any more. `HandleCam` only
  teleports an eye that already exists (`if (Creature* eye = FreecamEyeOf(player))`), so it
  cannot bring it back. Suggested fix: have `HandleCam` call `EnsureFreecamEye(player)` —
  the client only sends `CMSG_SUI_CAM` while the free view is up, so the eye then tracks the
  client's real camera mode. Until this lands, flying far while commanding a toon will fly
  into unstreamed world.
- **Ordering the toon you are commanding** is refused by design (`orderBot` bails on
  `ai->IsPossessed()`). Owner-confirmed symptom: with a group marquee-selected, everyone moves
  EXCEPT the commanded one — and if the commanded one is the real character (unattended, not
  possessed) the whole group moves. If this should work, the server has to allow orders to a
  possessed bot while its possessor is in the free view. **When that lands, delete the
  matching client-side filter in `HandleFreeCamWorldClick`** (it drops `_controlTargetGuid`
  from the subject list so the chat line stops promising a move the server threw away).
- **`BridgeHandleAttackTarget` guid reconstruction** — see the section above.

## Verification status (2026-08-11 update — this section was "NOT yet verified")

The 2026-08-11 joint deploy happened; core loop is owner-verified (see the top
summary). The "uncommitted / NOT deployed" stamps on Rounds 6–8 below are
HISTORICAL — that code is compiled into the running server binary. Not yet
individually exercised in play: waypoint chains + patrol, links, party
portraits end-to-end, bot bars end-to-end.

## Known gaps / next steps

1. **Roles → rotations**: `BotRoles` is client-side only; wire into the brain/server
   rotation selection.
2. **Free-view bars for never-possessed bots**: spell cache only fills on first
   possession. Server could stream spell lists with the roster instead.
3. **Multi-human control authority** (who may possess whose bots) — undesigned.
4. **Patrol UX**: route is invisible after issuing; consider persisting/showing it.
5. **PlayerRenderer** exists but is unwired; players draw via CreatureRenderer
   (highlight lives there).
6. Streaming eye edge cases: freecam + teleport (no force-release when not
   possessing), cross-map camera not handled.
7. The party-portrait rework above is uncommitted — commit after live verification.

## Incident log (2026-08-10): character theft — DO NOT reintroduce

The enrolled unattended real character HELLO'ed the brain like a fleet bot; the
brain auto-registered it into `characters.playerbot`; the next mangosd restart
spawned it as a fabricated bot on a synthetic account (`SaveToDB` stamped the
account over NICO's) — the character vanished from the owner's list and quested
as a bot. Walls now in place: real chars never bridge-connect (`UpdateBridgeTick`
gates on `m_ownedDummyEntry`), the spawn path refuses guids whose account exists
in realmd (`OnSessionLoaded`), and the brain refuses to register real-account
characters (MangosSuperUI `main` `ac3a63c` — needs a brain deploy eventually).
Theft detector query: `SELECT ... FROM characters WHERE account NOT IN (SELECT id
FROM realmd.account)` — fleet bots are all on synthetic 10xxx accounts, real
characters must never be.
