# Code Structure Law

**This document is binding. Read it before adding, moving, renaming, or splitting any `.cs` file in `MSUIClient/`.** It exists so the source tree stays legible as it grows. If a change would violate a rule here, either don't make it or update this law first — do not quietly break the pattern.

> Naming note: this is the *client's* code-structure law. The historical name people reach for is "Program structure," but the app's real god-class is **`GameLoop`**, not `Program` (see §1). The law is named for what it governs, not for the old file prefix.

---

## 1. The mental model (know this before anything else)

The client has exactly **two top-level concerns** and a set of **subsystems**:

- **`Program`** — `public static partial class Program`. The entry point only: `Main`, argument handling, bootstrap. Small. Lives in `MSUIClient/Program.cs`. Do not grow it.
- **`GameLoop`** — `public sealed partial class GameLoop : IDisposable`. The **application object** and the **conductor**. Its spine (constructor, `Update(dt)`, `Render(dt)`) lives in `Program.cs`; the rest of it is split across ~117 partial files under `MSUIClient/GameLoop/`. `GameLoop` holds a reference to each subsystem and calls them in order every frame. It does **not** contain rendering, parsing, or protocol code itself.
- **Subsystems** — ordinary, separately-testable classes under `Engine/ Formats/ Net/ World/ Player/ Creator/`. These do the heavy lifting (GL, file formats, networking, physics). `GameLoop` owns instances of them (`_terrain`, `_wmo`, `_character`, `_net`, …) and drives them.

**The one direction rule (the layering law):** data flows **down**. `GameLoop` pushes state into subsystems each frame; **a subsystem must never reference `GameLoop` or `Program` in code.** This inversion of control is what keeps the engine from tangling with game logic. It is currently true with zero exceptions — keep it that way. (Referencing `GameLoop` in a `//` comment to explain who calls you is fine; a `using`/type reference is not.)

---

## 2. Where a file goes

### 2.1 Subsystem code → its subsystem folder

If the file is a renderer, a format reader, a network/protocol piece, physics, or offline content tooling, it belongs in the matching top-level folder and must obey §1's one-direction rule:

| Folder | Owns | Namespace |
|---|---|---|
| `Engine/` | Windowing, GL primitives, asset workers, login/glue screens, and the pure `*Law` UI-rule classes (see §4) | `MSUIClient.Engine[.UI]` |
| `Formats/` | Pure binary decode: MPQ, DBC, M2, WMO, ADT, BLP, VMAP. **No GL, no game logic.** | `MSUIClient.Formats[.Mpq]` |
| `Net/` | 1.12 protocol/session: sockets, opcodes, SRP6, object-update parsing | `MSUIClient.Net` |
| `World/` | 3D scene rendering + world sim. Sub-folders: `Units/ Wmo/ Doodads/ Particles/ Spells/ Collision/ Portals/` | `MSUIClient.World[.<sub>]` |
| `Player/` | Movement/physics controller | `MSUIClient.Player` |
| `Creator/` | Offline content-authoring / MPQ-patch build tooling (not runtime) | `MSUIClient.Creator[.Mpq]` |

### 2.2 `GameLoop` slices → `GameLoop/<bucket>/`

Every partial of `GameLoop` lives under `MSUIClient/GameLoop/` in exactly one bucket:

| Bucket | What belongs here | Examples |
|---|---|---|
| `GameLoop/Panels/` | Toggled UI windows/frames the player opens (usually have a `_*Open` flag) | Chat, Mail, Inventory, Bank, Vendor.\*, Quest, Talents, Settings, WorldMap |
| `GameLoop/Hud/` | Always-on overlays: unit frames, action bars, nameplates, tooltips, portraits, minimap, and the panel-ownership/layout machinery | UnitFrames, ActionBars, Nameplates, GameTooltip.\*, UiPanelOwnership, VanillaUi |
| `GameLoop/Combat/` | Casting, spell/combat feedback, targeting, death/rez, combat-driven FX | Casting, CombatFeedback, SpellEvents, Targeting, DeathRez, GroundFx |
| `GameLoop/Scene/` | World streaming, session/networking glue, zone/instance flow, world-space control, real (shipping) portals | Net, Loading, Instances, RealPortals, Control, GameObjects, Mount.\* |
| `GameLoop/CreatorMode/` | The in-client Creator mode (content/spell authoring UI) | Creator, Creator.Ui, Creator.Spells, Creator.World, Creator.Session, Creator.Probe |
| `GameLoop/Dev/` | **All** diagnostics/instrumentation: dev windows, parity checks, probes, capture/batch harnesses, the hitch recorder. Ships in the binary but is not gameplay. | DevTools.\*, DevWindow.\*, \*Parity, \*Probe, LiveRun, Hitch, LightProbe, Portals (WMO-portal debug readout), HudPreview |

