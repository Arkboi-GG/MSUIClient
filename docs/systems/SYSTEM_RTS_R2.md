# RTS R2 Honor, Heroes, and Faction Control

**Document type:** canonical implementation record and operating contract.

**Repositories covered:**

1. `MSUIClient` -- the native game client and Commander user interface.
2. `VMaNGOS / SuperUI-Core` -- the authoritative game simulation and custom
   session protocol.
3. `MangosSuperUI` -- offline World State construction, saved configuration,
   and owner-facing load/resume controls.

**Status:** source-integrated and build-verified across all three repositories on
2026-08-14. The authoritative VMaNGOS `development` checkout received the exact
27 reviewed semantic files, passed the hash/diff integrity checks, and completed
its Release build with scripts through `[100%] Built target mangosd`. Nothing in
this record means that build artifact or the web application was installed, a
live database was changed, a World State was loaded, or a process was restarted.
R2 has not yet received owner-operated live gameplay validation. A section is
not evidence that deployment or live validation passed unless its verification
table explicitly records that result.

**Authority:** this is the detailed authority for what R2 changes in the three
repositories. `RTS_WORLDSTATE_PLAN.md` remains the phased product plan and
`CRPG_RTS_WIP.md` remains the chronological decision record. Neither should be
used as a substitute for the exact implementation and isolation laws here.

---

## 1. Executive summary

R2 adds the first true match-economy loop on top of the R1 boot-loaded RTS
worldstate foundation:

1. combat against the opposing faction awards a shared faction Honor pool;
2. an eligible friendly AiBot can be declared as a persistent hero by spending
   that Honor;
3. a living hero can be upgraded through five configured levels;
4. a dead hero keeps its level and slot and must be revived for a configured
   Honor fee;
5. hero level changes the bot's configured scale and damage contribution;
6. Commander can discover and select active bots across the player's faction,
   rather than treating the current party as the whole army;
7. a single eligible faction bot can enter the existing SuperUI possession
   pipeline and receive full MMO-style direct control.

R2 deliberately does **not** implement territory, standing supplies, dungeon
control, automated army creation, autonomous strategic planning, capital
commanders, or a victory condition. Those remain later phases.

The most important engineering property is negative: the same server binary
continues to run an ordinary MMO world without activating any R2 mechanic. RTS
is a property of the save loaded before boot, not a second executable and not a
runtime switch.

---

## 2. The three boundaries that must not be confused

### 2.1 Tier 1: generic CRPG/RTS controls

Tier 1 is the existing possession, free-view camera, party selection, movement
orders, attack orders, patrols, links, waypoints, Commander map, and population
census. These controls can exist in an MMO world. Their original authority is
party membership.

R2 does not replace Tier 1. It adds a true-RTS-only faction force catalogue and
an additional authorization path for direct control. The legacy party path is
preserved for ordinary worlds.

### 2.2 R1: worldstate foundation

R1 owns the boot mode, scalar configuration, rate overrides, faction bot
admission caps, state request/reply allocation, and empty persistence scaffolds.
R2 consumes that foundation. R2 must not manufacture an RTS save at server boot;
MangosSuperUI prepares the selected save before the owner starts the server.

### 2.3 R2: Honor, heroes, and faction force control

R2 owns the Honor currency, hero roster and lifecycle, configurable hero ranks,
and the ability to address a particular same-faction AiBot independently of
party membership. R2 is enabled only by an RTS save containing the R2 module
configuration.

---

## 3. Immutable boot law

### 3.1 Mode is data selected before process start

The active CharacterDatabase save owns `superui_worldstate`. The exact
lowercase row `mode=rts` is the outer gate. The server reads it during boot,
before the bot fleet and gameplay systems begin.

The resulting mode is immutable for that server process. Commands may report
status, but they must not turn an MMO process into an RTS process or turn an RTS
process into an MMO process. Changing worlds means the owner unloads/prepares a
different World State and starts the server through the owner's normal process.

### 3.2 Missing mode means a passive server extension

If the mode row or RTS table is absent, the server latches RTS off. In that
state it must not:

- create, alter, seed, or clean RTS tables;
- write faction pools or hero rows;
- apply RTS rates or faction bot caps;
- scan the world for a faction force roster;
- modify damage, scale, death, resurrection, Honor, movement, or possession;
- permit faction-wide control;
- answer an RTS action as though a module were active;
- hot-load module rows later in the process.

MangosSuperUI, not VMaNGOS boot code, owns preparing the required schema and
configuration for an RTS save.

### 3.3 Every module has a second gate

`mode=rts` is necessary but not sufficient. R2 feature modules are latched from
their configuration at the same boot:

- Honor requires the configured Honor module/weights;
- heroes require a valid hero-rule set;
- faction force control requires its explicit configuration flag.

Absent or invalid module configuration leaves only that module inert. This is
important for loading an R1-only RTS save with an R2-capable binary.

### 3.4 Disabled helpers are identity functions

An unavoidable stock-core hook follows one of two forms:

```cpp
SuiRts::OnEvent(...);                  // immediate no-op while disabled
value = SuiRts::AdjustValue(...);      // returns the original value while disabled
```

It is not acceptable for a disabled helper to write the same apparent value
back into a stock field. Writing `scale=1.0`, for example, is not a no-op: it
can erase a normal aura or an administrator's scale override. Disabled means no
mutation, not mutation to a default.

---

## 4. R2 gameplay contract

### 4.1 Faction Honor

Honor is a signed 64-bit shared faction pool stored separately for Alliance and
Horde. The server is authoritative for accrual and spending.

Only combat against the opposing faction is eligible. Neutral wildlife and
ordinary hostile PvE creatures are not opposing-faction units merely because
they can be attacked. Creature awards require an unambiguous Alliance/Horde
faction classification.

The initial configurable weights are:

| Victim classification | Default Honor |
|---|---:|
| Opposing human player | 10 |
| Opposing AiBot player | 5 |
| Opposing-faction ordinary NPC | 1 |
| Opposing-faction elite NPC | 3 |

Same-faction kills, self-inflicted/environmental deaths, pets without an
opposing-faction player owner, and neutral creatures award zero.

Bot-versus-bot kills may be suppressed from the stock per-character Honor
history to avoid write amplification. Human-involved PvP keeps the ordinary MMO
Honor path. Suppression must not reduce a human group member's stock share by
counting a suppressed bot in the divisor.

### 4.2 Hero candidates are AiBots

R2 heroes are persistent AiBot player characters. The logged-in human commander
and ordinary real-account characters are not hero declaration targets. This
keeps hero death/revival inside the bot lifecycle and avoids changing the normal
human resurrection contract.

The server validates every action. A client-provided GUID is never proof that a
unit is a bot or belongs to the faction.

### 4.3 Slot law

R2 uses a fixed configured slot cap, initially four per faction. Living and dead
heroes both occupy a slot. Losing or lowering capacity never demotes an existing
hero. A full roster blocks another declaration without spending Honor.

R3 may replace declaration capacity with territory-derived capacity. Existing
R2 heroes remain valid when that later rule arrives.

### 4.4 Rank law and initial economy

Rows are target levels. The cost on level 1 is the declaration price. The cost
on levels 2 through 5 is the price to enter that level.

| Hero level | Enter-level cost | Revive fee | Total scale | Total damage |
|---:|---:|---:|---:|---:|
| 1 | 20 | 10 | 120% | 120% |
| 2 | 40 | 20 | 140% | 140% |
| 3 | 80 | 40 | 160% | 160% |
| 4 | 160 | 80 | 180% | 180% |
| 5 | 320 | 160 | 200% | 200% |

These are initial play-test defaults, not final balance claims. The loaded World
State owns them.

### 4.5 Spending is validate-then-commit

An action must validate mode, module, actor authority, target identity, faction,
state, slot/rank, and affordability before moving, resurrecting, scaling, or
persisting the target. Insufficient Honor is a read-only failure. No corpse may
be moved and no rank may change before the pool debit succeeds.

### 4.6 Death and paid revive

A dead hero retains its row, level, and slot. Ordinary autonomous bot revival is
held while that declared hero is dead. A successful Revive spends the configured
fee, revives the same bot at its normal graveyard, restores full resources for
the R2 vertical slice, and reapplies its configured hero effects.

Administrator recovery remains outside the normal player action contract.

---

## 5. Faction force and direct-control contract

### 5.1 Party is not the faction

The old control roster and free-view selection enumerate the logged-in character
and group members. R2 keeps those collections unchanged because they also drive
party orders and group UI.

R2 maintains a separate faction force catalogue. A force row is an active,
same-faction, genuine AiBot with its full GUID and strategic location. It is not
silently inserted into party-order collections.

### 5.2 Server-filtered authority

The server derives the requesting faction from the authenticated session. The
client never supplies a trusted team. A direct-control grant requires:

- boot-latched RTS mode and faction-control module;
- an in-world Player with a bot WorldSession and AiBotAI;
- the same faction as the commander;
- alive and not taxi-flying, teleporting, transported, or otherwise invalid;
- no other active possessor;
- a compatible outdoor map transfer or the same allowed instance.

MMO mode continues to require the existing same-group law.

### 5.3 Paged roster

A faction may contain roughly 1,250 bots and the world may contain roughly
2,500 sessions. VMaNGOS rejects outbound packets above `0x8000` bytes, so R2
must not append a global army dump to the five-second zone-intel packet.

The reserved RTS opcode pair 842/843 carries a bounded, sorted, paged force
query. Rows are held in a separate client snapshot and names are resolved with
the normal name-query protocol only for newly visible GUIDs.

### 5.4 Selection versus control

Commander distinguishes these actions:

