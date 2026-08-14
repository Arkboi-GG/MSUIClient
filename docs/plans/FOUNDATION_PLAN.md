# MSUI Client — Foundation Plan (Plan 00)

**The shared-language layer: how we test, how we speak, and how every later step gets planned.**

Companion to `PROJECT_HANDBOOK.md` (Draft 24). This document does **not** fix
rendering. It builds the instrument panel and the working agreement that every
rendering fix will run through, so we stop trying reasonable-seeming things at
random and start closing a loop we can both read.

**Status: BUILT (2026-07-25).** Plans 01–04 and 06 are code; 05's `TuningState`
exists in `GameLoop/Dev/GameLoop.DevTools.cs` but the HUD is not fully reorganized. §11's plan
index is the per-step detail. Section 7 is the ordered build sequence it was
built in; sections 3–6 are the specs those steps implement and remain the
reference for what each piece is *supposed* to do.

> *This line read "Status: proposed. Nothing here is code yet" until the Draft 24
> doc sync, long after the layer shipped — handbook §1.2 had been carrying a note
> saying so. Two things it earned since: the hitch recorder killed six streaming
> hypotheses in six runs (SYSTEM_STREAMING.md §5A), and the light probe overturned
> a by-eye lighting tune (SYSTEM_EXTERIOR_LIGHTING.md §5.3). **What is still
> unexercised is the `refs/` comparison half of the loop** — §3.3's paired
> artifact has never had a real-client capture on the other side of it.*
>
> *One live violation of §12's seam, recorded here because this is the document
> that defines it: authored exterior lighting is applied from inside
> `GameLoop/Dev/GameLoop.LightProbe.cs`, which early-returns when DevTools is off. Core's
> lighting therefore depends on the dev layer running. Handbook §4 and §7.1 item
> 4 carry the fix.*

---

## 0. Why this comes first

We have two problems tangled together and we keep trying to solve the second by
guessing at the first:

1. **The client has real visual and feel defects.** Buildings that should show
   don't, lighting reads wrong, streaming stutters, the city looks off from
   certain angles.
2. **We have no shared language to describe those defects precisely.** You see
   something, screenshot it or describe it, I guess which of a dozen heuristics
   caused it, propose a change, you try it. When it "does nothing" we've burned a
   build cycle and learned almost nothing.

Problem 2 is what makes problem 1 a random walk. The handbook already knows this
— its whole working method (§6) is "prefer a measurement to another theory,"
"ask for a console paste not a description," "build a control that drives the
mechanism directly." Every hard bug in this project was killed by a *number*, not
a screenshot: the 26,000-unit collision error, the 119-bone count, the missing
attachment list. We just haven't made that loop *systematic*. Right now it's
reinvented per bug.

So the foundation is: **make the measurement automatic, make the viewpoint
reproducible, and make every visibility/lighting decision explain itself in a
form you can see and I can read at the same time.** Once that exists, planning
each feature stops being "what might be wrong" and becomes "stand at vantage X,
dump the scene, compare to the real client, read the reason code."

Your test is your eyes. My test is the dump. The foundation is the thing that
puts those two side by side on the same frame.

---

## 1. The core loop we are building

Every test, forever, produces one **paired artifact**:

```
  a named VANTAGE  (map + position + camera + time-of-day + every toggle)
        │
        ├──►  what YOU see:   a screenshot from that vantage
        │                     + the real 1.12 client screenshot from the SAME vantage
        │
        └──►  what I read:    a SCENE DUMP — a structured text/JSON report of
                              exactly what the client decided this frame and WHY
```

The screenshot answers *does it look right*. The dump answers *why did it do
that*. The vantage guarantees we are both talking about the identical spot,
angle and lighting — so when you say "the building's gone," I open the dump for
that vantage and read `group 46 'thief01': HIDDEN reason=DISTANCE_SHELL_NEAR`
instead of guessing which rule ate it.

That is the entire point of the foundation. Everything in sections 3–6 exists to
make that one loop fast, cheap and unambiguous.

---

## 2. Definition of "done" — the 70/30 rule

You named the yardstick, and it changes how we judge every step. There are two
kinds of work in this client and they are measured differently:

