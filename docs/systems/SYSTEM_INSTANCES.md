# System: Instances — maps, travel, and instance portals

Draft 1, 2026-07-26. Extracted from `PLAN_13_INSTANCES.md` under the handbook
§1.2 rule once Deadmines had survived a session.

> **NAMING TRAP, READ THIS FIRST.** "Portal" means two unrelated things in this
> project. **This** doc is about *instance portals* — the doorways that move the
> player between maps. `PLAN_10_WMO_PORTALS.md` and the planned
> `SYSTEM_WMO_PORTALS.md` are about *WMO portals* — the MOPV/MOPT/MOPR polygons
> used for interior visibility culling. They share a word and nothing else.

---

## 1. What this system does

Pick a dungeon, stand outside its door in the open world, walk in, and come back
out. No server, no loading screen.

**Status: working.** Deadmines, Shadowfang, Scarlet Monastery, Razorfen Kraul and
the battlegrounds all travel. The trip back lands on the exact spot you left.

---

## 2. The finding that shaped everything

The obvious mental model is *"a dungeon is one big WMO"*. **It is wrong for four
of the dungeons this project cares about most, including the handbook's stated
target.** Instance maps come in two structurally different kinds:

| Kind | MPHD flags | ADT tiles | MODF | Examples |
|---|---|---|---|---|
| **Global WMO** | `0x0001` | **0** | 1 | Wailing Caverns, Stockade, Gnomeregan, Blackrock Depths, Uldaman, Sunken Temple, Onyxia, Molten Core |
| **Terrain** | `0x0000` | real tiles | 0 | **Deadmines (36)**, Shadowfang (25), Scarlet Monastery (36), Razorfen Kraul (6) |

**Deadmines is a terrain map**, which makes it the *cheapest* target rather than
the hardest. So the branch that matters anywhere downstream is
`WdtFile.UsesGlobalWmo`, **never `instanceType` and never "is this a dungeon"**.
Azeroth and Deadmines travel through identical code with different tile counts.

`PLAN_13 §11` carries the full 44-row reference table. The client's Instances
panel reproduces it exactly — verified 44/44 on 2026-07-26.

---

## 3. The four data sources

| Source | Gives | Reader |
|---|---|---|
| `Map.dbc` | 44 rows: id, **Directory** (the on-disk name), instanceType, display name | `MapTable`, `Formats/DbcReader.cs` |
| `World\Maps\{dir}\{dir}.wdt` | which tiles exist, or the single global WMO | `Formats/WdtReader.cs` |
| `AreaTrigger.dbc` | 432 trigger **volumes** — sphere or oriented box, both sides of every portal | `AreaTriggerTable`, `Formats/DbcReader.cs` |
| `areatrigger_teleport.tsv` | where each trigger **sends** you | `Formats/AreaTriggerTeleport.cs` |

### 3.1 The client cannot tell you where a portal goes

`AreaTrigger.dbc` has the volumes and says nothing about destinations — not the
map, not the position, not even the direction. Vanilla never needed to: you walk
in, you tell the server, the server teleports you.

Ruled out before reaching for the server, so nobody re-checks them:
- **`AreaPOI.dbc`** is landmarks ("Echo Ridge Mine", "Sentinel Hill").
- **pfQuest's `areatrigger.lua`** carries only map-pin percentages.

The destinations live in VMaNGOS's `areatrigger_teleport`. **This project talks
to a VMaNGOS server, so we have it** — `areatrigger_teleport.tsv` is committed at
the repo root, raw `mysql -B` output so the dump itself is the provenance.

The key is **`(id, patch)`, not `id`**. Six Dire Maul entrances appear twice —
patch 0 with *"You Shall Not Pass!"* at level 61, patch 1 with the real level-45
requirement. 1.12.1 is the last vanilla patch, so the **highest patch wins**.
Taking the first would lock Dire Maul at 61 forever, a bug that reads as content
rather than as parsing.

### 3.2 The WDT tile convention

`MAIN` is 64x64 indexed `y * 64 + x`, low bit = "this tile has an ADT". **MAIN x
is the client's col, MAIN y is the client's row** — confirmed by following
`AdtCache.Get(col, row)` into `ReadFromMpq(gridX: row, gridY: col)` into
`{map}_{col}_{row}.adt`. Getting this backwards transposes every map about its
diagonal, which on a square dungeon looks like nothing at all.

World position: `originX = (32 - row) * 533.33333`, `originY = (32 - col) * 533.33333`.

---

## 4. Travel

`TravelTo` in `GameLoop/Scene/GameLoop.Instances.cs`. **It swaps the world's content; it never
re-runs `Load()`**, which builds the MPQ mount, worker pools, GL renderers,
shaders and character — almost none of which is per-map.

### 4.1 The five per-map clears, and the two that hurt

| State | Cleared by |
|---|---|
| ADT cache's map directory | `AdtCache.SetMap` |
| resident terrain tiles | `TerrainRenderer.UnloadAll` |
| built liquid meshes | `LiquidRenderer.UnloadAll` |
| WMO + doodad placements, and the WMO ring bookkeeping | `WmoRenderer.ResetForMapChange`, `DoodadRenderer.ResetPlacements` |
| collision world | `LoadCollision`, **plus a generation bump** |

