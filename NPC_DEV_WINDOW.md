# NPC Dev Window — architecture & handbook (updated 2026-08-11)

> **STATUS: P1–P3 SHIPPED; P4 (P1+P2) DEPLOYED & LIVE-VERIFIED; P5 BUILT.** P1/P2 live-verified
> (`dumps/gameplay-devwindow-*.png`). **P4** — direct commit through SuperUI (audited) +
> owner-clicked live reload (`.reload creature_template` for aggro; `.npc reloadspawn <guid>`,
> the additive vmangos core command, for spawn/path) — is deployed and confirmed live: a spawn
> move committed, audited (`vmangos_admin.audit_log`), and reloaded on the spot (§11). **P5** —
> OG baseline (`og_creature*`), a "changed from original" diff, per-spawn/entry
> reset-to-original, and wash-on-commit — is **BUILT & compile-verified in both repos**; owner
> deploys SuperUI and captures the baseline via `Baseline/Initialize` (§15). `/NpcDev/Snapshot`
> is deployed. Full original plan: `C:\Users\nico\.claude\plans\zany-scribbling-pie.md`.

**What it is:** press **Ctrl+N** (live mode or creator mode) for a proper chrome window that
visualizes NPC **spawns**, **patrol paths** and **aggro radii** while you fly the free view
(Ctrl+F), shows exactly **which DB table/row/column** every datum lives in, and (P3+) turns
in-world edits into a **change-set** that MangosSuperUI verifies, applies and audits when you
**Commit** — then you **Reload** to make it live and test. The client never writes the DB
itself (SuperUI does, audited); the live reload is a GM command **you** click, never issued
autonomously.

---

## 1. Owner decisions (binding)

| Decision | Consequence |
|---|---|
| DB reads + commit over **HTTP from MangosSuperUI** (`http://192.168.0.2:5000`) | No new SUI opcodes. The ONE vmangos C++ change is P4 Phase 2's **additive** `.npc reloadspawn` command (owner builds + deploys), for instant spawn/waypoint reload only — reads, commit and aggro reload need zero core changes. |
| Client **commits through SuperUI**; reload is **owner-clicked** | Edits become change packets → **Commit** POSTs them to `/NpcDev/Apply` (verify → apply → audit); the local `dev-changes/*.json` stays as a record. The live **Reload** is a GM command YOU click (`.reload creature_template` / `.npc reloadspawn <guid>`), never issued autonomously. |
| Auditing lives in **MangosSuperUI** | `NpcDevApplyService` writes the world DB and records each packet via `AuditService.LogAsync` (before/after JSON in `vmangos_admin.audit_log`), one `AuditBatch` per commit → one node in the Change Graph (domain "npc"). Never copy `WorldMapController.PlaceObject` — it writes unaudited. |
| Start surface: live mode + free view | Creator mode opens the window too; live-only sections state "requires live server". |

---

## 2. The three layers — WHERE LOGIC IS ALLOWED TO LIVE

The feature is deliberately layered so later changes don't detangle spaghetti. **Respect
these boundaries when modifying:**

```
┌─ DATA LAYER ──────────────────────────────────────────────────────────────┐
│ Net\DevDataClient.cs      HTTP + CSV/JSON parsing + disk cache. NO ImGui, │
│ Net\DevWorldData.cs       NO GL, NO GameLoop state. Publishes IMMUTABLE   │
│ (NpcTemplateInfo etc.)    snapshots via volatile fields; background Task. │
├─ CONTROL / UI LAYER ──────────────────────────────────────────────────────┤
│ Program.DevWindow.cs      Window shell (chrome), sections, toggles,       │
│                           fetch TRIGGERS (when to fetch — not how),       │
│                           provenance panel. NO rendering, NO parsing.     │
├─ RENDER LAYER ────────────────────────────────────────────────────────────┤
│ Program.DevWindow.Overlays.cs                                             │
│                           3-D pass (discs, beams) + screen pass (labels,  │
│                           polylines) + the two wire taps. Reads published │
│                           data snapshots + EntityStore. NO I/O.           │
├─ SHARED RENDER PRIMITIVES (host-agnostic, reusable) ──────────────────────┤
│ World\Units\SpellEffectMeshRenderer.cs   GroundDisc/RenderGroundDiscs     │
│ World\Collision\CollisionDebugRenderer.cs RenderHighlight mode 3 (x-ray)  │
└───────────────────────────────────────────────────────────────────────────┘
Server side: MangosSuperUI\Controllers\NpcDevController.cs — read-only JSON
snapshot endpoint, self-contained (fixed SQL, no schema-map coupling).
```

Rules of thumb:
- New **data source** → `DevDataClient` + a record in `DevWorldData`/its own model file.
- New **overlay** → `Program.DevWindow.Overlays.cs` (3-D goes in `RenderDevOverlays3D`,
  screen-space in `DrawDevOverlayLabels`), toggle in `GameSettings.DevWindowSettings`.
