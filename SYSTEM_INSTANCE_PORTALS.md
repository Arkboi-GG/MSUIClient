# SYSTEM — Instance Portals (teleport-entrance effect)

**Last updated:** 2026-07-27
**Status:** in active fine-tuning toward 1.12 vanilla. Everything below is live in
the client; the only pending "code" work is whatever tuning you land on next.
**Not compiled in the assistant sandbox** — there is no .NET SDK here. Build in
Visual Studio; the edits are verified structurally (brace/paren/CRLF balance,
git-diff review) but the build is the proof.

---

## 0. Read this first — which "portal" this is

The word *portal* is overloaded in this codebase. There are two completely
separate systems and they must never be confused:

- **Instance portals (THIS DOC).** The swirling teleport disc you walk through to
  enter a dungeon — the Deadmines mine-cart entrance, etc. It is an **M2 particle
  effect** (`InstancePortal.m2` / `INSTANCEPORTAL.MDX`/`MDL`) plus a flat "looking
  glass" film, a whole-scene glow, and (new this session) a fill light that lights
  the room you see *through* it. Owned by `ParticleRenderer` + `GameLoop`.

- **WMO portal culling (NOT this — see `PLAN_10_WMO_PORTALS.md`).** Interior
  visibility: flooding a WMO's portal graph to hide the roof of Stormwind from
  inside. Owned by `WmoRenderer.ComputePortal...`/`UsePortalCulling`. Nothing to
  do with teleporting.

If a future change says "portal," check which one. This doc is *only* the
teleport-entrance effect.

Related docs: `SYSTEM_PARTICLES.md` (the general M2 particle engine this rides on),
`PLAN_14_PARTICLES.md` (the plan the particle engine was built from),
`BENILLA_VS_MSUI_PORTAL.md` (the benilla comparison that seeded the motion/color
facts), `SYSTEM_WMO_INTERIOR_LIGHTING.md` (MOCV interior lighting, the *wrong* lever
for the too-dark interior — see §5).

---

## 1. The effect, top to bottom

A player standing at a dungeon entrance sees, from back to front:

1. **The interior geometry** behind the portal (WMO walls/floor + doodad props),
   now lifted by the **beyond-portal fill light** (§5) so the room is not pitch black.
2. **The "looking glass" film** — a flat, faintly rippling translucent quad on the
   disc plane (§4). Gives the sense of a surface you look *through*.
3. **The converging particle disc** — a spinning ring of sprites that pull inward
   toward the centre, additively blended, tinted toward ocean blue (§3). The centre
   is deliberately hollow (a "see-through" hole) so you read the film/interior
   through it, matching 1.12.
4. **A whole-scene glow (FFXGlow)** — optional bloom that gives the whole thing a
   soft glaze (§6).

The design pivot this session: **stop chasing benilla parity, tune to 1.12.**
benilla got us close (the sphere-kernel motion, the additive look) but is knowingly
different from vanilla. Everything is now a live in-game knob so the look is dialed
against 1.12 directly, not against benilla.

---

## 2. The particle motion (model space, sphere kernel)

`InstancePortal.m2` emitter(s) are **model-space** (M2 flag `0x10`) **sphere**-shape
emitters with **negative emission speed** (particles are born on a ring and pull
*inward* — a converging disc, not a fountain). The disc is swept by an animated
**bone spin**.

In `ParticleRenderer` the model-space path (the `if (pool.ModelSpace)` branches,
`Fill()` ~line 834 and the simulate branch ~509):

- Bone rotation is sampled at `_time * ModelSpinScale` and each particle position is
  `Vector3.Transform(p.Position * PortalScale, boneRot)`, then placed at
  `pool.Origin + Transform(spun, rot)`. `pool.Origin` is the emitter's **absolute
  world** pivot.
- Spawn phase is distributed by `SpawnArms` (clock-face origins, round-robin) +
  `SpawnPhaseJitter`; convergence direction is flipped by `ReverseConverging` where
  `emissionSpeed < 0`.
- Sprite billboards are **±1 corner** quads (a vertex sits at `centre ± size`, so a
  sprite spans `2·size`; `size` is the half-extent — byte-verified against the real
  1.12 client / benilla `quads.rs`). An earlier ±0.5 bug made every sprite
  half-width / quarter-area, which is why the portal never accumulated into a cloud.

