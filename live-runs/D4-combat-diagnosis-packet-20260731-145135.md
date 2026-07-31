# SPEC-17 D4 combat diagnosis packet

Run date: 2026-07-31 14:51:35 local

## Root-cause verdicts

| Item | Decision-table row / status | Evidence-backed verdict |
|---|---|---|
| CB1 | No valid V-A/V-B/V-C/V-D specimen | NOT DETERMINED. The runner could not teleport, create, delete, identify, or kill a controlled target through its GM chat path. V-A's `SMSG_ATTACKSWING_NOTINRANGE` (0x0145) against a pre-existing creature proves error capture only. It does not distinguish GM suppression from ordinary range rejection. |
| CB2 | Nico ruling: vanilla defer-to-server | CLOSED AS LAW. Client intent remains latched; no client range/facing gate is authorized. Server errors are authoritative. Displaying them is a later order. |
| CB3 | Nico ruling: vanilla defer-to-server | CLOSED AS LAW. Same as CB2. |
| CB4 | PARTIAL | Archived player swing samples change 1 -> 0. Two intent starts/two ATTACKSWING sends and two local cancels/two ATTACKSTOP sends stand. A player-guid swing is still required to restore re-arm swing confirmation. |
| CB5 | PARTIAL | TargetSwitch and its same-time stop/off/start/on wire sequence stand. The sole archived swing was foreign, so animation/swing proof is NO_DATA. |
| CB6 | No valid death-confirmed specimen | NOT DETERMINED. `.die` was locally sent but neither acknowledged nor executed; no target descriptor transitioned to server-confirmed dead. The eight archived swing rows were foreign. |
| CB7 | PARTIAL; audit defect corrected | Archived `swingInsideIntent` changes FAIL -> PASS after excluding the foreign attacker. It has zero player swing samples, so this is not positive player-combat proof. |

CB6 and CB7 still expose a separate audit hygiene row: each archive begins
with `IntentOff` while the audit model is already Off. That legal-transition
failure is retained; it is not conflated with a swing-scope finding.

## Complete archived re-audit

| Scenario | Legal transitions | Start/send | Cancel/stop | Player swings | One-shot return |
|---|---|---|---|---:|---|
| CB1 | PASS | 1/1 PASS | 1/1 PASS | 0 | NO_DATA |
| CB2 | PASS | 1/1 PASS | 1/1 PASS | 0 | NO_DATA |
| CB3 | PASS | 1/1 PASS | 1/1 PASS | 0 | NO_DATA |
| CB4 | PASS | 2/2 PASS | 2/2 PASS | 0 | NO_DATA |
| CB5 | PASS | 2/2 PASS | 2/2 PASS | 0 | NO_DATA |
| CB6 | FAIL: initial IntentOff while Off | 1/1 PASS | 1/1 PASS | 0 | NO_DATA |
| CB7 | FAIL: initial IntentOff while Off | 1/1 PASS | 2/2 PASS | 0 | NO_DATA |

## Implied future order queue

1. Repair or replace the autonomous GM-command transport capability, then
   require positive proof before combat reruns: a server response to `.gps` or
   `.go`, an observed position mutation, and a response-derived identity for a
   newly created target. This is a prerequisite order, not a combat-law fix.
2. Re-run D1 V-A through V-D unchanged against controlled targets. Apply the
   existing decision table without changing its law or variants.
3. Re-run CB6 only after both GM response and target death descriptors confirm
   server death. Queue an intent-drop fix only if intent survives that proof.
4. Re-run CB4/CB5/CB7 with player-GUID scoping. Restore behavioral claims only
   from player-guid swing rows.
5. Implement server attack-error text display in its later signed order, as
   already ruled for CB2/CB3.
6. Independently decide whether redundant initial `IntentOff` is an audit
   normalization issue or a real state-transition defect before ordering a fix.

Explicit non-orders: no client range gate, no client facing gate, no combat
behavior change, no F3-F6 work.

## Actual versus predicted

```text
PREDICTED D1 qualifying decision-table row: one of V-A/V-B/V-C/V-D
ACTUAL: none; all four specimens invalid because no controlled spawn existed
PREDICTED CB6 prerequisite: response + descriptor-confirmed server death
ACTUAL: neither; client behavior remains NO_DATA
PREDICTED CB4 restore condition: player-guid SwingReceive after re-arm
ACTUAL: unavailable live; archived player swing count is 0
PREDICTED foreign swing treatment: ForeignSwingReceive, excluded from player assertions
ACTUAL: CB7 swingInsideIntent FAIL -> PASS; CB4/CB5/CB6/CB7 player samples all 0
```

HARD STOP: this packet orders no fixes. Combat behavior, F3-F6, and every
other gameplay plane remain untouched pending a new signed order.
