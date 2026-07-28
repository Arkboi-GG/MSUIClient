# Login Glue Scene (UI_MainMenu) — status & handoff

Scope: the 1.12 login screen. `Engine/GlueScene.cs` renders
`Interface\Glues\Models\UI_MainMenu\UI_MainMenu.m2` (the burning gate) fullscreen
behind the login UI, through the model's own authored camera, with fog, an authored
light rig, animated M2Colour tracks, and 28 particle emitters (the brazier fires)
driven by its own `World/Particles/ParticleRenderer`.

benilla is the byte-faithful ground truth for everything below:
`crates/benilla/src/particles/{sim.rs,quads.rs}`, `crates/benilla/src/particles.rs`
(`emit_local`), `crates/benilla-formats/src/particles.rs` (`OverLife`/`CellRamp`),
and `crates/benilla/src/portrait/glue_booth.rs` + `login/mod.rs` for the framing.

## Where it stands

The scene loads, is lit, and burns. Static mesh + authored camera + fog work.
The light rig gives it depth (it was flat/full-bright before). Animated M2Colour
tint + UV scroll are sampled. Screen corners are cleared to a dark sky fill
(they used to show the blue backdrop). The brazier flames now exist, rise upward
from the top of each brazier, and are much brighter — this was the last pass.

Nico's read after that pass: "flames exist, brighter, roughly correct, but too
jittery. Entire color is still too far from OG, and we need to zoom in a bit more."
That is the honest current state — the three items below are open.

## What the last pass actually fixed (verified)

The flame was omnidirectional and faint. Two portal-legacy mechanisms were hitting
the world-space brazier emitters and neither belongs in a flame:

1. **Direction.** The world-space `Spawn` branch computed velocity with WoWee's
   linear jitter `dir = (S11·hRange, S11·hRange, 1 + S11·vRange)`. That was tuned
   against the InstancePortal (hRange 0). A brazier authors hRange = 2π, so the
   ±6.28 on X/Y buried the 1.0 on Z and normalized to almost pure horizontal — the
   sideways spray. Replaced with benilla's spherical cone (`emit_local`,
   `particles.rs:314-347`), the same kernel the model-space swirls already use:
   `dir = (sinθcosφ, sinθsinφ, cosθ)`, θ=S11·verticalRange, φ=S11·horizontalRange.
   Measured on the real em24 params: fraction of particles pointing up went
   **0.4% → 100%**, mean horizontal component **0.957 → 0.044**.

2. **Brightness.** The world-space `Fill` applied `CentreHoleYards` (default 4.74yd)
   — a portal center-fade — multiplying every sprite within 4.74yd of its emitter by
   `r/4.74`. A brazier flame lives ~0.5yd from its emitter, so every sprite was cut
   to ~0.1 alpha. `GlueScene.TryInitParticles` now sets `CentreHoleYards = 0` and
   `SpawnArms = 0` on its renderer (those knobs are portal-only).

Files touched: `World/Particles/ParticleRenderer.cs` (world-space `Spawn` branch),
`Engine/GlueScene.cs` (`TryInitParticles`). The portal itself is a **model-space**
emitter and never reaches the world-space branch, so nothing it needs was lost.

Real brazier params (em24/26, dumped): plane shape, texture `FlameLick.blp` (4×4
flipbook), blend 4 (ADD), speed 0.389, speedVar 0.55, vertRange 0.087 (~5°),
horizRange 6.283 (2π), gravity 0, life 1.3s, rate 20/s, area 0.222, MidPoint 0.50,
colorkeys red(α200) → orange(α255) → warm-white(α0).

## Open item 1 — flame is too jittery

Strongest lead (unconfirmed): **the flipbook is global-time lockstep.**
`ParticleRenderer.RenderInternal` picks ONE cell for the whole draw group each frame:

```
int idx = ((int)(Time * 24f) % cells + cells) % cells;   // ~24 fps
```

and hands it to the `uCellRect` uniform. So every flame sprite in every brazier snaps
to the *same* FlameLick cell at once, and the whole flame flips cell in lockstep 24
times a second. That reads as flicker/jitter rather than a living fire.

benilla drives the cell **per particle, by the particle's own age** — each particle
walks the sheet over its life via the `head_cells` `(begin,end)` ramp (two segments
split at `MidPoint`), plus a per-particle twinkle LUT that de-syncs the flicker
(`quads.rs` `twinkle_noise`, `OverLife::sample` in `benilla-formats/src/particles.rs`).
Head-cell offsets are at record `+0x168/+0x16a` (seg A) and `+0x16e/+0x170` (seg B),
repeat at `+0x16c/+0x172`; `MidPoint` is `+0x14c`, which `M2Reader` already parses.
`M2Reader.M2ParticleEmitter` does NOT yet parse the head_cells.

