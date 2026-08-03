# SPEC_TOOLKIT_26 — SPEC-25 resumption: root-mediated gdb attach (HARD STOP)

Binding rules: `SPEC_TOOLKIT_00_ORDERS.md`, PILOT_PROTOCOL standing laws.
Ruling basis: SPEC-25 W0 `PTRACE_ATTACH_DENIED` reviewed and accepted; Nico
authorizes the sudo-gdb attach path, 2026-07-31.

This order resumes SPEC-25 W1–W3 unchanged except as amended here. The full
SPEC-25 scope fence remains in force: observation only; no server code,
rebuild, binary replacement, package install, DB write, persistent config,
sysctl, combat behavior, error display, or F3–F6 changes. Linux tcpdump
remains excluded. Windows elevation only for the SPEC-24 Z1-style capture
via the established relay.

## Amendments to SPEC-25

1. **Attach mechanism.** W1's gdb attach runs as
   `sudo gdb -q -batch -x <script> -p <pid>`. `kernel.yama.ptrace_scope`
   is NOT changed — root attach is permitted under scope 1. No other
   command in this order runs under sudo; enumerate every sudo invocation
   in the W3 packet (expected: exactly one attach dry-run + one W1 attach,
   plus a `sudo -K` cache drop at the end of each).
2. **Password handling (SPEC-22 X0 law).** HARD STOP first and ask Nico
   for the sudo password for `wowvmangos@192.168.0.2` through an ephemeral
   secure path. Never echo, store, trace, commit, or place it in any file,
   scrollback artifact, shell history, or environment that survives the
   session; feed it to sudo interactively only. Drop the sudo timestamp
   cache (`sudo -K`) immediately after each sudo use. If wowvmangos is not
   a sudoer, report the exact refusal and HARD STOP — Nico will decide
   between a root account path or a sudoers change; do not attempt either.
3. **Live-population caution (512 online sessions observed at W0).** All
   tracepoints MUST be dprintf-style auto-continue — no stopping
   breakpoints anywhere. If any site cannot be expressed as auto-continue,
   drop that site and note it in the packet rather than pausing the world.
   Total attach time ≤5 minutes as before; detach even on partial failure;
   post-detach RA + `.gps` health probes are mandatory before the stage
   boundary.
4. **Attach dry run repeats under the new mechanism** (attach, resolve the
   candidate symbols, verify each resolved address maps to the cited
   source lines, detach) BEFORE the instrumented W1 coincidence run.
5. Stage naming continues SPEC-25: W0b (password hard-stop + sudo dry
   run), W1, W2, W3 per the original order. The W3 packet additionally
   records that ptrace_scope remained 1 throughout and that no sudo
   credential persists (cache dropped, no NOPASSWD entry added).

One commit per stage; four gates at every boundary; run-dated artifacts +
SHA-256 manifests; actual-versus-predicted per stage; never overwrite an
existing evidence path.

## NOTE TO NICO

The agent will hard-stop once to ask for the sudo password for wowvmangos
on 192.168.0.2 — same drill as the SPEC-22 X0 SSH password: give it
through the ephemeral path, it is used interactively and never written
anywhere. If wowvmangos turns out not to be in sudoers, the agent stops
and reports instead of trying anything else. The Windows side needs the
same Administrator PowerShell relay as SPEC-23/24 for the capture half.
