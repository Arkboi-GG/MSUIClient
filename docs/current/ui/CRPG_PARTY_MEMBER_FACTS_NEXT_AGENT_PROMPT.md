# Party member facts — next-agent handoff

> **STATUS: BUILT 2026-08-25** (same-day follow-up session). Everything below
> was implemented as specified; this document is now the implementation record.
> What exists:
>
> - **Server** (compiled clean on the box, NOT installed/restarted):
>   `CMSG_SUI_MEMBER_FACTS = 850` / `SMSG_SUI_MEMBER_SPELLS = 851` (848/849
>   left reserved for the rotation pair), capability bit 3
>   (`CAPABILITY_PARTY_MEMBER_FACTS_V1`) in the control-ACK trailer,
>   `SuiPossess::HandleMemberFacts` (exact-length pull, 1/s per-session rate
>   limit, empty subjects = whole party), `SendMemberSpells` (SendInitialSpells
>   filter, u32 ids), `SendSnapshot` reused verbatim, roster-edge push via
>   `BroadcastRoster → PushMemberFactsTo`. Authorization exactly as specified:
>   real player session, AiBot subject, SAME group/raid; faction authority
>   insufficient. `docs/SUI_WIRE_PROTOCOL.md` on the box updated.
> - **Client** (compiles clean; all four guard tools green):
>   `ApplySuiSnapshot` gate lifted (party/raid members accepted via
>   `IsPartyMemberFactsSubject`, non-party still dropped with the honest log,
>   possessed-body rebuild stays controlled-only), new
>   `GameLoop/Scene/GameLoop.MemberFacts.cs` (capability, roster-fingerprint
>   pull, `ApplySuiMemberSpells` → `SeedSpells` + `PopulateBotBar` + BotSpells
>   cache), `Net/MemberFactsWire.cs` (exact-length wire law), pulls on Party
>   Inventory / Party Tactics open, "possess once" labels + "?" -well tooltips
>   retired for party members under the capability.
> - **Verification:** `interface-wire-check` extended with
>   `PartyMemberFactsClinicalChecks` (`--party-member-facts-only`); the
>   snapshot-gate law is fenced. Dirty-hook re-send deliberately deferred as
>   allowed below.
>
> Remaining: owner deploy (mangosd + MangosSuperUI together, client rebuild)
> and the owner-run live proof with SuperUI bots at the bottom of this doc.

**Date:** 2026-08-25
**Owner decision:** party/raid members must have their bags and skills available
to the client **without possession**. Non-party faction bots (the ones you only
issue commands to) get no such entitlement — commands only. The Tier-2 RTS
world will have different rules; explicitly out of scope here.
**Rule of thumb:** party = full facts; faction = orders.

This document is self-contained: read it plus the two referenced docs and you
can build the feature without this conversation.

## Related

- [`CRPG_RTS_MMO_PARTY_COMMAND_UI.md`](./CRPG_RTS_MMO_PARTY_COMMAND_UI.md) —
  design, build-state table (read it first: most of the client is BUILT), and
  the original "Required SuperUI-Core hooks" contract this feature realizes.
- [`../../systems/SYSTEM_CRPG_CONTROL_GROUPS.md`](../../systems/SYSTEM_CRPG_CONTROL_GROUPS.md)
  — order wire (§5, §5b formations/sheath, §5c conscription).
- [`../CRPG_RTS_WIP.md`](../CRPG_RTS_WIP.md) — dated session records
  and deploy state.

## Why the client is already 90% ready

The 2026-08-24/25 sessions built the entire consuming UI. It runs today on
possession-synced data and flips to always-available automatically once the
server pushes facts for party members:

- **Per-guid retention already exists.** `GameLoop.Control.cs`:
  `_suiSnapshotItemsByBot` / `_suiSnapshotAtByBot` keep each bot's inventory
  snapshot after release (synthetic item entities + guid wiring on the bot's
  own `PLAYER_INV_SLOT`/`CONTAINER_SLOT` fields). `ApplySuiSnapshot` parses the
  wire, `PurgeSuiSnapshotFor(bot)` replaces one bot's data, session teardown
  purges all.
- **Per-guid ability stores already exist.** `_actionsByGuid` /
  `ActionsFor(guid)` (`PlayerActions`: known spells, 120-slot bar, cooldowns);
  `EnsureBotBarForViewing` seeds from the `botbars.json` `BotSpells` cache;
  `ResolveBotBar`/`PopulateBotBar` lay out bars from a class layer + known
  spells.
- **Every consumer surface is built and honest**: party-row quick slots ("?"
  wells when unknown), free-view console unit card, Party Tactics panel,
  Party Inventory (BG3-style columns: equipment public/always, bags
  live | synced-Xs-ago | possess-to-sync).

The one client-side GATE to lift: `ApplySuiSnapshot` drops any snapshot whose
`source != ControlledGuid` (a deliberate fence from the possession-only era —
see the log line "snapshot DROPPED"). When the server starts pushing snapshots
for non-possessed party members, this check must accept any **party/raid
member** (validate against `_partyMembers`/`_suiRoster`), and the "not synced"
labels in `GameLoop.PartyInventory.cs` / quick-slot "?" wells should be
retired for party members (keep them for whatever edge remains).