**THE TILE-KEY TRAP.** Tile keys are `(col, row)` on a grid *every map shares* —
Deadmines is col 30..35 row 30..35 and Azeroth has those tiles too. So
`SetResidency`, `LiquidRenderer.LoadForTiles` and the ADT cache all **keep**
whatever sits under a wanted key. Across a map boundary that renders Elwynn
hillside and Elwynn river inside the dungeon, **with nothing logged**. Clearing
is the correctness condition, not an optimisation.

**THE ASYNC COLLISION RACE.** `BeginCollisionBuild` stamps a worker build with
`_collisionGeneration`; `AcceptReadyCollision` installs it if the stamp still
matches. Setting `_collision = null` does **not** stop a build already in flight,
so a tile crossing a second before the travel lands the **old map's BVH** on the
new map one frame *after* arrival. The ordering guard (collision resident before
the player is placed) does not catch it, because it arrives late. Bumping the
generation is what drops it.

`AdtCache` is generation-guarded on all four doors — the parse path, the publish
path, the blocking pending path, and `_pending` eviction. A worker parse started
before the swap would otherwise publish Azeroth's `[32,32]` into Deadmines'
`[32,32]`: same key, different world, no error anywhere.

### 4.2 Failure handling

Everything checkable before the teardown is checked before it: world loaded, not
a global-WMO map, tile count non-zero. After it, a zero-tile load and a
mid-refill exception both unwind through `RestoreAfterFailedTravel` — **inline,
not a recursive `TravelTo`**, because recursing into the same code to handle
"the reload produced nothing" is how one bad map becomes an infinite loop.

---

## 5. Portals

Two halves joined: the **volume** from `AreaTrigger.dbc` and the **destination**
from `areatrigger_teleport`. Most of the 432 triggers are quest and script
triggers, so the join is what turns the table into a set of doorways.

### 5.1 "Go to entrance" is derived, not authored

It stands you where the dungeon's **paired exit** drops you, facing back at the
door — a legal, walkable position just outside the entrance, authored by
Blizzard, costing us no geometry.

Pairing is **geometric, never by name**: for an entrance E on map A leading to
map B, the paired exit is the trigger on B whose destination is nearest E's own
volume. Validated on all twelve dungeon entrances — every one resolves to a spot
**8.7–22.1 yards** out, outside the volume, with the facing dot against the
doorway between **+0.96 and +1.00**.

Scarlet Monastery is what proves it: four doors within sixty yards, and the
pairing gave each its own exit (Graveyard→602, Cathedral→604, Armory→606,
Library→608). A loose pairing would have crossed them, and the symptom would be
arriving outside the wrong wing — which looks like nothing at all until you walk
in.

### 5.2 The latch is not optional

Vanilla drops you **8.2 yards** from Deadmines' exit trigger 119, whose radius is
**6**. Outside — but only just, and one step back through the door re-enters it.
So every arrival latches the trigger it lands in, and the latch clears only when
the player leaves every volume.

**It must be set in `TravelTo`, not on the portal path alone.** Review caught it
scoped too narrowly, which made the Return button unusable for any dungeon
entered through a portal: the return trip puts you back inside the entrance
volume, the stale latch names a trigger on the map you just left, and the next
frame sends you straight back in.

### 5.3 Not enforced

`required_level` is read and displayed, never checked — there is no character
level in this client yet, and refusing on a value we cannot evaluate would make
the feature untestable.

---

## 6. Instruments

- **Instances panel** — every map with kind, tile count, col/row range, centre
  tile, world origin, global-WMO path and MODF placement. `Dump to console`
  prints the table for diffing against PLAN_13 §11. Read-only.
- **Travel / Go to entrance / Return** buttons, and a portal on/off switch.
- **`[travel]` and `[portal]` console lines** carry the whole transition.

---

## 7. Open

- **Stage 3: global-WMO maps.** Server-authoritative `SMSG_NEW_WORLD` transfers
  now load the WDT's single global WMO, its doodads, and client-geometry
  collision before revealing the player. This covers `.tele brd`, entering BRD
  through the server portal, and logging in while already inside one of these
  maps. The offline Instances-panel travel buttons still refuse them because
  that synchronous debug path has not been moved onto the incremental global-WMO
  loader yet. Every one of the 21 has a **degenerate MODF** — nameId 0,
  uniqueId −1, flags 0, position `(0,0,0)`, rotation `(0,0,0)` — so the bounding
  box is the only field that varies and the placement maths has nothing to get
  wrong except the box. The likeliest failure is falling through the floor:
  there is no terrain, so WMO collision must be resident before the player is
  placed.
- **`development` is a trap.** 18 tiles scattered across col 0..63, and its
  centre tile `[31,1]` has no ADT. `WdtFile.SpawnTile` exists for this.
- **`ScottTest`** is a terrain map with **zero** tiles. "Terrain kind" and "has
  terrain" are not the same predicate.
- **Scarlet Monastery is two `Map.dbc` rows.** Id 44 `Monastery` is a global-WMO
  map marked `<unused>`; the playable one is id 189 `MonasteryInstances`.
  **Filter on the display name, not the directory.**
- **`Vantage.MapName`** now has a real destination behind it; the cross-map
  warning could become a travel offer.
