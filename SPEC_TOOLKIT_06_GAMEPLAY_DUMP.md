# SPEC TOOLKIT 06 — F10 gameplay dump

Instrument I4 of `GAMEPLAY_FOUNDATION_PLAN.md`: one keypress captures the complete
gameplay-plane state as `dumps/gameplay-<name>.json` — the F9 scene dump's twin for
the entity/UI plane. Requires SPEC 01 (ring, all channels) and SPEC 05 (wire ring).
SPEC 00's orders remain binding.

Model the implementation on `DumpScene()` in `Program.DevTools.cs`: same JSON
options (`DumpJson`), same `dumps/` destination, same naming rule (current vantage
name else timestamp, but prefixed `gameplay-`), same one-line console summary. New
file `MSUIClient/Program.DevTools.GameplayDump.cs` (partial GameLoop), invoked from
the DevTools-gated input path on **F10** with the repo's edge-detect idiom, plus a
button in the DevTools HUD next to the F9 dump's.

## 1. Blocks (top-level JSON keys, in this order)

- `name`, `takenLocal`, `map` — as F9.
- `scenario`:
  - player: race/gender (however the appearance state names them), level, guid,
    position, current power/health values (from the descriptor accessors used by
    the HUD/action code — reuse, don't re-derive), mounted/dead flags if cheap.
  - equipment: the equipped display ids the portrait/paper-doll invalidation
    already tracks (whatever list drives "equipment changed → rebake").
  - selection: guid, displayId, scale, reaction, dead + lootable flags (the
    accessors from the loot pass), distance to player, plus `TryGetPortraitFraming`
    output if creature.
  - pendingCast: the pending/auto-repeat cast state the casting code keeps
    (spell id, stage, remaining) — read the actual fields, cite them.
  - panelsOpen: character/spellbook/backpack/loot/etc. booleans (`_characterOpen`
    and siblings).
  - uiScale: `GameplayUiScale()` result, the configured scale preference, the
    framebuffer size, and `io.DisplaySize` — the HiDPI mismatch evidence
    (NEXT_03 flagged this pair; the dump settles it forever).
- `portraits`: for player and target: latest `PortraitVerdict` from the ring (as a
  serialized object, enum names as strings), the `_playerPortraitUsable`/
  `_targetPortraitUsable` flags, dirty flags, retry timestamps, and the active
  override key if any (from the SPEC 02 store).
- `actionBar`: current page; all 120 packed slot values; then for each **visible**
  slot the full `ActionButtonVerdict` — obtained by calling the same
  `ComputeButtonVerdict` the draw path uses, with the same frame context (call it
  at dump time; do not cache-and-drift). Include the last 20 `action` entries from
  the verdict ring.
- `animator`: for player and selection, per track (0 base / 1 action / 2 spell
  hold — the 1C mapping): requested id, played id, and the last `AnimChoice`; plus
  the last 20 `anim` ring entries.
- `verdicts`: the full ring snapshot, each entry as
  `{ channel, time, line, data: {…} }` — line is `ToLine()`, data the typed record
  (System.Text.Json handles the records; enum-as-string converter is already in
  `DumpJson`).
- `wire`: the last 100 `WirePacket`s from SPEC 05's ring (no payloads — name,
  direction, size, time).
- `layout`: the authored-rect → screen-rect table. Emit one row per drawn element
  the bottom-bar/unit-frame code positions this frame: action slots 1–12, micro
  buttons, bag slots + backpack, cast bar, player frame, target frame:
  `{ id, authored: [x,y,w,h], screen: [x,y,w,h] }`. **Source these from the
  actual draw code's computed rectangles** — the cleanest mechanism is a small
  frame-scoped collector the layout/draw code writes into when a dump is armed
  (set a flag on F10, collect during the next frame's draw, then write the file at
  frame end). That guarantees the dump records what was truly drawn, not a
  re-derivation. If the draw code turns out to funnel through few enough shared
  helpers that collection is a 20-line change, do that; if it would mean touching
  dozens of scattered call sites, collect the top 6 container rects only (bar,
  micro cluster, bag cluster, cast bar, two unit frames) and record the reduction
  as a deviation.

## 2. Screenshot pairing (best effort)

After writing the JSON (i.e. the armed frame has completed), read the default
framebuffer (`glReadPixels`, RGBA, flip vertically) and save
`dumps/gameplay-<name>.png` with the same encoder `SavePng` uses. If backbuffer
readback misbehaves with the current swap/ImGui timing, ship JSON-only and record
the deviation — do not sink time into it; the Lab/batch PNGs cover pixel evidence
elsewhere.

## 3. Console + clipboard

One line: `[gdump] wrote dumps/gameplay-<name>.json (+ .png)` — and put the JSON
path on the clipboard (standing rule). The Verdicts panel gets a `Dump (F10)`
button for mouse-first use.

## 4. Boundaries

Read-only observation of state that already exists; the only new per-frame cost is
the armed-frame layout collection (a list append behind an `if`, nothing when not
armed). No new accessors that require descriptor-layout guesswork: if a scenario
field listed above has no existing accessor, omit it and record the omission —
this dump reports what the client knows, it does not expand what it knows.

## Test protocol / definition of done

1. Stand in a normal gameplay scene, press F10 → file appears, console line +
   clipboard path; JSON parses; every block present.
2. The `actionBar` verdicts in the dump equal the Verdicts panel's latest rows for
   the same slots (same structs, so they cannot differ — spot-check anyway).
3. `layout` rows: at 1920×1080 and one other resolution, the backpack row's screen
   rect moves proportionally with the bar (the NEXT-handoff backpack-escape bug
   class becomes two comparable rectangles).
4. Dump twice with no state change → JSONs differ only in name/time fields.
5. `devTools:false` → F10 inert, no dump.

## Live checks for Nico (copy into report verbatim)

1. Reproduce any current visual gripe, press F10, send the JSON (+PNG) to the
   assistant instead of describing the gripe. That exchange is the toolkit's
   acceptance test.
