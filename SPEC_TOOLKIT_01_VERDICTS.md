# SPEC TOOLKIT 01 — Verdict structs + the verdict ring

Instrument I1 of `GAMEPLAY_FOUNDATION_PLAN.md`. Four gameplay decisions become
machine-readable verdicts feeding one in-memory ring. Everything later (Portrait Lab,
batch bake, F10 dump) consumes these. Read `SPEC_TOOLKIT_00_ORDERS.md` first; its
rules apply to every line below.

**The one law: report must equal act.** A verdict is captured at the exact site where
the game acts on the decision, from the same variables. Never recompute a verdict in a
second place.

---

## Stage 1A — infrastructure + portrait verdicts

### New file `MSUIClient/Engine/Verdicts.cs`

```csharp
namespace MSUIClient.Engine;

public enum PortraitSubject { Player, Target, PaperDoll, Lab }
public enum PortraitCameraSource { Authored, Bounds, Override }   // Override used by spec 02
public enum PortraitOutcome
{
    Ready,            // HasSubject true, portrait usable
    Blank,            // bake ran, subject pixels below threshold
    NotDrawn,         // creature path: RenderPortrait returned false
    Skipped,          // preconditions failed (no model / not loaded) — optional, only if cheap
}

public readonly record struct PortraitVerdict(
    double Time,
    PortraitSubject Subject,
    PortraitOutcome Outcome,
    PortraitCameraSource CameraSource,
    bool AuthoredRetriedAsBounds,     // authored bake was blank, bounds retry happened
    int SubjectPixels,
    int RgbLo, int RgbHi, int AlphaLo, int AlphaHi,   // from ReadbackStats — see mapping note
    int Pieces,                        // VisiblePieces; -1 for creature path if unavailable
    int DisplayId,                     // 0 for player
    float BindPoseHeight,              // 0 if not applicable
    float EyeHeight, float Distance, float FovyDegrees, float NearPlane) : IVerdict
{
    public string Channel => "portrait";
    public string ToLine() => ...   // one line, key=value pairs, stable field order
}

public interface IVerdict
{
    string Channel { get; }
    string ToLine();
}

public sealed class VerdictRing
{
    // capacity 256, single-threaded (main loop only), oldest overwritten.
    public void Add(IVerdict v);
    public IReadOnlyList<IVerdict> Snapshot();                    // newest last
    public IEnumerable<T> Recent<T>(int max) where T : IVerdict;  // filtered tail
}
```

Mapping note: read `ReadbackStats` in `Engine/PortraitRenderTarget.cs` first and mirror
its actual field names/types into the struct (the rgb/alpha lo–hi ranges and subject
count exist — its `ToString()` prints them). If a field the spec lists is not in
`ReadbackStats`, record a deviation rather than synthesizing it.

`GameLoop` gains one field, `private readonly VerdictRing _verdicts = new();` —
declare it in `Engine`-agnostic fashion in an existing partial (put it beside the
portrait fields in `Program.Portraits.cs`).

### Capture sites (all in `Program.Portraits.cs`, current source read 2026-07-30)

1. **Player bake** — in `BakeDirtyPortraits`, at the existing
   `Console.WriteLine($"[portrait] player bake ...")` site. Build the struct from the
   locals already present: `stats`, `usedFallbackCamera`, `authoredCamera`,
   `_character.VisiblePieces`, `_character.BindPoseHeight()`, and the `camera` used
   (read `EyeHeight/Distance/FieldOfViewDegrees/NearPlane` off it; for the authored
   camera these describe the authored projection — set EyeHeight/Distance to 0 and
   record the authored fov instead). `AuthoredRetriedAsBounds` = the existing retry
   branch executed. Add to ring; **keep the existing console line unchanged** (greps
   and docs depend on its shape), but append nothing to it.
2. **Target bake** — same treatment at the target `Analyze()`/blank site, including
   the `drawn == false` case → `Outcome.NotDrawn`, `DisplayId = target.DisplayId`.
3. **Paper doll** — bake site exists but has no analyze; add a ring entry only if
   `Analyze()` is already computed there. It is not — so **skip paper doll in 1A**
   and note it (this is intentional scope, not an omission).

### 1A test protocol
Log in → console shows the unchanged `[portrait] player bake ready ...` line AND
(dev HUD later / debugger now) the ring holds a `PortraitVerdict` whose numbers match
that line exactly, field for field. Select a creature → second entry. **Done** when
the ring entry and the console line can never disagree (same locals, same site).

