# Spell FX semantic-frame audit

Prepared: 2026-08-04
Authority target: original WoW 1.12.1 client build 5875

## Current claim boundary

This document supersedes the coarse attachment claim in `SPELL_RENDERER_DECISION_AUDIT.md` row P-039.
It is a living audit, not an A-to-Z parity certificate. The root/bone/cloud law now has a bounded static
implementation and a passing numeric regression check. On 2026-08-04 the user visually validated angled
Blizzard shards and restored tails after the shared frame and two-sided-raster corrections. This supports an
MSUI `CAPTURED` observation, but no recorded matched original-client/MSUI Blizzard capture has been made, so
the Blizzard wake is not `PARITY_CERTIFIED`.

Current evidence for the corrected ordinary-anchor and special-motion laws: `STATIC/DATA_COMPLETE`. The
production build, pure frame checks, complete mounted M2 census, real authored fixtures, and live
`SpellEffectSource` frame feed pass. Matched original-client pixels/runtime traces are still required.

M-017 effect-mesh skinning is also `STATIC/DATA_COMPLETE` at its bounded claim: all four weights, nonzero
pivots, hierarchy, scale, billboard palette propagation, matrix packing, normals, root placement, and camera
subtraction are executable-audited against the mounted build-5875 corpus. No mesh-only MSUI/original-client
capture has been made, so this is not pixel or runtime parity certification.

## Authority and evidence rules

1. Original 1.12.1 captures decide pixels, timing, direction, scale, and composition.
2. Original client M2/DBC/BLP data decide authored values.
3. VMaNGOS packets and object fields decide server-owned runtime state.
4. Benilla is an implementation oracle only for lanes it implements.
5. MSUI code and handoffs are leads, never final proof.

A build proves only that a static implementation compiles. The pure numeric check proves its formulas, but it
does not prove that a live asset supplies the expected frames or that final pixels agree with 1.12.

## Semantic frame ledger

| Symbol | Exact meaning | Producer | Consumers / forbidden reuse |
|---|---|---|---|
| `F_file` | Raw authored M2 space, WoW Z-up. | M2 bytes | Must pass the single `(x,y,z) -> (x,z,-y)` conversion before MSUI model-space use. |
| `F_model` | MSUI parsed effect-model space, Y-up. | `M2Reader` | Bones, mesh bind vertices, emitter records; not game-world space. |
| `R(t)` | Effect/model instance root-to-world placement. Translation is the hosted/area ordinary-particle cloud anchor, but not the free-missile history anchor. | `SpellEffectSource.TryTransform` | Mesh, particles, ribbons, decals. It is not an emitter bone and not the lifetime owner. |
| `A(t)` | Host attachment frame for an effect attached to a unit/item. | `SpellAttachment.World` through `R(t)` | May rotate anchored stored particles after birth. Must remain distinct from effect bone animation. |
| `B_i(t)` | Evaluated effect-model bone/joint `i` relative to the effect root, including decomposed live scale and rotation. | `M2Animator` effect skin | Composes emitter births, ribbon heads, and skinned geometry. Ordinary particles must not reapply it after birth; model-space draw and motion conversion must retain its scale. |
| `K` | Emitter shape-kernel position/direction after the authored +90-degree basis fold. | `SpellParticleSystem.Spawn` | Composed through `B_i(t0)` and `R(t0)` at birth. |
| `C(t)` | Root cloud anchor position: translation of `R(t)`. | Particle instance input | Draw anchor for hosted/area ordinary non-model particles. Free missiles use `S_world`. Never substitute current emitter position. |
| `E_i(t)` | Current evaluated emitter world origin: `R(t) * B_i(t) * emitterPosition`. | `SpellEffectSource.EmitterInstances` + particle simulation | Birth, follow/inherit deltas, density distance, kill-outbound center. Never the ordinary cloud draw anchor. |
| `S_anchor` | Persistent hosted/area ordinary-particle position: attach-local offset from `C(t0)`. | `SpellParticleFrameLaw.StoreAtBirth` | Draw is `C(t) + A(t) * S_anchor`; no `B_i(t)` term. |
| `S_model` | Persistent flag-`0x10` particle state in the verified model-space lane. | Particle birth | Intentionally reprojected through the live model/bone frame. Must not define ordinary storage. |
| `S_world` | Absolute world storage used by the free-missile history lane. | Lane-specific birth | Fireball/Frostbolt were user-validated after restoring this law; recorded matched original evidence remains open. |
| `N_world` | Committed ribbon node world/history state. | Ribbon simulation at commit time | Old nodes must not receive a live emitter-bone transform unless authored law says so. |
| `G_bind` | Bind-pose mesh/decal vertex with weights and pivots. | M2 vertex data | Must use inverse bind/pivot rebase before posed bones; direct joint multiplication is insufficient. |
| `G_pose(t)` | Skinned/posed effect geometry. | Mesh palette | Then passes through `R(t)` exactly once. |
| `D_surface` | Projected terrain/WMO surface geometry and fitted animated corner UV frame. | Ground projector | Camera subtraction occurs only after world projection. Current WMO coverage is open. |
| `W(t)` | Final game-world position. | Per-lane composition | Camera origin must be subtracted once after reaching this frame. |
| `V(t)` | Camera-relative GPU position. | Vertex upload/shader boundary | Must not be fed back into terrain, attachment, or simulation logic. |
| `F_fb` | Framebuffer color representation and encode state. | Render/postprocess pipeline | Blend/gamma/glow claims require numeric probes, not appearance alone. |

### Per-lane transform laws