**When in doubt about `Dev/`:** if the file only *observes and reports* (reads renderer/game state and prints, dumps, captures, or asserts) and nothing in core depends on it, it is `Dev/`. This is the FOUNDATION_PLAN §12 seam — *core decides, the dev layer observes.* If a change to a `Dev/` file alters what is on screen in normal play, the seam is broken.

---

## 3. Naming and namespace rules

- A `GameLoop` partial is named **`GameLoop.<Feature>.cs`** (e.g. `GameLoop.Chat.cs`, `GameLoop.Vendor.Repair.cs`). The filename names the class it opens. Never reintroduce the old `Program.<Feature>.cs` name for a file that contains `GameLoop`.
- **Every file declares `namespace MSUIClient;`** (file-scoped) regardless of which folder it sits in, *except* subsystem files which use their subsystem namespace from §2.1. The folder does **not** determine the `GameLoop` namespace — `GameLoop/Panels/GameLoop.Chat.cs` is still `namespace MSUIClient;`.
- The project is **SDK-style with default globbing** — there are **no `<Compile>` items** in `MSUIClient.csproj`. Moving or adding a `.cs` file anywhere under `MSUIClient/` is picked up automatically; you never edit the `.csproj` to register a file.
- Because of the two facts above, **moving a `GameLoop` partial between buckets is a pure `git mv`** — no code change, no namespace change, no project edit. Use `git mv` so history is preserved (renames show as `R100`).
- **Caution — `tools/`**: some projects in `tools/` link *subsystem* source files by relative path (`<Compile Include="..\..\MSUIClient\Net\...">`). They do **not** link any `GameLoop` partial. If you ever move or rename a file under `Net/`, `World/`, etc., grep `tools/**/*.csproj` for its path and fix the links. Moving files under `GameLoop/` is always safe for `tools/`.

---

## 4. Keeping `GameLoop` from rotting (the discipline that matters)

`GameLoop` is one class with ~1,100 instance fields shared across all its partials. It stays safe to edit only because of these habits — follow them:

1. **Own your fields.** A new feature declares its own `_camelCase` fields at the top of its own partial file and touches only those plus shared subsystem objects (`_net`, `_character`, `_controller`). ~87% of fields are written by exactly one file today; keep new state single-owner.
2. **Treat shared "hotspot" fields with care.** A small set of fields are written from many files and are the only real "spooky action at a distance": the panel-visibility bools (`_*Open`), the render dirty-flags (`_paperDollDirty`, `_playerPortraitDirty`), the world anchor `_residentCentre`, and `_quitRequested`. Changing how these are set can affect unrelated features. Read their existing writers before adding one.
3. **Extract pure logic into a `*Law` class.** Decision logic that can be stated without GL/frame state goes into a `public static class <Name>Law` under `Engine/UI/` (there are ~52 already; `GameLoop` calls into ~45). This is the project's testable seam and the sanctioned way to shrink `GameLoop`. Prefer growing a `*Law` over growing a partial.
4. **Diagnostics go to `Dev/` or `tools/`, never inline in gameplay.** There is no unit-test project; correctness is guarded by `IVerdict` self-tests and the standalone `tools/*-check` executables. New instrumentation belongs beside them, behind the observe-only seam — not woven into a gameplay path.

---

## 5. Adding a feature — the checklist

1. Heavy logic (rendering/format/net/physics)? Put it in a subsystem class under §2.1; expose a method `GameLoop` can call. Do **not** reference `GameLoop` from it.
2. UI/glue that lives on `GameLoop`? Create `GameLoop/<bucket>/GameLoop.<Feature>.cs` (§2.2), `namespace MSUIClient;`, `public sealed partial class GameLoop { … }`.
3. Give it its own fields (§4.1). Only reach for a shared hotspot field (§4.2) if the feature genuinely needs it, and read the other writers first.
4. Any state-free decision logic → a `*Law` static class with a test (§4.3).
5. Wire it into the frame from `Program.cs`'s `Update`/`Render` if it needs per-frame work — one call, in the right order.
6. Build (`dotnet build MSUIClient.csproj`) and, where one exists, run the relevant `tools/*-check`.

---

## 6. Authority

This law governs **where code lives and how it is named**. It does not override the per-system ground truth in `docs/systems/SYSTEM_*.md` or the invariants in `PROJECT_HANDBOOK.md` §3 — those govern *how a system behaves*. If this law and a system doc disagree about behavior, the system doc wins; if they disagree about layout, this law wins and the system doc should be corrected.
