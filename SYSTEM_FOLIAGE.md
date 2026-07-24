# System — Foliage / Ground Effects

**The grass tufts, ferns, flowers and pebbles vanilla scatters on the terrain
near the camera.** One of the per-system docs the handbook indexes (see
PROJECT_HANDBOOK.md §1.2). Read this plus the handbook's cross-cutting ground
truth (§3.1 coordinates, §8.5 shader ASCII rule, §11 working agreements) before
touching foliage. You should not need the rest of the handbook.

Version: Draft 1 — 2026-07-24.

Owner files: `World/FoliageRenderer.cs`, `Shaders/grass.vert`,
`Shaders/grass.frag`, `GroundEffectDoodadTable` / `GroundEffectTextureTable` in
`Formats/DbcReader.cs`, the MCNK cell-layer / no-doodad / hole accessors in
`Formats/AdtTerrainReader.cs`, and the Foliage panel in `Program.cs`.

---

## 0. The bar

Foliage is not decoration scattered by taste. Vanilla has a **complete authored
data chain** that says which clutter appears on which square of ground, and the
bar is to follow it rather than to invent a plausible-looking distribution.

The single clearest test of whether it is right: **grass must not creep onto the
Northshire cobblestone.** Every wrong version of this system fails that test, and
the two mechanisms that pass it (§1.2, §1.3) are both hand-authored data the
artists baked in, not anything derivable from the alpha maps.

---

## 1. What is implemented now

### 1.1 The authentic data chain

```
MCLY.EffectId                     (per texture layer, per MCNK chunk)
  -> GroundEffectTexture.dbc      (up to 4 doodad IDs + weights + a density)
     -> GroundEffectDoodad.dbc    (the grass M2 model path)
```

For each ~4-yard terrain cell near the camera: find the cell's texture layer,
read that layer's ground-effect id, look up the recipe, and scatter
`density`-many little M2s at random position, yaw and scale on the terrain
surface.

Placement is **deterministic per cell** —
`new Random(HashCode.Combine(col, row, chunk.IndexX, chunk.IndexY, cx, cy))` —
so the same tuft lands in the same place every time you walk back. Grass that
reshuffles when you turn around is the giveaway that this seeding got broken.

### 1.2 The MCNK cell layer map (`0x40`) — not alpha sampling

Each cell's texture layer is read from the **8x8 map the artists baked into the
MCNK header**, not guessed by sampling the alpha maps at the cell centre.

This is what the retail client does, and it is **the whole reason grass never
creeps onto the Northshire cobblestone**: those cells name the road layer, whose
recipe holds one pebble and no grass at all. An alpha-map guess gets this wrong
at every road edge, because the blend there is genuinely ambiguous and the
authored answer is not.

`UseCellLayerMap` (default on) switches back to the alpha-sampling guess for A/B.

### 1.3 The no-doodad mask (`0x50`)

A hand-authored per-cell "place nothing here" bitmap. In `Azeroth_32_48`
(Northshire) it covers **303 cells, 195 of them road** — the second half of why
the road reads clean.

`UseNoDoodadMask` (default on); `MaskedCells` reports how many cells it skipped
on the last scatter.

### 1.4 Holes

Cells the MCNK holes field cut away have no terrain under them — they are the
doorway the artists carved so a dungeon WMO's entrance is reachable. Scattering
there is what puts **shrubs growing through a mine's wooden beams**.

`SkipHoles` (default on) rejects the whole cell up front; `HoleCells` reports the
count. `TerrainRenderer.SampleHeight` also refuses holes now, so this is belt and
braces — but rejecting early is cheaper and gives the HUD something to read.

### 1.5 Per-kind curation

