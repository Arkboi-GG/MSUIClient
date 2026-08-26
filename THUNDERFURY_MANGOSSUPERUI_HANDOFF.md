# MangosSuperUI Thunderfury Animation Handoff

## Status and scope

This document describes the remaining Thunderfury rendering defect in MangosSuperUI's JavaScript GLB viewers and the proposed fix. It is an investigation and implementation handoff; no MangosSuperUI source code was changed as part of this task, and nothing was installed, deployed, restarted, or changed on the server.

The relevant MangosSuperUI surfaces are:

- `ItemsController` item/model preview
- the standalone JavaScript GLB item viewer
- the three-dimensional character viewer and its equipped-weapon path

The desired result is for Thunderfury's native model effects to match their authored behavior:

- the lightning fins should move around the weapon instead of only pulsing in place around the model origin;
- the lightning orb should face the camera and animate instead of appearing as a frozen BLP/PNG card;
- existing working weapon effects, including Warglaives and the Stormwind guard sword, must remain working.

Do not report this as visually fixed based only on a successful build or a structurally valid GLB. The final result must be checked in both the standalone item preview and the equipped character viewer.

## Executive diagnosis

Thunderfury's missing motion is not an `ItemVisual`, particle-emitter, ribbon-emitter, animated-BLP, or UV-animation problem.

The model's effects are ordinary skinned M2 geometry:

- Lightning fins are weighted to bones whose translation and rotation are animated by M2 global sequences.
- The orb is geometry textured with `LIGHTNINGBALL.BLP`, weighted to camera-facing bones. The texture itself is not supposed to animate.
- MangosSuperUI currently exports these item models as rigid GLBs. It preserves material alpha/weight animation through `extras.suiFx`, but discards the skeleton, vertex joint indices, vertex weights, bone animation tracks, and M2 camera-facing bone semantics.

That precisely explains the observed output:

- Material alpha/weight still changes, so the lightning appears partly animated while remaining spatially fixed.
- The orb's textured card exists, but its camera-facing joint behavior is gone, so it looks like a frozen image.

The fix therefore needs two coordinated halves:

1. Export affected item models as skinned GLBs with their authored global-sequence bone animation.
2. In the browser, play those global-sequence clips and apply M2 billboard/ignore-parent-rotation behavior after animation sampling.

## Evidence captured from the server

All server investigation was read-only. The running service and data were not changed.

### Exact item and model

The live display record was queried for display ID `30606`.

- Source model: `Sword_2H_Ashbringer02.mdx`
- M2 version: 256 (vanilla)
- Source length: 46,896 bytes
- Bones: 35
- Vertices: 271
- Submeshes: 11
- Batches: 12
- Colors: 7
- Transparency tracks: 2
- Texture animations: 0
- Particle emitters: 0
- Ribbon emitters: 0
- Live `ItemVisualId`: 0

The lack of `ItemVisual`, particles, ribbons, and UV transforms is important. Looking for an external enchant model or an animated BLP will not solve this particular model.

### Material/submesh layout

The relevant effect submeshes are:

- Submeshes 0 through 6: `SPELLS\\ZAP1.BLP`, blend mode 4, animated alpha/weight. Their rest weight is approximately `0.19000824`.
- Submesh 7: `BLUE_GLOW2.BLP`, blend mode 4.
- Submesh 8: `LIGHTNINGBALL.BLP`, blend mode 4.
- Submeshes 9 and 10: the ordinary item skin, blend mode 0.

The current `suiFx` material path successfully retains alpha/weight animation for submeshes 0 through 6. It does not retain the transforms that move those vertices.

### Bone ownership of visible geometry

The exact model bytes were read from the active `patch.MPQ`, not from the older base `model.MPQ` copy.

- Submesh 7 has 12 of 12 vertices influenced by camera-facing bones 0, 1, and 3.
- Submesh 8 has 8 of 8 vertices influenced by camera-facing bones 2 and 4.
- Submeshes 0 through 6 use animated bones 21 through 28.
- Submeshes 9 and 10 use static bone 5.

Bones 0 through 4 have flags `0x00000208`. Bit `0x08` is one of the camera-facing modes. The client-side camera-facing mask already proven in MSUIClient is:

```text
IgnoreParentRotation = 0x04
BillboardMask        = 0x78
CameraFacingMask     = 0x7c
```

### Authored global-sequence tracks

