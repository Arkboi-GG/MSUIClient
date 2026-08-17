# Encounter Lab — architecture & handbook (built 2026-08-16 · raid sim 2026-08-17)

> **STATUS: BUILT & VERIFIED, HEADLESSLY *AND* IN-CLIENT.**
> `tools/encounter-lab-check`: **106/106** (offline + live world-DB, re-run
> 2026-08-17 after the raid-sim work). The in-client scripted probe
> (`MSUI_ENCLAB_PROBE`, §11): **11/11** — raid placement, live world, staging,
> GO, roam, Ctrl+F — measured in the real client on the real code paths, with
> screenshots. Owner-verified eyes-on in the lair the same day. No server
> changes, no deploy, no core work — the Lab reads the world DB through
> MangosSuperUI's **existing** CSV export and runs entirely client-side.

**What it is:** press **Ctrl+E** (live mode or creator mode) for a window that
loads an NPC's combat behaviour as a **declarative encounter definition**, runs it
as a **deterministic fixed-step simulation**, and lets you **play, pause,
single-step, scrub, rewind and re-seed** it while drawing every ability's
footprint on the real terrain. Drop a **body capsule** anywhere — with a
trajectory, if you like — and it answers *what can hit this body, when, and why*.
Every fact carries a **fidelity label**, so a shape you cannot trust never looks
like one you can.

**Since 2026-08-17 it is also a raid sim (§8):** the world runs **live from the
moment a document loads** — the boss roams her room until pulled — and a
**ten-body raid with jobs** (2 tank / 2 heal / 3 melee / 3 ranged) is placed at
your feet, commanded RTS-style from the **Ctrl+F** free view: shift-click queues
waypoints per body, the dotted plan draws on the floor, **GO** sends everyone at
once, the fight starts by proximity pull, and any paused instant accepts a
**teleport what-if** that reflows the whole future while leaving every event
before the edit bit-identical. Nobody dies — bodies count hits, never health.

---

## 1. The problem this solves, and the decision behind it

Onyxia's behaviour lives in 924 lines of compiled C++ (`boss_onyxia.cpp`). The
naive readings of "make it visualisable" both fail:

| Idea | Why it fails |
|---|---|
| Pause and rewind the live realm | `mangosd` cannot rewind. Its world update is a real-time loop over mutable global state — grids, threat tables, spell queues, aura lists, RNG — with no snapshot. This is not a property of it being *live*; it is a property of its *architecture*. |
| Port the C++ into the client and run it | The script calls `Creature`, `Unit`, `MotionMaster`, `Map`, `ObjectMgr`, `Spell`, grid queries, `ScriptedInstance`. Pulling the script pulls the core. That is not embedding a script, it is embedding mangosd. |

**So the Lab reimplements the one shape that matters** — a fixed-step `UpdateAI`
over a small, snapshottable state — and gets determinism, pause, rewind,
branching and what-if for free.

And the load-bearing decision, the one to defend hardest:

> **An encounter definition is DATA, never code.**

The moment behaviour is expressed in a real programming language it becomes
opaque to the tool meant to visualise it, and you are back to reverse-engineering
compiled logic. Everything in the format is statically analyzable, which is the
only reason the client can answer "what would land here" *without running
anything*. When the format cannot express a behaviour, it does **not** get an
escape hatch into a scripting language — it gets an ability marked
`unknown-unmodeled` and a visible hole in the coverage report. **A visible hole
beats a confident lie.**

This is what makes it repeatable for encounters that do not exist yet: authoring
a new boss as data means the tool understands it for free, forever, with no
per-boss work.

---

## 2. Layers — where logic is allowed to live

Built to `CODE_STRUCTURE_LAW.md`. The one-direction rule holds with no
exceptions: **nothing in `World/Encounters/` or `Net/` references `GameLoop`.**

```
┌─ SIMULATION SUBSYSTEM (pure; no GL, no ImGui, no GameLoop, no I/O) ────────┐
│ World/Encounters/EncounterDefinition.cs  the schema + fidelity registry    │
│ World/Encounters/EncounterGeometry.cs    footprints + capsule hit tests    │
│ World/Encounters/EncounterSim.cs         fixed-step machine, seeded RNG,   │
│                                          snapshot ring                     │
│ World/Encounters/EncounterProbe.cs       trajectories + the "why" report   │
│ World/Encounters/EncounterTranslator.cs  world-DB rows → definition        │
│ World/Encounters/EncounterLibrary.cs     JSON file format (DTO ⇄ model)    │
│ World/Encounters/EncounterSpellFacts.cs  the only file touching Formats/Net│
├─ DATA LAYER (HTTP + parsing + cache; immutable publish, background Task) ──┤
│ Net/EncounterWorldData.cs                row model + published snapshot     │
│ Net/EncounterDataClient.cs               6 tables over the CSV export       │
├─ CONTROL / UI LAYER (GameLoop partials) ──────────────────────────────────┤
│ GameLoop/Dev/GameLoop.EncounterLab.cs          window, transport, sections,│
│                                                staged orders, playbook     │
│ GameLoop/Dev/GameLoop.EncounterLab.Overlays.cs 3-D decals + screen pass    │
│ GameLoop/Dev/GameLoop.EncounterLab.Puppets.cs  rendered raid/boss models   │
│ GameLoop/Dev/GameLoop.EncounterLab.Rts.cs      Ctrl+F select/order bridge  │
│ GameLoop/Dev/GameLoop.EncounterLab.Tape.cs     live recorder + diff        │
│ GameLoop/Dev/GameLoop.EncounterLab.Probe.cs    MSUI_ENCLAB_PROBE self-test │
└───────────────────────────────────────────────────────────────────────────┘
```