`FoliageKind` — Grass, Flower, Bush, Rock, Plant, Mushroom, Other — is derived
from the model's **name code**, the 2–3 letters just before the trailing number:
`ElwGra01` -> Grass, `ElwRoc01` -> Rock, `ApkBus01` -> Bush. Zone-prefixed
variants carrying an extra letter (Durotar's `DurIRo01`) still land on the right
3-letter tail.

This exists because **retail hand-curated which clutter appeared where — most
visibly keeping road pebbles out of the starting zones — and the raw DBCs do not
encode that curation.** Per-kind enable plus a per-kind keep-probability lets it
be reproduced by hand instead of scattering everything the data technically
allows. The kind is rolled *before* `ResolveModel`, so a hidden kind costs
nothing to load.

### 1.6 Scatter throttling and residency

Re-scatter only happens once the camera has moved `RescatterDistance` (8 yd) from
the last scatter point. Whole chunks are rejected when their centre is outside
`Radius + 24` yd. `MaxInstances` (24,000) is the hard ceiling.

### 1.7 Rendering

Reuses the doodad pipeline exactly: one interleaved VBO (pos3 + normal3 + uv2)
per model, a per-instance `mat4` as four `vec4` attributes at **locations 3..6,
divisor 1**, drawn with `DrawElementsInstanced`. Positions are **camera-relative
for float precision**.

Grass has its own shader pair (`grass.vert` / `grass.frag`) for wind sway,
distance fade and alpha cutout. It is a fork of the doodad path and should stay
one — the sway is not wanted on furniture.

---

## 2. Ground truth — do not re-derive

| Fact | Value |
|---|---|
| Chain | `MCLY.EffectId` -> `GroundEffectTexture.dbc` -> `GroundEffectDoodad.dbc` |
| Recipe contents | up to 4 doodad ids + weights + one density |
| Cell grid | 8x8 cells per MCNK chunk, cell = `AdtTerrainReader.CELL_SIZE` (~4.17 yd) |
| MCNK cell layer map | header `0x40`, **16 bytes, 2 bits per cell**, 8x8 grid, authored. Wiki calls it `ReallyLowQualityTextureingMap` — misleading; it is the ground-effect layer index |
| MCNK no-doodad mask | header `0x50`, **8 bytes = one `uint64`, 1 bit per cell**, same 8x8 grid, authored. Set means "place nothing here" |
| Tile origin | `originX = (32 - row) * 533.33333`, `originY = (32 - col) * 533.33333` |
| Chunk origin | `chunkX = originX - chunk.IndexY * 8 * cell` (note the **swap**: X uses IndexY) |
| Cell origin | `cellX = chunkX - cy * cell`, `cellY = chunkY - cx * cell` (swapped again) |
| Model space | M2 is **Y-up**; `YUpToZUp = (x, y, z) -> (x, -z, y)` |
| Scatter seed | `HashCode.Combine(col, row, IndexX, IndexY, cx, cy)` — deterministic |
| Instance attributes | mat4 at locations 3..6, divisor 1 |
| Name-code -> kind | see `FoliageRenderer.Classify` |

The X/Y index swaps in the origin maths are correct and load-bearing. They look
like typos. They are not. Do not "fix" them without reproducing the Northshire
road test first.

---

## 3. Files and responsibilities

| File | Responsibility |
|---|---|
| `World/FoliageRenderer.cs` | The whole system: DBC load, `Scatter`, `Classify`, `Render`, every knob |
| `Formats/DbcReader.cs` | `GroundEffectDoodadTable`, `GroundEffectTextureTable` |
| `Formats/AdtTerrainReader.cs` | `NoGroundEffect(cx,cy)`, `IsHole(cx,cy)`, `GroundEffectLayer(cx,cy)`, `HasGroundEffectLayerMap` |
| `World/TerrainRenderer.cs` | `SampleHeight` — the surface foliage sits on; refuses holes |
| `Shaders/grass.vert` / `.frag` | Wind sway, distance fade, alpha cutout |
| `Program.cs` | Foliage panel: Coverage, Placement rules (1.12), Types, Wind and fade, Look |

---

## 4. Tuning — the Foliage panel

**Coverage** — `Radius` (45 yd), `DensityScale` (0.5, multiplies the DBC
density), `MaxPerCell` (6), `MaxInstances` (24,000), `RescatterDistance` (8 yd).

**Placement rules (1.12)** — `UseCellLayerMap`, `UseNoDoodadMask`, `SkipHoles`,
all default on, each with its skipped-cell readout. **These three are the
authenticity switches.** Turning any of them off is a diagnostic, not a setting.

**Types** — per-kind enable and per-kind density, plus a live instance count per
kind. This is where the retail curation gets reproduced.

**Wind and fade** — `WindStrength` (0.06), `WindSpeed` (1.4), `FadeStart` (30),
`FadeEnd` (45).

**Look** — `Scale` (1.0), `ScaleJitter` (±0.25), `AlphaCutoff` (0.4),
`Brightness` (1.0).

Any coverage change calls `ForceRescatter()`, so edits take effect on the next
frame rather than after you walk 8 yards.

---

## 5. Not done — the honest ceiling

- **Foliage takes pure daylight.** It is not lit by MOCV or MODD, which is
  correct — ground effects are outdoor-only in vanilla — but it does mean
  foliage under an overhang does not darken.
- **No per-texture or per-zone override table.** The per-kind toggles are a
  blunt global stand-in for retail's actual hand curation. A real override table
  keyed by texture or zone was offered and not taken; it is the obvious next
  step if the curation ever needs to differ between zones.
- **No LOD or impostor** for distant foliage — it simply fades out at
  `FadeEnd`.
- **Density is uniform within a cell.** Vanilla's clumping is not reproduced.
