# UI Interface Proportions & Functionality — HANDOFF

## Mission

Get **every in-game interface (UI panel/frame) correctly proportioned** to the real 1.12 client, and
**verify each one actually works** (opens, shows real data, buttons/tabs/scroll respond). Two deliverables
per panel: (1) it looks right at any resolution, (2) it functions.

**Read this whole doc before touching anything.** Then work panel-by-panel, verifying each by eye against
the real 1.12 client / benilla — not by asserting from code. (Nico verifies by eye; the last big lesson on
this project was that "the data parses" ≠ "the picture is right." Do not claim a panel is correct without a
capture that shows it.)

## The reference is benilla's FrameXML

MSUI's UI is its **own ImGui-based reimplementation** of Blizzard's 1.12 FrameXML — it is NOT a copy. The
**authoritative source for correct proportions** is the benilla reference client (Rust) at
`C:\Users\nico\Desktop\benilla-main`, which ships the transcribed 1.12 FrameXML:

- `crates\benilla\assets\ui\*.xml` — 47 files with the authored 1.12 anchor/size numbers, each citing the
  extracted MPQ FrameXML line-by-line: `CharacterFrame.xml`, `SpellBookFrame.xml`, `ActionBar.xml`,
  `MicroMenu.xml`, `UnitFrames.xml`, `BagFrame.xml`, `MerchantFrame.xml`, `QuestFrame.xml`,
  `TalentFrame.xml`, `UiPanels.xml`, etc.
- benilla renderer (if you need behavior, not just numbers): `crates\benilla-ui\src\{framexml,layout}.rs`,
  `loader/`, `script/` per-frame handlers.

For any panel, **open the matching benilla `assets\ui\*.xml` and read the authored `<Size>`/`<Anchor>`
numbers** — those are the truth for MSUI's logical rects. The real Blizzard art itself is loaded live from
`interface.MPQ` in both clients, so textures already match; the gap is geometry.

Also useful, in-repo: `SYSTEM_GAMEPLAY_UI.md` (the gameplay-UI handoff; §"Live validation checkpoint" table
lists per-surface status — most are marked *implemented, live-unverified*), `PORT_GAMEPLAY_UI.md` (293 KB of
exact FrameXML/wire citations — read in slices), `SYSTEM_SETTINGS_UI.md` (the glue/backdrop story), and the
archived per-surface specs `docs\archive\NEXT_01..07_*.md` (the house handoff style: benilla file:line proof →
MSUI gap → concrete spec).

## How the UI is built (so you know what you're tuning)

Two rendering stacks, both drawing real `.blp` art out of the MPQ as textured quads on **ImGui draw lists**
(ImGui is used only for windowing/input hit-testing — these are NOT native ImGui widgets):

- **Glue/menu stack** (login, char-select, char-create, settings): `MSUIClient\Engine\UI\WowSkin.cs` —
  Blizzard nine-slice `<Backdrop>` system, `PanelButton`/`GlueButton`/`CheckBox`, header plaques. Backdrop
  edge/tile/inset constants transcribed from FrameXML (`WowSkin.cs:52-69`); texture paths in
  `WowSkin.Paths` (`WowSkin.cs:86-175`). Its doc block says these are Blizzard-verbatim — **do not tune
  by eye**, correct them against FrameXML.
- **Gameplay HUD stack** (in-world panels): the `Program.*.cs` partials + `Engine\UI\GameplayArt.cs`
  (texture cache) + shared widgets in `MSUIClient\Program.VanillaUi.cs` (`BeginVanillaWindow`,
  `VanillaButton`, `VanillaTab`, `VanillaCheckButton`, `DrawVanillaScrollBar`, `DrawFourPieceShell`,
  `VanillaInputText`, `DrawWrappedText`, `Program.VanillaUi.cs:19-373`).
- `Engine\UI\GlueAdditive.cs` — a separate raw-GL additive pass for glow quads (ImGui draw lists have no
  per-quad blend mode). Only used for char-select highlights.

### The single scale path — the heart of "correctly proportioned"

Every gameplay panel is authored on a **fixed 1024×768 logical canvas** and multiplied by one factor,
`GameplayUiScale()` in `MSUIClient\Program.GameplayLayout.cs:14-20`:

