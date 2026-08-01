# NIGHT_01 1-3 — attack-error display readiness

Status: `CLOSED-PASS`

## Actual versus predicted

```text
PREDICTED decode: five payload-free build-5875 attack-error opcodes
ACTUAL: all five are dispatched by opcode and mapped by a total, checked law
RESULT: PASS

PREDICTED display: server error produces copyable red player-facing text
ACTUAL: receive path calls the existing red center-text surface and emits a
        combat verdict containing symbolic opcode, numeric opcode, byte count,
        target GUID, and exact text; verdict rows are click/copy enabled
RESULT: PASS (mechanical combat-wire instrument)

PREDICTED live error during item 1-2
ACTUAL: zero attack-error packets appeared in all clean 1-2 sends
RESULT: NO_DATA; no synthetic error was invented
```

Text law:

- `SMSG_ATTACKSWING_NOTINRANGE` → `You are too far away!`
- `SMSG_ATTACKSWING_BADFACING` → `You are facing the wrong way!`
- `SMSG_ATTACKSWING_NOTSTANDING` → `You must be standing to attack!`
- `SMSG_ATTACKSWING_DEADTARGET` → `Your target is dead!`
- `SMSG_ATTACKSWING_CANT_ATTACK` → `You can't attack that target!`

Boundary gates: Debug build PASS (only established CA2014; 0 errors),
combat-wire PASS including all five text assertions, portrait-camera PASS with
10,534 specimens and controls 1,224 / 1,289 / 56, move-audit-check PASS.

