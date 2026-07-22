# MSUI Client — Project Handbook

**A native C# client for World of Warcraft 1.12.1 (build 5875), talking to a private VMaNGOS server.**

Version: Draft 4 — 2026-07-22
Supersedes: Draft 3 (same day; superseded by characters, animation, gear and the DBC layer)
Supersedes: `MSUI_CLIENT_DESIGN.md` (browser-era architecture, abandoned — see §2)

---

> **If you are a fresh assistant picking this project up cold, read §0, §1, §3, §4 and §10 before touching anything.**
> §3 lists facts established empirically that cost real hours. Several are counter-intuitive and several were got wrong once — sometimes twice — before being got right. Re-deriving them will waste a session.
> §10 lists exactly what to ask Nico for, and why.

---

## 0. Orientation — what this is in one page

Nico runs a private VMaNGOS server (WoW 1.12.1 vanilla) plus **MangosSuperUI**, an ASP.NET Core admin and content-creation web app he built for it. This project is a **separate, standalone game client** — a real playable client, not a viewer — written in C# on Silk.NET/OpenGL.

**Why it exists.** Two goals converged:

1. A long-standing wish to see WoW 1.12 rendered in a painterly, hand-painted art style. Owning the renderer makes that a shader variant instead of a fight with a platform.
2. A working multiplayer client for his own realm — quest, kill, dungeon, raid, craft — where his AiBot fleet, real 1.12 clients, and this client all coexist.

**Core design stance.** The client reads WoW's own files directly (MPQ → BLP/ADT/M2/WMO/DBC) and will speak the genuine 1.12.1 network protocol. No asset server, no bake step, no format conversion, no coordinate conversion. The server is unmodified.

### Current state (2026-07-22)

Elwynn Forest renders and is walkable, entirely from the client's own MPQs, with no server and **no vmaps**. On top of Draft 3's world, there is now a **character**:

- A skinned Human Male M2 walks, runs, strafes, jumps and stands, driven by real vanilla animation clips
- Gear works: Tier 1 warrior plate paints into the body atlas, switches geosets, and mounts helm, pauldrons, Quel'Serrar and a shield on skeleton attachment points
- `ItemDisplayInfo.dbc` and `CharSections.dbc` are read straight out of the MPQs
- Third-person camera with the vanilla left/right mouse split

**Not started:** liquid rendering, tile streaming, networking, painterly pass.

**Known open bug:** texture flicker on the character. See §9.

---

## 1. Repository layout

Fully standalone. No project reference to MangosSuperUI.

```
MSUIClient/                          <- repo root, open MSUIClient.sln here
├── MSUIClient.sln
├── SETUP.md
├── PROJECT_HANDBOOK.md              this file
├── setup-gamedata.ps1               creates GameData/, copies MPQs (robocopy)
├── setup-vmaps.ps1                  optional; -Wsl mode for the travel machine
├── .gitignore                       MUST contain GameData/ — see §8.6
├── .gitattributes                   CRLF for C#/shaders/config, LF for markdown
├── GameData/                        GITIGNORED, several GB
│   ├── Data/                        the WoW 1.12.1 .MPQ archives
│   └── vmaps/                       optional; collision comes from client geometry
└── MSUIClient/                      project folder
    ├── Program.cs                   entry point + GameLoop + ImGui HUD + diagnostics
    ├── ClientConfig.cs
    │
    ├── Engine/
    │   ├── ClientWindow.cs          window, GL, main loop, INPUT (see §3.13)
    │   ├── Camera.cs                orbit camera, Yaw vs OrbitYaw (see §3.12)
    │   ├── Shader.cs                compile/link/uniform cache + SetVec4Array
    │   └── Texture.cs
    │
    ├── Player/
    │   └── CharacterController.cs   movement, gravity, sweep/slide/step-up
    │
    ├── World/
    │   ├── AdtCache.cs
    │   ├── TerrainTile.cs / TerrainTextures.cs / TerrainRenderer.cs
    │   ├── Wmo/WmoRenderer.cs
    │   ├── Doodads/DoodadRenderer.cs
    │   ├── Collision/{CollisionWorld,CollisionDebugRenderer,VmapCollisionLoader}.cs
    │   └── Units/                   <- ALL CHARACTER WORK LIVES HERE
    │       ├── M2Animator.cs        clip baking, bone matrices, the two strafe yaws
    │       ├── CharacterRenderer.cs skinned draw, geosets, texture slots, appearance
    │       ├── CharacterEquipment.cs body atlas composite + geoset rules
    │       └── AttachedItemRenderer.cs helms, shoulders, weapons, shields
    │
    ├── Formats/
    │   ├── Mpq/{MpqCrypto,MpqArchive,PkwareExplode,MpqArchiveWriter}.cs
    │   ├── MpqMount.cs              opens all archives once — §3.9, critical
    │   ├── BlpDecoder.cs
    │   ├── AdtTerrainReader.cs      ADT + the general MPQ file read
    │   ├── WmoReader.cs / M2Reader.cs / M2TextureParser.cs
    │   ├── DbcReader.cs             WDBC + ItemDisplayInfo + CharSections
    │   └── VmapFormat.cs
    │
    └── Shaders/                     ALL PURE ASCII, NO BOM — see §8.5
        ├── terrain.vert / terrain.frag
        ├── wmo.vert / wmo.frag      also used by doodads AND attached items
        ├── character.vert           skinned; pairs with wmo.frag UNCHANGED
        └── collision.vert / collision.frag
```