The camera-facing effect bones contain these tracks:

- Bone 0: scale on global sequence 14, 6 keys
- Bone 1: scale on global sequence 15, 6 keys
- Bone 2: rotation on global sequence 3, 21 keys; scale on sequence 16, 6 keys
- Bone 3: scale on global sequence 14, 6 keys
- Bone 4: rotation on global sequence 17, 21 keys; scale on sequence 18, 5 keys

Bones 21 through 28 have flag `0x200`, parent bone 9, and animated global-sequence translation/rotation:

- translation uses global sequence 10 or 13, generally 3 keys;
- rotation uses global sequence 10, 11, or 13, generally 11, 12, or 29 keys.

These are the tracks that move the lightning fins around the blade.

### What the current GLB proves

The currently generated GLB was inspected directly:

`/opt/mangossuperui-assets/item_models/30606.v60794.glb`

It contained:

- no `skins`;
- no `animations`;
- no `JOINTS_0` attribute;
- no `WEIGHTS_0` attribute;
- no particle/ribbon emitters;
- a `suiFx` manifest that animates the material alpha/weight of Geosets 0 through 6;
- no equivalent motion for Geosets 7 and 8.

This is the direct structural proof that the exporter, rather than the source data, is dropping the missing behavior.

## Relevant source files

The local MangosSuperUI checkout examined for this handoff was:

`C:\Users\nico\source\repos\MangosSuperUI`

Primary files:

- `MangosSuperUI/Services/GlbWriter.cs`
- `MangosSuperUI/Services/SkinnedGlbWriter.cs`
- `MangosSuperUI/Services/M2Handlers/M2Reader.cs`
- `MangosSuperUI/Services/M2Fx/M2FxManifest.cs`
- `MangosSuperUI/wwwroot/js/character-viewer/m2fx.js`
- `MangosSuperUI/wwwroot/js/character-viewer/equip.js`
- `MangosSuperUI/wwwroot/js/character-viewer/item-preview.js`
- `MangosSuperUI/wwwroot/js/character-viewer/animation-control.js`
- `MangosSuperUI/Services/CacheVersionRegistry.cs`

Proven reference implementation in MSUIClient:

- `MSUIClient/World/Units/AttachedItemBillboardLaw.cs`
- `MSUIClient/World/Units/SpellMeshSkinningLaw.cs`

The MSUIClient code should be treated as the behavioral reference for flag interpretation and camera-facing basis construction.

## Current exporter behavior

`GlbWriter.SaveGlb` is used by item previews, armor attachments, weapon previews, and other rigid-model consumers.

Its mesh construction currently uses:

```csharp
MeshBuilder<VertexPositionNormal, VertexTexture1, VertexEmpty>
MeshBuilder<VertexPositionNormal, VertexTexture2, VertexEmpty>
scene.AddRigidMesh(...)
```

The third vertex fragment is `VertexEmpty`, so the source M2's four joint indices and four weights are discarded. `AddRigidMesh` then emits no glTF skin.

By contrast, `SkinnedGlbWriter` already contains almost all required server-side machinery:

- `VertexJoints4` construction from M2 bone indices and weights;
- bone hierarchy construction using M2 pivots;
- `scene.AddSkinnedMesh`;
- per-sequence animation emission;
- global-sequence animation emission as independently named clips such as `GlobalSequence_10`.

This should be reused or factored into shared helpers rather than independently reimplemented with different pivot or quaternion rules.

## Recommended implementation

### 1. Selectively choose a skinned item path

Do not convert every model passed through `GlbWriter` to a skinned GLB.

Use the skinned path only when all of the following are true:

- the M2 has bones;
- visible vertices carry usable bone weights; and
- at least one visible influence depends on either:
  - a bone/ancestor with flags matching `0x7c`, or
  - a bone with an authored global-sequence translation, rotation, or scale track.

This retains the existing rigid fast path for ordinary weapons, helms, shoulders, props, and game objects. It also limits the regression surface.

A conservative first implementation may select any model with bones plus either a global-sequence bone track or camera-facing bone flag. A later optimization can restrict the scan to bones reachable from emitted vertices.

### 2. Preserve joint indices and normalized weights

For selected models, use vertex builders whose skinning fragment is `VertexJoints4`:

```csharp
VertexBuilder<VertexPositionNormal, VertexTexture1, VertexJoints4>
VertexBuilder<VertexPositionNormal, VertexTexture2, VertexJoints4>
```

