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

The parked worklist lives in `portrait-known-blank.txt`: 10 AncientProtector,
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

Run this against the accepted W3 tree. W2 attachments and W3 type-6 hair are
present; W4's type-1 npc-bare head composite is not. Record the live result
without folding the remaining W4 symptom into the accepted W3 result.

**V1. Willem, Northshire.** Find Deputy Willem (display 2072 / extra 675). Is
his authored Stormwind plate helm visibly mounted, without changing his face
or scalp texture?
> Expect: helmet visibly mounted and fitted. His W4 scalp-composite correction
> is not present, so judge the attachment independently of any exposed scalp.

**V2. Dressed humanoid NPC hair.** Check several merchants or guards with
authored clothing and visible hair. Is the hair/scalp supplied by a real
`Character\\...\\Hair*.blp`, with no clothing texture or black chunks on the
head?
> Split verdict: the hair mesh should bind a real `Character\\...\\Hair*.blp`
> (W3 PASS expectation). Clothing-atlas bleed can remain on type-1 scalp/ear
> under-passes because W4 hard-stopped on 689 forbidden type-8 changes.

**V3. Portrait Lab cycle.** Open Portrait Lab in Specimen mode and press `]`
through at least ten humanoids. Use the new copy controls to paste one active
override key, one latest portrait verdict, and one specimen id/model pair.
>

**V4. Player collision spot-check.** Create or select a few visibly distinct
race/sex/customization combinations. Record any creation-vs-render mismatch.
The offline W6 baseline is 634/634 Ready, but 359 specimens encounter a
duplicate `CharSections` key; their exact file-order winners are the proposed
7C-3 protocol, not yet a renderer fix. It remains queued for its own ruling.
>
