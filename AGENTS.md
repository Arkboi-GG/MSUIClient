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
- `shared_docs/GAMEPLAY_INTERACTION_CHECKLIST.md` — evidence and verification status
  for subtle gameplay feedback; how to regenerate the archive-driven audit and triage it.
- `shared_docs/FULL_GAME_COVERAGE.md` — full-game coverage inventory, quest/spell
  acceptance rules, background execution batches and evidence gaps.
- `shared_docs/Sept 8, 26 fixes.md` — complete September 7–8 audit recap, all GI entries,
  evidence, open defects and the owner-requested pause handoff.
- `shared_docs/INTERIOR_UNIT_LIGHT.md` — how units, mounts, items and server
  gameobjects are lit inside a WMO (the floor's MOCV under the feet, one law with
  the props); the `MSUI_INTERIORLIGHT_PROBE` offline proof.
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

## Find code with the locator, not with grep (2026-09-08)

All three repos (this client, the `MangosSuperUI` web app, the vmangos C++ core on the box)
are indexed by one local service, the **superui-locator**, at `http://127.0.0.1:5077`: every
type and member of both C# repos live from the working tree (a saved file is re-indexed within
a second), the libclang graph of the core, string literals, leading comments, and the
cross-repo seams (SUI opcodes <-> core handlers, bridge message names, twin files). Before any
tree-wide grep, `Select-String`, `sed -n` walk or "where is X" reasoning, ask it; grep only
when it returns nothing after two phrasings, and say so.

- MCP tools (Claude Code, Codex with MCP): `locate(task)`, `search(q, repo?, kind?)`,
  `outline(file|id)`, `neighbours(id, types?)`, `read(id | file,start,end)`, `grep(q)` (core
  tree only), `stats`.
- Any agent: the same as GET routes: `curl -s "http://127.0.0.1:5077/locate?task=..."`,
  `/search?q=...&repo=cli`, `/outline?file=GameLoop.Net.cs`, `/neighbours?id=...&types=calls,seam`,
  `/read?id=...`, `/stats`.
- Ids: `cli:MSUIClient.GameLoop::ControlledGuid`, `core:WorldSession.SuiPossess/HandleOrder`; a
  unique suffix (`GameLoop::ControlledGuid`) is accepted. Order: locate -> outline -> neighbours ->
  read one span at a time (<= 400 lines). Never read whole files to find a method.
**Enforcement (Claude Code):** a PreToolUse hook (`SourceMapper/Locator/hooks/locate-first.py`, registered in
`.claude/settings.json`) denies tree-wide searches (Grep without a file path, recursive grep/rg/Select-String)
until a locator tool has been called in the last 15 minutes; file-scoped searches always pass, and the hook
stands down when the host is not running. Codex/Qwen have no hook: the rule above is the contract.

- If `stats` does not answer, the host is down: start it (`dotnet run -c Release` in
  `C:\Users\nico\source\repos\SourceMapper\Locator`, or the `locator` entry in
  `.claude/launch.json`) or tell the owner. Markdown docs (`shared_docs/`, `docs/`, root),
  JS functions and Razor views are indexed with sections/spans; JSON and binary assets are not.

## Box and machine facts

Host names, ssh config, tree paths and the install/restart one-liner are
machine-specific and live in `AGENTS.local.md` (git-ignored). Copy the block
from another machine or ask the owner.
