# SPEC_TOOLKIT_16 — autonomous live runs: protocol runner + CB1–CB7 agent-run (HARD STOP)

Binding rules: `SPEC_TOOLKIT_00_ORDERS.md` plus PILOT_PROTOCOL standing law
12 (autonomy-first evidence, added 2026-07-31): Nico runs only what cannot
feasibly be agent-run. This order builds the agent-run live capability and
then executes the combat protocol with it. Combat FIXES are still out of
scope — this is the diagnosis run that replaces Nico's CB session. F3–F6
remain untouched.

## A0 — session bootstrap

Make a full live session startable and disposable from the command line:
launch vmangos (document the existing start procedure in SETUP.md if not
already), launch MSUI with auto-login to a dedicated GM TEST account and
auto-enter-world on a named test character (config-driven: account,
character, realm — extend client-config; never Nico's own characters),
teleport to the movement-arena vantage via the C0 GM console, ready-check
(world loaded, wire up, verdict ring live). Deliver `tools/live-run` (or a
script) that does all of it and exits nonzero with a named reason on any
failure. If vmangos cannot be auto-started, document the manual step and
proceed — one manual server start per sitting is acceptable; per-scenario
manual steps are not.

## A1 — protocol runner

Extend the SPEC-13 input player into a protocol runner: scripted lines may
now also be `gm <command>` (C0 console send), `select <spawned-target-ref>`,
`attack start|stop`, `wait <s>`, `waitfor <verdict-pattern> <timeout>`,
`assert <verdict-pattern>`, `dump <name>`, `trace start|stop <name>`.
Targets spawned by scenario GM scripts are addressable by a deterministic
ref (e.g. spawn order). All actions route through the SAME code paths as
user input (selection, attack intent, GM console). A protocol file plus the
runner yields: run-dated combat/movement traces, verdict ring dump, F10
dumps at marked points, and a runner log with per-step pass/fail. Runner
failures are results, not blockers: report and continue to the next step
where safe.

## A2 — CB1–CB7 executed agent-side

Author `scenarios/combat/cb-protocol.txt` implementing CB1–CB7 from
SPEC-15/C2 (stationary swings, orbit-while-attacking, range-edge dance,
cancel + re-attack, mid-swing target switch, target death, chase-attack) as
runner scripts. Movement during combat uses the movement-script primitives.
Execute the full protocol against live vmangos; run `tools/combat-audit`
on every trace. Deliverables, all run-dated + SHA-256 in the report:

- traces + verdict dumps + runner logs per CB item
- combat-audit verdicts CSV per item
- a findings table: for each CB item, observed behavior vs vanilla-law
  expectation (server-owned cadence acknowledged: what did the CLIENT send,
  show, and animate at each edge?), each anomaly stated as a candidate
  defect with the exact verdict lines that evidence it

Given the C4 finding (no client swing timer / range gate / arc gate —
server owns cadence), the audit's client-side laws are: intent transitions
correct and single-shot; wire sends exactly once per intent edge; UI/anim
state tracks SMSG attack events without stalls or orphaned states; cancel
and re-attack leave no dead state. Where a check needs a law Nico hasn't
ruled (e.g. should the client gate attacks on range/facing at all, or defer
to server errors?), record it as a RULING-NEEDED row, not a defect.

## A3 — CHECKS migration audit (doc only)

Sweep CHECKS_GAMEPLAY.md (Sessions 2 and 3): reclassify every item as
AGENT-RUNNABLE (rewrite it as a runner protocol reference, or mark
runnable-next-order if it needs a primitive the runner lacks) or NICO-ONLY
(one-line justification: perceptual/taste/hardware). Session 2's V1/V2/V2b
visual judgments stay Nico-only but get their evidence pre-gathered
agent-side (targeted contact-sheet renders already exist for V2b). Produce
the migration table in the report.

## A4 — HARD STOP

Packet: CB findings table (defect candidates + RULING-NEEDED rows), the
CHECKS migration table, and any runner primitives still missing. Next
orders (combat fixes, F3 which can now use the runner + GM `.speed`) are
written from this packet.

Standard four gates every boundary; one commit per stage; run-dated
artifacts + SHA-256 manifests throughout; report appends per stage.