### 1.1 Where each file's responsibility ends

| File | Owns | Does NOT own |
|---|---|---|
| `Program.cs` | Startup order, game loop, HUD, cross-system diagnostics | Rendering internals, parsing |
| `ClientWindow.cs` | GL context, loop, raw input, mouse capture | What gets drawn |
| `Camera.cs` | View/projection, frustum, the Yaw/OrbitYaw split | Input handling, movement rules |
| `CharacterController.cs` | Movement, gravity, ground resolution | What the world is made of |
| `M2Animator.cs` | Clip baking, bone matrices, leg and torso yaw | GL, textures, which clip to play |
| `CharacterRenderer.cs` | Skinned draw, geoset visibility, texture slots, appearance, clip choice | Item data, attachment placement |
| `CharacterEquipment.cs` | Body-atlas composite, geoset rules from ItemDisplayInfo | GL, drawing |
| `AttachedItemRenderer.cs` | Item M2s on attachment points | The character's own mesh |
| `Formats/*` | Pure parsing, no GL, no game logic | Rendering, gameplay |

**Rule:** nothing in `Formats/` may reference Silk.NET or GL. `M2Animator` holds no GL either, and could move there if it ever needs to.

---

## 2. History — why it is native

The project began as a **browser client** (TypeScript, three.js, WebGL). It was abandoned on 2026-07-21 for one reason: **almost none of what it forced us to build was game code.**

```
Browser:  ADT -> C# parse -> GLB write -> HTTP -> JS parse -> GPU
Native:   ADT -> C# parse -> GPU
```

All format knowledge survived and is still exactly correct. The format readers were **copied** from SuperUI with namespaces changed; they are independent and will drift, so a genuine parser bug must be fixed in both places.

---

## 3. Ground truth — established facts, do not re-derive

### 3.1 Coordinate system — there is no conversion anywhere

WoW world space throughout: **+X north, +Y west, +Z up**, orientation in radians CCW about +Z from +X.

`System.Numerics.Matrix4x4` is **row-vector**. `Shader.Set` uploads with `transpose: false`. System.Numerics is row-major in memory and GL reads those bytes as column-major, which *is* the flip GLSL needs. Transposing in C# first double-flips it and the screen shows only the clear colour while draw calls and culling all look healthy.

### 3.2 Tile indexing — the axes are swapped

```
col = floor(32 - worldY / 533.33333)     first number, from Y
row = floor(32 - worldX / 533.33333)     second number, from X
```

Northshire is tile **[col 32, row 48]**. Both `000_32_48` and `000_48_32` exist on disk. `AdtTerrainReader.ReadFromMpq` takes **(gridX = row, gridY = col)** — inverted from the filename.

### 3.3 ADT placement space — MODF and MDDF are NOT world coordinates

```
worldX = C - posZ,  worldY = C - posX,  worldZ = posY
C = 32 * 533.33333 = 17066.67
```

Linear part determinant +1, so it is a rotation, not a mirror.

### 3.4 Model vertex conventions — three arrays, two conventions

| Data | Convention | Basis needed |
|---|---|---|
| **WMO vertices (MOVT)** | Z-up | `(x,y,z) -> (x,z,-y)` |
| **M2 render vertices** | **Y-up after M2Reader** | none for a doodad |
| **M2 collision hull** | Z-up | `(x,y,z) -> (x,z,-y)` |

**A bounding-box score is structurally blind to a 180° heading error.** Calibration settles which axis is up; only looking at the screen settles which way something faces.

### 3.5 M2Reader converts EVERYTHING to Y-up at parse

