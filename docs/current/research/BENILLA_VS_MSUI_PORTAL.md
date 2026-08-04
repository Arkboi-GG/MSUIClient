# Why benilla's InstancePortal is right and MSUIClient's is "flat wrong" — full proof

Date: 2026-07-26. Method: five agents read both particle pipelines in full; every decisive
claim below was re-verified by hand against the staged source. Citations are `file:line` from
the code as it stands today. benilla = the Rust/Bevy reference (renders the Deadmines-style
teleport vortex correctly); MSUIClient = the C# client whose portal is wrong.

The InstancePortal effect is a **particles-only M2** — the swirl has zero render batches
(`benilla/src/doodad_anim.rs:158-160`), so it is drawn entirely by the particle path. Both
clients parse the same file. They diverge in *how they simulate and draw the particles*.

---

## 0. The one-sentence answer

> benilla simulates the portal's particles in **model space** (M2 emitter flag `0x10`): each
> particle stores raw local coordinates, and **every frame at draw time it is re-projected
> through the live, spinning bone**. MSUIClient has **no model space at all** — it bakes the
> bone's spin into each particle **once at birth** and then flies it in fixed **world**
> coordinates. That single difference is the whole thing: benilla's cloud is continuously
> swept around by the rotor (a flat rotating sheet, particles spiralling inward), MSUIClient's
> particles shoot out in straight lines from a spinning nozzle (a volumetric plume spitting
> outward). Every one of MSUIClient's six hand-tuned magic numbers is a compensation for the
> mechanism it never implemented.

And the reason it was never implemented is in your own design docs: they have the flags
**backwards**. `SYSTEM_PARTICLES.md §1.2` records `0x10` as *"do not trail"* and puts
model-space on `0x80`. benilla's code proves the opposite — `0x10` **is** model-space, and
`0x80` is `kill_outbound` (sphere-only, dead data on a plane emitter). InstancePortal's flags
are `0x39 = 0x20|0x10|0x08|0x01`, so `0x10` is **set**: it is a model-space emitter, and MSUI
treated it as a world-space one.

---

## 1. The two particle pipelines, traced from code

### benilla — model space, re-projected every frame

- The flag: `benilla-formats/src/particles.rs:446-447` — `pub fn model_space(&self) -> bool { self.flags & 0x10 != 0 }`. For `0x39` this is `true`.
- Birth (`benilla/src/particles/sim.rs:405`): `let anchored = !def.model_space();` → `anchored = false` for the portal. The birth fold then stores the **raw local** position/velocity, un-baked (`sim.rs:610-622`, the `else` branch: `(base, dir * speed)`).
- The emitter is owned by the **animated bone joint**, and its origin is rebased into the bone's own pivot frame (`terrain_stream/spawn/fx.rs:132-148`; pivot from `benilla-assets/src/m2.rs:319-322`). The cloud's translation anchor is the model, never the bone — "an animated bone never drags the risen cloud."
- Each frame the emitter's `placement` is refreshed to the **joint's live world transform** (`sim.rs:412-419`), so `placement` carries the current spin.
- Draw (`benilla/src/particles/quads.rs:151-155`): the model branch draws each particle at
  `placement.transform_point(wow_to_bevy(p.pos))` — i.e. it **re-applies the live spinning bone to every particle, every frame**. A particle emitted "outward" in local space is swept around the disc as the frame it lives in keeps turning.
- The spin is a **free-running global sequence** (clock-driven, continuous), `doodad_anim.rs:138-140`.
- A fixed **R(+Z, 90°)** is prepended to every emitter's kernel output (`particles.rs:278, 307, 357`, `rot90 = |v| Vec3::new(-v.y, v.x, v.z)`), which stands the ring perpendicular to the rotor's +X axis — "swirling in place instead of tumbling edge-on."

### MSUIClient — world space, spin baked at birth

- No `model_space` / `anchored` / `SpaceMode` / `BonePivot` exists anywhere in the particle code (grep-confirmed). Particles live in world space.
- Birth (`World/Particles/ParticleRenderer.cs:430, 452, 476-479`): it samples the bone rotation, multiplies the clock by a magic `SpinRateScale = 1.86` (`:113`), rotates the **birth direction and spawn offset** by that quaternion **about the origin** (translation zeroed at `:479`), then bakes it into a fixed world-space velocity.
- Integration (`ParticleRenderer.cs:335-342`) is only `Velocity.Z -= Gravity*dt; Position += Velocity*dt`. **Once born, a particle never sees the bone again** — it flies a straight world-space line. There is no re-projection and no drag term.
- To fake the inward "black hole", it time-reverses converging emitters (`ReverseConverging = true`, `:81`, `:497-503`: `position += velocity*Life; velocity = -velocity`).
- To fake the empty middle, it fades alpha inside `CentreHoleYards = 4.74` (`:154, :614-617`).
- To fake overlapping arms, it quantises births to `SpawnArms = 24` evenly-spaced slots (`:133, :437-452`).

