# T2 positive-proof acceptance — 20260731-154700

All four SPEC-18 G2 proofs passed through the real chat, movement, descriptor,
and protocol-runner paths.

| Proof | Result | Server/client evidence |
| --- | --- | --- |
| `.gps` response | PASS | Server returned map 0 and `X: -8949.950195 Y: -132.492996 Z: 83.529526 Orientation: 2.722710`. Runner: 4/4 PASS. |
| `.go` position mutation | PASS | `TeleportApplied counter=1;position=-8970|-132.493|83.53`; the movement trace records the requested X/Y on the same live tick. Runner: 6/6 PASS. |
| Identified spawn within 3 yd | PASS | Spawn `0xF13000000604A26F`, entry 6, DB GUID 303727, measured distance 0, `within3=true`; `.npc info` independently identified Entry 6; cleanup removed the exact descriptor. Runner: 10/10 PASS. |
| Descriptor-confirmed death | PASS | Spawn `0xF13000000604A270`, entry 6, DB GUID 303728, measured distance 0; `.die` was followed by `waitdeath ... health=0`; cleanup removed the exact descriptor. Runner: 13/13 PASS. |

The spawn resolver sorts only newly observed descriptors by measured distance.
The selected ordinal in both creature proofs is therefore the zero-distance
server-created specimen, not a pre-existing GUID-sorted nearby creature.

## Actual versus predicted

```text
PREDICTED .gps: server response
ACTUAL: PASS
PREDICTED .go: requested position visible in movement trace
ACTUAL: PASS; exact requested X/Y within one live tick
PREDICTED controlled spawn: response-identified and <= 3 yd
ACTUAL: PASS; entry 6, distance 0, within3=true, exact cleanup
PREDICTED controlled death: descriptor-confirmed
ACTUAL: PASS; exact target health=0 before cleanup
RESULT: T2 ACCEPTED; T3 gate opens
```

The committed scenario scripts and run-dated runner/verdict artifacts are the
machine-verifiable record. Their staged-byte SHA-256 values are frozen in
`live-runs/manifests/T2-20260731-154700.sha256`.