**Space note that trips people up:** the particle renderer draws camera-relative
(`world - uCameraOrigin`), but `pool.Origin` and the emitter world positions are
**absolute world**. The WMO/doodad renderers *also* draw camera-relative but compute
`vWorldPos` as `world - cameraPosition` with `uCameraPos = 0`. So when the fill light
(§5) hands a world position to those shaders, the renderer subtracts the camera
itself. MSUI coordinate swap is `Swap(x,y,z) = (x, z, -y)`; the disc lies in the
emitter-local Y-Z plane, normal = local X.

---

## 3. Particle colour (ocean blue) and the hollow centre

- **Colour.** After the over-life ramp (`SampleRamp`), model-space particles get an
  HSV adjust — `AdjustColor(rgba, ParticleHueShift, ParticleSaturation,
  ParticleValue)` — applied **only to model-space (portal) sprites** so torches and
  other world-space effects are untouched. This is how the disc is pushed toward
  ocean blue-green.
- **See-through centre.** `PortalCentreHole` (yards): a portal particle whose radius
  from the pivot is inside the hole fades its alpha linearly to 0 at the centre, so
  you look *through* the disc to the film/interior. This is the model-space hole;
  the legacy world-space path has its own `CentreHoleYards` (different knob).

---

## 4. The "looking glass" film

`ParticleRenderer.DrawPortalSurfaces()` draws a flat quad on the disc plane, **before**
the sprites (so sprites composite over it), alpha-blended:

- Placed at `pool.Origin`; oriented by the emitter's local Y/Z axes; half-extent
  `EmissionAreaLength · pool.Scale · PortalScale · PortalSurfaceSize`.
- Tint = `HsvToRgb(PortalSurfaceHue, PortalSurfaceSat, PortalSurfaceVal)`; peak
  opacity `PortalSurfaceAlpha`; a gentle sin-ripple over `uTime` so it reads as a
  surface, not a decal.
- Inline GLSL (`SurfaceVert`/`SurfaceFrag`) in `ParticleRenderer.cs`.

Toggle: `PortalSurface`.

---

## 5. Beyond-portal fill light (NEW — the dark-interior fix)

**Problem.** In real 1.12 you see a lit room through a dungeon portal; our interiors
read as pitch black. The *wrong* fix (tried and reverted) was
`WmoRenderer.InteriorBrightness`, a multiplier on baked MOCV — it brightens the
interior **where you already stand (pre-portal)**, not the room beyond. Left at `1.0`
(neutral); do not use it for this.

**The fix.** A soft point light placed a little way *past* the portal, into the room,
handed to the world renderers each frame:

- `GameLoop.UpdatePortalFillLight()` (in `Program.Particles.cs`) runs every frame
  before the WMO pass. It finds the nearest instance portal via
  `ParticleRenderer.TryGetNearestPortal(camera, 150yd, out centre)` (nearest
  model-space **sphere** pool origin). If none is near, it sets
  `PortalLightRadius = 0` on both renderers → **light off, exterior untouched.**
- When a portal is near it places the light at `centre + dir · PortalLightOffset`
  where `dir = normalize(centre − eye)` (i.e. *through* the doorway, into the room),
  colour = `HsvToRgb(hue,sat,val) · intensity`, and pushes world-abs position +
  colour + radius onto `WmoRenderer` and `DoodadRenderer`.
- **Shaders** (`wmo.frag`, `doodad.frag`) add, into the *pre-albedo* light term (so
  it brightens the textured surface like baked light does):
  `lighting += uPortalLightColor · atten²`, `atten = clamp(1 − dist/radius, 0, 1)`,
  `dist = distance(uPortalLightPos, vWorldPos)`.
  - **WMO**: gated **off exterior batches** (`uBatchType != 3`) — daylight surfaces
    are never touched.
  - **Doodads**: scaled by `(1 − vLight.a)` so it favours interior-baked props and
    fades out on daylight-lit ones.
- The renderers convert to camera-relative themselves (`PortalLightWorldPos − eye`).

**Scope guarantee (honours the standing constraint that exterior lighting is
off-limits):** the light only exists within `radius` of a point *inside* the
instance, is gated off exterior WMO batches, and is fully **off** whenever no portal
is near. Exterior/daylight lighting is never modified.

**Coverage caveat.** It currently lifts **WMO interior surfaces (walls, floor) and
interior props**. If some still-dark surface turns out to be *terrain* rather than
WMO, the same uniform block would need to be added to `terrain.frag` — deliberately
left out for now because terrain is mostly exterior and that risks the off-limits
rule. Verify in-game which surfaces lift; extend only if needed.

---

## 6. Whole-scene glow (FFXGlow)

