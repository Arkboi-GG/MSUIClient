# Plan 13 — Instances: loading in and out of dungeons

Status: **stage 1 VERIFIED on Nico's machine 2026-07-25 — all 44 rows match §11
exactly, zero mismatches. Stage 2 BUILT and unrun. Stage 3 specified, not
built.** Every fact in §1 was read out of the archives with `tools/mpqpeek`
before a line was written.

## 1. What the data actually says — read, not assumed

The obvious mental model is "a dungeon is one big WMO". **It is wrong for four of
the dungeons we care about most, including the P5 target.**

There are **56 WDTs** and **44 `Map.dbc` rows**, and instance maps come in **two
structurally different kinds**:

| Kind | MPHD flags | ADT tiles | MODF | Examples |
|---|---|---|---|---|
| **Global WMO** | `0x0001` | **0** | 1 | Wailing Caverns, Stockade, Gnomeregan, Blackrock Depths, Uldaman, Sunken Temple, Onyxia, Molten Core |
| **Terrain** | `0x0000` | real tiles | 0 | **Deadmines (36)**, Shadowfang (25), Scarlet Monastery (36), Razorfen Kraul (6) |

**Deadmines — handbook §7 P5's stated target — is a terrain map.** A design that
assumed one-WMO-per-dungeon would have shipped broken for exactly the dungeon the
project is aiming at.

### 1.1 `Map.dbc`

44 records, 42 fields, 168-byte records. Field **0** id, **1** directory,
**2** instanceType (0 world, 1 dungeon, 2 raid, 3 battleground), **4** name
(enUS). Verified against known ids: 33 Shadowfang, 36 Deadmines, 43 Wailing
Caverns, 90 Gnomeregan, 189 Scarlet Monastery.

Resolution order matters: `Map.dbc` is in **patch.MPQ, patch-2.MPQ and dbc.MPQ**,
and patch wins. `MpqMount`'s load order already gets this right.

### 1.2 Global-WMO maps

Every one sampled has **MODF position `(0,0,0)` and rotation `(0,0,0)`** — the
WMO is placed at the origin of ADT placement space, unrotated. That is a
simplification worth knowing: the placement transform that WmoRenderer applies to
ADT-sourced WMOs still applies, but with an identity-ish input.

Bounds give a usable spawn box:

```
WailingCaverns      lo=(-554.4, -194.9, -187.5)  hi=(369.8,  56.5, 483.5)
StormwindJail       lo=(-151.8,  -35.7, -197.3)  hi=(150.3,  15.2,   8.1)
GnomeragonInstance  lo=(-756.2, -331.8,  200.9)  hi=(142.0, -90.8, 914.0)
Uldaman             lo=(-466.3,  -56.4, -184.3)  hi=( 69.8,   2.2, 371.5)
```

### 1.3 Terrain maps, and the tile convention

`MAIN` is a 64x64 table indexed `y * 64 + x`, low bit of each entry's flags means
"this tile has an ADT". The client's `(col, row)` maps to **col = MAIN x,
row = MAIN y** — confirmed by following `AdtCache.Get(col,row)` into
`ReadFromMpq(..., gridX: row, gridY: col)` into the path
`{map}_{gridY}_{gridX}.adt`, which is `{map}_{col}_{row}.adt`.

| Map | tiles | col | row | centre tile | world approx |
|---|---|---|---|---|---|
| DeadminesInstance | 36 | 30..35 | 30..35 | [32,32] | (-267, -267) |
| Shadowfang | 25 | 25..29 | 30..34 | [27,32] | (-267, 2400) |
| MonasteryInstances | 36 | 28..33 | 27..32 | [30,29] | (1333, 800) |
| RazorfenKraulInstance | 6 | 27..29 | 27..28 | [28,27] | (2400, 1867) |

World position from `originX = (32 - row) * 533.33333`,
`originY = (32 - col) * 533.33333`, matching `AdtTerrainReader`.

## 2. Class

**Addition** in behaviour — there is no server, so "entering a dungeon" is our
own affordance and is measured against intent.

**Emulation-core** in the parsing. The WDT parse either matches Blizzard's bytes
or it does not, and §1 is the evidence that it does.

## 3. Target

Pick a dungeon from a list, be standing in it a second later, and come back out
to where you were. Both kinds of map. No server, no loading screen.

## 4. Key design decisions

**H1 — special-case "global WMO map", never "dungeon".**
The branch that matters is `MPHD & 0x0001`, not `instanceType`. Azeroth and
Deadmines take the *same* code path — one just has fewer tiles. Battlegrounds and
the raid maps then come along free, and `DeeprunTram` (a global-WMO map that is
not an instance at all) does not become a special case.
*Falsifiable:* Deadmines must load through exactly the terrain path Azeroth uses,
with no `if (isDungeon)` anywhere in it.