- **Select:** choose one exact faction bot and expose its details/hero actions;
- **View:** move the strategic camera when the destination can be streamed;
- **Take Control:** enter the existing possession pipeline and obtain the full
  MMO character experience for that bot.

Taking control means live movement, combat, targeting, casting, action bars,
inventory, character information, and the other already-proxied possessed-bot
surfaces. It is not a lighter AI order.

### 5.5 Cross-map transfer

`Camera::SetView` cannot attach to an object in another Map. R2 therefore treats
cross-map control as an explicit asynchronous transfer, not as a relaxed
visibility check:

1. validate the target and outdoor destination;
2. remove/retire the source free-view eye;
3. return a relocating control result;
4. transfer the logged-in commander character to the bot's map/location;
5. let the client complete the ordinary world load;
6. wait until the target entity is resident;
7. retry the normal control request and grant possession;
8. leave the commander at that destination when control later ends.

R2 never relocates into an instance. An instance target is controllable only
when it is already on the commander's same map and instance, is streamed and
passes every ordinary gate; any unstreamed instance target is denied.

The transfer is an RTS-only extension workflow. It must not alter ordinary MMO
teleport or party-possession behavior.

### 5.6 Multi-human lease

A bot has at most one possessor. A second commander receives a busy result.
Faction membership gives eligibility, not concurrent ownership.

---

## 6. Repository responsibility matrix

| Responsibility | MSUIClient | VMaNGOS / SuperUI-Core | MangosSuperUI |
|---|---|---|---|
| Draw Commander force/hero UI | owns | no | no |
| Parse/build custom wire | owns client half | owns server half | no |
| Decide faction/eligibility | presentation only | authoritative | profile metadata only |
| Award/spend Honor | no | authoritative | configures defaults |
| Persist hero/pool state | no | CharacterDatabase | snapshots/restores it |
| Create RTS schema | no | must not on MMO boot | owns offline preparation |
| Apply loaded launch profile | no | consumes at boot | owns while services are stopped |
| Direct-control movement/combat | sends existing input | authoritative | no |
| Start/stop/restart server | never | never by implementation | never automatically; owner only |

---

## 7. MSUIClient implementation record

### 7.1 Exact R2 client file ledger

| File | R2 responsibility | Isolation/lifecycle law |
|---|---|---|
| `MSUIClient/Net/RtsWire.cs` **(new)** | Defines typed faction, hero, dungeon, action-result and faction-force rows. Builds 840 and 842 bodies. Strictly parses 839, 841 and 843 into complete temporary values before publication. | Rejects undersized strides, invalid booleans, malformed GUIDs, bad page cursors, over-limit rows, truncation and trailing bytes. A rejected packet cannot partially replace the last good state. |
| `MSUIClient/Net/Opcodes.cs` | Names 842/843 and advances the custom range without moving 844-847 portal opcodes. | Stock opcodes and the existing portal allocation remain unchanged. |
| `MSUIClient/Net/WorldSession.cs` | Sends the existing RTS action and the bounded force-page request through `RtsWire`. | Contains no gameplay authority; it serializes exact-width bodies only. |
| `MSUIClient/Net/NetworkClient.cs` | Exposes in-world facades for the action and force requests. | Returns failure when no in-world session exists, allowing the UI to unwind its pending latch. |
| `MSUIClient/GameLoop/Scene/GameLoop.Net.cs` | Routes 843 to Commander, clears Commander state at a terminal disconnect, and refreshes the locally controlled renderer's authoritative `OBJECT_SCALE_X`. | Does not infer hero scale locally. It renders the scale the server/native aura published. |
| `MSUIClient/GameLoop/Combat/GameLoop.Targeting.cs` | Clears session-scoped player identity/name-query caches. | Prevents a low GUID reused after an MMO/RTS database swap from inheriting an old bot name. |
| `MSUIClient/GameLoop/Panels/GameLoop.Logout.cs` | Clears SUI control, Commander/RTS state and identity caches on a clean return to character select. | A later vanilla login cannot briefly expose the prior RTS roster, Honor value or pending action. |
| `MSUIClient/GameLoop/Scene/GameLoop.CommanderMap.cs` **(mixed file)** | Publishes atomic RTS snapshots; pages the selected zone's faction force; lazily requests bot names; renders own-faction Honor/heroes/force rows; joins hero state by full GUID; sends Declare/Upgrade/Revive; correlates one pending action; and exposes `Take Control` for the exact selected bot. | The force dictionary is separate from Tier-1 party/group units. Honor, Heroes and Faction Control each require their server module bit. Force loading keeps the previous complete view until the final page. Timeouts, module loss and session reset discard staging/pending state. This file also contains earlier fullscreen dual-continent Commander work that is **not** an R2 change. |
| `MSUIClient/GameLoop/Scene/GameLoop.Control.cs` **(mixed file)** | Implements the R2 `Take Control` state machine: request, relocating ACK, normal NEW_WORLD wait, target-stream wait, final 828 retry and ordinary possession grant. | Explicit Take Control exits strategic free view and lands in the existing full MMO possession path. Existing party/free-view possession, orders, patrol and waypoint code remains Tier 1 and is not reclassified as R2. Pending relocation is cleared on timeout, denial, grant, logout and disconnect. |
| `MSUIClient/Engine/UI/CommanderMapUiLaw.cs` **(mixed file)** | Defines module bits, hero action eligibility and Faction Control presentation gates. | Pure helpers never grant authority. Server row flags and action results remain authoritative. The same file also owns earlier Commander layout/atlas laws unrelated to R2. |
| `tools/commander-map-clinical-check/RtsWireClinicalChecks.cs` **(new)** | Golden and adversarial tests for 839-843, row strides, all truncations, page limits, flags, full GUIDs and action bodies/results. | A wire change cannot silently drift from the paired server layout. |
| `tools/commander-map-clinical-check/Program.cs` **(mixed file)** | Adds R2 module, eligibility, lifecycle/source-fence and presentation assertions to the existing Commander clinical suite. | Earlier map-layout/overlay assertions in this file are not claimed as R2. |
| `tools/commander-map-clinical-check/commander-map-clinical-check.csproj` | Links the production R2 wire source into the focused executable check. | Tests the same implementation used by the client rather than a duplicate codec. |

### 7.2 State publication and reset sequence

The client treats 839 and 843 as replaceable snapshots, not as incremental
authority. It parses an entire 839 body first, then replaces mode, module,
faction, hero and dungeon collections together. A force request has a nonzero
generation ID, exact zone, exclusive GUID-low cursor and a staging dictionary.
Only a page with `nextGuidLow=0` publishes that dictionary. A malformed page,
duplicate/backward GUID, correlation mismatch, send failure or timeout discards
staging and leaves the last complete published force intact.

Logout and terminal disconnect clear all of the following together:

- RTS mode and module bits;
- faction, hero and dungeon snapshots;
- published and staging force dictionaries;
- selected force GUID and page correlation;
- pending hero action;
- pending relocation/control retry;
- SUI roster/selection state;
- player-name and outstanding name-query caches.

### 7.3 What direct control means in the client

The button is not an AI command. On final ACK the existing controlled-GUID,
mover, proxy, action-store, movement, targeting, combat, casting, inventory and
character surfaces bind to that bot exactly as they do for party possession.
R2 only adds discovery, eligibility and the cross-map correlation needed to
reach a bot outside the party. It does not fork a second MMO-control stack.

### 7.4 Explicitly excluded earlier client work

The same working tree also contains earlier Commander and normal-MMO map work:
`AreaTable.cs`, `WorldMapOverlayCatalog.cs`, `GameLoop.WorldMap.cs`, ordinary
map exploration fields, vanilla UI/action-bar adjustments and their overlay
clinical checks. Those changes fix atlas registration, fullscreen dual-continent
Commander presentation, shaped highlights and 1.12 fog/exploration behavior.
They are valuable, but they are not R2 Honor/Hero/Faction Control and are not
counted as R2 files in this ledger.

---

## 8. VMaNGOS / SuperUI-Core implementation record

This record was reconciled against the complete semantic diff in the frozen R2
server review tree, based on commit `526bcbea8`. There are 27 semantic server
files in that diff. `src/shared/Progression.h` is deliberately **not** in this
record: Git reported a working-tree status caused by line-ending metadata, but
its worktree blob and `HEAD` blob both hash to
`f9a08bce25ab943b8d26308d9a0d3e90a08422da`; `git diff`, `--numstat`,
`--name-only`, and `--raw` all contain no content change for it.

The existing R1 lifecycle calls in `World.cpp` are also not R2 edits. They
already call `SuiPossess::LoadWorldState()` followed by
`SuiRts::LoadRuleset()` during boot, `SuiRts::Tick(diff)` in the joined
world-update window, and `SuiRts::Shutdown()` before session teardown. R2 fills
those existing extension seams; it does not add another core update loop.

### 8.1 Immutable boot latch and configuration ownership

The server no longer creates or seeds any `superui_*` table. The loaded World
State artifact, prepared by MangosSuperUI, owns schema and configuration. Core
boot performs the following one-way sequence:

1. `SuiPossess::LoadWorldState()` defaults the process latch to vanilla and
   reads only `superui_worldstate['mode']`.
2. If that exact value is not `rts`, `SuiRts::LoadRuleset()` returns before it
   probes any R2 module/rule/runtime table. Every module accessor remains false.
3. On an RTS boot, the core reads the scalar key/value set once, applies only
   positive configured XP/drop rate overrides, clamps `state.flush_ms` to
   1,000-3,600,000 ms, and latches faction bot caps and explicit module gates.
