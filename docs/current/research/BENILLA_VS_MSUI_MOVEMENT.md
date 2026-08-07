# Benilla vs MSUI — player character movement, facing and animation

Scope: the local (controlled) player only. Compared:

> **Implementation update, 2026-07-29.** The original comparison found a complete
> outbound-wire gap. MSUI now claims the active mover and sends ground locomotion
> axis transitions, turn transitions, jump/fall-land, changed facing at frame
> cadence, and 500 ms moving heartbeats from `Net/LocalMovementSender.cs`.
> Server speed changes/acks, swim and transport state remain open; the animation
> and physics comparisons below are otherwise unchanged.

| Concern | MSUI | Benilla |
|---|---|---|
| Physics | `Player/CharacterController.cs` | `player/mover.rs`, `player/state.rs` |
| Input → intent | `Program.cs` ~1190-1225, `Engine/ClientWindow.cs` | `player.rs` `control` |
| Facing | `Engine/Camera.cs` | `player/camera.rs`, `player/gait.rs` |
| Anim select | `World/Units/CharacterRenderer.cs` `ChooseClip` | `creature_anim/select.rs` `gait_candidates` |
| Anim playback | `World/Units/M2Animator.cs` | `creature_anim/driver.rs` |
| Wire | `Program.Net.cs` | `player/movement_net.rs` |

The physics layers are close cousins. **The difference is almost entirely above the physics** — in
how the character's *heading* is produced and how the animation layer is *driven*. That is exactly
the layer that produces "feel", which is why it can't be pinned to a number.

---

## The headline: five structural differences

Ranked by how much each one contributes to "this is wrong but I can't say why".

### 1. MSUI picks the animation from *measured displacement*; benilla picks it from *intent*

`CharacterRenderer.MeasureMotion` (CharacterRenderer.cs:1678-1714) never sees the input. It
differences the position and low-passes the result:

```csharp
var delta = state.Position - _lastPosition;
float raw = flat.Length() / dt;
_instantGroundSpeed = raw;
float blend = 1f - MathF.Exp(-dt * 12f);          // ~83 ms time constant
if (raw < MoveThreshold) _groundSpeed = raw;      // stops instant
else _groundSpeed += (raw - _groundSpeed) * blend;
_forwardness += (Vector3.Dot(direction, facing) - _forwardness) * blend;
_sideness    += (Vector3.Dot(direction, right)  - _sideness)    * blend;
```

Benilla feeds the animation layer the *commanded* state directly (player.rs:1147-1159):
`motion.speed = player.horiz_vel.length()`, `motion.flags = anim_flags`, where the flags are the
netted input axes.

Four consequences, all of which read as "off":

- **~83 ms of animation lag on every start.** Press W and the legs take five frames at 60fps to
  reach run speed. Release and the stop is instant (deliberate asymmetry at :1701) — so starts feel
  mushy and stops feel abrupt. In benilla both edges are instantaneous because `horiz_vel` is set
  outright (mover.rs:142).
- **Collision changes your animation.** Slide along a wall while holding W and your displacement
  direction rotates — `_sideness` climbs, and past 110° (`backwards = 1.92f`, :1769) you play
  WalkBackwards while pressing forward. Step up a stair and the frame's horizontal displacement
  drops, so the leg rate dips. Benilla is immune: intent doesn't care what the wall did.
- **Turn-in-place is invisible to the animator.** Turning produces no displacement, so
  `_groundSpeed < 0.3` and `ChooseClip` returns Stand (:1747). The character pivots rigidly in the
  idle pose. Benilla synthesises TURN_LEFT/TURN_RIGHT from the *body's actual rotation step*
  (gait.rs:67-75) and plays ShuffleLeft/ShuffleRight (11/12).
- **The playback rate jitters.** `rate` uses `_instantGroundSpeed` — raw `Δpos/dt`, unsmoothed
  (:1832). Any frame-time jitter, any slide, any ground snap modulates the leg cycle speed directly.