**H2 — swap the world's CONTENT, do not re-run `Load()`.**
`Load()` is one linear method that builds the MPQ mount, the worker pools, the
GL renderers, the shaders and the character. Almost none of that is per-map.
Re-running it would leak GL objects and restart the workers.

What is actually per-map: the ADT cache's map directory, the resident terrain
tiles, the WMO and doodad placements, the collision world, and the player's
position. Two of the five clears already exist — `WmoRenderer.ResetPlacements`
and `DoodadRenderer.ResetPlacements` — and `AdtCache` already has `Clear()`.
**What is missing is a terrain unload and a settable map name on `AdtCache`.**
*Falsifiable:* enter and leave a dungeon ten times; GL object count and managed
heap must not climb monotonically.

**H3 — entry points are ours, and honestly so.**
There is no offline source for "where does this dungeon's entrance put you".
`AreaTrigger.dbc` carries the trigger *volumes*; the destinations live in the
server's `areatrigger_teleport` table, which we do not have. So v1 derives a
spawn:

- terrain map -> centre of the tile cluster, dropped onto whatever collision is
  under it;
- global-WMO map -> centre of the MODF bounds in X/Y, top of the box in Z,
  dropped.

Both are *arrival points*, not *entrances*, and are labelled as such. When P2
lands, the server sends a real position and this whole mechanism becomes a
debug affordance. **Do not spend time hand-authoring 20 entrance coordinates
that the server will supersede.**

**H4 — everything downstream must tolerate zero terrain tiles.**
On a global-WMO map there is no ADT at all: no heightmap, no MCLQ liquid, no
ground effects, no terrain collision. `CharacterController` takes ground height
from terrain first and collision second (§3's `VanillaHeightPrecedence`), and on
these maps terrain simply never answers. **The likeliest failure of this whole
plan is falling through the floor of Gnomeregan**, and the guard is that WMO
collision must be resident before the player is placed, not after.
*Falsifiable:* `NO GROUND` in the HUD on arrival means the ordering is wrong.

**H5 — stage it, and prove the parsing before tearing anything down.**
Stage 1 is read-only: `Map.dbc` + WDT parsed and displayed. It cannot break the
working outdoor client, and it converts §1's claims into something that either
renders correctly on Nico's machine or does not. Stages 2-3 only then touch the
world. This is deliberate — the last four untested changes in this project each
took several runtime rounds to settle, and every one of them would have been
cheaper with a checkpoint in front of it.

## 5. Resources

| Source | Gives |
|---|---|
| `tools/mpqpeek/` | Everything in §1. This is what it was built for |
| `Formats/DbcReader.cs` `DbcFile`, `GroundEffectTextureTable` | The typed-table pattern to copy for `MapTable` |
| `Formats/AdtTerrainReader.cs` `WmoInstance`, MODF parse at ~1261 | MODF is 64 bytes and is already parsed once; the WDT's is the same record |
| `World/AdtCache.cs` | `Clear()`, `Retain()`, and the `_mapName` that needs to become settable |
| `World/Wmo/WmoRenderer.cs` `ResetPlacements` | Half of the teardown, already written |
| `World/Doodads/DoodadRenderer.cs` `ResetPlacements` | The other half |
| `Program.cs` `UpdateWorldResidency`, `PopulateDoodads`, `LoadCollision` | The refill path a swap has to re-trigger |
| `Engine/Vantage.cs` | Already stores `MapName` and already warns on mismatch — the return trip is a vantage |

## 6. Tools / instrument

- **Stage 1's own panel** — every map with kind, tile count, tile range and
  global-WMO path. It is the test for §1.
- **Vantages** already carry `MapName` and `ApplyVantage` already warns when it
  does not match. Once stage 2 lands, that warning becomes a travel instruction.
- **Scene dump** records the map; two dumps either side of a transition are the
  A/B.
- **`NO GROUND` HUD line** is H4's alarm and already exists.

## 7. Test protocol

**Stage 1 (buildable and checkable now):**

1. Open the Instances panel. It must list **44 maps**, and the four in §1's
   second table must show the tile ranges printed there. Any disagreement means
   the WDT parse is wrong, and §1 is the reference.
2. Wailing Caverns must show `global WMO` and
   `world\wmo\dungeon\kl_wailing\wailingcaverns_instance.wmo`.

**Stage 2 (terrain maps):**

3. Travel to Deadmines. Terrain appears, the player stands on it, `NO GROUND`
   never shows.
4. Travel back to Northshire. The outdoor world is intact and identical.
5. Ten round trips; watch the managed heap and the GL object count (H2).

**Stage 3 (global-WMO maps):**

6. Travel to the Stockade — the smallest global-WMO map, so the fastest to load
   and the easiest to eyeball. The WMO appears, the player stands inside it.
7. Gnomeregan, whose bounds are the largest and whose Z range (200..914) is
   furthest from zero — the case most likely to expose an origin mistake.

## 8. Definition of done

- Stage 1: the panel agrees with §1 on Nico's machine.
- Stage 2: Deadmines, Shadowfang and Scarlet Monastery load and are walkable, and
  the trip back leaves the outdoor world unchanged.
- Stage 3: the Stockade and Gnomeregan load and are walkable.
- `SYSTEM_INSTANCES.md` extracted under the §1.2 rule once one dungeon has
  survived a session.
- **Not done:** instance *reset*, multiple instances of one map, or anything
  requiring a server. This is travel, not instancing.

## 9. Fallback

Stage 1 is read-only and cannot regress anything. Stage 2's swap is guarded by
"if any per-map clear fails, refuse the travel and stay where you are" — a
refused transition is a bad afternoon, a half-cleared world is a bug hunt.

## 10. Reconciliation

- **Handbook §7 P5** names Deadmines as the dungeon target. §1 says it is a
  terrain map, which makes it the *cheapest* target, not the hardest. P5's
  ordering is still right, for a better reason than it knew.
- **§3.26's 120-yard interior rule and PLAN_10's portal work** are about WMO
  interiors, which is precisely what a global-WMO map is entirely made of.
  Stage 3 will be the harshest test PLAN_10's traversal ever gets — and a good
  reason to do PLAN_10 *after* stage 3 rather than before, so it has a real case.
- **`Vantage.MapName`** stops being a warning and becomes a destination.
- No overlap with PLAN_12 or the streaming front.

## 11. Appendix — the full reference table

Read with `tools/mpqpeek` on 2026-07-25, from `patch.MPQ`'s `Map.dbc` (9761
bytes, 44 records, 42 fields, 168-byte records). **All 44 maps have a WDT.**
This is what the stage-1 panel's `Dump to console` must reproduce; any row that
differs means the C# readers and the Python tool disagree, and the handbook's
rule is that the C# is right and the tool gets fixed — but the *first* thing to
suspect is the C#, because the tool produced this before the C# existed.

