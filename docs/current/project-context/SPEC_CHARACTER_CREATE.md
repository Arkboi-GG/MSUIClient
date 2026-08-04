# Create New Character - build spec

Scope: the 1.12 **Create New Character** screen (opened by the "Create New Character" button on
char-select).

**STATUS 2026-07-29: BUILT.** This was written as a spec before any of it existed; the screen now
ships. It stays here as the AUTHORED-LAYOUT reference - every offset with its benilla citation, which
is still the thing to check a number against. For what actually shipped, the handful of places the
build diverged from this spec, and the live dial bank that replaced its hardcoded offsets, read
`SYSTEM_CHARACTER_SELECT.md` sections 9 and 11.

It is derived from benilla (the byte-faithful reference) on the device at
`C:\Users\nico\Desktop\benilla-main`; every non-obvious claim cites the benilla file + line it came
from so it can be checked, not taken on faith. benilla in turn transcribes the 1.12 client's
`CharacterCreate.xml` / `CharacterCreate.lua` and the `GetAvailableRaces`/`GetClassesForRace` engine
calls; where benilla notes a wow-re decompile address, it is carried through below.

Reference index (all under `benilla-main/crates/benilla/src/`):
- `char_create/mod.rs` - state, input, flow, name entry, the create request, result handling.
- `char_create/screen.rs` - the authored layout (every offset/size).
- `char_create/panels.rs` - the right-hand info stack.
- `char_create/parts.rs` - the component vocabulary (markers).
- `char_create/refresh.rs` - the per-frame systems that repaint selection state.
- `glue/art.rs` - the glue art table + the colour/font constants.
- `glue/widgets.rs` - `abs()`, `glue_button`, `icon_button`, `dial_arrow`, `glue_edit_box`.

Coordinate convention: the glue engine renders a **1024x768 virtual screen scaled to the window**;
every offset/size below is the reference number times `s = windowHeight / 768` (screen.rs:1-6). MSUI
already uses the same `s = height/GlueCanvasH` convention in `DrawCharacterSelect`, so these numbers
port directly.

---

## 1. The screen at a glance

A full-bleed screen over a per-race 3D booth (the selected race's `UI_<Race>` scene with the
character standing in it - the SAME GlueBooth machinery char-select uses, but fog ON for create -
benilla glue_booth.rs:476-479). Four clusters over it (screen.rs:8-15, 107-232):

- **Left**: the configuration tower (206x600 at TOPLEFT (28,74)) - faction-bannered race grid, the
  gender pair, the valid-classes-only class grid, the five appearance dials, and Randomize.
- **Center**: the model, full-height, drag- or button-rotatable.
- **Right**: three info panels (Faction / Race / Class, 240 wide at TOPRIGHT (-20,-20)), tinted per
  faction, quoting the GlueStrings paragraphs.
- **Bottom**: the NAME edit box centered low, the rotate pair bottom-left, Accept-over-Back
  bottom-right.
- WoW logo TOPLEFT (3,7) 256x128 (screen.rs:204).

Backdrop behind it all: `BACKDROP` = srgb(0.05,0.06,0.08), shifting to (0.05,0.06,0.10) Alliance /
(0.09,0.05,0.05) Horde (art.rs:26-28).

---

## 2. Selection state (the model behind the screen)

`CreateSelection` (mod.rs:240-251):

| field | type | notes |
|---|---|---|
| `race` | u8 | ChrRaces id (1..8) |
| `sex` | u8 | 0 male, 1 female |
| `class` | u8 | ChrClasses id |
| `dials` | [u8; 5] | skin, face, hairStyle, hairColor, facialHair (indices) |
| `name` | String | typed name |
| `creating` | bool | a create is in flight (Accept disarmed) |

- **reset** (mod.rs:262-271, on screen enter): race=1 (Human), sex=0 (male), class = first valid
  class for Human, dials=[0;5], name cleared, creating=false.
- **clamp** (mod.rs:274-289, after any race/gender change): if the current class isn't valid for the
  race, snap to the race's first valid class; clamp each dial into the new (race,sex) range
  (`dial >= count` -> `count-1`).
- **dial_counts(race,sex)** (mod.rs:252-260) = `[skin, face, hair_style, hair_color, facial_hair]`
  from the `CharCreate` catalog's `ranges(race,sex)`; `[1;5]` if the catalog is missing (so the UI
  still renders, degenerate).