4. Honor requires `honor.enabled=1`. Heroes require both `hero.enabled=1` and
   Honor, then fail closed unless all five rule rows and their five native
   aura-spell rows validate. Faction control requires
   `control.faction_bots=1`. Territory and dungeon bits remain boot-latched R3
   and R4 placeholders; R2 adds no mechanics behind them.
5. Loaded faction Honor is normalized to zero if a malformed preserved row is
   negative. The correction marks the pool dirty so the ordinary write-behind
   flush repairs the persisted row; positive additions and refunds saturate at
   `INT64_MAX` instead of overflowing through zero/negative values.
6. A second `LoadRuleset()` call is refused. `.sui rts reload` reports that
   reload is disabled, and `.sui worldstate <value>` reports that the requested
   runtime override was not applied. There is no supported hot path from MMO to
   RTS or from one R2 configuration to another.

This is the core isolation guarantee: in an MMO boot, the only new work is the
single mode-row read through the pre-existing R1 boot seam. The kill, Honor,
hero, faction-roster, cross-map possession, automatic-resurrection and periodic
flush branches all return before changing state.

### 8.2 Extension-module file ledger

| File | Exact R2 symbols and behavior | Disabled behavior, thread and persistence law | Review evidence |
|---|---|---|---|
| `src/game/SuperUiBots/SuiRts.h` | Declares the boot-latched module facade, typed scalar accessors, atomic Honor pool API, faction bot-cap accessor, minimal kill/world-entry dispatchers, 839 state handler and 840 action handler. Adds `FactionControlEnabled`, `TrySpendHonor`, and `RefundHonor`; removes the runtime `Reload` API. | Every public module accessor includes the immutable RTS mode gate. Callers do not receive a permissive raw flag that can outlive the mode latch. | Header/source pairing and every call site were reviewed; no runtime reload declaration remains. |
| `src/game/SuperUiBots/SuiRts.cpp` | Replaces R1 table creation/row-count inference with read-only boot configuration. Explicitly latches Honor, Heroes, Territory, Dungeons and Faction Control; applies managed stock rates; caches bot caps; loads pools; normalizes a negative preserved pool to zero/dirty; dispatches the single kill and world-entry seams; performs atomic CAS spend and checked saturating add/refund; drains hero deaths and dirty pool writes; emits complete 839 faction/hero state; routes 840 actions to `SuiHero`; and exposes diagnostic `status`, `heroes`, and positive Honor-add commands. | MMO returns before module-table reads. `AddHonor` rejects disabled/nonpositive awards. Spend/refund require active Honor+Heroes. Add/refund saturate at `INT64_MAX`, log the attempted overflow, and never publish a wrapped negative pool; refund still reports the unchanged pool when disabled/invalid. Map-thread awards touch atomics only. The existing main-thread tick writes dirty pools synchronously with `UPDATE`, not schema creation or row replacement; shutdown invokes the same synchronous flush. This prevents an older queued write from overtaking the final clean-shutdown value. Reload is refused. A disabled action returns result 4 through `SuiHero`. | Full semantic diff reviewed, including overflow/negative-state and shutdown-order hardening; `git diff --check` passes; the authoritative Release/scripts build passes as recorded in section 15. |
| `src/game/SuperUiBots/SuiHonor.h` | Declares only boot scalar loading, the single kill-classification entry point, and the narrowly defined bot-versus-bot vanilla-HK suppression predicate. | No generic Player/Unit override is exposed. | All three symbols have one bounded purpose and reviewed callers. |
| `src/game/SuperUiBots/SuiHonor.cpp` | Caches nonnegative player, bot, faction-NPC and elite weights plus the suppression switch in atomics. Resolves the killing player from the killer or its owner, requires hostility, awards only for an opposing-faction player/bot or an unowned creature whose faction template belongs **exclusively** to the opposing player faction, and delegates positive awards to the atomic faction pool. `SuppressVanillaHonor` is true only when both recipient and victim are bots. | First gates are RTS mode and Honor. Same-faction kills, suicides, duels, neutral/ordinary hostile wildlife, mixed/neutral faction masks, pets, guardians and summons award zero. Human HK history is never suppressed. The kill hook is map-thread safe: cached atomics plus `SuiRts::AddHonor`; it performs no DB write. | Classification, ownership, faction-mask and suppression paths reviewed against `Unit::Kill` and `Player::RewardHonorOnDeath`. |
| `src/game/SuperUiBots/SuiHero.h` | Defines the persisted hero snapshot, ruleset lifecycle, death/world-entry hooks, the resurrection-hold predicate, per-team fielded/slot reporting, snapshot export, and action IDs 1 declare, 2 upgrade, 3 paid revive with 841 result codes 0-4. | The public hold predicate is false unless RTS+Heroes and a persisted/pending/reviving bot hero match. | Interface and all consumers reviewed. |
| `src/game/SuperUiBots/SuiHero.cpp` | Loads exactly levels 1-5 and validates spell IDs 51001-51005, 100-200 percent configured effects, exact passive/no-cancel/permanent/native spell shape, caster `MOD_SCALE`, and all-school `MOD_DAMAGE_PERCENT_DONE`. Loads valid persisted heroes, clamps the **per-faction** slot cap to 1-127, applies one native aura and removes stale R2 rank auras, queues bot-hero deaths, writes `dead=1` on the main tick, and implements bot-only declare/upgrade/revive. On bot world entry it reconciles both possible persisted/physical mismatches: a persisted live row paired with a physically dead bot is synchronously marked dead before AiBotAI can free-revive it; a persisted `dead=1` row paired with a physically alive bot is returned through the ordinary self-kill path, which awards no Honor and adds no duplicate pending death. It then refreshes or removes the rank aura. Actions validate the human actor and same-faction in-world `AiBotAI`, validate state/cost under the module mutex, spend through atomic CAS, then revalidate and refund on races/failure. Paid revive requires a normal faction graveyard before spending, teleports the corpse there, restores full resources, stops combat, creates bones, clears persisted death, and reapplies the same rank aura. | `Active()` requires RTS+Heroes. Declaration/action identity requires both a bot session and attached `AiBotAI`; the resurrection hold uses persisted bot-session identity because login corpse recovery can precede AI attachment. World-entry reconciliation is active only for an attached AiBot and only for a persisted/physical mismatch. Humans, nonheroes and ordinary bots retain stock death/resurrection. Death hooks enqueue only; main-thread draining and every declare/upgrade/revive persistence statement execute synchronously. Using one write ordering prevents a queued action/death write from landing after the shutdown drain and resurrecting stale state on the next boot. The cap of 127 per side guarantees the combined 839 hero roster is at most 254 rows and fits its global `u8` count. | Full source reviewed for validate-before-spend, saturating refund integration, synchronous write ordering, revalidation/refund, bidirectional dead-state reconciliation, death queue, aura lifecycle, DB statements and all early exits; the authoritative Release/scripts build passes. |
| `src/game/SuperUiBots/SuiFactionControl.h` | Defines the only same-faction party-bypass predicate, the outdoor relocation result enum, and the paged force-roster handler. | `CanControl` is false unless boot-latched Faction Control is active. | Header/source and `SuiPossess` integration reviewed. |
| `src/game/SuperUiBots/SuiFactionControl.cpp` | Authorizes only a real in-world human actor and an in-world same-team bot session with attached `AiBotAI`. Opcode 842 produces a GUID-low-sorted live scan, optional exact-zone filter, exclusive cursor, maximum 200 rows, fixed 32-byte stride, total/count/next cursor, full GUID/map/zone/position/race/class/level, and alive/busy/eligible/same-map/hero/dead/instance flags. It resolves hero flags from a complete `SuiHero` snapshot and deliberately omits names so the client uses stock name queries. Relocation removes any free-view eye, uses ordinary `Player::TeleportTo` to the bot's exact outdoor position, restores the eye if teleport submission fails, and never enters an instanceable map or nonzero destination instance. | MMO or disabled Faction Control returns a syntactically complete empty page. Reserved request flags or request ID zero return an empty terminal page **without scanning**. `ROW_ELIGIBLE` additionally requires the commander to be alive, self-mover, not already controlling, and not taxi/transport/teleporting; the bot must be alive, unpossessed and likewise not taxi/transport/teleporting, then be either same-map+same-instance+visible or at a non-instance destination. The player registry is scanned under its read guard; pagination is explicitly live, not a frozen snapshot. | Request validation, requester/target eligibility parity, row stride/flags, cursor math, 200-row clamp, read guard and relocation exits reviewed against the paired client codec and protocol document. |
| `src/game/SuperUiBots/SuiPossess.h` **(mixed R1/Tier-1 file)** | Adds ACK 7 `ACK_RELOCATING`, denial 8 `DENY_CROSS_INSTANCE`, and free-camera prepare/restore helpers used only by faction relocation. Removes the runtime worldstate-override declaration. | The existing party possession API and all release reasons remain intact. | Enum values match client/wire review; no existing values were renumbered. |
| `src/game/SuperUiBots/SuiPossess.cpp` **(mixed R1/Tier-1 file)** | Makes the mode read boot-only/read-only; permits an existing same-group target **or** the `SuiFactionControl::CanControl` bypass; relocates an authorized non-visible faction target when its destination is outdoor/non-instance (including a distant target on the same outdoor map); returns 7 so the client waits for normal streaming and retries; returns 8 for any instance target that is not already same-map, same-instance and visible; removes free view only after ordinary denial gates pass; and parks a nonparty commander's unattended body idle/out of combat instead of attaching the party-follow unattended AI. | With Faction Control false, authorization remains same-group only and a non-visible target is denied exactly through the legacy path. All existing requester, bot identity, busy, death, taxi, transport, teleport and mover gates still run. A rejected same-map request no longer destroys an existing free view. Party possession still attaches the original unattended-owner AI; only the new faction/nonparty grant parks the owner. | Complete diff reviewed through `TryBegin`, `HandleRequest`, relocation helpers, owner parking and `.sui worldstate`. |