- New **window content** → a `DrawDev*Section` method in `Program.DevWindow.cs`.
- Anything reusable by other features (a decal shape, an x-ray pass) → the shared
  renderer classes, NOT the dev-window partials.

---

## 3. Client file map (exact hook points)

All `Program.*` files are partials of `public sealed partial class GameLoop`.

### New files (this feature owns them)

| File | Responsibility |
|---|---|
| `MSUIClient\Program.DevWindow.cs` | `_devWindowOpen`, Ctrl+N edge toggle (`UpdateDevWindowInput`), window shell `DrawDevWindow()` (creator chrome), toolbar + sections (Overlays / Aggro reference / Selected NPC / Data source), fetch trigger `UpdateDevWorldFetch()` |
| `MSUIClient\Program.DevWindow.Overlays.cs` | `RenderDevOverlays3D()` (aggro discs, DB spawn discs, wander rings, x-ray beams), `DrawDevOverlayLabels()` (labels, observed + DB polylines, counters), wire taps `RecordDevObservedPath` / `ApplyAiReaction`, aggro math `DevAggroRadius` |
| `MSUIClient\Net\DevDataClient.cs` | ALL HTTP + parsing + caching. Template fetch (`BeginFetchTemplates` → `Templates`), world fetch (`BeginFetchWorld` → `World`): JSON snapshot first, CSV export fallback |
| `MSUIClient\Net\DevWorldData.cs` | Immutable DB model: `DevSpawnRow`, `DevWaypointRow`, `DevWorldData` (+ `ResolvePath` mirroring vmangos), `DevPathOrigin` |

### Touched files (thin hooks only — keep them thin)

| File · location | Hook |
|---|---|
| `Program.cs` BuildGui, ABOVE the creator-mode return (`if (_creatorWorldRequested…) return;`) | `DrawDevWindow(); DrawDevOverlayLabels();` gated `!GlueFrontDoorActive && !CreatorLaunchActive`. **This placement is what makes the window mode-neutral** — the F1 dev stack below the return never shows in creator mode. |
| `Program.cs` 3-D pass, right after `RenderRtsGroundFx()` | `RenderDevOverlays3D();` — after units populate depth (bodies occlude discs), before particles/water. |
| `Program.Control.cs` end of `UpdateControlInput` | `UpdateDevWindowInput(typing);` — beside the Ctrl+F edge toggle; same `typing` guard. |
| `Program.Net.cs` `SMSG_MONSTER_MOVE` case | `RecordDevObservedPath(mm);` — no-op while the window is closed (instrumentation-hazard rule). |
| `Program.Net.cs` new `SMSG_AI_REACTION` case | `ApplyAiReaction(body);` — the opcode (0x13C) was declared but unhandled before this feature. |
| `Engine\GameSettings.cs` | `DevWindowSettings` class + `GameSettings.DevWindow` property. |
| `Program.Targeting.cs` left-click branch + `Program.Control.cs` free-view left-click | `HandleDevFocusClick(picked)` (Program.DevWindow.cs) — overlay focus-set maintenance, ahead of normal selection: Ctrl+LeftClick toggles a creature in/out and consumes the click; a plain click retargets/clears the set and falls through. |
| `Engine\ClientWindow.cs` + `Program.Control.cs` free-view update | In FreeSelectMode the wheel accumulates into `TakeFreeFlightScroll()` instead of `Camera.Zoom` (whose MaxDistance=40 read as a height ceiling); the game loop flies the rig along `Camera.Forward` with altitude-scaled steps, through `FlyMove` (collision-swept). Wheel down = climb, unlimited. Flight itself stays flat W/S + Space/Ctrl vertical in BOTH the free view and plain F fly (a look-direction flight experiment was rejected 2026-08-11). |
| `Program.cs` F toggle + Up axis | Read through `InputKeyDown` (the protocol seam) instead of raw `_window.IsDown`, so scripted runs can exercise F-fly; real-keyboard behaviour unchanged. F-fly climb live-verified (dumps/gameplay-flytest-*). |
| `Program.Targeting.cs` `PickUnit` | The ControlledGuid pick-skip lifts while `_freeView` is up: solo in the free view YOUR OWN toon is the controlled unit, and the unconditional skip made it the one unit you could not click for the command halo. Normal third-person keeps the skip (you would click your own back constantly). |
| `World\Units\SpellEffectMeshRenderer.cs` | `GroundDisc` record, `DevDiscTexture(innerFraction)` (per-ratio annulus texture cache, 1/32 steps), `RenderGroundDiscs`, `FlatDiscVertices` (WMO fallback), dispose hook. |
| `Shaders\collision.frag` + `CollisionDebugRenderer` | `uHighlight == 3` → solid red (x-ray beams). `RenderHighlight` needs no `Build()` — safe with an empty collision debug mesh. |
| `.gitignore` | `/dev-cache/` (downloaded CSVs). `dev-changes/` will need the OPPOSITE decision in P3 (they are work product — likely committed or at least kept). |

