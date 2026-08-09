# Spell emitter lab

This is the working, evidence-backed model behind Spell Creator's Advanced
controls. It separates facts verified in the mounted 1.12 data and MSUI's
runtime from features that still need an authoring strategy.

## Reproducible census

Run:

```powershell
dotnet run --project tools/spell-emitter-lab/spell-emitter-lab.csproj --no-restore -- MSUIClient/client-config.json
```

The lab walks every M2 referenced by a spell visual phase, parses it through
both `M2Reader` and `Creator.M2EmitterParser`, validates their shared fields,
then performs disposable patch/clone round trips. It writes the full inventory
to `dumps/spell-emitter-census.csv`.

Current mounted-data baseline:

- 559 referenced model paths; 541 resolved and 18 unresolved.
- 1,699 primary particle emitters.
- Shape IDs: 868 Plane (`1`), 811 Sphere (`2`), 20 Spline (`3`).
- 699 emitters have per-particle billboard spin.
- 164 have rotation animation on their emitter bone; 535 have some emitter-bone motion.
- 44 spawn geometry M2s per particle; 13 use a recursion model.
- 173 use one-shot burst emission.
- 21,736 schema, finiteness, mutation, runtime-parse, and clone-isolation checks pass.

The last count grows when the lab gains assertions; the dataset counts are the
stable comparison baseline.

## Verified 504-byte primary-emitter layout

| Offset | Field | Creator meaning |
|---:|---|---|
| `+0x004` | `uint32 flags` | Conditional motion/render behaviors |
| `+0x008` | raw `vec3` | Local X/Y ground plane and Z up |
| `+0x014` | `uint16 bone` | Animated frame the emitter rides |
| `+0x016` | `uint16 texture` | Direct index into the M2 texture table |
| `+0x028` | `uint8 blend` | Particle compositing mode |
| `+0x029` | padding | Not an emitter type |
| `+0x02A` | `uint16 shape` | `1` Plane, `2` Sphere, `3` Spline |
| `+0x02C` | `uint8 head/tail` | Head quad, trail, or both |
| `+0x034..0x14B` | 10 scalar tracks | Speed through `zSource` |
| `+0x14C` | midpoint | Middle color/scale key time |
| `+0x150` | 3 colors | Start/middle/end color |
| `+0x15C` | 3 scales | Start/middle/end size |
| `+0x194` | drag | Velocity damping, not `zSource` |
| `+0x198` | billboard spin | Image rotation over particle age |
| `+0x19C..0x1B3` | angular min/max | Spawned geometry tumble |
| `+0x1C4..0x1D0` | follow response | Parameters used by follow behavior |
| `+0x1D4` | spline reference | Authored control-point chain |
| `+0x1DC` | enable track | Animated emission gate |

The ten scalar value-array references are at `+0x048`, `+0x064`, `+0x080`,
`+0x09C`, `+0x0B8`, `+0x0D4`, `+0x0F0`, `+0x10C`, `+0x128`, and `+0x144`.
Clones receive private timestamp and value arrays for all ten so editing a clone
does not leak back into its source.

## What "spin" means

There is no single spell-spin property:

1. **Billboard spin** (`+0x198`) rotates every particle image around its own
   center as that particle ages. This is the Advanced `Billboard spin` control.
2. **Emitter-bone rotation** rotates the birth frame and origin. It sweeps the
   source through space, so particles born at different times form a whole
   swirl or disc. Portals and several nova effects use this mechanism.
3. **Geometry tumble** (`+0x19C..0x1B3`) gives spawned model particles an
   orientation and angular velocity. Billboard particles ignore it.
4. Mesh animation and ribbon motion are separate systems again.

Changing billboard spin cannot reproduce an animated portal bone. Bone-track
authoring therefore remains a later Advanced feature, not another spin slider.

## What adding emitters does

An exact clone shares the source's birth frame, shape, scalar behavior and
placement. It creates another independent particle pool. With no other edits it
roughly increases population and, for additive blends, brightness. It does not
make the source spatially wider.

The line composer adds real position offsets to cloned sources, producing a row
or column. This is the first useful composition primitive for wall-like effects:

- Local X/Y create a horizontal row in the source model's authored frame.
- Local Z creates a vertical column.
- Spacing controls coverage; rate/lifespan still control per-source population.
- The original remains centered and each clone stays independently editable.

A complete wall authoring tool still needs orientation control. Position can
place birth regions, and Plane Area L/W can enlarge them, but an emitter riding
an animated or rotated bone inherits that frame. Safely creating/editing bone
rotation tracks is not implemented yet.

## Exposed behavior flags

Advanced mode preserves the full authored flag word and toggles only verified
bits: model-space motion (`0x10`), instance scale (`0x20`), inherited motion
(`0x40`), sphere outbound kill (`0x80`), sphere-up direction (`0x100`), random
model-tumble sign (`0x200`), tail-age clamp (`0x400`), local XY quads (`0x1000`),
ground snap (`0x2000`), follow (`0x4000`), and burst (`0x8000`). Several are
conditional on shape or particle kind; the UI says so instead of presenting
them as universal modifiers.

## Next empirical slices

- Bone-frame inspection and deliberate orientation/rotation authoring.
- Geometry-particle selection plus angular-velocity editing.
- Head/tail and flipbook controls, with texture-atlas validation.
- Follow-response controls gated by the follow flag.
- A composition preview that labels source frames and bounds before cloning.
- Import rules for BLPs and geometry M2s based on the consumer field (billboard,
  atlas, mesh material, ribbon, spawned geometry, or recursion model).