### 2. MSUI has no animation blending at all — hard cut plus a phase reset to zero

The entire transition logic (CharacterRenderer.cs:1633-1637):

```csharp
if (!ReferenceEquals(next, _clip)) { _clip = next; _clipTime = 0f; }
```

`M2Animator.Evaluate` takes one `Clip?` (M2Animator.cs:578) — there is no slot for a second pose,
so a cross-fade is not currently expressible.

Benilla cross-fades over the incoming clip's own `blend_time`
(`tr.play(&mut player, c.node, Duration::from_secs_f32(c.blend_time))`, driver.rs:358) and, just as
importantly, **preserves phase where WoW does**:

- landing from a bracket-less step-off fall does *not* re-pick the gait (driver.rs:893-899,
  decision 0187) — a re-pick "replays the run cycle from its head: the landing-frame leg pop";
- an unchanged gait is never re-armed, only re-rated (driver.rs:1009-1018).

In MSUI every Stand↔Run, Run↔WalkBackwards, Jump→Run boundary snaps the leg cycle to frame 0. That
is a visible pop on every direction change and every landing. **If you fix one thing, fix this.**

### 3. There is no body-heading pipeline — the model is welded to the camera

MSUI: `Program.cs:1218` passes `Yaw = _window.Camera.Yaw` → `CharacterController.cs:276`
`Yaw = Normalize(input.Yaw)` → `BuildTransform` renders at `state.Yaw + 90° + _moveYaw`. One angle,
zero lag.

Benilla carries **two**: `face_yaw` (the aim, what goes on the wire) and `model_yaw` (the rendered
body), reconciled every frame by `gait.rs:drive_body_heading` per the client's `0x607ed0` tail:

| State | Benilla body behaviour |
|---|---|
| Strafing | body turns to `face_yaw ± 90°` (pure) / `± 45°` (diagonal, mirrored on backpedal), eased in *aim-relative offset space* at `STRAFE_BLEND_RATE = 17.26 /s`, with a SpineLow/Head counter-twist so the head keeps looking at the aim |
| Moving fwd/back, airborne | snap to aim |
| Standing, steering | **frozen chase** — body holds, 90° ceiling only, so the camera and head lead and the body trails exactly 90° |
| Standing, released | sweeps onto the aim at `8 × TURN_RATE` ≈ 63 ms for 90° |
| Swimming | snap, plus a separate pitch quaternion |

MSUI's nearest equivalent is `_moveYaw`, a geometric twist derived from the *smoothed displacement
dot products*, clamped to 100° (`MaxTwistDegrees`, :337), eased at `1-exp(-dt*14)` ≈ 71 ms, with the
torso counter-rotated to `0.66 × _moveYaw` (`TorsoFollow`, :324). Different source (displacement, not
key flags), different magnitude (100° cap, not the exact ±90/±45 ladder), different ease law, and
crucially **no standing frozen chase and no turn shuffle at all**.

The frozen chase is the single most recognisable "this is WoW" tell in stationary play. Its absence
is likely a large share of what you're feeling.

### 4. Speeds are hardcoded; the server's speed opcodes are not handled

MSUI reads `_config.Movement.RunSpeed` (7.0) live, every frame, forever
(CharacterController.cs:292-294). A grep for `SMSG_FORCE_*_SPEED_CHANGE` / `MSG_MOVE_SET_*_SPEED`
across the tree returns **zero matches**. A mount, a speed buff, a daze, a GM `.speed` — none of it
reaches the controller, and there is no ack loop.

Benilla takes run/runBack/swim/swimBack from the server's `UnitSpeeds` component every frame
(player.rs:702-712), falls back to the config only pre-create, and acks each
`SMSG_FORCE_*_SPEED_CHANGE` with the live pose (movement_net.rs:150-164).

Two knock-on effects beyond the obvious:

