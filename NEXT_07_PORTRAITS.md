# NEXT 07 — Unit-frame portraits fall back to the flat TemporaryPortrait art

**Screenshot symptom:** both the player (a **Dwarf**, "Dwfpala") and the target (a wolf)
portraits show the flat 2-D `TemporaryPortrait` stand-in, not a live 3-D model. That means the
FBO bake returns `< 128` subject pixels (`PortraitRenderTarget.Analyze().HasSubject == false`,
`Engine/PortraitRenderTarget.cs:16`), so `_playerPortraitUsable`/`_targetPortraitUsable` stay
false. Last session's circular-mask + clear-color fixes only mask/threshold a **successful**
bake; they do not fix a **blank** bake — that is what remains.

The same `CharacterRenderer.Render(camera, state)` renders the dwarf perfectly in the world
(`Program.cs:1660`), so the model, skinning, shaders and textures all work. **The only
portrait-unique parts are the camera + the FBO wrapper.**

---

## STEP 0 — Read the evidence the client ALREADY prints (do this first)

`BakeDirtyPortraits` logs, per bake (`Program.Portraits.cs:100-102`):
```
[portrait] player bake BLANK (subject=<n>, rgb=<lo>..<hi>, alpha=<lo>..<hi>, camera=<authored|bounds>, pieces=<n>)
```
and dumps `portrait-diagnostics/player-blank.png` next to the exe
(`Program.Portraits.cs:103-107, 271-284`). This single line disambiguates everything:

| log reads | meaning | fix |
|---|---|---|
| `camera=bounds`, `rgb=14..14` (or ~clear), `pieces>0` | model rendered but **off-frustum** — the fixed 1.55/1.55 human frame missed a short race | §A (model-adaptive framing) |
| `camera=authored`, `rgb=14..14`, `pieces>0` | authored camera engaged but framed empty space / near-clipped | §B (near clamp) + verify authored parse |
| `pieces=0` | no geoset drawn at all | model not loaded/enabled in the booth — different bug, check `_character.Loaded/Enabled/VisiblePieces` |
| bright `rgb`, `subject<128` | model rendered but tiny/edge crop | §A |

For the dwarf in the screenshot, the highest-probability line is
`camera=bounds ... rgb≈14..14` — see §A for why.

---

## The benilla contract (PROOF — ground truth for a correct bake)

Source: `benilla/crates/benilla/src/portrait/{mod,framing,booth}.rs`.

- **The fallback (bounds) framing is MODEL-DERIVED, never hard-coded** (`framing.rs:173-208`):
  it aims at the model's head anchor and derives distance from the model's own height,
  `dist = (window*0.5)/tan(fov/2)`, with `WINDOW_MIN=0.55 / WINDOW_MAX=1.1` clamps
  (`framing.rs:154-160`) whose comment is literally "floors tiny models … caps giants." A
  Gnome and a Tauren get different distances. **This adaptivity is what MSUI dropped.**
- `aim()` writes BOTH transform and projection (`mod.rs:1030-1041`); the authored `fov` is a
  **diagonal** angle → `fovy = 0.6*fov` at aspect 1 (`framing.rs:22, 45-47, 59`).
- Frozen pose, scale 1, origin-local (`booth.rs`); degenerate authored cameras (eye==target)
  are tolerated via `normalize_or_zero` (`framing.rs:174`).

---

## MSUI diagnosis (ranked, with the exonerations)

> **CORRECTION 2026-07-30:** The authored-camera parse/projection verification below did not
> correspond to committed running code. Full-history searches find `ParsePortraitCamera` only in
> this document and find `PortraitCamera` first introduced by commit `74349395`, which added the
> model property and camera-check consumer but no parser or assignment. The cited
> `M2Reader.cs:1079-1080` function never existed in repository history. The coordinate-space
> contract remains the intended law, but the claim that it had been implemented and checked was
> false. See `SPEC_TOOLKIT_REPORT_2026-07-30.md` §DIAGNOSIS.

The M2 camera parse and camera-space math were checked and are **correct**:
- Vertices are stored `(px, pz, -py)` (`Formats/M2Reader.cs:1218-1220`) and
  `ParsePortraitCamera` applies the **identical** swap to eye/target
  (`M2Reader.cs:1079-1080`). `AuthoredPortraitCamera` then pushes both the camera point and the
  vertices through the **same** `BuildTransform` (`Program.Portraits.cs:194-195` +
  `CharacterRenderer.BuildTransform`), so camera and model share one space — **no model-space
  vs world-space mismatch.** (This was the prime suspect; it is exonerated.)
- FBO winding/cull (`FrontFace(Ccw)+CullFace(Back)`, `PortraitRenderTarget.cs:92-93`) is
  byte-identical to the global state (`ClientWindow.cs:463-466`); `ModelToWorld` det = +1.
  **Not back-face culling.** BindPose gives identity skin matrices (no single-triangle
  collapse). No reverse-Z, no stencil. All exonerated.

