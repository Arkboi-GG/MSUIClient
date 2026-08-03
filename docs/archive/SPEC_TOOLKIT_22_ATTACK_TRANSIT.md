# SPEC_TOOLKIT_22 — attack packet transit diagnosis + travel-laptop re-bootstrap (HARD STOP)

Binding rules: `SPEC_TOOLKIT_00_ORDERS.md`, PILOT_PROTOCOL standing laws.
Diagnosis + bounded observation only. No combat behavior, server code, DB,
persistent server config, error-display, or F3–F6 changes.

Context: SPEC-21 P2 (SSH-resumed) captured a debug window around one proven
GM-off CMSG_ATTACKSWING and found ZERO receive/dispatch/handler lines. The
permitted logging cannot distinguish packet loss from an unlogged server
predicate. This order discriminates WHERE the packet dies: client socket →
wire → server socket → dispatch. The environment has ALSO moved to Nico's
travel laptop (same LAN; 192.168.0.2 still valid).

## X0 — travel-laptop re-bootstrap

1. Verify the repo state: build + all four gates green. If any
   SPEC_TOOLKIT_*.md / plan / protocol root docs are untracked-and-missing
   after the machine move, HARD STOP and ask Nico to restore them from the
   pilot chat before proceeding. Commit any untracked order/plan docs that
   ARE present so this never recurs.
2. Recreate untracked local state that does not travel with git:
   client-config.json (TEST credentials, RA credentials) — ask Nico only
   for values that cannot be recovered; do not guess.
3. SSH: test key auth to wowvmangos@192.168.0.2 first (the old key may
   not exist on this machine). If absent, generate a NEW dedicated
   keypair; ask Nico for the (possibly rotated) password via a HARD STOP
   line — never assume the old one; install the key, confirm, cease
   password use, update SETUP.md (fingerprint only). Same handling rules
   as before: never print/store/commit the password.
4. Re-run the four positive proofs (SPEC-19 T2) once to confirm the live
   loop works from this machine.

## X1 — client socket-flush evidence

Instrument the client's LOWEST network layer (post-encryption, at the
actual socket write): per outbound packet, log opcode, byte count, and a
hash of the exact bytes written+flushed. Report=act at the socket. Repeat
one proven GM-off attack; compare the attack send against an adjacent chat
send (known-delivered control): both must show flushed socket writes. Not
flushed ⇒ client send-path defect below the wire tap — HARD STOP with the
evidence (that would be a production fix needing its own order).

## X2 — transit capture (bounded, documented, reverted)

Establish whether the flushed bytes ARRIVE at the host:

- Preferred, Linux side: check `sudo -n` availability for tcpdump as
  wowvmangos. If permitted: capture one attack repeat filtered to the
  world port, bounded duration, delete the capture file after extracting
  the relevant frames. If no sudo: report and use the Windows side.
- Windows side: pktmon / netsh trace on the client machine for the same
  bounded repeat (requires elevation; if unavailable, report).
- Also enumerate, read-only, the deployed world.conf and vmangos source
  for any built-in packet-log option this build supports (cite file:line);
  if one exists, prefer it (enable → capture → revert).

## X3 — decision table

- Bytes flushed + present on wire + no handler line even at debug ⇒
  server-side pre-handler discard or unlogged predicate: HARD STOP. The
  packet includes the exact frames and the enumerated candidate discard
  sites from the vmangos dispatch path (WorldSocket/WorldSession queue,
  opcode table state checks — file:line). The ruling on deeper server
  instrumentation (gdb attach, temporary instrumented rebuild on a COPY)
  is Nico's.
- Bytes flushed + absent on wire ⇒ LAN/socket anomaly: characterize
  (does chat traffic show? MTU/fragmentation?) and report.
- Not flushed ⇒ X1's client-defect hard stop.

## X4 — HARD STOP

Packet: transit verdict with frames/hashes; prior-runs reconciliation
updated; whatever fix or instrumentation ruling the verdict implies,
stated as options for Nico. P3/P4 of SPEC-21 (combat matrix completion)
remain queued behind this root cause.

One commit per stage; standard four gates; run-dated artifacts + SHA-256
manifests; actual-versus-predicted per stage.