- **request()** (mod.rs:306-318) -> `CharCreateReq { name, race, class, gender, skin, face,
  hair_style, hair_color, facial_hair }` = the CMSG_CHAR_CREATE payload.

---

## 3. Colours + fonts (art.rs:19-42)

| name | value | used for |
|---|---|---|
| GOLD | srgb(1.0, 0.78, 0.0) | titles, headers, name label, race/faction headers |
| INFO_TEXT | white | info-panel bodies, dial labels |
| DIM | srgb(0.5, 0.5, 0.5) | status line |
| BACKDROP | srgb(0.05,0.06,0.08) | page behind the booth |
| ALLIANCE_BORDER / _FILL | srgb(0.5,0.5,0.5) / srgb(0.09,0.09,0.19) | name box + Alliance panel tints |
| HORDE_FILL | srgb(0.19,0.05,0.05) | Horde panel tint |
| BTN_BG / BTN_HOVER | srgba(1,1,1,0.05) / (…,0.14) | no-art fallback button faces |

Fonts (FRIZQT unless noted; every glue string carries the black drop shadow via `outlined_text`):
GlueFontNormal 15, GlueFontNormalLarge 18, GlueFontNormalSmall 12, GlueFontHighlightSmall 12,
GlueFontCharacterCreate 12 (white), GlueFontDisableSmall 12. The name **edit box** types in
`GlueEditBoxFont` = **ARIALN**, not FRIZQT (screen.rs:112, 524).

MSUI mapping: GOLD = `WowSkin.GlueGold` (1,0.78,0) - exact match. INFO_TEXT = `WowSkin.Normal`
(white). DIM = `WowSkin.Disabled` (0.5,0.5,0.5) - exact match. Faction tints are new constants.

---

## 4. Left tower (screen.rs:200-470)

Tower node: 206x600 at TOPLEFT (28,74) (screen.rs:216-222). Children, each at its authored offset:

**Frame art** (screen.rs:228-247): `UI-CharacterCreate-Background` stretched at (-3,0) 218x680;
three `OuterBorder` 3-slice pieces at x -9, width 224, heights 236/240/210 at tops 0/236/476 (with
the listed texcoords).

**Banners** (screen.rs:249-254): `CharacterCreateBanners` 256x259 at (-27,60), behind the race grid.

**Faction headers** (screen.rs:256-280): "Alliance" / "Horde" - GlueFontNormal 15 GOLD, centered at
x 51 / 151, y 40. These are **GlueStrings keys** rendered mixed-case, never the raw ALL-CAPS key.

**Race grid** (screen.rs:282-305): two columns of **48x48** check-buttons, col A left 33, col B left
127, top 68, row gap 5 (pitch 53). Column contents (mod.rs:47-49, pinned by a regression test at
mod.rs:625-645):
- Alliance (col A): `[1, 3, 4, 7]` = Human, Dwarf, Night Elf, Gnome.
- Horde (col B): `[2, 5, 6, 8]` = Orc, Scourge, Tauren, Troll.
- Order = `GetAvailableRaces()` engine order (Alliance then Horde, ascending race id) - NOT the
  `RACE_ICON_TCOORDS` table order (that is a name->UV lookup that never reaches layout).
- Icons from `UI-CharacterCreate-Races` (4x4 cell sheet); the (race,sex) cell picks female = row+2
  (art.rs `race_cell` / `race_tc`, art.rs:395-420).

**Gender pair** (screen.rs:307-345): a 2-button row at (53,303), gap 5. Male / Female icon buttons;
icon from the gender sheet's left/right half (`[0,0.5]` male, `[0.5,1]` female).

**Class grid** (screen.rs:347-385): 3-wide grid at (27,369), col gap 4 (col x 27/79/131), rows
touching, **8 slots**. Each slot is `CreateAction::ClassSlot(slot)`; the slot->class mapping is the
selected race's valid-class list (mod.rs:341-345, `race_classes` = `classes_for_race` ascending class
id = the ref's `GetClassesForRace` / CharBaseInfo order). Slots past the race's class count collapse
(hidden), exactly like the ref's enumerate-then-hide. Icons from `UI-CharacterCreate-Classes`
(art.rs `class_tc`, art.rs:423-436).

