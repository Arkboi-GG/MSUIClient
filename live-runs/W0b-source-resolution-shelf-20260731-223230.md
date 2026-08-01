# NIGHT_01 item 1-1 / SPEC-26 W0b — sudo dry run and source-resolution shelf

Run date: 2026-07-31 (America/New_York)

## Actual versus predicted

```text
PREDICTED sole password gate: password entered only at interactive sudo prompt
ACTUAL: first helper attempt had piped stdin and sudo refused without consuming a
        password; corrected direct-terminal attempt prompted once and attached
RESULT: PASS; no password echo, file, environment, command history, or artifact

PREDICTED root-mediated dry attach: attach, resolve, source-map, detach <=5 min
ACTUAL: PID 576688 attached as root via sudo; attach marker at 22:32:31,
        detached and cache dropped by 22:32:36 (about 5 seconds)
RESULT: attach/detach PASS

PREDICTED source verification: every trusted trace address maps to cited lines
ACTUAL: function addresses resolved, but gdb reports the executable was compiled
        without debugging and "No line number information available" for every
        queried candidate address
RESULT: SOURCE_ADDRESS_RESOLUTION_UNAVAILABLE

PREDICTED W1 predicate coverage: every labeled silent Unit::Attack return plus
        dispatch sites can be placed and trusted as auto-continue tracepoints
ACTUAL: function-entry symbols exist, but optimized interior return addresses
        cannot be mapped honestly to source predicates in this deployed binary
RESULT: SHELVED-BLOCKED; W1/W2/W3 not entered

PREDICTED hygiene: detach, sudo -K, ptrace_scope unchanged, server healthy
ACTUAL: transcript contains detached + W0B_SUDO_CACHE_DROPPED; ptrace_scope=1;
        PID 576688 still running; RA server-info and TEST .gps round-trip PASS
RESULT: PASS
```

## Why W1 is not supportable

The order requires each resolved address to map to the frozen source citation
before it can be trusted. Function names and entry addresses are available, but
the deployed optimized binary has no line table. In particular, the required
one-label-per-silent-`Unit::Attack`-return tracepoints cannot be distinguished
from the executable and source without inventing an assembly-to-source mapping.
Proceeding with entry-only tracepoints would not satisfy any of SPEC-25 W2's four
exclusive outcomes: it could show handler/Unit entry but could not name the exact
false-return predicate.

This is a tool/evidence blocker, not a combat verdict. Per NIGHT_01, item 1-1 is
`SHELVED-BLOCKED` and the run continues to item 1-2.

## Sudo and server accounting

Successful interactive attempt, exactly:

1. `sudo -k gdb -q -batch -x <temporary-script> -p 576688`
2. `sudo -K`

The failed pre-attempt invoked the same remote shell path but sudo received no
password and returned `a password is required`; it did not attach. Both temporary
gdb scripts were removed. No W1 attach occurred. No NOPASSWD entry, root shell,
sysctl change, package, server file, rebuild, binary replacement, restart, DB
access, persistent configuration, capture, or client behavior change occurred.

Primary transcript:
`live-runs/W0b-sudo-dry-run-20260731-223230/interactive-gdb-transcript.txt`.
It contains the resolved runtime addresses, every source-line failure, detach,
and sudo-cache-drop markers. The password is not present.
