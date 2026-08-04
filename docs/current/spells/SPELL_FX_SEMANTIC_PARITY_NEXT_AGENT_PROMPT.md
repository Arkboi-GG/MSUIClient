# Spell FX semantic parity — current-state next-agent prompt

Prepared: 2026-08-03  
Repository: `C:\Users\nico\source\repos\MSUIClient`  
Authority target: original World of Warcraft 1.12.1 client, build 5875  
Immediate next slice: **M-017, multi-weight effect-mesh skinning and nonzero-pivot inverse-bind composition**

## Assignment

Continue the spell-FX parity work from the current dirty working tree. Do not restart the broad audit and do
not accept an older `MATCH` label without retracing its semantic frames, owners, clocks, and evidence.

The next unresolved implementation/investigation item is M-017. Trace the complete effect-mesh skinning path,
prove the four-weight and pivot law against Benilla and mounted 1.12 data, correct production code if the
proof exposes a divergence, add an executable corpus/fixture validator, run all neighboring regressions, and
record exactly what remains unverified.

The current shader already consumes four weights and `M2Animator` already produces matrices containing an
inverse-pivot fold. That is a lead, not a verdict. Determine whether the end-to-end row-vector packing,
matrix order, billboard palette rewriting, invalid-index behavior, normals, and bind-pose behavior actually
match the reference before changing code. If the implementation is already correct, preserve it and close
the row with adversarial executable evidence instead of forcing a cosmetic rewrite.

## Non-negotiable claim boundary

No new lane in this handoff is `PARITY_CERTIFIED`. Several bounded laws are implemented, mounted-data audited,
and regression-checked, but synchronized original-client/MSUI captures have not been made for the corrected
ordinary-particle, special-particle, missile, ribbon, animation, or sound behavior.

Use these labels consistently:

| Label | Meaning |
|---|---|
| `UNSCOPED` | The full decision path or adversarial fixture has not been traced. |
| `STATIC_IMPLEMENTED` | Production code exists and builds; no bounded executable proof is implied. |
| `STATIC_FOUND` | The production path and a bounded formula/wiring regression agree. |
| `STATIC/DATA_COMPLETE` | The bounded law, production wiring, real fixtures, and the relevant mounted-data census pass. |
| `ASSET_CONFIRMED` | A real shipped asset activates the branch; runtime rendering is not proved. |
| `REFERENCE_LIMITED` | MSUI/data behavior is audited, but Benilla lacks the lane or original-client evidence is required to choose the exact law. |
| `CAPTURED` | The named MSUI fixture was observed; this certifies only the recorded behavior and conditions. |
| `PARITY_CERTIFIED` | Comparable original-1.12 evidence agrees in form, direction, timing, scale, density, color, and composition. |
| `KNOWN_MISMATCH` | A visible or semantic discrepancy is still present. |

A build is not runtime evidence. A pure numeric test is not a live-asset trace. An MSUI screenshot is not an
original-client comparison. A Benilla match is not original-client pixel certification.

## Authority hierarchy

1. Original 1.12.1 captures decide pixels, timing, direction, scale, density, and composition.
2. Original build-5875 M2/DBC/BLP data decide authored values and which branches are real.
3. VMaNGOS packets and object fields decide server-owned state and lifetime.
4. Benilla is an implementation oracle only for lanes it implements.
5. MSUI code, old audit labels, comments, and handoffs are leads, never final proof.

Known limits of the reference comparison:

- Benilla does not implement MSUI's DynamicObject/type-9 area-spawn orchestration. That lane is governed by
  original captures, VMaNGOS state, and build-5875 assets.
- Benilla does not implement the generic targeting decal/radius rule used by MSUI.
- Benilla explicitly approximates missile target interception/deflection behavior and the effect-controller
  wait around ribbon owner destruction. Keep those limits visible.
- One camera cannot certify billboard rules, and one frozen image cannot certify history storage or clocks.

## Current status at a glance

