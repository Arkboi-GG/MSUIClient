# System — Foliage / Ground Effects

**The grass tufts, ferns, flowers and pebbles vanilla scatters on the terrain
near the camera.** One of the per-system docs the handbook indexes (see
PROJECT_HANDBOOK.md §1.2). Read this plus the handbook's cross-cutting ground
truth (§3.1 coordinates, §8.5 shader ASCII rule, §11 working agreements) before
touching foliage. You should not need the rest of the handbook.

Version: Draft 2 — 2026-07-26 (adds the LIQUID gate — grass was growing along the
bed of the Elwynn river because this renderer had no idea liquid existed. See §6.)
Previous: Draft 1 — 2026-07-24.

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

### 1.1a The reshuffle bug — the seed was never the problem (fixed 2026-07-25)

Nico: *"its redrawing into different spots as I'm moving."* He was right, and the
paragraph above points at the wrong culprit. **The seed was always correct. The
stream POSITION was not.**

The draws used to be interleaved with the rejection tests:

```csharp
float px = ... rng.NextDouble() ...          // 2 draws
if (offRadius) continue;                     // <-- CAMERA-DEPENDENT, skips the rest
float? h = terrain.SampleHeight(px, py);
if (h is null) continue;
string modelPath = PickWeighted(..., rng);   // 1 draw
...                rng.NextDouble()          // keep roll
float yaw        = rng.NextDouble()          // yaw
float s          = rng.NextDouble()          // scale
```

The radius test depends on **where the camera is**. When it rejected tuft `i`,
the loop skipped four further draws — so the stream position at tuft `i+1`
depended on the camera, and **every remaining tuft in that cell got a new
position, model, rotation and scale on every re-scatter.**

Cells fully inside the radius never showed it. Cells straddling the radius edge
reshuffled constantly — and as you walk, **every cell takes its turn at the
edge**. That is why it read as continuous churn rather than an occasional glitch.

**Fix:** draw every random value for a tuft *before* any test, then reject using
values already drawn. The stream position now depends only on `(cell, i)`.

> **The rule this leaves behind matters more than the fix: no `continue` may
> appear between the first and last `rng` call of a placement loop.** A
> deterministic seed buys nothing if consumption is conditional. Any future
> filter goes below the draws, never among them.

Two things came free with the reordering:

- **The rejections are now cheapest-first.** `SampleHeight` used to run before
  the per-kind filter, so every in-radius candidate paid a height lookup even
  when its kind was switched off and it was about to be discarded.
- **`ResolveModel` moved above `SampleHeight`** for the same reason.

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

**A re-scatter is a full rebuild — every resident tile, every chunk in range,
every cell — not an incremental update.** At walking pace 8 yd arrives roughly
once a second, so this fires about once a second while moving and does nothing in
between. That schedule is why it must not be averaged with the draw (§1.7a).

`adts.TryPeek` is used, never `adts.Get`. `Get` blocks on a pending parse
(`return pending.GetAwaiter().GetResult();`) and this runs inside `Render` on the
main thread — the same call cost the WMO ring 61 ms
(`SYSTEM_STREAMING.md` §3.1, which listed `FoliageRenderer:270` as the identical
latent bug). Unparsed tiles are counted in `DeferredTiles` and the throttle is
cleared so the next frame retries, rather than leaving a bald patch until the
camera has moved another 8 yd.

> `TryPeek` returning **true with a null adt** means "known to have no ADT" —
> an answer, not a miss. It must not set the retry flag, or ocean tiles spin
> forever.

### 1.7 Rendering

Reuses the doodad pipeline exactly: one interleaved VBO (pos3 + normal3 + uv2)
per model, a per-instance `mat4` as four `vec4` attributes at **locations 3..6,
divisor 1**, drawn with `DrawElementsInstanced`. Positions are **camera-relative
for float precision**.

Grass has its own shader pair (`grass.vert` / `grass.frag`) for wind sway,
distance fade and alpha cutout. It is a fork of the doodad path and should stay
one — the sway is not wanted on furniture.

### 1.7a Cost — scatter and draw are measured apart

`Program.cs` timed `Scatter` and `Render` together as one
`_foliageRenderMilliseconds`. They are unrelated jobs on **different schedules**:
the draw runs every frame and is small, the scatter runs about once a second and
rebuilds everything. Averaged together they read as a small constant and hide a
periodic spike — the failure `SYSTEM_STREAMING.md` §1.2 already records three
times.

