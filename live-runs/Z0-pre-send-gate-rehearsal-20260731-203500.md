# SPEC-24 Z0 - mechanical pre-send gate and uncaptured rehearsal

## Actual versus predicted

```text
PREDICTED implementation: DevTools-gated refusal immediately before packet construction
ACTUAL: ObserveAttackPrecondition returns false before NetworkClient.AttackSwing;
        devTools=false returns true without a gate verdict or behavior change
RESULT: implemented at the report=act send boundary

PREDICTED pass law: present, visible, alive 100/100, dynamicFlags=0,
                    unitFlags=0, GM off, distance zero
ACTUAL attempt 1: all predicates true; distance=0; gate reasons=none
RESULT: AttackPreconditionGatePass; packetConstructed=true

PREDICTED refusal law: verdict row and packet never constructed
ACTUAL code path: AttackPreconditionGateRefusal returns before _net.AttackSwing;
                  live runner marks a refused attack step FAIL
RESULT: refusal path mechanically enforced; rehearsal did not trigger it

PREDICTED fresh target: new entry-6 spawn at player immediately before attack
ACTUAL: GUID 0xF13000000604A28E, spawn distance=0, gate distance=0,
        spawn-to-attack=0.175 s
RESULT: FRESH TARGET PASS on attempt 1 of maximum 3

PREDICTED delivered control to attack: <=2 s
ACTUAL .gps socket flush at 7.033; attack socket flush at 7.040
RESULT: 0.007 s, PASS

PREDICTED uncaptured rehearsal: gate PASS + both socket writes flushed
ACTUAL .gps: 19 bytes, SHA-256 45fdd04c6a8d2f1e19261a030c38fd86ce0f757f94b5e5b767ca0c4639c4e811
ACTUAL attack: 14 bytes, SHA-256 3874595e24d5d00f5e0cb2d4c8b7faf409c0d6332c097e8646c49cebd0ef8e52
RESULT: REHEARSAL PASS; runner 26/26, zero refusals
```

## Symbol verification and deviation

The actual send seam is `Program.Targeting.cs` immediately between
`ObserveAttackPrecondition(entity)` and `_net.AttackSwing(guid)`. The same
`WorldEntity` reference and controller position are read once there. Presence
is identity in the live `EntityStore`; visibility is the same streamed-store
membership used by targeting because out-of-range descriptors are removed.

No named accepted-X1 distance epsilon exists in source or SPEC-20/21. Z0 uses
`Vector3.DistanceSquared <= 1e-6f`, i.e. distance <=0.001 yd. The accepted X1
and this rehearsal both measured exactly zero, so the conservative epsilon does
not widen either accepted observation.

## Scope

No capture ran during this rehearsal. No server code, DB, persistent config,
combat behavior outside the DevTools refusal, error display, or F3-F6 behavior
changed. The spawned target was deleted and GM mode was left off. SPEC-21 P3/P4
remain queued.
