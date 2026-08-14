# Agent operating rules

## Owner-only live runtime control

Nico alone installs or deploys server artifacts and controls live runtime state.

- Codex and every sub-agent must never install or deploy server artifacts, start,
  stop, restart, reload, signal, or kill a service or process, or create, stop, or
  replace a `screen`/`tmux` session. This includes `make install`, deploy scripts,
  `systemctl`/`service`, RA or server-console shutdown commands, and process signals.
- The furthest an agent may go is a successful build, and only when the task asks
  for one. Report the built artifact and leave installation, deployment, and all
  runtime control to Nico.
- Read-only inspection of processes, services, ports, and logs is allowed. Never
  infer permission for runtime control from requests to finish, test, deploy, or
  make a change live, and never delegate prohibited runtime actions to another
  agent.
- Documentation and handoffs must preserve this boundary and must not instruct an
  agent to run an installation, deployment, shutdown, or restart command.