**Appearance dials** (screen.rs:387-403, dial_row 405-490): a column of **5** spinner rows at
(4,480), width 198, each row 198x32. A dial row (screen.rs:405-490):
- `CharacterCreate-LabelFrame` 128x64 art as a 25|78|25 horizontal 3-slice (Left 25 at x -5, Middle
  stretched, Right 25 ending x 154), overhanging the 32-tall row (top -16, height 64).
- A per-race **label** centered on the frame middle, GlueFontHighlightSmall 12 INFO_TEXT
  (`DynText::DialLabel`). Labels come per-race from the ChrRaces customization tokens (e.g. "Skin
  Color", "Face", "Hair Style", "Hair Color", "Facial Hair"/"Piercings"/"Features" depending on
  race/sex).
- A **32x32 arrow pair** on the right: `<` at x 137 (`Dial(dial,-1)`), `>` at x 166 (`Dial(dial,+1)`)
  - `Glue-Left/RightArrow-Button` art.
- The five dials are, in order: **skin, face, hair style, hair color, facial hair** (mod.rs:292-304,
  `look()` maps dials[0..5] in that order).

**Randomize** (screen.rs:410-432): a 146x30 Small glue button at (30,645), 5px below the dial column
(the ref re-anchors it under the last dial on every race change).

---

## 5. Center: the model preview

The whole page IS a fullscreen booth (screen.rs:150-198): the per-race `UI_<Race>` scene with the
character standing in it, rendered into the window-sized target. In MSUI terms this is the SAME
`GlueBooth` used for char-select, driven by the create selection instead of a roster row - build the
model from `look()` (race/sex/class + the 5 dials). Notable: the **class** dresses the model in the
(race, class, sex) **starting outfit** (mod.rs:292-296, decision 0527), so a class change re-bakes
the preview.

Facing/rotation (mod.rs:47-49, 519-558):
- `INITIAL_FACING = -15 deg`, applied on enter and **reset on every race switch**.
- Drag anywhere on the model pane to rotate: `yaw += cursorDeltaX * 0.01` (~0.6 deg/px; the ref's
  full-frame `CHARACTER_ROTATION_CONSTANT`, magnitude unverified).
- Hold a rotate button: `ROTATE_RATE = 120 deg/s`; **left decrements** the facing, right increments.

---

## 6. Right info stack (panels.rs)

240 wide at TOPRIGHT (-20,-20); three panels stacked, gap 10 (panels.rs:22-43):

| panel | height | content |
|---|---|---|
| Faction | 160 | faction title + paragraph |
| Race | 260 | race title + paragraph + racial abilities (gold) |
| Class | 210 | class title + paragraph |

