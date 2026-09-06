# Macro Book

The macro window, reimagined (owner decision 2026-09-04 after the Discord thread with
MightyDorf and cam). The vanilla MacroFrame's imported limits - 255 characters, 18 slots per
set, no grouping - were replaced; the vanilla *vocabulary* (dialog backdrop, inset tabs,
UIPanelButtons, Common-Input-Border edit boxes, UIPanelScrollBars, the QuestLog plus/minus)
stays so it reads as a 1.12 frame.

## What it does

- **Two books**: General (account) and *Character* Specific, as before. Files:
  `macros/accounts/<account-key>/account.txt` and `macros/accounts/<account-key>/<Realm>-<Character>.txt` (git-ignored).
- **Sections**: collapsible headers with a macro count; collapsed state persists. A macro's
  section is changed from the Section button (or "New section..." there). Deleting a section
  ungroups its macros. Renaming: select the header, edit the Name field.
- **Limits**: 4000 characters per macro, 500 macros per book, names up to 32. A single LINE is
  still capped at 255 by the Core's chat handler; the linter flags that.
- **Search** over names and bodies (expands sections, hides empty ones).
- **Editor** with a linter strip under it, live as you type.
- **Reference shelf**: *Templates* (fill in the `<placeholders>`; the linter refuses to call the
  macro clean until they are gone) and *Commands* (the Core's full command tree with the
  required rank - GM / Dev / Admin - as a tag; click to insert).
- **Run Macro** in place - a gear kit no longer needs a hotbar slot to be applied.
- **Drag to hotbar** from any list row, double-click a row to run it, exactly as before.
- **Drag inside the book** (QoL round 2026-09-05): drop a macro on another macro to reorder
  (upper half = before, lower half = after; it joins that macro's section), on a section
  header to move it there (last), or below the last row to ungroup it. Drag a section header
  onto another header to reorder sections. A gold line / box shows where the drop lands.
- **Delete asks first** for a macro (a stock yes/no StaticPopup naming the macro). Deleting a
  section is not asked about: it only ungroups its macros.
- **Hotbar buttons show the macro's name** in the 36x10 ActionButton name box at the bottom,
  clipped to the first characters that fit (like 1.12, no ellipsis).
- Escape closes the icon picker / section menu first, then the book.

## Stable ids (why the hotbars did not break)

`ActionSlot.Macro` carries the macro id in the 24-bit action field and the server stores the
bars, so ids must never move. The old client bound by list position: account slots were 1..18
and character slots 19..36. Those ranges are reserved as *legacy* ids and a pre-book file is
migrated onto them 1:1, so every hotbar button placed before the book still works. New account
macros count up from 37; new character macros from `0x800000`. See `MacroBookLaw`.

## Store format (v2)

```
MACROBOOK 2
NEXT 41
MACRO 37 "Ungrouped" INV_Misc_QuestionMark
/say hi
END
SECTION "Gear Sets" COLLAPSED
MACRO 38 "Caster 60" INV_Staff_13
.additem 14460
END
```

The `MACRO n "name" icon / body / END` block is the 1.12 shape; an old reader skips the header,
`NEXT` and `SECTION` lines and still finds every macro. A file without the `MACROBOOK` header is
read as the legacy positional store and rewritten in v2 on first load.

## The command catalogue

`MSUIClient/Data/vmangos-commands.tsv` is exported from the Core's `Chat.cpp` command tables
(name, SEC_* rank, runnable, has_subcommands) and embedded in the client at build time. When
the Core's command table changes, regenerate it:

```bash
ssh 192.168.0.2 'python3 -' < tools/macro-commands/export-commands.py > MSUIClient/Data/vmangos-commands.tsv
```

The linter resolves dot commands the way `ChatHandler::FindCommand` does (exact or unique
prefix per level), so `.addi 14460` is accepted. Client slash verbs are the roster in
`MacroLintLaw.ClientVerbs` (plus every text emote); a 1.12 verb missing there is a verb this
client has not armed - the linter says so instead of guessing.

## Checks

`dotnet run --project tools/interface-wire-check -- --macro-book-only` freezes the id ranges,
the store round trip and migration, the row projection, the geometry, the linter against a
fixture catalogue AND the real embedded export (every template must resolve there), and the
source fences. `GameLoop.Macro.cs` is enrolled in `--imgui-policy-only` (no ImGui widgets).

## Status

Built 2026-09-04, both trays; checks green. **Live-unverified** - the owner launches the client.
Things to look at on first run: the header plaque over a dark world, the editor's ImGui font
size against the GameText labels, and that a pre-existing hotbar macro still fires after the
the account-scoped store rewrite.