Not just vertices. Normals, **bone pivots**, **translation keys**, **rotation keys** `(qx, qz, -qy, qw)`, **scale keys** `(x, z, y)`. So vertices, pivots and animation tracks all live in one consistent space and **skinning needs no basis anywhere**.

The rotation mapping deliberately diverges from WMV's `(-qx,-qz,qy,qw)`, which assembles the body correctly and then rotates every joint the wrong way.

### 3.6 The skinning maths — free inverse bind

```
rest local     = T(pivot - parent.pivot)
animated local = S(scale) * R(rot) * T(rest + translationKey)      row-vector
global         = local * parentGlobal
```

Rest translations accumulate to exactly `T(pivot)`, so **`inverseBind = T(-pivot)`** — no matrix inversion, no error.

**Consequence worth keeping:** with no clip playing, every skin matrix is the identity and the model draws in bind pose, byte-identical to a static mesh. A placement bug and an animation bug can therefore never be the same bug, and the HUD has a `Bind pose` checkbox to split them in one click.

### 3.7 A character needs the model-to-world basis explicitly

`(x,y,z) -> (-z,-x,y)` — the **linear part of `DoodadRenderer.PlacementToWorld`**. Doodads look basis-free only because ADT placement space is itself Y-up and carries the flip; a character has no ADT placement.

**Heading = Yaw + 90°.** Model +X forward maps to world `(sin h, -cos h, 0)`. Confirmed on screen. Do not revisit.

### 3.8 Bone budget — 119, not 50

**HumanMale.m2 has 119 bones.** Vanilla characters carry a full set of finger and facial joints. `M2Animator.MaxBones` is **160** and `MAX_BONES` in `character.vert` must **always move with it** — 160 × 3 vec4 = 1920 float components.

The failure mode when it was 80 is worth knowing: bones past the limit were clamped onto the last valid one, which is **invisible in bind pose** (every skin matrix is the identity there) and grotesque the moment anything animates. Bind-pose-perfect plus animation-broken means a capacity failure, not a transform failure. `BoneOverflow` now refuses to animate rather than deform.

For many units in Phase 2, the answer is a uniform buffer object: GL 3.3 guarantees 16 KB, which is 341 bones.

### 3.9 Animation clip looping is NOT flag 0x20

`M2Sequence.IsLooping` reads bit 0x20 and **that bit is not a loop flag** — it is clear on Stand, Walk and Run. Trusting it made every clip a one-shot that clamped and held: a character who walks a few steps and freezes mid-stride, still correctly posed.

Real looping lives in the repetition fields at +24/+28, which `M2Reader` skips. `M2Animator.OneShotAnimations = {37 JumpStart, 39 JumpEnd}`; everything else loops.

**Clip key selection must use the sequence's absolute timestamp window**, never `Ranges[seqIdx]` — vanilla character M2s leave Ranges as `(0, count-1)` for every sequence.

### 3.10 Strafing is a SPLIT — legs and torso at different angles

Not "turn the body" and not "turn the legs". **Both, at different angles.** Measured against the real client at roughly 90° on the legs and 60° on the torso.

WoWee's `character_renderer.hpp` carries the matching hook: `setInstanceTorsoYaw(id, deltaYawRad)` with a per-instance `torsoYawOverrideRad`. A **delta** on the torso, over whatever the body is already doing.

```
angle phi = atan2(-sideness, forwardness)      relative to facing, + is his left
model heading += phi                            legs take all of it
TorsoYaw = (TorsoFollow - 1) * phi              torso keeps TorsoFollow of it
```

`TorsoFollow` defaults to 0.66. **1.0 reproduces whole-body, 0.0 reproduces lower-body-only** — one slider spans every mode we tried.

The angle derivation is not a guess: facing = `(cos Y, sin Y)`, right = `(sin Y, -cos Y)`, so a direction at world yaw `Yaw + phi` gives `forwardness = cos phi` and `sideness = -sin phi`.

**It does not touch `state.Yaw`.** That stays the character's facing, the camera stays behind it, and a movement packet wants it in Phase 2. Only the drawn model turns.

WoWee confirms there are **no strafe clips on land**: their `LocomotionFSM::resolve` computes strafe booleans and uses them only in the SWIM case.

### 3.11 Subtree yaw — how both halves are applied

A rotation appended AFTER a bone's global transform is applied in model space, and because every child does `local * parentGlobal`, whatever is appended to a bone becomes **the rightmost factor of that bone's entire subtree**. So one append at the hips rotates hips, thighs, calves and feet and touches nothing above; a second append at the spine does the torso.

