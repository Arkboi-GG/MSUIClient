# Port session 2026-07-30 — wound-on-entry root fix, portrait circle mask, action-bar state pass, corpse looting

Four gameplay-UI defects/gaps from SYSTEM_GAMEPLAY_UI.md's live-validation table worked
this session, all cross-read against benilla (reference paths cited inline in the code).
**Nothing here has been compiled** — there is still no .NET SDK reachable from the
assistant sandbox (dotnet mirrors 403). Every change passed a structural syntax check
plus an adversarial symbol-by-symbol review against the tree, but **build first**:

```powershell
dotnet build MSUIClient.sln -c Debug
```

This session the device write-bridge held, so everything was written straight into the
repo (no chat-download manifest needed).

---

## 1. World-entry wound reaction — root cause found and removed

`Program.Casting.cs ApplySpellImpact` synthesized a wound (`TriggerCombatReaction`)
whenever a spell's impact kit had **no** authored animation — backwards twice over. The
server casts LOGINEFFECT (spell 836) on every player at world entry; its impact kit has
no anim id, so the very first thing the character did after the curtain was flinch.

Reference law (benilla `creature_anim/driver/wound.rs:14-15`, `net/apply/combat_log.rs`):
a spell impact animates its victim **only** through the kit's own authored anim id, and
only the CombatWound family (8 StandWound / 9 CombatWound / 10 CombatCritical) routes to
the wound reaction. Damage logs and health deltas never animate.

Now: no kit anim → nothing; anim 8/9/10 → wound; any other authored id → one-shot.
Applies to the player and creatures symmetrically (the creature "else → wound" fallback
had the same bug).

**Test:** enter world → no flinch. Take a real melee hit → wound still plays (that path
is `SMSG_ATTACKERSTATEUPDATE` → `ApplyCombatAnimation`, untouched).

## 2. Portraits — the square-bake presentation defect

benilla's portrait research (`portrait/mod.rs`, `UnitFrames.xml:20-23`) settles the
presentation question: the ring chrome is a thin band with **transparent corners**; it
never hides a square image. The reference cuts the portrait to the inscribed circle in
its UI shader. ImGui has no per-image mask and `AddImageRounded` is banned (single-fan-
triangle backend bug, the captured wedge), so the disc is now cut into the baked texture
itself: `PortraitRenderTarget.ApplyCircularMask()` zeroes alpha outside the inscribed
circle after a successful bake (CPU pass over the 256² readback, once per bake). Both
player and target bakes call it after `Analyze()` passes.

Also:
- Booth clear colour now matches the reference exactly — opaque `(0.055, 0.045, 0.04)`
  (was BGR-swapped); `Analyze()` thresholds updated to the new clear bytes.
- A degenerate authored camera (eye == target — would normalize a zero forward into
  NaNs) now falls back to bounds framing, both player and creature paths.
- The undead temporary-portrait art token is `Scourge`, not `Undead`
  (`TemporaryPortrait-Male-Scourge.blp`); fixed in `DrawUnitPortraitImage`.

**Test:** live screenshot of player + creature portraits. Check specifically: no black,
no wedge, no stand-in when the model is loaded, and **no square corners past the ring**.

## 3. Action bar — the reference button-state pass

`Program.ActionBars.cs` now implements the transcription of `ActionButton_UpdateUsable`
/ `UpdateState` / `UpdateFlash` (benilla `ActionBar.xml:194-459`, `ui_action/state.rs`,
`usable.rs`):

- **Usability tri-state**: usable → icon+ring white; not-enough-power → icon+ring
  `(0.5,0.5,1)`; unusable (dead / item count 0) → icon `(0.4,0.4,0.4)`, ring reset white.
  Power law is benilla `usable.rs:172-186`: cost = ManaCost + base·ManaCostPercent/100,
  base = `UNIT_FIELD_BASE_MANA` (new accessor, index 162) for mana, MAXPOWER otherwise.
  The other eleven usable gates (reagents, stances, aura states…) remain future slices.
- **Range**: hotkey text `(1.0,0.1,0.1)` red out of range, grey `(0.6,0.6,0.6)` in.
  `SpellRange.dbc` is now loaded (`SpellCatalog`, spell field 36 → new `RangeIndex`);
  melee rows use edge-to-edge reach `max(selfReach+targetReach+1.3333, 5.0)` via the new
  `UNIT_FIELD_COMBATREACH` accessor (130); ranged rows widen both ends by the reaches,
  the min only when non-zero; no row / (0,0) row / no target never reddens.
- **Textures**: pushed `UI-Quickslot-Depress` (mouse or bound key held), hover
  `ButtonHilight-Square`, current/auto-repeat `CheckButtonHilight`, attack/auto-repeat
  flash `UI-QuickslotRed` on the reference 0.4 s show/hide toggle, equipped-item border
  `UI-ActionButton-Border` 62×62 tinted `(0,1,0,0.35)`, and the empty-slot ring swaps to
  `UI-Quickslot` while a payload is carried (grid-shown), else stays `UI-Quickslot2`.
- **Text**: hotkeys right-justified in the top corner with the black (1,1) shadow; ITEM
  actions show their bag stack count bottom-right (white, shadowed), summed across
  backpack + equipped bags.

**Test:** the eight-state exercise now in SYSTEM_GAMEPLAY_UI.md's checklist item 3.

## 4. Corpse looting — new system

