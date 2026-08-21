# Agent operating rules

## Local Benilla reference checkout

Nico's existing local Benilla reference checkout is:
`C:\Users\nico\Desktop\benilla-main`.

- Use this checkout for Benilla/vanilla rendering comparisons.
- Do not create another Benilla clone in the MSUIClient workspace.

## SuperUI-Core is homeserver-only

The authoritative and only development checkout of SuperUI-Core is Nico's
homeserver checkout at `~/vmangos`.

- Never clone, fork, branch, worktree, edit, build, commit, merge, stash, fetch,
  pull, or otherwise develop SuperUI-Core on Windows.
- Never create or use a Windows SuperUI-Core checkout as a substitute for the
  homeserver checkout, even for parallel agent work.
- Treat any existing Windows SuperUI-Core directories as legacy cleanup
  candidates, not working copies. Do not modify or delete them without Nico's
  explicit approval.
- If a task requires SuperUI-Core changes and the homeserver checkout is not
  accessible, stop and ask Nico for the appropriate homeserver workflow. Do not
  recreate the repository on Windows.

## Owner-only live runtime control

Nico alone installs or deploys server artifacts, creates/restores/swaps server
database or worldstate saves, and controls live runtime state.

- Codex and every sub-agent must never install or deploy server artifacts, start,
  stop, restart, reload, signal, or kill a service or process, or create, stop, or
  replace a `screen`/`tmux` session. This includes `make install`, deploy scripts,
  `systemctl`/`service`, RA or server-console shutdown commands, and process signals.
- Codex and every sub-agent must never write, restore, or swap a live server
  database/worldstate save, or invoke a web/API/SQL workflow that does so. Codex
  may prepare a checklist and walk Nico through his owner-operated steps.
- The furthest an agent may go is a successful build, and only when the task asks
  for one. Report the built artifact and leave installation, deployment, and all
  runtime control to Nico.
- Read-only inspection of processes, services, ports, and logs is allowed. Never
  infer permission for runtime control from requests to finish, test, deploy, or
  make a change live, and never delegate prohibited runtime actions to another
  agent.
- Documentation and handoffs must preserve this boundary and must not instruct an
  agent to run an installation, deployment, database/worldstate mutation,
  shutdown, or restart command.