`TwistBone` (hips) resolves from per-bone `KeyBoneId == 5` (Waist) — bone 21 on HumanMale. `TorsoBone` from `KeyBoneId == 4` (SpineLow). Both validated by subtree size, and the torso is rejected if it sits *inside* the leg subtree, where the two yaws would compound instead of splitting.

**The earlier version rotated everything and cancelled at one bone. It rotated the UPPER body** — which is how the right answer was found. If you invert something and the wrong half moves, the reading names the fix.

### 3.12 Camera — Yaw is the character, OrbitYaw is the camera

`Camera.Yaw` is the **character's facing**; the controller reads it directly. `Camera.OrbitYaw` is a camera-only offset wrapped to (-π, π]; `ViewYaw = Yaw + OrbitYaw` drives where the camera sits.

- **Left drag** → `RotateView` — swings the camera without turning him, so you can walk north and look at your own face
- **Right drag** → `Rotate` — turns him and the camera together
- **Right button DOWN** → `FoldOrbitIntoFacing` — `Yaw += OrbitYaw; OrbitYaw = 0`. Turns him to where the camera was swung **without the view moving**, because the same angle simply moved from one term to the other
- **Moving** → `EaseOrbitBehind`, unless the left button is held

`FlatForward`/`FlatRight` stay on `Yaw`, so W walks the character forward rather than toward the camera.

**Keys:** A/D turn, Q/E strafe, and **holding right mouse swaps them**. Arrows turn and walk, PgUp/PgDn tilt.

### 3.13 Mouse capture must be POLLED

Deriving capture from MouseDown/MouseUp alone is unreliable three separate ways: `CursorMode.Raw` can be refused or silently stop reporting motion; switching to Raw moves the reported position into another coordinate space so the first delta is nonsense; and a MouseUp delivered while ImGui owns the mouse, or with the pointer outside the window, is never seen.

`ClientWindow.PollMouse` derives capture from `IMouse.IsButtonPressed` every frame. Events supply only the motion delta. First delta after capture discarded, deltas over 300 px dropped, Raw falls back to Hidden with a printed line.

### 3.14 Movement feel — no smoothing on the stop

The measured ground speed is smoothed on the way **up** and taken immediately on the way **down**. WoWee's `LocomotionFSM` holds a grace window past the last motion; it was copied here and Nico's verdict was that it feels awful. **Do not reintroduce it.** The strafe angle snaps home on stop for the same reason.

### 3.15 Gear is THREE mechanisms

1. **Body atlas** — chest, legs, boots, gloves, bracers, belt, tabard have no geometry. They paint into the single 256×256 skin at fixed rectangles, eight texture slots per item.
2. **Geoset variants** — the same items switch which body geosets draw, via `m_geosetGroup`.
3. **Attached models** — helm, shoulders, weapons, shields, capes are separate M2 files on attachment points.

A Tier set is not one feature.