```
  id ty directory                  kind      tiles col     row     centre
   0  0 Azeroth                    terrain     687 24..44  20..61  [34,40]
   1  0 Kalimdor                   terrain    1018 0..50   0..55   [25,27]
  13  0 test                       globalWMO     0
  25  0 ScottTest                  terrain       0
  29  1 Test                       globalWMO     0
  30  3 PVPZone01                  terrain      35 30..34  29..35  [32,32]
  33  1 Shadowfang                 terrain      25 25..29  30..34  [27,32]
  34  1 StormwindJail              globalWMO     0
  35  0 StormwindPrison            globalWMO     0
  36  1 DeadminesInstance          terrain      36 30..35  30..35  [32,32]
  37  0 PVPZone02                  terrain      30 29..34  29..33  [31,31]
  42  0 Collin                     globalWMO     0
  43  1 WailingCaverns             globalWMO     0
  44  1 Monastery                  globalWMO     0
  47  1 RazorfenKraulInstance      terrain       6 27..29  27..28  [28,27]
  48  1 Blackfathom                globalWMO     0
  70  1 Uldaman                    globalWMO     0
  90  1 GnomeragonInstance         globalWMO     0
 109  1 SunkenTemple               globalWMO     0
 129  1 RazorfenDowns              terrain      24 27..32  26..29  [29,27]
 169  2 EmeraldDream               terrain     256 24..39  25..40  [31,32]
 189  1 MonasteryInstances         terrain      36 28..33  27..32  [30,29]
 209  1 TanarisInstance            terrain      21 29..31  27..33  [30,30]
 229  1 BlackRockSpire             globalWMO     0
 230  1 BlackrockDepths            globalWMO     0
 249  2 OnyxiaLairInstance         globalWMO     0
 269  1 CavernsOfTime              terrain      39 17..32  25..36  [24,30]
 289  1 SchoolofNecromancy         terrain      16 30..33  29..32  [31,30]
 309  2 Zul'gurub                  terrain      25 33..37  52..56  [35,54]
 329  1 Stratholme                 terrain      20 36..40  24..27  [38,25]
 349  1 Mauradon                   globalWMO     0
 369  0 DeeprunTram                globalWMO     0
 389  1 OrgrimmarInstance          globalWMO     0
 409  2 MoltenCore                 globalWMO     0
 429  1 DireMaul                   globalWMO     0
 449  0 AlliancePVPBarracks        globalWMO     0
 450  0 HordePVPBarracks           globalWMO     0
 451  0 development                terrain      18 0..63   0..2    [31,1]
 469  2 BlackwingLair              terrain      16 32..35  44..47  [33,45]
 489  3 PVPZone03                  terrain      16 27..30  27..30  [28,28]
 509  2 AhnQiraj                   terrain      40 26..30  46..53  [28,49]
 529  3 PVPZone04                  terrain      16 28..31  28..31  [29,29]
 531  2 AhnQirajTemple             terrain      25 26..30  46..50  [28,48]
 533  2 Stratholme Raid            terrain      24 37..42  24..27  [39,25]
```

