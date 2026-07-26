# Plan 14 — M2 particle emitters

Status: **stage 1 VERIFIED on Nico's machine across ~35 models. Stage 2 (the
ramp block, the simulator and the billboard renderer) BUILT and unrun. Stage 3
specified, not built.**

## 1. Why this, and why it is not a portal task

Nico asked for the dungeon portal's *look* — the swirling animation at a dungeon
entrance. Reading the model settled what that actually is:

```
InstancePortal.m2   8672 bytes   particleEmitters 2   ribbonEmitters 0   uvAnimations 0
                    textures: DUST6.BLP, GLOWBALL.BLP        renderFlags: 1, blend 0 opaque
                    30 vertices, 1 batch, 4 bones, 1 sequence
```

**The portal is particles.** The mesh is thirty vertices; everything you see is
spawned sprites. `M2Reader` documents `uvAnimations` in its header comment and
parses no emitters at all, so today we draw the thirty-vertex stub and nothing
else.

And it is not a portal problem. Sampling 500 of the **15,214** M2s in the
archives:

| feature | models | share |
|---|---|---|
| **particle emitters** | 88 (386 emitters) | **18%** |
| ribbon emitters | 8 | 2% |
| UV animations | 3 | 1% |

Nearly one model in five. On the Moonbrook tile alone it is the portal, a torch
and the dock. This is the largest missing piece of the M2 pipeline: every fire,
brazier, waterfall, chimney and glow in the world is currently a mesh with the
life taken out of it.

## 2. Class

**Emulation-core.** The yardstick is the real 1.12 client and the input is
Blizzard's own bytes.

## 3. The layout, derived rather than looked up

**The commonly-quoted vanilla emitter size is 476. It is 504.** Trusting the
number would have shifted every field after the first emitter.

Method — the same constraint sweep that settled the WDT and the nine-slice:
take 80 models with **three or more** emitters, then for every candidate
(field offset, stride) pair in a wide range, require that *every* emitter in
*every* model satisfies `bone < nBones` **and** `texture < nTextures`. Those are
tight constraints and a wrong stride desynchronises immediately.

```
(boneOffset, stride) pairs satisfying the constraint for EVERY emitter:
   +20  stride 504   80/80          <-- the only pair in 380..556
```

### 3.1 Confirmed

| offset | field | how it was confirmed |
|---|---|---|
| header `0x13C` | `M2Array particleEmitters` | count/offset sane across the sample |
| +0 | `uint32 particleId` | — |
| +4 | `uint32 flags` | — |
| +8 | `C3Vector position` | 12 bytes; forced by bone landing at +20 |
| +20 | `uint16 bone` | **80/80**, unique |
| +22 | `uint16 texture` | **80/80**, unique |
| +24, +32 | `M2Array` geometry / recursion model filename | consistent, not independently swept |
| +40 | `uint8 blendingType` | InstancePortal reads **4 = ADD**, which is what a glowing portal must be |
| +41 | `uint8 emitterType` | reads **0 = plane** |
| +46/+48/+50 | `uint16` tileRotation / rows / cols | reads 1x1, sane |
| +52 .. +332 | **exactly 10 `M2Track`s, stride 28** | **200/200** models validate 10 consecutive tracks; the 11th fails on **200/200**. A clean boundary, not a threshold. |

The ten tracks, in order: `emissionSpeed`, `speedVariation`, `verticalRange`,
`horizontalRange`, `gravity`, `lifespan`, `emissionRate`, `emissionAreaLength`,
`emissionAreaWidth`, `zSource`.

### 3.2 InstancePortal decoded

```
emitter 0: bone 1  texture 0 (DUST6)     pos (0, 0, 2.74)  blend ADD  type plane  1x1
   emissionSpeed -3.333   speedVariation 0     verticalRange 3.142   horizontalRange 0
   gravity 0   lifespan 1.050   emissionRate 500   area 4.167 x 4.167   spin 0.70
emitter 1: bone 2  texture 1 (GLOWBALL)   pos (0, 0, 2.73)  blend ADD  type plane  1x1
   emissionSpeed -2.778   speedVariation 0.5   verticalRange 3.142   horizontalRange 0
   gravity 0   lifespan 1.100   emissionRate 250   area 4.167 x 4.167   spin 0.70
```

**`emissionSpeed` is NEGATIVE.** The particles travel *toward* the emitter —
that is the portal pulling inward, and it is the whole character of the effect.
An implementation that clamps speed to positive will produce a fountain and look
wrong in a way that is hard to name.

`verticalRange` is `3.142` = pi: emission over a full hemisphere. Steady-state
population is `rate x lifespan` = 525 + 275 = **~800 sprites per portal**, which
is nothing.

### 3.3 Still open — the tail, +332 to +504

