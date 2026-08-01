# NIGHT_01 1-4 — repeatable combat regression pack

Status: `CLOSED-PASS`

## Actual versus predicted

```text
PREDICTED: reusable fresh-target protocol proves spawn identity, Z0 gate, socket
           write, adjacent delivered control, exact cleanup, and verdict rows
ACTUAL: 31/31 protocol steps PASS; SpawnObserved identity 0xF13000000604A29C;
        gate PASS at alive 100/100, flags 0/0, GM off, distance 0;
        .gps socket write at t=5.242 and attack socket write at t=5.255;
        delivered .gps response observed; target descriptor absent after cleanup
RESULT: PASS

PREDICTED: post-encryption attack write is recorded, not inferred
ACTUAL: opcode 0x0141, 14 bytes, flushed=true,
        SHA-256 402ccbe93c48283d272761956017df71f075ed51fb1ab4ffee52787861e41d1e
RESULT: PASS

PREDICTED: combat audit pairs one start/one send and one cancel/one stop
ACTUAL: both pairing checks PASS; legal transitions PASS; cadence and one-shot
        remain NO_DATA because no player swing was received
RESULT: PASS / honest NO_DATA
```

The reusable protocol is
`scenarios/combat/combat-regression-fresh-target.txt`. Run evidence is under
`live-runs/N1-14-combat-regression-20260731-232500/`. This is a scripted
protocol baseline only; no cohort or expected-list was regenerated.

Boundary gates: Debug build 0 warnings / 0 errors; combat-wire PASS with only
the established CA2014 dependency warning; portrait-camera PASS with 10,534
specimens and controls 1,224 / 1,289 / 56; move-audit-check PASS.