| Lane | Current status | What is closed | What is still open |
|---|---|---|---|
| Animation selection, clocks, lifecycle, M2 sound markers | `STATIC/DATA_COMPLETE` | One exact sequence slot is shared by rig/material/particle/ribbon consumers; bit 0 clamp/loop semantics; monotonic instance age; global clocks; self-termination; cast-hold ownership; `$SND/$DSL/$DSO`; 5,788-check mounted audit. | Matched original timing, persistent clamp presentation, sound mix, and spatialization. |
| Ordinary particle root/bone/cloud frame | `STATIC_FOUND` | Bone composes birth once; root translation carries the cloud; host attachment rotation is independent; production helper passes the moving-bone/moving-root/rotation checks. | Live Blizzard trace and capture after the correction, plus a second animated-bone asset. |
| Special particles `0x10`, `0x40`, `0x4000` | `STATIC/DATA_COMPLETE` | Full live joint/root TRS; model-space round trip; follow response; strict 30 Hz inherit gate/hold; shared 100 ms simulation clamp; 61-check mounted audit. | Isolated live runtime traces and matched original pixels for the pinned fixtures. |
| Missile release/root/history/impact | `STATIC/DATA_COMPLETE` | Release event/fallback/backstop, actual-launch clock, fixed GO-time deadline, raw-dt homing, parsed flight basis, no-tag fallback, root-carried cloud, impact ordering; 54-check audit. | Matched Fireball/Arcane Shot/markerless/close-range captures; original interception and miss-deflection behavior. |
| Ribbon committed-node history | `STATIC/DATA_COMPLETE` | World-committed pairs, parsed authored width axis, separate raw/clamped clocks, cadence, gravity, visibility, drain animation; 41-check mounted audit. | Matched moving-camera original pixels and exact original effect-controller owner-destruction wait. |
| DynamicObject/type-9 persistent areas | `REFERENCE_LIMITED` with mounted static/data audit complete | All 8 type-9 rows, 7 literal assets, distribution/rate/update/despawn ownership, one-shot shard lifetimes; 100,104 checks. | Exact original random/birth phase and impact-sound trigger instant; second original comparison spell. |
| Ground-target radius selection | `REFERENCE_LIMITED` with mounted static/data audit complete | All populated effect lanes, 24-row radius table, 218 location-target rows, maximum-positive rule, explicit 8-yard zero fallback; 779 checks. | Original mixed-radius and zero-radius presentation rule; exact marker texture/orientation/animation. |
| Geometry-model particles and recursive child pools | `STATIC_IMPLEMENTED` + `ASSET_CONFIRMED` | Code paths, tumble, child ownership/drain, 55 geometry and 13 recursion references resolved in the recorded census. | Lane-isolated runtime trace/captures and original comparison. Blizzard is not a fixture for either branch. |
| Effect mesh skinning | `UNSCOPED` for M-017 | Four weights are uploaded; skin matrices and billboard rewrites exist. This has not been accepted as an end-to-end proof. | **Immediate next item:** multi-weight, nonzero-pivot, bind-pose and animated-pose proof; fix any discovered matrix/normal/index divergence. |
| Effect mesh/ribbon/particle fog, depth, blend, alpha, lighting | `STATIC_IMPLEMENTED` | Static changes exist for fog/far clip, unfogged/no-depth-test flags, blend classes, alpha-key cutoff, authored lighting, ramp/value-zero semantics, and faint-alpha discard removal. | Controlled numeric fragment/framebuffer evidence and matched original pixels. |
| Billboard hierarchy | `CAPTURED` for the recorded Frost Armor basis fix only | One multi-angle fixture exposed and fixed a basis error. | Full axis-flag matrix, child propagation, and original comparison. |
| Terrain ground decal | `CAPTURED` for terrain only | Frost Nova terrain clipping/projection was observed. | WMO floors and repeated sloped fixtures. |
| WMO-floor ground projection | `KNOWN_MISMATCH` / open | No complete WMO triangle source is proven in the current projector. | Implement and verify WMO participation or prove the original no-surface behavior for the fixture. |
| Cone of Cold / `CLOUDS.BLP` | `KNOWN_MISMATCH` | Dropped HDR/DXT experiments did not fix the square appearance. | Decoder pixel dump, Benilla/original reference, alpha/blend diagnosis, and correction. |
| FFXGlow/framebuffer color policy | `STATIC_IMPLEMENTED`, numeric proof open | Gamma-byte combine and active `LightParams.glow` feed exist. | Controlled black/gray/color probes with explicit sRGB state and original output pixels. |

## Completed bounded slices — do not redo them blindly

### Animation and lifecycle

