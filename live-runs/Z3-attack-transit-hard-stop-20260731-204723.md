# SPEC-24 Z3 — attack-transit decision and HARD STOP

Run date: 2026-07-31 (America/New_York)

## Frozen verdict

`SERVER_PREHANDLER_OR_UNLOGGED_PREDICATE_PROVEN`

The SPEC-22 X3 present/ACKed/silent row is selected. In the accepted SPEC-24 run, the client constructed one attack only after its mechanical target gate passed, the post-encryption socket observer recorded a successful 14-byte write, pktmon retained those exact 14 bytes in client-to-server TCP sequence 817834466:817834480, and the server subsequently ACKed 817834507. The packet therefore reached the server TCP stack completely.

SPEC-21 P2's separately bounded, fully proven-valid same scenario class remains the authorized server-silence measurement: at console debug level 3 with combat logging enabled, the complete window contained zero CMSG_ATTACKSWING, ATTACKSWING, HandleAttackSwing, received-opcode, opcode-0x0141, or opcode-321 lines. The P2 log itself explains that normal dispatch, handler entry, and several `Unit::Attack` false returns are unlogged. SPEC-24 does not invent which of those downstream sites consumed or rejected the packet.

Together, the ordered cross-run reconciliation proves the packet is no longer a client/socket/LAN transit mystery. Its observable failure lies after server TCP receipt and before any visible attack-family outcome: either a pre-handler server admission/dispatch predicate or an unlogged handler/`Unit::Attack` predicate. Distinguishing those alternatives requires Nico's option-3 ruling.

## Actual versus predicted

```text
PREDICTED mechanical validity: fresh target, exact gate pass, GM off, distance 0
ACTUAL: GUID 0xF13000000604A28F, entry 6, present/visible/alive 100/100,
        unitFlags=0, dynamicFlags=0, GM off, distance 0; attempt 1
RESULT: PASS

PREDICTED client flush: exact run-local post-encryption write
ACTUAL: 14 bytes, SHA-256
        1654b77ff91a6e47b4f3b402c46ad0373eb423875b24f4d709fed06f0507a24e,
        flushed=true, hex=92E386E4B7428FA20406000030F1
RESULT: PASS

PREDICTED on-wire/ACK: exact substring present and fully ACKed
ACTUAL: seq 817834466:817834480 at 20:35:54.147617900;
        first covering server ACK 817834507 at 20:35:54.732850400;
        no retransmission, no RST
RESULT: PASS

PREDICTED SPEC-21-style server diagnostic evidence
ACTUAL P2: zero receive/dispatch/opcode/handler lines in the complete bounded
           debug/combat console for its proven-valid GM-off distance-zero attack
RESULT: SERVER SILENT within the authorized diagnostic surface

PREDICTED causal row
ACTUAL: present + ACKed + server-silent
RESULT: SERVER_PREHANDLER_OR_UNLOGGED_PREDICATE_PROVEN; HARD STOP
```

## Frozen server candidate table

No candidate is promoted beyond its evidenced class:

| Candidate site | Deployed file:line | Frozen interpretation after Z1 |
|---|---|---|
| socket header/body read | `Server/WorldSocket.cpp:98-148` | TCP delivery is proven. A malformed header path would normally close/log; continued traffic makes that subcase unlikely, but application read completion remains uninstrumented. |
| auth/session admission | `Server/WorldSocket.cpp:153-183` | Adjacent delivered control and continued session make missing authentication/closing unlikely; queue handoff remains uninstrumented. |
| queue/parser | `Server/WorldSession.cpp:277-331` | Opcode 321 registration is confirmed; parser verification and queue admission are unobserved. |
| opcode registration | `Server/Protocol/Opcodes.cpp:398-401` | Maps opcode 321 to `STATUS_LOGGEDIN`, `PACKET_PROCESS_SPELLS`, `HandleAttackSwingOpcode`; registration is not the missing fact. |
| flood/session gates | `Server/WorldSession.cpp:518-549,1250-1313` | `AllowPacket` and the silent `!IsInWorld()` skip remain pre-handler candidates. |
| attack handler | `Handlers/CombatHandler.cpp:32-62` | Handler has no entry log. Response-producing invalid-target branches are excluded by the zero-response premise; entry and silent behavior remain unmeasured. |
| unit attack law | `Objects/Unit.cpp:4721-4804` | Several silent false-return predicates remain. Prior evidence excludes GM mode, identity, range/facing, mount, stale combat, dead/absent target, and HOME-motion evade, but no deeper predicate is selected. |

## Prior-runs reconciliation

- SPEC-21 P2: preserved as the bounded server diagnostic-silence proof for a proven-valid, alive, flags-zero, GM-off, distance-zero target. It never claimed a transit fact and remains valid.
- SPEC-22 X1: preserved as accepted client socket-flush evidence. Z1 independently repeats and strengthens it with a fresh mechanically gated target.
- SPEC-22 X2–X4: their `TRANSIT_UNRESOLVED_CAPTURE_PRIVILEGE` outcome was correct at the time. SPEC-24 supplies the missing on-wire and ACK facts; it does not retroactively relabel those stages.
- SPEC-23 Y0: elevation/relay capability remains proven.
- SPEC-23 Y1–Y3: `CAPTURE_FILTER_DEFECT` and `SCENARIO_PRECONDITION_DRIFT` remain correct for that NIC-only, drifted run. SPEC-24 changed both measurement conditions: all components and a mechanical fresh-target gate.
- SPEC-24 Z0: the gate and fresh-target rehearsal passed on attempt 1.
- SPEC-24 Z1: exact attack payload present and ACKed, with transient raw files hashed then deleted.
- SPEC-24 Z2: correctly not entered because both payloads were not absent.

## Nico ruling options

1. Authorize a new, separately bounded option-3 order to instrument the minimum server path needed to discriminate: socket application read/queue handoff, session queue/`AllowPacket`/status admission, handler entry, and each silent `Unit::Attack` false-return site. It must include complete restoration, no DB or persistent-config change, and one valid fresh-target repeat.
2. Do not authorize server instrumentation and retain this frozen root-cause class. SPEC-21 P3/P4 remain queued and no further combat behavior work begins.

Linux tcpdump remains excluded. No server instrumentation is implied by this packet.

## Cleanup and scope

The elevated relay completed and exited. Pktmon stopped and its endpoint filter was removed in the elevated lifecycle. The ETL, PCAPNG, and formatted raw capture were hashed before deletion and no raw capture survives. The temporary relay helper and control files were deleted. The live-run rewrite of `vantages.json` was restored to the committed baseline.

No server code, database, persistent server/client configuration, combat behavior, error display, or F3–F6 behavior changed. SPEC-21 P3/P4 remain queued.

**HARD STOP — SPEC-24 Z0–Z3 is complete at `SERVER_PREHANDLER_OR_UNLOGGED_PREDICATE_PROVEN`. Deeper server instrumentation requires Nico's new option-3 ruling.**