172 bytes holding colour, alpha, scale, spin, drag, tumble and wind. A first
reconstruction (three 16-byte FBlocks at +332/+348/+364) **failed**: 0/200,
0/200, 20/200. It is recorded here as a wrong answer so nobody re-derives it.

What the byte sweep does say:
- **+480, +488, +496 are M2Array-shaped** (count then in-file offset) at 100%
  across 910 emitters, so the struct ends with arrays, not scalars.
- +332, +344, +388, +456, +464 hold values confined to `[0,1]`, consistent with
  colour/alpha/fraction fields.
- +456 reads `0.70` on both InstancePortal emitters, matching a spin rate.

**Stage 2 must crack this before it can colour a particle**, and the method is
already proven: sweep the offset, classify each 4-byte slot by its distribution
across all 910 emitters, and keep only interpretations that hold at 100%.
**Do not import a struct definition from a wiki — the 476 would already have
been wrong.**

## 4. Key design decisions

**H1 — parse before drawing, and prove the parse on its own.**
Stage 1 is read-only: emitters parsed and displayed. It cannot regress the
working client, and it converts §3's claims into something that either matches on
Nico's machine or does not. Same reason PLAN_13 staged, and PLAN_13 stage 1
caught nothing precisely because the research was done first — that is the
outcome to repeat, not an argument against the checkpoint.

**H2 — simulate on the CPU, draw as one instanced batch per emitter.**
800 sprites per portal, ~386 emitters per 500 models. A GPU simulation is a
larger machine than this needs and would make every value harder to inspect.

**H3 — the blend mode is not decoration.**
Every InstancePortal emitter is `ADD`. Additive sprites with no depth write are
what make a glow read as light rather than as a decal. Getting this wrong makes
the portal a grey disc, which looks like a texture bug and is not one.

**H4 — negative emission speed is a feature.**
See §3.2. Do not clamp.

**H5 — emitters are per-INSTANCE state, not per-model.**
Two torches must not share a particle pool. This is the same trap the doodad
placement path already navigates: the model is shared, the placement is not.

## 5. Test protocol

**Stage 1 (built):**
1. Open the Particles panel. `InstancePortal` must list **2 emitters**, both
   `blend ADD`, `type plane`, with the exact numbers in §3.2.
2. `generaltorch01` and `smalldock` must each show 2 emitters — they are the
   other two emitter-bearing models on the Moonbrook tile.

**Stage 2 (the portal):** plane emitters, additive, billboarded, CPU-simulated.
Done when the Deadmines entrance swirls and pulls inward.

**Stage 3:** the remaining emitter types and spawn shapes, the tail fields from
§3.3, and then ribbons (2%).

## 6. Definition of done

- Stage 1: the panel agrees with §3.2 on Nico's machine.
- Stage 2: the portal looks like a portal, and torches have flames.
- `SYSTEM_PARTICLES.md` extracted once one emitter type has survived a session.
- **Not done:** ribbon emitters, and any emitter feature not measured in §3.


## 7. Stage 1 result — verified 2026-07-26

`InstancePortal` reads back **exactly** §3.2, to the last decimal:

```
instanceportal.m2 (2 emitters)
  [0] bone 1 tex 0 ADD plane 1x1  speed -3.333 var 0.000 life 1.050 rate 500.0 vRange 3.142 area 4.167
  [1] bone 2 tex 1 ADD plane 1x1  speed -2.778 var 0.500 life 1.100 rate 250.0 vRange 3.142 area 4.167
```

`generaltorch01` and `smalldock` each show 2 emitters, as §5 required. **The 504
stride is confirmed.**

### 7.1 The dump is much stronger evidence than the three checks asked for

About thirty-five models came back, and every value is physically sensible —
which a wrong stride could not produce, because it would slice fields across
boundaries and print noise:

| model | what the numbers say |
|---|---|
| `HOUSESMOKE` | life **17 s**, rate 1/s, speed 0.10 — slow drifting chimney smoke |
| `elwynncampfire` | 2 emitters: embers 1x1 life 4 s, flame **4x4** life 1.5 s rate 20 |
| `elwynntallwaterfall01` | **alpha**, speed 3.06, life 4 s, area **18.0** — a wide falling sheet |
| `fountainparticles` | 3 emitters, speed **8.33** jet + a 4.17-wide ADD haze |
| `candle01/02` | 4x4, life 6 s, rate **1/s** — six live sprites, a candle flame |
| `INNCHANDELIER` | **12 emitters** — six candles and six glows |
| `BLACKSMITH_SMOKE` | alpha, speed 5.4-5.6, life 5.5-5.7 s |
| `dustwestfall` | rate **0.0** — emits nothing until something drives it |

Two structural facts fall straight out of that table and shape stage 2:

- **The blend split is meaningful.** ADD for anything that is light — flames,
  glows, embers, the portal. `alpha` for anything that is matter — waterfalls,
  smoke, steam, dust. H3 said the blend mode is not decoration; this is the
  evidence.
