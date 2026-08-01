# SPEC-23 Y3 - attack wire-capture HARD STOP

## Transit verdict

`CAPTURE_FILTER_DEFECT` - no causal transit row is selected.

The client socket observer froze this run's 19-byte delivered `.gps` control
and 14-byte CMSG_ATTACKSWING after successful writes and flushes. The bounded
pktmon session was elevated, endpoint-filtered, under 60 seconds, and lossless,
but its NIC-component Wi-Fi view recorded only client ACK-only frames around
the writes. Neither post-encryption byte substring appeared, and no
server-to-client frame supplied ACK coverage for a matched payload range.

The same run also failed the accepted target precondition: the target was
alive and GM mode was off, but it had `unitFlags=0x00080000` and had moved to
9.533476 yd. This is `SCENARIO_PRECONDITION_DRIFT`, independently preventing
promotion as a repeat of accepted X1.

## Actual versus predicted

```text
PREDICTED present + ACKed + server silent: prove server pre-handler/unlogged predicate
ACTUAL: no payload byte-substring and no covering server ACK in the capture view
RESULT: NOT PROVEN; option 3 remains gated

PREDICTED chat present + attack absent/unACKed: characterize client/LAN anomaly
ACTUAL: chat payload also absent; capture omitted client payload-bearing records
RESULT: NOT PROVEN; no send/LAN fix attempt

PREDICTED neither present: capture/filter defect, no causal row
ACTUAL: neither present in 44 packets with zero capture loss
RESULT: CAPTURE_FILTER_DEFECT; HARD STOP

PREDICTED identical accepted X1 target
ACTUAL: distance 9.533476 yd and unitFlags 0x00080000
RESULT: SCENARIO_PRECONDITION_DRIFT; HARD STOP reinforcement
```

## Prior-runs reconciliation

- SPEC-21 P2 remains valid negative logging evidence: its proven-valid attack
  produced zero receive/dispatch/handler debug lines. Y1 does not weaken or
  reinterpret that run.
- SPEC-22 X1 remains valid client-boundary evidence: the accepted target's
  attack was written and flushed. This run independently proves only its own
  two socket writes.
- SPEC-22 X2-X4 correctly stopped at unavailable capture privilege. SPEC-23
  resolves the privilege capability but not transit, because the selected
  pktmon component view did not retain either payload.
- The X3 server candidate table remains frozen and unentered:
  `WorldSocket.cpp:98-183`, `WorldSession.cpp:277-331,518-549,1250-1313`,
  `Opcodes.cpp:398-401`, `CombatHandler.cpp:32-62`, and
  `Unit.cpp:4721-4804`.
- All prior exclusions continue to apply to the accepted X1/P2 evidence. They
  are not claimed for this drifted Y1 target.
- SPEC-21 P3/P4 remain queued behind a valid transit verdict.

## Nico ruling options

1. Recommended smallest next order: one fresh bounded Windows capture using
   pktmon all-components (not NIC-only), with the same endpoint filter and
   byte-substring law, plus a mechanical pre-send rejection unless the target
   is alive, flags-zero, and distance-zero. Raw cleanup remains mandatory.
2. If all-components pktmon still omits both writes, authorize the already
   named `netsh trace` fallback for one equivalent bounded run.
3. Deeper server instrumentation remains unauthorized unless a valid attack
   payload is captured and ACKed while SPEC-21-style server admission remains
   silent.

Linux tcpdump authorization remains explicitly excluded.

## Cleanup and scope

The temporary elevated relay exited after deleting its ETL and pcapng. The
full formatted capture text and all relay/control files were then deleted.
No ETL, PCAP, or pcapng survives anywhere under `live-runs`; pktmon was stopped
and its filters removed. Close the Administrator PowerShell used to launch the
relay; subsequent work must run unelevated.

No server code, database, persistent server/client configuration, combat
behavior, error display, or F3-F6 behavior changed.

## En-route housekeeping

Yes - this implementing-agent work authored preflight commit `145db11`
(`verification work`) under the repository's configured `Yafrovon` identity.
It is retroactively designated the SPEC-22 X0 document-preservation commit;
it preserved the restored order/plan/protocol documents, including SPEC-22,
before the named X0 hard-stop and completion commits.

**HARD STOP - SPEC-23 is complete at `CAPTURE_FILTER_DEFECT` plus
`SCENARIO_PRECONDITION_DRIFT`. Nico must issue a new measurement ruling before
another capture or any option-3 server instrumentation.**
