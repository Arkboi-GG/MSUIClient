# SPEC_TOOLKIT_23 — elevated Windows wire capture of the accepted X1 scenario (HARD STOP)

Binding rules: `SPEC_TOOLKIT_00_ORDERS.md`, PILOT_PROTOCOL standing laws.
Bounded observation only. Ruling basis: SPEC-22 X4 option 1 (smallest next
step), authorized by Nico 2026-07-31.

Scope fence (hard): capture + frame extraction ONLY. No server code, database,
persistent server or client config, combat behavior, error display, or F3–F6
changes. SPEC-22 X4 option 2 (Linux tcpdump authorization on 192.168.0.2) is
NOT authorized. Option 3 (deeper server instrumentation) stays gated behind
this order's verdict and a further Nico ruling. Elevation is granted for
pktmon/netsh capture control and nothing else: no installs (no Npcap/
Wireshark/dumpcap), no service changes, no firewall changes, no elevated
mutations outside `pktmon`/`netsh trace` and deletion of this order's own
run-dated capture files. Reuse the existing DevTools-gated X1 socket observer
unchanged; no new production client code.

## Y0 — elevation preflight

1. Nico starts this agent session from an Administrator-elevated shell (see
   NOTE TO NICO below). First act: prove elevation with `net session`
   (succeeds only elevated) and record `whoami /groups | findstr S-1-16-12288`
   (High Mandatory Level). If NOT elevated: HARD STOP immediately with the
   single line `ELEVATION_ABSENT — restart the agent from an Administrator
   shell per SPEC-23 NOTE TO NICO`. Do not loop UAC attempts.
2. Verify `pktmon` responds to `pktmon status` (the P2/X2 "driver access
   denied" must now pass). Fallback engine is `netsh trace` only. If both
   still fail while elevated, HARD STOP with the exact error text.
3. Confirm repo tree clean at `2c71edb` (or its descendant), four gates
   green, and SSH key auth + RA reachability still PASS (one probe each; no
   re-bootstrap — X0 already ran).

## Y1 — bounded capture of the IDENTICAL accepted X1 scenario

1. Filter to the world connection only: remote host 192.168.0.2, TCP port
   8085. `pktmon filter add` accordingly (or the equivalent bounded netsh
   trace scenario). Start capture immediately before the scenario, stop
   within ≤60 s of the attack send.
2. Run the same accepted X1 protocol: delivered `.gps` control, then one
   proven GM-off CMSG_ATTACKSWING against a distance-0, alive, flags-zero,
   in-store target, with the same precondition proofs and the socket-flush
   observer active so this run's own post-encryption bytes and SHA-256 are
   frozen alongside the capture.
3. Convert (pktmon etl2pcap / `pktmon format`) and extract ONLY frames on the
   filtered connection around the two writes. Matching law: match on TCP
   payload BYTE-SUBSTRING against this run's recorded post-encryption writes
   (X1 reference shapes: 19-byte chat `3F85…7300`-form, 14-byte attack
   `8263DEEECF998CA20406000030F1`-form — but match THIS run's bytes, since
   cipher state advances), NOT on frame counts: the two writes were 12 ms
   apart in X1 and Nagle/coalescing may legally place both in ONE TCP
   segment, or split them. Record per matched write: frame timestamp, TCP
   seq/len, ACK from 192.168.0.2 covering that seq range, and any
   retransmission or RST on the connection during the window.
4. Cleanup in the same stage: extract the relevant frames (hex + parsed
   summary) into the run-dated markdown, then DELETE all raw ETL/PCAP/pcapng,
   `pktmon filter remove all`, confirm `pktmon status`/netsh show trace
   stopped. No raw capture file may survive the stage boundary.

## Y2 — transit decision (the three causal rows are now selectable)

- Attack payload present on wire + ACKed by 192.168.0.2 + (per SPEC-21 P2)
  zero receive/dispatch/handler debug lines ⇒ server-side pre-handler discard
  or unlogged predicate is PROVEN. Freeze the verdict against the X3
  candidate table (WorldSocket.cpp:98-183, WorldSession.cpp:277-331 /
  518-549 / 1250-1313, Opcodes.cpp:398-401, CombatHandler.cpp:32-62,
  Unit.cpp:4721-4804). HARD STOP — the option-3 ruling (gdb attach vs
  temporary instrumented rebuild on a COPY) is Nico's.
- Chat payload present + attack payload absent (or present but never ACKed /
  retransmitted to exhaustion) ⇒ client/LAN send anomaly: characterize from
  the frames (coalescing, MTU/fragmentation, seq gap, RST timing) and HARD
  STOP with the characterization. No fix attempt under this order.
- Neither payload present ⇒ capture/filter defect, not a transit verdict:
  say so, do not select a causal row, HARD STOP.

## Y3 — HARD STOP packet

Transit verdict with extracted frames and hashes; prior-runs reconciliation
updated (SPEC-21 P2, SPEC-22 X1-X4); implied next-order options stated for
Nico. SPEC-21 P3/P4 remain queued behind the verdict. Close the elevated
session at the end of this order; subsequent orders run unelevated.

One commit per stage; standard four gates at every boundary; run-dated
artifacts + SHA-256 manifests; actual-versus-predicted per stage; never
overwrite an existing evidence path.

## NOTE TO NICO — how to grant the agent elevation (once, for this order)

Elevation is per-process on Windows and inherited by child processes, so the
agent must be LAUNCHED from an elevated shell — it cannot self-elevate (X2's
UAC preflight proved that):

1. Close the agent's current terminal session.
2. Right-click the terminal app you launch the agent from (Windows Terminal /
   PowerShell) → **Run as administrator** → accept the one UAC prompt.
3. In that elevated shell, `cd C:\Users\nico\source\repos\MSUIClient` and
   start the implementing agent exactly as usual, directive pasted as normal.
   Everything it runs now inherits Administrator, so pktmon/netsh work with
   no further prompts.
4. When the agent hard-stops at Y3, close that elevated terminal. Launch
   future agent sessions from a normal (unelevated) shell again.

While elevated, the agent has full machine rights; the scope fence above is
the guard. Keep the elevated session single-purpose: this order only.
