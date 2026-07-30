# Toolkit Slice 1 — implementation report 2026-07-30

Build status: BUILT+GATES-PASS

## Stages completed

| Stage | Status | Commit/summary |
|---|---|---|
| Core prerequisite | IMPLEMENTED, GATES PASS | Commit `931f1f2`: restored bounded vanilla-v256 authored-camera parsing, expanded the camera instrument, and corrected false historical verification claims. |
| 1A | IMPLEMENTED, GATES PASS; LIVE UNVERIFIED | Added verdict infrastructure and captured player/target portrait verdicts at the existing bake decision sites. Stage commit: `toolkit: verdicts-1A portrait verdict ring`. |
| 1E | IMPLEMENTED, GATES PASS; LIVE UNVERIFIED | Added the DevTools Verdicts panel with derived channel filters, text filtering, pause snapshots, bottom-following log rows, and row/visible/tail clipboard actions. Stage commit: `toolkit: verdicts-1E add copyable verdict panel`. |
| 1B | IMPLEMENTED, GATES PASS; LIVE UNVERIFIED | Added branch-derived cast-target reasons, cast verdict capture at every existing send/refusal exit, and mechanical reason assertions for the existing pure-law scenarios. |
| 1C | IMPLEMENTED, GATES PASS; LIVE UNVERIFIED | Centralized requested-versus-played animation resolution and added always-ringed, transition-latched animation verdicts for player and creature base/action/spell-hold tracks. |
| 1D | IMPLEMENTED, GATES PASS; LIVE UNVERIFIED | Unified visible action-button state and drawing through `ActionButtonVerdict`; ring/console output occurs only when usability, range, flashing, or checked state transitions. |

## Files touched

| File | New/Edit | What |
|---|---|---|
| `MSUIClient/Engine/Verdicts.cs` | New | `IVerdict`, portrait enums/record, and the 256-entry single-threaded verdict ring. |
| `MSUIClient/Program.Portraits.cs` | Edit | One verdict ring field and player/target verdict capture from the exact locals used by the existing bake/accept decisions. Existing console lines are unchanged. |
| `MSUIClient/Program.DevTools.Verdicts.cs` | New | DevTools-only copyable Verdicts panel. |
| `MSUIClient/Program.cs` | Edit | One call to draw the Verdicts panel inside the existing `_config.DevTools`-gated HUD. |
| `MSUIClient/Net/CastTargetLaw.cs` | Edit | Added a reason to each existing pure-law exit without changing target resolution. |
| `MSUIClient/Program.ActionBars.cs` | Edit | Captured cast verdicts from the existing send/refusal locals. Stage 1D also moved the existing per-button predicates into `ComputeButtonVerdict`; stateful drawing now consumes that result directly. |
| `tools/combat-wire-check/Program.cs` | Edit | Added one expected-reason assertion to each existing cast-target scenario; no new scenario was added. |
| `MSUIClient/World/Units/M2Animator.cs` | Edit | Added the single runtime resolution point that classifies cached exact, on-demand bake, explicit authored fallback, or missing without changing clip order. |
| `MSUIClient/World/Units/CharacterRenderer.cs` | Edit | Routed existing player animation choices through the classified resolver and forwarded its results. Track mapping: base `0`, action `1`, spell hold `2`. |
| `MSUIClient/World/Units/CreatureRenderer.cs` | Edit | Routed existing per-display creature animation choices through the same resolver and forwarded its results with `creature:<display>` identity. |
| `MSUIClient/Program.AnimationVerdicts.cs` | New | Captured all resolution results into the ring and emitted warning kinds only when a unit/track choice transitions. |
| `MSUIClient/Program.Net.cs` | Edit | Connected the gameplay creature renderer to the animation verdict capture sink. |
| `tools/portrait-camera-check/Program.cs` | Edit | Diagnosis-only provenance and raw v256 camera-header/camera-record output authorized after the required gate failed. No resolver/parser behavior changed. |
| `SPEC_TOOLKIT_REPORT_2026-07-30.md` | New | This stage-boundary report. |

## Symbol verification