`ty` is `Map.dbc` instanceType: 0 world, 1 dungeon, 2 raid, 3 battleground.

Global-WMO paths and MODF bounds, all at position `(0,0,0)` rotation `(0,0,0)`:

```
  13 test                world\wmo\dungeon\test\test.wmo
  29 Test                world\wmo\dungeon\test\test.wmo
  34 StormwindJail       world\wmo\dungeon\az_stormwindprisons\stormwindjail.wmo
                         lo(-151.8, -35.7, -197.3)  hi(150.3, 15.2, 8.1)
  35 StormwindPrison     world\wmo\dungeon\az_stormwindprisons\stormwindprison.wmo
  42 Collin              world\wmo\dungeon\test\collintest.wmo
  43 WailingCaverns      world\wmo\dungeon\kl_wailing\wailingcaverns_instance.wmo
                         lo(-554.4, -194.9, -187.5)  hi(369.8, 56.5, 483.5)
  44 Monastery           world\wmo\dungeon\monestary\scarlet_monestary_interior.wmo
  48 Blackfathom         world\wmo\dungeon\kl_blackfathom\blackfathom_instance.wmo
  70 Uldaman             world\wmo\dungeon\kz_uldaman\kz_uldaman_b.wmo
                         lo(-466.3, -56.4, -184.3)  hi(69.8, 2.2, 371.5)
  90 GnomeragonInstance  world\wmo\dungeon\kz_gnomeragon\kz_gnomeragon_instance.wmo
                         lo(-756.2, -331.8, 200.9)  hi(142.0, -90.8, 914.0)
 109 SunkenTemple        world\wmo\dungeon\sunkentemple\az_sunkentemple_instance.wmo
 229 BlackRockSpire      world\wmo\dungeon\az_blackrock\blackrock_upper_instance.wmo
 230 BlackrockDepths     world\wmo\dungeon\az_blackrock\blackrock_lower_instance.wmo
 249 OnyxiaLairInstance  world\wmo\dungeon\kl_onyxiaslair\kl_onyxiaslair_b.wmo
 349 Mauradon            world\wmo\dungeon\kl_maraudon\kl_maraudon_instance01.wmo
 369 DeeprunTram         world\wmo\dungeon\az_subway\subway.wmo
 389 OrgrimmarInstance   world\wmo\dungeon\kl_orgrimmarlavadungeon\lavadungeon.wmo
 409 MoltenCore          world\wmo\dungeon\az_blackrock\blackrock_lower_guild.wmo
 429 DireMaul            world\wmo\dungeon\kl_diremaul\kl_diremaul_instance.wmo
 449 AlliancePVPBarracks world\wmo\azeroth\buildings\stormwind\az_pvpbarracks.wmo
 450 HordePVPBarracks    world\wmo\kalimdor\ogrimmar\kl_pvpbarracks.wmo
```

Three things in this table were not in §1 and change what stage 2 and 3 must
handle:

1. **`ScottTest` is a terrain map with ZERO tiles.** "Terrain kind" and "has
   terrain" are not the same predicate. H4's "tolerate zero terrain tiles" is
   therefore not only a global-WMO concern, and any travel UI must refuse this
   map rather than drop the player into an empty grid.
2. **Scarlet Monastery is two maps.** Id 44 `Monastery` is a global-WMO map
   marked `<unused> Monastery` in `Map.dbc`; the playable one is id 189
   `MonasteryInstances`, terrain, 36 tiles. Travelling to the wrong one loads a
   legacy interior with nothing around it. **Filter on the display name, not on
   the directory.**
3. **`development` spans col 0..63 with only 18 tiles** — a sparse, deliberately
   scattered map. Any assumption that the occupied tiles form a solid rectangle
   is false, which is why the panel prints a count *and* a range rather than
   inferring one from the other. The centre tile of `development` is `[31,1]`,
   which has no ADT at all; H3's "centre of the tile cluster" spawn must fall
   back to the first occupied tile when the centre is empty.

