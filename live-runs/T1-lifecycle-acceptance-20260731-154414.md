# T1 creature lifecycle acceptance — 2026-07-31 15:44:14

## Command discovery

The server's live `.help npc spawn` response enumerated `add`, `addentry`,
`delete`, `info`, `set`, and `move`. Leaf commands have no prose help in this
deployment. The matching official VMaNGOS development command table was read
read-only: `src/game/Chat/Chat.cpp:662-670` binds `.npc spawn add`,
`.npc spawn delete`, and `.npc spawn info`; `Chat.cpp:687-710` binds `.npc info`
and `.npc summon`. `src/game/Commands/CreatureCommands.cpp:928-976` shows that
spawn-add creates and saves the requested entry at the player pose, while
lines 1001-1046 show selected/guid deletion and removal.

The Linux server's own source checkout was not filesystem-accessible from this
Windows session: port 22 is reachable, but no Linux login credentials or source
path were provided. The live server command tree and behavior match the official
source exactly; this provenance limitation is recorded rather than disguised.

## Actual versus predicted

| Row | Predicted | Actual | Result |
|---|---|---|---|
| Spawn syntax | Server-supported creature creation | `.npc spawn add 6`; new GUID `0xF13000000604A26E` / DB GUID 303726 | PASS |
| Client observation | New entry 6 within 3 yd | `SpawnObserved`: entry 6, distance `1.7489377`, `within3=true` | PASS |
| Server identity | Response identifies selected creature | `.npc info`: `Player selected: Creature (Entry: 6 Guid: 303726)`, plus `Entry: 6`, display 10913, level 2 | PASS |
| Cleanup | Selected spawn demonstrably removed | `.npc spawn delete` response `Creature Removed`; `waitgone` reports the exact GUID with `descriptorPresent=False` | PASS |
| Deck replacement | No active invalid `.npc add` / `.npc delete` rows | `dummy.txt`, `reset.txt`, `cb-protocol.txt`, and `cb1-matrix.txt` use the verified `.npc spawn ...` forms | PASS |

Two earlier validation attempts are retained as results. They exposed descriptor
arrival ordering and the unsafe assumption that only <=3-yard descriptors should
be tracked. Each leaked throwaway was removed by exact DB GUID through the server
command path (303723, 303724, 303725) before the passing run. The runner now uses
`SpawnObserved` as identity authority, waits up to five seconds for selection,
tracks every new descriptor, records `within3` separately, and can prove removal
with `waitgone`. These are protocol-instrument changes, not combat behavior.

## Standard four gates

- Debug build: PASS (known CA2014 may appear on a full rebuild).
- Combat/movement/targeting/wire foundation: PASS.
- Portrait-camera: PASS (1,224 / 1,289 / 56).
- `move-audit-check`: PASS.
