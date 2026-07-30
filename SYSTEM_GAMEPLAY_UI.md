# Gameplay UI — action bars, inventory, portraits and character panels

Status: Draft 6 — 2026-07-30 09:00 — corrective implementation checkpoint, live validation incomplete

This document records the implemented native-ImGui port of the build-5875 gameplay UI. The
authoritative research and exact FrameXML/wire citations remain in `PORT_GAMEPLAY_UI.md`; this file
is the concise implementation handoff. In this document, **implemented** means code and wire paths
exist; it does not mean the result has passed live visual or interaction validation.

## Live validation checkpoint

| Surface | Current status | Evidence / remaining problem |
|---|---|---|
| Integrated bottom action/micro/bag bar | **Mostly working visually; state pass added 2026-07-30, unverified live** | Layout acceptable. The reference button-state pass now exists in code: the three-way usability verdict (oom blue 0.5/0.5/1 on icon+ring, unusable grey 0.4 on icon only), out-of-range hotkey red (1,0.1,0.1) vs grey via SpellRange.dbc + combat-reach math, item stack counts, pushed/hover/checked textures, the 0.4 s attack/auto-repeat red flash, the equipped-item green ADD border, right-justified shadowed hotkeys, and the UI-Quickslot grid swap while a payload is carried. All of it needs a live pass. |
| Player/target portraits | **Reworked 2026-07-30; needs a live screenshot before any status change** | Root presentation defect identified from the reference: the ring chrome's corners are TRANSPARENT (a thin band), so a square bake can never hide behind it. Live portrait textures are now pre-masked to the inscribed circle at bake time (`PortraitRenderTarget.ApplyCircularMask`), replicating the reference's shader-side circular cut without `AddImageRounded`. Also: booth clear colour matched to the reference (0.055, 0.045, 0.04 opaque), degenerate authored cameras (eye == target) fall back to bounds framing instead of NaN, and the undead stand-in art token fixed to "Scourge". Do not mark portraits fixed without a new live screenshot. |
| World-entry combat animation | **Root cause found + fixed 2026-07-30, unverified live** | The flinch was client-made: `ApplySpellImpact` synthesized a wound reaction whenever a spell impact kit had NO authored animation, and the server casts the login visual (LOGINEFFECT, spell 836) on every player at world entry. Reference law (benilla wound.rs / combat_log.rs): spell impacts animate the victim only through the kit's own anim id, and only the CombatWound family 8/9/10 routes to a wound. The fallback is removed; authored non-wound impact ids now play as one-shots. Verify live: enter world, no flinch; then take a real hit and confirm the melee wound still plays. |
| Corpse looting | **Implemented 2026-07-30; entirely unverified live** | Full solo-loot slice: dead units are pickable again, right-click on a dead+`UNIT_DYNFLAG_LOOTABLE` creature sends `CMSG_LOOT` (kneel plays at send), `SMSG_LOOT_RESPONSE` (both shapes) / `LOOT_REMOVED` / `CLEAR_MONEY` / `RELEASE_RESPONSE` are handled with benilla's invariants (single session, wire slots never renumber, auto-release only on the transition to empty, guid-matched idempotent clears), and the authored LootFrame renders (UI-LootPanel, skull, quest-parchment name plates, quality colours, coin row by denomination, 4-row pages with the >4 pager, close button). Items leave via `CMSG_AUTOSTORE_LOOT_ITEM` with the wire slot; coins via `CMSG_LOOT_MONEY`. Escape and walking away release. `SMSG_ITEM_PUSH_RESULT` drives a green "You receive loot" line. Loot refusals surface the 1.12 error strings. |
| Spellbook and action slots | **UI present; live behavior unverified** | Learned-spell/action models, casting packets, bar state and visuals exist and compile. Casting, cancellation, targeting, cooldowns, action persistence and effect visuals still require live-realm verification. |
| Backpack and equipped bags | **UI present; live behavior unverified** | Backpack/bag drawing and move/use/equip packet paths exist. Opening, cross-container moves, equipment moves, item use, server reconciliation and relog persistence have not been signed off live. |
| Character/skills page | **Implemented; partially visually checked** | Genuine assets and data paths exist. Final compositing, paper-doll/portrait presentation, values and interaction still need a structured live pass. |
| Loading-screen HUD exclusion | **Implemented; not yet signed off here** | Gameplay ImGui is gated through curtain fade and the synthetic blue bar was replaced with the 1.12 fill/border assets. Keep this in the live checklist. |