**`ItemDisplayInfo.dbc`, 23 fields, 92 bytes** (from SuperUI's DbcService, established by dumping all fields across robes, plate, cloth, boots and gloves plus a histogram over 29,604 records):

```
[0] ID  [1-2] modelName  [3-4] modelTexture  [5] inventoryIcon
[6-8] geosetGroup  [9] spellVisualID  [10] groundModel  [11] groupSoundIndex
[12-13] helmetGeosetVis  [14-21] texture[0..7]  [22] itemVisual
```

An earlier parser used a −2 texture shift that looked right on chests because the compositor's slot map started at 2 — two errors cancelling — and it hid LegLower and Foot entirely.

**m_texture slot → region:** 0 ArmUpper, 1 ArmLower, 2 Hand, 3 TorsoUpper, 4 TorsoLower, 5 LegUpper, 6 LegLower, 7 Foot.

**Atlas rectangles** (canonical, each column sums to 256): armUpper(0,0,128,64) armLower(0,64,128,64) hand(0,128,128,32) faceUpper(0,160,128,32) faceLower(0,192,128,64) torsoUpper(128,0,128,64) torsoLower(128,64,128,32) legUpper(128,96,128,64) legLower(128,160,128,64) foot(128,224,128,32).

**Composite order is equip order**, because vanilla textures are often overlay strips: a plate belt's LegUpper strip is a buckle band meant to draw over the legplates' thigh texture.

**Geoset rules** carry their confidence from `geoset-rules.js`: boots (cat 5) and gloves (cat 4) are verified against the decompiled `GeosRenderPrep` — the client computes `BASE + geosetGroup[N]`, so `+1` in variant terms. Robes are verified against a real DBC row. Chest, pants, tabard and shoulders are pattern-matched only. **A geosetGroup of zero means "leave the default", not "hide".**

**Helm hair suppression:** `helmetGeosetVis1 != vis2` means a closed helm. Helm of Wrath 248/306 closed; Helm of Might 247/247 open. It matters because the scalp dome is baked into each hair geoset, so hiding hair for an open helm leaves a hollow above the face.

### 3.16 Texture slots are filled BY TYPE, and the types do not share a source

This is the whole of the hair-and-cape problem, in both codebases.

```
type 0  the slot names a BLP - just read it
type 1  CHAR_SKIN         the body atlas
type 2  OBJECT_SKIN       a cape or item texture
type 6  CHAR_HAIR         CharSections section 3
type 7  CHAR_FACIAL_HAIR  CharSections section 2
type 8  SKIN_EXTRA        CharSections section 4 (underwear)
```

**Pointing every empty slot at the body atlas renders plausibly and is wrong everywhere it matters** — that is "hair textures like skin", and it hides every upstream error underneath it.

**`CharSections.dbc`, 10 fields, 40 bytes:** `[0] ID [1] Race [2] Sex [3] BaseSection [4] VariationIndex [5] ColorIndex [6-8] TextureName[0..2] [9] Flags`. Sections: 0 Skin, 1 Face, 2 FacialHair, 3 Hair, 4 Underwear.

**The match keys differ per section** and getting them wrong returns a plausible row for the wrong character:

| Section | Match on |
|---|---|
| Skin | colour (skin tone) |
| Face | variation (face shape) **and** colour (skin tone) |
| Hair | variation (hair style) **and** colour (hair colour) |
| Underwear | colour |

### 3.17 The eyes are not a geoset

Most races' body skin BLP has **no eye detail at all**. Eyes come from compositing the CharSections **Face** row onto the atlas — Texture1 is the lower face, Texture2 the upper, and the upper carries the eyes.

Miss that step and the character renders blank-faced, which reads as "eyes closed" and sends you hunting through geosets for something that was never there. (SuperUI's character viewer has this exact bug, and its own comment contains the proof: Human Female and Troll Female look right only because their base BLPs happen to have eyes baked in.)

**Take the region from the DBC field the texture came out of, never from its dimensions.** Texture1 is the lower face and Texture2 the upper — that is stated, not inferred. Inferring it back from image height paints the face across the eyes.

### 3.18 Attachment points

From SuperUI's `equip.js`, established by eye:

```
 0  LeftWrist    shields mount HERE, not on the palm
 1  HandRight
 2  HandLeft
 5  ShoulderRight   ModelName2, the R file
 6  ShoulderLeft    ModelName1, the L file
11  Helm
```

Placement is free: a rigid point attached to bone *b* transforms by that bone's **skin matrix**, so `T(attachment.Position) * Skin[BoneIndex] * instanceMatrix` is the whole thing and attached models follow the animation with no second bone chain. Item M2s draw unskinned — a sword does not bend.

**Helm models are per race and gender and nothing else is.** A helm must fit the head it sits on, so vanilla ships one file per head shape with a suffix like `Helm_Plate_A_01_HuM`; shoulders and weapons have a single file each. That asymmetry is why the helm was missing while everything else mounted.

Shoulders are **two files** and both are needed.

### 3.19 BLP alpha is not always on a 0..255 scale

Some BLPs decode 1-bit alpha as **0 or 1** rather than 0 or 255. In the shader that is 0.004, which fails any sensible cut on every texel — the surface loads, textures correctly, and renders as nothing at all. Guarded at the point of use in `WmoRenderer`, `CharacterRenderer` and `AttachedItemRenderer`. **The proper fix belongs in `BlpDecoder`** and has not been done.

### 3.20 MPQ access — the startup bottleneck

`AdtTerrainReader.ReadFileFromMpqs` reopens every archive on every call. `MpqMount` opens all 15 once and is hooked in with one line:

```csharp
AdtTerrainReader.StormLibExtractor = _mpq.ReadFile;
```

Terrain 4.7s → 0.4s, buildings 27.2s → 0.9s, doodads 26.9s → 13.1s. **Load order must stay** patches reverse-alphabetical, then `terrain.MPQ`, `model.MPQ`, then the rest. `MpqMount` is **not thread-safe**.

### 3.21 Network protocol (Phase 2 — not started)

Opcodes (1.12.1 build 5875): `SMSG_UPDATE_OBJECT=169`, `SMSG_COMPRESSED_UPDATE_OBJECT=502`, `CMSG_AUTH_SESSION=493`, `SMSG_AUTH_CHALLENGE=492`, `CMSG_PLAYER_LOGIN=61`, `SMSG_LOGIN_VERIFY_WORLD=566`, `MSG_MOVE_HEARTBEAT=238`, `CMSG_CHAR_ENUM=55`. 825 opcodes, `NUM_MSG_TYPES=828`.

UpdateFields: use `UpdateFields_1_12_1.cpp` (flat table, 324 rows), **not** the `.h`. PLAYER=1282 slots, UNIT=188.

Vanilla is **client-authoritative for movement**, which is why `CharacterController` is the real simulation and not a prediction.

**Appearance arrives as four bytes** on the character record: skin, face, hairStyle, hairColor — the exact CharSections lookup keys in §3.16.

### 3.22 Server environment

realmd `0.0.0.0:3724`, world `0.0.0.0:8085`, DataDir `/home/wowvmangos/vmangos/run/data`, client MPQs `/home/wowvmangos/wowclient/Data`. **`Anticheat.Enable=0` and `Warden.*Enabled=0` already.**

Travel machine: project on Windows at `C:\Users\nico\source\repos\MSUIClient`, vmangos fork inside WSL on the same machine.

---

## 4. Verified vs unverified

### Verified against reality

- Terrain heights match the server exactly (`[verify] PASS delta -0.00`)
- WMO placement, M2 render and collision bases, client-geometry collision
- Character controller: walking, running, jumping, stairs, wall slide
- Skinned character: placement, heading offset 90, 119-bone skeleton, clip playback
- Gear: ItemDisplayInfo layout, atlas rectangles, item texture and model path conventions, attachment points
- The Yaw/OrbitYaw camera split, and the A/D-turn Q/E-strafe binds

### Not yet verified — expect bugs here

- **Character texture flicker** — open. See §9.
- **The face composite method.** WoWee stacks CharSections layers full-canvas; SuperUI paints them into face rectangles. Both cannot be right for the same file; the client tries size-appropriate handling and prints which happened.
- **Geoset rules for chest, pants, tabard and shoulders** are pattern-matched, not verified.
- **`TorsoFollow` 0.66** is Nico's read by eye, not their constant.
- `BlpDecoder` alpha scaling (§3.19), MOPY F_DETAIL filter, WMO group culling
- Liquid, tile streaming, networking: not written

---

## 5. Runtime architecture

### 5.1 Startup order — this order matters

```
ClientConfig.Load
MpqMount + StormLibExtractor hook    BEFORE anything reads a file
TerrainRenderer.LoadShaders
AdtCache
TerrainRenderer.LoadAround / VerifyAgainst
WmoRenderer.LoadForTiles             buildings BEFORE collision
DoodadRenderer.LoadForTiles + WMO interior doodads
adts.Clear()
LoadCollision()
CharacterController                  teleported to sampled ground
CharacterRenderer.LoadShaders + Load + Equipment + ApplyEquipment
CollisionDebugRenderer.Build
```

### 5.2 Shaders

`wmo.vert`/`wmo.frag` are used by `WmoRenderer`, `DoodadRenderer` **and** `AttachedItemRenderer`. `character.vert` pairs with `wmo.frag` **unchanged**, so a character cannot light differently from the world.

Each owns its **own `Shader` instance, which is a separate GL program** — a uniform set on one does not apply to another. Forgetting `uAlphaCutoff` on the doodad program turned every tree into a black rectangle.

Bones upload as **three vec4 rows per bone**, so skinning is three dot products and there is no mat3x4 column-order question.

### 5.3 Debug tooling — use it before theorising

| Tool | What it answers |
|---|---|
| `Bind pose` | Splits placement bugs from animation bugs — identities everywhere |
| `Force angle (deg)` | Drives the strafe mechanism directly, decoupled from the trigger |
| `Solo one geoset` | Draws one geoset at a time. **Solo beats hide for overlap bugs** |
| `Geosets drawn` | Category and variant of everything currently drawn |
| `Attached items` | Per-piece switches. Attached models are not geosets |
| `Hide hair` | Hair without the body — category 0 holds both |
| `Magenta unbound` | Which geosets have no texture |
| Mouse diagnostics | Buttons, capture, move events, applied events, last delta, cursor mode |
| `C` / Show collision | Green standable, red wall, yellow the exact triangle underfoot |
| Cyan capsule | Where the character actually is, at real radius and height |

---

## 6. Working method — what has actually worked

Written down because the same three moves have solved almost every hard bug in this project.

**When something "does nothing", build a control that drives the mechanism directly.** Two rounds were lost to "didn't change anything" on the strafe twist. A slider that applied the twist regardless of the trigger turned it into "that caused strafe on the upper body, not lower" — which contained the fix.

**For overlap bugs, solo beats hide.** Hiding one participant proves a pair stopped fighting but never says which pair. Soloing enumerates participants and the answer is an index.

**An isolation control that cannot isolate the suspect is not an instrument.** Category 0 holds the base body *and* every hairstyle, so the category checkbox could not test hair against a helm without deleting the character.

**Prefer a measurement to another theory.** Printing collision bounds found a 26,000-unit coordinate-space error in one line, after walking around inside it found nothing.

**Do not guess at something you were handed.** CharSections states which texture is the lower face; inferring it back from image height painted the face across the eyes.

**When two references disagree, implement both and print which fired.** The face composite does this.

**Nico's eyes beat a comment in someone else's codebase.** The grace timer was well-argued and felt awful.

---

## 7. Phase plan

| Phase | Content | State |
|---|---|---|
| P0 | Foundations, opcode/updatefield generators | done |
| **P1** | **Northshire offline: terrain, buildings, doodads, collision, movement** | **DONE** |
| **P1.5** | **Character: skinning, animation, camera, gear, DBC** | **substantially done** |
| P2 | Enter world — realmd, world server, SRP6, header crypto, movement packets | not started |
| P3 | Combat | not started |
| P4 | Quests and systems | not started |
| P5 | Dungeons (Deadmines target) | not started |
| P6 | Raids | not started |
| P7 | Painted art pass | parallel from P4 |

### 7.1 Immediate next steps

1. **The texture flicker** (§9).
2. **Tile streaming.** Fixed 3×3 block at boot, so the world ends at the edge. `AdtCache.Forget` exists; buildings and doodads need per-tile instance tracking. BVH rebuild is 0.3–0.4s against a tile crossing roughly every 75s, so synchronous is probably fine.
3. **Liquid.** `MCLQ` and `MLIQ` are both parsed and drawn nowhere.
4. **`BlpDecoder` alpha fix** (§3.19), then remove the three point-of-use guards.
5. **P2 networking.**

### 7.2 Deliberately out of scope, permanently

- Supporting retail or any client other than 1.12.1 build 5875
- Server modifications
- Reimplementing FrameXML/Lua

### 7.3 Environment and how to run

```powershell
cd C:\Users\nico\source\repos\MSUIClient
dotnet build
dotnet run --project MSUIClient
```

Controls: **W/S walk, A/D turn, Q/E strafe** (holding right mouse swaps A/D to strafe), arrows turn and walk, PgUp/PgDn look, Shift walk, Space jump, F fly, C collision, left mouse orbits the camera, right mouse turns him, wheel zooms, Esc quits.

---

## 8. Troubleshooting playbook

### 8.1 Build errors

- **CS0102 duplicate definition** — a nested type and a method cannot share a name. `Mount` the class and `Mount` the method collided; the method became `AddMount`.
- **CS0111 / CS0103 after a refactor** — from editing by text slice and cutting a neighbour out with the target. **After any excision or cross-file copy, grep the surrounding scope for every identifier the remaining code still references.**
- **Named tuple elements lost in a ternary.** Name the fallback: `(x: 0f, y: 0f, z: 1f)`.
- **Silk.NET.OpenGL has its own `Texture` and `Shader`.** Alias them.

### 8.2 Nothing renders

Double transpose (§3.1) — draw calls and culling counts will all look healthy. Then shader compile errors, then frustum culling.

### 8.3 A surface loads and textures but does not appear

§3.19, alpha. Drag the alpha cutoff to 0.

### 8.4 The model is folded, exploded or flat

Check the bone count against `MaxBones` first (§3.8), then `Bind pose`. Bind pose correct plus animation wrong is a capacity or clip problem, not a transform one.

### 8.5 Non-ASCII kills shaders and PowerShell

One em-dash in a shader comment made Intel's GLSL compiler report "pre-mature EOF" on a complete shader, and the same character made PowerShell report brace mismatches across a whole script. **All `.vert`, `.frag` and `.ps1` files must be pure ASCII with no BOM.**

### 8.6 GameData must never enter git

`git check-ignore -v <path>` prints **nothing** for a file that is already **tracked**. Use `--no-index`. A first attempt committed 5.34 GiB before this was noticed.

---

## 9. OPEN BUG — character texture flicker

Surfaces on the character flicker in and out, "as if things are competing with one another for visibility and position". Still present with all gear removed and every attached item hidden.

**Ruled out:**

- LOD duplication — `M2Reader` reads view 0 only
- Attached items — present with them off
- A single geoset category — hiding categories individually did not stop it

**Instruments in place:**

- `Solo one geoset` — draws one at a time. A geoset that flickers *alone* is self-overlapping or fighting something outside the geoset list; if none flickers alone, the fight is between two of them
- **Overlapping-draw detector** — at load, any two visible pieces whose index ranges intersect are printed. Two draws sharing triangles are the same surface submitted twice, which is z-fighting by construction. Silence means the geometry is disjoint and the cause is elsewhere

**Not yet examined, in order of suspicion:**

1. **M2 render flags are ignored.** `M2RenderFlag` carries `Unlit`, `TwoSided` and **`NoZWrite` (0x10)**, and `BlendingMode`. Everything is currently drawn opaque with an alpha discard and full depth writes. A geoset authored as a blended, non-depth-writing layer — hair, eye glow — will fight whatever is behind it when forced to write depth.
2. **Batch draw order.** `M2Batch` carries `PriorityPlane` and `MaterialLayer`, both ignored.
3. **Coplanar-but-distinct meshes** that vanilla resolves by draw order rather than by depth.

---

## 10. What to ask Nico for

He has a large adjacent codebase (**MangosSuperUI**) and a cloned reference client (**WoWee**, `C:\Users\nico\Desktop\WoWee-master`). **Check both before writing anything from scratch** — that mistake has been made twice, once on WMO rendering and once on the DBC layer.

### Already brought over — do not ask again

`MpqCrypto`, `MpqArchive`, `PkwareExplode`, `MpqArchiveWriter`, `BlpDecoder`, `AdtTerrainReader`, `VmapFormat`, `WmoReader`, `M2Reader`, `M2TextureParser`. Plus the layouts and rules lifted from `DbcService`, `geoset-rules.js`, `region-rects.js`, `equip.js` and `SkinnedGlbWriter`.

### Ask for these when the matching work starts

| Work | Ask for | Why |
|---|---|---|
| The flicker (§9) | WoWee `src/rendering/m2_renderer.cpp` render-state setup | Whether they honour NoZWrite, blend mode and priority plane |
| Torso yaw constant | WoWee `src/rendering/character_renderer.cpp`, `setInstanceTorsoYaw` | The real fraction behind §3.10's 0.66 |
| Cape rendering | SuperUI `equip.js` cape path, WoWee `appearance_composer.cpp` cloak slot | Type-2 OBJECT_SKIN handling |
| Dungeons (P5) | SuperUI `WdtReader.cs` | Detects global-WMO instance maps; 13 dungeons with Map.dbc names |
| Particles / spell visuals | SuperUI `M2EmitterParser.cs`, `M2ParticlePatcher.cs` | |
| Anything protocol (P2) | The opcode and UpdateFields generators from the browser era | §3.21 has the values |

### Surveying WoWee

`index-cpp.ps1` on his Desktop builds a symbol index and assembles context packets:

```powershell
.\index-cpp.ps1 -Find strafe
.\index-cpp.ps1 -Packet "torso yaw bone"
.\index-cpp.ps1 -Symbol setInstanceTorsoYaw -Depth 1
```

### Always ask before writing code against a file

**Ask for the relevant files rather than guessing at their API.** Every reader in `Formats/` has surprises in it, and inventing a signature wastes a build cycle. He would rather paste a file than debug a wrong assumption.

### Ask for a console paste, not a description

Almost every hard bug here was resolved by a number in the log — the collision bounds that revealed a 26,000-unit error, the bone count that explained the folded character, the attachment list that explained the missing helm. When something looks wrong, add the measurement, ask him to run it, and read the output.

---

## 11. Working agreements

- **Complete files, not diffs.** Deliver whole replacement files and say plainly where each one goes.
- **CRLF** for `.cs`, `.vert`, `.frag`, `.json`, `.ps1`; **LF** for markdown.
- **Pure ASCII, no BOM** in shaders and PowerShell (§8.5).
- **Never question deployment steps.**
- **Empirical over documented.** If a doc and the bytes disagree, the bytes win.
- **Land an answer.** Exploration-only replies waste his time; every response should produce something he can build, run or read.
