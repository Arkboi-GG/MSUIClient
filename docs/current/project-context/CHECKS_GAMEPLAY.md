# CHECKS_GAMEPLAY — one live session verifies the whole toolkit

2026-07-30. Every toolkit instrument (verdicts, panel, Portrait Lab, overrides,
batch, wire tap, F10 dump) is implemented and gate-green but **live-unverified**.
This is the EMPIRICAL_CHECKS twin for the gameplay plane: run top to bottom
(~35 min), paste into the `>` slots, hand back. Use the Verdicts panel's
click-to-copy for every line — if you find yourself fishing in the terminal,
that is itself a FAIL for stage 1E.

Answers decide what gets built next; ★ = highest information value.

---

## A. Startup + the parser fix ★

**A1.** Log in. Paste the player `[portrait]` line (copy from the Verdicts panel).
It should now read `camera=authored` — the first live proof of the camera-parser
fix — and the portrait should look 1.12-framed (head-and-shoulders, not a
bounds guess).
>

**A2.** Target any creature. Paste its portrait line. Expect `camera=authored`
for most creatures (the sweep measured 10,452/10,522 authored).
>

**A3.** Click one row in the Verdicts panel, paste it here directly from the
clipboard. (This *is* the 1E test — the paste must be byte-identical to the row.)
>

*Why: A1/A2 close the authored-camera saga live; A3 verifies the copy loop the
whole handback method now depends on.*

## B. Cast-target law ★

**B1.** Select a hostile (wolf), cast Holy Light. Paste the `[verdict:cast]`
line — expect `SelfFallback` — and confirm the heal landed on you.
>

**B2.** Immediately recast while on cooldown. Paste the refusal line
(`Sent=false`, cooldown reason).
>

## C. Action-bar verdicts ★

**C1.** With a target: walk out of range, then back in. Paste the two
`[verdict:action]` transition lines; confirm the hotkey went red/grey at the
same moments.
>

**C2.** Drain mana below a spell's cost. Paste the `NotEnoughPower` line;
confirm icon+ring turned blue. Numbers in the line (`cost` vs `power`) should
explain the verdict on sight.
>

**C3.** Start auto-attack. Paste the `Flashing=true` line — exactly one line,
not a stream (transition-latch test).
>

**C4.** Eyeball pass: does the bar look byte-identical to before the 1D
refactor? Any state (hover, pushed, cooldown, equipped border) that draws
differently is a 1D regression — name it.
>

## D. Animation choices

**D1.** Cast, then move immediately. Paste any `[verdict:anim]` lines that
appeared (expect none at `Missing`/`Substituted`); confirm locomotion resumed
instantly.
>

## E. Portrait Lab ★

**E1.** Open the Lab, subject Player. Drag FovyDegrees to 60 and back. Does the
bake reframe live both ways within a frame or two? Paste one before/after
verdict pair.
>

**E2.** Switch to Specimen, filter, and `]`-cycle through ~10 creatures. Does
each bake + verdict appear without touching the server? Any that error (not
Blank — error) get named here.
>

**E3.** Pick any badly framed specimen, tune it right with the sliders, Save
override, relog, re-open the Lab on it. Did the override hold? Paste its
verdict line (expect `camera=Override`).
>

**E4.** `Save PNG` on any subject — did the file land where the console/panel
said?
>

## F. F10 gameplay dump ★

**F1.** In a normal scene (target selected, bars populated), press F10. Paste
the `[gdump]` line, then paste the clipboard (should be the JSON path). Confirm
the `.png` exists next to it and shows the scene.
>

**F2.** Open the JSON. Spot-check three things and say pass/fail each:
`actionBar` verdicts match what the panel showed; `portraits` matches A1/A2;
`layout` has rows for bar slots, micro, bags, cast bar, unit frames.
>

**F3.** Change resolution (or UI scale), F10 again. Paste the backpack row's
`authored`/`screen` rects from both dumps — the screen rect should scale with
the bar (this retires the backpack-escape bug class into two comparable
rectangles).
>

**F4.** Send the assistant one full dump JSON (+ PNG). *This exchange is the
toolkit's acceptance test: a gripe travels as a file, not a paragraph.*
>

## G. Wire recorder