### 8.3 Wire, registration, build-list and protocol-document ledger

| File | Exact hook/purpose | Disabled/compatibility behavior | Review evidence |
|---|---|---|---|
| `src/game/Server/Packets/SuiControl.h` | Adds typed 842 request decoding: `u8 flags`, `u32 requestId`, `u32 zoneId`, `u32 afterGuidLow`, `u8 limit`. | Parsing only creates a value object; authority remains in `SuiFactionControl`. Existing control/RTS packet classes are unchanged. | Body is exactly 14 bytes and matches the client builder/clinical vectors. |
| `src/game/Server/Protocol/Opcodes_1_12_1.h` | Assigns 842/843 to force request/reply. | Existing 828-841 and fixed portal 844-847 values are not moved; `NUM_MSG_TYPES` already covers the range. | Numeric allocation cross-checked with both protocol docs and client `Opcodes.cs`. |
| `src/game/Server/Protocol/Opcodes.cpp` | Registers 842 as logged-in, `PACKET_PROCESS_WORLD`; declares 843 server-only. | Stock clients never send this custom opcode. An inbound 843 is rejected as server traffic. | Handler registration and execution category reviewed. |
| `src/game/Server/WorldSession.h` | Declares the typed 842 handler. | No session state or generic opcode path is widened. | Declaration matches the definition in `SuiFactionControl.cpp`. |
| `src/game/CMakeLists.txt` | Adds the three new module source/header pairs and explicitly lists `SuiRts.h`. | No compiler flags, stock source selection or install rule changes. | All six new files are present in `game_SRCS`; source-level diff check passes. |
| `docs/SUI_WIRE_PROTOCOL.md` | Documents module bit 4, 842/843 request/reply layouts, force flags/cursors, control results 7/8, faction-control authorization/relocation, explicit module keys, 1-127 cap, native hero aura contract and MangosSuperUI schema ownership. | Keeps the portal block fixed at 844-847 and states that disabled/MMO force queries return empty. | Reviewed against server structs/serialization and client parser/builder. One wording rule is important: four slots is the **profile default**; the actual wire/core contract is configurable 1-127. |

### 8.4 Stock seam ledger: Honor and hero lifecycle

These are the only stock gameplay files changed by R2. Every hook delegates to
an extension function whose first effective decision is the boot/module gate.
The resurrection call sites are distributed throughout the 1.12 core; guarding
only `Player::ResurrectPlayer` would also block R2's own paid revive and would
turn a universal stock primitive into mode-specific policy. The narrow call-site
guards therefore preserve the stock primitive and make the exception explicit.

| Stock file | Exact function/hook | Why this seam is required; enabled mutation | Disabled behavior and thread law | Review evidence |
|---|---|---|---|---|
| `src/game/Objects/Unit.cpp` | First line of `Unit::Kill` calls `SuiRts::OnUnitKill(this, pVictim)`. | This is the one authoritative killing-blow seam before later loot-recipient/tag logic. It atomically awards faction Honor and records a hero victim in the pending-death set. | MMO/disabled calls return immediately. No generic damage calculation, attack result or loot code changes. Map-thread hook performs no DB write. | Diff reviewed; `Unit::DealDamage` remains untouched. |
| `src/game/Objects/Player.cpp` | `RewardHonorOnDeath` filters bot recipients through `SuiHonor::SuppressVanillaHonor`, before group divisor calculation and in the solo loop; empty filtered groups are skipped. | Prevents bot-versus-bot RTS fights from filling stock PvP HK history while preserving the new faction pool. | Predicate is false outside RTS+Honor, when suppression is off, or when either side is human. Human and ordinary MMO Honor distribution remain stock. | Group/solo branches and zero-recipient divisor guard reviewed. |
| `src/game/Objects/Player.cpp` | `SendInitialPacketsAfterAddToMap` calls `SuiRts::OnPlayerWorldEnter` immediately after `UpdateZone`. | Reapplies the persisted native rank aura after login/map add. It also closes both dead-state drift directions: a physically dead hero with a stale live row is persisted dead before bot AI updates, while a physically alive hero with a preserved dead row returns through the ordinary self-kill path and loses its rank aura. | No-op unless Heroes is active and the player is an attached AiBot hero; no human/nonhero login state is changed. | Hook ordering, bidirectional dead-state reconciliation and aura dispatch reviewed. |
| `src/game/Objects/Player.cpp` | Guards map/corpse auto-revive in `TeleportTo`, delayed revive in `ProcessDelayedOperations`, transport fallback in `RepopAtGraveyard`, corpse-less dead-login repair in `LoadCorpse`, and player-request acceptance in `ResurrectUsingRequestData` (clearing a held request). | Closes five core-owned bypasses that could otherwise revive a persisted dead hero without paying; paid revive continues to call the unchanged primitive after placing its internal `reviving` guard. | `BlocksResurrection` is false for humans, ordinary bots, nonheroes and all MMO boots, so original branches execute unchanged. It is a mutex-protected read/clear only; no DB write occurs at these seams. | All five call sites reviewed and matched to the central hold predicate. |
| `src/game/Battlegrounds/BattleGround.cpp` | Guards the dead-player revive in `EndBattleGround` and `RemovePlayerAtLeave`. | Prevents battleground completion/exit from bypassing paid hero death. | Stock resurrection remains for every unheld player. | Both direct battleground revive sites reviewed. |
| `src/game/Handlers/BattleGroundHandler.cpp` | Guards queue-entry revive in `HandleBattleFieldPortOpcode`. | Prevents joining/leaving the battleground flow from silently reviving a held hero. | Stock branch remains byte-for-byte reachable for unheld players. | Direct call site reviewed. |
| `src/game/Handlers/MiscHandler.cpp` | `HandleReclaimCorpseOpcode` returns for a held hero; the pre-1.3 Molten Core area-trigger special revive also checks the hold. | Covers manual corpse reclaim and the historical special-case revive. | Outside the narrow predicate both paths are stock. | Both handler paths reviewed. |
| `src/game/Handlers/NPCHandler.cpp` | `SendSpiritResurrect` returns for a held hero. | Prevents spirit-healer activation from bypassing the fee. | Normal spirit-healer resurrection, sickness and durability loss are unchanged for everyone else. | Entry guard reviewed. |
| `src/game/Spells/SpellEffects.cpp` | Guards `EffectResurrectNew`, `EffectResurrect`, `EffectSelfResurrect`, and `EffectSpiritHeal`. | Covers spell-created resurrection requests, legacy resurrection, self-resurrection and spirit-heal spell paths without changing the underlying player primitive. | All spell behavior remains stock when the predicate is false. | All four effect entry points reviewed. |
| `src/game/Transports/Transport.cpp` | Guards cross-map ship-transport resurrection in `ShipTransport::TeleportTransport`. | Prevents a transport map transition from being a free hero revive. | Movement/transport cleanup and normal player revive remain stock otherwise. | Direct transport call site reviewed. |
| `src/scripts/eastern_kingdoms/burning_steppes/blackwing_lair/instance_blackwing_lair.cpp` | Guards the Orb of Command corpse resurrection in `AreaTrigger_at_orb_of_command`. | The scripted BWL shortcut directly calls resurrection and teleport, so it would bypass every core handler guard without this local check. | The predicate is false for MMO, humans, nonheroes and ordinary bots; their quest/corpse/teleport script is unchanged. | Script resurrection site reviewed after the final bypass sweep. |
| `src/game/SuperUiBots/AiBotAIBridge.cpp` | `AiBotAI::BridgeHandleResurrect` logs and returns for a held hero. | Stops external bot-brain resurrection commands from bypassing paid revival. | Existing bridge resurrection state machine runs unchanged for ordinary bots. | Bridge entry path reviewed. |
| `src/game/SuperUiBots/AiBotAIMain.cpp` | `AiBotAI::UpdateAI` cancels `m_pendingGraveyardRez` and its timer when the bot becomes a held hero. | Closes an already-scheduled graveyard self-rez race after hero death. | Nonhero AI update and graveyard timing remain unchanged. No DB write occurs here. | Pending-state branch reviewed. |

### 8.5 Behavior deliberately not implemented in stock core

R2 does **not** modify `Unit::DealDamage`, weapon/spell damage formulas, model
scale fields, creature templates, group membership, generic teleport rules,
generic bot roster creation, normal name generation, or `Progression.h`. Hero
power is represented only by validated native passive auras 51001-51005. The
new same-faction right is checked only in `SuiFactionControl` and consumed only
by the existing SUI possession path; it does not make the target a party member
and does not widen group-order recipients.

The server diff passes `git diff --check` and was reviewed file-by-file against
the client codec and web profile contract. The same 27 normalized source files
were copied to the authoritative `development` checkout, hash-compared with zero
mismatches, and compiled successfully in the Release/scripts build recorded in
section 15. No install, runtime, database swap, World State load, or live gameplay
result is claimed here.

---

## 9. MangosSuperUI implementation record

### 9.1 Exact World State application file ledger