`Engine/FfxGlow.cs` — the reference client's bloom, adapted as an **additive**
(glow-only) composite so the base scene/exterior lighting is untouched:
resolve the default framebuffer → ¼ Box downsample → separable Gauss → recombine
`scene + gain · blur²` (square-law). Whole-scene, so it glazes the portal along with
everything bright.

Config: `Render.Glow` (default **off**), `Render.GlowGain` (default 0.5). Live knobs
in the panel. If the portal specks re-merge into "vapour," lower the gain — bloom over
bright additive sprites is one of the things that smears them together.

---

## 7. The specks (size + sharpness) — why the disc can look like "vapour"

Distinct converging **specks** vs a smooth **coloured cloud** is controlled by three
things; the first two were the missing in-game levers, added this session:

- **`SpriteSizeScale`** (panel: *Sprite size x*) — per-sprite footprint multiplier,
  **portal-only** (model-space). Smaller = less overlap = the specks separate out.
- **`SpriteSharpness`** (panel: *Sprite sharpness (mip bias)*) — a mip **LOD bias**
  handed to `particle.frag` as `texture(uTexture, vUv, uMipBias)`, set **per draw
  group**: portal (model-space) groups use the knob, every other effect stays at 0.
  `0` = full trilinear (soft — a shrinking converging speck samples a coarser, softer
  mip and blurs into vapour); **negative sharpens** toward the base level so each
  speck stays crisp.
- **`DensityScale`** (panel: *Density*) — fewer sprites = more distinct specks. This
  one is also mirrored in `ClientConfig.Render.ParticleDensity`, which **overrides**
  the property default at startup (`Program` line ~411), so change **both** if you
  re-default it.

The texture is mipmapped/trilinear on purpose (it killed an earlier "blocky chips"
aliasing). Sharpness trades that anti-aliasing back for crispness — there is a sweet
spot; a very negative bias can bring the chips back.

---

## 8. The control surface — every in-game knob

