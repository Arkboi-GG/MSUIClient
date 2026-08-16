# Encounter Lab — architecture & handbook (built 2026-08-16)

> **STATUS: BUILT & VERIFIED HEADLESSLY (106/106 checks, including live world-DB
> integration). NOT YET EYES-ON IN THE CLIENT.** Client builds clean; the
> simulator, geometry, probe, translator and file format are proven by
> `tools/encounter-lab-check`. Visual confirmation in-world is an owner step
> (§10). No server changes, no deploy, no core work — the Lab reads the world DB
> through MangosSuperUI's **existing** CSV export and runs entirely client-side.

**What it is:** press **Ctrl+E** (live mode or creator mode) for a window that
loads an NPC's combat behaviour as a **declarative encounter definition**, runs it
as a **deterministic fixed-step simulation**, and lets you **play, pause,
single-step, scrub, rewind and re-seed** it while drawing every ability's
footprint on the real terrain. Drop a **body capsule** anywhere — with a
trajectory, if you like — and it answers *what can hit this body, when, and why*.
Every fact carries a **fidelity label**, so a shape you cannot trust never looks
like one you can.

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
│ GameLoop/Dev/GameLoop.EncounterLab.cs          window, transport, sections │
│ GameLoop/Dev/GameLoop.EncounterLab.Overlays.cs 3-D decals + screen pass    │
│ GameLoop/Dev/GameLoop.EncounterLab.Tape.cs     live recorder + diff        │
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
├── actors[]     boss / add / friendly, with bounding radius + combat reach
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
second) purely so health-gated phases are reachable, or pinned directly. It is
never presented as a damage model.

**What the simulator does not have, and says so:** no threat table (so
`CurrentVictim` is "closest friendly", and any ability needing threat order drops
to `heuristic`), no aura system, no GCD, no resists, no line-of-sight or
pathfinding for actor movement.

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

## 8. Using it

**Ctrl+E** opens the window. Works with no server connected — the simulator needs
nothing but the client.

| Section | What it does |
|---|---|
| **Encounter** | Pick an authored document, or **Load selected** to derive one live from the world DB for whatever creature you clicked. Shows source, coverage flags, weakest fidelity, core build hash. |
| **Overlays** | Footprints-at-this-instant · **structural** (everything that could ever land, ignoring timing) · authored route · actors + probe · labels · linger. Includes the fidelity colour key. |
| **Transport** | Play / Pause / Step / Back / Reset, a scrub slider over the whole run, playback speed, **seed**, step size, and the labelled raid-dps dial. |
| **Scenario** | Place the boss, add dummies, place any actor by clicking the world. |
| **Timeline** | A ±12 s window around the scrub head, coloured by fidelity; unmodeled beats show in red. |
| **Position probe** | Place a body, or **Add waypoint** repeatedly to give it a trajectory. Reports hits with times and near-misses with clearance. Warns when "safe" rests on unmodeled mechanics. |
| **Abilities** | Every ability: trigger, timing band, target rule, resolved shape, chance, phases, notes, and `← source` lines pointing at the exact table/column or `file:symbol`. |
| **Coverage & holes** | Source flags and every declared hole, verbatim. |
| **Tape** | Record live `SMSG_SPELL_GO` traffic and diff **predicted vs observed** per spell. |

An armed placement owns the world click completely, so dropping a probe never also
issues an RTS order.

---

## 9. The tape — and why it matters most

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

## 10. Verification

**Done — 106/106 assertions, `tools/encounter-lab-check`:**

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

**Not done — owner, eyes-on:** open Ctrl+E in-world, confirm the window renders in
both live and creator mode; load Onyxia in her lair and check the lane discs
project onto the WMO floor; turn on **structural** and confirm Tail Sweep's wedge
points at her **back**; scrub through a takeoff; drop a probe in a lane and
confirm it reports hits.

---

## 11. Known limitations

1. **No threat model.** `CurrentVictim` is "closest friendly". Every ability
   depending on threat order is labelled `heuristic`. This is the single largest
   source of divergence from a real pull.
2. **Actor movement is straight-line.** `MmapNavLoader` parses mmaps into
   triangles for the X-ray but there is **no Detour query** in the client, so
   `MOVE_PATHFINDING` cannot be reproduced. Flight paths are fine (they are
   straight anyway); ground pathing around geometry is not.
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

---

## 12. Found in passing — NOT fixed (in-flight file)

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

## 13. Extension recipes

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

---

## 14. Files

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
| `MSUIClient/GameLoop/Dev/GameLoop.EncounterLab.cs` | Window, transport, sections, click intercept |
| `MSUIClient/GameLoop/Dev/GameLoop.EncounterLab.Overlays.cs` | 3-D decals + screen pass |
| `MSUIClient/GameLoop/Dev/GameLoop.EncounterLab.Tape.cs` | Recorder + predicted-vs-observed diff |
| `encounters/onyxia.json` | The authored encounter + spec fuzz |
| `tools/encounter-lab-check/` | 106 headless assertions, `--live` integration mode |

**Touched — thin hooks only**

| File · location | Hook |
|---|---|
| `Program.cs` BuildGui, beside `DrawDevWindow()` | `DrawEncounterLab(); DrawEncounterLabOverlay();` |
| `Program.cs` 3-D pass, after `RenderDevOverlays3D()` | `RenderEncounterLab3D();` |
| `Program.cs` `Update`, before `ObserveUiPanelOwnership()` | `UpdateEncounterLab(dt);` |
| `GameLoop/Scene/GameLoop.Control.cs` | `UpdateEncounterLabInput(typing);` |
| `GameLoop/Combat/GameLoop.Targeting.cs` click drain | `if (HandleEncounterLabClick(click)) continue;` |
| `GameLoop/Combat/GameLoop.Casting.cs` spell-go handler | `RecordEncounterTapeCast(packet);` |
| `Engine/GameSettings.cs` | `EncounterLabSettings` + `GameSettings.EncounterLab` |
| `.gitignore` | `/encounter-tapes/` (recordings). `/encounters/` deliberately tracked. |

**Not touched:** `Net/DevDataClient.cs`, `Net/DevWorldData.cs`,
`GameLoop/Dev/GameLoop.DevWindow*.cs` — the in-flight NPC dev work.
