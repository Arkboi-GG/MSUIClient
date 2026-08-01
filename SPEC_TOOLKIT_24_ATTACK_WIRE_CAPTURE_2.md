# SPEC_TOOLKIT_24 — wire capture, second attempt: all-components pktmon + mechanical precondition gate (HARD STOP)

Binding rules: `SPEC_TOOLKIT_00_ORDERS.md`, PILOT_PROTOCOL standing laws.
Bounded observation only. Ruling basis: SPEC-23 Y3 options 1 AND 2, both
authorized by Nico 2026-07-31 — option 2 (netsh trace fallback) is folded
into this order so a second pktmon component failure does not cost another
round-trip.

Scope fence (hard): unchanged from SPEC-23. Capture + frame extraction +
the Z0 pre-send gate (DevTools-gated, observational refusal only) — nothing
else. No server code, database, persistent server/client config, combat
behavior, error display, or F3–F6 changes. Linux tcpdump remains explicitly
excluded. Option 3 (server instrumentation) remains gated behind this
order's verdict and a further Nico ruling. Elevation is for pktmon/netsh
capture control and this order's own raw-file deletion only; same relay
arrangement as SPEC-23 Y0 is acceptable.

## Z0 — mechanical precondition gate + fresh-target scenario

The drift failure has now occurred twice (invalid first X1 attempt; Y1).
Wandering, guard-aggroable targets are prohibited for transit evidence.

1. Pre-send gate at the report=act send path (DevTools-gated): the attack
   send is REFUSED — logged as a gate-refusal verdict row, packet never
   constructed — unless, at send time: target present, visible, alive
   100/100, `dynamicFlags=0`, `unitFlags=0` (exactly zero, not masked),
   GM off, and distance == 0 within the same epsilon accepted X1 used.
   The gate reads the same descriptor state the send path acts on — never
   a parallel recomputation (standing law 2).
2. Scenario: spawn a FRESH entry-6 target at the player's position
   immediately before the attack (the X0 spawn proof is 10/10 with
   within3=true), send the delivered `.gps` control, then the single
   gated attack — control-to-attack inside the same ≤2 s window. Up to 3
   spawn attempts if the gate refuses; each refusal is a recorded verdict
   row. Three refusals ⇒ HARD STOP with the gate evidence (that would be
   a new finding in its own right).
3. One rehearsal run WITHOUT capture to prove the gate and scenario
   (expected: gate PASS row, both socket writes flushed, run-dated
   artifacts). Gates green at the boundary.

## Z1 — bounded capture, primary engine: pktmon ALL components

1. Elevation preflight as SPEC-23 Y0 (prove first, `ELEVATION_ABSENT`
   hard-stop line if missing, no UAC loops).
2. `pktmon start --capture --pkt-size 0` with the same endpoint filter
   (192.168.0.2, TCP 8085) and NO component restriction — Y1's
   `--comp nics` Wi-Fi view is the proven defect; do not repeat it. Full
   packet bytes (`--pkt-size 0`), ≤60 s window around one Z0-gated run.
3. Matching law unchanged: TCP payload BYTE-SUBSTRING of THIS run's
   recorded post-encryption writes (cipher state advances), tolerant of
   coalescing/splitting. A frame recorded at multiple components counts
   once; report which component(s) retained payload. Record per matched
   write: timestamp, TCP seq/len, server ACK coverage of that seq range,
   retransmissions/RST.
4. Same-stage cleanup: extract relevant frames (hex + parsed) into the
   run-dated markdown with pre-deletion hashes of the transient files,
   then delete all ETL/pcapng/formatted text, remove filters, confirm
   stopped.

## Z2 — fallback engine, only if Z1 omits BOTH payloads again

If and only if neither payload byte-substring appears in the healthy
all-components capture: one equivalent bounded `netsh trace` run
(pre-authorized here per Y3 option 2), same filter, same gated scenario,
same matching and cleanup law. If BOTH engines omit both payloads while
the socket observer shows flushed writes, that is
`CAPTURE_ENGINE_EXHAUSTED` — HARD STOP, no causal row, and state the
remaining measurement options for Nico (e.g. capture on a wired NIC, or
mirror/capture at the server side under a new ruling).

## Z3 — transit decision + HARD STOP packet

The three causal rows from SPEC-22 X3, now selectable:

- Attack payload present + ACKed + SPEC-21-style server silence ⇒
  server-side pre-handler discard / unlogged predicate PROVEN; freeze
  against the X3 candidate table (WorldSocket.cpp:98-183,
  WorldSession.cpp:277-331/518-549/1250-1313, Opcodes.cpp:398-401,
  CombatHandler.cpp:32-62, Unit.cpp:4721-4804). HARD STOP — option 3
  ruling is Nico's.
- Chat present + attack absent or never ACKed ⇒ client/LAN send anomaly:
  characterize from frames, HARD STOP, no fix attempt.
- Both absent with a healthy capture ⇒ Z2 fallback, then
  `CAPTURE_ENGINE_EXHAUSTED` as above.

Packet: verdict, frames/hashes, prior-runs reconciliation (P2, X1-X4,
Y0-Y3), options for Nico. SPEC-21 P3/P4 remain queued. Close the elevated
relay/session at the end; subsequent orders run unelevated.

One commit per stage; standard four gates at every boundary; run-dated
artifacts + SHA-256 manifests; actual-versus-predicted per stage; never
overwrite an existing evidence path.

## NOTE TO NICO — elevation, same as last time

Reopen an Administrator PowerShell (right-click PowerShell → Run as
administrator, accept the one UAC prompt) and start the relay/agent the
same way as for SPEC-23 — the Y0 relay arrangement is fine to reuse. Close
the elevated window when the agent hard-stops at Z3.
