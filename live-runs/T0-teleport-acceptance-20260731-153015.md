# T0 same-map teleport acceptance — 2026-07-31 15:30:15

## Protocol layout verification

- Project 1.12 source: `MSUIClient/Net/Opcodes.cs:71` fixes the shared opcode at
  `0x00C7`; `MSUIClient/Net/MovementInfo.cs:77-101` is the established build-5875
  MovementInfo decoder.
- Benilla: `crates/benilla-protocol/src/messages/parse.rs:201-210` reads packed
  GUID, counter, then MovementInfo; `src/messages/client.rs:293-300` writes full
  GUID, counter, then client time; `src/world/session.rs:548-554` sends that body;
  `tests/client.rs:182-184` fixes the golden body bytes.
- VMaNGOS development source, read-only from the official repository:
  `src/game/Movement/MovementPacketSender.cpp:186-218` sends packed GUID,
  incrementing counter, and destination MovementInfo; `src/game/Server/Packets/Movement.cpp:16-23`
  reads the client full GUID, counter, and time; `src/game/Handlers/MovementHandler.cpp:200-224`
  verifies the GUID and pending counter before executing the near teleport.

## Actual versus predicted

| Acceptance row | Predicted | Actual | Result |
|---|---|---|---|
| Requested pose | `-8970, -132.493, 83.53`, applied within one tick; orientation retained | packet received at verdict time 12.811; `TeleportApplied` at 12.827 reports the exact requested pose and orientation `2.7227101`; trace frame 129 at trace time 2.145329 is the first destination row (one 18.613 ms tick), and frame 130 retains `aimYaw=bodyYaw=2.7227101` | PASS |
| Acknowledgement | Exactly one reply with the same full GUID and counter | incoming counter 1: `010101000000000000006757C70000280CC6357E04C35C0FA742E2402E4011000000`; sole reply: `0100000000000000010000001B320000` = full GUID 1, counter 1, nonzero client time | PASS |
| 30-second stability | No position snap-back | 1,846 idle trace rows from t=2.212484 through t=32.986892 have `posX=-8970` and `posY=-132.493` exactly; Z settles locally onto terrain, with no server relocation | PASS |
| Movement at destination | Normal real-input run and server acceptance | start, two heartbeats, and stop were sent at destination; the final `.gps` server response is `-8976.505859,-129.595657,84.061958`, matching the final trace pose within float precision | PASS |
| Kinematic bands | All current-tree run-start-stop rows PASS | 7/7 PASS: maxSpeed 7; stopDistance 0; displacement 1 tick; clip latency 0 ms; stallWindows 0; hardCuts 0; substitutions 0 | PASS |
| Runner hygiene | No false bootstrap-position finding after scenario teleport | 13/13 protocol steps PASS; no `BootstrapTeleportUnconfirmed` verdict | PASS |

The initial live bootstrap is also a same-map teleport and independently paired
counter 0 once in each direction. Thus both observed server requests have exactly
one matching client acknowledgement.

Far-map transfer (`SMSG_TRANSFER_PENDING` / `SMSG_NEW_WORLD`) was not changed or
tested in T0. Its existing path remains outside this order and is not claimed.

## Standard four gates

- Debug build: PASS, one pre-existing CA2014 warning.
- Combat/movement/targeting/wire foundation: PASS, including the new exact
  full-GUID/counter teleport acknowledgement fixture.
- Portrait-camera: PASS (1,224 / 1,289 / 56).
- `move-audit-check`: PASS.

