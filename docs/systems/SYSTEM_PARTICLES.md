# System: Particles — M2 emitters, and the instance portal

Draft 1, 2026-07-26. Extracted from `PLAN_14_PARTICLES.md` under the handbook
§1.2 rule once the portal had survived a session. PLAN_14 keeps the long
derivation record and every wrong turn; **this doc is what you need to work on
particles without reading it.**

Read this plus the handbook's cross-cutting ground truth (§3.1 coordinates,
§8.5 shader ASCII rule, §11 working agreements).

Owner files: `Formats/M2Reader.cs` (`M2ParticleEmitter`, `ParseParticleEmitters`,
`ReadEmitterBoneSpin`, `SampleRamp`, `SampleBoneRotation`),
`World/Particles/ParticleRenderer.cs`, `Shaders/particle.vert|frag`,
`World/Doodads/DoodadRenderer.cs` (`Model.Emitters`, `EmitterInstances`),
and the Particles panel in `GameLoop/Combat/GameLoop.Particles.cs`.

> **NAMING TRAP.** The dungeon doorway effect this doc's §5 is about is an
> **instance portal**. `SYSTEM_INSTANCES.md` covers the travel it triggers;
> `PLAN_10_WMO_PORTALS.md` is a completely different thing (interior culling
> polygons). Three meanings, one word.

---

## 0. The bar, and the scope

Every fire, brazier, waterfall, chimney, glow and portal in vanilla is particles.
Sampling 500 of the **15,214** M2s in the archives: **18% carry particle
emitters** (88 models, 386 emitters), 2% ribbons, 1% UV animation. Before this
system, all of them drew as a mesh with the life taken out.

**Class: emulation-core for the format, inference for the portal's look.** The
byte layout is Blizzard's own and is derived, not guessed. The portal's final
appearance is currently six hand-dialled values matched against the live client
by eye — §6 is explicit about which is which, and that debt is the main open
item in the system.

---

## 1. Ground truth — the emitter struct, do not re-derive

**The commonly-quoted vanilla emitter size is 476. It is 504.** Trusting the
published number shifts every field after the first emitter. The stride was
derived by constraint sweep — 80 models with 3+ emitters, requiring
`bone < nBones` **and** `texture < nTextures` for every emitter in every model
across a wide (offset, stride) range. `(+20, 504)` was **the only pair in
380..556 that satisfied it**, at 80/80. Later confirmed byte-for-byte by WoWee's
independent vanilla loader, whose comment says *"empirically confirmed from real
vanilla M2 files"* — two derivations, one answer.

| offset | field | evidence |
|---|---|---|
| header `0x13C` | `M2Array particleEmitters` | count/offset sane across the sample |
| +0 | `uint32 particleId` | — |
| +4 | `uint32 flags` | see §1.2 |
| +8 | `C3Vector position` | 12 bytes; forced by bone landing at +20. **Z-up in the file** — see §3 |
| +20 | `uint16 bone` | 80/80 |
| +22 | `uint16 texture` | 80/80 |
| +24, +32 | `M2Array` geometry / recursion model | consistent |
| +40 | `uint8 blendingType` | 4 = ADD |
| +41 | `uint8 emitterType` | 0 = plane |
| +46/48/50 | `uint16` tileRotation / rows / cols | 1x1 and 4x4 both appear |
| +52 .. +332 | **exactly 10 `M2Track`, stride 28** | 200/200 models validate ten consecutive tracks; **the 11th fails on 200/200**. A boundary, not a threshold |
| +332 | `float midPoint` | 1086/1086 in `[0,1]`, range 0.05..0.95 |
| +336 | `CImVector colour[3]` — **BGRA** | see §1.1 |
| +348 | `float scale[3]` | 1086/1086 finite, non-negative |
| +480/+488/+496 | `M2Array` | M2Array-shaped at 100% across 910 emitters — the struct **ends with arrays** |
| **504** | stride | the sweep |

The ten tracks, in order: `emissionSpeed`, `speedVariation`, `verticalRange`,
`horizontalRange`, `gravity`, `lifespan`, `emissionRate`, `emissionAreaLength`,
`emissionAreaWidth`, **`deceleration`**.

> The tenth track was called `zSource` for one draft. It is **deceleration** —
> the wiki's `drag`, `speed *= exp(-drag * t)`. Renamed because it is the
> obvious suspect for "particles bunch at the far end" and someone will chase
> it. **It is 0.000 on both InstancePortal emitters.** Not the lever.