- Ordinary effects select file-order sequence slot 0.
- Missiles prefer AnimationData 144 (`InFlight`) and otherwise select slot 0.
- M2 sequence flag bit 0 set means clamp; clear means loop.
- Rig, material, particles, and ribbons consume the same resolved sequence slot.
- Instance age is monotonic. Local track wrap/clamp does not reset global sequences.
- Self-terminating effects reap after one selected sequence-0 pass; particles and ribbons drain separately.
- Every new cast start releases the prior cast/channel hold even if the new spell has no visual; aura state is
  separately owned.
- M2 `$SND`, `$DSL`, and `$DSO` events reach the spatial spell-audio path on crossed keyframes.
- `tools/spell-animation-lifecycle-check`: `PASS (5,788 checks)`. Mounted corpus: 599 referenced paths,
  555 resolved, 44 missing/stale, 157 multi-sequence, 339 with global sequences, 409 authored loop-decision
  corrections, 42 missile fallbacks, 579 exact rig-sequence probes, and 10 sound-marker models.

### Ordinary and special particle frames

- Ordinary non-model particles store attach-local offsets from the effect root cloud anchor. The current
  emitter bone is not reapplied after birth.
- Moving root translation carries old particles. Host attachment rotation may rotate stored particles, but
  animated effect-bone motion may not drag old ordinary particles.
- Model-space `0x10` uses the live decomposed emitter-joint and root TRS for positions, velocities/tails,
  fixed-plane basis, and geometry-particle orientation.
- Follow `0x4000` and inherit `0x40` consume the shared `min(dt, 0.1)` simulation step. Inherit uses strict
  `> 1/30 s`, current-frame delta, an already-live-pool gate, and sample-and-hold behavior.
- `tools/spell-frame-law-check`: passes moving-bone, moving-root, and attachment-rotation invariants.
- `tools/spell-particle-motion-check`: `PASS (61 checks)`. Mounted corpus: 9,717 listed M2 paths, 9,654
  parsed, 7,860 emitters, 2,550 special records, 2,391 model-space, 124 inherit, 96 follow; 599 referenced
  effect paths include 505 model-space, 61 inherit, and 20 follow records. There are 115 special records
  beneath scale-animated chains, including 52 spell records.

### Missile pipeline

- Authored release events win. A playing eventless animation waits for sequence finish, then uses
  `$CSL -> $CSR -> $CST -> base`. A request with no animation launches immediately. A requested animation
  that never starts uses a strict `> 0.25 s` backstop.
- Flight animation age begins at actual launch, not cast creation.
- The arrival deadline is fixed at GO time: `distance / speed - already queued time`.
- Homing uses raw elapsed time and `gap * dt / remaining`; it does not inherit the particle 100 ms clamp.
- Authored +X faces flight; parsed +Y remains the roll-free world-up direction; parsed +Z closes the frame.
- Ordinary missile particles ride root translation without receiving the free-model rotation after birth.
- Impact ownership changes before the final visual position can snap.
- Missing/out-of-range destination attachment state uses the explicit no-tag sentinel and begins fallback at
  `0x0F/0x13/base`, never chest/attachment zero by accident.
- `tools/spell-missile-pipeline-check`: `PASS (54 checks)`. Mounted corpus: 981 speed spells; 824 with visual
  rows and 157 without; 64 distinct missile paths, 63 resolved; 45 particle models, 35 ribbon models, 25
  InFlight models, 169 emitters, 8 follow, and 5 inherit. The one unresolved shipped path is
  `Particles\FrostBolt_Missle.m2`.

### Ribbon history

- The live posed root/bone creates only the current head and a newly committed edge.
- Committed top/bottom pairs remain in world history and never receive a later root/bone pose.
- Authored WoW local +Y maps through MSUI's `(x,y,z) -> (x,z,-y)` parser to parsed local `-Z`, not `+Y`.
- Width uses live rotation only; scale is discarded for the cross-section direction.
- Raw source/wall age expires pairs and advances longitudinal U. A separate `min(dt, 0.1)` clock advances
  emission, sag, height/color/alpha tracks, including during owner-loss drain.
- `tools/spell-ribbon-history-check`: `PASS (41 checks)`. Mounted corpus: 176 ribbon models, 590 records,
  350 spell ribbons, 318 referenced ribbons, 80 missile ribbons, 102 gravity, 142 animated height, 214
  animated alpha, 90 scale-animated chains, and 570 animated-bone chains. The adversarial fixtures are
  `Spells\ArcaneShot_Missile.m2`, `Spells\HolySmite_Low_Chest.m2`, and
  `Item\ObjectComponents\Weapon\Thrown_1H_Dagger_A_01.m2`.