- Benilla's **gait threshold is server-relative**: run if `speed > 2.0 × walk_speed`, sprint if
  `speed >= FAST_RUN_SPEED (11.0)` (select.rs:377-386). MSUI has no speed-derived gait at all — run
  vs walk is purely the Shift key, and Sprint/143 is not baked.
- Benilla's backpedal speed is `min(runBack, run)` and the animation rate divides by it, so the
  backpedal clip never drags. MSUI has `BackwardSpeed = 4.5` (correct) but selects it on
  `input.Forward < -0.01f` only, so S+strafe applies 4.5 to the whole normalized diagonal.

### 5. Turn rate is wrong, and doesn't drop while moving

| | MSUI | Benilla / vanilla |
|---|---|---|
| Standing turn | `_turnSpeed = 2.8f` rad/s ≈ 160°/s (Program.cs:235) | `TURN_RATE = π` rad/s = 180°/s (state.rs:25) |
| While translating | same 2.8 | `× TURN_RATE_MOVING (0.75)` = 135°/s (state.rs:33, player.rs:549-564) |

~11% slow standing, ~19% fast while running. Small numbers, but turn rate is one of the things the
hand calibrates to in minutes.

---

## Missing states

| State | MSUI | Benilla |
|---|---|---|
| Swim | **absent** — 41/42 not in `BakedAnimations` (:63-64), no selection branch | full ladder: SwimIdle/Swim/SwimLeft/Right/Backwards (select.rs:340-370), own speed pair, swim pitch, breach jump |
| Landing (JumpEnd 39) | baked and marked one-shot, but **appears in no `FindFirst` chain** — unreachable | `jump_land_pick` (select.rs:284-295): JumpEnd 39 standing, JumpLandRun 187 moving, nothing on backpedal/walk |
| JumpStart → Jump handoff | `VerticalVelocity > 0.5f` → clip 38 directly | 37 armed at MSG_MOVE_JUMP, 38 only after 37's own 833 ms window (select.rs:210-220) |
| FALLINGFAR / Fall 40 | `FallTimeMs >= 180ms` debounce → 40 | latched by 1/9 yd of descent (jump) or 0.5 s (step-off), state.rs:132/138 |
| Sprint 143 | not baked | `speed >= 11.0` |
| Sit/sleep/kneel/chairs | none | full bracketed pose machine with move-interrupt |
| Turn-in-place shuffle | none | 11/12 from body step |
| Wound flinch, overlays | none | masked SpineLow overlay at 8:1 weight, smoothstep-decayed wound blend |

---

## Physics: closer than you'd think, but three real gaps

| Knob | MSUI | Benilla | Note |
|---|---|---|---|
| Gravity | 19.2911 | 19.291105 | same |
| Jump velocity | 7.9558 | 7.955547 | same |
| Terminal velocity | 60.148 | 60.148003 | same |
| Run speed | 7.0 | server, fallback 7.0 | see §4 |
| **Max slope** | **55°** (MaxSlopeDegrees, cfg) | **50°** (`GROUND_COS = cos 50°`) | benilla's is the client's own election constant |
| **Step height** | **1.0** | **0.7** (`STEP_UP_HEIGHT`) | MSUI climbs things it shouldn't |
| Capsule | r 0.4, h 2.1 | r 1/3, h 2.0278 | benilla's h = the unit collision height from `[unit+0xb8]` |
| Ground probe | terrain height grid + downward raycast at `StepHeight` | capsule cast, `GROUND_PROBE 0.2` walking / `LAND_PROBE 0.05` airborne | |
| Step-down snap | `GroundSnapDistance = 0.5` flat | `travel × STEP_SLOPE_RATIO (1.8494) + STEP_SNAP_SLACK (1/36) + CAPSULE_HEIGHT` | benilla's reach scales with this frame's travel, per the client's step-vs-fall election |
| Horizontal sweep | **single raycast from mid-body** (acknowledged limitation, :53-57) | capsule `move_and_slide` | MSUI clips outside corners at speed |
| Slide | project + `× 0.98`, 2 iterations | avian `MoveAndSlide` + `walkable_ride_velocity` / `steep_wall_plane` | |
| dt | variable, clamped to 0.05 twice | variable, unclamped | |
| Accel ramp | none | none | matches |

