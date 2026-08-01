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
