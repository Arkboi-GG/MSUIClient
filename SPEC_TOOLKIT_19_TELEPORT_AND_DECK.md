# SPEC_TOOLKIT_19 — same-map teleport support + scenario deck correction + diagnosis completion (HARD STOP)

Binding rules: `SPEC_TOOLKIT_00_ORDERS.md`, PILOT_PROTOCOL standing laws.
This order EXPLICITLY AUTHORIZES one production networking change (T0); it
is a prerequisite-infrastructure fix, not a combat fix. Combat behavior,
error-text display, and F3–F6 remain untouched.

## T0 — same-map teleport receive/apply/ack (production change, authorized)

Implement client handling of the server's same-map teleport
(MSG_MOVE_TELEPORT_ACK server→client variant): parse the movement counter +
destination MovementInfo, apply position AND orientation to the character
controller, the camera, and the rendered body (note the SMSG_LOGIN_VERIFY
facing-discard precedent in BENILLA_VS_MSUI_MOVEMENT — do not repeat it:
the applied orientation must survive the next frame), then send the client
MSG_MOVE_TELEPORT_ACK reply (counter + time) per the 1.12 handshake.
Symbol-verification law applies: verify the packet layout and ack shape
against the project's authoritative 1.12 protocol source AND benilla's
implementation (file:line cited), and confirm what vmangos expects by
reading its handler source read-only.

Acceptance (agent-run, run-dated artifacts):

```text
.go <x y z> ⇒ movement trace shows position == requested coords within one
  tick of packet receipt, orientation applied and retained next frame
ack sent exactly once, correct counter, hex logged both directions
post-teleport movement is server-accepted: heartbeats continue, no
  position snap-back within 30 s, a scripted run segment behaves normally
  at the destination (trace attached)
kinematic audit on a post-teleport run-start-stop script: all
  current-tree bands PASS
far-map transfer (SMSG_TRANSFER_PENDING / SMSG_NEW_WORLD) is OUT OF SCOPE;
  note its status in the report, change nothing there
```

One commit for this root cause.

## T1 — scenario deck correction for this server

The chat path now works: discover the correct creature lifecycle commands
for THIS vmangos build via `.help`/command responses and by reading the
server's command-table source on disk (read-only). Replace the invalid
`.npc add 6` deck with verified spawn / identify / despawn commands;
document the verified command set in SETUP.md. Validation: spawn ⇒ server
response identifies the creature, it appears in the client within a
measured 3 yd, and the cleanup command demonstrably removes it.

## T2 — positive-proof gate (G2 re-run)

All four proofs from SPEC-18 G2, now expected to pass: `.gps` response;
`.go` position mutation visible in the movement trace; response-identified
spawn within 3 yd; descriptor-confirmed `.die` on a throwaway spawn.
Run-dated artifact per proof. Any failure ⇒ HARD STOP with evidence.

## T3 — combat diagnosis completion (G3 unchanged)

Execute SPEC-18 G3 exactly as written: D1's V-A..V-D matrix, CB6 with
confirmed death, CB4/CB5/CB7 re-runs with player-GUID-only claims plus
swing cadence vs weapon speed, and the initial-IntentOff audit-hygiene
determination (report, don't fix).

## T4 — HARD STOP

Packet: teleport acceptance evidence; verified command deck; completed CB
findings table with every claim backed by player-GUID rows; final combat
fix-order queue. No further fixes in this order.

One commit per stage; standard four gates every boundary; run-dated
artifacts + SHA-256 manifests; report appends with actual-versus-predicted.