Both the ordinary one-UV path and the fused two-UV `_mod` path must retain joints and weights. Fixing only `VertexTexture1` would silently break the Warglaive-style multi-texture path.

Weights should follow the existing shared behavior:

- divide authored byte weights by their total, or rely on `VertexJoints4` normalization only after confirming identical output;
- if the total is zero, assign full weight to bone 0;
- reject or clamp invalid joint indices consistently;
- do not split the billboard cards into guessed rigid quads.

MSUIClient's `SpellMeshSkinningLaw.Resolve` is the reference policy:

```text
zero total -> weight 1 on bone 0
otherwise  -> normalize all four authored weights by their actual total
```

Note that simply dividing every byte by 255 is not identical when malformed or non-255 totals occur.

### 3. Build an item armature and export global clips

Reuse the pivot and hierarchy rules in `SkinnedGlbWriter.BuildBoneArmature`:

```text
root local translation  = bone pivot
child local translation = bone pivot - parent pivot
animated translation    = rest local translation + M2 translation key
animated rotation       = normalized M2 quaternion key
animated scale          = M2 scale key
```

Reuse `SkinnedGlbWriter.EmitGlobalSequences` so each global loop remains an independent glTF clip:

```text
GlobalSequence_3
GlobalSequence_10
GlobalSequence_11
GlobalSequence_13
GlobalSequence_14
...
```

They must be played concurrently. Combining them into one ordinary character animation or selecting only one clip will still leave part of Thunderfury static.

Give the item armature a unique name, for example `ItemArmature`, so the JavaScript runtime can distinguish it from the character body armature. This prevents `m2fx.js` from starting a second mixer on character clips that `animation-control.js` already drives.

### 4. Keep animation and billboard correction on separate nodes

glTF and Three.js have no native M2 billboard-bone semantic. Avoid having JavaScript overwrite the same quaternion channel that `AnimationMixer` is sampling.

For every bone matching `CameraFacingMask`, export two nodes:

```text
AuthoredBone_i
└── M2Billboard_i_f<flags>
```

- Put M2 translation/rotation/scale animation on `AuthoredBone_i`.
- Keep the correction child at identity in the GLB.
- Use the correction child as the skin joint for that M2 bone.
- Parent descendant M2 bones beneath the correction child so they inherit the rewritten parent orientation, matching the client palette behavior.
- Encode the original flags in the correction node name or in root extras. A stable extras field is cleaner, but a node-name suffix is compatible with the conventions already used in this codebase.

This separation allows the frame order to be:

1. sample authored global-sequence animation;
2. update world matrices;
3. apply camera-facing correction to the child;
4. render the skinned mesh.

### 5. Extend `m2fx.js` with an item-bone runtime

`installM2Fx` is already called by both required viewer paths:

- `item-preview.js` for standalone previews;
- `equip.js` for weapons mounted on the character.

That makes it the appropriate integration point. It already receives elapsed time, delta time, and the camera on every update.

Add an item-rig handle that:

- detects `ItemArmature` or the billboard correction nodes;
- creates a `THREE.AnimationMixer` on the loaded item scene;
- filters `gltf.animations` to clips named `GlobalSequence_<number>`;
- starts every global clip with repeat looping;
- advances the mixer using `dtMs / 1000`;
- applies billboard corrections after `mixer.update`;
- stops/uncaches the actions during disposal.

Do not start this mixer for the character body's normal armature. `animation-control.js` already starts all character `GlobalSequence_*` clips alongside the selected body animation. Double-running them would produce subtle timing and state bugs.

`installM2Fx` currently returns `null` when no material manifest exists. Change that condition so a GLB containing an item rig or bone animation still receives an update handle even if its `suiFx.meshes` dictionary is empty.

The returned aggregate handle should update all applicable channels in this order:

```text
material tracks
item animation mixer
billboard corrections
particle systems
```

Particles can remain last unless a future emitter needs an animated bone mount. Thunderfury itself has no particles.

### 6. Port the proven M2 camera-facing law

Use `SpellMeshSkinningLaw.ApplyBillboardBones` as the source of truth. The relevant flag precedence is:

1. `0x04`: ignore parent rotation;
2. otherwise evaluate billboard bits in `0x78`.

The full billboard (`0x08`) basis in model space is:

```text
bx = -cameraForward
by = cameraRight
bz = cameraUp
```