## 12. Cross-check against SuperUI's `WdtReader.cs`

Nico handed over the WDT reader from MangosSuperUI — a codebase where these
dungeons **actually rendered**. That makes it evidence, not just a second
opinion, and it was checked line by line against `Formats/WdtReader.cs` and
against a fresh sweep of all 44 archives.

**The two readers agree on everything that matters.** Chunk walk, `MPHD & 0x01`,
`MAIN` as 64x64x8 with the low flag bit, MWMO as a string, MODF as 64 bytes at
the same offsets, and `World\Maps\{dir}\{dir}.wdt`. Nothing in §1 is contradicted
by a reader that shipped working output.

The sweep that check prompted also closed off the "what else might be in there"
question. Across all 44 files:

- **MVER is 18 everywhere.** No exceptions.
- **MPHD flags are only ever `0x00000000` or `0x00000001`.** No other bit is set
  on any map, so `UsesGlobalWmo` is the *whole* of the header, not one field
  among several.
- **MAIN is always exactly 32768 bytes**, so the grid is never short.
- **MWMO holds exactly one string** when present and is zero bytes on every
  terrain map. **MODF holds exactly one 64-byte entry.** Never zero, never two.
- **Chunk order is always `MVER > MPHD > MAIN > MWMO [> MODF]`.** Our deferral of
  the MODF parse until after MWMO is therefore belt-and-braces, and stays.
- **Every MODF placement is degenerate the same way:** nameId 0, uniqueId
  `0xFFFFFFFF`, flags 0, doodadSet 0, nameSet 0, position `(0,0,0)`, rotation
  `(0,0,0)`. **The bounding box is the only field that varies.** Stage 3's
  placement maths has nothing to get wrong except the box.

### What was adopted

- **SuperUI's MVER guard**, softened. It returns `null` on anything other than
  18; ours warns and carries on, because a panel row reading `MVER 21` names the
  problem where a null just makes the map vanish. The guard's *point* is right:
  the version is what licenses trusting MAIN's layout.
- **The degenerate-placement fact**, written into the `GlobalWmo` doc comment so
  stage 3 starts from it instead of rediscovering it.

### What was deliberately not adopted

- **`Flags`, `NameSet`, `Padding`, `UniqueId` on the MODF record.** SuperUI
  captures all four; measured, all four are zero (or -1) on all 21 maps. Fields
  that are constant across the entire dataset are not data.
- **`KnownDungeonAliases`** — SuperUI needs `Gnomeragon` / `Gnomeregan` /
  `GnomereganInstance` and `BlackfathomDeeps` as fallback folder names. We read
  the directory out of `Map.dbc`'s Directory column, which resolved all 44 maps
  on the first try, so there is nothing to alias. **If a map ever fails to
  resolve here, the bug is in the DBC read, not in the folder name** — do not
  reach for an alias table.

### What it independently confirms

SuperUI's own comment lists the terrain-based instances it had to route
elsewhere: *"Deadmines, Shadowfang, Stratholme, BWL, Scholomance, Scarlet
Monastery, Zul'Farrak, Razorfen Kraul/Downs, Zul'Gurub, AQ20, AQ40,
Naxxramas"*. That is §1's terrain column, arrived at independently by a project
that had to make them render. **§1's headline finding is corroborated, not just
measured.**

Its curated `KnownDungeons` list is also the right shape for stage 2's travel
menu: 13 global-WMO maps, excluding `test`, `Test`, `Collin`, `StormwindPrison`,
the `<unused>` `Monastery` (id 44), `DeeprunTram` and the two PVP barracks —
exactly the exclusions §11 argues for. It is recorded here rather than hardcoded,
because the panel lists everything and the *menu* is what needs curating.

### The one thing it does NOT settle

**The col/row convention.** SuperUI stores `TileExists[y, x]` from MAIN index
`y * 64 + x`, which matches ours — but its `KnownDungeons` list covers only
global-WMO maps, and those have no tiles at all. Its terrain instances went down
a separate `_terrainPresets` path that is not in this file. So the axis question
is still answered only by our own trace through `AdtCache.Get(col, row)` ->
`ReadFromMpq(gridX: row, gridY: col)` -> `{map}_{col}_{row}.adt`, and §7 step 3
(Deadmines terrain appearing where it should) is still the test that proves it.


## 13. Stage 1 result — verified 2026-07-25

The panel's `Dump to console` was captured from a running client and diffed
row-by-row against §11's reference table.

**44 rows out of 44 identical. Zero mismatches, no extra ids, no missing ids.**

That settles §7's two stage-1 steps:

1. 44 maps, and Deadmines 36 tiles col 30..35 row 30..35, Shadowfang 25 tiles
   col 25..29 row 30..34, Scarlet Monastery (`MonasteryInstances`) 36 tiles col
   28..33 row 27..32, Razorfen Kraul 6 tiles col 27..29 row 27..28 — all exactly
   as printed in §1.
2. Wailing Caverns reads `global WMO` and
   `world\wmo\dungeon\kl_wailing\wailingcaverns_instance.wmo`.

**What this actually proves.** `Formats/WdtReader.cs` and `MapTable` agree with
`tools/mpqpeek` on every byte either of them reads, and the two were written
against the archives independently — the Python before the C# existed. The
handbook's rule is that the C# wins any disagreement; there was none to
adjudicate. §1 is now a measurement confirmed twice, not a claim.

`development` still prints centre tile `[31,1]`, which has no ADT. The console
dump does not show `SpawnTile`; the panel's expanded row does, in amber. That is
the one row where stage 2 must not use the centre.

**What it does not prove:** the col/row convention. Every number here comes from
the same MAIN read on both sides, so a transposition would agree with itself.
Deadmines terrain appearing where it belongs (§7 step 3) is still the only test
that can catch it — and it is the first thing stage 2 will do.


## 14. Stage 2 — travel to terrain maps (built 2026-07-25, unrun)

### 14.1 What was missing, and what closed it

H2 said three of the five per-map clears did not exist. Review of the built code
found **two more nobody had listed**, and one of them is H4's failure arriving
through a door H4 was not watching.

| Per-map state | Before | Now |
|---|---|---|
| ADT cache's map directory | fixed at construction | `AdtCache.SetMap` |
| resident terrain tiles | `SetResidency` only moves the ring | `TerrainRenderer.UnloadAll` |
| built liquid meshes | **not listed in H2** | `LiquidRenderer.UnloadAll` |
| WMO + doodad placements | `ResetPlacements` existed | `WmoRenderer.ResetForMapChange` adds the ring state |
| collision world | `LoadCollision` rebuilds it | plus the async-build generation bump below |
| **in-flight async collision build** | **not listed in H2** | `_collisionGeneration++` in the teardown |
| **background discovery queue** | **not listed in H2** | cleared in the teardown |

**The tile-key trap is the reason three of these are needed at all.** Tile keys
are `(col, row)` on a 64x64 grid *every map shares*. Deadmines occupies
col 30..35 row 30..35, and Azeroth has those tiles too. So `SetResidency`,
`LiquidRenderer.LoadForTiles` and the ADT cache all *keep* whatever is already
under a wanted key — and across a map boundary that means Elwynn hillside and
Elwynn river surface rendering inside the dungeon, **with nothing logged**.
Clearing is not an optimisation here, it is the correctness condition.

**The async collision race is the one worth remembering.**
`BeginCollisionBuild` snapshots placements onto a worker and stamps them with
`_collisionGeneration`; `AcceptReadyCollision` installs the result if the stamp
still matches. Setting `_collision = null` does not stop a build already in
flight. A tile crossing that started a second before the travel would therefore
land the **old map's BVH** on the new map one frame after arrival, and the player
would be colliding with Elwynn inside the Deadmines. Bumping the generation is
what makes that result get dropped. H4 said *"WMO collision must be resident
before the player is placed, not after"* and the ordering in the code obeys that
— `LoadCollision()` is synchronous and runs before `Teleport`. This defect got
past that guard entirely, because it arrives a frame later.

### 14.2 Design as built

- **H1 held.** There is no `if (isDungeon)` anywhere in the travel path. The
  only branch is `wdt.UsesGlobalWmo`, and it is a *refusal* (stage 3), not a
  second code path. Azeroth and Deadmines travel through identical code.
- **H2 held.** `Load()` is not re-run. The refill is the same sequence, in the
  same order, that `UpdateWorldResidency` runs on a tile crossing — deliberately,
  because a second refill path is a second thing to keep correct.
- **H3 held.** Arrival is `WdtFile.SpawnTile` centre, dropped onto sampled
  terrain height, and the UI labels it *"arrival point, not the real entrance"*.
  No coordinates were hand-authored.
- **The current map is `_config.Start.Map` / `.MapName`, mutated in place.** That
  is what makes exterior lighting, vantages, scene dumps, hitch records and the
  vmap collision loader all follow along without a second notion of "which map
  are we on". Nothing writes `client-config.json` back, so this is memory-only.

### 14.3 Failure handling (§9)

Every check that can be made before the teardown is: world loaded, not a
global-WMO map, tile count non-zero. After the teardown two things can still go
wrong, and both now unwind:

- **Zero terrain tiles loaded** — logged, then `RestoreAfterFailedTravel` puts
  the old map back. Inline, not a recursive `TravelTo`, because recursing into
  the same code to handle "the reload produced nothing" is how one bad map
  becomes an infinite loop.
- **An exception mid-refill** — the `catch` now restores as well. Before review
  it only logged, which would have left the config naming the new map while the
  player stood at the old map's coordinates with no collision; the next frame's
  `UpdateWorldResidency` would have re-homed around those coordinates and
  dropped them through the world, with no Return button on a first travel.

### 14.4 What to watch on the first run

1. **Deadmines terrain must appear where it belongs.** This is the col/row
   convention test §13 said was still owed. Transposed terrain means `col` and
   `row` are swapped in `WdtReader.ReadMain`, and §1 is the reference.
2. **`NO GROUND` must never appear on arrival** (H4).
3. **The trip back must leave Northshire identical.** Compare a scene dump
   either side.
4. **Ten round trips**, watching the GL object count and the managed heap. The
   model, texture and GPU-buffer caches are deliberately kept across maps —
   they are keyed by file path and a WMO file is the same file whichever map
   places it — so the heap should plateau, not climb.
5. **`[adt] map -> X, dropped N cached tile(s)`** should appear on every travel.
   Its absence means `SetMap` early-returned on a name compare and the cache is
   about to serve the wrong world.


## 15. Stage 2b — portals (built 2026-07-25, unrun)

### 15.1 H3 was wrong, and the correction matters

H3 said the entrance destinations *"live in the server's `areatrigger_teleport`
table, which we do not have"*, and built a derived spawn instead. **This project
talks to a VMaNGOS server. We always had it — it just was not in the repo.**
Nico dumped it in about ten seconds.

The cost of that mistake was not wasted work, it was the wrong shape of work: a
derived arrival point put the player 469 yards from the Deadmines with every
doodad distance-culled, when the real answer — walk in through the door, like
the game does — was one query away. **"We do not have it" deserved a check, not
a design.** Ruled out on the way to the correction, so nobody re-treads it:
`AreaPOI.dbc` is landmarks ("Echo Ridge Mine", "Sentinel Hill"), and pfQuest's
`areatrigger.lua` carries only map-pin percentages.

### 15.2 The two halves

| Source | Gives | Where |
|---|---|---|
| `AreaTrigger.dbc` | 432 trigger VOLUMES — sphere or oriented box, both sides of every portal | `AreaTriggerTable` in `Formats/DbcReader.cs` |
| `areatrigger_teleport` | where each one sends you: target map, position, facing | `Formats/AreaTriggerTeleport.cs`, from `areatrigger_teleport.tsv` |

Most of the 432 are quest and script triggers. **The join is what turns the
table into a set of doorways** — a trigger with no teleport row is not a portal.

The key is `(id, patch)`, not `id`. VMaNGOS gates content by patch and six of
Dire Maul's entrances appear twice: patch 0 with *"You Shall Not Pass!"* at level
61, patch 1 with the real level-45 requirement. 1.12.1 is the last vanilla patch,
so the highest patch row wins. Taking the first would lock Dire Maul at level 61
forever — a bug that reads as content rather than as parsing.

### 15.3 The entrance is derived from the data, not authored

"Go to entrance" stands you where the dungeon's **paired exit** drops you, facing
back at the door. That spot is a legal, walkable position just outside the
entrance, authored by Blizzard, and it costs us no geometry at all.

Pairing is geometric, never by name: for an entrance E on map A leading to map B,
the paired exit is the trigger on B whose destination is nearest E's own volume.
For Deadmines that picks exit 119 (9.4 yd from entrance 78) over the Back Exit
121 (173 yd) — which is also what the names say, but the names are VMaNGOS's
prose and nothing depends on them.

### 15.4 The latch, and why it is not optional

Vanilla drops you close enough to the return portal that a naive volume test
bounces: Deadmines' entrance lands you **8.2 yards** from exit trigger 119, whose
radius is **6**. Outside it — but only just, and one step is an infinite loop.

So every arrival latches the trigger it lands in, and the latch clears only when
the player leaves every volume. Review caught that the latch was being set on the
portal path alone, which made the Return button unusable for any dungeon entered
through a portal: the return trip puts you back inside the entrance volume, the
stale latch names a trigger on the map you just left, and the next frame sends
you straight back in. It is set inside `TravelTo` now, so every arrival is
covered.

### 15.5 What is deliberately not enforced

`required_level` is read and displayed, never checked. There is no character
level in this client yet, and refusing a portal on a value we cannot evaluate
would make the feature untestable. The message is shown so it is obvious what
vanilla would have said.