**G1.** Toggle "Record wire log" on, cast once, kill and loot one mob, toggle
off. Confirm the `.txt` exists; paste the loot sequence lines
(CMSG_LOOT → SMSG_LOOT_RESPONSE → …). Send the assistant the `.txt`.
>

**G2.** Confirm the `wire` pseudo-channel in the Verdicts panel showed the same
packets live, copyable.
>

## H. Ship-state bit-identity ★

**H1.** Set `"devTools": false`, relaunch. Confirm: no panels, F10 inert, no
wire toggle — and the game *looks and plays identically* (portraits, bar
states, casting). Any visual difference with DevTools off is a boundary
violation — describe it.
>

**H2.** Still with DevTools off: does your saved E3 override still apply?
(Overrides are data, not dev UI — they must.)
>

## I. Deferred rulings (NOT part of this session — whenever, or never)

The parked worklist lives in `data/diagnostics/portrait-known-blank.txt`: 10 AncientProtector,
2 MouthofKathune, 3 PortalofKathune, plus the dark-cohort-vs-real-1.12 question
(one GM spawn) and the Kathune/AncientProtector visibility rulings. Nothing
blocks on these.

---

### Handback

Paste this file back filled in (or just the failing sections). FAILs route to
the implementing agent as targeted fix orders with the pasted line as evidence;
PASSes flip the report's LIVE UNVERIFIED markers. When A–H are green, Slice 3
(Action Bar Lab, Animation Lab + anim-audit, scenario deck, refs/shot-diff)
gets specced against a verified foundation.

---

## Session 2 - variant fixes

Run this against the accepted post-7C Option B tree. W2 attachments, W3 type-6
hair, and W4 type-1 npc-bare scalp/ear composites are present. The separate
type-8 Tauren inheritance question is parked for V2b and is not part of W4's
mechanical acceptance.

**V1. NICO-ONLY (visual correctness). Willem, Northshire.** Find Deputy Willem (display 2072 / extra 675). Is
his authored Stormwind plate helm visibly mounted, without changing his face
or scalp texture?
> Expect: helmet visibly mounted and fitted. His W4 scalp-composite correction
> is present; his face and exposed scalp should remain coherent.

**V2. NICO-ONLY (texture appearance). Dressed humanoid NPC hair.** Check several merchants or guards with
authored clothing and visible hair. Is the hair/scalp supplied by a real
`Character\\...\\Hair*.blp`, with no clothing texture or black chunks on the
head?
> Full-PASS expectation: hair meshes bind real `Character\\...\\Hair*.blp`
> files and type-1 scalp/ear under-passes use the npc-bare composite. No
> clothing-atlas bleed or black chunks should remain on these regions.

**V2b. NICO-ONLY (parked visual ruling). Tauren facial-hair and horns.** Check several Tauren NPCs with distinct
facial-hair or horn variants. Do the chin and horn geosets look correct, with
no dressed-atlas color or clothing pattern?
> This is the parked type-8 inheritance question. A FAIL reopens it through a
> new implementation order using the pasted live line as evidence; it is not
> a regression of the accepted Option B cohort.

**V3. AGENT-RUNNABLE-NEXT (needs portrait-lab primitive). Portrait Lab cycle.** Open Portrait Lab in Specimen mode and press `]`
through at least ten humanoids. Use the new copy controls to paste one active
override key, one latest portrait verdict, and one specimen id/model pair.
>

**V4. AGENT-RUNNABLE-NEXT (needs character-selection primitive). Player collision spot-check.** Create or select a few visibly distinct
race/sex/customization combinations. Record any creation-vs-render mismatch.
The offline W6 baseline is 634/634 Ready, but 359 specimens encounter a
duplicate `CharSections` key; their exact file-order winners are the proposed
7C-3 protocol, not yet a renderer fix. It remains queued for its own ruling.
>

---

## Session 3 - movement feel after F1/F2

Run on the post-SPEC-14 tree at normal frame rate. The automated fixed-step
gate is green; these checks are the human feel/visual half and do not authorize
F3-F6.

**CB0. AGENT-RUNNABLE (`gm .gps`). GM send capability.** Open DevTools -> GM console, send `.gps`, and
paste the `[verdict:combat] event=GmCommand` line plus the server's chat
response. Use Previous/Next to confirm command recall.
>

