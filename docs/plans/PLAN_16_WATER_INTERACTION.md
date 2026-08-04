# Plan 16 — Water interaction: the walking wake, and swimming

Status: **specified, assets and animations verified from the archives, NOT built.**
Written 2026-07-26. Feeds `SYSTEM_WATER.md` (§5's two open interaction entries)
and, for the swim state, `SYSTEM_CHARACTER.md` when that gets extracted.

> **Read §4 first.** Every asset and every number below was pulled out of Nico's
> own MPQs this session with `tools/mpqpy`, not recalled. Blizzard ships the
> textures for the wake and the splash, and the character model carries all six
> swim clips with authored move speeds. **This is not a feature that has to be
> invented — it has to be wired.**

---

## 1. Problem

Two things the real 1.12 client does that MSUI does not, both quoted from Nico:

1. *"When you step in water in the client, you get a trail behind you, basically
   a visual of you moving through it (walking)."*
2. *"Swimming in water."*

Today the character walks along the riverbed as if it were dry ground. There is
no surface interaction of any kind, no swim state, no buoyancy, and the water
surface has no idea the player exists.

## 2. Class

**Emulation-core, both halves.** Measured against the real 1.12 client.

Better conditioned than water colour was, because the yardstick is not a
screenshot for most of it: the wake has an authored texture, and the swim speeds
and clip lengths are **numbers in the model file** (§4.2). The parts that remain
judgement — wake size, fade rate, how far the swimmer floats out of the water —
are called out in §8.

## 3. Target

**Wake.** Walking through shallow water leaves a visible disturbance that trails
behind and fades: a soft light streak at the waterline that follows the feet, not
a hard decal and not a geometry ripple.

**Swim.** Walk into deepening water and the character transitions to swimming at
the surface: the swim clips play, movement changes speed and gains a vertical
axis, gravity is replaced by buoyancy, and coming out the other side returns to
walking without a visible pop.

## 4. Ground truth — verified this session, do not re-derive

### 4.1 Blizzard ships the wake and splash textures

From `texture.MPQ`, decoded with `tools/mpqpy`:

| file | size | format | mean RGB | mean alpha |
|---|---|---|---|---|
| `XTextures\splash\wake.blp` | 128x128 | DXT3 | `0.024 0.023 0.024` | **0.451** |
| `XTextures\splash\splash.blp` | 128x128 | palettized | `0.057 0.057 0.057` | — |
| `XTextures\caustic\caustic02..30.blp` | 256x256 | DXT1 | `0.320 0.314 0.320` | — |

`XTextures\splash\` contains **exactly two files**, and they are named `wake` and
`splash`. That is not a coincidence and it is the strongest single signal in this
plan: **the effect Nico is describing has a dedicated authored texture.**

**Note the pattern — `wake.blp` is near-black greyscale RGB with a strong alpha
channel**, exactly like `lake_a.blp` (SYSTEM_WATER §8.1). These are **masks**, not
pictures. Whatever draws the wake must treat it the way §8.3 now treats the liquid
texture: the mask supplies shape and animation, something else supplies colour.
**Do not sample `wake.blp` and expect a visible image** — that is the identical
mistake that made the river black for the life of the project.

`caustic\` holds **29 frames**, the same count as the liquid animations. That is
the underwater caustic sweep, and it belongs to the underwater overlay
(SYSTEM_WATER §1.6), not to the wake. Out of scope here; recorded so it is not
lost.

### 4.2 The character has every swim animation, with Blizzard's own speeds

`Character\Human\Male\HumanMale.m2` — 142 sequences, 128 distinct animation IDs,
read with `SEQUENCE_STRIDE_VANILLA = 68` (the value in `M2Reader.cs`; **the
commonly-quoted 64 is wrong and produces pure garbage** — it gave animation IDs
of 65535 and 32767 before being corrected against the client's own reader).

| id | name | length | authored moveSpeed |
|---|---|---|---|
| 41 | SwimIdle | 1667 ms | 0.000 |
| 42 | Swim | 1000 ms | **4.722** |
| 43 | SwimLeft | 1333 ms | 2.500 |
| 44 | SwimRight | 1334 ms | 2.500 |
| 45 | SwimBackwards | 1500 ms | 2.500 |
| 46 | SwimForward | 1000 ms | 0.000 |

For scale, from the same model: **Walk 2.500, Run 6.944**, Stand 2667 ms.

**`4.722` is the swim speed and it should not be invented or tuned.** It is
authored in the model, it sits sensibly between walk and run, and it matches
vanilla's documented swim speed. `M2Animator` already maps 41 -> `SwimIdle` and
42 -> `Swim`, so the naming half is done.

`SwimForward` (46) having moveSpeed 0.000 while `Swim` (42) has 4.722 is worth
noting rather than "fixing": 42 is the locomotion clip.

### 4.3 The hooks that already exist

- `LiquidRenderer.TryGetSurface(x, y, out height, out type)` — **two arguments,
  not three.** SYSTEM_WATER §5 has called this "the hook a future swim state would
  reuse" since Draft 2, and it is that hook.

  > **CORRECTED 2026-07-26 — this entry originally claimed a 3-argument overload
  > taking a query Z and returning "the lowest surface above z".** That overload
  > was added by PLAN_15 and went away when PLAN_15 was reverted; the plan was
  > written against code that no longer existed and the wake shipped a CS1501 to
  > prove it. The 2-argument form returns the first covering surface it finds; a
  > caller that cares about "above me" compares heights itself, which is what both
  > the wake gate and the underwater overlay already do.
  >
  > **If swimming wants the lowest-surface-above-Z behaviour, that is a real
  > change to a shared query and gets its own decision — not a silent addition.**
- `CharacterController` exposes `Grounded`, `Flying`, `GroundZ`, `TerrainGroundZ`,
  `NoGroundBelow`, and already has a fly mode that suspends gravity — swimming is
  structurally closer to fly-with-drag than to walking, and the F-key fly path is
  the proof that the controller tolerates a non-grounded locomotion mode.
- `water.frag` receives `vAbsXY` (absolute world XY) already, for world-locked
  effects. A player-position uniform has somewhere to go.

### 4.4 WoWee is only half applicable — do not copy it wholesale

WoWee implements a player ripple in **both** `water.vert.glsl` (concentric
geometry displacement from `playerPos`) and `water.frag.glsl` (radial normal
perturbation, `rippleEnv = rippleStrength * exp(-d * 0.12)`).

**The vertex half does not apply.** Our surface is deliberately flat —
`uWaveAmp` defaults to 0 and SYSTEM_WATER Draft 2 records at length why the
Gerstner displacement was turned off: it made the locked texture swim. Adding
player-driven geometry waves walks straight back into a bug that was already paid
for once.

The **exponential falloff shape** `exp(-d * k)` and the idea of a strength scalar
are worth taking. The displacement is not.

## 5. Key design decisions

**D1 — The wake is a screen-agnostic term inside `water.frag`, not a decal
renderer.** The surface already exists, already has world XY, already has the
right blend state and draw order. Adding a second pass with its own geometry,
sorting and depth rules to draw a 128x128 quad is a large amount of machinery for
an effect that is a function of `distance(fragXY, playerXY)`.

**D2 — Feed a short TRAIL, not just a point.** Nico's word is "trail", and a
single radial splat centred on the player is a puddle, not a trail. Keep a small
ring buffer of recent player positions (say 8 samples over ~1.5 s) and pass them
as a uniform array with ages; the wake is the union of their contributions, each
fading with its own age. This is the difference between the effect he asked for
and the one that is easier to write.

**D3 — The wake only exists where the player is actually IN the water.** Gate on
`TryGetSurface` reporting a surface above the feet and below the head. No wake
while swimming in deep water far from the surface, none while walking on a bridge
over a river, none on dry land. The gate is a scalar the shader already needs
(`uWakeStrength`), so "not in water" is strength 0 and costs nothing.

**D4 — Swimming is a controller STATE, entered and left on hysteresis.** Two
thresholds, not one: enter swim when submersion exceeds ~1.4 yd, leave when it
drops below ~1.1 yd. A single threshold at a shoreline produces a swim/walk
flicker at exactly the depth a player naturally stands at, and that flicker will
also strobe the animation. This is the same class of defect as snapping vs
falloff in the light blend (`SYSTEM_EXTERIOR_LIGHTING.md` §3).

**D5 — Buoyancy replaces gravity, it does not fight it.** In the swim state,
gravity is off and vertical velocity is driven toward "eye at the surface" with a
spring plus damping, so the swimmer settles at the waterline instead of bobbing.
Space swims up, Ctrl swims down, both clamped by the surface. Reusing the fly
path's "gravity suspended" branch is deliberate: it is proven, and it means swim
does not introduce a third way for vertical motion to work.

**D6 — Do not touch the shared water path beyond one new uniform block.** The
lesson of the PLAN_15 revert. The wake adds `uWakeXY[]`, `uWakeAge[]`,
`uWakeCount`, `uWakeStrength`. Nothing else in `water.frag` changes. With
`uWakeStrength = 0` the shader must be **bit-identical** to today, and the
feature ships **default ON but instantly killable** by that one float.

**D7 — Land the wake first, swimming second, and do not braid them.** They share
only `TryGetSurface`. The wake is a shader term with a HUD strength slider and
carries almost no risk; swimming touches the character controller, the animator
and the camera, and is where the risk actually is.

## 6. Instrument

Mostly present. `tools/mpqpy` answered every format question in §4 without a
build. In game:

- The **Water tuning panel** gains a wake group (strength, radius, falloff, trail
  length, fade) so the look is dialled live rather than rebuilt.
- A HUD readout for the swim state: `submersion`, `state (ground/swim/fly)`,
  `surfaceZ`, `current clip`. Without this, "swimming feels wrong" is not
  debuggable — and **the handbook's loudest lesson is §"never debug movement in a
  world where you cannot see the state"**, which cost rounds on a 2-yard camera
  offset that did not exist.
- **What is missing: a `refs/` capture of the real client's wake.** §8.

## 7. Test protocol — written before the change

**Wake**

1. Walk into the Elwynn river to knee depth. A visible disturbance follows the
   feet and **trails behind**, fading over roughly a second.
2. Stand still in the same spot. The trail fades to nothing and does not
   re-trigger.
3. Walk along a bridge over the river. **No wake.** (D3 — this is the one that
   catches a missing gate.)
4. Set `Wake strength` to 0. The river must look **exactly** as it does today.

**Swim**

5. Walk down the riverbank into deep water. The transition to swimming happens
   once, at a consistent depth, with no flicker at the margin. Walk back out: the
   reverse transition also happens once, at a **shallower** depth than the entry
   (D4's hysteresis, and the readout shows both).
6. While swimming, the character sits **at** the surface, not under it and not
   floating above it. Turn, swim forward and backward; the clip changes and the
   speed is ~4.722 forward.
7. Swim into a wall. Collision still works — the horizontal sweep must not be
   skipped just because gravity is.
8. Swim over a spot where the riverbed rises. You should ground out and start
   walking, not hover.
9. Jump into deep water from the bank. No fall damage state weirdness, no
   grounding on the surface.

**Both**

10. `Escape -> Video Options` with `DevTools: false`. Both features still work —
    the FOUNDATION_PLAN §12 seam violation that exterior lighting shipped once
    already (`SYSTEM_EXTERIOR_LIGHTING.md` §4.0).

## 8. Definition of done, and what will remain unverified

Done when §7's ten steps pass and `SYSTEM_WATER.md` §5's two interaction entries
are replaced by a section.

**Explicitly NOT claimed:** a 1:1 match to the real client's wake. `refs/` still
holds only a README, and the wake's size, brightness and fade are ours. Following
§8.4's precedent for water colour, this ships as **sufficient**, tunable from the
HUD, and reopened only with a capture. Say so in the doc rather than implying a
match that was never tested.

## 9. Fallback

- **The trail (D2) proves fiddly:** ship the single-point radial wake first. It is
  strictly less than what was asked for, so say so plainly rather than quietly
  shipping the easier thing.
- **Swimming destabilises the controller:** it is a state behind a flag; default
  it off and leave walking untouched. **Do not repeat PLAN_15** — a movement
  change must not ship default-on and uncompiled.
- **Both are independently revertable** and neither is a prerequisite for the
  other.

## 10. Reconciliation

- **`SYSTEM_WATER.md`** — §5's "player interaction ripples" and "swim physics"
  entries close; a new section takes them. §8.4's "sufficient, not 1:1" framing is
  the precedent this plan follows.
- **`SYSTEM_FOLIAGE.md`** — unaffected.
- **`M2Animator`** — already names 41/42; may need 43-46 added to the map.
- **`CharacterController`** — gains a state. This is the risky file in the plan
  and the reason for D7's ordering.
- **Handbook** — §3.14 "movement feel — no smoothing on the stop" is the
  neighbouring ground truth; swimming must not reintroduce smoothing on the walk
  transition. §7.1 gains nothing; this is new work, not a listed item.
- **PLAN_15** — unrelated, still reverted, still a spec.