MSUIClient then forms a facing matrix whose axes correspond, after converting its row-vector `System.Numerics` convention to Three.js's column-major object convention, to:

```javascript
matrix.makeBasis(bx, bz, by.clone().negate());
```

Other modes must be retained rather than treating every value in `0x78` as full-facing:

- `0x40`: retain the bone's animated Y axis and solve the other axes against camera forward;
- `0x10`: retain the bone's animated X axis;
- remaining billboard mode (normally `0x20`): retain the corresponding authored axis exactly as in `SpellMeshSkinningLaw`.

Important coordinate-space rule: compute the camera vectors in the item model's local space, build the desired model-local orientation, then convert it into the correction node's parent-local quaternion. This is necessary because equipped weapons inherit character hand transforms and may be rotated/scaled by the attachment hierarchy.

Conceptually:

```javascript
rootWorldQ       = itemRoot.getWorldQuaternion(...)
rootWorldInvQ    = rootWorldQ.clone().invert()
cameraForwardM   = cameraForwardWorld.applyQuaternion(rootWorldInvQ)
cameraRightM     = cameraRightWorld.applyQuaternion(rootWorldInvQ)
cameraUpM        = cameraUpWorld.applyQuaternion(rootWorldInvQ)
desiredModelQ    = quaternionFromM2Basis(...)
desiredWorldQ    = rootWorldQ * desiredModelQ
parentWorldInvQ  = inverse(correction.parent.getWorldQuaternion(...))
correction.quaternion = parentWorldInvQ * desiredWorldQ
```

Process correction nodes in parent-before-child order, and refresh the corrected subtree's world matrices before calculating a descendant correction.

### 7. Preserve the existing material and multi-texture paths

The current `GlbWriter` includes substantial fixes that must survive this work:

- per-pass blend suffixes;
- animated `suiFx` alpha, weight, RGB, and UV tracks;
- additive/modulate overlay emission;
- the fused two-UV `_mod` path used by Warglaive-style effects;
- environment mapping markers;
- embedded emitter sheets and ItemVisual effect emitters;
- identity item root transform for attachment mounting.

The new skin path should alter only vertex skin fragments and the scene insertion method. Material selection, pass naming, `emittedPasses`, manifest construction, and overlay behavior should remain shared.

For each emitted pass:

```text
ordinary rigid model -> AddRigidMesh (existing behavior)
selected animated item -> AddSkinnedMesh with the shared item joints
```

### 8. Cache invalidation

Generated item GLBs are versioned through `CacheVersionRegistry.RigidGlbVersion`, which is derived from the `GlbWriter` assembly/type stamp. Confirm that editing `GlbWriter` produces a different versioned filename in the build being tested.

Do not manually delete or replace server assets as part of implementation. Nico controls installation, deployment, cache cleanup, services, and live runtime state.

## Suggested code organization

A minimal, maintainable change should touch approximately three production files:

1. `SkinnedGlbWriter.cs`
   - expose or factor internal armature and global-sequence helpers;
   - optionally add an item-specific armature builder with correction children.

2. `GlbWriter.cs`
   - detect models requiring skinning;
   - retain `VertexJoints4` in both one-UV and two-UV vertex forms;
   - use `AddSkinnedMesh` only for affected items;
   - emit global clips using the authored bone nodes.

3. `m2fx.js`
   - play all item global-sequence clips;
   - apply M2 camera-facing corrections;
   - retain handles even when bone animation exists without material entries;
   - clean up the mixer/actions on disposal.

A fourth file is justified only if billboard metadata is added to `M2FxManifest.cs` instead of encoded in node names. Avoid broad controller changes; `ItemsController`, `equip.js`, and `item-preview.js` already route through the correct shared code.

## Tests to add

### Exporter selection tests

- A rigid item with no relevant bone behavior still exports without `skins` or `animations`.
- A model with a global-sequence bone track exports a skin and matching `GlobalSequence_*` clip.
- A model with a camera-facing influenced vertex exports a skin and a correction node carrying the exact flags.
- A model with bones that are not referenced by visible vertices does not unnecessarily switch paths, if using the strict reachability selector.

### Vertex tests

- `JOINTS_0` and `WEIGHTS_0` exist on both `TEXCOORD_0`-only and two-UV primitives.
- Four authored weights are normalized by their actual sum.
- Zero-total weights fall back to bone 0 at weight 1.
- Invalid indices cannot produce an out-of-range glTF joint reference.