| Cited symbol | Found as | Note |
|---|---|---|
| `BoundsPortraitCamera` | `Program.Portraits.cs:236` before edits | Exists as cited. |
| `CreatureBoundsPortraitCamera` | `Program.Portraits.cs:250` before edits | Exists as cited. |
| `BakeDirtyPortraits` | `Program.Portraits.cs:51` before edits | Exists as cited. |
| `ReadbackStats` | `Engine/PortraitRenderTarget.cs:13` | Exact fields are `SubjectPixels`, `MinRgb`, `MaxRgb`, `MinAlpha`, and `MaxAlpha`; range values are bytes. |
| `_config.DevTools` | `Program.cs` and existing DevTools partials | Exists; Stage 1A adds no UI or dev-only behavior. |
| `NowSeconds()` | `Program.Casting.cs:347` | Exists as a private static member of the same partial `GameLoop`. |
| `VisiblePieces` | `World/Units/CharacterRenderer.cs` | Exists for the player renderer. Creature pieces are unavailable at this capture site, so the specified `-1` is used. |
| `BindPoseHeight()` | `World/Units/CharacterRenderer.cs:409` | Exists for the player. Target framing exposes `Height`, which is recorded as the target bind/framing height. |
| Existing DevTools HUD | `Program.cs:1885-1903` | The master `_config.DevTools` return and existing main HUD were found; `DrawVerdictsPanel()` is invoked only below that gate. |
| `CastTargetLaw.Resolve` consumer | `Program.ActionBars.cs` (`ResolveCastTarget` called by `TryCast`) | The real send/refusal site is here, not in the spec-cited `Program.Casting.cs`. `_net.CastSpell` and all local refusal gates are in the same method. |
| `M2Animator.FindOrBake` | `World/Units/M2Animator.cs` | Existing exact on-demand path confirmed. Stage 1C's `Resolve` preserves it while adding classification and the existing authored fallback lists at one decision point. |
| Exact spell-action paths | `CharacterRenderer.BeginSpellVisual/ReleaseSpellVisual`; `CreatureRenderer` `AuthoredExact` and spell-hold branches | Both still request the authored ID with on-demand baking and no Stand fallback. |
| Action-button state rules | `Program.ActionBars.cs` (`DrawActionBars`, former `SpellActionUsable` and `ActionInRange`) | Usability, range/reach, current/flash, carried-grid, equipped, and stack predicates existed as cited. They now execute once in `ComputeButtonVerdict`. |

## Deviations

| Spec § | What differed | What I did instead / BLOCKED |
|---|---|---|
| SPEC 01 Stage 1A mapping note | `ReadbackStats` names the ranges `MinRgb/MaxRgb/MinAlpha/MaxAlpha`, not `RgbLo/RgbHi/AlphaLo/AlphaHi`. | Mirrored the real byte fields into the verdict's integer range fields without synthesizing measurements. |
| SPEC 00 §4 gate | The prescribed portrait-camera command initially failed because the committed parser never populated `M2Model.PortraitCamera`. | Resolved in the separately approved core commit `931f1f2`; the exact documented counts now pass. |
| SPEC 01 Stage 1A paper doll | The paper-doll bake has no `Analyze()` call. | Skipped exactly as the spec directs. |
| SPEC 01 Stage 1E generic timestamp | The specified `IVerdict` shape exposed no timestamp even though every verdict record carries `Time` and the generic panel must format it. | Added `double Time { get; }` to `IVerdict`; positional verdict records satisfy it from the same captured timestamp without reflection or recomputation. |
| SPEC 01 Stage 1E console identity | Stage 1A explicitly keeps the legacy `[portrait]` console line unchanged and adds no `[verdict:portrait]` stdout line, while 1E asks copied rows to match a verdict console line. | Panel rows use the stable future-channel shape `HH:mm:ss.f [verdict:<channel>] <ToLine()>`; no new console output was invented. Later transition-only channels can use the same `[verdict:<channel>] <ToLine()>` payload, excluding the panel-only wall-clock prefix. |
| SPEC 01 Stage 1B capture-site citation | The spec names `Program.Casting.cs`, but the sole `CastTargetLaw.Resolve` consumption and `_net.CastSpell` send/refusal path are in `Program.ActionBars.cs`. | Instrumented the actual consumer and its existing local exits. No cast-selection, refusal, or send behavior was changed. |
| SPEC 01 Stage 1C track values | The spec requires an integer track but does not assign values. | Used the renderer's three real layers: base/locomotion `0`, combat/action `1`, and held spell `2`; documented here and shared by player and creature paths. |

