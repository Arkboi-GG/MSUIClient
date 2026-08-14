# System — Character & Unit Rendering

**How a player, a humanoid NPC and a beast all become a textured, correctly-geoseted,
skinned M2 on screen — and why the same model can render *twice at once* if one method
forgets to reset.** One of the per-system docs the handbook indexes (see
PROJECT_HANDBOOK.md §1.2). This is the planned extraction of handbook §3.4–3.19 plus the
unit/NPC work and the re-entrancy fix landed 2026-07-27. Read this plus the handbook's
cross-cutting ground truth (§3.4 vertex conventions, §3.7 the model-to-world basis, §11
working agreements) before touching any of it. You should not need the rest of the
handbook.

Version: Draft 2 — 2026-07-29 (adds §1.5's SCALP/hairline composite — the missing benilla
overlay pair that made hair dissolve into the forehead — and the blank-hair-row substitute).
Draft 1 — 2026-07-27 (first extraction; adds the geoset-visibility engine, the
humanoid-NPC / beast split, live diagnostics, and the **Load re-entrancy** post-mortem in
§2, which is why the logged-in character grew a second head of hair).

Owner files: `World/Units/CharacterRenderer.cs` (player + humanoid-NPC body: model load,
texture slots, atlas composite, geoset apply, skinned draw, diagnostics, `ResetModelState`),
`World/Units/CreatureRenderer.cs` (beast + humanoid-NPC *instances*: type-aware textures,
skeletal animation, geoset filter), `World/Units/AttachedItemRenderer.cs` (helm/shoulder/
weapon M2s on attachment points), `World/Units/M2Animator.cs` (clip baking, bone matrices,
leg/torso yaw), `Formats/CharacterGeosets.cs` (the benilla `visible_geosets` engine +
`CharacterFacialHairTable` + `HelmetGeosetVisTable`), `Formats/CreatureDbc.cs`
(CreatureDisplayInfo / CreatureModelData / CreatureDisplayInfoExtra + the resolver),
`CharacterEquipment.cs` (the body-atlas composite + geoset rules from ItemDisplayInfo), the
`Net/*` entity + `SMSG_MONSTER_MOVE` stream that feeds NPCs, and the **Character** panel in
`Program.cs` (the live HUD + the Capture button).

---

## 0. The bar

A unit is not "an M2 with a texture on it." Vanilla ships a **data chain** — DBC rows that
say which model, which skin, which hairstyle, which geoset variants and which attachments a
given character or display id resolves to — and the bar is to follow that chain rather than
to render a plausible-looking approximation of it.

The single clearest test of whether it is right: **your logged-in character must look like
your character, and a Stormwind guard must look like that guard** — one hairstyle, one beard,
sleeves that flare the right way, no chainmail bleeding into the hair, and *no second model
stacked on top of the first*. Every wrong version of this system fails one of those, and the
ways it fails are specific and repeatable (§2, §3), not mysterious.

The reference is benilla (`benilla-formats/characters/`, `benilla-formats/creatures.rs`).
Where this doc says "benilla port" it means byte-faithful, and the parity is the point: the
same `visible_geosets` function drives the player and every humanoid NPC.

---

## 1. What is implemented now

### 1.1 The pipeline — one chain, three entry points

```
PLAYER            roster / SMSG_UPDATE_OBJECT: race, sex, skin, face, hair, facial, equipment
HUMANOID NPC      UNIT_FIELD_DISPLAYID -> CreatureDisplayInfo -> CreatureDisplayInfoExtra
BEAST             UNIT_FIELD_DISPLAYID -> CreatureDisplayInfo (ExtendedDisplayId == 0)
        |
        v
  model path (.m2)  +  scale
        |
        v
  TEXTURE SLOTS filled BY TYPE (§1.4)  ---- body atlas composited from equipment (§1.5)
        |
        v
  GEOSET VISIBILITY set (§1.3)  ---- which skinSectionIds to draw; everything else culled
        |
        v
  SKINNED DRAW (§1.6)  +  ATTACHED item M2s on points (§1.7)
```