The `StepHeight = 1.0` / slope `55°` pair is worth a look on its own: a 1-yard free step-up plus a
55° walkable gate lets you walk up terrain and over obstacles that vanilla makes you jump. That is a
"feel" difference you'd notice in the world without being able to name it.

Two behaviours benilla has that MSUI has no analogue for, both of which change how slopes feel:

- **`walkable_ride_velocity`** (mover.rs:409-417) — a walkable slope never slows or deflects the
  walk. Collide-and-slide's true-plane clip would shorten horizontal speed to `h·cos²θ` (half speed
  at 45°) and bend a diagonal approach off the input line. MSUI dodges this differently: it moves
  horizontally by `speed·dt` and then snaps Z, which gets the same 2D-speed result on terrain — but
  *not* on WMO/doodad collision surfaces, where `MoveHorizontal` does clip.
- **`steep_wall_plane`** (mover.rs:434-444) — a steep face never *lifts* you. MSUI's `MoveHorizontal`
  passes any surface with `Normal.Z > _minGroundZ` straight through to the ground resolver
  (:380-384), and a 50-55° face qualifies, so you can walk up banks benilla makes you slide off.

---

## One outright bug worth fixing regardless

`Program.Net.cs:122` sets `_controller.Yaw = enter.Orientation` on
SMSG_LOGIN_VERIFY_WORLD / SMSG_NEW_WORLD, but never touches `_window.Camera.Yaw`. Since
`CharacterController.Update` overwrites `Yaw` from `input.Yaw` on the very next frame
(CharacterController.cs:276), that assignment is discarded — you always face whatever the camera yaw
happened to be (0, from `Start.Orientation`). Same for the ctor initializer at Program.cs:471.

---

## Suggested order of attack

1. **Cross-fade + phase preservation** in `CharacterRenderer`/`M2Animator`. Needs a two-clip mixer;
   biggest single visual win. Preserve phase on landing specifically.
2. **Drive the animation from intent, not displacement.** Pass the `MovementInput` / move flags and
   `horiz_vel` into `CharacterRenderer.Update` and delete `MeasureMotion`'s smoothing path. Removes
   the 83 ms lag, the wall-slide misfires, and the rate jitter in one change.
3. **Body heading.** Split `state.Yaw` into aim vs rendered body; port `drive_body_heading`'s four
   cases. Turn-in-place shuffle falls out of it for free.
4. **Turn rate** → π rad/s, × 0.75 while translating.
5. **Landing animation** — wire 39/187 into `ChooseClip`.
6. **Server speed opcodes** — parse the FORCE_*_SPEED_CHANGE family, ack them, feed the controller.
7. Step height 1.0 → 0.7, slope 55° → 50°.
8. Swim.
9. Capsule sweep to replace the single horizontal ray.

## 2026-07-30 09:00 spell-animation correction

The reported “movement freezes after casting” case had a narrower, presentation-level cause than
the broader movement work above. A requested spell animation could be absent from the animator's
already-baked clips. The exact spell-action path then substituted Stand, so locomotion input could
continue while the model appeared stalled.

`M2Animator.FindOrBake` now obtains the requested clip on demand. Exact player and creature spell
paths no longer treat Stand as an acceptable missing-spell fallback, and control returns to the
movement-driven base state after the one-shot. This follows Benilla's exact spell action/return law
in `crates/benilla/src/creature_anim/spell_visual.rs` (approximately lines 420–670).

This does not close the structural movement items above. Live sign-off must specifically cast while
stationary, begin moving during/after recovery, and confirm that the correct walk/run clip resumes
without a Stand latch.

## 2026-07-31 measured reconciliation addendum

