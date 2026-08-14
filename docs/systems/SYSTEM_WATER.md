# System — Water / Liquid

**Open-world liquid: lakes, rivers, ocean, slime and magma.** One of the
per-system docs the handbook indexes (see PROJECT_HANDBOOK.md §1.2). Read this
plus the handbook's cross-cutting ground truth (§3.1 coordinates, §8.5 shader
ASCII rule, §11 working agreements) before touching water. You should not need
the rest of the handbook for a water change.

Version: Draft 6 — 2026-08-12 (**WMO liquid is BUILT, as a DRAW-ONLY pass** — §7
now describes code that exists. `LiquidRenderer` keeps a second, separate mesh
set fed by `WmoRenderer.EnumerateLiquid()` and rebuilt on a per-frame
`LiquidVersion` int-compare. WMO surfaces are **still NOT in `TryGetSurface`** —
submersion, the underwater tint and the wake ignore WMO liquid, deliberately,
because rewriting that shared path is what got the first build reverted.)
Previous: Draft 5 — 2026-07-26 (the body-colour fix landed — **new §8**, which is the
section the shader and settings comments point at. Water colour is now declared
**SUFFICIENT, not 1:1 with the 1.12 client**, and closed until a much later
refining pass; see §8.4. Foliage no longer grows in the river.)
Previous: Draft 4 — 2026-07-26 (two corrections, both from evidence gathered the same day.
**(a) The authored water colours of PLAN_12 are WRONG and are now default-OFF** — see
§5. The band indexing is right and the values are real; the *interpretation* is not.
**(b) The WMO-liquid code of PLAN_15 was REVERTED** — §7 is a specification, not a
description of the build. Its format research stands; the renderer does not exist.)
Previous: Draft 3 — 2026-07-26 (WMO liquid, since reverted)
Previous: Draft 2 — 2026-07-24 ("real vanilla textures + live tuning" pass)
Previous: Draft 1 — first water system, procedural "look at WoWee" pass.
Owner files: `World/LiquidRenderer.cs`, `Shaders/water.vert`, `Shaders/water.frag`,
`World/Wmo/WmoRenderer.cs` (`EnumerateLiquid`, `WmoLiquidSurface`),
`Shaders/underwater.vert`, `Shaders/underwater.frag`, the MCLQ parse in
`Formats/AdtTerrainReader.cs`, and the render/atmosphere/HUD wiring plus the
`WaterTuningWindow` in `Program.cs` / `GameLoop/Dev/GameLoop.DevTools.cs`.

---

## 0. The bar

Water has to **feel and look like WoW 1.12 water** — the real client, not a
stylized reinterpretation. The hard-won lesson of the Draft 2 pass: 1.12 water is
**a dark, near-opaque surface covered by a scrolling animated texture**. It is NOT
see-through, and its motion is the *texture* scrolling, not the geometry waving.
Chasing "transparent, wavy" water (Draft 1 / WoWee) walked away from the target;
loading the client's own animated liquid textures walked back to it.

The complaints this pass answered, in Nico's words, in order:

1. *"All green."* — river was mis-routed to the slime shader path (fixed §1.1).
2. *"Too transparent / it's just wrong."* — 1.12 is opaque and textured, not
   clear; the procedural surface could never match it (fixed §1.2).
3. *"The light is swimming with the animation."* — cross-fading texture frames
   made highlights glide; vanilla swaps frames in place (fixed §1.3).
4. *"Weird undulation."* — the Gerstner geometry waves; flattened (fixed §1.3).
5. *"Super dark."* — lighting defaults over-dimmed the texture; rebalanced, and
   every knob is now live in a HUD so it can be dialed by eye (fixed §1.4, §4).

The ground rule that came out of this pass: **build the real thing, not a
stand-in dressed up as progress.** When only an approximation is possible, say so
up front. That is why the surface is the client's own BLP frames, not procedural
noise.

---

## 1. What is implemented now

### 1.1 Type routing — river is water, not slime

Vanilla MCLQ stores a per-tile liquid type. `AdtTerrainReader.ParseMclqLayers`
reduces each layer to a dominant type code, and **the shaders route by that exact
code**, not by a coarse threshold:

| Code | Liquid | Texture / path |
|---|---|---|
| 1 | Ocean | ocean_h / ocean |
| 3 | Slime | slime |
| 4 (and 0/2/5) | River / lake / inland water | lake_a |
| 6 | Magma | lava |

Routing in `water.frag` / `water.vert`: `type > 5.5` magma, `2.5..3.5` slime,
`0.5..1.5` ocean, **everything else = river/lake water**. The Draft 1 bug was a
single `type > 2.5` test that swept river (type 4) into the slime branch and
painted it luminous green. Do not re-introduce a threshold that catches 4.

### 1.2 Real vanilla animated liquid textures (the surface)

