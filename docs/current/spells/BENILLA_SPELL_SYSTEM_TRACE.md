# benilla Spell System — Complete Port Trace (benilla → MSUIClient/C#)

**Purpose.** A file:line-cited, math-complete trace of the ENTIRE benilla (Rust, Bevy) WoW-1.12 spell
system, structured so it can be copied to the C# MSUIClient without silently dropping a spell class. This
document specifies **what benilla actually implements**, including its incomplete gates and named
approximations; it does not promote those gaps to original-client behavior. Every section cites
`crate/path.rs:line` in the audited source snapshot at `C:\Users\nico\Desktop\benilla-main`.

**Critical truth boundary.** benilla is a presentation client, not a spell-gameplay simulator. The server
selects targets, computes damage/healing/power changes, applies and removes auras, resolves misses, and sends
cooldowns. benilla consumes those outcomes and presents them. To recreate benilla perfectly in C#, reproduce
both the data-driven presentation pipeline and the same server-authoritative inputs; do not try to derive
authoritative combat outcomes from the few `Spell.dbc` fields benilla parses. See Sections 13-20 for the
complete spell-class, targeting, aura, cast-state, outcome, and audit coverage that sits around the renderer.

**How to use.** Port in dependency order: data (§1–2) → events (§3) → orchestration (§4) → attach (§6) →
FX render (§5) → particles (§7–8) → decals (§9) → missiles (§10) → body animation (§11) → sound (§12). Each
section is self-contained but cross-references the others by number. Where benilla and MSUI already agree, the
section says so; where a naive port silently diverges (almost always a coordinate/axis/pivot/winding detail),
the section flags it explicitly.

---

## Coordinate systems — the single most important thing to get right

A port that gets any spell geometry wrong is nearly always getting one of these conversions wrong. Both
engines are Y-up but with DIFFERENT axis maps from WoW, so a value carried from a DBC/M2 straight across will
be silently rotated.

| Space | Up axis | Ground plane | Notes |
|---|---|---|---|
| **WoW / server** | +Z | XY | DBC positions, server coords, M2 raw bone pivots/attach positions |
| **benilla (Bevy)** | +Y | XZ | right-handed; converts at asset load / particle birth via `wow_to_bevy` |
| **MSUI (C#)** | +Z | XY | keeps WoW Z-up for world/units; M2Reader converts M2 model data to a LOCAL Y-up: `(px, pz, −py)` |

- **benilla `wow_to_bevy`** (documented exactly in §1): WoW `X→−Z, Y→−X, Z→+Y`, a proper rotation (det +1).
  Applied to positions/normals at asset load and to emitter vectors at particle birth.
- **MSUI M2Reader** converts each M2 vertex/pivot/attachment to local Y-up as `(PosX, PosZ, −PosY)` — a
  DIFFERENT map than benilla's. **When porting a benilla formula that contains `wow_to_bevy(v)`, do NOT copy
  the Bevy axes; instead express the operation in whatever space the MSUI value already lives in.** The safe
  discipline: port the *geometry* (which bone, which pivot, which offset, which rotation order), not the raw
  component swaps.
- **Up-axis substitution:** any benilla step that touches `.y` as "up" (gravity, ground plane, height tracks,
  ribbon sag, decal projection slab) becomes `.z` in MSUI world space, or `.y` in MSUI *model-local* space
  (which is Y-up). Each section states which frame it is in.
- **Winding / handedness:** Bevy and MSUI's GL are both right-handed, but a coordinate map with an odd number
  of axis negations flips triangle winding. §9 (ground decals) and §8 (quads) call out where this matters —
  the current MSUI half-ring bug is in this family.

## Glossary of benilla → MSUI equivalents (populated by the sections)

| benilla | MSUI (current) | Section |
|---|---|---|
| `route_cast_visuals` | `Program.Casting.cs` (ApplySpellStart/Go/Impact) | §4 |
| `attach_effect_visuals` / `SpellKitFx` | `SpellEffectSource` + `SpellEffectMeshRenderer` | §5 |
| `BoneAttach` / `spawn_joints` / cascade | `SpellAttachment` + `SpellUnitPose` | §6 |
| particle `sim.rs` / `emit_local` | `World/Spells/SpellParticleSystem.cs` | §7 |
| `quads.rs` expand | `SpellParticleSystem` Fill/Render | §8 |
| `ribbons.rs` | `SpellRibbonRenderer.cs` | §8 |
| `ground_fx.rs` | `SpellEffectMeshRenderer` ground-quad path | §9 |
| `entities/missile.rs` | `SpellEffectSource` missile path | §10 |
| creature_anim `driver.rs` | `M2Animator` + `CharacterRenderer`/`CreatureRenderer` | §11 |
| `sound/*` | (none — MSUI has no audio subsystem) | §12 |

## Section index
1. Coordinates, M2 asset load, skeleton & animation bake
2. DBC & binary record data layer (spell chain, emitter, ribbon, anim tracks)
3. Network packets → internal spell/aura events
4. Cast orchestration / router (the 5 stages, auras, channel, sound/anim emission)
5. Effect-model FX spawn + render body + materials
6. Bone-attach & joint system
7. Particle simulation (birth, emission, integration math)
8. Particle rendering (quads/billboards) + ribbons
9. Ground-decal projection (AoE rings)  ← the 180° half-ring bug lives here
10. Missile pipeline
11. Body/unit animation driver + billboard joints
12. Spell sound
13. Completeness boundary and exhaustive spell-class matrix
14. Target acquisition, local cast admission, and outbound packet paths
15. Buffs, debuffs, tracking, shapeshifts, channels, and aura UI
16. Cast bar, cancellation, cooldowns, auto-repeat, and next-swing state
17. Direct, periodic, AoE, miss, heal, power, shield, and environmental outcomes
18. Spell metadata formulas and parsed-versus-consumed field ledger
19. C# architecture, update order, and acceptance tests
20. Source inventory, hashes, and final no-exception audit


---

# Section 1 — Coordinates, M2 → In-Memory Model, Skeleton, and Animation Bake

Scope: the pure geometry/skeleton/animation-bake layer of benilla's spell rendering. Everything
here is what the M2 **asset loader** produces at load time (once per model, shared across
instances). Runtime posing/driving is deferred to sections 6/7/11. Every fact below is portable to
C# with no benilla context; cite lines when you copy.

Crate roles:
- `crates/benilla-m2` — raw byte reader → `M2Model` (bones, vertices, tracks, attachments…). Raw
  WoW model space (Z-up), no coordinate conversion.
- `crates/benilla-formats/src/models/*` — a second raw layer (`ModelAnimation`, `RenderSubmesh`,
  `Skeleton`, `GroundQuad`, …) built on `benilla-m2`. Still **raw WoW space**. Does the per-sequence
  key-band slicing and interpolation-window logic.
- `crates/benilla-assets` — the Bevy boundary. **This is where WoW→Bevy conversion happens.**
  Produces `M2Model` (the asset), `ModelSkeleton`, `AnimClip`, `ModelAnimations`, the inverse
  bindposes, mesh sub-assets, etc.

---

## 1. `wow_to_bevy` — the coordinate transform (THE cross-cutting fact)

File: `crates/benilla-assets/src/coords.rs`.

### 1.1 Position map

```rust
// coords.rs:17-19
pub fn wow_to_bevy(p: [f32; 3]) -> Vec3 {
    Vec3::new(-p[1], p[2], -p[0])
}
```

- Input: raw WoW `[x, y, z]` with **+X = north, +Y = west, +Z = up**, 1 unit = 1 yard
  (coords.rs:5).
- Output: Bevy right-handed **+Y up, −Z forward**.
- **Axis mapping** (golden-tested at coords.rs:137-153):
  - WoW **+X (north)** → Bevy **−Z** (forward)
  - WoW **+Y (west)** → Bevy **−X**
  - WoW **+Z (up)** → Bevy **+Y** (up)
- Component formula: `bevy.x = -wow.y`, `bevy.y = wow.z`, `bevy.z = -wow.x`.
- It is a **pure rotation, determinant = +1** (coords.rs:170-179 asserts `det ≈ 1`), so it never
  mirrors: **winding order is preserved and normals transform by the same map** (no flip needed).
  1 unit stays 1 yard (no scale).

### 1.2 Inverse map

```rust
// coords.rs:22-24
pub fn bevy_to_wow(b: Vec3) -> [f32; 3] {
    [-b.z, -b.x, b.y]
}
```

Exactly undoes `wow_to_bevy` (round-trip tested coords.rs:101-115). Use it to send benilla positions
back upstream (network / server frame).

### 1.3 The transform as a quaternion (for rotating quats/normals via conjugation)

Two byte-identical definitions exist (kept local to each module deliberately, each derived from
`wow_to_bevy` so they cannot drift):

```rust
// coords.rs:28-35  — columns are the Bevy images of the WoW basis: X→−Z, Y→−X, Z→+Y
fn wow_to_bevy_quat() -> Quat {
    Quat::from_mat3(&Mat3::from_cols(
        Vec3::new(0.0, 0.0, -1.0),   // image of WoW +X
        Vec3::new(-1.0, 0.0, 0.0),   // image of WoW +Y
        Vec3::new(0.0, 1.0, 0.0),    // image of WoW +Z
    ))
}
```

```rust
// model.rs:441-447  — same rotation, built by feeding the basis vectors through wow_to_bevy
fn wow_to_bevy_quat() -> Quat {
    Quat::from_mat3(&Mat3::from_cols(
        wow_to_bevy([1.0, 0.0, 0.0]),
        wow_to_bevy([0.0, 1.0, 0.0]),
        wow_to_bevy([0.0, 0.0, 1.0]),
    ))
}
```

The 3×3 rotation matrix (columns are images of WoW X,Y,Z):

```
        | 0  -1   0 |
R =     | 0   0   1 |
        |-1   0   0 |
```
i.e. `R * v_wow = wow_to_bevy(v_wow)` for any vector. Verified equal to `wow_to_bevy` on all basis
vectors (coords.rs:156-168, model.rs:734-745).

### 1.4 How it is applied — three different rules by data kind

| Data kind | Rule | Where |
|---|---|---|
| **Position / pivot / translation-track value** | `wow_to_bevy(p)` (the linear map) | model.rs:147-151, 285, 490 |
| **Normal** | `wow_to_bevy(n)` — identical map, valid because det +1 (pure rotation preserves normals) | model.rs:163-169 |
| **Rotation quaternion** (bone rot track, grip pose, global-seq rot) | **conjugation** `r · q · r⁻¹` where `r = wow_to_bevy_quat()` and `q = Quat::from_xyzw(x,y,z,w)` | model.rs:499-508, 541, 666-677 |
| **Scale vector** `[sx,sy,sz]` (magnitudes, not vectors) | **axis permutation only, no sign flip**: `Vec3::new(s[1], s[2], s[0])` = `(sy, sz, sx)` | model.rs:516-520, 679-686 |

Conjugation law (proven by tests): a WoW rotation about **+Z (up)** by θ maps to a Bevy rotation
about **+Y (up)** by the same θ (model.rs:751-764; coords.rs:123-133). The quaternion input order is
**`[x, y, z, w]`** — the raw M2 stores it in this order (uncompressed 4×f32 in vanilla).

**PORT WARNING (MSUI uses its own M2 Y-up convention):** MSUI must NOT reuse benilla's `(-y, z, -x)`.
The porter must (a) pick MSUI's own WoW→engine basis `R'`, (b) apply positions/normals/translation
values via `R'`, (c) conjugate every rotation quat by `R'` (`R' q R'⁻¹`), and (d) permute scale
components to match `R'`'s axis reordering with **no sign changes**. If `R'` has determinant −1 (a
mirror, e.g. a left-handed target), you MUST additionally flip triangle winding and negate the
conjugated rotation handedness — benilla sidesteps all of that by keeping det = +1. Do not copy the
literal numbers; copy the *procedure* (linear map for points/normals, conjugation for rotations,
permutation for scale).

### 1.5 Placement / doodad rotation helpers (context; not on the spell-mesh bake path but same conjugation pattern)

- `placement_rotation(rotation_deg)` (coords.rs:49-62): MDDF/MODF Euler → Bevy quat.
  In-WoW rotation `Rx(π/2)·Ry(ry−π)·Rz(−rx)·Rx(rz−π/2)`, then conjugated `to_bevy * in_wow *
  to_bevy.inverse()`.
- `wmo_doodad_local(position, orientation[xyzw], scale)` (coords.rs:75-89): MODD full-quat doodad;
  translation via `wow_to_bevy`, rotation conjugated + normalized, uniform scale kept (basis-invariant).

These matter for doodad placement (section 6+), not for the intrinsic model bake — noted so other
sections know where placement rotation lives.

---

## 2. M2 → in-memory model: what the loader produces

The Bevy asset is `M2Model` (`crates/benilla-assets/src/m2.rs:41-110`). Produced by
`M2ModelLoader::load` (m2.rs:229-618). Fields (exhaustive):

| Field | Type | Meaning / source |
|---|---|---|
| `submeshes` | `Vec<ModelSubmesh>` | one per render batch; both a static and a skinned mesh handle |
| `bounds` | `Option<M2Bounds>` | authored bounding sphere/box (distance-fade size) |
| `collision` | `Option<CollisionMesh>` | coarse collision hull (raw WoW space) |
| `emitters` | `Vec<ModelEmitter>` | particle emitters (def + texture handle + host-bone pivot + recursion/geometry child model handles) |
| `ribbons` | `Vec<ModelRibbon>` | ribbon/trail emitters (def + texture + host-bone pivot) |
| `lights` | `Vec<ModelLight>` | M2 light blocks (def + host-bone pivot) |
| `skeleton` | `ModelSkeleton` | rest skeleton in **Bevy space** |
| `inverse_bindposes` | `Handle<SkinnedMeshInverseBindposes>` | `translate(−pivot)` per bone, labeled sub-asset |
| `animations` | `Option<ModelAnimations>` | one `AnimationGraph` + per-sequence clip metadata |
| `first_seq_span` | `Option<f32>` | file-order-first sequence's authored duration (seconds) — see §7 |
| `attachments` | `Vec<ModelAttachment>` | weapon/hand/sheath attach points (Bevy-space bone-local offset) |
| `markers` | `Vec<ModelMarker>` | `$CSL/$CSR/$CST/$BWR` event positional markers |
| `portrait_camera` | `Option<PortraitCamera>` | authored portrait rig (eye/target Bevy space) |
| `camera0` | `Option<PortraitCamera>` | camera table record 0 (glue screens) |
| `string_anchors` | `Option<[(u16,Vec3);2]>` | bow `$WTT/$WTB` limb tips |
| `global_flags` | `u32` | MD20 `GlobalModelFlags` @ header +0x10 (terrain-conform gate) |

### 2.1 `ModelSubmesh` (per render batch)

`crates/benilla-assets/src/model.rs:53-135`. The load-bearing fields for a spell-effect mesh:

- `mesh: Handle<Mesh>` — **static** geometry, baked to Bevy space (no joint attributes).
- `skinned_mesh: Handle<Mesh>` — same geometry **plus** `JOINT_INDEX` (`Uint16x4`) and
  `JOINT_WEIGHT` (`Float32x4`). Built for every M2 (loader can't know role); only creature/skinned
  path uses it (model.rs:181-194).
- `texture: Option<Handle<Image>>`, `skin_slot: Option<u8>` (Monster1/2/3), `char_slot:
  Option<CharSkinSlot>`, `geoset_id: u16` (skinSectionId = group*100+variant).
- `blend: ModelBlend` (Opaque / AlphaTest / Blend / Mod / Mod2x), `two_sided`, `emissive` (UNLIT
  0x01 → fullbright), `additive` (blend 3/4 glow cards), `no_depth_write` (flag 0x10),
  `no_depth_test` (flag 0x08), `fog_policy`.
- `billboard: Option<BillboardInfo>` (see §9), `alpha_anim`/`uv_anim`/`rgb_anim` (`Arc`-wrapped
  material-animation loops — section 5), `wmo_batch: None` (M2), `ground_quad: Option<GroundQuad>`
  (see §8).
- `interior=false`, `sidn=None`, `window=false` for M2 (WMO-only fields).

Mesh bake, `build_submesh_mesh` (model.rs:141-174):
1. `center` = `wow_to_bevy(billboard.pivot)` if billboarded else `Vec3::ZERO` (billboard meshes are
   built **centred at their pivot** so the runtime rotates them in place).
2. positions: `(wow_to_bevy(p) - center)`.
3. UVs: copied verbatim (`Mesh::ATTRIBUTE_UV_0`).
4. indices: copied verbatim (`Indices::U32`) — **winding unchanged** (det +1).
5. vertex colors: inserted only if `vertex_colors.len() == positions.len()` (M2 tint bake — WMO MOCV
   for WMO).
6. normals: `wow_to_bevy(n)` per normal if authored, else `mesh.compute_normals()` (flat fallback).

Raw source `RenderSubmesh` (`crates/benilla-formats/src/models/types.rs:200-311`): positions,
normals, uvs, indices (all raw WoW space), texture path string, skin_slot, geoset_id, char_slot,
blend, two_sided, `joints: Vec<[u16;4]>` (**global M2 bone-array indices, used directly as joint
indices**), `weights: Vec<[f32;4]>` (normalized to sum 1.0), vertex_colors, the render flags,
billboard, alpha/uv/rgb anims, wmo_batch. Joints come straight from vertex `bone_indices`; weights
from `bone_weights/sum` (`normalize_weights`, m2_batches.rs:87-98 — a zero-weight vertex binds fully
to bone 0). See m2_batches.rs:502-538.

### 2.2 Emitters / ribbons / lights (each carries a host-bone pivot for joint riding)

- `ModelEmitter` (m2.rs:133-151): `def: ParticleEmitterDef`, `texture`, `bone_pivot: [f32;3]` (**raw
  WoW space** — `skeleton_raw.bones[def.bone].pivot`), `recursion`/`geometry` child `Handle<M2Model>`.
- `ModelRibbon` (m2.rs:202-207): `def`, `texture`, `bone_pivot`.
- `ModelLight` (m2.rs:214-218): `def: M2Light`, `bone_pivot`.

Convention (m2.rs:139-142): `def.position` is model-space; `position − bone_pivot` is the same point
in the bone's own frame — the offset an emitter riding a live joint composes through the joint
transform. **Emitter/ribbon/light pivots stay RAW WoW space** (unlike the skeleton, which is baked);
sections 6/7 must map them.

The raw skeleton used for these pivots is `parse_m2_skeleton(&bytes)` (m2.rs:304), i.e. `Skeleton`
in raw space — NOT the baked `ModelSkeleton`.

---

## 3. `build_skeleton` — bones → joints, bind pose, inverse bindposes

File: `crates/benilla-assets/src/model.rs:243-279`. Verified rig math for **vanilla M2**: there is
**no inverse-bind-matrix array on disk; rest pose is identity TRS; the bone PIVOT encodes bind
position** (model.rs:233-242, anim.rs:13-32).

Raw input `Skeleton { bones: Vec<SkeletonBone> }` (anim.rs:37-40). Each `SkeletonBone`
(anim.rs:16-32): `parent: i16` (`-1` = root), `pivot: [f32;3]` (raw WoW space), `key_bone: i16`
(`KeyBoneID`, `-1` none), `billboard: Option<BillboardKind>`, `ignore_parent_rotation: bool` (flag
0x04). Raw record: stride **0x6c**, `keyBoneId i32 @+0x00`, `flags u32 @+0x04`, `parent i16 @+0x08`,
`pivot C3 @+0x60` (benilla-m2 model.rs:90-103; anim.rs:42-62).

Algorithm:

```rust
// model.rs:243-279 (condensed)
let pivots = skeleton_pivots(skel);              // pivots[i] = wow_to_bevy(bone[i].pivot)
let joints = skel.bones.iter().enumerate().map(|(i, b)| {
    let parent_pivot = pivots.get(b.parent).copied().unwrap_or(Vec3::ZERO); // ZERO if -1/oob
    ModelJoint {
        parent: b.parent,
        local_translation: pivots[i] - parent_pivot,   // pivot_i − pivot_parent
        billboard: b.billboard,
        ignore_parent_rotation: b.ignore_parent_rotation,
    }
}).collect();
let inverse_bindposes = pivots.iter().map(|p| Mat4::from_translation(-*p)).collect();
```

Exact matrices (all in **Bevy space**):
- `pivot_i = wow_to_bevy(bone[i].pivot)` (model.rs:284-286, `skeleton_pivots`).
- **inverse bind pose** `I_i = translate(−pivot_i)` — a pure translation Mat4, **not** a full inverse
  bind matrix, because rest is identity TRS.
- **joint rest local translation** `L_i = pivot_i − pivot_parent(i)`. Pure translations, so they
  **telescope** up the chain to `pivot_i` (parent chain sums back to the absolute pivot). Root
  (`parent = -1`): `parent_pivot = Vec3::ZERO`, so `L_root = pivot_root`.
- Rest global of joint i = product of ancestor `translate(L)` = `translate(pivot_i)`. Therefore
  `joint_global_rest · I_i = translate(pivot_i) · translate(−pivot_i) = Identity` → at rest every
  skinning matrix is identity and the skinned mesh renders exactly where the static mesh did,
  undeformed, even under a scaled entity transform (model.rs:237-240).

Result `ModelSkeleton` (model.rs:218-229): `joints: Vec<ModelJoint>`, `spine_bone: Option<u16>` =
`key_bone == 4` (SpineLow), `head_bone: Option<u16>` = `key_bone == 6` (Head). `ModelJoint`
(model.rs:199-213): `parent: i16`, `local_translation: Vec3`, `billboard: Option<BillboardKind>`,
`ignore_parent_rotation: bool`.

Joint order = **M2 file/bone-array order** (unchanged). Vertex JOINT_INDEX values index directly into
this list (they ARE the global bone indices). The inverse bindposes vector is in the same order and
uploaded as a labeled sub-asset (m2.rs:378-382).

`skeleton_pivots` (model.rs:284-286) is the single source both `build_skeleton` (inverse bindposes)
and `build_attachments`/`build_markers` derive pivots from, so they never diverge.

**Attachments** `build_attachments` (model.rs:379-394): `offset = wow_to_bevy(position) −
pivot_bevy(bone)` — a Bevy-space **bone-local** offset (a child spawned at
`Transform::from_translation(offset)` under the joint sits at the attach point at bind and rides the
bone thereafter). ≈`Vec3::ZERO` on character hand bones. `ModelAttachment { id, bone, offset }`
(model.rs:293-298). **Markers** identical bake, `ModelMarker { ident:[u8;4], bone, offset }`
(model.rs:401-428).

---

## 4. `build_animation_clip` — tracks → a Bevy `AnimationClip`

File: `crates/benilla-assets/src/model.rs:474-530`. Consumes one raw `ModelAnimation`
(`crates/benilla-formats/src/models/anim.rs:271-335`) and the baked `ModelSkeleton`, emits a Bevy
`AnimationClip` (or `None` if nothing moves).

### 4.1 The three channels (exact composition, per bone, per keyframe)

`r = wow_to_bevy_quat()`. For each `BoneKeys` (`anim.bones`), `target = bone_target_id(bk.bone)`,
`rest = skeleton.joints[bk.bone].local_translation` (or ZERO):

```rust
// model.rs:487-527 (condensed)
// TRANSLATION — the M2 track is a DELTA on the pivot offset:
trans = bk.translation.map(|(t, v)| (t, rest + wow_to_bevy(v)));
// ROTATION — WoW quat conjugated into Bevy space:
rot   = bk.rotation.map(|(t, q)| (t, r * Quat::from_xyzw(q[0],q[1],q[2],q[3]) * r.inverse()));
// SCALE — WoW axes permuted to Bevy's (magnitudes; no sign flip):
scale = bk.scale.map(|(t, s)| (t, Vec3::new(s[1], s[2], s[0])));
```

Each becomes a curve on `Transform::translation` / `Transform::rotation` / `Transform::scale` via
`AnimatableCurve::new(animated_field!(...))`. `any` tracks whether any channel produced a curve;
returns `Some(clip)` iff `any`.

Critical facts:
- **Translation is rest + delta.** The clip translation is the **absolute Bevy-space local
  translation** = `local_translation (= pivot − pivot_parent)` **plus** the mapped track value. So
  the raw M2 track is authored as an offset relative to the pivot rest, and the bake folds rest in.
  (This is why a constant-but-nonzero translation ≠ rest — see the DuelingFlag content gate,
  m2.rs:167-183.)
- **Rotation is pivot-relative by conjugation.** `r·q·r⁻¹` reexpresses the WoW-space rotation in
  Bevy space. Order is exactly `r * q * r.inverse()` (quaternion product), input `[x,y,z,w]`.
- **Scale is (sy, sz, sx)** — the component permutation matching `wow_to_bevy`'s axis reorder with
  signs dropped (scales are magnitudes). Near-always uniform in practice.

### 4.2 Interpolation types and how each is evaluated

Vanilla M2 (v256) authors only **two** interpolation modes — there is **NO hermite/bezier** on this
path (those are TBC+ interp types 2/3; vanilla art never uses them):
- `interp_type == 0` → **step** (hold each key until the next; "none").
- `interp_type != 0` → **linear**.

Raw parse `M2Track` (`crates/benilla-m2/src/track.rs:15-36`): `interp: u16`, `gseq: u16`, `ranges:
Vec<(u32,u32)>`, `keys: Vec<(u32, V)>` (absolute ms). Quaternions are **uncompressed 4×f32**
(track.rs:129-138). Track stride 0x1c: `interp@0, gseq@2, ranges M2Array@0x04/0x08, timestamps
M2Array@0x0c/0x10, values M2Array@0x14/0x18` (anim.rs:339-357, track.rs:8-14).

**How the bake evaluates them:** benilla does NOT hand step/linear to Bevy as a mode flag. Instead it
pre-selects/samples the raw keys per sequence in `benilla-formats`, then hands the resulting keyframe
list to Bevy as a plain keyframe curve (`AnimatableKeyframeCurve`). Bevy then interpolates linearly
between the emitted keys at play time. Step semantics survive because the band-slicer, where it must
sample a step track at a band edge, emits the *held* value as a key. For material/UV/global channels
the sampler is explicit (`sample_window` / `KeyAnim::sample_clocked`) — see §5.

`keyframe_curve` (model.rs:451-464): 0 keys → `None` (channel stays at rest); **1 key → a flat curve
spanning `[0, duration.max(1e-3)]`** (Bevy requires ≥2 samples); ≥2 keys → the keys verbatim.

### 4.3 Per-sequence band slicing on shared key arrays

This is the subtle part. The raw tracks are read **once per model** (`ChannelTrack`,
anim.rs:343-357; `read_channel_track` anim.rs:370-412); every sequence carves its band out of the one
shared, absolute-timeline key list. `ChannelTrack::band(slot, start, end)` (anim.rs:446-491) is the
core:

- Times are absolute global-timeline ms; the sequence band is `[start_ms, end_ms]` (`M2Sequence`
  start@+0x04 / end@+0x08). Output times are **rebased to seconds from band start**.
- If the channel has no keys → empty.
- **Global-sequence channel** (`gseq != 0xffff`): here a lone key is folded in as a pure constant
  `(0.0, keys[0])`; a multi-key gseq belongs to the global-sequence lane (§5) and is dropped from the
  clip.
- Otherwise: window `(lo, hi) = ranges[slot]` (fallback `(0, last)`). `at(t) = sample_window(keys,
  step, lo, hi, t)` (key_anim.rs:139-169). In-band keys = keys with `start ≤ ts ≤ end`, rebased.
  - **Empty band** (no in-band keys): emit the window-resolved value at band start held across the
    band; two keys only when the bracket lerp actually moves within the band. This is why a bone
    keyed in another sequence's band still holds an authored pose here (decision 0133 fix).
  - Non-empty band: optionally prepend a head sample at `t=start` (only if it differs from the first
    in-band key), then all in-band keys, then optionally a tail sample at `t=end`.

`sample_window` (key_anim.rs:139-169): collapsed window `lo≥hi` → `keys[lo]`; else `k0` = last key ≤
`t` within `[lo,hi]` (clamped to `lo`), step/past-last → `keys[k0]`, else linear lerp with the
fraction **clamped to `[0,1]`** (benilla's one deliberate deviation from the reference's unclamped
extrapolation, key_anim.rs:130-138).

`parse_m2_animations` (anim.rs:570-717) drives all of this: reads sequences (stride 0x44), bones
(stride 0x6c, three tracks at +0x0c/+0x28/+0x44), events; skips zero-duration sequences; sets
`looping = (flags & 1 == 0)` (**bit0 SET ⇒ clamp/one-shot, CLEAR ⇒ loop** — anim.rs:665).

`ModelAnimation` fields you carry into the clip (anim.rs:271-335): `anim_id` (AnimationData.dbc id,
selection key), `seq_index` (file slot — NOT list index), `start_ms/end_ms`, `duration` (=
`(end−start)/1000`), `looping`, `move_speed` (yd/s, locomotion rate denom), `blend_time` (seconds,
cross-fade in), `bounds_center/radius/min/max` (raw WoW space — mapped to Bevy in the clip),
`frequency` (variation weight), `min_replay/max_replay`, `bones: Vec<BoneKeys>`, `events`.

`AnimClip` (`crates/benilla-assets/src/model/anims.rs:13-76`) is the per-clip metadata the graph
node carries: `anim_id`, `seq_index`, `node: AnimationNodeIndex`, `looping`, `duration`,
`move_speed`, `blend_time`, `bounds_center/radius/min/max` (**now Bevy space** — mapped at
m2.rs:509-513), `events`, `arm_nodes`/`upper_node` (masked variants), `frequency`, `replay`.

---

## 5. Global sequences — independent-clock loops

Detection (raw): `parse_m2_global_sequence_bones` (`crates/benilla-formats/src/models/anim.rs:762-808`).
- Global-sequence duration table at MD20 `0x14` (count) / `0x18` (offset), u32 ms
  (`period_of(gseq)` returns the duration, `None` if oob or 0).
- Per bone (stride 0x6c) read the three tracks (translation +0x0c, rotation +0x28, scale +0x44) via
  `read_global_channel` (anim.rs:723-753): qualifies **iff** `gseq != 0xffff`, period > 0, and
  **more than one key** (a lone gseq key is a constant folded into every clip — §4.3, not a loop).
- Result `GlobalSeqBone { bone, translation/rotation/scale: Option<GlobalSeqChannel<T>> }`
  (anim.rs:243-248) where `GlobalSeqChannel { period_ms: u32, keys: Vec<(u32, T)> }` — keys are
  absolute ms within `[0, period_ms]`.

Bake to Bevy: `build_global_bones` (`crates/benilla-assets/src/model.rs:644-690`): same per-channel
transforms as the clip (translation = `rest + wow_to_bevy(v)`; rotation = `r·q·r⁻¹`; scale =
`(s[1],s[2],s[0])`), and **times/period ms → seconds** (`ms(t) = t/1000`). Result
`GlobalBone { bone, translation/rotation/scale: Option<GlobalSeqChannel<{Vec3|Quat}>> }`
(model.rs:632-638).

Sampling on the **independent clock** — `GlobalSeqChannel::bracket` (model.rs:597-612): `t =
elapsed.rem_euclid(period)` (period `≥ 1e-3`), clamp to endpoints, otherwise find the bracketing key
pair and fraction. `sample`: `Vec3` uses `a.lerp(b, f)` (model.rs:615-620); `Quat` uses
`a.slerp(b, f)` (model.rs:622-627). The runtime samples at `(model_time mod period)` and writes ONLY
the driven components onto the joint, leaving the rest to the playing animation (model.rs:629-631,
anims.rs:106-109).

Contrast with per-sequence clips: a normal clip advances on the armed sequence's own play clock and
loops/clamps per `looping`; a global sequence free-runs on `elapsed mod period` regardless of what
animation is armed — canonical example the character **eye-blink** (an eyelid bone's scale track,
HumanMale bone 75; anim.rs:824-868).

Carried on `ModelAnimations.global_bones` (anims.rs:106-109). **A model whose sequences produce no
clips can still carry `ModelAnimations` if it has global bones** (m2.rs:552-569) — dropping them
because `clips` is empty would silently freeze a pulsing doodad.

---

## 6. `preferred_clip` / clip selection, and `seq_index` (`played_seq`)

`crates/benilla-assets/src/model/anims.rs`.

- `find(anim_id)` (anims.rs:144-146): first clip whose `anim_id` matches — the **head variation**,
  the stable identity callers loop/compare on.
- `preferred_clip(preferred: Option<u16>)` (anims.rs:163-167):
  ```rust
  preferred.and_then(|id| self.find(id)).or_else(|| self.clips.first())
  ```
  i.e. the caller's requested id if the model has it, **else the file-order-first clip** (`clips[0]`).
  This is the one selector the effect rig and its ribbon gate share so they agree on which sequence
  is playing. A thrown-weapon missile passes `InFlight`; most effects pass `None` and get `clips[0]`.
- `pick_variation(anim_id, roll)` (anims.rs:178-189): the weighted variation walk — `roll <
  frequency` picks the current, else `roll -= frequency` and advance; chain exhaustion falls back to
  the **head** (not the last). Variations are sequences sharing an `anim_id`, file order.
- `resolve(requested, catalog)` (anims.rs:202-216): the two-path AnimationData.dbc resolver (PATH 1 =
  baked `playable_animation_lookup`; PATH 2 = DBC fallback walk; empty table → identity). This is
  section 3/4 territory (net→events, cast router) — noted so those sections know it lives here.

**`seq_index` (the "played_seq" that gets captured):** each `AnimClip.seq_index` is set to
`anim.seq_index` (m2.rs:501), i.e. the sequence's **file slot** in the M2's own sequence array — NOT
the index within `ModelAnimations.clips` (which drops unbuildable/zero-duration sequences). It is the
key that indexes a batch's **per-sequence material-alpha loops** (`AlphaAnim::seq`), so the lane that
knows which clip a unit is playing can sample the batch visibility that sequence authored
(anims.rs:16-22). When you port: keep the file slot separate from the clip-list index.

**Loader-idle seed `first_seq`** (m2.rs:475-482, field at anims.rs:110-124): NOT the file-order-first
sequence. It is the index in `clips` of the sequence for **animation id 0 ("Stand") resolved through
the model's own `playableAnimationLookup`** (`idle_id = playable_animation_lookup.first().resolved_id`,
default 0), *and only when* `idle_pose_differs(anim)` is true. `None` when the resolved idle leaves
every bone at bind pose (the ~90% static-doodad content-gate skip). `idle_pose_differs`
(m2.rs:167-183): a bone track qualifies if it MOVES (len > 1) or is a **constant away from rest**
(translation `|c| > 1e-4`, rotation `|w|≠1`, or scale `≠1`) — the DuelingFlag counter-example (Spawn
band constant translation −9.124 vs bind 9 yd up).

---

## 7. `first_seq_span` — the raw first sequence's authored duration

`crates/benilla-assets/src/m2.rs:401`:
```rust
let first_seq_span = sequences.first().map(|a| a.duration).filter(|d| *d > 0.0);
```
- Source: `sequences = parse_m2_animations(&bytes)` (m2.rs:400) — the **raw** `ModelAnimation` list.
  `sequences.first()` is the **file-order-first** sequence record; its `duration` is
  `(end_ms − start_ms)/1000` (anim.rs:661), i.e. **one full authored pass in seconds**.
- **Why from the raw sequence table, not the built clip:** `ModelAnimations` **drops** sequences that
  move no bone (`build_animation_clip` returns `None`), and a model whose sequences all sit still has
  no `animations` at all — but the effect self-termination clock still needs the authored span. The
  raw table always has it (m2.rs:74-83).
- Fallback: `filter(|d| *d > 0.0)` → `None` for a zero-length first sequence; and `sequences.first()`
  is `None` (→ `first_seq_span = None`) for a model with no sequences at all.
- Semantics (m2.rs:74-83): the real client's effect-completion clock runs **one pass of the armed
  sequence regardless of bone keys or the LOOP flag** (a pure end-boundary compare; loop tested only
  after). Named approximation: the client arms `animationLookup[0]`'s sequence, not strictly
  file-order-first; they coincide on every single-sequence effect model probed. **This is the value
  section 4's cast-orchestration uses to know when an effect model has "finished".**

---

## 8. `ground_quad` detection (feeds section 9 — ground decals)

Method `RenderSubmesh::ground_quad()` (`crates/benilla-formats/src/models/types.rs:346-411`).
Constant `GROUND_FLAT_EPS = 0.01` (types.rs:337). Called at m2.rs:293 (`sub.ground_quad()`), result
stored on `ModelSubmesh.ground_quad`.

A batch qualifies as a flat ground-plane quad **only if ALL** hold (strict — 11 "OTHER-FLAT" spell
batches deliberately excluded):
1. `billboard.is_none()` (billboards never match).
2. exactly **4** each of `positions`, `joints`, `weights`, `uvs`.
3. every vertex within `|z| ≤ 0.01` (model space, WoW axes — the z≈0 plane).
4. **single bone, full weight**: `bone = joints[0][0]`, and every vertex has `joints[k][0] == bone`
   and `weights[k][0] ≥ 0.999`.
5. corners form an **axis-aligned XY rectangle**: compute XY bbox; reject degenerate (side ≤ `(extent
   *1e-3 + 1e-6)`); each vertex must land exactly on one of the 4 bilinear corners (any x/y strictly
   between the extremes → reject; two verts on one corner → reject).

Stored data `GroundQuad` (types.rs:322-333):
- `bone: u16` — the global bone-array index all four verts skin to (the quad's slide/spin/scale rides
  this joint; consumer poses corners through `joint_matrix × inverse_bindpose[bone]`, exactly the
  skinned-vertex path).
- `corners: [[f32;3];4]` in **model space (WoW axes, z=0)**, bilinear rect order:
  `(min_x,min_y), (max_x,min_y), (min_x,max_y), (max_x,max_y)`.
- `uvs: [[f32;2];4]` parallel to corners.

**Corners stay RAW WoW space** — the section-9 consumer maps them through `coords::wow_to_bevy`. The
Bevy-side `ModelSubmesh.ground_quad` is the same `GroundQuad` (raw), carried unchanged (model.rs:129-134).

---

## 9. Billboard bones (rewrite noted here, owned by section 11)

Flag → kind mapping `BillboardKind::from_bone_flags(bits: u32)`
(`crates/benilla-formats/src/models/types.rs:70-87`), priority order (0x08 wins over combined bits):
- `0x08` → **Spherical** (faces camera fully; glow cards, coronae)
- `0x10` → **LockX** (cylindrical, keeps bone X; chains, ropes)
- `0x20` → **LockY** (cylindrical)
- `0x40` → **LockZ** (cylindrical, keeps model up; questgiver `?` marker, frost-armor sheets — spins
  about vertical to face viewer)
- none set → `None`.

Also bone flag `0x04` = `ignore_parent_rotation` (bone keeps the MODEL ROOT's orientation; pivot
still rides parent's full matrix) — anim.rs:26-31, model.rs:210-212.

Where it lands:
- Per-bone (skeleton): `SkeletonBone.billboard` (anim.rs:57) → `ModelJoint.billboard`
  (model.rs:208) → `ModelSkeleton`. The **palette-level** law replaces the billboarded bone's
  rotation IN THE JOINT PALETTE so children (and geometry skinned to them) inherit the camera facing
  (model.rs:203-208). **That rewrite is section 11's job** — section 1 only records the flag on the
  joint.
- Per-batch card: `billboard_info(sub)` (model.rs:555-583) → `ModelSubmesh.billboard:
  Option<BillboardInfo>` (m2.rs:288). `BillboardInfo` (model.rs:31-48): `pivot: Vec3` (Bevy space),
  `bone: u16`, `normal: Vec3` (from first triangle's cross product, `.try_normalize()`, fallback
  `Vec3::Z`), `kind`, `scale_anim: Option<BoneScaleAnim>` (global-seq glow pulse), `seq_translations:
  Vec<(u16, BoneScaleAnim)>` (per-sequence translation loops, **keys baked to Bevy axes** via
  `wow_to_bevy` — model.rs:571-581). The mesh for a billboard batch is **centred at `wow_to_bevy(pivot)`**
  (model.rs:144-147) so the runtime rotates it about the pivot to face the camera. The per-batch split
  only covers geometry skinned to the billboard bone itself; geometry skinned to a *child* of a
  billboard bone is handled by the palette rewrite (section 11).

Raw source `Billboard` (types.rs:145-164) and `BoneScaleAnim` (types.rs:96-140, with its own linear/
step `sample(time_ms)` on the gseq clock).

---

## 10. Per-frame skinning (the matrix contract)

Documented at `crates/benilla-assets/src/model.rs:233-242`. Bevy's joint formula:

```
joint_matrix = joint_global · inverse_bindpose         (which BECOMES world_from_local)
```

Concretely for benilla's vanilla rig:
- **`inverse_bindpose[i] = translate(−pivot_i)`** (Bevy space; §3). Uploaded once per model as
  `SkinnedMeshInverseBindposes` (m2.rs:378-382), shared across instances.
- **`joint_global[i]`** = the posed joint's world matrix = the running product of ancestor local
  transforms down from the entity root, where each joint's local transform is its animated
  TRS: `translate(local_translation from clip) · rotate(clip rot) · scale(clip scale)` (or the rest
  `translate(local_translation)` when unanimated). Billboard/ignore-parent bones have their rotation
  replaced in the palette (section 11) before this product.
- **Final skinning matrix per joint** = `joint_global[i] · translate(−pivot_i)`.
- **Vertex skinning** (GPU): each vertex carries `JOINT_INDEX` (`Uint16x4` — the raw M2 global bone
  indices, used directly) and `JOINT_WEIGHT` (`Float32x4`, normalized sum 1). Skinned position =
  `Σ_k weight_k · (skinning_matrix[joint_k] · v_bindpose)`. These two attributes are the entire
  trigger for Bevy's SKINNED shader path (model.rs:176-194); a skinned-layout mesh WITHOUT a
  `SkinnedMesh` component would index the joint buffer out of bounds → garbage (model.rs:57-63).
- **Rest invariant:** at bind, `joint_global_rest[i] = translate(pivot_i)`, so the skinning matrix =
  `translate(pivot_i)·translate(−pivot_i) = I`, and the skinned mesh coincides with the static mesh
  exactly (even under a scaled entity transform — the two pivot translations cancel before scale).

For MSUI: the porter builds the same `joint_global · translate(−pivot)` product, but `pivot` must be
in MSUI's engine space (apply MSUI's `R'`), joint indices are the raw M2 bone-array indices, and the
per-joint local TRS is exactly the clip's baked (translation = rest + mapped-delta, rotation =
`R' q R'⁻¹`, scale permuted). The billboard/ignore-parent palette rewrite happens between building
`joint_global` and the multiply — see section 11.

---

## Appendix A — Raw M2 header/record offsets referenced (for section 2 cross-check)

MD20 header (magic @0):
- `+0x10` GlobalModelFlags (u32) — `global_flags` (m2.rs:596-598).
- `+0x14 / +0x18` globalSequences count/offset (u32 durations, ms).
- `+0x1c / +0x20` sequences count/offset (stride **0x44**).
- `+0x24 / +0x28` AnimationLookup (u16 slots, `0xffff` = not owned).
- `+0x2c / +0x30` PlayableAnimationLookup (stride 4: low16 resolved id, high16 dir flags; fixed 203
  rows in 1.12.1).
- `+0x34 / +0x38` bones count/offset (stride **0x6c**).
- `+0x114 / +0x118` events count/offset (stride **44**).

M2Sequence (stride 0x44): id u16 @+0x00, start u32 @+0x04, end u32 @+0x08, moveSpeed f32 @+0x0c,
flags u32 @+0x10 (**bit0 set = clamp/one-shot, clear = loop**), frequency u16 @+0x14, minReplay u32
@+0x18, maxReplay u32 @+0x1c, blendTime u32 @+0x20 (ms), CAaBox min @+0x24, max @+0x30, sphere radius
f32 @+0x3c. (anim.rs:635-660.)

M2Bone (stride 0x6c): keyBoneId i32 @+0x00, flags u32 @+0x04, parent i16 @+0x08, translation M2Track
@+0x0c, rotation M2Track @+0x28, scale M2Track @+0x44, pivot C3 @+0x60. (benilla-m2 model.rs:90-103.)

M2Track (stride 0x1c): interp u16 @0, gseq u16 @+0x02, ranges M2Array @+0x04/+0x08, timestamps
M2Array @+0x0c/+0x10, values M2Array @+0x14/+0x18. Vanilla quats uncompressed 4×f32; interp 0=step,
nonzero=linear (no hermite/bezier). (track.rs:8-14, anim.rs:339-357.)

M2Vertex (48 bytes): position C3 @0, bone_weights [u8;4] @0x0c, bone_indices [u8;4] @0x10, normal C3
@0x14, tex_coords C2 @0x20. (benilla-m2 model.rs:111-122.)

M2Attachment (stride 48): id u32 @+0, bone u32 @+4, position C3 @+8. M2EventMarker (stride 44): ident
4CC @+0, data u32 @+4, bone u32 @+8, position C3 @+12.

---

## Appendix B — Shared constants / magic numbers (name : value : source)

- `GROUND_FLAT_EPS` = `0.01` — ground-quad z-flatness tolerance (types.rs:337).
- ground-quad full-weight threshold = `0.999` (types.rs:363).
- ground-quad degeneracy eps = `extent*1e-3 + 1e-6` per axis (types.rs:376-379).
- `idle_pose_differs` EPS = `1e-4` (m2.rs:168).
- `keyframe_curve` flat-curve min span = `duration.max(1e-3)` (model.rs:459).
- `grip_clip` key span = `0.033` s (model.rs:542).
- `GlobalSeqChannel::bracket` min period = `1e-3`; min span = `1e-6` (model.rs:598,606).
- loop flag polarity: `looping = (flags & 1 == 0)` (anim.rs:665).
- gseq/no-gseq sentinel = `0xffff` (throughout).
- PlayableAnimationLookup row count in 1.12.1 = fixed **203**; AnimationData.dbc rows = **208**
  (anims.rs:198-199, 233).
- `sample_window` fraction clamped to `[0,1]` (benilla deviation from reference extrapolation)
  (key_anim.rs:167).


---

# Section 2 — DBC + binary-record data layer (benilla-formats)

Traced read-only from `C:\Users\nico\Desktop\benilla-main`. Every claim is `file:line`. All
offsets are byte offsets. "field N" for a DBC always means byte offset `N*4` — every DBC field in
these tables is a 4-byte cell (`dbc.rs`; a String field is a 4-byte offset into the trailing
string block, resolved by `str_at`, empty string ⇒ `None`).

---

## 0. DBC record mechanics (`crates/benilla-formats/src/dbc.rs`)

- `parse(bytes, schema, what)` (dbc.rs:16) applies a `Schema` whose field count must equal the WDBC
  header's `fieldCount`; `fieldCount * 4 == recordSize` (dbc.rs:5). Fields read by index.
- `u32_at(r, i)` (dbc.rs:28) — accepts `UInt32`/`Int32` (same 4 raw bytes; schema tag only picks the
  decode). `f32_at` (dbc.rs:37) `Float32`. `i32_at` (dbc.rs:47). `str_at(rs, r, i)` (dbc.rs:56)
  resolves a `StringRef` (u32 offset) through `rs.get_string`; **empty string ⇒ `None`**.
- WDBC header (from the test builder spell_visual.rs:368-379): `b"WDBC"` + `record_count`(u32) +
  `field_count`(u32) + `record_size`(u32) + `string_block_size`(u32) = 20-byte header, then records.

---

## 1. SpellVisual.dbc (`spell_visual.rs`)

Schema: `SPELL_VISUAL_FIELDS = 16` all-u32 (spell_visual.rs:72). Real 5875 = **2165 rows × 16
fields × 64 B** (spell_visual.rs:8, test asserts 2165 @ :579). Load loop spell_visual.rs:284-301.

| field | byte | meaning | how read | none rule |
|------|------|---------|----------|-----------|
| 0 | 0x00 | id | `u32_at(r,0)` | skip row if absent |
| 1 | 0x04 | **precast** kit id (`SpellVisualKit`) | `g(1)=u32_at.unwrap_or(0)` | `0` = no kit |
| 2 | 0x08 | **cast** kit id | `g(2)` | `0` = no kit |
| 3 | 0x0c | **impact** kit id | `g(3)` | `0` = no kit |
| 4 | 0x10 | **state** kit id | `g(4)` | `0` = no kit |
| 5 | 0x14 | **channel** kit id | `g(5)` | `0` = no kit |
| 6 | 0x18 | `hasMissile` | **NOT read** (spell_visual.rs:18) — spawn gate is Spell.dbc Speed alone | — |
| 7 | 0x1c | **missile model** = `SpellVisualEffectName` id | `g(7)` → `missile_model` | `<1`/`0` ⇒ ammo/weapon fallback; unresolvable ⇒ client's literal `Spells\ErrorCube.mdx` (spell_visual.rs:14) |
| 8 | 0x20 | `missilePathType` | **NOT read** (dead) (spell_visual.rs:20) | — |
| 9 | 0x24 | **missile dest-attachment ORDINAL** (index into `MISSILE_ATTACH_TABLE`) | `g(9)` → `missile_attach` | — |
| 10 | 0x28 | **missile in-flight LOOP sound** (`SoundEntries` id) | `u32_at(r,10).and_then(some_unless_none)` → `missile_sound` | **0 OR 0xFFFFFFFF ⇒ None** |
| 13 | 0x34 | caster ground-arrival kit | **NOT read** yet (spell_visual.rs:20) | — |
| 14 | 0x38 | **`$TRD` strike sound** (`SoundEntries` id) | `u32_at(r,14).and_then(some_unless_none)` → `strike_sound` | **0 OR 0xFFFFFFFF ⇒ None** |

Stage kits (fields 1-5) use the plain **`0` = none** convention (via `g()` = `unwrap_or(0)`); they
do **not** fold `0xFFFFFFFF`. The two SoundEntries FKs (fields 10, 14) and the kit's anim/sound/slots
fold both sentinels (see `some_unless_none`, §below).

`NONE_SENTINEL: u32 = u32::MAX` (0xFFFFFFFF) at spell_visual.rs:91.
`some_unless_none(v) = (v != 0 && v != NONE_SENTINEL).then_some(v)` (spell_visual.rs:269-271).

Verified chain (Fireball spell 133 → visual 67, spell_visual.rs:584-598, :56-58):
precast 30 / cast 38 / impact 286 / state 0 / channel 0; missile_model 365, missile_attach 1,
missile_sound Some(3011) (`FireMissileLoop.wav`), strike_sound None. Mining visual 93 strike_sound
1143, Herb visual 91 strike_sound 1142 (spell_visual.rs:602-611). Thrown weapon visual 98:
missile_model 0 (flies its own model), missile_sound 3318 (`WeaponLoop.wav`).

## 2. SpellVisualKit.dbc (`spell_visual.rs`)

Schema `SPELL_VISUAL_KIT_FIELDS = 35` all-u32 (spell_visual.rs:73). Real 5875 = **1772 rows × 35
fields × 140 B** (spell_visual.rs:24, test :580). Load loop spell_visual.rs:312-327.

| field | byte | meaning | read |
|------|------|---------|------|
| 0 | 0x00 | id | `u32_at(r,0)` |
| 1 | 0x04 | unpinned | — |
| 2 | 0x08 | **`AnimationData.dbc` id** | `u32_at(r,2).and_then(some_unless_none).map(as u16)` → `anim_id` |
| 3..11 | 0x0c..0x2c | **nine `SpellVisualEffectName` emitter slots** | `effect_slots[i] = u32_at(r, 3+i).and_then(some_unless_none)`, i in 0..9 |
| 12 | 0x30 | missile effect slot | **NOT read** (spell_visual.rs:31) |
| 13 | 0x34 | **`SoundEntries.dbc` id** | `u32_at(r,13).and_then(some_unless_none)` → `sound` |
| 14 | 0x38 | visual-group fallback id | **NOT read** (spell_visual.rs:32) |

anim(2), sound(13), and all nine slots(3-11) fold **both** 0 and 0xFFFFFFFF to `None`
(spell_visual.rs:43-53 empirical finding — 41 kits carry anim `0`, 875 carry `-1`, 856 real).

`VisualKit::effects()` (spell_visual.rs:144-149) yields `(KIT_SLOT_TAGS[i], effect_id)` for each
populated slot in kit-field order — **all** populated slots fire at **every** stage; the stage sets
lifetime policy only (spell_visual.rs:21-23,142-143).

## 3. SpellVisualEffectName.dbc (`spell_visual.rs`)

Schema 5 fields × 20 B; fields 1 & 2 are String, rest u32 (`effect_name_schema` spell_visual.rs:255-266).

| field | byte | meaning |
|------|------|---------|
| 0 | 0x00 | id |
| 1 | 0x04 | **name** string — debug label + the boot-time HARDCODED-effect lookup key. Rows whose name starts `"HARDCODED "` load into `hardcoded` map (name→path), matched case-insensitively (spell_visual.rs:340-351,230-236) |
| 2 | 0x08 | **effect model `.mdx` path** — the only column the kit slots consume (`effect_paths` map, spell_visual.rs:349) |
| 3,4 | 0x0c,0x10 | dead-by-absence (spell_visual.rs:39,254) |

**`.mdx`/`.mdl` → `.m2` extension law:** NOT applied in this data layer. The table stores the raw
authored path (e.g. `Spells\Fireball_Missile_Low.mdx` spell_visual.rs:634, `Particles\LootFX.mdl`
:706). The **consumer** rewrites `.mdl`/`.mdx` → `.m2` at load time — proven by test :706-711
(`loot_art_path()` = `Particles\LootFX.mdl` but the model that must ship is `Particles\LootFX.m2`).
So the port applies the extension swap at the render/asset-load boundary, not here.

## 4. The attach arrays (values verbatim)

- **KIT_SLOT_TAGS**: `[u16; 9] = [0x14, 0x22, 0x13, 0x15, 0x16, 0x11, 0x17, 0x18, 0x19]`
  (spell_visual.rs:79) — indexed by kit slot 0-8 (fields 3-11). Each a **direct M2 AttachmentID**:
  `[0]=Head 0x14, [1]=Chest 0x22, [2]=Base 0x13, [3]=LeftHand 0x15, [4]=RightHand 0x16,
  [5]=Breath 0x11, [6]=Special1 0x17, [7]=Special2 0x18, [8]=Special3 0x19`.
- **MISSILE_ATTACH_TABLE**: `[u16; 11] = [0x14, 0x22, 0x13, 0x15, 0x16, 0x11, 0x17, 0x18, 0x19,
  0xf, 0x10]` (spell_visual.rs:85) — SpellVisual field 9 indexes here for the M2 attach tag the
  missile homes to (= KIT_SLOT_TAGS + `0xf`,`0x10` at ordinals 9,10). Fireball ordinal 1 → 0x22 chest.
- **ATTACH_FALLBACKS**: `[u16; 2] = [0xf, 0x13]` — lives in `crates/benilla/src/entities/spell_fx.rs:65`
  (NOT in benilla-formats). Client attach cascade: requested tag, then 0xf, then 0x13, then the
  unit's base position (spell_fx.rs:62-65,722-726).
- **HARDCODED_FX_ATTACH**: `u16 = 0x13` — `crates/benilla/src/creature_anim/spell_visual.rs:38`.
  The M2 attach point the engine-spawned effects (loot sparkle, level-up ding) hang from; a model
  lacking 0x13 lands on the unit base (the reference's root fallback).

## 5. Spell.dbc columns (`spells/display.rs`, `spells/mod.rs`)

- **SpellVisual id = column 115** (`COL_VISUAL_ID = 115`, spells/mod.rs:272; read u32 `visual =
  u32_at(r,115).unwrap_or(0)` mod.rs:541). `0` ⇒ no visual chain (silent cast) — display.rs:19.
- **Speed = column 37** (`COL_SPEED = 37`, spells/mod.rs:229; read f32 `speed =
  f32_at(r,37).unwrap_or(0.0)` mod.rs:542). Units = world units/sec (display.rs:23).
- **Missile-spawn gate = `Speed > 0`** (decision 0099 `Speed==0` gate — no missile phase;
  display.rs:23-24, spells/mod.rs:26-27). `hasMissile` (SpellVisual field 6) is never consulted.
- Both column pins are uniqueness-verified across all 173 columns (spells/mod.rs:14-27).

## 6. Particle emitter record (`particles.rs`)

Header: `count @ MD20+0x13c` (`HDR_COUNT`), `ptr @ MD20+0x140` (`HDR_PTR`), **stride `STRIDE =
0x1f8`** (particles.rs:560-562). Guard: `count==0 || count>256 || base+count*0x1f8 > len` ⇒ empty
(particles.rs:678). Textures resolved via benilla-m2's textures table (particles.rs:655-671).

All offsets from record start `e = base + i*0x1f8`. Parse loop particles.rs:731-843.

| off | field | type / decode |
|-----|-------|---------------|
| +0x04 | flags | u32 (particles.rs:800) — see flag table below |
| +0x08 | position | C3Vector (3×f32), model-local |
| +0x14 | bone | u16 |
| +0x16 | texture | u16 index → M2 textures table (particles.rs:733) |
| +0x18/+0x1c | geometry_model | `M2Array<char>` (count@+0x18, ofs@+0x1c); needs `n>=2`; model-particle geometry (particles.rs:749,738-748) |
| +0x20/+0x24 | recursion_model | `M2Array<char>` (count@+0x20, ofs@+0x24); child emitters (particles.rs:750) |
| +0x28 | blendingType | u8 → `blend_of`: **3\|4 → Add, 2\|5\|6 → Alpha, else Opaque** (particles.rs:634-640) |
| +0x2a | emitterType | u16 → `shape_of`: **2 → Sphere, 3 → Spline, else Plane** (particles.rs:642-648) |
| +0x2c | particleType / head_tail | **read as one byte** `bytes[e+0x2c]` (0=head, 1=tail, 2=both) — NB module doc calls it u16 @+0x2c but code reads a u8 (particles.rs:811) |
| +0x30 | tileRows | u16 |
| +0x32 | tileCols | u16 — both must be **non-zero powers of two**, else fall back to `(1,1)` (particles.rs:766-769) |
| +0x34 | emissionSpeed | M2Track `value[0]` (`track_first`, default 0.0) |
| +0x50 | speedVariation | value[0], default 0.0 |
| +0x6c | verticalRange | value[0], default 0.0 (spline: tangent-spin range ψ) |
| +0x88 | horizontalRange | value[0], default 0.0 (spline: scatter) |
| +0xa4 | gravity | value[0], default 0.0 |
| +0xc0 | lifespan | value[0], **default 1.0** |
| +0xdc | **emissionRate** | full keyed M2Track (elem 4, f32) → `EmitTiming` (particles.rs:819) |
| +0xf8 | areaLength | value[0], default 0.0 (spline: tMin) |
| +0x114 | areaWidth | value[0], default 0.0 (spline: tMax) |
| +0x130 | zSource | value[0], default 0.0 |
| +0x14c | over_life.mid | f32 (normalized age split) |
| +0x150/+0x154/+0x158 | color keys k0/k1/k2 | **packed BGRA u32 → linear RGBA**: R=(v>>16)&0xff, G=(v>>8)&0xff, B=v&0xff, A=(v>>24)&0xff, each /255.0 (particles.rs:720-728). A = over-life opacity |
| +0x15c/+0x160/+0x164 | scale keys k0/k1/k2 | f32 yards |
| +0x168,+0x16a | head_cells[0] (A) begin,end | u16,u16 → `CellRamp::new` |
| +0x16c | repeat[0] | u16 → f32 (flipbook repeat, segment A) |
| +0x16e,+0x170 | head_cells[1] (B) begin,end | u16,u16 |
| +0x172 | repeat[1] | u16 → f32 |
| +0x174,+0x176 | tail_cells[0] (A) | u16,u16 |
| +0x178,+0x17a | tail_cells[1] (B) | u16,u16 |
| +0x17c | tailTime | f32 SECONDS (streak length = \|vel\|·tailTime) |
| +0x180 | twinkleSpeed | f32 |
| +0x184 | twinklePercent | f32 (draw-gate fraction) |
| +0x188 | twinkleScale min | f32 → `twinkle_min` |
| +0x18c | twinkleScale max | f32 → `twinkle_max` |
| +0x190 | inheritScale | f32 → runtime +0x1c4 |
| +0x194 | drag | f32 plain scalar (NOT a track); `vel -= min(dt·drag,1)·vel` per frame |
| +0x198 | spin | f32 rad/s quad rotation; negative = alternating-direction randomizer |
| +0x19c | angular_velocity_min | C3Vector (tumble min, rad/s) |
| +0x1a8 | angular_velocity_max | C3Vector (tumble max) |
| +0x1c4 | follow_speed1 | f32 |
| +0x1c8 | follow_scale1 | f32 |
| +0x1cc | follow_speed2 | f32 |
| +0x1d0 | follow_scale2 | f32 (the follow-delta line) |
| +0x1d4/+0x1d8 | spline points | `M2Array` (count@+0x1d4, ofs@+0x1d8), N = `3*(count/3)+1` C3Vectors; only for shape==Spline (particles.rs:753-763) |
| +0x1dc | **enabled** | full keyed `M2Track<u8>` step (elem 1, `f32::from(b[o]!=0)`) → `EmitTiming` gate (particles.rs:820). Closes the 0x1f8 record |

**Only `emissionRate` (+0xdc) and `enabled` (+0x1dc) are keyed/baked per sequence** (via
`EmitTiming::bake`, particles.rs:818-824). The other nine emission tracks are `value[0]`-baked
(constant across the rendered corpus).

### Particle flag bits (file flag → runtime remap), particles.rs:438-557

| file bit | runtime | name | meaning |
|---------|---------|------|---------|
| 0x10 | 0x100 | `model_space` | cloud rides emitter's live bone matrix each frame; clear = rotation baked at birth. Either way re-anchored to emitter pos every frame — no world-frozen trail (particles.rs:446-448) |
| 0x20 | 0x200 | `scale_size_by_instance` | particle size × emitter transform scale magnitude (particles.rs:484-486) |
| 0x40 | 0x400 | `inherits_emitter_motion` | births inherit emitter's ~30 Hz motion × inherit_scale (particles.rs:478-480) |
| 0x80 | 0x800 | `kill_outbound` | **Sphere only** (type==2): kill particle the frame `dot(stepVelocity, updatedPos) > 0` (particles.rs:499-501) |
| 0x100 | 0x4000 | `sphere_up` | **Sphere only**: birth velocity straight +Z, not radial (particles.rs:489-491) |
| 0x200 | 0x8000 | `tumble_random_sign` | each tumble axis's angular vel sign-flipped w/ prob ½ (particles.rs:510-512) |
| 0x400 | 0x10000 | `tail_clamps_to_age` | tail streak tail_time clamped to age (grows from 0 at birth) (particles.rs:504-506) |
| 0x1000 | 0x2000 | `xy_quad` | head quad does NOT billboard — flat in emitter model-space XY plane (particles.rs:546-548) |
| 0x2000 | 0x20000 | `ground_snap` | at-spawn: probe 20 yd down; on hit up-coord = surfaceZ + birth over-life SIZE (particles.rs:523-525) |
| 0x4000 | 0x40000 | `follow_emitter` | live particles keep follow_line's fraction (≤1) of emitter world motion (particles.rs:457-459) |
| 0x8000 | (bit 15) | `burst` | one-shot burst: single `ftol(rate·density·LOD)` spawn on the rising `(enabled && rate>0)` edge; re-arms only when gate falls (particles.rs:526-537) |

### CellRamp (flipbook), particles.rs:97-127
`new(begin,end)`: if `end>=begin`: `base=begin, span=end-begin+1` (forward); else `base=begin+1,
span=end-begin-1` (**NEGATIVE — plays backwards**, legal/shipped). `sample(t) = floor(base +
span·t) & 0xFF` (mod-256 wrap, not a clamp).

### OverLife::sample(u), particles.rs:175-217
`u=age/lifespan` clamped [0,1]; `mid` clamped [1e-3,1]. If `u<=mid`: `(k0,k1,t,seg)=(0,1,u/mid,0)`
else `(1,2,(u-mid)/(1-mid),1)`. **Endpoint inset** applied once: `t = clamp(t,0,1)·0.99 + 0.005`.
`color[c] = color[k0][c] + (color[k1][c]-color[k0][c])·t`; `size` same on `scale`. Cell time
`ct = repeat[seg]!=1.0 ? (t·repeat[seg]).fract() : t`; `head_cell = head_cells[seg].sample(ct)`,
`tail_cell = tail_cells[seg].sample(ct)`. Color/size do NOT cycle with repeat; only cells do.

## 7. Ribbon emitter record (`ribbons.rs`)

Header: `count @ MD20+0x134`, `ptr @ MD20+0x138`, **stride `0xdc`** (ribbons.rs:40-42). Render-flags
(material) table `@ MD20+0x84` (count) / +0x88 (offset), stride 4 = `{flags u16, blend u16}`
(ribbons.rs:44,209-210). Look tracks rebased to seq-0 band (`seq0_band`).

All offsets from record start `e = base + i*0xdc`. Parse loop ribbons.rs:215-267.

| off | field | decode |
|-----|-------|--------|
| +0x04 | boneIndex | u16 |
| +0x08 | position | C3Vector (3×f32), bone-local |
| +0x14/+0x18 | textureIndices | `M2Array<u16>` (count@+0x14, ofs@+0x18) → `textures[indices[0]]` (ribbons.rs:221-227) |
| +0x1c/+0x20 | materialIndices | `M2Array<u16>` (count@+0x1c, ofs@+0x20) → render-flags[mat[0]].blend = `u16 @ rf_base + m*4 + 2`: **3\|4→Add, 2→Alpha, else Opaque; unresolved→Add** (ribbons.rs:229-241) |
| +0x24 | colorTrack | `M2Track<C3Vector>` (elem 12), default `[1,1,1]`, keyed RGB 0..1 |
| +0x40 | alphaTrack | `M2Track<fixed16>` (elem 2), default 1.0, **value = `u16/32767.0`** (ribbons.rs:254-256) |
| +0x5c | heightAboveTrack | `M2Track<f32>` (elem 4), default 0.0 (yards, above bone path) |
| +0x78 | heightBelowTrack | `M2Track<f32>` (elem 4), default 0.0 |
| +0x94 | edgesPerSecond | f32 |
| +0x98 | edgeLifetime | f32, **clamped `≥ 0.25`** via `.max(0.25)` (ribbons.rs:260) |
| +0x9c | gravity | f32 (per-frame `2·g·dt` sag) |
| +0xa0 | textureRows | u16 `.max(1)` |
| +0xa2 | textureCols | u16 `.max(1)` |
| +0xa4 | texSlotTrack | `M2Track<u16>` value[0]-baked (elem 2, default 0) → `tex_slot` (ribbons.rs:264) |
| +0xc0 | visibilityTrack | `M2Track<u8>` → per-sequence `visible_in_anim` map (ribbons.rs:265, `visibility_by_anim`) |

`visibility_by_anim` (ribbons.rs:132-181): returns `None` if track OOR, if `gseq (u16@+0x02) !=
0xffff` (global-seq free clock), or if keyless. Keys `(u32 ts @ tofs+i*4, byte @ vofs+i != 0)`,
`n=min(tn,vn)` where tn@+0x0c/tofs@+0x10, vn@+0x14/vofs@+0x18. Sequences: count@0x1c, ofs@0x20,
stride 0x44, anim id@+0, band start@+4. Nearest-previous at band start; default ON; `Some(map)` only
if some sequence resolves OFF. Thrown dagger: Stand(0)=false, InFlight(144)=true, Impact(191)=false
(ribbons.rs:343-345).

## 8. SoundEntries.dbc (`sound_entries.rs`)

**4623 rows × 29 fields × 116 B** (sound_entries.rs:7, test :187). Schema sound_entries.rs:99-117.

| field | byte | meaning |
|------|------|---------|
| 0 | 0x00 | ID |
| 1 | 0x04 | SoundType (1 spells, 2 UI, 3 footsteps, …) |
| 2 | 0x08 | Name (str) — the `PlaySoundByName` key |
| 3..12 | 0x0c..0x30 | File[10] (str) |
| 13..22 | 0x34..0x58 | Freq[10] (u32 weight, one per file) |
| 23 | 0x5c | DirectoryBase (str) |
| 24 | 0x60 | Volume (f32, default 1.0) |
| 25 | 0x64 | **Flags** (u32) |
| 26 | 0x68 | MinDistance (f32, default 0.0) |
| 27 | 0x6c | DistanceCutoff (f32, default 0.0; `0` = non-positional) |
| 28 | 0x70 | EAXDef (u32) |

**Sound id → files** (sound_entries.rs:131-143): for `i in 0..10`, if `File[i]` non-empty, path =
`DirectoryBase.is_empty() ? File[i] : "{dir}\\{File[i]}"`, weight = `Freq[i]`. Only non-empty slots
kept as `(path, weight)`. By-name lookup is case-insensitive (`by_name` keyed on lowercased Name,
sound_entries.rs:144-146,74-78).

**Flags** (`sound_kit_flags`, sound_entries.rs:31-36) — copied raw into runtime kit flag word:
- `NO_DUPLICATES = 0x20`
- **`LOOPING = 0x200`** ← the looping flag value
- `VARY_PITCH = 0x400`
- `VARY_VOLUME = 0x800` (dormant — no 5875 kit sets it)

Spot row (id 3): type 1 "Invisibility Impact", `Sound\Spells\Dispel_Low_Base.wav`×weight 1, vol 1.0,
flags 0, min 8, cutoff 45, EAX 2 (sound_entries.rs:191-202).

## 9. Material / color / alpha / texture-anim track layouts & sample function

### M2Track binary layout (`benilla-m2/src/track.rs`, stride 0x1c = 28 B)
- `interp` u16 @ **0x00** — **0 = step (nearest-previous), nonzero = linear** (track.rs:19-21)
- `gseq` u16 @ **0x02** — `0xffff` = sequence-timeline; else index into global_sequences (track.rs:22)
- interpolation_ranges `M2Array` @ **0x04**(count)/**0x08**(offset), 8-byte entries `(lo u32, hi u32)`
  — per-sequence key-index window; empty ⇒ `[track+4]==0` "search whole key list" fallback (track.rs:23-33,92-100)
- timestamps `M2Array` @ **0x0c**(count)/**0x10**(offset), u32 ms
- values `M2Array` @ **0x14**(count)/**0x18**(offset)
- key count = `min(timestamps, values)` (track.rs:84)

### Fixed-point / value conversions
- **fix16** scalar (color-alpha, transparency-weight, ribbon alpha): `value = u16 / 32767.0`
  (`track_fix16` track.rs:115-119; ribbons.rs:255)
- vec3 track: 3×f32 (`track_vec3_timed`); quat track: 4×f32 uncompressed in v256 (`track_quat`)
- particle color key: packed **BGRA u32** → each byte /255 (particles.rs:720-728)
- WMO MOLT color: BGRA bytes /255 (records.rs:341-345)

### Material animation wiring (`benilla-m2/src/model.rs`)
- `color_alpha_tracks` — one `M2ScalarTrack` per M2Color record (header 0x54); selected by
  `texUnit.colorIndex` (direct) (model.rs:224-228)
- `color_rgb_tracks` — one `M2Vec3Track` per M2Color record (record's +0x00 track); same colorIndex
  (model.rs:229-236)
- `transparency_tracks` — one per M2TextureWeight (header 0x64); **two-hop** via
  `texUnit.weight_combo_index → transparency_lookup (header 0xa4, u16) → track` (model.rs:237-242)
- `texture_transforms` `{translation, rotation, scaling}` — via `texUnit texture_transform_combo_index
  (+0x16) → texture_transform_lookup (header 0xac, u16; 0xffff = none) → record` (model.rs:243-249)
- `global_sequences` — header 0x14/0x18, ms durations (the free clocks `gseq` tracks wrap on)
- **Combine law** (mat_anim.rs:6-13): `A = instanceAlpha × colors[colorIndex].alpha ×
  transparency[transLookup[idx]].weight`, both evaluated per frame; **`A ≤ 0` culls the batch
  before the blend mode is read**; an Opaque batch with 0<A<1 still draws opaque.

### Sample function (two faithful implementations)

**A. `sample_window` — the reference's raw window read** (`models/key_anim.rs:139-169`): given
`keys`, `step`, window `(lo,hi)`, absolute `t_ms`:
1. clamp `lo,hi` to last index; **if `lo >= hi` return `keys[lo]`** (collapsed window — the
   `{lo,lo,0}` degenerate, the reason a band that keys nothing still has a value);
2. `k0` = last key in `[lo,hi]` with `ts <= t_ms`, floored to `lo`;
3. if `step` or `k0+1 > last`: return `keys[k0]`;
4. else lerp with fraction `(t_ms-ta)/(tb-ta)` **clamped [0,1]** (one named deviation — the reference
   extrapolates; benilla holds at the bracket to avoid a data-quirk negative culling a batch).

**B. `bake_track` — per-sequence bake to `KeyAnim`** (`key_anim.rs:185-263`):
- keyless ⇒ None; all-equal ⇒ constant (or None via `drop_constant`);
- `gseq != 0xffff`: global-seq clock, `period = global_sequences[gseq]` (or last-key time), keys→sec;
- else sequence-timeline: `(lo,hi) = ranges[index]` (or whole list), `head = sample_window(start)`,
  bake band keys + both endpoints, `period = (end-start)/1000`. A band that never moves ⇒ constant.

`KeyAnim::sample_clocked(elapsed, wrap, empty)` (key_anim.rs:88-122): `t = wrap ?
elapsed.rem_euclid(period) : elapsed.clamp(0,period)`; `k0` = last key `≤ t`; step or past-last ⇒
hold; else lerp. **`wrap` = playing sequence loops; `!wrap` = clamps (parks at band end, never aliases
`t==period` back to key 0).** Channel identities: alpha→`1.0`, RGB→white, UV→`[0,0]`.

**C. `ValueTrack::sampled_ms(ms)` — the particle/ribbon lane sampler** (`value_track.rs:109-131`):
`interp==0` ⇒ `step_ms` (nearest-previous, hold-last, first-key before first); else linear, **held
past last, backward-extrapolated (negative fraction) below the first key — no clamp**; consumer floors
rate at 0. `rebase_keys_to_band` (value_track.rs:168) rebases global-timeline keys onto the seq band
(pre-band keys collapse to `t=0` last-wins; post-`end` clamps to `end-start`). `seq0_band`
(value_track.rs:193): sequences @0x1c/0x20 stride 0x44, start@+4/end@+8.

### EmitTiming (`emit_timing.rs`) — the runtime emission gate
Bakes `rate (+0xdc)` and `enabled (+0x1dc)` one loop **per file sequence slot** via `bake_track`
(emit_timing.rs:36-56). `emitting(seq, elapsed)`: gate `sample_clocked > 0.5`; **no gate track ⇒ ON
(loader default `block+0x14c = 1`)** (emit_timing.rs:69-76). `rate(seq, elapsed)`: no track ⇒ 0,
**floored at 0** (a tail may go negative) (emit_timing.rs:80-88). Slot resolution: out-of-range/None
⇒ slot 0 (the doodad one-time-arm lane) (emit_timing.rs:60-65). Looping slot wraps its band; clamped
slot parks at the tail.

## 10. AnimationData.dbc (`anim_data.rs`)

**208 rows × 7 fields × 28 B** (anim_data.rs:5, test :149). Schema field 1 = String, rest u32
(anim_data.rs:96-107). Fields: `ID(0), Name(str,1), WeaponFlags(2), BodyFlags(3), _col4(4),
_col5(5), Fallback(6)`. Loaded: `weapon_flags = u32_at(r,2)`, `fallback = u32_at(r,6) as u16`
(anim_data.rs:120-124); name from field 1 (anim_data.rs:126-128).

- WeaponFlags (col 2) 5875 value set `{0,4,0x10,0x14,0x20}` (anim_data.rs:17): `4` force-stow,
  `0x10` force-stow, `0x20` force-draw-melee (sheath reconcile).
- Fallback (col 6): substitute anim id when a model lacks the clip; `0` = Stand.
  `fallback(id)` returns `None` when the value is `0` or `== id` (anim_data.rs:74-79).
- This is a `SpellVisualKit` field-2 target: kit anim id → `AnimationData` id → PlayAnimation route.

---

## Notes / underspecified

1. **`.mdl`/`.mdx` → `.m2` rewrite** is NOT in this layer — `SpellVisualEffectName` field 2 and the
   HARDCODED map store the raw authored extension; the render/asset boundary swaps to `.m2`
   (proven by spell_visual.rs:706-711). The C# port must apply the rewrite at model load.
2. **`ATTACH_FALLBACKS` `[0xf,0x13]` and `HARDCODED_FX_ATTACH 0x13`** live in the `benilla` crate
   (`entities/spell_fx.rs:65`, `creature_anim/spell_visual.rs:38`), not benilla-formats — they are
   the section-6 attach layer, included here because the prompt asked for the arrays.
3. **particleType/head_tail at +0x2c**: module doc labels it `u16` but the loader reads a single
   **u8** (`bytes[e+0x2c]`, particles.rs:811). Treat as a byte.
4. Only 2 of the particle emitter's 11 emission tracks are keyed/baked (emissionRate +0xdc,
   enabled +0x1dc); the other nine are `value[0]` snapshots — a named residual if a model ever
   authors a keyed one (particles.rs:361-364).
5. SpellVisual fields **6 (hasMissile), 8 (missilePathType), 13 (ground-arrival kit)** and
   SpellVisualKit fields **12 (missile effect slot), 14 (visual-group fallback)** are parsed-past
   but never read; the port may skip them until a consumer appears.
6. All real-data tests skip without `WoW/Data` at the repo root (gitignored) — row counts (2165 /
   1772 / 4623 / 208) are the byte-verified header shapes to assert against.


---

# Section 3 — Network packets → internal spell/aura events (benilla → C# port)

Scope: how the wire's spell/aura opcodes are **parsed** (byte layout), turned into flat
`SessionEvent`s, and then dispatched into internal ECS events/components (`CastEvent`,
`SpellGoTargets`, `Casting`, cast-bar edges) + how channel/aura **state** is polled off descriptor
fields. Read-only trace; every claim carries file:line.

Three-layer pipeline for every packet:

1. `parse_server(opcode, body)` — `crates/benilla-protocol/src/messages/parse.rs:155` — byte-decode
   → `ServerPacket` (delegating to `messages/spells.rs` readers).
2. `decode(ServerPacket)` — `crates/benilla-protocol/src/events/decode.rs:16` — flatten → one or
   more `SessionEvent` (defined `crates/benilla-protocol/src/events.rs`).
3. `apply_net_updates` dispatch — `crates/benilla/src/net/apply.rs` — one match arm per event,
   arm bodies in `crates/benilla/src/net/apply/spells.rs`, resolving guid→Entity via `GuidIndex`
   and emitting internal messages/components.

Opcode numeric values (`crates/benilla-protocol/src/messages/opcode.rs`):
`SMSG_CAST_RESULT=0x0130`, `SMSG_SPELL_START=0x0131`, `SMSG_SPELL_GO=0x0132`,
`SMSG_SPELL_COOLDOWN=0x0134`, `SMSG_UPDATE_AURA_DURATION=0x0137`, `MSG_CHANNEL_START=0x0139`,
`MSG_CHANNEL_UPDATE=0x013A`, `SMSG_SPELL_DELAYED=0x01E2`, `SMSG_PLAY_SPELL_VISUAL=0x01F3`,
`SMSG_CANCEL_AUTO_REPEAT=0x029C`, `SMSG_SPELL_FAILED_OTHER=0x02A6`.
Note benilla names 0x0130 `SMSG_CAST_RESULT` (this is vanilla `SMSG_CAST_FAILED`; it is the
**self-cast verdict** — `SMSG_SPELL_FAILURE` is never sent, decision 0099).

Wire primitive readers used throughout: `read_packed_guid` (WoW packed guid: 1 mask byte + present
non-zero bytes), `read_u64_le` (RAW 8-byte guid), `read_u32_le`, `read_u16_le`, `read_u8`,
`read_f32_le`, `Vector3d::read` (3×f32), `read_cstring`. **Packed vs raw is per-field and
load-bearing** (see below).

---

## 1. SMSG_SPELL_START (0x0131)

Parser `read_spell_start` — `crates/benilla-protocol/src/messages/spells.rs:160-181`. Struct
`SpellStart` (`spells.rs:149-158`). Field order/type:

```
item_or_caster : packed guid   (read_packed_guid)   — cast item's guid if item cast, else caster's own
caster         : packed guid   (read_packed_guid)   — always the casting Unit (m_casterUnit)
spell_id       : u32 LE
cast_flags     : u16 LE        — always 0x2 (CAST_FLAG_UNKNOWN2); +0x20 (CAST_FLAG_AMMO) for ranged
cast_time_ms   : u32 LE        — remaining cast time (m_timer); 0 for an instant
targets        : SpellCastTargets (see §below)
ammo           : present iff cast_flags & 0x0020: {u32 displayId, u32 inventoryType} → keep displayId only
```

`SpellCastTargets` decode — `read_spell_cast_targets` `spells.rs:87-127`. **Must mirror vmangos
WRITE-side branch order, not the symmetric read side.** Order:
- `mask : u16 LE` (`spells.rs:88`).
- If `mask & (UNIT 0x2 | GAMEOBJECT 0x800 | CORPSE_ENEMY 0x200 | CORPSE_ALLY 0x8000)` → read **one**
  `read_packed_guid`; assign to `unit_target` if `UNIT` bit set, else `go_target` if `GAMEOBJECT`
  bit set (priority UNIT > GAMEOBJECT) (`spells.rs:91-106`). Corpse guids are consumed but dropped.
- If `mask & (ITEM 0x10 | TRADE_ITEM 0x1000)` → one packed guid, dropped (`spells.rs:107-109`).
- If `mask & SOURCE_LOCATION 0x20` → `Vector3d`, dropped (`spells.rs:110-112`).
- If `mask & DEST_LOCATION 0x40` → `Vector3d` → `dest` (`spells.rs:113-117`).
- If `mask & STRING 0x2000` → cstring, dropped (`spells.rs:118-120`).
Target-flag consts at `spells.rs:59-67`. `TARGET_FLAG_LOCKED 0x4000` reads no bytes (`spells.rs:328`).

Flatten — `decode.rs:139-146`: `SessionEvent::SpellStart { caster, spell_id, cast_flags,
cast_time_ms, target: targets.unit_target, ammo_display_id }`. (item_or_caster/go_target/dest dropped
for START.) Event def `events.rs:485-495`.

Dispatch — `apply.rs:1037-1059` → `spell_start(...)` `spells.rs:159-258`. Effects:
- **Nocked-ammo** (`spells.rs:188-206`): if the spell's catalog row `ranged_attack()`, on the
  resolved caster entity insert `NockedAmmo{display_id}` when `ammo_display_id` present & != 0, else
  remove `NockedAmmo`. Runs for ANY caster (self or observed), before the self/other split.
- **Cast bar** (`spells.rs:229-240`): only if `self_guid == caster` AND `cast_time_ms > 0` AND NOT a
  known `ranged_slot()` spell → push `CastBarEdge::Start{spell_id, cast_time_ms}`. Also
  `pending.refine(cast_time_ms)` (in-flight guard) for self timed casts (ranged too).
- **Casting component** (`spells.rs:241-247`): if `cast_time_ms > 0`, insert `Casting{spell_id,
  until: now + cast_time_ms}` on the caster entity. Instants get **no** component.
- **CastEvent** (`spells.rs:251-256`): always (instants included) write `CastEvent{entity, spell_id,
  kind: CastEventKind::Start, seq}`.

---

## 2. SMSG_SPELL_GO (0x0132)

Parser `read_spell_go` — `spells.rs:209-247`. Struct `SpellGo` (`spells.rs:195-207`):

```
item_or_caster : packed guid
caster         : packed guid
spell_id       : u32 LE
cast_flags     : u16 LE        — always 0x100 (CAST_FLAG_UNKNOWN9); +0x20 (AMMO) for ranged
hit_count      : u8
hits[hit_count]: RAW u64 LE each  (read_u64_le — the hit list is NEVER packed)   spells.rs:215-219
miss_count     : u8
misses[]       : per miss: { RAW u64 LE guid, u8 SpellMissInfo reason,
                             u8 reflectResult IFF reason==11 (SPELL_MISS_REFLECT) — read & dropped } spells.rs:221-229
targets        : SpellCastTargets (same reader as START)
ammo           : {u32 displayId, u32 inventoryType} iff cast_flags & 0x20 → keep displayId
```

`SPELL_MISS_REFLECT = 11` const `spells.rs:185`. Nothing about missile travel is on this packet —
sent at launch; server schedules impact off `Spell.dbc` Speed (`spells.rs:187-194`).

Flatten — `decode.rs:147-157`: `SessionEvent::SpellGo { caster, spell_id, cast_flags, hits, misses,
target: targets.unit_target, go_target: targets.go_target, ammo_display_id,
item_caster: (item_or_caster != caster).then_some(item_or_caster) }`. Event def `events.rs:499-515`.

Dispatch — `apply.rs:1060-1099` → `spell_go(...)` `spells.rs:266-455`. Effects:
- **GO lid** (`spells.rs:306-308`): if `go_target` present → `GoLidOpen{go_guid, spell_id}` (chest/door).
- **Self cast-bar/cooldown** (`spells.rs:312-392`, gated `self_guid==caster`): push
  `CastBarEdge::Stop` only if the showing bar's `Casting.spell_id == spell_id` (proc-GO guard);
  `pending.clear_if`/`queued_melee.clear_if`; start local cooldown at GO — item leg via
  `item_caster`→template `use_spell` else spell-keyed `start_spell`; ranged-slot cast folds live
  `UNIT_FIELD_RANGEDATTACKTIME` into category recovery (`spells.rs:351-357`).
- **Miss floating words** (`spells.rs:398-418`): only if caster is classified as our source; per
  miss, anchor = caster if `code==11` (REFLECT re-anchor) else the missed guid; skip if anchor is
  self; `combat_text::miss_word(code)` → `CombatTextSpawn`.
- **Casting reap + CastEvent** (`spells.rs:422-431`): remove `Casting` iff `Casting.spell_id ==
  spell_id`; always write `CastEvent{kind: Go, seq}`.
- **SpellGoTargets** (`spells.rs:436-453`): resolve hit guids and `(miss guid, code)` to entities
  (targets not streamed drop out); if any → write `SpellGoTargets{caster, spell_id, hits:Vec<Entity>,
  misses:Vec<(Entity,u8)>, ammo_display_id, seq}` (feeds section 10 missiles / instant impact).

---

## 3. SMSG_PLAY_SPELL_VISUAL (0x01F3) — out-of-cast kit push

Parser `read_play_spell_visual` — `spells.rs:300-304`:
```
unit   : RAW u64 LE   (read_u64_le, unpacked)
kit_id : u32 LE       (SpellVisualKit.dbc id)
```
Flatten — `decode.rs:195-197`: `SessionEvent::PlaySpellVisual { unit, kit_id }`
(`events.rs:553-556`).
Dispatch — `apply.rs:1178-1188` (inline, not in spells.rs): resolve `unit`→entity via `index`; write
`creature_anim::KitPush { entity, kit_id, seq }`. Consumer is `creature_anim::spell_visual` (eat/drink
kits, mid-channel swaps). No `Casting`/`CastEvent` touched.

---

## 4. Cast interrupt / cancel / fail

Three distinct wires:

**SMSG_CAST_RESULT (0x0130)** — our own cast verdict. Parser `read_cast_result` `spells.rs:42-53`:
```
spell_id : u32 LE
status   : u8    (0 = OKAY → CastOutcome::Ok; 2 = FAIL → append u8 reason → CastOutcome::Failed{reason})
```
Reason-specific extra arg words are left unread (`spells.rs:39-41`). Flatten `decode.rs:70-77`:
`SessionEvent::CastResult{ spell_id, success: outcome==Ok, reason: Some(u8) on fail }`
(`events.rs:404-410`). Dispatch `apply.rs:657` → `cast_result(...)` `spells.rs:77-154` (only acts on
failure): `cooldowns.clear_gcd`; if `auto_repeat==Some(spell_id) && reason != Some(0x17)` →
`cancel_auto_repeat_local` (deselect during Auto Shot — 0x17 is the one skip); `pending.clear_if`,
`queued_melee.clear_if`; push `(spell_id, reason)` to `CastErrors`; if the failure is our showing
cast (`Casting.spell_id==spell_id`) push `CastBarEdge::Failed` and remove `Casting`; always write
`CastEvent{kind: Fail, seq}` for self.

**SMSG_SPELL_FAILED_OTHER (0x02A6)** — an observed cast interrupted/cancelled. Parser
`read_spell_failed_other` `spells.rs:253-257`:
```
caster   : RAW u64 LE   (unpacked)
spell_id : u32 LE
```
Flatten `decode.rs:161-163` → `SessionEvent::SpellFailedOther{caster, spell_id}`
(`events.rs:516-518`). Dispatch `apply.rs:1100` → `spell_failed_other(...)` `spells.rs:460-504`: if
self & showing → `CastBarEdge::Interrupted`, `pending.clear_if`, `queued_melee.clear_if`; remove
`Casting` iff `spell_id` matches; always write `CastEvent{kind: Fail, seq}`. This is the edge that
stops an observed unit's precast hold.

**SMSG_SPELL_DELAYED (0x01E2)** — pushback (NOT a cancel; extends the cast). Parser
`read_spell_delayed` `spells.rs:263-267`: `caster: RAW u64 LE`, `delay_ms: u32 LE`. Flatten
`decode.rs:158-160` → `SessionEvent::SpellDelayed{caster, delay_ms}`. Dispatch `apply.rs:1113` →
`spell_delayed` `spells.rs:512-528`: self-only → `CastBarEdge::Delayed{delay_ms}` + `pending.delay`.

**SMSG_CANCEL_AUTO_REPEAT (0x029C)** — empty body (`parse.rs:419`) →
`SessionEvent::CancelAutoRepeat` (`decode.rs:164`). Dispatch `apply.rs:1120` → `cancel_auto_repeat`
`spells.rs:544-557`: runs `cancel_auto_repeat_local` (dead packet vs vmangos; kept for fidelity).

CastEventKind (`crates/benilla/src/creature_anim.rs:248-266`): `Start` (SPELL_START), `Go`
(SPELL_GO), `Fail` (SPELL_FAILED_OTHER **or** own failed CAST_RESULT), `Impact{weapon_visual}`
(written back by the missile layer, not the net drain). `Casting` component
(`creature_anim.rs:207-212`): `{spell_id: u32, until: Option<Instant>}`.

---

## 5. Channel state (from UNIT_CHANNEL_SPELL descriptor)

The channeled **spell id** is descriptor field `UNIT_CHANNEL_SPELL = index 144` (u32, PUBLIC),
`crates/benilla-protocol/src/messages/update_object/fields/mod.rs:92-94`. Accessor
`unit_channel_spell()` → `get_u32(144).unwrap_or(0)` (`fields/unit.rs:32-34`). The aim target is a
separate 2-field guid `UNIT_FIELD_CHANNEL_OBJECT = index 20` (`mod.rs:42-46`), accessor
`unit_channel_object()` (`unit.rs:28-30`) — not used by the channel poll.

Channel start/stop **detection is a unified per-frame descriptor poll for self + observed units**,
in `route_cast_visuals` — `crates/benilla/src/creature_anim/spell_visual.rs:753-819`:
```
for (entity, store, _) in &units {
    let cur  = store.0.unit_channel_spell();               // 0 = not channeling
    let prev = channel_cache.get(&entity).unwrap_or(0);
    if cur == prev { continue; }                            // only EDGES act
    channel_cache.insert(entity, cur);
    if cur != 0 { /* start hold; if prev!=0 reap old channel first (channel→channel) */ }
    else        { /* clear: reap the ending channel's hold */ }
}
```
`channel_cache` is a per-system `Local` edge cache; rows dropped for despawned units
(`spell_visual.rs:818`). This is the ONLY channel-animation source for observed units — the self-only
`MSG_CHANNEL_START/UPDATE` UI packets never carry the channel spell (`mod.rs:44-45`, decision 0099).

The self-only channel UI pair (bar timer, not animation) is separate:
- `MSG_CHANNEL_START (0x0139)` — `read_channel_start` `spells.rs:272-276`: `{u32 spell_id, u32
  duration_ms}`, no guid. → `SessionEvent::ChannelStart` (`events.rs:542-544`). Dispatch inline
  `apply.rs:1149-1162`: `ChannelTicker.start(...)` + `CastBarEdge::ChannelStart{spell_id,
  duration_ms}`.
- `MSG_CHANNEL_UPDATE (0x013A)` — `read_channel_update` `spells.rs:281-283`: single `u32
  remaining_ms` (0 = channel over). → `SessionEvent::ChannelUpdate` (`events.rs:546-547`). Dispatch
  `apply.rs:1163-1171`: ticker `.update(...)` + `CastBarEdge::ChannelUpdate{remaining_ms}`.

---

## 6. Auras (from the UNIT_FIELD_AURA descriptor arrays)

Four parallel PUBLIC arrays (`mod.rs:58-71`), build-5875 indices:
- `UNIT_FIELD_AURA = 47` — 48 slots, **1 u32 spell id per slot** → indices 47..94.
- `UNIT_FIELD_AURAFLAGS = 95` — nibble-packed, **8 slots per u32** → 95..100.
- `UNIT_FIELD_AURALEVELS = 101` — byte-packed, 4 slots per u32 (caster level) → 101..112.
- `UNIT_FIELD_AURAAPPLICATIONS = 113` — byte-packed, 4 per u32, holds `stack-1` → 113..124.
Constants: `UNIT_AURA_SLOTS = 48`, `UNIT_AURA_POSITIVE_SLOTS = 32` (slots 0–31 buffs, 32–47 debuffs)
(`mod.rs:151,157`). Nibble bits: `AURA_FLAG_CANCELABLE = 0x01`, `AURA_FLAG_EFF_INDEX_MASK = 0x0E`
(`mod.rs:163,170`). Duration and caster-guid are NOT in any field (`mod.rs:62-63`).

Per-slot read `unit_aura(slot)` — `fields/unit.rs:72-93`:
- returns None if `slot >= 48`.
- **occupancy = `get_aura_nibble(slot) & 0x0E != 0`** (at least one effect-index bit) — NOT "has a
  spell id"; a cleared slot keeps a stale id. `spell_id != 0` is a belt-and-braces second guard.
- Returns `UnitAuraSlot{ slot, spell_id, flags(nibble), level: get_aura_byte(101,slot),
  stacks: get_aura_byte(113,slot).saturating_add(1) }` (struct `mod.rs:176-189`).
Nibble/byte unpack helpers: `get_aura_nibble` `mod.rs:491-496` (`(word>>((slot&7)*4))&0xF`),
`get_aura_byte` `mod.rs:500-503` (`(word>>((slot&3)*8))`).

`unit_auras()` — `fields/unit.rs:105-107`: `(0..48).filter_map(|s| unit_aura(s))`, **ascending slot**
(buffs before debuffs). Exposed on `ObjectFields`; the ECS component wrapping it is
`ObjectStore(pub ObjectFields)` (`net.rs:155`), read as `store.0.unit_auras()`.

Add/remove **detection between updates** (feeds section-4 aura watcher) — `arm_aura_state_fx`
`spell_visual.rs:846-898`:
```
let mut cur: Vec<u32> = store.0.unit_auras().map(|a| a.spell_id).collect();
cur.sort_unstable(); cur.dedup();
// prev = per-system Local `armed: EntityHashMap<Vec<u32>>`
for spell_id in prev: if !cur.contains → SpellKitFx::Reap{class: AuraState}   // REMOVE edge
for spell_id in cur:  if !prev.contains → SpellKitFx::Begin{persistent, class: AuraState}  // ADD edge
```
Same unified poll over all streamed units; `armed` rows dropped on despawn (`spell_visual.rs:897`).
The self-only `SMSG_UPDATE_AURA_DURATION (0x0137)` is the ONLY remaining-duration source — see §below.

`SMSG_UPDATE_AURA_DURATION` — `read_update_aura_duration` `spells.rs:290-294`: `{u8 slot, u32
remaining_ms}`, self-only, keyed by `UNIT_FIELD_AURA` slot; arrives BEFORE the descriptor delta that
names the slot's spell → `SessionEvent::AuraDuration{slot, remaining_ms}` (`events.rs:548-552`).
Dispatch inline `apply.rs:1175-1177`: `AuraDurations.set(slot, remaining_ms, now)` — the `ui_aura`
feed joins slot→spell by arrival order (decisions 0255/0257). An occupied slot with no duration
packet = permanent/"until cancelled".

---

## 7. Miss reason codes (SpellMissInfo → words)

The wire byte is vmangos `SpellMissInfo`, 1-based into the words table.
`crates/benilla/src/combat_text/law.rs:97-107`:

| code | word | notes |
|------|------|-------|
| 1 | Miss | |
| 2 | Resist | |
| 3 | Dodge | missile arrival plays victim Dodge(30) defense clip |
| 4 | Parry | (never a spell-missile clip; client dispatches only Dodge/Block) |
| 5 | Block | missile arrival plays ShieldBlock(24) clip |
| 6 | Evade | |
| 7 | Immune | |
| 8 | Immune | (second immune slot) |
| 9 | Deflect | |
| 10 | Absorb | category 1 (all other words category 3) |
| 11 | Reflect | `SPELL_MISS_REFLECT`; re-anchors floating word to caster (`spells.rs:402`); carries an extra reflectResult byte on the wire (`spells.rs:225-227`) |

`miss_word(code)` returns `(word, category)`; category = 1 for code 10, else 3
(`law.rs:104-107`). Codes 0 or >11 → `None`. `SpellGoTargets.misses` carries `(Entity, u8 code)` to
section 10; the missile arrival plays DODGE(3)/BLOCK(5) defense clips only (`creature_anim.rs:409-419`).

---

## 8. GUID / entity resolution

- `GuidIndex(HashMap<u64, Entity>)` — `crates/benilla/src/net.rs:207-211`. guid→ECS entity, O(1);
  maintained solely by `apply_net_updates`, populated on object create
  (`net/apply/objects.rs:175` `index.0.insert(guid, entity.id())`), removed on
  destroy/stream-out. Every spell arm resolves targets via `index.0.get(&guid).copied()`; a guid not
  in the index (out of range / never streamed) simply drops out of the hit/miss lists.
- `SelfGuid(Option<u64>)` — `net.rs:213-217`. Our own player guid; the "is this mine" test is
  `self_guid.0 == Some(caster)` (used for cast-bar, self cooldown, own-cast fail).
- Guids on the wire are packed for SPELL_START/GO's `item_or_caster`/`caster` and the target block;
  **raw u64** for SPELL_GO hit/miss lists, SPELL_FAILED_OTHER, SPELL_DELAYED, PLAY_SPELL_VISUAL. Port
  must honor this per-field.
- `item_caster` in SPELL_GO = the first packed guid **only when it differs from the caster** (an item
  use — potion/scroll); else `None` (`decode.rs:156`).


---

# Section 4 — Core Cast Orchestration / Router

Primary file: `crates/benilla/src/creature_anim/spell_visual.rs` (985 lines, read in full).
Supporting (followed): `crates/benilla-formats/src/spell_visual.rs` (VisualStages/VisualKit/catalog),
`crates/benilla/src/creature_anim.rs` (CastEvent/CastHold/EmoteAnim/WoundAnim/SpellGoTargets/PlaySeq/RangedHold/AutoRepeatArmed types + system scheduling),
`crates/benilla/src/net/apply/spells.rs` (net bridge that WRITES the CastEvents/SpellGoTargets),
`crates/benilla-formats/src/spells/display.rs` (SpellDisplay accessors: `ranged_slot`/`ranged_attack`/`visual`/`speed`),
`crates/benilla/src/creature_anim/spell_visual/tests.rs` (semantics),
`crates/benilla/src/entities/spell_fx.rs` (the FX-layer consumer — section 5's entry).

This module is the ONE place spell ids resolve to animations/sounds/effects. It is pure routing +
lifetime policy: it never spawns models, plays clips, or rings sounds itself — it writes messages
(`EmoteAnim`, `WoundAnim`, `SpellKitSound`, `SpellKitFx`, `MissileSpawn`, `SheathRequest`) and inserts/
removes the `CastHold` component. Sections 5/6/10/11/12 consume those.

---

## 0. Data model the router resolves through (from benilla-formats/spell_visual.rs)

Chain: spell id → `Spell.dbc` col 115 = `SpellDisplay::visual` (u32) → `SpellVisual.dbc` row =
`VisualStages` → one of 5 stage columns = a `SpellVisualKit.dbc` id → `VisualKit`.

`VisualStages` (spell_visual.rs formats, lines 96-123) — five stage kit ids + missile block:
```
precast: u32   // field 1
cast: u32      // field 2
impact: u32    // field 3
state: u32     // field 4
channel: u32   // field 5
missile_model: u32   // field 7  (SpellVisualEffectName id; <1 → ammo/weapon fallback)
missile_attach: u32  // field 9  (ordinal into MISSILE_ATTACH_TABLE)
missile_sound: Option<u32> // field 10 (looping flight sound)
strike_sound: Option<u32>  // field 14 ($TRD work/craft strike; held_strike_sound only)
```
`0` at a stage column = no kit there (the usual absent-FK convention).

`VisualKit` (formats lines 127-150):
```
anim_id: Option<u16>       // field 2 → AnimationData.dbc id (None = both sentinels 0/0xFFFFFFFF)
sound: Option<u32>         // field 13 → SoundEntries.dbc id
effect_slots: [Option<u32>; 9]  // fields 3-11 → SpellVisualEffectName ids
```
`VisualKit::effects()` (formats lines 144-149) yields `(attach_tag, effect_id)` for each populated
slot, tag from `KIT_SLOT_TAGS` in kit-field order.

Constants (formats):
- `KIT_SLOT_TAGS: [u16;9] = [0x14,0x22,0x13,0x15,0x16,0x11,0x17,0x18,0x19]` (line 79) —
  Head, Chest, Base, LeftHand, RightHand, Breath, Special1-3. Slot i attaches at index i.
- `MISSILE_ATTACH_TABLE: [u16;11] = [0x14,0x22,0x13,0x15,0x16,0x11,0x17,0x18,0x19,0xf,0x10]` (85-87).
- `NONE_SENTINEL: u32 = u32::MAX` (line 91); `some_unless_none(v) = (v!=0 && v!=MAX)` (269-271) —
  folds BOTH 0 and 0xFFFFFFFF to `None` on anim/sound/effect-slot columns.

Constants in the router module (spell_visual.rs):
- `ERROR_CUBE: &str = "Spells\\ErrorCube.mdx"` (line 26) — missile-model fallback for an
  unresolvable nonzero effect id.
- `LOOT_FX_KEY: u32 = u32::MAX` (line 32) — reserved reap key for the loot sparkle (NOT a spell id).
- `HARDCODED_FX_ATTACH: u16 = 0x13` (line 38) — Base attach for engine-spawned effects (loot, ding).
- `LEVEL_UP_EFFECT: &str = "HARDCODED Unit Level Up"` (line 42).

Verified Fireball chain (formats docs 55-58 + real-data test): spell 133 → visual 67 →
precast 30 / cast 38 / impact 286 / state 0 / channel 0; cast kit 38 anim 53 sound 1484;
impact kit 286 anim 9 (CombatWound) sound 1507; missile_model 365 → `Fireball_Missile_Low.mdx`,
missile_attach 1 → 0x22 (chest), missile_sound 3011 (FireMissileLoop).

---

## 1. route_cast_visuals — complete control flow (spell_visual.rs 433-819)

System signature (433-451): reads `MessageReader<CastEvent>`, `MessageReader<SpellGoTargets>`,
`MessageReader<KitPush>`; writes `EmoteAnim`, `WoundAnim`, `SpellKitSound`, `SpellKitFx`,
`MissileSpawn`, `SheathRequest`; has `Res<SpellVisuals>` + `Res<Spells>` (both Optional — absent =
early return, line 452-454), `WeaponVisualSrc` system-param, `Query<(Entity,&ObjectStore,Has<SelfPlayer>)>`,
`Query<&CastHold>`, and a `Local<EntityHashMap<u32>>` channel_cache.

The four `SpellKitFx` writers (`route_cast_visuals`, `arm_loot_fx`, `arm_level_up_fx`,
`arm_aura_state_fx`) run in ONE `.chain()` after `WorldStage::Net` and `WorldStage::Input`,
before `EntityVisualsSet` (creature_anim.rs 695-733). The net bridge writes the CastEvents in
`WorldStage::Net`, so the router sees them the same frame.

Order of operations inside the system, per frame:
1. Build `KitOut` writer bundle (455-460) and the `pending` overlay + `held_spell` closure (469-475).
2. Drain `KitPush` (477-493) — server-pushed stage-0 kits.
3. Drain `CastEvent` (495-675) — Start / Go / Impact / Fail.
4. Drain `SpellGoTargets` (681-751) — Speed branch (inline impacts vs missiles).
5. Poll channel descriptor edges over all units (757-816).
6. GC channel_cache for despawned units (818).

### START event → precast stage (spell_visual.rs 497-578)
1. `SpellKitSound::StopHold{entity}` unconditionally (500-501) — a replacing cast reaps the prior
   hold's sound loop before its own sound starts.
2. If `held_spell(pending, entity) == Some(prior)`: `SpellKitFx::Reap{entity, spell_id:prior,
   class:Hold}` (502-508) — reap the prior spell's persistent hold models.
3. If `d.ranged_attack()` (attr&0x2 OR attrEx2&0x20): `SheathRequest{state:2, ceremony:false}`
   (516-526) — snap ranged stance on the START edge (client `SetSheatheState(2,1,1)`).
4. `resolve_kit(precast selector, weapon fallback = weapon_src.caster(entity))` (527-533). If it
   resolves:
   - Compute `ranged = d.ranged_slot()` (attr&0x2). If ranged insert `RangedHold` component else
     remove it (542-550) — the `0x400` weapon-visual hold.
   - If kit has `anim_id`: insert `CastHold{anim_id, spell_id, ranged}` AND
     `pending.insert(entity, Some(spell_id))` (551-558) — the persistent cast pose.
   - If kit has `sound`: `SpellKitSound::Play{entity, kit_sound}` (559-564) — rings once at START.
   - `resolve_kit_effects(kit)`; if non-empty: `SpellKitFx::Begin{persistent:true, class:Hold,
     effects}` (567-576) — the glowing-hands, persistent until cast resolves.

### GO event → reap precast + play cast stage (spell_visual.rs 579-645)
1. If `held_spell(pending, entity) == Some(ev.spell_id)`: remove `CastHold` + `pending.insert(None)`
   (582-585) — spell-id-keyed release (a foreign proc's GO never drops this).
2. `SpellKitSound::StopHold{entity}` unconditionally (589-590) — reaps loop even if no CastHold
   (an instant whose precast had a loop but no anim).
3. `SpellKitFx::Reap{spell_id:ev.spell_id, class:Hold}` unconditionally (591-595).
4. If `d.ranged_slot()`: `SheathRequest{state:2}` (601-611) — re-draw bow before the cast kit plays.
5. `resolve_kit(cast selector, weapon fallback)` (616-622). If it resolves:
   - `ranged` → insert/remove `RangedHold` again (626-634).
   - `play_kit(entity, spell_id, kit, KitPlay::DISCRETE, seq)` (635-643) — the release flash,
     self-terminating.
   NOTE: the GO does NOT itself play impacts or launch missiles — that is the separate
   `SpellGoTargets` drain (step 4 above), keyed on `Spell.dbc` Speed.

### IMPACT event → play_impact (spell_visual.rs 646-660)
`CastEventKind::Impact{weapon_visual}` (carries the caster's ranged fallback, resolved at GO time
and ridden through the flight). Calls `play_impact(entity, spell_id, weapon_visual,
is_self = units.get(entity) self flag, seq)` (650-659). Only missile arrivals reach here; speed-0
impacts come inline from the SpellGoTargets drain.

### FAIL event → silent reap (spell_visual.rs 661-673)
Same as GO steps 1-3 but with NO cast-kit play, NO ranged re-snap: if `held_spell==Some(spell_id)`
remove `CastHold`+pending.insert(None); `StopHold`; `SpellKitFx::Reap{class:Hold}`.

---

## 2. The 5 stages

### PRECAST
- (a) Trigger: `CastEventKind::Start` (SMSG_SPELL_START, incl. instants cast_time_ms==0 —
  net/apply/spells.rs `spell_start` 251-256 writes Start for every start; the `Casting`
  component is only inserted when cast_time_ms>0, lines 241-247).
- (b) Slot: `VisualStages::precast` kit (spell_visual.rs 531). Effect slots via `KIT_SLOT_TAGS`
  (Fireball: LeftHand 0x15 + RightHand 0x16).
- (c) Lifetime: PERSISTENT / `FxClass::Hold`. Enforced by `SpellKitFx::Begin{persistent:true,
  class:Hold}` (569-575) and the anim by `CastHold` component. Reaped by the cast router's edges
  (GO/Fail/replacing-Start/channel-clear), spell-id-keyed.
- (d) Plays: body anim → `CastHold` (sustained pose, NOT a one-shot); sound → once at start;
  effect models on the CASTER, persistent.

### CAST (release)
- (a) Trigger: `CastEventKind::Go` (SMSG_SPELL_GO — net/apply/spells.rs `spell_go` 426-431).
- (b) Slot: `VisualStages::cast` kit (616). For a ranged basic shot with visual 0, resolves through
  the weapon-visual fallback (AttackThrown/AttackBow).
- (c) Lifetime: SELF-TERMINATING (`KitPlay::DISCRETE`, persistent:false). Effects run out after one
  pass of their model's first sequence (client stage-0/1 completion callback). No reap needed.
- (d) Plays via `play_kit`: body one-shot (or wound if anim 8-10) + sound + self-terminating
  effects, on the CASTER.

### IMPACT
- (a) Trigger: TWO paths. (i) `CastEventKind::Impact` (missile arrival, written by
  crate::entities::missile). (ii) inline from the `SpellGoTargets` drain for `speed<=0` (688-700).
- (b) Slot: `VisualStages::impact` kit (play_impact 399), on the TARGET.
- (c) Lifetime: SELF-TERMINATING (`KitPlay::DISCRETE`).
- (d) Plays via `play_kit`: impact kit anim (often CombatWound 8-10 → WoundAnim overlay) + sound +
  effects on the TARGET.

### STATE (impact-time flash leg)
- (a) Trigger: fired by `play_impact` immediately AFTER the impact kit (397-415), on the same
  target, from the Impact event / inline speed-0 path.
- (b) Slot: `VisualStages::state` kit (404).
- (c) Lifetime at this leg: `KitPlay{ effects:false, sound:is_self, ..DISCRETE }` (404-409) —
  effect models are SUPPRESSED here (they belong to the aura watcher, §6); only anim + (self-only)
  sound play. So this leg is a short flash, self-terminating.
- (d) Plays: anim (unconditional) + sound (ONLY if the target is the active player, `is_self` gate).
  NO effect models at this leg.

### STATE (real lifetime — aura-owned)  →  see §6 (arm_aura_state_fx)
- (a) Trigger: spell id APPEARS in `unit_auras()` slots.
- (b) Slot: `VisualStages::state` kit's effect slots.
- (c) Lifetime: PERSISTENT / `FxClass::AuraState`. Reaped ONLY when the id leaves the slots.
- (d) Plays: effect models only (the bread in the hand), persistent for the aura's whole life.

### CHANNEL
- (a) Trigger: an EDGE on the public `UNIT_CHANNEL_SPELL` descriptor (poll loop 757-816). Self AND
  observed unified — there is NO channel CastEvent; it is purely descriptor-driven.
- (b) Slot: `VisualStages::channel` kit (777). NO weapon fallback (`|| None`, 777).
- (c) Lifetime: PERSISTENT / `FxClass::Hold` while the field holds. `CastHold` for the anim
  (779-783), `SpellKitFx::Begin{persistent:true, class:Hold}` for effects (793-799). Reaped when the
  field clears to 0 (802-815).
- (d) Plays: channel anim as `CastHold`; sound once at the rising edge; persistent effect models.

---

## 3. play_kit — the full fan-out (spell_visual.rs 339-375)

```
fn play_kit(entity, spell_id, kit, play: KitPlay, seq, visuals, out)
```
Body-animation leg (348-358):
- `if let Some(anim_id) = kit.anim_id`:
  - `if (8..=10).contains(&anim_id)` → `WoundAnim{entity, anim_id}` (CombatWound family →
    SECONDARY-blend wound slot; never interrupts what plays — decision 0111).
  - else → `EmoteAnim{entity, anim_id, seq}` (ordinary over-the-gait one-shot). `seq` is the
    CastEvent's PlaySeq stamp, carried through for same-frame collision resolution.
Sound leg (359-361):
- `if let Some(kit_sound) = kit.sound.filter(|_| play.sound)` → `SpellKitSound::Play{entity, kit_sound}`.
  The `play.sound` gate is how the STATE stage's sound is made self-only.
Effects leg (362-374):
- `if !play.effects { return; }` — early-out that suppresses the STATE stage's models.
- `let effects = resolve_kit_effects(visuals, kit)`; `if !effects.is_empty()` →
  `SpellKitFx::Begin{entity, spell_id, persistent:play.persistent, class:FxClass::Hold, effects}`.
  NOTE: play_kit ALWAYS uses `class:Hold`. `AuraState` never comes through play_kit — only through
  `arm_aura_state_fx` directly.

`KitPlay` struct (322-337):
```
struct KitPlay { persistent: bool, effects: bool, sound: bool }
const DISCRETE: Self = { persistent:false, effects:true, sound:true }
```
- `persistent` → the Begin's lifetime (self-terminating vs hold).
- `effects` → whether models spawn at all (false = STATE flash leg).
- `sound` → whether the kit sound rings (used self-only for STATE).
There is only ONE named variant, `DISCRETE`. The STATE leg builds an ad-hoc `KitPlay{effects:false,
sound:is_self, ..DISCRETE}` inline in play_impact (404-409). play_kit is called with DISCRETE from:
KitPush (483-491), GO cast release (635-643), and both impact stages via play_impact (412).

`resolve_kit_effects` (281-285): maps `kit.effects()` `(tag,id)` pairs → `(tag, path)`, dropping
slots whose `effect_path(id)` is missing (client NULL-record skip). Returns `Vec<(u16, String)>`.

---

## 4. Instant vs timed casts — the `pending` overlay (spell_visual.rs 462-475, 582-585)

There is NO time-based mechanism. **There is NO `PENDING_TIMEOUT` constant anywhere in benilla**
(see §"Underspecified" — the prompt's assumption does not match this codebase). `pending` is a
per-frame, per-call `EntityHashMap<Option<u32>>` allocated fresh at the top of every
`route_cast_visuals` run (line 469) and dropped at the end. It is an in-frame WRITE-OVERLAY over the
one-command-flush-stale `holds: Query<&CastHold>`.

The problem it solves (comment 462-468): an instant cast's START and GO drain from the wire in the
SAME frame. The GO's spell-id-keyed `remove::<CastHold>` must see the hold that its own batch's
START just inserted — but the insert is a deferred `commands` op not yet applied, so the query can't
see it, the remove is skipped, and the deferred insert lands unopposed → the cast pose loops forever
(the Demon Armor / Ice Armor stuck-cast bug).

- `held_spell(pending, entity)` (470-475): returns `pending[entity]` if present (the overlay), else
  `holds.get(entity)` from the stale query.
- START, when it inserts `CastHold`, ALSO does `pending.insert(entity, Some(spell_id))` (557).
- GO/Fail/channel-clear, when they remove `CastHold`, ALSO do `pending.insert(entity, None)`
  (584, 664, 807).

So within one frame the GO reads `held_spell == Some(spell_id)` from `pending`, matches, and removes
the just-inserted hold. Tests `same_frame_start_and_go_leave_no_hold` (140-154) and
`same_frame_start_and_fail_leave_no_hold` (325-335) confirm.

How the body one-shot plays for an instant cast: START inserts `CastHold` (the pose) + writes the
precast sound/effects; GO removes `CastHold` and calls `play_kit` on the CAST kit → the actual
release one-shot (`EmoteAnim`). Since both happen the same frame the precast pose never visibly
renders; the observed animation is the cast-kit one-shot. The precast's persistent effect Begin is
reaped by the GO's unconditional `SpellKitFx::Reap` (591-595) even though it just spawned. (The
`SpellKitSound::StopHold` on GO reaps any loop the instant's precast may have started even when the
precast carried no anim, hence no CastHold — comment 586-588.)

---

## 5. play_impact (spell_visual.rs 387-415)

```
fn play_impact(entity, spell_id, weapon_visual: Option<u32>, is_self: bool, seq, spells, visuals, out)
```
Iterates a 2-element array of `(stage_selector, KitPlay)`:
1. `(|s| s.impact, KitPlay::DISCRETE)` — impact kit, full DISCRETE play.
2. `(|s| s.state, KitPlay{effects:false, sound:is_self, ..DISCRETE})` — state flash leg.
For each: `resolve_kit(spells, visuals, spell_id, stage, || weapon_visual)`; if Some →
`play_kit(entity, spell_id, kit, play, seq, visuals, out)`.

Two call sites (per-hit behaviour):
- Missile arrival: `CastEventKind::Impact` arm (650-659), `entity` = struck target, `weapon_visual`
  from the event (the caster's fallback, ridden through the flight because the caster may have
  despawned). `is_self` from `units.get(entity)` self flag.
- Speed-0 inline: `SpellGoTargets` drain, `if display.speed <= 0.0 { for &target in &go.hits {
    play_impact(target, spell_id, wv, is_self, go.seq, ...) } }` (688-700). `wv =
  weapon_src.caster(go.caster)` resolved once per GO (687). Misses do NOT play impact inline (only
  `hits` iterate). Same-spell-id, one impact per hit target.

The impact/state order matches the client's `0x61dc50` (impact then state). The persistent
aura-state instance (§6) overlaps this short flash with no visible gap.

---

## 6. arm_aura_state_fx — the aura/buff watcher (spell_visual.rs 846-898)

System: `Query<(Entity,&ObjectStore)>`, Optional visuals+spells, `MessageWriter<SpellKitFx>`,
`Local<EntityHashMap<Vec<u32>>> armed`. Runs in the same `.chain()` after `route_cast_visuals`.

Per unit each frame (856-895):
1. `prev = armed.entry(entity).or_default()` — the (unit) spell ids this watcher has armed.
2. `cur: Vec<u32> = store.0.unit_auras().map(|a| a.spell_id).collect()`; `sort_unstable(); dedup()`
   (860-862) — occupied aura slots, deduped (two casters of the same buff = one state instance).
3. REAP edge (863-871): for each `spell_id in prev` NOT in `cur` (binary_search) →
   `SpellKitFx::Reap{entity, spell_id, class:FxClass::AuraState}`.
4. ARM edge (872-893): for each `spell_id in cur`:
   - already in `prev` → push to `next`, no edge (aura still live; a refresh keeps the slot, no edge).
   - else `resolve_kit(state selector, || None)`; skip if no state kit. `resolve_kit_effects`; skip
     if empty. Otherwise `SpellKitFx::Begin{entity, spell_id, persistent:true,
     class:FxClass::AuraState, effects}`; push to `next`.
5. `*prev = next`. GC `armed.retain(units.contains)` (897).

Self-gate on state sound: NONE here — this watcher plays EFFECTS ONLY (no sound leg, no anim leg).
The state kit's sound self-gate lives in `play_impact`'s flash leg (`sound:is_self`). The ADD-edge
sound is self-only per the client (`0x5fa6d0` IsActivePlayer); observers never hear another unit's
buff kit. A state kit carrying a real anim on the aura-ADD edge is a NAMED residual, not built (docs
836-839).

Avoids re-eating on cast GO: the `armed` map keys exactly the (unit, spell) pairs THIS watcher
began, so a GO's `SpellKitFx::Reap{class:Hold}` never touches an `AuraState` instance (different
class discriminator). And `FxClass` exists precisely so the cast router's `force=0` reaps spare the
aura-state (flag 0x1000) models — the GO releasing a food spell's precast must not take the bread
(docs 66-81). Test `aura_state_kit_arms_persistent_and_reaps_on_aura_end` (424-545) confirms Begin
persistent/AuraState on ADD, nothing on hold, one Reap on REMOVE. Food shape: spell 433 → visual 51
→ state kit 409 → effect 393 `Spells\Item_Bread.mdx` at tag 0x16 (spell hand).

---

## 7. Channel (spell_visual.rs 753-818)

Unified self + observed: driven ENTIRELY off the public `UNIT_CHANNEL_SPELL` descriptor poll over
all units — the self-only MSG_CHANNEL_* packets never carry this, so there is no CastEvent for
channels. The `Local<EntityHashMap<u32>> channel_cache` provides edge detection (= the client's
per-tick armed-id dedup, so a held channel's clip is started ONCE).

Per unit (757-816):
- `cur = store.0.unit_channel_spell()`; `prev = channel_cache.get(entity).unwrap_or(0)`.
- `if cur == prev { continue; }` — only EDGES act.
- `channel_cache.insert(entity, cur)`.
- Rising / replace (`cur != 0`, 764-801):
  - `SpellKitSound::StopHold{entity}` (765).
  - if `prev != 0` (channel replacing channel): `SpellKitFx::Reap{spell_id:prev, class:Hold}`
    (766-773) — old spell's effects reap first.
  - `resolve_kit(channel selector, || None)` (no weapon fallback). If kit:
    - if `anim_id`: insert `CastHold{anim_id, spell_id:cur, ranged:false}` + `pending.insert(entity,
      Some(cur))` (778-785).
    - if `sound`: `SpellKitSound::Play{entity, kit_sound}` (786-788) — once at rising edge.
    - if effects non-empty: `SpellKitFx::Begin{persistent:true, class:Hold, effects}` (791-799).
- Falling (`cur == 0`, 802-815):
  - if `held_spell(pending, entity) == Some(prev)`: remove `CastHold` + `pending.insert(None)`
    (803-808) — ONLY the ending channel's own hold; a precast for the next spell already in flight
    survives.
  - `SpellKitSound::StopHold{entity}` (809).
  - `SpellKitFx::Reap{spell_id:prev, class:Hold}` (810-814).
- (818) `channel_cache.retain(units.contains)` — drop despawned units' rows.

---

## 8. Sound emission points (SpellKitSound / MissileSound)

`SpellKitSound` enum (spell_visual.rs 55-64): `Play{entity, kit_sound}` (LOOPING SoundEntries flag
0x200 → tracked hold loop) and `StopHold{entity}` (reap the tracked hold loop). Written here,
consumed by `crate::sound`.

Every write site:
| site | line | edge |
|---|---|---|
| START replacing-cast reap | 500-501 | `StopHold` (unconditional) |
| START precast sound | 559-564 | `Play` iff precast kit has `sound` |
| GO release reap | 589-590 | `StopHold` (unconditional) |
| GO cast-kit sound | via play_kit 359-361 | `Play` iff cast kit `sound` AND `play.sound` (DISCRETE=true) |
| Impact/inline via play_kit | 359-361 | `Play` iff kit `sound` AND `play.sound`; STATE leg gated `sound:is_self` |
| FAIL reap | 666-667 | `StopHold` (unconditional) |
| KitPush via play_kit | 359-361 | `Play` (DISCRETE) |
| Channel rising | 765 | `StopHold` |
| Channel rising kit sound | 786-788 | `Play` iff channel kit `sound` |
| Channel falling | 809 | `StopHold` |
| Channel replace prior reap | (766-773 is FX, not sound) | — |

MissileSound: the router does NOT emit missile sounds directly. It carries `missile_sound:
Option<u32>` (VisualStages field 10) on the `MissileSpawn` message (723, 746); `crate::entities::missile`
rings/stops that looping flight sound (a `MissileSound` Play/StopHold is written there, section 10/12,
not here).

---

## 9. Content gating (resolve_kit_effects(...).is_empty() guard)

The `!effects.is_empty()` guard is the HasVisibleContent-equivalent — an `SpellKitFx::Begin` is
suppressed whenever a kit's populated slots all resolve to a missing path (or the kit has no slots).
Guard sites:
- START precast effects: `if !effects.is_empty()` (568).
- play_kit effects leg: `if !effects.is_empty()` (366).
- Channel effects: `if !effects.is_empty()` (792).
- arm_aura_state_fx: `if effects.is_empty() { continue; }` (882-884) — no Begin, no `next.push`, so
  it is NOT recorded in `armed` and won't emit a phantom Reap later.
Note: anim (`CastHold`/`EmoteAnim`) and sound legs are NOT gated on effects being present — a kit
with an anim but no models still poses / plays its sound. There is no whole-Begin/whole-play
suppression beyond the effects emptiness check; the client's tiny stage-2 gate `0x61dc20` is
explicitly NOT modeled (docs 384-385).

---

## 10. The exact call into the FX layer (section 5 continues from here)

The router writes `SpellKitFx` messages ONLY (it never calls into spell_fx directly). The FX-layer
entry point is `resolve_spell_fx` in `crates/benilla/src/entities/spell_fx.rs`:
```
pub(super) fn resolve_spell_fx(
    mut commands: Commands,
    mut events: MessageReader<SpellKitFx>,
    mut units: Query<&mut FxAttached>,
    fx: Option<ResMut<SpellFx>>,
    asset_server: Res<AssetServer>,
)   // spell_fx.rs 577-583
```
`SpellKitFx::Begin` payload (spell_visual.rs 96-102) passes: `entity`, `spell_id: u32`,
`persistent: bool`, `class: FxClass`, `effects: Vec<(u16 tag, String path)>`. `SpellKitFx::Reap`
(107-111): `entity`, `spell_id`, `class`. There is NO "preferred anim" on the message — that
concept (`preferred_anim: Option<u16>` in spell_fx.rs 310/507) is internal to the FX layer's rig
build, derived there, not passed by the router.

`resolve_spell_fx` groups edges per unit preserving emission order (585-592), so a GO's
reap-then-begin lands in order (precast dies before release flash spawns). A persistent `Begin`
REPLACES the unit's live persistent instances of the same `(spell_id, class)` (616-626). `Reap`
despawns matching `(spell_id, class)` persistent instances (645-657); self-terminating instances run
out on their own clock (`FxInstance.expires`, 562-564; `attach_spell_fx` 673+). Scheduling in
entities.rs 319-345: `resolve_spell_fx` then `attach_spell_fx`.

---

## 11. Ranged weapon-visual fallback (auto-shot / wands / throw)

Present and central. Gate is `SpellDisplay::ranged_slot()` = `attributes & 0x2`
(display.rs 290-292). Mechanism in `resolve_stages` (spell_visual.rs 232-244):
```
match visuals.stages(def.visual) {
    own @ Some(_) => own,                                  // own visual wins (client 60d4b4)
    None if def.ranged_slot() => visuals.stages(weapon_visual()?),  // borrow weapon visual
    None => None,                                          // non-ranged, no visual → silent
}
```
The `weapon_visual` closure is `WeaponVisualSrc::caster(entity)` (spell_visual.rs 198-219):
caster → equipped ranged weapon display → `ItemDisplayInfo` col 10 `spell_visual` (0 → None).
Unit uses `unit_virtual_item_display(2)`; Player uses `player_visible_item_entry(17)` →
item template `display_info_id`. It is a `FnOnce` — a non-ranged spell never pays the lookup.

Where the fallback is invoked:
- START precast resolve: `|| weapon_src.caster(ev.entity)` (532).
- GO cast resolve: `|| weapon_src.caster(ev.entity)` (621).
- SpellGoTargets: `wv = weapon_src.caster(go.caster)` resolved once (687), fed to inline impacts
  (694), missile model/attach/sound resolve (707), the `awaits_release` cast-kit test (735), AND
  ridden on `MissileSpawn.weapon_visual` (745) so a basic shot's IMPACT kit resolves through it at
  arrival (the caster may be gone) — carried back as `CastEventKind::Impact{weapon_visual}`.
- Channel and held_strike_sound use `|| None` (no fallback: channels/`$TRD` never ride a shot).
Every basic shot (Throw, Auto Shot, wand Shoot) has `SpellVisual1 = 0` and rides this. Also drives
the `RangedHold` (0x400) component insert/remove on any caster (START 542-550, GO 626-634) and the
`SheathRequest{state:2}` snaps (START 516-526, GO 601-611). Test `ranged_spells_fall_back_to_the_
weapon_visual` (337-418) and `ranged_visual_play_arms_...` (658-690) confirm.

### Missile branch (SpellGoTargets, speed>0) — spell_visual.rs 701-750
Spawn gate is `Spell.dbc` Speed alone (`display.speed > 0.0`, 688). Builds `MissileSpawn`:
- `path`: `(s.missile_model >= 1).then(|| effect_path(model).unwrap_or(ERROR_CUBE))` — None → the
  spawner falls to the wire `ammo_display_id` (708-716).
- `dest_tag`: `MISSILE_ATTACH_TABLE.get(s.missile_attach as usize)` (719-720).
- `speed`: `display.speed`; `missile_sound`: `s.missile_sound` (723); `weapon_visual: wv`.
- `targets`: hits (`(e, None)`) chained with misses (`(e, Some(code))`) (724-729). Misses still fly.
- `awaits_release`: `resolve_kit(cast).is_some_and(|k| k.anim_id.is_some())` (734-736) — a cast kit
  that animates defers the launch to its release keyframe; no-anim launches at GO. Test
  `missile_spawn_defers_iff_the_cast_kit_animates` (551-652).
Only written `if !targets.is_empty()` (730). Section 10 owns flight/arrival.

---

## Loot sparkle & level-up ding (same module, SpellKitFx writers — brief)
- `arm_loot_fx` (911-940): DEAD+`unit_lootable()` unit → `Begin{spell_id:LOOT_FX_KEY(=u32::MAX),
  persistent:true, class:Hold, effects:[(0x13, loot_art_path)]}`; falling edge → `Reap`. Edge-cached
  via `Local<EntityHashSet>`.
- `arm_level_up_fx` (952-985): `UNIT_FIELD_LEVEL` CHANGE (first sight arms silently) →
  `Begin{spell_id:0, persistent:false, class:Hold, effects:[(0x13, "HARDCODED Unit Level Up" path)]}`
  — self-terminating (its own 1.867 s clip), sound is the model's own `$SND(888)` event.

---

## Cross-cutting facts other sections need

- **FxClass** (spell_visual.rs 73-81): `Hold` (cast-lifecycle: precast/channel holds + all
  self-terminating flashes; reaped by the cast router's edges, force=0, spares stage-2), `AuraState`
  (stage-2 under a live aura; reaped ONLY by arm_aura_state_fx, the force=1 path). The discriminator
  that lets a GO's Hold reap NOT sweep a same-spell aura-state instance.
- **KitPlay** (322-337): fields `{persistent, effects, sound}`. One named const `DISCRETE =
  {false,true,true}`. STATE flash uses inline `{effects:false, sound:is_self, ..DISCRETE}`. play_kit
  always emits `class:Hold`.
- **KitPush** (292-298): server-pushed stage-0 kit (SMSG_PLAY_SPELL_VISUAL) — `{entity, kit_id, seq}`,
  played DISCRETE with spell_id 0.
- **MissileSpawn** (119-157): `{caster, spell_id, path:Option<String>, ammo_display_id:Option<u32>,
  dest_tag:Option<u16>, speed:f32, targets:Vec<(Entity,Option<u8>)>, weapon_visual:Option<u32>,
  missile_sound:Option<u32>, awaits_release:bool}`. Section 10 entry.
- **SpellKitFx** (89-112): the FX message; consumed by `resolve_spell_fx(Commands,
  MessageReader<SpellKitFx>, Query<&mut FxAttached>, Option<ResMut<SpellFx>>, Res<AssetServer>)`
  (spell_fx.rs 577-583). `Begin{entity, spell_id, persistent, class, effects:Vec<(u16,String)>}` /
  `Reap{entity, spell_id, class}`. NO preferred-anim on the wire — that is derived inside spell_fx.
- **SpellKitSound** (55-64): `Play{entity, kit_sound}` / `StopHold{entity}`. Consumer =
  `crate::sound` (section 12). Missile flight sound rides `MissileSpawn.missile_sound`, rung by
  section 10/12, NOT by the router.
- **CastEventKind** (creature_anim.rs 247-266): `Start` / `Go` / `Fail` / `Impact{weapon_visual:
  Option<u32>}`. Written by net/apply/spells.rs (`spell_start`, `spell_go`, `spell_failed_other`,
  `cast_result` for own fail); Impact written by crate::entities::missile.
- **CastHold** (creature_anim.rs 276-286): `{anim_id:u16, spell_id:u32, ranged:bool}` — the
  persistent pose the driver renders (standing → full-body gait slot, moving → masked overlay).
- **held_strike_sound** (spell_visual.rs 270-276): `$TRD` work/craft strike sound resolver
  (VisualStages field 14), no weapon fallback — Mining 93→1143, Herb 91→1142.
- **Key constants**: ERROR_CUBE `Spells\ErrorCube.mdx`; LOOT_FX_KEY = u32::MAX; HARDCODED_FX_ATTACH
  = 0x13; LEVEL_UP_EFFECT = "HARDCODED Unit Level Up"; KIT_SLOT_TAGS / MISSILE_ATTACH_TABLE (§0);
  NONE_SENTINEL = u32::MAX (0 and 0xFFFFFFFF both fold to None).
- **Scheduling**: route_cast_visuals, arm_loot_fx, arm_level_up_fx, arm_aura_state_fx are ONE
  `.chain()` (creature_anim.rs 695-733) after WorldStage::Net + Input, before EntityVisualsSet.
  resolve_spell_fx → attach_spell_fx in entities.rs (319-345). The four FX writers all land in the
  same frame, so an aura-ADD Begin and a GO's impact/state flash arrive in the same burst (no gap).

---

## Underspecified / prompt mismatches

1. **PENDING_TIMEOUT does not exist in benilla.** The prompt asks for its value and treats `pending`
   as a timed overlay with a timeout. In this code `pending` is a fresh per-frame
   `EntityHashMap<Option<u32>>` Local (spell_visual.rs 469), an in-frame write-overlay over the
   command-flush-stale `CastHold` query — NO timeout, NO persistence across frames. (Grep for
   `PENDING_TIMEOUT` hits only `entities/spell_fx.rs` and `model_fade.rs` for unrelated fade timing,
   not this router.) The C# port should model an intra-frame overlay keyed to command-buffer
   staleness, not a timer. The nearest timed thing, `PendingCast` (ui_cast), is the cast-BAR
   in-flight guard, a different concept.
2. **STATE stage double-play by design.** The state kit plays TWICE conceptually: a short
   self-terminating flash at impact (play_impact, effects suppressed) AND a persistent aura-owned
   instance (arm_aura_state_fx). A port must keep them as separate lifetimes, not merge.
3. **CastEvent has no channel variant** — channels are descriptor-poll only; a port needs the
   per-entity `channel_cache` edge detector, not an event.
4. **State kit with a real anim on the aura-ADD edge** is a named residual not implemented (docs
   836-839): arm_aura_state_fx plays effects only; no live kit demonstrates a state anim.
5. **Miss deflect flight** is a named approximation — misses fly but the glancing-off visual isn't
   built; ours ends the flight at the target (SpellGoTargets docs, creature_anim.rs 414-420).
6. The client's stage-2 gate `0x61dc20` (37 bytes, content unpinned) is explicitly NOT modeled
   (spell_visual.rs 384-385).


---

# Section 5 — Effect-model FX spawn + render body + materials

Root files:
- `crates/benilla/src/entities/spell_fx.rs` (the FX render lane; the SpellFx cache, FxAttached/FxInstance, resolve_spell_fx, attach_spell_fx, attach_effect_visuals, arm_effect_rig, fx_part_material, ground_part_material, tick_fx_tint, FxTintAnims)
- `crates/benilla/src/entities/display.rs` (DisplayModel + EntityPart, build_parts)
- `crates/benilla/src/model_render.rs` (model_material — the flag→render-state mapping)
- `crates/benilla/src/doodad_anim.rs` (MatAnim: driving_tag / following, sample)
- `crates/benilla/src/mesh_tag.rs` (alpha_bits, the tag payload layout)
- `crates/benilla/src/billboard.rs` (BillboardCard following / following_joint, BillboardJointRig)
- `crates/benilla/src/ground_fx.rs` (spawn_ground_fx_decal, GROUND_FX_DEPTH_BIAS — section 9 owns it)
- `crates/benilla-assets/src/model/anims.rs` (AnimClip, ModelAnimations::preferred_clip)
- `crates/benilla-formats/src/models/mat_anim.rs` + `models/key_anim.rs` (AlphaAnim/RgbAnim sample)

Coordinate note: benilla is Bevy Y-up. All EntityPart meshes, quads, offsets, skeleton translations, bounds, and attachment offsets are already `wow_to_bevy`-mapped **at bake time** (`display.rs`/asset crate), so this render lane never re-maps geometry — the only `wow_to_bevy` call in the whole lane is in `drive_fx_view` seeding the fixture world point (`spell_fx.rs:201`) and inside `ground_fx::spawn_ground_fx_decal` mapping the authored quad corners (`ground_fx.rs:70`).

---

## Constants (spell_fx.rs)

| name | value | line | meaning |
|---|---|---|---|
| `ATTACH_FALLBACKS` | `[0xf, 0x13]` (u16) | 65 | attach cascade after the requested tag: `0xf`, then `0x13`, then unit base |
| `FALLBACK_SPAN` | `1.0` (f32 secs) | 69 | self-terminate span when the model has no sequence table at all |
| `PENDING_TIMEOUT` | `10.0` (f32 secs) | 73 | max wait for a self-terminating instance whose model never loads before it is dropped |
| `GROUND_FX_DEPTH_BIAS` | `8192.0` (f32) | ground_fx.rs:40 | rasterizer depth bias baked onto every ground-decal material clone |

---

## 1. resolve_spell_fx / attach_spell_fx — instance creation, cascade, root spawn, span, retry

### FxInstance / FxAttached (spell_fx.rs:547–571)
```
struct FxInstance {
    spell_id: u32,      // reap key; LOOT_FX_KEY = u32::MAX reserved for corpse sparkle
    persistent: bool,   // precast/channel (reaped by id) vs cast-release (self-terminates)
    class: FxClass,     // which reaper (cast router vs aura watcher) can kill a persistent one
    tag: u16,           // M2 attachment id to hang from (KIT_SLOT_TAGS)
    path: String,       // model-cache key
    root: Option<Entity>,   // spawned instance root, None while model loads
    expires: Option<f32>,   // self-terminate deadline on time.elapsed_secs() clock
}
#[derive(Component, Default)] struct FxAttached { instances: Vec<FxInstance> }
```

### resolve_spell_fx (spell_fx.rs:577–667) — consumes router `SpellKitFx` edges
- Groups edges per unit into an `EntityHashMap<Vec<&SpellKitFx>>`, **preserving emission order** (586–592) — so a per-GO reap-then-begin lands reap-before-begin.
- Per unit: skip if the unit entity is gone (595); `std::mem::take` the existing `FxAttached.instances` (599–602).
- **Begin** (605–644): if `persistent`, first `retain`-drop every live instance with `i.persistent && i.spell_id==spell_id && i.class==class`, despawning each `i.root` (616–626) — a re-apply re-arms at one sync point. Then for each `(tag, path)` in `effects`:
  - create the cache shell: `fx.models.entry(path).or_insert_with(|| DisplayModel { handle: ModelHandle::M2(asset_server.load(m2_url(path))), ..super::empty_shell() })` (628–633).
  - push `FxInstance { spell_id, persistent, class, tag, path, root: None, expires: None }` (634–642).
- **Reap** (645–657): `retain`-drop matching persistent instances + despawn their roots.
- Write back: existing unit → set `att.instances`; new unit → `insert(FxAttached { instances })` (660–665).
- NOTE: `resolve_spell_fx` only RECORDS; it never spawns geometry. Model-cache **parts** are built elsewhere (`super::update_display_models` via `display::build_parts`, the held-items pattern). Spawning is `attach_spell_fx`.

### attach_spell_fx (spell_fx.rs:673–788) — the spawn + self-termination pump
Runs each frame over `(Entity, &mut FxAttached, Option<&BoneAttach>)`. `now = time.elapsed_secs()`. `retain_mut` over the unit's instances:
1. **Self-terminate** (692–705): if `inst.expires = Some(expires)` and `now >= expires`, despawn `inst.root` and return `false` (drop it).
2. Already spawned (`inst.root.is_some()`) → keep (706–708).
3. **Pending timeout arm** (713–715): `if !inst.persistent && inst.expires.is_none() { inst.expires = Some(now + PENDING_TIMEOUT) }` — a self-terminating instance whose model never materialises is dropped after 10 s instead of piling up. (A real spawn overwrites this with the true span, step 7.)
4. Cache lookup (716–721): `let dm = fx.models.get(&inst.path)?`; `if dm.parts.is_none() { return true }` — model still loading, retried next frame.
5. **Attach cascade** (723–732) — inline, over the unit's `BoneAttach` (`bones`):
   ```
   let point = bones.and_then(|b|
       std::iter::once(inst.tag).chain(ATTACH_FALLBACKS)         // tag, 0xf, 0x13
           .find_map(|tag| b.points.get(&tag).copied().map(|p| (tag, p)))  // (tag, (bone, offset))
           .and_then(|(tag, (bone, offset))|
               b.joints.get(bone as usize).map(|&joint| (tag, joint, offset))));
   ```
   Section 6 owns `BoneAttach` (`entities/equipment.rs:241`): `joints: Vec<Entity>` (bone-index → joint entity) and `points: HashMap<u16,(u16 bone, Vec3 offset)>` (attachment-id → bone + Bevy-space offset from the bind pivot). The cascade PASSES the tag list `[inst.tag, 0xf, 0x13]` and GETS BACK `Option<(tag, joint_entity, offset)>`.
6. **Ground-anchor decision** (737): `let ground_anchor = point.is_none_or(|(tag,..)| tag == 0x13);` — true when the cascade found nothing (fell through to the unit root) OR resolved the base point `0x13`. Both are feet-level anchors; a hand/head/chest tag keeps `false`.
7. **Root spawn** (738–742):
   ```
   let (parent, offset) = point.map_or((unit, Vec3::ZERO), |(_, j, o)| (j, o));
   let root = commands.spawn((Transform::from_translation(offset), Visibility::default())).id();
   commands.entity(parent).add_child(root);
   ```
   The root is a CHILD of the attach joint at the attachment offset, so the whole effect (meshes + emitters) rides the animating bone.
8. **Attach visuals** (746–760): `attach_effect_visuals(commands, root, dm, now, ground_anchor, Some(root) /*attached model*/, ..., None /*preferred_anim → default first clip*/)`. Parts were checked ready, so it always attaches.
9. `inst.root = Some(root)` (761). **Span** (762–773): `if !inst.persistent { let span = dm.first_seq_span.unwrap_or(FALLBACK_SPAN); inst.expires = Some(now + span) }`. `first_seq_span` is the **file-order-first sequence's authored duration in seconds**, read from the raw M2 sequence table (`benilla-assets/src/m2.rs:401`: `sequences.first().map(|a| a.duration).filter(|d| *d > 0.0)`), NOT the built clips — a zero-bone-key sequence (eat/drink tankard) builds no clip but still has a span. A looping sequence counts as one pass (inferred at the loop boundary).

`fire_fx_anim_events` (796–837): separate system firing each live instance's playing-clip `$SND`-family keyframes as `AnimSoundEvent`, emitted with the HOST UNIT as the event entity, `(prev, cur]` window scan; first sight fires the `[0, cur]` head window (level-up pillar `$SND(888)` at 0.033 s). Reads `clips.first()` (not preferred_clip). Detail belongs to section 12 (sound).

`drive_fx_view` (172–278): the `fxview` capture fixture — spawns a root at `FXVIEW_POS` (optionally ray-cast down onto terrain when `req.ground`), creates the cache entry, calls the SAME `attach_effect_visuals` with `ground_anchor=true`, `attach=Some(root)`, `preferred_anim=None`, and applies the one-pass reap using `dm.first_seq_span.unwrap_or(FALLBACK_SPAN)`.

---

## 2. attach_effect_visuals — the full body (spell_fx.rs:297–491)

Signature:
```
fn attach_effect_visuals(commands, root: Entity, dm: &DisplayModel, now: f32,
    ground_anchor: bool, attach: Option<Entity>,
    meshes, particle_materials, wow_materials, tint_reg: &mut FxTintAnims,
    ibps: &Assets<SkinnedMeshInverseBindposes>, light: &SharedLightBuffer,
    preferred_anim: Option<u16>) -> bool
```
- `let Some(parts) = dm.parts.as_ref() else { return false }` (312–314) — **content gate**: still loading → caller retries next frame.
- `is_ground_decal = |part| ground_anchor && part.ground_quad.is_some()` (315) — the exact ground-decal predicate.
- **Per-instance material resolution** (319–328), done BEFORE the child-spawn closure so the material assets aren't borrowed inside it: for each part, `if is_ground_decal(p) { ground_part_material(...) } else { fx_part_material(...) }` → `Vec<Handle<WowModelMaterial>>` parallel to `parts`.
- `let joints = arm_effect_rig(commands, root, dm, preferred_anim)` (329) — spawns the rig; returns joint entities (empty for boneless).
- **`rigged = !joints.is_empty() && dm.inverse_bindposes.is_some()`** (330).
- Clip pick for keying (336–339, 474): `let played = dm.animations.preferred_clip(preferred_anim)`; `played_seq = played.map(|c| c.seq_index)` (the FILE sequence slot — key into per-sequence alpha loops); `played_anim = played.map(|c| c.anim_id)` (ribbon per-sequence visibility).

### Mesh children (340–380) — non-billboard, non-ground-decal parts
Inside `commands.entity(root).with_children(|children| …)`:
- `if part.billboard.is_some() { continue }` (342) — spawned as a following card below.
- `if is_ground_decal(part) { continue }` (345) — spawned as a decal below.
- **Which mesh** (348–351): `match (rigged, &part.skinned_mesh) { (true, Some(sm)) => sm.clone(), _ => part.mesh.clone() }` — a rigged instance uses the SKINNED twin; otherwise the static mesh.
- Spawn child (352–361) with: `Mesh3d(mesh)`, `MeshMaterial3d(material.clone())`, `Transform::default()`, `bevy::camera::visibility::NoFrustumCulling` (a billboarded subtree renders wherever the camera is → bind-pose Aabb is wrong by construction), and `ModelPart { kind: ModelKind::Creature, blend: part.blend }`.
- **SkinnedMesh** (362–368): `if let (true, Some(ibp), Some(_)) = (rigged, &dm.inverse_bindposes, &part.skinned_mesh)` → `child.insert(SkinnedMesh { inverse_bindposes: ibp.clone(), joints: joints.clone() })`.
- **Alpha loop** (371–378): `if let Some(anim) = &part.alpha_anim { let mat_anim = MatAnim::driving_tag(anim.clone(), now, played_seq); child.insert((MeshTag(alpha_bits(mat_anim.current)), mat_anim)) }`. The sampler OWNS this child's render-alpha tag (no other writer on fx parts).

### Billboard cards (381–407) — one per part with `billboard.is_some()`
```
let card = match joints.get(info.bone as usize) {
    Some(&j) => BillboardCard::following_joint(info, j),  // rides the bone's joint
    None     => BillboardCard::following(info, root),     // boneless → rides the instance root
};
commands.spawn((Mesh3d(part.mesh.clone() /* STATIC mesh, never the skinned twin */),
    MeshMaterial3d(material.clone()), Transform::default(),
    ModelPart{kind:Creature, blend:part.blend}, card));
```
Cards are world ROOTS (not children of `root`). The same `MatAnim::driving_tag` alpha loop is copied onto the card (400–406).

### Ground decals (408–447) — flat quad parts of a ground-anchored instance
`let binds = dm.inverse_bindposes.and_then(|h| ibps.get(h))` (412). For each part where `part.ground_quad.filter(|_| is_ground_decal(part))` is `Some(quad)`:
```
let (joint, ibp) = match joints.get(quad.bone as usize) {
    Some(&j) => (j, binds.and_then(|b| b.get(quad.bone)).copied().unwrap_or(Mat4::IDENTITY)),
    None     => (root, Mat4::IDENTITY),   // boneless → rides the instance root
};
let decal = ground_fx::spawn_ground_fx_decal(commands, meshes, material.clone(), &quad, joint, ibp);
commands.entity(decal).insert(ModelPart{kind:Creature, blend:part.blend});
// + the same MatAnim::driving_tag alpha loop rider (440–446)
```
Section 9 owns `spawn_ground_fx_decal` (`ground_fx.rs:62`): it maps the quad corners `wow_to_bevy`, normalizes handedness, and spawns a world-root `GroundFxDecal` (mesh `seed_mesh()`, `Visibility::Hidden`, `NoFrustumCulling`) that re-projects each frame through `joint_global × ibp × corner`. The FX lane PASSES `(material, &quad, joint_entity, ibp: Mat4)` and GETS BACK the decal `Entity`.

### Emitters (448–471) and ribbons (475–489)
- Emitters: `owner = joints.get(em.def.bone).map_or((root,[0;3]), |&j| (j, em.bone_pivot))`; `particles::spawn_emitter(commands, meshes, particle_materials, light, em, Transform::IDENTITY, Some(owner), attach, Some(root) /*anchor*/, played_seq.map_or(EmitClock::Pinned, EmitClock::PinnedSeq))`. (Sections 7/6.)
- Ribbons: `(owner, use_pivot) = joints.get(rb.def.bone).map_or((root,false), |&j| (j,true))`; `ribbons::spawn_ribbon(commands, meshes, particle_materials, light, rb, owner, use_pivot, played_anim)`. (Section 8/10.)
- Returns `true` (490).

---

## 3. arm_effect_rig (spell_fx.rs:503–544)

```
fn arm_effect_rig(commands, root, dm: &DisplayModel, preferred_anim: Option<u16>) -> Vec<Entity>
```
- `if dm.skeleton.joints.is_empty() { return Vec::new() }` (509–511) — boneless → empty (everything then rides `root`).
- `let joints = super::spawn_joints(commands, root, &dm.skeleton)` (512). `spawn_joints` (`entities/attach/mod.rs:1030`) spawns one entity per bone (`Transform::from_translation(j.local_translation)` + `Visibility::default()`), then parents each to `joints[parent]` or `root` if parent `<0`.
- Billboard rig (515–517): `if let Some(bb) = BillboardJointRig::new(&dm.skeleton, &joints, root) { insert bb on root }` — camera-facing at the palette level so skinned children inherit.
- **AnimationPlayer** (518–537): `if let Some(clip) = anims.preferred_clip(preferred_anim)` → `let mut player = AnimationPlayer::default(); let play = player.play(clip.node); if clip.looping { play.repeat() }`; insert `(player, AnimationGraphHandle(anims.graph.clone()))` on root; then for each joint `commands.entity(j).insert((bone_target_id(i as u16), AnimatedBy(root)))` (532–536).
- **GlobalSeqDrive** (538–541): `if let Some(drive) = GlobalSeqDrive::new(&anims.global_bones, &joints) { insert on root }` — free-running global-sequence channels (fireball molten-core tumble).
- Returns `joints`.

**preferred_anim**: `None` for a kit effect / fxview → `preferred_clip` returns `clips.first()` (file-order-first). `Some(144)` (InFlight) for a thrown-weapon missile (section 10) → its authored spin plays and its ribbon's per-sequence visibility keys ON. `preferred_clip` (anims.rs:163): `preferred.and_then(|id| self.find(id)).or_else(|| self.clips.first())`.

**played_seq semantics**: `played_seq = preferred_clip(preferred_anim).map(|c| c.seq_index)` — the clip's FILE sequence slot (`AnimClip.seq_index`, anims.rs:20), NOT its index in `clips`. It threads to: (a) every part's `MatAnim::driving_tag(anim, now, played_seq)` alpha loop; (b) each emitter's `EmitClock::PinnedSeq(seq)`. `played_anim = c.anim_id` threads to ribbons' per-sequence visibility. `arm_effect_rig` and `attach_effect_visuals` both call `preferred_clip(preferred_anim)`, so the rig's armed clip and the material/emitter keys agree.

---

## 4. Billboard cards (billboard.rs)

`BillboardCard` (billboard.rs:45–73) is a world ROOT with `#[require(Transform, Visibility, MeshTag)]`. Fields: `world_pivot`, `scale`, `kind: BillboardKind`, `scale_anim`, `phase_ms`, `seq_translation`, `placement_rot`, `follow: Option<Entity>`, `local_pivot: Vec3`.
- `following(info, owner)` (101–105): `Self::new(info, Transform::IDENTITY)` then `card.follow = Some(owner)`. `local_pivot = info.pivot`. Each frame `face_billboards` re-derives world pivot from the owner's live `GlobalTransform` (`re_place`: `world_pivot = owner_global.transform_point(local_pivot)`), and despawns the card when the owner is gone.
- `following_joint(info, joint)` (110–114): `following(info, joint)` then `card.local_pivot = Vec3::ZERO` — the joint's frame ALREADY bakes the bone pivot (rig identity `joint = root · M_bone · T(pivot)`), so the card sits at the joint origin.
- **Pivot handling**: `face_billboards` (393–465) each frame sets `tf.translation = card.world_pivot + bob`, `tf.rotation = billboard_basis(kind, placement_rot, fwd, right, up)`, `tf.scale = splat(card.scale) * pulse`, then writes `GlobalTransform` directly (post-propagation). The camera basis is the shared VIEW-matrix axes `(fwd,right,up)`, never a per-pivot aim. `billboard_basis` (160–203): Spherical (`0x08`) `(-fwd,right,up)`; LockZ (`0x40`) keeps model-up, rebuilds in-plane `by = fwd.cross(bz)`, `bx = by.cross(bz)`; final quat `Quat::from_mat3(&Mat3::from_cols(-by, bz, -bx))` (WoW→Bevy fold X→−Z, Y→−X, Z→+Y).
- `BillboardJointRig` (214–255) + `billboard_joint_palette` (280–377): rewrites every billboard/ignore-parent-rotation JOINT's propagated world rotation to the camera basis then recomposes descendants — so mesh/emitter/ribbon geometry skinned to a billboard bone's CHILD inherits the facing. Nested rigs (fx instance on a unit's attach helper) are do-not-enter for the outer walk. Runs in `BillboardPlace` (PostUpdate, after propagation, before visibility).

---

## 5. model_material — complete flag→render-state mapping (model_render.rs:117–323)

`WowModelMaterial = ExtendedMaterial<StandardMaterial, WowModelExt>` (terrain.rs:31). Deduped by `MatKey` (26–67). Inputs relevant to M2 fx parts (from `display::build_parts:375–401`, entity M2 path): `is_wmo=false`, `is_interior=false`, `is_emissive = sub.emissive` (M2 UNLIT 0x01), `is_additive = sub.additive` (M2 blend 3/4), `fade_variant=false`, `no_depth_write = sub.no_depth_write` (0x10), `no_depth_test = sub.no_depth_test` (0x08), `fog_policy`, `shade = ShadeSel::Lit`, `uv_anim=None`, `rgb_anim = sub.rgb_anim`, `wmo_class=None`, `sidn=None`, `window=false`.

### AlphaMode (174–190)
- `is_additive` (M2 blend 3/4) → `AlphaMode::Blend` (transparent pass; specialize later swaps state to pure ONE,ONE add).
- else by `ModelBlend`: `Opaque → AlphaMode::Opaque`; `AlphaTest → AlphaMode::Mask(VANILLA_ALPHA_KEY_REF = 224/255 ≈ 0.878)` (23); `Blend | Mod | Mod2x → AlphaMode::Blend`.

### Two-sided (191–213)
- `cull_mode = if two_sided { None } else { Some(Face::Back) }` (192). `StandardMaterial.double_sided = two_sided`, `cull_mode` set. M2 flag `0x04` → `two_sided` (display.rs / submesh).
- **No-texture path** (207–213): when `texture` is `None`, base color `Color::WHITE` (WHITE modulate — byte-verified no-source runtime-texture case, texture stage disabled), `alpha_mode`/`double_sided`/`cull_mode` still honoured.

### WowModelExt packed markers
`clutter_fade.z` (221–252) is the `specialize`-keyed pipeline word (bit layout):
| bit | value | meaning |
|---|---|---|
| 0 | `no_depth_write` | M2 0x10 — disable depth write |
| 1 | `no_depth_test` | M2 0x08 — disable depth test |
| 2 | `is_additive` | additive glow → specialize swaps blend to (ONE,ONE); shader gammma-premultiplies on this same bit |
| 3 | `matches!(blend, Opaque\|AlphaTest) && !fade_variant && !is_additive` | OPAQUE-INTENT: pin output alpha to 1.0 |
| 4–6 | `fog_policy as u8` | 0 scene / 1 additive-black / 2 Mod-white / 3 Mod2x-grey / 4 fog off (0x02) |
| 7 | `blend == Mod` | specialize → DST_COLOR/ZERO |
| 8 | `blend == Mod2x` | specialize → DST_COLOR/SRC_COLOR |

`model_flags` (263–273): `x = is_wmo?1:0`; `y = fade_variant?1:0`; `z = is_interior?1:0`; **`w` = fullbright/unlit** (`is_emissive || (!is_wmo && matches!(blend, Mod|Mod2x))` → 1.0). Shader bypasses lighting when `w > 0.5`. So M2 UNLIT (0x01) and M2 Mod/Mod2x are unlit; **un-flagged additive is LIT** (not fullbright).

`sun_scale` (285–288): `x = shade.selector()` (Lit=1.0 → shader thresholds ≥0.85 to the 2.5 lit intensity), `y = wmo_batch_order` (0 for M2), `zw = uv_anim.sample(0.0)` (0 for fx — UV anim deferred).

`tint` (297–305): `xyz = rgb_anim.map_or([1,1,1], |a| a.sample(0.0))` (first key seeded; white if static), `w = class_lane` (0 for M2). **This is the uniform `fx_part_material`/`tick_fx_tint` re-write per instance.**

`sidn` (309–317): all-zero for M2. `light_buf` = the shared light buffer (318).

Summary render-state table for an fx part (M2, `ShadeSel::Lit`, exterior):

| M2 blend | AlphaMode | specialize blend | lit? |
|---|---|---|---|
| Opaque | Opaque | — | lit (unless 0x01) |
| AlphaTest (1) | Mask(0.878) | — | lit (unless 0x01) |
| Blend (2) | Blend | src-alpha over | lit (unless 0x01) |
| additive (3/4) | Blend | ONE, ONE | lit unless 0x01 |
| Mod (5) | Blend | DST_COLOR, ZERO | **unlit** |
| Mod2x (6) | Blend | DST_COLOR, SRC_COLOR | **unlit** |
| + flag 0x04 | — | — | double_sided, cull None |
| + flag 0x10 | — | no depth write | — |
| + flag 0x08 | — | no depth test | — |
| + flag 0x01 (emissive) | — | — | **unlit/fullbright** |

---

## 6. Per-instance color/alpha

### fx_part_material (spell_fx.rs:121–138) + tick_fx_tint (99–116) — the RGB tint clone
- `fx_part_material`: `if part.rgb_anim is None → return part.material.clone()` (shared handle). Else clone the shared material asset, set `mat.extension.tint = Vec4(anim.sample(0.0)…, 1.0)` (seed at first key), `add` a NEW material asset, register `tint_reg.0.insert(handle.id(), (anim.clone(), now))` → per-instance clone (one cast = one phase). Falls back to the shared handle if the shared material isn't built yet.
- `tick_fx_tint` (system): for every live clone id, `mat.extension.tint = Vec4(anim.sample(now - origin)…, 1.0)`; drops entries whose material asset is gone (instance despawned). NOT capture-gated. `FxTintAnims` = `HashMap<AssetId<WowModelMaterial>, (Arc<RgbAnim>, f32 origin)>`.
- `RgbAnim::sample(elapsed)` (mat_anim.rs:40) → `sample_or(elapsed, [1,1,1])` → `sample_clocked(elapsed, wrap=true)` (key_anim.rs:88): `t = elapsed.rem_euclid(period)` (loops), linear or step per `step`, holds first/last key outside the span; `period<=0 || 1 key ⇒ constant`.

### ground_part_material (spell_fx.rs:144–163) — decal clone
Always a per-instance clone (carries `mat.base.depth_bias = GROUND_FX_DEPTH_BIAS = 8192.0` for coplanarity), plus the same tint-clone contract folded in when `part.rgb_anim` is `Some`.

### MatAnim::driving_tag (doodad_anim.rs:323–333) — the alpha loop that drives render alpha
`MatAnim::driving_tag(anim: Arc<AlphaAnim>, now: f32, seq: Option<usize>)`: `drives_tag = true`, `seq = played_seq`, `current = anim.sample(seq, 0.0)`. Not frozen (fxview ages effects through captures). The FX part carries `(MeshTag(alpha_bits(mat_anim.current)), mat_anim)`.
- `sample_mat_anim` (doodad_anim.rs:387–414) ticks it each frame BEFORE the visibility authority: a pinned instance (`host=None`) reads `elapsed = now - spawned_at` on its own slot → `m.current = anim.sample(seq, elapsed)`.
- `AlphaAnim::sample(seq, elapsed)` (mat_anim.rs:108) = `AlphaSeq::sample` = `color.sample(elapsed) * weight.sample(elapsed)` (57–61), each `ScalarAnim::sample` = `sample_or(elapsed, 1.0)` (wraps `elapsed mod period`). `seq` indexes the per-sequence `AlphaSeq` (`seq(None)` or out-of-range → slot 0).
- **Value → tag → render alpha**: `mesh_tag::alpha_bits(alpha)` (mesh_tag.rs:106): `alpha<=0 → 1u32` (≈0, invisible, never the `0` untagged-⇒-opaque sentinel); else `((alpha.min(1.0)*65535).round()).max(1)` into bits 0..15. The shader reads bits 0..15/65535 as the fade alpha, multiplied into cutout alpha; combined alpha `≤0` culls the batch (wow-re `m2-alpha-combine-cull`). `drives_tag=true` means the MatAnim owns the whole tag by itself (no other writer on fx parts).

---

## 7. Content gating / retry

- **`attach_effect_visuals` returns `false`** iff `dm.parts.is_none()` (spell_fx.rs:312–314) — the model asset hasn't built its parts yet. There is NO per-part "has visible content" check; once `parts` is `Some`, it always attaches and returns `true`.
- **Retry**: `attach_spell_fx` calls it only after asserting `dm.parts.is_some()` (719–721), so in the game lane the return value is effectively always true; the retry is the `dm.parts.is_none() { return true /*keep pending*/ }` guard at 719–721 (keeps the instance for a later frame). `drive_fx_view` (260–277) retries by only setting `state.attached_at` when `attach_effect_visuals(...) == true`.
- **Pending timeout**: a self-terminating instance that never spawns is dropped after `PENDING_TIMEOUT = 10.0 s` (713–715); persistent instances are reaped only by their spell edge.
- **HasVisibleContent-equivalent**: none. An empty `parts` vec (cube/no-geometry fallback, `display::empty_display`) yields zero children — the instance still "attaches" (true) and self-terminates on its span; there is no visible-content precondition.

---

## Cross-cutting facts for other sections

### EntityPart field list (display.rs:32–101) — the per-part input to this lane
`mesh: Handle<Mesh>`; `skinned_mesh: Option<Handle<Mesh>>` (skinned twin, `Some` for every M2 part, `None` WMO); `material: Handle<WowModelMaterial>` (exterior); `material_interior`, `material_interior_bake`, `material_interior_bake_blend`, `fade_blend: Option<...>` (interior/fade variants — UNUSED by the fx lane, which only reads `material`); `blend: ModelBlend`; `two_sided: bool`; `geoset_id: u16`; `char_slot: Option<CharSkinSlot>`; `billboard: Option<BillboardInfo>`; `alpha_anim: Option<Arc<AlphaAnim>>`; `rgb_anim: Option<Arc<RgbAnim>>`; `ground_quad: Option<GroundQuad>`. DisplayModel-level fields the fx lane reads: `parts: Option<Vec<EntityPart>>`, `skeleton`, `inverse_bindposes: Option<Handle<SkinnedMeshInverseBindposes>>`, `animations: Option<ModelAnimations>`, `emitters`, `ribbons`, `first_seq_span: Option<f32>`, `handle`.

### Attach-call signature to section 6
The cascade is inline (`attach_spell_fx`, spell_fx.rs:723–732), consuming section 6's `BoneAttach { joints: Vec<Entity>, points: HashMap<u16,(u16 bone, Vec3 offset)>, markers: HashMap<[u8;4],(u16,Vec3)> }` (equipment.rs:241). Tag cascade `[requested_tag, 0xf, 0x13]` → first `points` hit → `(tag, joints[bone], offset)`, else `None` (unit root). `ground_anchor = point.is_none() || tag == 0x13`.

### Section 9 dispatch: `spawn_ground_fx_decal(commands, meshes, material: Handle<WowModelMaterial>, quad: &GroundQuad, joint: Entity, ibp: Mat4) -> Entity`. Called only for `ground_anchor && part.ground_quad.is_some()`. `GROUND_FX_DEPTH_BIAS = 8192.0` is baked onto the material clone by `ground_part_material` (NOT by section 9).

### Section 7 dispatch: `spawn_emitter(commands, meshes, particle_materials, light, em: &ModelEmitter, Transform::IDENTITY, owner: Some((joint_or_root, bone_pivot)), attach: Option<Entity>, anchor: Some(root), clock: EmitClock)` where `clock = played_seq.map_or(EmitClock::Pinned, EmitClock::PinnedSeq)`.

### Material flag → state table: see §5 (bit values `0x01/0x04/0x08/0x10` and blend 3/4/5/6; `clutter_fade.z` bits 0–8; `model_flags.w` fullbright).

### played_seq semantics
`played_seq = dm.animations.preferred_clip(preferred_anim).map(|c| c.seq_index)` — the **file sequence slot** (not the clips-vec index). `preferred_anim`: `None` (kit/fxview → file-order-first clip), `Some(144)` (missile InFlight). `played_seq` threads into every part's `MatAnim::driving_tag(.., played_seq)` (per-sequence alpha loop) and each emitter's `EmitClock::PinnedSeq(seq)`; `played_anim = c.anim_id` threads into ribbons.

---

## Underspecified / approximations (named in-code)
- Span-based self-termination stands in for the client's model-event completion callback (module doc, spell_fx.rs:41). A looping first sequence is counted as one pass (INFERRED, 767–770).
- `first_seq_span` is the **file-order-first** sequence duration; the client arms `animationLookup[0]`'s sequence (they coincide on single-sequence effect models — m2.rs:80–82). Note `dm.first_seq` (used by the doodad idle) is a DIFFERENT selection (animationLookup[0]) than `first_seq_span` (file-order-first).
- UV-scroll channel does not run in this lane (`uv_anim=None` at build; scope was placed doodads only — display.rs:396).
- Kit sound plays unconditionally where the client gates it on no-visual-attached (module doc, spell_fx.rs:42–43).
- The exact `AlphaAnim`/`RgbAnim`/`KeyAnim` interpolation math (period wrap, band windows, step vs lerp) is baked in `benilla-formats` (section 2 territory); this lane only calls `.sample(seq, t)` / `.sample(t)`.


---

# Section 6 — Bone-attach & joint system (benilla → C# port)

How an effect / weapon / marker rides a unit's animated skeleton. benilla is **Bevy, Y-up, right-handed, column-vector matrices** (a point transforms as `p' = M·p`, and a composed transform is read right-to-left: the transform closest to the point applies first). The WoW→Bevy map is the pure rotation `wow_to_bevy([x,y,z]) = (−y, z, −x)` (det +1, 1 unit = 1 yard) — `crates/benilla-assets/src/coords.rs:17`.

The whole mechanism has ONE governing invariant, stated verbatim in the source and repeated here because it is the exact thing a port gets wrong:

> **The joint entity already carries the bone's bind pivot. Therefore every attach/marker offset is baked as `wow_to_bevy(position) − pivot`, and the visible child is spawned at `Transform::from_translation(offset)` UNDER the joint. At bind pose `pivot + offset = wow_to_bevy(position)`, so the child lands exactly on the authored attach point; under animation the joint's live matrix carries both the pivot and the offset through the up-chain.**

---

## 1. `spawn_joints` — each bone becomes a joint ENTITY

`crates/benilla/src/entities/attach/mod.rs:1030` (`spawn_joints`). One entity per bone, seeded with the bone's **rest-local translation** and a `Visibility`, then parented per the skeleton (root bones → `root`, others → their parent joint entity):

```rust
// attach/mod.rs:1035
let joints: Vec<Entity> = skeleton.joints.iter().map(|j| {
    commands.spawn((
        Transform::from_translation(j.local_translation),   // seed = rest-local
        Visibility::default(),
    )).id()
}).collect();
for (i, j) in skeleton.joints.iter().enumerate() {
    let parent = usize::try_from(j.parent).ok()
        .and_then(|p| joints.get(p).copied())
        .unwrap_or(root);                                    // parent < 0 → `root`
    commands.entity(parent).add_child(joints[i]);
}
```

The seed `j.local_translation` is defined in `crates/benilla-assets/src/model.rs:243` (`build_skeleton`):

```rust
// model.rs:250 — pivots = wow_to_bevy(bone.pivot), file order (skeleton_pivots, model.rs:284)
local_translation: pivots[i] - parent_pivot,   // pivot_i − pivot_parent
// model.rs:262
let inverse_bindposes = pivots.iter().map(|p| Mat4::from_translation(-*p)).collect();
```

Because every rest-local translation is `pivot_i − pivot_parent` (a **pure translation**, no rotation — vanilla M2 rest pose is identity TRS), they **telescope up the chain to `pivot_i`**. So at bind pose the joint entity's transform relative to `root` is `T(pivot_i)`, i.e.

> **`joint_global == root_global · T(pivot_i)` at bind pose** — the claimed invariant (`model.rs:234-240`: "the pivot encodes bind position … at rest every joint matrix collapses to the entity transform"). The `pivot_i` is `wow_to_bevy(bone.pivot)` (Bevy-space bind pivot).

`skeleton_pivots` (`model.rs:284`) is the single pivot source that `build_skeleton` (inverse bindposes) AND `build_attachments`/`build_markers` (offsets) all derive from, so they can never compute a divergent pivot (`m2.rs:387-395` passes `skeleton_pivots(&skeleton_raw)` to both bakes).

Wiring into animation (`setup_skinned_instance`, `attach/mod.rs:150`): each joint entity gets `bone_target_id(i)` + `AnimatedBy(entity)`, binding it to the `AnimationPlayer` living on the unit `entity`:

```rust
// attach/mod.rs:150
for (i, &j) in joints.iter().enumerate() {
    commands.entity(j).insert((bone_target_id(i as u16), AnimatedBy(entity)));
}
```

`bone_target_id(bone) = AnimationTargetId::from_name("benilla_bone_{bone}")` (`model.rs:433`) — the clip curves target the same synthetic id, so no real `Name` hierarchy is needed.

---

## 2. `BoneAttach` struct + the pivot-subtraction bake

Struct: `crates/benilla/src/entities/equipment.rs:240`:

```rust
pub(crate) struct BoneAttach {
    pub(crate) joints: Vec<Entity>,                 // joint entities, bone order
    pub(crate) points: HashMap<u16, (u16, Vec3)>,   // attach id → (bone idx, bevy offset)
    pub(crate) markers: HashMap<[u8; 4], (u16, Vec3)>, // 4CC → (bone idx, bevy offset)
}
```

Built at visual-attach in `attach_entity_visuals` (`attach/mod.rs:496-509`): `points` from `d.attachments` mapping `a.id → (a.bone, a.offset)`; `markers` from `d.markers` with **first-record-per-ident wins** (`entry(...).or_insert(...)` — the client's `0x7130e0` first-match scan, and character models carry six `$CSD` records):

```rust
// attach/mod.rs:497
let mut markers = HashMap::new();
for m in &d.markers { markers.entry(m.ident).or_insert((m.bone, m.offset)); }
commands.entity(entity).insert(super::BoneAttach {
    joints: joints.clone(),
    points: d.attachments.iter().map(|a| (a.id, (a.bone, a.offset))).collect(),
    markers,
});
```

Where `offset` is computed — the pivot subtraction — `build_attachments` (`model.rs:379`) and `build_markers` (`model.rs:413`), identical bake:

```rust
// model.rs:386 (attachments) / 420 (markers)
let pivot = pivots.get(a.bone as usize)?;                 // bevy-space bind pivot of the bone
Some(ModelAttachment { id: a.id, bone: a.bone,
    offset: wow_to_bevy(a.position) - *pivot });          // offset = pos_bevy − pivot_bevy
```

`pivots` here is `skeleton_pivots` = `wow_to_bevy(bone.pivot)` per bone. **WHY subtract the pivot:** the child rides the joint entity, and the joint entity's global already contains `T(pivot_i)` (§1). Spawning the child at `T(offset)` gives `T(pivot_i)·T(offset) = T(pivot_i + offset) = T(wow_to_bevy(pos))` at bind pose — the attach point. If the port fed raw `pos` while the bone/Skin matrix already carries the pivot, the pivot double-counts. Note `model.rs:291`: on character models the attach bones are leaves sitting exactly at the point, so `offset ≈ Vec3::ZERO` — a port that forgets the subtraction will look correct on human hands and wrong on creatures.

---

## 3. The attach cascade (effects — the caller into §4)

Not in equipment.rs (held items do a direct `points.get(&hs.attach)`, no fallback). The cascade lives in `crates/benilla/src/entities/spell_fx.rs`. Fallback constant (`spell_fx.rs:65`):

```rust
const ATTACH_FALLBACKS: [u16; 2] = [0xf, 0x13];   // retry 0xf, then 0x13, then unit base
```

Search order and return (`spell_fx.rs:722`):

```rust
let point = bones.and_then(|b| {
    std::iter::once(inst.tag)                       // requested tag first
        .chain(ATTACH_FALLBACKS)                     // then 0xf, then 0x13
        .find_map(|tag| b.points.get(&tag).copied().map(|p| (tag, p)))
        .and_then(|(tag, (bone, offset))| {
            b.joints.get(bone as usize)
                .map(|&joint| (tag, joint, offset))  // returns (tag, joint_entity, offset)
        })
});
```

Ground-anchor decision + placement (`spell_fx.rs:737`):

```rust
let ground_anchor = point.is_none_or(|(tag, ..)| tag == 0x13);   // no point OR landed on 0x13
let (parent, offset) = point.map_or((unit, Vec3::ZERO), |(_, j, o)| (j, o));
let root = commands.spawn((Transform::from_translation(offset), Visibility::default())).id();
commands.entity(parent).add_child(root);
```

So: returns `(tag, joint_entity, offset)`; if nothing resolves, `parent = unit` (the unit root entity) and `offset = ZERO`. `ground_anchor` is true iff the point is absent (fell through to unit root) OR the resolved tag is `0x13` (the model's feet-level base point) — both feet-level anchors; a ground-anchored instance turns its flat quads into projected terrain decals (`spell_fx.rs:733-737`, `crate::ground_fx`).

---

## 4. Effect/weapon ROOT placement + composed world transform

The child root entity is spawned **at `Transform::from_translation(offset)` under the joint entity** (effects: `spell_fx.rs:739-742`; held items: `equipment.rs:766-769`):

```rust
// equipment.rs:766
let root = commands.spawn((Transform::from_translation(offset), Visibility::default())).id();
commands.entity(joint).add_child(root);
```

No explicit multiply is ever written. Bevy's transform propagation composes the world transform from the parent chain: unit root → (optional conform / mount-seat node) → joint entities up the bone chain → this root. The item/effect mesh parts are then plain children of `root` at `Transform::default()` (`equipment.rs:815`, `spell_fx.rs`), so the item's own model origin **is** the grip/attach point (`equipment.rs:17-18`).

**Full composed world transform of the attached child, symbolically (Bevy column-vector, right-to-left):**

```
W_child = T_unitRoot · [R_unit] · S_unit
              · Π_{k∈ chain(bone←…←root)}  L_k(t)      // animated local TRS of each joint up-chain
              · T(offset)                               // offset = wow_to_bevy(pos) − pivot_bone
```

where each `L_k(t)` is the joint entity's current local `Transform` (translation `rest_k + wow_to_bevy(track)`, rotation `r·q·r⁻¹`, scale) sampled from the playing clip (§5), and `S_unit = splat(net.scale)` (§7). At **bind pose** every `L_k` is a pure translation and the chain telescopes to `T(pivot_bone)`, so:

```
W_child(bind) = T_unitRoot·R_unit·S_unit · T(pivot_bone) · T(offset)
              = T_unitRoot·R_unit·S_unit · T(pivot_bone + offset)
              = T_unitRoot·R_unit·S_unit · T(wow_to_bevy(pos))       // the authored attach point
```

**Mapping to the C# target `T(pos−pivot) · Skin · unit`:** that expression is the same composition in reverse (row-vector) order — `unit · Skin · T(pos−pivot)` in column-vector terms. Here **`Skin` ≡ the bone's animated global matrix = `Π L_k(t)`**, which at rest equals `T(pivot_bone)` (it BAKES the pivot). So the port MUST use `offset = wow_to_bevy(pos) − pivot_bone` (exactly benilla's bake), NOT raw `pos`. The only way raw `pos` would be correct is if the port's `Skin` were the *deformation delta* (identity at rest, pivot NOT baked in); benilla's joint matrix is the bind-inclusive matrix, so the pivot is subtracted. Confirm which convention the C# `Skin` uses before choosing.

---

## 5. Per-frame bone update (what moves the joint each frame)

No custom benilla system writes the joint TRS — it is **Bevy's built-in `AnimationPlayer`**. Chain:
- The `AnimationPlayer` + `AnimationGraphHandle` + `ModelAnimations` live on the unit `entity` (`attach/mod.rs:119-123`).
- Each joint entity is bound to it by `bone_target_id(i)` + `AnimatedBy(entity)` (`attach/mod.rs:150-154`).
- The playing clip's curves target `bone_target_id(bone)` (`model.rs:474` `build_animation_clip` → `add_curve_to_target(bone_target_id(bk.bone), …)`, `model.rs:482,493,510,522`).
- Bevy's animation system samples the clip at the player's current time and **overwrites each joint entity's local `Transform`** (translation/rotation/scale) each frame; transform propagation then recomputes every `GlobalTransform` down-chain, so the attached child root (and its mesh parts) follow for free.

Clip channel bake (`model.rs:487`), the exact per-channel math the port must reproduce:

```rust
// translation: rest-local + wow_to_bevy(track)  (track is a DELTA on the pivot offset)
let rest = skeleton.joints.get(bk.bone as usize).map_or(Vec3::ZERO, |j| j.local_translation);
trans: (t, rest + wow_to_bevy(*v))
// rotation: conjugate the WoW quat into Bevy space
rot:   (t, r * Quat::from_xyzw(q[0],q[1],q[2],q[3]) * r.inverse())   // r = wow_to_bevy_quat()
// scale: axis-permute WoW→Bevy magnitudes
scale: (t, Vec3::new(s[1], s[2], s[0]))
```

`r = wow_to_bevy_quat()` (`model.rs:441`) is the rotation part of `wow_to_bevy`. Supplementary per-frame passes that also touch joints (read the propagated globals, rewrite specific bones): the billboard/ignore-parent-rotation pass (`billboard.rs:309-334`) and `GlobalSeqDrive` for global-sequence bone channels (`attach/mod.rs:157`, free-clock loops). These are additive; the primary driver is the `AnimationPlayer`.

---

## 6. Markers ($CSL/$CSR/$CST/$BWR) → (bone, offset) → world position

Baked identically to attachments (`build_markers`, `model.rs:413`, §2): `ModelMarker { ident:[u8;4], bone:u16, offset = wow_to_bevy(pos) − pivot_bevy(bone) }`, file order preserved, first ident match wins. Stored in `BoneAttach.markers` (`equipment.rs:248`).

World-position resolution (the section-10 missile consumer), `missile.rs:186` `launch_world_pos`:

```rust
// missile.rs:193 — cascade: the fired event's own marker, then $CSL → $CSR → $CST, else unit base
let point = bones.and_then(|b| fired.into_iter().chain(MARKER_CASCADE)
    .find_map(|ident| b.markers.get(&ident).copied())
    .and_then(|(bone, offset)| b.joints.get(bone as usize).map(|&j| (j, offset))));
// missile.rs:201 — transform the bone-local offset by the joint's LIVE global
match point.and_then(|(j, off)| joints.get(j).ok().map(|g| g.transform_point(off))) {
    Some(p) => p,
    None => base.translation(),         // fallback: the unit's base position
}
```

`MARKER_CASCADE = [$CSL, $CSR, $CST]` (`missile.rs:75`); the full release-ident set is `[$CSL,$CSR,$CST,$BWR]` (`missile.rs:71`). So the **marker world position = `joint_GlobalTransform.transform_point(offset)`** = `W_joint · offset` where `W_joint` is the bone's live animated global and `offset = wow_to_bevy(pos) − pivot`. Same pivot-subtraction rule as §2/§4: `transform_point` applies the joint's full matrix (which carries `+pivot`) to `offset` (which carries `−pivot`), netting the authored `pos` at bind pose and the animated launch point otherwise. `$BWR` is a documented approximation — it launches from its own event marker rather than the true ranged-slot muzzle (`missile.rs:43`).

---

## 7. Scale handling

Yes — the unit's per-object scale factors into the attach transform, and it enters through the joint hierarchy, not the offset bake.

- The unit root's `Transform.scale` is set to `Vec3::splat(net.scale)` at attach (`attach/mod.rs:860`): `t.scale = Vec3::splat(net.scale);`. `net.scale` = the server's `OBJECT_FIELD_SCALE_X` **alone** (the server already folded the DBC `CreatureModelData.modelScale × CreatureDisplayInfo.scale` into it — `attach/mod.rs:854-859`). Do NOT multiply the DBC scale again.
- Because the joint entities are children of the unit root (or of a conform node / mount-seat anchor that is itself under the root), `S_unit` propagates through the whole bone chain and thus into the attached child's world transform (see §4's `S_unit` term). The offset bake itself is pure geometry (`pos − pivot`), scale-free; scale is applied by propagation.
- The skinned-mesh render path is scale-safe by construction: `joint_matrix = joint_global · inverse_bindpose = (… · T(pivot)) · T(−pivot)`, the two pivot translations cancel **before scale acts**, so a scaled unit renders undeformed (`model.rs:238-240`).
- **Mount seat counter-scale** (the one place scale is explicitly manipulated): the rider's seat anchor under the mount's attachment-0 joint is spawned with `.with_scale(Vec3::splat(1.0 / mount_scale.max(0.001)))` (`attach/mod.rs:350`), so the rider keeps its own size while riding the mount's (already-scaled) seat joint — `mount_scale = CreatureDisplayInfo.creatureModelScale` alone (`attach/mod.rs:300-308`).


---

# Section 7 — Particle SIMULATION math (birth, emission, integration)

benilla root: `C:\Users\nico\Desktop\benilla-main`. Bevy is **Y-up, −Z forward, right-handed**;
the M2 emitter local frame is **WoW Z-up (+X north, +Y west, +Z up)**. Files traced in full:
- `crates/benilla/src/particles.rs` (1153 lines) — components, `spawn_emitter`, `emit_local`, `accumulate_emission`.
- `crates/benilla/src/particles/sim.rs` (945 lines) — the update system, `integrate_particle`, `inherit_trigger`, `drive_child`, births, placement refresh.
- `crates/benilla/src/particles/model.rs` — 3-D model-particle DRAW pool (over-life sampling + instance transform; sim part is the tumble/quat done in sim.rs).
- `crates/benilla/src/particles/material.rs` — **render-only, belongs to section 8** (gamma combine, fog policy, Mod2x blend). No sim effect. Not documented further here.

Followed-into definitions:
- `crates/benilla-assets/src/coords.rs` — `wow_to_bevy` / `bevy_to_wow` axis map.
- `crates/benilla-formats/src/particles.rs` — `ParticleEmitterDef` + all its flag/query methods, `OverLife::sample`, `CellRamp`, `SplineData::{eval,tangent}`, `ParticleShape`, `ParticleBlend`.
- `crates/benilla-formats/src/emit_timing.rs` — `EmitTiming::{rate,emitting,peak_rate,constant}` (details are SECTION 2; signatures noted).
- `crates/benilla/src/particles/quads.rs` — `DrawFrame`/`CamBasis` and the draw-side re-application of the anchored transform (SECTION 8; cited for the birth↔draw round-trip only).

---

## 0. THE AXIS CONVERSIONS (the thing a C# port silently diverges on)

### 0.1 `wow_to_bevy` / `bevy_to_wow` (coords.rs:16-24)
```rust
pub fn wow_to_bevy(p: [f32; 3]) -> Vec3 { Vec3::new(-p[1], p[2], -p[0]) }   // (x,y,z)_wow → (−y, z, −x)_bevy
pub fn bevy_to_wow(b: Vec3) -> [f32; 3] { [-b.z, -b.x, b.y] }               // inverse
```
Golden (coords.rs:137-153): WoW **+X (north) → Bevy −Z**; WoW **+Y (west) → Bevy −X**; WoW **+Z (up) → Bevy +Y**.
Determinant +1 (pure rotation, no mirror, 1 yd = 1 unit). **Key identity** (coords.rs:123-133):
`wow_to_bevy(rot_z(θ)·v) == rot_y(θ)·wow_to_bevy(v)` — a WoW yaw about +Z equals a Bevy yaw about +Y.

**C# port note:** this is componentwise, applied per-vector. `placement.scale` is applied
**componentwise in Bevy space** (`placement.scale * wow_to_bevy(v)`), i.e. AFTER the axis swap, so a
non-uniform placement scale must be multiplied per-Bevy-axis, not per-WoW-axis. All benilla placement
scales here are effectively uniform (doodad/creature scales), so this rarely bites — but replicate the order.

### 0.2 The `rot90`-about-local-+Z prepended at EVERY emission (particles.rs:278, 348-357)
`emit_local` closes with a fixed **R(+Z, +90°)** applied to the kernel-relative vectors (NOT the origin):
```rust
let rot90 = |v: Vec3| Vec3::new(-v.y, v.x, v.z);   // R(+Z, +90°) in the WoW Z-up local frame
...
(origin + rot90(local), rot90(dir))                // sphere/plane tail  (particles.rs:357)
return (origin + rot90(pos - origin), rot90(dir)); // spline branch      (particles.rs:307)
```
- Applied to `local` (birth offset from `def.position`) and to `dir` (unit velocity direction).
- **`origin = def.position` stays OUTSIDE the rotation** — the record-position translation is never rotated by R.
- Byte ground: `0x719114–0x719142` — axis literal (0,0,1), angle π·0.5, Rodrigues + mat4_mul into the
  per-frame emitter matrix rt+0x1fc. It is per-EMITTER and subclass-independent (sphere, plane, spline all take it).
- Under `wow_to_bevy`, WoW local +Z ≡ Bevy +Y, so R rides as a **+90° yaw about Bevy +Y** — this is exactly
  what the model-particle seed basis uses: `r90 = Quat::from_rotation_y(FRAC_PI_2)` (sim.rs:669).
- benilla applies R **at emission** (stored vectors are already post-R); the reference stores pre-R and folds
  R inside its draw matrix. Equivalent compositions; benilla keeps a single application point (sim.rs:465-468).

**Because R is baked at emission, EVERY stored↔world fold in sim.rs is R-free.** A C# port that instead
folds R at draw MUST NOT also apply it at birth (double-rotation), and vice versa. Pick one point.

---

## 1. `spawn_emitter` — components, owner/attach/anchor, the bone_pivot rebase (particles.rs:519-612)

Signature (particles.rs:520-531): `commands, meshes, materials, light, emitter: &ModelEmitter,
placement: Transform, owner: Option<(Entity,[f32;3])>, attach: Option<Entity>, anchor: Option<Entity>,
clock: EmitClock`.

- Env kill-switch: `$WOW_NO_PARTICLES` ⇒ spawn nothing (particles.rs:533).
- Texture gate: no texture ⇒ `None`, UNLESS a geometry (model-particle) emitter, which keeps a
  `Handle::default()` non-resident handle so quads stay empty (particles.rs:539-543).
- **bone_pivot rebase (particles.rs:544-555):** `def = emitter.def.clone()`, then
  ```rust
  def.position = [ def.position[0]-pivot[0], def.position[1]-pivot[1], def.position[2]-pivot[2] ];
  ```
  Raw WoW axes throughout. For a **joint owner** the pivot is the bone-local pivot (rebases model-space
  origin into the joint's own frame → an unanimated chain reproduces the static path exactly). For a
  **whole-model owner** pivot = `[0,0,0]` (leaves it model-space). `owner` collapses `(entity, pivot)` → just `entity`.
- Emit-nothing gate (particles.rs:559): `if def.lifespan <= 0.0 || def.timing.peak_rate() <= 0.0 { return None }`
  — `peak_rate()` is the PEAK over every sequence, so a `0→200→0` burst still passes.
- `EmitClock` → `(host, seq)` (particles.rs:562-566): `Pinned ⇒ (None,None)`; `PinnedSeq(s) ⇒ (None,Some(s))`;
  `Host(h) ⇒ (Some(h),None)`.
- RNG seed from placement position (particles.rs:570-573): `(x.bits ^ y.bits.rol(11) ^ z.bits.rol(22)) * 0x9E3779B9 | 1`.
- **Components spawned (particles.rs:576-609):** `Mesh3d`, `MeshMaterial3d`, `Transform::IDENTITY`,
  `NoFrustumCulling`, and the `ParticleEmitter` with: `def`, `placement`, `owner`, `draining:false`,
  `attach`, `attach_rot: Quat::IDENTITY`, `anchor`, **`anchor_pos: placement.translation`**,
  `particles: []`, `accumulator:0`, `emitter_prev:None`, `inherit_accum:0`, `inherit_vel:ZERO`,
  `gate_prev:false`, `age:0`, `host`, `seq`, `rng`, `mesh`, `texture`, `recursion`, `children:[]`,
  `geometry`, `model_instances:[]`.

### The three owner/frame roles (particles.rs:99-193 field docs)
- `owner: Option<Entity>` — the entity whose live world transform is copied into `placement` each frame
  (creature/GameObject/joint). `None` = static terrain doodad (fixed placement).
- `attach: Option<Entity>` — the **attach frame `A`** for an emitter on an ATTACHED model (spell-kit root,
  held item). Anchored-mode births divide `A(t₀)⁻¹` out; draw re-applies live `A(t₁)`. `None` ⇒ `A = identity`.
- `anchor: Option<Entity>` — the **cloud anchor** (the MODEL root — creature/effect/item root), whose live
  translation carries the whole pool. **NOT the bone joint.** `None` ⇒ anchor at the spawn placement.

---

## 2. Per-frame placement refresh from the joint (sim.rs:401-464)

Order inside the per-emitter loop:
1. `*age += dt` (sim.rs:401).
2. `anchored = !def.model_space()` (sim.rs:405) — `model_space()` is file flag `0x10` (particles-formats:446).
3. **placement track / drain (sim.rs:412-420):** if `owner` set → `transforms.get(o)` OK ⇒
   `*placement = gt.compute_transform()`; Err ⇒ `*owner = None; *draining = true`.
4. drain-despawn if `draining && pool empty && all children empty` (sim.rs:421-432): despawn child/instance
   entities + self, `continue`.
5. **attach rot (sim.rs:435-441):** if `attach` set and live → `*attach_rot = rot`; then `attach_inv = attach_rot.inverse()`.
6. **anchor_pos (sim.rs:444-452):** `Some(a)` live ⇒ `*anchor_pos = gt.translation()`; `None && owner.is_none()`
   ⇒ `*anchor_pos = placement.translation`; `None` joint-owned ⇒ unchanged (spawn placement stands).
7. **emitter origin world (sim.rs:462-464):**
   ```rust
   let emitter_world = placement.transform_point(wow_to_bevy(def.position));
   let emitter_delta = emitter_prev.map_or(Vec3::ZERO, |prev| emitter_world - prev);
   *emitter_prev = Some(emitter_world);
   ```
   `emitter_world` is the birth ORIGIN in Bevy world space: `placement · wow_to_bevy(def.position)`.
   `emitter_delta` is the one-frame world Δ (refreshed EVERY frame — a multi-frame inherit window still
   measures one frame's motion).

`to_stored(world, attach_inv, placement)` folds a world vector into the stored frame (sim.rs:469-477):
```rust
if anchored { attach_inv * world }
else { bevy_to_wow((placement.rotation.inverse() * world) / placement.scale.max(Vec3::splat(1e-6))) }
```

---

## 3. `emit_local` — birth position + unit velocity, in the WoW Z-up local frame (particles.rs:272-358)

`origin = Vec3::from(def.position)`. Returns `(position, unit_dir)`, both **post-R (see §0.2), WoW frame**.
Speed roll is applied by the CALLER, not here.

### 3.1 SPHERE (particles.rs:314-321, 332-340)
```rust
let r   = def.area_length + rand01(rng) * (def.area_width - def.area_length).max(0.0);
let lat = rand_s11(rng) * def.vertical_range;      // S11 = uniform (−1,1)
let lon = rand_s11(rng) * def.horizontal_range;
let (slat,clat)=lat.sin_cos(); let (slon,clon)=lon.sin_cos();
let shell = Vec3::new(clat*clon, clat*slon, slat);  // unit by construction
let local = r * shell;                              // birth offset
```
Direction (particles.rs:332-340): if `z_source != 0` ⇒ radial from pivot (see §3.4); else if `sphere_up()`
(file flag `0x100` on a sphere, particles-formats:489) ⇒ `Vec3::Z`; else ⇒ `shell` (radial outward, **reuses
the exact same lat/lon unit vector** — so a ZERO-radius sphere `min=max=0` still sprays uniformly, never a
degenerate normalize). `area_length`=min radius, `area_width`=max radius.

### 3.2 PLANE (particles.rs:322-331, 341-347)
```rust
let local = Vec3::new(rand_s11(rng)*0.5*def.area_width, rand_s11(rng)*0.5*def.area_length, 0.0);
```
Rectangle is **±½·area** (width along local X, length along local Y, z=0). Direction (else-branch, no shell):
```rust
let theta = rand_s11(rng)*def.vertical_range;  let phi = rand_s11(rng)*def.horizontal_range;
Vec3::new(st*cp, st*sp, ct)   // cone around +Z, SYMMETRIC angles (S11, i.e. ±range not [0,range])
```
If `z_source != 0` ⇒ radial from pivot instead (§3.4).

### 3.3 SPLINE (particles.rs:287-308) — repurposed fields
`t0 = area_length.clamp(0,1)`, `t1 = area_width.clamp(0,1)`; `t = t0 + rand01·(t1−t0)`.
`pos = origin + spline.eval(t)`. Direction:
- `z_source != 0` ⇒ `(pos − origin − (0,0,z_source)).normalize_or(Z)`.
- else `vertical_range != 0` ⇒ +Z rotated about the local tangent by `ψ = S11·vertical_range` (Rodrigues,
  particles.rs:298-299); optional scatter `pos += rand01·horizontal_range·dir` (particles.rs:300-302).
- else ⇒ `Vec3::ZERO` (particle sits on curve; only gravity/drag move it).
Return: `(origin + rot90(pos−origin), rot90(dir))`. `SplineData::eval/tangent` are arc-length parameterized
cubic-Bézier chain walks (particles-formats:298-314); details are SECTION 1/2.

### 3.4 zSource pivot (any shape, particles.rs:332-334): `dir = (local − (0,0,z_source)).normalize_or(Z)`
— radial from the point `(0,0,z_source)` toward the birth point.

### 3.5 Speed roll — applied by the CALLER at each birth
Parent (sim.rs:602-603): `speed = def.emission_speed * (1.0 + def.speed_variation * (rand01(rng)*2.0−1.0))`.
Child (sim.rs:135-136): `speed = child.def.emission_speed * (1.0 + child.def.speed_variation * rand_s11(&mut child.rng))`.
(Both are `emission_speed·(1 + variation·S11)`; parent inlines the S11 as `rand01*2−1`.)

RNG helpers (particles.rs:234-253): xorshift32; `rand01 = (next>>8)/2^24 ∈ [0,1)`; `rand_s11 = rand01*2−1 ∈ (−1,1)`.

---

## 4. `accumulate_emission` — the emission front end (particles.rs:747-772; called sim.rs:587-595)

```rust
if !emitting { *accumulator = 0.0 }                       // gate OFF resets the fractional accumulator
let rate = if emitting { rate.max(0.0) } else { 0.0 };    // floored at 0 (track tails go negative)
let gate = rate > 0.0;
let mut burst = 0.0;
if is_burst {                                             // file flag 0x8000 (def.burst())
    if gate && !*gate_prev { burst = (rate * scale).trunc(); *accumulator = burst; }   // rising edge only
} else if gate {
    *accumulator += rate * scale * dt;                    // continuous pour
}
*gate_prev = gate;
```
- `scale` passed in = `density * dist_lod` (sim.rs:591).  So **continuous:** `acc += rate·density·distLOD·dt`.
  **Burst:** `acc = trunc(rate·density·distLOD)` ONCE on the rising edge of the gate, re-armed when gate falls.
- Birth loop drains whole particles (sim.rs:599-600): `while *accumulator >= 1.0 && particles.len() < MAX_PARTICLES { *accumulator -= 1.0; ... }`.

Inputs (sim.rs:583-586):
```rust
let emitting = !*draining && def.timing.emitting(clock_seq, elapsed_s);   // enabled M2Track gate
let rate     = def.timing.rate(clock_seq, elapsed_s);                     // keyed rate, floored ≥0
let dist_lod = (1.0 - (placement.translation.distance(e_cam_pos) - 50.0) * 0.02).clamp(0.25, 1.0);
```
- **distLOD is EXACTLY** `clamp(1 − (camDist − 50)·0.02, 0.25, 1.0)` — full inside 50 yd, linear falloff,
  25% floor from 87.5 yd out, never zero. `camDist = placement.translation.distance(e_cam_pos)` (booth camera
  for booth-layered emitters, else world camera). Scales RATE only, never size/alpha.
- **density = `tuning.density.clamp(0.25, 1.0)`** (sim.rs:231), the `particleDensity` CVar. Also RATE-only.
- `EmitTiming::{emitting,rate,peak_rate}` (emit_timing.rs:69-98) sample the playing sequence's keyed window
  (STEP/lerp per SECTION 2). `emitting` returns `sample > 0.5`; `rate` returns `sample.max(0.0)`.
- clock resolution (sim.rs:569-582): `Host(h)` ⇒ live `playing_seq(player,anims)` gives `(slot, clip_time)`,
  caches `*seq`; else pinned lane uses `(*seq, *age)`.

---

## 5. `integrate_particle` — EXACT step order (sim.rs:38-78)

`StepEnv { dt, lifespan, gravity, drag, anchored, kill_origin: Option<Vec3>, follow: Vec3 }`.
```rust
let (dt, g) = (env.dt, env.gravity);
p.age += dt;
if p.age >= env.lifespan { return false; }                       // 1. AGE + KILL (>= lifespan)
if p.fresh { p.fresh = false; } else { p.pos += env.follow; }    // 2. FOLLOW-DELTA (skip on fresh)
let theta = p.angvel.length();                                   // 3. TUMBLE (model particles only)
if theta > 1e-4 { p.quat = (p.quat * Quat::from_axis_angle(p.angvel/theta, theta*dt)).normalize(); }
let step_vel = p.vel;                                            //    capture PRE-gravity, PRE-drag velocity
p.pos += p.vel * dt;                                             // 4. POSITION step (uses current velocity)
if env.anchored {                                               // 5. GRAVITY on the frame's UP axis
    p.pos.y -= 0.5 * g * dt * dt;  p.vel.y -= g * dt;            //    anchored ⇒ Bevy +Y (world up)
} else {
    p.pos.z -= 0.5 * g * dt * dt;  p.vel.z -= g * dt;            //    model  ⇒ WoW  +Z (local up)
}
if env.drag != 0.0 { let f = (dt*env.drag).min(1.0); p.vel -= f*p.vel; }   // 6. DRAG (after gravity)
if let Some(origin) = env.kill_origin {                         // 7. KILL-OUTBOUND (sphere flag 0x80)
    if step_vel.dot(p.pos - origin) > 0.0 { return false; }     //    step_vel (pre-grav/drag) vs UPDATED pos
}
true
```
**Exact clamps/constants:** `dt` clamp = **0.1** (sim.rs:226 `time.delta_secs().min(0.1)`, early-out if `<=0`).
Tumble threshold **1e-4**. Drag factor **`min(dt·drag, 1.0)`** (never over-shoots into negative velocity).
Kill test **`> 0.0`** strictly. Gravity split: position gets `−½·g·dt²`, velocity gets `−g·dt` — semi-implicit,
`step_vel` (the value the position update consumed) is what the kill test uses, NOT the post-gravity velocity.

Called via `particles.retain_mut(|p| integrate_particle(p, &env))` (sim.rs:542) — dead particles removed.

`kill_origin` in the STORED frame (sim.rs:524-532):
```rust
def.kill_outbound().then(|| if anchored {
    attach_inv * (placement.translation - *anchor_pos + placement.rotation*(placement.scale*wow_to_bevy(def.position)))
} else { Vec3::from(def.position) })
```
i.e. the emitter origin `def.position` composed exactly like a birth. `kill_outbound()` = sphere shape AND
file flag `0x80` (particles-formats:499-501).

---

## 6. Anchored vs model-space storage — the birth transform + attach_inv (sim.rs:610-643)

At birth, `(base, dir) = emit_local(def, rng)` (post-R WoW frame), `speed` rolled (§3.5), then:
```rust
let (mut pos, vel) = if anchored {
    ( attach_inv * (placement.translation - *anchor_pos
                    + placement.rotation * (placement.scale * wow_to_bevy(base.to_array()))),   // POS
      attach_inv * (placement.rotation * (placement.scale * wow_to_bevy((dir*speed).to_array()))) ) // VEL
} else {
    (base, dir * speed)                                                                          // model: raw WoW local
};
```
**Anchored (attach may or may not be set; `attach_inv = identity` when unattached):**
- Convert WoW→Bevy (`wow_to_bevy`), apply `placement.scale` componentwise (Bevy axes), then `placement.rotation`
  → world-oriented Bevy vector. For POSITION also subtract `anchor_pos` (store relative to the cloud anchor,
  NOT the emitter/bone), and add back `placement.translation` (the bone/emitter offset from the anchor is baked in).
- Then `attach_inv * (...)` divides out the live attach rotation `A(t₀)⁻¹` — the birth heading of the host.
- **Result stored:** world-oriented Bevy axes, relative to the anchor, attach-local. Bone & model rotation
  baked at birth; the anchor translation follows the model each frame for free.

**Model mode (file flag 0x10 set):** `pos = base`, `vel = dir*speed` — **raw WoW model-local frame, Z-up, unrotated.**

### `attach_inv` explained
`attach_inv = attach_rot.inverse()` (sim.rs:441), refreshed each frame from the attach entity's live world
rotation. At birth the pool divides `A(t₀)⁻¹` out; at DRAW the pool is re-multiplied by the CURRENT `A(t₁)`
(quads.rs:151-152: `center = anchor + attach_rot * p.pos`; velocity quads.rs:222-223: `attach_rot * p.vel`;
model.rs:141: `translation = anchor_pos + attach_rot * p.pos`, `rotation = attach_rot * p.quat`). Net: a host
that turns mid-effect swings the frozen cloud by the heading change since each particle's birth (the
Eviscerate/Feint scatter); a stationary host (`A` constant) is bit-identical to the plain path.

### The cloud anchor = the MODEL root (NOT the bone)
`anchor_pos` (§2 step 6) is the model/effect/item root's live translation. The bone joint composes only each
particle's BIRTH (position + rotation baked into `pos`/`vel`) and must never move the risen cloud again — an
emitter riding an animated bone (a global-sequence spin) births a moving ring of straight risers, never a
swirling cloud. Draw anchor (sim.rs:731-736): anchored ⇒ `anchor_pos`, model ⇒ `placement.translation`; written
to the entity Transform AND directly to GlobalTransform (post-palette, sim.rs:742).

### GROUND SNAP (file flag 0x2000, anchored only, at spawn — sim.rs:628-634)
```rust
if anchored && def.ground_snap() {
    let world = *anchor_pos + *attach_rot * pos;                                   // to world
    if let Some(hit) = spatial.cast_ray(world, Dir3::NEG_Y, 20.0, true, &snap_filter) {  // 20 yd straight down
        let lifted = world.y - hit.distance + def.over_life.sample(0.0).size;       // stand on surface + birth half-size
        pos = attach_inv * (Vec3::new(world.x, lifted, world.z) - *anchor_pos);      // back to stored frame
    }
}
```
Probe distance **20.0 yd**, `Dir3::NEG_Y`, against terrain+WMO+doodad (`player_query_filter`). Miss ⇒ untouched.

### VELOCITY INHERIT consumption at birth (file flag 0x40 — sim.rs:638-643)
```rust
let vel = if def.inherits_emitter_motion() && *inherit_vel != Vec3::ZERO {
    vel + (1.0 + def.speed_variation * rand_s11(rng)) * to_stored(*inherit_vel, attach_inv, placement)
} else { vel };
```
`inherit_vel` (a WORLD vector) is folded into the stored frame via `to_stored` (§2). Its own S11 draw.

### FOLLOW-DELTA computed once per frame (file flag 0x4000 — sim.rs:488-495)
```rust
let follow = if def.follow_emitter() && emitter_delta != Vec3::ZERO {
    def.follow_line().map_or(Vec3::ZERO, |(slope,intercept)| {
        let fraction = (slope * emitter_delta.length()/dt + intercept).clamp(0.0, 1.0);
        to_stored((fraction - 1.0) * emitter_delta, attach_inv, placement)          // (fraction−1)·Δ over anchor-riding storage
    })
} else { Vec3::ZERO };
```
`follow_line()` = the two-point response line (particles-formats:464-470); `None` (⇒ zero) when the two authored
speeds coincide. Applied to every non-fresh particle in integrate step 2. Over benilla's anchor-riding storage
the stored move is `(fraction−1)·Δ`: 0 at saturation (rigid ride), full `−Δ` at fraction 0 (world-frozen trail).

### VELOCITY-INHERIT trigger (~30 Hz — sim.rs:85-96, driven sim.rs:498-507)
```rust
const INTERVAL: f32 = 1.0/30.0;
*accum += dt;
if *accum > INTERVAL {
    *held = if live { delta * (INTERVAL / *accum) * scale } else { Vec3::ZERO };   // scale = def.inherit_scale
    *accum = 0.0;
}
```
`delta = emitter_delta` (world), `live = !particles.is_empty()`, `scale = def.inherit_scale`. Held between
triggers (births read the held value). The `INTERVAL/accum` factor is load-bearing (a ~30× over-kick without it).

---

## 7. Over-life sampling — color/scale across [0,1] life (particles-formats:173-218)

`OverLife::sample(u)` where `u = age/lifespan` clamped [0,1]:
```rust
let u = u.clamp(0.0, 1.0);
let mid = self.mid.clamp(1e-3, 1.0);                             // mid=0 divides by zero in the reference; refused
let (k0,k1,t,seg) = if u <= mid { (0,1, u/mid, 0) }             // segment A: key0→key1, split INCLUSIVE toward A
                    else        { (1,2, (u-mid)/(1.0-mid).max(1e-3), 1) };  // segment B: key1→key2
let t = t.clamp(0.0,1.0) * 0.99 + 0.005;                        // THE ENDPOINT INSET (0.5%..99.5%), for ALL of color+size+cells
color[c] = self.color[k0][c] + (self.color[k1][c] - self.color[k0][c]) * t;   // 3-key ramp, two linear segments
size     = self.scale[k0]   + (self.scale[k1]   - self.scale[k0])   * t;
let ct = if self.repeat[seg] != 1.0 { (t*self.repeat[seg]).fract() } else { t }; // repeat cycles CELLS ONLY
head_cell = self.head_cells[seg].sample(ct);  tail_cell = self.tail_cells[seg].sample(ct);
```
- **Midpoint split** at `mid` (default 0.5), inclusive toward segment A (`u > mid` is the only way into B).
- **Endpoint inset** `t·0.99 + 0.005`: a particle at a segment start sits 0.5% along, never exactly on the key.
- **BGRA→RGBA:** the color keys are decoded at PARSE time (particles-formats:720-728): packed u32 →
  `R=(v>>16)&0xff, G=(v>>8)&0xff, B=v&0xff, A=(v>>24)&0xff`, each /255. So `color[..]` here is already linear RGBA.
- **Alpha as additive weight:** `color[3]` is the particle's over-life opacity/additive weight (no separate alpha array).
- `repeat` cycles the FLIPBOOK cells only (via `fract`); color and size never cycle.
- Cells (`CellRamp`, particles-formats:97-127) are SECTION 8's concern (render), not sim.

### Model-particle DRAW sampling (model.rs:135-153) — sim-adjacent
`u = (p.age/lifespan).clamp(0,1)`; `ol = over_life.sample(u)`; `rgba=ol.color`, `size=ol.size`.
Instance transform:
- anchored: `translation = anchor_pos + attach_rot·p.pos`, `rotation = attach_rot·p.quat`, `scale = size·inst_scale`.
- model:    `translation = placement.transform_point(wow_to_bevy(p.pos))`, `rotation = placement.rotation·p.quat`, `scale = size·inst_scale`.
- `inst_scale = scale_size_by_instance() ? placement.scale.x.max(1e-4) : 1.0` (model.rs:118-123; flag 0x20).
Tint on a per-instance material clone `Vec4(r,g,b,1)`; alpha `rgba[3]` rides a `MeshTag`.

### Model-particle TUMBLE seed (sim.rs:651-678) — the birth quat + angvel
```rust
let mut w = [ amin[0] + rand01(rng)*(amax[0]-amin[0]),    // X: min + u·range
              (1.0+rand01(rng))*(amax[1]-amin[1]),         // Y: RAW [1,2) mantissa × range (authored min DEAD)
              (1.0+rand01(rng))*(amax[2]-amin[2]) ];        // Z: same asymmetry — a verified original-client quirk
if def.tumble_random_sign() { for a in &mut w { if next_u32(rng)&1==0 { *a = -*a } } }  // flag 0x200
let r90 = Quat::from_rotation_y(FRAC_PI_2);                // the R(+Z,90°) as a Bevy +Y yaw (§0.2)
let quat = if anchored { attach_inv * placement.rotation * r90 } else { r90 };
(quat, wow_to_bevy(w))                                     // angvel converted to Bevy axes
```
Quad particles carry `Quat::IDENTITY` / `Vec3::ZERO` (sim.rs:677).

---

## 8. ALL constants / clamps (with values + file:line)

| Name | Value | Where |
|---|---|---|
| `MAX_PARTICLES` (per-emitter pool cap) | **1024** | particles.rs:43 |
| `MAX_INSTANCES` (model-particle draw pool) | **128** | model.rs:32 |
| dt clamp | `time.delta_secs().min(0.1)`, early-out if `<= 0` | sim.rs:226-228 |
| density clamp | `tuning.density.clamp(0.25, 1.0)` (default 1.0) | sim.rs:231, particles.rs:59 |
| distLOD | `(1.0 - (dist - 50.0)*0.02).clamp(0.25, 1.0)` | sim.rs:585-586 |
| gravity position term | `pos.up -= 0.5*g*dt*dt` | sim.rs:62,65 |
| gravity velocity term | `vel.up -= g*dt` | sim.rs:63,66 |
| gravity up axis | anchored ⇒ Bevy **+Y**; model ⇒ WoW **+Z** | sim.rs:61-67 |
| drag | `f = (dt*drag).min(1.0); vel -= f*vel` (only if drag≠0) | sim.rs:68-71 |
| tumble threshold | `angvel.length() > 1e-4` | sim.rs:56 |
| kill-outbound test | `step_vel.dot(pos - origin) > 0.0` (step_vel = pre-grav/drag) | sim.rs:72-76 |
| rot90 (emitter frame R(+Z,90°)) | `(v)=>(-v.y, v.x, v.z)` | particles.rs:278 |
| model-particle r90 | `Quat::from_rotation_y(FRAC_PI_2)` | sim.rs:669 |
| inherit trigger INTERVAL | `1.0/30.0` s; held `= delta*(INTERVAL/accum)*scale` | sim.rs:87-95 |
| inherit consumption | `vel += (1 + speed_variation·S11)·to_stored(inherit_vel)` | sim.rs:638-643 |
| follow fraction | `clamp(slope·|Δ|/dt + intercept, 0, 1)`, stored move `(fraction-1)·Δ` | sim.rs:490-491 |
| speed roll | `emission_speed·(1 + speed_variation·S11)` | sim.rs:602-603, 135-136 |
| ground-snap probe | `cast_ray(world, Dir3::NEG_Y, 20.0)`; lift `= surfaceZ + over_life.sample(0).size` | sim.rs:628-634 |
| plane rect | `±0.5·area_width` (X), `±0.5·area_length` (Y), z=0 | particles.rs:322-330 |
| sphere radius | `[area_length, area_width]` (min,max) | particles.rs:315 |
| over-life mid clamp | `mid.clamp(1e-3, 1.0)` | particles-formats:179 |
| over-life endpoint inset | `t*0.99 + 0.005` (covers color+size+cells) | particles-formats:196 |
| scale.max in to_stored/model-fold | `placement.scale.max(Vec3::splat(1e-6))` | sim.rs:474 |
| inst_scale | `scale_size_by_instance()? placement.scale.x.max(1e-4) : 1.0` | model.rs:118-123 |
| child recursion cap | 4 emitters | particles.rs:678-679 |
| spawn emit-nothing gate | `lifespan<=0 || timing.peak_rate()<=0` | particles.rs:559 |

Twinkle (`twinkle_speed/percent/min/max`) is RENDER-side size modulation (SECTION 8): the sim does NOT touch
it. `def.twinkle(noise)` = `min==max ? 1.0 : noise·(max-min)+min` (particles-formats:551-557); `phase` (spawn
`next_u32`) is the twinkle LUT de-sync index only. `tail_time`/`spin`/`head_tail`/`xy_quad`/`tile_*` are all render-side.

### CHILD emitters (sim.rs:98-161, 697-723) — per-parent-particle drive
`drive_child` runs the child's `accumulate_emission` **once per live parent particle** (volume ∝ live count),
births at each parent particle's post-integration position folded through the PARENT's rotation
(`fold(v) = anchored ? attach_inv·(placement.rotation·(placement.scale·wow_to_bevy(v))) : v`, sim.rs:137-144);
the child's OWN record position is subtracted (`local = base - origin`). Child flag 0x40 adds `(1+S11·var)·p.vel`
(the PARENT particle's velocity IS the child inherit vector). Child slot-0 clock on parent `*age`. Then the
child pool is integrated with its own `StepEnv` (no follow, no kill_origin, sim.rs:711-722).

---

## SUMMARY (6-10 lines)

The CPU integrator (`integrate_particle`, sim.rs:38-78) runs a fixed order: age+kill (age≥lifespan) →
follow-delta add (skipped on the fresh frame) → Rodrigues tumble (>1e-4) → capture pre-gravity `step_vel` →
`pos += vel·dt` → gravity on the frame's UP axis (`pos.up −= ½g·dt²`, `vel.up −= g·dt`; Bevy +Y anchored, WoW +Z
model) → drag `vel −= min(dt·drag,1)·vel` → sphere kill-outbound `dot(step_vel, pos−origin) > 0`. `dt` is clamped
to 0.1. Emission is `acc += rate·density·distLOD·dt` continuous, or `acc = trunc(rate·density·distLOD)` once on
the gate's rising edge for burst (flag 0x8000); the birth loop drains whole particles up to MAX_PARTICLES=1024.
Birth shapes come from `emit_local` in the WoW Z-up local frame with a fixed **R(+Z,90°)** prepended to every
kernel vector at emission (NOT to `def.position`). Anchored births are baked to world-oriented Bevy axes,
stored relative to the MODEL anchor with the live attach rotation divided out (`attach_inv`), re-applied at
draw; model-space births keep raw WoW-local coords, folded through `placement` every frame. Over-life color
(BGRA→linear RGBA, alpha = additive weight) and size sample a 3-key, midpoint-split ramp with a `t·0.99+0.005`
endpoint inset.

## CROSS-CUTTING FACTS OTHER SECTIONS NEED

1. **Axis map (all sections):** `wow_to_bevy([x,y,z]) = (−y, z, −x)`; inverse `bevy_to_wow(b) = [−b.z, −b.x, b.y]`
   (coords.rs:17-24). WoW +X→Bevy −Z, +Y→−X, +Z→+Y. Determinant +1. `placement.scale` multiplied componentwise
   in **Bevy** axes (after the swap).
2. **The R(+Z,90°) emitter frame (§0.2, sections 5/6/8):** applied ONCE at emission in `emit_local`
   (`rot90(v)=(-v.y,v.x,v.z)`), to kernel-relative vectors only, not the record position. It equals a +90° yaw
   about Bevy +Y (`Quat::from_rotation_y(FRAC_PI_2)`). The quad/model-particle draw side (section 8) must NOT
   re-apply it — benilla folds it at birth, so all stored↔world folds are R-free. This is the #1 silent-divergence hazard.
3. **Anchored birth transform (section 8 must invert it):**
   `pos = attach_inv·(placement.translation − anchor_pos + placement.rotation·(placement.scale·wow_to_bevy(base)))`,
   `vel = attach_inv·(placement.rotation·(placement.scale·wow_to_bevy(dir·speed)))` (sim.rs:610-622).
   Draw re-applies: `world = anchor_pos + attach_rot·pos` (quads.rs:151-152, model.rs:141). Model mode stores
   raw WoW-local and draws `placement.transform_point(wow_to_bevy(pos))`.
4. **anchor = MODEL root, NOT the emitter bone** (particles.rs:130-142). Bone motion bakes into each birth only.
   `def.position` gets `−= bone_pivot` at spawn (particles.rs:544-555).
5. **space split = file flag 0x10** (`model_space()`): clear ⇒ anchored (world-oriented Bevy axes, gravity on +Y);
   set ⇒ model-local (WoW Z-up, gravity on +Z).
6. **Constants (section 4/8/10):** MAX_PARTICLES=1024, dt clamp 0.1, density clamp [0.25,1.0],
   distLOD=clamp(1−(d−50)·0.02, 0.25, 1.0), inherit interval 1/30, ground probe 20 yd, over-life inset t·0.99+0.005.
7. **Emission rate/enabled come pre-sampled from `EmitTiming`** (section 2): `emitting` = sample>0.5,
   `rate` = sample.max(0). `peak_rate()` gates spawn at all. Burst flag 0x8000 vs continuous is the emission-model split.

## UNDERSPECIFIED / CAVEATS

- **`OverLife::sample` mid=0 and cell NaN:** benilla deliberately refuses the reference's div-by-zero
  (`mid.clamp(1e-3,1.0)`, `CellRamp::sample` saturating float→int, particles-formats:120-126,179). The reference
  walks NaN; benilla clamps. No shipped emitter authors mid∈{0,1} (corpus-swept), but a C# port that mirrors
  the reference bit-for-bit would fault where benilla doesn't. Match benilla's clamp.
- **Model-mode emitter-motion fold** is an acknowledged frame quirk (sim.rs:454-461): the reference adds the raw
  WORLD Δ vector to LOCAL coords; benilla folds it into the local frame via `bevy_to_wow` instead (its world
  axes are Bevy's). "Translation-dominant content is equivalent" — a port should follow benilla's fold, not the
  reference's raw add, unless matching the reference's world axes.
- **Spline per-segment arc length** is a 16-chord subdivision (particles-formats:245-253); the reference's exact
  `0x453e50` length method is untraced ("within a fraction of a percent"). Section 1/2 territory.
- **Y/Z tumble asymmetry** (sim.rs:654-658): authored `angular_velocity_min[1..2]` is DEAD (only X reads its min).
  A faithful original-client quirk — a port must replicate the `(1+rand01)·(amax−amin)` form for Y/Z, not `amin+u·range`.
- **`spatial.cast_ray` ground snap** depends on the collision world being populated that frame; a port needs the
  equivalent terrain+WMO+doodad collision audience resident, or snap silently no-ops (leaves spawn untouched).


---

# Section 8 — Particle RENDERING (billboards/quads) + RIBBONS

Scope: how simulated particles become drawable geometry (`quads.rs`), how the
particle/ribbon material maps to render state (`material.rs` + `wow_particle.wgsl`),
and the full ribbon trail build (`ribbons.rs`). Section 7 feeds the pool; this section
turns it into a mesh. Section 5 wires ribbons into the fx render.

Coordinate note: benilla is **Bevy, Y-up, right-handed, camera looks down −Z**.
`wow_to_bevy([x,y,z])` maps WoW (Z-up) axes to Bevy. Nothing here is GPU-instanced —
see §7 (Grouping/upload), a major correction to the task's hypothesis.

Files traced in full:
- `crates/benilla/src/particles/quads.rs`
- `crates/benilla/src/particles/material.rs`
- `crates/benilla/assets/shaders/wow_particle.wgsl`
- `crates/benilla/src/ribbons.rs`
- supporting: `crates/benilla/src/particles.rs` (material recipe, spawn, plugin),
  `crates/benilla/src/particles/sim.rs` (cam basis + expand_quads callers),
  `crates/benilla-formats/src/particles.rs` (blend/flag/cell/overlife defs),
  `crates/benilla-formats/src/ribbons.rs` (RibbonEmitterDef).

---

## 1. expand_quads — head billboard corner math + camera basis

`expand_quads` entry: `quads.rs:70-77`. It rewrites ONE pool's mesh in place. It builds
CPU-side vectors `positions/normals/uvs/colors` (n*4) and `indices` (n*6) — a
`TriangleList`, NOT instances.

### Camera basis (billboard axes)
Derived once per frame in the sim, `sim.rs:233-236`:
```rust
let (_, cam_rot, _) = cam_tf.to_scale_rotation_translation();
let cam_right = cam_rot * Vec3::X;
let cam_up    = cam_rot * Vec3::Y;
let face_normal = cam_up.cross(cam_right).normalize_or_zero(); // toward the camera
```
`cam_up × cam_right`: for identity rotation = `Y×X = (0,0,−1)` = −Z = toward the camera
(Bevy cameras look down −Z). Passed as `CamBasis { right, up, face_normal }`
(`quads.rs:53-58`, `sim.rs:748-752`). A booth-layered emitter substitutes its booth
camera's rotation instead (`sim.rs:252-264`) so glue-scene braziers face the booth cam.

### Head corners (particleType 0/2), `quads.rs:181-206`
`size` from the over-life ramp is the **half-extent** (confirmed `quads.rs:156-158`:
"the reference quad corners are ±1.0 … a vertex sits at `center ± size` and the world
quad edge spans 2·size"). The rendered half is:
```rust
let half = size * def.twinkle(noise) * scale;   // quads.rs:162
```
Corners (no spin), with `(base_r,base_u)=(cam_right,cam_up)` scaled by `half`:
```rust
push_quad(
  [ center - r - u,   // 0 bottom-left
    center + r - u,   // 1 bottom-right
    center + r + u,   // 2 top-right
    center - r + u ], // 3 top-left
  [[u0,v1],[u1,v1],[u1,v0],[u0,v0]]);            // quads.rs:197-205
```
Indices per quad: `[b,b+1,b+2, b,b+2,b+3]` (`quads.rs:177`) = two triangles. Every vertex
normal = `face_normal` (`quads.rs:173`). `cull_mode: None`, so winding is irrelevant.

**±pattern:** `C ± half*cam_right ± half*cam_up`, corners ordered
BL→BR→TR→BL→TR→TL. Full world edge span = `2*half`.

`center` is computed per particle (`quads.rs:151-155`): anchored mode
`anchor + attach_rot * p.pos`; model mode `placement.transform_point(wow_to_bevy(p.pos))`.
Vertices are pushed **anchor-relative**: `positions.push((*c - anchor).to_array())`
(`quads.rs:172`) — the entity transform carries the anchor as its translation, which is
the transparent-pass depth sort key.

### XY-quad head (file flag 0x1000 → `def.xy_quad()`), `quads.rs:104-110`
When set, the head does NOT camera-face; it lies flat in the emitter model-space XY plane
carried by the live placement, with the R(+Z,90°) emitter turn folded in:
```rust
let plane_basis = def.xy_quad().then(|| {
    let s = placement.scale.x.max(1e-4);
    ( placement.rotation * (wow_to_bevy([0.0, 1.0, 0.0]) * s),   // X̂ → +Ŷ
      placement.rotation * (wow_to_bevy([-1.0, 0.0, 0.0]) * s) ) // Ŷ → −X̂
});
```
Head uses `plane_basis.unwrap_or((cam_right, cam_up))` (`quads.rs:183`). NOTE the plane
basis carries its OWN placement scale `s` (stacks with the flag-0x200 `scale` multiply).
Only the HEAD honors XY-quad; the tail always uses the camera basis.

---

## 2. Tail / streak geometry (particleType 1/2), `quads.rs:207-260`

Tail block runs when `def.head_tail >= 1`. World velocity (`quads.rs:222-226`):
```rust
let vel_world = if anchored { frame.attach_rot * p.vel }
                else { placement.rotation * (placement.scale * wow_to_bevy(p.vel.to_array())) };
```
Tail length / vector (`quads.rs:227-232`):
```rust
let t_eff = if def.tail_clamps_to_age() { def.tail_time.min(p.age) } else { def.tail_time };
let tail  = -vel_world * t_eff;
```
- **Tail length formula: `|velocity| · tail_time`**, directed `−velocity` (trails behind).
- `def.tail_clamps_to_age()` = file flag **0x400** (`particles.rs:502-506`): streak grows
  from zero with age (`tail_time` clamped to `p.age`).

Screen-space projection + degenerate fallback (`quads.rs:233-259`):
```rust
let (tr, tu) = (tail.dot(cam_right), tail.dot(cam_up));
let l2 = tr*tr + tu*tu;
if l2 < 7.7e-4 {                       // view-parallel velocity → plain billboard
    let (r, u) = (cam_right*half, cam_up*half);
    push_quad([C-r-u, C+r-u, C+r+u, C-r+u], [[u0,v1],[u1,v1],[u1,v0],[u0,v0]]);
} else {
    let inv_l = half / l2.sqrt();
    let perp = (cam_up*tr - cam_right*tu) * inv_l;   // ⟂ tail in screen space, half-width
    push_quad([ C-perp, C+perp, C+tail+perp, C+tail-perp ],
              [[u0,v1],[u0,v0],[u1,v0],[u1,v1]]);
}
```
- **Degenerate constant: `l2 < 7.7e-4`** (squared screen-projected tail length). Falls back
  to the head billboard (`0x7b33fa`).
- Streak width = `2*half` perpendicular in screen space (`perp` has magnitude `half`).
- **U runs along the tail**: U=0 at the particle head (`center` end), U=1 at the tip
  (`center+tail`). V spans the cell band across the width.
- Tail reads its OWN independent flipbook cell `ol.tail_cell` (`quads.rs:221`), NOT the
  head's — a second authored ramp (see §5).

---

## 3. Twinkle — draw-gate + size multiply + LUT

LUT: `TWINKLE_LUT` 128 f32 in [0,1), seeded once (`quads.rs:14-24`).
Noise sample (`quads.rs:30-33`):
```rust
let idx = ((twinkle_speed * age).clamp(0.0, 255.0) as u32).wrapping_add(phase) as usize & 0x7f;
TWINKLE_LUT[idx]
```
- period 128 (`& 0x7f`), phase = spawn-time random `Particle::phase`, speed =
  `def.twinkle_speed`, ramped by `age`, clamped [0,255] before add.

Draw-gate (`quads.rs:130-136`):
```rust
let noise = twinkle_noise(def.twinkle_speed, p.age, p.phase);
if def.twinkle_percent < 1.0 && noise > def.twinkle_percent { continue; }   // emits NO quad
```
Size multiply — `half = size * def.twinkle(noise) * scale` (`quads.rs:162`), where
(`particles.rs:551-557`):
```rust
pub fn twinkle(&self, noise: f32) -> f32 {
    if (self.twinkle_max - self.twinkle_min).abs() < 1e-6 { 1.0 }              // degenerate → identity
    else { noise * (self.twinkle_max - self.twinkle_min) + self.twinkle_min }
}
```
Both `{0,0}` and `{1,1}` burn steady (min==max ⇒ multiplier 1.0). `twinkle_percent >= 1`
never gates (placed content authors 1.0).

---

## 4. Spin, `quads.rs:186-196`

Applied only when `def.spin != 0.0`, to whichever base (`base_r,base_u` = plane or cam):
```rust
let (sa, ca) = spin_angle(def.spin, p.age, p.phase).sin_cos();
(r,u) = ( (base_r*ca + base_u*sa) * half,
          (base_u*ca - base_r*sa) * half );
```
Angle (`quads.rs:44-51`):
```rust
pub fn spin_angle(spin: f32, age: f32, phase: u32) -> f32 {
    let angle = spin * age;                       // rotation = spin · age
    if angle < 0.0 && phase & 0x20 != 0 { -angle } else { angle }
}
```
- Base angle = `spin · age`. A NEGATIVE-spin emitter counter-rotates exactly its **bit-5**
  half (`phase & 0x20`); a positive spin never splits (byte-verified negate `0x7b2dda`).
  Tests `quads.rs:277-287`.
- On an XY quad the same 2×2 rotation IS the reference's Rodrigues about the plane normal
  (since `normal × base_r == base_u`).

---

## 5. Cell atlas UV, `quads.rs:111-128`

```rust
let (cols, rows) = (def.tile_cols, def.tile_rows);   // both non-zero powers of two
let (inv_cols, inv_rows) = (1.0/cols as f32, 1.0/rows as f32);
let cell_uv = |idx: u16| {
    let cx = f32::from(idx & (cols - 1));                 // COLUMN wraps (pow2 mask)
    let cy = f32::from(idx >> cols.trailing_zeros());     // ROW does NOT wrap
    ( (cx*inv_cols, (cx+1.0)*inv_cols),
      (cy*inv_rows, (cy+1.0)*inv_rows) )
};
```
- `col = idx & (cols−1)` (pow2 wrap-mask), `row = idx >> log2(cols)`. **Column wraps, row
  does not** — an index past the last cell yields `V ≥ 1.0`, handed to the sampler's
  (repeat) addressing (lands back on row 0, not clamped to the final cell).
- UV rect = `(u0,u1)=(col/cols,(col+1)/cols)`, `(v0,v1)=(row/rows,(row+1)/rows)`.

**Head cell vs tail cell over life** — `OverLifeSample` carries TWO indices
(`particles.rs:161-217`): head uses `ol.head_cell` (`quads.rs:182`), tail uses
`ol.tail_cell` (`quads.rs:221`), independent ramps.

Cell index derivation (`CellRamp`, `particles.rs:97-127`):
```rust
// new(begin,end): forward  e>=b : base=b,     span=e-b+1
//                 reverse  e< b : base=b+1,   span=e-b-1   (plays backwards, shipped)
pub fn sample(&self, t: f32) -> u16 { ((( self.base as f32 + self.span as f32 * t ).floor() as i32) & 0xFF) as u16 }
```
- `floor(base + span·t) & 0xFF` — **mod-256 wrap** (NOT a clamp into the atlas; the atlas
  walk masks the column). `t` carries the endpoint inset `t*0.99 + 0.005` and per-segment
  `repeat` cycling from `OverLife::sample` (`particles.rs:175-217`): two linear segments
  split at `mid`, cells cycle `fract(t·repeat)` while colour/size do not.

---

## 6. Blend / material mapping

### M2 blend → ParticleBlend, `particles.rs:634-640`
```rust
fn blend_of(v: u8) -> ParticleBlend {
    match v { 3 | 4 => Add,               // NoAlphaAdd / Add → (SRC_ALPHA, ONE)
              2 | 5 | 6 => Alpha,          // Alpha, and mod/mod2x FOLD to alpha for now
              _ => Opaque }                // 0 Opaque / 1 AlphaKey
}
```
Note: 5/6 (mod/mod2x) currently fold to alpha (`particles.rs:56-67`) — NOT the task's
"2/5/6 → alpha/mod/mod2x". Only rain's Mod2x has a real path (material.rs, below).

### ParticleBlend → Bevy AlphaMode, `particles.rs:628-632` (identical in ribbons.rs:138-142)
```rust
let alpha_mode = match def.blend {
    ParticleBlend::Add    => AlphaMode::Add,
    ParticleBlend::Alpha  => AlphaMode::Blend,
    ParticleBlend::Opaque => AlphaMode::Opaque,
};
```
Material recipe `particle_material` (`particles.rs:622-654`): `StandardMaterial` base with
`base_color_texture = Some(texture)`, `unlit: true`, `cull_mode: None` (never backface),
+ `WowParticleExt`. Fog policy (`particles.rs:633-639`):

> **2026-08-04 MSUI regression guard:** `cull_mode: None` is unconditional particle-pipeline state,
> not a per-emitter/two-sided flag. Projected velocity tails can have the opposite winding from head
> billboards. Inheriting world-mesh back-face culling left Blizzard heads/shards visible while silently
> removing every submitted tail-only `FROST3.BLP` quad. The user visually confirmed that disabling culling
> for the whole spell-particle pass restores those tails. Production pins this through
> `SpellParticleTrailLaw.CullBackFaces == false` and restores the previous GL state after drawing.

```
flags & 0x8 (unfogged) → 0.0 ;  Add → 2.0 (fog toward BLACK) ;  else → 1.0 (scene fog)
```
`mod2x: false` for all particles/ribbons (rain-only).

### Fragment (`wow_particle.wgsl`)
- `base_color = white × tex (Rgba8Unorm, gamma bytes, NOT decoded on sample) × vertex
  colour (raw authored track values)` — a **gamma-space product**, no CPU premultiply
  (`wgsl:79-83`).
- Add mode: `#ifdef PREMULTIPLY_ALPHA` (pipeline sets it for `AlphaMode::Add`) →
  `final_color = vec4(rgb * c.a, 1.0)` — alpha folded into rgb in GAMMA space; else
  `final_color = vec4(rgb, c.a)` (`wgsl:117-123`).
- Fog: scene day-night fog applied in gamma space before blend when
  `fog_color.w>0.5 && wow_ext_params.x>0.5`, colour chosen by policy (scene / black / white
  / rain-grey) (`wgsl:91-101`); forced rain fog toward grey-0.5 when `params.y>0.5`
  (`wgsl:105-110`); hard far-clip discard `clip_z > fog_params.w` (`wgsl:73-78`).
- **Depth**: `AlphaMode::Add`/`Blend` place the draw in Bevy's transparent pass —
  depth TEST on, depth WRITE off (the reference's `0x12=0`; material.rs:38-40 states it).
  Draw order = transparent pass, after opaque units + effect meshes.
- Mod2x override (rain only, `material.rs:61-90`): pipeline `specialize` swaps blend to
  `color:(Dst, Src, Add)` = `2·src·dst`, `alpha:(Zero, One, Add)`.

---

## 7. Grouping / upload / draw — CORRECTION: NOT instanced

The task hypothesized an instance buffer (center/size/color/cell-rect) +
`DrawArraysInstanced TriangleStrip 4`. **benilla does not do this.** The actual scheme:

- Each `ParticleEmitter` (and each `ChildEmitter`, and each `RibbonTrail`) owns ONE
  `Handle<Mesh>` + ONE `Handle<WowParticleMaterial>` (`particles.rs:567-608`,
  `particles.rs:684-694`). No grouping by (texture, blend) across emitters — the material
  is per-emitter, built from that emitter's own def blend + texture.
- Every frame `expand_quads` REWRITES the whole mesh (`quads.rs:263-267`): four vertex
  attributes (`POSITION`, `NORMAL`, `UV_0`, `COLOR`) + `Indices::U32`. Topology is
  `PrimitiveTopology::TriangleList` (`particles.rs:373-379`). Each particle emits 4 verts +
  6 indices for the head, and (if tail) another 4+6 for the streak.
- Vertices are anchor-relative; the entity `Transform` translation carries the anchor
  (`sim.rs:731-742`), which is the transparent-pass sort key. Within one cloud, quad order =
  pool order — the reference's global per-particle painter sort is a named simplification
  (`quads.rs:167-171`).
- `NoFrustumCulling` on every emitter/trail entity (moving AABB). Draw is a plain Bevy
  `Mesh3d` + `MeshMaterial3d<WowParticleMaterial>`; `MaterialPlugin::<WowParticleMaterial>`
  registered (`particles.rs:780`). System order: `PostUpdate`, in `BillboardPlace` set,
  AFTER `billboard_joint_palette` (`particles.rs:788-798`, ribbons `ribbons.rs:377-383`).
- Gating: geometry-model emitters draw NO quads (handled by `model::update_model_particles`);
  a non-resident texture → `clear_particle_mesh` (draws nothing) (`sim.rs:753-761`).

**Port implication:** per-emitter dynamic vertex+index buffers, `DrawIndexed` triangle
list, 4 verts / 6 indices per quad (head and tail separately). Not instancing.

---

## 8. RIBBONS (`ribbons.rs`)

`MAX_EDGES = 512` (`ribbons.rs:32`) — backstop; reference ring capacity =
`ceil(rate·lifetime)+2`.

### spawn_ribbon (`ribbons.rs:88-197`)
Args: `owner: Entity`, `use_pivot: bool`, `current_anim: Option<u16>`.
1. `$WOW_NO_PARTICLES` kill-switch → None (`ribbons.rs:99-102`).
2. **Per-sequence visibility gate** (`ribbons.rs:108-118`): if `def.visible_in_anim` is
   `Some`, `anim = current_anim.unwrap_or(0)`; `visible = vis.get(&anim).or(vis.get(&0))
   .unwrap_or(true)`; `!visible → None`. (`None` gate = always shows.)
3. `texture = ribbon.texture.clone()?`.
4. **Degenerate gate on track PEAKS** (`ribbons.rs:123-127`):
   ```rust
   if def.edges_per_second <= 0.0
      || (def.height_above.peak().max(0.0) + def.height_below.peak().max(0.0)) <= 0.0 { return None; }
   ```
   Peaks, not value[0] — a keyed slash born at height 0 must not trip.
5. **local_offset** (`ribbons.rs:128-137`, then `wow_to_bevy(local)` at 185):
   `use_pivot ⇒ position − bone_pivot` (joint owner), else `position` (root owner).
6. Material = shared `WowParticleMaterial` (`ribbons.rs:138-170`): `unlit`, `cull_mode: None`,
   `double_sided: true` (visible both sides), `alpha_mode` from blend, `params.x =
   Add?2.0:1.0` (additive trails fog toward black), `mod2x:false`.
7. Mesh = `TriangleList`, cleared; entity spawns with `Transform::IDENTITY`,
   `NoFrustumCulling`, `RibbonTrail{ owner: Some, edges: VecDeque::new, accumulator:0, age:0 }`.

### simulate_ribbons per frame (`ribbons.rs:201-366`)
`dt = delta.min(0.1)`, `now = elapsed_secs` (`ribbons.rs:216-217`).
- `age += dt; ms = age*1000`; heights sampled on the clip clock:
  `h_above = height_above.sample_ms(ms).max(0)`, `h_below` likewise (`ribbons.rs:232-235`).
- **Owner-gone drain** (`ribbons.rs:240-242`): owner set but transform missing → `owner=None`
  (stops committing, ages edges out, despawns with last edge).
- **Node + cross-section axis** (`ribbons.rs:243-254`):
  ```rust
  let node = owner_gt.transform_point(*local_offset);           // owner · localOffset
  let axis = (owner_gt.rotation() * wow_to_bevy([0,1,0])).normalize_or(Vec3::Y); // bone-local +Y
  ```
  Axis = the bone frame's local **+Y**, captured FRESH each frame from the live bone matrix
  (byte-verified row 1). `head=None && edges empty → despawn` (`ribbons.rs:255-258`).
- **Expire** (`ribbons.rs:261-266`): `while front.born older than edge_lifetime → pop_front`.
- **Gravity sag** (`ribbons.rs:267-273`): `if gravity != 0 { sag = 2·gravity·dt; each edge
  top.y -= sag; bottom.y -= sag; }` — the `2·g·dt` per-edge term.
- **Commit at cadence** (`ribbons.rs:274-286`): only while head alive:
  `accumulator += edges_per_second·dt; if accumulator >= 1 { accumulator = fract; if len<MAX_EDGES
  push_back Edge{ top: node + axis·h_above, bottom: node − axis·h_below, born: now } }`.
  Each edge freezes the width it was born with.

### Strip build (`ribbons.rs:288-364`)
- Texture not resident, or `n < 2` (edges + head) → clear mesh (`ribbons.rs:292-304`).
- **Cell UV** (`ribbons.rs:305-314`): `cell = tex_slot.min(rows*cols−1)` (CLAMPED here,
  unlike particles' column-wrap); `u0=(cell%cols)/cols, u1=(cell%cols+1)/cols,
  v0=(cell/cols)/rows, v1=(cell/cols+1)/rows`.
- Colour: `rgb = color.sample_ms(ms)`, `rgba = [r,g,b, alpha.sample_ms(ms).max(0)]` — raw
  authored, gamma decode in shader (`ribbons.rs:315-318`).
- **Anchor** (`ribbons.rs:325-333`): head node while alive, else midpoint of the newest
  surviving edge `(top+bottom)*0.5`. `entity_tf.translation = anchor`; global published
  directly (post-propagation).
- **Strip order — newest→oldest** (`ribbons.rs:334-350`):
  ```rust
  let push_pair = |t, b, age01| { positions.push(t-anchor); positions.push(b-anchor);
      let u = u0 + (u1-u0)*age01; uvs.push([u,v0]); uvs.push([u,v1]); };
  if let Some((node,axis)) = head { push_pair(node+axis*h_above, node-axis*h_below, 0.0); } // live head first
  for e in edges.iter().rev() { push_pair(e.top, e.bottom, ((now-e.born)/edge_lifetime).clamp(0,1)); }
  ```
  `u = u0 + (u1−u0)·age01` — U slides from `u0` at the head (age01=0) to `u1` at the oldest
  edge; the texture's transparent tail is the fade. V spans the cell band `[v0,v1]`.
- Verts are **anchor-relative** (`t-anchor`, `b-anchor`); normals all `[0,1,0]`; colours all
  `rgba` (`ribbons.rs:351-352`).
- **Index build — TriangleList quad strip** (`ribbons.rs:353-357`): tops at even indices,
  bottoms at odd; `for k in 0..n-1 { b=k*2; [b,b+1,b+2, b+1,b+3,b+2] }`.

### Ribbon material / blend
Same `WowParticleMaterial` + `wow_particle.wgsl` as particles (shares the far-clip discard,
fog, gamma combine). `AlphaMode` from `def.blend`; `params.x = Add?2.0:1.0`; `double_sided`.

---

## Cross-cutting facts (for the C# port)

- **Billboard basis:** `cam_right = camRot·X`, `cam_up = camRot·Y`,
  `face_normal = cam_up × cam_right` (toward camera). Corner = `C ± half·cam_right ±
  half·cam_up`; order BL,BR,TR,TL; indices `[0,1,2, 0,2,3]`; span = `2·half`; `size` is the
  half-extent. `cull_mode: None` so winding is cosmetic.
- **XY-quad (flag 0x1000):** flat plane basis = `placement.rot·wow_to_bevy([0,1,0])·s` and
  `placement.rot·wow_to_bevy([-1,0,0])·s` (the R(+Z,90°) turn: X̂→Ŷ, Ŷ→−X̂), carries its own
  placement scale. Head only; tail always camera-faces.
- **Tail formula:** `tail = −vel_world · t_eff`, `t_eff = flag0x400 ? min(tail_time,age) :
  tail_time`. Length `|vel|·tail_time`, width `2·half` in screen space. Degenerate when
  screen-projected `l2 < 7.7e-4` → plain billboard. U along tail 0→1.
- **Twinkle:** LUT[128], `idx = (clamp(speed·age,0,255)+phase) & 0x7f`. Draw-gate: skip
  particle if `twinkle_percent<1 && noise>twinkle_percent`. Size mult identity when
  `min==max`, else `noise·(max−min)+min`.
- **Spin:** `angle = spin·age`, negated iff `angle<0 && phase&0x20`; rotate corners with a
  2×2 `sin_cos` fold on the base axes.
- **Cell-UV math (particles):** `col = idx & (cols−1)` WRAPS; `row = idx >> log2(cols)` does
  NOT wrap (V may exceed 1 → repeat addressing); cell index `floor(base+span·t) & 0xFF`
  with endpoint inset `t·0.99+0.005`. Head/tail use independent ramps. Ribbons CLAMP the
  slot instead: `cell = tex_slot.min(rows*cols−1)`.
- **Blend map:** M2 `3|4→Add`, `2|5|6→Alpha`, else `Opaque` → Bevy `AlphaMode::Add /
  Blend / Opaque`. Fragment `tex(gamma)·vColour(raw)`, no CPU premultiply; Add folds α into
  rgb in gamma space. Transparent pass, depth test on / write off, after opaque.
- **Upload:** per-emitter dynamic `TriangleList` mesh rebuilt every frame (4 verts + 6 idx
  per quad), anchor-relative verts, entity transform = anchor for depth sort. NOT instanced.
- **Ribbon constants:** `MAX_EDGES = 512`; `edge_lifetime` clamped `≥ 0.25` at load
  (`ribbons.rs:260`); commit at `edges_per_second` via a fractional accumulator; gravity
  sag `2·g·dt` per edge; cross-section axis = live bone-local +Y; strip newest→oldest with
  `u = u0+(u1−u0)·age01`, `age01 = (now−born)/edge_lifetime`.

---

## Underspecified / open items (flag for the port)

1. **`twinkle_percent` byte condition is "thin"** (`particles.rs:412-414`): benilla uses
   `percent<1 && noise>percent`. Consumers may treat `≥1`/`0` as always-draw. Exact
   reference condition not fully pinned.
2. **Spin bit-5 split & twinkle phase** use a spawn-time random `Particle::phase`, NOT the
   reference's particle-pointer hash (`quads.rs:37-43`) — a faithful approximation, not
   bit-exact.
3. **Degenerate tail threshold `7.7e-4`** is benilla's chosen screen-space epsilon; the
   reference's exact fallback epsilon (`0x7b33fa`) is not cited numerically.
4. **Per-cloud quad order = pool order** — the reference's global per-particle painter sort
   is a deliberate simplification (`quads.rs:167-171`); overlapping additive quads within one
   cloud may differ subtly.
5. **Additive composite-space gap** (`quads.rs:141-146`, decision 0148, OPEN): reference sums
   gamma bytes in an LDR framebuffer; benilla sums linear and encodes once. Bonfire-core
   brightness may differ — explicitly "do not fix here again".
6. **Ribbon owner-drain vs reference lifecycle** (`ribbons.rs:52-59`, decision 0206, one OPEN
   half): whether the client polls the emitter active flag before destroy or hard-cuts at
   animation end is unresolved; benilla drains from the owner side.
7. **mod/mod2x particle blends (5/6) fold to alpha** (`particles.rs:637`) — no particle path
   yet; only rain's Mod2x has a real blend state (`material.rs`). If a ported model needs true
   mod/mod2x particles it must be added.


---

# Section 09 — Ground-decal projection (flat AoE rings: Frost Nova / Arcane Explosion)

Scope: how benilla drapes an M2 effect's flat ground-plane quads onto the terrain as **projected
surface decals** instead of drawing them as free flat quads. This is the lane that produces the
Frost Nova / Arcane Explosion ground rings. Files traced in full:

- `crates/benilla/src/ground_fx.rs` (this lane: spawn / per-frame update / frame fit / bilerp)
- `crates/benilla/src/decal.rs` (shared projector: `DecalFrame`, `project_decal`, `clip_to_frame`, `seed_mesh`)
- `crates/benilla/src/entities/spell_fx.rs` (`ground_part_material`, the spawn call site, ibp/joint wiring)
- `crates/benilla/src/collision.rs` (`GroundDecalSurface` marker — the receiving-surface set)
- `crates/benilla-formats/src/models/types.rs` (`GroundQuad`, `RenderSubmesh::ground_quad` detector)
- `crates/benilla-assets/src/coords.rs` (`wow_to_bevy` — the up-axis remap, critical for the Z-up port)
- `crates/benilla/src/model_render.rs` / `crates/benilla/src/assets/mod.rs` (cull_mode / blend derivation)

---

## 0. Coordinate frames — READ THIS FIRST (the porter must remap up-axis)

benilla is **Bevy, Y-up**: the ground plane is **XZ**, up is **+Y**. The C# target is **Z-up**:
ground plane **XY**, up **+Z**.

The quad corners are authored by the M2 in **WoW model space** (`GroundQuad.corners`,
`crates/benilla-formats/src/models/types.rs:328-330`):

> "The corners in **model space (WoW axes, z = 0)**, in bilinear rect order:
> `(min_x, min_y)`, `(max_x, min_y)`, `(min_x, max_y)`, `(max_x, max_y)`."

WoW is **Z-up** (`coords.rs:5`: "+X north, +Y west, +Z up"), so the quad is authored in the WoW
**XY plane (z=0)** — which is EXACTLY the C# Z-up ground plane. benilla then rotates it into Bevy via
`wow_to_bevy` (`coords.rs:17-19`):

```rust
pub fn wow_to_bevy(p: [f32; 3]) -> Vec3 { Vec3::new(-p[1], p[2], -p[0]) }   // (x,y,z) -> (-y, z, -x)
```

So a z=0 WoW quad lands in the Bevy **y=0** plane (Bevy up = WoW z). Determinant +1, no mirror, so
winding is preserved (`coords.rs:6-7`).

**Porter remap rule:** the C# engine is already Z-up like WoW, so the quad corners need NO
`wow_to_bevy` rotation — keep them in WoW/Z-up coords (or your own WoW→engine transform). Everywhere
benilla drops the vertical component `.y` and works in the horizontal `(.x, .z)` plane, the C# port
drops `.z` and works in `(.x, .y)`; benilla's vertical slab along **+Y** becomes the slab along **+Z**;
the emitted flat normal `[0,1,0]` (Bevy up) becomes `[0,0,1]`.

---

## 1. `spawn_ground_fx_decal` — inputs and initial entity

`crates/benilla/src/ground_fx.rs:62-101`. Called from `attach_effect_visuals`
(`entities/spell_fx.rs:413-434`) once per ground-quad part of a **ground-anchored** instance.

Inputs:
- `material: Handle<WowModelMaterial>` — the per-instance decal material (already carries the depth
  bias + any animated-tint clone; see §6).
- `quad: &GroundQuad` — the 4 authored corners + their 4 UVs + the `bone` index (`types.rs:322-333`).
- `joint: Entity` — the live rig joint whose pose animates the quad.
- `ibp: Mat4` — the bone's inverse bindpose (identity for a boneless model).

Wiring at the call site (`entities/spell_fx.rs:417-426`): the joint is `joints[quad.bone]` and the
ibp is `binds[quad.bone]` (`SkinnedMeshInverseBindposes`); a boneless model falls back to
`(root, Mat4::IDENTITY)`.

Body — corner prep + handedness normalization (`ground_fx.rs:70-83`):

```rust
let mut corners = quad.corners.map(wow_to_bevy);
let mut uvs = quad.uvs;
let ex = corners[1] - corners[0] + corners[3] - corners[2];
let ez = corners[2] - corners[0] + corners[3] - corners[1];
if ez.z * ex.x - ez.x * ex.z < 0.0 {        // horizontal-plane cross(ex,ez).y < 0
    corners.swap(0, 2); corners.swap(1, 3);
    uvs.swap(0, 2);     uvs.swap(1, 3);
}
```

This is a one-time 2D cross product **in the horizontal plane** (Bevy XZ): it guarantees the fitted
frame's `+z'` (UV `t`) axis matches the quad's authored `+y` edge, so the bilerp is never flipped.
Runtime joint transforms are proper rotations × positive scale, so the sign is invariant after this
one fix (`ground_fx.rs:72-75`). **C# note:** the cross becomes `ex.x*ez.y - ex.y*ez.x < 0` (drop Z).

Entity spawned (`ground_fx.rs:84-100`): a **world-root** entity (absolute placement, no parent) with
`Mesh3d(seed_mesh())`, `MeshMaterial3d(material)`, `Transform::default()`, **`Visibility::Hidden`**
(first placement pass reveals it), and **`NoFrustumCulling`** (mesh is rewritten in place each frame
and Bevy won't recompute the Aabb on asset change).

`seed_mesh` (`decal.rs:27-38`): one degenerate triangle — 3 verts at origin, `NORMAL=[0,1,0]`,
`UV=[0.5,0.5]`, `COLOR=[1,1,1,1]`, indices `[0,1,2]`. Just establishes the vertex-buffer layout; the
first `project_decal` overwrites every attribute.

The `GroundFxDecal` component stores `{ joint, ibp, corners (normalized), uvs }`
(`ground_fx.rs:43-56`).

---

## 2. `update_ground_fx_decals` — per-frame pose of the 4 corners

`crates/benilla/src/ground_fx.rs:145-197`. Runs in `BillboardPlace` — **post-propagation**, so
`joint.affine()` is THIS frame's global pose (`ground_fx.rs:140-142`).

Per decal:
1. `joints.get(decal.joint)` fails (joint despawned with its effect instance) → `despawn()` the decal
   (`ground_fx.rs:160-163`) — the orphan rule.
2. **The pose math** (`ground_fx.rs:164-165`):

```rust
let pose = joint.affine() * Affine3A::from_mat4(decal.ibp);
let corners = decal.corners.map(|c| pose.transform_point3(c));
```

So each corner's world position is `joint_global × ibp × corner`. This is EXACTLY the skinned-vertex
transform (`GroundQuad` doc, `types.rs:324-326`; component doc `ground_fx.rs:48-49`): a rigidly
skinned vertex with a single full-weight bone `b` computes `world = joint_global[b] · ibp[b] ·
v_bind`. `ibp` maps the bind-pose corner into bone-local space; `joint_global` maps bone-local back to
the current animated world pose. Including `ibp` is what makes the authored slide/spin/scale animation
come out right — omit it and the corner would be double-transformed.

**How the ring EXPANDS:** the 4 `corners` are **static** (authored once at spawn). All growth comes
from the **joint's animated scale**: `joint.affine()` for a Frost Nova bone carries the outward scale
keyframes, so `pose.transform_point3(corner)` pushes the static corners radially outward each frame —
the ring blooms. (Same mechanism for slide/spin: it's all in the joint's global transform.)

3. `fit_frame(&corners)` → frame (§3); if `Some`, run `project_decal` (§4). On success, write the
   absolute transform directly (`ground_fx.rs:182-188`):

```rust
tf.translation = frame.center;  tf.rotation = Quat::IDENTITY;  tf.scale = Vec3::ONE;
*global = GlobalTransform::from(*tf);   // propagation already ran — direct global write renders
```

(The emitted mesh positions are stored **relative to `frame.center`**, so the transform just places
them at the center — see §4.)

4. Visibility: `Visible` iff `project_decal` returned `true`, else `Hidden` (`ground_fx.rs:191-195`)
   — the mid-air / no-ground gate (§5).

---

## 3. `fit_frame` — the projection frame fitted to the posed rectangle

`crates/benilla/src/ground_fx.rs:107-131`. Produces a `DecalFrame` (a yaw-rotated horizontal
rectangle × a vertical slab; `decal.rs:45-56`).

```rust
let center = (corners[0] + corners[1] + corners[2] + corners[3]) * 0.25;
let ex = (corners[1] - corners[0] + corners[3] - corners[2]) * 0.5;   // averaged +x edge
let ez = (corners[2] - corners[0] + corners[3] - corners[1]) * 0.5;   // averaged +y edge
let exh = Vec2::new(ex.x, ex.z);                                      // HORIZONTAL only (drop up-Y)
let (half_x, half_z) = (exh.length() * 0.5, Vec2::new(ez.x, ez.z).length() * 0.5);
if half_x < 1e-3 || half_z < 1e-3 { return None; }                   // degenerate / edge-on / scale-0
let d = exh / (half_x * 2.0);                                         // normalized horiz +x edge dir
let vert = 2.0 * half_x.max(half_z);                                  // vertical slab half-height
Some(DecalFrame { center, sin: -d.y, cos: d.x,
    min_x: -half_x, max_x: half_x, min_z: -half_z, max_z: half_z, min_y: -vert, max_y: vert })
```

- **center** = mean of the 4 posed corners.
- **frame basis / rotation:** `(cos, sin) = (d.x, -d.y)` where `d` is the normalized **horizontal**
  projection of the `c0→c1` edge. `DecalFrame::in_frame` computes
  `x' = dx·cos − dz·sin`, `z' = dz·cos + dx·sin` (`decal.rs:60-63`), which sends the posed `c0→c1`
  edge onto the frame's `+x'` axis and the `c0→c2` edge onto `+z'` (`ground_fx.rs:119-121`,
  `fit_frame` test `ground_fx.rs:206-234` pins c0→(min_x,min_z), c3→(max_x,max_z)).
- **extents:** `half_x`/`half_z` are HALF the averaged **horizontal** edge lengths (the up component
  of the edge is discarded — a tilted quad projects its horizontal footprint).
- **vertical slab:** `±vert = ±2·max(half_x, half_z)` about `center.y` (§5).
- Returns **None** for a degenerate pose (the scale-0 first animation frame, or an edge-on tilt where
  the horizontal projection collapses) → decal hidden.

**C# remap:** `exh = (ex.x, ex.y)`, `half_z` uses `(ez.x, ez.y)`; the slab is along `center.z`; the
in-frame rotation is about the up axis (Z).

---

## 4. `project_decal` — re-emitting terrain/WMO triangles as decal geometry (THE WINDING)

`crates/benilla/src/decal.rs:113-174`. This is the shared projector (also used by the selection ring
and blob shadow). It does **NOT** draw the quad; it gathers the real ground triangles inside the
frame's box and re-emits them with the quad's UVs bilerped on top.

Algorithm:
1. `gather = frame.gather_aabb()` — the world-AABB bounding the rotated box (`decal.rs:76-102`).
2. Reject if the frame is zero-area (`decal.rs:122-124`).
3. For each `GroundDecalSurface` collider (`decal.rs:129-158`): it's a static world-space trimesh
   (identity pose, so local == world). Broad-phase against `gather` via the trimesh BVH
   (`trimesh.bvh().intersect_aabb(&gather)`), then per candidate triangle:

```rust
let tri = trimesh.triangle(i);
let poly = clip_to_frame([tri.a, tri.b, tri.c], frame);   // Sutherland-Hodgman, 6 half-planes
if poly.len() < 3 { continue; }
let base = positions.len() as u32;
for p in &poly {
    let d = *p - frame.center;
    positions.push([d.x, d.y, d.z]);                      // RELATIVE to center (tf places them)
    let (u, v) = frame.in_frame(*p);
    uvs.push(uv(u, v));                                    // ground_fx passes the bilerp closure
    let a = alpha(Vec3::new(u, d.y, v));                  // vertical-fade alpha (§5)
    colors.push([1.0, 1.0, 1.0, a]);
}
for k in 1..poly.len() as u32 - 1 {                       // fan-triangulate the convex polygon
    indices.extend([base, base + k, base + k + 1]);
}
```

The UV closure passed by the ground-fx lane (`ground_fx.rs:176-180`) computes normalized
`(s, t) = ((x−min_x)/(max_x−min_x), (z−min_z)/(max_z−min_z))` and `bilerp_uv(&decal.uvs, s, t)`
(`ground_fx.rs:134-138`) — bilinear interpolation of the quad's **four authored corner UVs**. So the
crescent/ring texture is stretched across the frame rectangle and painted onto whatever terrain
triangles fall inside.

4. If nothing was gathered → return `false` (§5). Otherwise rewrite the mesh: `NORMAL` = `[0,1,0]`
   for every vertex (`decal.rs:165-168`), plus POSITION/UV/COLOR/indices (`decal.rs:169-172`).

### THE WINDING — why benilla always yields a full 360° ring

- The emitted triangle order is **the source terrain/WMO triangle's own order**: `tri.a, tri.b, tri.c`
  (`decal.rs:139-140`) → Sutherland–Hodgman clip preserves orientation (`clip_to_frame`,
  `decal.rs:181-213`) → fan triangulation `[base, base+k, base+k+1]` preserves orientation
  (`decal.rs:153-156`). **The quad's own 2-triangle winding is never used** — benilla throws the
  quad's triangles away and keeps only its frame + UVs.
- Terrain / WMO ground triangles are wound so their **front face points up** (Bevy +Y). The gameplay
  camera views the ground from **above**, so every emitted decal triangle is front-facing → survives
  back-face culling → **the entire ring draws, all 360°, regardless of view azimuth**.
- The projection does **NOT** depend on camera side or on a per-triangle normal-direction test: the
  emitted `NORMAL` is a constant `[0,1,0]` (`decal.rs:166-167`) and is unrelated to cull (cull is
  decided by screen-space winding of the actual positions). There is no "front/back of the ring" —
  the geometry is real up-facing ground.
- Cull state of the decal **material**: derived from the M2 batch's `0x04` two-sided flag —
  `cull_mode = if two_sided { None } else { Some(Face::Back) }` (`model_render.rs:191-192`,
  `assets/mod.rs:306-308`). So benilla's ring is **not** guaranteed `cull_mode: None`; it does not
  need to be, because the emitted terrain geometry is always camera-facing from above. (Contrast the
  blob shadow and particles, which ARE explicitly `cull_mode: None`, `blob_shadow.rs:133`,
  `particles.rs:645` — because those are camera-facing billboards, not ground-projected geometry.)

**Conclusion:** benilla emits a full ring because it re-projects genuine up-facing terrain triangles
(one consistent winding, always facing a top-down camera). It does not emit both windings, and it
does not rely on a double-sided material.

---

## 5. The vertical slab + hide-when-no-surface

- **Slab extent:** `±vert = ±2·max(half_x, half_z)` about `center.y` (`ground_fx.rs:117`,
  `min_y=-vert, max_y=vert` at `:129`). So the box reaches 2× the larger horizontal half-extent both
  **above and below** the quad plane — a ledge/wall a bit above or below still catches a fading smear.
  The clip's vertical half-planes are `(center.y + max_y) − p.y` and `p.y − (center.y + min_y)`
  (`decal.rs:190-191`).
- **Vertical fade (alpha trapezoid)** (`ground_fx.rs:173-175`):

```rust
|p| ((vert - p.y.abs()) / (0.75 * vert)).clamp(0.0, 1.0)
```

Full alpha (=1) for `|p.y| ≤ 0.25·vert`, then linear ramp down to 0 at `|p.y| = vert`. A wall/ledge
smear dims with height instead of ending in a hard clip. (`p.y` here is `d.y`, the vertical offset
from the quad plane — see `alpha(Vec3::new(u, d.y, v))`, `decal.rs:150`.)

- **Hide when no receiving surface (mid-air gate):** `project_decal` returns `false` when the gather
  produced no triangles (`positions.is_empty()`, `decal.rs:159-161`) — this is the reference's own
  no-ground gate (`decal.rs:110-112`, `0x6d74b5`: the whole draw is skipped). `fit_frame` returning
  `None` (degenerate/edge-on/scale-0) also yields `false`. Either way
  `update_ground_fx_decals` sets `Visibility::Hidden` (`ground_fx.rs:191-195`). So a decal cast in the
  air (no terrain/WMO in its ±vert slab) simply doesn't render until ground enters the box.

**C# remap:** slab is along Z; the fade uses `d.z`; the box half-planes clamp `p.z` about `center.z`.

---

## 6. Depth bias, blend mode, render order

- **`GROUND_FX_DEPTH_BIAS = 8192.0`** (`ground_fx.rs:40`). Applied in `ground_part_material`
  (`entities/spell_fx.rs:153`): `mat.base.depth_bias = crate::ground_fx::GROUND_FX_DEPTH_BIAS;` — i.e.
  it's set on the `StandardMaterial.depth_bias` (`WowModelMaterial = ExtendedMaterial<StandardMaterial,
  WowModelExt>`, `terrain.rs:31`). The projected decal vertices are **geometrically coplanar** with the
  drawn ground; the bias pushes only their **depth** far above the f32 noise floor so the decal wins
  the LEQUAL depth test against the opaque ground (`ground_fx.rs:35-39`). Same constant/rationale as
  the selection ring's `RING_DEPTH_BIAS`. All three decal lanes write **no depth**, so the bias only
  ever competes with the opaque ground, never with each other.
- **Material is a per-instance clone.** `ground_part_material` (`entities/spell_fx.rs:144-163`):
  clones the part's resolved `WowModelMaterial`, sets `depth_bias`, and — if the part has an M2Color
  RGB animation — sets `mat.extension.tint` and registers it for per-frame tint ticks (`MatAnim`).
  Per-part `alpha_anim` (`MatAnim::driving_tag`) is attached to the decal entity for the animated
  render-alpha loop (`entities/spell_fx.rs:440-446`).
- **Blend mode:** inherited from the part's authored M2 batch. Spell FX glow batches are **additive**:
  `is_additive` → `AlphaMode::Blend` in the transparent pass, and `specialize` overrides the pipeline
  blend STATE to a **pure `(ONE, ONE)` add** (the shader pre-folds radial alpha into colour in gamma
  space — `terrain.rs:230-254`, `model_render.rs:167-190`). Non-additive parts keep Blend / Mod /
  Mod2x / Mask per their flags. So a typical AoE ring is additive.
- **Render order:** transparent pass (Blend), depth **test** LEQUAL on, depth **write** off (the decal
  writes no depth; `ground_fx.rs:38`). The depth bias is what settles coplanarity vs the opaque ground.

---

## 7. Why this differs from a naive flat quad — and the concrete half-ring fix for the C# port

**What the C# port does now:** draws the quad's 4 corners as a flat 2-triangle mesh on the ground.
**Symptom:** only ~180° of the ring shows.

**benilla's true difference:** it never draws the quad. It (a) fits a frame to the posed quad, then
(b) re-emits the actual **terrain/WMO triangles** under it with the quad's UVs bilerped on. This
matters for **draping over slopes, steps and ledge faces** (projective texturing down a vertical
face). But on **flat ground** the projected geometry is essentially the same flat rectangle a naive
quad would be — so the terrain projection is **not** required to fix the half-ring. The half-ring is a
**winding/cull** bug, independent of projection.

**Root cause of the 180° ring (most likely):** the flat quad's two triangles have **opposite
screen-space winding** (or the whole quad is wound facing **down**, away from a top-down camera), so
with back-face culling on, exactly one triangle (half the quad ≈ half the ring texture) is culled.
benilla avoids this entirely because its emitted geometry is real up-facing terrain with a single
consistent winding.

**Concrete recommendation (pick any; #1 is the zero-risk fix):**

1. **Disable back-face culling on the decal material (two-sided / `cull_mode = None`).** This
   guarantees the full 360° ring regardless of triangle winding or camera side. Because these rings
   are **additive** and flat, drawing both faces is visually identical to drawing one — no downside.
   This is the smallest, safest change. (benilla itself relies on always-up-facing geometry rather
   than a two-sided material, but for a *flat quad* port, two-sided is the direct equivalent guarantee.)

2. **Fix the winding** so both triangles are CCW as seen from above (**+Z up** in the Z-up engine).
   With corners in bilinear order `[0]=(min,min) [1]=(max,min) [2]=(min,max) [3]=(max,max)`, emit two
   triangles that are both front-facing from +Z, e.g. `(0,1,3)` and `(0,3,2)` (verify against your
   engine's front-face convention — D3D/Unity default front = **clockwise** when viewed from +Z, so
   you may need `(0,3,1)` / `(0,2,3)`). Test by confirming a single ring shows a complete circle from
   the default top-down camera.

3. **Emit both windings** (6 indices → 12, duplicating each triangle reversed). Equivalent to #1 but
   heavier; only needed if you cannot toggle cull state per material.

For faithful slope-draping later, the port would need benilla's full projector (gather terrain
triangles in the frame box, Sutherland–Hodgman clip, bilerp the quad UVs, depth-bias). But that is a
**separate visual upgrade** — it is not what fixes the half ring. Fix the ring first with #1/#2.

---

## Cross-cutting quick-reference

- **Pose math:** `corner_world = joint_global.affine() · ibp · corner_bindpose` (`ground_fx.rs:164-165`)
  — the exact single-bone full-weight skinned-vertex transform. Corners are static; the joint's
  animated **scale** grows the ring. Boneless model ⇒ `ibp = IDENTITY`, joint = instance root.
- **Winding / two-sided:** benilla emits **terrain triangles' native winding** (up-facing), never the
  quad's; cull is `Some(Face::Back)` unless the M2 batch is `0x04` two-sided. Full 360° ring comes
  from real up-facing ground under a top-down camera, not from a double-sided material.
- **Depth bias:** `GROUND_FX_DEPTH_BIAS = 8192.0`, set on `StandardMaterial.depth_bias`
  (`entities/spell_fx.rs:153`); decal writes no depth, tests LEQUAL, coplanar-safe with the ground.
- **Blend:** additive `(ONE,ONE)` in the transparent pass for glow rings; per-instance material clone
  carries the bias + animated tint/alpha.
- **Up-axis:** benilla Bevy up = **+Y**, ground = XZ; WoW/C# up = **+Z**, ground = XY. Quad is authored
  in WoW z=0 = C# XY plane — the C# port should NOT apply `wow_to_bevy`; swap every benilla `.y`
  vertical op for `.z` and every horizontal `(.x,.z)` for `(.x,.y)`.
- **Half-ring fix:** set the decal material two-sided / `cull_mode = None` (or fix the 2-triangle
  winding to be consistently front-facing from +Z). Terrain projection is NOT needed to get a full
  ring on flat ground.


---

# Section 10 — the MISSILE pipeline (spawn, launch, homing, arrival)

Traced files (read in full):
- `crates/benilla/src/entities/missile.rs` — spawn/queue, launch, per-frame homing, arrival, fallbacks, sound.
- `crates/benilla/src/creature_anim/spell_visual.rs` — where `MissileSpawn` is built on the Speed>0 GO branch, all DBC field resolution.
- `crates/benilla/src/entities/spell_fx.rs` — `attach_effect_visuals` (`preferred_anim=Some(144)`), `arm_effect_rig`.
- Supporting: `crates/benilla-formats/src/spell_visual.rs` (`MISSILE_ATTACH_TABLE`, `VisualStages`), `crates/benilla-assets/src/model/anims.rs` (`preferred_clip`), `crates/benilla-assets/src/coords.rs` (`wow_to_bevy`), `crates/benilla/src/creature_anim/driver.rs` (`oneshot_is_live`), `crates/benilla/src/entities/equipment.rs` (`BoneAttach`).

Bevy is Y-up; positions/directions come from WoW via `wow_to_bevy(p)=Vec3::new(-p[1],p[2],-p[0])` (`crates/benilla-assets/src/coords.rs:17-19`). Missiles operate entirely in already-transformed Bevy world space (they read `GlobalTransform` of joints), so no per-frame WoW conversion happens inside the missile code — only the model-forward convention (WoW +X → Bevy −Z) matters, see §6.

---

## 1. Spawn / queue — when a `MissileSpawn` is created and the fields it carries

### 1a. The Speed>0 gate (Section 4 orchestration, in the router)
The router branches the GO's target lists on `Spell.dbc` Speed. Speed ≤ 0 plays impacts inline; Speed > 0 emits exactly one `MissileSpawn` for the whole GO (all targets batched).

`crates/benilla/src/creature_anim/spell_visual.rs:681-751`:
```rust
for go in go_targets.read() {
    let Some(display) = spells.catalog.get(go.spell_id) else { continue; };
    let wv = weapon_src.caster(go.caster);           // ranged-weapon fallback SpellVisual, once per GO
    if display.speed <= 0.0 {
        for &target in &go.hits { play_impact(target, ...); }   // inline, no missile
    } else {
        // ... build path / dest_tag / missile_sound / targets / awaits_release ...
        missiles.write(MissileSpawn { caster: go.caster, spell_id: go.spell_id, path,
            ammo_display_id: go.ammo_display_id, dest_tag, speed: display.speed,
            targets, weapon_visual: wv, missile_sound, awaits_release });
    }
}
```
The gate is Speed **alone** (comment `spell_visual.rs:704-706`: "The spawn gate is Speed **alone** … every basic shot spell has no `SpellVisual` row at all and still flies"). Field 6 `hasMissile` is never read (`crates/benilla-formats/src/spell_visual.rs:18-19`).

### 1b. `MissileSpawn` fields (the wire the router hands the missile module)
Definition `crates/benilla/src/creature_anim/spell_visual.rs:119-157`:
- `caster: Entity` — who fired it.
- `spell_id: u32` — the impact event's key.
- `path: Option<String>` — projectile MODEL path. Field 7 (`SpellVisual.missile_model`) `>= 1` → `effect_path(id)`, and `unwrap_or(ERROR_CUBE)` when nonzero-but-unresolvable; `< 1` / no `SpellVisual` row → `None` (falls to ammo). Built at `spell_visual.rs:707-716`:
  ```rust
  let path = stages.and_then(|s| (s.missile_model >= 1).then(|| {
      visuals.0.effect_path(s.missile_model).unwrap_or(ERROR_CUBE).to_string() }));
  ```
  `ERROR_CUBE = "Spells\\ErrorCube.mdx"` (`spell_visual.rs:26`).
- `ammo_display_id: Option<u32>` — the GO's ammo `ItemDisplayInfo` row (used only when `path` is `None`). Comes off `go.ammo_display_id` (`spell_visual.rs:741`).
- `dest_tag: Option<u16>` — the M2 attach tag the missile homes to on the target, from field 9's ordinal through `MISSILE_ATTACH_TABLE`; `None` for an out-of-table ordinal (→ base position). `spell_visual.rs:719-720`:
  ```rust
  let dest_tag = stages.and_then(|s| MISSILE_ATTACH_TABLE.get(s.missile_attach as usize).copied());
  ```
- `speed: f32` — `Spell.dbc` Speed, world units/sec (`display.speed`, `spell_visual.rs:743`).
- `targets: Vec<(Entity, Option<u8>)>` — hits carry `None`, misses carry `Some(code)` (the wire `SpellMissInfo`). Built `spell_visual.rs:724-729`:
  ```rust
  let targets = go.hits.iter().map(|&e| (e, None))
      .chain(go.misses.iter().map(|&(e, code)| (e, Some(code)))).collect();
  ```
- `weapon_visual: Option<u32>` — caster's ranged-weapon fallback `SpellVisual` id, resolved ONCE at GO (`wv = weapon_src.caster(go.caster)`, `spell_visual.rs:687`), carried on the flight and returned in the arrival's `Impact` so a basic shot's impact kit resolves even if the caster despawned.
- `missile_sound: Option<u32>` — field 10 `SoundEntries` id looped whole flight (`spell_visual.rs:723`: `stages.and_then(|s| s.missile_sound)`).
- `awaits_release: bool` — whether the launch waits for the cast animation's release keyframe. `spell_visual.rs:734-736`:
  ```rust
  let awaits_release = resolve_kit(spells, &visuals.0, go.spell_id, |s| s.cast, || wv)
      .is_some_and(|k| k.anim_id.is_some());
  ```
  i.e. the cast kit has a body anim → defer to release; no kit / no anim → launch at GO.

### 1c. `MISSILE_ATTACH_TABLE` (ordinal → tag)
`crates/benilla-formats/src/spell_visual.rs:85-87`:
```rust
pub const MISSILE_ATTACH_TABLE: [u16; 11] = [
    0x14, 0x22, 0x13, 0x15, 0x16, 0x11, 0x17, 0x18, 0x19, 0xf, 0x10,
];
```
(= `KIT_SLOT_TAGS` in kit-field order plus `0xf`/`0x10` at ordinals 9/10.) Fireball uses ordinal → `0x22` = chest (`spell_visual.rs:637-638`).

### 1d. Intake — model key resolved at queue time, then park-or-launch
`spawn_missiles` (`missile.rs:383-446`). For each spawn it resolves the `SpellFx` cache key NOW (warms the async M2 load while the release anim plays), `missile.rs:409-424`:
```rust
let key = match (&spawn.path, spawn.ammo_display_id) {
    (Some(path), _) => { fx.models.entry(path.clone()).or_insert_with(|| DisplayModel {
            handle: ModelHandle::M2(asset_server.load(m2_url(path))), ..empty_shell() }); Some(path.clone()) }
    (None, Some(display_id)) => displays.as_deref().and_then(|d| ensure_ammo_model(&mut fx, d, display_id, &asset_server)),
    (None, None) => None,   // invisible flight
};
```
Then `missile.rs:425-445`: build `QueuedGo { spawn, key, queued: 0.0, saw_oneshot: false }`; if `awaits_release` push onto `PendingMissiles[caster]`, else launch immediately via `launch_world_pos(caster, None, ...)`.

---

## 2. Launch timing — release-keyed launch and the backstops

### 2a. Release markers that fire the visible launch
`RELEASE_IDENTS` (`missile.rs:71`): `[*b"$CSL", *b"$CSR", *b"$CST", *b"$BWR"]` — casting hand left / right / two-hand, and ranged release. These are the anim-event dispatcher's drain arms (`0x5ffbd0` → `0x60c940`). The launch consumes the caster's `AnimSoundEvent` stream; markers are resolved to world points from `BoneAttach.markers` (Section 6 populates them). `missile.rs:450-473`:
```rust
for ev in anim_events.read() {
    if !RELEASE_IDENTS.contains(&ev.ident) { continue; }
    let Some(gos) = pending.0.remove(&ev.entity) else { continue; };
    let Some(launch) = launch_world_pos(ev.entity, Some(ev.ident), &units, &joints) else { continue; };
    for go in &gos { launch_go(go, launch, ...); }
}
```
So the whole queue on that caster drains from the **fired** marker's live bone-ridden position.

### 2b. Backstops when no marker fires
Two, polled in the aging pass `missile.rs:474-518`:
```rust
go.queued += dt;
go.saw_oneshot |= live;   // live = oneshot_is_live(...) on the caster this frame
flush |= (go.saw_oneshot && !live) || (!go.saw_oneshot && go.queued > RELEASE_WAIT_MAX);
```
- **Anim-end flush** — a one-shot was seen live (`saw_oneshot`) and has now ended without ever firing a release keyframe (the client's on-anim-finish `0x5fc920` → `0x60c9b0`; ours polls the edge via `oneshot_is_live`, `crates/benilla/src/creature_anim/driver.rs:97-105`).
- **Never-played timeout** — no one-shot ever started and `queued > RELEASE_WAIT_MAX`. `RELEASE_WAIT_MAX = 0.25` seconds (`missile.rs:83`). This window has no client analogue (the client always gets an anim-finish callback); it exists so a caster whose kit anim never resolved doesn't hang its projectiles forever.
- A caster that streamed out drops its whole queue: `if !units.contains(caster) { return false; }` (`missile.rs:481`).

Both backstops (and an immediate `awaits_release=false` GO) launch through `launch_world_pos(caster, None, ...)`, i.e. the `$CSL → $CSR → $CST → base` cascade with no fired marker (`missile.rs:433, 502`).

### 2c. The marker cascade / launch-point resolution
`MARKER_CASCADE` (`missile.rs:75`): `[*b"$CSL", *b"$CSR", *b"$CST"]`. `launch_world_pos` (`missile.rs:186-206`):
```rust
let point = bones.and_then(|b| {
    fired.into_iter().chain(MARKER_CASCADE)              // fired marker first, then the cascade
        .find_map(|ident| b.markers.get(&ident).copied())
        .and_then(|(bone, offset)| b.joints.get(bone as usize).map(|&j| (j, offset)))
});
Some(match point.and_then(|(j, off)| joints.get(j).ok().map(|g| g.transform_point(off))) {
    Some(p) => p, None => base.translation() })   // else the unit's base translation
```
`BoneAttach.markers: HashMap<[u8;4],(u16,Vec3)>` maps a 4CC event ident → `(bone index, Bevy-space offset)`; the joint's live `GlobalTransform.transform_point(offset)` is the world launch point (`crates/benilla/src/entities/equipment.rs:241-249`). `None` only when the caster entity is gone.

---

## 3. `launch_go` — travel time and initial aim

`missile.rs:314-371`. Per target:
```rust
let dest = go.spawn.dest_tag.into_iter().chain(DEST_FALLBACKS);
let Some(aim) = attach_world_pos(target, dest, units, joints) else { continue; };  // target gone → skip
let arrive_in = launch.distance(aim) / go.spawn.speed.max(f32::EPSILON) - go.queued;
if arrive_in <= 0.0 { arrival_handoff(target, ..., miss, ...); continue; }   // past deadline → impact on the spot
let dir = (aim - launch).normalize_or(-Vec3::Z);
let entity = commands.spawn((
    Missile { spell_id, target, dest_tag, miss, path: go.key.clone(), arrive_in,
              parts_spawned: go.key.is_none(), weapon_visual },
    Transform::from_translation(launch).with_rotation(missile_facing(dir)),
    Visibility::default() )).id();
if let Some(kit_sound) = go.spawn.missile_sound {
    sounds.write(MissileSound::Start { entity, kit_sound, pos: launch });
}
```
Key facts:
- `arrive_in = distance / speed − queued` (`missile.rs:330`). `queued` = seconds this GO sat parked (the arrival deadline was fixed at GO; riding the hand eats flight time). `speed.max(EPSILON)` guards divide-by-zero.
- **Arrive-on-the-spot**: `arrive_in <= 0.0` (melee range / past deadline) → no flight entity, no sound; `arrival_handoff` fires immediately (`missile.rs:331-342`).
- Initial launch position = the marker world point (`launch`, from §2c). Initial aim = target's dest-attach point NOW (§4b resolution). Initial facing `missile_facing(dir)` with `dir = (aim − launch)` normalized (fallback `−Vec3::Z`).
- `parts_spawned` starts `true` when there's no model key (invisible flight — nothing to wait on).

`DEST_FALLBACKS` (`missile.rs:86`): `[0xf, 0x13]` — appended after the spawn's own `dest_tag` (`0xf` then `0x13`=base) — same cascade as the attach-point effects.

---

## 4. `move_missiles` — per-frame homing

`missile.rs:589-631`. Exact update, per missile per frame:
```rust
let dt = time.delta_secs();
let dest = missile.dest_tag.into_iter().chain(DEST_FALLBACKS);
let Some(aim) = attach_world_pos(missile.target, dest, &units, &joints) else {
    sounds.write(MissileSound::Stop { entity }); commands.entity(entity).despawn(); continue;   // target streamed out
};
let to_target = aim - transform.translation;
if missile.arrive_in <= dt {
    arrival_handoff(missile.target, missile.spell_id, missile.weapon_visual, missile.miss, ...);
    sounds.write(MissileSound::Stop { entity }); commands.entity(entity).despawn(); continue;
}
transform.translation += to_target * (dt / missile.arrive_in);   // arrive-on-time homing step
missile.arrive_in -= dt;
if let Some(dir) = to_target.try_normalize() { transform.rotation = missile_facing(dir); }
```
Precise semantics:
- Homing step is **`pos += (aim − pos) · (dt / arrive_in)`** — NOT the `* dt / arrive_in` scalar-per-axis you'd write naively; it is `to_target` (a Vec3) scaled by the scalar `dt/arrive_in`. Same thing: covers `dt/remaining-time` of the current gap each frame. Confirmed at `missile.rs:625`.
- `arrive_in` is decremented by `dt` AFTER the move (`missile.rs:626`). Effective speed re-derives each frame as `|gap| / arrive_in`.
- **As `arrive_in → 0`**: when `arrive_in <= dt` the missile does NOT do the fractional step — it runs `arrival_handoff`, stops its sound, and despawns (children go with it) (`missile.rs:611-624`). It does not snap-translate to `aim` first; arrival is by event, and the last pre-arrival frame already put it within one `dt`-step of the point.
- **Aim re-resolved every frame from the target's LIVE pose**: `attach_world_pos(missile.target, dest_tag then DEST_FALLBACKS, ...)` reads the target's current `GlobalTransform` + `BoneAttach` (`missile.rs:602-603`). A moving target bends the path (homing) and arrival still lands on schedule.
- **"Same body point in practice"**: `dest_tag` is the same tag throughout, so the aim is the same body attachment on the target every frame (a named approximation of the client's ray-vs-bounding-sphere intercept `0x61d230`, module docs `missile.rs:47-48`).
- Facing tracks velocity direction each frame via `missile_facing(to_target.normalize())` (`missile.rs:627-629`).

`attach_world_pos` (`missile.rs:161-179`): reads `BoneAttach.points` (the M2 attachment table, `id → (bone, offset)`), through the tag cascade, `transform_point` on the bone joint's `GlobalTransform`; else the unit base translation. `None` only when the target unit is gone.

---

## 5. `arrival_handoff` — impact vs miss-defense

`missile.rs:277-306`:
```rust
match miss {
    None => impacts.write(CastEvent { entity: target, spell_id,
                kind: CastEventKind::Impact { weapon_visual }, seq: play_seq.next() }),
    Some(code) => if let Some(victim_state) = miss_defense_state(code) {
                     defenses.write(DefenseAnim { victim: target, victim_state }); },
}
```
- **Landed (`miss = None`)** → writes a `CastEvent { CastEventKind::Impact { weapon_visual } }` back to the router (Section 4 `route_cast_visuals` → `play_impact`, which plays stage-1 impact kit + stage-2 state kit). `weapon_visual` is the caster's ranged fallback that rode the flight (caster may be gone). Timestamp `play_seq.next()` (fresh scene tick, sorts after the frame's packet handlers).
- **Miss (`miss = Some(code))** → NO impact kit; plays the victim's defense clip only for DODGE/BLOCK. `miss_defense_state` (`missile.rs:265-271`):
  ```rust
  match code { 3 => Some(2),  // SPELL_MISS_DODGE → the $CPP dispatch's DODGES state
               5 => Some(5),  // SPELL_MISS_BLOCK → BLOCKS
               _ => None }
  ```
  DODGE(3) → victim_state 2, BLOCK(5) → victim_state 5. Every other code (MISS(1)/RESIST/EVADE/IMMUNE/DEFLECT) plays **nothing**, and PARRY(4) → nothing by construction ("a ranged arrival never parries"). Test `missile.rs:810-856` pins `(3→Some(2), 5→Some(5), 1→None, 4→None)`.
- **Target-lost handling**: if the target entity is gone when the aim is resolved (`attach_world_pos` returns `None`), the missile stops its sound and despawns silently — no impact, no defense (`missile.rs:603-608`; also `launch_go` skips a target already gone at launch, `missile.rs:327-329`).

---

## 6. The flying model — transform / orientation / clip / scale

### 6a. Orientation (`missile_facing`)
`missile.rs:96-112`:
```rust
fn missile_facing(f: Vec3) -> Quat {           // f = flight direction, Bevy space
    let side = Vec3::Y.cross(f);
    if side.length_squared() < 1e-6 {          // straight up/down degenerate case
        return Quat::from_rotation_arc(-Vec3::Z, f);
    }
    let up = f.cross(side.normalize()).normalize();
    Quat::from_mat3(&Mat3::from_cols(f.cross(up), up, -f))
}
```
Frame build (client `0x61e2a0`): model **+X = flight direction**; side = up × dir; up re-orthogonalized as dir × side — **NO roll**, model-up stays world-up-ish however flight pitches (avoids rolling the trail ribbons on pitched flight). In Bevy terms: local −Z (= WoW +X image) → `f`; local +Y (= WoW +Z image) → the re-orthogonalized up. Columns of the rotation matrix are the images of local X/Y/Z: X→`f.cross(up)` (=−side), Y→`up`, Z→`−f`. Degenerate vertical uses a shortest-arc `−Z → f`. Threshold `1e-6`.

### 6b. Clip — `INFLIGHT_ANIM = 144`
`missile.rs:94`: `const INFLIGHT_ANIM: u16 = 144;` — `AnimationData.dbc` **InFlight** sequence. Passed as `preferred_anim` into `attach_effect_visuals` in `attach_missile_models` (`missile.rs:561-575`, last arg `Some(INFLIGHT_ANIM)`).

Selection logic `preferred_clip` (`crates/benilla-assets/src/model/anims.rs:163-167`):
```rust
pub fn preferred_clip(&self, preferred: Option<u16>) -> Option<&AnimClip> {
    preferred.and_then(|id| self.find(id)).or_else(|| self.clips.first())
}
```
So: model authors seq 144 → play InFlight (a thrown weapon `Thrown_1H_*.m2` tumbles end-over-end AND keys its trail ribbon's per-sequence visibility ON only here); model lacks 144 (arrows/bullets fly straight, fireball tumbles via a **global** sequence) → fall to file-order-first clip. `arm_effect_rig` arms the same pick, repeating if `clip.looping` (`spell_fx.rs:518-537`). The ribbons' per-sequence visibility keys off the played clip's `anim_id` (`spell_fx.rs:473-489`), so the thrown trail shows in flight, never in the worn hand.

### 6c. Model parts / scale
`attach_missile_models` (`missile.rs:525-580`) waits on the async M2 build then calls the shared `attach_effect_visuals` body:
```rust
attach_effect_visuals(&mut commands, entity, dm, time.elapsed_secs(),
    false,  // NOT ground-anchored: a missile's flat quads are geometry, never decals
    None,   // a missile is a FREE world model — its trail stays world-frozen
    ..., Some(INFLIGHT_ANIM));
```
- `ground_anchor = false` (`missile.rs:566`) — flat quads are ordinary geometry, not projected ground decals.
- `attach = None` (`missile.rs:567`) — emitters treat the missile as a free world model; trail ribbons stay world-frozen (client `part-kit-effect-attach-orient.md`), unlike a unit-attached kit effect which passes `Some(root)`.
- Parts, skinned meshes, billboard cards, emitters, ribbons, material animation all come from the single `attach_effect_visuals` (`spell_fx.rs:297-491`); parts are children of the missile entity so they ride the mover. **No explicit scale** is applied by the missile code — model scale is whatever the M2/DisplayModel shell bakes (`empty_shell()`), same as every effect model. Returns `false` while parts still loading → retried next frame; sets `parts_spawned = true` on success (`missile.rs:561-578`).

---

## 7. Fallback model chain (exact order)

1. **DBC missile effect model** — `SpellVisual` field 7 `>= 1` → `effect_path(id)`; nonzero-but-unresolvable → literal `ERROR_CUBE = "Spells\\ErrorCube.mdx"` (`spell_visual.rs:707-716, 26`). This becomes `MissileSpawn.path = Some(...)`.
2. **Ammo / weapon `ItemDisplayInfo`** — only when `path == None` (field 7 `< 1` / no `SpellVisual` row — every basic shot). `ensure_ammo_model` (`missile.rs:232-257`) resolves the GO's `ammo_display_id` by SHAPE:
   ```rust
   let (dir_name, col) = if row.model[0].is_some() { ("Weapon", 0) } else { ("Ammo", 1) };
   let model = row.model[col].as_ref()?;
   let dir = format!("Item\\ObjectComponents\\{dir_name}");
   // handle = M2(load(m2_url("{dir}\\{model}"))), object_texture = row.model_texture[col]
   ```
   Right slot (`model[0]`) present → thrown weapon in `Item\ObjectComponents\Weapon\` (the weapon itself flies); else left slot (`model[1]`) → arrow/bullet in `Item\ObjectComponents\Ammo\`. Cache key `format!("ammo:{display_id}")` (unique per display so shared-model/different-skin never collide). Returns `None` for unknown display / model-less row.
3. **ErrorCube** — already folded into step 1 (a nonzero unresolvable field-7 id resolves to `Spells\ErrorCube.mdx` in the ROUTER, before the spawn). It is a real MPQ checkerboard cube, not a placeholder-in-code.
4. **Invisible-but-flies** — `path == None` AND ammo unresolved (`(None, None)` or ammo lookup returns `None`) → `key = None` → `Missile.path = None`, `parts_spawned = true` at spawn. The missile carries no model, still integrates the mover, still fires `arrival_handoff` on schedule (`missile.rs:353-354, 544-548`; module docs `missile.rs:40-41`).

Exact fork in intake: `missile.rs:410-424` (`(Some(path),_)` / `(None, Some(display_id))` / `(None, None)`).

---

## 8. Missile sound hook (Section 12 owns playback)

`MissileSound` message (`missile.rs:118-130`):
```rust
enum MissileSound {
    Start { entity: Entity, kit_sound: u32, pos: Vec3 },   // begin loop tracked to the projectile
    Stop  { entity: Entity },                              // projectile gone → stop the loop
}
```
- **Start at launch**: `launch_go` writes `MissileSound::Start { entity, kit_sound: missile_sound, pos: launch }` right after spawning the missile, only if `missile_sound.is_some()` (`missile.rs:363-369`). Born positional at `launch` (the just-spawned Transform may not have flushed), then rides the entity. Independent of the model — an invisible missile still whooshes.
- **Stop at arrival / stream-out**: `move_missiles` writes `MissileSound::Stop { entity }` on arrival, and also when the target streams out mid-flight, immediately before despawning (`missile.rs:606, 621`).
- Source field: `SpellVisual` field 10 → `VisualStages.missile_sound` (`crates/benilla-formats/src/spell_visual.rs:110-113`); a LOOPING whoosh (thrown `WeaponLoop` 3318, fireball `FireMissileLoop`); client per-missile loop handle `CMissile+0x44`. Playback (the actual loop channel + proximity volume `0x61d790`) is Section 12's `crate::sound::missile`.
- Arrive-on-spot (`arrive_in <= 0`) launches emit no `Start` (no flight entity) — the melee cast has no whoosh (`missile.rs:331-342`).

---

## 9. Every constant / magic number

| Name | Value | Location | Meaning |
|---|---|---|---|
| `RELEASE_IDENTS` | `[$CSL, $CSR, $CST, $BWR]` | `missile.rs:71` | anim-event idents that drain the queue (visible launch) |
| `MARKER_CASCADE` | `[$CSL, $CSR, $CST]` | `missile.rs:75` | launch-point marker fallback order (after any fired ident) |
| `RELEASE_WAIT_MAX` | `0.25` (seconds) | `missile.rs:83` | never-played one-shot timeout before flushing the queue |
| `DEST_FALLBACKS` | `[0xf, 0x13]` | `missile.rs:86` | dest-attach fallback tail (after spawn's own tag) |
| `INFLIGHT_ANIM` | `144` (u16) | `missile.rs:94` | `AnimationData.dbc` InFlight sequence the projectile model plays |
| degenerate-vertical threshold | `1e-6` | `missile.rs:104` | `side.length_squared()` below → shortest-arc facing |
| speed guard | `f32::EPSILON` | `missile.rs:330` | `speed.max(EPSILON)` divide-by-zero guard |
| launch dir fallback | `-Vec3::Z` | `missile.rs:343` | when `aim == launch` |
| `MISSILE_ATTACH_TABLE` | `[0x14,0x22,0x13,0x15,0x16,0x11,0x17,0x18,0x19,0xf,0x10]` | `benilla-formats/src/spell_visual.rs:85-87` | field-9 ordinal → dest attach tag |
| `ERROR_CUBE` | `"Spells\\ErrorCube.mdx"` | `spell_visual.rs:26` | unresolvable-field-7 missile model |
| ammo shape dirs | `"Weapon"`/`col 0`, `"Ammo"`/`col 1` | `missile.rs:241-247` | thrown vs arrow/bullet slot rule |
| `miss_defense_state` | `3→2`, `5→5`, else `None` | `missile.rs:265-271` | DODGE→state2, BLOCK→state5, PARRY/others→nothing |
| `wow_to_bevy` | `Vec3::new(-p[1], p[2], -p[0])` | `benilla-assets/src/coords.rs:17-19` | WoW→Bevy basis (positions come pre-transformed to missiles) |

Miss codes referenced: SPELL_MISS_DODGE=3, SPELL_MISS_BLOCK=5, MISS=1, PARRY=4 (`missile.rs:143-144, 265-271`).

---

## 10. Systems / ownership map (for the C# port's scheduling)

- `spawn_missiles` (`missile.rs:383`) — intake `MissileSpawn`; owns `PendingMissiles` queue; reads `AnimSoundEvent` for release markers; runs the aging/backstop poll; calls `launch_go`.
- `attach_missile_models` (`missile.rs:525`) — async model-parts attach once the M2 builds (retries).
- `move_missiles` (`missile.rs:589`) — per-frame homing + arrival + despawn + sound stop.
- Router side: `route_cast_visuals` (`spell_visual.rs:434`) emits `MissileSpawn` on Speed>0 and consumes the returned `CastEventKind::Impact`.

---

## Anything underspecified / named approximations (from module docs `missile.rs:43-51`)

- `$BWR` launches from its own event marker, not the ranged weapon's HandArrow/Bullet attachment (`0x23`/`0x24`) — but the client also falls back to the event position, so close.
- Anim-end flush is a **polled** edge (+ `RELEASE_WAIT_MAX` timeout) where the client has a per-anim finish callback (`0x5fc920`). The `saw_oneshot`→`!live` transition path needs a live driver rig and is not unit-tested (`missile.rs:633-637`).
- Homing aims at the dest attach point, not the client's ray-vs-bounding-sphere intercept (`0x61d230`) — "same body point in practice".
- A missed target's arrival plays its dodge/block clip but the projectile still ENDS there rather than deflecting (client `0x61dd50` bounce not modeled).
- Ground/dest-location arrival path (`0x61d870` — needs the wire's dest position + positional effect spawn) is deferred; a target that streams out mid-flight just ends the flight silently rather than doing the client's dead-handle ground path.
- Misses "fly too" per the router comment but ours fizzle silently on a pure MISS (no visible deflection); only DODGE/BLOCK produce a reaction.
- Missile model **scale** is inherited from the DisplayModel/M2 shell — no explicit per-missile scale factor found in code; if the C# port needs an exact scale it must come from the M2, not this layer.


---

# Section 11 — Body / Unit Animation Driver (benilla → C# port)

The animation state machine that makes a caster/target actually **move** during a spell: the cast
gesture (one-shot over locomotion), the CombatWound flinch (secondary decaying blend), the sustained
cast/channel hold, plus the id→sequence resolver, the animation clock/blend model, global sequences on
units, and the billboard-joint palette rewrite.

Bevy is **Y-up**. WoW bone axes land in the model-local Bevy frame as **X→−Z, Y→−X, Z→+Y** (coords.rs;
cited by billboard.rs:159). Read-only findings; every claim carries file:line.

Files traced (all read in full):
- `crates/benilla/src/creature_anim/driver.rs` (1305 lines — the per-frame system `drive_animations`)
- `crates/benilla/src/creature_anim/driver/play.rs` (playback primitives + one-shot rolls)
- `crates/benilla/src/creature_anim/driver/wound.rs` (CombatWound secondary-blend slot)
- `crates/benilla/src/creature_anim/emote_anim.rs` (SMSG_EMOTE → EmoteAnim)
- `crates/benilla/src/creature_anim/global_seq.rs` (global sequences on units)
- `crates/benilla/src/creature_anim/select.rs` (id→sequence selection/routing/rate)
- `crates/benilla/src/creature_anim.rs` (module glue: the components + consumers wiring)
- `crates/benilla/src/billboard.rs` (BillboardJointRig / billboard_joint_palette)
- Supporting: `crates/benilla-assets/src/model/anims.rs` (AnimClip / ModelAnimations::{find,pick_variation,resolve})
  and `crates/benilla-formats/src/anim_data.rs` (AnimDataCatalog / fallback / weapon_flags).
- Cross-ref writer: `crates/benilla/src/creature_anim/spell_visual.rs` (`route_cast_visuals`, `play_kit`).

---

## 0. Where the driver sits in the frame

`drive_animations` runs in the `Update` schedule, chained after the cast router and emote bridge, and
gated `.after(WorldStage::Net).after(WorldStage::Input).before(EntityVisualsSet)`
(creature_anim.rs:695-732). The chain order matters: `route_cast_visuals` (writes CastHold/EmoteAnim/
WoundAnim) → `arm_*_fx` → `emote_to_anim` → `flourish_to_anim` → **`drive_animations`** →
`drive_hand_grip` → `fire_anim_events` → `route_swing_impacts` → `drive_nock_latch`
(creature_anim.rs:696-724). So the spell components are written the same frame, upstream, and consumed
by the driver immediately.

The per-unit state lives in the **`AnimDriver`** component (creature_anim.rs:478-539). Its fields are the
whole port surface:
- `mode: Mode` — the base-track state machine (Gait / Entering / Looping / Exiting / Land / Swing).
- `gait: Option<u16>` — the currently-targeted gait id (so a change cross-fades, a same id does not).
- `sheath_cur / sheath_byte / sheath_swap` — client-side sheath cache + ceremony.
- `overlay: Option<Overlay>` — the masked upper-body one-shot slot (SpineLow subtree). `Overlay{node,id,looping}` (creature_anim.rs:567-572).
- `wound: Option<Wound>` — the CombatWound secondary-blend slot. `Wound{node,span,masked}` (creature_anim.rs:580-588).
- `jump_arc / was_falling` — airborne arc bookkeeping.
- `deferred: Option<u16>` — the deferred combat one-shot cache (fast-path park).
- `ranged_held: bool` — the ranged Load→Hold twin latch.
- `loop_window: Option<(node, u32)>` — the looping-arm replay-watchdog window.

Base track is driven through Bevy's `AnimationTransitions` (`tr`); overlay/wound slots are driven by raw
`AnimationPlayer::play`/`stop` calls (parallel graph nodes, weighted).

---

## 1. The three spell-written components: EmoteAnim, WoundAnim, CastHold

### EmoteAnim { entity, anim_id: u16, seq: u64 } (creature_anim.rs:342-349)
A one-shot **AnimationData.dbc id** played over the gait, returning to base when finished. Two writers:
1. The spell kit release/impact leg: `play_kit` (spell_visual.rs:348-357) — if the kit's `anim_id` is
   **NOT** 8..=10 it writes an `EmoteAnim`; if it **is** 8..=10 it writes a `WoundAnim` instead
   (spell_visual.rs:349-357). This is the byte-verified branch (kit player `0x60edf0`): wound ids never
   ride the emote route (which replaces the base track), they ride the secondary slot.
2. `SMSG_EMOTE` → `emote_to_anim` (emote_anim.rs:41-80): resolves `Emotes.dbc` id → `AnimID` through the
   shared `EmoteSounds` catalog, gates on `receive_eligible` (suppress only at stand-state 3 SLEEP or
   swimming — emote_anim.rs:86-88), writes EmoteAnim. `/wave`, `/bow`, `/cheer` etc.
3. `MountFlourish` → `flourish_to_anim` (creature_anim.rs:366-387) hops unit→mount child and writes an
   EmoteAnim of `MOUNT_SPECIAL`(94) on the child.

**Consumed** in `drive_animations`: emotes drained into `pending` per-entity as `OneShotReq::Emote(id)`
(driver.rs:234-239), sorted by `seq` alongside swings (driver.rs:531-534), then routed play-by-play. See §3.

### WoundAnim { entity, anim_id: u16 } (creature_anim.rs:397-401)
A **CombatWound-family (8–10)** flinch laid into the wound SECONDARY-blend slot — a decaying overlay that
never interrupts what plays underneath. Written only by `play_kit` (spell_visual.rs:350). **Consumed**:
drained into `pending_wound` as `WoundEdge::Spell(anim_id)` (driver.rs:215-217), then triggered via
`wound_trigger` at driver.rs:1183-1200. The same slot also serves the melee flinch (`WoundEdge::Melee`,
from `SwingImpact` with HitInfo & 0x2, driver.rs:203-214). See §5.

### CastHold { anim_id: u16, spell_id: u32, ranged: bool } (creature_anim.rs:276-286)
The **sustained** pose held while a cast/channel is in flight (SpellVisualKit field-2 of the precast/
channel kit). Written/removed by `route_cast_visuals` via `commands.insert/remove` (spell_visual.rs:552,
583, 663, 779, 806). **Consumed** by the driver every frame as a *component* (Option<&CastHold> queried at
driver.rs:126), not an event — it renders per frame:
- **Stationary caster → gait slot, full-body** (driver.rs:939-946): the hold id pins `cands = [h.anim_id,
  STAND]`, outranking Ready idle / state-emote idle. "Stationary" = `flags & CAST_PIN_MOVE == 0`
  (translation + swim, **never** the turn bits — select.rs:121-129).
- **Moving/swimming caster → looping masked overlay** (driver.rs:1137-1178): a torso-masked loop over the
  gait; `masked_hold` computed at driver.rs:1146-1152 and (re)taken/released at driver.rs:1153-1177.
- A **Special** (jump/pose) outranks the hold (both branches gate `special.is_none()`).

CastHold removal (spell_visual.rs) is keyed on spell_id: on GO / FAIL of the matching spell the router
removes the component. When the component is gone, the driver's overlay branch `(None, Some(ov)) if
ov.looping` stops the node and clears `drv.overlay` (driver.rs:1170-1174).

---

## 2. Animation id → sequence resolution (select.rs + benilla-assets)

A selector produces a **requested AnimationData.dbc id** (a `u16`, e.g. Stand 0, Run 5, Attack1H 17). Two
levels turn that into a concrete Bevy graph node:

### Level A — model fallback resolution (`ModelAnimations::resolve`, anims.rs:202-248)
Returns `ResolvedAnim { id, dir_flags }` (anims.rs:128-136). Three paths (this is the "Fallback vs Exact"
mechanism):
- **PATH 1** (`requested < playable_animation_lookup.len()`, ~203 rows): one dword read of the model's own
  baked **PlayableAnimationLookup** table (anims.rs:203-208). The table row is already the precomputed
  result of the `AnimationData.dbc` Fallback-column walk against **this model's** sequence set — e.g. a
  chicken lacking Attack2H bakes `[18] = 16` (AttackUnarmed). Carries a `dir_flags` high16 (plumbed but
  **not yet consumed** — anims.rs:132-135).
- **PATH 2** (`requested ≥ table length`, table non-empty): `resolve_path2` (anims.rs:228-248) walks
  `catalog.fallback(id)` until `self.find(id).is_some()`, guarded by a 208-entry visited set (cycle
  guard). Exhaustion → Stand(0). Reachable only for the 5 ids ≥203.
- **No table** (empty lookup — cube/malformed M2): degrades to **identity** (anims.rs:209-214).

`AnimDataCatalog::fallback` (anim_data.rs:74-79) returns `None` when fallback is 0/self/unknown, folding
NULL-row + self-fallback + "already Stand" into one dead-end.

The driver never calls `resolve` directly; it goes through two thin wrappers (creature_anim.rs:742-755):
- `resolved_id(anims, id, catalog)` → `anims.resolve(id, cat).id`, or identity when catalog is `None`
  (the brief window before AnimationData.dbc loads — driver.rs:248).
- **`find_resolved(anims, id, catalog)` → `anims.find(resolved_id(...))`** — THE core call. Returns the
  `AnimClip` this model actually plays for a requested id.

### Level B — id → clip → variation (`find` / `pick_variation`)
- `ModelAnimations::find(anim_id)` (anims.rs:144-146): linear scan of `clips`, returns the **head
  variation** (the stable identity to compare/loop on).
- `ModelAnimations::pick_variation(anim_id, roll)` (anims.rs:178-189): the client's `_rand`-weighted walk.
  Iterate clips with matching anim_id in file order; `roll < c.frequency` picks that clip, else
  `roll -= frequency` and advance; **chain exhaustion falls back to the HEAD** (not the last —
  anims.rs:188). `frequency` = `M2Sequence.frequency` @+0x14 (anims.rs:66-69).
- `owns(anim_id)` (anims.rs:153-157): a bounds-checked read of `animation_lookup` vs the 0xffff sentinel —
  a *different* question ("does the model author it at all") used by the GameObject arm, not the play path.

### The seq index / node
Each `AnimClip` (anims.rs:13-76) carries: `anim_id`, `seq_index` (the *file slot*, key into per-sequence
alpha loops), `node: AnimationNodeIndex` (the Bevy graph node the player plays), `looping`, `duration`,
`move_speed` (rate denominator), `blend_time` (cross-fade in), bounds, `events`, `arm_nodes` (per-arm
masked variants for sheath ceremony), **`upper_node`** (the SpineLow-masked variant for the one-shot
overlay route — `None` when the model has no split key-bone), `frequency`, `replay: (min,max)`.

### The MSVCRT RNG (select.rs:713-716)
`msvc_rand(state)`: `state = state*214013 + 2531011; return (state >> 16) & 0x7fff`. **One shared stream**
per world (a `Local<u32>` in the system — driver.rs:174), like the client's single CRT `_rand`. Feeds both
the variation pick and the replay-count roll.

### The replay-count roll (select.rs:723-731)
`replay_count((min,max), roll)` = `max(1, min + ⌊roll·(max−min)/32768⌋)`. `(0,0)` → 1. Expressed as a Bevy
`RepeatAnimation::Count(R)` on a one-shot, or a watchdog **window** on a loop (see §3/§6).

### Selector tables (select.rs) — how requested ids are chosen
- `gait_candidates(state, walk, ready, ranged_load)` (select.rs:335-433): returns a `&'static [u16]`
  priority list — swim (41-45), fly (135), backward (13→4→0), fast-run (143→5→4→0) / run (5→4→0) / walk
  (4→0) by speed, turn-shuffle (11/12), engaged Ready (25-28), ranged Load (105/106/109-112), chair loops
  (102-104), else `[STAND]`. First candidate that `find_resolved` resolves wins (driver.rs:1006-1008).
- `swing_anim_main/off` (select.rs:544-566), `ready_anim` (735-745), `defense_anim` (483-499),
  `ranged_load_anim`/`ranged_hold_anim` (442-467), `wound_anim` (657-665) — all `(class,subclass)` LUTs.
- `current_special(mv, jump_arc)` (select.rs:753-767): Fall(FALLINGFAR) / Jump(upward arc) / Pose(sit=1,
  sleep=3, kneel=8) / None.
- `playback_rate(clip, speed)` (select.rs:834-840): `speed / clip.move_speed` **only** for the RATE_SCALED
  locomotion set (select.rs:829: `[4,5,11,12,13,37,38,39,42,43,44,45,135,143,187]`); else **1×**.

---

## 3. One-shot play OVER locomotion (play.rs + driver.rs one-shot loop)

A cast gesture / swing / emote plays **once** without disrupting the base gait via the **masked overlay**
route, or replaces the base full-body when standing idle.

### Route decision (per play, from live state) — `route_oneshot` (select.rs:813-825)
```
if is_forced_full_body(id) || !is_class_a(id) { return FullBody }   // Death-class, sit-transition, non-CLASS_A
let committed_lower = flags & ROUTE_COMMITTED_MOVE != 0             // 0x20003f: dir bits + turn keys + swim
    || stand_state != 0                                            // seated/sleep/kneel/chair
    || (is_combat(id) && flags & FALLING != 0);                    // combat id while airborne
committed_lower ? Masked : FullBody
```
The id gates only *which* tests apply; the same Attack1H is full-body standing and masked running. Mounted
**forces** Masked (driver.rs:617-622).

### The play loop (driver.rs:563-670)
Requests are gathered per-entity into `pending` (swings + emotes), sorted by `seq` (PlaySeq stamp — packet
order), plus a deferred-cache injection (driver.rs:557-562) and the defense reaction appended last
(driver.rs:551). For each requested id:

1. **Combat fast-path** (driver.rs:570-586): if a combat clip is already live (`live_oneshot`) and the
   requested id is also combat (`is_combat_anim`, select.rs:515-517), do **NOT** arm — instead **double
   the current clip's speed** (`active.set_speed(2.0)`) and **park** the request in `drv.deferred`. This is
   why the Eviscerate spin survives its auto-swings (sped up, never cut).
2. Otherwise clear `deferred` (driver.rs:589).
3. **Same-id dedup** (driver.rs:595-610): a requested id already occupying its slot and still playing is
   NOT re-armed (lets a looping eat/drink kit free-run across resends).
4. Stop any prior overlay node (driver.rs:614-616).
5. Resolve + roll: `picked = find_resolved(id).map(|h| roll_oneshot(anims, h, rng))` (driver.rs:626-627).
   `roll_oneshot` (play.rs:42-55) does the client's two rolls in order: **variation pick** (weighted walk),
   then **replay budget** → `RepeatAnimation::Count(R)` if R>1 else `Never`.
6. **Masked route** (driver.rs:631-644): if `masked && picked.upper_node` exists — `player.play(node)`,
   `.replay()`, `.set_repeat(repeat)`, `.set_speed(1.0)`, **`.set_weight(ONESHOT_OVERLAY_WEIGHT = 8.0)`**
   (driver.rs:53). Records `drv.overlay = Overlay{node, id, looping:false}`. The base machine (`mode`) runs
   **untouched** — the legs keep the gait. The 8:1 weight makes the torso dominate the SpineLow subtree
   (the base is NOT masked out of that subtree, so they blend — a ~8:1 dominance, small bleed accepted —
   driver.rs:47-52).
7. **Full-body route** (driver.rs:645-658): else if no Special holds — `play_clip` onto the **base track
   via `tr`** (cross-fade), set `drv.mode = Mode::Swing{id, flags}`, `drv.gait = None`. This replaces the
   base.
8. Else **dropped** (driver.rs:659-661): wanted a mask we can't build (no split bone) while a Special
   holds — benilla can't stack it.

### Blend-in
`play_clip` (play.rs:21-35): `tr.play(player, c.node, Duration::from_secs_f32(c.blend_time.max(0.0)))` then
`set_repeat`/`set_speed`. So a **full-body** one-shot cross-fades over the clip's authored `blend_time`.
A **masked** overlay is played raw (`player.play`, no transition) at weight 8 — it appears/disappears by
weight, not a fade (driver.rs:634-637).

### Finish → return to base
- Masked overlay completion (driver.rs:1118-1135): a finished non-looping overlay is stopped and
  `drv.overlay = None`; the base clip (still running underneath) reclaims the torso. A clip authored
  looping with no hold is released when `moving || special.is_some()` (driver.rs:1121-1127).
- Full-body `Mode::Swing` (driver.rs:847-877): when `oneshot_finished(id)` **or** a movement-flag change,
  `drv.mode = Mode::Gait; drv.gait = None` — a fresh gait is re-picked next frame. `oneshot_finished`
  (play.rs:135-149) is true iff **no variation** of the resolved id is still running.
- The **whiff slow-down** (driver.rs:679-690): a miss/dodge drops the in-flight swing to `set_speed(0.5)`
  (half speed for the remainder) on whichever node (overlay or base) holds a swing id.

---

## 4. CastHold — sustained cast/channel pose

CastHold is a **component**, rendered each frame (not a one-shot event). Two render modes selected by
movement (`CAST_PIN_MOVE` = ANY_MOVE | SWIMMING, select.rs:129 — turn bits deliberately excluded):

- **Stationary → full-body gait-slot pin** (driver.rs:939-946): inside the `Mode::Gait` arm, before the
  Ready/state-emote/loot fallbacks, `cast_hold` with `flags & CAST_PIN_MOVE == 0` overrides `cands` to
  `[h.anim_id, STAND]`. The hold pose thus becomes the "gait" — cross-faded and looped like any base gait
  (driver.rs:1036-1075). A turning caster keeps this pin (feet sliding), fixing the frostbolt right-drag
  jitter (select.rs:121-128).
- **Moving/swimming → looping masked overlay** (driver.rs:1137-1178): `masked_hold` = `find_resolved(h.
  anim_id).upper_node` when `CAST_PIN_MOVE != 0 && special.is_none()`. The match at driver.rs:1153-1177:
  a free slot (or a stale hold loop) is (re)taken with `player.play(node); .replay(); .repeat();
  .set_weight(8.0)` and `drv.overlay = Overlay{node, id, looping:true}`. A masked one-shot swing wins while
  it plays; the hold re-takes the subtree the frame after (last-writer-wins slot). The `looping:true` flag
  makes the overlay-completion branch skip natural expiry (driver.rs:1119-1120) — it is released only when
  the CastHold component disappears (`(None, Some(ov)) if ov.looping` → stop + clear, driver.rs:1170-1174).

A Special outranks the hold in both modes. `hold_played` is fed into the sheath reconcile so the hold's
retake play is a "play" the reconcile sees (driver.rs:1145, 1251-1264) — e.g. a caster staff stows.

### Instant-cast same-frame START→GO (Section 4's `pending`)
The writer side (`route_cast_visuals`, spell_visual.rs) resolves this, not the driver. The `holds` query is
one command-flush stale, so the router keeps a **`pending: EntityHashMap<Option<u32>>`** overlay
(spell_visual.rs:462-471) tracking this frame's hold insert/remove. On **Start** with a nonzero-castTime
kit it inserts `CastHold` + `pending.insert(entity, Some(spell_id))` (spell_visual.rs:552-557). On **GO/
FAIL** it removes CastHold + `pending.insert(entity, None)` **only if** `held_spell(pending, entity) ==
Some(spell_id)` (spell_visual.rs:582-584, 662-664). An **instant** cast (SpellStart carried no cast time,
so no CastHold — see `Casting` doc, creature_anim.rs:198-212) has its GO's release reap the precast loop
unconditionally-of-hold-state — nothing to remove, no hold ever rendered. Net effect at the driver: the
component is present for a real cast and simply absent for an instant one; the driver reads whatever the
router committed this frame.

---

## 5. CombatWound (anims 8–10) — the secondary-blend slot (wound.rs)

A **separate slot** from `overlay` — the client's per-bone SECONDARY blend (creature_anim.rs:502-508). A
masked swing and a wound decay **coexist**. Three functions, called at three points in `drive_animations`:

### Upkeep — every frame, above the death override (driver.rs:340; wound.rs:28-53)
`wound_upkeep` runs **unconditionally**, before all state logic (even before `dead` continue). For an armed
`drv.wound`, if the node is still playing: `remaining = 1.0 - seek_time/span`, then
`a.set_weight(wound_weight(remaining, others))`. `others` = `1.0 + 8.0` when masked and an overlay is live,
else `1.0` (wound.rs:38-42). When the node finishes → `player.stop`, `drv.wound = None`. Self-releasing,
never a snap, never stops what plays underneath.

### The λ math — `wound_weight` (select.rs:701-705)
`t = remaining_frac.clamp(0,1)`; `λ = smoothstep(t)·0.75` where `smoothstep(t) = (3−2t)·t²`; and the Bevy
graph weight is `w = others·λ/(1−λ)`. λ **decays 0.75 → 0** over the clip span (t runs 1→0). Amplitude
`WOUND_AMPLITUDE = 0.75` (select.rs:692) — the flinch peaks at 75% wound, never fully replacing the pose.
Bounded `w ≤ 3·others`.

### Eviction — same-bone re-arm (driver.rs:1181; wound.rs:66-83)
`wound_evict(masked_played, base_played)`: a **full-body** wound is evicted by a `base_played` this frame
(bone 0: swing on base / gait/mode change); a **masked** wound by a `masked_played` (the key-bone: masked
swing / cast-hold retake). A play on the *other* bone leaves the wound decaying. `base_played` is
`base_played || (drv.mode, drv.gait) != pre_state` (driver.rs:1180). This is why the flinch never smothers
the next attack.

### Trigger — arm the slot (driver.rs:1183-1200; wound.rs:101-163)
After eviction, `pending_wound` (melee HitInfo & 0x2 → `WoundEdge::Melee`, or spell → `WoundEdge::Spell`)
resolves an id: melee via `wound_anim(hit_info, engaged)` (crit 0x80→10, engaged→9, else 8 —
select.rs:657-665); spell carries the kit's own id. `wound_trigger`:
- Alive gate: `mv.stand_state != 7` (wound.rs:113).
- `full = wound_full_body(id, base, flags, mounted)` (select.rs:682-687): full-body iff the base pose is a
  ready stance {25–29}, **or** (StandWound 8 only) genuinely stationary and unmounted.
- Roll a variation (wound.rs:120-122), `filter(duration > 0.0)` (span-0 = degenerate seed, skip).
- Node = full-body `c.node` (masked=false), else `c.upper_node` (masked=true) with fallback to `c.node`
  (wound.rs:123-133).
- Re-trigger re-seeds: stop the prior wound node (wound.rs:135-138). If the node is already active the base
  track owns it — skip (wound.rs:141).
- Arm: `player.play(node); .replay(); .set_repeat(Never); .set_weight(wound_weight(1.0, others))`;
  `drv.wound = Some(Wound{node, span: c.duration, masked})` (wound.rs:147-159). Wound seeded at the FULL
  weight (remaining=1.0), then upkeep decays it. Uses raw `player.play` (not `tr`) — invisible to the
  sheath reconcile and event scan, matching the client calling op4 directly (wound.rs:93-97).

---

## 6. The animation clock & per-frame evaluation

- **Clock**: Bevy's `AnimationPlayer` advances every active clip by `Time::delta` each frame (Bevy
  internal). `ActiveAnimation::seek_time()` is the clip clock the driver reads for finish/decay checks
  (wound span decay wound.rs:31, sheath swap-event crossing driver.rs:478). `is_finished()` and
  `completions()` gate one-shot end and loop-watchdog re-arm.
- **Cross-fade duration = `blend_time`**: every base-track transition goes through
  `tr.play(player, node, Duration::from_secs_f32(c.blend_time.max(0.0)))` (play.rs:28-31). `blend_time` is
  the sequence's authored `M2Sequence.blend_time` (anims.rs:30-32). This is the **only** cross-fade timer —
  benilla uses Bevy's `AnimationTransitions` weighted blend, which keeps the outgoing clip RUNNING while
  its weight ramps down over `blend_time` (noted at play.rs:222-227). Overlay/wound slots are NOT
  cross-faded — they're weight-blended parallel nodes.
- **Rate**: locomotion clips scale by `playback_rate = speed / move_speed` for the RATE_SCALED set, else 1×
  (select.rs:834-840). The gait arm re-syncs the rate every frame on whichever variation node is live
  (driver.rs:1013-1018).
- **Loop-variation watchdog** (driver.rs:710-734; `roll_loop` play.rs:85-94): a looping arm installs a
  window `R` clip-lengths wide (`drv.loop_window = (node, R)`). When the armed node (still the MAIN
  animation) reaches `completions() >= budget`, the same id is re-armed with a fresh weighted **memoryless**
  pick + fresh window — this alternates a gryphon's flap/glide and strings a `/dance` together. Ranged Load
  and Loot are deliberate freezes: `RepeatAnimation::Never`, `loop_window = None` (driver.rs:1066-1074).
- **Final bone palette**: produced by Bevy's `AnimationSystems` in `PostUpdate` from all active weighted
  nodes (base transition + overlay + wound + grip + sheath overlays). Section 1's skinning consumes the
  resulting joint `GlobalTransform`s. Two post-animation passes then compose ON TOP, after
  `AnimationSystems`, before `TransformSystems::Propagate`: the **body twist** (strafe counter-rotation,
  twist.rs) and the **global sequences** (§7). The billboard-joint palette (§8) runs later, in `PostUpdate`
  after propagation.
- **Anim LOD** (`AnimParked`, creature_anim.rs:62-68): an off-frustum unit parks per-bone pose evaluation
  (clocks + state machine + events keep running); a re-appearing unit snaps to the absolute-clock pose.

---

## 7. Global sequences on units (global_seq.rs)

Free-clock loops **independent of the playing animation** (eye-blink eyelid scale, resting fidget pulses) —
benilla's per-sequence reader deliberately drops them (global_seq.rs:1-5).

- **Component** `GlobalSeqDrive { bones: Vec<(Entity, GlobalBone)>, elapsed: f32, paused: bool }`
  (global_seq.rs:23-33). `bones` pairs each channel's joint entity with its baked `GlobalBone` channels.
  `GlobalSeqDrive::new` maps the model's global bones to joint entities, returns `None` when none resolve
  (the common case — ordinary rigs cost nothing) (global_seq.rs:39-49).
- **Clock**: `elapsed` = seconds since spawn, wrapped modulo each channel's own `period` inside the sampler.
  **Zero arming**, purely clock-driven (global_seq.rs:7-12). `set_paused`/`sync` support the doodad draw
  gate (units never pause; `sync(elapsed)` re-seats the clock on resume, drift-free — global_seq.rs:51-61).
- **Sampling** `apply_global_sequences` (global_seq.rs:69-102): runs in `PostUpdate` after
  `AnimationSystems`, before `TransformSystems::Propagate` (global_seq.rs:105-112). Each frame:
  `drive.elapsed += dt`; if `paused` skip; a **parked** unit still advances the clock but skips joint
  writes (absolute-clock, never freeze — global_seq.rs:79-85). Then for each `(joint, bone)`: overwrite
  ONLY the channels the bone authors — `tf.translation = c.sample(t)`, `tf.rotation = c.sample(t)`,
  `tf.scale = c.sample(t)` (global_seq.rs:87-100). A bone the playing animation never keyed (the eyelid)
  keeps its rest T/R and takes only the global scale — so the eye opens and blinks over whatever gait
  plays. Canonical example: eyelid scale is 0 (open) ~96% of the loop, 1 (shut) ~100 ms (global_seq.rs:
  119-135). Each channel is sampled by its own `period`, not the playing sequence's time band.

Port note: `GlobalSeqChannel { period, keys: Vec<(f32, Vec3)> }`; sampling wraps `t mod period` and
interpolates/plateaus between keys (test global_seq.rs:124-135 shows plateau behavior).

---

## 8. billboard_joint_palette — camera-facing at the palette level (billboard.rs)

The re-orientation law is **byte-pinned** (billboard.rs:5-22): the M2 bone palette is computed in **VIEW
space**, and a billboard bone's matrix rows are **REPLACED** with the camera basis — ONE shared orientation
for every billboard (the view-matrix axes), NOT a per-pivot aim and NOT the geometry facet normal.

### The basis function `billboard_basis(kind, kept_rot, fwd, right, up)` (billboard.rs:160-203)
`fwd/right/up` are the camera's world axes (`cam_tf.forward()/right()/up()`, billboard.rs:293). Produces the
bone's WoW-frame X/Y/Z as world directions `(bx, by, bz)`:
- **Spherical (0x08)** (billboard.rs:170): the whole fixed basis — `(bx,by,bz) = (-fwd, right, up)` — X
  toward the viewer, Y screen-right, Z screen-up.
- **LockZ (0x40)** — the `?` marker, frost-armor sheets (billboard.rs:178-183): keep the authored bone Z
  (`bz = normalize(kept_rot * Vec3::Y)`), rebuild the in-plane pair from the camera:
  `by = normalize(fwd.cross(bz))` (fallback `right` on degenerate), `bx = by.cross(bz)`. The in-plane sign
  is **`Y = Fwd × Z`** (0168 handedness residual — the other order mirrors the model).
- **LockX (0x10)** (billboard.rs:189-193): `bx = normalize(kept_rot * -Vec3::Z)` (fallback `-fwd`),
  `bz = normalize(fwd.cross(bx))`, `by = bz.cross(bx)`.
- **LockY (0x20)** (billboard.rs:195-200): `by = normalize(kept_rot * -Vec3::X)` (fallback `right`),
  `bx = normalize(fwd.cross(by))`, `bz = bx.cross(by)`.

**Return** (billboard.rs:202): `Quat::from_mat3(&Mat3::from_cols(-by, bz, -bx))` — the WoW→Bevy axis fold
mapping the mesh's model-local frame onto the world dirs (local X→−by, Y→bz, Z→−bx, i.e. WoW X→−Z, Y→−X,
Z→+Y).

### The rig component `BillboardJointRig` (billboard.rs:213-255)
`{ root, joints: Vec<Entity>, parents: Vec<i16>, kinds: Vec<Option<BillboardKind>>, ignore_rot: Vec<bool> }`.
`new` returns `None` when the skeleton authors no billboard bone and no ignore-parent-rotation bone
(billboard.rs:236-241). `ignore_rot` is M2 bone flag **0x04** (keep the MODEL's orientation — the
HandArrow/Bullet attach helpers).

### The palette pass `billboard_joint_palette` (billboard.rs:280-377)
Runs in `PostUpdate` in the `BillboardPlace` set, **after `TransformSystems::Propagate`**, chained before
`face_billboards` (billboard.rs:379-489). It **writes `GlobalTransform` directly** (post-propagation).
Critically, every palette consumer (particle/ribbon sims, following-joint cards) must read AFTER it, same
frame — avian re-propagates from locals in the fixed loop, so an Update-time read gets the un-billboarded
pose (billboard.rs:263-268). Per rig (skipping parked hosts, billboard.rs:297-299):
1. `root_rot` = the host root's world rotation (the frame flag-0x04 joints reset to, billboard.rs:303-306).
2. Walk joints `0..n` **in parent-sorted order** (M2 guarantees parent < child). For joint `i`:
   - Recompose from a rewritten parent if the parent was replaced: `g = parent_new.mul_transform(*local)`,
     else keep the propagated `*global` (billboard.rs:310-323). Untouched subtrees are skipped when neither
     the joint nor an ancestor was rewritten (billboard.rs:314).
   - **ignore_rot (flag 0x04)** (billboard.rs:324-333): keep the parent-composed pivot (translation +
     scale), reset rotation to `root_rot` — the nocked arrow lies flat along the model facing, not the
     draw-hand twist.
   - **billboard kind** (billboard.rs:334-341): decompose `g`, replace rotation with
     `billboard_basis(kind, rot, fwd, right, up)`, keep translation + scale.
   - Record `replaced[i] = Some(g)` and write `*global = g` (billboard.rs:342-343).
3. **Re-compose rigid (non-joint) children** hanging under a rewritten joint (held item, nocked arrow) —
   they got propagated globals BEFORE this rewrite (billboard.rs:346-375). A stack walk from each replaced
   joint re-multiplies `parent_g.mul_transform(local)` down the subtree, **excluding sibling joints** (the
   replaced chain owns them) and **excluding any nested rig's root** (`rig_roots` set — a nested rig owns
   its own interior; billboard.rs:349-373). Skinned geometry never needs this (it reads joint frames).

**Why palette-level, not per-card**: geometry skinned to a billboard bone's CHILDREN inherits the facing
because the child recompose carries the replaced parent frame — the per-batch card split can't catch that
(the frost-armor sheets skin to the scale-in child of the lock-Z bone; billboard.rs:207-212, 561-656 test).

### The card pass `face_billboards` (billboard.rs:393-466)
The other consumer of `billboard_basis` — a `BillboardCard` (world-root glow cards) re-seated from its
owner's live `GlobalTransform`, faced via `billboard_basis(card.kind, card.placement_rot, ...)`, then a
global-sequence scale pulse and an armed first-sequence translation bob applied, writing both `Transform`
and `GlobalTransform` directly (billboard.rs:446-464).

---

## Cross-cutting facts (for the port)

- **Component names**: `EmoteAnim{entity,anim_id:u16,seq:u64}`, `WoundAnim{entity,anim_id:u16}` (Messages,
  drained each frame), `CastHold{anim_id:u16,spell_id:u32,ranged:bool}` (a persistent component read per
  frame). Slots inside `AnimDriver`: `overlay: Option<Overlay{node,id,looping}>` (masked SpineLow one-shot
  / moving cast-hold), `wound: Option<Wound{node,span,masked}>` (decaying secondary flinch), `deferred`
  (combat fast-path park), `loop_window` (loop-variation watchdog).
- **id→seq resolution**: requested u16 → `ModelAnimations::resolve` (PATH1 baked PlayableAnimationLookup /
  PATH2 DBC Fallback walk / identity-degrade) → `find` (head) for compare/loop or `pick_variation`
  (weighted `_rand` walk) for a play → `AnimClip{node, blend_time, move_speed, duration, upper_node,
  frequency, replay}`. Wrapped by `find_resolved`. Kinds: **Exact** = model owns the id (PATH1 identity /
  PATH2 direct hit); **Fallback** = the baked/walked substitute (chicken Attack2H→AttackUnarmed).
- **One-shot-over-base blend model**: full-body → cross-fade onto the base track via `AnimationTransitions`
  over `blend_time`, sets `Mode::Swing`, returns to Gait on finish/flag-change. Masked → a parallel graph
  node on `upper_node` (SpineLow subtree) at **weight 8.0** (ONESHOT_OVERLAY_WEIGHT, ~8:1 dominance since
  base is not masked out), `mode` untouched, released when the node finishes. Combat re-plays don't hard-cut
  — the fast-path doubles the current clip's rate and defers the new one.
- **Wound blend model**: `w = others·λ/(1−λ)`, `λ = (3−2t)t²·0.75`, `t` = remaining fraction over the clip
  span (decays 0.75→0, self-releases). `others = 1.0` (full-body) or `1.0+8.0` (masked with a live
  overlay). Evicted by a same-bone re-arm.
- **Animation clock**: Bevy `AnimationPlayer` (delta-advanced); cross-fade timer is the sequence's
  `blend_time`; locomotion rate = `speed/move_speed` for RATE_SCALED ids. Final palette from Bevy
  `AnimationSystems` (PostUpdate), then global-sequence + body-twist compose, then billboard palette
  (post-propagation).
- **Billboard palette basis**: view-space replacement, one shared camera basis for all billboards. Spherical
  `(bx,by,bz)=(-fwd,right,up)`; LockZ keeps `kept_rot*Y`, rebuilds `by=fwd×bz, bx=by×bz`; LockX keeps
  `kept_rot*(-Z)`; LockY keeps `kept_rot*(-X)`. Final quat `from_mat3(from_cols(-by, bz, -bx))`. Flag 0x04
  (ignore-parent-rotation) resets rotation to host root, keeps pivot. Runs after propagation, writes
  GlobalTransform, recomposes non-joint rigid children, never enters a nested rig's subtree.

## Anything underspecified / port hazards

- **`dir_flags`** from PATH-1 resolution (anims.rs:132-135) is plumbed through `ResolvedAnim` but **never
  consumed** — the exact playback consumption (`~0x7126d2`, direction/variant code) is untraced. Port can
  ignore it for now, but carry the field.
- **`ranged_held` / ranged Load→Hold twin** (creature_anim.rs:526-529; driver.rs:1019-1093) is flagged
  **INTERIM** (decision 0400/0409/0412) — the real `[+0xd58] |= 0x400` post-shot hold-pose layer is
  underived. The one-pass-then-swap-to-hold behavior is a stand-in.
- **Blend model deviation**: benilla uses Bevy's weighted transition (outgoing clip keeps RUNNING while
  weight ramps down over `blend_time`), whereas the client does a **pose-snapshot decay** (op4 copies the
  outgoing pose to `+0xc4` and decays the frozen pose). Only the airborne cut freezes the outgoing node to
  approximate this (`set_speed(0.0)`, play.rs:228-235); adopting the snapshot law universally is a recorded
  follow-up (decision 0503). Wound/masked overlays weight-blend instead of true masking, causing a small
  ~8:1 bleed into the base on the shared subtree (accepted, driver.rs:47-52).
- **CastHold render split** is by movement (`CAST_PIN_MOVE`), turn bits deliberately excluded — porting the
  wrong mask reproduces the frostbolt right-drag jitter (select.rs:121-128, decision 0491).
- **LockX/LockY billboard signs** have not been A/B-verified against shipped content (billboard.rs:184-200)
  — only LockZ and Spherical are director-confirmed. If a chain/rope reads mirrored, flip the cross-product
  sign (the one knob).
- The wound/emote 8–10 split lives in the **writer** (`play_kit`, spell_visual.rs:348-357), not the driver —
  the driver assumes WoundAnim ids are already CombatWound-family. `wound_full_body` handling of ids other
  than 8 (i.e. 9/10 CombatWound/Critical) only goes full-body via the ready-stance clause, never the
  stationary clause (select.rs:682-687) — worth preserving exactly.


---

# Section 12 — SPELL SOUND (kit sound + missile sound playback)

Scope: what benilla plays WHEN (the events) and HOW it plays it (the kit player over a mixer
seam), so a C# port with no audio backend yet can (a) wire the event triggers now and (b) build a
backend against a known contract later. Read alongside section 4 (orchestration emits the events),
section 2 (SoundEntries.dbc layout), section 10 (missiles).

Root: `C:\Users\nico\Desktop\benilla-main`. All paths below are under `crates\benilla\src\`
(client) and `crates\benilla-formats\src\` (DBC).

The audio subsystem here is **WoW's owned selection/scheduling over a delegated backend**
(decision 0070): benilla computes every audible parameter (which file, volume, distance rolloff,
positional pan feed) and hands only play/stop/set-volume/set-position to the backend (kira, behind
an FMOD-shaped seam). A C# port replaces the seam; the selection math above it ports 1:1.

---

## 0. Module wiring (who owns what)

`sound\mod.rs` — the Bevy plumbing and the two shared resources every sound system reads:

- `SoundOutput` (non-Send resource, `mod.rs:100-104`): `mixer: Option<Mixer>` (None = headless/CI
  or `$WOW_NOSOUND`; every consumer tolerates silence) + `channels: Vec<ActiveChannel>` — the live
  voices, owned and pumped by `kit::pump_channels`.
- `SoundConfig` (resource, `mod.rs:51-67`): master enable, `muted`, `master` linear [0,1], and the
  three per-category sliders `sfx`/`music`/`ambience`. Defaults are the client's CVar defaults:
  master 1.0, **music 0.4, ambience 0.6** (a fresh 1.12 install is not uniform full volume;
  `mod.rs:83-94`). `muted` starts **true** (boots silent until unmuted). `category_amp(cat)`
  returns 0.0 if `!enabled`, else the matching slider (`mod.rs:71-81`).
- `AudioListener` (resource, `mod.rs:116-120`): `{ pos: Vec3, rot: Quat }` — the single per-frame
  3D listener pose every sound system reads.

`SoundPlugin::build` (`mod.rs:146-198`) creates the mixer (or None on `$WOW_NOSOUND`/device
failure), inserts the resources, and registers each sub-module's `plugin(app)` — including
`spell::plugin` and `missile::plugin` (`mod.rs:182-183`). The two spell-relevant consumers both run
in `WorldStage::Present` (`spell.rs:99`, `missile.rs:67`); the pump also runs in `Present`
(`kit.rs:590-592`).

---

## 1. Event types (exact fields + emission points)

Two message (event) types cross from section 4/10 into section 12. Both are Bevy `Message`s
registered via `add_message`.

### 1a. `SpellKitSound` — the caster's kit sound

Defined `creature_anim\spell_visual.rs:55-64`:

```rust
#[derive(Message, Clone, Copy, Debug)]
pub(crate) enum SpellKitSound {
    Play { entity: Entity, kit_sound: u32 },   // kit_sound = a SoundEntries.dbc id
    StopHold { entity: Entity },
}
```

- `Play { entity, kit_sound }`: ring `kit_sound` at `entity`. If the kit is LOOPING (SoundEntries
  flag 0x200) it becomes the unit's tracked **hold loop** (sustained until `StopHold`); otherwise a
  fire-and-forget positioned one-shot.
- `StopHold { entity }`: a cast/channel hold ended — reap `entity`'s tracked hold loop, if any.

`kit_sound` is `SpellVisualKit` **field 13** (the kit's own sound column), resolved by the cast-edge
router (`spell_visual.rs:51-52` doc). Registered `creature_anim.rs:680`, re-exported
`creature_anim.rs:474`.

**Emission points (all in `spell_visual.rs`, section 4 owns these):**

| Stage | Line | Event | Kit source |
|---|---|---|---|
| `CastEventKind::Start` (precast) — reap prior first | 500-501 | `StopHold` | — |
| `CastEventKind::Start` (precast) — begin buildup loop | 559-563 | `Play` | precast kit `.sound` (`resolve_kit(..., s.precast)`) |
| `CastEventKind::Go` (release) — reap precast loop | 589-590 | `StopHold` | — |
| `CastEventKind::Go` (release) — cast/fire flash | via `play_kit(DISCRETE)` 635-643 → 359-360 | `Play` | cast kit `.sound` (`resolve_kit(..., s.cast)`) |
| `CastEventKind::Impact` — missile arrival | via `play_impact` 650-659 → 359-360 | `Play` | impact kit `.sound` |
| `CastEventKind::Fail` — reap precast loop | 666-667 | `StopHold` | — |
| Channel-field change (new channel) — reap old, begin new | 765, 787 | `StopHold` then `Play` | channel kit `.sound` |
| Channel-field clear | 809 | `StopHold` | — |
| Discrete `PlaySpellVisualKit` (KitPush) | 359-360 | `Play` | pushed kit `.sound` |

Note the discrete-play helper `play_kit(entity, spell_id, kit, KitPlay::DISCRETE, seq, &visuals,
&mut out)` in `spell_visual.rs` (NOT the sound crate's `play_kit`) is the common path for the
GO-release flash, the impact, and pushed kits; internally at line 359-360 it writes
`SpellKitSound::Play { entity, kit_sound }` gated on `kit.sound.filter(|_| play.sound)`.

### 1b. `MissileSound` — the projectile's flight loop

Defined `entities\missile.rs:118-130`:

```rust
#[derive(Message, Clone, Copy)]
pub(crate) enum MissileSound {
    Start { entity: Entity, kit_sound: u32, pos: Vec3 },
    Stop  { entity: Entity },
}
```

- `Start { entity, kit_sound, pos }`: begin `kit_sound` as a loop tracked to `entity` (the launched
  projectile). `pos` is the **launch point** — the channel is born positional there because the
  just-spawned entity's `Transform` may not have flushed yet, then rides the entity as it flies.
- `Stop { entity }`: the projectile is gone (arrived / target streamed out) — stop its flight loop.

`kit_sound` is `SpellVisual` **field 10** (the missile's `WeaponLoop`/`FireMissileLoop`;
`missile.rs:114`). Re-exported `entities.rs:81`, registered `entities.rs:306` and
`missile.rs:661`.

**Emission points (`entities\missile.rs`, section 10 owns these):**

- `spawn_missiles` → `MissileSound::Start { entity, kit_sound, pos: launch }` at launch, gated on
  `go.spawn.missile_sound` being present (`missile.rs:363-368`).
- `move_missiles` → `MissileSound::Stop { entity }` in two cases: target streamed out mid-flight
  (`missile.rs:606`) and arrival at target (`missile.rs:621`). Both immediately `despawn` the
  missile entity the following statement.

---

## 2. Consumer: `SpellKitSound` router (`sound\spell.rs`)

`route_spell_kit_sounds` (`spell.rs:23-95`), in `WorldStage::Present`. Key state: a
`Local<EntityHashMap<u32>>` **`hold_loops`** mapping each unit → the kit id of its currently live
tracked hold loop, so `StopHold` reaps exactly that kit's channel and never a sibling (e.g. the
caster's NPC-greeting line on the same entity).

Despawn handling first (`spell.rs:36-38`): drains `RemovedComponents<NetEntity>` and drops each
despawned entity from `hold_loops` (the channel itself is stopped by the greeting despawn reaper,
§6).

`Play { entity, kit_sound }` (`spell.rs:48-87`):
1. `pos = transforms.get(entity).map(|t| t.translation).ok()` — the caster's world position (None →
   plays 2D).
2. `looping = kit_looping(&kits, kit_sound)` — tests SoundEntries flag 0x200 (`kit.rs:274-278`).
3. Logs `debug!("spell kit sound {kit_sound} on {entity:?} (looping {looping})")` — the greppable
   proof-of-fire line for headless probes (`spell.rs:53`).
4. If **looping**: `play_kit_ext(..., pos, Sfx, variant=None, source=Some(entity),
   force_loop=false)` — a tracked loop tagged to the caster (`spell.rs:54-68`). On `Ok(())`,
   `hold_loops.insert(entity, kit_sound)` (`spell.rs:81-83`).
5. If **not looping**: `play_kit(..., pos, Sfx)` — a fire-and-forget positioned one-shot
   (`spell.rs:69-79`).

`StopHold { entity }` (`spell.rs:88-92`): `if let Some(kit_sound) = hold_loops.remove(&entity)` then
`kit::stop_source_kit(&mut out, entity, kit_sound)` — force-stops only that entity's channels
playing that kit.

Guards: if `events.is_empty()` returns early (`spell.rs:39-41`); if the kit catalog or world assets
are absent, returns (silent, `spell.rs:42-44`).

---

## 3. Consumer: `MissileSound` router (`sound\missile.rs`)

`route_missile_sounds` (`missile.rs:21-63`), in `WorldStage::Present`.

`Start { entity, kit_sound, pos }` (`missile.rs:38-58`):
```rust
play_kit_ext(
    &mut kits, &assets, &mut out, &config, listener,
    KitRef::Id(kit_sound),
    Some(pos),              // born positional at the launch point
    SoundCategory::Sfx,
    None,                   // no explicit variant
    Some(entity),           // tag the loop to the missile — the pump follows it in flight
    true,                   // force_loop: missile loops by construction, NOT by the 0x200 flag
)
```
Distinct from the caster's hold loop in two ways: `force_loop=true` (the flight loop is looping by
construction — the client's `CMissile+0x44` loop handle — not by SoundEntries flag; `missile.rs:8-10`),
and there is no `hold_loops` ledger — the missile carries exactly one channel.

`Stop { entity }` (`missile.rs:60`): `stop_source(&mut out, entity)` — stops **everything** tagged
to that missile entity (it only ever has the one flight channel).

Same guards as the spell router (`missile.rs:29-35`).

---

## 4. The kit player (`sound\kit.rs`) — resolution, selection, looping split

This is the WoW-owned layer between "play kit N" and the backend. All of §4 ports 1:1 to C#.

### 4a. SoundEntries resolution (id/name → concrete file)

`play_kit_ext` (`kit.rs:144-253`) resolves `KitRef::Id(id)` via `kits.catalog.get(id)` or
`KitRef::Name(name)` via `by_name` (`kit.rs:157-161`; unknown → `Err("unknown sound kit")`). A
`SoundKit` (from `sound_entries.rs:39-59`) carries: `id`, `sound_type`, `name`, `files:
Vec<(String path, u32 weight)>` (up to 10, `DirectoryBase\File[i]` joined at load; only non-empty
slots), `volume` [0,1], `flags`, `min_distance`, `distance_cutoff`, `eax_def`.

The concrete file is `kit.files[pick].0` (`kit.rs:201-203`), decoded (and cached by lowercased path)
via `SoundKits::sfx` → `mixer::sfx_from_bytes` (`kit.rs:206`, `428-441`). A kit with **no files** is
"playable-as-nothing", not an error (`kit.rs:180-183`).

**SoundEntries.dbc layout** (section 2; `benilla-formats\src\sound_entries.rs:5-13`, byte-verified
build 5875, 4623 records · 29 fields · 116 B/record): `ID(0), SoundType(1), Name(2 str),
File[10](3..12 str), Freq[10](13..22 = weights), DirectoryBase(23 str), Volume(24 f32), Flags(25),
MinDistance(26 f32), DistanceCutoff(27 f32), EAXDef(28)`.

### 4b. The random-file (variation) selection

`pick_variation(kit, weights)` (`kit.rs:399-424`) — a **depleting weighted pool** (client
`0x45bb70` pick + `0x45bd40` refill): each pick decrements the chosen slot's remaining weight; when
the pool empties it refills from the base weights. With the data's typical all-1 weights this is
"no repeats until every variation has played once, then reshuffle" (test `kit.rs:607-620`). Single
file → index 0 (`kit.rs:400-401`). PRNG is a local xorshift32 `Rng` seeded `0x9e37_79b9`
(`kit.rs:52-65`, `372`) — the *transform* of the draw is the fidelity surface, not the generator.
A caller may pass an **explicit variant index** (`Some(i)`), bypassing the pool (`kit.rs:196-200`);
neither spell nor missile uses that (both pass `None`).

### 4c. The LOOPING flag (0x200) test → tracked loop vs one-shot

`sound_kit_flags` (`sound_entries.rs:31-36`): `NO_DUPLICATES=0x20, LOOPING=0x200, VARY_PITCH=0x400,
VARY_VOLUME=0x800`. The DBC Flags word is copied **raw** into the runtime kit flag word.

`kit_looping(kits, id)` (`kit.rs:274-278`): `kit.flags & LOOPING != 0`. This is the client's
`0x458830` looping-test that splits playback:
- **plain kit** → one-shot (`0x458870`);
- **LOOPING kit** (Fireball precast buildup id 702, Arcane Missiles channel hum id 3136) → a
  channel **tracked to the caster** (`0x61fec0`), reaped by `StopHold` when the hold ends (client's
  `0x614150`). Without the reap a `/castvis 133` buildup would loop forever past its own release
  (`spell.rs:4-8` doc).

Inside `play_kit_ext` the effective loop decision is `looping = force_loop || flags & LOOPING != 0`
(`kit.rs:226`), and `data = data.loop_region(..)` when looping (`kit.rs:227-229`).

### 4d. `play_kit` vs `play_kit_ext` (source tracking)

- `play_kit(...)` (`kit.rs:119-132`) = `play_kit_ext` with `variant=None, source=None,
  force_loop=false`. Used for one-shots (the caster's non-looping kit; the debug play).
- `play_kit_ext(...)` (`kit.rs:144-253`) adds `variant: Option<usize>`, `source: Option<Entity>`,
  `force_loop: bool`. `Some(entity)` **tags the played channel to that entity** (`ActiveChannel.source`),
  so its liveness serves as the entity's per-unit latch and so `stop_source*` can find it. A channel
  is **tracked** (rides its unit each frame) iff `looping && source.is_some()` (`kit.rs:242`).
  Both the caster hold loop (`spell.rs`, `force_loop=false`) and the missile loop (`missile.rs`,
  `force_loop=true`) pass `Some(entity)`.

### 4e. Selection-time gates (before allocating a voice)

1. **Audibility cull** (`kit.rs:173-178`): if positional and `cutoff > 0`, `!math::audible(d_sq,
   cutoff)` (i.e. `cutoff² ≤ d²`) → return `Ok(())` without playing. Checked *first* so an
   out-of-range per-frame retry driver costs only a distance test.
2. **Duplicate suppression** (`kit.rs:190-192`): if `flags & NO_DUPLICATES (0x20)` and a channel
   with the same kit id is already live → return `Ok(())` (client's FMOD pre-play gate `0x7a66a0`).

Gates are **not errors** — they silently succeed without playing (matches the client).

### 4f. Per-shot volume/pitch + stop helpers

- Volume: `v = math::variation_volume(...)`; varied only if `flags & VARY_VOLUME (0x800)`
  (`kit.rs:210-215`) — dormant in 5875 data (no kit sets 0x800). Formula `(draw-15)·0.01 + base`,
  clamped [0,1] (`math.rs:15-31`).
- Pitch: if `flags & VARY_PITCH (0x400)`, `playback_rate = variation_pitch_freq(draw) / sample_rate`
  (`kit.rs:221-225`, `math.rs:39-41`).
- Draw: `variation_draw(raw) = floor(31·raw / 2³²)` — mulhi scale, not modulo (`math.rs:47-49`).
- `stop_source_kit(out, source, kit_id)` (`kit.rs:295-304`): retain-drops channels matching BOTH
  source and kit, calling `handle.stop(snap())` — the `StopHold` reap (kit-scoped so a caster's
  greeting line survives).
- `stop_source(out, source)` (`kit.rs:308-317`): drops **all** channels tagged `source` — the
  missile `Stop` and the unit-despawn teardown.
- `set_source_kit_gain` (`kit.rs:287-293`): a driver-animated fade lane (0..1) folded into the mix;
  used by liquid loops, not spells/missiles.

---

## 5. Positional audio + the mixer/source model

### 5a. `ActiveChannel` — the per-voice struct (`kit.rs:79-106`)

One live voice the pump owns:
- `kit: u32` — the SoundEntries id (0 for raw `play_file`).
- `source: Option<Entity>` — the entity this voice is tagged to (`None` = untagged one-shot).
- `tracked: bool` — a source-tagged **looping** voice; the pump refreshes its `pos` from the
  source's transform each frame. One-shots stay where they fired.
- `handle: StaticSoundHandle` — the backend play handle (stop/set-volume).
- `track: Option<SpatialTrackHandle>` — the spatial track keeping a 3D voice alive; **`None` = 2D**
  (main track).
- `pos: Option<Vec3>`, `min_dist` (rolloff knee = kit MinDistance), `cutoff` (cull radius = kit
  DistanceCutoff; 0 = never cull), `v` (per-shot volume), `gain` (fade lane, default 1.0),
  `category`.

Voices are allocated by `out.channels.push(ActiveChannel { ... })` (`kit.rs:239-251`). There is a
`Vec` of them; **no max-voices cap and no priority system is implemented** — see §7. Duplicate
suppression (0x20) is the only pre-play throttle.

### 5b. 2D vs 3D at play time (`kit.rs:231-238`)

```rust
let (track, handle) = match pos {
    Some(p) => { let (t, h) = mixer.play_3d(data, p)?; (Some(t), h) }
    None    => (None, mixer.play_2d(data)?),
};
```
`pos == Some` → a fresh spatial track at that world position; `pos == None` → the 2D main track
(UI/self sounds). Both spell caster sounds and missile sounds always pass `Some(pos)` (caster
transform / launch point), so they are 3D.

### 5c. The per-frame pump (`pump_channels`, `kit.rs:464-514`) — `WorldStage::Present`

For each channel, retain-mut:
1. If `handle.state() == Stopped` → drop it (reap finished voices; a tagged channel's death IS its
   latch release).
2. If `tracked` → refresh `pos` from `source`'s live `Transform`, and if changed call
   `mixer::set_track_position(track, p)` (`kit.rs:478-491`). This is the tracked-follow that rides
   the caster hold loop and the missile flight loop along with their entities (`0x61fec0`).
3. If `pos == None` (2D): set volume = `amp_to_db(category_amp · v · gain)` (`kit.rs:492-499`).
4. Else compute `d_sq = dist_sq(listener, pos)`. If `cutoff > 0 && !audible` → `handle.stop`, drop
   (out-of-range one-shots stop; the client virtualizes — INTERIM, `kit.rs:501-505`). Else volume =
   `category_amp(cat) · v · gain · fmod_rolloff(d²,min_dist) · near_field(d²,cutoff)`, fed via
   `handle.set_volume(amp_to_db(amp), snap())` (`kit.rs:506-511`).

**Distance math (all WoW-owned, `math.rs`):**
- `dist_sq` (52-54): squared listener↔source distance.
- `audible(d², maxdist)` (59-61): `maxdist² > d²` (strict; equal/NaN inaudible). Selection gate +
  per-frame cull.
- `fmod_rolloff(d², min_dist)` (96-106): FMOD 3.x inverse model — full inside `min_dist`, then
  `min_dist / (min_dist + 4.0·(d − min_dist))` (global `ROLLOFF_FACTOR = 4.0`, `math.rs:87`).
- `near_field_atten(d², maxdist)` (68-76): a 1→0 linear ramp across the outer 10% of `maxdist`,
  layered on top of rolloff. `near_field` wrapper (`kit.rs:351-357`) returns 1.0 for the
  `cutoff==0` non-positional sentinel.

### 5d. The listener (`mod.rs:205-232`, `AudioListener`)

Computed once per frame in `WorldStage::Stream` (after input writes the pose, before Present's
consumers). Default `SoundListenerAtCharacter=1`: `pos` = the self-avatar's **head** (feet +
`head_height`), `rot` = the **character's facing** about world-up (`Quat::from_rotation_y(facing)`)
— so 3D volume/pan are independent of camera zoom/orbit (`mod.rs:214-223`). Fallback (pre-login,
free-fly, or before the body attaches) = the camera eye/basis (`mod.rs:224-231`). The pose is pushed
to the backend via `mixer.set_listener(pos, rot)`.

### 5e. The mixer seam (`sound\mixer.rs`) — the swappable backend

kira 0.12 behind a narrow FMOD-shaped surface. **The backend contributes pan only; all gain-over-
distance is benilla's math** — backend attenuation is disabled (`play_3d`
`.attenuation_function(None)`, `mixer.rs:189-214`). Surface a C# backend must provide:
- `play_2d(data) -> StaticSoundHandle` (`mixer.rs:179-183`): decoded SFX on the main track (2D).
- `play_3d(data, pos) -> (SpatialTrackHandle, StaticSoundHandle)` (`mixer.rs:189-214`): a fresh
  spatial sub-track at a world position, routed into the zone reverb send at unity. The track
  handle must outlive the sound (dropping it unloads the track).
- `set_track_position(track, pos)` (free fn, `mixer.rs:233-242`): move a live 3D emitter (the pump's
  per-frame follow).
- `set_listener(pos, rot)` (`mixer.rs:151-171`), `set_master(amp)` (`mixer.rs:174-176`).
- `set_volume(db, tween)` on the handle, `stop(tween)` on the handle. `snap()` = zero-duration
  tween (immediate; the WoW-side ramps are benilla's own math, `mixer.rs:38-43`); `fade(ms)` for
  music/ambience fade-stops.
- `amp_to_db(amp)` (`mixer.rs:59-65`): linear [0,1] → dB (`20·log10`, floor −60 dB below 1e-3) — the
  seam's only unit conversion; benilla computes linear amplitudes, kira consumes dB.
- Decode: `sfx_from_bytes` (short SFX incl. IMA-ADPCM WAV + MP3, `mixer.rs:265-267`),
  `loop_from_bytes`/`stream_from_bytes` (beds/music — not used by spell/missile).
- Reverb send (`mixer.rs:72-148`): a zone wet-only Freeverb send every 3D track routes into; per-
  zone `set_reverb(preset)`. Orthogonal to spell/missile, but the spatial track they play on carries
  the send at unity.

Coordinate note (`mixer.rs:12-16`): kira's listener is X-right/Y-up = Bevy camera space, so Bevy
`Transform`s feed straight in with **no remap**. A C# port using a different-handed audio API must
apply its own convention here.

---

## 6. Lifecycle: start/stop/despawn/world-exit

- **Start**: on `Play`/`Start` events, one voice per play, pushed onto `out.channels`.
- **Stop (explicit)**: `StopHold` → `stop_source_kit(entity, kit_id)` (kit-scoped);
  `MissileSound::Stop` → `stop_source(entity)` (all tagged). Both call `handle.stop(snap())`
  (instant) and drop the channel.
- **Stop (natural)**: a one-shot ends → `pump_channels` sees `Stopped` and reaps it (`kit.rs:472`).
- **Stop (out of range)**: a positional one-shot beyond `cutoff` → the pump stops+drops it
  (`kit.rs:501-505`; INTERIM vs the client's virtualize/resume).
- **Despawn**: `sound\greeting.rs:326-329` `stop_on_despawn` drains
  `RemovedComponents<NetEntity>` and calls `kit::stop_source(entity)` — this is the shared reaper
  that kills a caster's or missile's tagged channels when the entity despawns (client `0x5fbb6c`).
  `spell.rs:36-38` additionally clears the `hold_loops` ledger entry (the channel itself dies via
  the greeting reaper). A tracked channel whose source despawned keeps its last `pos` for the one
  frame until this reaper runs (`kit.rs:475-477`).
- **World exit**: `OnExit(InWorld)` → `stop_all_channels` (`kit.rs:573-582`) stops and clears every
  live channel.

---

## 7. Trigger matrix (WHEN each spell/missile sound fires and stops)

Cross-ref section 4 (cast-edge router) and section 10 (missiles). Kit id source in parentheses;
"loop?" = whether SoundEntries flag 0x200 makes it a tracked loop (or force_loop for missiles).

| Gameplay moment | Event emitted | Loop? | Reaped by |
|---|---|---|---|
| **Precast hold begins** (`CastEventKind::Start`) | `SpellKitSound::Play` (precast kit .sound, e.g. Fireball 702) | 0x200 → tracked loop on caster | the release/fail/replace `StopHold` |
| Precast start, replacing a prior cast | `SpellKitSound::StopHold` (before its own Play) | — | (reaps prior) |
| **Cast release / fire** (`CastEventKind::Go`) | `StopHold` (kills precast loop) **then** `Play` (cast kit .sound, one-shot fire flash) | one-shot | self / natural |
| **Impact** (`CastEventKind::Impact`, missile arrival) | `SpellKitSound::Play` (impact kit .sound) | one-shot | self / natural |
| **Cast fail** (`CastEventKind::Fail`) | `SpellKitSound::StopHold` | — | (reaps precast loop) |
| **Channel hold** — channel field takes a value | `StopHold` (old) then `Play` (channel kit .sound, e.g. Arcane Missiles 3136) | 0x200 → tracked loop on caster | channel-clear/change `StopHold` |
| **Channel ends** — channel field clears | `SpellKitSound::StopHold` | — | (reaps channel loop) |
| **Discrete `PlaySpellVisualKit`** (state/aura self-gated push, KitPush) | `SpellKitSound::Play` (pushed kit .sound) | 0x200 → tracked (rare) else one-shot | StopHold / despawn |
| **Missile launch** (`spawn_missiles`) | `MissileSound::Start` (SpellVisual field 10, e.g. FireMissileLoop), born at launch pos | **always loop** (force_loop, not the flag) | `MissileSound::Stop` |
| **Missile arrival OR target lost** (`move_missiles`) | `MissileSound::Stop` | — | (reaps flight loop) |
| **Any tagged unit/missile despawn** | (no event) — greeting `stop_on_despawn` reaper | — | `kit::stop_source` |

Key behavioral facts a porter must preserve:
- The precast buildup and the channel hum are the only spell voices that **loop and are tracked to
  the caster**, and they must be explicitly reaped on the matching hold-end edge (a LOOPING kit
  never self-terminates).
- The release-flash and impact are **one-shots** — no reap needed; they die on their own clock or
  by the pump's out-of-range/finished cull.
- The missile flight loop loops **by construction** (`force_loop=true`), independent of the 0x200
  flag, and is tracked to the missile so it Dopplers/pans along the flight path; it is reaped on the
  arrival/target-lost `Stop` (and the entity despawns the same frame).

---

## Underspecified / INTERIM (flag for the porter)

1. **No max-voice / priority system.** benilla's `out.channels` is an unbounded `Vec`; the only
   throttle is the per-kit NO_DUPLICATES (0x20) gate. The client had a per-category concurrent cap
   (`count >= limit`, 13 categories) that benilla explicitly **defers** until the limit table is
   read out (`kit.rs:185-192` comment). A C# port can match benilla today (unbounded) but should
   know the client capped voices.
2. **Out-of-range looping channels STOP rather than pause/virtualize** (`kit.rs:16-17`, `501-505`):
   the client virtualizes (silent but alive, resumes when back in range); benilla stops, relying on
   the per-frame retry driver to restart. Spell/missile loops are entity-tracked and typically stay
   near the listener, so this rarely bites, but a distant caster's loop that goes out of range and
   comes back will NOT resume on its own.
3. **Volume variation (0x800) is dormant** in 5875 data (no kit sets it); the gate is present and
   faithful but never fires. Pitch variation (0x400) is live.
4. **Absolute-Hz pitch semantics** are preserved from FMOD (`math.rs:34-38`): pitch variation is
   computed as an absolute frequency then divided by the file's sample rate, so a non-22050 Hz file
   would shift pitch — deliberate fidelity, worth replicating exactly.
5. `SpellVisualKit` **field 13** (kit sound) and `SpellVisual` **field 10** (missile sound) are the
   authoritative id columns — confirm section 4/10 pass these through unchanged; the sound layer
   treats them as opaque SoundEntries ids.


---

# Appendix A — Consolidated constants, tables & cross-cutting facts

Every value below is cited fully in the owning section; this is the quick-reference the porter keeps open.

## A.1 Coordinate conversion (owning §1, §6, §7, §9)
- `wow_to_bevy([x,y,z]) = (−y, z, −x)` — WoW +X(north)→Bevy −Z, +Y(west)→−X, +Z(up)→+Y. Proper rotation, **det +1** (no winding flip). Inverse `bevy_to_wow(b) = [−b.z, −b.x, b.y]`.
- Rotations map by **conjugation** `q' = r · q · r⁻¹` with `r = wow_to_bevy_quat = Quat::from_mat3(from_cols((0,0,−1),(−1,0,0),(0,1,0)))`. Quaternion component order in files is `[x,y,z,w]`.
- Scale maps by **component permute** `(s.y, s.z, s.x)` (i.e. `(s1,s2,s0)`).
- **MSUI target frames:** world/units stay WoW **Z-up (ground = XY)**; `M2Reader` converts M2 model data to a LOCAL **Y-up** as `(PosX, PosZ, −PosY)` — a *different* map than benilla's. **Do not copy benilla's raw axis swaps; port the geometry (which bone/pivot/offset/rotation-order) and express it in whatever frame the MSUI value already lives in.** Any benilla step using `.y` as "up" becomes MSUI world `.z` (or model-local `.y`).

## A.2 Attach / kit slot tables (owning §2, §4, §6)
- `KIT_SLOT_TAGS = [0x14, 0x22, 0x13, 0x15, 0x16, 0x11, 0x17, 0x18, 0x19]` = Head, Chest, Base, LeftHand, RightHand, Breath, Special1-3 (SpellVisualKit effect fields 3-11, in order).
- `MISSILE_ATTACH_TABLE = [0x14, 0x22, 0x13, 0x15, 0x16, 0x11, 0x17, 0x18, 0x19, 0x0F, 0x10]` (SpellVisual field 9 is an ordinal into this).
- `ATTACH_FALLBACKS = [0x0F, 0x13]`; effect/missile cascade = `requested → 0x0F → 0x13 → unit root`. `HARDCODED_FX_ATTACH = 0x13`. `DEST_FALLBACKS = [0x0F, 0x13]`.
- `ground_anchor = point.is_none() || tag == 0x13` (resolved tag, not requested).
- **Pivot rule:** attach `offset = wow_to_bevy(attach.position) − pivot_bevy(bone)`; the joint entity already carries the pivot, so the child rides `…·T(offset)` and reconstructs the authored point at bind. In MSUI terms: `unit · Skin[bone] · T(pos − pivot)` **iff** MSUI `Skin` is the bind-inclusive bone matrix. This is a no-op on character hands (offset≈0) but wrong on creatures if the pivot subtraction is dropped.

## A.3 None-sentinels (owning §2)
- SpellVisual stage kit fields 1-5: plain `0` = none.
- Every foreign-key field (kit anim/sound/9 slots, SpellVisual missile_sound/strike_sound): **BOTH `0` and `0xFFFFFFFF`** fold to none.
- `.mdx`/`.mdl` DBC model path → `.m2` swap happens at model load (consumer), not the DBC layer.

## A.4 Descriptors, opcodes, flags (owning §3)
- Descriptor indices (build 5875): `UNIT_CHANNEL_SPELL = 144`, `UNIT_FIELD_CHANNEL_OBJECT = 20`, `UNIT_FIELD_AURA = 47` (48× u32 spell ids, indices 47..94), `AURAFLAGS = 95`, `AURALEVELS = 101`, `AURAAPPLICATIONS = 113`. **Aura slot occupied = `flags & 0x0E != 0`** (NOT `spell_id != 0`). Positive auras slots 0-31, debuffs 32-47.
- Channel + aura state are per-frame **descriptor polls** (self and observed identical); self-only `MSG_CHANNEL_*` packets do not carry the channel spell for observers → do not rely on them.
- Cast flags: START always `0x02`, GO always `0x100`, `+0x20 (AMMO)` gates the trailing `{u32 displayId, u32 inventoryType}`.
- Miss reasons (1-based): 1 Miss, 2 Resist, 3 Dodge, 4 Parry, 5 Block, 6 Evade, 7/8 Immune, 9 Deflect, 10 Absorb, 11 Reflect. **Missiles play a defense reaction only for Dodge(3)→victim_state 2 and Block(5)→5; all others (incl. Parry, Miss) do nothing.**
- Key opcodes: SPELL_START 0x0131, SPELL_GO 0x0132, CHANNEL_START 0x0139, CHANNEL_UPDATE 0x013A, SPELL_DELAYED 0x01E2, PLAY_SPELL_VISUAL 0x01F3, CAST_RESULT 0x0130 (self-cast failure verdict), SPELL_FAILED_OTHER 0x02A6.

## A.5 Orchestration (owning §4)
- `FxClass::{Hold, AuraState}` — the reap discriminator: `play_kit` always emits `Hold`; only `arm_aura_state_fx` emits `AuraState`, so a cast's GO reap (Hold) never sweeps a same-spell aura visual.
- `KitPlay = {persistent, effects, sound}`; `DISCRETE = {false, true, true}`; STATE flash overrides `{effects:false, sound:is_self}`.
- FX entry: `SpellKitFx::Begin{entity, spell_id, persistent, class, effects:Vec<(u16 tag, String path)>}` / `Reap{entity, spell_id, class}`. **No preferred-anim is passed to the FX layer** — it is derived inside spell_fx (None for kits, Some(144) for missiles).
- Missile gate = **Spell.dbc Speed > 0 alone** (SpellVisual field 6 `hasMissile` is never read). Spell.dbc: visual id = col 115 (u32), Speed = col 37 (f32).
- The router's `pending` is a **per-frame command-buffer overlay** (so an instant cast's same-frame START+GO see each other's deferred CastHold), **not a timer** — there is no `PENDING_TIMEOUT` in the router.

## A.6 FX render / materials (owning §5)
- `FALLBACK_SPAN = 1.0 s`; self-terminating span = file-order-first sequence's authored duration (`first_seq_span`, >0), one pass if looping. `PENDING_TIMEOUT = 10.0 s` (spell_fx's still-loading retry, distinct from §4).
- Material flag → render state: additive (M2 blend 3/4) → `(ONE, ONE)`; two-sided (`0x04`) → double-sided + cull None; no-depth-write (`0x10`); no-depth-test (`0x08`); unlit/fullbright (`0x01`, and Mod/Mod2x); AlphaTest cutoff ≈ `224/255 = 0.878`; **no texture → base color WHITE**; transparent pass, depth test on / write off.
- `played_seq = preferred_clip(preferred_anim).seq_index` (the **file** sequence slot, not the clips-vec index) — keys every part's alpha loop and each emitter's `EmitClock::PinnedSeq`.

## A.7 Particle sim (owning §7) & render (owning §8)
- Integrate order (fixed): age (+kill if age≥lifespan) → follow-delta (skip on fresh) → Rodrigues tumble → capture `step_vel` → `pos += vel·dt` → gravity on UP axis (`pos.up −= ½g·dt²`, `vel.up −= g·dt`; up = Bevy **+Y** anchored / WoW **+Z** model) → drag (`vel −= min(dt·drag,1)·vel`) → kill-outbound (Sphere & flag `0x80`: `dot(step_vel, pos−origin) > 0`).
- Emission: continuous `acc += rate·density·distLOD·dt`; burst (flag `0x8000`) `acc = trunc(rate·density·distLOD)` once on the ENABLED-gate rising edge (gate-off forces rate 0 and resets acc). Birth drains whole particles.
- Constants: `MAX_PARTICLES = 1024`, dt clamp `0.1`, density clamp `[0.25, 1.0]`, `distLOD = clamp(1 − (d−50)·0.02, 0.25, 1.0)`, follow-inherit `1/30 s`, ground probe `20 yd`, over-life endpoint inset `t·0.99 + 0.005`.
- Emitter flag bits: model_space `0x10`, kill_outbound `0x80`, sphere_up `0x100`, tail_clamp `0x400`, xy_quad `0x1000`, burst `0x8000`.
- **`R(+Z, 90°)` (`(x,y,z)→(−y,x,z)`) is prepended ONCE at emission** to kernel birth vectors (never to `def.position`). **The draw MUST NOT re-apply it** — #1 silent-divergence hazard.
- Anchored birth (kit effects): stored relative to the MODEL root with the live attach rotation divided out; `pos = attach_inv·(placement.translation − anchor_pos + placement.rot·(placement.scale·wow_to_bevy(base)))`, redrawn `anchor_pos + attach_rot·pos`. Model-space (missiles/world): raw WoW-local, drawn `placement.transform_point(wow_to_bevy(pos))`.
- Render (benilla): **per-emitter dynamically-rebuilt `TriangleList` mesh (4 verts/6 indices per quad), NOT GPU instancing.** Billboard: `cam_right = camRot·X`, `cam_up = camRot·Y`; corner `= C ± half·cam_right ± half·cam_up` (size = **half-extent**, span = 2·half). XY-quad → flat plane basis via `placement.rot · wow_to_bevy(±axis)`. Tail streak `= −vel_world · t_eff`, `t_eff = flag0x400 ? min(tail_time, age) : tail_time`, degenerate when screen `l² < 7.7e-4` → billboard. Cell UV: `col = idx & (cols−1)` wraps, `row = idx >> log2(cols)` does not; index `floor(base + span·t) & 0xFF`.
- Ribbons: `MAX_EDGES = 512`, `edge_lifetime ≥ 0.25 s`, commit at `edges_per_second` (fractional accumulator), sag `2·g·dt` per edge, cross-section axis = live bone-local **+Y**, strip newest→oldest with `u = u0 + (u1−u0)·age01`, `age01 = (now−born)/edge_lifetime`.

## A.8 Ground decals (owning §9)
- benilla does **not** draw the flat quad; it projects the 4 posed corners (`corner_world = joint.affine() · inverse_bindpose · corner`) onto real terrain/WMO triangles (Sutherland-Hodgman clip, UVs bilerped), emitting the **terrain triangles' own up-facing winding** (constant normal `[0,1,0]`) — which is why the ring is a full 360° regardless of view side. `GROUND_FX_DEPTH_BIAS = 8192.0`, additive `(ONE,ONE)`, no depth write, LEQUAL test. Hidden when no surface in the slab (mid-air). Receiving set = terrain + WMO collider only (no doodads/units/liquid).
- **MSUI half-ring:** see §9 and Appendix B below.

## A.9 Missiles (owning §10)
- Homing per frame: `pos += (aim − pos)·(dt/arrive_in); arrive_in −= dt`; when `arrive_in ≤ dt` → handoff + despawn (no snap). `arrive_in = launch.distance(aim)/max(speed,ε) − queued`; ≤0 → impact on the spot, no flight entity.
- Release-keyed launch: `RELEASE_IDENTS = [$CSL, $CSR, $CST, $BWR]`; no-marker backstops = anim-end flush and a **0.25 s** never-played timeout; immediate launch when the cast kit has no body anim.
- `INFLIGHT_ANIM = 144`; flight model orients +X along velocity, no roll, `attach = None` (world-frozen trail).
- Fallback model chain: DBC effect (→ `Spells\ErrorCube.mdx` if nonzero-unresolvable) → ammo/weapon `ItemDisplayInfo` by shape (`model[0]`→Weapon\, else `model[1]`→Ammo\) → invisible-but-still-flies.

## A.10 Body animation (owning §11) & sound (owning §12)
- Carriers: `EmoteAnim{entity, anim_id:u16, seq:u64}` (one-shot), `WoundAnim{entity, anim_id:u16}` (flinch), `CastHold{anim_id:u16, spell_id:u32, ranged:bool}` (persistent pose). The 8..=10 wound-vs-emote split is in the **writer** (`play_kit`), not the driver.
- id→sequence: `ModelAnimations::resolve` (baked PlayableAnimationLookup, else DBC Fallback walk, else identity) then `pick_variation` (LCG-weighted). One-shot masked overlay weight `ONESHOT_OVERLAY_WEIGHT = 8.0` on the SpineLow subtree; full-body cross-fade over the sequence `blend_time`; locomotion rate = `speed / move_speed`.
- Billboard joint palette rewrite runs post-propagation: spherical `(-fwd, right, up)`; LockZ/X/Y keep the authored axis and rebuild the other two; final `Quat::from_mat3(from_cols(−by, bz, −bx))`.
- Sound events: `SpellKitSound::{Play{entity, kit_sound:u32}, StopHold{entity}}`, `MissileSound::{Start{entity, kit_sound, pos}, Stop{entity}}`. SoundEntries `LOOPING = 0x200` → looping tracked-to-entity voice vs positioned one-shot; missiles force-loop tracked to the projectile. Listener at the character head, not the camera.

---

# Appendix B — benilla's OWN known approximations (do NOT port as canonical)

Each agent flagged places where benilla itself deviates from the 1.12 reference client. A faithful C# port should know these are *benilla shortcuts*, not the ground truth to lock onto:
- **Missile homing** aims at the dest-attach point, not the client's ray-vs-bounding-sphere intercept ("same body point in practice").
- **`$BWR` ranged launch** fires from its own marker, not the true muzzle.
- **Missile miss** only reacts to Dodge/Block; pure Miss/Resist/Parry fizzle silently (no deflection/bounce).
- **Router `pending`** is a same-frame command-buffer overlay, not a timeout (there is no router `PENDING_TIMEOUT`).
- **Particle Y/Z tumble** uses the authored-min-DEAD form `(1+rand01)·(amax−amin)` — replicate exactly (do not "fix" it to `amin + u·range`).
- **Spline arc-length** is a 16-chord approximation.
- **Model-mode emitter-motion** fold is a known frame quirk (folds world Δ into local via `bevy_to_wow`).
- **Particle blends 5/6 (Mod/Mod2x)** fold to Alpha — no true mod path except rain's Mod2x.
- **Wound/masked overlays** weight-blend at ~8:1, not a true bone mask (slight base bleed).
- **LockX/LockY billboard** cross-product signs are not verified against shipped content (only LockZ/Spherical are).
- **Sound**: no max-voice/priority cap; out-of-range looping channels stop (no virtualize/resume); pitch keeps absolute-Hz FMOD semantics.
- **`dir_flags`** (directional animation variants) is plumbed everywhere but never consumed.
- Content gating in the FX layer is `parts.is_none()` only — there is no `HasVisibleContent` check; an empty-parts effect still "attaches" and self-terminates on its span.

---

# Appendix C — Port order & the MSUI half-ring decal bug

**Recommended port order:** §1 coords/skeleton → §2 data → §3 net → §4 orchestration → §6 attach → §5 FX render → §7/§8 particles+ribbons → §9 decals → §10 missiles → §11 body anim → §12 sound.

**MSUI half-ring (only ~180° of the AoE ring renders) — DIAGNOSED & FIXED (verified by capture).** §9 shows benilla emits real terrain triangles at their native up-facing winding, so its ring is always a full 360°. MSUI instead drew a single flat 4-corner quad (`SpellEffectMeshRenderer.BuildGroundQuad` + `RenderGroundQuads`). Ruled out: winding/culling (`RenderGroundQuads` already disables `CullFace`, and the strip order is a proper square, not a bowtie). **Actual cause:** the quad is one planar patch fit through 4 ground-snapped corners; over rolling terrain (Northshire) its far half sinks below the curved ground and depth-fails against the terrain, leaving a consistent world-half — the crescent. **Fix applied:** tessellate the quad into a 10×10 grid and snap EVERY grid vertex to ground height (`GroundTessellation`, bilinear position+UV, emitted as a triangle list), so the ring drapes the terrain everywhere — a cheap stand-in for benilla's true terrain-triangle projection. Verified: Frost Nova now renders a complete 360° ring from a top-down capture. This is the recommended long-term approach; benilla's full Sutherland-Hodgman terrain projection (§9) is only needed if you want the ring to clip cleanly against WMO edges and steep terrain.

---

# Section 13 — Completeness boundary and exhaustive spell-class matrix

This section closes the most dangerous ambiguity in a “complete spell implementation” request. There are
three different things that are often collapsed into the word *spell*:

1. **Gameplay resolution** — target selection, radius/chain expansion, damage, healing, power changes,
   dispels, aura application/removal, proc triggering, threat, immunity and miss results.
2. **Client control/UI** — whether a button is usable, which target packet is sent, cast bars, cooldowns,
   aura icons, tracking, shapeshift bar, crafting/lock/duel special flows, and combat text.
3. **Presentation** — body animation, visual kits, effect models, particles, ribbons, projected decals,
   missiles and sound.

benilla implements (2) and (3), with named gaps. It **does not implement (1)**. The source states this
directly: `crates/benilla-formats/src/spells/mod.rs:110-112` says spell damage/effect simulation remains on
the server and the client only needs display/casting metadata. `SMSG_SPELL_GO`, aura descriptors and the
combat-log packets therefore are authoritative inputs, not hints from which benilla reconstructs a spell.

## 13.1 What “every SpellEffect/AuraType” means in benilla

`Spell.dbc` contains three effect lanes. benilla preserves the numeric effect and aura metadata needed by
its UI, but it does **not** switch over every server `SpellEffect` or `AuraType`. Damage, heal, summon,
teleport, dispel, interrupt, threat, drain, resurrect, trigger-spell, knockback, charge, area-aura and the
rest all reach the client through the same cast/GO/aura/combat-log presentation paths. A faithful C# port
must keep unknown numeric values and must not reject a spell because its effect enum is absent from a local
switch.

The complete set of effect/aura numbers which **do** alter benilla client behavior is finite and is listed
below. This list was audited by searching every Rust comparison of `effect_1`, `effect_apply_aura`,
`SPELL_EFFECT_*` and `SPELL_AURA_*` in `crates/benilla` and `crates/benilla-formats`.

| Numeric value | Name / local meaning | Exact benilla behavior | Source |
|---:|---|---|---|
| effect 20 | DODGE | Passive tooltip line only | `crates/benilla/src/ui_tooltip.rs:239-257` |
| effect 22 | PARRY | Passive tooltip line only | `crates/benilla/src/ui_tooltip.rs:239-257` |
| effect 23 | BLOCK | Passive tooltip line only | `crates/benilla/src/ui_tooltip.rs:239-257` |
| effect 24 | CREATE_ITEM | Recipe product/icon and tradeskill output metadata | `crates/benilla-formats/src/spells/mod.rs:414-416,625-627`; `crates/benilla/src/ui_craft.rs:197-205`; `crates/benilla/src/ui_tradeskill.rs:251-267` |
| effect 33 | OPEN_LOCK | Derives `open_lock_type` from the matching effect's `EffectMiscValue`; GameObject click uses it to choose a known opener | `crates/benilla-formats/src/spells/mod.rs:250-255,565-573`; `crates/benilla/src/target/click.rs:190-243,413-500` |
| effect 36 | LEARN_SPELL | Maps a trainer wrapper to its `EffectTriggerSpell` | `crates/benilla-formats/src/spells/mod.rs:243-248,525-539` |
| effect 40 | DUAL_WIELD | A learned spell flips the inventory/UI dual-wield capability | `crates/benilla/src/ui_items/feed.rs:365-371` |
| effect 47 | TRADE_SKILL | Always usable in the usable walk and opens the profession UI instead of sending a cast | `crates/benilla-formats/src/spells/mod.rs:409-413`; `crates/benilla/src/ui_action/usable.rs:93-96`; `crates/benilla/src/ui_action/cast_send.rs:59-68` |
| effect 53 | ENCHANT_ITEM | Arms an item cursor and sends an item-targeted cast after a bag click | `crates/benilla-formats/src/spells/mod.rs:421-425`; `crates/benilla/src/ui_craft.rs:190-205,309-369` |
| effect 54 | ENCHANT_ITEM_TEMPORARY | Same item-target path as effect 53 | same references as effect 53 |
| effect 78 | ATTACK | Identifies the melee auto-attack and special spellbook/tooltip behavior | `crates/benilla-formats/src/spells/mod.rs:232-236,349-353`; `crates/benilla-formats/src/spells/display.rs:332-358` |
| effect 83 | DUEL | Finds the learned duel spell; `/duel` casts it at the selected player | `crates/benilla/src/ui_duel.rs:55-61,376-413` |
| effect 95 | SKINNING | Unit click finds the known skinning spell and sends it directly | `crates/benilla-formats/src/spells/mod.rs:417-420`; `crates/benilla/src/target/click.rs:317-345` |
| aura 36 | MOD_SHAPESHIFT | `EffectMiscValue` becomes the form id; admits and activates stance-bar rows | `crates/benilla-formats/src/spells/mod.rs:257-261,600-607`; `crates/benilla/src/ui_shapeshift.rs:6-25` |
| aura 44 | TRACK_CREATURES | Diverted from ordinary aura icons into minimap tracking | `crates/benilla-formats/src/spells/mod.rs:356-362`; `crates/benilla-formats/src/spells/display.rs:423-435`; `crates/benilla/src/ui_aura.rs:197-217` |
| aura 45 | TRACK_RESOURCES | Same tracking path | same references |
| aura 151 | TRACK_STEALTHED | Same tracking path | same references |

No other effect/aura enum changes local gameplay or presentation dispatch. It may still affect tooltip token
data, a server outcome, a public aura slot, or the spell's generic visual stages. That is the exhaustive
answer to “every effect type” **as implemented by benilla**, not an accidental omission.

## 13.2 Exhaustive client spell-class matrix

| Spell class | How benilla recognizes it | Cast/target path | Presentation/outcome path |
|---|---|---|---|
| Instant self spell | cast time from wire is zero; implicit target 1 or autoself fallback | `CMSG_CAST_SPELL` self target | START and GO may arrive same frame; pending overlay prevents a stuck hold; speed 0 impacts each GO hit immediately |
| Timed self spell | same target, nonzero START cast time | normal shared send | precast hold until GO/fail; cast kit on GO; impact per GO hit |
| Instant/timed hostile single target | implicit 6, 53, 25 or 63 plus reaction/range checks | unit-guid target | hit/miss list is server truth; speed selects instant impact versus missile |
| Friendly single target | implicit 21 or 45 | selected friendly unit; autoself if absent and setting enabled | generic hit/impact/aura pipeline |
| Corpse ally | implicit 5 | selected corpse; no live-unit substitute | generic cast; server judges resurrection/effect |
| Party/raid target | implicit 35, 57 or 61 | benilla only resolves the player itself; group-member targeting is a named limitation | generic server outcome if such a cast is nevertheless received |
| Ground/source/destination AoE | target masks carry source/destination vectors; implicit 16 is identified | generic action path refuses because cursor/world targeting is not built; no outbound builder exists | incoming GO still renders every server hit; destination arrival visuals are not implemented |
| Caster-centered/unit-centered AoE | server expands the area into GO hit/miss GUID lists | ordinary self/unit cast | speed 0: impact kit on every hit now; speed >0: one missile per hit and miss; radii never drive local target expansion |
| Chain spell | `effect_chain_target[3]` is parsed | ordinary unit cast | chain victims are simply the ordered GO hit list; no local chain geometry or target selection |
| Direct damage | server damage packet | generic | portrait/center/world combat text per Section 17; visual impact is independent of damage log |
| Direct heal | server heal packet | generic friendly/self | portrait + center feedback only; no world floating heal number |
| Periodic damage / DoT | aura descriptor plus periodic-aura log type 3/89 | generic aura | aura icon/state FX persist; each server tick feeds combat UI; no local tick timer |
| Periodic heal / HoT | periodic-aura log type 8/20 | generic aura | portrait + center tick feedback; no world heal number |
| Periodic energize | periodic-aura log type 21/24 | generic aura | center power feedback only |
| Leech/drain | periodic-aura log type 64 | generic | decoded; no separate world-text visualization in the current apply path |
| Damage shield | `SMSG_DAMAGE_SHIELD` | server reactive outcome | damage/miss UI path; no locally simulated proc |
| Buff | positive aura slot 0-31 | server applies descriptor | buff icon, duration/stack/cancel, persistent state-kit effect models |
| Debuff | aura slot 32-47 | server applies descriptor | debuff icon, dispel type, duration only when it is the player's own aura feed, persistent state kit |
| Passive | spellbook attribute/effect rules | not ordinarily cast | no cast line where omitted; aura/public visual behavior if server exposes it |
| Tracking aura | aura type 44/45/151 | cancel/cast as aura | removed from aura bar and becomes the current minimap tracking choice |
| Shapeshift/stance/stealth form | aura 36 or force-admit flag | stance bar; active cancel or shared cast | form byte is authoritative; state-kit/aura visuals use generic aura watcher |
| Channeled spell | public `UNIT_CHANNEL_SPELL` plus self-only channel timing messages | generic cast | channel kit hold/effects while descriptor is nonzero; cast bar from channel messages |
| Melee auto-attack | effect 78 | attack engage/disengage, not a normal spell cast | melee swing pipeline, not SpellVisual missile routing |
| On-next-swing ability | Attributes mask exposed by `on_next_swing()` | queued until swing; 100 yd self-range short circuit | pending queue/cooldown behavior; actual outcome arrives from server |
| Basic ranged attack | ranged attributes/slot, weapon visual fallback | normal or auto-repeat | ranged sheath/hold, body release marker, ammo/weapon missile fallback, per-shot GO |
| Ranged auto-repeat | auto-repeat attributes | toggle active id; START once, repeated GO packets | each GO reasserts ranged hold and emits projectile/impact; stop opcode/state clears it |
| Wand autorepeat | `cancel_auto_repeat_when_cast()` special rule | casting another eligible spell cancels wand shot | otherwise ranged fallback path |
| Profession opener | effect 47 | opens tradeskill UI, no cast packet | none until recipe execution |
| Recipe/create item | effect 24 | shared non-item craft path | server inventory result; normal cast visuals if packets say so |
| Item enchant/temp enchant | effects 53/54 | cursor then item GUID packet | normal server START/GO visuals after send; cursor limitations are documented below |
| Open lock / gathering | effect 33 | GameObject click selects matching spell and sends GO target | target GO lid animation occurs on GO in addition to spell visual |
| Skinning | effect 95 | corpse unit click direct send | normal server visual/outcome |
| Duel | effect 83 (spell 7266 in tested 5875 data) | ordinary unit-target cast | duel state then comes from dedicated duel packets/descriptors |
| Server kit push | `SMSG_PLAY_SPELL_VISUAL` | no cast | stage-0 discrete kit every send; body anim dedups, FX/sound do not |
| Environmental damage art | damage type 0-5 maps through `EnvironmentalDamage.dbc` | server log or local fall predictor | discrete KitPush; hard-landing prediction can duplicate later server art |
| Aura state prop | a public aura spell has a state kit | no separate cast | persistent models for full aura lifetime; impact also does the short state-stage flash leg |
| Loot sparkle | dead + lootable dynamic flag | not a spell | persistent hardcoded loot effect at base attach |
| Level-up ding | level descriptor changes | not a spell cast | transient hardcoded model with its own `$SND(888)` event |

The visual cells above are implemented by the renderer sections 4-12. The control/outcome cells are expanded
in Sections 14-17.

## 13.3 Four invariants that prevent “works for Fireball” implementations

1. **Do not branch rendering on damage/heal/buff enum names.** Resolve `spellId → SpellVisual → stage kit →
   nine effect slots` for every spell, and use GO/aura/channel state to choose stages.
2. **Do not infer AoE locally.** The complete affected set is `SMSG_SPELL_GO.hits/misses`. Radius and chain
   fields are metadata here, not presentation-time target queries.
3. **Do not equate a spell with particles.** A kit effect model can contain ordinary/skinned mesh parts,
   billboard cards, flat projected ground geometry, particle emitters, ribbon emitters, animation event
   sound, or any combination. `attach_effect_visuals` fans all of them out
   (`crates/benilla/src/entities/spell_fx.rs:297-490`).
4. **Do not equate an aura icon with aura VFX.** The UI feed uses flag liveness/order/filtering, while the
   world state-kit watcher independently scans public aura spell ids. Both must exist.

---

# Section 14 — Target acquisition, local cast admission, and outbound packet paths

## 14.1 Wire target mask and decoder

The target flags are declared in `crates/benilla-protocol/src/messages/spells.rs:30-52`:

| Flag | Value | Payload/meaning |
|---|---:|---|
| UNIT | `0x0002` | packed unit GUID |
| ITEM | `0x0010` | packed item GUID; decoder currently reads/drops it |
| SOURCE_LOCATION | `0x0020` | source coordinates; reads transport GUID + XYZ on the wire |
| DEST_LOCATION | `0x0040` | destination coordinates; surfaced as a vector |
| CORPSE_ENEMY | `0x0200` | packed corpse GUID |
| GAMEOBJECT | `0x0800` | packed GO GUID |
| TRADE_ITEM | `0x1000` | trade slot/item value; currently read/dropped |
| STRING | `0x2000` | string payload; currently read/dropped |
| CORPSE_ALLY | `0x8000` | packed corpse GUID |

`read_spell_cast_targets` is `messages/spells.rs:69-127`. One `target_guid` field is exposed and the decoder's
precedence is unit, then GameObject, then corpse. Item/trade/source/string bytes are consumed so packet
alignment remains correct but are not retained in the public target structure. Destination is retained.
Current cast consumers use the unit/GO GUID; the missile path homes to GO hit-list entities, not to the
decoded destination.

Outbound helpers are intentionally narrower (`messages/spells.rs:311-362`): self/unit cast builders,
GameObject mask `0x4800`, item casts, and cancel-aura exist. There is no generic outbound ground/source,
string or trade-target builder. Do not mistake the broad **decoder** for an equally broad cast UI.

## 14.2 `SMSG_SPELL_START` and `SMSG_SPELL_GO` layouts

`SpellStart` (`messages/spells.rs:142-180`) reads item/caster GUID, caster GUID, spell id, cast flags,
cast-time milliseconds, target block and — when flag `0x20` is set — ammo display/inventory type. `SpellGo`
(`:187-247`) reads caster identifiers, spell id, flags, a raw `u64` hit list, a miss list of GUID + reason
(reason 11/Reflect also consumes an extra result byte), target block and optional ammo. The GO packet carries
no travel time. benilla derives travel duration from current source/target positions and `Spell.dbc.Speed`.

Application is in `crates/benilla/src/net/apply/spells.rs:156-455`:

- every START, including an instant one, emits a visual `Start` edge;
- only `cast_time_ms > 0` creates the local `Casting` component;
- every GO removes the matching `Casting`, emits visual `Go`, stores entity-resolved hit/miss vectors in
  `SpellGoTargets`, completes the player's matching cast state and starts its local cooldown;
- a GO GameObject target with a known open-lock spell also triggers the lid transition
  (`crates/benilla/src/go_anim.rs:279-305`);
- misses generate combat words; Reflect anchors its word to the caster rather than the intended target.

## 14.3 Implicit-target classification

`crates/benilla/src/ui_action/cast_target.rs:182-198` is the complete local mapping:

| Implicit target id | benilla class/result |
|---:|---|
| 1 | self |
| 5 | allied corpse |
| 6, 53 | hostile unit |
| 16 | ground/destination family — recognized but generic resolver refuses it |
| 21, 45 | assist/friendly unit |
| 23 | GameObject — handled by specialized click flow, not generic action targeting |
| 25, 63 | unit |
| 26 | locked/special — refused |
| 35 | party — only self is implemented |
| 57, 61 | raid — only self is implemented |

The packed target word's relevant kind bits are 36-43 and target-type values 61-69
(`cast_target.rs:36-69`). Resolution (`:262-310`) is: explicit self word → self; reject families outside
the implemented unit set; try current selection with relation predicate; if none and AutoSelfCast is true,
use self when relation permits. AutoSelfCast defaults true in the app despite the reference variable's
documented zero default (`:164-174`). Hostile uses `can_attack`; assist currently approximates CanAssist with
reaction rank ≥4 (`:209-256`). Party/raid selection beyond self, ground cursor, item target, GO target and
string target are explicitly refused by the generic path.

## 14.4 Plain cast send: exact ordered gate walk

The shared send function and its order are documented and implemented in
`crates/benilla/src/ui_action/cast_send.rs:1-281`. Preserve the order because earlier failures must not run
later visible side effects:

1. resolve spell metadata;
2. if effect 47/TRADESKILL, queue the profession UI open and return without a cast packet;
3. if auto-repeat, toggle/arm it as that special action;
4. refuse spell/category cooldown and GCD locks;
5. classify normal, ranged, or on-next-swing behavior;
6. refuse mounted casting unless Attributes bit `0x01000000` allows it;
7. verify reagents and totems;
8. resolve target and its relation;
9. perform the pre-send min/max range test;
10. cancel a currently armed wand autorepeat when the new spell requires it;
11. commit: ranged sheath snap / autorepeat state / outbound command;
12. arm `PendingCast` or `QueuedMeleeSpell`, engage auto-attack when the spell says so;
13. start GCD locally.

The dynamic `spell_usable` walk is a related, not identical, button-grey predicate
(`crates/benilla/src/ui_action/usable.rs:1-188`). Its modeled order is:

- profession effect early success;
- death unless castable-while-dead;
- reagent counts and totem/tool presence;
- equipped item class/subclass match (unknown item template fails open);
- form/stance admission;
- stealth-only;
- out-of-combat-only;
- caster aura-state bit;
- target aura-state bit and enemy/friendly relation branch;
- cooldown only for the cooldown-on-event attribute;
- flat + percentage power cost, where percent mana uses base mana and other powers use max power.

Only the last leg returns `notEnoughMana=true`. Named omissions at `usable.rs:14-19` are caster
silence/pacify/mechanic immunity, self-only identity attributes, durability and some AttributesEx3
subconditions, ghost state, and exact CanAssist. A C# clone of **benilla** should retain those omissions; a
clone of the original 1.12 client should implement them separately.

## 14.5 Range math

`crates/benilla/src/ui_action/state.rs:43-148` implements the pre-send squared-distance gate:

- on-next-swing spells short-circuit to `[0,100]` yards;
- melee range = `max(5.0, casterReach + targetReach + 1.3333)` and minimum zero;
- an authored `{0,0}` row means no test;
- ranged max = authored max + bare reach sum;
- ranged min is authored min + reach sum **only when authored min is nonzero**;
- missing target reach leaves authored min/max unchanged;
- compare 3D squared distance: `d² > max²` → reason `0x59`; nonzero min and `d² < min²` → `0x76`;
- missing range row/distance fails open to the server.

The named deviations at `state.rs:109-117` are the unmodeled hostile-PvP +2.6667 yd max bonus,
item range-mod leg (a no-op for tested player weapons), and a simplified missing-target melee reach fallback.

## 14.6 Every specialized outbound path

### Self/unit normal casts

Use the shared send path and `CastSpellSelf`/unit builder (`benilla-protocol/.../spells.rs:311-321`). Instant,
timed, damage, heal, buff and debuff casts do not use separate packet functions.

### Ground AoE

Implicit target 16 and destination payloads are known to the decoder, but the generic cursor is absent
(`cast_target.rs:182-198,262-310`). Therefore Benilla cannot originate arbitrary Blizzard/Rain-of-Fire style
ground casts through this UI. It can still display an incoming spell's casts and per-GUID impacts. The
decoded destination is not used to spawn a destination ground-arrival kit; `SpellVisual` field 13 is also
deferred (`crates/benilla-formats/src/spell_visual.rs:93-123`). This is a hard completeness limitation, not
an implicit feature.

### Ranged, auto-repeat, wand and next-swing

Classification helpers live at `crates/benilla-formats/src/spells/display.rs:258-407`. Ranged START snaps
sheath state 2; GO re-snaps it and maintains `RangedHold`. Auto-repeat activation gets one START, then the
server emits repeated GO packets; each GO plays the release/missile. Wand-specific casts cancel their active
repeat when required. On-next-swing spells arm `QueuedMeleeSpell` rather than behaving like an immediate
normal cast.

### GameObject open-lock/gathering

`crates/benilla/src/target/click.rs:190-243,413-500` scans known spells for an OPEN_LOCK effect whose
`EffectMiscValue` matches a skill slot in the GO's Lock record, checks only the implemented reagent/totem
requirements, then sends the specialized GO-target packet. It does not run the complete shared send gate.
On GO, `go_anim.rs:279-305` sets active state and initiates the object's transition animation.

### Skinning

`target/click.rs:317-345` finds a known spell with effect 95 and sends it at the clicked unit. Like open-lock,
this is a specialized direct path, not the full shared admission pipeline.

### Item enchant / temporary enchant

`crates/benilla/src/ui_craft.rs:190-205,309-369` detects effects 53/54, arms a pending craft and waits for a
bag item click, then sends the item GUID. It intentionally lacks original-client cursor artwork, replacement
confirmation, paper-doll targeting and trade-window targeting. The item leg also bypasses several shared
send gates/tail state changes; reproduce that if the goal is exact benilla behavior.

### Profession opener and recipe execution

Effect 47 never reaches the network; it opens the TradeSkill frame (`cast_send.rs:59-68`). Recipe execution
uses `ui_craft.rs`; create-item effect 24 supplies product metadata, and a non-item-target recipe uses the
shared spell path. Server inventory changes remain authoritative.

### Duel

`crates/benilla/src/ui_duel.rs:376-413` locates the player's learned effect-83 spell and casts it as an
ordinary unit-target spell. The tested 5875 DBC has exactly spell 7266 (`ui_duel.rs:482-505`). Request,
countdown, bounds and completion are then driven by duel-specific server events, not inferred from spell GO
(`ui_duel.rs:73-213,224-350`).

### Shapeshift bar

Inactive form buttons call the shared spell-send path; the active form sends CancelAura unless its form row
blocks cancellation. Full details are in Section 15.

---

# Section 15 — Buffs, debuffs, tracking, shapeshifts, channels, and aura UI

## 15.1 Aura descriptor layout and liveness

The unit update-field readers are the source of truth, not a local duration calculation:

- 48 spell-id slots begin at `UNIT_FIELD_AURA`; slots 0-31 are positive, 32-47 negative
  (`crates/benilla-protocol/src/messages/update_object/fields/mod.rs:150-200`);
- parallel flags, levels and application-count words are read by slot;
- a slot is live iff `flags & 0x0E != 0`, **not** iff the spell id is nonzero;
- visible stack count is the stored application byte plus one
  (`crates/benilla-protocol/src/messages/update_object/fields/unit.rs:64-107`).

This liveness law is used both by the aura UI and the public aura spell-id iterator which feeds world state
VFX. Reproducing only the `spellId != 0` check causes recycled/stale slots to appear or disappear at the
wrong time.

## 15.2 Buff/debuff bar ordering and filtering

The complete feed is `crates/benilla/src/ui_aura.rs:1-443`.

- **Player's own unit:** maintain a cache in insertion order. Existing survivors keep their order, removed
  rows are packed away, and newly observed auras append (`:126-152`). This is not raw slot order.
- **Other target:** enumerate live slots in ascending raw slot order.
- **Target is self:** mirror the player's insertion order and known durations.
- helpful/debuff classification is purely `slot < 32` versus `slot >= 32`.
- spell records carrying the hidden-aura attribute are filtered. Missing spell metadata fails open so a
  server aura is not silently lost (`:180-195`).
- tracking aura types 44/45/151 are removed from the ordinary aura list; the last matching raw slot wins as
  the current tracking selection (`:197-217`).
- the cancelable property is aura flags bit `0x01`.
- debuff type/color text is looked up through the `SpellDispelType.dbc` catalog
  (`crates/benilla-formats/src/spells/dispel_types.rs:1-98`).
- the feed emits discrete `UNIT_AURA` and `PLAYER_AURAS_CHANGED` events on changes; tracking joins the same
  comparison key (`ui_aura.rs:410-442`).

## 15.3 Aura durations, refresh and slot reuse

`SMSG_UPDATE_AURA_DURATION` is self-only and keyed by slot. It arrives before the descriptor delta that tells
the client which spell occupies the slot (`benilla-protocol/src/events.rs:548-551`). benilla buffers
`{total, expires, received}` by slot, then joins it to the live descriptor. A one-second freshness allowance
rejects an old timer when a slot is quickly recycled (`ui_aura.rs:48-79,263-303,427-431`). Permanent auras
never receive a timer. Other units' target auras therefore show duration zero; the selected player mirrors
the self cache (`ui_aura.rs:346-386`).

Server refresh is authoritative. benilla does not use `SpellDuration.dbc` to count down active auras; that
DBC is a tooltip source. A C# port should store an absolute local expiration calculated at packet receipt,
preserve the receipt timestamp, and apply the same freshness check on descriptor join.

## 15.4 Aura cancellation

The UI sends `CMSG_CANCEL_AURA` by **spell id**, not by slot (`ui_aura.rs:410-442`;
`benilla-protocol/src/messages/spells.rs:361-362`). The server removes the descriptor; the local icon and state
VFX disappear only when that authoritative update arrives.

## 15.5 Persistent aura state-kit visuals

World aura visuals are independent of the icon cache. `arm_aura_state_fx` scans every streamed unit's public
live aura spell ids each frame (`crates/benilla/src/creature_anim/spell_visual.rs:821-898`):

1. sort and deduplicate spell ids, so two caster-owned slots for one spell produce one state instance;
2. for ids which left, emit `SpellKitFx::Reap { class: AuraState }`;
3. for newly present ids, resolve `SpellVisual.state`, collect all nine effect-model slots and begin them
   persistent with class `AuraState`;
4. retain only pairs this watcher actually armed, so removing an aura cannot reap a same-spell cast/channel
   `Hold` instance;
5. a refresh which never removes the spell id produces no replay; remove→add reaps and begins again;
6. streaming in a unit which already has the aura immediately creates the persistent state prop.

`play_impact` also performs the original stage order's short state-kit flash leg, but it suppresses that
leg's effect models and only plays its self-gated sound/body handling; the persistent effect models belong to
the aura watcher (`spell_visual.rs:377-415,821-839`). A known residual is that the aura watcher does not replay
a real state-kit body animation on the add edge; shipped/live examined state kits use the none sentinel
(`:834-839`).

## 15.6 Buffs, debuffs, periodic effects and proc behavior

There is no local `Buff`, `Debuff`, `Dot`, `Hot`, `Proc` or `AreaAura` simulation class. They are combinations
of:

- a public aura descriptor (existence, positivity, flags, stacks);
- a self aura-duration packet when applicable;
- optional state-kit persistent world art;
- periodic combat-log packets for server ticks;
- later GO/combat-log/aura updates for triggered spells or procs.

`effect_apply_aura[3]`, amplitude, base points, dice, misc value, trigger spell, radius and chain count are
parsed for tooltip/UI metadata (`crates/benilla-formats/src/spells/display.rs:151-176`), but benilla does not
schedule ticks or triggers from them. Implementing those locally would produce double ticks against a real
server.

## 15.7 Tracking

`SpellDisplay::tracking_aura()` checks all three aura lanes for 44, 45 or 151
(`display.rs:423-435`). The aura UI's last matching raw slot becomes the active minimap tracking definition.
The actual minimap dot classifier then reads the corresponding aura lane and its misc value
(`crates/benilla/src/minimap/blips/dots.rs:501-510`). Tracking auras are therefore real server auras with
special UI routing, not a standalone local toggle.

## 15.8 Shapeshift, stances and stealth

`crates/benilla/src/ui_shapeshift.rs:1-229` is complete:

- a known spell is admitted when AttributesEx2 bit `0x2` is clear and it either has aura 36 or force-admit
  bit `0x10`;
- order is ascending signed `StanceBarOrder`, negatives last, spell id as tiebreak;
- active means the player's form byte equals the spell's aura-36 `EffectMiscValue`;
- active texture uses ActiveIconID when nonzero, else SpellIconID; inactive uses SpellIconID;
- active rows are hardcoded castable; inactive rows run the full `spell_usable` predicate;
- cooldown is the form spell's ordinary spell/category cooldown;
- clicking an inactive row uses shared `send_spell_cast`;
- clicking the active row sends CancelAura only if `SpellShapeshiftForm.dbc` flags allow cancellation;
  warrior stances with blocking bit `0x2` are a silent no-op;
- any list/state/cooldown difference emits `UPDATE_SHAPESHIFT_FORMS`.

The general usability form gate is `SpellDisplay::usable_in_form`
(`crates/benilla-formats/src/spells/display.rs:437-462`). A form row's stance flag distinguishes warrior
stance/stealth from a true shapeshift: ordinary spells remain usable in a stance but respect NOT_SHAPESHIFT
and required-stance masks in cat/bear/etc. Form row parsing is
`crates/benilla-formats/src/spells/forms.rs:1-84`.

## 15.9 Channels

There are two separate inputs and they must not be conflated:

- public `UNIT_CHANNEL_SPELL` on every streamed caster determines whether the world channel pose, kit sound
  and persistent channel effects are active;
- self-only `MSG_CHANNEL_START/UPDATE` packets drive the player's cast bar and remaining time.

The visual poll is `spell_visual.rs:753-818`. On a nonzero edge it stops/reaps a prior hold, resolves the
channel kit, creates `CastHold`, plays the kit sound once and begins all effects persistent with class Hold.
An unchanged id does nothing, so the clip and sound do not restart every frame. On clear it removes only the
ending spell's matching hold, stops its loop and reaps its effects. Replacement reaps the previous channel
before arming the next. Range-despawn removes the edge-cache row.

The cast-bar packet semantics are in Section 16. An observer has no channel timing bar from these packets;
the descriptor is sufficient for world presentation.

## 15.10 Passive/hidden and aura-state edge cases

- hidden aura is a Spell attribute test (`display.rs:409-421`), independent of positive/negative slot;
- tracking is filtered after liveness and metadata resolution;
- state VFX deduplicates by spell id, while icon entries remain per live slot;
- an aura with no state kit still appears in the UI;
- a state kit with no resolved effect models arms nothing;
- missing DBC data causes no spell visuals (`spell_visual.rs:452-454,853-855`) but the server aura fields still
  exist; UI metadata uses fail-open conventions where documented.

---

# Section 16 — Cast bar, cancellation, cooldowns, auto-repeat, and next-swing state

Presentation timing is split among four independent clocks. A port that uses one “spell timer” will drift:

1. START's server `cast_time_ms` drives the precast/cast bar;
2. channel start/update messages drive channel remaining time;
3. `Spell.dbc.Speed` and launch distance drive missile arrival;
4. effect-model/M2 sequence spans and emitter clocks drive visual lifetime.

## 16.1 Local cast state objects and slack windows

`crates/benilla/src/ui_cast.rs:22-41` defines the player-side state machine:

- `PendingCast`: provisional client request, five-second timeout plus two seconds of cleanup slack;
- `QueuedMeleeSpell`: on-next-swing ability waiting for the server/swing path;
- active normal cast: START-derived end time with two seconds of stale-state slack;
- `ActiveChannel`: channel remaining time with two seconds of slack.

These are UI/guard objects, not combat authority. Remote `Casting` components are created from START only
when the wire cast time is nonzero (`net/apply/spells.rs:156-258`) and are removed by matching GO/fail.

## 16.2 Cast-result and failure behavior

`crates/benilla/src/net/apply/spells.rs:75-154` applies the player's cast result:

- only a failure result enters this path;
- clear the locally started GCD, but do not erase arbitrary spell/category cooldowns;
- cancel a matching auto-repeat except for reason `0x17`;
- repaint/stop the cast bar only if the currently displayed cast id matches;
- clear matching `PendingCast` and `QueuedMeleeSpell`;
- publish the mapped error text;
- remove the matching `Casting` component and emit visual `Fail`, which reaps the precast hold/effects/sound
  without playing the cast release.

`SMSG_SPELL_FAILED_OTHER` applies the same visual release logic to observed casters
(`net/apply/spells.rs:457-504`). Cast-failure text and reason mapping are centralized in
`crates/benilla/src/ui_action/cast_fail.rs:1-403` and `ui_action/errors.rs:1-416`; a C# port should copy those
tables as data rather than inventing partial switch statements.

## 16.3 Cast delay

`SMSG_SPELL_DELAYED` supplies a delay in milliseconds (`benilla-protocol/src/messages/spells.rs:259-267`).
For the matching player cast, `net/apply/spells.rs:506-528` extends the tracked end time and sends the UI
`SPELLCAST_DELAYED(ms)` event. It does not restart the visual precast kit; the sustained `CastHold` simply
continues.

## 16.4 Cast bar event contract

`crates/benilla/src/ui_cast.rs:401-477` emits the legacy UI contract verbatim:

| Situation | UI event/arguments |
|---|---|
| timed normal START | `SPELLCAST_START(name, milliseconds)` |
| normal GO/success | `SPELLCAST_STOP` |
| server failure | `SPELLCAST_FAILED` |
| local interrupt | `SPELLCAST_INTERRUPTED` after the stop edge used by the red bar |
| delay | `SPELLCAST_DELAYED(milliseconds)` |
| channel start | `SPELLCAST_CHANNEL_START(milliseconds, name)` |
| channel remaining update | `SPELLCAST_CHANNEL_UPDATE(milliseconds)` |
| channel update zero | `SPELLCAST_CHANNEL_STOP` |

Instant spells and ranged-slot basic attacks do not show the player's normal cast bar. They still emit
START/GO visual edges and animations.

## 16.5 Local movement/Escape cancellation

The ordered cancellation walk is `ui_cast.rs:226-399`:

- forward/back/strafe or jump counts as movement; turn and pitch alone do not;
- normal casting checks interrupt flag `0x01`;
- channel movement checks channel-interrupt flag `0x08`;
- Escape first stops auto-repeat and consumes that keypress;
- otherwise Escape cancels a normal cast, but cannot cancel a channel;
- moving during a channel sends the cancel-channelling command, but keeps local channel state/bar until the
  server's channel update/descriptor clear arrives;
- a local normal-cast cancel emits visual Stop/Fail semantics and the interrupted red-bar sequence.

`SpellStopCasting` only models the normal cast path; channel stop uses its distinct command and update.

## 16.6 Cooldown storage model

`crates/benilla/src/cooldowns.rs:1-529` stores three possible timer families plus an event hold:

- spell/item own recovery;
- category recovery;
- global cooldown category/time;
- `on_hold`, parked until a cooldown-on-event release.

When asked for one action's cooldown triple, benilla resolves all applicable own/category/GCD records and
returns the one with the longest remaining duration. `is_on_cooldown` intentionally excludes the GCD;
`gcd_locked` is a separate test (`cooldowns.rs:193-218,261-294,403-529`).

## 16.7 Exact cooldown timing edges

- **GCD starts at local send**, before the server reply (`cooldowns.rs:261-277`; shared cast-send tail).
- **Cast failure clears only that GCD** (`:280-294`; `net/apply/spells.rs:75-154`).
- **Spell/category cooldown starts on the player's matching GO**, not START
  (`net/apply/spells.rs:312-392`; `cooldowns.rs:193-218`).
- ranged basic attack padding is folded into category recovery by `start_spell` (`cooldowns.rs:193-218`).
- item cooldowns use item-spell/category metadata; one special received item path uses a fixed 30 seconds
  (`cooldowns.rs:223-255,380-400`).
- cooldown-on-event spells park until the event release path (`:296-311`).
- server cooldown-list packets overwrite/populate authoritative timers (`:335-377`).
- UI sweeps are absolute start+duration triples; the UI extrapolates progress rather than requiring a
  per-frame event (`ui_action/state.rs:150-190`).

## 16.8 Auto-repeat and ranged hold lifecycle

The states are separate:

- local `AutoRepeatActive` is the armed spell id and drives action-button current/auto-repeat state;
- `AutoRepeatArmed`/`RangedHold` in the animation layer maintain the drawn ranged stance;
- `NockedAmmo`/`NockLatch` attach and clear ammo at `$BWP`/release markers
  (`crates/benilla/src/creature_anim.rs:95-195`);
- START for a ranged-attack spell snaps sheath state 2; auto-repeat generally gets one START on activation;
- every repeated GO reasserts ranged hold/sheath and plays the release kit/missile
  (`creature_anim/spell_visual.rs:509-556,596-644`);
- server cancel-auto-repeat or a local toggle/cast rule clears the local active state
  (`net/apply/spells.rs:530-557`);
- Escape stops auto-repeat before it attempts to cancel a normal cast.

Body animation release markers `$CSL`, `$CSR`, `$CST` and `$BWR` launch queued missiles; animation-end and
0.25-second timeout are backstops (Section 10).

## 16.9 Next-swing abilities

`SpellDisplay::on_next_swing()` and `initiates_auto_attack()` are
`crates/benilla-formats/src/spells/display.rs:382-407`. Such a press arms `QueuedMeleeSpell`, may engage melee
auto-attack, and uses the range resolver's `[0,100]` self shortcut. It is cleared on matching success/failure
or replaced by later state. benilla does not locally compute the strike, resource spend or damage; the
server's swing/GO/combat-log remains decisive.

## 16.10 Visual lifetime versus cast-bar lifetime

Do not bind these together in C#:

- a timed START begins a persistent **precast visual hold** and a cast bar;
- GO/fail reaps the precast hold even if the cast bar object is absent/stale;
- instant START+GO still begins and reaps the hold in packet order within one frame;
- a channel visual follows the public descriptor, while its cast bar follows self-only channel messages;
- cast-kit effects are transient for their authored model span, unrelated to missile flight;
- aura state effects persist for descriptor lifetime, unrelated to the cast which applied them.

---

# Section 17 — Direct, periodic, AoE, miss, heal, power, shield, and environmental outcomes

This is the exhaustive presentation side of server spell outcomes. It is deliberately separate from visual
impact: a spell can play its impact kit even when no combat number is shown, and a combat-log packet can
arrive without a visual spell row.

## 17.1 Combat-log wire types

`crates/benilla-protocol/src/messages/combat_log.rs:1-299` decodes:

| Packet/outcome | Fields consumed | Apply/presentation |
|---|---|---|
| non-melee spell damage | caster, target, spell, damage, school, absorb, resist, periodic/blocked/critical flags and state | source filter, UNIT_COMBAT, center/portrait/world damage feedback |
| periodic aura damage | log type 3 or 89 plus amount/school/absorb/resist | periodic damage UI |
| periodic heal | type 8 or 20 plus amount | portrait + center, no world number |
| periodic energize | type 21 or 24 plus amount/power | center power feedback |
| periodic leech | type 64 plus damage/heal-related values | decoded; no dedicated floating feedback in current apply branch |
| direct spell heal | caster, target, spell, amount, critical | portrait + center; no world number |
| energize | caster, target, spell, power type, amount | center only when applicable |
| damage shield | owner/attacker, spell, damage, school | damage/shield presentation path |
| environmental damage | target, damage type, damage, absorb, resist | health/combat text plus EnvironmentalDamage kit mapping |
| spell miss | caster, target, spell, miss reason | miss word and optional defense reaction |

Periodic log type dispatch is at `messages/combat_log.rs:89-157`; heal/energize/shield/environmental/miss wire
records are `:159-299`. Unknown periodic types must still be consumed/handled conservatively so the session
stays aligned.

## 17.2 Source/anchor filter

`crates/benilla/src/net/apply/combat_log.rs:1-567` only creates the player's primary floating/center feedback
when the source is the player or the player's pet. Other sources are suppressed from that personal feed.
Damage anchored to the player's own unit is suppressed from overhead world text to avoid drawing it on the
player, but `UNIT_COMBAT` UI events are not gated the same way. These filters are presentation policy, not a
claim that the server packet was invalid.

## 17.3 Direct damage

`net/apply/combat_log.rs:182-247`:

- resolve source/target entities and spell name/school where available;
- emit the legacy `UNIT_COMBAT` event;
- player/pet-source damage can produce center feedback and overhead target text;
- critical affects the world-text category/animation, but the center event still uses the spell-damage
  family rather than a distinct critical event;
- absorb/resist/blocked metadata comes from the packet; benilla does not recompute mitigation.

## 17.4 AoE and multi-target damage

There is no distinct AoE combat-log renderer. The server sends one spell GO containing the entire hit/miss
set for visual impacts and the applicable per-target damage/miss logs for numbers. The router loops all
`go.hits` for Speed 0 and all hits+misses for Speed >0
(`creature_anim/spell_visual.rs:677-750`). Thus:

- an instant caster-centered AoE can play its impact stage on every hit entity in the same frame;
- a projectile multi-target spell creates one homing entity per hit and miss;
- misses never receive an impact kit on arrival;
- radius, cone, party, chain and max-target calculations are nowhere in the client;
- decoded destination coordinates do not substitute for a hit list.

## 17.5 Periodic auras

`net/apply/combat_log.rs:249-380` switches on the already decoded periodic outcome:

- damage ticks use the damage feedback family;
- heal ticks update portrait/center feedback only;
- energize ticks use center power feedback;
- leech is accepted but produces no independent world-text leg.

The aura UI countdown does not generate these ticks. Each displayed tick corresponds to a server log packet.
This prevents local/server drift after dispel, immunity, lag, refresh or haste-like rule changes.

## 17.6 Direct healing and power

Direct healing is `net/apply/combat_log.rs:424-458`; energize is `:460-477`.

- direct and periodic heals intentionally have **no overhead world number** in benilla;
- healing produces portrait and center feedback where applicable;
- energize produces center feedback, not overhead target text;
- critical heal is carried but does not invent a world-number lane.

## 17.7 Misses, immunity, reflect and projectile defense

Miss values are defined/decoded at `benilla-protocol/src/messages/combat_log.rs:262-299` and mapped at
`net/apply/combat_log.rs:480-567`:

| Code | Word |
|---:|---|
| 1 | Miss |
| 2 | Resist |
| 3 | Dodge |
| 4 | Parry |
| 5 | Block |
| 6 | Evade |
| 7, 8 | Immune |
| 9 | Deflect |
| 10 | Absorb |
| 11 | Reflect |

For GO misses, Reflect also carries an extra byte and reanchors the floating word to the caster
(`net/apply/spells.rs:398-418`). Speed >0 missiles fly to miss targets just like hit missiles. At arrival,
only Dodge(3) and Block(5) trigger a defense reaction; all miss outcomes, including Parry, fizzle without an
impact kit (`crates/benilla/src/entities/missile.rs:259-305`). This is explicitly a benilla approximation.

## 17.8 Damage shields

`net/apply/combat_log.rs:386-422` presents server damage-shield packets. There is no local “if struck then
deal aura base points” logic. Aura presence, proc ownership, damage and miss are all server decisions.

## 17.9 Environmental damage and supplemental kits

Environmental types 0-5 map in order to Exhausted, Drowning, Fall, Lava, Slime and Fire entries in
`EnvironmentalDamage.dbc`. The server log apply writes a `KitPush`
(`crates/benilla/src/net/apply.rs:1189-1208`). `crates/benilla/src/creature_anim/env_damage.rs:1-100` also
predicts a hard landing when descent speed exceeds 13 and plays the Fall kit locally. A later server echo may
therefore display it twice. Remote falling prediction and safe-fall immunity are not modeled.

`SMSG_PLAY_SPELL_VISUAL` is a generic out-of-cast kit push. Despite the stale comment in
`crates/benilla-protocol/src/events.rs:553-555` saying there is no VFX consumer, current
`crates/benilla/src/net/apply.rs:1178-1187` resolves the unit and writes `KitPush`. The current runtime wins:
every push plays a fresh transient FX/sound instance, while body-animation same-id dedup lets a looping
eat/drink clip continue (`creature_anim/spell_visual.rs:477-493`).

## 17.10 Floating-combat-text law

World text is implemented in `crates/benilla/src/combat_text/mod.rs:1-367` and
`combat_text/law.rs:24-302`:

- maximum four concurrent rows per unit;
- snapshot the overhead anchor at creation (roughly one-third of overhead height below the top); text does
  not continue following the target;
- rise in world space before projection, then draw at constant screen size;
- resolve screen overlap against other active rows;
- use the category law below.

| Category | Rise | Fade-in | Fade-out start | Duration | Style |
|---:|---:|---:|---:|---:|---|
| 0 | 2 | 150 ms | 760 ms | 1500 ms | normal white |
| 1 | 2 | 150 ms | 90 ms | 1500 ms | white alternate |
| 2 | 0 | 150 ms | 1000 ms | 1500 ms | critical |
| 3 | 2 | 150 ms | 1000 ms | 1500 ms | alternate hit |
| 4 | 0 | 500 ms | 2000 ms | 4500 ms | purple |
| 5 | 0 | 500 ms | 2000 ms | 4500 ms | honor |

Critical scale keyframes are: time 0→0.1, scale 0.1→2; 0.1→0.2, scale 2→1; then 1. Exact fade/size math is
`law.rs:203-302`; wording and colors are in the same file. A visual-equivalent C# port must retain the cap,
snapshot behavior, time law and category choice, not merely print numbers above units.

---

# Section 18 — Spell metadata formulas and parsed-versus-consumed field ledger

## 18.1 Spell.dbc record contract

The parser is `crates/benilla-formats/src/spells/mod.rs:515-652`; the stored public record is
`spells/display.rs:8-185`. The audited build-5875 schema is 22,357 records × 173 fields
(`spells/mod.rs:1-112`). These are the exact columns benilla assigns:

| Domain | Spell.dbc columns | Stored/use |
|---|---|---|
| identity/UI | id 0; category 2; name 120; rank 129; description 138; aura description 147; icon 117; active icon 118 | spellbook, tooltip, stance bar, messages |
| attributes | Attributes 6; Ex 7; Ex2 8; Ex3 9 | casting helpers, visibility, passive/form/ranged/cooldown rules |
| stance/aura gates | stances 11; stances-not 12; Targets 13; focus 15; caster aura state 16; target aura state 17 | usability/targeting/tooltip |
| cast/cooldown | CastingTimeIndex 18; RecoveryTime 19; CategoryRecoveryTime 20; InterruptFlags 21; ChannelInterruptFlags 23; proc chance 25; DurationIndex 30 | UI/timing metadata; wire time still drives active cast |
| power | power type 31; mana cost 32; mana cost percent 156 | usable power gate |
| range/missile | RangeIndex 36; Speed 37 | local range gate; Speed alone selects missile path |
| tools/items | totems 40-41; reagents 42-49 + counts 50-57; equipped item class 58; subclass mask 59 | usable/cast admission |
| effect type | Effect[3] 61-63 | only effect 0 kept directly plus derived special scans |
| dice/base | die sides 64-66; base dice 67-69; base points 76-78 | tooltip token math, not gameplay resolution |
| implicit targets | A[3] 82-84 | target classifier primarily consumes A1/first lane |
| radius | EffectRadiusIndex[3] 88-90 | tooltip `$a`, not local AoE expansion |
| aura | EffectApplyAuraName[3] 91-93 | tooltip/shapeshift/tracking derivation |
| period | EffectAmplitude[3] 94-96 | tooltip `$t`, not local ticking |
| scaling | EffectMultipleValue[3] 97-99 | parsed metadata |
| chains | EffectChainTarget[3] 100-102 | tooltip `$x`, not local target selection |
| created item | EffectItemType[3] 103-105 | recipe product metadata |
| misc | EffectMiscValue[3] 106-108 | open-lock/form/tracking derivations and tooltip |
| trigger | EffectTriggerSpell[3] 109-111 | trainer learn wrapper / tooltip; no local proc execution |
| visual | SpellVisual 115 | resolves stages |
| global cooldown | StartRecoveryCategory 157; StartRecoveryTime 158 | local GCD |
| stance bar | StanceBarOrder 166 signed | form ordering |

The parser computes three important derived values while all lanes are available:

- first LEARN_SPELL lane's trigger spell;
- first OPEN_LOCK lane's misc value;
- first MOD_SHAPESHIFT aura lane's misc value.

It also stores effect arrays needed by tooltip/tracking/crafting. It does not retain an executable behavior
object for all three effect enums.

## 18.2 SpellCastTimes.dbc

`crates/benilla-formats/src/spells/cast_times.rs:1-88` parses 52 build-5875 rows of
`{id, base:u32, perLevel:i32, minimum:u32}`. Static tooltip time uses `base`. The original formula documented
there is:

```text
scaled = max(minimum, base + perLevel * (casterLevel - spellBaseLevel))
then apply casting-time spell modifiers (op 0x0A)
```

Row 1 is instant `{0,0,0}`; row 16 is Fireball rank 1 at 1500 ms; row 10 demonstrates negative per-level
`{1000,-100,500}` (`cast_times.rs:11-18,107-126`). benilla's active cast presentation uses the server START
time, so a C# online client must not substitute this local formula for the packet.

## 18.3 SpellDuration.dbc

`crates/benilla-formats/src/spells/duration.rs:1-109` parses 82 rows of signed
`{id, base, perLevel, max}`. `base == -1` is permanent. The documented original formula scales and clamps to
max, then applies duration modifiers. benilla uses the row for tooltip tokens; active aura remaining time is
the self duration packet. Row 30 is Frost Armor's 1,800,000 ms; row 21 is permanent
(`duration.rs:116-150`).

## 18.4 SpellRange.dbc and range consumption

`crates/benilla-formats/src/spells/ranges.rs:1-82` parses 28 build rows, each 22 fields, consuming id, min,
max and flags. Flags bit 0 selects the combat-reach melee branch. Probe rows are melee row 2 `{0,5,1}`,
Auto Shot row 114 `{8,35}`, Charge 95 `{8,25}`, Fireball 35 `{0,35}`
(`ranges.rs:13-17,89-119`). The exact local formula is Section 14.5.

## 18.5 SpellRadius.dbc

`crates/benilla-formats/src/spells/radius.rs:1-66` parses 24 rows of
`{id, radius, radiusPerLevel, radiusMax}`. Per-level is zero on every 5875 row. Probe rows include 13→10 yd
(Arcane Explosion), 8→5 yd and 10→30 yd. This data expands `$a` tooltip tokens only. It does not size a
generic client decal and does not select AoE targets; visible area geometry is authored inside the selected
M2 effect model.

## 18.6 SpellVisual and kit field consumption

The full layout is in Section 2; this is the consumption ledger:

| Field | Consumed? | Behavior |
|---|---|---|
| visual 1 precast | yes | START persistent hold |
| visual 2 cast | yes | GO discrete release |
| visual 3 impact | yes | every speed-0 hit or missile-hit arrival |
| visual 4 state | yes | impact-time state leg + aura lifetime watcher |
| visual 5 channel | yes | public channel descriptor edges |
| visual 6 hasMissile | **no** | Speed is the only projectile gate |
| visual 7 missile effect | yes | model path; nonzero unresolved → ErrorCube |
| visual 9 missile destination ordinal | yes | `MISSILE_ATTACH_TABLE` lookup |
| visual 10 flight loop sound | yes | force-loop on missile entity |
| visual 13 ground-arrival | **no/deferred** | no destination-stage implementation |
| visual 14 strike sound | yes | held-spell `$TRD` animation event |
| kit field 2 animation | yes | body one-shot/hold |
| kit fields 3-11 effects | yes, all nine | effect-model fan-out with fixed attachment tags |
| kit field 12 missile slot | **no** | not loaded |
| kit field 13 sound | yes | SpellKitSound |
| kit field 14 group fallback | **no** | not loaded |

Zero and `0xFFFFFFFF` are none for foreign keys; plain stage-kit fields use zero none
(`crates/benilla-formats/src/spell_visual.rs:24-53,93-150`).

## 18.7 Attribute-derived spell types

`crates/benilla-formats/src/spells/display.rs:258-462` is the authoritative local helper set. Port the helper
methods as named predicates rather than scattering masks:

- `ranged_attack`, `auto_repeat`, `cancel_auto_repeat_when_cast`, `ranged_slot`;
- `is_melee_auto_attack`, `ranged_speed_cooldown`, `in_spellbook`;
- `omit_cast_time_line`, `ranged_icon`, `cooldown_on_event`;
- `on_next_swing`, `initiates_auto_attack`;
- `hidden_aura`, `tracking_aura`, `usable_in_form`.

Raw attribute constants and their documented meanings are at
`crates/benilla-formats/src/spells/mod.rs:313-425`. Preserve unknown bits. Some helpers deliberately combine
multiple bits/effect values; replacing them with one remembered flag will misclassify wands, auto shot,
trade-skill rows or next-swing abilities.

## 18.8 Parsed-but-not-simulated ledger

The following values are real and must be retained for UI/data fidelity, but **must not be treated as local
combat authority** in an online port:

- base points, dice sides/base dice and multiple value;
- effect radius and chain target count;
- amplitude/tick interval;
- aura type and misc value except the explicitly listed local special cases;
- trigger spell except trainer wrapper/tooltip;
- duration scaling;
- proc chance;
- target masks beyond local admission checks.

Server outputs that replace local simulation are GO hit/miss lists, object-field health/power/aura changes,
combat-log packets, cooldown packets and duel/channel events.

## 18.9 Data absence/degradation

The audited source snapshot does not include `WoW/Data`; tests in the loaders explicitly skip when it is
absent (for example `cast_times.rs:95-103`, `duration.rs:116-124`, `radius.rs:73-81`). Consequently:

- row counts and probe values in this document are assertions/comments embedded in the source, not a fresh
  extraction performed during this audit;
- no loaded Spell/Visual row means no spell visuals (`spell_visual.rs:452-454`);
- unresolved effect content waits up to ten seconds in the FX attachment layer, then gives up (Section 5);
- UI paths generally use documented fail-open or empty-catalog behavior rather than crashing.

---

# Section 19 — C# architecture, update order, and acceptance tests

This section turns the trace into an implementation contract. Class names are recommendations; fields and
ownership boundaries are not optional if exact lifetime behavior is required.

## 19.1 Data-only catalogs

Load immutable catalogs before constructing runtime systems:

```csharp
sealed record SpellDef(
    uint Id, uint VisualId, float Speed,
    uint Attributes, uint AttributesEx, uint AttributesEx2, uint AttributesEx3,
    uint CastingTimeIndex, uint DurationIndex, uint RangeIndex,
    uint RecoveryMs, uint CategoryRecoveryMs,
    uint StartRecoveryCategory, uint StartRecoveryMs,
    uint InterruptFlags, uint ChannelInterruptFlags,
    int PowerType, uint ManaCost, uint ManaCostPct,
    uint ImplicitTargetA1,
    uint[] Effect, int[] EffectBasePoints, uint[] EffectDieSides,
    uint[] EffectRadiusIndex, uint[] EffectApplyAura, uint[] EffectAmplitude,
    float[] EffectMultiple, uint[] EffectChainTarget, uint[] EffectItemType,
    int[] EffectMiscValue, uint[] EffectTriggerSpell,
    int EquippedItemClass, uint EquippedItemSubclassMask,
    uint[] Totems, (int entry, uint count)[] Reagents,
    uint CasterAuraState, uint TargetAuraState,
    ulong Stances, ulong StancesNot, int StanceBarOrder,
    string Name, string Rank, string Description, string AuraDescription,
    string? Icon, string? ActiveIcon,
    int? OpenLockType, uint? LearnedSpell, uint? ShapeshiftForm);

sealed record SpellVisualDef(
    uint PrecastKit, uint CastKit, uint ImpactKit, uint StateKit, uint ChannelKit,
    uint MissileEffect, uint MissileAttachOrdinal,
    uint? MissileSound, uint? StrikeSound);

sealed record SpellVisualKitDef(
    ushort? AnimationId,
    uint?[] EffectIds,       // exactly nine, indexed with KIT_SLOT_TAGS
    uint? SoundId);
```

Keep the raw unknown columns/bits if your DBC reader already exposes them. Normalize foreign-key none
sentinels only at the catalog boundary. Do not pre-flatten kits into particles: each EffectName path resolves
to a whole M2 with every render lane.

## 19.2 Runtime event types

Keep network parsing separate from entity resolution and visual routing:

```csharp
readonly record struct SpellStartWire(
    ulong CasterGuid, ulong ItemCasterGuid, uint SpellId,
    uint Flags, uint CastTimeMs, SpellTargets Targets,
    uint? AmmoDisplayId, uint? AmmoInventoryType);

readonly record struct SpellGoWire(
    ulong CasterGuid, uint SpellId, uint Flags,
    ulong[] Hits, (ulong Guid, byte Reason)[] Misses,
    SpellTargets Targets, uint? AmmoDisplayId, uint? AmmoInventoryType);

enum CastEdgeKind { Start, Go, Impact, Fail }
readonly record struct CastEdge(EntityId Entity, uint SpellId, CastEdgeKind Kind,
                                WeaponVisual? WeaponVisual, ulong Sequence);

readonly record struct GoTargets(EntityId Caster, uint SpellId,
                                 EntityId[] Hits, (EntityId Target, byte Reason)[] Misses,
                                 uint? AmmoDisplayId, ulong Sequence);

enum FxClass { Hold, AuraState }
readonly record struct FxBegin(EntityId Owner, uint SpellId, bool Persistent,
                               FxClass Class, (ushort AttachTag, string ModelPath)[] Effects);
readonly record struct FxReap(EntityId Owner, uint SpellId, FxClass Class);
```

Use an ever-increasing sequence value on discrete animation/kit requests. The body driver uses it to
distinguish genuinely new plays while separately deduplicating same-id persistent/looping requests.

## 19.3 Exact cast visual router pseudocode

```text
route frame:
  pendingHoldOverlay := empty map<Entity, Optional<SpellId>>

  for each KitPush:
      resolve raw kit id
      play discrete body anim + sound + all nine effect models

  for each CastEdge in packet order:
      START:
          StopHoldSound(owner)
          reap previous held spell's FxClass.Hold
          if spell is ranged attack: request sheath state 2
          resolve PRECAST kit (including ranged weapon fallback)
          if kit has animation:
              write CastHold(anim, spell, rangedSlot)
              pendingHoldOverlay[owner] = spell
          play kit sound once
          begin every kit effect PERSISTENT, FxClass.Hold

      GO:
          if held spell == this spell: remove CastHold; overlay = none
          StopHoldSound(owner)
          reap (owner, spell, FxClass.Hold) unconditionally
          if ranged slot: request sheath state 2 and maintain RangedHold
          resolve CAST kit; play DISCRETE body anim, sound, all effects

      IMPACT:
          play IMPACT kit discrete on target
          then play STATE body/sound leg with effects suppressed;
          aura watcher owns persistent state effects

      FAIL:
          remove only matching CastHold
          StopHoldSound(owner)
          reap (owner, spell, FxClass.Hold), no release kit

  for each GoTargets:
      if Spell.Speed <= 0:
          playImpact(each hit); do nothing visual for each miss
      else:
          resolve missile model/sound/destination tag and cast-animation release gate
          enqueue one missile for every hit and every miss

  poll UNIT_CHANNEL_SPELL edges:
      nonzero/replaced => reap old Hold; arm channel hold/sound/effects persistent
      cleared          => remove matching hold; stop sound; reap Hold

  poll live public aura spell-id sets:
      disappeared => reap AuraState
      newly present and has state effect models => begin persistent AuraState
```

The pending overlay is frame-local visibility over deferred component writes. It is not a timeout and must be
consulted by every hold read in this drain. This preserves same-frame START→GO order for instant spells.

## 19.4 Effect-instance ownership

Use a stable key at minimum:

```csharp
readonly record struct PersistentFxKey(EntityId Owner, uint SpellId, FxClass Class);
```

One key owns a list because a kit can contain nine effect models and a model can fan out into many child
render/emitter entities. `Begin(persistent:true)` replaces/drains an existing list for that key; `Reap`
drains exactly that key. Transient instances have unique ids and terminate after their first authored
sequence span (fallback one second). Never key only by spell id: two casters and Hold versus AuraState would
collide.

Each resolved effect model owns:

- root/world transform and optional attach joint/tag;
- model rig and current preferred clip (`InFlight=144` for missiles, first sequence otherwise);
- ordinary/skinned submesh instances and per-instance material tint clones;
- billboard cards;
- ground-decal parts;
- particle emitters and recursive children;
- ribbon emitters;
- model animation-event cursor for `$SND` and related events;
- persistent/draining/transient lifetime state.

## 19.5 Required update order

The exact engine labels may differ, but the partial order must be preserved:

```text
1. Receive/decode network packets in wire order.
2. Apply object fields and create CastEdge / GoTargets / combat-log / KitPush messages.
3. Resolve GUIDs to currently streamed entities; retain the packet's hit/miss ordering.
4. Run local UI input drains and outbound cast admission.
5. Route START/GO/FAIL/IMPACT, KitPush, GO targets, channel edges and aura-state edges.
6. Flush created/removed holds and effect roots where your ECS requires it.
7. Select/advance body animation, including one-shots, CastHold, wound overlay and ranged markers.
8. Consume release markers; launch queued missiles.
9. Refresh live attachment/bone transforms and billboard-joint palettes.
10. Advance missile homing; arrival emits Impact or defense reaction and stops flight sound.
11. Resolve/attach pending effect assets; evaluate model rigs/material animation/event tracks.
12. Simulate particles and ribbons after owner/attach movement is known.
13. Project/update ground decals against current world collision geometry.
14. Build particle/ribbon/decal draw buffers and render.
15. Pump tracked/one-shot 3D audio from final entity/listener positions.
16. Feed aura/cast/cooldown/combat UI diffs and expire stale UI-only state.
```

The important constraints are packet ordering before the router, release events before queued launch,
attachment refresh before emitter follow/inherit math, and simulation before draw-buffer construction.
benilla's concrete spell/animation ordering is registered in `crates/benilla/src/creature_anim.rs:658-734`;
particle/ribbon ordering is detailed in Sections 7-8.

## 19.6 Missile object contract

```csharp
sealed class Missile {
    EntityId Source, Target;
    uint SpellId;
    byte? MissReason;
    ushort? DestinationAttachTag;
    Vector3 Position;
    float RemainingSeconds;
    WeaponVisual? RangedFallback;
    EffectModelInstance? Visual;
    AudioHandle? FlightLoop;
}
```

At launch, resolve current source/destination attach positions. Set
`remaining = distance / max(speed, epsilon) - queuedSeconds`. If nonpositive, hand off immediately without a
flight entity or loop. Each update:

```text
aim = current live target attach position
if remaining <= dt:
    hit  => emit Impact(target, spell, rangedFallback)
    miss => only Dodge or Block defense reaction
    stop loop; destroy missile
else:
    position += (aim - position) * (dt / remaining)
    remaining -= dt
    orient model +X toward (aim - position), zero roll
```

Do not snap to `aim` on arrival; the benilla handoff occurs before a final snap. If the target entity
despawns, destroy the missile. Model visibility and flight sound are independent: no model can still yield an
invisible traveling loop.

## 19.7 Aura UI/state implementation split

Implement two services:

```csharp
AuraUiFeed       // 48 slots, liveness flags, self insertion order, timers, filtering, stacks, cancel
AuraStateFxWatch // per streamed unit sorted/dedup spell-id set, state-kit begin/reap
```

They may share the parsed live-slot iterator but must not share ordering or lifetime keys. Keep timer state by
slot with receipt time; never infer target aura duration from the DBC. Keep tracking selection as a distinct
output and omit those rows from ordinary buff/debuff lists.

## 19.8 Render and simulation fidelity checklist

Before calling a spell visually complete, verify every item:

- [ ] nine kit slots map to `[0x14,0x22,0x13,0x15,0x16,0x11,0x17,0x18,0x19]`;
- [ ] requested attach falls back through `0x0F`, `0x13`, then root;
- [ ] bind-pivot subtraction is correct for creature as well as player bones;
- [ ] ordinary meshes, skinned meshes, billboard cards, ground quads, particles and ribbons all spawn;
- [ ] per-instance animated alpha/color does not mutate a shared material;
- [ ] ground quads are projected to terrain/WMO geometry and hidden if no surface;
- [ ] emitter `rot90` is applied exactly once at birth;
- [ ] emission enabled gate, burst rising edge, global/pinned sequence clock and recursive children work;
- [ ] particle tail/head, twinkle, cell ramp, tumble, follow/inherit, ground snap and drain behavior match;
- [ ] ribbon edge lifetime, sag, commit accumulator, texture UV and owner-loss draining match;
- [ ] model `$SND` events and kit/missile sounds coexist;
- [ ] persistent holds/aura states reap only their exact `(owner,spell,class)` instances;
- [ ] asset pending has a ten-second timeout; transient fallback span is one second;
- [ ] missing DBC/model/material data degrades as documented rather than crashing.

## 19.9 End-to-end acceptance matrix

The spells/examples below are chosen to force different code paths. Exact spell ids can be replaced when the
test account lacks one, but every row needs a fixture/capture.

| Test | What must be asserted |
|---|---|
| Fireball 133, timed projectile | START hand effect + cast hold/sound; GO release; release-keyed chest/hand launch; loop; homing; one impact; cast bar; cooldown starts at GO |
| instant self buff | same-frame START+GO leaves no stuck hold; state aura effect remains until descriptor removal; icon order/duration/cancel correct |
| Frost Armor 168 | state/impact kit placement at authored attach; 30-min DBC is tooltip only; active timer comes from aura packet |
| Arcane Intellect 1459 | buff uses generic visual chain despite no local effect-enum branch; persistent aura state and icon are independent |
| Arcane Explosion 1449 | speed-zero caster AoE impacts every GO hit; projected dome/ring is complete 360°; no local radius query |
| Frost Nova 122 | every server hit gets impact, misses do not; expanding projected ground geometry; periodic/root effects only if server aura/log says so |
| hostile single target instant | selected enemy relation/range gates; speed-zero impact and damage/miss feedback |
| friendly direct heal | assist/autoself target; portrait+center heal; no overhead heal number |
| DoT | aura icon/state persists; every tick requires a periodic log; dispel stops when server removes/logs cease |
| HoT | timer/stack semantics; periodic center/portrait; no world number |
| channel | public descriptor arms remote/local channel FX once; self messages drive bar; movement sends cancel and waits for server clear |
| multi-target projectile | exactly one missile per GO hit+miss; only hits impact; each target remains homed live |
| miss matrix | words for 1-11; Reflect anchored to caster; only Dodge/Block missile defense reaction |
| auto shot | one activation START, repeated GO releases, weapon/ammo fallback, sheath 2/RangedHold, auto-repeat stop order |
| wand | active wand repeat cancels on qualifying new cast, without breaking other ranged classification |
| next-swing ability | queues, engages attack when flagged, does not present as immediate normal spell |
| tracking aura | absent from aura bar, last raw matching slot controls minimap, cancel waits for descriptor update |
| shapeshift/stance | list admission/order/icon; active form byte; active cancel rule; true-shift vs stance usability |
| item enchant/temp enchant | cursor arm then item packet; document missing paper-doll/trade/replacement flow; normal server visual echo |
| profession opener/create item | opener sends no cast; recipe path and product metadata; inventory result server-owned |
| open lock | matching LockType spell chosen; GO packet; object lid transition on GO |
| skinning | effect-95 known spell direct path on eligible corpse |
| duel | effect-83 learned spell ordinary player target; request/countdown/bounds/completion from server events |
| `SMSG_PLAY_SPELL_VISUAL` eat/drink | every packet spawns new transient FX/sound; same body anim does not restart; state aura prop survives by aura watcher |
| environmental types 0-5 | correct DBC kit; hard fall local predictor; capture possible duplicate server echo |
| loot sparkle | dead+lootable edge begins persistent base effect; falling edge reaps immediately |
| level-up | first observed level is silent; changed level spawns one 1.867 s model and model-event sound |
| missing data | no crash; visual absent/fallback/pending timeout behavior matches each layer |

## 19.10 Diagnostic capture schema

A “looks close” review is insufficient. Log one structured row per stage and per visual child:

```text
frame/time, packet sequence, caster/target GUID, spellId,
edge(Start/Go/Impact/Fail/Channel/Aura/KitPush),
visualId, kitId, kitSlot, effectId, modelPath, requested/resolvedAttach,
persistent, fxClass, bodyAnimId, soundId,
speed, missileId, source/dest/remaining,
model sequence + span, emitter/ribbon counts, particle/ribbon live counts,
ground-decal triangle count, combat outcome/category, aura slot/flags/stacks/remaining
```

For every acceptance fixture compare packet ordering, spawn/reap counts, transforms, first-visible frame,
authored lifetime and final cleanup. Image comparison catches blend/geometry faults; structured traces catch
duplicate/missing lifetime edges which a single screenshot cannot.

---

# Section 20 — Source inventory, hashes, and final no-exception audit

## 20.1 Snapshot identity and limitations

Audit date: **2026-08-02**. Source root: `C:\Users\nico\Desktop\benilla-main`. The supplied directory has no
Git metadata from which to report a commit id and no `WoW/Data` directory from which to re-run real-DBC
tests. To make the reviewed snapshot identifiable, the table below records the first 12 hexadecimal digits
of SHA-256 for every spell-specific/control/presentation source read in this pass. Earlier Sections 1-12
also cite their M2/asset/animation support files directly.

## 20.2 Audited spell-specific source inventory

| Source path under benilla-main | Nonblank/source lines reported by audit tool | SHA-256 prefix |
|---|---:|---|
| `crates/benilla-formats/src/spell_visual.rs` | 664 | `29D2E9C95E27` |
| `crates/benilla-formats/src/particles.rs` | 1023 | `5C1264F56D06` |
| `crates/benilla-formats/src/ribbons.rs` | 333 | `09BE36496A30` |
| `crates/benilla-formats/src/sound_entries.rs` | 198 | `AB70DC6E5046` |
| `crates/benilla-formats/src/spells/mod.rs` | 628 | `AED4126C5B2E` |
| `crates/benilla-formats/src/spells/display.rs` | 442 | `F1A067CEC332` |
| `crates/benilla-formats/src/spells/cast_times.rs` | 112 | `A546EB1C966E` |
| `crates/benilla-formats/src/spells/duration.rs` | 134 | `CF2B5C42B4F6` |
| `crates/benilla-formats/src/spells/ranges.rs` | 103 | `CC2BD92D6BD2` |
| `crates/benilla-formats/src/spells/radius.rs` | 77 | `A893D582F9E0` |
| `crates/benilla-formats/src/spells/forms.rs` | 84 | `6765668873D3` |
| `crates/benilla-formats/src/spells/dispel_types.rs` | 98 | `9D09A13F3A56` |
| `crates/benilla-formats/src/spells/tokens.rs` | 384 | `8254D23EB51B` |
| `crates/benilla-formats/src/spells/catalog_tests.rs` | 642 | `84BA94C0752F` |
| `crates/benilla-protocol/src/messages/spells.rs` | 367 | `F98224BF1667` |
| `crates/benilla-protocol/src/messages/combat_log.rs` | 281 | `E49519E2ABC3` |
| `crates/benilla-protocol/src/events.rs` | 976 | `C826C1F27E75` |
| `crates/benilla/src/net/apply/spells.rs` | 884 | `851B6C735640` |
| `crates/benilla/src/net/apply/combat_log.rs` | 564 | `89CD22770548` |
| `crates/benilla/src/net/apply.rs` | 1481 | `E00DA8D8802F` |
| `crates/benilla/src/ui_action/mod.rs` | 220 | `3EE9CBC94C3F` |
| `crates/benilla/src/ui_action/cast_target.rs` | 405 | `2BC542EEF872` |
| `crates/benilla/src/ui_action/cast_send.rs` | 276 | `1AA509B45B96` |
| `crates/benilla/src/ui_action/cast_fail.rs` | 403 | `B9228B437412` |
| `crates/benilla/src/ui_action/errors.rs` | 416 | `568B4C52ADDA` |
| `crates/benilla/src/ui_action/usable.rs` | 337 | `7C1354CD12AB` |
| `crates/benilla/src/ui_action/state.rs` | 505 | `403AEBAFBDE3` |
| `crates/benilla/src/ui_action/drain.rs` | 302 | `6709F9DDAD16` |
| `crates/benilla/src/ui_action/feed.rs` | 350 | `4DC0DA4C6C7A` |
| `crates/benilla/src/ui_action/weapon_icon.rs` | 102 | `EF8FEF280258` |
| `crates/benilla/src/ui_aura.rs` | 574 | `5E017BFF257C` |
| `crates/benilla/src/ui_cast.rs` | 928 | `94C2DEBB2DB0` |
| `crates/benilla/src/cooldowns.rs` | 772 | `2E4CB1733E2D` |
| `crates/benilla/src/ui_craft.rs` | 369 | `E318A6FCE968` |
| `crates/benilla/src/ui_duel.rs` | 482 | `54162433C362` |
| `crates/benilla/src/ui_shapeshift.rs` | 229 | `E83D315F0ED8` |
| `crates/benilla/src/ui_tooltip.rs` | 818 | `77B991FBB9C1` |
| `crates/benilla/src/ui_items/feed.rs` | 767 | `49B22325B6A5` |
| `crates/benilla/src/ui_tradeskill.rs` | 525 | `0AF25F6B1C21` |
| `crates/benilla/src/target/click.rs` | 875 | `098AE4D80AA6` |
| `crates/benilla/src/go_anim.rs` | 541 | `4AFAE9FBBACD` |
| `crates/benilla/src/minimap/blips/dots.rs` | 521 | `B89E226A2646` |
| `crates/benilla/src/creature_anim.rs` | 754 | `FEDF837AE62C` |
| `crates/benilla/src/creature_anim/spell_visual.rs` | 949 | `C76B56447124` |
| `crates/benilla/src/creature_anim/env_damage.rs` | 93 | `D428D815AB9F` |
| `crates/benilla/src/creature_anim/driver.rs` | 1277 | `8AA9A7A98645` |
| `crates/benilla/src/creature_anim/driver/play.rs` | 250 | `DBF3CD2F568D` |
| `crates/benilla/src/creature_anim/driver/wound.rs` | 157 | `E77006C4047F` |
| `crates/benilla/src/entities/spell_fx.rs` | 817 | `D1F27116CA3E` |
| `crates/benilla/src/entities/missile.rs` | 840 | `A960D6D7A294` |
| `crates/benilla/src/ground_fx.rs` | 241 | `0A10A6925BAD` |
| `crates/benilla/src/particles/sim.rs` | 924 | `23C0838635CB` |
| `crates/benilla/src/particles/quads.rs` | 277 | `29C454A2705F` |
| `crates/benilla/src/particles/model.rs` | 163 | `D9F67B28571E` |
| `crates/benilla/src/particles/material.rs` | 85 | `A912D419C41F` |
| `crates/benilla/src/ribbons.rs` | 370 | `6B0A56791F56` |
| `crates/benilla/assets/shaders/wow_particle.wgsl` | 122 | `A15A46E3617B` |
| `crates/benilla/src/sound/spell.rs` | 95 | `977B72449EB0` |
| `crates/benilla/src/sound/missile.rs` | 63 | `1FF4DC41F614` |
| `crates/benilla/src/sound/anim_events.rs` | 123 | `1C6427F68FE7` |
| `crates/benilla/src/sound/kit.rs` | 578 | `8650CE91D88F` |
| `crates/benilla/src/sound/math.rs` | 152 | `B2AC0A48D954` |
| `crates/benilla/src/combat_text/mod.rs` | 465 | `0CE8277B726E` |
| `crates/benilla/src/combat_text/law.rs` | 457 | `AFCDC3C6506D` |

The first 57-file core inventory was 22,956 reported source lines; the additional protocol/apply/animation/UI
support rows above were inspected to close routing and special-case gaps. Line totals are identification aids,
not semantic claims; file:line citations in the body are the implementation references.

## 20.3 No-exception coverage audit

| Required domain | Covered in this document | Authoritative source locus |
|---|---|---|
| every locally special-cased effect/aura numeric | Section 13.1 exhaustive table | global comparison audit + `spells/mod.rs` |
| ordinary self/friendly/hostile/corpse target | Sections 13-14 | `ui_action/cast_target.rs` |
| party/raid/ground/GO/item/trade/string limitations | Section 14 | target decoder + resolver + special paths |
| instant/timed/direct/AoE/chain/projectile | Sections 4, 10, 13, 17 | visual router + GO lists |
| buffs/debuffs/stacks/durations/cancel/hidden/tracking | Section 15 | `ui_aura.rs` + unit fields |
| shapeshift/stance/stealth | Sections 14-15 | `ui_shapeshift.rs`, forms/display helpers |
| channels | Sections 4, 15-16 | public descriptor + self channel packets |
| melee/ranged/wand/auto-repeat/next-swing | Sections 11, 13-16 | display helpers, cast_send, animation state |
| professions/create/enchant/open lock/skinning/duel | Sections 13-14 | dedicated UI/click modules |
| START/GO/fail/delay/channel/aura/kit-push packets | Sections 3, 14-17 | protocol + net apply |
| cooldown/GCD/cast bar/cancel | Section 16 | `cooldowns.rs`, `ui_cast.rs` |
| direct/periodic damage/heal/power/leech/shield/miss | Section 17 | combat-log decoder/apply |
| environmental, loot and level-up supplemental art | Sections 4, 13, 17 | env/apply/spell_visual watchers |
| all five visual stages and lifetime rules | Sections 2-5 | SpellVisual/kit router |
| all nine effect slots/attachments/fallbacks | Sections 2, 5-6 | SpellVisualKit + attach cascade |
| body animation/holds/wounds/ranged release | Section 11 | creature animation driver |
| ordinary/skinned mesh, billboard, material/tint | Sections 1, 5-6, 11 | effect model/render paths |
| particles: all shapes, flags, emission, life, rendering | Sections 2, 7-8 | formats/runtime/shader |
| ribbons: tracks, edges, sag, strip/material | Sections 2, 8 | formats/runtime |
| AoE projected ground geometry | Section 9 | `ground_fx.rs` |
| missiles: queue/release/model/sound/homing/miss | Section 10 | `entities/missile.rs` |
| kit, animation-event and missile audio | Section 12 | sound modules + SoundEntries |
| metadata columns/formulas/consumption gaps | Section 18 | Spell parsers/catalogs |
| implementable C# state/ordering/tests | Section 19 | synthesis constrained by all cited code |

## 20.4 benilla limitations which a perfect benilla clone must preserve or consciously supersede

This ledger consolidates the named gaps so they cannot be rediscovered as “missing spell types” during the
C# port:

- no spell gameplay simulator; server owns targeting/effects/outcomes;
- generic ground-target cursor/send absent; source/item/trade/string target details are not retained by the
  general decoder; destination-stage art is deferred;
- party/raid local resolver supports only self; CanAssist is a reaction approximation;
- open-lock, skinning and item-craft special sends bypass parts of the shared cast gate;
- item-enchant cursor lacks artwork, confirmation, paper-doll and trade targeting;
- several usability legs are unmodeled (silence/pacify immunity, durability, ghost, some Ex3/self-only);
- active aura timers are self-only; other target durations display as zero;
- aura state watcher does not replay a non-sentinel state-kit body animation on add;
- missile destination uses live attach rather than original bounding-sphere ray interception;
- misses only animate Dodge/Block; `$BWR` is accepted as a release point approximation;
- ground decals are a deliberate modern triangle-projection improvement over the original flat behavior;
- particle spline length uses 16 chords; RNG distribution is matched but not the original stream;
- model-space emitter motion contains a documented world/local fold adaptation;
- particle blend modes 5/6 fold to Alpha; mini model-particles do not run a separate rig animation and draw
  at most 128 instances;
- particle simulation freezes when owner draw/frustum/far-clip gate is false, with no catch-up;
- ribbons commit at most one edge per frame and use a shortened minimum 0.25-second lifetime;
- effect persistent completion is lifetime polling rather than the original callback;
- effect UV scrolling is absent;
- wound overlays are weighted blends rather than a strict bone mask; LockX/LockY billboard signs are not
  verified against shipped spell content;
- audio lacks the original category concurrency cap; out-of-range loops stop rather than virtualize;
- environmental hard-fall prediction can duplicate the server kit;
- a stale `events.rs` comment says PlaySpellVisual lacks VFX, but the current apply/router implements it.

If the target is **perfect benilla parity**, reproduce these results. If the target is **perfect WoW 1.12
client parity**, treat them as a backlog and validate every change against original-client captures rather
than silently mixing semantics.

## 20.5 Final implementation rule

There is no per-spell C# switch to write. The complete implementation is:

```text
all spell ids
  + preserved Spell.dbc metadata
  + server-authoritative START/GO/object-field/combat-log/cooldown inputs
  + exhaustive finite local special-case table in Section 13.1
  + generic five-stage visual resolver
  + complete M2 mesh/billboard/decal/particle/ribbon/event pipeline
  + exact animation, missile, sound, aura and cleanup state machines
= benilla's spell behavior
```

Any design which instead has handlers named only `Fireball`, `Heal`, `Buff`, `AoE` or `Projectile` is not a
port of benilla. It is a sample-spell implementation and will fail as soon as a different SpellVisualKit
combines the same primitives in a new way.
