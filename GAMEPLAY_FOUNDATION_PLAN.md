# Gameplay Foundation Plan — instruments for the entity/UI plane

Status: Draft 1 — 2026-07-30 — planned against SYSTEM_GAMEPLAY_UI.md Draft 6,
`July-30-20206-9AM-HANDOFF.md`, NEXT_07, PORT_SESSION_2026-07-30, and the existing
foundation (`FOUNDATION_PLAN.md`, PLAN_01–07, `Program.DevTools.cs`, `EMPIRICAL_CHECKS.md`).

## 0. The thesis: we already solved this problem once

World-rendering debugging used to be the same "black hole": a screenshot of a missing
building, a guess, a patch, another screenshot. The foundation killed that by building
five instruments **before** chasing more bugs:

1. **Vantages** — reproducible state (PLAN_01, `vantages.json`, `[vantage]` echo).
2. **Reason codes** — every draw/skip decision resolves to one enum value, and the
   reason *reported* is computed by the same predicate as the reason *acted on* (PLAN_03).
3. **Scene dump** — F9 writes machine-readable state + decisions to `dumps/` (PLAN_02).
4. **Override DB / TuningState** — single-variable knobs, persisted (PLAN_04/05).
5. **Structured handback** — `EMPIRICAL_CHECKS.md`: Nico runs a checklist and pastes
   verdict lines; the assistant reads measurements, not prose descriptions of pixels.

The gameplay plane — portraits, action bars, spell animation, casting, unit frames —
has **none of this yet**, except one embryo: the `[portrait] ... BLANK (subject=, rgb=,
camera=, pieces=)` line, `portrait-diagnostics/*.png`, and `tools/portrait-camera-check`.
That embryo is why NEXT_07 could be root-caused at all. This plan generalizes it.

**The translation.** A world bug is located by *where you stand*; a gameplay bug is
located by *what state the entities and UI are in*. So each foundation instrument has a
gameplay twin:

| World foundation | Gameplay twin | Why |
|---|---|---|
| Vantage (pos, camera, toggles) | **Scenario** (specimen model, unit fields, selection, action slots, pending cast) | the reproducible half |
| WMO reason codes | **Verdict enums** per decision: bake outcome, button state, clip choice, cast-target resolution | "why does it look like that?" becomes a lookup |
| F9 scene dump | **F10 gameplay dump** (`dumps/gameplay-*.json`) | the coherent data channel |
| Override DB (portal/WMO tuning) | **Per-model framing overrides + Labs** with live sliders | the "fine-tune a portal" workflow, aimed at portraits/anims/buttons |
| `refs/<vantage>.png` | **refs/gameplay/** goldens from the real 1.12 client + scripted Benilla bakes | the yardstick |
| Hitch recorder | **Wire recorder** (packet tap + replay) | server-dependent bugs become deterministic files |
| EMPIRICAL_CHECKS.md | **CHECKS_GAMEPLAY.md** | Nico's leverage: paste verdicts, not descriptions |

Two hard rules carry over unchanged from `PLAN_TEMPLATE.md`:

- **If a defect's test protocol can't be written, stop — the missing instrument is the
  real task.** (Portraits proved this: the fix only became findable after the BLANK
  line + diagnostics PNG existed.)
- **Report must equal act.** Every verdict shown by a lab/dump must come from the same
  code path the game uses, never a parallel re-computation that can drift.

And one new rule this plane needs:

- **Batch beats anecdote.** One wolf portrait is an anecdote; 60 baked portraits with a
  verdicts CSV is a pattern ("all quadrupeds blank, all short races cropped"). Every
  instrument here should have a *sweep* mode, because sweeps are what an assistant can
  actually reason over.

## 1. Nico's assets, and what each is for

- **Real 1.12 client** → the *presentation* yardstick: what it must look like.
  Screenshot source for `refs/gameplay/`.
- **Runnable Benilla** → the *behavior and math* yardstick: what values the law
  produces. Source citations stay primary; a scripted Benilla portrait bake (if its
  booth can be invoked headlessly) is a bonus golden, not a dependency.
- **Local vmangos with GM** → the *scenario* machine: spawn any creature, learn any
  spell, teleport, set health/mana. This is how "cycle ALL portraits **in game**"
  becomes one command instead of a world tour.
- **The MSUI DevTools layer** (`Program.DevTools.cs`, gated by `config.DevTools`) →
  where every in-game instrument below lives. Core never references it; that
  separation is kept.

## 2. The instruments

Ordered by build order (§3 justifies it). Each follows the PLAN template shape in
miniature; the ones marked **[SLICE 1]** are specced deepest and are the next build.

---

### I1 — Verdict enums + the `[verdict]` channel  **[SLICE 1]**

**Problem.** Portraits have a real verdict line; nothing else does. Action-button
state is computed and drawn but never explained (`Program.ActionBars.cs` now has the
tri-state usable/range/flash pass — invisible at runtime). Animation selection is the
worst: the post-cast freeze was a *silent* Stand substitution inside the animator; it
took a Benilla source dive to even name the failure mode.

**Target.** Four decisions become enums, computed once, consumed by both the game and
the reporting channel:

- `PortraitVerdict` — formalize what the log line already encodes:
  `Ready | BlankOffFrustum | BlankAuthoredEmpty | NoPieces | TinySubject | Degenerate`
  plus the inputs (camera source, subject px, rgb/alpha range, framing numbers).
  Lives beside `BakeDirtyPortraits` (`Program.Portraits.cs`); the existing console
  line and diagnostics PNG become renderers of this struct.
- `ActionButtonVerdict` — per slot: `{ Usable | NotEnoughPower | Unusable }` ×
  `{ InRange | OutOfRange | NoRangeCheck }` × flash/checked/pushed/carried, **with the
  inputs that produced it**: spell/item id, computed cost, current power, base mana,
  range index, min/max after reach math, distance to target. The predicates in
  `Program.ActionBars.cs` are refactored to return this struct; drawing switches on
  it (the PLAN_03 `ClassifyGroup` move, exactly).
- `AnimChoice` — every time the animator resolves a clip request:
  `{ Exact | FallbackChain(path) | BakedOnDemand | MissingClip | Substituted(from→to) }`
  with track, requested id, played id. Hangs off `M2Animator.FindOrBake` and the
  spell-action paths fixed in the 09:00 pass. `Substituted` should be loud — that
  class of bug is now known.
- `CastTargetVerdict` — `Net/CastTargetLaw.cs` is already pure and tested; give its
  result a reason field (`SelfFallback | SelectedUnit | Refused(reason) | Ground | Item`)
  and echo it on every cast send.

**Channel.** One helper: `Verdict.Log("portrait", struct)` → a `[verdict:portrait]`
console line **and** a rolling in-memory ring (last ~200 verdicts) that the F10 dump
(I3) and the labs read. Console stays human-eyeballable; the ring is the machine
channel.

**Files.** `Engine/Verdicts.cs` *(new: structs + ring)*; edits in
`Program.Portraits.cs`, `Program.ActionBars.cs`, the animator, `Program.Casting.cs`.

**Test protocol.** Drain mana → slot verdict flips to `NotEnoughPower` with the cost >
power numbers visible in the line. Cast with a hostile selected → `SelfFallback`.
Cast a spell with a missing clip → `MissingClip`, never `Substituted`, and locomotion
resumes. Bake a portrait → the same struct appears in console, ring, and (once I3
lands) the dump. **Done:** every verdict line's values can be traced to the exact
branch that drew the pixel, with no second computation.

---

### I2 — Portrait Lab + specimen cycler  **[SLICE 1]**

The direct attack on the live defect, and the template every later lab copies. This is
the "fine-tune a portal" workflow pointed at portraits.

**Problem.** NEXT_07's fix (model-adaptive framing) ships constants — `0.92*head`,
`window 0.34*head clamp [0.55, 1.10]`, `fovy 34°`, pitch `0.02` — that are *derived
from Benilla but must be tuned by eye across the whole bestiary*. Today tuning one
constant means: edit C#, rebuild, log in, find a wolf. That loop is minutes-long and
single-specimen. It must be seconds-long and any-specimen.

**Target.** A DevTools "Portrait Lab" section:

- **Specimen picker.** Player (current character) + a browsable list of creature
  display ids from `CreatureDisplayInfo.dbc` → `CreatureModelData.dbc` (both already
  parsed for the world). A text filter, plus `[` / `]` hotkeys to cycle the list —
  the booth loads the model client-side through the existing `CreatureRenderer`
  path; **no server spawn needed** to look at any model in the game data.
- **Live re-bake.** Sliders for the framing inputs: eye height, distance, fovy,
  pitch, yaw offset, near plane, window min/max, head-fraction — and a camera-source
  radio: `authored | bounds | manual`. Any change marks the bake dirty; the result
  appears next frame. (The bake path exists: `PortraitRenderTarget` + the booth
  contract from PORT_SESSION §2. The lab only feeds it parameters and displays it.)
- **Evidence panel.** The baked 256² texture drawn at 2–4×, un-masked, with the
  `PortraitVerdict` fields beside it: subject px, rgb/alpha ranges, camera source,
  pieces, `BindPoseHeight()`, and the frustum-vertex count (fold
  `tools/portrait-camera-check`'s counting into the engine so the lab shows
  "1,224 verts in clip volume" live).
- **Override store.** A `Save framing override` button writing the current sliders to
  `portrait-overrides.json` keyed by model path (creatures) / race-gender (players),
  applied between derived framing and the bake — PLAN_04's precedence idea:
  `override > authored > bounds-derived`. Committed to git like `vantages.json`.
  This is the escape hatch that guarantees no single model can hard-block: any
  stubborn specimen gets a hand-tuned entry instead of another constants hunt.

**Files.** `Program.DevTools.Portraits.cs` *(new)*; `Engine/PortraitOverrides.cs`
*(new: store + lookup)*; small hooks in `Program.Portraits.cs` (parameterize the
framing function; consult overrides); expose `BindPoseHeight` and a
clip-volume-count helper.

**Test protocol.** Open the lab, cycle Gnome → Dwarf → Human → Tauren → wolf →
whelp without leaving the spot; each bake lands non-blank with verdicts visible.
Drag distance until the head crops; the subject count tracks it. Save an override for
one creature; relog; the override still applies. **Done:** any model in the MPQs can
be framed, inspected, and if needed hand-tuned, in under ten seconds each, without a
rebuild.

---

### I3 — Batch bake + contact sheets  **[SLICE 1]**

**Problem.** "Troubleshoot ALL portraits" cannot mean eyeballing them one at a time,
and an assistant can't pattern-match across screenshots that don't exist.

**Target.** `tools/portrait-batch-bake` (the `portrait-camera-check` pattern grown
up): serverless, MPQ-backed, uses the same booth + framing code as the client
(reference the engine project — no math duplication), and for a specimen list —
all race×gender combos + all creature display ids, or a curated `specimens.txt` —
writes:

- `portrait-batch/<id>-<name>.png` — each bake, un-masked;
- `portrait-batch/contact-sheet-NN.png` — 8×8 grids, labeled;
- `portrait-batch/verdicts.csv` — one row per specimen: id, name, model path, camera
  source, subject px, rgb range, pieces, verdict, framing numbers used.

Success gates are mechanical: zero `Blank*` verdicts; subject px within a band
(say 800–20,000 of 65,536); optional center-of-mass-in-middle-third heuristic for
crop sanity. **Diff mode:** run before and after a change, print only rows whose
verdict or subject count moved — a regression test for every framing edit,
forever.

**Test protocol / done.** Full sweep completes on installed GameData; the CSV names
every failure; handing the assistant `verdicts.csv` + one contact sheet is sufficient
to diagnose a framing bug class without a single live screenshot.

---

### I4 — F10 gameplay dump

**Problem.** F9 describes the world plane only. Every live review in the 9 AM handoff
was Nico describing UI state in prose — the exact failure mode vantages+dumps were
built to end.

**Target.** F10 writes `dumps/gameplay-<name>.json`:

- `scenario`: character (race/gender/level/equipment display ids), selection
  (guid, display id, faction/reaction, dead/lootable flags, distance), pending cast,
  open panels, UI scale + framebuffer + `GameplayUiScale()` output.
- `portraits`: the two current `PortraitVerdict` structs.
- `actionBar`: all 120 slots' packed values; for the visible page, full
  `ActionButtonVerdict` per slot.
- `animator`: per track (player + selection): requested vs playing clip id + name
  (`AnimationData.dbc`), last five `AnimChoice` entries.
- `verdictRing`: the last ~200 `[verdict]` entries with timestamps.
- `wire`: last ~100 opcodes (name, direction, size, timestamp) — the tap from I7.
- `layout`: authored-rect → screen-rect table for the bottom bar (each slot, micro
  buttons, bags, cast bar, unit frames) — turns "the backpack escaped its slot at
  1440p" into two rectangles that either match or don't.

Pair with the screenshot key so `dumps/gameplay-X.json` + `screenshots/X.png` share a
name (the PLAN_02 pairing convention).

**Test / done.** For each defect in the 9 AM handoff's sign-off list, the dump alone
(no screenshot) contains the fields that would have proven or refuted it. Two dumps
from the same scenario across a code change diff cleanly.

---

### I5 — Action Bar Lab (forced-state matrix)

**Problem.** SYSTEM_GAMEPLAY_UI checklist item 3 requires draining mana, walking out
of range, starting auto-attack, equipping items — minutes of setup to eyeball eight
states, so it gets skipped, so state bugs survive.

**Target.** A DevTools section that **overrides the inputs** to the (now
verdict-returning, I1) predicates — force oom / out-of-range / unusable / flash /
checked / pushed / carried / cooldown fraction on a chosen slot, plus an "exercise"
button that cycles slot 1 through all states at 1 s intervals for a capture. Forcing
inputs (pretend power = 0) rather than outputs keeps report=act honest.
Additionally an **all-states strip**: draw one row of dummy buttons showing every
state simultaneously for a single screenshot against the 1.12 reference (this is how
the additive-art class of bug — black hover rectangles — becomes a one-glance check).

**Test / done.** One screenshot of the strip vs one `refs/gameplay/actionbar-states.png`
from real 1.12 settles all button art; the forced states drive the *real* draw path
(verified because the verdict lines show the forced inputs).

---

### I6 — Animation Lab + clip auditor

**Problem.** The freeze/flinch bugs were animation-*selection* bugs, invisible until
someone reads Rust. `Substituted`/`MissingClip` (I1) makes them loud; this lab makes
them explorable, and the auditor makes them enumerable.

**Target.** In-game: pick player or selection → table of the model's sequences
(id, `AnimationData.dbc` name, duration, baked?), click to play any clip on a chosen
track, live animator state readout (per track: requested vs playing + last
`AnimChoice`). Offline: `tools/anim-audit` sweeps models × the anim ids referenced by
`SpellVisualKit.dbc` and the combat families (wound 8/9/10 etc.), and prints every
(model, anim id) pair that would resolve to `MissingClip` or a fallback — the entire
"post-cast freeze" bug class as one CSV, found before a user ever casts the spell.

**Test / done.** The wolf's wound clip, the LOGINEFFECT no-anim case, and a
known-missing spell clip each show the correct `AnimChoice` in the lab; the audit CSV
is empty of `Substituted` for the shipped fallback rules.

---

### I7 — Wire recorder + replay

**Problem.** Every gameplay behavior is downstream of the SMSG stream, and the stream
is currently unobservable and unrepeatable. This is the deepest cause of "black hole"
feeling: the *input data* to the system under debug is invisible.

**Target, staged.**
- **Stage A — tap (cheap, build early):** `NetworkClient` writes every opcode
  in/out to `dumps/wire-<session>.log` (name, size, hex payload ≤256 B, timestamp),
  toggleable in DevTools. Feeds the dump's `wire` block. Immediately turns "the loot
  window misbehaved" into greppable bytes.
- **Stage B — offline parse:** `tools/wire-replay` runs a recorded stream through the
  real parsers (`WorldSession` handler layer, `LootState`, descriptor updates) headless
  and prints resulting state transitions — the combat-wire-check pattern generalized.
  Regression: replay a canned session, assert final descriptor/loot/aura state.
- **Stage C — in-client replay (later):** boot the client into a recorded session
  (no server), for pixel-identical repro of server-dependent visuals. Valuable but
  invasive (time, RNG, movement echo); do not gate A/B on it.

**Test / done (A+B).** Kill-and-loot once; the log shows the documented
`SMSG_LOOT_RESPONSE` shape byte-for-byte; replaying it offline reproduces the
LootState invariants. A future desync bug's first artifact is a wire log, not prose.

---

### I8 — Scenario deck (vmangos GM) + specimen zoo

**Problem.** Even with the client-side booth, live verification needs real units with
real fields (auras, factions, healths), and finding specimens by walking is slow.

**Target.** A checked-in `scenarios/` folder of GM command scripts (and/or SQL) with
a README: `zoo.txt` spawns the extreme-bestiary pen near a chosen spot (Gnome-height,
Tauren-height, wolf, whelp, giant, critter); `states.txt` sets up the action-bar
exercise (drain mana, add cooldown spell, hostile+friendly pair for cast-target law);
`loot.txt` spawns lootable corpses. Each maps 1:1 to a checklist section so "set up
checklist 3" is paste-one-block. Document the GM account setup once in `SETUP.md`.

**Test / done.** Any live checklist in CHECKS_GAMEPLAY (§I10) can be staged in under
a minute from the scripts alone.

---

### I9 — refs/gameplay + shot-diff

**Problem.** `refs/` is still nearly empty and gameplay presentation has *two* valid
yardsticks now available (real 1.12, Benilla). Unanchored eyeballing is how the
glue-font-on-world-text class of bug got in.

**Target.** Naming convention `refs/gameplay/<surface>-<scenario>.png` (e.g.
`portrait-dwarf-male.png`, `actionbar-states.png`, `lootframe-4rows.png`), captured
from real 1.12 at a recorded UI scale; a one-page capture checklist so a single
session with the real client fills the set. `tools/shot-diff` produces side-by-side
strips (MSUI | ref | difference) — perceptual polish stays human, but geometry
(rect positions, proportions) can be measured off the pair. If Benilla's booth can be
invoked to dump portrait bakes, add those as a second golden column for the framing
math specifically; treat as opportunistic.

---

### I10 — CHECKS_GAMEPLAY.md (the handback loop)

**Problem-as-stated by Nico:** "my contribution is almost 0 beyond 'here are the
files'." False once the instruments exist — the world plane proved it. EMPIRICAL_CHECKS
answers were the highest-density information in the whole project (C1's four hitch
blocks redirected an entire performance plan).

**Target.** A living `CHECKS_GAMEPLAY.md` in the EMPIRICAL_CHECKS format: numbered
questions, each with *why it decides what*, answered by pasting `[verdict]` lines,
dump files, CSV rows, or contact sheets — **never by describing pixels in prose**.
Each work session ends by regenerating it; each live session starts by running it.
The 9 AM handoff's 8-point sign-off list becomes its first page, rewritten so every
item names the artifact that answers it (e.g. item 4 → paste the `CastTargetVerdict`
line; item 1 → the additive strip screenshot from I5).

## 3. Build order and rationale

```
I1 verdicts  →  I2 portrait lab  →  I3 batch bake  →  I4 F10 dump  →  I7a wire tap
                                                   →  I5 action lab →  I6 anim lab
I8 scenario deck: any time (docs/scripts only)     I9 refs: first real-1.12 session
I7b replay: after I4          I10 checks doc: starts at I1, grows every step
```

- **I1 first** for the same reason PLAN_03 moved ahead of PLAN_02: every later
  instrument consumes verdicts; building labs before verdicts would mean building
  them twice.
- **I2/I3 next** because portraits are the live, user-visible defect *and* the
  worked example the 9 AM handoff demands live sign-off on. Finishing the portrait
  loop end-to-end (verdict → lab → override → batch → golden) proves the whole
  pattern on one subsystem before replicating it.
- **I4 before the remaining labs** so their state is capturable from day one.
- **I7a early, I7c late**: the tap is ~an afternoon and pays immediately; full
  replay is the only genuinely large item here and nothing gates on it.

Everything above is additive DevTools-layer work in the established
`Program.DevTools.*` pattern — no renderer or protocol changes, shippable dormant,
low regression risk. The single riskiest edit is the I1 refactor of
`Program.ActionBars.cs` predicates into verdict-returning functions; its fallback is
PLAN_03 §9's: ship the verdicts as a read-only replay first, unify next pass.

## 4. How this changes the collaboration

The current loop: Nico sees wrongness → describes it → assistant reads source + Benilla
→ patches blind → repeat. Nico's information *out* is prose; information *in* is a
diff he can't evaluate. After the foundation:

1. Nico hits F10 + screenshot key at the wrongness (or runs the batch tool). Artifacts,
   not prose.
2. Assistant reads scenario + verdicts; the failing branch is *named* by the enum, as
   NEXT_07's STEP 0 table already demonstrated for portraits.
3. The fix ships with its test protocol pre-written against the instruments; Nico runs
   the CHECKS section and pastes lines/sheets.
4. Tunable disagreements (framing taste, art feel) move out of code entirely — Nico
   adjusts sliders in the lab and commits the override JSON himself. **That is a real,
   growing contribution surface that requires zero engine knowledge**, exactly like
   portal/vantage tuning was.

The pattern for every future system (auras, tooltips, vendors, talents, chat) is then
fixed: *before* porting behavior from Benilla, add its verdict enum, its dump block,
its lab section if it has tunables, its check questions. The PLAN_TEMPLATE rule,
restated for this plane: **no gameplay feature is "implemented" until its verdicts are
observable and its checks are runnable.**

## 5. One decision for Nico, and two small asks

- **Decision — hotkeys:** F10 for the gameplay dump and `[`/`]` for specimen cycling
  are assumed; veto or reassign freely (F9 stays the scene dump).
- **Ask 1:** one session with the real 1.12 client to fill `refs/gameplay/` per the
  I9 checklist, once I5's strip exists (it makes the capture worth the most).
- **Ask 2:** confirm the vmangos GM account works and note the command style/version
  in `SETUP.md`, so I8's scripts can be written against the right syntax.