### Reused machinery (do not duplicate — call these)

| Need | Call |
|---|---|
| Chrome window shell | `PushCreatorStyle`/`PopCreatorStyle`, `CreatorChromeFlags`, `DrawCreatorPanelChrome(title, tuneId)`, `BeginCreatorContent`/`EndCreatorContent`, `ClampCreatorWindowOnScreen` (Program.Creator.Ui.cs). The gear popup host is `DrawCreatorPanelTunePopup()` — the dev window calls it itself in live mode (creator HUD draws it otherwise). |
| World→screen | `Camera.TryWorldToScreen` (Engine\Camera.cs); distance-scaled font via `ProjectedWorldPitch` (Program.Nameplates.cs). |
| Ground/unit picking (P3 editing) | `TryPickGround(pixel, out Vector3)` / `PickUnit(pixel)` (Program.Targeting.cs:~273/309). |
| Reaction/hostility | `ReactionTargetTowardPlayer(unit)` (Program.Targeting.cs), `ReactionColorU32` (Program.UnitFrames.cs). |
| Dashed screen lines | `DrawDashedLine` (Program.Control.cs). |
| Ghost preview entities (P3) | `EntityStore.AddSynthetic` / `RemoveSynthetic`; creator synthetic guids count up from `0xF000_0000_0000_0100`. |

---

## 4. Data flow

```
                    ┌────────────── MangosSuperUI :5000 ───────────────┐
                    │  /Database/Export/mangos/<table>   (CSV, exists) │
                    │  /NpcDev/Snapshot?...   (JSON, needs deploy §12) │
                    └──────────────────┬───────────────────────────────┘
                                       │ background Task (DevDataClient)
                                       │ disk cache: dev-cache/*.csv (12 h)
                                       ▼
              volatile publish: Templates (NpcTemplateInfo by entry)
                                World     (DevWorldData: spawns + paths)
                                       │ game thread reads, never blocks
        ┌──────────────────────────────┼─────────────────────────────────┐
        ▼                              ▼                                 ▼
  Program.DevWindow.cs        RenderDevOverlays3D()             DrawDevOverlayLabels()
  (window, provenance)        discs/beams in 3-D pass           labels/polylines (bg drawlist)
        ▲                              ▲
        │            EntityStore (_entities): live guid ↔ DB row join
        │            join key: guid low 24 bits == mangos.creature.guid
        └── SMSG_MONSTER_MOVE → RecordDevObservedPath (observed truth)
            SMSG_AI_REACTION  → ApplyAiReaction (server's actual aggro moment)
```

**Threading contract (the one hard rule):** `DevDataClient` fetch+parse runs on background
Tasks and publishes ONLY immutable objects through volatile fields. The game thread reads
`DevData.Templates` / `DevData.World` and does all joining against `EntityStore` itself.
Nothing background-side may touch `_entities`, `Settings`, ImGui, or GL. Ever.

---

## 5. Aggro math (client-side reproduction of the server)

`DevAggroRadius(npcLevel, targetLevel, tpl)` in Program.DevWindow.Overlays.cs =
vmangos `Creature::GetAttackDistance` (`src/game/Objects/Creature.cpp:2122`), with
aggroRate = 1 and aura mods = 0:

```
det = creature_template.detection_range        // per-ENTRY; vmangos default 18
if flags_extra & 0x2 (NO_AGGRO)          → 0
if static_flags1 & 0x2000000 (IGNORE_COMBAT) → 0
if det < 1                               → 0
radius = det − clamp(targetLevel − npcLevel, −25, +∞)     // 1 yd per level
radius = max(radius, min(det, 5))                          // the floor
```

- **Reference-level selector** (window → Aggro reference): `Level60` (raid), `MyLevel`
  (dungeon walking), `NpcLevel`. Concentric bands = radii at ref, ref−1, … ref−(n−1);
  lower target level ⇒ BIGGER ring. Band colors red→orange→yellow→green→cyan→blue
  (`DevBandTint`).
- **Who-aggros-me** always uses MY level + my toon's real position (controller when
  driving, streamed entity in free view — `DevPlayerPosition`), hostiles only, 3-D
  distance. It is a **distance-only estimate**: no line-of-sight, no aura mods, no
  `RATE_CREATURE_AGGRO` config. `SMSG_AI_REACTION` (reaction 2 = hostile) is the
  empirical check — disc rim flashes white and the console logs
  `[dev-aggro] … went hostile at X yd (predicted Y yd)`.
- `detection_range` is **per-entry**: editing it (P3) affects every spawn of the entry.
  Surface that in any edit UI ("affects N spawns") — decided in plan, non-negotiable.
- Related fields on the same template row: `call_for_help_range` (default 5),
  `leash_range` (0 = config threat radius); server leash ≈ `max(attackDist × 1.5,
  THREAT_RADIUS)`. Not yet drawn — see extension recipe §9.

