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

## 1-3 — attack-error text display readiness

Status: `CLOSED-PASS`

### Actual versus predicted

```text
PREDICTED: five server attack-error opcodes reach visible and copyable text
ACTUAL: exact opcode-to-text law added; receive path uses the existing red center
        text and a copyable combat verdict with opcode, byte count, GUID, and text
RESULT: PASS by combat-wire assertions

PREDICTED: verify a live error returned during 1-2
ACTUAL: the server returned no attack-error opcode in those runs
RESULT: NO_DATA; Q4 preserves the no-synthetic-error ruling
```

No packet construction, combat state, or server behavior changed. Evidence:
`live-runs/N1-13-attack-error-display-20260731-231500.md`.

Manifest: `live-runs/manifests/N1-13-20260731-231500.sha256`, SHA-256
`91eca7d61b6973d262bc3e4770020ecf6b35138b10a00060b578a675b9af5624`;
all four entries recomputed exactly. Boundary gates: build PASS (established
CA2014 only), combat-wire PASS, portrait-camera 10,534 / 1,224 / 1,289 / 56,
move-audit-check PASS.

## 2-1 through 2-12 — gameplay interfaces triage

Statuses: all twelve items `SHELVED-BLOCKED` under the parent document's
whole-item time-pressure rule.

### Actual versus predicted

```text
PREDICTED: each item enters a complete wire → instrument → live fixture → UI loop
ACTUAL: ten interface protocol/state/UI/runner families are absent; loot and
        character/inventory are partial foundations but cannot meet their entire
        live acceptance without mixing root causes or the blocked attack path
RESULT: every item shelved whole; no transaction or partial opcode fix attempted
```

The per-item table, vendor server file:line map, and recommendation are in
`live-runs/N2-interface-triage-20260731-234000.md`. Q5 recommends a dedicated
follow-on ordered vendor → quest → loot work order.

Boundary gates: build PASS (established CA2014 only, 0 errors), combat-wire
PASS, portrait-camera 10,534 / 1,224 / 1,289 / 56, move-audit-check PASS.

Manifest: `live-runs/manifests/N2-20260731-234000.sha256`, SHA-256
`ea5bfd3e2a2fffeae5b03cb46fd96be315b82d510dfd7fa283dfc330a8957f87`;
all four entries recomputed exactly.


## 1-4 — combat regression pack

Status: `CLOSED-PASS`

### Actual versus predicted

```text
PREDICTED: repeatable X1/Z0/Z1 protocol with fresh identity, delivered chat
           control, gated attack, post-encryption socket evidence, and cleanup
ACTUAL: 31/31 steps PASS; control-to-attack delta 0.013 s; exact Z0 gate PASS;
        14-byte 0x0141 write flushed; delivered .gps response; cleanup PASS
RESULT: PASS

PREDICTED: transition/pairing regression audit
ACTUAL: legal transitions and send pairing PASS; cadence/one-shot NO_DATA because
        the unresolved server path still supplies no player swing
RESULT: PASS / NO_DATA
```

The committed baseline is `scenarios/combat/combat-regression-fresh-target.txt`;
the dated packet is
`live-runs/N1-14-combat-regression-packet-20260731-232500.md`. It changes no
combat behavior and regenerates no cohort/baseline data.

Manifest: `live-runs/manifests/N1-14-20260731-232500.sha256`, SHA-256
`5064f6d273be318275e2bc702f8ecc6e4d607200352fbe991f3364f0fc02c725`;
all seven entries recomputed exactly. Boundary gates: Debug build 0 warnings /
0 errors, combat-wire PASS (established CA2014 only), portrait-camera 10,534 /
1,224 / 1,289 / 56, move-audit-check PASS.
