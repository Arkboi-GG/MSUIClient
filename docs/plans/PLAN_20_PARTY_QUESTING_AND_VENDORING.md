# Plan 20 — Party questing & vendoring in the CRPG/RTS MMO world

**Status:** Plan written 2026-08-25. **P1 + P2 + P3 BUILT 2026-08-25** (client
compiled + guard-checked; core compiled on the box, deploy pending on the usual
owner gates). P4-P5 not started.
**Class:** Addition (measured against owner intent, not against the 1.12 client).
**Scope:** Player-led questing and vendoring for a party of AiBot companions inside
the persistent MMO world, plus removal of the 20-quest log cap.

Companion authorities: [`CRPG_RTS_WIP.md`](../../CRPG_RTS_WIP.md),
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
  ([`GameLoop.Quest.cs:997`](../../MSUIClient/GameLoop/Panels/GameLoop.Quest.cs:997)),
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
([`GameLoop.Control.cs:601`](../../MSUIClient/GameLoop/Scene/GameLoop.Control.cs:601)),
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

`NUM_MSG_TYPES` is **858** as built (P1+P3 shipped 854-857); P4 raises it to 860
when it claims 858/859. Vendor actions: 1 `SELL_ITEM`, 2 `BUY_ITEM`,
3 `SELL_JUNK` (sweep), 4 `REPAIR` (sweep). Buyback deferred past v1.

Capability bits (`SuiCapabilityWire`, continuing bit 4):

| Bit | Name | State |
|---|---|---|
| 5 | `party-quest-facts-v1` | BUILT (P1) |
| 6 | `party-quest-acts-v1` | BUILT (P3) |
| 7 | `party-vendor-v1` | reserved for P4 |

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
  as a P3 follow-up, not silently dropped.
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

### 4.3 Party vendoring (decisions 1, 4)

Per decision 1 the vendor frame gains a **member selector**: pick a companion and
the bag grid becomes *their* bags, their purse in the money frame, and every
sell/buy/repair acts on them. It should feel exactly like vendoring your own
character, because mechanically it is — `SELL_ITEM`/`BUY_ITEM` mirror the proven
`CMSG_SUI_MEMBER_ITEM_MOVE` validation (party line, same map, no trade window,
conjured refused) and the server re-snapshots the subject afterwards.

On top of that, two sweeps: **Sell junk (all)** and **Repair all**, with a party
strip showing per member junk value / repair cost / purse before you commit.
`keepQuality` is surfaced as a party setting in the Tactics panel (decision 4) with
the brain's constant as its default.

Each bot pays from its own purse. If a repair is refused for funds, the result row
says so per member; moving coin between members is a later verb, not v1.

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
| **P1** ✅ | Quest facts wire (0x0356/0x0357, bit 5); Party Quest Log panel; merged self+companion view | You can see every companion's log without possessing |
| **P2** ✅ | Quest cap (§4.4) — shipped as config `Quests.MaxHeld`, no capability bit needed | 40+ quests held, kill/item/talk/cast credit all still land, relog clean |
| **P3** ✅ | Quest acts (0x0358/0x0359, bit 6): accept-for-party, turn-in-all with per-member reward pickers, id-based abandon; vanilla Share Quest lit up | A five-hand quest chain runs start to finish without possession |
| **P4** | Vendor (0x035A/0x035B, bit 7): per-member vendor drive, sell/buy/repair, sweeps, exposed `keepQuality` | You can clear and repair the whole party at one vendor |
| **P5** | World markers: parenthesised numeral over the vanilla `!`/`?` art | `(4)` reads correctly at nameplate range |

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
- **A timed quest accepted while all twenty slots are full** shows no countdown
  in either client (the deadline word lives in the slot). It still expires
  correctly: the server owns `m_timer` and never reads the field back. Only one
  timed quest can be held at a time (`SatisfyQuestTimed`), so this needs twenty
  slotted quests plus a timed accept to occur.
- **`SetSkill`'s quest-invalidation path** frees a slot without promoting into
  it. Rare (a skill loss invalidating a quest); the slot fills on the next
  reward or abandon.

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
5. **P4:** sell one item from a companion's bag, buy one for another, repair all with
   one bot deliberately broke — the funds refusal must surface per member.
6. **P5:** stand at a hub with five members; the numeral must track eligibility as
   members join, leave and accept.

## 7. Definition of done

You can walk a five-hand party into a quest hub, see everything all five hold,
accept and turn in for all of them with rewards you chose per member, clear their
bags and repair their gear at the vendor, and hold a hundred quests doing it —
without possessing anyone.

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
- Deploy gates are unchanged: nothing here installs, restarts, or mutates the live
  DB. Owner runs those.