```
resolutionScale = min(display.X/1024, display.Y/768)      // constrained by BOTH axes
preference      = clamp((skin.Scale ?? 1.8)/2.0, 0.5, 2)  // captured 1.12 uses shipped 1.8 = 90%
return max(0.5, resolutionScale * preference)
```

- `1024f`/`768f` are the FrameXML logical canvas (`Program.GameplayLayout.cs:11,17`).
- The MSUI default UI scale **1.8 is the empirically matched 90% presentation** (`:18`); **2.0 is raw
  FrameXML 100%**. This was pinned by a same-resolution 2048×1152 spellbook/HUD A/B on 2026-08-04.
  Do not restore `1.8/1.8`: it makes every gameplay panel and tooltip about 10% too large.
- Panels get scale via `BeginVanillaWindow(...)` → `scale = GameplayUiScale()`, origin `logicalOrigin *
  scale`, size `logicalSize * scale`; default panel origin `(0,104)` logical (`Program.VanillaUi.cs:16-30`).
  Every widget multiplies logical px by `scale`.
- Bottom bar: `GameplayBarWidth=1024f`, `GameplayBarHeight=53f` (`:8-9`), centered + pinned to bottom
  (`GameplayBarMin`, `:22-24`).
- **37 files call `GameplayUiScale()`** — the full panel set (see inventory).

**Failure modes to look for (this IS the task):**
1. **Wrong logical rect** — a panel authored at guessed sizes, not benilla's FrameXML `<Size>`. Most common.
2. **Wrong anchor/offset** — panel not centered/positioned like 1.12 (see `GameplayBarMin` for the pattern).
3. **Mixed scales** — some code uses `GameplayUiScale()`, some uses raw `display.Y / 768f` directly
   (`Program.CombatFeedback.cs:264`) or raw pixels. Inconsistency warps a panel relative to its neighbors.
4. **HiDPI mismatch** — `io.DisplaySize` ≠ framebuffer size floats the whole cluster (flagged in
   `docs\archive\NEXT_03_BOTTOM_BAR_FLUSH.md`). The dump's `uiScale` block exists to settle this; check it.

### Dense hand-tuned files you'll be fighting
- `Program.CharCreate.cs` — a `CcTune` block of ~50 tunable floats with a live in-client tuning modal
  (`_ccTuneOpen`) and a `[cc-tune]` logger (`:1181-1240`). Heaviest.
- `WowSkin.cs` — backdrop/button/plaque constants (`:52-69,203-259,680-695`).
- Per-panel inline logical rects scattered in each `Program.*.cs` (e.g. action bar `0,715,1024,53`).

## The complete interface inventory

Each panel is a `Program.*.cs` partial with a `_<name>Open` flag. Group them and work top-down by
"how often the player sees it."

**Core HUD (always visible):** action bars + micro-menu + page arrows (`Program.ActionBars.cs`,
`Program.ActionIcons.cs`, `Engine\UI\ActionIconLaw.cs`), casting bar (`Program.Casting.cs`), unit frames +
portraits (`Program.UnitFrames.cs`), party frames (`Program.PartyFrames.cs`), pet frame (`Program.Pet.cs`),
minimap (`Program.Minimap.cs`, `Engine\UI\MinimapProjection.cs`), chat + floating combat text
(`Program.Chat.cs`, `Program.CombatFeedback.cs`), nameplates (`Program.Nameplates.cs`), rest/XP + reputation
bars (`Program.RestXp.cs`, `Program.Reputation*.cs`), tooltips/static-popups/UI-errors parity
(`Program.TooltipParity.cs`, `Program.StaticPopupParity.cs`, `Program.UiErrorsParity.cs`).

**Toggled windows (keybind/menu):** character paper-doll (`Program.CharacterPage.cs`, 384×512), inspect
(`Program.Inspect.cs`), spellbook (`Program.Spellbook.cs`), talents (`Program.Talents.cs`), quest log
(`Program.Quest.cs`), inventory/backpack/bags (`Program.Inventory.cs`), world map (`Program.WorldMap.cs`),
social/friends/who (`Program.Social.cs`), macro (`Program.Macro.cs`), keybindings
(`Program.Keybindings.cs`, `Program.Bindings.cs`), help (`Program.Help.cs`), settings/game-menu
(`Program.Settings.cs`).