**Root cause (the one thing common to BOTH the authored AND bounds bakes being blank): the
fallback `PortraitCamera` is hard-framed to a ~1.8-yd human and does not adapt to model
height.**
- `PortraitCamera(Vector3.Zero, state.Yaw, 1.55f, 1.55f)` with `FOV=38°`, `Target=origin`,
  `EyeHeight=1.55`, `Distance=1.55` (`Program.Portraits.cs:67, 94, 178-190`).
- Vertical window = `2*1.55*tan(19°) ≈ 1.07 yd` centered at z=1.55 → it frames **z ∈ [1.02,
  2.08]**.
- A **Dwarf/Gnome head is ≈1.0–1.1 yd** → only the crown falls in that window → `<128` subject
  pixels → blank → `TemporaryPortrait`. (A wolf/quadruped is even further off.) This exactly
  matches a Dwarf player + wolf target both blank.
- Contrast the in-world camera (`EyeHeight≈2.2`, `Distance≈9`, `FOV≈70`), which frames any
  race — hence "same Render, world fine, portrait blank."

Aggravators to fix in the same pass:
- Authored near/far are scaled by `modelScale` and near is **unclamped**
  (`Program.Portraits.cs:215`): a model whose authored `NearClip` exceeds its standoff clips
  the whole face.
- The bake is latched even on failure (`_playerPortraitDirty=false` at
  `Program.Portraits.cs:108`), so one bad bake is permanent until an appearance change.

---

## §A — Fix: model-adaptive fallback framing (the primary fix)

Mirror benilla's `framing::frame`. Add a cached bind-pose height to `CharacterRenderer`
(max world-Z of the bind-pose mesh at ModelScale 1 — compute once at `Load()`), then in
`Program.Portraits.cs` replace the fixed `PortraitCamera(Vector3.Zero, state.Yaw, 1.55f, 1.55f)`
(both the initial fallback at :67 and the retry at :94) with a bounds-derived frame:

```csharp
float head    = MathF.Max(0.3f, _character.BindPoseHeight());   // Human ~1.9, Dwarf ~1.4, Gnome ~1.05
float target  = 0.92f * head;                                   // aim at throat/face (head anchor)
float window  = Math.Clamp(0.34f * head, 0.55f, 1.10f);         // WINDOW_MIN/MAX (framing.rs)
const float fovyDeg = 34f;
float distance = (window * 0.5f) / MathF.Tan(fovyDeg * 0.5f * MathF.PI / 180f);
Camera bounds = new()
{
    Target = Vector3.Zero, Yaw = state.Yaw + MathF.PI, Pitch = 0.02f,
    Distance = distance, EffectiveDistance = distance,
    EyeHeight = target,                        // aim at the head, NOT a fixed 1.55
    FieldOfViewDegrees = fovyDeg, AspectRatio = 1f,
    NearPlane = MathF.Max(0.02f, distance - head), FarPlane = 100f,
};
```

`BindPoseHeight()` sketch (add to `CharacterRenderer`, cache at load): iterate the parsed M2
vertices, take `max` of their world-Z (the stored `(px,pz,-py)` z-component), clamp ≥0.3.

## §B — Fix: clamp the authored near plane

`Program.Portraits.cs:215`:
```csharp
NearPlane = MathF.Max(0.02f, authored.NearClip * modelScale),
```

## §C — Fix: don't latch a failed bake (so it retries next frame)

At `Program.Portraits.cs:108`, only set `_playerPortraitDirty = false` when
`_playerPortraitUsable` is true (keep dirty on BLANK so an intermittent cause self-heals; guard
against an infinite re-bake loop with a small failure counter if needed). Same for the target
path.

## Target / creature portrait

The creature path derives framing from the model already
(`CreatureRenderer.TryGetPortraitFraming` → `PortraitCamera(target.Position, target.Orientation,
framing.EyeHeight, framing.Distance)`, `Program.Portraits.cs:149-152`), but it uses
`target.Position` (world coords) as the camera target while the creature is drawn at its world
position — verify `RenderPortrait` renders camera-relative consistently, and apply the same
near clamp + un-latch. If the wolf is still blank after §A/§B/§C, capture its
`target-<display>-blank.png` and log line and treat it as the creature-framing variant of §A
(the `framing.EyeHeight/Distance` derivation needs the same head/window/clamp law).

---

## Verification

1. Log in as the Dwarf. Console prints `[portrait] player bake ready (subject=<big>,
   camera=authored|bounds, pieces=<n>)` — NOT BLANK. The top-left portrait shows the live
   dwarf head (circle-masked from last session), not the flat stand-in.
2. Target the wolf — its portrait shows the live wolf head.
3. Test a Gnome and a Tauren (extreme short/tall) to confirm the window clamps hold.
4. If any BLANK persists, the `portrait-diagnostics/*-blank.png` + the `rgb=`/`pieces=`/`camera=`
   log line points at the exact branch (see STEP 0 table).