## Findings (bugs noticed, NOT fixed)

- The target path retries the bounds camera whenever a drawn bake is blank, even when the initial camera was already bounds-derived. Stage 1A preserves that existing behavior byte-for-byte.
- The authored-camera parser omission and false prior gate claims were resolved/corrected in the separately approved core commit `931f1f2` before Stage 1A was committed.

## DIAGNOSIS — authored M2 cameras missing from the parsed model

### Root cause

Commit `74349395f0fbb5021c21bcb0da1d5802b39cab4c` (`Action bars, inventory, portraits, skills books`, 2026-07-30 08:22:41 -0400) introduced an incomplete authored-camera feature:

- It added the nullable `M2Model.PortraitCamera` property, now at `MSUIClient/Formats/M2Reader.cs:55-59`.
- It added the `M2PortraitCamera` record, now at `MSUIClient/Formats/M2Reader.cs:2055-2061`.
- It added `tools/portrait-camera-check`, which immediately requires that property to be populated.
- It did **not** add a camera parse or any assignment to `model.PortraitCamera`. `M2Reader.Parse` runs from `MSUIClient/Formats/M2Reader.cs:1039-1126`; its final parsed blocks are particles/transparency and attachments, and it returns the model without reading the camera arrays at header offsets `0x124..0x130`.

Therefore every successfully parsed M2 has `PortraitCamera == null`. The player path then returns false at `MSUIClient/World/Units/CharacterRenderer.cs:411-418`; the creature path does the same at `MSUIClient/World/Units/CreatureRenderer.cs:409-416`. This is not collateral from `FindOrBake`: the missing assignment was introduced in the same commit that introduced the property and gate.

The same commit added the `SYSTEM_GAMEPLAY_UI.md` claim that the gate passed at DwarfMale 1,224 / HumanMale 1,289 / Wolf 56. That claim is internally inconsistent with the committed implementation: repository-wide history contains no committed `ParsePortraitCamera`, no committed read of header offset `0x124`, and no assignment to `PortraitCamera` in `M2Reader`.

Full-history reconciliation requested before the fix confirms **case (b): the documentation claims never corresponded to committed running code**. `git log --all -S "ParsePortraitCamera" -p --` finds the symbol only in `NEXT_07_PORTRAITS.md`; it never appears in a source file. `git log --all -S "PortraitCamera" -p --` finds only commit `74349395`, where the property/type, consumers, tool, `NEXT_07` claim, and `SYSTEM_GAMEPLAY_UI` passing numbers arrived together without a parser. There is no earlier implementation to restore. Both documents now carry explicit 2026-07-30 correction notes; their coordinate/count statements remain requirements, not historical verification evidence.

### Raw-file and archive evidence

The authorized tool instrumentation prints the absolute directory, complete priority chain, supplying archive, byte count, shared-resolver byte comparison, and raw camera fields before calling `M2Reader.Parse`.

All three models resolve from `patch.MPQ`; the independent provenance read is byte-identical to `MpqMount.ReadFile` (`sharedBytesMatch=True`):

| Model | Bytes | Version | Cameras | Camera lookup | Selected camera | Parsed result |
|---|---:|---:|---|---|---|---|
| `Character\Dwarf\Male\DwarfMale.m2` | 2,134,688 | 256 | `2 @ 0x209050` | `2 @ 0x209290`, lookup[0]=0 | `0x209050` | `PortraitCamera=null` |
| `Character\Human\Male\HumanMale.m2` | 2,577,936 | 256 | `2 @ 0x2753C0` | `2 @ 0x275600`, lookup[0]=0 | `0x2753C0` | `PortraitCamera=null` |
| `Creature\Wolf\Wolf.m2` | 714,000 | 256 | `2 @ 0xAE2C0` | `2 @ 0xAE500`, lookup[0]=0 | `0xAE2C0` | `PortraitCamera=null` |