The next gameplay-UI pass should begin from this table. A successful build, camera-frustum count, or
packet-law test is supporting evidence only; none may be promoted to **verified** without observing
the corresponding behavior in the running client.

## 09:00 corrective checkpoint

This section supersedes conflicting status statements in older `NEXT_*` research notes. The complete
conversation narrative and sign-off list are in `July-30-20206-9AM-HANDOFF.md`.

| Surface | Implemented correction | Benilla authority | Live boundary |
|---|---|---|---|
| Action, micro, bag, loot and cast additive art | `GameplayArt.AdditiveHandle` is used for authored ADD overlays; spell hover and cast spark no longer depend on alpha-compositing additive source pixels. | `ActionBar.xml` 588–641; `BagFrame.xml` 1233–1424; `CastingBarFrame.xml` 215–225; `MicroMenu.xml` | Verify no black rectangles or unreadable hovered icons. |
| Character micro portrait | Portrait is composited between the button face and additive overlay instead of behind the frame. | `MicroMenu.xml` 161–199, 244–257 | Verify visible portrait plus working Character button. |
| Backpack/bags | Authored hover/open checkbutton states and tooltips are wired; placement remains relative to scaled action-bar slots. | `BagFrame.xml` 643–680, 1233–1424 | Verify all UI scales and bag states. |
| Helpful cast target | Pure `Net/CastTargetLaw.cs` resolves masks; hostile selection falls back to self for a self-capable helpful spell, unsupported shapes are refused. | `ui_action/cast_target.rs` | Verify Holy Light with a hostile wolf selected heals the player. |
| Post-cast locomotion | `M2Animator.FindOrBake` obtains exact requested spell clips; exact player/creature spell paths do not use Stand as a missing-clip substitute. | `creature_anim/spell_visual.rs` 420–670 | Verify immediate movement-animation recovery after casting. |
| Character selection | `GameSettings.LastCharacterGuid` is saved and restored. | Direct product requirement; no Benilla equivalent is claimed. | Verify logout and return-to-select. |
| Enter World transition | `ArmEnterWorldCurtain` raises the loading cover on click, before asynchronous verification or HUD rendering. | 1.12 transition requirement plus existing world-ready gate | Verify no dialog/action-bar frame leaks. |

Later user direction is authoritative over `NEXT_02_TARGET_PLATE.md`: floating target plates are not
wanted. Keep reaction-aware overhead names; do not reintroduce selected-target plates.

## Implemented in the current tree

- Real 3D player and creature portraits render through depth-backed OpenGL targets inside the
  original `UI-TargetingFrame` PlayerFrame/TargetFrame geometry. Round portraits select the M2's
  authored `cameraLookup[0]` camera (including its static position/target tracks, roll, clip planes
  and diagonal-FOV projection); model-bounds framing is only the camera-less fallback. The character
  sheet uses a separate 466×448 full-body bake sampled into the original 233×224 model pane. The
  booth uses the ported neutral portrait light and no world fog. Player equipment changes invalidate
  both player bakes.
- Hovered and selected creatures receive the reference model-brightening lift, and a selected unit
  receives the real `Textures\\UnitSelectTexture.blp` reaction-colored ground ring.
- The main 12-button bar uses the original dwarf main-menu plate, end caps, Quickslot rings and
  DBC-resolved spell/item icons. Keys `1` through `=`, mouse activation, radial cooldown shading,
  spell and item execution, drag-swap, drag-off clear, and server persistence are connected.
- The bottom HUD is one continuous build-5875 bar: action slots occupy the authored left seat,
  the real Character/Spellbook/Talents/Quest/Social/World/Main Menu/Help micro-button art begins at
  logical x=552, and the four bag slots plus backpack occupy the authored right seat. Implemented
  panels open from both their keyboard shortcuts and matching micro buttons; unfinished panels keep
  their genuine disabled art rather than substitute controls.
- Gameplay UI is laid out on the original 1024×768 logical canvas. `GameplayUiScale()` derives the
  physical scale from the current framebuffer (constrained by both axes), so the bar, unit frames,
  inventory, spellbook, character/skills page and casting bar retain approximately the same fraction
  of the screen at 1920×1080, 2560×1440 and 3840×2160. The configured 1.8 UI scale is the neutral
  100% preference and remains an accessibility multiplier instead of a high-resolution size cap.
