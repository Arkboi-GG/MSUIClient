# SPEC_TOOLKIT_25 — option-3 server discrimination via gdb attach (HARD STOP)

Binding rules: `SPEC_TOOLKIT_00_ORDERS.md`, PILOT_PROTOCOL standing laws.
Ruling basis: SPEC-24 Z3 option 1, authorized by Nico 2026-07-31, gdb-attach
path selected. Diagnosis only.

Scope fence (hard): OBSERVATION via gdb attach to the RUNNING deployed
mangosd as `wowvmangos` over the established key-auth SSH. No server code,
no rebuild, no binary replacement, no database change (law 11: DB stays
read-only), no persistent server/client config change, no combat behavior,
error display, or F3–F6 changes. No client production code beyond the
existing DevTools-gated Z0 gate and socket observer, reused unchanged.
Windows elevation is authorized ONLY for the same bounded pktmon capture as
SPEC-24 Z1 (same relay arrangement). Linux tcpdump remains excluded. gdb is
the only new server-side tool; if gdb is absent, HARD STOP and report — do
not install packages on the server without a new ruling.

Precondition: breakpoints pause the world briefly on hit; this is accepted
for the private test server. Keep total attach time bounded (≤5 minutes)
and detach in the same stage regardless of outcome.

## W0 — preflight (unelevated except the capture relay)

1. Tree clean at `114a7c9` (or descendant); four gates green; SSH key-auth
   probe PASS; RA probe PASS.
2. On the server, read-only: confirm gdb exists (`command -v gdb`), confirm
   mangosd runs as wowvmangos and get its PID, confirm ptrace attach is
   permitted for same-user (`cat /proc/sys/kernel/yama/ptrace_scope`; 0 or
   ptrace-capable). Any blocker ⇒ HARD STOP with exact evidence; do not
   modify sysctls without a new ruling.
3. Resolve symbol availability: `info functions` for the candidate symbols
   after attach-and-detach dry run, or inspect the binary read-only
   (`file`, `nm`/`objdump -T` best-effort). If the build is fully stripped
   and symbols are unrecoverable, HARD STOP — the fallback (instrumented
   rebuild on a COPY) needs its own order.

## W1 — instrumented observation run (the single decisive attack)

1. Attach gdb in batch/scripted mode (`gdb -p <pid> -x <script>`) with
   NON-STOPPING tracepoints where possible: prefer `dprintf`-style
   breakpoints (auto-continue printf) so the world never blocks. Sites, in
   dispatch order (line anchors from the frozen X3/Z3 table; resolve by
   symbol, verify each resolved address maps to the cited source before
   trusting it):
   - `WorldSocket` application read / packet completion and
     `QueueBinaryPacket` handoff (WorldSocket.cpp:98-183);
   - `WorldSession` opcode parse/queue admission (WorldSession.cpp:277-331);
   - `WorldSession::AllowPacket` / flood gate (518-530, 1250-1313);
   - session-status check incl. the silent `!IsInWorld()` skip (535-549);
   - `WorldSession::HandleAttackSwingOpcode` entry
     (CombatHandler.cpp:32-62) — log the received GUID argument;
   - `Unit::Attack` entry and EVERY silent false-return site
     (Unit.cpp:4721-4804) — one dprintf per return path, labeled, so the
     exact predicate that fires is named by its label;
   - the success path send (melee attack start) as the positive terminus.
   Filter to opcode 321 / the test session where the site allows it, so
   ambient traffic doesn't flood the log.
2. With gdb live and the elevated pktmon capture running (SPEC-24 Z1
   parameters exactly), execute ONE Z0-gated fresh-target scenario:
   delivered `.gps` control, single gated attack. Concurrently record the
   SPEC-21-style debug console window. This is the coincidence run: socket
   write, wire frame, ACK, gdb site trace, and server console silence must
   all describe the SAME packet.
3. Detach gdb, stop and clean the capture (same hash-then-delete law),
   confirm the server is running normally (RA probe + one `.gps`
   round-trip). Save the full gdb transcript run-dated; it is the primary
   artifact.

## W2 — discrimination decision

Read the gdb trace in dispatch order. Exactly one of:

- Trace stops between TCP receipt and handler entry ⇒ the pre-handler
  site that last fired and the first site that did NOT fire bracket the
  discard: name it (read failure, queue rejection, AllowPacket, status/
  IsInWorld skip) with the gdb lines as proof. HARD STOP — the fix ruling
  is Nico's.
- `HandleAttackSwingOpcode` fires ⇒ log the GUID it received; follow into
  `Unit::Attack`: the labeled false-return that fires IS the verdict.
  HARD STOP — fix ruling is Nico's (this is where a production fix or a
  vmangos-behavior acceptance decision gets made).
- All sites fire through the success send yet the client sees nothing ⇒
  contradiction with prior evidence: report as its own finding, HARD STOP,
  no reinterpretation.
- No site fires at all while the wire/ACK proof repeats ⇒ symbol/address
  resolution defect, not a causal result: say so, HARD STOP.

## W3 — HARD STOP packet

Named-predicate verdict with the gdb transcript excerpts, the same-packet
coincidence table (socket write hash, wire seq range, ACK, gdb timeline,
console silence), prior-runs reconciliation (P2, X1-X4, Y, Z), and fix/
acceptance options for Nico. Server left untouched and running; capture
cleaned; elevated relay closed. SPEC-21 P3/P4 remain queued behind the fix
ruling.

One commit per stage; four gates at every boundary; run-dated artifacts +
SHA-256 manifests; actual-versus-predicted per stage; never overwrite an
existing evidence path.

## NOTE TO NICO

Same elevation drill as SPEC-23/24 for the capture half only (Administrator
PowerShell → relay). The gdb half needs no elevation from you — it rides
the existing SSH key as wowvmangos. If W0 reports gdb missing or ptrace
blocked, the agent will hard-stop and tell you exactly what it found;
installing gdb or changing ptrace_scope on the server would be your call,
not the agent's.