- **`4x4` cells appear on every flame** and `1x1` on every glow. So the sprite
  sheet is a flipbook and flames are *animated* through it. Stage 2 needs cell
  animation for a torch to look like fire rather than a static blob — and that
  lives in the tail region §3.3 has not cracked yet (`headCellTrack`).

`dustwestfall`'s rate of 0.0 is worth a note: an emitter can be authored inert.
Stage 2 must skip a zero-rate emitter rather than divide by it.


## 8. §3.3 cracked — the ramp block at +332

The 172 bytes §3.3 left open are not three FBlocks. They are **inline keys**, and
the first twenty bytes are all stage 2 needed:

| offset | field | confirmation |
|---|---|---|
| +332 | `float midPoint` | **1086/1086** emitters in `[0,1]`; range 0.05..0.95 |
| +336 | `CImVector colour[3]` — BGRA bytes | see below |
| +348 | `float scale[3]` | **1086/1086** finite and non-negative |

**The shape is what settles it, not the range.** Of 1086 scale triples, **510
grow then shrink**, 252 shrink, 225 grow, 23 are flat. That is what a particle
size ramp looks like and what nothing else does.

**The colour is BGRA and the alpha byte is real.** Across the same 1086
emitters: **zero** have all three alpha bytes at zero, **100%** have at least
one non-zero, **85%** peak on the middle key, and **40%** are an exact
`(0, X, 0)` fade-in/fade-out. Median peak 255. This was worth measuring rather
than assuming, because `Fill` culls a particle whose alpha is ~0 — if that byte
had been padding, **every particle would have been silently culled and stage 2
would have drawn nothing**, which is indistinguishable from a dozen other bugs.

Read the right way round it also self-checks: InstancePortal's first emitter is
`(210, 158, 91)` = RGB `(91, 158, 210)`, **a light blue** — which is what a 1.12
instance portal is.

```
InstancePortal emitter 0:  midPoint 0.200  colour BGRA (210,158,91,0) (170,118,51,50) (210,158,91,0)
                           scale 0.278 -> 0.972 -> 0.028
           emitter 1:  midPoint 0.300  colour BGRA (210,187,157,0) (210,187,157,100) (210,187,157,0)
                           scale 0.056 -> 0.042 -> 0.033
```

Note the middle key sits at **0.20** and **0.30**, not 0.5 — the flash happens
early. A renderer that assumed half-life would put the peak in the wrong place
on every emitter in the game.

## 9. Stage 2 as built

`World/Particles/ParticleRenderer.cs` plus `Shaders/particle.vert|frag`.
CPU simulation, one instanced draw per (texture, blend mode), camera-facing
billboards, depth test on and depth write off.

Per-instance pools keyed by placement position **and rotation** (H5) — two
torches do not share particles, and two placements in the same tenth-of-a-yard
cell do not either.

### 9.1 What review caught before it ran

- **A missing texture bound name 0**, which samples opaque black; black at the
  ramp's alpha survives the discard, so an alpha-blended group would have
  painted black squares over the scene instead of drawing nothing. Skips now.
- **Depth test was enabled and never restored** — a silent z-fail in whatever
  pass ran next.
- **Pools froze their transform at creation.** `PoolKey` carries only the
  rounded translation, so a placement rebuilt with a new rotation kept emitting
  along an orientation that no longer existed.
- **`HorizontalRange` was parsed, printed, and never used**, so a narrow fan
  emitted a full ring.
- **Placement scale reached the spawn rectangle but not the speed or the sprite
  size** — a doodad at scale 2 would have had a doubled emission area, half-size
  sprites and unscaled speed: three different worlds in one effect.
- **`ofsKeys + 4 <= data.Length` was unchecked `uint` arithmetic** — a misparsed
  offset near `uint.MaxValue` wraps, passes, and then throws on the cast, taking
  down the model load.

### 9.2 Still not done

The **4x4 sprite-sheet flipbook**. Every flame in §7.1 uses one and glows use
1x1, so a torch will glow but not lick. `headCellTrack` lives in the part of the
struct still uncracked — +360 to +504, which now holds only cell tracks, spin,
drag, tumble and wind. Also absent: bone animation of the emitter origin, sphere
and spline emitter types, tails, and ribbons (2% of models).

## 10. First-run checklist

1. `[particles] texture not found:` lines at startup mean the BLP path from the
   M2 did not resolve — the sprites will be missing, and that is the first thing
   to look at if nothing appears.
2. Stand at the Deadmines entrance. The portal should be **blue, additive, and
   pulling inward** — not a fountain. A fountain means the negative speed got
   clamped somewhere (H4).
3. The panel's `LIVE:` line should read roughly **800 particles in 2 pools** for
   a portal alone.