**The emulation core (~70%) — measured against the real 1.12 client.**
Terrain, buildings, doodads, water, characters, animation, lighting at the level
of "looks like vanilla WoW." Here *done is objective*: stand at a shared vantage,
put our screenshot next to the real client's screenshot of the same spot, and
they match — or they differ and the scene dump explains the difference on
purpose. There is a right answer and the real client is holding it. We are not
inventing; we are reproducing. This is most of the road.

**The additions (~30%) — measured against your intent.**
The painterly art pass, quality-of-life the real client never had, whatever your
AiBot fleet needs to look right, deliberate divergences. Here there is no
external truth, so *done is your eye*. The vantage system still applies — we A/B
before-vs-after from the same spot — but the reference is a written note of what
you wanted, not a real-client capture.

Why this matters for planning: **the first question on every backlog item is
"emulation-core or addition?"** because that decides whether we go capture a
real-client reference or write down an intent. Getting this wrong is how you end
up hand-tuning a slider by eye for something the real client already answers
exactly (lighting constants, draw distance, cull ranges), or conversely
chasing "matches vanilla" on something that was always meant to be your own.

The handbook already has one live example of the split done right: movement-stop
smoothing (§3.14). WoWee's grace window is "correct" by their code, but your eye
said it feels awful, so feel won. That's an addition-class decision. Buildings
showing or not showing is emulation-core — the real client has an exact answer.

---

## 3. Pillar A — the test harness (the "how we speak" layer)

This is the heart of the foundation. Five pieces, smallest and highest-leverage
first.

### 3.1 Vantages — reproducible viewpoints

**Problem it solves.** "Near Stormwind's gate" is not a location. You and I and
the real client all need to stand in the *exact* same place, facing the same
way, at the same time of day, or the comparison is worthless and any "it
changed" observation is noise.

**What it is.** A named, saved snapshot of everything that determines what's on
screen except the geometry itself:

- map, world position (x, y, z), current tile [col, row]
- camera yaw, pitch, orbit distance/zoom, FOV, far plane
- player facing and animation state (so character shots reproduce too)
- time-of-day / atmosphere preset
- **the full state of every visibility, lighting and debug toggle**

**Behaviour.**
- `Save vantage` writes the current state to a vantages file under a name you
  type (e.g. `sw-front-gate`, `northshire-abbey-approach`, `goldshire-inn-int`).
- `Load vantage` teleports the player, sets the camera, sets the time-of-day and
  restores every toggle, so the scene is bit-for-bit the conditions of the save.
- A dropdown of saved vantages in the HUD; also loadable from config at launch so
  a session can start already parked at the problem.

**Why this is step one.** It is small (position + camera + toggles are all
already in memory), and it upgrades the communication channel immediately: the
moment vantages exist, "the building is gone" becomes "the building is gone at
`sw-front-gate`," which is a thing we can both reproduce on demand and which the
scene dump can be keyed to.

### 3.2 The Scene Dump — coherent data output on one keypress

**Problem it solves.** This is the "coherent data output for me" you asked for.
A screenshot tells me the building is missing; it does not tell me *why the
client decided to skip it*. The dump does.

**What it is.** A hotkey (say `F9`) writes a single structured report — plain
text or JSON, whichever reads cleaner; I'll pick when we build it — describing
what the client did for the current view. It should be complete enough that,
combined with the vantage, it fully reproduces the situation. Contents:

| Block | Fields |
|---|---|
| Header | timestamp, vantage name (if loaded), map, client build |
| Camera | world pos, yaw, pitch, zoom, FOV, far plane |
| Player | world pos, facing, tile [col,row], anim/state |
| Atmosphere | preset/time, sun dir+colour+intensity, ambient colour+intensity, fog start/end, clear colour |
| Terrain | resident tiles, pending-discovery count |
| WMO | per near instance: root path, position; per group: index, name, flags (INT/EXT, ALWAYS_DRAW, antiportal), vert count, distance, **decision + reason code**, override state |
| Doodads | placed / drawn / distance-culled / frustum-culled counts; M2 queue depth |
| Perf | frame time, FPS, per-pass CPU submission ms, delayed per-pass GPU ms, update-phase ms |
| Toggles | current value of every visibility/lighting/debug switch |

The WMO block is the one that ends the building argument: a line per group with
the *reason it was drawn or skipped* (§3.4). The Toggles block means a dump is
self-describing — I never have to ask "was occlusion on when you took this?"