### DynamicObject area and target radius

- The area validator pins all eight build-5875 type-9 rows, the seven client-literal model selectors, all
  populated rates, 124 persistent-area spells, 30 distinct visuals, live field updates, uniform-disc
  placement, despawn birth stop, and tail drainage.
- The radius validator pins all three Spell effect lanes, the 24-row SpellRadius table, all 218
  location-target rows, 182 authored footprints, 36 zero-radius fallbacks, and values from 1 to 100 yards.
- These are not Benilla-certified laws. Keep both lanes `REFERENCE_LIMITED` until original evidence decides
  exact phase/random/audio and mixed/zero-radius visual presentation.

## Immediate next item — M-017 effect-mesh skinning

### Decision to settle

For every effect-mesh vertex in parsed MSUI model space, the reference law is:

`posed = sum(weight[k] * (bindVertex * inverseBind[bone[k]] * jointGlobal[bone[k]]))`

under MSUI's row-vector convention, followed by the effect instance root exactly once and camera subtraction
exactly once. In the current animator, the intended per-bone skin matrix is the row-vector equivalent
`T(-pivot) * jointGlobal`. At bind pose, `jointGlobal = T(pivot)`, so each influence must reduce to identity.

Do not copy Benilla's column-vector multiplication order textually. Preserve the semantic order while
translating between Bevy/glam and `System.Numerics`/GLSL packing conventions.

### Required trace

Trace and cite the complete path:

1. Raw M2 vertex position, normal, four byte weights, and four global bone indices.
2. `M2Reader`'s one-time `(x,y,z) -> (x,z,-y)` conversion for vertices, normals, pivots, and animation data.
3. Weight normalization and zero-total fallback in `SpellEffectMeshRenderer.Resolve`.
4. Bone hierarchy/rest offsets and animation sampling in `M2Animator`.
5. The exact meaning of `M2Animator.Evaluate` output and `M2Animator.Pack`'s row/column handoff.
6. The shader's four-influence position and normal sums, including zero/invalid indices and weight totals.
7. `ApplyBillboardBones`, including parent propagation, pivot removal/reapplication, and how its rewritten
   palette remains valid for vertices weighted partly to a billboard bone and partly elsewhere.
8. The effect instance `uModel` transform and camera-relative boundary.

Use `BENILLA_SPELL_SYSTEM_TRACE.md` sections 2.1, 3, and 10 as the initial map. Recheck the cited Benilla
source if it is available; the trace document is not final authority.

### Required mounted-data census and fixtures

Add a dedicated executable validator, preferably `tools/spell-mesh-skinning-check`, that scans the complete
mounted build-5875 M2 list rather than only a hand-picked spell. Report at minimum:

- listed and successfully parsed M2 counts;
- models/vertices with 1, 2, 3, and 4 nonzero influences;
- effect models referenced by SpellVisual, and the same influence histogram for that subset;
- multi-weight effect models with nonzero pivots;
- multi-weight vertices under animated translation, rotation, and scale chains;
- vertices with zero total weight, out-of-range indices, duplicate indices, or totals not equal to 255;
- multi-weight vertices touching billboard/ignore-parent-rotation bones;
- selected real fixtures and why each is adversarial.

Choose at least:

- one real SpellVisual-referenced effect with two or more nonzero weights and a nonzero pivot;
- one fixture with three or four live influences if the corpus contains one;
- one animated translation/rotation fixture;
- one scale-animated chain if present;
- one billboard/child propagation fixture if real data combines that condition with multi-weight skinning;
- one negative/control asset whose vertices are single-weight or unskinned.

If a requested category does not exist in mounted build-5875 data, report a zero census and retain a
synthetic negative fixture for the branch. Do not silently replace a missing real fixture with a synthetic
one.

### Required executable invariants

The validator must exercise production helpers or the exact production packing code, not a parallel formula
that can agree with itself. At minimum pin:

1. **Bind-pose invariance:** every influence reduces to identity at rest, including nonzero pivots.
2. **Four-weight contribution:** all four nonzero weights affect the result; removing any one changes an
   adversarial output by a known amount.
3. **Weight normalization:** byte weights are normalized exactly as production does; zero-total behavior is
   explicit and verified.