**M1. AGENT-RUNNABLE (SPEC-13 movement script). Start and stop.** From idle, press W for several seconds and release.
Does Run begin immediately, cross-fade without a pose pop, and stop sharply
without foot sliding or a Stand latch? Paste the relevant `[verdict:move]`
transition lines.
>

**M2. AGENT-RUNNABLE (SPEC-13 movement scripts). Standing and moving turns.** Turn left while planted, then repeat while
running. Does the planted body use the shuffle/frozen chase naturally, and does
the moving circle feel slower (0.75 of the standing turn rate)? Paste both
turn-rate audit rows or note the visible failure.
>

**M3. AGENT-RUNNABLE (SPEC-13 movement scripts). Pure strafe and diagonal.** Hold Q, then E, then W+E and S+E. Does the
body remain approximately +/-90 degrees from aim during pure strafe, with no
sqrt(2) speed surge or gait pop on the diagonal reversal?
>

**M4. AGENT-RUNNABLE (SPEC-13 jump scripts). Jump arc and bracket.** Jump standing and while running. Does JumpStart
play immediately, with the same physical arc as before and no visible hang or
premature Fall pose? Paste the 37-to-landing transition sequence.
>

**M5. NICO-ONLY (perceptual pose-pop judgment). Landing blend.** Watch feet/hips at touchdown in both jumps. Does the
standing jump blend into 39 and the running jump into 187 without a landing
pop? A failure here reopens F1/F2 with the pasted sequence; it does not
authorize terrain, swim, speed-wire, or collision work.
>

### Session 3 combat run (CB1-CB7)

Start a run-dated combat trace in DevTools. Prepare two targets with
`scenarios/combat/dummy.txt`; after the run, clean them with `reset.txt`.
Paste verdict lines, not visual summaries. F10 once during combat and retain
the run-dated CSV path.

**CB1. AGENT-RUNNABLE (`cb-protocol.txt`). Stationary swings.** Attack the first target and stand still through at
least three `SwingReceive` events. Paste `IntentOn`, `AttackSwingSend`,
`AttackStartReceive`, three `SwingReceive`, and the associated `AnimChoice`
lines.
>

**CB2. AGENT-RUNNABLE (`cb-protocol.txt`). Orbit while attacking.** Orbit the target while staying near it, so
facing changes. Paste the trace Tick rows at the bearing-delta edges plus the
surrounding `SwingReceive` lines. The answering columns are `distance`,
`bearingDelta`, `rangeEligibility`, `arcEligibility`, and `clientAction`.
>

**CB3. AGENT-RUNNABLE (`cb-protocol.txt`). Range edge.** Walk out of range mid-swing, pause, and walk back. Paste
the Tick rows at both distance edges and every `AttackSwingSend`,
`AttackStopSend`, and `SwingReceive` in that window.
>

**CB4. AGENT-RUNNABLE (`cb-protocol.txt`). Cancel and re-attack.** Cancel once (Esc/stop action), then attack once
again. Paste `IntentOff cause=user-cancel`, the single `AttackStopSend`, then
the new `IntentOn` and single `AttackSwingSend`. Include any server stop/start
echoes and the next `SwingReceive`.
>

**CB5. AGENT-RUNNABLE (`cb-protocol.txt`). Mid-swing target switch.** Switch from the first target to the second.
Paste `TargetSwitch`, `IntentOff cause=target-switch`, `AttackStopSend`, the
new `IntentOn cause=target-switch`, `AttackSwingSend`, and server echoes.
>

**CB6. AGENT-RUNNABLE (`cb-protocol.txt`). Target death.** Kill the selected target mid-swing. Paste
`AttackStopReceive cause=target-death`, `IntentOff cause=target-death`, and all
later attack-family lines through two expected weapon periods; there must be
no lingering `SwingReceive` for the dead target.
>

**CB7. AGENT-RUNNABLE (`cb-protocol.txt`). Chase attack.** Attack while moving continuously. Paste consecutive
`SwingReceive` and `AnimChoice` lines plus Tick rows showing `clipName`,
`clipB`, `blendWeight`, distance, and bearing. This is the locomotion/attack
mixer-overlap evidence.
>