**NPC/interaction windows:** gossip (`Program.Gossip.cs`), vendor/merchant (`Program.Vendor.cs`), trainer
(`Program.Trainer.cs`), professions/tradeskill (`Program.Professions.cs`), bank (`Program.Bank.cs`),
auction (`Program.Auction.cs`), mail (`Program.Mail.cs`), trade (`Program.Trade.cs`), taxi
(`Program.Taxi.cs`), tabard (`Program.Tabard.cs`), guild (`Program.Guild.cs`), loot (`Program.Loot.cs`),
gossip/unit right-click popup (`Program.UnitPopup.cs`), death/rez (`Program.DeathRez.cs`), hearth
(`Program.Hearth.cs`).

**Glue:** char-create (`Program.CharCreate.cs`), plus login/char-select (WowSkin/GlueScene).

Shared primitives for every panel: `Program.VanillaUi.cs`.

## How to verify — the loop

Everything is driven by the scripted live-run harness. **Command:**
```bash
dotnet run --project MSUIClient/MSUIClient.csproj -- MSUIClient/client-config.json --live-bootstrap --character Magetest --live-protocol scenarios/interfaces/<scn>.txt --out live-runs --timeout 170
```
`Magetest` is a level-60 GM mage with all spells + 1000g (already provisioned). Server: LAN VMaNGOS
(realmd `192.168.0.2:3724`). Window renders at **1600×900** (`client-config.json`); world-ready can take up
to ~180s.

**Two protocol steps do all the work** (`Program.LiveRun.cs`):
- **`panel <name>`** (`:514-540`) — closes all panels, then opens exactly one. Many use `Simulate*`
  handlers so the window fills with data **without a live NPC/server round-trip** (offline-friendly).
  Keywords: `character, spellbook, quest, social, who, worldmap, help, keybindings, macro, guild, auction,
  mail, profession, talent, trade, bank, trainer, taxi`. (Loot/vendor/gossip/tooltip/etc. open via their own
  gm/interact steps or flags — grep the switch for the current set.)
- **`dump <name>`** (`:672`) — writes `dumps/gameplay-<name>.json` **and** `dumps/gameplay-<name>.png` (a
  full-framebuffer screenshot). Read the PNG with your image tool and compare to the real 1.12 client.

Minimal example (this pattern already exists — `scenarios/night06/core-window-sweep.txt`):
```
panel character
wait 0.6
dump character
panel spellbook
wait 0.6
dump spellbook
```

**The machine-checkable proportion data is in the JSON** (`Program.DevTools.GameplayDump.cs`,
`BuildGameplayDump` `:95-268`):
- `scenario.uiScale` (`:206-212`): `effective = GameplayUiScale()`, `configuredPreference`, `framebuffer`,
  `displaySize` — this is the HiDPI/scale evidence. Confirm `framebuffer == displaySize`.
- `scenario.panelsOpen` (`:196-205`): which panels the frame thinks are open.
- `layout[]` (`:266`): per-element `{ id, [x,y,w,h] authored-logical, [minX,minY,sizeX,sizeY] actual-screen
  }`, from `CollectGameplayLayout(id, x, y, w, h, screenMin, screenSize)` (`:37-44`). This lets you assert
  "authored rect × GameplayUiScale() == screen rect" and diff two resolutions.

**Existing scenarios to copy from:** `scenarios/interfaces/` (auction, bank, character-inventory, guild,
mail, profession, quest-*, tabard, talents, trainer, vendor, loot) and `scenarios/night06/`,
`scenarios/night07/` (panel sweeps: `core-window-sweep.txt`, `all-modal-visual-sweep.txt`,
`economy-system-sweep.txt`, `worldmap-profession-verify.txt`).

## Prerequisite gap — extend the layout hook

`CollectGameplayLayout` is currently wired into **only ~4 surfaces**: action bar (`Program.ActionBars.cs:228`),
action slots (`:315`), micro-cluster (`:612,640`), cast bar (`Program.Casting.cs:358`), bag cluster
(`Program.Inventory.cs:357,372`), unit frames (`Program.UnitFrames.cs:22`). **Every other panel dumps NO
layout row** — you can eyeball its PNG but can't machine-check its geometry.

**First real task: add one `CollectGameplayLayout("<panel>", logicalX, logicalY, logicalW, logicalH,
screenMin, screenSize)` call per panel** at its draw site (right where it computes `origin`/`size` from
`GameplayUiScale()`). That turns "looks right to me" into "authored rect × scale == screen rect, verified in
JSON across 1600×900 and a second resolution." Do this as you touch each panel.

