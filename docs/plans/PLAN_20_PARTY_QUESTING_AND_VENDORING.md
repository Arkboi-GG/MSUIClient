# Plan 20 — Party questing & vendoring in the CRPG/RTS MMO world

**Status:** Plan written 2026-08-25. **P1 + P2 + P3 + P4a + P5 BUILT AND
DEPLOYED, NOT SIGNED OFF.** P4b and P4c not started.

Two adversarial audit rounds have run over P1-P3 (§5e, §5f) and both found real
defects — 21 between them, every one live while the full guard suite reported
green. "Built" is the honest word and "complete" is not: the §5 phase gates are
acceptance tests that need the fleet, and **not one of them has been run.**

**Deploy state (verified 2026-08-25 23:30, not assumed).** Earlier revisions of
this line said "compiled on the box, never installed, never run live." That was
**wrong**, and it was repeated across several sessions. Checked directly:
`~/vmangos/run/bin/mangosd` carries the `SUI-LEAD` and `QUEST-HELD` markers, and
the vmangos `mangosd` process restarted at 23:25:21 running that binary — so
every phase above, P4a included, is live right now. The lesson is the one the
box memory already records: **verify the installed binary, never infer it from
whether you personally deployed.** `ls -l` build vs run, `ps` for start time, and
`strings | grep` a marker unique to the change in question.

Deployed is still not gate-verified. §6 remains the separate, unmet question.
**Class:** Addition (measured against owner intent, not against the 1.12 client).
**Scope:** Player-led questing and vendoring for a party of AiBot companions inside
the persistent MMO world, plus removal of the 20-quest log cap.

Companion authorities: [`CRPG_RTS_WIP.md`](../current/CRPG_RTS_WIP.md),
[`SYSTEM_CRPG_CONTROL_GROUPS.md`](../systems/SYSTEM_CRPG_CONTROL_GROUPS.md),
[`CRPG_RTS_MMO_PARTY_COMMAND_UI.md`](../current/ui/CRPG_RTS_MMO_PARTY_COMMAND_UI.md).

---

## 1. Problem

A bot in your control group is the only bot in the world that **cannot quest or
vendor**. Conscription (order 11) is the law — control-group membership IS
enlistment — and the enlistment fence in `AiBotAI::BridgeProcessLine` drops every
brain line but `PING`, `COMBAT_DIRECTIVE`, `LOAD_ROTATION`, `LOAD_RAID_PLAN`. The
brain stands the bot down; nothing replaces it. Meanwhile the client's whole quest
and vendor stack is hard-bound to your own character, so you cannot even *see* what
your companions hold.

Five concrete gaps:

1. **Blindness** — a companion's quest log is invisible (owner-only update fields).
2. **Agency** — you cannot make them accept, turn in, sell, buy, or repair.
3. **Coordination** — no notion of a party quest: who is eligible, who is done.
4. **Economy** — each bot has its own purse; no party-level junk/repair/bill view.
5. **Cap** — 20 quests is a 2004 UI constraint the owner does not want.

## 2. Owner decisions (2026-08-25 — binding)

1. **Vendoring is per bot, driven like your own character.** You see their bags and
   handle it yourself: sell this item, buy that one, repair. Party-wide sweeps
   (sell junk / repair all) are a convenience layer *on top*, not the mechanism.
2. **Quest rewards are per bot, chosen by you.** On a group turn-in you see every
   member's reward picker at once — the Party Inventory v3 column idiom applied to
   rewards. Default highlight follows the member's spec/talents; you can override.
3. **Real per-character quest logs, merged in the view.** Each companion owns a
   genuine vanilla quest log. The client merges them into one panel — same concept
   as the rewards above.
4. **Junk policy is exposed**, and each service can be *auto-complete* (server picks
   with the proven bot heuristics) or *you choose*.
5. **World markers get creative.** Keep the exact vanilla `!` art, font and yellow,
   and hang a parenthesised numeral over it — `(4)` when four of your five can take
   what this NPC offers. Same treatment for `?` turn-ins.
6. **Remove the quest cap.** ~100 held quests. The OG 1.12 client will still only
   ever show 20; that is accepted and explicitly not a bug.

## 3. Verified ground truth

Probed, not assumed. Client evidence is this repo; core evidence is
`~/vmangos/src` on the box (`wowvmangos@192.168.0.2`) unless marked otherwise.

### 3.1 The server-side mechanics already exist

`AiBotAIBridge.cpp` already implements, and the fleet already exercises daily:

| Verb | Payload | Behaviour |
|---|---|---|
| `QUEST_INTERACT` | `{action: accept\|complete, quest_id, npc_entry}` | `CanTakeQuest`/`CanAddQuest`, zero-objective auto-complete; turn-in runs `ChooseQuestReward` then `RewardQuest`, `TryAutoEquip`, `TryAutoEquipBags` |
| `SELL_ITEMS` | `{npc_entry, keep_quality}` | Protects quest items, gear above `keep_quality`, 40 of each consumable, non-upgrade bags |
| `REPAIR_AT_NPC` | `{npc_entry}` | `DurabilityRepairAll`, reports `not_enough_gold` |
| `TRAIN_AT_NPC` | `{npc_entry}` | Learns affordable green spells |
| `ABANDON_QUEST`, `QUERY_QUEST_STATUS`, `QUEST_CAST`, `USE_GAMEOBJECT` | — | quest-log removal, status blob, class-quest casts, node looting |

`ChooseQuestReward` is the existing spec-aware picker that decision 2's default
highlight should reuse rather than re-derive.

The commander-injection lane already exists: `SuiInjectCommandLine`
(`SYSTEM_CRPG_CONTROL_GROUPS.md` §5c) is how commander RTS orders reach a
conscripted bot past the fence.

### 3.2 The client stack is complete but self-only

- [`GameLoop.Quest.cs`](../../MSUIClient/GameLoop/Panels/GameLoop.Quest.cs) — 2568
  lines: giver frames, log, watch frame, abandon, rewards. `QuestGate` (line 57)
  gates on the *session body's* distance; `InspectQuestLog` reads
  `player.Fields.QuestLog()`.
- [`ObjectFields.cs:102`](../../MSUIClient/Net/ObjectFields.cs) —
  `PLAYER_QUEST_LOG_1_1 = 198`, three fields per slot. Owner-only: the server never
  streams these for anyone else, so a companion's log is structurally invisible.
