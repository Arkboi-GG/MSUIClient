# SPEC-22 X0 travel-laptop re-bootstrap — credential hard stop

Run: `20260731-185842`  
Host: Nico's travel laptop  
Repository: `C:\Users\nico\source\repos\MSUIClient`  
Accepted HEAD at preflight: `145db117e475110e646a88792bb6b0b9383d6b3d`

## Actual versus predicted

```text
PREDICTED required root SPEC/plan/protocol documents: all present
ACTUAL: all tracked root SPEC_TOOLKIT_*.md, *PLAN*.md, and *PROTOCOL*.md
        files are present; SPEC_TOOLKIT_22_ATTACK_TRANSIT.md is tracked;
        zero untracked order/plan documents required preservation
RESULT: PASS

PREDICTED repository boundary gates: build + combat-wire + portrait-camera + move-audit pass
ACTUAL build: PASS, 0 errors, established CA2014 warning only
ACTUAL combat-wire: PASS
ACTUAL portrait-camera: PASS, 10,534 specimens; controls 1,224 / 1,289 / 56
ACTUAL move-audit: PASS
RESULT: PASS

PREDICTED local client config: dedicated TEST credentials and 192.168.0.2
ACTUAL: ignored MSUIClient/client-config.json exists, but points to an obsolete
        host and is not configured for the dedicated TEST account; tracked files
        do not contain the missing TEST credential
RESULT: BLOCKED on unrecoverable local secret; no config bytes changed

PREDICTED SSH: existing key-only authentication succeeds, or generate a new dedicated key
ACTUAL key-only probe: Permission denied (publickey,password)
ACTUAL recovery: generated a new dedicated ED25519 keypair on this laptop
PUBLIC FINGERPRINT: SHA256:mwe0xwrQKqTTTi4jhIPj1JjC3vdzcHGW38ymAZTkTi4
RESULT: HARD STOP before password authentication/key installation, as ordered
```

The private key remains outside the repository in the user's SSH directory.
Only the public fingerprint is recorded. No password was requested through a
command, printed, stored, traced, or committed. X0's four SPEC-19 T2 positive
proofs have not been rerun because config repair and key installation are
prerequisites.

## Scope confirmation

No client production code, combat behavior, server code, database, persistent
server configuration, error display, or F3-F6 behavior changed. X1-X4 and
SPEC-21 P3/P4 remain not started.