All tuning is **live in-game** (standing preference: no knobs that aren't in-game).
Panel: dev HUD → **"Particles (PLAN_14)"** (opens expanded by default). Defaults live
in code (`ParticleRenderer` property initializers; Density also in `ClientConfig`).

| Panel label | Field | What it does |
|---|---|---|
| Density | `DensityScale` (+`ClientConfig.ParticleDensity`) | sprite count / overlap |
| Sprite size x | `SpriteSizeScale` | per-speck footprint (portal only) |
| Sprite sharpness (mip bias) | `SpriteSharpness` | crisp specks (−) vs soft vapour (0); portal only |
| Particle hue shift / saturation / brightness | `ParticleHueShift/Saturation/Value` | disc colour (portal only) → ocean blue |
| Portal surface film | `PortalSurface` | the looking-glass quad on/off |
| surface opacity / reach x | `PortalSurfaceAlpha` / `PortalSurfaceSize` | film alpha / radius |
| film hue / saturation / brightness | `PortalSurfaceHue/Sat/Val` | film colour |
| Portal see-through centre (yd) | `PortalCentreHole` | hollow centre radius |
| Portal circle size x | `PortalScale` | scales the whole disc about its centre |
| Solo emitter (−1=all) | `SoloEmitter` | debug: draw one emitter index only |
| Spin rate x | `ModelSpinScale` | model-space disc spin (1.0 = authored). NOTE: the legacy world-space `SpinRateScale` does **nothing** for this portal |
| Beyond-portal fill light | `PortalLight` | the interior fill light on/off |
| fill intensity / radius (yd) / reach past portal (yd) | `PortalLightIntensity/Radius/Offset` | strength / falloff sphere / how far past the doorway |
| light hue / saturation / brightness | `PortalLightHue/Sat/Val` | fill-light colour |
| Glow (FFXGlow bloom) / Glow gain | `FfxGlow.Enabled/Gain` | whole-scene bloom |
| Spawn arms / Phase jitter | `SpawnArms` / `SpawnPhaseJitter` | spawn-phase distribution |
| Centre hole (yd) | `CentreHoleYards` | legacy world-space hole (not the portal's) |
| Reverse converging / Density at far end | `ReverseConverging` / `ReverseRamp` | world-space converging behaviour |
| Simulate within (yd) | `SimulationDistance` | sim cull distance |

---

## 9. Current presets (defaults as of 2026-07-27)

These are baked as the code defaults so the client boots with this look.

| Knob | Value |
|---|---|
| Density | **0.89** |
| Sprite size x | **1.77** |
| Sprite sharpness (mip bias) | **−4.00** |
| Particle hue shift | 0.000 |
| Particle saturation | 1.15 |
| Particle brightness | 1.00 |
| Portal surface film | ON |
| surface opacity | 0.106 |
| surface reach x | 1.20 |
| film hue | 0.424 |
| film saturation | 1.00 |
| film brightness | **1.06** |
| Portal see-through centre (yd) | 4.33 |
| Portal circle size x | 1.01 |
| Solo emitter | −1 (all) |
| Spin rate x | 0.86 |
| Beyond-portal fill light | ON |
| fill intensity | 0.85 |
| fill radius (yd) | 34 |
| reach past portal (yd) | 10 |
| light hue | 0.090 |
| light saturation | 0.16 |
| light brightness | **0.67** |
| Spawn arms | 24 |
| Phase jitter | 0.25 |
| Centre hole (yd) | 4.74 |
| Reverse converging | ON |
| Density at far end | OFF |
| Simulate within (yd) | 120 |

(Values in **bold** are the ones changed in the final preset pass this session; the
rest were already the defaults.)

---

## 10. Files (where each piece lives)

- `World/Particles/ParticleRenderer.cs` — the whole particle disc: model-space sphere
  kernel, spin, convergence, HSV colour + `AdjustColor`/`HsvToRgb`, see-through
  centre, sprite size, per-group mip bias, the film (`DrawPortalSurfaces` + inline
  GLSL), and `TryGetNearestPortal` / `PortalLightRgb` for the fill light. All portal
  tuning properties live here.
- `Shaders/particle.vert` / `particle.frag` — ±1 billboard; `texture(..., uMipBias)`.
- `Engine/FfxGlow.cs` — whole-scene additive bloom.
- `Shaders/wmo.frag`, `World/Wmo/WmoRenderer.cs` — fill-light uniforms
  (`uPortalLightPos/Color/Radius`), gated off exterior batches; `PortalLight*`
  properties. (`uInteriorBrightness` here is the *reverted* MOCV lever — leave at 1.0.)
- `Shaders/doodad.frag`, `World/Doodads/DoodadRenderer.cs` — fill-light uniforms,
  weighted by `(1 − vLight.a)`; `PortalLight*` properties. Also the WMO-supersedes-
  terrain emitter dedup (`RemoveNearEmitterPlacement`) so the portal is placed once.
- `Program.cs` — `UpdatePortalFillLight()` call before the WMO pass; `_glow` wiring.
- `Program.Particles.cs` — `UpdatePortalFillLight()` body + the entire panel.
- `ClientConfig.cs` — `Render.Glow`, `Render.GlowGain`, `Render.ParticleDensity`.

---

## 11. Gotchas / rules for future-you

- **Exterior lighting is off-limits.** benilla knowingly differs from 1.12 vanilla and
  prefers his exterior; every lighting change here must leave exterior/daylight
  untouched. The fill light is scoped and gated precisely for this reason.
- **In-game knobs only.** Do not add a tuning value that isn't a live slider. Defaults
  are baked in code once a look is agreed.
- **There is ONE portal.** The panel's "if one still shows 2 rings, portal is placed
  twice" hint and any "2 emitters" reading are misleading — confirmed single portal;
  the near-coincident terrain/WMO duplicate is deduped in `DoodadRenderer`.
- **Spin is `ModelSpinScale`** (model space). The legacy world-space `SpinRateScale`
  is inert for the portal — don't wire the spin slider back to it.
- **Density has two homes** — the property default *and* `ClientConfig.ParticleDensity`
  (the latter wins at startup). Change both.
- **Build in Visual Studio.** Nothing here is compiled in the assistant sandbox.

---

## 12. Where we are / next

- **Done this session:** sphere-kernel motion confirmed; whole-scene glow (additive);
  looking-glass film with HSV colour; ocean-blue particle colour controls; see-through
  centre; portal-disc size knob; spin slider repointed to the model-space rate;
  **beyond-portal fill light** (walls/floor/props, exterior-safe); **sprite size +
  sharpness** controls to bring the specks back from "vapour"; final preset pass baked
  as defaults (§9).
- **Open / candidate next steps:**
  1. Confirm in-game exactly which surfaces the fill light lifts. If the floor or any
     still-dark surface is *terrain*, add the same uniform block to `terrain.frag`.
  2. Keep dialing specks vs cloud against 1.12 references (size / density / sharpness),
     and the fill-light colour/intensity to match the room tone you see through a real
     1.12 portal.
  3. If the glow smears the specks, decide a default `GlowGain` (or leave Glow off by
     default and treat it as an opt-in glaze).