4. Torches and campfires should glow. They will not flicker through their sheet
   yet — that is §9.2.
5. `simulate` and `draw` in the panel should both be well under a millisecond.


## 11. The swirl is a bone spin (2026-07-26, after the first render)

First render put particles on screen but Nico's report was exact: *"kinda? seems
off center, not nearly enough outward swirl to fill out the entrance."* Both
symptoms have one cause.

**The emitter's `position` IS its bone's pivot.** Emitter 0's position is
`(0, 0, 2.737)` and bone 1's pivot is `(0, 0, 2.737)` - identical. So the
position is already model-space and the origin was never the problem.

**Bones 1 and 2 carry flag `0x0200` — animated — and their ONLY animation is
rotation.** No translation, no scale. Eighteen keys, a steady turn of about
twenty degrees each, a full revolution every 3334 ms:

```
t=3333  (0.000, 0, 0, 1.000)
t=3541  (0.171, 0, 0, 0.985)      ~ 19.7 deg about local X
t=3750  (0.337, 0, 0, 0.942)      ~ 39.4
t=3958  (0.493, 0, 0, 0.870)      ~ 59.1
...
```

The emission plane is the bone's local XY. A full turn about X sweeps that plane
through every orientation, which is what throws the particles out into a **disc
that fills the doorway**. Without it the emitter is fixed, the hemisphere
collapses into a blob near the origin, and the result is a small off-centre haze
- precisely what the screenshot showed. **The swirl was never a shader problem.**

### 11.1 Two format facts, both checked rather than assumed

- **Vanilla animation blocks are FLAT.** One timestamp list, one key list, and a
  `ranges` array giving `[first, last]` per sequence. The nested
  array-of-arrays is a later format, and reading it that way here yields keys
  that are not unit quaternions - which is how it was caught.
- **Vanilla rotation keys are four FLOATS, not packed int16.** The same probe
  reads **1/18** unit quaternions as int16 and **18/18** as float. Not a close
  call. `M2Reader` already had this right and says so at line 859; the mistake
  was in the Python probe, not the client.
- **Timestamps are ABSOLUTE and need not start at zero.** InstancePortal's
  sequence runs **3333..6667 ms**, so a sampler that wraps on `duration` and
  starts at 0 reads off the front of the track forever.

The keys are held raw, in the M2's own Z-up space. `M2Model.Bones` applies the
glTF Y-up swap for the character pipeline, and particles work in world space
through the placement matrix, so borrowing that would only mean undoing it.

### 11.2 What the placement contributes

The MDDF entry: world `(-11208.2, 1679.6, 22.6)`, rotation `(0, 89.5, 0)`,
scale **1269/1024 = 1.239**. Yaw only, so the model is turned rather than tipped,
and the emitter sits `2.737 x 1.239 = 3.39` yards above the placement point. The
scale now reaches the sprite size and the speed as well as the spawn rectangle
(§9.1), so the whole effect grows together.


## 12. It was a coordinate space, not the maths (2026-07-26)

The bone spin changed nothing on screen. *"Still bottom left."* The reason is
one line in `ParseVertices`:

```csharp
PosX = px,  PosY = pz,  PosZ = -py;
```

**Every M2 the doodad pipeline sees is Y-up.** `BuildPlacement` yaws about **Y**
and then applies `PlacementToWorld`, and its own comment says *"an M2's render
vertices are already in placement space"* — which is true precisely because
`ParseVertices` swapped them. Bone pivots get the same swap. Bone rotations get
the same swap.

**The emitter position did not.** It was read raw, in the M2's own Z-up space, so
InstancePortal's `2.737` — up the disc's axis — became `2.737` **sideways** once
the heading was applied. The portal appeared low and off to one side, with
nothing logged and nothing to distinguish it from a maths error in the emitter.
It cost two rounds and a plausible wrong answer (the bone spin, which is real and
was also missing, but was not this).

The tell was there all along and §11 recorded it without noticing: **the
emitter's raw position equals its bone's raw pivot exactly**, `(0, 0, 2.737)`.
M2Reader already swaps that pivot. Two readers of the same number in two
different spaces.

Now swapped at parse: the position, the bone rotation keys, and — via one
`Swap()` helper in the renderer — the emission cone and the spawn rectangle, so
the geometry is still *written* in the terms the file uses and converted exactly
once.

**The lesson is not "check the axes".** It is that this project has one axis
convention per pipeline and a new consumer joins an existing one. The question
to ask of any new M2 field is not "what does it mean" but **"which of this
file's readers is already reading a neighbouring field, and in what space did it
leave it?"**

The console dump now prints the emitter position and whether a bone spin was
found, because the previous run could not answer either.


## 13. The shape is wrong, and it is `verticalRange` — instrumented, not decided