This addendum supersedes the historical “Suggested order of attack” status,
without rewriting the source-reading record that produced it. SPEC-13's fixed
60 Hz scripts and committed traces establish the current tree as follows:

| Original item | Measured status | Proving trace/audit evidence |
|---:|---|---|
| 1. Cross-fade + phase preservation | **Unresolved by the original instrument; legacy observation classifies every below-window transition as a hard cut.** | S2 `hardCuts`: 18 across all eight scripts (12 gait/landing/turn transitions plus 6 internal Jump/Fall transitions). S3 adds clipB/weight observability before accepting this item. |
| 2. Intent-driven animation | **Implemented.** | `run-start-stop`: start displacement 1 tick, clip latency 0 ms, stalls 0; all scripts substitutions 0. |
| 3. Body heading | **Implemented.** | `strafe-pure` trace bodyYaw-aimYaw is exactly +/-pi/2; standing turn plays shuffle while aim/body are split. |
| 4. Turn rate | **Implemented.** | `turn-standing=3.141604 rad/s`; `turn-moving=2.356214 rad/s` (pi and 0.75pi within trace precision). |
| 5. Landing animation | **Implemented.** | jump-standing selects JumpEnd 39; jump-flat selects JumpLandRun 187. |
| 6. Server speed opcodes | **Untested.** | Offline scripts prove only local 7.0/4.5 speeds; no FORCE_* change/ACK scenario exists. |
| 7. Step/slope constants | **Untested.** | Flat `movement-arena` has no threshold stair or slope course. |
| 8. Swim | **Untested.** | No water vantage or swim ladder script exists. |
| 9. Capsule sweep | **Untested.** | No wall-glance/corner collision course exists. |

Separate jump-bracket status: landing selection is present, but liftoff enters
Jump 38 directly. The authored JumpStart 37 -> Jump 38 handoff is absent and is
tracked as F2 under SPEC-14.

The old `phaseResets` audit counted only a clock wrap while a clip name stayed
unchanged and therefore could not observe a transition cut. It is superseded by
`hardCuts`: on legacy traces, every clip-name transition entering below the
blend window without observable clipB state counts; on S3-format traces, a
transition is a hard cut only when no outgoing clip/positive blend weight is
present. One-shots may legitimately start their incoming clock at zero while
still blending from the outgoing pose.

## 2026-08-06 live character-turn correction

This live-observation addendum supersedes two presentation assumptions without rewriting the
historical Benilla source-reading record above.

- HumanMale `ShuffleLeft`/`ShuffleRight` each key only 17 bones. Sampling them as complete poses
  restored absent shoulder/hand channels to bind pose and widened the measured hand span from
  roughly `0.705` (Stand) to `0.925`. `M2Animator.TurnBasePose`, supplied only by
  `CharacterRenderer`, now layers those absent channels over the continuously sampled Stand pose.
  Both turn directions stay within `0.01` of Stand hand span across eleven sampled phases while the
  keyed foot shuffle remains authored.
- Model load now selects Stand before the first render, removing the same arms-out bind-pose flash
  at world entry.
- The `8 x TURN_RATE` release chase described in the table above was visibly a four-frame snap in
  MSUI and did not match Nico's 1.12 observation. Only the **released** stationary catch-up is now
  capped at `0.8 x pi rad/s`: a full 90-degree lag closes in about 625 ms and is carried by one or
  two shuffle cycles. While the turn key remains held, the existing 90-degree ceiling behavior is
  unchanged. `Turn: release catch-up rate` is live-tunable in the Character panel.
- Stationary frozen chase remains a whole-body heading lag. The torso counter-yaw is limited to
  moving split-strafe (plus the explicit diagnostic), which prevents this correction from becoming
  another shoulder-pose path.

The bounded regression evidence is `spell-animation-lifecycle-check` at 5,810 checks and
`spell-frame-law-check`'s held-ceiling/released-rate assertions. Nico visually approved the final
arms and slower-release behavior on 2026-08-06.