**How it pairs.** You press `F9` at the same instant you screenshot (or the dump
can auto-name itself with the vantage + timestamp so the screenshot and dump
line up by name). You send me both. That's the atomic unit of every test from
here on.

### 3.3 The paired-artifact convention

**The rule:** no visual bug report without its dump, and no dump without its
vantage. One screenshot + one dump + one vantage name = one testable
observation. Optionally a real-client screenshot from the same vantage for
emulation-core items (§2).

This is a working agreement more than code. It replaces "here's a picture, what's
wrong" — the lossy channel you're trying to escape — with a packet that is
unambiguous by construction. When something regresses, we diff two dumps from the
same vantage and the changed reason code names the cause.

For real-client references, we keep a small folder of vanilla screenshots keyed
by vantage name (`refs/sw-front-gate.png`). Capturing these is a modest upfront
cost that pays back every time we revisit a spot. Later, optionally, the client
can overlay the reference image at reduced opacity for direct alignment — nice to
have, not foundational.

### 3.4 Visibility reason codes — every decision names itself

**Problem it solves.** This is the direct fix for your building example. Today,
whether a WMO group draws is decided by a stack of independent heuristics —
frustum cull, distance cull, antiportal rejection (§3.33), the distance-shell
impostor swap (§3.34/§3.35), the 120-yard interior cull (§3.26), occlusion cull
(§3.35) — and when a real building vanishes, we don't know which one ate it. We
guess, flip a toggle, and look. That is the random walk.

**What it is.** Every group's draw/skip resolves to exactly **one winning reason
code**, decided in a documented precedence order, and that code goes into the
dump and the middle-click picker. Proposed set:

```
DRAWN                        submitted normally
DRAWN_SHELL_FAR              drawn as a distance impostor (§3.34)
NOT_RESIDENT                 assets not streamed in yet
FRUSTUM_CULLED               outside the view frustum
DISTANCE_CULLED              beyond its draw distance  (report dist / limit)
ANTIPORTAL_SKIP              occlusion-only geometry (§3.33)
SHELL_NEAR_SUPPRESSED        a distance-shell hidden because you're close (§3.34)
INTERIOR_CULL_120YD          interior group beyond the temp 120yd rule (§3.26)
OCCLUSION_CULLED             all 8 corners blocked by nearer geometry (§3.35)
OVERRIDE_SHOW / OVERRIDE_HIDE   curated truth wins (§3.5)
```

**Why it's foundational, not a fix.** It doesn't change *what* the client shows —
it makes the client *tell us what it chose and why*. The building example becomes
a thirty-second diagnosis: load the vantage, dump, read the reason on the group
you expected. If it says `SHELL_NEAR_SUPPRESSED`, the impostor classifier grabbed
a real building; if `INTERIOR_CULL_120YD`, the interior heuristic did; if
`ANTIPORTAL_SKIP`, a real group is flagged antiportal. Three completely different
root causes that today look identical from a screenshot. Once we can *see* which
one it is, the actual fix is usually small — and until we understand it, §3.5
lets us override it by hand.

### 3.5 The visibility override database — your "manual click" idea, made real

**Problem it solves.** Two things. First, some of these visibility calls may never
have a clean heuristic — Blizzard hand-authored a lot of this and a rule that's
right for Stormwind is wrong for Ironforge. Second, and more important: **we need
a way to always be able to make it look right, even before we understand why it's
wrong.** That's the guarantee that de-risks the entire visibility area.

**What it is.** Exactly what you described — a hand-authored database of "show/
hide this thing here," built by clicking. Concretely:

- A file (`visibility_overrides.json`) of entries keyed either by WMO identity
  (`root path` + `group index`) or by a world-space region box.
- Each entry: `rule: SHOW | HIDE`, a free-text `note`, and the `vantage` it was
  authored from (so future-us knows the context).
- The renderer consults overrides **last**, as the highest-precedence reason
  code. Curated truth beats every heuristic.
- **Authoring is one click.** We already have triangle-accurate middle-click
  group picking (§5.3, the `[pick]` tool). Middle-click the offending group,
  press one key → it appends a `HIDE` (or `SHOW`) entry for that exact group. The
  DB grows by pointing at problems, which is precisely your instinct.

