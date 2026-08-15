# Plan 18 — Clouds (sky cloud layers + skybox models)

Two cloud "spots" were raised together: the glue-screen (login / character-select)
clouds, and in-world clouds. This plan closes the first as **not a bug** (with the
evidence, so it is not re-investigated) and specifies the second as a two-phase
addition on top of the already-resolved-but-unused Light.dbc data.

## STATUS

- **Phase 0 (glue clouds):** closed as not-a-bug (section 0). No code change.
- **Phase 1 (in-world sky cloud layers): BUILT** (2026-08-15). The procedural cloud
  field renders over the screen-space sky, driven by the authored Light.dbc cloud
  bands + density, with the sun-glow. Byte-faithful kernel (23/23 checks in
  `tools/cloud-field-check`), verified in-world (a forced-density capture shows
  scattered sun-lit clouds over the gradient). Owner A/B against a real-1.12 ref is
  the remaining sign-off. **See "AS-BUILT (Phase 1)" below** for what shipped and the
  two corrections to this plan it turned up.
- **Phase 2 (skybox M2 models): BUILT (2026-08-15) for the LightParams path.** The
  `LightSkybox.dbc` reader + a camera-centred M2 `SkyboxRenderer` render a zone's sky
  model over the gradient/clouds, before the world. Verified in-world (forced
  CavernsOfTimeSky shows its starfield+nebula dome). KEY FINDING below: the
  LightParams-driven path only ever resolves DeathClouds (the ghost world); the
  dramatic INSTANCE skies (Blackrock/Stratholme/DireMaul/CoT) come via WMO-MOSB,
  which is a natural Phase 2b. See "AS-BUILT (Phase 2)".

## AS-BUILT (Phase 1)

Files: `World/CloudField.cs` (NEW - the kernel), `World/SkyRenderer.cs` (owns the
field + the tile texture + the tick/upload; new `CloudsEnabled` + `CloudDensityOverride`
dev knobs), `Shaders/sky.frag` (samples + composites the tile), `World/WorldAtmosphere.cs`
(`SetAuthoredClouds` + accessors + the `AuthoredCloudsReady` gate), `World/ExteriorLighting.cs`
(`CloudSunGlow/CloudSlope/CloudBase/CloudDensity` on `Sample`), `Formats/DbcReader.cs`
(the band-index constants), `GameLoop/Dev/GameLoop.LightProbe.cs` (feeds the bands each
frame + the "Draw clouds" / density-override dev controls).

Two corrections to this plan, learned from benilla's byte-exact kernel:

1. **Band ROLES, not the DbcReader names.** The DbcReader labels sub-10/11/12
   "cloud emissive / cloud L1 ambient / cloud L2 ambient", but WoW.exe's colour pass
   (benilla `clouds/kernel.rs`, `lighting/mod.rs cloud_colors`) uses them as
   **sun-GLOW (10) / gradient SLOPE (11) / gradient BASE (12)**. Density is
   FloatBand **3**. There is no separate always-on "emissive" term - a cloud's base
   colour (band 12) is its unlit look (dark blue at night), and the sun-glow (band 10)
   brightens the sunward side by day. "Provide light" = base + sun-glow, per the data.
2. **The render projection is OURS, and MUST be, for a screen-space pass.** benilla
   renders the tile on a camera-centred DOME mesh and reserves `project_cells` for the
   scaled-offset glare-occlusion sampler; fed a unit view ray, `project_cells` clamps
   the whole upper sky to the tile centre (a uniform wash - the first wrong result this
   session). `CloudField.SkyProject` (azimuthal-equidistant: zenith->tile centre,
   horizon->rim) is used by BOTH the GPU sampling and the CPU sun-glow placement, so the
   glow lands under the sun. `project_cells`/`Coverage` are kept, faithful, for a future
   glare pass. This is the one deliberate deviation from the bytes, and it is a
   rendering-topology choice (dome vs screen-space), not a colour/coverage one.

The kernel itself IS byte-faithful: 4-octave toroidal value noise -> density threshold
-> tone curve -> colour pass, with the frozen PERM/CURVE/gradient/fade tables and the
8-key cloud-glow day envelope, all transcribed from benilla and checked against its
tests. Owner night-lighting law untouched (clouds render into the sky only).

## AS-BUILT (Phase 2)