| File | R2 responsibility | Safety boundary |
|---|---|---|
| `MangosSuperUI/Models/WorldConfigurationModels.cs` | Adds the `rts-r2-v1` profile, makes it the new-create UI default, retains `rts-r1-v1`, defines Honor/hero fields and five target-level rows, validates all bounds, and emits the managed scalar/rule set. | The model-level fallback stays R1 so pre-profile manifests do not silently gain mechanics. Hero slots are 1-127 so both faction rosters fit the existing global `u8` hero count. |
| `MangosSuperUI/Controllers/WorldsController.cs` | Returns both profile descriptors/defaults from `CreateOptions` and accepts the selected launch configuration for create/resume. | Accepts structured allowlisted fields, not SQL, table names or arbitrary configuration keys. |
| `MangosSuperUI/Services/RtsWorldCreationService.cs` | Builds a clean R1 or R2 genesis: current CharacterDatabase schema, managed scalar rows, compatible hero-rule columns, zero faction pools, zero hero/territory/dungeon state, zero characters/bots, preserved accounts and selected world/core/admin data. | Creation produces a parked artifact only. It does not boot the server or populate a roster. |
| `MangosSuperUI/Services/RtsHeroSpellWorldStore.cs` **(new)** | Transforms only the staged `world_mangos.sql.gz` artifact: captures any pre-existing build-5875 rows 51001-51005 once, installs the five native R2 passive aura rows for R2, and restores the captured originals for R1. | It never edits a live WorldDatabase. Only five reserved IDs are managed; 51006+ and every unrelated world row are untouched. |
| `MangosSuperUI/Services/WorldStateService.cs` | Forces an RTS profile resume through a full snapshot restore, stages the profile-specific world artifact, applies managed CharacterDB configuration in one stopped-world transaction, preserves runtime `superui_faction`/`superui_heroes` on resume, validates the final rules/spells, and records profile metadata. | R2 configuration is immutable for the next boot. It does not hot-edit a running match. MMO resume follows the ordinary captured artifacts and receives no RTS postlude. |
| `MangosSuperUI/Views/Worlds/Index.cshtml` | Adds R1/R2 selectors and explicit create/review descriptions for preserved/reset data and reserved spell rows. | The final operation remains an owner-clicked parked create or owner-clicked prepare/resume; no automatic server lifecycle action was added. |
| `MangosSuperUI/wwwroot/js/worlds.js` | Renders server-owned profiles and editable R2 Honor, suppression, control, slots and five hero rows on both create and resume; validates them; previews exact effects and warnings. | The browser mirrors bounds for feedback, but the server repeats all validation. It never constructs SQL. |
| `tools/worldstate-clinical-check/Program.cs` | Exercises clean R2 seed, R1 inert seed, managed keys, schema compatibility, spell artifact install/rollback, preservation/reset laws, 1-127 slot bounds and invalid rule rejection. | File-only checks prove deterministic artifacts, not a live MariaDB import or gameplay result. |

### 9.2 Profile identities and defaults

`rts-r1-v1` remains the foundation-only profile. It removes the R2 boot gates
and managed Honor/hero rule rows from the prepared save while leaving parked
runtime pool/hero rows intact for a later R2 resume. It restores the original
51001-51005 world rows captured before R2 first owned those IDs.

`rts-r2-v1` is the new UI default for a newly created RTS campaign. Its initial
values are:

| Field | Initial value |
|---|---:|
| `PlayerLimit` / `PlayerHardLimit` | 2600 / 2600 |
| Alliance/Horde bot caps | 1250 / 1250 |
| kill/quest XP rates | 40 / 40 |
| `state.flush_ms` | 30000 |
| player/bot/faction-NPC/elite Honor | 10 / 5 / 1 / 3 |
| suppress bot-vs-bot vanilla HK history | true |
| `control.faction_bots` | true |
| fixed hero slots per faction | 4 |
| enter-level costs | 20 / 40 / 80 / 160 / 320 |
| revive fees | 10 / 20 / 40 / 80 / 160 |
| spell IDs | 51001 / 51002 / 51003 / 51004 / 51005 |
| total scale and damage | 120 / 140 / 160 / 180 / 200 percent |

All of these are load-time configuration, not compiled balance promises. The
profile accepts only complete five-level rules, level-matched reserved spell
IDs, nonnegative bounded costs/weights, 100-200 percent effects, 1-127 slots,
valid capacity relationships and the existing safe rate/config ranges.

### 9.3 Create versus resume state law

A **new** R2 campaign starts with no characters, no bot roster, zero Honor and
no heroes. It preserves human accounts, the selected world content, reusable
admin/configuration data and the global name list. Later bot creation reuses the
existing name-list path.

An **R2 resume/profile change** starts from an immutable selected snapshot,
fully restores it while services are stopped, changes only the managed launch
configuration/rule rows and staged five-spell world content, and preserves its
existing faction pools and hero roster. This is why an owner can park an R2
match, load MMO, then return to the same match without erasing progress.

MangosSuperUI configuration is not a second live authority. VMaNGOS reads the
prepared save at boot and owns the running match thereafter.

---

## 10. Database ownership and schema

### 10.1 CharacterDatabase

CharacterDatabase owns:

- `superui_worldstate` scalar mode/configuration;
- `superui_rules_hero` configured rank/cost/effect rows;
- `superui_faction` Honor pools;
- `superui_heroes` persistent bot hero state.

The final schema, compatibility treatment, seed statements, and managed key
allowlist will be copied here exactly from the implemented profile and loader.

### 10.2 WorldDatabase

R2 uses native spell auras for hero scale and damage instead of adding parallel
math to `Unit::DealDamage` or resetting scale in `Unit::UpdateModelData`. Spell
IDs 51001 through 51005 represent hero levels 1 through 5. Their configured
effects are:

- aura 61, total scale 120/140/160/180/200 percent;
- aura 79, damage done percent for the all-school mask at the same totals.

The spell IDs are referenced by `superui_rules_hero`. The server validates the
loaded world rows against the save-bound rule percentages before enabling the
hero module. These spell rows are authored WorldDatabase content and travel with
the world snapshot. An MMO snapshot restores its own original world content and
does not apply the hero auras.

This choice deliberately removes two tempting stock-core hooks: R2 does not
modify the general damage calculation and does not write every Player's model
scale during ordinary model updates. Native aura stacking/removal rules remain
the one implementation of those MMO mechanics.

### 10.3 vmangos_admin

`vmangos_admin` may hold reusable web metadata and audit history. It does not own
the authoritative Honor pool, hero roster, or active mode. The currently unused
`bot_registry` table must not be described as an authoritative persisted army
until code actually maintains it.

---

## 11. Wire allocation and compatibility

R1 allocated opcodes 838 through 841:

- 838 `CMSG_SUI_RTS_STATE`;
- 839 `SMSG_SUI_RTS_STATE`;
- 840 `CMSG_SUI_RTS_ACTION`;
- 841 `SMSG_SUI_RTS_ACTION_RESULT`.

R2 uses the already-reserved pair:

- 842 `CMSG_SUI_FORCE_ROSTER`;
- 843 `SMSG_SUI_FORCE_ROSTER`.

The frozen request body is exactly 14 bytes:

| Offset | Type | Field | Law |
|---:|---|---|---|
| 0 | `u8` | flags | version-1 requires zero |
| 1 | `u32` | requestId | opaque client correlation |
| 5 | `u32` | zoneId | zero means all zones; otherwise exact cached zone |
| 9 | `u32` | afterGuidLow | exclusive cursor; zero begins the snapshot |
| 13 | `u8` | limit | zero means 200; server clamps to at most 200 |

The reply begins with this exact 16-byte header:

| Offset | Type | Field | Law |
|---:|---|---|---|
| 0 | `u32` | requestId | echoes the request |
| 4 | `u32` | zoneId | echoes the effective filter |
| 8 | `u32` | nextGuidLow | zero means complete; otherwise last emitted low GUID |
| 12 | `u16` | total | matching row count, saturated to `u16` |
| 14 | `u8` | count | rows in this packet; never above 200 |
| 15 | `u8` | stride | version-1 row stride is 32 |

Each row is exactly 32 bytes:

| Offset | Type | Field |
|---:|---|---|
| 0 | `u64` | full player GUID |
| 8 | `u32` | mapId |
| 12 | `u32` | zoneId |
| 16 | `f32` | x |
| 20 | `f32` | y |
| 24 | `f32` | z |
| 28 | `u8` | race |
| 29 | `u8` | class |
| 30 | `u8` | level |
| 31 | `u8` | flags |

Rows are sorted by low GUID. The cursor is exclusive, so additions/removals
before the cursor do not cause a row to repeat inside one forward traversal.
The server rebuilds and filters every bounded page from the authoritative
online-player collection; it does not retain a cross-page snapshot or army
cache. Consequently `total` may legitimately change while a page chain is in
flight as bots log in, log out, die, revive, or cross the selected zone. The
client validates page order/correlation and publishes the completed staging set
atomically, but it does not require the final row count to equal an earlier
`total`. The resulting collection is a replaceable live view, not persistent
database truth.

Row flags are:

| Bit | Meaning |
|---:|---|
| `0x01` | alive |
| `0x02` | busy/already possessed |
| `0x04` | eligible for the RTS control policy |
| `0x08` | currently on the requester's same map and instance |
| `0x10` | declared hero |
| `0x20` | hero is dead or death persistence is pending |
| `0x40` | destination is instanceable/instance-restricted |
| `0x80` | reserved |

The existing control ACK adds two request results below the release-code range:

- 7 `RELOCATING`: the target is an otherwise eligible outdoor faction bot; the
  server accepted an owner-body world transfer. The ACK carries the target GUID
  and a server snapshot of its position/orientation. The client keeps the
  correlated control request pending, completes NEW_WORLD, waits for that target
  entity to stream, and retries opcode 828.
- 8 `DENY_CROSS_INSTANCE`: the target is instance-restricted and is not already
  same-map, same-instance and visible. R2 does not transfer into an instance;
  no transfer or possession mutation occurred.