**A first reconstruction of the tail as three 16-byte FBlocks at +332/+348/+364
FAILED** — 0/200, 0/200, 20/200. Recorded so nobody re-derives it. The block is
inline keys, not FBlocks.

### 1.1 The colour is BGRA and the alpha byte is real

Measured across 1086 emitters: **zero** have all three alpha bytes at zero,
**100%** have at least one non-zero, **85%** peak on the middle key, **40%** are
an exact `(0, X, 0)` fade-in/fade-out. Median peak 255.

This was worth measuring rather than assuming, because the simulator culls a
particle at alpha ~0 — **had that byte been padding, every particle in the game
would have been silently culled and the renderer would have drawn nothing**,
which is indistinguishable from a dozen other bugs.

It self-checks when read the right way round: InstancePortal emitter 0 is BGRA
`(210, 158, 91)` = RGB `(91, 158, 210)`, **a light blue** — which is what a 1.12
instance portal is.

**`midPoint` is not 0.5 and assuming it is breaks every emitter in the game.**
InstancePortal's keys sit at **0.20** and **0.30** — the flash happens early.

The shape of the scale triples is what settles them as a size ramp: of 1086,
**510 grow then shrink**, 252 shrink, 225 grow, 23 flat. Nothing else looks
like that.

### 1.2 Flags — decoded, mostly unimplemented

`InstancePortal` reads `0x39`: `0x1` lit, `0x8` "up" is **world** space not
model, `0x10` do-not-trail, `0x20` unlightning.

The one that is **not set** is the interesting one:

> `0x20000` — *STYLE: 'Outward' particles, **most emitters have this** and their
> particles move away from the origin.*