- `P` opens the original four-quadrant spellbook. Its contents come only from
  `SMSG_INITIAL_SPELLS`/learn/supercede state joined to `Spell.dbc`, `SpellIcon.dbc`,
  `SkillLine.dbc` and `SkillLineAbility.dbc`. Pages contain 12 real spells; spells can be cast or
  dragged to the action bar.
- `B` opens the 16-slot backpack. A permanent five-button bag bar opens the four streamed equipped
  bags. Bag windows use `UI-BackpackBackground` or stitched `UI-Bag-Components`, real item icons,
  stack counts and money. Click-carry supports backpack↔backpack, backpack↔bag, bag↔bag and
  equipment moves; right-click uses or auto-equips.
- `C` opens the 384×512 character panel. The paper-doll page uses the four Blizzard quadrants,
  real empty-slot art, live equipment icons, a rotatable dressed model, primary stats, armor,
  melee/ranged damage and attack power, and resistance icons. The Skills tab uses the original
  SkillFrame art and actual SkillLine names/categories/descriptions/ranks from DBC + descriptor
  state. `Ctrl+C` retains the developer collision toggle.
- Item templates are asked once with `CMSG_ITEM_QUERY_SINGLE`; icon resolution joins the response's
  display id to `ItemDisplayInfo.dbc` field 5. Live equipment is re-applied to the world character,
  unit portrait and paper-doll once every equipped template has resolved.
- Weapon presentation follows `UNIT_BYTES_2` and the item template's sheath tail. Main hand,
  off hand, shield and ranged models use the M2's authored hand/back/lower-back/hip attachment
  points. `Z` sends `CMSG_SETSHEATHED`; animation 89/90 plays before the model relocates. Streamed
  humanoid NPCs use the same path from their three `UNIT_VIRTUAL_ITEM_*` slots.
- Spell presentation follows START/GO/failure/delay/channel packets and resolves
  `Spell.dbc -> SpellVisual.dbc -> SpellVisualKit.dbc -> SpellVisualEffectName.dbc`. It drives
  cast/hold/release animations, original CastingBar art, and real effect-M2 mesh and particle
  instances for precast, cast, missile and impact stages.
- Escape and movement cancellation use `CMSG_CANCEL_CAST`, `CMSG_CANCEL_CHANNELLING`, and
  `CMSG_CANCEL_AUTO_REPEAT_SPELL`. Auto-repeat and on-next-swing spells stay outside the ordinary
  pending-cast latch; ranged auto-repeat toggles off on a second press or Escape.
- Unit frames request the selected unit's real server name. Public 48-slot aura descriptors feed
  actual spell icons below TargetFrame and in the player's top-right aura row.
- Portrait framebuffer bakes explicitly suspend ImGui's screen-space scissor test. Without that
  isolation the last ImGui clip rectangle could reject the off-screen clear and model draw, leaving
  a valid portrait texture containing only black/transparent pixels.
- Portrait bakes now use the reference booth contract instead of photographing the live world
  transform: scale 1, origin-local coordinates and a frozen pose. The FBO establishes its complete
  raster state before clearing (colour/depth write masks, opaque blend state, LEQUAL depth and
  back-face/CCW culling), then synchronously validates that subject pixels were actually written.
  A blank authored-camera bake retries bounds framing; a still-blank result is rejected so the
  reference `TemporaryPortrait-*` stand-in appears instead of a black live texture. The failed FBO
  is saved under `portrait-diagnostics` beside the executable and its pixel range/camera/piece count
  is logged.
- `tools/portrait-camera-check` is the serverless MPQ-backed camera harness. On the installed data,
  DwarfMale's authored camera places 1,224 parsed vertices inside its clip volume, HumanMale 1,289,
  and Wolf 56. This pins the M2 camera parser/projection independently of OpenGL framebuffer state.
- Portrait consumers draw the complete baked texture quad beneath Blizzard's circular frame chrome.
  `ImDrawList.AddImageRounded` is not used: this ImGui.NET/backend combination emitted only one
  textured fan triangle, producing the captured face-shaped wedge even though FBO readback and the
  camera harness both proved the full portrait existed.
- The loading curtain is an exclusive presentation state. Gameplay unit frames, auras, action/bag
  bars, panels and developer ImGui windows remain hidden until world streaming and the curtain's
  fade-out have both completed.

## Protocol and descriptor laws

- Action buttons: 120 packed slots; `CMSG_SET_ACTION_BUTTON` is `{u8 slot,u32 packed}`. A swap sends
  both changed slots. Spell kind is `0x00`, item kind `0x80` in the high byte.