So MSUI reproduces the *symptoms* of model space with four invented knobs (`ReverseConverging`,
`SpinRateScale`, `SpawnArms`, `CentreHoleYards`) instead of the mechanism.

---

## 2. Head-to-head — the mechanisms that decide it

| Dimension | benilla (correct) | MSUIClient (wrong) | file:line |
|---|---|---|---|
| **Flag 0x10 meaning** | model space | mislabelled "do not trail"; model-space wrongly assigned to 0x80 | `benilla-formats/particles.rs:446` vs `SYSTEM_PARTICLES.md §1.2` |
| **Simulation space** | model (local), re-projected each frame | world; baked at birth | `sim.rs:405,610-622` vs `ParticleRenderer.cs:335-342` |
| **Bone spin applied** | **every frame at draw**, through live placement | **once at birth**, to direction only | `quads.rs:151-155` vs `ParticleRenderer.cs:452,476-479` |
| **Spin pivot** | the bone's own pivot (rebased origin) | the model origin (translation zeroed) | `m2.rs:319-322` vs `ParticleRenderer.cs:479` |
| **Spin source** | free-running global sequence, 1× | sequence band × `SpinRateScale 1.86` (fudge) | `doodad_anim.rs:138-140` vs `ParticleRenderer.cs:113,430` |
| **Emitter frame** | R(+Z,90°) prepend stands the disc up | none | `particles.rs:357` vs (absent) |
| **Inward spiral** | emerges from negative speed in model space | faked by time-reversal `ReverseConverging` | `sim.rs:602-622` vs `ParticleRenderer.cs:497-503` |
| **Empty centre** | emerges from the geometry/spin | faked by `CentreHoleYards 4.74` alpha fade | (emergent) vs `ParticleRenderer.cs:614-617` |
| **Premultiply / glow** | gamma-space premultiply then FFXGlow square-law bloom | bare `texel*vColour`, no premultiply, no bloom | `wow_particle.wgsl:112-123`, `ffx_glow.wgsl:83-98` vs `particle.frag` |
| **Additive fog** | additive fogs toward **black** | no fog on particles at all | `wow_particle.wgsl:91-102` vs (absent) |
| **Cell flipbook** | 4×4 head/tail cell ramps animate | not implemented (full-quad UV) | `particles.rs:196-215`, `quads.rs:181-205` vs `ParticleRenderer.cs:41` |
| **Ramp endpoint inset** | `t*0.99+0.005` on colour + size + cells | raw `t` | `benilla-formats/particles.rs:196-215` vs `M2Reader.cs:615` |
| **zSource / drag parse** | track10 (+0x130) = zSource; drag = f32 at +0x194 | track10 read as `Deceleration`; +0x194 never read | `benilla-formats/particles.rs:827-828` vs `M2Reader.cs:1467` |

---

## 3. Ranked root causes (each proven from both sides, with the fix)

### #1 — MSUIClient has no model space; it bakes the spin at birth and flies in world space
**This is the whole thing. Everything below is downstream of it.**

benilla: `anchored = !model_space()` is `false` for `0x39` (`sim.rs:405`), so particles store raw
local coords (`sim.rs:610-622`, else branch) and are drawn through the live spinning placement
**every frame** (`quads.rs:151-155`). MSUI: no space mode; the spin is applied once at birth
(`ParticleRenderer.cs:452`) and the particle then integrates in fixed world space with no
further reference to the bone (`:335-342`).

This is *exactly* the difference your docs describe as the symptom
(`PLAN_14 §13`): *"Ours is a bright volumetric plume. Live is a broad, flat, low-contrast sheet
with a sparse centre."* A world-space particle from a spinning nozzle is a plume. A model-space
particle is continuously re-swept by the rotor, so the cloud reads as a flat rotating sheet.

**Fix:** implement model space. Store each particle's position/velocity in the emitter's local
(bone-pivot) frame, and at draw time transform it by the *current* bone×placement matrix rather
than baking the spin at birth. This is the one change that makes the swirl real; do it and #4,
#5, #6 below (the fake knobs) can be deleted.

