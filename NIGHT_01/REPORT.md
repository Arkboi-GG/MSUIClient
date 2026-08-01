# NIGHT_01 autonomous run report

Append-only per-item evidence report. Tier-0 end-of-run packet follows after the list is exhausted.

## 1-1 — SPEC-26 sudo attach / server discrimination

Status: `SHELVED-BLOCKED`

### Actual versus predicted

```text
PREDICTED sudo dry attach: one interactive prompt, attach/detach <=5 min
ACTUAL: successful attach/detach in about 5 seconds; cache dropped immediately
RESULT: PASS

PREDICTED trusted site resolution: source lines available for every address
ACTUAL: runtime function addresses resolve, but all candidates report compiled
        without debugging / no line number information
RESULT: SOURCE_ADDRESS_RESOLUTION_UNAVAILABLE

PREDICTED post-detach health: RA + TEST .gps
ACTUAL: both PASS; ptrace_scope remains 1; original mangosd PID remains running
RESULT: PASS
```

The required labeled interior `Unit::Attack` false-return sites cannot be mapped
honestly in the deployed optimized binary. W1-W3 were not entered. Q1 recommends
an exact Build-ID-matched debug-info sidecar. Full evidence is in
`live-runs/W0b-source-resolution-shelf-20260731-223230.md` and its manifest.

No capture, server/client behavior change, DB access, persistent configuration,
package, sysctl, rebuild, binary replacement, or restart occurred.

W0b manifest: `live-runs/manifests/W0b-20260731-223230.sha256`, SHA-256
`014cd5a10a84d06e6d27f77acf1047884770cca846022d3cf89427d5e4a0f4ae`;
all eleven entries recomputed exactly at the boundary. Four gates passed:
Debug build 0 warnings / 0 errors, combat-wire PASS (established CA2014 during
its dependency build only), portrait-camera 10,534 / 1,224 / 1,289 / 56, and
move-audit PASS.

## 1-2 — SPEC-21 P3/P4 combat matrix completion

Status: `SHELVED-BLOCKED`

### Actual versus predicted

```text
PREDICTED: eight isolated matrix/behavior cells, each with a Z0-standard send gate
ACTUAL: V-B, CB4, quiet CB5, quiet CB6, and CB7 produced clean gated evidence;
        V-A was safely refused for GM-on; V-C/V-D drifted or became guard-
        contaminated before the frozen exact-zero/flags-zero gate could pass
RESULT: five decisive cells; three cells require an explicit gate/profile ruling

PREDICTED: player response after each valid attack send
ACTUAL: zero player-GUID AttackStart/swing/error rows after valid sends; foreign
        guard rows were identified by attacker GUID and excluded
RESULT: prior server-silent finding reproduced

PREDICTED: confirmed target death cancels local intent
ACTUAL: health=0 was descriptor-confirmed, but target-death cancellation never
        appeared; explicit cancel was required
RESULT: CLIENT_BEHAVIOR_FINDING (Q2); no fix attempted
```

CB4, quiet CB5, quiet CB6, and CB7 pass legal-transition and send-pairing audit
checks. Cadence and one-shot-return are honestly `NO_DATA` because the server sent
no player swing. Q3 records the frozen-gate/matrix incompatibility. Full packet:
`live-runs/N1-12-combat-matrix-shelf-20260731-230000.md`.

Manifest: `live-runs/manifests/N1-12-20260731-230000.sha256`, SHA-256
`c4e9133908d07ec06426c6b8fc626f913a61d39971097db676432603b8d6b1e6`;
all 23 entries recomputed exactly. Boundary gates: Debug build 0 warnings /
0 errors, combat-wire PASS (established CA2014 only), portrait-camera 10,534 /
1,224 / 1,289 / 56, move-audit-check PASS.