## Suggested method (per panel)

1. Open the benilla FrameXML for the panel (`crates\benilla\assets\ui\<Frame>.xml`); note the authored
   `<Size>` and anchor offsets (1024×768 space).
2. `panel <name>` → `dump <name>`; read the PNG. Compare shape/proportion/anchor to benilla + the real
   1.12 client by eye.
3. If off: correct the panel's logical rect / origin to the FrameXML numbers (NOT by eye-tuning), and add
   the `CollectGameplayLayout` row.
4. Re-dump; confirm the PNG matches and `layout[]` shows `authored × uiScale.effective == screen`.
5. **Verify functionality**, not just looks: tabs switch, scrollbars move, buttons hit-test, the window
   shows real data (the `Simulate*` handlers populate most of these offline; for the rest drive the real
   flow — e.g. `gm .npc spawn add <trainer>`, `interact`, `trainer open`).
6. Regression-check a second resolution: change `client-config.json` `window` to e.g. 1024×768 and 2560×1440,
   re-dump the same panel, confirm it scales uniformly and stays anchored.

## Gotchas / rules of the road

- **Do NOT trust "success" verdicts as proof of rendering.** Several harness steps inject synthetic
  verdicts that assert success without drawing anything. Only the **PNG** and the **`layout[]`/`uiScale`
  JSON** are real evidence. (This is the recurring trap on this project — verify behavior, not assertions.)
- **`1.8` matched preference / `2.0` reference divisor** (`GameplayUiScale`) and per-panel **logical rects** are the two levers. Prefer
  fixing the logical rect to FrameXML over nudging global scale.
- **WowSkin/glue numbers are Blizzard-verbatim** — correct them against FrameXML, don't eye-tune
  (`WowSkin.cs:34-37`; `SYSTEM_SETTINGS_UI.md:186` warns about "proportions that exist in no version of WoW").
- **`dotnet build` is the first gate** each session (there are pre-existing warnings; 0 errors is the bar).
- **Screenshots include the DevTools "Server" panel overlay** (top-left) — it occludes the left ~30% of the
  frame. Orient panels/cameras so the target isn't hidden, or note it. There is no clean HUD-hide step yet;
  adding one (suppress the dev overlay for a `dump`) would help every future capture.
- **Resolution is fixed at window creation** — to test another resolution, edit `client-config.json`
  `window.width/height` and relaunch.

## Start here

1. `dotnet build MSUIClient/MSUIClient.csproj` (confirm 0 errors).
2. Read `SYSTEM_GAMEPLAY_UI.md` (per-surface status table + the 1024×768 rationale) and skim
   `docs\archive\NEXT_03_BOTTOM_BAR_FLUSH.md` (the exemplar of the proof→gap→fix format, and the HiDPI note).
3. Run `scenarios/night06/core-window-sweep.txt` (or write a fresh sweep) to dump character / spellbook /
   quest / talent / trainer / taxi. Read each PNG + JSON. Build the **defect list** first: for each panel,
   "proportion OK? / functional? / has a layout row?".
4. Pick the most-seen offenders first (action bar cluster, unit frames, character page), fix logical rects
   against benilla FrameXML, add `CollectGameplayLayout` rows, re-dump, verify by eye + JSON.
5. Do NOT claim a panel is "done" until a capture shows it correct at ≥2 resolutions. Track status in a
   table like `SYSTEM_GAMEPLAY_UI.md`'s.

## Key files (quick index)
- Scale: `MSUIClient\Program.GameplayLayout.cs` (`GameplayUiScale`, canvas constants).
- Shared widgets: `MSUIClient\Program.VanillaUi.cs`.
- Verification: `MSUIClient\Program.DevTools.GameplayDump.cs` (dump + `CollectGameplayLayout`),
  `MSUIClient\Program.LiveRun.cs:514-540,672` (`panel`/`dump` steps).
- Glue skin: `MSUIClient\Engine\UI\WowSkin.cs`, `GlueAdditive.cs`, `GameplayArt.cs`.
- Per-panel: the `Program.<Panel>.cs` partials listed in the inventory.
- Reference: `C:\Users\nico\Desktop\benilla-main\crates\benilla\assets\ui\*.xml`.