Files: `World/SkyboxRenderer.cs` (NEW - loads an M2/MDX skybox, renders it
camera-centred + unlit + depth-off, over the sky and before the world, with the M2's
texture-transform UV scroll via `M2TrackSampling`), `Shaders/skybox.{vert,frag}` (NEW),
`Formats/DbcReader.cs` (`LightSkyboxTable`: skybox id -> model path), `World/ExteriorLighting.cs`
(loads LightSkybox.dbc + `SkyboxPath(id)`), `Program.cs` (owns `_skybox`, resolves the
active id from the dominant LightParams each frame, draws it right after the sky pass),
`GameLoop/Dev/GameLoop.LightProbe.cs` (a "Draw skybox" toggle + a force-model dropdown for
inspecting any of the six models anywhere). `tools/skybox-dump` dumps the DBC.

**KEY FINDING - the LightParams skybox path is thin.** LightSkybox.dbc has 6 models
(StratholmeSkybox, PortalWorldLegionSky, DeathClouds, Stars, CavernsOfTimeSky,
DireMaulSkyBox), but only **LightParams 1-5 reference one, all DeathClouds** - the
ghost/death world's sky. NO Light.dbc zone's ParamsClear carries a skybox. So outdoor
zones use the procedural sky (Phase 1); the LightParams-driven skybox only ever shows
in the ghost world. The dramatic INSTANCE skies (Stratholme/DireMaul/Caverns of Time,
and Blackrock) are wired via the **WMO MOSB** chunk (a WMO root names a skybox model,
gated by group flag 0x40000), NOT via Light.dbc. That is a separate integration:

**Phase 2b (proposed): WMO-MOSB skyboxes.** Reuse this same `SkyboxRenderer`, but drive
its model from the current interior WMO root's MOSB name (when the containing group has
SHOW_SKYBOX 0x40000) instead of the LightParams id. This is what actually lights up the
"some maps/places" skies. Not started; awaiting the owner's call.