The coordinate fix worked: the portal is now centred in the doorway and blue.
Side by side against the real 1.12 client, one difference remains and Nico named
it exactly — ours *"looks like a spinning 3D emitter spitting stuff out"* where
the live one *"feels more like an animated 2D plane pulling things in a circular
pattern"*.

**Ours is a bright volumetric plume. Live is a broad, flat, low-contrast sheet
with a sparse centre.**

That is a direction problem. Today `verticalRange` is read as a cone half-angle,
so the portal's **pi** is the entire sphere: particles leave the spawn plane in
every direction and, with a negative speed, converge through the middle. A ball.

### 13.1 What was measured before reaching for a switch

- **212 of 910 sampled emitters use `verticalRange` = pi exactly.** It is a
  common authored value, not an outlier, so whatever it means has to be right
  for a quarter of the world's emitters.
- **The flags do not separate wide from narrow.** `0x29` appears both in the pi
  group and among torches at 0.087, and no single bit correlates with the mean.
  So the interpretation is not flag-switched; the same formula has to produce a
  narrow torch jet at 0.087 and a flat portal sheet at pi.

### 13.2 The fork, as a runtime switch

I have reasoned about this twice now and been wrong once (§12), so this one gets
instrumented rather than argued:

| model | direction | what it should look like |
|---|---|---|
| `Cone` | cone about the plane normal, half-angle = `verticalRange` | today: sphere at pi |
| `InPlaneRadial` | from the origin out through the spawn point, staying in the plane | flat sheet, negative speed pulls straight in |
| `Blended` **(default)** | leans from the normal toward in-plane as `verticalRange` goes 0 -> pi | torch stays a narrow jet, portal goes fully flat |

`Blended` is the default because it is the only one of the three that can be
right for **both** ends of the measured range at once. If it is, the portal reads
flat and torches keep their shape. If the portal is right but torches go
sideways, the lean is too aggressive. If `InPlaneRadial` is visibly better for
the portal, then `verticalRange` is not a cone at all and the torches need a
separate explanation.

**Whichever wins, record it here and delete the other two.** A switch left in
place is a decision not taken.

### 13.3 Resolved by WoWee before the switch was ever run

Nico: *"surely wowee has this one figured out no?"* It had, and asking sooner
would have saved two rounds of theory. `src/rendering/m2_renderer_particles.cpp`:

```cpp
glm::vec3 dir(0.0f, 0.0f, 1.0f);
dir.x += distN(particleRng_) * hRange;     // distN is uniform_real(-1, 1)
dir.y += distN(particleRng_) * hRange;
dir.z += distN(particleRng_) * vRange;
normalize(dir);
p.velocity = rotMat * dir * speed;         // rotMat = model * bone, rotation only
```

**`verticalRange` and `horizontalRange` are not angles at all.** They are
additive jitter on the *components* of a direction vector that starts as model
up, normalised afterwards. None of the three models in §13.2 was right.

Read it with InstancePortal's numbers — `hRange 0`, `vRange pi`:

- X and Y get **nothing**, so there is no lateral spread whatsoever;
- Z becomes `1 + U(-3.14, 3.14)`, which normalises to `(0,0,+1)` about two
  thirds of the time and `(0,0,-1)` the rest.

**Every particle travels straight along one axis.** That is the flat sheet, and
the bone's full revolution every 3.33 s sweeps that axis through a circle,
tracing the disc. Direction and spin together make the shape; neither does it
alone.

It also explains §13.1's dead end. The same formula gives a torch at `0.087` a
tight jet and the portal at `pi` a flat sheet, so **no flag needs to switch
between two readings** — which is exactly why the flag sweep found no separation.
The absence of a correlation was the answer, and I read it as a missing clue.

One more divergence, followed deliberately: **WoWee never uses
`emissionAreaLength/Width`.** Every particle is born at the emitter point.
Spreading births over the portal's 4.17-yard rectangle is what turned a crisp
disc into a fuzzy ball, so that is now matched. The fields stay parsed and
displayed; if waterfalls (area 18.0) later look too thin, that is the evidence
to revisit it with.

### 13.4 The lesson, which is not about particles

Three rounds went into deriving behaviour that a working reimplementation had
already settled, sitting on the same machine. The handbook's §7 lineage section
exists because WoWee has been consulted before — for streaming, in PLAN_08 — and
the reflex did not fire here.

**When the question is "how does the real client behave", the archives answer
what the data IS and WoWee answers what a client DOES with it.** The rule this
session earned: reach for the reference implementation the moment the question
stops being about bytes and starts being about behaviour.


## 14. WoWee punts on this exact effect — and I over-applied it for one commit

The direction fix was a large step: sharp blue arcs, flat, circular, correctly
placed. But they are **too coherent** — hard ribbons where the real client draws
a soft haze. Reading further into WoWee explains why, and carries a warning.

### 14.1 WoWee does not emulate the instance portal at all