---

## 6. DB schema notes (mangos world DB)

- **`creature`** (spawn table; PK `guid`): `id..id5` (random entry pool), `map`,
  `position_x/y/z`, `orientation`, `spawntimesecsmin/max`, `wander_distance`,
  `movement_type` (0 idle / 1 random-wander / 2 waypoint), `spawn_flags`,
  `patch_min/max`.
- **`creature_movement`** (per-GUID path; key `id` == creature.guid, `point`):
  `position_x/y/z`, `orientation`, `waittime` (ms), `wander_distance`, `script_id`,
  `path_id`.
- **`creature_movement_template`** (per-ENTRY path; key `entry`, `point`; same shape).
  vmangos resolution (`WaypointManager::GetDefaultPath`): **guid table first, else entry
  template** (internal key `(entry<<8)+pathId`, so pathId must stay 0..254).
  `DevWorldData.ResolvePath` mirrors this exactly; keep them in sync.
- **CONSTRAINT for P3/P4**: `point` values must be **gapless and 1-based per path**
  (`WaypointManager::Cleanup` silently renumbers otherwise). That is WHY waypoint edits
  ship as full-path replacement, and why the P4 applier renumbers inside a transaction.
- **Guid embedding** (`Net\GuidInfo.cs`): a live creature guid = `0xF130` high | entry
  (bits 24–47) | **counter (bits 0–23) == creature.guid PK**. This is the exact live↔DB
  join and needs no server help. Synthetic (creator) entities fail the `HighUnit` check
  and are excluded from the join.
- **`creature_template`** is multi-row per entry (`patch` column): highest patch wins —
  both the client parser and the snapshot endpoint apply this rule.

---

## 7. HTTP contracts

### 7a. CSV export (exists on today's deploy — the working fallback)

`GET /Database/Export/mangos/<table>[?filterCol=<col>&filterVal=<val>]` → RFC 4180 CSV,
UTF-8 BOM, header row. Used with: `creature_template` (full, ~2 MB),
`creature?filterCol=map&filterVal=<map>` (~3 MB for map 0), `creature_movement` (full,
~5 MB), `creature_movement_template` (full, ~0.6 MB). Client parses **by header name**
(schema drift degrades to defaults, never mis-parses). Column traps: the static flags
column is **`static_flags1`** (not `static_flags`); SESSILE 0x100 and IGNORE_COMBAT
0x2000000 live in that first word.

### 7b. JSON snapshot (built in MangosSuperUI repo; NOT deployed yet — see §12)

`GET /NpcDev/Snapshot?map=<m>[&nearX=&nearY=&range=][&guids=csv][&entries=csv]`
(`Controllers\NpcDevController.cs`). Selection = guid list OR square around
(nearX,nearY); movement rows for every selected spawn guid; template paths + template
subsets for the derived entry pool. Caps: 500 guids, 500 entries, range ≤ 600,
4000 spawn rows. Response (camelCase):

```json
{ "fetchedUtc": "...", "map": 0,
  "creatures":        [ { "guid":79644, "id":721, "id2":0, …, "positionX":-9455.02,
                          "spawnTimeSecsMin":270, "wanderDistance":10, "movementType":1 } ],
  "movement":          [ { "id":51, "point":1, "positionX":…, "waittime":0, "pathId":0 } ],
  "movementTemplates": [ { "entry":721, "point":1, … } ],
  "templates":         [ { "entry":721, "detectionRange":18, "staticFlags":16, … } ] }
```

### 7c. Client fallback ladder (`DevDataClient.FetchWorld`)

1. Try `/NpcDev/Snapshot` (area-limited, one round trip). Any failure → step 2 with a
   console note. **This is how the feature survives an undeployed endpoint.**
2. CSV exports (whole map), each cached at `dev-cache/*.csv` for 12 h; on HTTP failure
   any cache age is used (the window shows source + age: `snapshot` / `csv` /
   `csv-cache`).

Fetch **triggers** live in `UpdateDevWorldFetch()` (Program.DevWindow.cs): fetch when no
data, map changed, or an area-limited snapshot fell >250 yd behind the camera; debounced
to one attempt per 5 s. "Refresh DB" toolbar button forces both templates + world.

---

## 8. Rendering — what draws where (frame order matters)

| Pass | Where in the frame | What |
|---|---|---|
| `RenderDevOverlays3D` | 3-D pass, after `RenderRtsGroundFx` (units already wrote depth), before particles/water | Aggro annuli + AI_REACTION flash rims + DB spawn discs + wander rings (all via `RenderGroundDiscs`, depth-test ON / write OFF, `UnitAwareDepthBias −64` so bodies occlude); then x-ray beams (depth-test OFF, collision shader mode 3) |
| `DrawDevOverlayLabels` | GUI phase, right after `DrawDevWindow` in BuildGui, background draw list | Spawn labels, observed dashed polylines, DB solid polylines + numbered nodes + waittime badges, spawn connectors, DB-only dim labels, toolbar counters |

