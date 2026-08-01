# SPEC-24 Z1 — bounded all-components pktmon capture

Run date: 2026-07-31 (America/New_York)  
Capture window: 20:35:26–20:36:04 (38 seconds; within the 60-second bound)  
Endpoint filter: TCP 192.168.0.2:8085  
Engine: `pktmon start --capture --pkt-size 0` with no component restriction

## Predicted versus actual

Predicted: elevation and the endpoint-filtered, all-components/full-packet capture pass; one fresh entry-6 target passes the mechanical gate; this run's post-encryption chat and attack write substrings are searched with component deduplication and split/coalesced-stream tolerance; matched traffic is checked for ACK coverage, retransmission, and RST; transient capture files are hashed and deleted before the boundary.

Actual: elevation passed (`net session` exit 0, High Mandatory Level); pktmon monitored `All` components and reported no ETW events lost. Fresh target `0xF13000000604A28F` passed on attempt 1: present, visible, alive 100/100, `dynamicFlags=0`, `unitFlags=0`, GM off, distance 0. The runner passed 26/26. Both socket writes flushed 8 ms apart. The exact 14-byte encrypted attack write was retained on the wire, repeated at multiple pktmon component appearances but representing one TCP segment. A server packet ACKed beyond the attack sequence end 585.233 ms later. No RST and no retransmission of the attack sequence were observed. The exact 19-byte chat write was not retained in the formatted capture, although its delivered-control responses were received 131 ms after its socket write. Because the attack payload is present, Z2's strict "both payloads absent" entry condition is false.

## Mechanical gate and delivered control

- fresh target: `0xF13000000604A28F`, entry 6
- gate: `AttackPreconditionGatePass`, `packetConstructed=true`
- gate snapshot: player `0x0000000000000001`; target present/visible/alive; health 100/100; unit/dynamic flags exactly zero; GM source `server-response`, GM false; distance 0
- `.gps` socket write: time 7.191, 19 bytes, SHA-256 `530e593fa1c115c94f59fd49a3a416b987716f0d76ff24c9d7419d07f33cd83f`, flushed true
- attack socket write: time 7.199, 14 bytes, SHA-256 `1654b77ff91a6e47b4f3b402c46ad0373eb423875b24f4d709fed06f0507a24e`, flushed true
- `.gps` delivery: server chat responses at time 7.322, including map/position; control-to-attack socket-write interval 0.008 seconds

## Attack frame — exact match, deduplicated

This run's attack substring was `92E386E4B7428FA20406000030F1`. The first appearance was:

```text
20:35:54.147617900 PktGroupId 562949953421516, Direction Tx, Type Ethernet,
Component 88, Edge 1, OriginalSize 68, LoggedSize 68
192.168.0.38.56730 > 192.168.0.2.8085: Flags [P.],
seq 817834466:817834480, ack 39633848, win 254, length 14
0x0000: 5811 221a 6d63 1475 5bd3 e681 0800 4500
0x0010: 0036 5af2 4000 8006 0000 c0a8 0026 c0a8
0x0020: 0002 dd9a 1f95 30bf 29e2 025c c3b8 5018
0x0030: 00fe 81a1 0000 92e3 86e4 b742 8fa2 0406
0x0040: 0000 30f1
```

The same logical segment appeared ten times across pktmon's component graph. Payload-retaining component IDs were 88, 39, 40, 41, 42, and 14 (Ethernet/Wi-Fi edges). These appearances count once. There was no second transmission of TCP range 817834466:817834480.

## Server ACK coverage

The first captured server packet covering the attack ended at ACK 817834507, which is 27 bytes beyond attack end 817834480:

```text
20:35:54.732850400 PktGroupId 535, Direction Rx, Type WiFi,
Component 14, Edge 1, OriginalSize 204, LoggedSize 204
192.168.0.2.8085 > 192.168.0.38.56730: Flags [P.],
seq 39645285:39645415, ack 817834507, win 63, length 130
0x0000: 8802 3c00 1475 5bd3 e681 0cef 1596 9fa5
0x0010: 5811 221a 6d63 204a 0000 aaaa 0300 0000
0x0020: 0800 4500 00aa 4086 4000 4006 784f c0a8
0x0030: 0002 c0a8 0026 1f95 dd9a 025c f065 30bf
0x0040: 2a0b 5018 003f ff31 0000 589c 885b df44
0x0050: 3801 2b01 30f1 9180 0bc6 43d5 18c2 f6b6
0x0060: b842 165b a301 0000 0100 0015 1500 0004
0x0070: 0000 00f3 4a0b c630 8717 c2dd a7b5 4216
0x0080: 0840 ff0e 0080 ff06 00c0 ff08 e19b 3adf
0x0090: 4538 012b 0130 f137 5a0b c63e 1161 c2ce
0x00a0: 2aaf 4217 5ba3 0100 0001 0000 4b0c 0000
0x00b0: 0400 0000 6e50 0bc6 ddba 45c2 c271 af42
0x00c0: 0a98 4000 0a58 0000 0528 0000
```

ACK latency from the first attack appearance was 0.5852325 seconds. No TCP RST was present. No retransmission of the attack payload/range was present.

## Capture health and drops

Pktmon formatted 800 packets, recorded two TCP-stack drops, and reported `No events lost`. Both drops were inbound `INET: duplicate segment` records from the server, about five seconds before the attack, with unrelated ranges 39630318:39630330 and 39630479:39630526. They are not the attack segment, do not affect its ACK proof, and are not capture-buffer loss.

## Transient-file hashes before deletion

- ETL: `82c86670e0e20d60405c19caca84fc6861d1976da16fe86ff4dee8b251389711`
- PCAPNG: `45a5d4dff565d05625df064978803f04efe452fca93a383f1c688846a90195a7`
- formatted text: `479d06d6b790b3023f830cdbb6ddfb813c934d4f8be507a5c26d53aad49ba8f6`

These hashes identify transient evidence only. The ETL, PCAPNG, and formatted text are required to be deleted before the Z1 boundary; the extracted frame text above survives. The relay lifecycle records filter removal and `Packet Monitor is not running` after capture.

## Z1 outcome

`ATTACK_PRESENT_AND_ACKED`. Z2 is not authorized to run because its if-and-only-if precondition (both socket-write substrings absent) is false. Causal selection remains for Z3, reconciled with the bounded SPEC-21 P2 server-silence proof.
