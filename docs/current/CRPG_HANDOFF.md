# CRPG/RTS Mode v1.0 — Agent Handoff

> **SUPERSEDED (2026-08-10) — TIER-1 MILESTONE ONLY.** This file describes the
> v1.0 possession/free-view milestone and is not authoritative for Tier-2 RTS match
> mechanics or status. Read [`CRPG_RTS_WIP.md`](../../CRPG_RTS_WIP.md) for the current
> Tier-1 session record, binding owner decisions and open items, and
> [`RTS_WORLDSTATE_PLAN.md`](../../RTS_WORLDSTATE_PLAN.md) for the current Tier-2
> architecture and phase boundary. The repo paths below are stale (the SuperUI-Core
> clone and brain worktree no longer exist locally; server source lives on the vmangos
> box at `~/vmangos`, branch `development`), and a large later Tier-1 feature set
> (free-view RTS orders, streaming eye, chain links, layered bot bars and real party
> portraits) sits on top of what is documented here.

## What this feature is

1. **Possession** — a real player takes direct control of any AiBot in their party/raid.
   Ctrl+Tab / Ctrl+Shift+Tab cycles own character + controllable bots; Alt+click a party
   portrait jumps. Bars/spellbook/cooldowns are the bot's real data; bags/talents render
   read-only. Movement/targeting/casting drive the bot through the stock mover machinery.
2. **Autonomy** — the abandoned own character runs `SuiUnattendedAI` (PartyBotAI behaviour,
   fabricated-bot init skipped). It never vendors; `SELL_ITEMS` refuses real-account
   characters as a hard wall.
3. **Free view** — Ctrl+F detaches a fly camera; the whole party (own char included) runs
   on AI; Ctrl+RightClick issues party move/attack orders (reuses the bridge command paths).

## Where everything is

| Repo | Branch | Notes |
|---|---|---|
| `repos/MSUIClient-crpg` (worktree) | `feature/crpg-rts-v1` | off `main`; user's spell-creator work lives on `codex/spell-creator-advanced-lab` untouched |
| `repos/SuperUI-Core` | `feature/crpg-possession-v1` | fresh clone of the vmangos fork |
| `repos/MangosSuperUI-crpg` (worktree) | `feature/crpg-possession-standdown` | brain stand-down only |

**Read first:** `SuperUI-Core/docs/SUI_WIRE_PROTOCOL.md` — the protocol authority
(opcodes 828–834, payload layouts, handshake ordering, GM test commands).

Key files:
- Server: `src/game/SuperUiBots/SuiPossess.{h,cpp}` (possession core, mirror, snapshot,
  orders, GM commands), `SuiUnattendedAI.h`, `AiBotAIMain.cpp` (`m_possessed` gates,
  `FindPartyBoss` possessed-first pre-pass), `AiBotAIBridge.cpp` (command gate, STATE
  `possessed`, SELL_ITEMS wall), handler edits in `SpellHandler/CombatHandler/MiscHandler`
  (`GetSuiActor`), hooks in `Group.cpp` / `Player.cpp` / `WorldSession.cpp`.
- Client: `MSUIClient/Program.Control.cs` (state machine, ACK/roster/proxy/snapshot,
  cycle keys, freecam, RTS clicks, banner), `Net/LocalMovementSender.cs` (`Parked`),
  `Net/Opcodes.cs` / `Net/WorldSession.cs` (senders).
- Brain: `BotLogic/Planners/GoalSelector.cs` "possessed" hold + `possessed` STATE parse.

## Architecture invariants (do not violate)

- Possession is **not** charm-based: no faction swap, no threat wipe. Ordering law:
  `Camera::SetView` → `SetPossessorGuid` → `SetMover` → `SetClientControl`; the client
  answers the grant with `CMSG_SET_ACTIVE_MOVER`.
- State: session `m_suiControlledGuid` + bot `possessorGuid` + AI `m_possessed`. No registry.
- The owner-packet mirror (`SMSG_SUI_PROXY`) is **additive** — never replaces
  `ai->OnPacketReceived` (teleport-ack/rez/roll self-service dies otherwise).
- Forced-release ACKs (result ≥ 16) must be honoured by the client in **any** state;
  server-initiated releases open a 1 s movement drain (`RejectMovementPacketsFor`).
- Custom SMSGs only go to sessions that spoke `CMSG_SUI_*` first (`IsSuiCapable`) —
  stock clients must never see out-of-table opcodes.
- Client mutations while possessing are gated read-only (inventory clicks, talent spend,
  action-bar edits, looting) because those opcodes act on the SESSION character.

## Next steps (in order)

1. **Build `feature/crpg-possession-v1` on the vmangos box.** An adversarial review found
   zero compile breaks and byte-exact wire consistency, but it has not met a compiler.
2. **M1 smoke test with a stock 1.12 client** (no custom client needed):
   `.partybot add mage` → `.sui possess <botname>` → WASD drives the bot → `.sui release`
   → AI resumes. Forced-release cases: kill the bot, leave group, teleport.
3. **MSUIClient demos**: Ctrl+Tab possess → drive into a mob pack → cast from the bot's
   bars (cooldowns tick via proxy) → own char follows/assists (M5) → release, AI finishes
   the fight → Ctrl+F free view → Ctrl+RightClick move/attack orders. Brain fleet log
   should show the possessed bot held with goal reason "possessed".

## Known deferrals / latent items

- v1.1: possessed-bot mutations (equip/talent/vendor/loot/bar edits), COOLDOWN_EVENT &
  CLEAR_COOLDOWN proxy consumption, roster badges on party frames, rebindable control
  chords (Ctrl+Tab / Ctrl+F are hard-coded like the F fly toggle).
- SUI protocol code assumes the fork's hard-pinned 1.12.1 build (same as existing guards).
- A mind-controlled unattended character loses its `SuiUnattendedAI` via
  `RemoveTemporaryAI` and idles until the next possess (safe, known).
- Freecam keeps the unattended AI anchored to the last-possessed bot until re-anchored.

Unrelated but recent: the Launch Options mode switch was fixed and live-verified
(launch choice now wins over `server.enabled`; modal no longer fights the login fields).
Commits exist on both `codex/spell-creator-advanced-lab` and this branch.
