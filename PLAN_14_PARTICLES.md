# Plan 14 — M2 particle emitters

Status: **layout derived from the bytes. Stage 1 VERIFIED on Nico's machine
2026-07-26 across ~35 models. Stages 2-3 specified, not built.**

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