- **"Share Quest" is drawn permanently disabled**
  ([`GameLoop.Quest.cs:997`](../../MSUIClient/GameLoop/Panels/GameLoop.Quest.cs#L997)),
  and `CMSG_PUSHQUESTTOPARTY` is absent from
  [`Opcodes.cs`](../../MSUIClient/Net/Opcodes.cs) — even though the core implements
  `HandlePushQuestToParty` and `QuestShareInfo` in full.
- Bots never answer `SMSG_QUEST_CONFIRM_ACCEPT`: `CombatBotBaseAI::OnPacketReceived`
  handles trade, resurrect, battlefield, loot-roll and teleport acks only *(read
  from the generated `docs_full` mirror — confirm against `CombatBotBaseAI.cpp`
  before relying on it)*. A vanilla quest share to a bot therefore dies silently.

### 3.3 The party-facts pattern to copy

Opcodes 850/851 (facts) and 852/853 (item move), capability bits 3 and 4
([`PortalWire.cs:99`](../../MSUIClient/Net/PortalWire.cs)): roster-edge push +
rate-limited client pull, exact-length parsing, and **the server re-pushes truth
after every mutation rather than the client guessing**
([`GameLoop.MemberFacts.cs`](../../MSUIClient/GameLoop/Scene/GameLoop.MemberFacts.cs)).
`SMSG_SUI_SNAPSHOT` already carries each bot's **coinage**
([`GameLoop.Control.cs:601`](../../MSUIClient/GameLoop/Scene/GameLoop.Control.cs#L601)),
so party purses are on the wire today.

### 3.4 The 20-quest cap is smaller than it looks

`MAX_QUEST_LOG_SIZE 20` (`QuestDef.h:34`) governs **update-field slots**, not
storage. Probe findings in `src/game/Objects/Player.cpp`:

- **Storage is already unbounded.** `mQuestStatus` is a map keyed by quest id, and
  `_LoadQuestStatus` (≈15926) *already tolerates* `slot >= MAX_QUEST_LOG_SIZE` — it
  loads the status and simply skips the UF write. `character_queststatus` is quest-id
  keyed. **Persistence needs zero changes.**
- **One gate.** `Player::SatisfyQuestLog` (≈13486) is the whole cap:
  `if (FindQuestSlot(0) < MAX_QUEST_LOG_SIZE) return true;` else `SMSG_QUESTLOG_FULL`.
- **Nine credit/query loops iterate slots, not the map** — this is the load-bearing
  work: `ItemAddedQuestCheck` (14062), `ItemRemovedQuestCheck` (14106),
  `KilledMonsterCredit` (14159), `CastedCreatureOrGO` (14216), `TalkedToCreature`
  (14287), `MoneyChanged` (14366), `ReputationChanged` (14396), `HasQuestForItem`
  (14424), `HasQuestForGO` (19688).
- **The loop bodies do not need the slot.** In `KilledMonsterCredit` the index `i` is
  used *only* to fetch `questid`; all real work runs off `mQuestStatus[questid]`.
- **`SetQuestSlot*` callers are already guarded** (`if (log_slot < MAX_QUEST_LOG_SIZE)`
  at 13156, 13174, 13191, 13283, 13418, 14006, 14612, 14640, 21686, 21744).
  `SetQuestSlot` itself asserts, so slotless quests must simply not call it.

**Hazard found while reading:** the loops take `QuestStatusData& q_status =
mQuestStatus[questid];` — `operator[]` **inserts** on a miss.

*(Corrected during the P2 build: this was first written up as undefined
behaviour. It is not — `mQuestStatus` is an ordered `std::map`, whose insert
never invalidates iterators or references. The genuine hazards are that a key
inserted mid-pass sorting after the cursor is visited in the same pass, and that
`operator[]` on a stale id silently creates a NONE-status row. Snapshotting the
ids remains correct, for determinism rather than for safety. The hazard that
actually mattered turned out to be a different one — see §5c.)*

## 4. Design

### 4.1 Wire (SuperUI extension; additive, capability-gated)

Continues the frozen numbering after `SMSG_SUI_MEMBER_ITEM_MOVE_RESULT = 0x0355`:

| Opcode | Dir | Body |
|---|---|---|
| `CMSG_SUI_QUEST_FACTS = 0x0356` | C→S | `u8 flags`, `u8 count`, `u64 subjects[count]` (count 0 = whole party **and self**) |
| `SMSG_SUI_QUEST_LOG = 0x0357` | S→C | `u64 subject`, `u8 flags`, `u16 heldCap`, `u16 count` (**13-byte header**), then per entry (**19-byte stride**): `u32 questId`, `u8 status`, `u8 entryFlags` (complete / failed / **overflow**), `u8 slot` (255 = no UF slot), `u8 objCount[4]`, `u16 itemCount[4]` |
| `CMSG_SUI_PARTY_QUEST = 0x0358` | C→S | `u8 action` (1 accept, 2 turn-in, 3 abandon), `u32 questId`, `u64 npcGuid`, `u8 count`, then per subject: `u64 guid`, `u8 rewardChoice` (255 = auto) |
| `SMSG_SUI_PARTY_QUEST_RESULT = 0x0359` | S→C | `u8 action`, `u32 questId`, `u8 count`, then per subject: `u64 guid`, `u8 result` |
| `CMSG_SUI_PARTY_VENDOR = 0x035A` | C→S | `u8 action`, `u64 npcGuid`, `u64 subject`, `u8 bag`, `u8 slot`, `u32 entry`, `u32 count`, `u8 keepQuality`, `u8 sweepCount`, `u64 sweep[sweepCount]` |
| `SMSG_SUI_PARTY_VENDOR_RESULT = 0x035B` | S→C | `u8 action`, `u8 count`, then per subject: `u64 guid`, `u8 result`, `i32 copperDelta`, `u16 itemsAffected` |
| `CMSG_SUI_GIVER_STATUS = 0x035C` | C→S | `u8 flags`, `u8 count` (1-64; **no whole-zone shorthand**), `u64 givers[count]` |
| `SMSG_SUI_GIVER_STATUS = 0x035D` | S→C | `u8 flags`, `u16 count` (**3-byte header**), then per entry (**17-byte stride**): `u64 giverGuid`, `u64 memberGuid`, `u8 status` (vanilla `DIALOG_STATUS_*`) |

`NUM_MSG_TYPES` is **862** as built (P1+P3 shipped 854-857; P5 shipped 860/861).
**858/859 stay reserved for P4 even though P5 shipped first** — they are named
here and in the client's `Opcodes.cs`, and renumbering a frozen wire to save two
indices is how a client and a core quietly stop agreeing; the two indices carry
`INVALID_PACKET` rows until P4 claims them. Vendor actions: 1 `SELL_ITEM`, 2 `BUY_ITEM`,
3 `SELL_JUNK` (sweep), 4 `REPAIR` (sweep). Buyback deferred past v1.

Capability bits (`SuiCapabilityWire`, continuing bit 4):

| Bit | Name | State |
|---|---|---|
| 5 | `party-quest-facts-v1` | BUILT (P1) |
| 6 | `party-quest-acts-v1` | BUILT (P3) |
| 7 | `party-vendor-v1` | reserved for P4 (unclaimed; P5 skipped it) |
| 8 | `party-giver-status-v1` | BUILT (P5) |

There is no `extended-quest-log-v1` bit and there will not be one: P2 shipped the
cap as the config key `Quests.MaxHeld`, and the client learns the live value from
the `heldCap` word in the quest-log header (0 = the server did not say).

**One wire, two jobs.** `SMSG_SUI_QUEST_LOG` addressed to *your own* guid carries
your overflow quests (§4.4); addressed to a companion it carries their log. The
client merges by subject. This is why the facts phase lands before the cap phase.

### 4.2 Party questing (decisions 2, 3)

- **Quest facts** push on the roster edge and on any accepted quest act; the client
  pulls on roster change and when a consumer panel opens, rate-limited exactly as
  `RequestPartyMemberFacts` does today.
- **Party Quest Log** — a new panel in the Party Inventory v3 idiom: one column per
  member, merged rows for quests more than one member holds, per-member progress
  pips. Your own overflow quests appear in your column.
- **Giver frame grows a party rail.** Standing at a giver, your normal quest frame
  gains a companion strip: portrait, eligibility verdict, and a checkbox. The accept
  button reads **"Accept for party (4)"**.
- **Turn-in shows every reward picker at once.** Per decision 2, the offer frame
  renders one reward column per member turning in, each overridable.
  `rewardChoice = 255` means "use the server's pick" — that is the *auto-complete*
  half of decision 4.
  **Not built, and the wire cannot currently support it:** this section originally
  promised each column *pre-highlighted by the server's `ChooseQuestReward`
  verdict*. `SMSG_SUI_QUEST_LOG` carries no reward field, so the board draws no
  default and a member with no click is shown as "on auto-pick" and resolved
  server-side at turn-in. The player therefore never sees which reward the server
  will choose before committing — which is the affordance decision 2 actually
  asked for. Closing it needs a reward-verdict field on the facts wire; tracked
  as a P3 follow-up, not silently dropped. *(§5f E1: the board does now draw the
  correct icon and name for every choice, so the player can at least identify
  what they are picking — which it could not do as first shipped. The
  server-verdict pre-highlight is still not built.)*
- **Server side** validates the party line, range and eligibility per subject, then
  runs the same code `BridgeHandleQuestInteract` runs (directly, or by injecting the
  bridge line through `SuiInjectCommandLine` — pick whichever keeps one code path),
  and re-pushes quest facts for every touched subject.
- **Vanilla quest share, lit up** as a cheap sub-slice: implement
  `CMSG_PUSHQUESTTOPARTY`, enable the dead Share Quest button, and teach the bot's
  `OnPacketReceived` to auto-accept. Real players in the party then get the real
  vanilla dialog, unchanged. *(Corrected while building: the primary hook is
  `SMSG_QUESTGIVER_QUEST_DETAILS`, not `SMSG_QUEST_CONFIRM_ACCEPT` — the latter
  fires only for `QUEST_FLAGS_PARTY_ACCEPT` escort quests. See §5d.)*

### 4.3 Party services (decision 1 — MECHANISM REVISED 2026-08-25)

**The member-selector design below the line is superseded.** It was written to
avoid possession, and the owner has since chosen the opposite: *"I want to be
able to interact, when I'm in direct control of party/raid bot — their vendor
interaction. In reality this also ends up being trainers, crafting, etc."* The
mechanism is now **direct control**, not a selector on your own frame.

**Why it does not already work, probed not assumed.** `SuiPossess` re-points the
session's **mover** at the bot (its own header says so) and proxies owner-only
data back for display — but `WorldSession::_player` remains your real character.
So while controlling a bot you can move it and *see* its bags (M4's snapshot),
every action opcode — `CMSG_SELL_ITEM`, `CMSG_BUY_ITEM`,
`CMSG_TRAINER_BUY_SPELL`, `CMSG_REPAIR_ITEM`, gossip — is handled with `_player`
and acts on your own character, standing wherever you left it. Owner-confirmed
symptom: *"I do see the bots bag, but I can't actually do anything since the
vendor interface currently belongs to my actual logged in char."*

The fix is **not** swapping `_player` — saving, loading, chat, social, map,
logout and anticheat all assume it. It is a `GetSuiActingPlayer()` that returns
the controlled bot while control is held and the bot passes the established party
authorization, applied to an enumerated, deliberately small set of handlers:
`GossipHello` / `GossipSelectOption` (the entry point), `TrainerList` /
`TrainerBuySpell`, `RepairItem`, `SellItem` / `BuyItem` / `BuyItemInSlot` /
`BuybackItem`, and `CMSG_LIST_INVENTORY`. **This needs no new wire at all** — the
client already sends these opcodes; only the subject is wrong.

**Crafting is NOT in that set.** Crafting is `CMSG_CAST_SPELL` with a trade
skill and runs through the whole spell system, not the NPC handlers. It is a
separate, larger slice with its own verification, recorded here so it is not
mistaken for something P4 delivers.

**Dropped from scope by the owner (2026-08-25), not deferred silently:**
party-wide auto-sell, the exposed junk policy, the sell/repair sweeps, and the
shared-heuristic refactor. Two findings behind that call are worth keeping:

- **`keepQuality` is not the junk policy.** In `AiBotAI::BridgeHandleSellItems`
  it is consulted in exactly ONE branch — misc/trade goods/recipes/lockboxes.
  Bags go by upgrade test, consumables by `AIBOT_CONSUMABLE_STALE_LEVELS`, and
  gear is *quality-blind by design* (the code says so). Surfacing it as a
  rarity slider would have been misleading UI: "keep greens" would still vendor
  a green non-upgrade sword.
- **The number 2 is hardcoded in five places** with no config key —
  `MaintenancePlanner.SellKeepQuality`, `QuestPlanner.GroupVendorSellKeepQuality`,
  an inline literal in `HubErrandPlanner`, `BotBridgeService`'s default
  parameter, and the core's own `if (keepQuality <= 0) keepQuality = 2;`.
- **`BridgeHandleSellItems` has a hard wall** refusing any unit whose session has
  a live client (`SELL_FAIL reason=real_account_protected`), and it is the ONLY
  bridge verb with one — repair, train and quest-interact have no such check.
  Whatever P4b does must leave that wall intact: it is what guarantees the brain
  can never vendor a real character.

Each bot still pays from its own purse. **P4c** adds a money-move verb between
party members — confirmed to have no existing plumbing anywhere in the core (no
`GIVE_MONEY`/`SEND_MONEY`/`TRANSFER_MONEY`; the item-move wire 852/853 moves
items only; snapshot coinage is read-only) — which is the primitive a
"pay from party funds" prompt would later compose.

### 4.4 Removing the quest cap (decision 6)

Keep `MAX_QUEST_LOG_SIZE 20` **exactly as is** — it is the vanilla update-field
layout, and every packet builder, the OG client, and anticheat depend on it. Add a
separate held-quest cap:

1. `QuestDef.h` — add `MAX_QUEST_HELD` (default 100), config-backed so it can be
   dialled without a rebuild.
2. `SatisfyQuestLog` — count held quests against `MAX_QUEST_HELD` instead of probing
   for a free UF slot. `SMSG_QUESTLOG_FULL` now means genuinely full.
3. `AddQuest` — tolerate `log_slot == MAX_QUEST_LOG_SIZE`: keep the status-map entry,
   skip the UF write. (`_LoadQuestStatus` already behaves this way.)
4. Convert the nine credit/query loops in §3.4 to iterate a **snapshotted vector of
   active quest ids** from `mQuestStatus` — never the map directly (§3.4 hazard).
5. Every `SetQuestSlot*` call stays behind its existing slot guard; slotless quests
   simply skip the mirror. Server-side `m_creatureOrGOcount` / `m_itemcount` remain
   the source of truth, which is what the bridge blob and our new wire read anyway.
6. **Slot promotion** — when a UF slot frees, promote the oldest overflow quest into
   it, so vanilla-shaped surfaces degrade gracefully. `HandleQuestLogSwapQuest`
   (`QuestHandler.cpp:489`) is prior art for moving a quest between slots.
7. **Timed quests always claim a UF slot** (the timer lives in the slot field); if
   none is free, a timed quest is refused with the normal full message rather than
   silently losing its timer.
8. **Abandon by quest id** — `CMSG_QUESTLOG_REMOVE_QUEST` is slot-indexed and cannot
   reach an overflow quest. `CMSG_SUI_PARTY_QUEST` action 3 addressed to yourself is
   the id-based path, and the client uses it for any overflow row.
9. Client: `ObjectFields.QuestLog()` stays the vanilla 20; the quest log panel merges
   it with the `SMSG_SUI_QUEST_LOG` entries for your own guid. The panel already
   scrolls. The quest watch frame keeps its vanilla five-quest limit.

## 5. Build order

| Phase | Content | Gate |
|---|---|---|
| **P1** built | Quest facts wire (0x0356/0x0357, bit 5); Party Quest Log panel; merged self+companion view | You can see every companion's log without possessing |
| **P2** built | Quest cap (§4.4) — shipped as config `Quests.MaxHeld`, no capability bit needed | 40+ quests held, kill/item/talk/cast credit all still land, relog clean |
| **P3** built | Quest acts (0x0358/0x0359, bit 6): accept-for-party, turn-in-all with per-member reward pickers, id-based abandon; vanilla Share Quest lit up | A five-hand quest chain runs start to finish without possession |
| **P4a** built | Claim party lead from a bot leader (0x035E/0x035F, bit 9) | `/claimlead` in a bot-led group makes you leader; refused by name when the leader is a real player |
| **P4b** | Acting-player redirection for gossip / vendor / trainer / repair while controlling a bot. **No new wire** | Driving a companion, you vendor and train as them, exactly as if they were your own character |
| **P4c** | Money move between party members (0x035A/0x035B or later, bit 7) | A member who cannot afford a repair can be funded without a trade window |
| **P5** built | World markers: parenthesised numeral over the vanilla `!`/`?` art (0x035C/0x035D, bit 8) | `(4)` reads correctly at nameplate range |

P1 before P2 so the overflow quests have somewhere to be seen. P5 is cheap and can
jump the queue once P1 lands.

## 5b. P1 implementation record (2026-08-25)

Client (`MSUIClient`, builds clean, `--party-quest-only` and the full
interface-wire-check suite green):

| Piece | File |
|---|---|
| Opcodes 0x0356/0x0357, 0x0358-0x035B reserved in comments | `Net/Opcodes.cs` |
| Capability bit 5 `PartyQuestFactsV1` | `Net/PortalWire.cs` |
| Wire codec, 13-byte header + 19-byte stride, exact-length parse (11 at P1; P2 added `u16 heldCap`) | `Net/QuestFactsWire.cs` |
| `SuiQuestFacts` send path | `Net/WorldSession.cs`, `Net/NetworkClient.cs` |
| Capability apply, pull, roster-hash trigger, store, `RequireQuestTemplate` | `GameLoop/Scene/GameLoop.QuestFacts.cs` |
| Inbound dispatch | `GameLoop/Scene/GameLoop.Net.cs` |
| Merged grid panel + per-member objective detail | `GameLoop/Panels/GameLoop.PartyQuestLog.cs` |
| Free-view `L` fork | `GameLoop/Hud/GameLoop.ActionBars.cs` |
| Guard | `tools/interface-wire-check/PartyQuestClinicalChecks.cs` (registered at both sites) |

Core (SuperUI-Core on the box, `make mangosd` clean, **not installed**):
opcodes 854/855 + `NUM_MSG_TYPES` 856 (`Opcodes_1_12_1.h`), `QuestFacts`
ClientPacket (`Server/Packets/SuiControl.h`), handler + INVALID_PACKET rows
(`Opcodes.cpp`), handler decl + independent pull-rate field
(`Server/WorldSession.h`), capability bit 5 (`SuiPortal.{h,cpp}`), and
`QuestLogSlotOf` / `IsQuestFactsSubject` / `SendMemberQuests` /
`PushMemberQuestsTo` / `HandleQuestFacts` + the `BroadcastRoster` hook + the
session shim (`SuiWorld/CRPG/SuiPossess.{h,cpp}`). Box
`docs/SUI_WIRE_PROTOCOL.md` updated.

Three things worth remembering from the build:

1. **`m_rewarded`, not `m_status`, means done.** VMaNGOS leaves a turned-in
   quest at `QUEST_STATUS_COMPLETE` forever. The sender gates on `m_rewarded`
   exactly as the bridge `questBlob` does; without it the panel would list every
   quest the character ever finished.
2. **`Player::FindQuestSlot` is private** and `SuiPossess` is not a friend, so
   the slot is resolved by scanning the update fields through the public
   accessor. Same result, no header surgery, and it yields the 255 sentinel for
   free once P2 lands.
3. **A latent guard bug was fixed in passing** — `GameLoop.RealPortals.cs` had a
   braceless `if` whose second, misleadingly indented line applied the
   member-facts capability unconditionally. Harmless against a current core
   (`SendAck` always writes the trailer) but a trap for every capability added
   after it; the clinical check now pins the braced form.

## 5c. P2 implementation record (2026-08-25)

Core compiled clean on the box, **not installed**. Client compiled + full
interface-wire-check suite green.

**The mechanism.** `MAX_QUEST_LOG_SIZE` stays 20 forever (the update-field
layout). The held cap is now the config key **`Quests.MaxHeld`**
(`CONFIG_UINT32_MAX_QUEST_HELD`, default 100, clamped floor 20, `.reload config`
picks it up). `Player::m_questsHeld` is the authoritative list of held quest ids
— slotted and slotless alike — and every objective-credit scan iterates it
instead of the twenty slots.

| Area | Change |
|---|---|
| Predicate | `Player::IsHeldQuestStatus` — `m_rewarded` is what means finished, not `m_status` |
| Cap gate | `SatisfyQuestLog` counts held quests, not free slots |
| Accept | `AddQuest` no longer asserts on a full slot array; the slot write is guarded, the quest is always held |
| Credit | seven scans iterate a snapshot of `m_questsHeld`; the two `const` ones (`HasQuestForItem`, `HasQuestForGO`) read it live, which is safe because they only `find` |
| Abandon | new id-keyed `RemoveQuestById`; `RemoveQuest`/`RemoveQuestAtSlot`/`FailQuest`/the bot bridge all route through it |
| Fail | `SetQuestStatus(FAILED)` hoisted out of the slot guard — a slotless quest could never be marked failed |
| Promotion | `PopulateQuestSlot` + `PromoteOverflowQuestToSlot`; a freed slot pulls up the oldest slotless quest (reward, abandon) |
| Load/save | untouched — `character_queststatus` is quest-id keyed and `_LoadQuestStatus` already tolerated slotless rows; it now also populates the held list under the same condition |
| Wire | `SMSG_SUI_QUEST_LOG` header grew `u16 heldCap` so the client prints an honest `n/100` |

**The trap was not the predicted one.** `ItemRemovedQuestCheck` has no status
check at all — the slot loop's implicit "owns a slot" filter was doing that job.
Converting it without stating the filter would have let `IncompleteQuest` drag a
finished DELIVER quest back to INCOMPLETE every time one of its items left the
bags. The conversion adds the explicit held check.

**A pre-existing bug fixed in passing.** `SendQuestUpdateAddItem` called
`SetQuestSlotCounter(slot + GetReqCreatureOrGOcount(), item_idx, …)` — adding the
objective count to the **slot** index instead of the **counter** index. For a
quest in a high slot that wrote past `PLAYER_QUEST_LOG_20_1` into
`PLAYER_VISIBLE_ITEM_1_CREATOR`, corrupting a GUID field; for a low slot it wrote
another quest's counter. Raising the cap makes high slots common, so this would
have gone from rare to routine.

**Drift guard.** The held list is hand-maintained derived state. Over-inclusion
is harmless (every consumer re-checks the status it cares about); under-inclusion
means a quest silently stops earning credit, which play-testing would not
surface. `ValidateHeldQuests()` runs on every save, repairs a missing entry, and
logs it at ERROR as `[QUEST-HELD]`. **That line appearing is a bug report, not a
success message** — it means some mutation path is not calling
`QuestHeldAdd`/`QuestHeldRemove`.

**Known limitations, deliberate:**

- **Abandoning a slotless quest** is refused with an explicit message
  (`CMSG_QUESTLOG_REMOVE_QUEST` addresses a slot and structurally cannot reach
  one). Free a slot and promotion pulls the quest up, or wait for P3's
  id-addressed act opcode. An alternative exists — slot values ≥ 20 are
  unreachable for a stock client, so the existing opcode could carry an overflow
  index from a SUI session only — deliberately not taken without owner sign-off.
- ~~**A timed quest accepted while all twenty slots are full** shows no
  countdown in either client.~~ **Fixed in §5f.** This was written up as a
  deliberate limitation; it was not one — §4.4(7) specifies refusing such a
  quest and that had simply not been implemented. `CanAddQuest` now refuses a
  timed quest with no free slot, with the ordinary log-full message.
- **`SetSkill`'s quest-invalidation path** frees a slot without promoting into
  it. Rare (a skill loss invalidating a quest); the slot fills on the next
  reward or abandon.
- **"Oldest overflow first" does not survive a relog.** `m_questsHeld` is
  rebuilt from `character_queststatus`, which has no acceptance-order column to
  sort by, so after a login the promotion order is *lowest quest id* rather than
  *oldest accepted*. §5f adds `ORDER BY quest` so it is at least deterministic
  instead of storage-engine dependent. Restoring true "oldest" needs a schema
  column and is not worth one.

## 5d. P3 implementation record (2026-08-25)

Core compiled clean on the box, **not installed**. Client compiled + full
interface-wire-check suite green (new `PartyQuestActs` check).

| Piece | Where |
|---|---|
| Act wire 856/857, capability bit 6, `NUM_MSG_TYPES` 858 | `Opcodes_1_12_1.h`, `SuiPortal.{h,cpp}`, client `Opcodes.cs`/`PortalWire.cs` |
| Codec + 12-code result vocabulary | `Net/PartyQuestWire.cs` |
| `SuiPossess::HandlePartyQuest` — per-subject authorize, act, answer | `SuiWorld/CRPG/SuiPossess.cpp` |
| Client acts, result reporting, id-addressed abandon, share push | `GameLoop/Scene/GameLoop.PartyQuestActs.cs` |
| Companion rail + per-member reward board | `GameLoop/Panels/GameLoop.QuestPartyRail.cs` |
| Shared-quest accept for AiBots | `SuperUiContent/SuiBots/AiBotAIMain.cpp` |
| Guard | `tools/interface-wire-check/PartyQuestActsClinicalChecks.cs` |

**No whole-party shorthand.** `CMSG_SUI_QUEST_FACTS` treats an empty subject
list as "everyone"; the act wire refuses it. Reading a companion's log is
harmless, acting on their behalf is not, and who is about to act must be visible
to the player who ordered it. It also makes the button's count honest.

**Range: the requester interacts, the companions are shared with.** The
requester answers to `INTERACTION_DISTANCE` (5 yd) because they are the one
talking to the NPC; companions must be within `QUEST_SHARE_DISTANCE` (14 yd) of
the *requester* — vanilla's own rule for "close enough to be shared with".
Measuring five companions against a 5-yard radius on one NPC would have made the
feature unusable in practice, and 15.0f (the bot bridge's grid radius) has no
authority behind it.

**The accept path mirrors the real handler, not the bot bridge.** Notably it
casts the quest's source spell, which the bridge's accept path omits — a real
bug in the fleet's questing, fixed here by not copying it.

**Auto-pick is refused for your own character** (`NEEDS_CHOICE`). The
spec-aware chooser belongs to a bot AI; a real player has no such thing, and the
client always has a picker for itself.

**UI parity is preserved by construction.** The rail is a separate window
starting at x = 390, beside the 384-wide quest frame: no vanilla element moves
and the frame's parity element tree is untouched. It wears the SuperUI dialog
skin rather than FrameXML parchment — dressing commander furniture as vanilla
art would invite a parity claim it could never satisfy — and it does not draw at
all while a UI-parity proof is armed.

**Two premises corrected while building, both mine:**

1. **Bot sessions DO receive SMSGs.** `WorldSession::SendPacket` diverts a
   socket-less session into `GetBot()->ai->OnPacketReceived`, and
   `CanProcessPackets` readmits bot sessions on the return leg. The packet path
   was live all along; what was missing was an answer.
2. **The share hook belongs on `SMSG_QUESTGIVER_QUEST_DETAILS`**, not
   `SMSG_QUEST_CONFIRM_ACCEPT` as §4.2 originally sketched — the latter fires
   only for `QUEST_FLAGS_PARTY_ACCEPT` escort quests. And the reply must be
   QUEUED, not a direct handler call: the send is synchronous inside
   `HandlePushQuestToParty`, which sets the share info *after* sending, so
   accepting inline would kill the sharer's confirmation and strand the bot at
   BUSY for every later share.

**Client-side sharable gate.** `HandlePushQuestToParty` does not check
`QUEST_FLAGS_SHARABLE` — it forwards anything and then refuses every accept — so
the Share Quest button owns that test. The client's `QuestTemplate` was
discarding the flags word; it now keeps it.

## 5e. Audit remediation (2026-08-25)

A five-auditor adversarial pass over P1-P3 returned FAIL on P2 and P3. Twelve
distinct defects after deduplication; the three criticals were re-verified by
hand in source before being accepted. All twelve are now fixed. Core recompiled
clean, client builds clean, full interface-wire-check suite green.

| # | Defect | Fix |
|---|---|---|
| D1 | Reward-panel party turn-in sent `npcGuid = 0` — the roster branch read only two of the three panel records, and both are null by construction there. Every companion refused with NO_QUEST on the most common quest shape. | The giver is resolved **once**, from all three records, before the branch; both branches take it as a parameter. A zero giver now returns early instead of reaching the wire. |
| D2 | `IsHeldQuestStatus` treated `m_rewarded` as final, but `AddQuest` never clears it, so a re-accepted REPEATABLE quest was pruned from `m_questsHeld` on the next save and silently stopped earning credit. | The predicate now takes the quest id and mirrors `_LoadQuestStatus`'s condition exactly (`!m_rewarded \|\| IsRepeatable()`), and the load path was changed to **call the shared predicate** so the two cannot drift apart again. |
| D3 | `AreaExploredOrEventHappens` wrote `m_explored` inside the slot guard, so an exploration/event quest held past slot 20 could never satisfy `CanCompleteQuest`. | Only the field mirror is slot-gated now; the status write is not. Uses `find()` rather than `operator[]` so a quest the character does not hold cannot be conjured into existence. |
| D4 | The P2 "bug fix" to `SendQuestUpdateAddItem` moved the objective count onto the counter index, which reaches 7 — index 4 clobbers the state byte and 5+ shifts off the word. `PopulateQuestSlot` copied it. | **Both item-counter mirrors removed.** A quest slot has four counter indices and the creature/GO objectives own them; vanilla's own load path writes no item counters for that reason, and the 1.12 client reads item progress from the player's bags. |
| D5 | Nothing refreshed the self-addressed half of the facts wire, so a quest accepted past slot 20 was invisible in the player's own log until an unrelated roster edge. | Rate-limited pulls added on ordinary accept, ordinary turn-in, and opening the quest log. |
| D6 | Only the requester was liveness-checked; a companion needed only same-map and distance, so a corpse was a legal subject and `RewardQuest` would hand it XP and items. | `IsPartyQuestInRange` now requires companions to be alive and not in flight. |
| D7 | The Progress panel offered "Turn in all", which forced auto-pick for everyone — making the per-member reward board skippable via a button shown first. | Progress is no longer a turn-in surface; it shows the roster and says "Turn in on the reward page." |
| D8 | The acts capability apply site, the acts dispatch and both panels' draw calls were unpinned — deleting any one killed P3 with both guards still green. The rate-limit assertion pinned a symbol name, not the behaviour. | All four wiring lines pinned; the rate-limit assertion now pins the comparison expression. D1's giver resolution and D7's rule are pinned too. |
| D9 | `ValidateHeldQuests` ran *before* the `QUEST_DELETED` erase, leaving dangling ids for a save cycle that `operator[]` then resurrected; the prune path logged at DETAIL, which this box suppresses. | Reconcile moved to the end of `_SaveQuestStatus`; the prune logs at ERROR with a message that says what it means. |
| D10 | Human party members are never described by the server (the push is AiBot-only), but the panel drew them a column whose em-dash was indistinguishable from "holds no quests". | "Not told" renders as `?` in a dimmer tint, with a one-line legend under the grid. The panel no longer asserts a fact the server never sent. |
| D11 | Kill and collect objectives share an index in vanilla, and 89 quest/index pairs in the shipped DB use both — the panel treated the index as either/or and discarded the item counter the server had sent. | Two independent progress banks per cell; the objective list emits both lines; the objective total counts both. |
| D12 | Plan doc drift: an 11-byte quest-log header (13 as built), a stale `NUM_MSG_TYPES` and capability table, and a promised server-supplied reward pre-highlight that was never built. | Tables corrected. The reward pre-highlight is now recorded as **not built, with the reason** — the facts wire carries no reward field — rather than left reading as delivered. |

**Two mistakes I made while fixing, caught before they compiled:** the D1 edit
first called `ImGui.End()` on a path where `Begin` had not run, which would have
unbalanced the ImGui stack; and the D11 edit was anchored in the wrong file. Both
corrected in place.

**The honest read on the guard suite:** it is stronger than it was, but it is
still a client-side round trip. It cannot catch a server/client wire divergence,
because it builds its fixture with the client's own writer. The byte agreement
recorded in §5b and §5d was established by hand. A fixture generated from the
server's field order is the real fix and is not built.

## 5f. Second audit round — remediation (2026-08-25)

An independent auditor re-reviewed P1-P3 after §5e and declined to sign off. Nine
findings; all nine were re-verified from source before being accepted (client
locally, core on the box, and the objective-overlap count re-run against the
shipped world DB), and all nine are fixed. Core recompiles clean, client compiles
clean, full interface-wire-check suite green.

| # | Defect | Fix |
|---|---|---|
| E1 | The per-member reward board drew a **blank grey box for every reward and named none of them**. `QuestRewardIconPath` returned an empty path whenever the offer carried a display id — which `SMSG_QUESTGIVER_OFFER_REWARD` always does — and did the exact opposite of the comment at its own call site. With no icon, no name and only a generic hover tip, owner decision 2's picker was undifferentiated squares. | Icon resolution now mirrors `DrawQuestItemRow` exactly (display id, then the item's own icon, then the question mark). The board grew a left gutter that **names each choice row once** — every member is offered the same list, so the name belongs to the row, not to 5x6 cells — and each cell's tooltip names the item and who it is for. |
| E2 | `SendMemberQuests` skipped every `m_rewarded` quest unconditionally, while `IsHeldQuestStatus` (post-§5e) holds a re-accepted **repeatable** quest. A third copy of a predicate whose own comment forbids copies: the quest earned credit server-side and was structurally unshowable to the client. | The sender now calls `Player::IsHeldQuestStatus` — the shared predicate, which already owns both halves of the rule. Three copies collapse to one. |
| E3 | §5e's D11 was fixed **only in the party grid**. `QuestObjectiveLine` returned out of the kill branch, so in the player's own quest log *and quest watch frame* a collect objective sharing an index with a kill was dropped — and the watch frame derives its objectives/complete tally from the same loop, so it **coloured a quest title complete while an unfinished objective was outstanding**. Re-run against the shipped DB: 89 mixed pairs across 83 quests. | Replaced with `QuestObjectiveLines`, which yields every line an index produces. Both consumers iterate it; the watch frame's line budget is now counted per line. Also: `ObjectiveText[i]` belongs to the *creature* objective, so when an index carries both, the collect line falls back to the item's name instead of repeating the kill's label — corrected in the party grid too. |
| E4 | §5e's D5 was lossy. A throttled `RequestPartyQuestFacts` returned false and was **forgotten**, and nothing on the server pushes after an ordinary accept or turn-in — so "turn in, then accept the follow-up from the same NPC", which lands inside the 2s window every time, silently lost its refresh. Ordinary abandon had no refresh at all, and `MergedOwnQuestLog` re-adds any cached entry lacking a slot, so the abandoned quest **reappeared as a phantom overflow row** whose Abandon button bounced with `NO_QUEST`. | The limiter now *defers* rather than drops: a pending pull is recorded and flushed by `UpdatePartyQuestFacts` the moment the window allows. Abandon drops the cached row (`ForgetOwnQuestFact`) and re-pulls. |
| E5 | Nothing pushes companion quest progress. `SendMemberQuests` is reached only by roster edges, explicit pulls and party acts — so §6 step 2, "kill a bot's mob and watch the pushed counter move", could not pass however long you watched. `MemberQuestLogAge` existed **with no consumer at all**, so the grid presented arbitrarily stale counters as if they were live. | A poll, scoped honestly and labelled as one: the facts refresh every 2.5s **only while a surface that renders them is on screen** (the party quest log, or the companion rail at a giver, where a stale "on it" verdict sends you to the wrong NPC). The panel now prints how old its facts are. A true credit-driven push needs a core hook and is recorded below as not built. |
| E6 | P3 carried a completion mark while decision 2's server-supplied reward pre-highlight was explicitly unbuilt. | Phase marks now read "built", and §5 says plainly that no gate has been run. E1 closes the *identification* half of decision 2; the pre-highlight half remains not built and is still recorded as such in §4.2. |
| E7 | P2 carried a completion mark while §4.4(7) — refuse a timed quest when no slot is free — was unimplemented and written up in §5c as a "deliberate limitation". It is not deliberate; it is the spec, skipped. The player-facing failure is a quest counting down with no countdown in any client, discoverable only by failing it. | `CanAddQuest` refuses a timed quest with no free update-field slot, with the ordinary log-full message. §5c's bullet is struck. |
| E8 | The party turn-in validated the reward index against the **array bound** (`QUEST_REWARD_CHOICES_COUNT`) and not against the quest, so an index inside the array but past this quest's real choice count reached `RewardQuest`, where a zero `RewChoiceItemId[reward]` rewards the quest and silently hands over nothing. | Now also rejected when the index is past `GetRewChoiceItemsCount()`. Note this is **stricter than vanilla's own handler**, which has the same array-bound-only check at `QuestHandler.cpp:411` — so this is hardening, not a P3 regression being repaired. |
| E9 | "Oldest overflow first" does not survive a relog: `character_queststatus` has no acceptance-order column and the login query had no `ORDER BY`, leaving promotion order at the storage engine's discretion. | `ORDER BY quest` added, making it deterministic (lowest quest id). True "oldest" would need a schema column; §5c now states what the order actually is instead of promising what it is not. |

**Every one of these was live while both party-quest guards and the full suite
reported green** — the same blind spot §5e named, now with five more instances.
Each fix above is pinned, and each pin was **negative-tested**: the fix was
mutated in a way that still compiles, the suite was confirmed to fail on that
specific assertion, and the mutation reverted. D11 had never been pinned at all,
which is exactly why it survived a round as a half-fix.

**Still not built, deliberately and on the record:**

- **The server-supplied reward pre-highlight** (decision 2). Unchanged from §5e:
  `SMSG_SUI_QUEST_LOG` carries no reward field. E1 means the player can now *see
  and identify* every choice; they still cannot see which one the server would
  pick on auto. Needs a reward-verdict field on the facts wire.
- **A credit-driven server push.** E5 ships a scoped client poll, which is what
  makes §6 step 2 pass. The push-shaped fix — a dirty flag on quest progress,
  coalesced out of the bot's existing AI tick — is the architecturally correct
  one under §3.3's law, and it is not built. The poll is labelled as a poll in
  the code and the panel states its own staleness, so nothing here claims to be
  a push.
- **A server-generated wire fixture.** §5e's closing caveat stands untouched: the
  suite still builds its fixture with the client's own writer and cannot catch a
  server/client wire divergence.

## 5g. P5 implementation record (2026-08-25)

Core compiled clean on the box, **not installed**. Client compiles clean; new
`PartyGiverStatus` clinical check, full interface-wire-check suite green.

| Piece | Where |
|---|---|
| Opcodes 860/861, `NUM_MSG_TYPES` 862, 858/859 left reserved | `Server/Protocol/Opcodes_1_12_1.h`, `Opcodes.cpp`, client `Net/Opcodes.cs` |
| Capability bit 8 (bit 7 left unclaimed for P4) | `SuiPortal.{h,cpp}`, client `Net/PortalWire.cs` |
| Wire codec, 3-byte header + 17-byte stride, exact-length parse | `Net/GiverStatusWire.cs` |
| `GiverStatus` ClientPacket, 1-64 givers, exact-length | `Server/Packets/SuiControl.h` |
| `GiverStatusFor` + `HandleGiverStatus` + session shim | `SuiWorld/CRPG/SuiPossess.{h,cpp}` |
| Capability apply, store, marker-driven pull, dispatch, count law | `GameLoop/Scene/GameLoop.GiverStatus.cs` |
| Family / numeral law | `Engine/UI/QuestMarkerUiLaw.cs` |
| The draw | `GameLoop/Hud/GameLoop.QuestMarkers.cs` |
| Guard | `tools/interface-wire-check/PartyGiverStatusClinicalChecks.cs` (registered at both sites) |

**§5 called this phase cheap. It is not, and the reason is worth recording.**
The estimate assumed the client could count eligible members itself. It
structurally cannot: vanilla's `SMSG_QUESTGIVER_STATUS` answers for the asking
session and nobody else; eligibility turns on level, prerequisites, race, class
and exclusive groups the client never receives for a companion; and the client is
never told which quests an NPC offers or ends, so it cannot even derive the
turn-in half from the P1 facts it already holds. A guessed numeral would be a
wrong number over an NPC's head, which is worse than no number — so P5 is a full
wire phase, the same shape as P1.

**The verdict is vanilla's own, per member.** `GiverStatusFor` walks exactly the
path `CMSG_QUESTGIVER_STATUS_QUERY` walks — the `sScriptMgr` hook first, the core
`WorldSession::GetDialogStatus` as the fallback when the script returns
`DIALOG_STATUS_UNDEFINED`, and the same hostility gate — so a scripted
questgiver answers identically for a companion and for you. This works only
because `GetDialogStatus` takes an explicit `Player*` rather than reading
`_player`; it was checked, not assumed.

**Counting rules, and why.**

- `DIALOG_STATUS_REWARD_REP` (4) draws as a blue question mark but *means*
  "available to take". It counts with `AVAILABLE` (5), not with the turn-ins it
  resembles. Counting by appearance would put a wrong number over every
  repeatable questgiver in the game.
- Our own verdict is read from the vanilla `_questStatuses` we already hold, not
  from this wire, so the numeral and the marker beneath it can never disagree
  about us. The server still emits our own row — as the marker that says "this
  giver was answered for", without which a giver whose whole party dropped to
  NONE would be absent from the reply and the client would show the previous
  answer forever.
- A grey marker still gets a numeral when someone else has business there. That
  is the case that earns the feature: the alternative is walking past.

**Additive by construction.** The numeral only ever appears above a marker
vanilla already drew. It never adds a marker, never moves or restyles one, and
`(1)` — a count that is only us — draws nothing, because that is what vanilla
already says by drawing the marker at all. Solo play is therefore pixel-identical,
and the numeral does not draw while a UI-parity proof is armed.

**Known limitation, deliberate:** where your own dialog status is
`DIALOG_STATUS_NONE`, vanilla draws no marker, so there is nothing to hang a
numeral on — a questgiver only your companions can use is still invisible to
you. Fixing that means *creating* world markers vanilla does not draw, which is
a different feature from decision 5's "keep the exact vanilla art and hang a
numeral over it", and is not taken without owner sign-off.

**Byte agreement** across the P5 pair was established by hand, as for 854-857:
the request is `2 + 8n` on both sides (the same shape P1 already proves on the
wire), and the answer is `3 + 17n`. §5f's caveat is unchanged — the guard suite
builds its fixture with the client's own writer and cannot catch a divergence
here. Each new assertion was negative-tested (mutate compile-cleanly, confirm the
specific assertion fails, revert).

**Not verified live.** Like P1-P3, this has never run against a running server.

## 5h. P4a implementation record (2026-08-25)

Core compiled clean on the box, **not installed**. Client compiles clean; new
`PartyLead` clinical check, full interface-wire-check suite green.

| Piece | Where |
|---|---|
| Opcodes 862/863, `NUM_MSG_TYPES` 864, 858/859 still reserved | `Opcodes_1_12_1.h`, `Opcodes.cpp`, client `Net/Opcodes.cs` |
| Capability bit 9 | `SuiPortal.{h,cpp}`, client `Net/PortalWire.cs` |
| Wire codec, 9-byte request / 10-byte result, exact-length | `Net/PartyLeadWire.cs` |
| `PartyLead` ClientPacket | `Server/Packets/SuiControl.h` |
| `HandlePartyLead` + session shim | `SuiWorld/CRPG/SuiPossess.{h,cpp}` |
| Capability apply, claim, result reporting | `GameLoop/Scene/GameLoop.PartyLead.cs` |
| `/claimlead` alias law + dispatch | `Engine/UI/PartyLeadCommandLaw.cs`, `GameLoop/Panels/GameLoop.Chat.cs` |
| Guard | `tools/interface-wire-check/PartyLeadClinicalChecks.cs` (registered at both sites) |

**The problem was real and had no existing way out.** `BridgeHandleFormGroup`
has bots create their *own* groups — `Group::Create` with the bot as leader, then
`AddMember` — so "an AiBot holds the lead" is a state the fleet produces by
design. Vanilla then offers no exit: `HandleGroupSetLeaderOpcode` gates on
`group->IsLeader(GetPlayer()->GetObjectGuid())` before promoting anyone, and
refuses `player == GetPlayer()` outright. Nothing in `SuperUiContent/` has ever
called `ChangeLeader`. A commander in a bot-led group could neither promote
themselves nor rearrange the party.

**Only ever from a bot.** The claim is refused when the current leader is a real
player, by name (`the leader is a real player — ask them`). A verb that seizes
lead from a human is a griefing verb whatever the intent behind it. The test is
`IsMemberFactsSubject` — the established "an AiBot in my group I may act on"
predicate, *reused rather than restated*, because a second copy of an
authorization rule is how the two quietly stop agreeing (§5f E2 is that mistake
twice over).

**v1 claims for yourself only.** Promoting one bot over another has its own
failure modes and is not smuggled in; the wire carries an explicit subject so
that stays expressible later without a format change.

**Vanilla's handler is untouched**, and the verb is a SuperUI slash command with
its own alias law. It is deliberately NOT in `GroupSlashCommandLaw` (a parity
surface listing vanilla's own GlobalStrings aliases) and NOT a row in the unit
popup (which mirrors Benilla's `UnitPopup.xml` exactly) — either would make a
parity table assert a command the 1.12 client never had. Both exclusions are
pinned by the guard.

Every refusal carries a reason the player can read; the guard asserts that no
result code falls through to "unknown". Each new assertion was negative-tested
(mutate compile-cleanly, confirm the specific assertion fails, revert).

**Not verified live**, like everything else in this plan.

## 6. Test protocol

Written before the change, per the plan law. This needs a live server and the real
fleet — **never vmangos partybots**.

1. **Wire guards (offline, every phase):** new
   `tools/interface-wire-check/PartyQuestClinicalChecks.cs` and
   `PartyVendorClinicalChecks.cs` following the existing clinical-check idiom —
   exact-length parse acceptance, off-by-one rejection, oversized subject-count
   rejection, unknown-action rejection, capability-absent refusal.
2. **P1:** form a party with four fleet bots holding known quests. Pull facts; the
   panel must match `.quest` output for each bot on the box. Kill a bot's mob and
   watch the pushed counter move.
3. **P2:** accept 40 quests on one character. Verify (a) UF slots hold 20 and the OG
   client shows exactly those, (b) kill credit lands for an overflow quest, (c) an
   item turn-in for an overflow quest completes, (d) relog preserves all 40,
   (e) abandoning a slotted quest promotes an overflow quest into the free slot.
4. **P3:** accept-for-party at a giver with one member ineligible — the ineligible
   member must be refused *by name*, not silently dropped. Turn in with three
   different reward choices and verify each bot equips its own pick.
5. **P4a:** in a bot-led group, `/claimlead` makes you leader and the party frame
   updates; you can then uninvite and rearrange. In a group led by a REAL player it
   is refused by name, not silently. Solo, it says you are not in a group.
   **P4b:** take control of a companion, open a vendor, and sell/buy/repair — every
   act must land on the COMPANION and none on your own character (check both
   inventories and both purses). Release control mid-vendor and confirm the frame
   closes rather than silently re-pointing at you.
   **P4c:** fund a broke companion from another member and repair.
6. **P5:** stand at a hub with five members; the numeral must track eligibility as
   members join, leave and accept. Specifically: (a) a giver only some can take
   reads the right count, (b) a repeatable giver counts as *take*, not turn-in,
   (c) accepting for the party drops the `!` numeral and raises the `?` one,
   (d) solo — or with the capability absent — no numeral is drawn anywhere,
   (e) a member walking out of the party is dropped from the count on the next
   pull, not left stale.

## 7. Definition of done

You can walk a five-hand party into a quest hub, see everything all five hold,
accept and turn in for all of them with rewards you chose per member, hold a
hundred quests doing it, and take the lead back from a bot whenever you want to
rearrange the group — **without possessing anyone**. That clause still holds for
everything in P1-P3 and P5, which is the questing half and is where it mattered.

**It no longer holds for services, and that is a deliberate owner change**
(2026-08-25), not an unmet goal: vendoring, training and crafting are now reached
by *taking direct control of the companion and playing it as your own*, which is
the simpler mechanism and the one the owner asked for. §4.3's member-selector was
the design that existed to satisfy the old clause; it is superseded. Anyone
auditing this plan should not record the selector as still-owed work.

## 8. Risks and open questions

- **The map-iteration hazard (§3.4)** is the one place a careless edit becomes
  memory corruption instead of a visible bug. Snapshot ids first, always.
- **Bot `OnPacketReceived` whitelist** was read from the generated docs mirror, not
  source. Confirm before building the share slice.
- **Scripted quests** (`ScriptMgr` gossip/quest hooks) may assume a UF slot exists.
  Sweep `GetQuestSlot`/`FindQuestSlot` callers outside `Player.cpp` during P2.
- **Anticheat / packet builders** must never see a quest id outside the 20 UF slots;
  keeping `MAX_QUEST_LOG_SIZE` frozen is what buys that, so do not "tidy" it.
- **Bot quest-log growth** — fleet bots run their own planners and would now also
  accept quests from you. If a conscripted bot's log fills, the planner's shelving
  logic (`MaintenancePlanner`) may fight the commander's picks after dismissal.
  Worth a look during P3; not a v1 blocker.
- **Deploy gates:** this plan's work does not install, restart or mutate the live
  DB — the owner runs those, and does so often. See the status header: assuming
  "not installed" because you did not install it yourself was wrong twice.
- **OPEN (2026-08-25, unresolved — owner raised, agent guessed wrong):** P4a's
  verb is `/claimlead`, an MSUIClient slash command, so **a stock 1.12 client
  cannot reach it.** The owner asked for something a 1.12 player can *say in
  chat* — "give me lead" — "like the other stuff I put in chat".

  Two candidate mechanisms were found; the owner has confirmed neither, and the
  reference they meant was **not located**:

  1. **Natural-language party chat.** `AiBotAIMain.cpp:353` already intercepts
     `SMSG_MESSAGECHAT` for SAY / WHISPER / PARTY, filters the bot's own echo,
     and forwards every message to the C# brain as a `CHAT_RECV` event with
     sender name and guid. Bots therefore already *hear* everything said in
     party chat; only the parsing side would be new. This is the reading that
     best fits "a chat message they can say".
  2. **A GM dot-command** — `.sui lead`, beside the existing
     `possess` / `release` / `worldstate` / `rts` entries in `suiCommandTable`
     (`Chat/Chat.cpp:95`, registered at 1247, `SEC_ADMINISTRATOR`). This is what
     the agent assumed and started toward; the owner stopped it.

  **What was searched and did not turn it up:** `BotLogic/Core/Chat/` in the
  brain mirror is the LLM persona/conversation system (`ChatCoordinator`,
  `PersonaService`, `UrgeScorer`) and holds no command vocabulary; greps for
  obvious command phrases across the brain and the client found nothing. Either
  it lives outside `~/botwatch/src-mirror`, or on the client side, or it is a
  pattern not yet written down. **Ask the owner for one concrete example of a
  phrase they type that a bot reacts to, and find the handler from that** —
  rather than inventing a third mechanism.