| Lane | Birth / source law | Persistent state | Draw law | Evidence |
|---|---|---|---|---|
| Hosted/area ordinary anchored particle | `birthWorld(t0) = R(t0) * B_i(t0) * K` | `S_anchor = A(t0)^-1 * (birthWorld(t0) - C(t0))` | `W(t) = C(t) + A(t) * S_anchor`; explicitly no `B_i(t)` | Static implementation + numeric check; Blizzard visually exercised. |
| Free-missile ordinary particle | Same one-time root/bone birth composition | Absolute `S_world` position and velocity | Draws stored world history; later missile root translation/rotation is not reapplied | Fireball/Frostbolt user-validated; missile validator pins the production feed. |
| Model-space particle `0x10` | Kernel remains in the model lane | `S_model` | Live decomposed root+bone TRS reprojects position, velocity/tail, flat-quad basis, and geometry-particle orientation | Production helper + mounted corpus + real scaled spell frame feed; pixel trace open. |
| Follow `0x4000` | Uses one-frame `E_i(t)-E_i(t-dt)` and the shared `min(dt,.1)` step | Adds the response-line correction converted into the chosen stored frame; fresh births skip it | Does not grant the ordinary lane implicit bone following | Pure behavior checks + Arcane Shot authored fixture; pixel trace open. |
| Inherit `0x40` | Strict 30 Hz sample-and-hold of the current one-frame emitter delta; zero until particles already live | Held world velocity, converted into the particle storage frame at birth | Birth adds `(1 + S11*speedVariation) * held` | Pure cadence/hitch/live-gate checks + Bloodlust/Abolish Magic fixtures; velocity trace open. |
| Geometry particle | Same parent-particle position lane; orientation seeded at birth | Particle position/quaternion/angular velocity | Per-particle sub-model root then ordinary M2 material path | Static implementation; captures open. |
| Recursive particle | Parent particle position/velocity drives child birth | Child uses the parent's storage lane | Draws at the same cloud anchor as parent | Static implementation; captures open. |
| Effect mesh | `G_pose = sum(w_i * (G_bind * T(-pivot_i) * B_i(t)))`; then `R(t)` once | No particle history | `W(t)`, then camera subtraction once; normals use inverse-transpose of the blended skin+root linear map | Production helper + complete mounted referenced-asset census + Rake/Undying/Arcane Shot fixtures; matched pixels open. |
| Billboard child | Camera basis converted into `F_model`, constrained by authored flags | Rewritten palette bone and descendants | Skinned through the same `R(t)` | Previously captured for Frost Armor, not full flag coverage. |
| Ground decal | Animated bind corners -> inverse bind -> posed bone -> world surface clip | Projected world triangles for the frame | World triangles -> `V(t)` | Terrain captured; WMO floor open. |
| Ribbon | Live root + posed bone create the head; authored WoW local +Y maps to parsed local -Z for width | Committed top/bottom pairs in `N_world`; raw age expires edges while a separate 100 ms-clamped clock samples ribbon look tracks | Only the live head sees the current root/bone; old pairs sag/expire in world and drain after owner loss | Benilla/static/data complete; matched original pixels open. |
| Missile | Live release marker/cascade -> moving missile root | Root-carried ordinary particle storage; free-model rotation is not reapplied; ribbon nodes remain their own history lane | Raw-dt arrive-on-time homing; impact handoff precedes final snap; particle pools drain at the last root | Benilla/static/data complete; matched original-client pixels remain open. |
| DynamicObject area | Server world position/radius/duration -> area instance `R(t)` | Server object owns duration; spawned shard model owns its one-shot | Model up-axis fold, phase/orientation, terrain contact | Benilla does not certify this orchestration lane. |
| Shader/postprocess | `V(t)` fragments plus authored blend/fog flags | Framebuffer/glow buffers | Gamma/linear policy and presentation encode | Static implementation exists; numeric pixel probes open. |

## Lifecycle-owner ledger

| Lane | Lifetime owner | Root transform owner | Persistent-state owner | Drain rule status |
|---|---|---|---|---|
| Precast/cast/impact/state/channel kit | Spell stage/aura/channel instance | Unit attachment or unit root | Each emitter/ribbon/mesh lane | Particles/ribbons statically drain; cross-lane clock proof open. |
| Missile | Fixed GO-time deadline until impact/miss/target loss | Moving projectile root | Each particle pool/ribbon history | Flight sound/root stop at handoff; particle/ribbon tails drain from their last committed state. |
| DynamicObject area | VMaNGOS DynamicObject | Server world position | Area spawner plus one-shot shard instances | Benilla unavailable as area-orchestration proof. |
| Self-terminating effect | First selected sequence span | Spawned effect root | Lane-local live state | Particles may outlive model emission; synchronized drain capture open. |
| Geometry/recursion child | Parent emitter and live parent particles | Parent particle/root frame | Per-particle submodel/private child pool | Static only. |

## Revised decision-point slice

This table uses the expanded schema. The original 96-row table remains a useful baseline, but its rows have
not all been migrated and must not retain a `MATCH` label automatically.