Each panel (panels.rs:44-173): a `TextPanel-Border` backdrop whose **bg tints with the faction**
(ALLIANCE_FILL / HORDE_FILL - the ref's `SetBackdropColor`), a 48x48 header icon overhanging the
top-left corner at (-3,-8), a title (GlueFontNormalLarge 18 GOLD at (17,10) width 190), and a
scrollable body (GlueFontCharacterCreate 12 white) inset (8,4,4). The Race panel additionally shows
the **racial abilities** in gold (GlueFontNormalSmall 12) - the ref's separate
`CharacterCreateRaceAbilityText`. Each panel has a right-side scrollbar
(`UI-CharacterCreate-ScrollBar`, panels.rs:190-260). All body text is quoted from **GlueStrings**
(the faction/race/class paragraphs), never embedded.

---

## 7. Bottom: name, rotate, Accept/Back

**Name cluster** (screen.rs:492-560), centered, bottom 50:
- "Name" label - GlueFontNormalLarge 18 GOLD.
- Edit box 156x40, `Glue-Tooltip-Border` chrome, **always Alliance-tinted** (the ref's OnLoad),
  TextInsets left 15, typed in ARIALN. Caret blinks 0.5 s on / 0.5 s off (mod.rs:368-398).
- Status line beneath - GlueFontDisableSmall-ish 13 DIM, **empty until a create fails** (never an
  idle hint); the ref surfaces errors in a dialog, this line is the minimal stand-in.

**Rotate pair** (screen.rs:562-640): two 50x50 buttons at BOTTOMLEFT (237,0), overlapping -19,
`UI-RotationRight-Big` art (left button mirrored), `UI-Common-MouseHilight` glow on hover.

**Accept over Back** (screen.rs:178-206), bottom-right column at (-50,20), gap 5:
- Accept: 160x35 Normal glue button, label GlueStrings `CHARACTER_CREATE_ACCEPT` ("Accept").
- Back: 120x30 Small glue button, label GlueStrings `BACK` ("Back").

---

## 8. Input + flow (mod.rs:400-508)

Click dispatch (mod.rs:415-472) - one `CreateAction` component covers every control:
- **Race(r)**: always play `gsCharacterCreationClass`; on a real change set race, reset class to the
  race's first valid, clamp, reset facing to -15 deg, re-bake.
- **Gender(g)**: sound `gsCharacterCreationClass`; on change set sex, clamp, re-bake.
- **ClassSlot(slot)**: resolve slot -> class via `race_classes[slot]`; sound
  `gsCharacterCreationClass`; on a real change set class, re-bake (new starting outfit).
- **Dial(dial,dir)**: sound `gsCharacterCreationLook`; `cycle_dial` wraps within `0..count`
  (`rem_euclid`); re-bake.
- **Randomize**: sound `gsCharacterCreationLook`; set every dial to a random valid index; re-bake.
- **Create**: fire the create (below).
- **Back**: sound `gsCharacterCreationCancel`; -> CharSelect.

Name typing (mod.rs:474-496): **ASCII alphabetic only, max 12 chars**; Backspace pops. **Enter /
NumpadEnter** = Create; **Escape** = Back.

Create (mod.rs:498-508): if not already `creating`, set `creating=true`, sound
`gsCharacterCreationCreateChar`, send `CharRequest::Create(request())` = CMSG_CHAR_CREATE.

Result (mod.rs:559-588): on the `SMSG_CHAR_CREATE` reply, if code == `CHAR_CREATE_SUCCESS (0x2E)` ->
re-enumerate the roster (phase 1) and return to CharSelect **with the new row armed**; otherwise
write the mapped 1.12 GlueStrings error into the status line and clear `creating`.

Result codes (mod.rs:592-616, verbatim from `GlueStrings.lua`; codes are the vmangos `ResponseCodes`
enum, anchor `CHAR_CREATE_SUCCESS = 0x2E`):

| code | text |
|---|---|
| 0x2E | Character created (success) |
| 0x2F | Error creating character |
| 0x30 | Character creation failed |
| 0x31 | That name is unavailable |
| 0x32 | Creation of that race and/or class is currently disabled. |
| 0x33 | You cannot have both a Horde and an Alliance character on the same PvP server |
| 0x34 | You already have the maximum number of characters allowed on this realm. |
| 0x35 | You already have the maximum number of characters allowed on this account. |
| 0x45 | Enter a name for your character |
| 0x46 | Names must be at least 2 characters |
| 0x47 | Names must be no more than 12 characters |
| 0x48 | Names can only contain letters |
| 0x4A | That name contains profanity |
| 0x4C | You cannot use an apostrophe as the first or last character of your name |
| 0x4E | You cannot use the same letter three times consecutively |
| 0x4F/0x50 | space-position / consecutive-space errors |

(Full list mod.rs:592-616; unknown codes fall back to "Invalid character name".)

Sounds (GlueSound keys, mod.rs): `gsCharacterCreationClass` (race/gender/class),
`gsCharacterCreationLook` (dial/randomize), `gsCharacterCreationCancel` (back/esc),
`gsCharacterCreationCreateChar` (create).

---

## 9. Data dependencies

- **`CharCreate` catalog** (benilla `crate::entities::CharCreate`) - the per-race/sex ranges + valid
  classes. Backed by:
  - `CharBaseInfo.dbc` -> `classes_for_race(race)` / `allows(race,class)` (which classes each race
    may be, ascending class id).
  - dial counts `ranges(race,sex)` = distinct-value counts of `CharSections.dbc`: skin =
    SECTION_SKIN colors, face = SECTION_FACE variations, hairStyle = `CharHairGeosets.dbc` styles,
    hairColor = SECTION_HAIR colors, facialHair = `CharacterFacialHairStyles.dbc` count. (MSUI
    already parses CharSections, CharHairGeosets, CharacterFacialHairStyles for char rendering -
    Formats/CharacterGeosets.cs + CharacterRenderer's CharSections load - so the counts are derivable
    from tables already in the client.)
- **ChrRaces.dbc** - the dial **label** tokens per race/sex, faction, model.
- **GlueStrings** (`Interface\GlueXML\GlueStrings.lua`) - faction/race/class info paragraphs, race
  ability text, button labels, the result-code error strings.
- **Art** (all under `Interface\Glues\CharacterCreate\` unless noted): `UI-CharacterCreate-Background`,
  `OuterBorder`, `CharacterCreateBanners`, `UI-CharacterCreate-Races`, `UI-CharacterCreate-Classes`,
  gender sheet, `CharacterCreate-LabelFrame`, `Glue-Left/RightArrow-Button-*`, `UI-RotationRight-Big`,
  `UI-Common-MouseHilight`, `TextPanel-Border`, `UI-CharacterCreate-ScrollBar-*`, and (already loaded
  for char-select/login) `Glue-Tooltip-Border`, the glue panel buttons, and the WoW logo.
- **Net**: `CMSG_CHAR_CREATE` (the `request()` payload) out; `SMSG_CHAR_CREATE` (1 result byte) in;
  on success, re-run the existing char-enum (SMSG_CHAR_ENUM) path.

---

## 10. MSUIClient implementation notes (what we have vs. what's new)

Already in the client (reuse):
- `GlueBooth` / `GlueScene` - the per-race booth + model. Create uses fog ON (vs char-select's fog
  OFF) - one flag (benilla glue_booth.rs:476-479). The model is built from `look()` exactly like a
  roster pick, plus the class-driven starting outfit.
- `WowSkin` (glue art table + `GlueImage`/`GlueButton`/`DrawBackdrop`), `GlueText` (with the drop
  shadow), the login edit-box widget (`LoginField`) - the name box is the same chrome.
- CharSections / CharHairGeosets / CharacterFacialHairStyles parsers (dial counts) and the character
  build pipeline (the preview model).
- The per-guid character **cache** pattern (GlueBooth) - the create preview re-bakes on every
  look/class change, so it does NOT cache; it rebuilds one live model in place.

New work:
1. A `CharCreate` catalog: `classes_for_race` (CharBaseInfo.dbc) + `ranges(race,sex)` (the five dial
   counts from the section/geoset tables). CharBaseInfo.dbc is a new parser; the rest reuse existing
   tables.
2. The race/class/gender **icon sheets** (register the 3 BLPs in WowSkin, with the frozen texcoord
   tables from art.rs `race_tc`/`class_tc`/gender halves).
3. The screen layout (§4-§7) drawn in the same immediate-mode style as `DrawCharacterSelect`
   (ImGui draw list + WowSkin), scaled by `s`.
4. Name entry (12-char ASCII-alpha, caret blink) + the appearance-dial controls (5 spinners with
   per-race labels, wrap on cycle, Randomize).
5. Net: `CMSG_CHAR_CREATE` build/send + `SMSG_CHAR_CREATE` result handling (§8 code table) -> status
   line, and on success re-enum + back to select with the new character armed.
6. Wire the char-select "Create New Character" button (currently disabled) to open this screen, and
   its Back/Accept to return.

Suggested phase split: (a) catalog + icon sheets + static layout (no interaction); (b) selection
(race/gender/class/dials/randomize) driving the live preview; (c) name + net create + result; (d)
the right info panels (GlueStrings paragraphs) and scrollbars; (e) sounds + the rotate cluster.

---

## 11. Open questions / confirm-before-build

- **Dial label source**: benilla pulls per-race dial labels from ChrRaces customization tokens.
  Confirm MSUI reads those, or hardcode the common set (Skin / Face / Hair Style / Hair Color /
  Facial Hair) and special-case the races that rename dial 5 (e.g. "Features"/"Piercings"/"Markings").
- **Right info paragraphs**: these are long GlueStrings blocks. Confirm we ship `GlueStrings.lua`
  parsing (benilla does) or defer the paragraphs to a later pass (panels render empty-but-tinted
  first, like the ref before strings load).
- **CharBaseInfo.dbc**: new parser needed for valid-classes-per-race; verify its 1.12 field layout
  (race u8, class u8 pairs) before wiring the class grid.