## What to build (server: ~/vmangos, SuperUiContent)

Two pushes, both party/raid-scoped, both capability-gated:

### 1. Inventory snapshot for every party AiBot member

- The sender already exists: `static void SendSnapshot(WorldSession* to,
  Player* bot)` in `src/game/SuperUiContent/SuiWorld/CRPG/SuiPossess.cpp`
  (currently called only on possession grant). Reuse it verbatim — the client
  parser is format-compatible.
- Wire format (client parse in `ApplySuiSnapshot`, keep byte-identical):
  `u64 source, u32 talentPoints, u32 coinage, u16 count`, then per item
  `u8 bag (255 = character-held), u8 slot, u64 itemGuid, u32 entry,
  u32 stackCount, u8 bagSlots (0 = not a container)`, then the optional v2
  stat trailer (5 stats, 7 resists, AP/mods, ranged AP/mods, attack times,
  6 damage floats).
- **Send triggers** (recommended v1): on the human's group roster
  change/login send one snapshot per AiBot member; plus a client **pull**
  (new CMSG, subjects list) so the client can refresh when a panel opens; plus
  re-send on that bot's inventory change if a cheap dirty hook exists
  (`Player::` inventory mutation paths) — otherwise pull-only staleness is
  acceptable, the client already displays age stamps.
- **Authorization:** recipient is a real player session
  (`IsSuiCapable`), subject is an AiBot (`AiBotAI` attached) in the SAME
  group/raid as the recipient. Faction-control authority is NOT sufficient —
  that is the owner's party/faction line.

### 2. Known-spells (member facts) for every party AiBot member

- Today spells reach the client only via the possession proxy
  (`SMSG_INITIAL_SPELLS` wrapped in `SMSG_SUI_PROXY`) and persist in
  `botbars.json BotSpells`. Add a compact member-facts message: `u64 guid,
  u16 spellCount, u32 spellIds[]` (chain roots or full list — full list is
  fine, the client's `ResolveBotBar` picks). Same triggers/authorization as
  the snapshot. Client side: seed `ActionsFor(guid).SeedSpells` +
  `PopulateBotBar(guid)` + write the `BotSpells` cache (mirror
  `EnsureBotBarForViewing`).
- Cooldowns/live facts are EXPLICITLY deferred (that plus remote one-shot
  casts is the rest of the doc's Phase B). Do not block on them.

### Wire discipline (from the main doc, unchanged)

- New SMSGs/CMSG need opcode numbers reconciled with the dynamic-combat plan
  (848/849 are provisionally spoken for — check
  `docs/plans/DYNAMIC_COMBAT_RULES_AND_ENCOUNTER_INTELLIGENCE.md`) and the
  current `Opcodes_1_12_1.h` tail (SUI opcodes run 830..0x034B; RTS pair
  0x0348/0x0349).
- Advertise a new capability bit in the control-ACK trailer (append-only; see
  `ApplyFactionControlGroupsCapability` client-side and the ACK builder in
  `SuiPossess.cpp SendAck`) so old clients never see the new SMSGs and the new
  client can label availability honestly.
- Exact-length parsers, rate-limit pulls separately from movement.
- Additive only: old server + new client = today's behavior (possession sync
  + retention); new server + old client = messages gated behind the
  capability request.

## Deploy state as of this handoff (nothing deployed this round)

| Piece | State |
|---|---|
| mangosd (formations 8/9/10, sheath 10, conscription 11/12, m_suiRtsHold wander fix, bridge fence, STATE conscripted flag) | compiled clean on box (`~/vmangos/build`), **NOT installed, NOT restarted** |
| MangosSuperUI brain (conscripted stand-down, wedge/death-blame guards) | builds clean locally (`repos/MangosSuperUI`), **NOT published/deployed** |
| MSUIClient (everything in the build-state table incl. free-view console, minimap dock, retention, BG3 inventory) | compiles clean; the running client held a lock on `bin\` — rebuild after closing it |

Deploy rules (standing): owner does install/restart and all git; deploy
mangosd + MangosSuperUI **together** (an old brain churns against the new
conscription fence). Box: `wowvmangos@192.168.0.2`, source `~/vmangos`, build
`cd ~/vmangos/build && find ~/vmangos/src/game/SuperUiContent \( -name '*.cpp'
-o -name '*.h' \) | xargs touch && make -j$(nproc)` (the touch is load-bearing
— PCH misses SUI edits).

## Verification expected of the next agent

- Server: compiles on the box (no install/restart).
- Client: `dotnet build MSUIClient.sln` clean; run
  `tools/rts-control-group-check`, `tools/rts-move-order-check`,
  `tools/interface-wire-check`, `tools/companion-voice-clinical-check` — all
  must stay green; extend `interface-wire-check` with the new snapshot-gate
  law (party members accepted, non-party still dropped).
- Live proof is owner-run with SuperUI bots (never vmangos partybots): join a
  party with never-possessed bots → their quick slots and Party Inventory
  columns populate without possessing anyone.