The player and a humanoid NPC run the **same body pipeline**. The only difference is where
the descriptor comes from: the player's from the character roster / object-update stream, the
NPC's from `CreatureDisplayInfoExtra`. That shared path is deliberate — a guard is a
character model wearing items, not a special case.

### 1.2 Two kinds of unit, and they texture/geoset *differently*

`Formats/CreatureDbc.cs` resolves a display id and reports `HasExtended`:

- **Beast** (`ExtendedDisplayId == 0`): a monster M2. Texture slots are **monster-skin**
  types (11/12/13), filled from the three `CreatureDisplayInfo.Textures` variation columns.
  **Every submesh draws** — a beast has no character geosets to filter.
- **Humanoid NPC** (`ExtendedDisplayId != 0`): a *character* M2. Its body slot (type 1) is
  filled from `CreatureDisplayInfoExtra` (race/sex/skin), and **its geosets are filtered
  exactly like a player's** from the extra row's hair/facial/equipment. Skip the filter and
  every hairstyle and beard the model carries stacks at once — the "messy head / clipping"
  symptom.

`CreatureRenderer.ResolveBatchTexture` is the switch:

```
type 11 / 12 / 13   monster skin  -> info.Textures[type-11], fallback to Textures[0]
type 1              CHAR_SKIN     -> NpcBodySkinCandidates(info)  (the extra row's body atlas)
default / 0         embedded name, else Textures[0]
```

### 1.3 The geoset-visibility engine — `CharacterGeosets.Visible()`

A character model carries **many** geosets; a submesh's `skinSectionId` (`M2Submesh.Id`)
encodes `group*100 + variant`. You draw a submesh **iff** its id is in the returned set.
`Formats/CharacterGeosets.cs` is a byte-faithful port of benilla
`characters/geosets.rs::visible_geosets`. Group meanings: 0 hair/scalp, 1/2/3 facial hair,
4 gloves, 5 boots, 7 ears, 8 sleeves, 9 knees, 10 doublet, 11 legs, 12 tabard, 13 robe skirt,
15 cape; 6 and 14 are always-on bases.

The algorithm, in order:

1. **Region bases** — start every region at its base variant:
   `{1, 101, 201, 301, 401, 501, 601, 702, 801, 901, 1001, 1101, 1201, 1301, 1401, 1501}`
   plus body `0`. Note **group 7 (ears) defaults to variant 2 → 702**, not 701.
2. **Hair** (group 0) from `CharHairGeosets` by (race, sex, hairStyle), clamped `>= 1` — a
   "bald" style is the bare scalp geoset, never the body.