New files `Net/LootState.cs`, `Program.Loot.cs`; loot opcode family added to
`Opcodes.cs` (264, 349–355, 357–358 — verified against benilla-protocol `opcode.rs` /
vmangos), senders on `WorldSession`/`NetworkClient`.

Wire (verified against benilla-protocol `messages/loot.rs`, which cites vmangos line
ranges): `CMSG_LOOT` = full u64 guid; `SMSG_LOOT_RESPONSE` = u64 guid, u8 lootType, then
either (lootType 0) u8 error, or u32 gold + u8 count + rows of
`u8 slot, u32 itemId, u32 count, u32 displayInfoId, u32 randomSuffix(0), u32
randomPropertyId, u8 slotType`; `CMSG_AUTOSTORE_LOOT_ITEM` = u8 **wire** slot;
`CMSG_LOOT_MONEY` empty; `CMSG_LOOT_RELEASE` = u64 guid.

Client invariants copied from benilla `ui_loot.rs`:
- exactly one session; `Open` replaces wholesale and disarms any stale auto-release;
- rows keep their wire slots forever (removal never renumbers → autostore stays correct);
- auto-release arms **only on the transition to empty** (an empty-at-open window stays
  up); the client is the one that closes+releases when the last row leaves;
- release-response clears are guid-matched and idempotent (the corpse-switch race);
- the icon never waits on the template round-trip — it resolves straight from the wire
  `displayInfoId` through `ItemDisplayInfo.dbc` (`ItemTemplateCache.IconForDisplay`);
  names/quality colours fill in when `SMSG_ITEM_QUERY_SINGLE_RESPONSE` lands.

Interaction: dead units are pickable again (`PickUnit` no longer skips corpses — that
skip also broke keeping a dead target selected, which the target frame's "DEAD" line
already expected); a dead selection is kept, only despawn clears it. Right-click routes
by classification (benilla `target/click.rs`): dead + `UNIT_DYNFLAG_LOOTABLE` (new
`Lootable` accessor, dynamic-flags bit 0x1 — per-viewer, the server strips it) → select
+ `CMSG_LOOT` + the kneel one-shot (anim 50); other corpses just select; live hostiles
begin the swing as before.

UI (LootFrame.xml transcription): 256×256 `UI-LootPanel` at the authored left seat,
`TargetDead` skull through the ring cut-out, "Items" title, rows of 37×37 icon +
`UI-QuestItemNameFrame` parchment + quality-coloured name + stack count, coin row at
display position 1 with the denomination icon (`INV_Misc_Coin_01/03/05`) and
"N Gold M Silver K Copper" text, 4 rows per page (3 + pager arrows when more), the
minimize-button close, hover highlight + item tooltip. Escape closes (before the game
menu — new leg in the Escape order), walking ~1.5 yd away releases, corpse despawn
releases. Loot refusals show the 1.12 error strings as red centre text;
`SMSG_ITEM_PUSH_RESULT` shows the green "You receive loot: [Name] xN." line once the
template resolves (~2 s budget). `SMSG_LOOT_MONEY_NOTIFY` is deliberately a no-op (the
purse rides `PLAYER_FIELD_COINAGE`).

Deliberately out of scope: corpse sparkle + loot cursor art, shift-click auto-loot (the
reference has no modifier handling either), group loot rolls, skinning, pickpocketing,
GameObject chest loot (`CMSG_GAMEOBJ_USE`, a different opcode family).

---

## Files touched

| File | Change |
|---|---|
| `Program.Casting.cs` | wound-on-entry root fix (impact anim law) |
| `Engine/PortraitRenderTarget.cs` | reference clear colour; `ApplyCircularMask()` |
| `Program.Portraits.cs` | mask calls; degenerate-camera fallbacks |
| `Program.UnitFrames.cs` | Scourge stand-in token; presentation comments |
| `Program.ActionBars.cs` | full button-state pass (tints, textures, flash, counts, range) |
| `Formats/SpellCatalog.cs` | SpellRange.dbc table; `SpellInfo.RangeIndex` (field 36) |
| `Net/ObjectFields.cs` | BOUNDINGRADIUS/COMBATREACH/BASE_MANA/DYNAMIC_FLAGS accessors |
| `Net/Opcodes.cs` | loot opcode family |
| `Net/WorldSession.cs`, `Net/NetworkClient.cs` | loot senders |
| `Net/LootState.cs` | **new** — session state + wire parse + error strings |
| `Net/Items.cs` | `IconForDisplay(displayInfoId)` |
| `Program.Loot.cs` | **new** — handlers, LootFrame UI, release logic, receive lines |
| `Program.Net.cs` | loot packet dispatch; `ResetLoot()` on enter-world |
| `Program.Targeting.cs` | corpses pickable; dead selection kept; right-click loot route |
| `Program.Settings.cs` | Escape order: cancel cast → close loot → game menu |
| `Program.CombatFeedback.cs` | `DrawLootFrame()` in the HUD pass |
| `SYSTEM_GAMEPLAY_UI.md` | Draft 5 status table + checklist |

## Known review notes

- The one review finding (oom ring not tinted) was fixed; ring now takes the blue tint.
- `SpellInfo` gained a trailing positional param; its single construction site was
  updated. If a branch adds another `new SpellInfo(...)`, it needs the 22nd arg.
- The kneel is a one-shot (plays once, ~0.5 s); the reference holds the kneel while the
  window is open. A hold mechanism (like `_spellHold`) is a possible polish follow-up.
- Loot-row count text uses the default font size (matches the bag windows' idiom).
