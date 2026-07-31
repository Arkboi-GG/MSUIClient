# SPEC-22 X1 client socket-flush evidence — accepted

## Actual versus predicted

The observer is attached to the lowest client write site in `WorldSession`.
It runs inside the serialized send lock only after the exact post-encryption
packet has returned successfully from both `NetworkStream.Write` and
`NetworkStream.Flush`. SHA-256 is computed at that site from the same byte array
passed to the socket. DevTools receives the already-computed hash and writes the
run-dated CSV; it does not reconstruct a packet or advance cipher state.

```text
PREDICTED delivered adjacent chat control: flushed socket write + server response
ACTUAL CMSG_MESSAGECHAT at 5.929:
  bytes=19
  sha256=80254993e43f40b1b225ddd72c330c80e1d7df63e9d7c8f444fecf5fbe36ffea
  exact post-encryption write=3F85FEA7407400000000070000002E67707300
  flush=true
  server .gps Map response at 6.077
RESULT: DELIVERED CONTROL PASS

PREDICTED proven GM-off attack: flushed socket write
ACTUAL CMSG_ATTACKSWING at 5.941:
  bytes=14
  sha256=784feef9f39b41853082ecfd8bb6dd47d801f1d3e7143986d79abb317c336420
  exact post-encryption write=8263DEEECF998CA20406000030F1
  plaintext body=8CA20406000030F1 (target 0xF13000000604A28C)
  flush=true
  precondition=GM off, present, visible, alive 100/100, flags zero, distance 0 yd
RESULT: ATTACK SOCKET-FLUSH PASS

PREDICTED not-flushed branch: client send-path defect and HARD STOP
ACTUAL: false; both control and attack returned from write+flush and have exact hashes
RESULT: X2 authorized
```

The two writes were 12 ms apart and are the only rows in the accepted bounded
socket trace. The subsequent attack-start row names the spawned creature as
attacker and a guard as victim; it is foreign combat, not a player attack
response, and does not alter the transit question.

## Invalid first attempt retained

The first scenario used a plain SAY control. It did not echo, and waiting five
seconds for that response allowed the random-motion target to drift to
3.5097475 yd and engage a guard. That run has one runner failure and is retained
under `live-runs/X1-socket-flush-20260731-191900/`; it is not promoted to X1
evidence. It nonetheless proved the instrumentation was observing the expected
post-write bytes before the corrected run.

## Gates and scope

The X1 boundary passed the Debug build (established CA2014 warning only),
combat-wire, portrait-camera (10,534 specimens; 1,224 / 1,289 / 56 controls),
and move-audit gates. No combat decision, packet construction, server code,
database, persistent server configuration, error display, or F3-F6 behavior
changed. `NetworkStream.Flush` is unbuffered/no-op for this socket type; the
observer is DevTools-gated and exceptions are observationally swallowed.