**Why it's a foundation piece and not a hack.** It converts the visibility problem
from "solve every heuristic perfectly or live with it broken" into "make it look
right now by hand, then optionally upgrade specific cases to a general rule
later." Worst case, we hand-annotate a city and it looks correct. Best case, the
override DB becomes *training data* — patterns in what we hand-hide/show tell us
what the missing heuristic actually is. Either way you are never stuck staring at
a wrong frame you can't fix. It is the safety net under all of Pillar A.

---

## 4. Pillar B — the HUD cockpit

The HUD is crude and disorganized today, and that directly taxes the test loop:
if finding the right toggle takes twenty seconds and half the controls are
unlabeled, we test less and guess more. The reorg is *in service of the loop*,
not cosmetics.

**Design rules (lifted from the handbook's own method, §6):**

- **Group by concern, collapsible, stable order.** Proposed sections: *Scene &
  Vantage* (save/load, dump, time-of-day) · *Visibility* (all the culls +
  override add/remove + the live reason readout) · *Lighting/Atmosphere* ·
  *Streaming & Perf* (the existing CPU/GPU timings, queue depths) · *Character &
  Gear* (the existing solo/bind-pose/geoset instruments) · *Debug Draw*
  (collision, capsule, picker).
- **Every control that changes a mechanism should be drivable directly.** "Build
  a control that drives the mechanism directly" solved the strafe twist; "solo
  beats hide" solved the geoset overlap. New instruments must be able to isolate
  their suspect — "an isolation control that cannot isolate the suspect is not an
  instrument" (§6). This is the acceptance test for any HUD control we add.
- **The reason readout is always visible in the Visibility section:** groups
  drawn / hidden this frame, broken down by reason code, plus the picked group's
  full line. That turns the HUD itself into a live version of the dump.

Scope discipline: the HUD reorg is bounded. It exposes the Pillar-A features and
tidies what exists. It is not a rewrite and not a place to add new rendering
behaviour.

---

## 5. Pillar C — the per-step plan template

This is what replaces "try a reasonable thing." Every future feature/fix gets a
short plan with these fields, and we don't start work until the plan is filled:

1. **Problem.** What looks or feels wrong, stated *from a vantage* — "at
   `sw-front-gate`, the entrance keep is missing." Attach the paired artifact.
2. **Class.** Emulation-core or addition (§2). Decides the yardstick.
3. **Target.** For emulation-core: the real-client reference at that vantage, and
   what it shows. For additions: a written note of what you want.
4. **Hypotheses.** Ranked, each *falsifiable by the dump*. "It's the impostor
   classifier" is a hypothesis the reason code confirms or kills in one dump.
5. **Resources.** Which WoWee / SuperUI files and which handbook § bear on it
   (the handbook's §10 table already maps a lot of these). Check the references
   before writing — the handbook notes we've twice written from scratch what
   already existed.
6. **Tools / instrument.** Which existing instrument tests it. **If none can
   isolate the suspect, the first task is building the instrument, not the fix.**
7. **Test protocol.** The exact vantage, the dump fields that will confirm/refute,
   and the visual you'll check. Written *before* the change so we can't fool
   ourselves after.
8. **Definition of done.** Emulation-core: matches the real client at the vantage,
   dump explains any residual difference. Addition: matches your intent by eye,
   A/B'd from the vantage.
9. **Fallback.** For visibility work, the override DB (§3.5) — so no step can hard
   block. For others, the smallest partial win that still ships.

A plan that can't fill field 7 isn't ready — that's usually the signal we're
missing an instrument, which is itself the real next task.

---

## 6. Backlog, triaged into the template

The current open work (handbook §4 "not yet verified" and §7.1 "immediate next
steps"), sorted by class and by whether it's even plannable yet or needs an
instrument first. This is the map you said we're missing — not the plans
themselves, but where each item sits and what it's waiting on.

| Item | Class | Blocked on an instrument? | Notes |
|---|---|---|---|
| Buildings shown/hidden wrong (your example) | core | **No — this is what Pillar A unblocks** | The reason codes + override DB turn this from guesswork into read-and-fix. First real application. |
| Stormwind shell swap (near/far) | core | No, once dump reports `DRAWN_SHELL_FAR` vs `SHELL_NEAR_SUPPRESSED` | §3.34 needs runtime confirmation; the dump gives it. |
| Interior WMO visibility (120yd temp rule) | core | Partly — needs the reason readout to see how often it fires wrongly | Real fix is portal traversal (§3.26), but measure first. |
| Lighting reads wrong (warm/dim, blowout) | core | No — vantage + time-of-day reproduction | Real client holds exact constants; compare side-by-side at a fixed vantage. §3.35 already retuned by eye — upgrade to reference-matched. |
| Streaming stutter | core | No — the CPU/GPU/stream timers already exist; needs the dump to correlate a hitch to a phase | §3.27/§7.1 say instrument before changing architecture again. |
| Ground/stair/fence feel | core-ish | Has instruments (collision draw, capsule) | Needs your validation passes (§4). |
| Face composite, geoset rules, torso-yaw constant | core | Has instruments (solo, bind-pose) | Verify against real client / WoWee constant. |
| Liquid rendering | core | Not yet — parsed, drawn nowhere (§7.1) | New surface; plan when we start it. |
| BLP alpha proper fix | core | No | Localized (§3.19). |
| Painterly / art pass | **addition** | No — vantage A/B | Yardstick is your intent, not the real client (§2). |
| Networking (P2+) | core (behaviour) | Separate track | Out of scope for the visual foundation. |

The pattern: **almost every visual item is either already unblocked by Pillar A
or is waiting on a measurement Pillar A provides.** That's the argument for
foundation-first in one table.

---

## 7. Build sequence for the foundation itself

Ordered so each step is small, testable, and makes the next one easier. Each
foundation step has its own "how we know it works" — the foundation has to
bootstrap the very loop it's creating.

> **Revised build order after planning 02–06: 01 → 03 → 02 → 04 → 05 → 06** —
> reason codes (03) now precede the dump (02). Rationale and the cross-cutting
> decisions are in §11. Vantages (01) still comes first and is unaffected.

1. **Vantages (§3.1).** Save/load position + camera + time-of-day + toggles.
   *Done when:* you save `sw-front-gate`, walk away, load it, and land back in the
   identical frame — same spot, same angle, same lighting, same toggle states.

2. **Scene Dump (§3.2).** `F9` writes the structured report; auto-named by
   vantage + timestamp.
   *Done when:* a dump taken at a vantage contains enough to reproduce the frame,
   and I can read it cold and tell you what the client was showing without a
   screenshot.

3. **Visibility reason codes (§3.4).** Thread one winning reason through the WMO
   draw/skip path; surface it in the dump and the picker.
   *Done when:* at `sw-front-gate`, the dump names why the entrance keep is drawn
   or skipped, and middle-clicking it shows the same reason.

4. **Override DB (§3.5).** File + renderer consults it last + one-key add from the
   picker.
   *Done when:* you middle-click a wrongly-shown group, press the key, reload, and
   it's gone — with an entry written you can read back.

5. **HUD cockpit reorg (§4).** Sectioned, collapsible, exposes 1–4 and the live
   reason readout.
   *Done when:* every control has a home and a label, and the Visibility section
   shows the per-reason counts live.

6. **Template + reference folder (§5, §3.3).** Adopt the per-step template as the
   working agreement; start the `refs/` folder of real-client captures at the
   vantages we care about.
   *Done when:* the next real fix (the building) is written up in the template
   before any code changes.

After step 6, the loop exists and the building problem — your example — is the
first thing we run through it, as the proof the foundation earns its keep (§8).

---

## 8. Worked mini-example: the missing building, run through the new loop

To show the foundation is worth building, here's how your exact example flows
through it once steps 1–6 exist. (This is illustration, not a plan to execute
now.)

- You're standing where a real exterior WMO should be visible and isn't. You
  `Save vantage` → `missing-building-01`, and `F9` → dump. You screenshot. You
  send me all three. (You also grab the real 1.12 client at the same spot — it's
  emulation-core, so the real client is the truth.)
- I open the dump, find the WMO block, and read the line for the group you
  expected. Instead of guessing, I see one of:
  - `ANTIPORTAL_SKIP` → a real group is mis-flagged antiportal; the fix is in how
    we read that flag, and meanwhile `OVERRIDE_SHOW` makes it appear now.
  - `SHELL_NEAR_SUPPRESSED` → the distance-impostor classifier (§3.35) grabbed a
    real building as a shell; fix the classifier's vert/flag test, override in the
    meantime.
  - `INTERIOR_CULL_120YD` → the interior heuristic hid an exterior-ish group; this
    is the portal-traversal debt, and the override holds the line until then.
  - `NOT_RESIDENT` → it's a streaming problem, not a visibility one — completely
    different track.
- Whatever it says, two things are now true that weren't before: I know the actual
  cause instead of proposing one, and you can make the frame look right *today*
  via the override DB regardless of when the general fix lands.

That is the difference between the foundation and no foundation: the same bug goes
from a multi-round guessing exchange to a single paired artifact and a named
cause.

---

## 9. What I need from you

- **Which vantages matter first.** A handful of spots where the client looks
  wrong to you — name them as we go; each becomes a saved vantage + a real-client
  reference. The building spot is the obvious first.
- **Real-client captures** at those vantages, for the emulation-core items. Same
  position/angle/time-of-day as best you can; the vantage system will let us get
  close and the dump records any mismatch.
- **The WoWee / SuperUI files** the handbook §10 table lists, *when* each matching
  step starts — especially the WMO visibility path if the building turns out to be
  the interior/portal heuristic.
- **A decision I'll bring back to you before building:** dump format (plain text
  vs JSON) and the exact hotkeys, once we start step 2. Small, but yours to call.

---

## 10. What this plan deliberately is not

- Not a rendering change. Sections 3–4 add *observability and control*, not new
  visual behaviour (the override DB is the one exception, and it only ever does
  what you explicitly click).
- Not a rewrite of the HUD or the renderer. The reorg is bounded (§4).
- Not a commitment to match WoWee or the real client's *method* anywhere. The
  real client is our *reference for the result* on emulation-core items (§2) and
  nothing more — the bible is still "looks and feels the way Nico wants."

---

## 11. Reconciliation & build order (after planning 02–06)

Planning Plans 02–06 as a set (rather than one at a time) surfaced interlocks that
reshaped the earlier steps. Locking the results here.

**Revised build order:** 01 Vantages → 03 Reason codes → 02 Scene Dump →
04 Override DB → 05 HUD + TuningState → 06 Template. The dump's value *is* the reason
codes, and both the dump and the override DB call the shared predicate step 03
builds, so 03 precedes 02. Overrides (04) follow the dump so we see a problem before
curating it. HUD + `TuningState` (05) is last of the mechanisms because it
reorganizes and consolidates a now-known set instead of a moving target. Template
(06) formalizes the agreement once the artifacts it standardizes exist.

**Cross-cutting decisions locked while planning the set:**

1. **One shared predicate, `ClassifyGroup` (Plan 03).** Draw, dump, picker and
   override all resolve visibility through the same method, so the reason reported
   can never drift from the reason acted on.
2. **Reasons live at three stages, and the dump reads all three (03/02).**
   Build-time drops (antiportal / empty / unbuilt / missing) — retained as
   `Model.Rejected` instead of only counted; render-time culls; and `NOT_RESIDENT`
   (a placement in a resident ADT with no live instance). "The building is missing"
   is answered at whichever stage owns it.
3. **`CaptureVantage()` is the reproducible half of every dump (01/02).** It returns
   a serializable object the dump embeds, so a dump is `{ vantage, decisions }`.
4. **`TuningState` is the single tunable store (05).** Vantage capture and the dump's
   toggle block both read it. Plan 01 ships interim per-owner mirroring and migrates
   onto `TuningState` when Plan 05 lands; the DTO is a subset, so nothing is wasted
   and old vantages still load.
5. **Overrides beat heuristics but not invariants (04).** `OVERRIDE_SHOW` bypasses
   the guess-culls (shell / interior / distance / occlusion) but still respects
   residency and frustum. Identity is `(root, groupIndex)` for unique city WMOs, with
   a region box for repeated models.

**Plan index:**

| # | File | Step | Status |
|---|---|---|---|
| 00 | `FOUNDATION_PLAN.md` | the shared-language layer | this doc |
| 01 | `PLAN_01_VANTAGES.md` | reproducible viewpoints | **BUILT** |
| 02 | `PLAN_02_SCENE_DUMP.md` | coherent data output | **BUILT** |
| 03 | `PLAN_03_REASON_CODES.md` | one reason per group | **BUILT** |
| 04 | `PLAN_04_OVERRIDE_DB.md` | curated show/hide | **BUILT** |
| 05 | `PLAN_05_HUD_TUNINGSTATE.md` | DevTools extraction + TuningState | seam **BUILT**; TuningState / full HUD move deferred |
| 06 | `PLAN_06_TEMPLATE.md` | per-step template + refs (`PLAN_TEMPLATE.md`, `refs/`) | **BUILT** |

The foundation loop is complete: name a vantage, dump the scene, compare to
`refs/<vantage>.png`, read the reason codes, and curate with the override DB — all
behind the DevTools switch. Troubleshooting the actual look/feel starts from here,
one `PLAN_TEMPLATE.md` at a time.

**Plans written against this template since (not part of the foundation layer;
handbook §1.2 is their index):**

| # | File | Step | Status |
|---|---|---|---|
| 07 | `PLAN_07_HITCH_RECORDER.md` | automatic frame-spike recorder | **BUILT** — caught the freeze on the first walk |
| 08 | `PLAN_08_INCREMENTAL_RESIDENCY.md` | per-tile ownership, budgeted adoption | D1 built; **D2/D3 open**; D4/D5 dropped with reasons |
| 09 | `PLAN_09_EXTERIOR_LIGHTING.md` | sky/fog/ambient/sun from `Light.dbc` | **BUILT** — see `SYSTEM_EXTERIOR_LIGHTING.md` |
| 10 | `PLAN_10_WMO_PORTALS.md` | portal traversal from MOPV/MOPT/MOPR | **D1 (instrument) BUILT; traversal not** |

The `refs/` half of the loop is still unused — see the status note at the top of
this file.

---

## 12. The DevTools seam - core decides, the dev layer observes

Raised while building step 1: as we add reason codes, dumps, vantages, overrides
and HUD, we must not weave troubleshooting through the client. This tooling will
never be removed - it becomes part of the client - so it has to be built to switch
off cleanly for a release. The rule that keeps it clean:

**Core decides; the dev layer only observes, presents and authors.** Anything the
client must run to render or simulate stays in core. Anything that merely watches,
shows or edits lives in one DevTools module behind one switch. The boundary between
them is a thin, stable set of read-hooks, so the dev layer can grow "bigger and
better" without ever reaching back into core.

| Tier | What | Where | In a release |
|---|---|---|---|
| Core decision | `ClassifyGroup` returning a reason; movement; streaming | renderers / game core | on (the reason return is free) |
| Core read-hooks (the seam) | per-frame counters, `ExplainGroup`, pick accessor, live tunable values (defaulted from config) | thin public members on core | on, ~free |
| Dev layer | every ImGui panel, the scene dump, vantage capture/apply UI, reason readouts, override *authoring*, the TuningState editor | one `DevTools` module (`DevOverlay`) | **OFF via one switch** |

**One switch, one wiring point.** `_dev = config.DevTools ? new DevOverlay(seams) :
null;` then `_dev?.Update()` / `_dev?.Render()`. A release ships the flag off: one
null-check per frame, all tooling dormant. A runtime flag (not scattered `#if`)
keeps the dev code always-compiled so it cannot bit-rot, and lets a shipped client
be debugged by flipping one setting. If zero dev code in the shipped binary is ever
wanted, wrap the single DevTools namespace in one `#if DEVTOOLS` - one guard, not a
thousand.

**The override DB straddles the seam on purpose.** The click-to-author editor is
dev-only; the resulting override *data* ships and is honoured by `ClassifyGroup`, so
a curated "make it look right" fix reaches players without the editor.

**What every step from here obeys:** no dev UI in core, ever; core exposes
read-hooks and decisions-with-reasons; all tooling lives in DevTools behind the
switch. This reshapes **Plan 05** from "HUD reorg + TuningState" into "extract the
dev overlay into the switchable DevTools module." Step 1's Vantages HUD (added to
`Program.cs` to get the loop working) migrates into `DevOverlay` there - it is
already a clean class, so the move is free. Step 3's `ClassifyGroup` / reason work is
pure core and is unaffected. Fold this rule into the next PROJECT_HANDBOOK draft so a
cold session inherits it.
```