| ID | Layer | Decision | Data source | Semantic owner | Input frame | Output/storage frame | Sampling time | Reference law | MSUI law | Fixture | Observable invariant | Verdict | Evidence level | Impact |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| P-046 | Particle | Hosted/area ordinary live particles anchor translation at the effect/model root, not the animated emitter bone; the free-missile exception uses `S_world`. | M2 flag `0x10` clear; effect instance root; evaluated emitter bone; owner lane | Root cloud anchor or free-missile world history | `birthWorld` in `W(t0)` | `S_anchor` or `S_world` | Birth; hosted root sampled again at draw; free missile root not reapplied | Benilla trace plus original-client visual authority where implementations conflict | `RootCarriesCloud`/`HostAttachmentRotatesCloud` supplied per lane; pure operations in `SpellParticleFrameLaw.cs` | Blizzard hosted/area path; Fireball/Frostbolt free-missile path; second animated-bone emitter still needed | Moving emitter bone does not drag old particles; hosted roots carry their clouds; free missiles retain absolute trail history | static checks plus user-validated named spells | `CAPTURED` | Collapsing owner lanes either destroys missile trails or leaves hand/body clouds behind. |
| P-047 | Particle | Emitter bone composes birth position and direction once. | Emitter bone index/position; evaluated effect skin | Emitter bone | `F_model` kernel/bone | `birthWorld(t0)` | Birth | Benilla `particles/sim.rs:462-475,601-619` | `SpellEffectSource.cs:427-438`; `SpellParticleSystem.cs:165-172,682-700` | Blizzard | Consecutive births numerically follow `E_i(t0)` | static-match | `STATIC_FOUND` | Ignoring the bone makes trails originate at the root; reusing it at draw collapses history. |
| P-048 | Particle | Root motion carries hosted/area ordinary non-model clouds, never free-missile world history. | Effect root translation plus owner lane | Root | `S_anchor` or `S_world` | `W(t)` | Draw | Reference implementation plus original visual evidence | `RootCarriesCloud` is explicit and false only for free missiles | Moving owner/static bone fixture; Fireball/Frostbolt | Root translation `d` moves hosted old particles by `d` and moves free-missile old particles by zero | static checks plus user validation | `CAPTURED` | Globalizing either choice breaks a different class of spell. |
| P-049 | Particle | Host attachment rotation after birth rotates anchored stored particles without reapplying the emitter bone. | Unit attachment root rotation | Host attachment | `S_anchor` | `W(t)` | Birth inverse; draw live rotation | Benilla `particles.rs:119-139`; `sim.rs:433-451,610-619` | `HostAttachmentRotatesCloud` and `HostAttachmentRotation` are independent inputs | Turning hand/chest effect | A 90-degree host turn rotates a stored unit-X offset to unit-Y while emitter-bone motion has no effect | static-match | `STATIC_FOUND` | Sprays fail to fan with the host or incorrectly swirl with effect bones. |
| P-050 | Particle | Flag `0x10` uses the distinct live model-space reprojection law and preserves the complete decomposed joint/root TRS. | Particle emitter flags; posed bone scale/rotation; root placement | Effect model/bone | `S_model` | `W(t)` | Tick/draw | Benilla `particles.rs:63-77`; `sim.rs:469-476,620-622`; `quads.rs:149-154,220-228` | `SpellEffectSource` supplies `LocalFrame`; `SpellParticleFrameLaw` composes/inverts it for draw, follow/inherit, tails, XY quads, and geometry particles | `Spells\AbolishMagic_Base.m2` scaled ancestor; full 9,717-path census | Model-vector draw/store round-trip is exact; a real live spell frame has non-unit scale; old particles reproject under the live frame | static/data complete; capture open | `STATIC_FOUND` | Quaternion-only storage discards joint scale and distorts placement/motion conversion. |
| P-051 | Particle | Follow flag `0x4000` applies only its authored response-line motion correction using the shared clamped step. | Follow speed/scale fields and flag | Emitter motion | `E_i` one-frame delta | Chosen particle storage frame | Tick, fresh-particle skip | Benilla `particles/sim.rs:478-495` | Pure `FollowCorrectionWorld`; `Simulate` supplies `min(dt,.1)` and model storage uses inverse full emitter frame | `Spells\ArcaneShot_Missile.m2` e0 `0x4109`, `(2.5,.1)->(16.667,.9)` | Control points/saturation agree; a .25 s hitch uses .1 s; unflagged path is zero | static/data complete; capture open | `STATIC_FOUND` | Raw hitch dt changes authored response and quaternion-only conversion corrupts scaled model-space corrections. |
| P-052 | Particle | Kill-outbound center is the emitter origin expressed in persistent storage, not assumed zero for root-anchored particles. | Shape sphere + flag `0x80` | Emitter bone | `E_i(t)` | `S_anchor` or `S_world` | Tick | Benilla `particles/sim.rs:520-532` | Corrected via `StoreAtBirth(E_i,C,A)` | Proven inward sphere emitter needed | Dot-product kill boundary remains centered on the live emitter | static-match | `STATIC_FOUND` | Converging streams die at the wrong point after root/bone separation. |
| P-053 | Particle | Flag `0x40` samples current-frame emitter motion on a strict `> 1/30 s` trigger, gates on an already-live pool, and holds the result until the next trigger. | Inherit flag/scale; emitter world delta; shared clamped dt | Emitter motion | Current-frame `E_i` delta in world | Held world velocity, then particle storage frame at birth | Tick sample; birth consume | Benilla `particles/sim.rs:498-507,637-644` | Pure `UpdateInheritedMotion`; one shared clamped step; model conversion uses inverse full emitter frame | `Spells\Bloodlust_State_Hand.m2` e0 model+inherit scale 3; `AbolishMagic_Base.m2` | Equality does not fire; prior deltas do not accumulate; no-live trigger yields zero; held value survives interim frames | static/data complete; capture open | `STATIC_FOUND` | Accumulated deltas, `>=`, raw hitch dt, or lost joint scale change launch velocity. |
| P-054 | Particle raster | All particle head/tail quads are two-sided; projected tails may reverse winding relative to head billboards. | Particle quad basis; inherited GL cull state | Particle render pass | Head/tail axes in camera/world space | Submitted GPU quads | Draw; prior state restored after pass | Benilla `particle_material` uses unconditional `cull_mode: None` | `SpellParticleSystem.Render`; `SpellParticleTrailLaw.CullBackFaces == false`; opposite-winding validator assertion | Blizzard emitters 1/2, tail-only `FROST3.BLP`, 1.7/1.2 s tails | Submitted tail quads remain visible regardless of winding; heads being visible is not a sufficient cull-state test | static check plus user-validated MSUI observation | `CAPTURED` | Inherited back-face culling removes every Blizzard tail while snowflakes/shard heads still render, disguising the pipeline failure as an asset problem. |
| R-015 | Ribbon | The current posed bone/root creates only the live head and new edge; every committed top/bottom pair remains in world history. | Ribbon bone/position; parsed axis fold; height/rate/lifetime/gravity tracks | Ribbon history state | Parsed `F_model` head and authored WoW +Y width axis through current bone/root | `N_world` top/bottom pairs plus raw birth age | Live head each render; pair only at cadence; old pairs sag/expire without pose resampling | Benilla `ribbons.rs:45-76,216-349`; `wow_to_bevy` basis | `SpellRibbonHistoryLaw`; renderer stores committed pairs directly and never reapplies root/bone | `ArcaneShot_Missile.m2` animated-bone InFlight trail; Holy Smite keyed slash; 9,717-path census | A later bone/root move changes the head by >1 yd while the old Arcane pair remains bit-identical; identity authored +Y is parsed -Z, not parsed +Y | static/data complete; capture open | `STATIC_FOUND` | Reapplying live pose rigidly drags the whole trail; using parsed +Y rotates its width plane by 90 degrees. |
| R-016 | Ribbon | Edge expiry and ribbon look animation intentionally consume different clocks. | Frame elapsed time; edge lifetime; height/color/alpha tracks | Ribbon history state | Raw source/wall elapsed plus clamped simulation delta | Raw edge age; private clamped clip age | Every live and drain frame | Benilla `ribbons.rs:216-235,255-318` | `AdvanceLive`/`AdvanceDrain`: raw time expires and advances U; `min(dt,.1)` advances emission, sag, and look tracks even after owner loss | Holy Smite alpha is still >0.9 at clamped 0.1 s while raw 0.5 s is <0.1; drain expiry crosses at raw lifetime | Hitches cannot prematurely skip the slash look or make edge lifetime stretch; draining alpha continues rather than freezing | static/data complete; capture open | `STATIC_FOUND` | One shared clock either kills trails late or skips keyed flare/fade frames during a hitch. |
| M-017 | Mesh | Four weighted bind vertices use pivot inverse-bind rebasing, posed hierarchy, reference normal policy, billboard-rewritten palette, one effect root, and one camera subtraction. | Raw M2 position/normal, four byte weights/global bone indices, bones/pivots/TRS/flags | Mesh vertex and joint palette | Parsed `G_bind` in `F_model` | `G_pose(t)`, then `W(t)`, then camera-relative `V(t)` | Selected sequence draw age; billboard rewrite after pose and before packing | Benilla `model.rs:141-194,243-286,474-525`; Bevy 0.18.1 `skinning.wgsl`; `billboard.rs:160-203,280-377` | `M2Reader` converts once; `M2Animator` emits `T(-pivot)*jointGlobal`; `SpellMeshSkinningLaw` owns four-weight resolution, invalid fallback, inverse-transpose normals, billboard propagation, root/camera boundary, and shader contract | `Spells\Rake.m2` v302 four weights; `Undying_Strength_Impact_Chest.m2` v378 T/R/S chain; `ArcaneShot_Missile.m2` all-single control; synthetic billboard/invalid/zero cases | 555 resolved referenced assets/5,222 bones are bind-invariant; every Rake influence changes the pose; CPU packing equals shader dots; referenced corpus has no invalid indices or non-255 totals | static/data complete; mesh-only capture open | `STATIC/DATA_COMPLETE` | Wrong order or forward-normal transform orbits/shears mesh parts and mislights non-uniformly scaled poses. |
| D-001 | Decal | WMO floors participate in effect ground projection. | Projectable WMO triangles | World surface | Animated decal world prism | `D_surface` | Draw | Original capture required; Benilla projector useful | Current terrain gatherer does not prove WMO coverage | Frost Nova/reticle on WMO floor | Decal conforms to floor or follows proven no-surface behavior | mismatch/open | `STATIC_FOUND` | Ground effects float, vanish, or fall back incorrectly indoors. |
| S-001 | Shader/postprocess | Effect color/blend/glow is evaluated in the proven framebuffer color representation. | Texture format, blend, fog, FFXGlow inputs | Framebuffer | Fragment output + scene buffer | `F_fb`/presented pixel | Draw/present | Original numeric pixels; Benilla only an oracle | Static gamma-byte combine exists | Controlled ramp on black/gray/color backgrounds | Recorded input RGBA and output pixel agree with original with glow on/off | open | `UNSCOPED` | Correct geometry still looks too hard, dim, bright, or square-edged. |
| O-022 | Orchestration | DynamicObject owns area position/radius/duration while each born type-9 M2 root owns its one-shot animation phase. | VMaNGOS DynamicObject fields + build-5875 Spell/SpellVisual/SpellVisualKit/M2 census | Server object and effect root | Server `W` position/radius | Persistent area `R(t)` plus world-born shard roots | Object spawn/update/removal; per-model one-shot clock | Original/VMaNGOS; Benilla lane absent | Seven-entry literal dispatch is range-validated; all populated type-9 lanes fire at authored rate; live field updates affect the loop/future births only; despawn stops births and preserves tails | `tools/spell-area-visual-check`: all 8 type-9 rows, 7 assets, 124 persistent-area spells/30 visuals, lifecycle and 100k-point distribution checks | Existing shards are invariant under later DynamicObject movement; new births remain inside the updated wire radius; removal stops births immediately and tails expire on their own model spans | static/data complete; exact original random law open | `REFERENCE_LIMITED` | Wrong selector, clamping, stale radius/position, or owner reap changes the signature rain/snow field. |
| O-023 | Orchestration/decal | An armed location-target marker spans the complete authored effect footprint. | Spell.dbc populated `Effect[3]` + `EffectRadiusIndex[3]`; SpellRadius.dbc | Armed cast | Cursor point in `W(t)` plus authored yard radius | `D_surface` clipped terrain footprint | Every targeting-frame draw | Original client required; Benilla parses radius only for tooltip `$a` and has no generic targeting decal | Maximum positive radius across populated lanes; exact row values with an explicit 8-yard fallback only when no positive radius exists | Blizzard 10=8, Flamestrike 2120=5, mixed Flamestrike 30091=max(8,5), Spice Mortar 31364=max(10,20), Far Sight 6196=100 | Every one of 218 location-target rows agrees with the raw mounted DBC; mixed lanes cannot silently collapse to lane zero | static/data complete; original visual rule open | `REFERENCE_LIMITED` | A fixed 8-yard marker misstates 3/5/10/15/20/30/45/100-yard footprints and can mislead cast placement. |
| O-024 | Missile orchestration/frame | Release, destination lookup, fixed deadline, homing, pose, free-history cloud ownership, sound, and impact are one pipeline. | Cast-kit animation; M2 release events; SpellVisual missile fields; Spell speed; live caster/target poses | Pending GO, then missile root, then impact owner | Release marker in caster model space; destination/root in `W`; parsed missile model in Y-up `F_model` | Absolute ordinary-particle world history with no free-model attachment rotation; ribbon nodes retain independent committed history | GO fixes deadline; release edge launches; every flight tick re-resolves target and consumes raw dt; impact before snap | Original-client pixels are decisive where the current Benilla implementation conflicts with observed trail history | `SpellMissileLaw`; `SpellEffectSource`; explicit no-tag sentinel; `RootCarriesCloud=false`; no particle-step clamp in mover | Fireball/Frostbolt user validation; 981 speed spells, 64 distinct missile paths, Arcane Shot follow model, synthetic timing/no-tag fixtures | +X faces flight; parsed +Y remains up; no roll; old ordinary particles remain at prior world positions; close release may impact without flight/audio | static/data complete plus named MSUI observation; matched capture open | `CAPTURED` | A generic root-carried effect rule collapses discrete missile trails into a rigid cloud and can make one spell fix regress another. |

