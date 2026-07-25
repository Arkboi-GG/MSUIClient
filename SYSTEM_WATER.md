# System — Water / Liquid

**Open-world liquid: lakes, rivers, ocean, slime and magma.** One of the
per-system docs the handbook indexes (see PROJECT_HANDBOOK.md §1.2). Read this
plus the handbook's cross-cutting ground truth (§3.1 coordinates, §8.5 shader
ASCII rule, §11 working agreements) before touching water. You should not need
the rest of the handbook for a water change.

Version: Draft 2 — 2026-07-24 ("real vanilla textures + live tuning" pass)
Previous: Draft 1 — first water system, procedural "look at WoWee" pass.
Owner files: `World/LiquidRenderer.cs`, `Shaders/water.vert`, `Shaders/water.frag`,
`Shaders/underwater.vert`, `Shaders/underwater.frag`, the MCLQ parse in
`Formats/AdtTerrainReader.cs`, and the render/atmosphere/HUD wiring plus the
`WaterTuningWindow` in `Program.cs` / `Program.DevTools.cs`.

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
(`Program.WaterTuningWindow`, DevTools only) — a second window next to the main
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

- **Deep water still reads a little dark.** A tuning target, not a bug — push
  Deep darkening / Base brightness in the HUD, then bake the values.
- **Player interaction ripples.** The concentric wake that spreads from a moving
  swimmer (WoWee feeds player XY + a ripple-strength scalar into its water vertex
  shader). Designed but NOT wired here yet — the water shader has no player-position
  input. This is the most likely next feature.
- **MCLQ liquid-type accuracy.** If the `under you` read-out ever shows a river
  tagged as ocean, the fix is in `ParseMclqLayers`' type detection (read the type
  from the MCNK header flags), not the texture.
- **WMO liquid (MLIQ).** Stormwind canals, fountains, indoor pools — parsed from
  MLIQ in local space, not ADT MCLQ. Not rendered.
- **`LiquidType.dbc` colours/materials.** Textures are now real, but the shader's
  colour/lighting constants are hand-tuned, not read from the DBC.
- **The ocean and river colours are invented ~~and unread~~ - WIRED 2026-07-25 by
  PLAN_12, and NOT YET VERIFIED.** `LiquidRenderer` now takes bands 13-16 and the
  `LightParams` alphas through `WorldAtmosphere`, behind
  `Video Options -> Water -> Authored water colours`, whose OFF state is
  bit-identical to the constants below. **Before believing it, run PLAN_12 §7 -
  step 1 in particular, because the river pair reads inverted (see H4).** The
  original entry follows as the record of what was invented: `LightIntBand` bands **13 (ocean close), 14
  (ocean far), 15 (river close), 16 (river far)** are resolved per zone and per
  time of day by `ExteriorLighting` and surfaced by the light probe —
  deliberately, so this entry could be written. `LiquidRenderer` has not been
  touched. **SYSTEM_EXTERIOR_LIGHTING.md §2.3, §7.** Measured for Azeroth's map
  default at noon: ocean close `0.380 0.510 0.718`, ocean far
  `0.067 0.294 0.349`, river close `0.000 0.114 0.161`, river far
  `0.310 0.365 0.078`. This is the same move foliage made with
  `GroundEffectTexture` and exterior lighting made with `Light.dbc`: stop tuning,
  start reading. `LightParams` also carries `waterShallowAlpha` /
  `waterDeepAlpha` / `oceanShallowAlpha` / `oceanDeepAlpha` (PLAN_09 §11), which
  is the authored answer to the "deep water reads a little dark" item at the top
  of this list — **push those sliders no further until they have been checked
  against the data.**
- **Swim physics / buoyancy.** Rendering only. `TryGetSurface` is the hook.

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
