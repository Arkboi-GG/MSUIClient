# MSUIClient — agent instructions (any agent: Claude, Codex, Cursor, a human)

This file is the standing brief for whoever works this repo. It exists because
rules that only live in chat get re-broken (owner, 2026-09-03: "I don't need to
repeat myself over and over"). Tracked at the repo root (2026-09-03) so it travels with the code; the content is
agent-agnostic on purpose. Tool-specific loaders may import it.

## Read first: `shared_docs/`

`shared_docs/` is the TRACKED home of the team's design documents and laws (2026-09-04).
`docs/` is git-ignored scratch and never travels; anything another agent or teammate must
read goes in `shared_docs/` and gets a line here. Read the ones for your topic before
touching the code:

- `shared_docs/POSSESS_LAW.md` — possession, companions, Command View, any
  NPC/loot/taxi/mail interaction, the fleet follow. Binding, enforced by
  `dotnet run --project tools/interface-wire-check -- --possess-law-only` and
  `tools/possess-law-check.sh` (Core, over ssh). Both must stay green after any
  change in those areas; add a check with every new rule.
- `shared_docs/CRPG_FREEZE_SYSTEM.md` — the CRPG/RTS freeze system.
- `shared_docs/MACRO_BOOK.md` — the Macro Book: stable macro ids and the legacy
  ranges, the v2 store, the embedded Core command export and how to regenerate it.
- `CODE_STRUCTURE_LAW.md` (repo root) — where a `.cs` file goes and how it is named.

`interface-wire-check --shared-docs-only` fails when a file in `shared_docs/` is not
listed above, so adding a document means adding its line.

Also, ignored on this machine only: the day-by-day CRPG/RTS record
`docs/current/CRPG_RTS_WIP.md` (append a dated section per round; never rewrite
history) and the server handoff `docs/current/POSSESSION_ROUTING_HANDOFF.md`.

## Standing rules (short form; the law files have the why)

1. The body you drive is the body that acts. Server: `GetSuiActor()`. Client:
   `TryGetInteractionBodyPose` / `ControlledGuid`. Never `_player` for gameplay,
   never `TryGetSessionBodyPose` / `_net.PlayerGuid` for a gate or a purse.
2. A reply built on the bot's socket-less session is lost unless it is in
   `MirrorOwnerPacket`'s whitelist AND unwrapped in `ApplySuiProxy`. Audit
   `Player::OnGossipSelect` for every routed family. "Silently does nothing"
   does not count as functioning.
3. The rest of the party STAYS: a driven body that flies, ports or is hopped
   away from is never chased by teleport; followers hold, and a hold ends the
   active follow leg.
4. Command View: nothing opens until the acting body is physically at the NPC;
   our own dialogs auto-hide out of range; a chooser only for NPCs with two
   distinct offers (mind the stale innkeeper bit on bowyers).
5. No ImGui widgets in gameplay UI (vanilla primitives only); the
   `--imgui-policy-only` check stays green.
6. Never commit, push, create branches or worktrees, or install/restart the
   Core on your own. Build both trays (`dotnet build -c Debug` and `-c Release`).
   By default, the owner launches Release. When Nico gives explicit permission
   in the current conversation, an agent may launch, control, and close the
   local MSUIClient application and local diagnostic, test, or benchmark
   processes, including automating client login and gameplay against Nico's
   configured local development server. This explicit-permission exception
   never authorizes installing or deploying server artifacts, controlling a
   server process/service or `screen`/`tmux` session, or mutating a server
   database/worldstate save. The owner runs all Core installation, deployment,
   restart, and live-server control steps (see `AGENTS.local.md`).
7. Pair-deploy: new opcodes/capability bits change both sides in one round.
8. Probe first, don't theorize: `~/vmangos/run/bin/Server.log` (grep `[SUI]`,
   `released bot`, `catch-up teleport`) and the client `msui-console.log`.

## Box and machine facts

Host names, ssh config, tree paths and the install/restart one-liner are
machine-specific and live in `AGENTS.local.md` (git-ignored). Copy the block
from another machine or ask the owner.