## Fixture coverage matrix

| Category | Fixture | Producing branch | Current evidence | Next required evidence |
|---|---|---|---|---|
| Animated-bone particle history | `Spells\Blizzard_Impact_Base.m2` | Ordinary particles on two moving frost-emitter bones; no ribbons | Asset facts confirmed in handoff; corrected law static only | World trace of `C(t)`, `E_i(t)`, births, stored positions, cloud axis/span; matched 1.12 capture. |
| Second animated-bone history | Archive census must select a non-Blizzard orbiting/translated emitter | P-046/P-047 | Not pinned | Exact path, flags, sequence, trace, capture. |
| Moving root/static bone | Moving attached effect or controlled numeric fixture | P-048 | Numeric check passes | Real M2/host turn/run capture. |
| Attached rotation | Hand/chest kit while host turns | P-049 | Numeric check passes | Exact kit/M2 and multi-heading runtime trace. |
| Model-space `0x10` | `Spells\AbolishMagic_Base.m2` e2 plus 505 referenced records | P-050 | Mounted census, numeric full-TRS round-trip, and live non-unit source frame pass | Isolated runtime trace/capture showing live scale/pose reprojection. |
| Follow `0x4000` | `Spells\ArcaneShot_Missile.m2` e0; 20 referenced records | P-051 | Authored fields and clamped-step response checks pass | Runtime correction trace plus matched original missile capture. |
| Inherit `0x40` | `Spells\Bloodlust_State_Hand.m2` e0; `AbolishMagic_Base.m2`; 61 referenced records | P-053 | Strict trigger/current-delta/live-gate/hold/hitch checks pass | Runtime birth-velocity trace at uneven frame times plus capture. |
| Head/tail quad | `Spells\Blizzard_Impact_Base.m2` emitters 1/2 (`FROST3.BLP`) | P-054 plus tail expansion | Real asset fields, opposite-winding guard, user-validated MSUI tails | Recorded multi-angle matched original/MSUI capture. |
| Geometry particle | `Spells\BestowDisease_Impact_Chest.m2` emitter 0 -> `Spells\Skull180.m2` | Geometry sub-model path | Exact archive reference and valid mesh confirmed | Isolated lane capture with tint/scale/tumble trace. |
| Recursive particle | `Spells\Bomb_ExplosionA.m2` emitter 4 -> `Spells\Fire_SmokeTrail.m2` | Private child pools | Exact archive reference and one eligible child confirmed | Parent/child position and velocity trace plus drain capture. |
| Ribbon | `ArcaneShot_Missile.m2`, `HolySmite_Low_Chest.m2`, thrown dagger InFlight, scaled spell fixtures | R-015/R-016 | 41-check law, real animated bone, keyed-width/alpha, visibility, gravity, drain, and full corpus pass | Matched moving-camera original capture of width plane, sag, and post-owner drain. |
| Multi-weight effect mesh | `Spells\Rake.m2` v302; `Undying_Strength_Impact_Chest.m2` v378 | Four/three-weight nonzero-pivot animation, including a scale chain | Mounted exact pose/influence checks and production CPU/GPU contract pass | Mesh-only MSUI trace and synchronized original-client pixels. |
| Pure mesh negative/control | `Spells\ArcaneShot_Missile.m2` | Seven vertices, all single-weight | Mounted control and bind pose pass | Retain beside multi-weight capture to isolate mesh skinning from missile particles/ribbon. |
| Billboard hierarchy | Frost Armor or proven billboard child | Billboard palette rewrite | Prior multi-angle capture fixed one basis bug | Axis-flag matrix coverage and child transform trace. |
| Ground decal | Frost Nova/Arcane Explosion | Animated ground quad + terrain projector | Terrain capture verified | Sloped repeat and WMO-floor fixture. |
| Missile | Fireball, Frostbolt, Arcane Shot, markerless cast, no-visual ranged shot | Release/deadline/root/trail/impact pipeline | 53-check executable law and mounted census; Fireball/Frostbolt user-validated with free-history particles and ribbons | Recorded matched original capture of world-history cloud, ribbon history, and close-range no-flight handoff. |
| DynamicObject area | Blizzard plus Rain of Fire or Consecration | Server area object + distributed one-shots | Blizzard orchestration previously captured | Second spell, radius/distribution/phase traces, original comparison. |
| Texture/alpha | Cone of Cold / `CLOUDS.BLP` | BLP alpha + particle fragment/blend | Known visible mismatch | Decoder pixel dump and controlled texture probe. |
| Fog/postprocess | Additive and alpha effects at near/mid/far range | Shader/fog/glow | Static implementation only | Numeric pixels with glow on/off and recorded framebuffer state. |