- Player-grid moves (equipment or backpack, wire bag 255) use `CMSG_SWAP_INV_ITEM` with source then
  destination slots. Any move touching an equipped bag uses `CMSG_SWAP_ITEM` with destination
  bag/slot first, then source bag/slot. Equipped bag ids are 19..22 and their content slots are
  zero-based.
- Equipment slots are descriptor positions 486+2*i; backpack slots 532+2*i; container contents
  50+2*i. Item instance fields provide stack, durability and flags; item names/classification come
  from the server query, not a nonexistent vanilla `Item.dbc`.
- Character stats use the exact build-5875 `UNIT_FIELD_STAT0`/resistance/damage/AP blocks and
  `PLAYER_FIELD_POSSTAT0`/negative-stat/resistance-buff blocks. Skills use the packed 128×3 dword
  block beginning at player field 718.

## Primary files

- `Program.GameplayLayout.cs`, `Program.ActionBars.cs`, `Program.Casting.cs`, `Program.Sheath.cs`,
  `Program.Spellbook.cs`
- `Program.Loot.cs`, `Net/LootState.cs` (solo corpse looting)
- `Formats/SpellCatalog.cs`, `Formats/SpellVisualCatalog.cs`, `Net/SpellPackets.cs`
- `World/Units/SpellEffectSource.cs`, `World/Units/SpellEffectMeshRenderer.cs`
- `Program.Inventory.cs`, `Net/Items.cs`, `Formats/DbcReader.cs`
- `Program.CharacterPage.cs`, `Formats/SkillLineCatalog.cs`
- `Program.Portraits.cs`, `Engine/PortraitRenderTarget.cs`, `Engine/UI/GameplayArt.cs`
- `Net/ObjectFields.cs`, `Net/Opcodes.cs`, `Net/WorldSession.cs`, `Net/NetworkClient.cs`

## Deliberate remaining work

The 2026-07-30 pass (portrait circular mask + booth parity, action-bar state tints/textures/flash,
the wound-on-entry root fix, and the complete solo-loot slice) is code-complete but has had NO live
run — it was authored and reviewed statically. Backpack/bag behavior and the complete spell/casting/
visual chain remain unverified. Effect-M2 mesh bone animation and particle tracks are implemented;
ribbon emitters remain a separate renderer type. Aura duration packets, tooltips and the self
insertion-order cache are not implemented. Broader work remains: complete item/stat/spell tooltips,
stack splitting and locked-slot prediction, bank/vendor/buyback, talents, macro/stance/multibar
pages, the remaining usability gates (reagents, stances, aura states), corpse sparkle + loot cursor,
group loot rolls, and a real chat frame for the receive/error lines now riding the center text.

## Verification

Run:

```powershell
dotnet build MSUIClient.sln -c Debug
dotnet run --project tools\combat-wire-check\MSUICombatWireCheck.csproj -c Release
dotnet run --project tools\portrait-camera-check\MSUIPortraitCameraCheck.csproj -c Release -- GameData\Data
```

Then verify live, recording pass/fail separately:

1. Enter the world without being attacked and confirm no wound/hit reaction plays during reveal —
   then take one real melee hit and confirm the wound reaction still plays.
2. Capture player and creature portraits; require a complete correctly framed model image, not black,
   a temporary stand-in, a partial triangle, or frame-background bleed-through. The bake is now
   circle-masked: specifically check no square corners poke past the ring.
3. Inspect the bottom bar over bright and dark terrain for shadow, colour, alpha and see-through
   errors. Exercise the new states: drain mana (icon+ring blue), select a far target (hotkey red),
   start auto-attack (red flash + checked), press-and-hold a key (depress), hover (highlight),
   drop an equipped-item action on the bar (green border), and carry an item (grid rings).
3b. Kill a mob, right-click the corpse: kneel, loot window with correct art/rows/quality colours,
   item click autostores (row leaves), coin click loots money, last row auto-closes and releases,
   Escape and walking away release, >4 drops paginate, and a second player's corpse refuses with
   the red error line.
4. Open `C`, `P`, and `B`; rotate the paper-doll and compare equipped icons/model.
5. Move one item backpack→bag→equipment→back, use one consumable, relog, and verify reconciliation.
6. Drag a spell to an empty action slot, relog, cast self/unit/channel/auto-repeat examples, cancel
   them, and verify cast bar, animation, cooldown and effect stages against server results.
