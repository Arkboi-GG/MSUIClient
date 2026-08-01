# NIGHT_01 1-2 — SPEC-21 P3/P4 combat matrix packet

Status: `SHELVED-BLOCKED`

## Actual versus predicted

```text
PREDICTED V-A: construct an otherwise-valid attack while GM is on
ACTUAL: the accepted Z0 gate refused gm-not-confirmed-off and constructed no packet
RESULT: SAFE_REFUSAL; not server-response evidence

PREDICTED V-B: fresh, alive, visible, flags-zero, GM-off, distance-zero send
ACTUAL: gate PASS; one 14-byte CMSG_ATTACKSWING socket write; no player-GUID
        AttackStart, swing, or attack-error response
RESULT: REPRODUCED

PREDICTED V-C/V-D: isolate range and facing, then construct a gated send
ACTUAL: moving/reorienting the fresh target allowed descriptor drift/guard combat;
        the exact-zero/flags-zero Z0 gate correctly refused every contaminated send
RESULT: MATRIX_GATE_INCOMPATIBILITY

PREDICTED CB4 cancel/re-arm: two starts, one cancel, paired wire sends
ACTUAL: 2 IntentOn / 2 AttackSwingSend; 1 local cancel / 1 AttackStopSend
RESULT: PASS; cadence and one-shot NO_DATA because the server emitted no player swing

PREDICTED CB5 target switch: two clean targets and paired switch sequence
ACTUAL quiet rerun: both exact gates PASS; TargetSwitch present; 2 starts / 2 sends;
        1 cancel / 1 stop. Later foreign-guard traffic excluded by attacker GUID.
RESULT: PASS; cadence and one-shot NO_DATA

PREDICTED CB6 confirmed death: descriptor death ends intent with target-death cause
ACTUAL quiet rerun: waitdeath PASS at health=0, but no target-death IntentOff;
        intent ended only on explicit user cancel
RESULT: CLIENT_BEHAVIOR_FINDING; no fix attempted

PREDICTED CB7 movement overlap: legal intent/wire transitions during movement
ACTUAL: gate PASS; one start / one send; movement audit transition checks PASS
RESULT: PASS; cadence and one-shot NO_DATA
```

The player GUID was `0x0000000000000001`. Rows from attacker
`0xF13000066A01384C` were mechanically foreign and excluded. All spawned targets
were deleted by the protocols. The quiet CB5/CB6 reruns moved the experiment away
from the first guard-contaminated location and retained fresh entry-6 identities.

The original matrix asks for GM-on, out-of-range, and facing-discriminator sends,
while the accepted Z0 construction gate requires GM off, exact flags zero, and
distance zero. Weakening or bypassing that gate would change a frozen combat-send
law, so the remaining cells are shelved for an explicit diagnostic-profile ruling.

Primary evidence is the ten run directories under
`live-runs/N1-12-combat-matrix-20260731-224000/`, including runner verdicts,
post-encryption socket traces, combat traces, and combat-audit verdict CSVs.

## Boundary gates

Debug build: PASS, 0 warnings / 0 errors. Combat-wire: PASS (only the established
CA2014 warning during its dependency build). Portrait-camera: PASS, 10,534
specimens and controls 1,224 / 1,289 / 56. Move-audit-check: PASS. The manifest is
`live-runs/manifests/N1-12-20260731-230000.sha256`.