## Evidence ledger

| Evidence | Level supported | What it proves | What it does not prove |
|---|---|---|---|
| `tools/spell-frame-law-check` output: `PASS (moving bone, moving root, attachment rotation)` | `STATIC_FOUND` | The exact production helper preserves the three frame invariants numerically. | Live M2 frame wiring, asset activation, final pixels. |
| `tools/spell-particle-motion-check` output: `PASS (100 checks)` | `STATIC/DATA_COMPLETE` | The full 9,717-path mounted census, 2,550 special records, reference fixtures, Blizzard frame/asset facts, pivot-rebased full-TRS round-trip, hitch cadence, and the mandatory two-sided/opposite-winding tail law agree with production. | Recorded matched original-client runtime pixels. |
| `tools/spell-missile-pipeline-check` output: `PASS (53 checks)` | `STATIC/DATA_COMPLETE` | Release-event/finish/strict-backstop timing, actual-launch animation clock, GO-time deadline, raw-dt homing, parsed-axis pose, no-tag attachment fallback, free-missile world-history feed, impact ordering, and the mounted 981-spell/64-path corpus are pinned. | Original-client pixels or the original ray/sphere and missed-projectile deflection approximations. |
| `tools/spell-ribbon-history-check` output: `PASS (52 checks)` | `STATIC/DATA_COMPLETE` | Indexed ribbon geometry, time-zero static tracks, world-committed pair history, parsed authored-axis mapping, pivot equivalence, separate clocks, gravity, cadence, drain animation, InFlight visibility, and the mounted corpus are pinned. | Original-client pixels or the exact CEffect-side owner-destruction wait beyond the supplied Benilla approximation. |
| `tools/spell-mesh-skinning-check` output: `PASS (84,393 checks)` | `STATIC/DATA_COMPLETE` | Four-weight contribution, byte normalization, zero/invalid/duplicate policy, nonzero-pivot bind and animated pose, hierarchy, scale, inverse-transpose normals, billboard child propagation, root/camera boundary, packing/shader source, 9,717 listed/9,654 parsed M2s, and all 555 resolved referenced assets/5,222 bind bones are pinned. | GL execution, a mesh-only live MSUI trace, final lighting pixels, or original-client parity. |
| `dotnet build MSUIClient/MSUIClient.csproj --no-restore` succeeds | `STATIC_FOUND` | The bounded correction integrates with the dirty working tree. | Runtime behavior or visual parity. |
| `SpellParticleSystem.CensusReport` root/emitter/boneOffset/cloudAxis/span fields | Enables future `INSTRUMENTED` evidence | Names root and emitter separately and measures live cloud history. | Nothing until a trace is actually recorded. |
| `SESSION_2026-08-03_SPELL_SLICES.md` captures | `CAPTURED` only for the specifically recorded prior fixes | Billboard basis, terrain decal, DynamicObject placement observations. | The new particle anchor correction or unrelated lanes. |
| `live-runs/spell-special-emitters-20260803.csv` if present | `ASSET_CONFIRMED` | Real geometry/recursion references activate those branches. | Runtime rendering or original parity. |
| `tools/spell-target-radius-check` output: `PASS checks=779` | `STATIC_FOUND` | The production catalog preserves all radius lanes; 218 ground rows resolve exactly to their raw DBC maximum or the named fallback. | That the original client chose the same mixed-lane maximum or fallback presentation size. |