To fix next pass: parse `head_cells` into `M2ParticleEmitter`, add a per-particle
`SampleCell(ageFraction)` mirroring benilla's `CellRamp`/`OverLife`, and upload the
cell **per instance** (a `Vector4 CellRect` on `GpuParticle` + a `VertexAttribPointer`
at location 4 + an `aCellRect` attribute in `particle.vert`) instead of the per-group
`uCellRect` uniform.

⚠️ That instance buffer is **shared with the model-space swirls**. Nico's standing
rule: do not touch the swirls. Non-flipbook pools (rows·cols == 1, e.g. the GLOWBALL
swirls) must upload `CellRect = (0,0,1,1)` so their UVs and appearance stay
byte-identical.

Secondary, lower-priority contributors worth ruling out first: speedVar 0.55 + short
1.3s life + low steady-state count (~23 sprites/brazier) can make birth/death pop
read as jitter. **Verify which it is** before building the per-particle flipbook —
e.g. temporarily freeze the flipbook cell and see if the jitter survives.

## Open item 2 — overall color is too far from OG

Honest status: **no side-by-side pixel comparison against a real 1.12 login has been
done.** Every color decision so far (fog strength, light rig, tint) is eyeballed. The
first move is to capture the real login and diff it, not to keep nudging constants.

Leads, roughly prioritized:

- **Gamma / color space.** benilla decodes authored particle RGB to linear once, in
  the fragment shader, and flags an *open* additive-composite question: its bonfire
  core is still off vs the reference because it sums linear and encodes once, while
  the reference sums gamma bytes in an LDR framebuffer (benilla decision 0148 — an
  acknowledged unresolved item, not a solved one). MSUI's particle path multiplies
  the raw BGRA ramp × texture with no gamma handling at all. A whole-scene warmth
  that's "off" is consistent with a gamma/composite mismatch. This likely affects the
  mesh path too, not just the flames.
- **The fog is a hand-tuned hack.** Fog colour is `(0.25, 0.06, 0.015)` (from
  benilla's `glue_booth` MainMenu value) blended at a flat strength over the frame.
  It is standing in for the model's real orange sky, which should come from
  UI_MainMenu's own sky texture + its animated colour tracks. If the sky/colour
  tracks aren't fully driving the backdrop, the fog constant is tinting the whole
  frame the wrong way. Confirm the current fog blend strength in `GlueScene` — this
  doc was written with the bridge down and could not re-read the live value.
- **The light rig is authored-guessed**, not measured against the file's lights
  (header notes values like brazier light ≈ (0.843,0.498,0.192)×2.0, range 3..6.5,
  plus a green valley light). Worth checking those against the actual MODL/lights
  block rather than trusting the guesses.

## Open item 3 — zoom / framing (needs to be zoomed in a bit more)

The camera is the model's authored camera 0 (`TryParseCamera` → eye/target/fov). The
FOV is treated as **diagonal** and converted to vertical for the projection. Nico
wants it framed tighter than it currently sits.

Leads: the diagonal→vertical FOV conversion may be over-widening the shot; the
eye/target parse may be slightly off; and benilla frames the same glue booth its own
way (`portrait/glue_booth.rs` + `login/mod.rs`) — compare the eye distance and FOV
benilla uses before guessing. The quick knobs are lowering `fovy` or pulling `_eye`
toward `_target`, but verify against benilla's framing rather than tuning blind.

## Ground rules (Nico)

- Do not touch the model-space swirling particles — they already look right.
- Verify empirically — dumps, pixel diffs, measured angles — don't tune constants
  blind. benilla is the byte-faithful reference.
- Prefer complete-file replacements over diffs; docs use LF, C#/shader files use CRLF.
- Shipped documentation stays server-agnostic.

## Key files

- `Engine/GlueScene.cs` — the scene: mesh, authored camera, fog, light rig, M2Colour
  tracks, emitter setup, particle `Simulate`/`Render`, `TryInitParticles`.
- `World/Particles/ParticleRenderer.cs` — `Spawn` (emission kernel; world-space vs
  model-space), `Fill`, `RenderInternal` (the flipbook + mip bias), the portal knobs.
- `Shaders/particle.vert` / `particle.frag` — camera-facing billboard, `uCellRect`
  flipbook remap, `uMipBias`.
- `Formats/M2Reader.cs` — `M2ParticleEmitter` (parses rows/cols, MidPoint, color/scale
  keys; does **not** yet parse `head_cells`).