`src/rendering/m2_renderer_particles.cpp` opens its spawn loop with:

```cpp
if (gpu.isInstancePortal) return;      // line 85, and again at 252, 374, 524
```

**No particles are spawned, updated or drawn for this model.** In their place,
`m2_renderer_render.cpp` substitutes two hand-authored sprites:

```cpp
GlowSprite core;
core.color = glm::vec4(0.35f, 0.55f, 1.0f, 1.25f);   // blue
core.size  = instance.scale * 7.0f;
GlowSprite halo = core;
halo.color.a *= 0.35f;
halo.size    *= 2.4f;
```

A blue core at seven times scale plus a soft halo at 35% alpha. `m2_model_classifier.cpp`
matches it by **filename** — `has(n, "instanceportal")`.

That is a stylised approximation, and a reasonable one. But it means **WoWee is
authoritative for the generic emitter formula and silent on this model.**

### 14.2 The mistake that cost a commit

§13.3 also adopted "WoWee never uses `emissionAreaLength/Width`" and moved every
spawn to the emitter point. **That inference is void**: the code it came from
never executes for a portal. Its omission is untested for this case, and
harmless for the others only because every other emitter's area is 0.007..0.5,
where a point spawn and an area spawn look the same.

The portal's area is **4.167** and the waterfall's is **18.0**. Born at one point
with a sweeping direction, particles trace a single thin coherent ribbon — the
sharp arcs. Born across the authored rectangle, the same sweep smears into a
soft sheet.

The area spawn is restored, carried by the bone spin so the plane turns with the
direction rather than staying flat while the direction rotates through it.

**The correction to §13.4's lesson:** reaching for the reference implementation
was right, and it produced the one fact three rounds of theory had missed. But
a reference is only evidence for the code paths it actually runs. *"WoWee does
not do X"* is a claim about WoWee's behaviour, not about the format — and here
the whole branch was disabled behind a filename check I had not read yet.
**Check that the code you are citing executes for the case you are citing it
for.**


## 15. White hole vs black hole — the sign of the speed is the switch

Nico, on the soft-haze build: *"We are having the particles emit OUTWARD. The
live client is like a black hole in the center surrounded by particles it's
pulling in in a spiral, we are like a white hole."*

That is the last structural error, and it comes from WoWee's formula being
**perpendicular** to the spawn offset. Pre-spin the offset is `(lx, ly, 0)` and
the direction is `(0, 0, 1)`; the bone spin rotates both by the same angle about
X, so they stay perpendicular forever. **Tangential motion.** Off a swept plane
that is the hard coherent arcs of §14, and once the area spawn softened them, an
outward haze.

### 15.1 Three measured things say radial-inward

- **The mesh is a flat ring in the model's YZ plane.** 30 vertices, every one
  weighted to the **unanimated** bone 0, radius out to **4.41**, and only
  **0.55** thick in X — centred exactly on the emitter pivot `(0, 0, 2.737)`.
  So the portal's face normal is local **X**, the disc is the plane the
  particles must live in, and its centre is where they must go.
- **The speed is NEGATIVE**, −3.333 and −2.778. A radial direction plus a
  negative speed is literally "travel toward the origin". No other reading of a
  converging emitter uses the sign at all.
- **The authored ramp makes the centre dark for free.** Alpha runs `0 -> 50 -> 0`
  and scale `0.278 -> 0.972 -> 0.028`, so a particle is **invisible at birth and
  again at death**. It fades up just after leaving the rim and fades out as it
  arrives at the middle. *The black hole is the ramp*, not a special case — and
  the spinning spawn plane makes the inward path a spiral rather than a straight
  fall.

### 15.2 The rule, and its honesty

**Negative `emissionSpeed` -> radial. Positive -> WoWee's axis formula.**

The sign is the switch, which is the only criterion in the data that separates
the two behaviours without a threshold. It touches very few emitters — the
portal's two, `BOTTLESMOKE` at −0.556, `VIALSBOTTLES[4]` at −0.833 — and leaves
every torch, campfire, waterfall and fountain on the path that was verified
against WoWee.

**This is not taken from WoWee and cannot be** (§14.1: its particle path returns
immediately for this model). It is the authored data plus the reference
screenshot, and it is recorded as an inference rather than as emulation. If a
converging emitter somewhere else looks wrong, this is the rule to question
first.


## 16. §15 was wrong, and the reasoning error is worth keeping

Radial-inward produced **two bright cores** — the exact inverse of the dark
centre it was meant to create. Nico: *"This is as opposite as you can get."*

**The mistake is in §15.1's third bullet.** I claimed the authored ramp makes the
centre dark for free, because alpha runs `0 -> 50 -> 0`. That is true *per
particle as a function of its own age* — and I silently treated age as a proxy
for distance from the centre. It is not. A particle born near the rim reaches
the middle late in its life, faded out. A particle born near the middle reaches
it almost immediately, at low age, while its alpha is at peak. **Radial motion
converges every particle on one point regardless of when it was born**, so the
centre collects the bright ones and becomes a caustic. Reverted.