Portals into global-WMO maps — the Stockade (34), Deeprun Tram (369) — are
refused, because that is stage 3. Those are real teleport rows, so the refusal is
repeated on the exact-position path rather than assumed unreachable.

### 15.6 What to watch on the first run

1. `[teleport] N destination(s) from areatrigger_teleport.tsv` at startup. Absent
   means the file is not at the repo root and portals are off.
2. **Go to entrance -> Deadmines.** You should be standing in Moonbrook facing
   the mineshaft. Walk forward: `[portal] entered trigger 78 'Deadmines -
   Entrance' -> map 36` and you are inside.
3. **Walk back out.** The exit should return you to Moonbrook, and you must not
   immediately bounce back in — that is §15.4's latch doing its job.
4. **Return button after a portal entry.** Must not ping-pong.
5. Walking into the Stockade portal must log a refusal, not tear the world down.


## 16. Stage 2b validated against the data before it was ever run

`areatrigger_teleport.tsv` landed 2026-07-25 (121 rows out of Nico's VMaNGOS
world DB). The join was then checked in Python, reimplementing exactly what the
C# does, **before the client was built** — the same move that settled the
nine-slice in `SYSTEM_SETTINGS_UI`: when you cannot run the renderer, run the
algorithm somewhere else.

### 16.1 Parsing

```
115 destination(s), 6 superseded by a later patch, 0 row(s) dropped
Dire Maul 3183 -> patch 1, level 45          (the level-61 "You Shall Not Pass!" row lost)
teleports with NO AreaTrigger.dbc volume: 0
```

Every teleport row has a matching DBC volume, so the join is total — there is no
destination we cannot place in the world, and no doorway we know about that has
nowhere to send you. The `(id, patch)` dedup does what §15.2 says: all six
Dire Maul duplicates collapse to the patch-1 row.

### 16.2 The latch

No destination in the entire table lands inside a *different* portal's volume.
So the latch is not load-bearing for arrival — but it still is for play, because
Deadmines' entrance drops you 8.2 yards from exit trigger 119 whose radius is 6:
outside by 2.2 yards, and one step back through the door re-enters it. It is also
what makes `Travel straight in` safe, since that arrives at a trigger *centre* by
construction.

### 16.3 The paired-exit rule, tested on every dungeon

This is the part that was most likely to be quietly wrong, and it is right in all
twelve cases:

```
Deadmines         Deadmines - Entrance                  exit  119   9.4 yd  outside  dot +1.00
Shadowfang        Shadowfang Keep - Entrance            exit  194   9.5 yd  outside  dot +1.00
ScarletMonastery  Scarlet Monastery Graveyard - Entr..  exit  602   9.6 yd  outside  dot +1.00
ScarletMonastery  Scarlet Monastery Cathedral - Entr..  exit  604  10.5 yd  outside  dot +1.00
ScarletMonastery  Scarlet Monastery Armory - Entrance   exit  606   8.7 yd  outside  dot +1.00
ScarletMonastery  Scarlet Monastery Library - Entrance  exit  608  10.4 yd  outside  dot +1.00
RazorfenKraul     Razorfen Kraul - Entrance             exit  242  10.6 yd  outside  dot +0.99
WailingCaverns    Wailing Caverns - Entrance            exit  226  15.9 yd  outside  dot +1.00
Gnomeregan        Gnomeregan - Entrance                 exit  322  11.8 yd  outside  dot +0.99
Gnomeregan        Gnomeregan - Back Entrance            exit  525  14.8 yd  outside  dot +1.00
RazorfenDowns     Razorfen Downs - Entrance             exit  444  14.3 yd  outside  dot +0.96
Zul'Farrak        Zul'Farrak - Entrance                 exit  922  22.1 yd  outside  dot +1.00
```

`dot` is the cosine between the facing we set (`exitOrientation + pi`) and the
direction from the standing spot to the doorway. **+1.00 means looking straight
at it**, so walking forward walks in. Every stand position is 8.7–22.1 yards out
and outside the volume, so nothing fires on arrival.

**Scarlet Monastery is the case that proves the rule.** It has four separate
doors within about sixty yards of each other, and the geometric pairing gave each
its own correct exit — Graveyard→602, Cathedral→604, Armory→606, Library→608.
A pairing that was even slightly loose would have crossed them, and the symptom
would have been arriving outside the wrong wing, which looks like nothing at all
until you walk in. Gnomeregan's two doors (main→322, back→525) likewise.

None of this makes the feature *run* — the client is still uncompiled — but it
does mean that if it fails on Nico's machine, the failure is in the C# or the
travel path, and **not** in the data, the join, the pairing or the facing.
