# NIGHT_03 resolved VMaNGOS GM syntax

This table is run-scoped setup. Later class legs reuse it and do not rediscover
the same commands. The deployed realm's `.help` result is preferred where it
was captured; otherwise the checked read-only VMaNGOS source is the authority.

| Purpose | Exact command | Verification |
|---|---|---|
| Enable GM mode | `.gm on` | deployed response `GM mode is ON`, all NIGHT_03 live packets |
| Disable GM mode | `.gm off` | deployed response `GM mode is OFF`, all accepted GM-off cells |
| Spawn test creature | `.npc spawn add 6` | deployed add/delete proof; `Chat.cpp:664`, `CreatureCommands.cpp:928` |
| Delete selected test creature | `.npc spawn delete` | deployed add/delete proof; `Chat.cpp:666`, `CreatureCommands.cpp:1001` |
| Make selected creature idle | `.npc set movetype idle` | deployed `.help npc set` capture `gm-npc-set-help`; `Chat.cpp:680`, `CreatureCommands.cpp:1378` |
| Set passive reaction | `.npc set reactstate 0` | deployed `.help npc set` capture `gm-npc-set-help`; `Chat.cpp:683`, `CreatureCommands.cpp:788` |
| Disable combat movement | `.npc allowmove off` | deployed `.help npc allowmove` capture `gm-npc-allowmove-help`; `Chat.cpp:692`, `CreatureCommands.cpp:1485` |
| Disable melee attacking | `.npc allowattack off` | deployed `.help npc allowattack` capture `gm-npc-allowmove-help`; `Chat.cpp:693`, `CreatureCommands.cpp:1511` |
| Refill selected target health | `.modify hp 1000000 1000000` | deployed response `You changed HP of <creature> to 1000000/1000000`; `Chat.cpp:582`, `UnitCommands.cpp:2283` |
| Clear selected target auras | `.unaura all` | `Chat.cpp:1231`, exact `all` branch at `UnitCommands.cpp:1064` |
| Refill player mana | `.modify mana 1000` | deployed response `You changed MANA ... to 1000/1000` |
| Clear player cooldowns | `.cooldown clear` | deployed response `All spell cooldowns removed` |
| Move to exact world point | `.go xyz X Y Z map` | deployed teleport controls in every NIGHT_03 matrix run |

Read-only source snapshot: `.reference-vmangos-core/src/game/Chat/Chat.cpp`,
`.reference-vmangos-core/src/game/Commands/CreatureCommands.cpp`, and
`.reference-vmangos-core/src/game/Commands/UnitCommands.cpp`. Deployed evidence
is under `live-runs/N3-0-3-mage-20260801-195500/` and the bounded Fireball layer-2
proof is `fireball-layer2-proof-v6/`.
