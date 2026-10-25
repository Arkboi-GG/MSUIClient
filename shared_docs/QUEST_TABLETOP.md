# Quest Tabletop — Creator Mode's raised-relief quest view (SPEC)

**Status: SPEC, unbuilt (2026-10-14).** Written in the MangosSuperUI session that built the web
Quest Atlas; the web page is the working sketch this view is ported from. Append a dated line here
whenever a phase's state changes (proven vs pending, like every doc in `shared_docs/`).

Companion docs: the web design guiderail `MangosSuperUI/QUEST_ATLAS.md` (repo root of the web app)
and `shared_docs/WORLD_BUILDER.md` (Creator Mode World Builder, World Content Packs, `wp_op`).

---

## 1. What the owner asked for

> "A swap in creator mode that renders what we drew here, this tabletop-esque view inside the
> client, so that we have access to the full effective GPU power to get a nice responsive tool."

The web Quest Atlas (`/QuestAtlas` in the web app) proved the idea: a zone as a **miniature on a
table** — our own raised relief painted with the minimap, a camera that sweeps over it like the map
shot in a film, quests as pieces on it, every data layer switchable like Google Maps. The web page
pays for every model over HTTP and has no creature models. The client already streams the real
terrain, WMOs, M2s and creatures from local MPQs on the GPU. This view is the same table, in the
client, at full speed, with the real world on it.

**It is:** a Creator Mode view (a swap, like the other Creator workspaces) for authors and
cartographers — read the quests of a zone in their place.
**It is not (in this build):** gameplay UI, a quest helper, or an editor. Editing on the board with
the World Builder tools is the stated *next* step (§9 P5) and the reason the view must stay inside
Creator Mode, not a reason to build it now.

**Division of labour with the web page (decided 2026-10-14):** the web page stays the reading and
authoring table for long text (Story/Progress/Completion/Voices), reward decisions and shareable
links. The client is the visual table. A compact card in the client links out to the full web card.
No further 3D investment goes into the web page (no web creature models).

---

## 2. Visual law (the look — port these numbers, then tune by eye with the owner)