### #2 — The spin is applied once, about the origin, not every frame about the bone pivot
benilla rebases the emitter origin into the bone's own pivot (`m2.rs:319-322`,
`particles.rs:549-553`) and folds the live joint transform in at draw (`quads.rs:154`). MSUI
zeroes the placement translation and rotates about the origin (`ParticleRenderer.cs:479`), once,
at birth. Your own docs already found the pivot equality (`PLAN_14 §11`: *"The emitter's
`position` IS its bone's pivot… (0,0,2.737)"*) but the fix that used it (a per-frame
`SpinAbout(pivot)`) is **not in the current code** — it was reverted. This is a direct
consequence of #1: with model space, the per-frame pivot spin is automatic.

### #3 — The R(+Z,90°) emitter prepend is missing
benilla prepends a fixed +90° about local +Z to every emitter's output (`particles.rs:357`),
which turns the kernel's ring into a wheel perpendicular to the rotor's +X spin axis — the disc
stands up instead of tumbling edge-on. MSUI has no such prepend for particles (grep for
"prepend"/"R(+Z"/"90" finds only the generic doodad heading `RotY-90`). Memory says this was
tried before; it is not in the shipped code now.

### #4 — `SpinRateScale = 1.86` is a fudge for the missing per-frame re-projection
Both clients read the same rotor animation (18 keys, one revolution per 3.334s). benilla plays
it at 1× and the *visual* swirl is fast because the whole cloud is re-swept every frame
(`quads.rs:154`). MSUI only rotates the *emission direction*, so the visual sweep is far slower,
and it multiplies the clock by `1.86` (`ParticleRenderer.cs:113`) to compensate. Your own doc
flags this as suspicious (`PLAN_14 §21.2`: *"Nothing in the format has been shown to say so…
most likely to have a real answer in the data"*). The real answer is model space — with it, 1×
is correct.

### #5 — The inward "black hole" is faked with time-reversal instead of emerging
InstancePortal's emission speed is **negative** (−3.333 / −2.778). In benilla's model space, a
negative-speed birth in the R(+Z,90) frame, continuously re-swept by the spin, *is* an inward
spiral — no special case (`sim.rs:602-622`; death is by age only, `sim.rs:41-43`). MSUI can't
get an inward spiral from a world-space bake, so it time-reverses the particle
(`ReverseConverging`, `ParticleRenderer.cs:497-503`) — your docs' own admission
(`PLAN_14 §16.1`: *"no single direction can give a dark centre with an inward spiral"*). That is
only true in world space.

### #6 — The render shader is bare: no gamma premultiply, no additive-to-black fog, no glow
MSUI's `particle.frag` is `texel * vColour` with an alpha discard — nothing else. benilla:
- **Gamma-space premultiply** (`wow_particle.wgsl:117-123`, `rgb * c.a` in gamma *before*
  linearization). Its own comment names the alternative as the bug: *"premultiplying after the
  linear conversion … inflates every soft edge by α^(1/2.2) (the fat glow-disc family)."*
- **Additive fogs toward black** (`wow_particle.wgsl:91-102`, fog policy 2.0 → `fog_rgb = vec3(0.0)`), so the portal adds full bright and fades to nothing rather than gaining grey.
- **FFXGlow square-law bloom** weighted by the zone's authored glow (`ffx_glow.wgsl:83-98`,
  `sg + glow.x * bg*bg`) — this is what makes the swirl "read as light" rather than as a decal
  (your `PLAN_14 §4 H3` predicted exactly this).
- **4×4 cell flipbook** and the `t*0.99+0.005` ramp inset on colour/size/cells
  (`benilla-formats/particles.rs:196-215`) — MSUI uses a full-quad UV and raw `t`, so its
  sprites don't animate their sheet and the ramp timing is slightly off.

These are why, even once the motion is fixed, the portal will still look flat/dull without a
glow pass and gamma-correct additive blending.

---

## 4. What is NOT the cause (so you don't chase it again)

- **The parser is fine *for the portal*.** MSUI's stride (504) and offsets match benilla. It
  does mislabel track 10 (+0x130) as `Deceleration` and never reads the real drag at +0x194
  (`M2Reader.cs:1467` vs `benilla-formats/particles.rs:827-828`) — a genuine bug — **but both
  values are 0.0 on both InstancePortal emitters**, so it does not affect the portal. Fix it
  anyway (it breaks models like `CandelabraTallWall01`, drag 10), just don't expect it to change
  the portal.
- **The emission direction was a red herring, but not for the reason the docs think.** The docs
  concluded "direction was never the bug" and adopted WoWee's componentwise formula. That's half
  right: the direction isn't the bug because the bug is the *space* the motion happens in. No
  direction model can produce the swirl in world space — that's why four of them failed.