Rendering specifics worth knowing before touching:
- **Annulus textures**: `DevDiscTexture(innerFraction)` caches one 256² alpha texture per
  1/32 inner-ratio step (≤32 total). Bands are TRUE annuli (inner cut in the texture) —
  concentric bands never overdraw-blend each other. The RTS ring texture is unusable here
  (its band is a fixed 78–95 % of radius).
- **WMO floors** (dungeon interiors): `ProjectDecal` gathers TERRAIN triangles only; when
  none exist under the centre, `FlatDiscVertices` draws a flat horizontal disc at feet-Z.
  Known v1 limitation — a real WMO-floor gather is future work.
- **DB path draw-once**: template routes are shared by every spawn of an entry with
  identical absolute coordinates — `drawnPaths` HashSet dedupes per frame. Path color IS
  provenance: **cyan = per-guid `creature_movement`, gold = shared template**; a dashed
  closing segment hints the loop.
- **Selected route nodes own their clicks**: numbered DB nodes for the inspected
  creature have hitboxes even before the edit is armed. Left-clicking one enters the
  real waypoint editor with that node selected and never falls through to ordinary
  ground targeting (which would clear the inspected creature). Only node hover reserves
  ordinary-view left input; the rest of the world retains normal camera look.
- **X-ray**: the ONLY depth-test-off primitive is `CollisionDebugRenderer.RenderHighlight`
  (modes: 1 yellow physics, 2 cyan player marker, **3 red aggro**). It works without a
  built collision debug mesh.
- Counters `_devStreamedInRange`/`_devDbOnlyInRange` are written by the label pass and
  read by the toolbar the NEXT frame (window draws first) — off-by-one-frame by design.
- **Overlay scope** (`Settings.DevWindow.FocusSelectedOnly`, "All NPCs" / "Selected only"
  radios at the top of the Overlays section): Selected scope draws ONLY the focus set
  `_devFocusGuids` (Ctrl+LeftClick while the window is open; plain click retargets the
  set, empty click clears it). DB rows filter through the low-24 join key
  (`DevFocusSpawnLows`), so DB-only spawns are hidden entirely in Selected scope; the
  in-range counters count the filtered set. Edit-mode overlays and queued-packet
  previews draw regardless of scope.

---

## 9. Extension recipes

**Add an overlay toggle** — property in `GameSettings.DevWindowSettings` (with default) →
checkbox in `DrawDevOverlaySection` → gate your drawing on it. Settings save via
`SettingsFile?.Save()` on change (`IsItemDeactivatedAfterEdit` for sliders).

**Draw a new template-driven radius** (e.g. `call_for_help_range` or leash rings): data is
already fetched (`NpcTemplateInfo`); add discs in `RenderDevOverlays3D`'s creature loop:
`discs.Add(new(unit.Position, inner, outer, tint, opacity))`.

**Fetch a new table/column**: add the column lookup in the relevant `DevDataClient`
parser (header-name based, with a sane default) + extend the record; if snapshot-served,
add it to `NpcDevController`'s SELECT with a camelCase alias and to the matching
`Snap*` DTO. Records are immutable — never mutate a published snapshot.

**Add a window section**: a `DrawDevXxxSection()` in Program.DevWindow.cs, called from
`DrawDevWindow` between `BeginCreatorContent`/`EndCreatorContent`, using
`ImGui.CollapsingHeader`. Keep it presentation-only; data prep belongs in the data layer.

**When the snapshot endpoint deploys** (§12): nothing to do client-side — the fallback
ladder tries it on every fetch and switches automatically (source shows "snapshot").

---

## 10. P3 — editing + change-set writer (BUILT 2026-08-11)

Files: `Program.DevWindow.Edit.cs` (edit-mode state machine + input + packet
bookkeeping + edit UI sections), `Formats\DevChangeSet.cs` (pure format layer: POCOs +
`DevChangeSetFile.Save`). As built:

- `enum DevEditMode { None, WaypointEdit, SpawnMove }`, armed from the Selected-NPC
  section ("Edit path" / "Move spawn" — need the spawn's DB row loaded). The intercept
  `HandleDevEditClick` sits in the world-click drain (`Program.Targeting.cs`) BEFORE the
  `_freeView` route; an armed mode **swallows every world click** (no stray RTS orders).
  `HandleFreeCamWorldClick` untouched.
- **Waypoint mode interactions**: left on a node = select (again = deselect); left on
  ground = insert AFTER the selected node (append when none selected);
  **Shift+left on ground = MOVE the selected node**; right on a node = delete; the
  window edits the selected node's `waittime`; **Esc cancels** (consumed by a dev-tool
  pre-gate at the top of `UpdateSettingsInput`, deliberately OUTSIDE `GameMenuUiLaw`).
  Node hit rects are filled by `DrawDevEditOverlay` (nameplate `_vplateHits` idiom) and
  consumed by the click handler.
