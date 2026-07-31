# P0 — attack precondition truth (2026-07-31 18:00)

## Actual versus predicted

```text
EXPECTED: prove arena position, server GM state, and target truth at send
ACTUAL: all three proven; the GM-OFF send was nevertheless silent
RESULT: P0 complete; P1 opens, with P2 required if P1 reproduces silence
```

The login notification at time 0.411 reported `GM mode is ON`, before the
protocol issued a GM-state command. Server responses then confirmed OFF at
6.447, status OFF at 7.498, ON at 8.450, status ON at 9.483, OFF at 11.852,
and status OFF at 12.869. Thus the account entered this session GM-ON and the
attack was sent in server-confirmed GM-OFF state.

The movement trace remained at the arena and ended at
`-8949.95|-132.493|83.529526`. Server `.gps` independently reported
`-8949.950195|-132.492996|83.529526`.

Immediately before the real send, the new report=act verdict recorded:

```text
AttackPrecondition target=0xF13000000604A282
player=0x1; position=-8949.95|-132.493|83.529526; gmMode=false
present=true; visible=true; alive=True; health=100; maxHealth=100
unitFlags=0x00000000; dynamicFlags=0x00000000; faction=25; entry=6
distance=0.66123295; targetPosition=-8949.507|-132.00243|83.52796
```

The actual `CMSG_ATTACKSWING` body was
`82 A2 04 06 00 00 30 F1`. No attack-start, attack-stop, swing, or attack
error followed during the two-second window. The fixture was deleted by exact
spawn identity and descriptor absence was confirmed.

## GM.LoginState provenance

A read-only SSH attempt used the configured host identity
`wowvmangos@192.168.0.2` with batch authentication and was refused before any
remote command ran (`Permission denied (publickey,password)`). Therefore zero
server config files were read and no installed value is claimed.

The authoritative VMaNGOS development template at
`src/mangosd/mangosd.conf.dist.in:2211-2215,2299` defines
`GM.LoginState = 2`, meaning last saved state; it does not prove the deployed
override. The live server notification does prove the effective result for
this login: ON. That runtime fact is the acceptance authority for P1.

## Instrument delta

`AttackPrecondition` is emitted immediately before the existing
`WorldSession.AttackSwing` call, after the same object-store lookup and
`CanAttack` decision used by production. GM state is updated only from decoded
server response text. No combat decision, input path, packet, or timing law
changed.

Primary artifacts:

- `live-runs/P0-20260731-180000/runner-20260731-171044.csv`
- `live-runs/P0-20260731-180000/verdicts-20260731-171044.txt`
- `dumps/movetrace-P0-arena-position-20260731-180000.csv`
- `dumps/combattrace-P0-precondition-truth-20260731-180000-20260731-171053.csv`
- `dumps/wire-20260731-171053.wlog` and `.txt`
