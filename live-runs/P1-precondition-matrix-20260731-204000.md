# P1 attack-precondition matrix — 2026-07-31 20:40

## Result

The predicted GM-state split did not occur. All three admissible cells sent one
well-typed `CMSG_ATTACKSWING` through the real user-input path and received zero
`SMSG_ATTACKSTART`, `SMSG_ATTACKSTOP`, swing, or attack-error events.

| Cell | Proven send-time state | Sent body | Result | H0 landing |
|---|---|---|---|---|
| A — GM OFF, fresh spawn | server-confirmed OFF; `0xF13000000604A284`; present, visible, alive 100/100; flags 0; faction 25; 0.000 yd | `84 A2 04 06 00 00 30 F1` | SILENT | Typed GUID excludes A0. Present descriptor plus zero response excludes observable A1–A3 and success. The exact `Unit::Attack` silent predicate requires P2 server state. |
| B — GM ON, same spawn | server-confirmed ON; exact same GUID; present, visible, alive 100/100; flags `0x08000000`; faction 25; 1.4692235 yd | `84 A2 04 06 00 00 30 F1` | SILENT | Same unresolved H0 silent-return family. B differs from A only by GM state and the server-authored flag update accompanying it. |
| C — GM OFF, live wild | server-confirmed OFF before anchoring; entry 299 chosen from the current client object store, teleported through real GM chat to its observed position, then re-resolved live; `0xF13000012B013849`; present, visible, alive 100/100; flags 0; faction 32; 1.1230221 yd | `49 38 01 2B 01 00 30 F1` | SILENT | Same unresolved H0 silent-return family. This is neither an archived GUID nor an absent-store cell. |

The combat auditor passed legal intent transitions, one SWING send per start,
one STOP send per cancel, and no swings outside intent for all three traces.
Cadence and one-shot return correctly report `NO_DATA` because the server never
accepted an attack.

## Actual versus predicted

```text
PREDICTED A: ATTACKSTART + swings
ACTUAL A: one SWING send; zero attack-family receives

PREDICTED B: silent refusal in GM mode
ACTUAL B: one SWING send; zero attack-family receives

PREDICTED C: ATTACKSTART + swings
ACTUAL C: one SWING send; zero attack-family receives

PREDICTED discriminator: A/C succeed while B fails
ACTUAL discriminator: A/B/C all fail identically
RESULT: GM mode is not the acceptance root cause; P2 is mandatory
```

## Calibration results retained

The first combined run was void because the spawn had wandered to 4.7765 yd
and the requested Westfall entry was absent after streaming. A tightened A/B
run produced the accepted two cells, but its C selector was absent. Subsequent
C runner attempts retained the same hard rule: absent targets and targets more
than 3 yd away were results but not evidence. The final runner-only `anchor`
primitive selects a current object-store creature, sends `.go xyz` through the
real GM chat path, and re-resolves the nearest live entry after arrival. It
does not alter combat, movement, physics, input, or packet law.

## Prior-run reconciliation at this boundary

- Runs 16/17: their GM commands were unproven, and the archived wild GUID is no
  longer relied upon. The historical attack-start remains real evidence that
  the same client framing was once accepted.
- H2/run 20: today's failure of the run-17-era client remains consistent with
  a changed live server/session precondition, not a client regression.
- P0/run 21: its GM-OFF silence is reproduced by A and independently by C.
- The proposed “GM mode active only after chat repair” explanation is rejected:
  two fully proven GM-OFF cells are silent today.

P2 must now observe the live VMaNGOS handler. No combat behavior or error-text
change was made.

The four boundary gates passed: Debug build (0 warnings/errors), combat wire
foundation, portrait camera (1224/1289/56), and movement audit.

## Primary artifacts

- `live-runs/P1-20260731-183000/`
- `live-runs/P1-C-20260731-203000/`
- `dumps/combattrace-P1-A-gm-off-spawn-20260731-183000-20260731-171741.csv`
- `dumps/combattrace-P1-B-gm-on-same-spawn-20260731-183000-20260731-171743.csv`
- `dumps/combattrace-P1-C-gm-off-live-wild-20260731-203000-20260731-172525.csv`
- `dumps/wire-20260731-171741.{txt,wlog}`
- `dumps/wire-20260731-172525.{txt,wlog}`
- `live-runs/P1-audits-20260731-204000/`
