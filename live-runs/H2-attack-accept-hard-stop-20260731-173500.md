# H2 — attack-acceptance matrix HARD STOP (2026-07-31 17:35)

## Result

The required wild positive control fails on the current tree. The exact
pre-existing object-store target from run 16 was rediscovered alive at 2.1473
yd with GUID `0xF13000007900B1EF`, entry 121, faction 17, and unit flags zero.
The real selection and attack packet bodies were both:

```text
EF B1 00 79 00 00 30 F1
```

No `SMSG_ATTACKSTART`, `SMSG_ATTACKSTOP`, or attack-error opcode followed the
attack during the two-second window. This is a binding H2 positive-control
failure, so the chain stops before H3.

## Identity → validity → framing matrix

| Cell | Identity source | Descriptor evidence | Sent body | Server result | H0 law landing |
|---|---|---|---|---|---|
| Wild positive control | Client object store | `0xF13000007900B1EF`; entry 121; faction 17; flags 0; alive; 2.1473 yd | `EF B1 00 79 00 00 30 F1` | FAIL: no attack-family response | Typed/found candidate reaches the H0 silent-return family; lookup miss and the handler's friendly/spawning/not-selectable/dead rows would have sent attack-stop, while success would have sent attack-start. The exact silent member cannot be distinguished without server-process state. |
| Spawn / object-store GUID | Client object store | `0xF13000000604A27F`; entry 6; faction 25; flags 0; alive; 0 yd | `7F A2 04 06 00 00 30 F1` | FAIL: no attack-family response | Same H0 silent-return family. The server independently reported entry 6 / low GUID 303743 for this object. |
| Spawn / response-derived GUID | Server `.npc info` | Same `0xF13000000604A27F` | N/A duplicate cell | N/A by SPEC-20: H1 proved response and object-store GUIDs identical | No independent identity variable exists. |

The spawn cell was removed by exact server identity after the run. An earlier
matrix calibration chose friendly/guard descriptors because the current arena
contains no locally classified hostile candidate; those attempts are retained
as runner results and are not substituted for the positive control.

## Archived contradiction and bisect

The archived run-16 verdict is unambiguous for the same full GUID:

```text
time=6.865 AttackSwingSend target=0xF13000007900B1EF
time=6.949 AttackStartReceive target=0xF13000007900B1EF attacker=0x1
```

The current control sent at time 13.671 and received nothing. This establishes
a live regression relative to the committed archive without relying on target
construction or packet framing.

Commit `6a5e73a` (the accepted run-17-era diagnosis tree, before T0) was built
and run in an isolated worktree against the same live server and TEST account.
It selected the exact GUID and emitted `AttackSwingSend`, but received no
attack-family response. A second isolation run changed its bootstrap vantage
to the archived target so it had only the historical single unacknowledged
bootstrap teleport; it again selected `0xF13000007900B1EF`, sent once, and
received nothing.

```text
PREDICTED if T0 were first bad: current FAIL; run-17-era tree PASS
ACTUAL: current FAIL; run-17-era tree FAIL today
DIFF: T0 cannot be marked first bad from a reproducible code-only bisect
ARCHIVE: the same run-16 tree/path/GUID previously PASSed
CONCLUSION: regression depends on live server/session state not captured by
            source revision alone; exact silent H0 precondition is undetermined
```

T0 changes teleport receive/apply/ack state but does not touch
`CMSG_ATTACKSWING` construction. The isolation result does not erase the
archived pass; it prevents a false attribution to T0. No production change was
made.

## Runner/instrument notes

H2 added object-store selectors that expose the chosen descriptor GUID,
entry, faction, flags, reaction classification, position, and distance. These
are protocol instruments only. Calibration outcomes are preserved:

- nearest client-attackable selected entry 823 / faction 12 / flags
  `0x08001300`; no response, therefore not an admissible hostile control;
- GUID-sorted arena control selected entry 69 / faction 32 / flags
  `0x08000000`; no response;
- strict client-hostile discovery found no candidate in the arena;
- a bounded 12-descriptor GUID-sorted probe produced 12 sends and zero
  attack-family responses;
- the second archived target (entry 122 / low GUID 59385) stalled while its
  remote cell streamed and was terminated at the bounded host timeout; it
  produced no protocol artifact and supports no acceptance claim;
- entry-121 after `.combatstop` / `.gm off` lost a usable client selection
  before the attack and is retained as a non-qualifying runner result.

## Chain disposition and next order

- H3: NOT STARTED. The control failure invokes SPEC-20's regression path;
  neither a runner identity fix nor any combat behavior fix is authorized.
- H4: NOT REACHED. This H2 hard stop is the final packet for this order.
- Required next diagnosis: capture server-process attack-handler state for the
  exact player/target pair (or an equivalent server-side opcode trace), then
  identify which H0 silent predicate changed between the archived pass and
  today's run. This requires a new signed order and access/proof from the Linux
  server side.

## Primary artifacts

- `live-runs/H2-20260731-165000/`
- `dumps/combattrace-H2-wild-object-store-20260731-165000-20260731-163948.csv`
- `dumps/combattrace-H2-spawn-object-store-20260731-165000-20260731-163951.csv`
- `dumps/wire-20260731-163948.wlog` and `.txt`
- `live-runs/H2-wild-archived-20260731-171000/`
- `dumps/combattrace-H2-wild-archived-entry121-20260731-171000-20260731-164508.csv`
- `dumps/wire-20260731-164508.wlog` and `.txt`
- `live-runs/H2-bisect-run17-20260731-171500/`
- `live-runs/H2-bisect-run17-single-teleport-20260731-173000/`