**Nothing is shared with the NPC dev window.** Separate data client, separate
settings block, separate files. That window is *spatial and static* (spawns,
routes, aggro radii); this one is *temporal and dynamic*. Neither can break the
other. They share only the creator chrome, `SpellEffectMeshRenderer`, and
`DrawDashedLine`.

**Threading contract** (same as the NPC dev window's, and just as binding):
`EncounterDataClient` fetches and parses on a background `Task` and publishes ONE
immutable `EncounterWorldData` through a volatile field. Nothing background-side
touches `EntityStore`, `Settings`, ImGui or GL.

---

## 3. The encounter definition format

`encounters/*.json`, schema version 1. A future `schemaVersion` is **rejected with
an error**, never parsed into nonsense. One bad document never takes the library
down.

```
EncounterDefinition
├── key, name, primaryEntry, memberEntries[], mapIds[]
├── provenance   { source, coreBuildHash, dbRevision, contentPatch }
├── coverage     source flags — NEVER a completeness percentage
├── actors[]     boss / add / friendly — radius, reach, job, speeds,
│                idle movement, timed move orders (see 3.3)
├── phases[]
│   ├── casterFlying, meleeEnabled
│   ├── onEnter[]      steps
│   └── transitions[]  { toPhase, trigger, fidelity, steps[] }   ← choreography
└── abilities[]
    ├── trigger   Timer | HealthBelow | OnAggro | OnPhaseEnter | OnMovementDone | …
    ├── timing    initial/repeat min+max in ms — a RANGE, because urand() is a range
    ├── target    CurrentVictim | RandomHostile | Self | DatabaseLocation | …
    ├── geometry  Circle | Cone | Line | PointChain | Projectile | None
    ├── fidelity  the registry below
    ├── steps[]   optional choreography
    └── sources[] where to go and check
```

### 3.1 The fidelity registry

Taken **verbatim** from `docs/plans/DYNAMIC_COMBAT_RULES_AND_ENCOUNTER_INTELLIGENCE.md`
§11.3 rather than inventing parallel vocabulary:

| Label | Meaning |
|---|---|
| `exact-db` | Straight out of a world-DB table. Reproducible exactly. |
| `declared-cpp-manifest` | Transcribed from compiled C++ by a reviewed manifest. Stale the moment the core commit moves — which is why definitions carry `coreBuildHash`. |
| `derived-dbc` | Derived from spell data: radii, cones, cast times, missile speed. |
| `heuristic` | An informed guess; scaffolded, not reviewed. |
| `unknown-unmodeled` | Known to exist, deliberately not modeled. Draws as a hole. |

Fidelity is **never averaged**. An ability whose timing is exact but whose target
mapping needs a threat table is `heuristic` overall, because the weakest fact
governs. `WorstFidelity()` reports the weakest fact in the whole document and the
window prints it. Footprint **colour is fidelity** in every overlay.

### 3.2 Choreography steps

A deliberate subset of the core's own `SCRIPT_COMMAND_*` vocabulary — `Wait`,
`MoveTo`, `Cast`, `SetFlying`, `SetSpeed`, `Say`, `Summon`, `SetPhase`,
`DespawnSummons`, `Unmodeled` — so a sequence authored here stays translatable
into DB script rows later instead of becoming a private dialect.

Transitions own choreography because that is exactly what EventAI cannot express
today, and it is what makes a boss a boss: Onyxia's takeoff is nine ordered beats,
not one action. **A running sequence blocks ability timers**, reproducing the
real script's `m_bTransition` early-return — the difference between a believable
transition and a boss that cleaves mid-takeoff.

### 3.3 Actors, jobs, idle movement, and orders (added 2026-08-17)

All additive; the DTO layer is tolerant, so schema stays **version 1** and every
older document parses with defaults.

| Actor field | Meaning |
|---|---|
| `job` | `Tank` \| `Healer` \| `Melee` \| `Ranged` \| `None`. Drives the playbook and the melee-reach dps gate — never invents numbers. |
| `runSpeedYdPerSec` / `walkSpeedYdPerSec` | The template's real speeds (`speed_run × 7`, `speed_walk × 2.5`). Onyxia: **9.0 / 2.5**. 0 = engine defaults. |
| `detectionRangeYards` | `creature_template.detection_range`, display-only honesty beside the pull-ring slider (the core adds a level delta). Onyxia: **20**. |
| `idleMovement` | What the spawn's DB row says she does out of combat: `Stationary` (an ANSWER, not an absence — Onyxia guid 47572: `movement_type 0`, `wander_distance 0`), `Wander` (+`wanderYards`), or `Waypoints` (+`points[{position, waitMs}]`, the `creature_movement` replay — looped, exact pauses, walk speed). A declared route always plays; when the answer is "stationary" the Lab roams her anyway as an explicitly-labelled sandbox behaviour (§8). |
| `dps` | Owner-set damage per second — an INPUT to the plan, never a simulated outcome. |

| Move order (`moves[]` entry) | Meaning |
|---|---|
| `anchor: AtTime` | The original verb: at `timeMs`, start running there. Travel is paid at run speed — a late order visibly costs the trip. |
| `anchor: AfterPrevious` | Chain leg: fires when the previous entry's run arrives. Shift-click chains author these. |
| `anchor: OnPhaseEnter` + `phaseKey` | Fires the instant the fight enters that phase — "when she lifts off, go here". Dispatched from `EnterPhase`, no step lag. |
| `arrivalFacing` | Radians; the body pivots to it on arrival (the tank's back to the wall). Null/NaN = keep the run's facing. |
| `teleport: true` | The paused what-if verb: the body IS there at that instant, no travel. History before the edit stays identical; the future reflows. |

The list keeps **authored order**, not time order — chains and phase orders have
no meaningful timestamp to sort by. Firing any order retires every unfired
`AfterPrevious` leg before it, so a fresh order cancels the stale remainder of the
route it replaces instead of that route resuming underneath it.

---

## 4. The simulator

Two decisions carry the whole thing:

1. **Fixed step.** `Advance()` never reads a clock. Same inputs, same output.
   `UpdateEncounterLab(dt)` is the *only* place wall clock appears, and it decides
   only *how many* steps to take, never how big. A scrub at 200 fps and one at
   20 fps give the same fight.
2. **Seeded RNG.** Every `urand()` the real script would roll becomes a draw from
   a seeded xorshift128 stream. **A seed names a fight, forever.** Deliberately
   *not* `System.Random`, whose sequence is an implementation detail that has
   changed between .NET versions.

State is small enough to snapshot **every single step** — a boss is a phase, a
dozen timers and a handful of actors — so **rewind is a list index, not a
re-simulation**. Onyxia's full 166-second fight is 1 666 snapshots and 267 events.

Health is driven by a labelled `raid dps` dial (a fraction of max health per
second) **plus each body's owner-set dps**, purely so health-gated phases are
reachable, or pinned directly. It is never presented as a damage model. With the
**melee-reach gate** on (default), tank/melee body dps counts only beside a
grounded boss — an air phase honestly stalls her health gates.

**Movement & aggro (2026-08-17).** The pull is **authored, never assumed**: she
idles until a Friendly body crosses `PullRangeYards` of her live position; an
empty scenario idles forever (the old fight-at-t=0 shortcut for empty scenarios
made a fixtureless boss run her script against nobody, and died for it). Aggro is
an **owner-assigned timed plan** (`TimedAggro`), never inferred — she faces the
holder continuously, and post-pull, grounded and unscripted, she **chases** the
holder to melee reach at her real run speed: a tank at the back wall drags her,
and every cone comes along. Friendlies run their ordered moves (§3.3), and a
**playbook** of per-(phase × job) standing orders — `Hold` / `ChaseBoss` /
`MoveToSpot` — answers "what does melee do when she lifts off" as data. An
explicit order takes a body off autopilot until the next phase turn re-applies
its directive. Pre-pull the boss plays her declared idle movement, or the sandbox
roam — which draws from its **own seeded stream**, so however long the roam runs,
the fight's ability rolls at the pull are untouched.

**What the simulator does not have, and says so:** no threat MODEL (aggro is
owner-assigned; the fallback victim is "closest friendly", and any ability
needing threat order drops to `heuristic`), no aura system, no GCD, no resists,
no healing or damage numbers (friendly bodies count `HitsTaken` and never die —
by design), no line-of-sight or pathfinding for actor movement.

---

## 5. Geometry — a position is a body, not a point

Whether something lands depends on the body's **width and height**, the caster's
**facing**, and where the body is **at impact** rather than at cast. So every test
takes a `BodyCapsule` (feet, radius, height) and footprints are evaluated at their
impact moment, with travelling effects re-resolved against the world as it is when
they land.

| Kind | Test |
|---|---|
| `Circle` | ground distance ≤ radius + body radius, with a vertical band |
| `Cone` | distance **and** arc, where the body's width buys angular slack that grows as it closes |
| `Line` | swept capsule: point-to-segment distance ≤ half-width + body radius |
| `PointChain` | covered by any sphere in the chain |
| `Projectile` | circle at the impact point |

**The sign convention is load-bearing.** `spell_cone.cone_degrees` stores rear
arcs as **negative** — Tail Sweep is `-120`. The renderer flips the arc's centre
line to the caster's back for a negative cone, because "do not stand behind her"
is the entire lesson of that ability and a front-facing wedge would teach the
opposite.

Verticality is not skipped: Onyxia hovers ~22 yd above the floor in phase 2, and a
body far above or below an effect plane returns `WrongHeight`, not a hit.

The probe reports **why not** as well as why — `outside the arc by 8 deg`,
`3.2 yd outside the radius` — because a report that only lists hits never teaches
you where the safe line is.

`StructuralThreats()` answers the different, better question: *can this ever reach
me here*, ignoring timing. One seeded run is one roll of the dice.

---

## 6. Where behaviour actually comes from

Three tiers on this world DB, and the Lab reads all three:

| Tier | Count | Handling |
|---|---|---|
| `creature_spells` | 2 595 spell lists | **exact-db.** Slots → timed abilities. |
| `creature_ai_events` + `creature_ai_scripts` | 3 470 creatures / 6 201 scripts | **exact-db** triggers; EventAI's inverse phase mask is modelled faithfully. |
| compiled C++ (`script_name`) | 725 creatures | **A declared hole.** `"Scripted behaviour exists in compiled C++; this encounter is not fully modeled."` |

`EncounterTranslator` is written once and covers ~6 000 creatures. That ratio is
the whole argument for authoring new encounters as data.

### 6.1 Traps, all of them verified against the live DB

1. **THE APOSTROPHE TRAP (found by the live test, and it bites elsewhere).**
   MangosSuperUI's CSV export writes negative numbers with a **leading apostrophe**
   — `'-215.238`, `'-120` — the Excel "treat as text" escape. A plain
   `float.TryParse` rejects it and falls back to the default, so **every negative
   coordinate and every rear arc silently reads as ZERO**. Onyxia's breath lanes
   collapse onto the origin; Tail Sweep's rear arc becomes nothing. `CsvRow.Raw`
   strips it before parsing. Before the fix the north-south lane measured 20.7 yd;
   after, 89.6 yd — the real chamber.
2. **THE UNITS TRAP.** `creature_spells` delays are stored in **SECONDS** and
   multiplied by `IN_MILLISECONDS` in `ObjectMgr::LoadCreatureSpells`, while
   `creature_ai_events` params are **already milliseconds**. Read raw, every
   DB-driven creature appears to cast 1000× too fast. Everything published from
   `Net/EncounterWorldData.cs` is normalised to ms.
3. **Server truth ≠ client truth.** Cone arcs (`spell_cone`) and literal landing
   coordinates (`spell_target_position`) exist **only** server-side; no Spell.dbc
   field carries them. `EncounterSpellFacts` consults both sources, DB winning
   where they overlap.
4. **`spell_template` is multi-build** (4222 / 4449 / 5302 / 5875) and
   `spell_target_position` rows are build-ranged. The parser filters to build 5875.
   This is a *different* trap from `creature_template.patch`.
5. **`urand` is a range.** Timings are stored and displayed as bands, never as
   fake-precise single beats.

---

## 7. Onyxia — the spec fuzz, and the gap list

`encounters/onyxia.json` is a full transcription of `boss_onyxia.cpp`: three
phases, the nine-beat takeoff and landing choreography, all five phase-1/3
abilities, phase-2 flight, whelp waves, and **all eight breath lanes resolved from
`spell_target_position`** (84 points, verified against the live DB).

Its more important job was to **enumerate what the format cannot say**. Each gap
below is a named, additive change — to the format, the simulator, or eventually
the core:

| Gap | What the C++ does | Why the format cannot say it |
|---|---|---|
| **GAP-1** | Deep Breath picks its lane from the flight waypoint she is parked on; whelps pick one of two egg pits with `urand(0,1)` | No **variant selection by state**. Modelled with a representative lane + all 8 catalogued as `Manual`. |
| **GAP-2** | Wing Buffet / Knock Away only fire `if CanReachWithMeleeAutoAttack(victim)`, and do not reset their timer otherwise | No **predicate gating** on an ability. |
| **GAP-3** | `DelayCastEvents(2000)` — one cast pushes every *other* timer out | No **cross-ability timer coupling**. The sim bunches casts the real fight spaces out. |
| **GAP-4** | Knock Away −25% threat, Fireball −100%, `DoResetThreat()` on landing | **No threat model at all.** |
| **GAP-5** | The eight-waypoint ring walk: 35% clockwise, 35% counter-clockwise, 30% opposite-with-breath | No **stateful graph traversal**; `MoveTo` is a literal point. |
| **GAP-6** | Whelp waves: two per second from two pits until 16, then `5 + urand(0,2)` after 30 s | `Summon` is a one-shot, not a **state machine**. |
| **GAP-7** | The landing pad branches on `GetPositionX() < -40` | No **conditional step**. |
| **GAP-8** | Evade at `x < -95`; yank an out-of-reach victim to the chamber centre | No **zone predicates**. |
| **GAP-9** | `SetData(DATA_ONYXIA_EVENT, …)` | No **instance/encounter-wide state**. |
| **GAP-10** | `isOnyxiaFlying()` reads `HasAura(SPELL_HOVER)` | No **aura state**; flying is a phase flag, which happens to be enough here. |

That list is the deliverable. Whatever an encounter-authoring format eventually
becomes, these ten are what it has to answer for — and **GAP-1, GAP-2 and GAP-7
(variant selection, predicate gating, conditional steps) are the three that would
unlock the most legacy bosses**.

---

## 8. The raid sim — a commandable, rewindable raid (2026-08-17)

The owner's workflow, verbatim, is the spec: *flag her on and she paths like the
game; a 10-man spawns at my feet up the walkway; Ctrl+F into RTS view; queue
every bot's waypoints; GO sends them; the pull starts the fight; then pause,
rewind, reposition a body and watch where her damage lands instead.* Everything
below exists to make that sentence literally true, and all of it rides the same
two §4 decisions — fixed step + seeded RNG. **Every edit is a full deterministic
re-run** (a couple thousand snapshots, one frame), with the view restored to the
same millisecond: history before the edit is bit-identical *by construction*
(probe-verified: 42 = 42 events before a mid-fight teleport), and the future
honestly reflows.

**The live room.** Loading a document presses Play. The sim pre-simulates and
rewinds to t=0 — and a view parked at time zero is a world where nothing can
ever move; an entire day was lost to a "roaming" boss standing statue-still for
exactly this reason. Pause and the scrub bar still do what they always did.
Documents' Friendly fixture actors are **not** loaded (they stood inside her
ring and pulled her the instant the world went live); the only raid is the one
the owner places.

**The raid.** *Place raid (10)* forms 2 tanks / 2 healers / 3 melee / 3 ranged
**at the player** (creator sandbox included — `DevPlayerPosition` is
offline-aware), facing the boss, tank 1 holding aggro, each body's Z from a
**collision-world raycast** (WMO floors; bare terrain sampling sank half a raid
through the lair floor). Bodies render as real character models (puppets) with
per-look weapons; puppet guids are **stable per body key across rebuilds** so a
selection survives the very edit it just ordered; puppets **ease** toward sim
positions (~100 ms exponential follow + shortest-arc facing) because snapping to
100 ms sim steps rendered a 10 Hz slideshow.

**Staged orders — queue, read, GO.** Pre-pull, every non-teleport click on a
selected body QUEUES. Nothing moves. Set every bot's waypoints, read the dotted
cyan plan on the floor (numbered legs, facing ticks), then **GO** — a green
button that appears with the staged count (Play does the same) — and the whole
raid moves at once: first legs fire at the current instant, chains run
`AfterPrevious`. Once the fight is engaged at the view, clicks become
**immediate** timed moves — mid-fight you are editing the timeline, not queueing
a plan — and `Ctrl+RClick` teleport is always immediate: that is the paused
what-if ("if he stood HERE at this exact moment"), the question the whole
machine exists to answer.

**The command deck (Ctrl+F).** Works offline — in the creator sandbox the free
view is purely a camera decision (seat the rig, exact pose restore on exit; the
live path's server release handshake is not consulted). Raid puppets are picked
and marquee-selected like any RTS unit; a selection carrying puppets routes to
the sim, never to `SuiOrder`, so server bots are untouched.

| Gesture (free view) | Effect |
|---|---|
| Left-click a body / drag a box | Select / multi-select (never take-command — there is no character behind a puppet) |
| **Shift+Click** ground (left or right) | **Stage a waypoint** for the selection; repeat to chain legs; multi-selections fan around the point |
| **GO** (Transport) or Play | Commit the whole staged plan; everyone moves at once |
| Plain RClick ground *(engaged only)* | Immediate walk order at the scrub instant — travel paid |
| **Ctrl+RClick** ground | **Teleport what-if** at the scrub instant — always immediate |
| **Alt+RClick** | Arrival facing for the last order (staged leg first): "…and then face this way" |
| Ctrl+F again | Land exactly where you detached |

**Two design rules, learned the expensive way.** *One:* no logic whose job is to
hold the world in a state the owner never wanted — the roam on/off toggle
briefly existed, a persisted `false` pinned her motionless through three rounds
of "it's still broken", and the toggle is gone; sliders shape behaviour, they do
not disable it. *Two:* claims about GameLoop behaviour are proven by the probe
(§11), not narrated — the headless suite proves sim math, and every genuinely
lost hour of 2026-08-17 lived in the gap between the two.

---

## 9. Using it

**Ctrl+E** opens the window. Works with no server connected — the simulator needs
nothing but the client.

| Section | What it does |
|---|---|
| **Encounter** | Pick an authored document, or **Load selected** to derive one live from the world DB for whatever creature you clicked. Shows source, coverage flags, weakest fidelity, core build hash. |
| **Overlays** | Footprints-at-this-instant · **structural** (everything that could ever land, ignoring timing) · authored route · actors + probe · labels · linger. Includes the fidelity colour key. |
| **Transport** | **GO (staged count)** when a plan is queued · Play / Pause / Step / Back / Reset, a scrub slider over the whole run, playback speed, **seed**, step size, and the labelled raid-dps dial. |
| **Scenario** | **Place raid (10)** at the player · place the boss, add dummies, place any actor by clicking the world · pull-ring slider with the exact-db detection_range line · pre-pull line (declared idle vs sandbox roam + radius) · melee-reach dps gate · the **role playbook** table (phase × job → hold / chase boss / to spot) · per-body rows: job, dps, aggro @ scrub, move list with anchor-aware labels. |
| **Timeline** | A ±12 s window around the scrub head, coloured by fidelity; unmodeled beats show in red. |
| **Position probe** | Place a body, or **Add waypoint** repeatedly to give it a trajectory. Reports hits with times and near-misses with clearance. Warns when "safe" rests on unmodeled mechanics. |
| **Abilities** | Every ability: trigger, timing band, target rule, resolved shape, chance, phases, notes, and `← source` lines pointing at the exact table/column or `file:symbol`. |
| **Coverage & holes** | Source flags and every declared hole, verbatim. |
| **Tape** | Record live `SMSG_SPELL_GO` traffic and diff **predicted vs observed** per spell. |

An armed placement owns the world click completely, so dropping a probe never also
issues an RTS order.

---

## 10. The tape — and why it matters most

`SMSG_SPELL_GO` already carries the caster, the spell, the destination and the
full hit/miss lists; `SMSG_MONSTER_MOVE` already carries splines. Both are parsed
by the client **today**. Recording them cost a few hundred lines and **no server
work whatsoever**.

The diff names, per spell: what the server cast that the model does not know about
(`MISSING FROM MODEL`), and what the model invented. Deliberately coarse —
per-spell counts, not per-instant alignment — because timing drifts legitimately
(threat, movement, resists) while a missing or invented spell is *always* a real
modelling defect.

**A simulator with no tape drifts into fiction.** This is what stops it, and it
works on all 725 C++ bosses the simulator will never model.

Recording is **off by default** and both taps no-op unless the window is open AND
recording is armed — the instrumentation-hazard rule, same as the NPC dev
window's observed-path tap.

---

## 11. Verification

Three layers, each catching what the others cannot.

**1. Headless suite — 106/106, `tools/encounter-lab-check` (re-run 2026-08-17
after the raid-sim work; nothing regressed):**

```bash
dotnet run --project tools/encounter-lab-check
```

Covers: rear/front cone arcs and body-width slack, vertical bands, chains, swept
lines, near-miss clearance; determinism (same seed ⇒ identical event stream),
different seeds diverging, exact rewind, `Reset` reproducibility, phase gating,
casts blocked during choreography; probe trajectories tested at impact; the
translator's unit conversion, C++-hole declaration and EventAI mapping; JSON round
trip and stale-schema rejection; and the whole Onyxia document loading,
simulating, reaching all three phases and declaring its holes.

Add `--live [baseUrl]` for the world-DB integration pass (read-only):

```bash
dotnet run --project tools/encounter-lab-check -- . --live http://192.168.0.2:5000
```

That pass is what found the apostrophe trap. It proves the cone sign, the
seconds→ms conversion, the exact lane coordinates, the missing `17096` row, all
eight lanes resolving, and a real EventAI creature translating and simulating.

**2. In-client scripted probe — 11/11, the REAL client on the REAL code paths:**

```bash
MSUI_ENCLAB_PROBE=1 dotnet run --project MSUIClient/MSUIClient.csproj -- MSUIClient/client-config.json
```

Boots the creator world at the persisted location, loads onyxia, and MEASURES the
owner's whole flow: document loads · **world runs live with zero clicks** (she
moves on her own within seconds) · *Place raid (10)* forms 10 jobbed bodies
within ~9 yd of the controller · staged ~75+ yd outside her ring · **on the
collision floor** (worst Z offset under 1 yd) · never pulled until ordered ·
roams on the timeline · **shift-click stages (queued, nothing moves)** · **GO
commits and the body walks** · Ctrl+F raises the free view offline. Dumps
screenshots to `dumps/gameplay-enclab-probe-*.png` and prints PASS/FAIL per
claim. The Encounter Lab toolbar shows the binary's **build stamp** — read it
before debugging any "not working" report; sim math passing headlessly proves
nothing about GameLoop wiring, and the probe exists because that gap ate a day.

(Probe-writing note: puppets spawn one frame after a sim rebuild — a probe stage
that touches fresh puppets needs a settle stage or it races the spawn.)

**3. Owner eyes-on (2026-08-17): done** — raid at the feet on the walkway, live
roam, staging + GO, all confirmed in-session in the lair.

---

## 12. Known limitations

1. **No threat model.** Aggro is an owner-assigned timed plan (the tank holds
   because you said so); the fallback victim is "closest friendly". Every ability
   depending on real threat order is labelled `heuristic`. This is the single
   largest source of divergence from a real pull — and also the point: "who is
   she on" is an input to the plan being tested, not a thing to guess.
2. **Actor movement is straight-line.** `MmapNavLoader` parses mmaps into
   triangles for the X-ray but there is **no Detour query** in the client, so
   `MOVE_PATHFINDING` cannot be reproduced. Flight paths are fine (they are
   straight anyway); ground pathing around geometry is not — raid walks, her
   chase and the roam all go as the crow flies. Placement Z uses the collision
   world; movement Z is linear between points.
3. **Cones and lines are screen-space polygons**, drawn at the origin's Z rather
   than terrain-projected. Circles, chains and projectile impacts *are* true
   ground decals and do follow WMO floors.
4. **No aura, GCD, resist, immunity or interrupt model.**
5. **The raid-dps dial is not damage.** It exists to make health gates reachable.
6. **Instance scripts are not read.** `instance_onyxia_lair.cpp`-style coordination
   — encounter state, door and trap control, cross-NPC signalling — has no
   representation. Only the creature's own `script_name` is surfaced, so an
   encounter driven mostly by its instance script will look emptier than it is.
7. **EventAI `condition_id` is not evaluated** — gated events drop to `heuristic`.
8. **`imgui.ini` persists the window rect** (`###encounter-lab`); a bad saved rect
   survives restarts. Reset = delete that ini block.
9. **The staged plan and the playbook are session-state, not document-state.**
   Committed move orders live on the scenario and would round-trip through the
   DTO layer, but nothing currently saves a raid plan back to a `.json`; a
   restart forgets it. First-class "save this plan" is future work.
10. **The sandbox roam is fiction and labelled as such.** Onyxia's DB truth
    (guid 47572) is stationary; the exact-db line says so while she roams anyway.
    A creature with a real `Wander`/`Waypoints` declaration plays the truth.

---

## 13. Found in passing — NOT fixed (in-flight file)

**The NPC dev window's CSV fallback has the apostrophe bug.**
`Net/DevDataClient.cs` parses coordinates with a plain `float.TryParse`, and the
`creature` export escapes negatives exactly the same way:

```
"47572","10184",…,"249","'-4.8689","'-217.171","'-86.7104","3.14159",…
```

So on the CSV path **every negative spawn coordinate reads as 0** — which is most
of Azeroth. It has not shown up because the deployed `/NpcDev/Snapshot` JSON
endpoint is the primary transport and JSON numbers are unescaped; the bug only
bites when the fallback ladder (`NPC_DEV_WINDOW.md` §7c) actually falls back.

Left alone deliberately — `DevDataClient.cs` belongs to the in-flight NPC dev
work. The one-line fix is the same as `EncounterDataClient.CsvRow.Raw`: strip a
leading `'` before parsing.

---

## 14. Extension recipes

**Author a new encounter** — drop a `.json` in `encounters/`, hit *Reload
documents*. Start from `onyxia.json`; the fields are in §3. Set `fidelity`
honestly and use `Unmodeled` steps for beats you cannot express — they surface in
the timeline and the coverage report rather than quietly disappearing.

**Add a geometry kind** — extend `FootprintKind`, add a `Resolve` case and a
`Test` case in `EncounterGeometryLaw`, then a draw case in
`GameLoop.EncounterLab.Overlays.cs`. If it is a disc, add it to
`AddFootprintDiscs` and it inherits real ground projection for free.

**Add a trigger or step kind** — extend the enum, handle it in
`EncounterSim.TriggerFires` / `AdvanceSequence`, and map the matching
`EVENT_T_*` / `SCRIPT_COMMAND_*` in `EncounterTranslator`. Keep the vocabulary a
subset of the core's, so authored encounters stay translatable to DB rows.

**Add a behaviour table** — add it to `EncounterDataClient.Tables`, write a
header-name-based parser (**strip the apostrophe**), extend `EncounterWorldData`.

**Close a gap from §7** — the highest-value three are GAP-1 (variant selection),
GAP-2 (predicate gating) and GAP-7 (conditional steps). Each is additive to the
format and to `EncounterSim`; none requires a core change to *simulate*, only to
eventually *execute* server-side.

**Give a patrolling creature its exact route** — dump its `creature` row and
`creature_movement` rows through the web API
(`http://192.168.0.2:5000/Database/Export/mangos/<table>`; direct MySQL is
refused, and negative numbers wear the apostrophe escape), then author
`idleMovement: {kind: Waypoints, points: [{position, waitMs}, …]}` on the actor
with a source ref. The sim loops it at true walk speed with exact pauses —
built, tested, waiting for its first real patrol.

**Add a playbook directive kind** — extend `RaidDirectiveKind`, handle it in
`EncounterSim.ApplyPlaybook`, add the combo label in `DrawEncounterPlaybook`.
Keep directives dumb verbs; anything with branching belongs in the format's gap
list, not in an escape hatch.

**Extend the probe** — every new Lab claim gets a check in
`GameLoop.EncounterLab.Probe.cs` stating what the owner's eyes would check, in
yards and booleans. If a stage touches puppets after a rebuild, give it a settle
stage (§11).

---

## 15. Files

**New — this feature owns them**

| File | Responsibility |
|---|---|
| `MSUIClient/World/Encounters/EncounterDefinition.cs` | Schema, fidelity registry, coverage flags, `Holes()`, `WorstFidelity()` |
| `MSUIClient/World/Encounters/EncounterGeometry.cs` | `BodyCapsule`, `Footprint`, `IEncounterSpellFacts`, resolve + hit tests |
| `MSUIClient/World/Encounters/EncounterSim.cs` | `SeededRng`, actors, events, snapshot ring, the step |
| `MSUIClient/World/Encounters/EncounterProbe.cs` | `ProbeTrajectory`, `ProbeReport`, structural scan |
| `MSUIClient/World/Encounters/EncounterTranslator.cs` | world-DB rows → definition |
| `MSUIClient/World/Encounters/EncounterLibrary.cs` | JSON DTOs ⇄ model, load/save |
| `MSUIClient/World/Encounters/EncounterSpellFacts.cs` | Spell.dbc + world DB bridge |
| `MSUIClient/Net/EncounterWorldData.cs` | Row model + immutable snapshot |
| `MSUIClient/Net/EncounterDataClient.cs` | 5 tables, CSV + 12 h disk cache |
| `MSUIClient/GameLoop/Dev/GameLoop.EncounterLab.cs` | Window, transport (GO), sections, click intercept, raid preset, staged orders, playbook UI, collision-ground placement |
| `MSUIClient/GameLoop/Dev/GameLoop.EncounterLab.Overlays.cs` | 3-D decals + screen pass; committed (green) and staged (dotted cyan) plans, anchor labels, facing ticks |
| `MSUIClient/GameLoop/Dev/GameLoop.EncounterLab.Puppets.cs` | Rendered raid/boss models: synthetic entities, per-look weapons, stable guid reserve, eased motion + facing |
| `MSUIClient/GameLoop/Dev/GameLoop.EncounterLab.Rts.cs` | Ctrl+F bridge: puppet select/marquee, order routing (stage / immediate / teleport / facing), never touches `SuiOrder` |
| `MSUIClient/GameLoop/Dev/GameLoop.EncounterLab.Probe.cs` | `MSUI_ENCLAB_PROBE` — 11 in-client checks + screenshots (§11) |
| `MSUIClient/GameLoop/Dev/GameLoop.EncounterLab.Tape.cs` | Recorder + predicted-vs-observed diff |
| `encounters/onyxia.json` | The authored encounter + spec fuzz — boss at the exact DB spawn (guid 47572), real speeds, `detectionRangeYards`, declared `Stationary` idle with full provenance note |
| `tools/encounter-lab-check/` | 106 headless assertions, `--live` integration mode |

**Touched — thin hooks only**

| File · location | Hook |
|---|---|
| `Program.cs` BuildGui, beside `DrawDevWindow()` | `DrawEncounterLab(); DrawEncounterLabOverlay();` |
| `Program.cs` 3-D pass, after `RenderDevOverlays3D()` | `RenderEncounterLab3D();` |
| `Program.cs` `Update`, before `ObserveUiPanelOwnership()` | `UpdateEncounterLab(dt);` |
| `GameLoop/Scene/GameLoop.Control.cs` | `UpdateEncounterLabInput(typing);` · free-view hooks: puppet select, shift-click stage, order routing, puppet marquee · **creator-sandbox Ctrl+F** (offline free view, exact pose restore) |
| `GameLoop/Combat/GameLoop.Targeting.cs` click drain | `if (HandleEncounterLabClick(click)) continue;` |
| `GameLoop/Combat/GameLoop.Casting.cs` spell-go handler | `RecordEncounterTapeCast(packet);` |
| `GameLoop/Dev/GameLoop.DevWindow.Overlays.cs` | `DevPlayerPosition()` is offline-aware (controller / free-view return pose) — every "at the player" affordance depends on it |
| `Engine/ClientWindow.cs` | `WorldMouseClick` carries Ctrl/Alt; `BuildStamp` (assembly write time, shown in the Lab toolbar) |
| `Engine/GameSettings.cs` | `EncounterLabSettings` + `GameSettings.EncounterLab` (roam radius, melee-reach gate, pull range — knobs shape, never disable) |
| `Program.cs` Update | `UpdateEncounterLabProbe();` |
| `.gitignore` | `/encounter-tapes/` (recordings). `/encounters/` deliberately tracked. |

**Not touched:** `Net/DevDataClient.cs`, `Net/DevWorldData.cs`,
`GameLoop/Dev/GameLoop.DevWindow*.cs` — the in-flight NPC dev work.
