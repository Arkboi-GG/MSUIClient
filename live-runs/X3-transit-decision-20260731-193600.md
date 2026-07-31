# SPEC-22 X3 transit decision table — unresolved capture boundary

## Decision table

| Client flush | On-wire measurement | Server debug handler evidence | Required branch | Actual |
|---|---|---|---|---|
| false | N/A | N/A | client defect HARD STOP | Not selected: X1 proves write+flush. |
| true | absent | N/A | LAN/socket anomaly characterization | Not selected: absence was not measured. |
| true | present | absent | server pre-handler/unlogged predicate HARD STOP | Not selected: presence was not measured. |
| true | unmeasured | absent | capability/evidence HARD STOP | **Selected.** Capture requires Linux sudo or Windows Administrator elevation unavailable to this session. |

The fourth row is an explicit evidence-capability outcome, not an invented
extension of the three causal rows. It refuses to equate a successful local
socket write with a captured network frame.

## Actual versus predicted

```text
PREDICTED X1 discriminator: attack flushed or not flushed
ACTUAL: flushed=true, bytes=14, exact post-encryption hash and bytes frozen

PREDICTED X2 discriminator: attack present or absent on wire
ACTUAL: UNMEASURED; tcpdump sudo -n denied, pktmon/netsh elevation denied,
        deployed built-in logger covers movement-anticheat penalty history only

PREDICTED X3 causal verdict: choose exactly one of three transit rows
ACTUAL: no causal row is supportable without fabricating the on-wire fact
RESULT: TRANSIT_UNRESOLVED_CAPTURE_PRIVILEGE; HARD STOP
```

## Exact client socket bytes (not claimed as captured frames)

```text
Delivered .gps control:
  CMSG_MESSAGECHAT bytes=19
  sha256=80254993e43f40b1b225ddd72c330c80e1d7df63e9d7c8f444fecf5fbe36ffea
  post-encryption write=3F85FEA7407400000000070000002E67707300

Attack:
  CMSG_ATTACKSWING bytes=14
  sha256=784feef9f39b41853082ecfd8bb6dd47d801f1d3e7143986d79abb317c336420
  post-encryption write=8263DEEECF998CA20406000030F1
  plaintext body=8CA20406000030F1
```

## Deployed dispatch/discard candidates

| Site | Deployed file:line | Candidate behavior | Current classification |
|---|---|---|---|
| Socket header/body read | `Server/WorldSocket.cpp:98-148` | Read error, decrypt/framing size/opcode rejection, or body read failure before packet completion. | On-wire capture required. Malformed headers close/log at 116-120, so continued session makes that subcase unlikely. |
| Auth/session admission | `Server/WorldSocket.cpp:153-183` | Closing socket or missing authenticated session prevents `QueueBinaryPacket`. | Adjacent delivered chat and continued session make missing auth/closing unlikely. |
| Opcode lookup/parser | `Server/WorldSession.cpp:305-331` | Unhandled opcode is logged/skipped; registered packet is parsed and verified before queue. | Opcode 321 is registered; parser/framing still needs ingress evidence. |
| Queue strategy | `Server/WorldSession.cpp:277-302` | Invalid processing strategy is logged/skipped; otherwise packet enters its processing queue. | ATTACKSWING is `PACKET_PROCESS_SPELLS`; queue admission is unobserved. |
| Opcode registration | `Server/Protocol/Opcodes.cpp:398-401` | Opcode 321 maps to `STATUS_LOGGEDIN`, `PACKET_PROCESS_SPELLS`, `HandleAttackSwingOpcode`. | Mapping confirmed in deployed checkout. |
| Flood gate | `Server/WorldSession.cpp:518-530,1250-1313` | `AllowPacket` can break processing after anti-flood threshold. | Possible pre-handler gate; no X2 frame/queue evidence. |
| Session status | `Server/WorldSession.cpp:535-549` | Missing player logs at debug; player present but `!IsInWorld()` silently skips `STATUS_LOGGEDIN` packet. | Silent pre-handler candidate, though adjacent delivered control and live world state argue against it. |
| Attack handler | `Handlers/CombatHandler.cpp:32-62` | Non-unit GUID silently returns; lookup/friendly/flags/dead send ATTACKSTOP; valid target calls `Unit::Attack`. | GUID type and response-producing branches excluded by prior evidence; handler entry itself unmeasured. |
| Unit attack law | `Objects/Unit.cpp:4721-4804` | Self/deleted/dead/out-of-world/mounted/GM-victim/evade/already-attacking paths can return silently; success sends melee attack start. | Premise excludes mounted, stale combat, dead/absent, HOME-motion evade, and identity errors; deeper server observation remains a Nico ruling. |

No server instrumentation was added. No database, server code, persistent
configuration, client combat behavior, error display, or F3-F6 behavior
changed. SPEC-21 P3/P4 remain queued.