Each selected 124-byte camera record is well-formed. Examples:

- Dwarf: fov `0.785398`, clip `0.2222..27.7778`, position/target/roll tracks each have one key.
- Human: fov `0.785398`, clip `0.2222..27.7778`, position/target/roll tracks each have one key.
- Wolf: fov `0.950022`, clip `0.2222..27.7778`, position/target/roll tracks each have one key.

This proves the canonical data contains the cameras and the shared MPQ read returns those bytes. The failure is exclusively that the committed parser never consumes them.

### Archive-chain comparison: tool versus client

There is no production resolver difference and no duplicated production chain:

- Tool: `tools/portrait-camera-check/Program.cs:16` constructs `new MpqMount(data)`, where the prescribed argument resolves to `C:\Users\nico\source\repos\MSUIClient\GameData\Data`.
- Client: `MSUIClient/client-config.json` names `GameData\Data`; `ClientConfig.cs:419` resolves it against the repo root; `Program.cs:377` constructs `new MpqMount(_config.ClientDataPath)`.
- Both therefore use `MSUIClient/Formats/MpqMount.cs:63-149`, including the same first-hit read at lines 89-107 and the same private load order at lines 125-149.

Observed shared order, verbatim:

```text
patch.MPQ > patch-4.MPQ > patch-2.MPQ > terrain.MPQ > model.MPQ > backup.MPQ > base.MPQ > dbc.MPQ > fonts.MPQ > interface.MPQ > misc.MPQ > sound.MPQ > speech.MPQ > texture.MPQ > wmo.MPQ
```

The diagnostic-only provenance helper in the tool mirrors that private order solely to name the supplying archive, then verifies its bytes against the real shared resolver. It is not used by the game or by the tool's parse path.

One separate finding is visible but is not causal here: reverse full-path lexical sorting puts `patch.MPQ` before `patch-4.MPQ`, contrary to `MpqMount`'s comment that numbered patches beat the base patch. Both client and tool share that order, and the selected `patch.MPQ` files above contain valid cameras, so it does not explain this failure and was not changed.

**Named follow-up — MPQ patch precedence audit.** Risk: custom patches may be shadowed by `patch.MPQ` or may shadow content they should not. Handle separately with the real 1.12 load-order rules as authority and a two-archive resolution test; do not combine it with portrait parsing.

### Git history since 2026-07-27

Every commit in the requested window touching `M2Reader` or the camera tool is listed below. No commit in the window changed `MpqMount` or `MpqArchive`.

| Commit | M2/archive change | Relevance and current line cites |
|---|---|---|
| `c8fceddcb93956906e13b78a63781377561b48a6` — 2026-07-27 22:56 | Corrected inclusive animation-range iteration; added particle head-cell flipbook parsing. | Animation/particle only: current `M2Reader.cs:374-406`, `617-622`, `856-905`, `1744-1749`. No camera or archive-resolution change. |
| `57ee29df9ec1cb96e6b3d9b3b3bf494fc31d7e05` — 2026-07-29 16:09 | Added sequence blend time and extensive particle global-sequence/twinkle/bone-chain parsing. | Animation/particle only: current `M2Reader.cs:276`, `697-722`, `1223`, `1787-1805`, `1875`, `1900-2052`. No camera or archive-resolution change. |
| `74349395f0fbb5021c21bcb0da1d5802b39cab4c` — 2026-07-30 08:22 | Added `PortraitCamera` property/type and the camera-check tool, without adding the parser that populates the property. | **Offending commit:** current `M2Reader.cs:55-59`, `2055-2061`; absent camera work in `Parse` at `1039-1126`; tool consumes the unpopulated property. No archive-resolution change. |

The `FindOrBake` / spell-animation work does not touch the raw camera header or populate this property. It exposed no regression here; `74349395` shipped the camera consumer and test around a model field that was never wired.

### Item 5 live line — CONFIRM

Provided live scrollback:

```text
[portrait] player bake ready (subject=15001, rgb=0..255, alpha=90..255, camera=bounds, pieces=6)
```

With no preceding `authored bake blank ... retrying` line, `authoredCamera` was false before the first bake. That is exactly the `CharacterRenderer.TryGetAuthoredPortrait` false path caused by `_m2.PortraitCamera == null`; this is not an authored camera that rendered blank.

### Smallest proposed fix — IMPLEMENTED AS APPROVED

Add one bounded vanilla-v256 camera parser inside `MSUIClient/Formats/M2Reader.cs` and call it from `M2Reader.Parse`:

1. Read camera count/offset from `0x124/0x128` and camera-lookup count/offset from `0x12C/0x130`.
2. Resolve signed `cameraLookup[0]`; reject `-1`, out-of-range indices, and any 124-byte record outside the file.
3. Parse the selected record's fov/far/near, position track + base, target track + base, and roll track. For these canonical specimens each track has one static key; use the existing vanilla flat-track bounds conventions rather than inventing a second animation format.
4. Assign exactly one `M2PortraitCamera` to `model.PortraitCamera` and leave models without a valid lookup as null.
5. Re-run the unchanged gate and require the documented 1,224 / 1,289 / 56 results before committing Stage 1A.

No MPQ resolver change was needed. The approved bounded parser shipped in core commit `931f1f2`; it preserves diagonal FOV at parse time, applies the vertex-identical `(x, z, -y)` basis to eye/target, accepts only absent/single static keys, and leaves malformed/animated camera tracks null for bounds fallback.

## Console evidence

### Build gate

```text
Build succeeded.
MSUIClient/Engine/UI/GlueAdditive.cs(141,28): warning CA2014 (pre-existing)
1 Warning(s)
0 Error(s)
```

### Combat-wire gate

```text
combat/movement/targeting foundation checks passed
```

### Portrait-camera gate

```text
Character\Dwarf\Male\DwarfMale.m2: vertices=3246, inFront=3246, inside=1224
  transformed renderer path inside=1224
Character\Human\Male\HumanMale.m2: vertices=3159, inFront=3159, inside=1289
  transformed renderer path inside=1289
Creature\Wolf\Wolf.m2: vertices=557, inFront=557, inside=56
  transformed renderer path inside=56
portrait camera check passed
```

No real `[verdict:portrait]` line is claimed: Stage 1A intentionally keeps the existing `[portrait]` console line unchanged and the new structured verdict is ring-only. A live client/debugger check remains Nico's verification boundary.

No real `[verdict:cast]` line is claimed from the offline gates. The combat-wire gate verifies the existing implicit-self, self-fallback, and selected-unit scenarios now carry `ImplicitSelf`, `SelfFallback`, and `SelectedUnit` respectively; Nico's Holy Light and cooldown checks remain the live verification boundary.

No real `[verdict:anim]` line is claimed from the offline gates. `Exact` and `BakedOnDemand` are ring-only; `Fallback`, `Missing`, and the currently unreachable `Substituted` tripwire are console-visible only when the `(unit, track, requested, played, kind)` choice changes. The live cast-and-move check remains Nico's verification boundary.

No real `[verdict:action]` line is claimed from the offline gates. The first live draw and later changes emit only on the specified `Usability | Range | Flashing | Checked` state tuple. Range still uses the original squared-distance predicate; `DistanceToTarget` adds a square root for evidence only. Nico's out-of-range, low-power, auto-attack, and same-scenario visual A/B checks remain the verification boundary.

## Live checks for Nico

1. Paste one `[portrait] player bake ...` line and confirm the HUD portrait matches
   its verdict (ready ↔ live model, BLANK ↔ stand-in).
2. Holy Light with a hostile wolf selected → paste the `[verdict:cast]` line
   (expect `SelfFallback`) and confirm the heal landed on you.
3. Drain mana, walk out of range, start auto-attack → paste the three
   `[verdict:action]` transition lines; confirm each matched the visual state.
4. Cast, then move immediately → paste any `[verdict:anim]` lines (expect none at
   `Missing`/`Substituted`); confirm locomotion resumed instantly.
