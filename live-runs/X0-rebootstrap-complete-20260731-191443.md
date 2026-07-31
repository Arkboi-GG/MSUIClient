# SPEC-22 X0 travel-laptop re-bootstrap — complete

Run: `20260731-191443`

## Actual versus predicted

```text
PREDICTED local config: LAN host + supplied test account + Test character
ACTUAL: ignored MSUIClient/client-config.json validated with host 192.168.0.2,
        supplied account credential present, auto-connect enabled, character Test
ACTUAL RA: authenticated read-only and returned server info; transcript contains
           prompts and server response only, never credential bytes
RESULT: PASS

PREDICTED SSH recovery: install new public key, confirm key-only auth, cease password use
ACTUAL: interactive public-key installation completed; key-only batch probe returned
        KEY_AUTH_OK; no password was printed, stored, traced, or committed
PUBLIC FINGERPRINT: SHA256:mwe0xwrQKqTTTi4jhIPj1JjC3vdzcHGW38ymAZTkTi4
RESULT: PASS

PREDICTED SPEC-19 T2 proofs: .gps / .go / identified <=3 yd spawn / confirmed death PASS
ACTUAL .gps: 4/4 runner rows PASS with server map and position responses
ACTUAL .go: 6/6 runner rows PASS; TeleportApplied and requested position assertion PASS
ACTUAL spawn: 10/10 runner rows PASS; entry 6 GUID 0xF13000000604A289,
              within3=true, server info response, exact descriptor cleanup
ACTUAL death: 13/13 runner rows PASS; entry 6 GUID 0xF13000000604A28A,
              descriptor health=0, exact descriptor cleanup
RESULT: T2 four-proof loop PASS once on the travel laptop
```

The live runner mechanically overwrote its historical generic movement-trace
filename and rewrote `vantages.json` on exit. The new trace was preserved under
the run-dated X0 directory, then both tracked files were restored byte-for-byte
from accepted HEAD. No historical evidence or user vantage change remains.

RA `server info` reported the running core revision
`d5ed9b2a4112560622be` and a live server. This is runtime identification only;
no server file, code, database, or persistent configuration was changed.

## Scope confirmation

X0 is complete. No client production code, combat behavior, server code,
database, persistent server configuration, error display, or F3-F6 behavior
changed. X1 is now authorized. SPEC-21 P3/P4 remain queued.