Now reported separately as `FoliageRenderer.ScatterMilliseconds` and
`DrawMilliseconds`, carried into the hitch record as
`render.foliage.rescatterMs` / `drawMs`, with `dominantPhase` naming
`foliage-rescatter` or `foliage-draw` instead of the old combined
`foliage-scatter-render`. The combined number is kept as the bracket that must
still sum.

Each scatter also prints what it did:

```
[foliage]   3.4 ms over 812 cell(s), 3210 candidate(s) rolled, 1006 kept
```

`ScatterCells`, `ScatterCandidates` and `ScatterCount` are the rate story. **The
cost driver here is frequency, not the cost of one scatter** — raising
`RescatterDistance` is the cheapest lever if it ever matters, and §5 records the
real fix.

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

**Wind and fade** — `WindStrength` (0.06), `WindSpeed` (1.4), and the fade
window.

> **`Radius` alone does nothing past the fade window, and that was a real bug.**
> `grass.frag` computes
> `fade = clamp((uFadeEnd - dist) / (uFadeEnd - uFadeStart), 0, 1)`, so grass
> beyond `FadeEnd` is alpha-faded to nothing however far `Radius` reaches. With
> the old fixed defaults (30/45) against a Radius slider that goes to 120, the
> slider scattered instances nobody could see — Nico's report was "doesn't
> actually increase past about 30 yards", which is `FadeStart` exactly.
>
> `LinkFadeToRadius` (**default on**) now derives the window from Radius:
> `FadeEnd = Radius`, `FadeStart = Radius * FadeStartFraction` (0.66). Coverage
> and visibility were two knobs for one intent. Untick it to tune the window by
> hand; the two yard sliders reappear and the old behaviour returns.
>
> Cost scales with area, so doubling Radius roughly quadruples instance count —
> watch `MaxInstances` (24,000) before blaming the renderer.

**Look** — `Scale` (1.0), `ScaleJitter` (±0.25), `AlphaCutoff` (0.4),
`Brightness` (1.0).

Any coverage change calls `ForceRescatter()`, so edits take effect on the next
frame rather than after you walk 8 yards.

---

## 5b. Clutter does not grow in the river (2026-07-26)

**The defect.** `FoliageRenderer` contained not one mention of liquid. Its per-cell
gate tested the no-doodad mask, terrain holes and the layer map — nothing about
water — so the Elwynn riverbed, whose texture layer legitimately authors ground
effects, scattered ordinary grass under several feet of water.

**Why it is a DEPTH test and not a liquid test.** Nico's call, and it is the right
one: the *water plants* down there are correct. A riverbed's `GroundEffectTexture`
authors reeds, and reeds at the shallow margin are exactly what vanilla shows. Only
the ordinary clutter out in the channel is wrong. A blanket "no foliage under
liquid" would have thrown away the good half.

```csharp
if (SkipDeepLiquidCells && UnderDeepLiquid(chunk, cx, cy, LiquidFoliageMaxDepth))
{ LiquidCells++; continue; }
```

`LiquidFoliageMaxDepth` defaults to **0.75 yd**, on a slider in Video Options ->
Ground clutter -> Advanced with a live count of skipped cells. Lower cuts into the
shallows and takes the reeds with it; higher lets grass back into the channel.

**The index arithmetic is a direct lookup, not a spatial query.** The MCLQ tile
grid is 8x8 per chunk and this scatter loop is 8x8 — index-aligned, the same
alignment `LiquidRenderer.Build` already relies on for per-vertex depth. The axis
pairing matches that method: the liquid loop's row `r` is this loop's `cy` and its
column `c` is `cx`, so the tile index is `cy*8+cx` and the vertex index `cy*9+cx`.

**Test:** stand at the Elwynn river. Reeds at the edge, no grass in the channel.
The Advanced panel prints how many cells were skipped.

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
- **The re-scatter is still a full rebuild.** Walking 8 yd throws away every
  instance and re-derives all of them, when the overwhelming majority of cells
  did not change — only the annulus entering and leaving `Radius` did. The
  per-cell determinism means a cell's tufts are now genuinely reproducible, so
  the incremental version is *possible*: keep per-cell instance lists, add cells
  that entered, drop cells that left, touch nothing else. This is the same shape
  as PLAN_08 D3 for doodads and the same reason applies — re-deriving data you
  already have is the cost.

  **Measure before building it.** With the split timers in place, read
  `render.foliage.rescatterMs` on the frames it fires. If it is a millisecond or
  two, the full rebuild is fine and this stays undone.