- **The six hand-tuned knobs aren't the fix, they're the symptom.** `ReverseConverging`,
  `SpinRateScale 1.86`, `SpawnArms 24`, `CentreHoleYards 4.74`, `SpawnPhaseJitter`, `DensityScale`
  are all compensations for missing model space. Implement #1 and they become unnecessary
  (keep them behind a flag for A/B, but the target is deleting them).

---

## 5. The fix path

1. **Implement model space (flag 0x10).** Give the particle sim two modes. In model mode: store
   position/velocity in the emitter's local frame (origin rebased to the bone pivot), and at
   draw transform each particle by the *current* `placement × boneSpin(pivot)` matrix — benilla
   `quads.rs:151-155` + `sim.rs:412-419` is the blueprint. This alone turns the plume into the
   sheet.
2. **Prepend R(+Z,90°)** to the emitter frame so the disc stands perpendicular to the rotor —
   benilla `particles.rs:357`.
3. **Drive the spin from the global sequence at 1×** and delete `SpinRateScale` — the visual
   speed comes from the per-frame re-projection, not a clock multiplier.
4. **Delete the fakes** once 1–3 are in: `ReverseConverging`, `SpawnArms`, `CentreHoleYards` — the
   inward spiral, overlapping arms and empty centre all emerge from model space + negative speed.
5. **Fix the render** (independent of motion, and needed for the *look*): gamma-space premultiply
   before linearize, additive-fogs-to-black, a glow/bloom pass, and the 4×4 cell flipbook +
   `t*0.99+0.005` ramp inset — benilla `wow_particle.wgsl` + `ffx_glow.wgsl` + `benilla-formats/particles.rs:196-215`.
6. **Fix the parser** (latent, not the portal): read zSource at the +0x130 *track* and drag as the
   plain f32 at **+0x194** — benilla `benilla-formats/particles.rs:827-828`.

Doing 1–3 is what makes the motion correct; doing 5 is what makes it glow like 1.12. The rest is
cleanup.

---

## Appendix — verified anchors (read directly during this analysis)

benilla:
- `benilla-formats/src/particles.rs:446-447` `model_space = flags & 0x10`; `:499-500` `kill_outbound = Sphere && 0x80`; `:547` `xy_quad = 0x1000`; `:196-215` over-life `t*0.99+0.005` on colour+size+cells; `:827-828` zSource=track@+0x130, drag=f32@+0x194; stride `0x1f8`.
- `benilla/src/particles/sim.rs:405` `anchored=!model_space()`; `:610-622` birth fold (model = raw local); `:412-419` placement←joint each frame; `:38-77` integrate; `:226` dt clamp 0.1.
- `benilla/src/particles/quads.rs:151-155` draw fold — model branch `placement.transform_point(...)` every frame.
- `benilla/src/particles.rs:278,307,357` R(+Z,90) `rot90`; `:628-639` blend + fog policy (Add→2.0).
- `benilla/src/terrain_stream/spawn/fx.rs:132-148` owner=joint, anchor=None; `benilla-assets/src/m2.rs:319-322` bone_pivot.
- `benilla/src/doodad_anim.rs:138-140` global-sequence spin drive.
- `benilla/assets/shaders/wow_particle.wgsl:91-102` additive fog→black; `:112-123` gamma premultiply before linearize (names the fat-glow bug).
- `benilla/assets/shaders/ffx_glow.wgsl:83-98` square-law bloom `sg + glow.x*bg*bg`.

MSUIClient:
- `World/Particles/ParticleRenderer.cs:81` ReverseConverging=true; `:113` SpinRateScale=1.86; `:133` SpawnArms=24; `:154` CentreHoleYards=4.74; `:430,452,476-479` spin applied at birth about origin; `:497-503` time-reversal; `:335-342` integrate (gravity+pos, no reproject, no drag); `:600-617` ramp raw t + centre-hole fade; `:630` SetBlend; `:41` flipbook deliberately not done.
- `Shaders/particle.frag` bare `texel*vColour` + discard; `Shaders/particle.vert:26` billboard.
- `Formats/M2Reader.cs:1389` stride 504; `:1442-1467` ten tracks, `values[9]=Deceleration`; `:512-520` Deceleration doc (no +0x194 drag).
- Design docs `PLAN_14_PARTICLES.md` / `SYSTEM_PARTICLES.md`: flags mislabelled (`0x10`="do not trail", model-space wrongly on `0x80`); six hand-tuned knobs named as the open debt; measured emitter values (flags 0x39, pos (0,0,2.737), speed −3.333/−2.778, life 1.05/1.10, rate 500/250, area 4.167, midpoint 0.20/0.30, blue colour (91,158,210), rotor one rev / 3.334s).