- Commit renumbers gapless 1-based and emits ONE `waypoint-path-replace` packet with the
  FULL replacement path. **A template-origin edit saves as per-guid `creature_movement`**
  (target: this spawn only) — editing one spawn must never rewrite a shared template;
  the packet's `context.affectsSpawnCount` appears only for true template targets.
- Spawn move: left-click proposes; a model ghost via `EntityStore.AddSynthetic` (ghost
  guids from `0xF000_0000_00DE_0000 + spawnGuid`, distinct from the creator range) when
  the creature is streamed (display id known), plus a green ring + dashed old→new line
  always. Field edits: respawn min/max (`spawn-timer`), movement_type + wander_distance
  (`spawn-field`, only changed fields), detection_range (`template-field`, per-ENTRY
  with "affects N spawns" warning; **queued value previews live in the drawn discs**
  via `ApplyDevPendingTemplate`).
- Queued packets stay visible as dim-green previews (`_devPacketPreviews`, runtime-only)
  until reverted; the "Change set" section lists packets (a **verdict badge** appears per
  packet after a commit) and the owner flow — **Commit (SuperUI)** → **Reload selected** —
  alongside revert-x / Save now / the file path. The set **saves to disk on every mutation**
  (add/revert), one file per session: `dev-changes\<yyyyMMdd-HHmmss>-<character>.json`.
- Previews are **client-side only** — a queued packet never touches the DB or the live world
  until you **Commit** (§11); the live world only changes when you then **Reload**.
  Schema v1 (serialization verified against this exact shape):

```json
{ "schemaVersion": 1,
  "session": { "createdUtc": "…", "character": "…", "sourceSnapshotUtc": "…",
               "suiBase": "http://192.168.0.2:5000" },
  "packets": [
    { "id": 1, "type": "spawn-move",
      "target": { "table": "creature", "guid": 80841, "entry": 94, "map": 0 },
      "before": { "position_x": -9458.4, "position_y": -14.9, "position_z": 57.1, "orientation": 1.2 },
      "after":  { "position_x": -9461.0, "position_y": -20.3, "position_z": 57.4, "orientation": 1.2 } },
    { "id": 2, "type": "waypoint-path-replace",
      "target": { "source": "creature_movement", "id": 80841, "entry": 94, "pathId": 0 },
      "before": { "points": [ { "point": 1, "x": -9458.4, "y": -14.9, "z": 57.1, "waittime": 0 } ] },
      "after":  { "points": [ { "point": 1, "x": -9461.0, "y": -20.3, "z": 57.4, "waittime": 5000 },
                               { "point": 2, "x": -9470.2, "y": -25.0, "z": 58.0, "waittime": 0 } ] } },
    { "id": 3, "type": "template-field",
      "target": { "table": "creature_template", "entry": 94 },
      "before": { "detection_range": 18 }, "after": { "detection_range": 12 },
      "context": { "affectsSpawnCount": 27 } } ] }
```

Packet types: `spawn-move | spawn-timer | spawn-field | spawn-add (no guid; applier
allocates) | spawn-delete | waypoint-path-replace | template-field`. `before` = values as
fetched (`sourceSnapshotUtc` anchors staleness) — P4's verify diffs current DB against it.

## 11. P4 — direct commit + owner reload (Phase 1 BUILT · Phase 2 designed)

The design changed from "upload a file, review in a web page, apply" to **direct commit from
the client + owner-clicked live reload**: for spawn/pathing/aggro there is no data phase to
add in SuperUI (unlike spells), so the client is the reviewer.

**Phase 1 — BUILT (build-verified both repos; owner deploy pending):**
- **Client** (`GameLoop.DevWindow.Edit.cs`, `Net\DevDataClient.cs`): the Change set section
  gained **Commit (SuperUI)** → `DevDataClient.BeginApply` POSTs the change-set JSON to
  `/NpcDev/Apply` on a background Task and publishes an immutable `NpcApplyResult` (per-packet
  verdicts). A separate **Reload selected** button sends the live GM command per applied
  packet via `SendGmCommand` (`.reload creature_template` for aggro; `.npc reloadspawn <guid>`
  for spawn/path). Two buttons, owner-clicked, in that order. The local `dev-changes/*.json`
  write stays as a record.
- **SuperUI** (`Controllers\NpcDevController.cs` `POST Apply`, `Services\NpcDevApplyService.cs`,
  `Models\NpcDevApply.cs`): **verify** = re-read current rows, diff vs `before` (drift →
  `stale`, blocks that packet); **apply** = raw SQL to the mangos world DB (per-type column
  whitelist; waypoint = transactional DELETE + renumbered 1-based INSERT); **audit** = one
  `AuditService.LogAsync` per packet (Category `npc`; Action `npc_move|npc_timer|npc_field|
  npc_aggro|npc_waypoint`; TargetType the table; before/after JSON) inside one `AuditBatch`
  per commit. No staging DDL — `audit_log` is the record. Returns per-packet verdicts JSON.