## Shared-impact mismatch list

1. **Ordinary root/bone/cloud identity:** corrected statically as P-046, but live Blizzard and a second asset are
   still required. This is the highest-risk shared transform law.
2. **Missile root/cloud law:** corrected to Benilla's live root-translation anchor with no free-model attach
   rotation. Static/runtime-source checks pass, but the post-correction Fireball/Arcane Shot pixels and the
   independent ribbon-node history still require matched original traces.
3. **Model-space `0x10`, follow `0x4000`, and inherit `0x40`:** now static/data complete with real mounted
   fixtures and executable laws, but still lack isolated runtime traces and matched original-client pixels.
4. **WMO-floor decal projection:** terrain projection is captured; indoor/projectable WMO behavior is not.
5. **Ribbon committed-node frame:** corrected and executable-audited as R-015/R-016. Original-client pixels
   and the precise effect-controller destruction wait remain open; the storage/frame law no longer is.
6. **Geometry and recursive particles:** code and asset census exist, but no lane-isolated runtime captures.
7. **Effect-mesh skinning:** M-017 is static/data complete, including real four-/three-weight assets and the
   normal correction. Mesh-only live/original captures remain; the implementation law is no longer open.
8. **Cone of Cold alpha/texture:** still visibly wrong; decoder and controlled shader probes are needed.
9. **Shader/framebuffer/glow policy:** static code comparison cannot replace numeric pixels under controlled
   backgrounds and explicit sRGB state.

## Behavioral test plan

Implemented numeric checks in `tools/spell-frame-law-check`:

1. stationary root + moving emitter bone preserves old birth positions and grows cloud span;
2. moving root + static emitter offset carries old particles exactly by root delta;
3. host attachment rotation after birth rotates stored particles independently of emitter-bone motion.

Still required:

4. isolated runtime/capture flag-`0x10` test that visibly differs from ordinary anchored storage (numeric law
   and a real live scaled source frame now pass);
5. runtime/capture follow trace for Arcane Shot (response line, saturation, unflagged negative, and hitch law
   now pass numerically);
6. runtime/capture inherit birth-velocity trace for Bloodlust/Abolish Magic (strict cadence, current delta,
   live gate, hold, and uneven-dt laws now pass numerically);
7. committed ribbon-node history, hitch-clock, gravity, visibility, and drain tests now pass against real
   Arcane Shot/Holy Smite/thrown fixtures; matched original pixels remain required;
8. multi-weight mesh + nonzero-pivot inverse-bind, hierarchy, scale, normal, billboard, root/camera, and
   CPU/GPU packing checks now pass against synthetic adversarial cases and mounted Rake/Undying/control assets;
9. mesh-only/particles-only/ribbons-only/glow-off captures that preserve the shared root and clock;
10. Blizzard cloud principal-axis/span regression from at least two times and two camera angles.

The runtime census now emits separate `root`, `emitter`, `boneOffset`, `cloudAxis`, and `span` values. A valid
Blizzard regression must show a stationary area root, a moving emitter offset, and a cloud span that retains
birth history rather than remaining centered compactly on the current bone.

The DynamicObject/type-9 data and ownership path now has a separate executable audit in
`tools/spell-area-visual-check`. It pins the complete build-5875 type-9 census (8 rows), the original
client's seven-entry literal model dispatch, every shipped asset and one-shot span, the 124
`SPELL_EFFECT_PERSISTENT_AREA_AURA` spells (30 distinct visuals), uniform-disc bounds/statistics, live
position/radius updates, and despawn tail drainage. This closes silent selector clamping, first-proc-only,
spawn-once field-staleness, and force-looping of non-loop impact banks. It does not promote O-022 beyond
`REFERENCE_LIMITED`: Benilla has no comparison branch and an original-client capture is still required for
exact random/birth phase and the precise impact-sound trigger instant.

