# P2 server observation — bounded hard stop (2026-07-31 22:00)

## Outcome

P2 reached the deployed VMaNGOS Remote Administration service at
`192.168.0.2:3443` with the authorized TEST credentials read at runtime from
`MSUIClient/client-config.json`. Credentials were never printed or committed.
The SOAP port 7878 was closed. SSH as `wowvmangos@192.168.0.2` was refused
before command execution, so no Linux config or log file was read.

The server initially reported console log level 2, file log level 2, and the
combat filter OFF. Through RA, the bounded observation set console level 3 and
combat filter ON. A fully proven GM-OFF attack was repeated while the RA
connection listened for 35 seconds. RA relayed no live process log lines. The
server still reported file level 2 because this build's `server log level`
command changes only the console sink. The observation was then reverted and
verified: console 2, file 2, combat filter OFF.

Authoritative VMaNGOS commit `db7450c6e4cc255cffa2620e5d0dd7d2f179d2d2`
confirms the deployed behavior shape:

- `src/game/Commands/ServerCommands.cpp:575-585` queries both levels but sets
  only `sLog.SetConsoleLevel`;
- `src/game/Chat/Chat.cpp:960-980` exposes the level/filter commands to RA;
- `src/game/Handlers/CombatHandler.cpp:32-62` and
  `src/game/Objects/Unit.cpp:4703-4786` contain the H0 paths but do not log
  normal handler entry or individual silent returns;
- `LOG_FILTER_COMBAT` covers attack outcomes, not attack-handler admission.

Therefore the remote console cannot supply the required handler proof. The
world log is on the separate Linux host and cannot be retrieved with the
available OS credentials.

## Additional server-state discriminator

Before stopping, a second bounded run exhausted observable silent predicates:

- server response: `You are not mounted so you can't dismount`;
- `.combatstop TEST` executed before the fresh spawn, clearing server-owned
  player attack state and hostile references;
- fresh target `0xF13000000604A286` was present/alive, faction 25, initially
  flags 0, and selected at 0 yd;
- server `.npc aiinfo`: `RANDOM_MOTION_TYPE (1)`, not HOME motion;
- server `.unit info`: target victim/target GUIDs zero, death state 0;
- a Northshire guard successfully started combat against this same creature
  immediately before the player send, proving the creature was server-found,
  alive, and accepted as a victim by `Unit::Attack` in that interval;
- at the player's send, the same descriptor remained alive at 1.8917537 yd;
  its `0x00080000` unit flag reflected the now-active guard combat;
- the player's correctly framed `CMSG_ATTACKSWING` again received no response.

This excludes GM state, stale/absent identity, distance, player mount state,
stale player attack state, dead target, and the ordinary HOME-motion evade
case. It strengthens the remaining fork to either pre-handler packet dispatch
or a deployed server predicate/state not observable from client/RA commands.
It does not name a root cause.

## Actual versus predicted

```text
PREDICTED P2: raised server logging yields a handler/path line for the repeat
ACTUAL: console level 3 + combat filter ON confirmed; RA relayed zero log lines;
        file level stayed 2 and the Linux world log is inaccessible
PREDICTED predicate reset: an observable Unit::Attack precondition explains silence
ACTUAL: mounted/stale-attack/HOME-motion/dead/absent/range cases excluded; still silent
RESULT: P2 BLOCKED at required world-log capture; P3 and P4 NOT STARTED
```

No server code, database, client combat behavior, error display, or F3–F6 work
changed. The temporary server logging state was fully reverted.

The four boundary gates passed: Debug build (0 warnings/errors), combat wire
foundation, portrait camera (1224/1289/56), and movement audit.

## Access required to resume

Provide either:

1. read-only SSH access for this Windows environment to the Linux VMaNGOS user
   (an authorized key is preferred), sufficient to inspect the mangosd command
   line/config and read or follow its world/console log; or
2. a Linux-side capture of the mangosd console/world log covering one repeated
   P2 attack while console debug is enabled. The capture must include the
   server timestamp and any receive/dispatch/attack-handler lines for TEST.

If the deployed binary logs no normal `CMSG_ATTACKSWING` admission even at
debug, that negative result must be returned verbatim; it is evidence that the
specified logging mechanism cannot discriminate the branch. A new order would
then be required before any server debugger or server instrumentation.