3. **Facial hair** (groups 1/3/2 — the DBC's three columns map to that order) from
   `CharacterFacialHairStyles`.
4. **Helm hides hair/facial/ears** — a closed head item's `HelmetGeosetVisData` row carries
   five **race-bitmask** columns; a set bit for this race forces that slot back to its base.
5. **Equipment branches** (benilla `geosets.rs:102-148`): gloves replace naked gloves else
   chest sleeves add; shirt sleeves only with no chest; a robe hides boots/knees/legs/trousers
   and shows its skirt, else boots, else kneepads; tabard unless a robe; doublet + pant legs;
   cloak replaces the naked cape.

**A `geosetGroup` of zero means "leave the default", not "hide"** (handbook §3.15). The
fail-safe matters: in `CreatureRenderer`, if the computed set matches **zero** of the model's
submeshes, the filter is dropped (`VisibleGeosets = match > 0 ? vis : null`) and everything
draws — a wrong filter that hides the whole NPC is worse than an unfiltered one.

### 1.4 Texture slots are filled BY TYPE — and the types do not share a source

This is the whole of the hair-and-cape problem, in both codebases (handbook §3.16). The M2's
texture units name a *type*, not always a file:

```
type 0   the slot names a BLP        -> just read it
type 1   CHAR_SKIN                    -> the body atlas (composited, §1.5)
type 2   OBJECT_SKIN                  -> a cape or item texture; nothing until one is worn
type 6   CHAR_HAIR                    -> CharSections section 3, by hair style AND colour
type 7   CHAR_FACIAL_HAIR            -> CharSections section 2
type 8   SKIN_EXTRA                   -> CharSections section 4 (underwear)
```

**Pointing every empty slot at the body atlas renders plausibly and is wrong everywhere it
matters** — that is "hair textured like skin / chainmail in the hair", and it hides every
upstream error underneath it. `CharSections.dbc` match keys differ per section (Skin: colour;
Face: variation **and** colour; Hair: variation **and** colour; Underwear: colour) — the
wrong key returns a plausible row for the wrong character.

Two guards this system already carries:

- **Unbound type-6 fallback.** A hair (type 6) slot with no `CharSections` row must not be
  left to sample the dressed atlas — that is exactly how armour bleeds onto the head. It
  falls back to the race hair convention BLP (`Character\<Race>\Hair00_00.blp`), like benilla.
- **Head pieces never sample the geared atlas.** In the draw loop, a hair/scalp/ear piece
  (category 0 variant > 0, or category 7) that resolves to the body-skin slot is redirected
  to the **bare** skin (`_bareSkin`), not the dressed one — so a helmet or glove strip
  composited into the atlas cannot appear on the scalp. (This override is currently slightly
  broad; see §6.)

### 1.5 The body atlas — gear that is paint, not geometry

Chest, legs, boots, gloves, bracers, belt and tabard have **no geometry of their own**. They
paint into a single **256×256** skin atlas at fixed rectangles, up to eight texture slots per
item, composited **in equip order** (vanilla textures are overlay strips — a plate belt's
thigh band is meant to draw over the legplates). `CharacterEquipment.Composite` builds this
onto the **bare** skin, which is kept alive so re-equipping composites onto a face rather than
erasing it.

Canonical rectangles (each column sums to 256): `armUpper(0,0,128,64)`
`armLower(0,64,128,64)` `hand(0,128,128,32)` `faceUpper(0,160,128,32)`
`faceLower(0,192,128,64)` `torsoUpper(128,0,128,64)` `torsoLower(128,64,128,32)`
`legUpper(128,96,128,64)` `legLower(128,160,128,64)` `foot(128,224,128,32)`. **These match
benilla's rects exactly** — verified this session; they were never the bug (§3).

**The eyes are not a geoset** (handbook §3.17). Most races' body BLP has no eye detail; eyes
come from compositing the CharSections **Face** row — Texture1 lower face, Texture2 upper (the
upper carries the eyes). Take each region from the DBC field the texture came from, **never**
inferred from image dimensions — inferring paints the face across the eyes.

**And neither are the EYEBROWS** (fixed 2026-07-28). The brows are the CharSections **FacialHair**
section (type 2) composited onto the SAME two face tiles as the face, *on top of* it, in benilla's
build order `skin → face → facial hair → hair` (`benilla-formats characters/sections.rs
composite_body`): FacialHair Texture1 → lower face, Texture2 → **upper (the brow row)**. It is keyed
by **hair colour, not skin** — which is why the brows tint to match the hair. `BuildTextureSlots`
originally looked up the FacialHair row only to bind it as the type-7 mesh slot (beard geometry) and
never added it to the skin-atlas overlay list, so a male composited eyes but no brows (the "no
eyebrows" report — ours vs 1.12, Human Male). Fix: append the FacialHair Texture1/Texture2 to
`overlays` after the Face entries; the alpha-aware `BlitOver` blends the brow strip over the eyes.
Human FEMALE has no FacialHair section (customization.rs: only NightElf/Undead females do), so that
path no-ops for them — their brows, if any, ride the Face row. Beard *geometry* (facial geosets
1/2/3) is a separate concern, textured off the hair sheet (type 6), not touched by this.

**AND NEITHER IS THE HAIRLINE** (fixed 2026-07-29 — Nico: *"the hair blends into the
forehead"*, ours vs 1.12, Human male). benilla's head fan-out is `skin → face → facial hair →
**hair**`, and that last pair is the CharSections **Hair** row's *other two columns*:
`Texture2 = ScalpLower<style>_<colour>` → the **lower** face tile, `Texture3 =
ScalpUpper<style>_<colour>` → the **upper** tile (`sections.rs composite_body`: SECTION_HAIR
col 1 → TILE_G9, col 2 → TILE_G8). They paint the hairline and the scalp shading onto the head
*itself*, under the hair mesh. This matters because a hair geoset has **two** submeshes — the
hair sheet one (type 6) *and* a scalp one that samples the **body atlas** (visible in the
capture: `geo19 … slot=type6` next to `geo19 … slot=type1`). With no scalp strips composited,
that second submesh is bare forehead skin, the hair has no painted root line, and the mesh
edge fades into the face. `BuildTextureSlots` read the Hair row only for `Texture1` (the mesh
sheet) and never added columns 2/3 to `overlays`.

> **Do not trust benilla's own comment here.** It claims the hair columns are empty for Human
> male and that the overlay only matters for other races. That is **wrong on 1.12.1 data** —
> verified by decoding `CharSections.dbc` straight out of `GameData/Data/dbc.MPQ`: Human male
> hair variations 1–11 all carry `ScalpLowerHair0x_00` / `ScalpUpperHair0x_00`, and only the
> blank variation 0 has none. The code comment records this so the overlay is not "optimised"
> back out.

**The blank hair row substitutes variation 1, not `Hair00_00`** (same fix). The client has no
fallback *lookup*: its single type-6 binder reads `TextureName[0]` and an **empty name is a
no-op that leaves the slot untouched**, with two of its three call sites passing variation
**literal 1**. The fixpoint is "take the hairStyle row; when it is blank take **variation 1 at
the same colour**" (benilla `hair_mesh_texture`). Human male variation 0 is the only fully
blank row in the table and resolves to `Hair03_<colour>` — *not* the `Character\Human\Hair00_00`
the old convention fallback picked, which is the **female** sheet at **colour 0**. The
convention fallback is still there, but only for a race/sex with no Hair row at all.

### 1.6 Skinning & animation — the free inverse bind (handbook §3.5–3.11)

`M2Reader` converts **everything** to Y-up at parse — vertices, normals, bone pivots and all
animation keys — so skinning needs no basis anywhere. Rest translations accumulate to exactly
`T(pivot)`, so `inverseBind = T(-pivot)` with no matrix inversion. **With no clip playing
every skin matrix is the identity and the model draws in bind pose, byte-identical to a static
mesh** — so a placement bug and an animation bug can never be the same bug (the HUD's *Bind
pose* checkbox splits them in one click).

Load-bearing facts that cost real debugging:

- **Bone budget is 119, not 50.** HumanMale.m2 carries a full finger/facial skeleton.
  `M2Animator.MaxBones = 160` and `MAX_BONES` in `character.vert` **must move together**
  (160 × 3 vec4). Over the limit, `BoneOverflow` refuses to animate rather than deform —
  because a truncated skeleton is bind-pose-perfect and grotesque in motion.
- **Looping is NOT flag 0x20** — that bit is clear on Stand/Walk/Run. Everything loops except
  `{37 JumpStart, 39 JumpEnd}`. Select clip keys by the sequence's absolute timestamp window,
  never `Ranges[]`.
- **Strafing is a split**, not a turn: legs take the full angle, the torso keeps `TorsoFollow`
  (default 0.66) of it, applied as subtree yaws at the waist and spine. It never touches
  `state.Yaw` — the facing a movement packet wants stays put.
- `CreatureRenderer` runs the **same** `M2Animator` per instance, gait chosen from the
  `SMSG_MONSTER_MOVE` spline speed (§1.8).

### 1.7 Attachments (handbook §3.18)

Helm, shoulders, weapons, shields and capes are **separate M2 files** on attachment points,
drawn by `AttachedItemRenderer`. A rigid point on bone *b* transforms by that bone's skin
matrix — `T(attachment.Position) * Skin[BoneIndex] * instanceMatrix` — so attached models
follow the animation with no second bone chain, and item M2s draw **unskinned** (a sword does
not bend). **Helm models are per race+gender**; shoulders are **two files** (L = ModelName1 /
point 6, R = ModelName2 / point 5) and both are needed. Shields mount on **LeftWrist (point
0)**, not the palm.

### 1.8 NPC movement — the entity stream

Humanoid NPCs and beasts move on `SMSG_MONSTER_MOVE` (0xDD) splines: packed 11/11/10-bit
quarter-yard points decoded in `Net/MonsterMove.cs`, driving each instance's position and
feeding the animator its gait (idle vs walk vs run) from the spline's speed. `Net/Entities.cs`
+ `GameLoop/Scene/GameLoop.Net.cs` carry the object-update stream that spawns/despawns units and supplies the
display id the resolver turns into a model. Without this, NPCs stand frozen at spawn — the
original "no movement on any NPCs" report.

### 1.9 In-client diagnostics — a colour you read, not console lines you scrape

Nico's rule, earned the hard way: *"I need a button that says capture. This 'turn the screen
and then paste hopefully what appear to be the right stuff' is poor."* So the head problem is
instrumented **in the client**, in the **Character** panel of `Program.cs`:

- **Live head status** — green "head: <resolution>" when a real DBC-matched hair geoset covers
  the scalp, red "HEAD ISSUE" when the bald base-body scalp would show through.
- **Head / hair geosets tree** — every visible category-0/7 piece with its slot type, fill and
  source, so "which texture is on the scalp" is readable at a glance.
- **Capture diagnostics → file** — writes `msui-character-diag.txt` next to the client
  (`AppContext.BaseDirectory`) with the full descriptor, the texture slots, the head/hair/ear
  geosets, and a **UV probe**: per visible submesh, the vertex Y-range and V-range plus the
  crown-V band. That probe is what caught §2 — the head geosets read correct while the probe
  listed submeshes that should not have existed.

---

## 2. The re-entrancy discovery — one model, drawn twice (fixed 2026-07-27)

Nico, on the logged-in character: *"I can see my dark hair under (correct), but there is
'Extra hair' on top … my arms/hands chainmail is wrong, almost look inverted — should be the
type that flares out towards the elbow, instead it looks like flaring near the hands like
robes."* Then the correction that pointed straight at the cause: *"the MSUIClient test
character was fine … as we started debugging the hair, it got crapped out. I am **not** saying
the texture is flipped. I am saying that the ARM TYPE which decides the shape the BLP is
painted onto is wrong."*

Three symptoms — extra hair on top, a doubled pale scalp cap, and a bracer that flared the
wrong way — and they were **one bug**.

### 2.1 The proof

The Capture file settled it. Inside `ApplyGeosetVisibility`, the head diagnostic reported a
**single, correct, DBC-matched hair** (`hair 17 → geoset variant 19`). But the UV probe,
which walks `_pieces` at draw time, listed **extra visible submeshes that the visibility set
never chose** — `geo12 / geo16 / geo10` (more category-0 hair) and `geo701` (the other ear
variant). The selection was right; the *draw list* had leftovers. That gap is the whole tell:
state was surviving across a reload.

### 2.2 The cause

`CharacterRenderer.Load()` runs **twice**. Once at startup for the test body (Human Male), then
again from `ApplyServerCharacter` for the real logged-in character (Human Female). And
`BuildPieces`, `BuildTextureSlots` and `BuildGpuBuffers` **all APPEND** to persistent lists and
buffers — nothing reset them between loads.

So the second login did not *replace* the test model; it **stacked the Female model on top of
the Male one**. Two sets of geosets in `_pieces`, two sets of texture slots, and — the reason
the arm "changed type" — old submesh indices from the Male model now pointing into the Female
mesh. That mis-indexing is the warped bracer; the leftover category-0 geosets are the extra
hair and the doubled cap. One missing reset, three symptoms.

This is also why Nico's instinct was exactly right and the earlier texture-flip theory was
exactly wrong: the arm's *shape* was wrong because the geoset feeding it was stale, not because
any BLP was mirrored.

### 2.3 The fix

A per-model `ResetModelState()`, called in `Load()` immediately after `_m2 = m2;`, **before**
the appending builders run. It mirrors `Dispose()`'s cleanup for the per-model resources —
deletes the VAO/VBO/EBO, disposes the distinct slot textures plus `_bareSkin` / `_dressedSkin`
/ `_magenta`, clears `_slots` / `_pieces` / `_headDiag`, and resets `_bodySlotIndex` /
`_baseSkin` / `_animator` / `_clip` / `BoneOverflow`. It deliberately does **not** touch
`_shader` or `_attached`, which are created once in `LoadShaders` and persist across models.
It is a no-op on the first load and only bites on the reload.

### 2.4 The rule this leaves behind

> **Any builder that APPENDS to a persistent list or GPU buffer must have a matching reset that
> runs before a re-entrant `Load` — or `Load` must be idempotent by construction.** The append
> pattern is fine; the *missing* reset is the bug. A renderer that is only ever built once hides
> this defect perfectly until the first thing that rebuilds it (here: logging in) turns one
> model into two.

A corollary for debugging: when the *selection* logic reports the right answer but the *drawn*
result has extras, stop auditing the selection. Look for retained state across a rebuild. The
UV probe existed precisely to make that gap visible, and it did.

---

## 3. What is CONFIRMED correct — do not re-chase these

The head problem sent several plausible theories up blind alleys before §2. They are recorded
here as **ruled out**, so the next person does not re-spend the hours:

- **The composite atlas rectangles are correct.** MSUI's rects match benilla's byte-for-byte
  (§1.5). "Chainmail in the hair" was never a rect error.
- **No texture is flipped or mirrored.** Nico said so directly and the fix confirmed it — the
  arm's wrong flare was a stale geoset (§2.2), not UV orientation.
- **Geoset *selection* is correct.** `CharacterGeosets.Visible()` is the benilla port and the
  head diagnostic proved it chose a single DBC-matched hair. The extras came from retained
  state, not from over-selection.
- **The hair geoset resolves correctly** — `CharHairGeosets` mapped style 17 → variant 19, a
  clean DBC match, with the scalp covered.
- **The unbound-type-6 fallback and the head bare-skin override help**, but they were treating
  symptoms; neither was the root cause. Keep them, but know that §2's reset is what actually
  fixed the doubling.

The parity ledger — what is a faithful benilla port and can be trusted as ground truth:
`visible_geosets` (players **and** humanoid NPCs), the type-aware texture resolution
(1/2/6/7/8 for characters, 1/11/12/13 for creatures), the 256×256 atlas rects and equip-order
compositing, the M2 Y-up conversion and free-inverse-bind skinning, and the
`SMSG_MONSTER_MOVE` spline decode.

---

## 4. Ground-truth facts worth pinning

- **Model-to-world basis is explicit for a character**: `(x,y,z) -> (-z,-x,y)`, and
  **heading = Yaw + 90°**. Doodads look basis-free only because ADT placement carries the
  flip; a character has none. Confirmed on screen — do not revisit (handbook §3.7).
- **`skinSectionId = group*100 + variant`**; ears default to variant **2** (702).
- **`ItemDisplayInfo.dbc` is 23 fields / 92 bytes**; texture slot → region is
  0 ArmUpper, 1 ArmLower, 2 Hand, 3 TorsoUpper, 4 TorsoLower, 5 LegUpper, 6 LegLower, 7 Foot.
- **Helm hair suppression** keys on `helmetGeosetVis1 != vis2` = a closed helm; the scalp dome
  is baked into each hair geoset, so hiding hair for an *open* helm leaves a hollow above the
  face.
- **BLP 1-bit alpha is not always 0..255.** Some decode to 0-or-1, i.e. 0.004 in the shader,
  which fails every alpha cut and renders a fully-textured surface as nothing. Guarded at use
  in `CharacterRenderer` and `AttachedItemRenderer`; the real fix belongs in `BlpDecoder` and
  is not done (handbook §3.19).

---

## 5. How to test

1. **Log in.** Your character must show **one** head of hair over the correct dark hair, a
   single scalp, and bracers that flare toward the elbow. No chainmail in the hair. This is the
   §2 regression check — if a second, pale, "extra" hair layer or a warped forearm returns, the
   reset was skipped or a new appending builder was added without extending `ResetModelState`.
2. **Capture.** Character panel → *Capture diagnostics → file* → open `msui-character-diag.txt`
   next to the client. "visible geosets N/total" should read a **single model's** worth (≈18
   for a dressed human, not double), and the UV-probe list should contain **no** duplicate
   category-0 hair submeshes or a stray `geo701`.
3. **Stormwind guards.** One hairstyle, one beard, sleeves/boots correct — not every variant
   stacked. If a guard renders as a clipping mess, the `CreatureRenderer` geoset filter is off
   or its fail-safe fired (0 matches → drew all); the console prints the match count.
4. **Beasts move and are textured.** A critter/beast should walk its `SMSG_MONSTER_MOVE`
   spline with the right skin variation, every submesh drawn.
5. **Bind pose split.** If something looks wrong, tick *Bind pose* — if it is correct there and
   wrong in motion, it is animation/bones (§1.6), not placement or geosets.

---

## 6. Debt / not done yet

- **Narrow the head bare-skin override.** It was broadened to `Fill == BodySkin || Type == 1`
  while chasing §2. Now that the real cause is fixed, the `|| Type == 1` half is likely
  unnecessary; narrow it back to `Fill == BodySkin` and confirm the head is unchanged (an A/B
  worth doing deliberately, not blind).
- **Confirm `AttachedItemRenderer` is reset-safe on reload.** `_attached` persists across a
  `Load` and is rebuilt via `Rebuild(Equipment)`; verify that path clears its own per-model
  state so a helm/shoulder cannot double the way the body did in §2. If it appends, it needs
  the same treatment.
- **Verify the hairline fix on other races.** The scalp strips are now composited for everyone;
  spot-check a Dwarf/NightElf/Undead male and a female of each, since their Hair rows author
  different Scalp sheets and a few have none.
- **Custom-MPQ gear is not loaded**, so a character shows base equipment, not the full
  transmog set — expected, not a bug (Nico: *"we dont load custom mpqs ergo why you dont see my
  full gear"*).
- **Other players are not yet drawn; player movement is not yet sent; combat is not wired.**
  All depend on the entity stream in §1.8 maturing.
- **Creature death-hold and clip cross-fade** are deferred — units snap between clips rather
  than blending, and a dead unit does not hold its final frame.

---

## Sources

- Handbook ground truth: `PROJECT_HANDBOOK.md` §3.4–3.19 (this doc's extraction source),
  §3.20 MPQ access, §11 working agreements.
- Reference: benilla `benilla-formats/characters/geosets.rs` (`visible_geosets`),
  `benilla-formats/creatures.rs`, the M2 skinning/animation modules.
- Code: the Owner files listed in the header.
- The §2 capture that proved it: `bin/Debug/net8.0/msui-character-diag.txt`.