Verification note: over a bright DAY sky, additive night skyboxes (Stars) and subtle
cloud skyboxes (DeathClouds) read as near-invisible - correct, they are authored for
their own dark/ghost lighting. The geometry + textured render were both confirmed
(magenta geometry check + CavernsOfTime's textured starfield dome).

Ground rules that gate this plan:
- The **night-lighting law is owner-settled** (raw bands x dnc intensity, never-setting
  sun; see `WorldAtmosphere.ParityDaylightIntensity` and SYSTEM_EXTERIOR_LIGHTING).
  Clouds are a **sky-only visual plus their own emissive** - they do NOT feed back into
  terrain/unit lighting. Nothing in this plan touches that law.
- The in-world sky is deliberately a **screen-space pass, no dome mesh**
  (`SkyRenderer` + `Shaders/sky.frag`). Clouds extend that pass; the no-dome decision
  stands.

---

## 0. Session finding — the GLUE clouds are NOT frozen (closed, not-a-bug)

Reported symptom: login + char-select clouds look "completely frozen." Investigated
empirically (live client + `tools/glue-cloud-dump`, this session):

- The cloud sheets DO animate. Instrumented `GlueScene.Render`: `MM_CLOUDS_01`'s UV
  offset advances a clean **-0.05 U/s at a steady 60 fps**, wrapping at t=20 s. Data,
  batch->transform resolution, linear interpolation, global-sequence period, and the
  `_time` clock are all correct. `GlueScene`'s own raw parse and `M2Reader`'s verified
  `TextureTransforms`/`GetTextureTransformForBatch` agree exactly (no mismatch).
- It reads as frozen because the motion is genuinely gentle: each cloud sheet is a
  **2-key linear translation of -1.0 U over a 20 s / 30 s / 40 s global sequence**
  (`UI_MainMenu` gseq 4/5/6; `UI_Human` gseq 0/2), and the cloud texture **tiles 3-4x
  across the sheet** (U span 0..4 / 0..3), so a full -1.0 U scroll slides the visible
  pattern by one tile - about **0.6-1.25 % of the sky per second**. That is almost
  certainly byte-faithful to 1.12 (same authored global-sequence durations, same
  sampling).
- **Owner decision: leave it faithful.** No code change. The only lever would have been
  a deliberate non-parity scroll multiplier, which was declined.

`tools/glue-cloud-dump` (added this session) dumps any glue/skybox M2's global
sequences, texture transforms, per-batch transform resolution, and UV tiling span. Kept
for the in-world work below (skybox M2 inspection).

---

## 1. Problem

**In-world (map 0, Northshire vantage, and any authored-sky zone):** the sky is a flat
five-band elevation gradient (`SkyRenderer`). There are no clouds at all, and no
skybox. Two authored data sets resolve every frame and drive nothing:

- `LightIntBand` **9 cloud sun**, **10 cloud emissive**, **11 cloud L1 ambient**,
  **12 cloud L2 ambient**, plus `LightFloatBand` **3 cloud density** (and **2 celestial
  glow-through**). Confirmed in `ExteriorLighting.Sample.Colors[9..12]` /
  `Floats[2..3]` and the band-name tables in `DbcReader`.
- `LightParams.SkyboxId` (parsed, `DbcReader:665/703`; shown by the probe,
  `GameLoop.LightProbe:423`). Zones with a dramatic authored sky (a `LightSkybox` M2)
  show only the gradient.

So "in some maps/places the clouds are in the sky and provide light" (owner's words)
has two faithful mechanisms behind it, both currently inert.

## 2. Class

**Emulation-core** for both phases (measured against the real 1.12 client). Two
quantities are ours (additions), exactly like the existing sky-band stop heights: the
cloud-layer **elevation onset/feather**, and the cloud **scroll speeds** where the data
does not pin them. Everything else (colours, density, which skybox, its animation) is
authored and must be read, not invented.

## 3. Target

Real-client `refs/<vantage>.png` captures to take BEFORE coding (field 7 depends on
them):
1. A cloud-band zone under clear day (clouds lit warm toward the sun) AND at night
   (clouds self-lit by the emissive band) - the "provide light" case.
2. A `LightSkybox` zone (non-zero `SkyboxId`) showing the authored sky model.

If these captures do not exist yet, taking them is task one (per the template's field-7
rule).

## 4. Key design decisions

1. **Clouds stay in the screen-space sky pass.** `sky.frag` already reconstructs a
   per-pixel ray direction and shades by elevation `dir.z`. Cloud layers sample a cloud
   texture by the **horizontal** ray direction (`dir.xy`), fade in above a horizon
   onset, and composite OVER the gradient. No dome, correct at any FOV/orientation.
2. **Two layers, matching vanilla's L1/L2.** Each layer scrolls independently and is
   tinted by its ambient band (`Colors[11]`, `Colors[12]`); the lit side warms toward
   `cloud sun` (`Colors[9]`) by the sun direction; `cloud emissive` (`Colors[10]`) is a
   floor that makes clouds glow at night. `cloud density` (`Floats[3]`) drives coverage
   / alpha. Start with one layer as the fallback (field 9).
3. **Skyboxes are M2 models**, camera-centred, depth-write OFF, drawn AFTER the sky
   gradient+clouds and BEFORE the world. Reuse `M2Reader` + the validated
   texture-transform UV scroll (`M2TrackSampling` / `DoodadRenderer.UvOffsetFor`, the
   same path `GlueScene` uses). Many skybox M2s carry emissive cloud layers - more of
   the "provide light".
4. **No feedback into world lighting.** Clouds/skybox render into the sky only. The
   owner-settled diffuse/ambient/dnc path is untouched. (Cloud shadows on terrain are
   explicitly out of scope.)
5. **Everything is a toggle**, mirroring `SkyRenderer.Enabled` and
   `WorldAtmosphere.UseAuthoredData`, so any zone that reads wrong degrades to today's
   look rather than hard-blocking.

## 5. Resources (exact files - check before writing)

- `World/SkyRenderer.cs` + `Shaders/sky.frag`, `Shaders/sky.vert` - the pass to extend
  (Phase 1). New uniforms + a cloud sampler; keep the `Enabled=false` path byte-identical.
- `World/WorldAtmosphere.cs` - mirror the `SkyTop/SkyMiddle/...` exposure to add
  `CloudSun/CloudEmissive/CloudAmb1/CloudAmb2/CloudDensity` (+ the `SetAuthored` gate),
  fed the same way the sky bands are.
- `World/ExteriorLighting.cs` - `Sample.Colors[9..12]`, `Sample.Floats[2..3]` already
  resolved and blended across zones. Add named accessors (`CloudSun` etc.) beside
  `SunColor => Colors[8]`.
- `GameLoop/Dev/GameLoop.LightProbe.cs` - where the resolved `Sample` is handed to
  `WorldAtmosphere.SetAuthored` each frame, and where the sky stop sliders live. Add the
  cloud plumbing + dev toggles/sliders here (Phase 1) and the skybox toggle (Phase 2).
- `Formats/DbcReader.cs` - `LightParamsRow.SkyboxId` (field 2) is parsed. **`LightSkybox.dbc`
  is NOT parsed yet** (only the id is read/reported). Phase 2 adds a `LightSkyboxTable`
  reader: `SkyboxId -> M2 model path` (LightSkybox.dbc: id, filename, flags).
- `World/Doodads/DoodadRenderer.cs` (`UvOffsetFor`, line ~2019) and `Engine/GlueScene.cs`
  - reference implementations of the M2 batch + texture-transform draw to reuse for the
  new `SkyboxRenderer` (Phase 2).
- `tools/glue-cloud-dump` - dump a skybox M2's transforms/batches before wiring it.
- SYSTEM_EXTERIOR_LIGHTING.md "Not yet applied" section - the standing record that
  these bands + the skybox id are resolved and unused; update it as each lands.

## 6. Tools / instrument

- **Light probe** (`GameLoop.LightProbe`) already prints resolved cloud bands + skybox
  id per vantage. Extend it with: a "Draw clouds" toggle, cloud onset/feather + scroll
  sliders (like the existing sky-stop sliders), and a "Draw skybox" toggle. This is the
  A/B instrument.
- **`tools/glue-cloud-dump`** for any skybox M2's data.
- **Vantage capture** (PLAN_01) for the ref A/B.

If the probe cannot isolate "cloud band wrong" from "cloud layer geometry wrong", add a
readout that prints the sampled cloud colour+alpha at screen-centre (task one for that
step).

## 7. Test protocol

**Phase 1 (cloud layers).** Vantage: a spot where the probe shows non-zero
`Colors[9..12]` and `Floats[3]`. Capture `refs/<vantage>_day.png` and `_night.png` from
the real client first. Then, from the same vantage:
- Clouds ON vs OFF: OFF must reproduce today's gradient pixel-for-pixel (diff the
  screenshot).
- Day: clouds visibly scroll and warm toward the sun (drag time-of-day; the lit edge
  follows the sun azimuth).
- Night: clouds remain faintly self-lit (emissive floor), not black, not day-bright.
- Density: raise/lower `cloud density` in the probe; coverage thickens/thins.
- A night terrain vantage is UNCHANGED with clouds off, confirming no lighting-law
  regression.

**Phase 2 (skybox).** Vantage: a zone with non-zero `SkyboxId`. Capture `refs/<vantage>.png`.
Verify the M2 skybox renders camera-locked (does not parallax with movement), animates
(texture transforms scroll / bones move), composites over gradient+clouds, and disables
cleanly to gradient+clouds. `glue-cloud-dump` output matches what draws (batch count,
transforms).

## 8. Definition of done

- Phase 1: cloud-band zones match the day+night refs by eye; the probe's band readout
  explains any residual; clouds OFF == today's sky exactly.
- Phase 2: skybox zones match the ref; skybox OFF == gradient+clouds.
- A night terrain vantage is unchanged from current with clouds+skybox off (owner
  night-lighting law provably untouched).

## 9. Fallback

- Phase 1 smallest win: a SINGLE cloud layer driven by `cloud L1 ambient` + `cloud
  density` + `cloud emissive`, no sun-lit warming. Still "clouds that provide light".
- Phase 2 smallest win: render the skybox M2 STATICALLY (no animation) for the handful
  of marquee zones; add transform/bone animation after.
- Any zone that reads wrong: the per-feature toggle (and, if needed, a per-zone skip
  like `UseAuthoredData`) prevents a hard block.

## 10. Reconciliation

- **Extends PLAN_09 (exterior lighting):** consumes its resolved-but-unused cloud bands
  (`Colors[9..12]`, `Floats[2..3]`) and `LightParams.SkyboxId`. No change to PLAN_09's
  resolve/apply split or the lighting law.
- **`SkyRenderer` gains a cloud pass;** the screen-space "no dome" decision is preserved.
- **DBC layer gains a `LightSkybox.dbc` reader** (Phase 2), new but self-contained.
- **New `SkyboxRenderer`** reuses the existing M2 draw + `M2TrackSampling`; no change to
  `DoodadRenderer`/`GlueScene`.
- **Glue clouds:** closed as not-a-bug this session (section 0); no code change.
- Build order: Phase 1 before Phase 2 (universal, reuses the existing sky pass + data;
  Phase 2 needs the new skybox-M2 infra). Owner selected "full plan first" - this is it.
