# Creator Mode

An offline sandbox built into MSUIClient for dressing characters and tuning
spell visuals in realtime. Selected from the login screen's **Launch Options**
menu (red glue buttons); the choice persists in `settings.json`
(`Settings.LaunchMode`: `"Client"` | `"Creator"`, empty = Client).

## Architecture

| Piece | Where |
|---|---|
| Launch mode + login front door | `MSUIClient/Program.Creator.cs`, login changes in `Program.Net.cs` (`DrawLoginScreen`, `NetHud`, `InitNet`) |
| Menu bar + Character/Gear panels + item search | `MSUIClient/Program.Creator.Ui.cs` |
| Teleport presets + world-map picker + target dummy | `MSUIClient/Program.Creator.World.cs` |
| Spell workshop (loop + tuning + export) | `MSUIClient/Program.Creator.Spells.cs` |
| Ported MangosSuperUI write stack | `MSUIClient/Creator/` (see below) |
| Tier sets T0–T3 (generated from tier-sets.js) | `MSUIClient/Formats/CreatorTierSets.cs` |
| Item catalogue loader | `MSUIClient/Formats/CreatorItemTable.cs` + repo-root `creator-items.tsv` |

Key mode plumbing:

- **Creator never connects.** `InitNet` builds the full presentation stack
  (spell FX, creatures, gameplay UI, portraits) for creator mode, then skips the
  network client when the server is disabled, and suppresses auto-login when it
  is not. Batch instruments (portrait/variant/movement/live-run) ignore
  `LaunchMode` entirely (`CreatorLaunchActive`).
- **`LocalPlayerGuid`** (`Program.Creator.cs`) is the identity key for the
  presentation path: `_net?.PlayerGuid ?? CreatorLocalGuid`. Targeting, spell
  poses and `PresentSpellEffect` were switched to it.
- The developer overlay ("MSUI Client" window + inspectors) never draws in
  creator mode — `BuildGui` returns before the DevTools stack.
- The **target dummy** is a synthetic `WorldEntity`
  (`ObjectFields.ForSyntheticUnit`) via `EntityStore.AddSynthetic`; targeting
  gates were relaxed to allow offline selection, and `CommitSelection` never
  begins an attack offline.
- **Teleports** ride `TravelToMapId`; a null-Z preset travels in at Z=500 and
  ground-snaps after arrival. The map picker inverts `WorldMapArea.dbc` bounds
  (the same math as the world-map player marker, reversed).

## The realtime spell loop

`SpellEffectSource` gained a model-override layer:
`SetModelOverride(path, bytes)` + `ReadOriginalModel(path)`. The workshop byte-
patches effect M2s in memory (ported `M2ParticlePatcher` / `M2EmitterParser`)
and hot-swaps them; the loop (`UpdateCreatorSpellLoop`) re-presents the chosen
phase every period, so a knob change is visible within one cycle. Patches are
always rebuilt **from the original bytes** (globals → per-emitter absolutes) so
multipliers never compound.

Export (`creator-exports/`):
- **patch MPQ** — patched M2s at their original paths (`patch-4.MPQ`; drop into
  `WoW/Data`, delete the WDB cache).
- **tuning JSON** — whole-model dials + per-emitter absolute values keyed by
  model path, for MangosSuperUI's tuning pipeline.

## Ported files (`MSUIClient/Creator/`)

From `MangosSuperUI/Services`, namespaces rewritten to `MSUIClient.Creator[.Mpq]`,
otherwise kept near-identical so upstream re-syncs stay diffs:
`DbcWriterService`, `SpellVisualCloner`, `M2ParticlePatcher`, `M2EmitterParser`,
`M2TextureParser`, `BlpWriterService`, and the managed MPQ stack
(`MpqArchive`, `MpqCrypto`, `PkwareExplode`, `MpqArchiveWriter`,
`MpqBuilderService`). `CreatorShims.cs` stands in for ASP.NET's `ILogger<T>`
and the archive-remount hooks. `DbcWriterService`/`SpellVisualCloner` are not
wired into the UI yet — they are the foundation for full custom-spell cloning
(new DBC rows at the SuperUI ID floors: visuals/kits/effects start at 10000,
spells 40000–49999).

## Data regeneration

- `creator-items.tsv` — dump of `item_template` via the running MangosSuperUI's
  `/Items/Search` endpoint (39,622 rows: entry, name, class, subclass, quality,
  displayId, inventoryType, reqLevel, itemLevel). Regenerate by paging that
  endpoint at `pageSize=1000`.
- `Formats/CreatorTierSets.cs` — generated from
  `MangosSuperUI/wwwroot/js/character-viewer/tier-sets.js`; regenerate rather
  than hand-edit.

## UI chrome and scaling

Creator panels wear the real 1.12 dialog chrome, not ImGui's: the
`UI-DialogBox` nine-slice + near-opaque fill, the `UI-DialogBox-Header` plaque
hanging above the frame with the title, and the round `UI-Panel-MinimizeButton`
close (`DrawCreatorPanelChrome` in `Program.Creator.Ui.cs`). Content is
organized into vanilla +/- drill-down categories (`CreatorCategory`, quest-log
plus/minus art; open state keyed by stable ids so labels may change freely -
the ImGui label-hash collapse bug is why). Buttons are `WowSkin.PanelButton`
(`UI-Panel-Button` art).

`Settings.Creator.UiScale` (widget/panel sizes) and `Settings.Creator.TextScale`
(font only) are independent dials, live from the bar's **UI** button and saved on
release. Control heights derive from the live text height and widths grow to fit
their captions (`CreatorButton` / `CreatorColumnWidth` / `CreatorComboWidth`),
so no dial combination clips a label.

## Known gaps / next slices

- BLP/PNG texture import: the pipeline pieces are ported (`BlpWriterService`,
  `M2TextureParser.PatchTextureFilenames`); the workshop's data model reserves
  per-emitter texture slots but no import UI exists yet.
- Full custom-spell cloning (new spell + DBC chain + patch, SuperUI-style
  `RebuildUnifiedPatch`) — foundations ported, orchestration not yet.
- Sounds and animation kits: cloned, never edited (same as SuperUI today).
- Export writes `creator-exports/patch-4.MPQ` rather than into `GameData/Data`
  (the client may have a mounted `patch-3.MPQ` open; also avoids colliding with
  SuperUI's own patch-3 output).