Compatibility requirements that do not change:

- explicit-width little-endian fields;
- full player GUID on wire, low GUID only in persistence;
- server-derived faction;
- bounded pages below the server packet limit;
- strict client minimum-stride and exact-consumption checks;
- future row-tail skipping only when a declared stride permits it;
- malformed packets publish no partial snapshot.

Stock clients never send these opcodes. An R1-only client/server remains safe
through module flags and reserved allocation.

---

## 12. Threading and persistence law

Map-thread kill/death hooks may perform only bounded classification, atomic
counter changes, and queue insertion. Structural hero changes and database
writes occur on the designated world/main-thread path.

Hot combat paths must not acquire a global hero-roster mutex for every ordinary
hit. Prefer the native aura system or a lock-free/cached configured effect over
a map-wide serialized lookup.

Honor pools use checked atomic addition/debit and a dirty flag. Persistence is
write-behind at the configured flush period plus a clean shutdown flush, but
each selected flush performs its database update synchronously on the
world/main thread. Hero action and drained-death writes use the same synchronous
ordering so an older queued statement cannot overtake shutdown. Action results
return the authoritative post-action pool.

All database writes are gated by the boot-latched RTS/module state. There is no
vanilla-mode periodic write.

---

## 13. Security and trust boundaries

- Client-provided team, bot status, price, rank, and eligibility are untrusted.
- Force-roster rows are filtered server-side.
- Action subjects must be reconstructed/validated as Player GUIDs.
- Hero subjects must be genuine AiBots, not human accounts.
- Opposing-faction, offline, invalid-state, and busy targets fail without spend.
- Cross-map destinations come from the live server object, never client xyz.
- Page limits and packet sizes are server-clamped.
- One pending hero action and one pending control transfer are allowed per
  session.
- Logout, disconnect, timeout, target death/despawn, and world transfer clear or
  safely reconcile pending state.

---

## 14. MMO equivalence checklist

The final server review must answer every row with evidence:

| Concern | Required MMO result |
|---|---|
| Boot without RTS tables | starts as MMO; no table creation or error loop |
| Boot with tables but `mode` absent/vanilla | no R2 module active |
| Rates and bot caps | ordinary config behavior |
| Combat damage | bit-for-bit stock calculation inputs/results |
| Model scale | no write; stock auras and `.modify scale` survive |
| Honor | stock human/player behavior |
| Death/resurrection | stock human and ordinary bot behavior |
| Possession | current party-only authorization |
| Orders | current party-only subjects |
| Custom roster opcodes | inert/disabled response; no global scan |
| Runtime commands | cannot hot-enable RTS |
| Periodic tick/shutdown | no RTS writes |
| MMO World State resume | restores that world's original DB/config/content |

This checklist is a release gate, not aspirational prose.

---

## 15. Verification layers

### 15.1 File-only and clinical checks

- exact packet construction/parsing;
- every truncation rejected;
- undersized strides rejected;
- page/cursor bounds and maximum size;
- same-faction and bot-only eligibility laws;
- hero action eligibility/result correlation;
- validate-before-mutate spending;
- session/world reset;
- R1/R2 profile SQL and artifact generation;
- MMO profile preservation.

### 15.2 Builds

Build evidence is repository-specific:

| Repository/check | Command | Result on 2026-08-14 |
|---|---|---|
| MSUIClient | `dotnet build MSUIClient/MSUIClient.csproj --no-restore` | PASS, 0 warnings and 0 errors in the final integrated rerun. |
| Commander/R2 clinical | `dotnet run --project tools/commander-map-clinical-check/commander-map-clinical-check.csproj --no-restore` | PASS, 130 assertions. |
| World-map overlay regression | `dotnet run --project tools/world-map-overlay-clinical-check/world-map-overlay-clinical-check.csproj --no-restore` | PASS, all 526 overlay rows and 812 referenced chunks. This protects adjacent earlier map work, not R2 gameplay. |
| MangosSuperUI | `dotnet build MangosSuperUI.sln --no-restore` | PASS, 0 errors and 54 pre-existing warnings. |
| World State clinical | `dotnet run --project tools/worldstate-clinical-check/worldstate-clinical-check.csproj --no-restore` | PASS, including R1/R2 artifacts, spell rollback and 1-127 slot bounds. |
| VMaNGOS / SuperUI-Core | `cmake --build /home/wowvmangos/vmangos/build --parallel 16` in the authoritative `development` checkout | PASS, Release configuration with scripts enabled; completed through `[100%] Built target mangosd`. All 27 reviewed semantic files matched the source-review hashes and remote `git diff --check` passed first. This was build-only: no `make install`, deploy, database operation, or process restart followed. |

`git diff --check` passed for the frozen MSUIClient, MangosSuperUI and normalized
authoritative VMaNGOS R2 changes. The broad legacy `interface-wire-check` is independently stale because
its project still links pre-reorganization `Program.*.cs` paths; it is not used
as positive or negative evidence for R2.

### 15.3 Owner-operated live validation

No agent starts, stops, restarts, reloads, installs, deploys, swaps a live World
State, or mutates the live databases. After reviewed source builds, Nico owns
deployment and the validation run.

The planned R2 run is:

1. load/prepare `rts-r2-v1` while services are stopped;
2. owner starts the normal server processes;
3. verify mode/module flags and empty initial pools/hero roster;
4. populate opposing faction AiBots through the existing owner workflow;
5. verify neutral and same-faction kills award zero;
6. verify configured player/bot/faction-NPC/elite weights;
7. select a same-faction bot outside the current party;
8. declare it and confirm exact debit, roster row, effect, and persistence;
9. upgrade it and confirm the next target-level debit/effect;
10. fill four slots and reject a fifth without spending;
11. kill a hero, confirm it remains dead and occupies its slot;
12. attempt an unaffordable revive and prove no movement/state mutation;
13. revive successfully at the normal graveyard with the same level;
14. take MMO-style direct control of a same-map faction bot;
15. exercise the explicit cross-map outdoor relocation/grant path;
16. prove enemy, human, offline, busy, and unstreamed instance targets are denied;
17. owner restarts and confirms pools/heroes persist;
18. owner returns to an MMO World State and confirms the equivalence checklist.

### 15.4 Known limitations carried into owner validation

The final source review found no release-blocking contradiction, but R2 is not
transactionally perfect and the first live run must keep these boundaries
visible:

- Honor is intentionally write-behind between configured flushes. An abrupt
  process/host failure can lose the last unflushed interval; a clean shutdown
  performs the synchronous final flush.
- A hero kill first enters an in-memory pending-death set. The world tick drains
  it synchronously, and login reconciles a physically dead character with a
  stale live row, but an abrupt failure before either the character death or
  hero row is durable can still lose that sub-tick transition.
- CharacterDatabase statements and native aura application are checked only at
  the API/result level available in this slice; the Honor debit, hero-row write,
  character state and aura application are not one cross-system SQL/game-state
  transaction. A database or aura failure is logged/observable but is not a
  general rollback engine.
- The hero tables have no foreign keys. An externally corrupted/orphaned hero
  row can consume a slot until an owner repairs the saved data; R2 does not
  silently garbage-collect authoritative campaign rows.
- Each force-roster page rebuilds and GUID-sorts the live matching AiBot set.
  The 200-row packet bound protects the wire, but the O(N log N) scan cost at
  the configured fleet ceiling is a performance item for later profiling.
- Outdoor relocation restores free view when `TeleportTo` rejects immediately.
  Once a transfer is accepted, later asynchronous world-load failure has no
  automatic server rollback; the client times out and the owner body remains
  wherever the ordinary transfer resolved.
- Administrator `.revive` commands intentionally bypass the paid-revive hold as
  a recovery tool. The persisted hero row remains authoritative, so an admin
  recovery should be paired with an explicit saved-state repair or the next R2
  entry may reconcile the bot back to dead.

---

## 16. Explicit non-goals for R2

- automatic creation or population of the faction army;
- faction-wide mass orders outside the existing group/order law;
- autonomous strategic AI or MangosSuperUI brain planning;
- territory capture and territory-derived hero capacity;
- standing ore/skins/herbs supplies;
- hero equipment purchasing or class-specific hero ability packages;
- dungeon ownership and faction buffs;
- capital commanders and win/loss state;
- relocation into an instance (already-streamed same-instance control remains allowed);
- a claim that the 2,500-session configuration is performance validated.

---

## 17. Final change ledger

This is the final cross-repository answer to "what did R2 change?" It lists the
semantic implementation and wire-contract files, including mixed files whose
older Commander/Tier-1 contents are not being claimed as R2. The companion
project-handbook/WIP documentation reconciliation is descriptive and is not
counted as gameplay implementation. The server wire protocol document is
included because it is the normative peer of the source-level packet contract.

### 17.1 Cross-repository summary

| Repository | Semantic R2 files | Authority added by R2 | What it explicitly does not own | Current evidence |
|---|---:|---|---|---|
| MSUIClient | 13 | Strict 839/841/843 decoding, 840/842 construction, Commander Honor/hero/force presentation, action correlation, and explicit direct-control relocation/retry orchestration. | Kill classification, Honor balances, hero truth, faction eligibility, teleport permission, possession grant, persistence. | Client build PASS; Commander/R2 clinical PASS, 130 assertions; diff check PASS. No live result is inferred. |
| VMaNGOS / SuperUI-Core | 27 | Boot-latched module truth, kill awards, atomic/persisted pools, bot-only hero lifecycle, native effects, resurrection hold, faction roster, outdoor transfer authorization, and final possession authority. | World-profile schema creation, offline snapshot preparation, UI presentation, automatic army creation. | Complete source/static review and diff check PASS; all 27 authoritative-checkout hashes match; Release/scripts build PASS through `[100%] Built target mangosd`. No install or live result is inferred. |
| MangosSuperUI | 8 | R1/R2 profile selection, validation, clean genesis, stopped-world configuration preparation, five reserved spell-row artifact transforms, and metadata. | Running match authority, live gameplay writes, service lifecycle, automatic deployment. | Solution build PASS, 0 errors/54 pre-existing warnings; World State clinical PASS; diff check PASS. |

