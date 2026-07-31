# SPEC-22 X4 attack-transit HARD STOP packet

## Transit verdict

`TRANSIT_UNRESOLVED_CAPTURE_PRIVILEGE`

The client defect branch is excluded: the exact CMSG_ATTACKSWING returned from
the post-encryption socket write and flush. The wire-present and wire-absent
branches remain deliberately unselected because both prescribed packet-capture
engines required privilege unavailable to this session.

```text
DELIVERED CONTROL
opcode=0x0095 CMSG_MESSAGECHAT (.gps)
bytes=19
sha256=80254993e43f40b1b225ddd72c330c80e1d7df63e9d7c8f444fecf5fbe36ffea
post-encryption socket bytes=3F85FEA7407400000000070000002E67707300
write+flush=true
server Map response=true

ATTACK
opcode=0x0141 CMSG_ATTACKSWING
bytes=14
sha256=784feef9f39b41853082ecfd8bb6dd47d801f1d3e7143986d79abb317c336420
post-encryption socket bytes=8263DEEECF998CA20406000030F1
plaintext body=8CA20406000030F1
target=0xF13000000604A28C
write+flush=true
GM off / present / visible / alive 100/100 / flags zero / distance 0 yd=true

ON-WIRE FRAME
UNMEASURED — do not substitute the socket write for a captured TCP frame
```

## Prior-run reconciliation

| Prior claim | X0-X3 reconciliation |
|---|---|
| SPEC-21 P2: one proven attack, zero receive/dispatch/handler debug lines | Still valid negative server-log evidence; deployed normal path has no unconditional admission log. |
| Client wire tap showed byte-correct body | Strengthened: X1 proves the exact post-encryption 14-byte socket write and SHA-256 after successful write+flush. |
| Packet loss versus unlogged server predicate remained indistinguishable | Still unresolved because X2 could not capture with Linux `sudo -n` or Windows Administrator-only engines. |
| GM mode, identity, framing, range, facing, mount, stale combat, dead/absent target, HOME-motion evade excluded | Unchanged; accepted X1 precondition is GM off, alive, flags zero, distance 0 yd. |
| SPEC-21 P3/P4 | Remain queued behind this missing on-wire discriminator. |

## Deployed server candidates if a later capture proves presence

- `Server/WorldSocket.cpp:98-183`: socket read/decrypt/framing/session admission.
- `Server/WorldSession.cpp:277-331`: opcode parse and processing-queue admission.
- `Server/Protocol/Opcodes.cpp:398-401`: opcode 321 registration.
- `Server/WorldSession.cpp:518-549,1250-1313`: anti-flood and logged-in state gates.
- `Handlers/CombatHandler.cpp:32-62`: typed target lookup and handler branches.
- `Objects/Unit.cpp:4721-4804`: silent `Unit::Attack` predicates and success send.

## Ruling options for Nico

1. **Windows capture-only rerun (smallest next step).** Run the same accepted X1
   scenario from an Administrator-elevated Codex/session so `pktmon` or `netsh`
   can filter `192.168.0.2:8085`. Extract the two frames, revert filters, and
   delete ETL/PCAP immediately. No server mutation or new client code is needed.
2. **Linux capture-only authorization.** Issue a new order granting a narrowly
   scoped noninteractive tcpdump capability to `wowvmangos`, then run one
   world-port-filtered repeat and remove/revoke the grant. This changes server
   authorization state and was not permitted by SPEC-22.
3. **Deeper server instrumentation.** Only if a capture proves the attack frame
   arrives, rule on gdb attach or a temporary instrumented rebuild on a COPY at
   the cited WorldSocket/WorldSession/handler boundaries. Current server code
   and deployment must remain untouched until that ruling.

If the captured attack frame is absent while the adjacent chat frame is
present, characterize the client/LAN send anomaly before any server work. If
both are present and server debug remains silent, the original SPEC-22
server-side pre-handler/unlogged-predicate HARD STOP becomes fully supported.

## Scope and cleanup

No raw capture exists. Netsh status is stopped; no pktmon/netsh filter was
changed; the temporary preflight helper was removed. No server code, database,
persistent server config, client combat behavior, error display, or F3-F6
behavior changed. P3/P4 remain queued.