The targeting-radius path has a separate mounted-data audit in `tools/spell-target-radius-check`. It pins the
24-row SpellRadius table, proves every build-5875 per-level term is zero and every cap equals its base value,
checks all 218 location-target spells against the raw three-lane Spell.dbc data, and covers class AoEs,
bombs, utility targeting, and both mixed-radius fixtures. The census contains 182 authored footprints and 36
zero-radius fallbacks, with authored values from 1 to 100 yards. This is data/static evidence, not a claim
that Benilla or an original-client capture has certified the mixed-lane maximum or fallback cursor size.

## M-017 effect-mesh skinning closure — 2026-08-03

### End-to-end frame and matrix trace

1. `M2Reader.ParseVertices` reads the raw 48-byte M2 vertex as position, four byte weights, four byte bone
   indices, normal, and UV. It maps position and normal exactly once from WoW Z-up `(x,y,z)` to MSUI model
   space `(x,z,-y)`. `ParseBones` applies the same point map to pivots/translation, the corresponding
   quaternion basis map to rotation, and an axis-only permutation to scale. Vertex indices remain global
   M2 bone-array indices.
2. `SpellMeshSkinningLaw.Resolve`, called by `SpellEffectMeshRenderer.Resolve`, divides all four weights by
   their authored byte sum. A zero sum binds fully to bone zero, matching the audited Benilla loader. Raw byte
   indices are uploaded unchanged. The shader skips any positive-weight index outside the live palette and
   renormalizes surviving weights; it no longer treats indices 160-255 differently by silently rebinding them
   to bone zero. The referenced effect corpus contains no zero totals, non-255 totals, or invalid live indices.
3. `M2Animator` stores rest local translation as `pivot_i-pivot_parent`, samples live `S*R*T` in
   `System.Numerics` row-vector order, and composes `global_i = local_i*global_parent` once. It returns
   `skin_i = T(-pivot_i)*global_i`. At bind, the rest translations telescope to `global_i=T(pivot_i)`, so
   every returned matrix is identity, including nonzero pivots and descendants.
4. `M2Animator.Pack` uploads three explicit GLSL dot-product rows per bone:
   `(M11,M21,M31,M41)`, `(M12,M22,M32,M42)`, `(M13,M23,M33,M43)`. These are the rows of the column-vector
   equivalent of the CPU row-vector affine matrix. `skinPoint` performs the corresponding three dots, so the
   semantic law is `sum(w_i * (bind * T(-pivot_i) * global_i))`; textual column-vector order is never copied
   from Benilla.
5. The shader blends all four valid matrices and divides by the surviving weight sum. Normals now follow
   Benilla's Bevy 0.18.1 policy: blend the skin matrices first, append `uModel`, exclude translation, and apply
   the inverse-transpose of that combined linear map. The former forward-3x3 weighted normal was a real
   divergence under non-uniform bone/root scale even when position skinning was correct.
6. `ApplyBillboardBones` reconstructs the pre-rewrite global as `T(pivot)*skin`, recovers each child local from
   its original parent, replaces billboard or ignore-parent rotation in model space, recomposes descendants
   in parents-before-children order, and folds the pivot back as `T(-pivot)*rewrittenGlobal`. Therefore a
   vertex partly weighted to a billboard bone and partly elsewhere consumes each rewritten palette entry once.
7. `SpellEffectSource` supplies the effect root independently of the palette. The vertex shader applies
   `uModel` once after skinning. `CameraRelativeModel` subtracts the camera only from that affine root
   translation, algebraically equal to `Transform(pos, worldRoot)-camera`; the translation-free relative view
   cannot subtract it again.

### Mounted build-5875 census and fixtures

- Complete listfile scan: 9,717 listed M2 paths, 9,654 parsed. Influence histogram (models containing the
  class / vertices): one = 9,315 / 2,109,706; two = 741 / 135,947; three = 446 / 24,172; four = 273 / 4,679.
- SpellVisual closure: 599 referenced paths, 555 listed/resolved, 350 with mesh vertices. Influence histogram:
  one = 350 / 37,399; two = 17 / 2,485; three = 4 / 170; four = 1 / 8. All 555 referenced rigs and all 5,222
  bones reduce to identity under production bind evaluation.
- Referenced multi-weight behavior: 16 models and 2,643 vertices touch nonzero pivots; 1,264 vertices lie under
  translation-keyed chains, 2,663 under rotation-keyed chains, and 264 under scale-keyed chains. Exactly zero
  referenced multi-weight vertices touch billboard or ignore-parent-rotation bones, so that branch retains a
  synthetic adversarial fixture rather than a fabricated real asset claim.
- Full-corpus negative census: zero zero-total vertices and zero non-255 totals; 2,840 vertices contain
  duplicate live indices. Three non-referenced models exceed/escape the 160-bone palette and account for 999
  invalid vertices / 1,061 invalid influences: `UI_Tauren.m2`, `Taerar.m2`, and
  `transportship_sails.m2`. No referenced effect asset exceeds 160 bones or has an invalid live index.
- `Spells\Rake.m2` is the only referenced four-influence asset. Sequence 0 at age 0.555 s, vertex 302 has
  weights `64/64/64/63` on bones `4/14/5/16`; its pinned posed point is
  `(1.0220361,1.3673285,-0.12145541)`, and removing even its weakest influence changes the result by
  `0.04776045` model units.
- `Spells\Undying_Strength_Impact_Chest.m2` supplies nonzero pivots and live translation/rotation/scale chains.
  Sequence 0 at age 0.61679006 s, vertex 378 has three equal weights on bones `5/9/15`; its pinned point is
  `(-0.17665625,1.1976844,-0.052858792)`. `Spells\ArcaneShot_Missile.m2` is the real negative control: seven
  mesh vertices, all single-weight.

### Evidence boundary and residuals

M-017 is `STATIC/DATA_COMPLETE`: production wiring, adversarial formula checks, real shipped fixtures, and the
complete relevant mounted corpus pass. It is not `CAPTURED` or `PARITY_CERTIFIED`; the validator does not
execute an OpenGL context and no synchronized original-client mesh-only capture exists.

- Implementation gaps after M-017: WMO-floor decal triangle participation; Cone of Cold `CLOUDS.BLP` alpha/
  blend diagnosis; controlled shader/framebuffer/glow probes; any head/tail particle or future mesh-normal
  divergence exposed by runtime capture.
