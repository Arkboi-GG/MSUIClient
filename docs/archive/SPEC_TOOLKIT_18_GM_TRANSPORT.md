# SPEC_TOOLKIT_18 — GM transport root cause + combat diagnosis completion (HARD STOP)

Binding rules: `SPEC_TOOLKIT_00_ORDERS.md`, PILOT_PROTOCOL standing laws.
This order executes the D4 queue items 1–4: repair the autonomous GM
transport with positive proof, then complete the stalled combat diagnosis.
Combat behavior fixes, error-text display, and F3–F6 remain untouched.

Pilot reframe folded in: run 16's "spawns" were GUID-sorted nearby
creatures, so GM commands have plausibly NEVER executed in any run — the
wild-mob hypothesis (spawn:1 = pre-existing distant creature; NOTINRANGE
unlogged in run 16; the foreign attacker = an aggroed wild mob) would
explain CB1, CB6, and the foreign swings with one root cause. Treat run
16's GM-dependent steps as unproven, not as a working baseline.

## G0 — transport vs permission discrimination

1. Echo test: with the live session up, send a plain non-command say line
   `ping-<runstamp>` via the GM console. A well-formed say echoes back as
   SMSG_MESSAGECHAT to the sender. Capture BOTH directions' full hex.
   Echo received ⇒ transport works, suspicion moves to permissions.
   No echo ⇒ transport defect: verify the sent CMSG_MESSAGECHAT bytes
   against the authoritative 1.12 layout (uint32 type, uint32 lang,
   null-terminated string — confirm against the project's opcode/struct
   authority AND benilla's chat sender, file:line cited). Fix the encoding
   (instrument-layer fix, authorized), re-test until the echo passes.
2. Permission check, read-only per law 11: with mangosd running, query the
   realmd DB for the Test account's gmlevel (connection details per
   SETUP.md; the prior refused attempt was with the server down — retry
   now). Record the exact rows read. If the DB is still unreachable,
   record zero-queries and instead locate mangosd's command/GM log and
   config on disk (read-only file evidence) for receipt/rejection lines.

## G1 — provisioning (authorized, documented)

If gmlevel is insufficient: provision the Test account via the mangosd
SERVER CONSOLE (`account set gmlevel <Test> ...`), NOT via SQL — the DB
write prohibition stands. Document the exact console command and result in
SETUP.md. If console access is not available to the runner, this becomes
the one Nico-only step (privileged server administration, justified under
law 12) — stop and ask via the report rather than working around it.

## G2 — positive-proof gate

Before any combat re-run, ALL of: a server text response to `.gps`; an
observed position mutation from `.go xyz` (movement trace shows the
teleport); a creature created by `.npc add` whose identity comes from the
server response/subsequent appearance (not GUID sort), selectable and
within a measured 3 yd; `.die` on a throwaway spawn producing
descriptor-confirmed death. Each proof is a run-dated artifact. Fail any ⇒
HARD STOP with the evidence.

## G3 — combat diagnosis completion

With controlled specimens finally real, execute unchanged:
- D1's V-A..V-D decision-table matrix (CB1 root cause).
- CB6 with server-confirmed death, judging the client's target-death
  intent drop.
- CB4/CB5/CB7 re-runs restoring behavioral claims only from player-GUID
  swing rows (swing cadence vs weapon speed now measurable — include it).
- The initial-IntentOff audit hygiene question from D4 item 6: determine
  from the trace whether it is audit normalization or a real transition
  defect; report, do not fix.

## G4 — HARD STOP

Packet: GM transport root cause with the discriminating evidence; the
completed CB findings table with every claim backed by player-GUID rows;
the final combat fix-order queue (candidates: target-death intent drop if
proven, error-event display per the standing CB2/CB3 ruling, plus whatever
the matrix exposes). No fixes in this order.

One commit per stage; standard four gates; run-dated artifacts + SHA-256
manifests; report appends with actual-versus-predicted blocks.