The 13/27/8 counts exclude unrelated earlier map work, generated build output,
line-ending-only status and descriptive handbooks. In particular,
`src/shared/Progression.h` has no server content diff and is not counted.

### 17.2 MSUIClient exact R2 ledger

| File | Symbols/routes and reason for R2 delta | Path class and disabled/non-R2 behavior | Verification |
|---|---|---|---|
| `MSUIClient/Net/RtsWire.cs` **(new)** | Owns typed faction/hero/dungeon/action-result/force rows; builds exact 840 and 842 bodies; parses 839, 841 and 843 into temporary complete values; validates mode/booleans, strides, counts, GUIDs, cursor direction, page size, truncation and trailing bytes before publication. | New extension codec. It grants no action or control locally; invalid input produces no partial state. | Production source is linked into the 130-assertion Commander clinical suite; client build passes. |
| `MSUIClient/Net/Opcodes.cs` | Names `CMSG/SMSG_SUI_FORCE_ROSTER` 842/843. | Mixed opcode registry. Existing 828-841 and portal 844-847 allocations are unchanged; no stock opcode moves. | Numeric values cross-checked with both server headers and protocol docs; wire clinical passes. |
| `MSUIClient/Net/WorldSession.cs` | Serializes RTS action and bounded force-page requests through the strict codec. | Existing session transport. A request is just a wire request; it conveys no client authority. | Client build and golden body vectors pass. |
| `MSUIClient/Net/NetworkClient.cs` | Exposes only in-world action/force send facades to gameplay UI. | Existing network facade. Returns failure when no world session exists so the caller can clear pending state. | Client build; source-fence/lifecycle clinical assertions. |
| `MSUIClient/GameLoop/Scene/GameLoop.Net.cs` | Routes 843 to Commander publication, resets Commander/RTS state on terminal disconnect, and refreshes the controlled renderer from authoritative `OBJECT_SCALE_X`. | Mixed network loop. No local hero-size formula is introduced; outside R2 it continues stock entity/session handling. | Client build; wire routing and reset assertions; native scale property path reviewed. |
| `MSUIClient/GameLoop/Combat/GameLoop.Targeting.cs` | Adds player identity/name-query cache reset used by World State transitions. | Mixed targeting file. Normal target selection is unchanged; reset only discards session-scoped identity. | Logout/disconnect lifecycle assertions. |
| `MSUIClient/GameLoop/Panels/GameLoop.Logout.cs` | Clears SUI control, RTS snapshots, staged force pages, selection, pending action/relocation and identity caches on clean character-select return. | Mixed logout file. Existing logout flow remains; a later MMO login cannot inherit RTS presentation. | Lifecycle/source-fence clinical assertions. |
| `MSUIClient/GameLoop/Scene/GameLoop.CommanderMap.cs` **(mixed)** | Publishes atomic 839 snapshots; pages the selected-zone force with nonzero generation/cursor correlation; publishes only on terminal page; lazy-resolves names; renders own-faction Honor, hero and force panels; joins hero rows by full GUID; sends declare/upgrade/revive; correlates one pending action; and presents `Take Control` only for the exact selected eligible bot. | The file's fullscreen dual-continent map, shaped overlays and ordinary Commander navigation predate R2. R2 data stays separate from Tier-1 party/group units. Module bits hide/disable each feature; timeouts, malformed pages, module loss and reset discard staging/pending state. | Client build; 130-assertion R2 clinical; earlier map work independently protected by 526-row/812-chunk overlay clinical. |
| `MSUIClient/GameLoop/Scene/GameLoop.Control.cs` **(mixed)** | Adds the explicit faction Take Control state machine: initial 828, ACK 7 relocation, ordinary `NEW_WORLD` wait, entity-residency wait, final 828 retry, then the pre-existing full MMO possession binding. | Existing party possession/free view/orders/patrol/waypoints are Tier 1. R2 does not duplicate mover/combat/inventory control. Denial, timeout, grant, logout and disconnect clear relocation. | Client build; relocation ACK/state/reset clinical assertions and source review. |
| `MSUIClient/Engine/UI/CommanderMapUiLaw.cs` **(mixed)** | Adds module-bit constants and pure hero/faction-control presentation/action eligibility laws. | Existing atlas/layout/overlay laws are earlier map work. Helpers never override server row flags or 841 results. | 130-assertion Commander clinical plus overlay regression suite. |
| `tools/commander-map-clinical-check/RtsWireClinicalChecks.cs` **(new)** | Exercises golden/adversarial 839-843 bodies, all truncations, bad strides/flags/booleans/GUIDs/cursors/counts/trailing data, page limits, and 840/842 builders. | Test-only; no runtime path. | PASS as part of the 130-assertion executable suite. |
| `tools/commander-map-clinical-check/Program.cs` **(mixed)** | Adds module, hero action, force eligibility, session lifecycle, pending-correlation and source-fence assertions. | Earlier Commander layout/overlay assertions are not R2 mechanics. | PASS, 130 total assertions. |
| `tools/commander-map-clinical-check/commander-map-clinical-check.csproj` | Links the production R2 wire implementation into the focused check. | Test-only; avoids a duplicate test codec. | Restore-free clinical invocation passes. |

### 17.3 VMaNGOS / SuperUI-Core exact R2 ledger

