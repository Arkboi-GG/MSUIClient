# System — Water / Liquid

**Open-world liquid: lakes, rivers, ocean, slime and magma.** One of the
per-system docs the handbook indexes (see PROJECT_HANDBOOK.md §1.2). Read this
plus the handbook's cross-cutting ground truth (§3.1 coordinates, §8.5 shader
ASCII rule, §11 working agreements) before touching water. You should not need
the rest of the handbook for a water change.

Version: Draft 1 — 2026-07-24 (first water system, "look at WoWee" pass)
Owner files: `World/LiquidRenderer.cs`, `Shaders/water.vert`, `Shaders/water.frag`,
`Shaders/underwater.vert`, `Shaders/underwater.frag`, the MCLQ parse in
`Formats/AdtTerrainReader.cs`, and the render/atmosphere wiring in `Program.cs`.

---

## 0. The bar

Water has to **feel and look like WoW water** — not match a reference pixel for
pixel, but read as water: it has depth, it moves, you can be in it and under it.
The reference studied for this pass is **WoWee** (`Desktop/WoWee-master`), a C++
/ Vulkan 1.12 client with a mature water renderer. We took its *technique*, not
its code (it is Vulkan; this client is OpenGL/Silk.NET). §6 records exactly what
was borrowed and what was deliberately left out.

The three complaints this pass answered, in Nico's words:

1. *"Character always in front even though half submerged."*
2. *"Flat like a line, no dimension, no movement."*
3. *"Still 0 depth, there is no underwater."*

---

## 1. What is implemented now

### 1.1 Render order — water draws AFTER the character

