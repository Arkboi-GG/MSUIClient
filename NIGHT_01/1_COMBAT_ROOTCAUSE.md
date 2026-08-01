# NIGHT_01 tier-1 doc 1 — combat root cause and matrix

Parent: `TIER0_MASTER.md` item 0-1. All Tier-0 rules apply.

- **1-1 SPEC-26 execution.** Execute
  `SPEC_TOOLKIT_26_ATTACK_SUDO_ATTACH.md` exactly as committed (it resumes
  SPEC-25 W1–W3 via sudo gdb; password hard-stop W0b; dprintf-only
  tracepoints; ≤5 min attach; capture relay per SPEC-24). AMENDMENT for
  this run: its terminal HARD STOP becomes the close of item 1-1 — write
  the W3 packet, put the implied fix/acceptance decision into
  `RULINGS_QUEUE.md` as entry #1 with your recommendation, status
  `CLOSED-FINDING` (or `SHELVED-BLOCKED` if sudo is refused / attach
  fails — record exactly why) — then CONTINUE to 1-2. Do not wait for
  Nico.
  - 1-1-1 W0b password gate + sudo dry run (interactive — do this FIRST).
  - 1-1-2 W1 coincidence run (gdb + capture + debug console, one packet).
  - 1-1-3 W2 discrimination, W3 packet, queue entry, sudo -K, hygiene.
- **1-2 SPEC-21 P3/P4 combat matrix completion.** Run as originally
  ordered in `SPEC_TOOLKIT_21_ATTACK_PRECONDITIONS.md`, now unblocked by
  the SPEC-24 transit verdict. Use the Z0 mechanical pre-send gate as the
  precondition standard for every matrix cell. Foreign-swing
  contamination controls mandatory (the CB4 lesson). Diagnosis only; any
  fix implied goes to the queue.
- **1-3 Attack-error text display readiness.** Per Nico's CB2/CB3
  defer-to-server ruling: build the CLIENT-side display path for server
  attack-error packets (instrument + verdict channel + copyable text),
  verify with whatever error the server actually returns during 1-2 runs.
  Client-side and mechanically verifiable ⇒ within your fix authority.
  The decision to surface errors the server never sends stays shelved.
- **1-4 Combat regression pack.** Fold the accepted X1/Z0/Z1 patterns
  into a repeatable scripted protocol (`scenarios/combat/`) so future
  combat claims re-run mechanically: gated fresh-target attack, socket
  trace, verdict rows. Commit as the new combat live-protocol baseline
  (a protocol file, NOT a cohort/baseline regeneration).