| File | Exact symbol/hook and reason for R2 delta | Path class and disabled behavior | Verification |
|---|---|---|---|
| `docs/SUI_WIRE_PROTOCOL.md` | Extends the normative contract for module bit 4, 842/843, roster row flags/cursors, ACK 7/denial 8, bot-only heroes, native aura rows, explicit module keys and web-owned schema. | Server protocol document. Keeps 844-847 stable and documents empty disabled replies. Default slots are four; valid configured range is 1-127. | Cross-checked against client/server serializers. |
| `src/game/CMakeLists.txt` | Adds `SuiFactionControl`, `SuiHonor`, `SuiHero` source/header pairs and lists `SuiRts.h`. | Build integration only; no flags, install rules or stock source removals. | Source diff/check passes; Release/scripts compilation passes as recorded in section 15. |
| `src/game/Server/Packets/SuiControl.h` | Adds typed `WorldPackets::SuiControl::ForceRoster::ReadFromWorldPacket` for the exact 14-byte 842 body. | Extension packet path; parsing alone never scans or authorizes. | Layout matches client golden vectors. |
| `src/game/Server/Protocol/Opcodes_1_12_1.h` | Assigns 842 request and 843 reply. | Protocol registry; portal allocation is not renumbered. | Numeric cross-check passes. |
| `src/game/Server/Protocol/Opcodes.cpp` | Registers 842 logged-in on `PACKET_PROCESS_WORLD`; marks 843 server-only. | Extension dispatch only; no stock handler changes. | Registration/type pairing reviewed. |
| `src/game/Server/WorldSession.h` | Declares `HandleSuiForceRosterOpcode`. | Narrow session API addition. | Declaration/definition pair reviewed. |
| `src/game/SuperUiBots/SuiRts.h` **(mixed R1)** | Declares immutable module accessors, Honor atomic API, kill/world-entry dispatch, state/action handlers; removes reload. | Extension facade. Every module accessor is RTS-gated; no generic gameplay API is widened. | All declarations/callers reviewed. |
| `src/game/SuperUiBots/SuiRts.cpp` **(mixed R1)** | Removes core DDL/seed/row-count inference; latches explicit configuration once; normalizes negative loaded Honor to zero/dirty; applies checked `INT64_MAX`-saturating add/refund and CAS spend; loads/flushes pools; clamps flush interval; applies rates; dispatches Honor/Hero; emits populated 839; executes 840 via `SuiHero`; adds diagnostics while refusing hot reload. | Extension implementation. MMO returns before module-table reads; disabled award/action/tick paths do not mutate state; overflow cannot wrap the wire/persisted pool negative; only the existing tick/shutdown seams persist dirty state. | Full diff/static review, overflow/negative normalization review and diff check pass; Release/scripts build PASS. |
| `src/game/SuperUiBots/SuiHonor.h` **(new)** | Declares ruleset load, authoritative kill classification and narrow vanilla-HK suppression. | Extension-only and inert without active Honor. | Interface/callers reviewed. |
| `src/game/SuperUiBots/SuiHonor.cpp` **(new)** | Implements nonnegative atomic weights, actual killer/owner credit, opposing-player/bot and exclusive opposing-faction NPC classification, elite weighting, positive atomic pool award, and bot-recipient/bot-victim stock-HK suppression. | Map-thread extension. Same faction, neutral wildlife, mixed masks, pets/summons and humans in suppression all fall through to zero/stock. No hook-side DB write. | Classification branches reviewed against stock seams. |
| `src/game/SuperUiBots/SuiHero.h` **(new)** | Declares snapshots, load/tick/shutdown, death/world-entry hooks, resurrection hold, slot/fielded data and action result contract. | Extension-only; hold is false outside active persisted bot heroes. | Header/consumers reviewed. |
| `src/game/SuperUiBots/SuiHero.cpp` **(new)** | Implements five-row/native-spell validation, persisted bot-hero roster, 1-127 per-side cap, rank aura lifecycle, pending death queue, declare/upgrade/revive validation, atomic spend, saturating refund, race revalidation, graveyard full revive and synchronous state persistence. On active R2 bot entry, a physically dead character with a stale live row is marked dead before the AI update, while a persisted-dead row paired with a physically alive character is restored to the ordinary dead path by a self-kill before aura refresh. | Extension implementation. Humans/nonbots/wrong faction/offline/dead declaration/bad state/invalid config fail without mutation. Reconciliation is exact-row/character only, awards no Honor, and cannot enqueue a duplicate already-dead row. Hook-side new death only queues; the world/main thread owns synchronous writes. | Full source review for every mutation/refund and bidirectional dead-state reconciliation; Release/scripts build PASS. |
| `src/game/SuperUiBots/SuiFactionControl.h` **(new)** | Declares the sole faction party bypass, relocation results and force handler. | Extension-only; bypass false unless RTS and `control.faction_bots=1`. | Interface/consumers reviewed. |
| `src/game/SuperUiBots/SuiFactionControl.cpp` **(new)** | Implements real-human/same-team/in-world-AiBot authorization; safe malformed empty reply; read-guarded GUID-sorted live scan; 200-row cursor pages; exact 32-byte rows/flags; hero join; and non-instance owner-body relocation with free-view restoration on submission failure. Defines the WorldSession handler. | MMO/disabled replies empty and scans nothing useful; malformed requests do not scan; an instance target is eligible only when already same-map, same-instance and visible, and R2 never relocates into it; names remain stock name-query data. | Server/client stride, flags, cursor and ACK paths cross-reviewed; diff check passes. |
| `src/game/SuperUiBots/SuiPossess.h` **(mixed Tier 1)** | Adds relocation ACK 7, cross-instance denial 8 and free-view relocation helpers; removes mutable worldstate override. | Existing enum values and party possession contract stay stable. | Header/client enum pairing reviewed. |
| `src/game/SuperUiBots/SuiPossess.cpp` **(mixed Tier 1)** | Reads mode without DDL/DML; accepts party authorization or the one faction-control predicate; returns relocate/retry or instance denial for a non-visible faction bot; retires free view only after grants/relocation; parks a nonparty owner's body; rejects runtime mode changes. | With R2 gate off, same-group authorization, ordinary visibility and existing possession/release behavior remain. No new general teleport or group right exists. | `TryBegin`, relocation, parking, release and command diffs reviewed. |
| `src/game/Objects/Unit.cpp` | Adds `SuiRts::OnUnitKill` at `Unit::Kill`. | Minimal stock seam. Inert outside active modules; `DealDamage` and all formulas unchanged. | Exact hook location reviewed. |
| `src/game/Objects/Player.cpp` | Adds world-entry hero reconciliation/aura dispatch; five automatic/request resurrection guards; and group/solo bot-vs-bot vanilla-Honor filtering before divisor/reward. | Stock seam file. Predicate false preserves human/nonhero/MMO paths; only an active persisted-dead/physically-alive AiBot is self-killed on entry; no generic damage, scale or resurrection primitive change. | Every changed hunk and reconciliation path reviewed. |
| `src/game/Battlegrounds/BattleGround.cpp` | Guards end/leave auto-revives. | Stock seam; original revive executes for every unheld player. | Both sites reviewed. |
| `src/game/Handlers/BattleGroundHandler.cpp` | Guards battleground-port auto-revive. | Stock seam; inert predicate preserves ordinary behavior. | Site reviewed. |
| `src/game/Handlers/MiscHandler.cpp` | Guards corpse reclaim and pre-1.3 Molten Core trigger revive. | Stock seam; all other handler behavior unchanged. | Both sites reviewed. |
| `src/game/Handlers/NPCHandler.cpp` | Guards spirit-healer revive. | Stock seam; normal sickness/durability behavior remains for unheld players. | Entry reviewed. |
| `src/game/Spells/SpellEffects.cpp` | Guards new/legacy/self/spirit-heal resurrection effects. | Stock seam; underlying spell and `ResurrectPlayer` primitives unchanged. | Four effects reviewed. |
| `src/game/Transports/Transport.cpp` | Guards ship cross-map revive. | Stock seam; transport path otherwise stock. | Site reviewed. |
| `src/scripts/eastern_kingdoms/burning_steppes/blackwing_lair/instance_blackwing_lair.cpp` | Guards the scripted Orb of Command corpse revive before its 50-percent resurrection/bones/BWL teleport sequence. | Stock script seam; quest shortcut remains unchanged for every unheld player. | Final resurrection-bypass sweep and site review. |
| `src/game/SuperUiBots/AiBotAIBridge.cpp` | Blocks bridge resurrection commands for a held hero. | Existing AiBot extension file; ordinary bots retain bridge behavior. | Entry guard reviewed. |
| `src/game/SuperUiBots/AiBotAIMain.cpp` | Cancels an already-pending graveyard self-rez for a held hero. | Existing AiBot extension file; ordinary update loop unchanged. | Pending-state branch reviewed. |

### 17.4 MangosSuperUI exact R2 ledger

| File | Symbols/routes and reason for R2 delta | Path class and stopped/offline boundary | Verification |
|---|---|---|---|
| `MangosSuperUI/Models/WorldConfigurationModels.cs` | Defines `rts-r2-v1`, retains `rts-r1-v1`, makes R2 the new-create UI default, models Honor/control/slot/five-level fields, validates bounds and emits only managed keys/rules. | World-profile model. Legacy manifest fallback remains R1; invalid values never reach artifact preparation. Slots are 1-127. | Solution build; clinical boundary tests. |
| `MangosSuperUI/Controllers/WorldsController.cs` | Returns R1/R2 descriptors/defaults from `CreateOptions` and binds the selected structured launch configuration for create/resume. | HTTP/controller path. Accepts allowlisted model fields, never raw SQL/table/key input. | Build and clinical route/model coverage. |
| `MangosSuperUI/Services/RtsWorldCreationService.cs` | Creates clean R1/R2 parked genesis from current schema and selected artifacts; emits managed scalars/rules, zero pools/state, zero characters/bots while preserving accounts/name-list/world/core/admin selections. | Offline artifact service. Produces a parked artifact only; no service start, deploy or automatic roster population. | World State clinical R1/R2 seed assertions. |
| `MangosSuperUI/Services/RtsHeroSpellWorldStore.cs` **(new)** | Captures pre-existing build-5875 world spell rows 51001-51005 once, installs exact R2 native aura rows in the staged compressed world artifact, and restores captured originals for R1. | Staged-artifact transformer. Never connects to or mutates live WorldDatabase; owns only five reserved IDs. | Clinical install/rollback/idempotence checks. |
| `MangosSuperUI/Services/WorldStateService.cs` | Forces RTS resume through full snapshot restore preparation, stages profile-specific world artifact, applies managed CharacterDB configuration transactionally while stopped, preserves faction/hero runtime rows, validates final rules/spells and records profile metadata. | Offline World State orchestration. It prepares only; no agent/service lifecycle authority and no MMO postlude. | Build plus preservation/reset/artifact clinical checks. |
| `MangosSuperUI/Views/Worlds/Index.cshtml` | Adds R1/R2 selectors, editable configuration surfaces, review descriptions and explicit preserved/reset/spell ownership text. | Server-rendered UI only. Final operation remains owner initiated. | Razor compilation in solution build. |
| `MangosSuperUI/wwwroot/js/worlds.js` | Renders server-owned profiles and R2 fields for create/resume, mirrors validation, and previews exact managed effects/warnings. | Browser feedback only. It emits structured fields and never constructs SQL; server validation is repeated authoritatively. | Build/static diff plus World State clinical source/bound assertions. |
| `tools/worldstate-clinical-check/Program.cs` | Tests clean R2 and inert R1 seeds, schema compatibility, managed-key ownership, spell install/rollback, preservation/reset, invalid rows and 1-127 bounds. | Test-only, file/artifact level. It does not claim a live MariaDB import or gameplay result. | PASS after final 127-bound correction. |

### 17.5 Intentionally unchanged or excluded targets

| Repository/target | Why it is not an R2 implementation file |
|---|---|
| MSUIClient `AreaTable.cs`, `WorldMapOverlayCatalog.cs`, `GameLoop.WorldMap.cs`, ordinary exploration fields, atlas assets and vanilla action-bar/UI adjustments | These are the earlier Commander and normal-MMO 1.12 map/presentation correction. They are regression-tested adjacent work, not Honor/Hero/Faction Control. |
| MSUIClient movement, combat, spell, inventory and action-store implementations | R2 reaches the existing MMO direct-control stack after possession; it does not fork or weaken it. |
| VMaNGOS `src/shared/Progression.h` | Worktree and `HEAD` content hashes are identical; status is line-ending metadata only. |
| VMaNGOS `World.cpp` lifecycle calls | R1 already owned the boot/tick/shutdown seams. R2 fills `SuiRts` behind them but does not change `World.cpp`. |
| VMaNGOS `Unit::DealDamage`, generic scale/model setters and `Player::ResurrectPlayer` | Native validated auras implement power; narrow automatic-resurrection callers implement the death hold. Broad primitives remain stock. |
| VMaNGOS group/order/roster creation and name generation | Faction eligibility is not group membership, does not create bots, and does not widen mass orders. Existing name-list reuse remains untouched. |
| MangosSuperUI live DB/service/deploy paths | Profile work is stopped-world artifact/configuration preparation. Nico alone installs, deploys, swaps World States and controls processes. |

Nothing in these ledgers represents deployment or live validation. A successful
file check or build creates an artifact/evidence only; the owner-operated steps
in section 15 remain the sole path to making and validating R2 live.