`LiquidRenderer.LoadLiquidTextures` reads the client's own numbered BLP frames
straight from the MPQs (via `AdtTerrainReader.ReadBlpPixels` -> `BlpDecoder`),
stacks each liquid's frames into one `GL_TEXTURE_2D_ARRAY`, and the shader samples
them. The real vanilla paths (from WoWMapViewer's `liquid.cpp`, §6):

```
river/lake  XTextures\river\lake_a.%d.blp     (the file is lake_a, NOT river)
ocean       XTextures\ocean\ocean_h.%d.blp     (falls back to ocean.%d.blp)
magma       XTextures\lava\lava.%d.blp
slime       XTextures\slime\slime.%d.blp
```

Frames are numbered from 1 until one is missing (vanilla ships 30). The loader
**probes an ordered candidate list per type and logs what resolved**
(`[liquid-tex] water: 30 frame(s) 256x256 from 'XTextures\river\lake_a.N.blp'`),
so a wrong path is visible in the console, not silent. If a type's frames fail to
load, it logs and — if the MPQ carries a `(listfile)` — dumps the real candidate
paths (`DiscoverLiquidTexturePaths`).

`water.frag` samples the array with **world-space UVs** (`vAbsXY * uTexScale`, no
stored UVs), so the texture tiles against world position and stays put on a flat
surface. A **river-with-no-texture borrows the ocean texture** so inland water
still gets a real animated surface rather than the fallback (§1.5).

### 1.3 Flat, still surface; frame-swap (not cross-fade) animation

Two Draft 1 behaviours actively fought the texture and were removed:

- **Geometry waves.** `water.vert` still computes the six-octave Gerstner stack,
  but the displacement is scaled by `uWaveAmp`, which **defaults to 0 = flat
  plane**. Vanilla water is a flat sheet; physically waving the mesh made the
  locked texture swim. Raise `uWaveAmp` to bring the waves back.
- **Wave-normal relighting.** The textured path lights the surface **flat** — it
  does not use `vNormal`. Relighting with the slow-moving Gerstner normal painted
  broad light/dark bands that drifted across and fought the texture's animation.
  The texture carries all the ripple detail.

Frame animation is a **discrete swap by default** (`uFrameBlend = 0`): the current
frame shows for `1/fps` seconds, then the next. Cross-fading (`uFrameBlend -> 1`)
blends offset caustic frames, which makes the highlights *glide* — that continuous
drift is what read as "the light swimming with the water." Vanilla swaps in place,
so the light twinkles/boils rather than gliding. The knob is continuous.

### 1.4 Opaque, depth-darkened, flat-lit look (`water.frag` textured path)

The textured branch runs first and returns; the procedural surface (§1.5) is only
a fallback. On top of the sampled texture: **near-opaque alpha** (deep water ~1.0,
only the shoreline softens — vanilla is not see-through), **depth darkening** (a
shallow->deep multiplier), **flat lighting** (base brightness + ambient + a
sun-elevation term; no wave normal), a **static grazing sky sheen** from the view
angle only, and distance fog. Every one of these is a live uniform (§4). The
defaults were rebalanced this pass so the texture shows near its own brightness at
noon instead of being multiplied down; deep water can still read a little dark and
is a tuning target, not a bug.

### 1.5 Procedural fallback (only when a texture is missing)

If `sampleLiquid` finds zero frames for the routed type (and no ocean texture to
borrow), the shader falls through to the Draft 1 **procedural surface**: dark
teal-green body, depth fade, dual-scroll detail normals, scrolling fbm shimmer,
foam. It exists so the client never renders broken water if the MPQ paths are
wrong — it is a safety net, loudly logged, **not** the intended look.

### 1.6 Underwater overlay + submersion

`underwater.vert/frag` draw a full-screen tint when the camera eye is below a
water surface (`LiquidRenderer.TryGetSurface` interpolates the resident grid at
the camera XY; if `cameraZ < surfaceZ`, the overlay runs). Per-type tint, opacity
grows with eye depth, slow caustic wobble + vignette. `TryGetSurface` also backs
the HUD's "liquid type under you" readout (§4) and is the hook a future swim state
would reuse.

### 1.7 Render order and state (unchanged, still the contract)

Draw order in `GameLoop.Render`:
`terrain -> WMO -> doodads -> CHARACTER -> water surface -> underwater -> HUD`.
The water pass **tests depth but does not write it** (`DepthMask(false)`), blends
`SrcAlpha/OneMinusSrcAlpha`, and disables face culling (reads from below). Drawing
after the character is what lets a submerged body be covered by the surface in
front of it. With opaque water the submerged half is simply hidden, which is the
1.12 look.

---

## 2. Ground truth — water facts, do not re-derive

- **Vanilla liquid textures live in `XTextures\`** and the river file is `lake_a`,
  not `river` (the folder is `river`). Ocean is `ocean_h`. 30 frames, 1-indexed,
  `%s.%d.blp`. Source: WoWMapViewer `liquid.cpp` (§6). These are verified against
  Nico's client: `river 30`, `ocean 30` in the HUD.
- **1.12 water is dark and opaque, and its motion is the scrolling texture**, not
  geometry waves and not transparency. This is the whole point; do not "improve"
  it back toward clear, wavy water.
- **UVs are world-space, derived in the fragment shader** (`vAbsXY * uTexScale`).
  The vertex format is still 5 floats: position(3) + type(1) + depth(1),
  attributes 0/1/2. There is no UV attribute.
- **Placement mirrors `TerrainTile.Prepare` exactly** and the liquid 9x9 grid is
  index-aligned with the terrain MCVT outer grid, so per-vertex depth is a direct
  lookup (`surfaceZ - groundZ`), not a spatial query. MCLQ heights are absolute
  WoW Z; MCVT heights are relative to `chunk.BaseZ`.
- **Camera-relative rendering, as everywhere else** (handbook §3.1). Positions are
  absolute WoW space; the vertex shader subtracts `camera.Position`.
- **Residency follows terrain.** `LoadForTiles` builds/keeps water meshes for the
  resident tiles; the textures load once at startup (`LoadLiquidTextures`) and are
  not per-tile.

---

## 3. Files and responsibilities

| File | Owns | Does NOT own |
|---|---|---|
| `World/LiquidRenderer.cs` | Mesh build (baked depth), residency, **loading the animated BLP frames into array textures**, the transparent surface pass, `TryGetSurface`, the underwater pass, and **every live tuning property** | Terrain heights (reads them), atmosphere (receives it), swim physics |
| `Shaders/water.vert` | Optional Gerstner displacement scaled by `uWaveAmp` (0 = flat), pass-through of world XY for UVs | Colour, lighting, texture |
| `Shaders/water.frag` | Animated texture sampling + frame swap, textured look (opacity, depth, flat lighting, sheen, fog), river->ocean borrow, procedural fallback | Placement, which frames exist |
| `Shaders/underwater.vert/frag` | Full-screen submerged tint + caustic | Deciding *when* submerged (Program does) |
| `Formats/AdtTerrainReader.cs` | MCLQ parse (type, heights, mask); `ReadBlpPixels`/`ReadFileFromMpqs` used to load the frames | Rendering |
| `Program.cs` | Draw order, atmosphere feed, submersion check, **`WaterTuningWindow` (the live HUD)** and calling `LoadLiquidTextures` | Water internals |

The atmosphere (sun, ambient, fog) is pushed into `LiquidRenderer` each frame so
water matches the world's light and fog.

---

## 4. Tuning — the live Water Tuning HUD

**Every look/feel constant is a live uniform, driven by a dedicated ImGui window
(`GameLoop.WaterTuningWindow`, DevTools only) — a second window next to the main
one.** Move a slider, see it immediately; nothing needs a rebuild to tune. Groups:

- **Texture & animation** — texture scale (world UV tiling), animation FPS, frame
  blend (0 twinkle / 1 glide), texture brightness, texture contrast, texture tint.
- **Opacity** — opacity (deep alpha), shoreline alpha, shoreline width.
- **Depth colour** — deep darkening (higher = brighter deep water), depth rate.
- **Lighting** — base brightness, ambient amount, sun amount, sky sheen.
- **Geometry waves** — wave amplitude (0 = flat), wave speed.
- **Reset to defaults**, plus read-outs: `frames river/ocean/slime/magma` (0 = that
  texture did not load) and **`under you: liquid type N -> which texture`** (the
  definitive routing check — stand in the water and read it).

The knobs are **session-only**: they reset to the coded defaults (in
`LiquidRenderer`'s property initializers and `ResetTuning`) on restart. Once a set
of values looks right, bake them into those defaults.

**How to test (the shared-language loop).** Water is visual, so pair a screenshot
with the HUD read-outs: the `frames` line confirms the textures loaded, the
`under you` line confirms routing, and the sliders let you bisect any "it looks
wrong" to a single number. That loop is how the Draft 2 issues were each pinned
to one cause (mis-routing, frame cross-fade, wave normal, lighting gain).

---

## 5. Not done — the honest ceiling

- ~~**Deep water still reads a little dark.**~~ **EXPLAINED AND FIXED, §8.** It was
  not a tuning shortfall: the shader was using a near-black greyscale highlight
  mask as the water's colour, so *all* the water was dark and deep water merely
  most obviously so. Water now has a body colour.
- **Player interaction: the walking wake, and swimming.** Both specified in
  **PLAN_16_WATER_INTERACTION.md**, neither built. Blizzard ships the assets:
  `XTextures\splash\wake.blp` and `splash.blp`, plus 29 frames of
  `XTextures\caustic\`. The character M2 carries all six swim clips (41-46).
  Note WoWee's concentric-ripple approach is only half applicable — our surface
  is deliberately flat, so the vertex-displacement half does not apply.
- **MCLQ liquid-type accuracy.** If the `under you` read-out ever shows a river
  tagged as ocean, the fix is in `ParseMclqLayers`' type detection (read the type
  from the MCNK header flags), not the texture.
- **WMO liquid (MLIQ).** Stormwind canals, fountains, indoor pools — parsed from
  MLIQ in local space, not ADT MCLQ. **RENDERED as of 2026-08-12, draw-only** —
  see §7. (The first PLAN_15 build of 2026-07-26 was reverted the same day
  because it also rewrote `TryGetSurface`; the rebuild leaves that path
  untouched, so WMO liquid still contributes nothing to submersion or the
  underwater tint. That half remains open debt.)
- **`LiquidType.dbc` colours/materials.** Textures are now real, but the shader's
  colour/lighting constants are hand-tuned, not read from the DBC.
- **THE AUTHORED OCEAN/RIVER COLOURS ARE WRONG. Default OFF as of 2026-07-26.**
  PLAN_12 wired `LightIntBand` bands 13-16 into the shader and shipped it ON. It
  makes the river **dark, monocolour and apparently static** — the animated
  highlights vanish. Nico: *"the top of the water textures are all gone. It had
  the animated white/color movement and now it's monocolor static."*

  **The mechanism.** `water.frag:217` is
  `vec3 tint = mix(uTexTint, uTexTint * aBody, uAuthoredWater);` — a 100%
  **multiply** of the animated liquid texture by the band colour. Azeroth's
  authored river-close is `(0.000, 0.114, 0.161)`; **red is exactly zero.** And
  vanilla's `lake_a.N.blp` frames *are* the bright animated highlight layer (§1.2,
  §2). Multiply highlights by near-black and you get a flat dark sheet.

  **What is NOT the problem — do not go re-derive these:**
  - *The band indexing.* Confirmed against wowdev (1-indexed 14..17 = our
    0-indexed 13..16) and against our own sky, which renders correctly from bands
    2-6 of the very same record.
  - *The record being a junk fallback.* It is not. Northshire resolves the map
    default (`Light id=1 -> LightParams 12`) because no positioned row reaches it
    — nearest is id 77 at 731 yd with a 495 yd falloff — and that map default **is**
    authored Azeroth outdoor lighting. The sky proves it.
  - *A close/far swap.* Across all 426 LightParams there is **no systematic
    brightness ordering** in either pair (river 156 vs 95, ocean 91 vs 84). If
    these were shallow/deep you would see a strong direction.

  **What the evidence actually says.** These bands are not a texture tint:
  - The authored alphas are `waterShallow 0.65 / waterDeep 0.50` and
    `oceanShallow 1.00 / oceanDeep 0.75` — **shallow MORE opaque than deep**,
    which is backwards for depth and sensible for *distance from camera*. Our
    shader drives the blend off `tdepthFade`, i.e. depth.
  - **WoWee loads all 18 colour bands and consumes seven** — ambient, diffuse,
    fog and the four sky bands. Its header says *"... more channels exist (ocean,
    river, shadow, etc.)"* and `WaterRenderer::getLiquidColor` **hardcodes** the
    colour per liquid type instead. The reference implementation reads these
    bands and deliberately declines to use them.

  **So: the hand-tuned constants are the shipping look**, not debt. The previous
  entry here framed them as *"invented and unread"* and that framing is what drove
  PLAN_12. It was wrong. Do not re-open this without new evidence about what bands
  13-16 actually drive in the real client.

  Measured for reference (Azeroth map default, noon): ocean close
  `0.380 0.510 0.718`, ocean far `0.067 0.294 0.349`, river close
  `0.000 0.114 0.161`, river far `0.310 0.365 0.078`.

  **The process lesson, which is the expensive part.** "Stop tuning, start
  reading" earned its keep on ambient colour, where the light probe could show
  `data` vs `applied` deltas of 0.000 AND the result looked right. It was then
  applied to water as a reflex, over a system already signed off as good, and
  **shipped defaulted to the unverified branch while its own plan recorded doubt**
  (PLAN_12 §4 H4; EMPIRICAL_CHECKS B1, labelled *"the one that decides
  everything"*, never run). An A/B defaulted to the untested side is not an A/B.

- **Swim physics / buoyancy.** Rendering only. `TryGetSurface` is the hook, and
  it now takes a query Z and returns the lowest surface above it. PLAN_16.

Screen-space refraction / planar reflection (Draft 1's stated "unlock") is
**deliberately not pursued** — 1.12 water is opaque, so there is nothing to refract
through. It was a WoWee idea that did not match the target.

---

## 6. Lineage — what the surface actually comes from

**The surface is the client's own vanilla animated BLP frames.** The authoritative
source for the paths is **WoWMapViewer** (`glararan/WoWMapViewer`, `liquid.cpp`),
a vanilla map viewer that hardcodes them; that is where `XTextures\river\lake_a`,
`ocean\ocean_h`, `lava\lava`, `slime\slime` come from. Verified against Nico's MPQs
via the loader's `[liquid-tex]` log.

**WoWee** (`Desktop/WoWee-master`) was the Draft 1 reference and is now mostly
*not* followed: its water is a modern, procedural, refractive, wavy renderer, and
this pass moved deliberately toward the plain, opaque, texture-scrolling 1.12 look
instead. What survives from the WoWee study: the Gerstner stack (kept, but off by
default behind `uWaveAmp`), the per-type routing idea, the `camPos.z < waterHeight`
submersion test, and the swim-ripple technique that is the documented next step
(§5). The rule of the borrow still holds — take the technique only where it serves
the target, and record why here.

---

## 7. WMO liquid — canals, fountains, indoor pools (PLAN_15) — BUILT, DRAW-ONLY

> **Rebuilt 2026-08-12 as a draw-only pass.** The first build (2026-07-26) was
> reverted the same day because it also rewrote `TryGetSurface`, which
> open-world water depended on. The rebuild does not touch that path at all:
> `LiquidRenderer` keeps a **second, fully separate mesh set** (`_wmoMeshes`)
> fed by `EnumerateLiquid()`, rebuilt when `WmoRenderer.LiquidVersion` moves
> (a per-frame int compare, NOT a tile-crossing event — see §7.6), and drawn
> in the same pass with the same shader, uniforms and GL state.
>
> **Deliberately deferred: submersion.** WMO surfaces are NOT in
> `TryGetSurface`, so swimming state, the underwater tint and the walking wake
> ignore WMO liquid. Wiring that is a separate, careful change to the shared
> query — the exact thing that broke last time.
>
> **Default ON** (`Water.DrawWmoLiquid` in settings). The old "default OFF"
> instruction below applied to a build that rewrote the shared path; a
> draw-only pass cannot regress open-world water, so it ships on.
>
> §7.2 and §7.3 remain the ground truth — the MLIQ format facts, derived from
> 235 real groups and since cross-confirmed twice: `LiquidType.dbc` (extracted
> from `patch.MPQ`: 1 Water, 2 Ocean, 3 Magma, 4 Slime) and WoWee's own
> `(liquidType - 1) % 4` reduction both match the `& 3` grouping exactly.


Stormwind's canals, Ironforge's lava channels, Undercity's slime, Blackrock's
lava, the Maraudon and Blackfathom pools, and every fountain. **235 groups in
`wmo.MPQ` carry an MLIQ chunk.** `WmoReader` had parsed all of them since the WMO
reader was written and nothing read the result.

### 7.1 It is deliberately the same pipeline as open-world water

`WmoRenderer.EnumerateLiquid()` yields `WmoLiquidSurface` — a placed, world-space
vertex grid — mirroring `EnumerateDoodads()`. `LiquidRenderer` builds one extra
mesh from those and draws it **with the same shader, the same uniforms, the same
draw state and the same tuning knobs** as the MCLQ surface.

That is not laziness, it is the requirement. A canal and the river outside the
gate are the same substance; one pipeline is what keeps them looking like it
through every future tuning pass. A second liquid pass inside `WmoRenderer` would
duplicate the whole `water.frag` uniform block and drift on the first change.

### 7.2 Ground truth — settled from bytes, do not re-derive

Full derivation and the scoring tables are in **PLAN_15 §4**. The short form:

| Fact | Value | How it was settled |
|---|---|---|
| Local layout | `(CornerX + i*U, CornerY + j*U, Height)`, **Z-up**, same space as MOVT | 5 candidates scored by escape from each group's authored MOGP box, over 235 groups. Z-up beat the Y-up reading **18 to 1** |
| Which axis `i` indexes | `i` is X, `j` is Y | the 187 **non-square** grids; square ones cannot tell |
| `U` | **4.1666667** (`33.3333/8`) | **470 of 470 corner coordinates are exact integer multiples of it.** 4.2 scores 1.1% |
| Hidden tile | `(b & 0x0F) == 0x0F` | low nibbles 8..14 never occur, so the old `0x08` test is right *by luck* |
| Substance | `b & 3`: 0 water, 1 ocean, 2 magma, 3 slime | Blackrock+Ironforge land in magma, Undercity+Stratholme in slime, Stormwind's canals in water. Zero counterexamples |
| `MOGP.groupLiquid` | **always 15** — carries no information | route per tile, always |

**Two comments in `WmoReader.cs` were wrong and are now fixed.** The `WmoLiquid`
docstring claimed Noggit's Y-up layout; that is Noggit's own render space, not the
file's. This was the *second* stale Noggit-derived comment in that file — the
first claimed MOVT was converted to Y-up at parse. **Treat prose in `WmoReader.cs`
as a lead, never as ground truth.**

### 7.3 THE TRAP — MLIQ type codes are not the shader's type codes

`water.frag` routes on the **MCLQ** codes of §1.1 (`4` river/lake, `1` ocean, `6`
magma, `3` slime). MLIQ uses a different encoding in the same numeric range, and
**three of its six live codes happen to mean the same thing under both**. Passing
a type through untranslated therefore works in Stormwind, works in Undercity,
works in Blackrock — and puts blue water in Ironforge's lava channels and in
Stratholme.

`WmoLiquidSurface.ShaderType(i, j)` is the translation and is the only place a
type should come from. §7.5 step 2 is the test that catches a regression.

### 7.4 Known debt — depth is a stand-in, and it is labelled

Open-world water bakes real per-vertex depth by subtracting the terrain height at
the same grid index; the two grids are index-aligned, so it is a free lookup. **A
WMO pool has no terrain beneath it** — its floor is the building's own mesh.

Until that is raycast against the collision BVH, every WMO liquid vertex gets one
number: `LiquidRenderer.WmoDepth`, default 3.0 yd, on a slider at
Video Options → Water. The visible cost is that a canal does not soften where it
meets its wall. **The upgrade is one raycast per vertex at build time and the BVH
is already there** — this is deferred, not unknown.

A plausible-looking alternative was tried and rejected: MLIQ's `CornerZ` is *not*
the pool floor. Measured, it equals the minimum vertex height, so
`height - CornerZ` is zero across the **87%** of surfaces that are flat, which
would paint every pool entirely at shoreline alpha.

### 7.4b Magma UV scale + creep — the "frames don't cycle" report (2026-08-13)

The first Blackrock test read as **"the lava lake renders but the magma frames
don't cycle."** Instrumented live (an env-gated probe that re-rendered the lake
mesh into a fixed screen rect and diffed gameplay dumps): the frames DO cycle,
and **ADT and WMO magma cycle identically** — they run the same `water.frag`
branch with the same uniforms, and the per-second pixel change measured equal on
both. There was no WMO-specific bug to fix.

What made it *look* frozen: magma was sampled at the water texture scale
(`uTexScale` = one repeat per 6.25 yd). Consecutive `lava.N.blp` frames differ by
only ~0.5% mean per texel (measured across all 30), so at that cell size mip
filtering averages the boil away a few yards out — on the 120-yd Blackrock lake
the surface is effectively static. Blizzard authors MLIQ magma `s/t` at one
repeat per **~35–200 yd** (read from Blackrock groups 38/43), which is why
vanilla's boil cells are big enough to stay visible.

The fix is in `water.frag`'s magma branch only: `tuv = tuv * 0.25 + uTime *
vec2(0.012, 0.007)` — 25 yd cells (inside the authored range) plus a slow
vanilla-style creep (~one cell per 80 s). It applies to magma on BOTH paths
(deliberate: they must stay identical); water, ocean and slime UVs are
untouched. Verified live with the probe: per-1.1 s pixel change on the Blackrock
lake went from mean 5–7 (under 1.5% of pixels) to mean 24–27 (30–36%).

~~The authored `s/t` are still discarded at parse~~ — **ADOPTED 2026-08-13,
see below.** WMO magma now samples Blizzard's per-vertex MLIQ UVs; ADT magma
keeps the planar 25-yd mapping above (MCLQ has no authored magma UVs to adopt).

#### 2026-08-13 — owner report against a real 1.12 reference shot (Blackrock,
#### the outer lake around the central spire, near the LBRS balcony)

Two observations: (1) our lava "isn't high enough" relative to the stone
ledges/stairs; (2) real 1.12 shows large swirls of lava visibly dragged
around the central rock — which is the authored per-vertex UV mapping this
section already flagged as discarded.

**HEIGHT — verified end-to-end, no pipeline bug found.** Every layer was
checked against bytes (`tools/mpqpy`, scripts in the session scratchpad):

* The owner's lake is **group 38, 'Blackrock Spire'** (55×82 verts, the only
  MLIQ under the central spire; the LBRS balcony at (-7527, -1226, ~181)
  sits on its edge). Authored surface: **flat at local −67.5 → world 168.40**
  (3,039 of 3,376 live verts), rim bumps to −60.9 → 175.0 at the lava
  inflows, `CornerZ` = min = −68.609 → 167.29. Group 43 (the second lake,
  390 yd away): flat −97.629 → **138.27**.
* **The vert layout is right**: magma SMOMVert is `int16 s, int16 t, float
  height` — height at +4, same offset as the water layout, confirmed by the
  parsed heights landing exactly on the CornerZ..0 band while bytes 0–3 form
  smooth int16 gradients (the UVs).
* **`patch.MPQ` DOES override `Blackrock_038/043.wmo`** (159 AZ_Blackrock
  files — the base-archive-only habit of tools/mpqpy README is NOT safe for
  this WMO in general), but the patched MLIQ is byte-identical where it
  matters: same grids, corners, heights, tile masks.
* **The runtime emits exactly the authored numbers.** New instrument:
  `MSUI_WMO_LIQUID_TRACE=1` logs every meshed WMO surface. Live at Blackrock:
  `blackrock.wmo[38] … worldZ 167.29..175.00`, `[43] … worldZ 138.27`; both
  meshed, both drawn (verified by screenshot from the shore ledge at 172.2).
  Liquid and walls share `instance.Transform`, so a liquid-only Z offset is
  structurally impossible.
* **The static opaque lava-plane batch** (BURNINGSTEPPSLAVA02, flat tris at
  local −100.2 → world 135.7) exists **only under group 43's lake**, 2.57 yd
  below its MLIQ — and the drawn MLIQ covers it. There is NO static plane
  under the big lake, so "we render only the lower plane" is ruled out too.

Conclusion: the surface is at the authored height, 3.6 yd below the shore
walkway (172.2) and ~13 yd below the balcony. The most plausible source of
the "too low" reading was the *mapping*, not the geometry: with planar UVs
the bright boil cells ignore the shoreline, so dark crust texels sit against
the shore rock and the lava's visible edge reads low and dead. The authored
UVs (below) hug the flow around the island and the shore, which is what the
reference shot shows. If the report survives this change, re-measure with
the trace env var before touching the transform — the numbers above are the
ground truth to compare against.

#### 2026-08-13 — authored MLIQ magma UVs adopted (the swirl)

* **Parse** (`WmoReader.cs`): the 4-byte union ahead of each height is kept
  as `WmoLiquid.VertexS/VertexT` (int16 pairs). Meaningful ONLY for magma —
  for water/ocean/slime those bytes are flow data, and every consumer gates
  on the substance.
* **Scale, measured not assumed**: with UV = raw/255 per repeat, group 38
  authors one repeat per **~35–175 yd** (median |grad|: 95 yd along u, 35 yd
  along v) and group 43 per ~8–30 yd — anisotropic, warped along the flow
  direction. That brackets the previously-recorded 35–200 yd band and the
  25-yd planar compromise, so /255 is the accepted divisor.
* **Carry** (`WmoRenderer.WmoLiquidSurface.AuthoredUv`) → **WMO-only vertex
  format** (`LiquidRenderer`): the WMO mesh grew to 7 floats (pos, type,
  depth, u, v) on attribute 3; the ADT path keeps its 5-float format and
  never enables the attribute, and a `uWmoAuthoredUv` uniform is 0 for the
  whole ADT loop — that pass stays bit-identical by construction.
* **Shader** (`water.frag` magma branch only):
  `tuv = (uWmoAuthoredUv > 0.5) ? vLiquidUv + creep : tuv*0.25 + creep` with
  the same `creep = uTime*vec2(0.012, 0.007)` on both, so ADT and WMO magma
  keep drifting at the same UV rate. Water/ocean/slime are untouched on both
  paths (their MLIQ "UVs" would be reinterpreted flow bytes — garbage).

### 7.5 How to test it

1. **The numeric gate — no screenshot needed.** `[wmo-liquid] escape total ...`
   at load recomputes the metric that settled the convention. If it is wildly
   large, **the instance transform is wrong, not the convention.**
2. **Substance.** Video Options → Water shows `types water=N magma=N ...`.
   Stormwind must read **water only**; Ironforge **magma only**. Ironforge showing
   water means §7.3's trap came back.
3. **The canal.** Trade District bridge: water in the canal, at the height of the
   canal walls.
4. **Submersion.** Walk in until the eye goes under — the overlay must fire, and
   must *not* fire while standing on the bridge above it.
5. **The crossing.** Leave Stormwind and come back. The canal must still be there.
   This is the async-adoption race and it fails **silently** — see §7.6.
6. **A/B.** `Draw WMO liquid` off must be bit-identical to the pre-PLAN_15 client.

### 7.6 Two bugs designed out, worth keeping

**Rebuild on a version counter, never on the tile-crossing event.** A WMO is
placed the instant its ADT is read, but its groups — and therefore its MLIQ — are
adopted asynchronously over later frames. A rebuild fired at the crossing runs
before `Model.Liquids` exists and leaves a canal permanently dry, with no
exception and no log line. `WmoRenderer.LiquidVersion` bumps on placement, on
adoption and on reset; `LoadWmoLiquid` is an int compare when nothing moved.
`SYSTEM_INSTANCES.md` records the identical race on async doors.

**`TryGetSurface` no longer returns the first hit.** It now takes the query Z and
returns the **lowest surface above it**, across both open-world and WMO liquid.
The old first-match behaviour was already latent-buggy with overlapping tile
water — whichever tile came first out of a dictionary won — and a canal above a
lake would have made it visible.

---

## 8. Body colour and the highlight mask (2026-07-26)

**The single most important fact about water in this client, and it was wrong for
the whole life of the project until now.**

### 8.1 The vanilla water textures contain no colour

Decoded straight out of `texture.MPQ` (`tools/mpqpy`), mean RGB over the whole
image:

| texture | mean RGB | what it is |
|---|---|---|
| `lava.1.blp` | `0.688 0.089 0.000` | a real coloured surface |
| `slime.1.blp` | `0.268 0.517 0.074` | a real coloured surface |
| `ElwynnGrassBase.blp` | `0.365 0.412 0.009` | control — decoder is sound |
| **`lake_a.1.blp`** | **`0.014 0.014 0.014`** | **near-black greyscale mask** |
| **`ocean_h.1.blp`** | **`0.016 0.016 0.016`** | **near-black greyscale mask** |

`lake_a` peaks at 0.158 luminance. It is a **highlight / caustic overlay**, not a
water surface. Magma and slime are genuinely coloured, which is exactly why those
two have always looked right — they take the early-return branch in `water.frag`
and never reach the code below.

### 8.2 What the shader was doing

`vec3 col = liq.rgb;` — using the mask **as the colour**. So water rendered as a
near-black sheet with faint moving specks. That was true before PLAN_12 and is
the real reason "deep water reads a little dark" sat in §5 for so long.

PLAN_12 then made it worse rather than better: it *multiplied* that near-black
mask by the authored band colour, and Azeroth's river-close is
`(0.000, 0.114, 0.161)` — **red exactly zero**. The remaining specks went to
~0.025 in blue and 0 in red. Nico: *"the top of the water textures are all gone.
It had the animated white/color movement and now it's monocolor static."*

### 8.3 The fix

```glsl
- col *= tint * uTexBright;                        // multiply: annihilates both
+ col = aBody * uTexTint * uTexBright + highlight; // body colour + added sparkle
```

The body colour comes from `LiquidRenderer.RiverBody` / `OceanBody`; shallow and
deep are derived from each (`x1.2` and `x(0.3, 0.5, 0.7)`), so there is **one
colour to dial per liquid, not four**. `uHighlightGain` lifts the 0..0.158 mask
into a visible sparkle; set it to 0 to judge the body colour alone.

Starting values are WoWee's — `(0.10, 0.28, 0.55)` inland, `(0.04, 0.16, 0.38)`
ocean — because WoWee reached the same conclusion independently from the same
DBCs: it loads all 18 `LightIntBand` colour bands, consumes seven, comments
*"... more channels exist (ocean, river, shadow, etc.)"* and hardcodes water
colour per liquid type.

`uAuthoredWater` no longer gates the colour path at all. `LiquidRenderer` decides
whether the body uniforms carry the tuned constants or the Light.dbc bands, so
the shader never branches on it.

### 8.4 STATUS: SUFFICIENT, NOT 1:1 — closed until a much later pass

**This is good enough and is not being refined further for now.** It is a
by-eye match to 1.12, not a verified one, and that is a deliberate, accepted
position rather than an outstanding defect:

- No `refs/` capture exists, so nothing here is measured against the real client.
- The body colours are hand-picked (via WoWee), not authored data.
- `close`/`far` are driven by **depth** in our shader. The evidence points at
  them meaning **distance from camera**: the authored alphas are
  `waterShallow 0.65 / waterDeep 0.50` and `ocean 1.00 / 0.75`, i.e. shallow MORE
  opaque than deep, which is backwards for depth; and across all 426 LightParams
  the close/far pairs show no systematic brightness ordering (river 156 vs 95,
  ocean 91 vs 84).

**Do not reopen this on aesthetics.** Reopen it only with a real-client capture
or a demonstration of what bands 13-16 actually drive. Until then the tuned
constants are the shipping look, and the sliders in Video Options -> Water ->
Advanced are how it gets adjusted.

### 8.5 The authored bands are now testable for the first time

`Authored water colours` is default OFF and labelled `[KNOWN BAD]` in the UI —
but the label is now about history, not mechanism. With the multiply gone,
ticking it swaps the *body colour source* to the Light bands rather than crushing
the texture. That is the A/B PLAN_12 intended and never actually delivered,
because an A/B whose "on" state destroys the image compares nothing.