### 16.1 What the three failed models have in common

| model | motion | result |
|---|---|---|
| cone about the normal | isotropic | volumetric plume |
| WoWee axis + spin | tangential — direction stays perpendicular to the offset | coherent arcs, then a soft outward haze |
| radial | converging on a point | two bright cores |

**None of them can produce a dark centre with an inward spiral**, because none
has an inward component that decays with radius. An orbit that decays is
tangential *plus* a small radial term, and nothing in the authored fields has so
far been shown to supply the second one.

### 16.2 Stopping the guessing

Three model guesses, three different wrong pictures. The next change should not
be a fourth. Two honest ways forward:

1. **Get evidence a still frame cannot carry.** Every screenshot so far is one
   instant, and the whole question is what the particles *do over time* — orbit,
   fall in, or drift out. A few seconds of the live portal in motion would settle
   direction, decay and lifetime at once, and it is the cheapest decisive thing
   available.
2. **Adopt WoWee's answer for this one model.** It classifies `instanceportal`
   by filename and draws a blue additive core at 7x scale plus a halo at 2.4x
   size and 35% alpha — no particles at all (§14.1). It is explicitly not
   emulation, and it demonstrably looks right. The handbook's §2 class for this
   would be **Addition**, not Emulation-core, and it should say so.

The current build is back to §14: WoWee's direction formula plus the area spawn —
the soft haze, which is the closest of the three and the one to iterate from.


## 17. Nico's fix: reverse time, and move the density to the far end

Two changes, both his, and together they are a better answer than three rounds of
arguing about what `verticalRange` means.

**1. Play a converging emitter backwards.** A time-reversed outward spiral *is*
an inward spiral. The authored direction, speed, lifetime and bone sweep are all
kept exactly as they are and simply run the other way, so nothing has to be
reinterpreted and the sweep still supplies the curve — the path is a spiral, not
a fall. Implemented as a spawn-time transform rather than a simulation mode:
displace the particle by one lifetime of travel and negate the velocity.
Gravity, the ramp and culling are untouched.

**2. Sample the ramp at `1 - t`, so the END owns the density.** This is what
empties the middle. InstancePortal's ramp peaks at MidPoint **0.20** — very
early — so particles are brightest just after birth, and whichever end of the
path birth happens to be, *that* end is bright. Flipping the sample moves the
peak to `t = 0.80`, putting the bright band at the far end.

Both apply only where `emissionSpeed` is negative, so torches, campfires,
waterfalls and fountains are untouched.

### 17.1 Why this fixes §16's error rather than repeating it

§16 concluded that no single direction can give a dark centre with an inward
spiral, *because none has an inward component that decays with radius*. That is
still true — and this does not add one. It sidesteps the requirement entirely:
the radius decay comes from **running the outward path in reverse**, and the dark
centre from **where the ramp peaks**, which are two independent knobs rather than
one overloaded direction vector.

It also repairs the specific mistake in §15.1. I had assumed the ramp already
darkened the centre; it could not, because the ramp is a function of a particle's
own age and age only maps to distance if every particle starts from the same
place. Reversing the motion makes every particle start at the rim — so now it
does.

Both are switches in the Particles panel, so if one is right and the other is
wrong that is one run to find out rather than another rebuild.


## 18. The format spec, and what it says we all got wrong

Researched before touching code, at Nico's instruction. Sources: wowdev.wiki's
M2 page, and WoWee's own vanilla loader.

### 18.1 The parse is independently confirmed byte-for-byte

WoWee's `m2_loader.cpp` vanilla branch lands on exactly the offsets §3 derived
from the constraint sweep — the ten 28-byte tracks starting at `0x34` (=+52),
then:

```
+0x14C (332)  float  midpoint
+0x150 (336)  uint32 colorValues[3]   // BGRA, A channel = opacity
+0x15C (348)  float  scaleValues[3]
```

Its comment says *"empirically confirmed from real vanilla M2 files"*, arrived at
independently. **Two derivations, same answer, including the BGRA order and the
alpha byte** — §8's ramp block is settled.

One correction: the tenth track is **deceleration**, not `zSource`. The wiki's
`drag` — *"speed is multiplied by exp(-drag * t)"* — is the same slot under a
third name. It was the prime suspect for "too much in the centre", because drag
bunches particles at the far end of their path. **It is 0.000 on both
InstancePortal emitters.** Not the lever. Renamed anyway, so nobody else spends
an hour on it.

### 18.2 The direction spec — and every model so far was wrong

wowdev.wiki, verbatim, for a **plane** generator:

> **verticalRange**: "the maximum **polar** angle of the initial velocity; 0
> makes the velocity straight up (+z)"
>
> **horizontalRange**: "the maximum **azimuth** angle of the initial velocity;
> **0 makes the velocity have no sideways (y-axis) component**"

They are ordinary spherical angles about +z. The second clause is decisive:
**InstancePortal's `horizontalRange` is ZERO**, so its velocity has **no y
component at all** — the direction is confined to the model's XZ plane. A **flat
fan**, swept by the bone's revolution. Nothing about it is three-dimensional.

Scored against that:

| model | error |
|---|---|
| first cone | sampled azimuth over the **full circle** regardless of `horizontalRange`; at `vRange = pi` that is an isotropic sphere — the plume |
| WoWee's | adds both ranges as componentwise jitter and normalises. A cheap approximation, not the spec — which is why following it got close and never right |
| radial | not in the spec at all |

Both angles are now sampled **symmetrically** about the axis: *"drifting away
vertically... they can do it horizontally too"* describes a spread either side,
and a one-sided `[0, range]` sample would throw every particle to the same side.

### 18.3 The flags, finally decoded — and one of them is direct evidence

`InstancePortal`'s flags are `0x39`:

| bit | meaning |
|---|---|
| `0x1` | particles are affected by lighting |
| `0x8` | particles travel "up" in **world** space, rather than model |
| `0x10` | do not trail |
| `0x20` | unlightning |

And the one that is **not** set:

> `0x20000` — *"STYLE: 'Outward' particles, **most emitters have this** and their
> particles move away from the origin"*

**The portal is explicitly not an outward emitter**, and that was sitting in a
field parsed since stage 1 and never decoded. It is the format's own statement of
what Nico said from the screenshots: this thing pulls in, and most emitters do
not. `0x8` is the other one to watch — a world-space up axis rather than a model
one is not implemented here yet.

### 18.4 Still not implemented, and now named

`0x20000` outward / `0x40000` its opposite, `0x80` particles-in-model-space
(*"causes animation of the particle emitter to be carried over to the
particles"* — which is the orbit §16 said was missing), `drag`, `spin`,
`twinkle`, `tumble`, `windVector`, and the head/tail cell flipbook. `0x80` in
particular is the mechanism for a decaying orbit, and it is a flag rather than a
formula.


## 19. §18's direction change was worse, and measuring it ends the direction hunt

Nico: *"I don't see a difference. Are you sure I have it?"* He had it — the build
post-dated the source by two minutes. It made no visible difference because the
change is **worse**, and this time it was measured instead of argued.

The model's mesh is a flat ring in its **YZ** plane: 30 vertices spanning
`x = -0.273..+0.273` against `y, z = -4.4..+7.2`. **X is the disc's normal.** So
a particle direction with any X component leaves the plane of the ring the whole
model is built around.

Sampling 4000 spawns through the real code path — `Swap` then the bone spin:

```
WoWee's formula   mean |x| of the direction = 0.000   max 0.000
§18's polar/azimuth reading    mean |x| = 0.636   max 1.000
```

**WoWee's formula already produces exactly the flat in-plane disc the geometry
implies**, and the spec-literal reading breaks it. §18's reasoning — that
`horizontalRange = 0` confines the velocity to XZ — is right about XZ and wrong
about which plane matters: XZ *contains* the disc's normal. Reverted.

### 19.1 What this settles, and where the actual problem is

**The direction was never the bug.** Four models have been tried and the first
one adopted — WoWee's — was already producing the correct shape. That line of
enquiry is closed.

The remaining complaint is *"too much is in the center"*, and it is a **spawn
distribution** problem, not a direction one:

> `lx = emissionAreaLength * 0.5 * U(-1,1)`, `ly = emissionAreaWidth * 0.5 * U(-1,1)`

is a **centred square**. Uniform over a centred square puts as many particles at
the middle as anywhere, and once the disc is viewed face-on the middle is where
paths cross — so the centre is the densest place on screen under *every*
direction model, forwards or reversed. That is why reverse-time helped and did
not fix it.

**Two candidates, both cheap, neither yet tested:**

1. **The ring mesh may be the portal's actual body.** It is an annulus of radius
   up to 4.4 with a hole in the middle, textured `DUST6`, unlit — geometry that
   is dark in the centre by construction. If the live client's dark middle is the
   *mesh*, the particles are only sparkle around it and no particle change will
   ever produce it. **First thing to check: is that 30-vertex mesh drawing at
   all?** It is blend mode 0, and a doodad renderer that alpha-tests or z-sorts
   it differently could be dropping it silently.
2. **Spawn on the ring, not the square.** If the mesh is an annulus, the emission
   area may be meant as its extent rather than a filled rectangle.

Check 1 first — it needs no new theory, only a look at whether something we
already parse is on screen.