The submersion bug (#1) was pure ordering. Water used to draw before the
character, so the character always painted on top of it. Now, in
`GameLoop.Render`, the order is:

```
terrain -> WMO -> doodads -> CHARACTER -> water surface -> underwater overlay -> debug/HUD
```

The water pass **tests depth but does not write it** (`DepthMask(false)`), blends
(`SrcAlpha / OneMinusSrcAlpha`) and disables face culling (so it reads from below
too). Because it draws after the character and depth-tests:

- A water surface *in front of* a submerged body part is nearer than that part,
  passes the depth test, and blends over it — you see the waterline climb the
  character.
- The near bank (opaque terrain, already in the depth buffer) still occludes
  water behind it.
- A character standing in front of a lake is nearer than the water, so the water
  fails the test there and the character stays crisply in front.

No depth *write* means overlapping water and the far side of a submerged model
still blend correctly instead of z-fighting.

### 1.2 Gerstner wave displacement — the surface has real relief (fixes #2)

`water.vert` displaces every water vertex with a stack of **six Gerstner waves**
(the same model WoWee uses). Each wave contributes vertical *and* horizontal
motion; the surface normal is derived analytically from the accumulated wave
slopes (cross product of tangent and binormal), so lighting and specular follow
the real moving geometry. Ocean uses a broader, choppier octave set than inland
water. This is what turns the old flat sheet into a surface with dimension and
motion.

The displacement is **damped to zero at the shoreline** using the per-vertex
depth (§1.3): `shore = smoothstep(0, 1.2, depth)`. Waves are full height in deep
water and lie flat where the water meets the beach, so they never climb up over
dry land.

### 1.3 Per-vertex baked depth — shoreline fade + darkening (fixes #3, part 1)

WoWee gets water depth by sampling a captured scene-depth texture in the shader.
This client has **no scene-depth pass yet**, so instead the depth is **baked into
the mesh per vertex** when it is built:

```
depth = surfaceZ - groundZ         (clamped >= 0)
```

This is a *direct index lookup*, not a spatial query, because the MCLQ liquid
grid and the terrain's MCVT outer grid are the same 9×9 grid at the same world
positions. For liquid vertex `(r, c)` in a chunk:

```
surfaceZ = MclqLayer.VertexHeights[r*9 + c]          (absolute WoW Z)
groundZ  = chunk.BaseZ + chunk.OuterHeight(c, r)     (== chunk.WorldHeightAt(c, r))
```

`water.frag` uses that depth for the two biggest depth cues:

- **Shoreline fade** — alpha goes to almost nothing at the waterline and rises to
  the type's base alpha in deep water. The soft, see-through edge is what sells
  "there is depth here."
- **Depth darkening** — a shallow-to-deep colour ramp (`shallowCol -> deepCol` by
  `1 - exp(-depth * k)`), a cheap Beer-Lambert stand-in.

This is the pragmatic substitute for WoWee's scene-depth refraction. When/if a
scene-colour+depth capture pass is added later, the shader can move to
screen-space depth and gain refraction and soft occlusion edges (see §5).

### 1.4 Underwater overlay — being *in* the water (fixes #3, part 2)

`underwater.vert/frag` draw a single full-screen triangle (generated from
`gl_VertexID`, no vertex buffer) tinting the whole screen when the **camera eye
is below a water surface**. `LiquidRenderer.TryGetSurface(x, y)` interpolates the
resident water grid at the camera's XY; if `cameraZ < surfaceZ`, the overlay
runs. Tint colour is per liquid type; opacity grows with how deep the eye is;
a slow caustic wobble and a darkened vignette keep it from looking like a flat
pane. Drawn with no depth test, on top of everything, before the HUD.

### 1.5 Surface look (`water.frag`)

On top of depth and waves: dual-scroll detail-normal ripples mixed with the mesh
normal; a Schlick fresnel that tints the surface toward the sky/fog colour at
grazing angles; a moving specular sun sparkle (the clearest sign of motion);
scattered cellular **shoreline foam** where the water is shallow; and wave-crest
brightening. Magma and slime take a separate self-luminous flowing-noise path
(no fresnel/foam), pulsing between a dark crust and a hot core.

### 1.6 Per-type palette and the MCLQ type codes

Vanilla MCLQ stores a per-tile liquid type in bits 0..2 of each 8×8 flag byte.
`AdtTerrainReader.ParseMclqLayers` reduces each layer to a dominant type:

| Code | Liquid | Shader path |
|---|---|---|
| 1 | Ocean | deep blue, choppier waves, higher alpha |
| 3 | Slime | green self-luminous flow |
| 4 (and 0) | River / lake water | blue-green, gentler waves |
| 6 | Magma | orange self-luminous flow |

Type routing in the shaders: `vType > 5.5` magma, `> 2.5` slime, `0.5..1.5`
ocean, else river/water. Colours currently live in the shader, not
`LiquidType.dbc` — see §5.

---

## 2. Ground truth — water facts, do not re-derive

- **Placement mirrors `TerrainTile.Prepare` exactly**, so water aligns with the
  ground it sits on:
  ```
  originX = (32 - row) * 533.33333 ;  originY = (32 - col) * 533.33333
  worldX  = originX - (chunk.IndexY*8 + r) * CELL_SIZE
  worldY  = originY - (chunk.IndexX*8 + c) * CELL_SIZE
  worldZ  = MclqLayer.VertexHeights[r*9 + c]     (already absolute WoW Z)
  ```
- **The liquid 9×9 grid is index-aligned with the terrain MCVT outer grid.** This
  is why depth is a direct lookup (§1.3). Do not spatial-query the terrain for
  water depth; the indices already correspond.
- **MCLQ vertex heights are absolute WoW Z.** MCVT heights are *relative to
  `chunk.BaseZ`*. Mixing the two conventions is the easy mistake; add `BaseZ` to
  MCVT, do not add it to MCLQ.
- **Vertex format is 5 floats:** position(3) + type(1) + depth(1), attributes 0/1/2.
- **Render state is the contract:** depth test on, depth write OFF, blend on, cull
  off, and it must run *after* the character. Anything drawn after water (debug
  lines, HUD) can rely on water not having touched the depth buffer.
- **Camera-relative rendering, as everywhere else** (handbook §3.1). Positions are
  absolute WoW space; the vertex shader subtracts `camera.Position` and uses
  `camera.RelativeViewProjection`. There is no coordinate conversion.
- **Residency follows terrain.** `LoadForTiles` builds/keeps water meshes for
  exactly the resident tiles and disposes the rest, diffing against the terrain's
  `LoadedTiles` on tile transitions — same pattern as the moving 3×3 ring.

---

## 3. Files and responsibilities

| File | Owns | Does NOT own |
|---|---|---|
| `World/LiquidRenderer.cs` | Mesh build (incl. baked depth), residency, the transparent surface pass, `TryGetSurface`, the underwater overlay pass | Terrain heights (reads them), atmosphere values (receives them), swim physics |
| `Shaders/water.vert` | Gerstner displacement, analytic normal, shore damping | Colour, lighting |
| `Shaders/water.frag` | Depth fade, darkening, detail normals, fresnel, sparkle, foam, per-type look | Placement, depth source |
| `Shaders/underwater.vert/frag` | Full-screen submerged tint + caustic | Deciding *when* submerged (Program does) |
| `Formats/AdtTerrainReader.cs` (MCLQ) | Parsing `MclqLayer` (type, heights, render mask) | Rendering |
| `Program.cs` (`Render`) | Draw order, feeding atmosphere to the renderer, the submersion check that triggers the overlay | Water internals |

The atmosphere (sun, ambient, fog) is pushed into `LiquidRenderer` each frame from
the same evaluated time-of-day environment every other renderer uses, so water
matches the world's light and fog.

---

## 4. Tuning and testing

**Where the feel lives.** The look is a handful of numbers, currently constants:
wave amplitude/frequency/speed per type (`water.vert`), shore-fade width
(`smoothstep(0, 1.2, depth)`), depth-darkening rate (`exp(-depth * 0.18)`),
base alpha per type, specular tightness, foam thresholds (`water.frag`), and the
underwater tint colours + density (`LiquidRenderer.UnderwaterTint`,
`underwater.frag`). These are the knobs to turn when dialing the feel. The next
step is exposing the main ones (wave height, transparency, underwater density) as
live HUD sliders behind the DevTools switch (FOUNDATION_PLAN §12, Plan 05
TuningState) so Nico can tune by eye instead of by rebuild.

**How to test (the shared-language loop).** Water is visual, so a screenshot is
the start, not the end. Pair it with data: wade in and confirm the waterline sits
on the character; look along a shore for the transparent-to-opaque fade; dip the
camera under for the overlay. When something is off, capture a vantage
(FOUNDATION_PLAN / PLAN_01) and dump the scene so the decision and the numbers
(resident water tiles, triangle count, the type at that spot, camera vs surface
Z) come back as text, not just an image.

---

## 5. Not done — the honest ceiling

- **WMO liquid (MLIQ).** Stormwind canals, fountains and indoor pools are WMO
  liquid, parsed from MLIQ in local space, not ADT MCLQ. Not rendered yet. This
  is the most visible gap in the city.
- **`LiquidType.dbc` colours.** Palettes are hard-coded per basic type. Real
  per-liquid colours/materials live in the DBC and are not read yet.
- **Screen-space refraction and planar reflection.** WoWee captures scene colour +
  depth and adds refraction, a blue underwater fog on submerged geometry, and a
  planar reflection pass. This client has none of those passes; depth is baked per
  vertex instead (§1.3). Adding a scene capture is the unlock for all three.
- **Player interaction ripples.** WoWee rings water outward from the player and
  spawns foot splashes / bubbles (`swim_effects`). Not implemented; the shader has
  no player-position input yet.
- **Swim physics / buoyancy.** Rendering only. The controller does not yet know it
  is in water; `TryGetSurface` is the hook that a swim state would reuse.
- **Real animated water textures.** The surface is fully procedural. Vanilla uses
  animated BLP frames from the MPQs; not sampled here.

Do not treat any of these as bugs — they are unstarted scope, listed so the next
change knows where the edge is.

---

## 6. WoWee lineage — what was borrowed, what was not

Studied under `Desktop/WoWee-master`: `assets/shaders/water.vert.glsl` +
`water.frag.glsl`, `src/rendering/water_renderer.cpp` + `.hpp`,
`src/rendering/swim_effects.cpp`.

**Borrowed (technique, re-implemented for GL):** the 6-octave Gerstner wave model
and analytic normals; dual-scroll detail normals; the depth-driven shoreline
fade + Beer-Lambert darkening idea; the fresnel sky tint; cellular-noise foam;
the magma/slime self-luminous path; per-basic-type palette split; the
`camPos.z < waterHeight` submersion test.

**Deliberately not carried over (yet):** WoWee's whole scene-history and planar
reflection infrastructure (extra render passes, FBOs, descriptor sets) — replaced
here by per-vertex baked depth, which gets most of the depth read for none of the
pipeline cost. Its Vulkan push-constant/descriptor plumbing is irrelevant to this
client. Its swim-effects particle systems.

The rule of the borrow: WoWee is a *reference to learn from, not a bible to
match*. Where a simpler approach gets the feel (baked depth vs a depth pass), take
the simpler one and record why here.