### Animation tests

- One glTF clip is emitted per used global sequence.
- Translation keys include the bone's rest local translation.
- Quaternions are normalized.
- Loop-closing keys do not duplicate an existing final timestamp.
- Multiple global clips can run concurrently.

### JavaScript tests

- Character body global sequences are not double-driven.
- An item rig returns an FX handle even with no material entries or particles.
- `dispose()` stops and uncaches item mixer actions.
- Full-facing correction tracks camera rotation under a rotated attachment parent.
- Axial modes preserve their authored axis.
- Parent and child correction nodes are processed in hierarchy order.

### Structural Thunderfury assertion

Regenerate display ID `30606` and inspect the resulting GLB. Before any visual claim, it must contain:

- one or more `skins`;
- `JOINTS_0` and `WEIGHTS_0` on the affected primitives;
- bone nodes for the 35 source bones;
- correction nodes for source bones 0 through 4;
- animations for the global sequences used by bones 0 through 4 and 21 through 28;
- the existing `suiFx` material entries for the lightning passes.

If any of those are missing, the fix is structurally incomplete even if one screenshot happens to look better.

## Runtime proof checklist

After Nico performs the owner-controlled build installation/deployment and runtime restart, validate all of the following.

### Standalone Items viewer

- Open display ID `30606`.
- Rotate the camera around the weapon.
- Confirm the `LIGHTNINGBALL.BLP` orb remains correctly camera-facing from all angles.
- Confirm the orb's authored scale/rotation loops continue while rotating the camera.
- Confirm lightning fins translate and rotate around the blade rather than pulsing at a fixed origin.
- Confirm alpha/weight pulsing remains present.
- Confirm the blade and hilt remain correctly textured and positioned.

### Equipped character viewer

- Equip Thunderfury in the main hand.
- Verify all the same effect motion while the weapon inherits the animated hand attachment.
- Rotate the character and camera independently.
- Play Stand, Walk, and Run if available; ensure the orb does not detach, lag, or inherit an incorrect world-space facing.
- Swap the weapon out and back in; ensure old animation handles are disposed and no duplicate-speed animation appears.

### Regression fixtures

- Warglaive: its existing multi-texture animated energy remains working.
- Stormwind guard sword or equivalent animated weapon: existing movement remains working.
- An ordinary static one-handed weapon: unchanged placement and rendering.
- A two-handed staff: unchanged mount/visibility behavior.
- A helm and shoulder model: unchanged textures, culling, and attachment transforms.
- A model with ItemVisual particle emitters: particles remain working.
- Standalone item viewer and character viewer agree on the same weapon effects.

## Failure signatures and likely causes

### Lightning pulses but remains spatially fixed

`suiFx` is running, but the item mixer is absent, the global clips were not exported, or `JOINTS_0`/`WEIGHTS_0` were lost on the affected primitive.

### Orb moves but appears as a flat frozen card from some angles

Skinning/global clips are running, but the camera-facing correction nodes are absent or not updated after the mixer.

### Orb faces correctly in standalone preview but fails when equipped

The correction was calculated in world space without accounting for the character attachment/root transform. Convert through the item root and correction parent spaces.

### Orb faces correctly but loses its authored rotation or scale

JavaScript is overwriting the animated bone itself. Use the separate identity correction child as the skin joint.

### Warglaive loses its travelling wave

The two-UV skinned vertex path was omitted or the existing `_mod` fused-pass logic was forked incorrectly.

### Character animations play too quickly or global loops look doubled

`m2fx.js` started an item mixer on the character body armature in addition to `animation-control.js`. Gate the new mixer on `ItemArmature`/item-rig metadata.

### Ordinary equipment changes unexpectedly

The item-skin selector is too broad, or the material/pass code was duplicated instead of shared. Restore the rigid path for models without relevant animated/camera-facing influences.

## Definition of done

This work is complete only when:

1. the MangosSuperUI build succeeds;
2. the regenerated Thunderfury GLB passes the structural assertions above;
3. Nico validates both viewer surfaces at runtime;
4. Thunderfury's fins move spatially and its orb remains camera-facing while retaining authored animation;
5. Warglaives and the other regression fixtures remain correct.

A successful build proves only that the implementation compiles. A GLB containing skins and animations proves only that the missing data now survives export. Neither alone proves visual parity.