4. **Pivot rotation:** a vertex offset from a nonzero pivot rotates about that pivot, not the model origin.
5. **Hierarchy:** parent and child animation compose once in the correct order.
6. **Scale:** non-uniform live scale neither loses the inverse-bind translation nor double-applies the root.
7. **Normals:** translation is excluded; the exact reference policy for weighted/non-uniformly scaled normals
   is stated and tested. Do not assume position skinning is sufficient proof for lighting.
8. **Billboard palette:** a rewritten parent affects children as authored without double rebasing the pivot.
9. **Root/camera boundary:** the effect root is applied once after skinning and camera subtraction occurs once.
10. **CPU/GPU contract:** packed matrices and shader dot products produce the same numeric result as the
    semantic reference for adversarial matrices and weights.

If GL execution is impractical in the headless validator, extract the matrix packing/dot-product contract
into a small production law used by both the renderer and validator, and separately keep a shader-source
interface assertion. Do not validate a newly invented test-only implementation.

### Acceptance criteria for M-017

- The full path is documented using data source, semantic owner, input frame, output frame, sampling time,
  transform-after-birth rule where applicable, fixture, invariant, verdict, and evidence level.
- Any real mismatch is corrected in shared production code without spell-specific exceptions.
- The dedicated validator passes pure adversarial cases, real mounted fixtures, and the full relevant corpus.
- The solution and all existing spell validators pass.
- `SPELL_FX_SEMANTIC_FRAME_AUDIT.md`, `SPELL_RENDERER_DECISION_AUDIT.md`, and
  `SESSION_2026-08-03_SPELL_SLICES.md` are updated consistently.
- The final claim is no higher than the evidence. Without matched original output, the expected ceiling is
  `STATIC/DATA_COMPLETE`, not `PARITY_CERTIFIED`.

## Verification backlog after M-017

### Implementation or unresolved-law work

1. WMO-floor decal projection: connect projectable WMO triangles and prove the no-surface behavior.
2. Cone of Cold `CLOUDS.BLP`: settle decoder alpha and the fragment/blend path behind the visible squares.
3. Controlled shader/framebuffer/glow policy: numeric sRGB/gamma/fog/blend/glow probes.
4. Any head/tail particle quad or mesh-normal divergence exposed by the fixture census.

### Runtime/MSUI capture work for already implemented laws

1. Blizzard after the ordinary root/bone/cloud correction: two descent times, two camera angles, recorded
   root, emitter, bone offset, cloud principal axis, and span.
2. A second animated-bone ordinary emitter and a moving-root/static-bone effect.
3. Isolated `0x10`, `0x4000`, and `0x40` fixtures with uneven/hitch timing.
4. Fireball and Arcane Shot after the missile/ribbon corrections, plus markerless launch and close-range
   no-flight impact.
5. Arcane Shot/Holy Smite ribbon width, world-history sag, visibility, and owner-loss drain.
6. Geometry-model and recursive-particle fixtures independently.
7. Multi-weight mesh fixtures selected by M-017, including a mesh-only capture.
8. Repeated terrain decal and billboard flag/child cases.

### Original-client-limited verification

1. Matched captures for every corrected runtime lane above.
2. DynamicObject exact random sequence, birth phase, and impact-sound trigger instant.
3. Target marker mixed-radius/zero-radius sizing and exact texture/orientation/animation.
4. Missile ray/sphere interception and missed-projectile deflection/bounce behavior.
5. Exact ribbon effect-controller destruction wait.
6. Numeric output pixels for blend/fog/lighting/alpha/glow with controlled backgrounds.

Do not let capture-only work fall back into `UNSCOPED`, and do not describe an implementation gap as merely
capture-only. The tables above deliberately separate these states.

## Required regression commands

Run from `C:\Users\nico\source\repos\MSUIClient` with the mounted client path configured in
`MSUIClient\client-config.json`:

```powershell
dotnet build MSUIClient.sln --no-restore
dotnet run --project tools\spell-frame-law-check\spell-frame-law-check.csproj
dotnet run --project tools\spell-animation-lifecycle-check\spell-animation-lifecycle-check.csproj -- MSUIClient\client-config.json
dotnet run --project tools\spell-particle-motion-check\spell-particle-motion-check.csproj -- MSUIClient\client-config.json
dotnet run --project tools\spell-missile-pipeline-check\spell-missile-pipeline-check.csproj -- MSUIClient\client-config.json
dotnet run --project tools\spell-ribbon-history-check\spell-ribbon-history-check.csproj -- MSUIClient\client-config.json
dotnet run --project tools\spell-area-visual-check\spell-area-visual-check.csproj -- MSUIClient\client-config.json
dotnet run --project tools\spell-target-radius-check\spell-target-radius-check.csproj -- MSUIClient\client-config.json
dotnet run --project tools\interface-wire-check\interface-wire-check.csproj
```