- MSUI runtime captures still required: Rake and Undying mesh-only posed traces; billboard/child flag matrix;
  corrected ordinary/special particle, missile, ribbon, geometry, and recursion fixtures.
- Original-client-only verification: matched mesh form/scale/lighting pixels, DynamicObject exact random phase/
  sound instant, target-marker mixed/zero-radius presentation, missile interception/deflection, exact ribbon
  owner-destruction wait, and numeric blend/fog/glow output.

The next unresolved implementation item is **WMO-floor decal projection**. This is selected ahead of capture-only
work because D-001 remains a known production-path gap, while M-017's remaining burden is runtime/original
evidence rather than an untraced implementation law.

## Reference limitations

- Benilla does not implement the DynamicObject/type-9 Blizzard area-spawn orchestration assumed by older
  handoffs. VMaNGOS state, original assets, and original captures control that lane.
- Benilla's current particle code is unusually well documented and separates `owner`, `placement`, `attach`,
  and `anchor`, but agreement with it still cannot certify original pixels.
- No original capture currently provides numeric frame values; transform claims require MSUI instrumentation
  plus asset data, while pixel certification still requires comparable original imagery.
- A prior Fireball picture cannot decide every missile/root/history law after the semantic schema changed.
- Blizzard has no geometry-model or recursion emitter and zero ribbons, so it cannot certify those lanes.
- Terrain decal captures do not certify WMO floors.
- Benilla uses SpellRadius for tooltip substitution only; it cannot certify the targeting-decal radius rule.
- No matched original-client capture currently decides mixed-radius selection or zero-radius cursor sizing.
- One camera cannot certify billboard rules; one frozen frame cannot certify history storage.

## Implementation sequence

1. Record the post-correction Blizzard trace using the new root/emitter/cloud fields; capture at two descent
   times and compare the world-space principal axis/span to the supplied 1.12 reference.
2. Pin and run one second animated-bone ordinary emitter plus a moving-root/static-bone kit.
3. Capture the now-pinned `0x10`, `0x4000`, and `0x40` fixtures; record the already user-validated Blizzard
   head-tail behavior from multiple camera angles.
4. Capture the corrected Fireball/Arcane Shot missile pipeline, including free-missile world-history particles,
   independently committed ribbon history, markerless launch, and close-range no-flight impact.
5. Capture the corrected ribbon committed-node/width/drain behavior; M-017 multi-weight mesh/pivot
   composition is now static/data complete and awaits its mesh-only capture.
6. Capture geometry and recursive particle fixtures independently.
7. **Next implementation slice:** add projectable WMO surfaces to the decal evidence chain and pin the
   no-surface behavior.
8. Resolve Cone of Cold with decoder pixels, then run controlled blend/fog/glow pixel probes.
9. Only then run orchestration composition/mute captures and update individual evidence levels.

## Final claim statement

User-observed in MSUI: Fireball/Frostbolt trails and Blizzard angled shards/tails. No lane is newly
`PARITY_CERTIFIED` without recorded matched original-client output.
Statically implemented and numerically regression-checked: ordinary particle root-cloud anchoring, one-time
emitter-bone birth composition at the corrected storage boundary, moving-root carry, independent host
attachment rotation, full live model-space joint/root TRS, clamped-step follow response, strict 30 Hz
inherited motion, the complete mounted-data DynamicObject/type-9 selector/rate/ownership path, and the
complete mounted-data ground-target radius path, the Benilla missile release/deadline/homing/pose/
free-missile world-history/impact pipeline, two-sided particle raster state, ribbon committed-world history
with separate raw/clamped clocks, and M-017
four-weight/nonzero-pivot effect-mesh skinning with inverse-transpose normals. The area lane remains original-capture-limited for its exact
random sequence and birth phase; target markers remain original-capture-limited for mixed/zero-radius visual
selection.  
Still unknown: recorded matched post-correction Blizzard pixels, second ordinary-history asset generality, runtime/pixel
certification of the special-motion, corrected missile, and corrected ribbon fixtures,
geometry/recursion and multi-weight mesh captures, WMO decals, and numeric shader/glow
parity. No row in this document is `PARITY_CERTIFIED`.

## Animation and lifecycle correction — 2026-08-03

The animation/lifecycle lane now has a separate mounted-data and executable audit in
`tools/spell-animation-lifecycle-check`. It traces the Benilla effect-model and missile paths through M2
sequence parsing, rig evaluation, material tracks, particle gates, ribbons, owner reap, tail drainage, and
M2 sound events.

The corrected shared law is:

- ordinary effect models select file-order sequence slot 0;
- missiles select AnimationData 144 (`InFlight`) when present and otherwise select slot 0;
- M2 flag bit 0 set means clamp after one pass; bit 0 clear means loop;
- one resolved sequence slot drives rig, material, particle, and ribbon sampling;
- the instance clock is monotonic; selected tracks wrap/clamp themselves, while global sequences retain an
  independent clock and are never reset by an ordinary clip wrap;
- self-terminating kits live for exactly one pass of sequence 0 (1.0 second only when no usable sequence
  exists); model/ribbon/particle tails drain after owner reap;
- any new cast start releases the prior cast/channel hold even if the new spell has no visual, while aura
  state remains a separate owner;
- `$SND`, `$DSL`, and `$DSO` markers fire when their selected-sequence timestamps are crossed.

Mounted build-5875 results: 599 referenced effect paths, 555 resolved assets, 44 known missing/stale paths,
157 multi-sequence models, 339 models with global sequences, and 42 missile models that require the
first-sequence fallback. The previous AnimationData-id heuristic disagreed with the authored M2 loop flag on
409 of the 555 resolved referenced models. The validator currently passes 5,810 checks, including 579 exact
runtime-selected rig sequences, ten referenced sound-marker models, and canonical HumanMale Stand/Walk/Run/
Death loop probes guarding the shared animator.

This is `STATIC/DATA_COMPLETE` for the bounded laws above, not pixel parity. A synchronized original-client
capture is still required to certify visible timing, persistent clamp presentation, and sound mix/position.