**The format states outright that the portal is not an outward emitter, and
that most emitters are.** That bit sat in a field parsed since stage 1 and
undecoded through four wrong direction models. Also unimplemented and named
here so they are not rediscovered: `0x40000` (its opposite), `0x80`
particles-in-model-space (*"animation of the emitter is carried over to the
particles"* — the mechanism for a decaying orbit), `0x400` pinned particles,
`0x8` world-space up, plus `spin`, `twinkle`, `tumble`, `windVector` and the
cell flipbook.

### 1.3 Two authored facts that shape any consumer

- **`emissionSpeed` can be NEGATIVE.** InstancePortal is −3.333 and −2.778;
  `BOTTLESMOKE` −0.556; `VIALSBOTTLES[4]` −0.833. **Do not clamp.** The sign is
  the only thing in the data that separates converging emitters from the rest.
- **An emitter can be authored inert.** `dustwestfall` has rate **0.0**. Skip a
  zero-rate emitter rather than divide by it.

The blend split is meaningful and not decoration: **ADD for anything that is
light** (flames, glows, embers, the portal), **alpha for anything that is
matter** (waterfalls, smoke, steam, dust). Additive with no depth write is what
makes a glow read as light rather than as a decal.

`4x4` cells appear on every flame and `1x1` on every glow, so the sprite sheet
is a flipbook — see §7.

---

## 2. Ground truth — the animation blocks

Three format facts, each checked rather than assumed, each of which silently
produces garbage if taken the modern way:

- **Vanilla animation blocks are FLAT.** One timestamp list, one key list, and a
  `ranges` array of `[first, last]` per sequence. The nested array-of-arrays is
  a **later** format; reading vanilla that way yields keys that are not unit
  quaternions, which is how it was caught.
- **Vanilla rotation keys are four FLOATS, not packed int16.** A probe reads
  **1/18** unit quaternions as int16 and **18/18** as float. Not a close call.
- **Timestamps are ABSOLUTE and need not start at zero.** InstancePortal's
  sequence runs **3333..6667 ms**. A sampler that wraps on `duration` from 0
  reads off the front of the track forever.

### 2.1 The swirl is a bone spin

`InstancePortal`'s emitter bones (1 and 2) carry flag `0x0200` — animated — and
their **only** animation is rotation. No translation, no scale. Eighteen keys,
about twenty degrees each, **a full revolution every 3334 ms**.

The emission plane is the bone's local plane; the revolution sweeps it through a
circle, and that is what traces the disc. **Direction and spin together make the
shape; neither does it alone.** Without the spin the emitter is fixed, the
spread collapses into a blob, and the result is a small off-centre haze.

`SampleBoneRotation(double)` slerps over the absolute timestamps.

---

## 3. Coordinate space — the trap that cost two rounds

```csharp
// ParseVertices
PosX = px,  PosY = pz,  PosZ = -py;
```

**Every M2 the doodad pipeline sees is Y-up.** `BuildPlacement` yaws about **Y**
and its comment says *"an M2's render vertices are already in placement space"*
— true precisely because `ParseVertices` swapped them. Bone pivots get the same
swap. Bone rotation keys get the same swap.

**The emitter position did not**, for one build. It was read raw in the file's
Z-up space, so InstancePortal's `2.737` — up the disc's axis — became `2.737`
**sideways** once the heading was applied. The portal appeared low and off to
one side with nothing logged, indistinguishable from a maths error.

The tell was recorded a draft earlier without being noticed: **the emitter's raw
position equals its bone's raw pivot exactly**, `(0, 0, 2.737)`. `M2Reader`
already swaps that pivot. Two readers of one number in two spaces.

Now swapped at parse (position, bone rotation keys) and via one `Swap()` helper
in the renderer (emission cone, spawn rectangle), so the geometry is still
*written* in the file's terms and converted **exactly once**.

> **The lesson is not "check the axes".** This project has one axis convention
> per pipeline and a new consumer joins an existing one. The question to ask of
> any new M2 field is not *"what does it mean"* but **"which of this file's
> readers is already reading a neighbouring field, and in what space did it
> leave it?"**

---

## 4. The renderer as built

`World/Particles/ParticleRenderer.cs` plus `Shaders/particle.vert|frag`. CPU
simulation, one instanced draw per **(texture, blend mode)**, camera-facing
billboards expanded in the vertex shader from camera-relative positions, depth
test on and depth write **off**, `discard` below alpha 0.003.

**Pools are per-INSTANCE, not per-model** — `PoolKey(Path, X, Y, Z, Emitter,
Rot)`. Two torches must not share a particle pool. The key carries **rotation as
well as position**: a placement rebuilt with a new heading otherwise keeps
emitting along an orientation that no longer exists.

**Placement scale reaches the spawn rectangle, the speed AND the sprite size.**
Reaching only the first would give a doodad at scale 2 a doubled emission area,
half-size sprites and unscaled speed — three different worlds in one effect.

`SimulationDistance` 120 yd, `MaxParticles` 40,000. Steady-state population is
`rate x lifespan`, so a portal is `525 + 275 = ~800` sprites — nothing.

### 4.1 What review caught before it ran

- A **missing texture bound name 0**, which samples opaque black; black at the
  ramp's alpha survives the discard, so an alpha group would have painted black
  squares over the scene rather than drawing nothing. Skips now.
- **Depth test enabled and never restored** — a silent z-fail in the next pass.
- **`HorizontalRange` parsed, printed, never used**, so a narrow fan emitted a
  full ring.
- **`ofsKeys + 4 <= data.Length` was unchecked `uint` arithmetic** — a misparsed
  offset near `uint.MaxValue` wraps, passes, then throws on the cast and takes
  down the model load.

---

## 5. The portal's shape — four models tried, the first one was right

This is the part that is inference rather than emulation, and the history is
kept because the same wrong turns are available to anyone who picks it up.

**Direction: settled, closed. Use WoWee's formula.**

```csharp
var dirRaw = new Vector3(
    pool.Symmetric() * e.HorizontalRange,
    pool.Symmetric() * e.HorizontalRange,
    1f + pool.Symmetric() * e.VerticalRange);
// normalise; then Swap(), then the bone spin
```

**`verticalRange` and `horizontalRange` are NOT angles.** They are additive
jitter on the *components* of a direction that starts as model up, normalised
afterwards. With InstancePortal's `hRange 0, vRange pi`: X and Y get nothing, Z
becomes `1 + U(-pi, pi)` which normalises to ±1. **Every particle travels along
one axis**, and the bone sweep turns that axis into a disc.

This also explains a dead end: 212 of 910 emitters use `verticalRange = pi`
exactly, and **no flag bit correlates with wide-vs-narrow** — because none needs
to. One formula gives a torch at 0.087 a tight jet and the portal at pi a flat
sheet. *The absence of a correlation was the answer, and it was read as a
missing clue.*

| model tried | motion | what it looked like |
|---|---|---|
| cone about the normal, half-angle = `verticalRange` | isotropic at pi | volumetric plume |
| **WoWee's componentwise jitter** | tangential, in-plane | **correct shape** |
| radial in/out from the origin | converging on a point | two bright cores |
| wiki-literal polar/azimuth | leaves the disc's plane | no visible change, measurably worse |

The last one was **measured, not argued**: the mesh is a flat ring in the model's
**YZ** plane (30 vertices, `x = ±0.273` against `y,z = -4.4..+7.2`), so **X is
the disc's normal**. Sampling 4000 spawns through the real code path:

```
WoWee's formula          mean |x| of direction = 0.000   max 0.000
wiki polar/azimuth       mean |x| = 0.636                max 1.000
```

WoWee's already produces exactly the flat in-plane disc the geometry implies.
**The direction was never the bug.**

### 5.1 What WoWee is and is not evidence for

`m2_renderer_particles.cpp` opens its spawn loop with
`if (gpu.isInstancePortal) return;` — at lines 85, 252, 374 and 524. It matches
by filename in `m2_model_classifier.cpp` and substitutes two hand-authored
sprites (a blue core at 7x scale, a halo at 2.4x size and 35% alpha).

**So WoWee is authoritative for the generic emitter formula and silent on this
model.** One build was lost to adopting *"WoWee never uses `emissionArea`"* from
code that never executes for a portal. Its omission is harmless for every other
emitter only because their areas are 0.007..0.5, where point and area spawn look
identical; the portal's is **4.167** and the waterfall's is **18.0**. Born at one
point under a sweeping direction, particles trace a single thin coherent ribbon.
Born across the rectangle, the same sweep smears into a sheet.

> **A reference is only evidence for the code paths it actually runs.** *"X does
> not do Y"* is a claim about X's behaviour, not about the format. Check that
> the code you are citing executes for the case you are citing it for.

### 5.2 The two mechanisms that produce the inward spiral

Neither is a direction change, and that is the point — §5's dead end was
concluding that no single direction gives a dark centre with an inward spiral
*because none has an inward component that decays with radius*. That is still
true. These sidestep the requirement with **two independent knobs** instead of
one overloaded vector.

**1. `ReverseConverging` — play a converging emitter backwards.** A time-reversed
outward spiral *is* an inward spiral. Authored direction, speed, lifetime and
bone sweep are all kept exactly and simply run the other way, so the sweep still
supplies the curve — the path is a spiral, not a fall. Implemented as a
spawn-time transform, not a simulation mode:

```csharp
if (ReverseConverging && e.EmissionSpeed < 0f)
{
    position += velocity * e.Lifespan;
    velocity  = -velocity;
}
```

Gravity, the ramp and culling are untouched. **On by default.**

**2. `ReverseRamp` — sample the ramp at `1 - t`.** Currently **off**; kept
because the reasoning behind it is the correction to a real error. The ramp is a
function of a particle's **own age**, and age only maps to distance-from-centre
if every particle starts from the same place. One draft claimed the authored
`0 -> 50 -> 0` alpha darkened the centre for free; it cannot, because radial
motion converges every particle on one point regardless of birth time, so the
centre collects the *bright* ones and becomes a caustic. Reversing the **motion**
is what makes every particle start at the rim — which is what finally makes the
ramp mean what it looked like it meant.

### 5.3 The clock face

Nico's description named the last structural piece:

> *"many real origin points that let their animation start, go for a bit till
> getting close to center, interrupt, and between that and a restart another one
> starts, and another, but all staggered, and as if each owns a position on a 24
> hour round clock"*

**Random phase could never produce that.** Uniform random is not evenly spaced —
neighbouring particles land a degree apart or a hundred — so it reads as a mess.

`SpawnArms` quantises the phase to N slots and issues them **round-robin**:

```csharp
float phase = 0f;
if (SpawnArms > 0)
{
    phase = MathF.Tau * (pool.NextArm % SpawnArms) / SpawnArms;
    pool.NextArm++;
    if (SpawnPhaseJitter > 0f)
        phase += MathF.Tau / SpawnArms * SpawnPhaseJitter * pool.Symmetric() * 0.5f;
}
```

Every stream gets its own angle, they stay evenly separated forever, and they
stagger in time for free — the next particle always belongs to the next slot, so
each stream is born at a different point in its own cycle. `SpawnPhaseJitter`
survives as a fraction of **one slot's width**, which softens a spoke without
dissolving it back into the mess.

### 5.4 The double swirl was never a bug, and spin rate is why

WoWee's `1 + U(-pi, pi)` on Z is positive about two thirds of the time and
negative the rest, so **every spawn instant fires in two opposite directions** —
two arms 180 degrees apart.

A particle lives **1.05 s** while the emitter sweeps `1.05 / 3.334 = 113
degrees`, so each arm is a 113-degree arc **with a gap after it**:

| spin x | arc per arm | result |
|---|---|---|
| 1 | 113 deg | two separated arms |
| **1.86** | **210 deg** | the arcs overlap — current default |
| 4 | 452 deg | each arm laps itself, one continuous band |

"Rotate faster" and "more starts" are therefore **one** change, not two. The
panel prints the arc so the number is not blind.

---

## 6. The tuned values, and the debt they represent

**Six values are hand-set to match a reference by eye.** That is the opposite of
how everything else here was derived, and it must not be allowed to settle. They
are the current defaults because they look right, and each is **a lead, not an
answer**:

| knob | value | what a real explanation would look like |
|---|---|---|
| `SpinRateScale` | **1.86** | **Most likely to have a real answer in the data.** If the bone were played 1.86x faster the sweep would be right — so either a sequence duration is misread, or the format carries a playback rate that is not being applied, or there is a global sequence |
| `SpawnArms` | **24** | No measured field carries a stream count. If 24 is visually right, the live client probably spawns continuously against a *much* faster sweep and the spokes are aliasing — in which case this is standing in for `SpinRateScale` |
| `CentreHoleYards` | **4.74** | **Chase this first.** Suspiciously close to the ring mesh's own outer radius of **4.41**. If the inner cutoff is really the geometry, the "hole" is not a particle behaviour at all — and the first check needs no new theory: **is that 30-vertex mesh drawing?** It is blend mode 0, and a doodad renderer that alpha-tests or z-sorts it differently could be dropping it silently |
| `DensityScale` | 1.09 | — |
| `SpawnPhaseJitter` | 0.25 | fraction of one arm's slot |
| `ReverseConverging` on / `ReverseRamp` off | — | §5.2 |

There is also a structural suspicion worth recording: the spawn rectangle is
`lx = length * 0.5 * U(-1,1)`, `ly = width * 0.5 * U(-1,1)` — **a centred
square**. Uniform over a centred square puts as many particles in the middle as
anywhere, and viewed face-on the middle is where paths cross, so **the centre is
the densest place on screen under every direction model, forwards or reversed.**
That is why reverse-time helped and did not finish the job. If the mesh is an
annulus, the emission area may be meant as its **extent** rather than a filled
rectangle — spawning on the ring is the untested cheap experiment.

> Whichever combination lands, **record the values and then go find what in the
> data produces them.** A tuned constant that matches the reference is a lead.

---

## 7. Instruments

- **Particles panel** (`GameLoop/Combat/GameLoop.Particles.cs`) — a `LIVE:` count of particles and
  pools, every tunable above, per-emitter readouts of the parsed fields, the
  computed arm-sweep arc, and `Dump to console`.
- **Startup lines.** `[particles] texture not found:` means a BLP path from the
  M2 did not resolve — sprites will be missing, and that is the first thing to
  check when nothing appears.
- **The console dump prints the emitter position and whether a bone spin was
  found**, because the build that got §3 wrong could answer neither.
- `simulate` and `draw` are timed separately in the panel; both should be well
  under a millisecond.

**First-run check:** stand at the Deadmines entrance. The portal should be
**blue, additive and pulling inward**. A fountain means the negative speed got
clamped somewhere (§1.3). The panel should read roughly **800 particles in 2
pools** for a portal alone.

---

## 8. Not done

- **The 4x4 sprite-sheet flipbook.** Every flame uses one and every glow uses
  1x1, so torches and campfires **glow but do not lick**. `headCellTrack` lives
  in the still-uncracked +360..+504 region, alongside spin, drag, tumble and
  wind.
- **The six tuned values of §6** — the main debt.
- **Sphere and spline emitter types.** Only `plane` (type 0) is handled.
- **Particle tails**, and **ribbon emitters** (2% of models).
- **The flags of §1.2** — notably `0x80` particles-in-model-space, which is the
  format's own mechanism for the decaying orbit that four direction models
  failed to produce, and `0x8` world-space up.
- **Whether the 30-vertex ring mesh is drawing at all** (§6). Cheapest
  outstanding check in the system.