- **Change Graph**: a `DomainDef("npc", "NPCs & Spawns", …)` in `ChangeGraphService` buckets
  those rows into one node under Worlds & History → Change Graph (richer visualization to be
  built out later). Rows carry before/after JSON but `RevertKind=None` for now — revert wiring
  is future work.
- Not yet handled by the applier: `spawn-add` / `spawn-delete` (verdict `unsupported`);
  `spawn-delete` will need the `Creature.cpp` cascade (creature_addon, creature_movement,
  game_event_creature, …).

**Phase 2 — BUILT (compile-verified on the box; owner commit + deploy + mangosd restart
pending):** the additive vmangos core command `.npc reloadspawn <guid>` (SEC_DEVELOPER, npc
group): re-read that one `creature` row + its path from DB into ObjectMgr, `SetHomePosition` +
`SetDefaultMovementType` + `SetWanderDistance` + reload path + kill/`Respawn()` if alive, **no
DB write** — modelled on `HandleNpcMoveHelperCommand` but sourced from the committed DB row.
Touches (in `~/vmangos`, uncommitted): `src/game/Movement/WaypointManager.{h,cpp}` (new
`ReloadPath(guid)`), `src/game/Chat/Chat.{h,cpp}` (decl + `npcCommandTable` entry),
`src/game/Commands/CreatureCommands.cpp` (`HandleNpcReloadSpawnCommand`). Until it deploys,
`.npc reloadspawn` is an unknown-command no-op, so spawn-move / waypoint commits are audited
but only go live on the creature's next natural respawn; aggro is instant via `.reload
creature_template`.
Why a new command is needed: no existing command re-reads one spawn's row live — `.reload
creature` refreshes the ObjectMgr cache but skips already-spawned creatures, `GetRespawnCoord`
prefers the cached `m_homePosition`, and `.npc move` snaps to the GM's position and self-writes
the DB. (Verified in `src/game/{Objects/Creature.cpp,ObjectMgr.cpp,Commands/CreatureCommands.cpp}`.)

---

## 12. Deploy notes

- **Client**: no opcode pairing — ship whenever. Commit needs the SuperUI `POST Apply`
  deploy to succeed; until then Commit fails gracefully (the HTTP error shows in the panel)
  and the local `dev-changes/*.json` still records the edits.
- **MangosSuperUI** (`mangossuperui.service` on 192.168.0.2, app at
  `/opt/mangossuperui`): agents may prepare and build the publish artifact, then must
  stop. Nico alone installs/deploys it or controls the service. Restarting
  mangossuperui also restarts the bot brain in the same process (fleet bots
  reconnect). Until Nico deploys it, the client silently uses the CSV fallback;
  afterward, the source line in the window flips to "snapshot".
- **vmangos core (P4 Phase 2)**: `.npc reloadspawn` is an additive C++ change. Agents may
  write + build it on the box (`~/vmangos/build`), then stop — Nico deploys the binary and
  restarts mangosd (a one-time restart lights up instant spawn/waypoint reload; aggro reload
  already works without it).

---

## 13. Verification playbook

- **Scripted in-client runs** (no hands needed): the live-run protocol step
  `key press <KeyName>` / `key release <KeyName>` feeds `InputKeyDown` (via
  `_liveInputHeld`), so any chord is scriptable — Ctrl+N, Ctrl+F, etc. Protocol used:

  ```
  wait 3
  key press ControlLeft
  key press N
  wait 1
  key release N
  key release ControlLeft
  wait 20                       # template CSV + world CSVs fetch/parse
  dump devwindow-db             # → dumps/gameplay-devwindow-db-*.png (+ .json)
  ```

  Run: `dotnet run --project MSUIClient/MSUIClient.csproj -- <config> --live-bootstrap
  --character Magetest --live-protocol <file> --out live-runs --timeout 240`.
  NOTE: the repo `client-config.json` may have `server.enabled=false` (offline/creator
  use); pass a copy with `"enabled": true` rather than editing the user's file.
- **Endpoint smoke** (read-only): `Invoke-WebRequest
  'http://192.168.0.2:5000/Database/Export/mangos/creature_template'` — header row must
  contain `detection_range`, `static_flags1`. SQL sanity over ssh:
  `mysql -umangos -pmangos mangos -e "SELECT …"` (SELECTs only).
- **Console markers**: `[dev-data]` (fetch results/fallbacks), `[dev-aggro]`
  (AI_REACTION predicted-vs-actual).
- **Eyes-on checklist** (owner): band colors ordered around a hostile camp; step into a
  disc → red x-ray beam then real aggro (+flash); a patroller's cyan/gold DB route vs its
  dashed observed route; dimmed "(not streamed)" markers; provenance panel names real
  table/columns; Deadmines entrance → flat-disc fallback, no crash; creator mode →
  window opens, live-only notes shown.

## 14. Known limitations & traps

1. **WMO floors**: discs fall back to flat at feet-Z indoors (terrain-only gather).
2. **Streaming eye radius**: only ~streamed NPCs exist client-side; DB-only markers +
   the "N streamed / M DB-only" counter make the gap visible instead of silent. The
   freecam eye teardown-on-possess bug (CRPG_RTS_WIP.md) also limits what streams while
   commanding from the sky.
3. **Aggro is an estimate** (no LoS/auras/config rate) — labeled in the window;
   AI_REACTION is the ground truth signal.
4. **`static_flags1`**, not `static_flags` — both the CSV parser and endpoint use word 1;
   flags in word 2 are not fetched.
5. **Chrome sizing**: default window rects must scale by the display factor `s`, not
   `CreatorUiScale` alone (cs-only = sliver at 4K). The window inherits the creator
   dials (`Settings.Creator.*`) by design; per-window gear dials tune it independently.
6. **UI font has no U+25A0 (■)** — draw color swatches with the draw list.
7. **Recording gates**: observed-path + AI_REACTION taps no-op while the window is
   closed — keep it that way (instrumentation-in-gameplay-path hazard).
8. **imgui.ini** persists the window rect (`###npc-dev`) — a bad saved rect survives
   restarts; "reset" = delete that ini block.
9. **creature CSV patch rows**: `patch_min/max` are kept as-is (guid is PK, no dedup
   needed); template rows DO need highest-patch-wins.
10. **Creator mode**: no reaction data (`_net` null → Neutral), so `HostilesOnly` is
    ignored there; synthetic spawns are excluded from DB joins by the guid high check.

## 15. P5 — OG baseline, wash-on-commit, reset-to-original (BUILT)

Turns "current DB" edits into an original-vs-current model, so a mob can be shown as
changed-from-vanilla and snapped back. BUILT + compile-verified in both repos.

- **OG baseline** — `BaselineController.SNAPSHOT_TABLES` gained `og_creature`,
  `og_creature_movement`, `og_creature_template` (captured from `mangos` into `vmangos_admin`
  by the existing `POST /Baseline/Initialize` — `CREATE TABLE LIKE` + `INSERT SELECT`, recorded
  in `og_baseline_meta`). This is the true VMaNGOS baseline the tool diffs/resets against. Owner
  runs Initialize ONCE from a pristine DB (undo stray edits first — the baseline bakes in
  whatever the tables hold at capture time).
- **Diff (changed from original?)** — `GET /NpcDev/Diff?guid=&entry=` (`NpcDevBaselineService`)
  cross-references `vmangos_admin.og_creature*` from the mangos connection and returns
  `hasBaseline` + `spawnModified` / `pathModified` / `templateModified` / `baselineHasSpawn`. The
  client fetches it when a creature is inspected and shows a "BASELINE (original)" row in the
  Selected-NPC section — a "● changed from original" badge (spawn / path / aggro) or "matches
  original".
- **Reset to original** — `POST /NpcDev/Reset` (`{guids, entries}`): per guid `REPLACE INTO
  creature SELECT * FROM og_creature` + transactional path restore from `og_creature_movement`;
  per entry restore `detection_range` from `og_creature_template`. Audited (category `npc`,
  action `baseline_reset_creature` / `baseline_reset_creature_template`, before/after) under one
  `AuditBatch` → same NPC node in the Change Graph. Client buttons: **Reset spawn to original**
  (per guid) and **Reset aggro to original — all spawns** (per entry, with the affects-all
  warning). On success the client reloads live, resnapshots, and re-diffs. Reset only writes the
  DB; the client makes it live, same as commit.
- **Wash-on-commit** — after a commit lands OK the client drops the applied packets from the
  change-set (stale/failed stay for retry), captures their reload targets first (so **Reload
  selected** still works post-wash), and force-resnapshots the world to the new DB baseline. A
  green `committed ✓ N applied…` line is the confirmation.
- Files: `GameLoop.DevWindow.Edit.cs` (`WashCommittedPackets`, `DrawDevBaselineControls`),
  `Net/DevDataClient.cs` (`BeginDiff`/`BeginReset` + `NpcDiff`), `Controllers/NpcDevController.cs`
  (`Diff`/`Reset`), `Services/NpcDevBaselineService.cs`, `Models/NpcDevApply.cs`.
- **Not yet:** map-wide "changed from OG" overlay badging (only the inspected creature diffs
  today); revert of a reset (rows are history-only, `RevertKind=None`).
- **Deploy (owner):** deploy the SuperUI artifact, then `POST /Baseline/Initialize` once from a
  pristine DB to capture `og_creature*`. Keep edits paused between the restore and the capture so
  OG is genuinely clean. Ship the client. No core change in P5.