---

## Stage 1B — cast-target verdict

Read `Net/CastTargetLaw.cs` and its tests first. The law is pure and already returns
a resolution; give it a reason without changing behavior:

- Add `public enum CastTargetReason { ... }` — **derive the members from the law's
  actual branches** (expected from docs: selected-unit accepted; helpful-on-hostile →
  self fallback; ground/AoE shape; item target; refused/unsupported shape; refused by
  local gates: cooldown/GCD, pending cast, mounted, range — use the branch names the
  code actually has, one enum member per exit path, no invented members).
- Extend the law's return (tuple/record — match its current style) with the reason.
  Update its tests mechanically to destructure the new shape; add one assertion per
  existing test naming the expected reason. Do not add new test scenarios in this
  stage.
- New struct in `Verdicts.cs`:
  ```csharp
  public readonly record struct CastVerdict(
      double Time, uint SpellId, CastTargetReason Reason,
      ulong SelectedGuid, ulong ResolvedGuid, bool Sent) : IVerdict   // Channel "cast"
  ```
- Capture site: in `Program.Casting.cs`, at the single place the law's result is
  consumed to send or refuse a cast (find it by grepping the law's method name). Add
  ring entry + one console line `[verdict:cast] ...` **only when Sent==false or the
  resolved target differs from the selected target** — a normal accepted cast stays
  console-silent (ring only).

**Test protocol.** Holy Light with hostile selected → `[verdict:cast] ... reason=SelfFallback`
and the heal goes to the player (existing behavior, unchanged). Cast on cooldown →
`Sent=false` with the matching reason. All existing CastTargetLaw tests pass.

---

## Stage 1C — animation choice verdict

Find the animator: `grep -rn "FindOrBake" MSUIClient/`. Read the file plus the exact
spell-action selection paths referenced by the 09:00 handoff ("exact player and
creature spell-action paths no longer substitute Stand"). Then:

```csharp
public enum AnimChoiceKind { Exact, BakedOnDemand, Fallback, Missing, Substituted }
public readonly record struct AnimChoice(
    double Time, string Unit /* "player" | "creature:<display>" */,
    int Track, int RequestedId, int PlayedId, AnimChoiceKind Kind) : IVerdict  // Channel "anim"
```

Capture inside the resolution path (`FindOrBake` and/or its callers — wherever the
requested-vs-actual decision is finally made; one site, not several). Ring always;
console `[verdict:anim]` **only** for `Fallback | Missing | Substituted` and only on
transition (don't repeat for every frame of a looping clip — latch last emitted
(unit, track, requested) and skip repeats).

`Substituted` means "played a different clip than requested for a non-authored
reason" — after the 09:00 fix this should never fire; it exists as a tripwire. If,
while reading, you find the current code cannot distinguish `Fallback` (an authored
fallback chain) from `Substituted`, implement `Exact | BakedOnDemand | Missing |
Fallback` only and record the limitation as a deviation — do not guess.

**Test protocol.** Normal locomotion → zero anim console lines. Cast a spell →
ring shows the spell one-shot resolution (Exact or BakedOnDemand) and movement
afterward resumes with an Exact choice, never Missing.

---

## Stage 1D — action-button verdict (the riskiest stage — do it last)

Read `Program.ActionBars.cs` in full first. The 2026-07-30 state pass (see
PORT_SESSION §3) computes per-button: usability tri-state (usable / not-enough-power
/ unusable), range state via SpellRange.dbc + combat-reach math, pushed / hover /
checked / flash / carried-grid / equipped-border, and stack counts.

```csharp
public enum ButtonUsability { Usable, NotEnoughPower, Unusable }
public enum ButtonRange { NoCheck, InRange, OutOfRange }
public readonly record struct ActionButtonVerdict(
    double Time, int Slot, bool IsItem, uint ActionId,
    ButtonUsability Usability, ButtonRange Range,
    bool Pushed, bool Hover, bool Checked, bool Flashing, bool CarriedGrid, bool EquippedBorder,
    // inputs — every number the predicates used:
    int PowerCost, int CurrentPower, int BaseMana,
    int RangeIndex, float RangeMin, float RangeMax, float DistanceToTarget,
    int StackCount) : IVerdict   // Channel "action"
```

**The refactor (PLAN_03's `ClassifyGroup` move, exactly):** extract the existing
per-button computations into one function
`ActionButtonVerdict ComputeButtonVerdict(int slot, <the context the loop already has>)`
and make the draw code consume **only** the struct — every tint/texture/text decision
switches on verdict fields, with zero residual re-computation in the draw path. The
branch conditions move; they do not change. Resist any urge to "clean up" the state
rules while moving them — byte-identical visual behavior is the gate.

Emission: computing happens per visible slot per frame (as today). Ring + console
only **on transition** per slot (keep a small last-verdict array; compare
`Usability|Range|Flashing|Checked` — presentation-only fields like Hover/Pushed do
not emit). Console shape: `[verdict:action] slot=3 spell=635 usable=NotEnoughPower
cost=155 power=90 range=InRange dist=8.2 ...`.

**Fallback** (PLAN_03 §9's, verbatim policy): if unifying draw+report in one pass
proves too invasive, first ship `ComputeButtonVerdict` as a read-only replay of the
same conditions (duplicated, clearly commented as temporary), emit verdicts from it,
and record the unification as the named follow-up. Prefer unifying now.

**Test protocol.** With a target: walk out of range → one transition line, hotkey
turns red the same frame (same struct). Drain mana → `NotEnoughPower` line, icon+ring
blue. Start auto-attack → `Flashing=true` line once, not 60/s. Visuals byte-identical
to pre-refactor (A/B screenshots at the same scenario).

---

## Stage 1E — the Verdicts panel (in-client, copyable — build right after 1A)

**Why this stage exists:** the human operator must never have to fish evidence out
of terminal scrollback. Every verdict must be readable AND copyable in-client.
This stage may be built immediately after 1A (it only needs the ring) and before
1B–1D; later stages' channels appear in it automatically.

### New file `MSUIClient/Program.DevTools.Verdicts.cs` (partial GameLoop)

DevTools layer rules apply (`config.DevTools` gated; find the existing HUD panel the
vantage section lives in and add a collapsing header **"Verdicts"** the same way).

Panel contents:

- **Filter row:** one checkbox per channel (`portrait / cast / anim / action` —
  derive from the entries present, don't hard-code), a free-text substring filter,
  and a `Pause` toggle (freezes the displayed snapshot while the ring keeps
  collecting).
- **The list:** the filtered ring tail, newest at the bottom, monospace
  (`ImGui.TextUnformatted` per row — no wrapping), auto-scrolled to bottom unless
  the user scrolled up (standard ImGui log-window idiom). Each row prefixed with a
  wall-clock time `HH:mm:ss.f`.
- **Copy affordances — the point of the stage:**
  - Clicking a row → `ImGui.SetClipboardText(row)` and a brief "copied" flash.
  - `Copy visible` button → all currently filtered rows joined with newlines.
  - `Copy last <N>` button with a small int input (default 20), ignoring filters.
- **Console mirror unchanged:** this panel supplements the terminal lines, never
  replaces them (headless/batch runs still need stdout).

Additionally, wherever a DevTools panel already shows portrait state or later specs
show evidence (Portrait Lab's evidence panel in SPEC 02 stage 2B), every read-only
evidence block gets a `Copy` button beside it that puts the same key=value text on
the clipboard. Standing rule from here on: **if a spec says "show" a diagnostic
value, that implies "and make it one click to copy."**

**Test protocol.** Trigger a few verdicts, click a row, paste into a text editor —
byte-identical to the console line. `Copy visible` with a channel filtered → only
that channel pastes. `devTools:false` → panel gone, ring still populates.

---

## Definition of done (whole spec)

All four channels populate the one ring and are visible + copyable in the Verdicts
panel (1E); console noise added is transition-only;
`devTools:false` behavior identical except ring population; gates pass; the report's
Console evidence section shows at least one real line per channel.

## Live checks for Nico (copy into report verbatim)

1. Paste one `[portrait] player bake ...` line and confirm the HUD portrait matches
   its verdict (ready ↔ live model, BLANK ↔ stand-in).
2. Holy Light with a hostile wolf selected → paste the `[verdict:cast]` line
   (expect `SelfFallback`) and confirm the heal landed on you.
3. Drain mana, walk out of range, start auto-attack → paste the three
   `[verdict:action]` transition lines; confirm each matched the visual state.
4. Cast, then move immediately → paste any `[verdict:anim]` lines (expect none at
   `Missing`/`Substituted`); confirm locomotion resumed instantly.