After M-017, add its validator to this list. If a validator is already built and inputs are unchanged,
`--no-build` is acceptable for iteration, but the final pass must compile the changed project. Record the
actual output; do not copy expected counts into a completion claim without running it.

The last recorded full solution build passed. A direct non-incremental project build has previously surfaced
an existing unrelated CA2014 warning in `GlueAdditive.cs`; report the exact command/output rather than
claiming the whole tree is permanently warning-free.

## Key files

Current authoritative audit and session record:

- `SPELL_FX_SEMANTIC_FRAME_AUDIT.md`
- `SESSION_2026-08-03_SPELL_SLICES.md`
- `SPELL_RENDERER_DECISION_AUDIT.md` — historical 96-row baseline plus follow-ups; lower baseline labels may
  be stale and must not be copied forward automatically.
- `BENILLA_SPELL_SYSTEM_TRACE.md`
- `MSUI_SPELL_SYSTEM_TRACE.md`

Immediate M-017 production path:

- `MSUIClient\Formats\M2Reader.cs`
- `MSUIClient\World\Units\M2Animator.cs`
- `MSUIClient\World\Units\SpellEffectMeshRenderer.cs`
- the inline `VertexSource` in `SpellEffectMeshRenderer.cs`
- `MSUIClient\World\Units\SpellEffectSource.cs`
- `MSUIClient\World\Units\SpellAttachment.cs`
- `MSUIClient\Shaders\character.vert` as a neighboring CPU/GPU skinning implementation, not automatic
  proof for the spell path.

Completed production-law helpers and validators:

- `MSUIClient\World\Spells\SpellParticleFrameLaw.cs`
- `MSUIClient\World\Units\SpellEffectPlaybackLaw.cs`
- `MSUIClient\World\Units\SpellMissileLaw.cs`
- `MSUIClient\World\Units\SpellRibbonHistoryLaw.cs`
- `tools\spell-*-check\`

## Working-tree and implementation guardrails

- The working tree is intentionally dirty and contains the accumulated parity work. Preserve unrelated user
  changes. Do not reset, discard, or mass-format them.
- Use a stable decision ID. M-017 already exists; update it rather than creating a competing row.
- Fix shared semantic laws, not individual spells. Do not special-case Blizzard, Cone of Cold, Fireball,
  Arcane Shot, or a chosen mesh fixture to hide a shared error.
- Convert coordinate bases once and name the frame at every boundary. “Local,” “world,” “bone,” and “age”
  are insufficient without a semantic owner.
- Keep root placement, bone pose, persistent history, host attachment rotation, and camera subtraction
  distinct.
- Keep raw/wall time, simulation-clamped time, selected-clip time, global-sequence time, and owner lifetime
  distinct.
- Build-5875 zeros and absent branches are evidence. Do not invent behavior to make a test convenient.
- Prefer real adversarial assets. Pair every positive fixture with a negative/control fixture.
- Keep diagnostics deterministic and off by default.
- Preserve exact mounted-corpus counts in validator output so archive/config drift fails loudly.
- Never raise a status merely because code now “looks Benilla-like.” State what the evidence cannot prove.

## Required final handoff from the next agent

Deliver all of the following:

1. The traced M-017 reference and MSUI laws, including coordinate and matrix-convention translation.
2. The mounted-data influence/pivot/animation/billboard census and named adversarial fixtures.
3. The production correction, or a documented proof that no correction was required.
4. A dedicated executable validator tied to production code, with exact passing counts.
5. Solution and neighboring-validator results.
6. Updated audit/session documents with no contradictory stale status.
7. An honest residual list split into implementation gaps, MSUI runtime captures, and original-client-only
   verification.
8. The next unresolved item selected from that residual list.

Success for the next slice means M-017 is no longer `UNSCOPED`, all four weights and nonzero pivots are
adversarially pinned in the actual production contract, neighboring spell-FX behavior remains green, and
the remaining verification burden is explicit. It does not mean global spell-FX pixel parity is certified.
