# SPEC-24 Z2 — conditional netsh fallback not entered

Run date: 2026-07-31 (America/New_York)

## Predicted versus actual

Predicted entry condition: run one bounded netsh trace if and only if the healthy Z1 all-components pktmon capture omits both this run's chat and attack payload substrings.

Actual: Z1 directly retained the exact 14-byte post-encryption CMSG_ATTACKSWING substring `92E386E4B7428FA20406000030F1` in one logical client-to-server TCP segment, sequence 817834466:817834480. A server packet ACKed 817834507, covering the complete attack range. Therefore both payloads were not absent.

Result: `Z2_NOT_ENTERED_CONDITION_FALSE`. No netsh trace was started, no second scenario was run, and no capture file was created. This is the required non-action under Z2's if-and-only-if rule, not `CAPTURE_ENGINE_EXHAUSTED`.

Z3 must select the SPEC-22 X3 causal row using the Z1 present-and-ACKed evidence reconciled with the bounded SPEC-21 P2 server-silence evidence.