| Element | Web sketch value | Notes |
|---|---|---|
| Relief exaggeration | default **2.2**, slider 1–5 | Height' = baseZ + (h − baseZ) · k. **k = 1 must be identical to normal rendering.** |
| Slab depth | 45 yd of "earth" under the board's lowest point | baseZ = min terrain on board − 45 (min with 0 if open sea is on the board) |
| Board | the zone's `WorldMapArea.dbc` rectangle (Left/Right = Y max/min, Top/Bottom = X max/min) | Terrain is **cut** at the rectangle; the cut face is a vertical "cut earth" skirt, vertex-coloured #6e5034 (top) → #24180f (bottom) |
| Table | large plane under the slab, dark walnut wood (#3a281a with grain), receives shadows | |
| Background / fog | #1a140e; fog near = 1.6·span, far = 4.5·span | No sky, no weather, no day/night on the table |
| Lighting | warm hemisphere (sky #fff0d8, ground #2a2016) + one warm directional sun (#ffe0b0) from the north-west, soft shadows fitted to the board | |
| Camera | perspective, **FOV 30°**, orbit around a ground target; polar angle ≤ 1.38 rad | narrow FOV is what sells "miniature" |
| Miniature lens | tilt-shift post pass: horizontal + vertical blur, focus band at screen y = 0.52, blur ≈ 2.2 px at the edges; toggleable | plus a light ACES tone map; keep it subtle |
| Models | **true scale**, riding the exaggerated ground: y = ground'(x,y) + (z − ground(x,y)) | a tower stands on the raised hill at its real height, never stretched |
| Water | per-chunk liquid level, translucent blue (#2f6f8f, α 0.72), exaggerated with the ground | |
| Opening shot | from (radius 2.4·span, polar 1.42, azimuth −1.2) sweep to (1.15·span, 0.82, 0.12) over **3.6 s**, ease-in-out-cubic | the "film pan" the owner liked |

**Input:** left-drag = pan along the ground plane, right-drag = orbit, wheel = dolly, Esc = clear
selection + fly back to overview. A click is a press-release under 5 px of travel. Any user input
cancels a running camera flight. Selecting something flies to frame it (1.3 s, same easing).

---

## 3. Data contract (all read-only HTTP GETs to the web app via `WebAppHttp`)

The web app already serves everything; the client never queries MySQL. All shapes are camelCase
JSON. Dictionary keys are entry ids as strings.

### 3.1 `GET /QuestAtlas/Zones`
`[{ areaId, mapId, name, team, quests, minLevel, maxLevel, hasMapRect }]` — top-level zones
(`area_template.zone_id = 0`) that own quests, subzone quests folded in. 73 zones on 2026-10-14.

### 3.2 `GET /QuestAtlas/Zone?areaId=40` — the zone bundle (the quest graph in one response)
```
zone:     { areaId, name, mapId, team, level, subAreas[], rect: {top,bottom,left,right} | null }
quests:   [{ id, patch, custom, title, level, minLevel, maxLevel, zoneOrSort, home, races, classes,
             type, method, questFlags, specialFlags, suggestedPlayers, limitTime,
             prev, next, nextInChain, exclusiveGroup, breadcrumbFor, srcItem, srcSpell,
             startScript, completeScript,
             text: { details, objectives, requestItems, offerReward, end },
             objectives: [{ kind: kill|object|item|explore|reputation, id, count, text, spell }],
             rewards: { xp, money, spell, spellCast, choice[{id,count}], items[{id,count}], rep[{faction,value}] },
             givers: [{ kind: npc|object, entry }], enders: [...] }]
stubs:    [{ id, title, level, zoneOrSort, zoneName, topZone, exclusiveGroup }]   // chain neighbours off this board
npcs:     { "<entry>": { entry, name, subname, levelMin, levelMax, faction, npcFlags, rank, spawnTotal,
                         spawns: [[map, x, y, z], ...] } }        // ≤300 spawns per entry
objects:  { "<entry>": { entry, name, type, spawnTotal, spawns: [[map,x,y,z], ...] } }
items:    { "<id>": { id, name, quality, itemClass, subclass, sources: [{ kind, entry, chance }] } }  // ≤25 sources
triggers: { "<id>": { id, name, map, x, y, z, radius, boxX, boxY } }
lines:    [{ script, phase: start|end, delay, command, targetType, targetParam, chatType, text, comments }]
greetings:[{ kind, entry, text }]
```
Membership: a quest is on the board if its `ZoneOrSort` is the zone or one of its subzones, **or**
one of its givers stands inside the rectangle (`home` = false for those "visitors"). Rows of
`(entry, patch)`-keyed tables are resolved to the highest patch. Reads the DB live, so World Pack
quests published a minute ago appear.

### 3.3 Not needed by the client, but available
`/QuestAtlas/Relief`, `/QuestAtlas/Objects`, `/QuestAtlas/Creatures` exist for the web page's own
relief, ADT placements and figurines. **The client must build these from its own MPQs and its own
object stream instead** — it has `patch-7.MPQ`, so pack-sculpted terrain (Gilneas) and pack
minimap tiles show correctly here, which the web page cannot do. `/QuestAtlas/Creatures` may still be
useful for the "everyone on the board" census if the client's own creature view is radius-limited.

### 3.4 Link-out
The web page honours `{web}/QuestAtlas#zone=<areaId>&quest=<questId>`. The client card's
"Open in web" button opens that URL in the system browser.

---

## 4. Rendering architecture — the far/near split (the real engineering problem)

The client's world streaming is tuned for a player's eye. The table needs the **whole zone** on
screen at once (Westfall's rectangle is ~20 ADT tiles; the Barrens ~250). Rendering every tile at
full detail will either crawl or leave blank edges. So:

- **Far layer — the compiled relief.** Per ADT tile a coarse 33×33 height grid (every 4th outer MCVT
  vertex, ~16.7 yd spacing) painted with that tile's minimap texture, plus per-chunk water level.
  Built once per tile from the client's own ADT reader and minimap catalog, cached for the session.
  This is the whole board and is cheap (Westfall ≈ 20 × 1,089 vertices).
- **Near layer — the real world.** Full terrain, WMOs, M2 doodads and creatures streamed in a radius
  around the **camera's ground target** (not the player), using the existing streaming. The near
  layer replaces the far layer underneath it; the seam must not show at k = 1 and should be hidden by
  the tilt-shift blur at k > 1.
- **Exaggeration** applies to both layers by the same rule (§2). For the near layer that is a uniform
  in the terrain vertex shader plus a per-instance y offset for models and units computed from the
  ground height under them. **Never** let exaggeration leak into collision, movement, possession,
  the server, or any gameplay state — it is a view transform only.
- **Board cut:** clip near- and far-layer geometry to the zone rectangle (clip plane or fragment
  discard) and draw the skirt + slab + table.
- **Gameplay isolation:** entering the view does not move the player or a possessed body, does not
  send movement or SUI packets, and leaving it restores the previous camera and streaming centre.

---

## 5. Overlays (port the web layer semantics exactly; draw them the client's way)

Pieces are built from the bundle (§3.2) after filters. "Place of" an entity = its first spawn on
the board. Up to 4 pieces per entity.

| Layer (default) | What | How it looks on the web |
|---|---|---|
| Quest givers `!` (on) / Turn-ins `?` (on) | one piece per giver/ender spawn; `!?` when both | glyph sprite on a thin post; gold = zone's own, pale = visitor, violet = custom (≥ 7,000,000) |
| Names (off) | NPC names | label over the piece; always on for the selection |
| Chain arcs (on) | for each link (prev → this, this → nextInChain, this → next > 0, breadcrumb dashed): arc from the **ender of the earlier quest** to the **giver of the later one** | quadratic Bézier, apex at max(y) + 0.28·distance; arrowhead at the receiver; same NPC → small loop above them; colour per chain component |
| Selected quest journey (on) | giver → centroid of each objective → ender | dashed warm-white arcs |
| Kill targets (off) / Item sources (off) / Objects to use (off) / Places to explore (on) | objective places for all visible quests; the selected quest's always shown, larger | dots: red / amber / blue; explore = violet ring at the trigger radius |
| Tint by level (off) | pin colour green → red over the zone's level range | |
| Lift by level (off) | post height grows with quest level — "height = level" | |
| Buildings, Trees & props (on) | the real ADT world | near layer |
| Creatures (on), critters (off) | the **real creature models**, true scale; optional figure-scale slider | the web uses pawns only because it has no creature models |
| Water (on), Miniature lens (on), Relief slider | §2 | |

**Objective centroid:** mean of the densest third of the objective's on-board points (one stray
spawn must not drag the stop into nowhere).
**Chain components:** union-find over the links among on-board quests; order inside a chain by
depth along links, then level, then id.
**Selection:** selecting a quest dims every unrelated piece to ~30 % and thickens its chain.
**Filters:** faction (from `RequiredRaces`: alliance mask 77, horde mask 178, 0 = both), level range
(`QuestLevel ≤ 0` means "scales with the player" and always passes), "filed in this zone only"
(`home`), class/race quests on/off, free-text search over title, id, NPC, target and item names.

**Picking:** clicking a quest piece with one quest selects it; with several, opens the NPC card.
Clicking a creature that is a quest NPC opens its card; any other creature shows "wanted by" — the
quests on the board that kill it or need an item it drops.

---

## 6. UI (Creator Mode — ImGui is allowed here; rule 5 of AGENTS.md is about gameplay UI)

- **Left panel:** zone picker (grouped Eastern Kingdoms / Kalimdor / Elsewhere, `name · min–max · Nq`,
  ✦ for custom zones), search, filters, quest list grouped by chain (chain header with its colour).
- **Layers popover** (top right): the §5 table as checkboxes + Relief and Figure sliders. Persist
  layer choices with the client's settings.
- **Compact card** (right): kicker (Quest id · zone), title, badges (level, faction, class/race,
  group/dungeon/raid, repeatable, timed, ours ✦, *no giver*/*no turn-in* warnings), From/To, the
  chain line (before → **this** → after, exclusive-group mates, off-board stubs that load their own
  zone), objectives with where, rewards; **Open in web** for the full text. The text tabs live on the
  web page; do not rebuild them here unless the owner asks.
- **Coordinates HUD** bottom-left (world x/y/z under the cursor) — authors use it constantly.
- Text tokens in any quest text: `$N/$C/$R` → ‹name›/‹class›/‹race›, `$Gmale:female;` → male/female,
  `$B` → line break.

---

## 7. Performance budget

- Target: **60 fps** on the owner's GPU with Westfall fully dressed (whole board in view), and
  smooth (≥ 30 fps) on the Barrens.
- Far layer: one draw per tile (or one merged mesh), minimap textures shared.
- Near layer radius: start at the client's normal streaming radius around the ground target and
  tune; use instancing for doodads and the existing LOD rules.
- Overlays: a few hundred pins, a few thousand objective dots — batch them; rebuild only on
  filter/layer/selection change, re-seat (positions only) on exaggeration change.
- Measure with the existing hitch recorder; record the numbers in §10.

---

## 8. Gotchas already paid for (from the web build)

- **MCNK origin order.** In 1.12 ADTs the MCNK header at 0x68 is **world X (north)** and 0x6C is
  **world Y (west)** — verified on `Azeroth_29_51.adt` (first chunk: −10133.3, 1600.0 = that tile's
  north-west corner). The web app's reader had the names swapped and every relief tile fell off its
  square. Check the client's reader the same way before trusting it for the far layer.
- **Tile law:** gx = floor(32 − X/533.333) (rows north → south), gy = floor(32 − Y/533.333)
  (columns west → east). ADT file `Map_{gy}_{gx}.adt`; minimap `map{gy}_{gx}` covers the same square
  with image-right = east, image-down = south.
- **WorldMapArea rectangles overlap neighbours.** Westfall's board pulls in 166 quests (levels 1–60)
  from Elwynn, Duskwood and Stormwind. That is why "filed in this zone only" exists; the owner has
  not yet decided whether it should default on (§11).
- **Zones without a WorldMapArea row** (dungeons, some custom zones): frame the board on the giver
  and ender spawns (+250 yd, at least one tile across).
- **`quest_template` / `creature_template` / `gameobject_template` key on `(entry, patch)`**; the
  bundle already resolves the highest patch.
- **WMOs are listed by every ADT they touch** — de-duplicate placements by model + rounded position.
- **Sizes relative to the board, not the camera:** web arcs and arrowheads sized from the board span
  turned into a giant teal arrow when zoomed in. Size overlays so they stay readable at both ends.
- **No hostile/friendly data in the bundle yet** (needs FactionTemplate); the web colours figurines
  by creature type. The client knows reactions natively — use that.

---

## 9. Phases (each ends with the owner looking at it)

- **P1 — The table.** Creator Mode swap; tabletop camera, FOV, controls, opening shot; far-layer
  relief from local ADTs + minimap for one zone; slab, skirt, table, water, lights, tilt-shift;
  exaggeration slider (far layer only). Gameplay isolation.
- **P2 — The real world on it.** Near-layer streaming around the ground target with exaggeration
  (terrain shader uniform + model/unit offsets), board cut, seam handling, buildings/props/creatures.
- **P3 — Quests.** Fetch the bundle; pins, posts, chain arcs, journey, objective dots and rings,
  filters, quest list, compact card, link-out; layers popover with persistence.
- **P4 — Polish to the owner's eye.** Labels, level tint/lift, critters, figure scale, performance
  pass with measured numbers.
- **P5 — (not in this build) Edit on the board** with the existing World Builder tools and `wp_op`
  documents, never raw SQL.

## 10. Acceptance (per phase; nothing counts until the owner has seen it)

1. `dotnet build` Debug **and** Release green; `interface-wire-check --shared-docs-only` and
   `--imgui-policy-only` green.
2. A scripted offline capture (follow the existing `MSUI_*_PROBE` pattern, e.g.
   `MSUI_TABLETOP_PROBE`) that opens the view on Westfall and Gilneas and saves screenshots at the
   overview and at a close framing — Gilneas must show its **pack-sculpted** terrain.
3. Exaggeration k = 1 matches the normal world render at the same camera (seam/regression guard).
4. Leaving the view returns the camera, streaming centre and controlled body exactly as before;
   no movement or SUI packet was sent while it was open (check the console log).
5. Frame times recorded with the hitch recorder for Westfall and the Barrens, written into §11's log.
6. P3: selecting "The People's Militia" (Westfall, Gryan Stoutmantle at Sentinel Hill) frames giver,
   objective places and turn-in, its chain lists its steps in order, and the same-NPC hand-offs show
   as loops over Gryan — matching the web page on the same quest.

## 11. Open questions for the owner / as-built log

- Should "filed in this zone only" default **on**?
- Creatures: true scale (realistic, tiny from the overview) or a default figure scale (> 1)?
- Should the far layer ever show creatures as markers when they are outside the near radius?

*(append dated as-built lines below)*

- **2026-10-14 — P1 (the table) built; Westfall proven, Gilneas BLOCKED, overview framing awaits the owner's eye.**
  Files: `Formats/ReliefTile.cs` (33×33 relief + water per tile, pure; places chunks by IndexX/IndexY and counts MCNK
  origins that agree), `Engine/UI/TabletopLaw.cs` (§2 numbers, board, orbit, easing, in world yards),
  `World/Tabletop/TabletopRenderer.cs` (relief painted with minimap tiles, skirt, walnut table, water, hemisphere + NW sun
  with a 2048² PCF shadow map, linear fog, tilt-shift + three.js ACES/sRGB output; exaggeration is a shader uniform),
  `GameLoop/CreatorMode/GameLoop.Creator.Tabletop.cs` (the swap: `CreatorPanel.Tabletop`, top-bar **Tabletop** button,
  own camera, OrbitControls port, opening shot, Esc → overview, Atlas-look panel with zone picker / relief / lens / water,
  coordinates HUD; settings `Creator.Tabletop`), `GameLoop/Dev/GameLoop.Tabletop.Probe.cs` (`MSUI_TABLETOP_PROBE`),
  `tools/tabletop/launch-tabletop.ps1`. **ADT reader fixed (§8):** `McnkChunk.BaseX/BaseY` were assigned swapped (0x68 went
  to BaseY); nothing read them. Now BaseX = 0x68 (north), BaseY = 0x6C (west); the probe reads Azeroth_29_51 chunk 0 as
  (−10133.3, 1600.0) and the relief compiler finds 256/256 per tile, 10752/10752 on Westfall. **Isolation:** while open the
  world is not drawn, `tabletopLocked` (Program.cs) zeroes every movement axis and skips the controller update, the table
  owns the mouse; the probe's close check: game camera, body, streaming centre and map unchanged, no packets (no session).
  Capture `p1-1`: `dumps/gameplay-tabletop-40-overview|close-*.png`. Open: (1) **Gilneas (7001)** has no WorldMapArea row
  on this client and `/QuestAtlas/Zone?areaId=7001` is 404 on the WSL web app; the local `patch-7.MPQ` is the WSL Road QA
  build (1 MB) — the Gilneas pack is not on WSL, so its sculpted terrain is not in the client's data. (2) Westfall's
  western tiles hold sea floor at about −524, so baseZ = −569 and the skirt stands ~1,300 yd at k 2.2; the overview
  (target on the slab base, as the web) puts the board in the top third. Both are owner questions, not guesses.
- **2026-10-14 (later) — P1 capture complete: Westfall and Gilneas, VERDICT PASS (`scratch/tabletop/logs/p1-2.log`).** Owner
  decisions: (1) Gilneas for the capture comes from the box's published patch-7 (one read-only GET of
  `http://192.168.0.2:5000/WorldPacks/Patch` into `scratch/tabletop/patch-7.box.MPQ`), mounted by the probe in its own
  process only (`MSUI_TABLETOP_PATCH7`, launcher `-Patch7`); the client's `GameData` stays the WSL build. That patch carries
  a WorldMapArea row for 7001 (X −3069..−1035, Y 157..3208), so Gilneas' board came from it: 33 land tiles + 2 sea, 33
  pack minimap tiles, 8448/8448 MCNK origins. (2) Zones without a WorldMapArea row get the bounding box of the MCNK chunks
  tagged with the zone or its subzones (`Formats/AdtChunkAreas.cs`, header-only scan of the map's WDT tiles, cached;
  `AreaTableCatalog.MapOf/ZoneMembers`) — built, **not yet exercised by a capture** (both zones have rows). (3) The slab
  sits 45 yd under the lowest land-or-water surface, not the sea floor (`ReliefTile.MinSurface`; the skirt runs from the
  water surface where the board edge crosses the sea): Westfall baseZ −569 → −52.3. Shots
  `dumps/gameplay-tabletop-{40,7001}-{overview,close}-20261014-1630*.png`. Close-up note: the far layer's 16.7 yd grid is
  visible at close range — P2's near layer is what replaces it there.
- **2026-10-14 (evening) — P2 (the real world on the table) built; Westfall + Gilneas VERDICT PASS (`scratch/tabletop/logs/p2-5.log`).**
  Near layer = `World/Tabletop/TabletopNearScene.cs`: a world of its own on the `PortalDestinationScene` pattern (own
  terrain/WMO/doodad/liquid/creature renderers + ADT cache, staged non-blocking load), a 3×3 tile ring around the camera's
  ground target (`Start.TileRadius`), re-centred when the target crosses a tile; the game's world, streaming centre,
  collision and body are never touched (close check unchanged). View transform = `World/TabletopViewTransform.cs`, set
  only on the tabletop's renderer instances: terrain.vert / water.vert take `z' = z + (k−1)(z − baseZ)` (identity at k = 1;
  `uReliefOn` is 0 there and in every game program), WMO and M2 placements get the same lift of the ground under them on
  the CPU (`ModelLift`; a WMO's furniture rides with its building), creatures likewise; the board cut is four inward
  planes (existing `uWorldClipPlane` + `uBoardClip1..3`, GL_CLIP_DISTANCE0..3). The far layer hides the tiles (and their
  water) the near layer has loaded; the board shader now tone-maps per fragment so the near renderers' display colours
  share one target; near liquids wear the table's water colour (`TabletopLaw.ShownWater`). Creatures: spawns from the WSL
  web census `GET /QuestAtlas/Creatures` for the board (Westfall 2632, Gilneas 54 — the WSL DB has no Gilneas pack NPCs),
  models via the local `creator-creatures.tsv` (entry → display id; `CreatorCreatureTable.TryGet`), view-only synthetic
  units never added to the game's entity store, true scale. Counts: Westfall overview ring [29,52] 90 buildings / 7228
  props, Sentinel Hill ring [30,51] 45 / 13103, 1343 creatures in the ring; Gilneas [28,36] 34 / 4891. Fixed on the way:
  the camera target now keeps its TRUE height and is exaggerated per frame (moving the relief slider while framed on a
  hill had left the camera inside it). Shots `dumps/gameplay-tabletop-{40,7001}-{overview,close,close-k1}-20261014-190*`
  and `-40-figures-` (the Sentinel Hill tower on the raised hill, guards at the gate). k = 1 reference: the shader path is
  the game's own (relief off, lift 0); lighting differs by design (the table's warm sun, not the zone's sky). For the
  owner's eye (P4): near ground reads greyer than the minimap paint; some far-distance trees look lime; the near ring
  covers 9 of Westfall's 42 tiles at the overview; true-scale creatures are specks beyond a few hundred yards.
- **2026-10-21 — P3 (quests) built; §10.6 PASS on Westfall (`scratch/tabletop/logs/p3-1.log` both zones, `p3-5.log` final
  Westfall).** `Net/QuestAtlasClient.cs` (read-only GETs of `/QuestAtlas/Zones` and `/QuestAtlas/Zone?areaId=`, the §3 DTOs),
  `World/Tabletop/QuestBoard.cs` (port of model.js: on-board spawns, ≤ 4 pieces per entity, objective targets, faction /
  class-race classification, search text, links → union-find chains ordered by depth, level, id, golden-angle palette,
  densest-third centroid, "wanted by"), `GameLoop/CreatorMode/GameLoop.Creator.Tabletop.Quests.cs` (pieces.js + index.js:
  pins on posts coloured custom > home > visitor or by level, focus/dim to 28 %, chain arcs ender → giver as quadratic
  Béziers with apex max + 0.28·d + 20 yd and arrowheads, breadcrumbs dashed, same-NPC loops drawn above the glyph, the
  selected quest's journey dashed warm white, objective dots and explore rings, labels; zone picker from the web list
  grouped EK / Kalimdor / Elsewhere; search, faction, level, "filed in this zone only" (off, §11 still open), class/race;
  the list grouped by chain; compact card with From/To, chain before → this → after incl. off-board stubs that load their
  zone, exclusive-group mates, objectives with where, rewards, **Open in web**; NPC and creature cards ("wanted by"); hover
  tooltips; the layers popover (every §5 layer + Relief) persisted in `Creator.Tabletop`; Esc clears + overview).
  Overlays are draw-list shapes over the 3D table, hidden behind hills by a sight-line test against the shown relief
  (instead of the web's depth test). Boards without a map row now try the bundle's rect, then givers/enders (+250 yd,
  ≥ 1 tile), then the ADT chunk scan. Selection framing centres the box in the gap between the sidebar and the card and
  pulls back in proportion to the gap (the web frames on the whole canvas). Westfall: 166 quests, 24 chains, 104 links;
  Militia 12 → 13 → 14 by depth, 5 loops over Gryan (the chain's two + his other chains), 4 focus points. Gilneas: the WSL
  web app has no bundle for 7001 (404), so its board stays quest-free. Shots `dumps/gameplay-tabletop-40-{quests,militia}-20261021-21*`.
- **2026-10-21 (later) — zone sweep + two owner decisions.** Sweeps `sweep-1..3` (VERDICT PASS): The Barrens, Durotar (Kalimdor),
  Stranglethorn Vale, Elwynn Forest, Blackrock Depths, The Deadmines, Molten Core, Blackrock Mountain - plus Westfall and
  Gilneas before. Open-world zones build cleanly (Barrens 280 tiles / 65536 MCNK origins agree). Findings and decisions:
  (1) **Dungeons/raids** (24 of the web's 72 zones, none with a map rectangle) framed on their givers gave useless boards
  (Blackrock Depths spread over half the continent; the Deadmines landed on its instance map with no pins). Owner: their
  table is the **region around the entrance** outside - `TryTabletopEntranceBoard`: the instance is the area's map or the
  Map.dbc instance of that name, its entrance from `areatrigger_teleport.tsv` + AreaTrigger.dbc (`EntrancesTo`), followed
  outward through another instance (Molten Core → Blackrock Depths → Blackrock Mountain), a ±800 yd square
  (`TabletopLaw.EntranceHalfYards`). Order for zones without a map rectangle is now: entrance → bundle rectangle → ADT
  chunk scan → givers/enders (Blackrock Mountain lands there: its terrain belongs to its neighbours). (2) **"Filed in this
  zone only" starts ON** (§11 answered): the Barrens' wide WorldMapArea rectangle otherwise pulls in 815 quests (104 its own).
  P4 notes from the sweep: a busy NPC's same-NPC loops line up in a long row (cap them); pins are large at overview.
