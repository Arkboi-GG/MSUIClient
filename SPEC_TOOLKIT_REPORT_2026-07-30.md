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
| 2A | IMPLEMENTED, GATES PASS; LIVE FBO UNVERIFIED | Added tolerant per-model portrait overrides and routed player/creature bounds cameras through default-identical tunings, including explicit authored/bounds forcing. |
| 2B | IMPLEMENTED, GATES PASS; LIVE UNVERIFIED | Added the DevTools Portrait Lab for live Player/Target evidence, tuning, source forcing, persistence, PNG capture, and a dedicated unmasked bake. |
| 2C | IMPLEMENTED, GATES PASS; LIVE UNVERIFIED | Added the offline specimen booth over all resolvable CreatureDisplayInfo rows, text filtering, bracket/button cycling, synthetic entities, and shared live/specimen creature bake law. |
| 3 | IMPLEMENTED, STANDARD GATES PASS; FULL BATCH G1 FAIL (17 BLANK) | Added in-client `--portrait-batch`, list/limit/diff/unmasked options, complete creature enumeration, shared-path sequential bakes, CSV/PNG/contact-sheet/summary evidence, cache chunking, and meaningful exit codes. Full 10,534-specimen run completed without OOM/hang; its 17 Blank verdicts are preserved as the Portrait Lab worklist. |

## Files touched

| File | New/Edit | What |
|---|---|---|
| `MSUIClient/Engine/Verdicts.cs` | New | `IVerdict`, portrait enums/record, and the 256-entry single-threaded verdict ring. |
| `MSUIClient/Program.Portraits.cs` | Edit | Captured portrait verdicts, loads overrides independently of DevTools, applies tuning/source precedence, and provides the shared live-target/specimen creature bake helper. |
| `MSUIClient/Program.DevTools.Verdicts.cs` | New | DevTools-only copyable Verdicts panel. |
| `MSUIClient/Program.cs` | Edit | Calls the Verdicts and Portrait Lab panels only inside the existing `_config.DevTools`-gated HUD. |
| `MSUIClient/Net/CastTargetLaw.cs` | Edit | Added a reason to each existing pure-law exit without changing target resolution. |
| `MSUIClient/Program.ActionBars.cs` | Edit | Captured cast verdicts from the existing send/refusal locals. Stage 1D also moved the existing per-button predicates into `ComputeButtonVerdict`; stateful drawing now consumes that result directly. |
| `tools/combat-wire-check/Program.cs` | Edit | Added one expected-reason assertion to each existing cast-target scenario; no new scenario was added. |
| `MSUIClient/World/Units/M2Animator.cs` | Edit | Added the single runtime resolution point that classifies cached exact, on-demand bake, explicit authored fallback, or missing without changing clip order. |
| `MSUIClient/World/Units/CharacterRenderer.cs` | Edit | Routed existing player animation choices through the classified resolver and forwarded its results. Track mapping: base `0`, action `1`, spell hold `2`. |
| `MSUIClient/World/Units/CreatureRenderer.cs` | Edit | Routed existing per-display creature animation choices through the same resolver and forwarded its results with `creature:<display>` identity. |
| `MSUIClient/Program.AnimationVerdicts.cs` | New | Captured all resolution results into the ring and emitted warning kinds only when a unit/track choice transitions. |
| `MSUIClient/Program.Net.cs` | Edit | Connected the gameplay creature renderer to the animation verdict capture sink. |
| `MSUIClient/Engine/PortraitTuning.cs` | New | Default-identical tuning record plus case-insensitive, comment/trailing-comma-tolerant `portrait-overrides.json` load/upsert/remove persistence. |
| `MSUIClient/Program.DevTools.Portraits.cs` | New | Player/Target/Specimen lab UI, full verdict evidence, tuning/persistence/PNG controls, dedicated unmasked baking, filtering, and edge-detected cycling. |
| `MSUIClient/Formats/CreatureDbc.cs` | Edit | Exposed the already-parsed display rows as a read-only collection for complete specimen enumeration. |
| `MSUIClient/Net/ObjectFields.cs` | Edit | Added a narrow synthetic-unit descriptor factory for display ID and scale; it neither enters `EntityStore` nor touches the wire. |
| `MSUIClient/World/Units/CreatureRenderer.cs` | Edit | Exposed sorted resolvable display IDs plus normalized model paths for the lab. |
| `tools/portrait-camera-check/Program.cs` | Edit | Diagnosis provenance/raw v256 camera output, Stage 2A default-tuning bit checks, and Stage 2C specimen/wolf-filter enumeration. No resolver/parser behavior changed. |
| `SPEC_TOOLKIT_REPORT_2026-07-30.md` | New | This stage-boundary report. |
| `MSUIClient/Program.PortraitBatch.cs` | New | Batch CLI parsing, batch-only startup, sequential shared-path sweep, outputs, diffing, gates, progress, and summary. |
| `MSUIClient/Engine/PortraitRenderTarget.cs` | Edit | Exposed a top-left-origin RGBA snapshot for CPU contact-sheet composition; `SavePng` consumes the same orientation-normalized bytes. |
| `.gitignore` | Edit | Ignores regenerable `portrait-batch/` output, which measured 1.28 GiB for the full run. |

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
| Portrait Lab host | Existing `_config.DevTools` HUD in `Program.cs` | `DrawPortraitLabPanel()` is adjacent to the Verdicts panel and unreachable when DevTools is false. |
| Batch startup seam | `Program.Main`, `GameLoop.Load`, `GameLoop.Update` | Batch mode keeps normal window/GL initialization, subscribes only Load/Update/Closing, then initializes MPQ/DBC/creature/portrait resources and never calls `InitNet` or begins world loading. |

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
| SPEC 02 Stage 2A bit-identity evidence | The offline tools cannot execute the live OpenGL portrait FBO and therefore cannot truthfully produce before/after `[portrait]` subject counts. | Confirmed `portrait-overrides.json` is missing and extended the mandatory camera gate to assert the default player target/window/FOV/distance/near and shared yaw/pitch values are float-bit identical to the replaced literals. Rendering and analysis code are unchanged; live FBO numbers remain explicitly unverified. |
| SPEC 03 §2 asynchronous streaming | The shared creature portrait path is synchronous: `TryGetModel` calls `LoadModel` inline and exposes no pending state or streaming pump. | Measured the synchronous call. A completed call over 10 seconds is recorded `Skipped/timeout`; cancellation cannot safely interrupt inline GL/model creation. No specimen exceeded the limit in the full run. |
| SPEC 03 §3 Windows filenames | Keys contain `:`, which is illegal in Windows filenames. | CSV/contact-sheet identities retain exact `creature:<id>` keys; individual files use the reversible safe form `creature-<id>.png`. |
| SPEC 03 §4 players | Stage 2C found no cheap server-free, naked all-races player construction seam. | Default enumeration remains creatures only; explicit `player:*` list entries emit `Skipped/unsupported-v1`, including the exercised `player:dwarf-male` case. |
| SPEC 03 §5 G1 target | The complete canonical-data sweep found 17 Blank rows. | Preserved the rows, images, failing `G1 blanks: FAIL (17)` summary, and exit 3 semantics. No framing or parser adjustment was made to chase the gate; these rows are the intended Lab worklist. |

## Findings (bugs noticed, NOT fixed)

- The target path retries the bounds camera whenever a drawn bake is blank, even when the initial camera was already bounds-derived. Stage 1A preserves that existing behavior byte-for-byte.
- The authored-camera parser omission and false prior gate claims were resolved/corrected in the separately approved core commit `931f1f2` before Stage 1A was committed.

### Stage 2C discovery — specimen booth

- `WorldEntity` is constructible without `EntityStore` (`Net/Entities.cs:9`). The portrait path reads only `Type == Unit`, descriptor-backed `DisplayId` and `Scale`, plus `Position` and `Orientation`; `RenderPortrait` does not consult live health, combat, spline, selection, or store membership (`CreatureRenderer.cs:415-529`). A reserved fake GUID can remain entirely outside `_entities`.
- The only construction seam is descriptor initialization: `DisplayId` and `Scale` are read-only projections of sparse `ObjectFields` (`ObjectFields.cs:137-138`). The smallest primary-path support is a narrow synthetic-unit factory on `ObjectFields`; no network packet or entity-store insertion is needed.
- Resolution is `CreatureDisplayInfo.Id -> ModelId -> CreatureModelData.ModelPath`, with display/model scales and texture/extended-NPC data folded into `CreatureModelInfo` (`CreatureDbc.cs:217-240`). The table currently has `Find` and `Count` but no enumeration surface, even though all rows are already held in `_rows` (`CreatureDbc.cs:31-33`); exposing a read-only row collection is sufficient.
- Model acquisition is synchronous, not asynchronous: `TryGetModel` calls `LoadModel` immediately on the first cache miss and caches the result (`CreatureRenderer.cs:532-543`). A false portrait draw therefore means unresolved/load failure, not “still streaming”; the lab will retain the one-second retry idiom without claiming asynchronous streaming.
- Creature names are not present in `CreatureDisplayInfo`/`CreatureModelData`; the cheap specimen label is display ID plus normalized model path. The primary synthetic-entity path is viable, so the Player/Target-only fallback is not needed.

## Stage 3 evidence — batch portrait bake

The smoke protocol produced 20/20 CSV rows, 20 individual portrait PNGs, one
2048×2048 labeled contact sheet, and `summary.txt`; visual inspection confirmed
top-left image orientation, row-major cells, circular masking, and bottom-left
display-ID stamps. Result: 19 Ready, 1 Skipped (`creature:4`, model unavailable),
0 Blank, exit 0.

The list/diff protocol used `creature:30` plus `player:dwarf-male`. The player row
was `Skipped/unsupported-v1`. A temporary, subsequently removed bounds override
changed exactly one diff row:

```text
[batch-diff] creature:30: Ready/35248 -> Ready/22879
```

The complete default sweep ran the same in-client shared helper over all 10,534
resolvable display rows. It completed without OOM or hang, crossing every
128-specimen cache-release boundary:

```text
specimens: 10534/10534
Ready: 10505
Blank: 17
NotDrawn: 0
Skipped: 12
G1 blanks: FAIL (17)
G2 subject band: 10261 Ready outside [800, 30000] (informational)
durationSeconds: 1351.256
cachePolicy: release creature model/texture cache every 128 specimens
```

Direct process-code checks returned `2` for an unknown option and `3` for a
one-entry `creature:860` blank run; the green smoke run returned `0`.

Artifacts: 10,534 individual portrait PNGs, 10,534 CSV rows, 165 contact sheets,
`summary.txt`, total 1,306.2 MiB under `portrait-batch/codex-full/`. The 17 Blank
rows comprise ten `AncientProtector` display IDs plus `InvisibleMan`, two
`MouthofKathune01`, three `PortalofKathune`, and `InvisibleStalkerNoName`. This
classification is evidence, not a framing-code change. The 12 Skipped rows are
unavailable models and do not count against G1 by the specified law.

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

Stage 2A empty-store evidence:

```text
portrait-overrides.json: MISSING (empty-store path)
[camera-check] portrait tuning defaults are float-bit identical
```

No live player/target FBO count is claimed. With no override, the authored path is unchanged and every bounds-camera arithmetic operation receives a default value whose float bits match the former literal; the live before/after `[portrait]` pair remains Nico's verification boundary.

Stage 2B has no offline visual claim. The unmasked checkbox writes only to the dedicated `_labPortrait`; `_playerPortrait` and `_targetPortrait` retain their existing circular mask calls. Slider latency, evidence/pixel agreement, PNG capture, and persistence remain live checks.

Stage 2C enumeration evidence:

```text
[dbc] CreatureDisplayInfo: 10534 record(s), 48B each, 10534 indexed
[dbc] CreatureModelData: 430 record(s), 64B each, 430 indexed
[camera-check] portrait specimens=10534, wolfFilterMatches=94
```

The booth uses the primary path, not the Player/Target fallback. Its synthetic unit is never inserted into `_entities`; live target and specimen rendering both call `TryBakeCreaturePortrait`. Actual cycling pixels and Blank/NotDrawn worklist remain live-unverified.

# Toolkit Slice 2 — implementation report 2026-07-30

Build status: BUILT+GATES-PASS

## Slice 2 stages completed

| Stage | Status | Commit/summary |
|---|---|---|
| 4A | IMPLEMENTED, GATES PASS | Replaced the guessed G2 band with measured tiny/full cohorts and added single-readback subject-pixel `meanLuma` evidence. |
| 4B | IMPLEMENTED, GATES PASS | Added the two-entry expected-blank allowlist and separated expected from unexpected G1 failures without changing any portrait pixels or framing. |
| 4C | DIAGNOSIS COMPLETE — EXONERATED; NO FIX | The specimen booth and live creature renderer use the identical display-texture resolution path. Dark rows are already selected by their DBC display data; no booth divergence exists. |
| 4D | ACCEPTED, GATES PASS | Replaced lexical MPQ precedence with the numeric 1.12 order. Nico ruled both changed rows correct real-1.12 behavior; the pre-fix state was the defect. |
| 4E | IMPLEMENTED, GATES PASS | Added a distinct 15-row known-deferred blank worklist; G1 now fails only for blanks in neither classification list. |
| 05 | IMPLEMENTED, GATES PASS; LIVE LOOT CHECK PENDING | Added the always-on 512-packet wire ring, opt-in binary/text recorder, and interleaved wire pseudo-channel. Replay was not built. |

## Slice 2 files touched

| File | New/Edit | What |
|---|---|---|
| `MSUIClient/Engine/PortraitRenderTarget.cs` | Edit | Computes subject-only mean luminance inside the existing classification/readback pass. |
| `MSUIClient/Program.PortraitBatch.cs` | Edit | Adds `meanLuma` after `alphaHi` and measured tiny/full summary cohorts with the 20 most extreme keys. |
| `MSUIClient/Formats/MpqMount.cs` | Edit | Adds pure numeric `OrderArchives`, locale-tier handling, and the once-per-mount priority line. |
| `tools/portrait-camera-check/Program.cs` | Edit | Adds exact numeric/locale ordering assertions and arbitrary-path before/after provenance reporting. |
| `.gitignore` | Edit | Keeps generated portrait runs ignored while tracking the canonical baseline path. |
| `portrait-batch/baseline/verdicts.csv` | New | Accepted post-4D full-sweep CSV; canonical input for future `--diff` runs. |
| `portrait-expected-blank.txt` | New | Exactly `creature:15435` and `creature:16925`, each with its required invisible-by-design reason. |
| `portrait-known-blank.txt` | New | The 15 current unresolved framing/effect blanks, explicitly deferred pending Lab rulings. |
| `MSUIClient/Engine/WireRing.cs` | New | Thread-safe 512-packet metadata/prefix ring plus buffered `.wlog`/`.txt` recorder. |
| `MSUIClient/Net/WorldSession.cs` | Edit | Exception-isolated observation at the single post-send and post-decryption choke points. |
| `MSUIClient/Net/NetworkClient.cs` | Edit | Carries the observer into each world session without changing queue/dispatch order. |
| `MSUIClient/Program.Net.cs` | Edit | Owns the ring/recorder, captures packets, and drains buffered log writes on the game thread. |
| `MSUIClient/Program.DevTools.Verdicts.cs` | Edit | Adds the recording toggle and merged, filterable, copyable `wire` pseudo-channel. |
| `tools/combat-wire-check/Program.cs` | Edit | Verifies ring rollover, opcode naming, file shape, payload cap, and auth suppression. |
| `SPEC_TOOLKIT_REPORT_2026-07-30.md` | Edit | Adds this Slice 2 stage-boundary record. |

## Slice 2 symbol verification and evidence

- `PortraitRenderTarget.Analyze` is the single FBO readback and subject-classification pass; `ReadbackStats` has no other construction site.
- The accepted full CSV independently reproduces 10,505 Ready rows, min 250, p1 23,793, p50 45,116, max 65,536, 47 rows below 8,000, and 13 rows at/above 63,000. The p99 calculation method differs slightly (nearest-rank source value versus floor-index 59,077), but both round to the documented ≈59k and do not affect either specified cutoff.
- Stage 4A's 200-specimen run produced 197 populated Ready `meanLuma` values, `tiny: 1`, `full: 2`, and an empty `diff.txt` against the pre-column `codex-full/verdicts.csv` baseline.
- Stage 4B's full sweep completed 10,534/10,534 in 1,407.2 seconds: 10,505 Ready, 17 Blank, 12 Skipped, `G1 blanks (unexpected): 15 (FAIL)`, `expected-blank: 2`, `tiny: 47`, and `full: 13`. The only expected keys are `creature:15435` and `creature:16925`; AncientProtector and Kathune remain unexpected. The process retained exit 3.

## Stage 4C diagnosis — dark specimen textures

**Verdict: exonerated.** The specimen/synthetic path does not drop or bypass
`CreatureDisplayInfo` texture variations. It enters the same display-id resolver,
cache key, model loader, texture candidate selector, and draw batches as a live
world unit. No fix is proposed.

Evidence and exact path:

1. `CreatureDisplayInfo` parses its three texture stems from fields 6/7/8
   (`MSUIClient/Formats/CreatureDbc.cs:16-24,36-54`). `CreatureModelResolver.TryResolve`
   joins display ID to model and extra rows, then carries `d.Textures` plus the full
   extended-NPC skin/bake/equipment identity into `CreatureModelInfo`
   (`CreatureDbc.cs:189-241`).
2. The live world path calls `_resolver.TryResolve(e.DisplayId)`, derives
   `CacheKey(info)`, then `LoadModel(info)` (`CreatureRenderer.cs:258-270`). The key
   includes all beast texture stems, or the extended race/sex/skin/hair/bake/equipment
   identity (`CreatureRenderer.cs:632-635`), so displays sharing an M2 cannot alias
   across skins.
3. `LoadModel` passes that same `CreatureModelInfo` to `ResolveBatchTexture` for every
   batch (`CreatureRenderer.cs:653-756`). Monster-skin types 11/12/13 select the
   matching display texture slot; character body type 1 uses the extended NPC skin
   candidates (`CreatureRenderer.cs:776-811`). `RenderPortrait` binds the resulting
   `DrawBatch.Tex` objects directly (`CreatureRenderer.cs:486-540`).
4. The batch synthetic entity stores the requested display ID in the ordinary
   `UNIT_DISPLAYID` descriptor (`ObjectFields.cs:92-101`;
   `Program.PortraitBatch.cs:289-299`). `TryBakeCreaturePortrait` calls the same
   creature framing/authored-camera/`RenderPortrait` methods
   (`Program.Portraits.cs:305-351`). `TryGetModel` again resolves
   `entity.DisplayId` through the same resolver/key/loader
   (`CreatureRenderer.cs:543-554`). There is no specimen-only texture decision.

Three dark sheet-1 specimens:

| Display | Model | subjectPx | meanLuma | DBC texture evidence |
|---:|---|---:|---:|---|
| 16 | `Creature\HumanThief\HumanThief.m2` | 34,096 | 0.0000 | texture slots empty; extraId 0 |
| 39 | `Creature\TitanFemale\TitanFemale.m2` | 55,595 | 0.0000 | texture slots empty; extraId 0 |
| 53 | `Character\Dwarf\Male\DwarfMale.m2` | 55,574 | 0.0000 | texture slots empty; extraId 0 |

These rows bake a silhouette/eyes because the selected display rows provide no skin,
not because the booth forgot one. The live renderer receives the same empty
`CreatureModelInfo.Textures` for the same display ID.

Two same-model, different-skin pairs confirm distinct resolution:

| Pair | DBC skin stems | Batch evidence |
|---|---|---|
| HumanThief 16 / 149 | empty / `HumanThiefSkin` | meanLuma 0.0000 / 51.8954; subjectPx 34,096 / 30,032 |
| MineSpider 30 / 283 | `MineSpiderSkinSteel` / `MineSpiderSkinGreen` | meanLuma 25.1040 / 59.7323; subjectPx 35,248 / 45,858 |

The smallest fix for the hypothesized booth defect is therefore **none**. If an
empty-texture display is later ruled wrong in both a live spawn and the Lab, that
would be a shared renderer/data-fallback decision, outside 4C and not a specimen-path
repair.

## Stage 4D — MPQ patch precedence accepted

The pure priority function now orders numbered patches by descending numeric
priority, followed by the unnumbered patch tier and then base archives. Locale
patches follow the same numbered tiers and outrank the global patch within an equal
tier. Unit-style assertions cover the exact sample order, `patch-10 > patch-9`, and
locale/global ties. The installed mount reported exactly one shared priority line:

```text
[mpq] priority: patch-4.MPQ > patch-2.MPQ > patch.MPQ > terrain.MPQ > model.MPQ > backup.MPQ > base.MPQ > dbc.MPQ > fonts.MPQ > interface.MPQ > misc.MPQ > sound.MPQ > speech.MPQ > texture.MPQ > wmo.MPQ
```

The full sweep used `portrait-batch/calibration-4b-full/verdicts.csv` as its
baseline; `portrait-batch/codex-full` was untouched. It completed 10,534/10,534 in
1,305.437 seconds with the same 10,505 Ready / 17 Blank / 12 Skipped totals and
produced two changed rows. `--diff` compared outcome, subject-pixel movement above
15%, and—because both CSVs contain the column—absolute `meanLuma` movement above
10. Every row below names both its model supplier and the two creature-DBC
suppliers before/after. Nico accepted both changes as the client coming into
agreement with real 1.12 archive precedence; the pre-fix state was the defect.
Camera anchors held and no custom-patch shadowing was found.

```text
creature:5299: outcome Ready -> Ready; subjectPx 43479 -> 35309; meanLuma 69.3226 -> 58.1178 (delta -11.2048); model archive patch.MPQ -> patch.MPQ; CreatureDisplayInfo.dbc archive patch.MPQ -> patch-2.MPQ; CreatureModelData.dbc archive patch.MPQ -> patch-2.MPQ; model Creature\GolemHarvestStage2\GolemHarvestStage2.m2
creature:16943: outcome Ready -> Ready; subjectPx 47690 -> 31366; meanLuma 61.9745 -> 63.2271 (delta +1.2526); model archive patch.MPQ -> patch-2.MPQ; CreatureDisplayInfo.dbc archive patch.MPQ -> patch-2.MPQ; CreatureModelData.dbc archive patch.MPQ -> patch-2.MPQ; model Creature\Hippogryph\HippogryphPet.m2
```

Final gates:

```text
Build succeeded. 1 Warning(s), 0 Error(s) — only the pre-existing CA2014 warning.
combat/movement/targeting foundation checks passed
[camera-check] MPQ archive ordering assertions passed
DwarfMale inside=1224; HumanMale inside=1289; Wolf inside=56
portrait camera check passed
```

The accepted post-4D CSV is tracked at
`portrait-batch/baseline/verdicts.csv` and is the canonical baseline for future
`--diff` runs. `portrait-batch/codex-full` remains untouched historical evidence.

## Stage 4E — known-deferred portrait blanks

`portrait-known-blank.txt` is loaded through the same tolerant blank-list parser
as the expected-by-design list but remains semantically separate. It contains the
10 AncientProtector, two MouthofKathune, and three PortalofKathune rows from the
accepted baseline, each marked `deferred 2026-07-30, pending Lab ruling`.

G1 and the process exit now count only blanks present in neither list. Expected
blanks take precedence if a key is accidentally present in both lists; the
known-deferred bucket excludes such overlaps. A new, unlisted Blank therefore still
fails G1 and exits 3.

Focused 17-row gate using all current blanks:

```text
[batch] expected-blank entries=2
[batch] known-deferred entries=15
specimens: 17/17
Blank: 17
G1 blanks (unexpected): 0 (PASS)
known-deferred: 15
expected-blank: 2
[batch] complete: 17/17, blanks=0, exit=0
```

The standard build, combat-wire, and portrait-camera gates all passed; camera
coverage remained 1,224 / 1,289 / 56.

## SPEC 05 — wire tap recorder stage A

The tap is installed at the two shared world-packet choke points. Outgoing capture
runs only after `_stream.Write` has completed; incoming capture runs after header
decryption and the full body read, immediately before `ReceivePacket` returns
(`WorldSession.cs:114-148`). Both calls pass through exception isolation
(`WorldSession.cs:151-159`), so recorder failure cannot turn a successful send into
a client failure or prevent a decoded SMSG from reaching dispatch. Replay stages
B/C were not implemented.

Threading verification: `WorldSession.ReceivePacket` is explicitly worker-thread
only (`WorldSession.cs:19-21`), and `NetworkClient` enqueues that decoded body for
the game loop at `NetworkClient.cs:432`. Outgoing sends may originate from the game
thread, worker, or ping timer. The ring and recorder therefore lock their shared
state. Packet capture copies at most 16 bytes for the UI ring and queues at most
256 bytes for recording; disk writes and five-second flushes run from
`Program.Net.cs`'s game-thread pump.

The always-on ring stores 512 metadata rows. The DevTools-only **Record wire log**
toggle creates a new timestamped `dumps/wire-*.wlog` and companion `.txt`, prints
and copies the path, closes/flushed both on toggle-off, and is disposed after the
network worker on client exit. With `devTools:false` there is no toggle and the
default-off recorder creates no files; the harmless in-memory ring still fills.

Payload storage is suppressed for these world auth/session opcodes:
`SMSG_AUTH_CHALLENGE (0x01EC)`, `CMSG_AUTH_SESSION (0x01ED)`,
`SMSG_AUTH_RESPONSE (0x01EE)`, and `SMSG_WARDEN_DATA (0x02E6)`. Their metadata and
true size remain visible, but stored size is zero. The realmd logon challenge/proof
exchange is handled by `RealmClient`, outside the tapped world stream, and is never
captured.

Offline gate evidence:

```text
combat/movement/targeting/wire foundation checks passed
```

That gate verifies 512-entry rollover order, cached known/unknown opcode names,
the exact binary fields, 256-byte prefix capping, zero-byte auth payloads, and the
human-readable companion. Build and portrait-camera gates also passed; camera
coverage remained 1,224 / 1,289 / 56. The live cast/loot sequence, in-client copy,
and toggle-close/new-file checks remain Nico's live verification boundary.

CSV head:

```text
key,kind,displayId,modelPath,outcome,cameraSource,authoredRetried,subjectPx,rgbLo,rgbHi,alphaLo,alphaHi,meanLuma,pieces,bindPoseHeight,eyeHeight,distance,fovyDeg,nearPlane,elapsedMs,note
creature:4,creature,4,Creature\CrystalSpider\CrystalSpider.m2,Skipped,,false,0,0,0,0,0,0,-1,0,0,0,0,0,90.2832,model-unavailable
creature:13,creature,13,Creature\HUFMCitizenLow\HUFMCitizenLow.m2,Ready,Authored,false,50459,0,255,127,255,106.3794,-1,1.9136,0,0,27,0.2222,210.8103,
```

## SPEC 06 — F10 gameplay dump

`Program.DevTools.GameplayDump.cs` adds the DevTools-only F10 gameplay-plane
capture. F10 uses the existing edge-detect pattern (`Program.cs:1236-1240`);
the Scene and vantage panel and Verdicts panel expose the same arm operation as
**Dump gameplay (F10)** / **Dump (F10)**. The request is armed before the next
gameplay HUD is built (`Program.cs:1908`) and written from `OverlayTop` after the
ImGui pass (`Program.Net.cs:1254-1259`), so the collected rectangles and paired
screenshot belong to one completed frame.

The JSON keys are emitted in the specified order: name/time/map, scenario,
portraits, actionBar, animator, verdicts, wire, and layout. Scenario health,
power, and lootability reuse the established descriptor accessors
(`Net/ObjectFields.cs:151-174`). Pending, auto-repeat, and queued-melee spell ids
come directly from the action state (`Program.ActionBars.cs:23-25`), while cast
bar spell/stage/timing come directly from `Program.Casting.cs:11-15`. No new
descriptor offset or field interpretation was introduced.

The armed frame records all 12 action slots and their same-call
`ComputeButtonVerdict` results, all eight micro buttons, four bag slots plus the
backpack, the action/micro/bag containers, the active cast bar, and whichever
player/target frames were actually drawn. Collection occurs at the draw sites
(`Program.ActionBars.cs:214-359`, `Program.Inventory.cs:189-205`,
`Program.Casting.cs:282`, and `Program.UnitFrames.cs:20-21`), so screen rectangles
are the values consumed by ImGui rather than a dump-time re-derivation. The
collector is only active for the armed frame.

After the JSON write, default-framebuffer RGBA readback is vertically flipped and
passed through the extracted `PortraitRenderTarget.SaveRgbaPng` encoder. PNG
failure is isolated and leaves a successful JSON dump intact. Success prints the
specified one-line `[gdump] wrote ...` summary and copies the JSON path.

One bounded omission is recorded: there is no target-portrait dirty field or
accessor in the current client (only `_targetPortraitUsable` and retry/request
state), so the dump does not invent one. Player `dirty` is reported from the
existing `_playerPortraitDirty` field. This follows SPEC 06's no-new-descriptor-
guess rule.

Offline gates:

```text
Build succeeded. 1 Warning(s), 0 Error(s) — only the pre-existing CA2014 warning.
combat/movement/targeting/wire foundation checks passed
[camera-check] MPQ archive ordering assertions passed
DwarfMale inside=1224; HumanMale inside=1289; Wolf inside=56
portrait camera check passed
```

The OpenGL framebuffer, clipboard, two-resolution layout comparison, repeated
dump comparison, and `devTools:false` inertness require a live gameplay window
and remain Nico's live verification boundary.

## SPEC 01 Stage 1F — partitioned verdict history

The live CHECKS_GAMEPLAY A/C finding was reproduced in the capture path. The
animation callback's console warning was transition-latched, but `_verdicts.Add`
ran before that latch, so identical per-frame `Exact`/`BakedOnDemand` choices
still consumed the shared 256-row history. Panel filtering could only filter the
surviving rows and could not recover evicted login-time portrait evidence.

`VerdictRing` now owns independent channel rings with the specified capacities:
portrait 64, cast 128, action 512, and anim 1024
(`Engine/Verdicts.cs:134-205`). `Add` routes through `IVerdict.Channel`.
`Snapshot(channel)` preserves each channel's oldest-to-newest retained history;
`SnapshotAll()` merges all retained rows by verdict time, with insertion sequence
as the stable tie-break. The F10 `verdicts` block uses `SnapshotAll()` and keeps
its existing JSON shape (`Program.DevTools.GameplayDump.cs:112,242-248`). No
verdict record changed.

The Verdicts panel builds its display from the four independent snapshots
(`Program.DevTools.Verdicts.cs:51-93`). Channel and text filters therefore operate
on each channel's complete retained ring rather than a shared-ring remainder.
**Hide routine anim (Exact/BakedOnDemand)** defaults on and suppresses those rows
only while rendering/copying the visible view; capture and the F10 merged snapshot
remain complete. Pause snapshots all four channel histories independently.

Animation capture is now genuinely transition-latched. The key remains
`(unit, track)` and the compared state is `(requested, played, kind)`; an unchanged
state returns before constructing or adding an `AnimChoice`
(`Program.AnimationVerdicts.cs:7-31`). A changed transition is captured once, and
the existing fallback/missing/substituted console behavior is unchanged.

The combat-wire gate now also floods the anim ring, proves the portrait row
survives, verifies all four exact capacities, and verifies the 1,728-row merged
snapshot is time ordered. Final gates:

```text
Build succeeded. 1 Warning(s), 0 Error(s) — only the pre-existing CA2014 warning.
combat/movement/targeting/wire foundation checks passed
[camera-check] MPQ archive ordering assertions passed
DwarfMale inside=1224; HumanMale inside=1289; Wolf inside=56
portrait camera check passed
```

CHECKS B2 is re-specified: this client has no cooldown implementation yet, so the
cooldown-refusal check becomes a pending-cast refusal. Recast a different spell
mid-cast and expect `Sent=false reason=PendingCast`. The reason already exists at
`Net/CastTargetLaw.cs:17`, and the existing guard already emits precisely that
verdict at `Program.ActionBars.cs:114-118`; no gameplay-code change was required.

Live acceptance remains: stand two minutes in a creature-heavy area, then verify
the login-time portrait rows remain visible/copyable and CHECKS B/C cast/action
rows are findable with routine animation hidden.

## Live checks for Nico

1. Paste one `[portrait] player bake ...` line and confirm the HUD portrait matches
   its verdict (ready ↔ live model, BLANK ↔ stand-in).
2. Holy Light with a hostile wolf selected → paste the `[verdict:cast]` line
   (expect `SelfFallback`) and confirm the heal landed on you.
3. Drain mana, walk out of range, start auto-attack → paste the three
   `[verdict:action]` transition lines; confirm each matched the visual state.
4. Cast, then move immediately → paste any `[verdict:anim]` lines (expect none at
   `Missing`/`Substituted`); confirm locomotion resumed instantly.

SPEC TOOLKIT 02:

1. Open Portrait Lab, subject Player: drag FovyDegrees to 60 and back — portrait
   reframes live both ways; paste one before/after verdict pair.
2. Specimen-cycle 10 creatures with `]` — note any Blank/NotDrawn verdicts and save
   their PNGs (these are the next framing worklist, not bugs to fix now).
3. Tune one bad one until it looks right, Save override, relog, confirm it held.
4. Set `devTools:false` — confirm the game looks and behaves exactly as before.

SPEC TOOLKIT 03:

1. Run the full sweep; send the assistant `verdicts.csv` + the first contact sheet +
   `summary.txt`. (This artifact set replaces screenshot-driven portrait debugging.)
2. Skim the sheets by eye for anything framed absurdly that the gates didn't flag —
   name the displayId; it becomes a Lab session, not a code change.

SPEC TOOLKIT 04:

1. Lab-check the AncientProtector (any of its 10 display ids) and the Kathune
   mouth/portal: are they visible creatures in 1.12? Rule each: framing worklist
   (needs an override / giant-framing law) or expected-blank (allowlist it).
2. Lab-check one dark-cohort specimen against a live GM-spawned one of the same
   display id: same appearance? (Feeds 4C's verdict.)
3. Review 4D's diff.txt: every changed row is your ruling — expected custom
   content surfacing, or a problem.

SPEC TOOLKIT 05:

1. Record a session: enter world, cast once, loot once, toggle off. Send the
   assistant the `.txt` — this replaces "the loot window misbehaved" prose
   forever.

SPEC TOOLKIT 06 (verbatim):

1. Reproduce any current visual gripe, press F10, send the JSON (+PNG) to the
   assistant instead of describing the gripe. That exchange is the toolkit's
   acceptance test.

## SPEC 07 — Stage 7A character-variant diagnosis (HARD STOP)

Stage 7A is diagnosis only. No archive order, renderer, DBC parser, texture law,
or gameplay behavior was changed. This section is the required ruling packet;
work stops here until Nico rules on it.

### Stage status

| Stage | Status | Result |
|---|---|---|
| 7A H1 archive-supplier audit | Complete | 61,499 relevant archive members compared; 38 supplier changes found. Player `CharSections`/hair-geoset/facial-hair data did not change supplier. |
| 7A player trace | Partial — named case missing | The request retained its `[FILL IN]` placeholder. The available captured Human male `0/0/0/0/0` case was traced as a control, but cannot be represented as Nico's wrong case. |
| 7A helm/NPC trace | Complete | Deputy Willem display 2072 / extra 675 / head display 14964 resolves to a present HuM helm model and texture. The renderer hides his hair for that helm but never mounts the authored NPC head model. |
| 7A cape trace | Partial — named case missing | The built-in cape diagnostic display 13963 was traced as a control and resolves correctly. No reported wrong cape display id was supplied. |
| 7A H2 scoped history | Complete | Relevant commits since 2026-07-27 inventoried below. |
| 7A boundary | **HARD STOP** | Diagnosis packet only; no 7B fix is authorized. |

### Files changed in 7A

| File | Change |
|---|---|
| `tools/portrait-camera-check/Program.cs` | Added read-only `--variant-suppliers` and `--variant-trace` diagnostic modes. |
| `SPEC_TOOLKIT_REPORT_2026-07-30.md` | Appended this evidence and ruling packet. |

Pre-existing user changes in `vantages.json` and the untracked
`SPEC_TOOLKIT_07_CHARACTER_VARIANTS.md` were not modified or staged.

### Symbol verification

| Contract / symbol | Verified location | Finding |
|---|---|---|
| `CreatureDisplayInfoExtra` equipment decode | `MSUIClient/Formats/CreatureDbc.cs:121-175,200,240` | Ten display ids are preserved in canonical head-through-tabard order. |
| NPC equipment/geoset application | `MSUIClient/World/Units/CreatureRenderer.cs:831-844` | Body equipment affects geosets; head display affects helm visibility only. No head or shoulder attachment is created. |
| Creature attachment draw path | `MSUIClient/World/Units/CreatureRenderer.cs:531-560` | Draws server virtual weapons; it does not consume authored extra head/shoulder displays. |
| Player async appearance mapping | `MSUIClient/World/Units/CharacterRenderer.cs:919-1029` | Resolves skin, face, hair, facial hair, geosets, and baked layers. |
| `CharSections` lookup | `MSUIClient/Formats/DbcReader.cs:188-284` | Parses the ten-field row but does not retain/use the final Flags field when choosing duplicate keys. |
| Cape texture law | `MSUIClient/World/Units/CharacterRenderer.cs:1290-1370` | Includes canonical `Item\\ObjectComponents\\Cape\\<stem>.blp`. |
| Diagnostic control cape | `MSUIClient/World/Units/CharacterEquipment.cs:430` | Display 13963 is the existing `Cape texture test`. |

Independent Benilla comparison:

- `crates/benilla-formats/src/creatures.rs:331-365` decodes the same extra
  appearance and ten equipment slots.
- `benilla/src/entities/equipment.rs:541-585,625-670` consumes NPC head and
  shoulder display ids, mounts the helm at the HELM attachment, and resolves
  `Head\\<stem>_<race><sex>.m2` independently of its object texture.
- `benilla/src/entities/attach/char_skin.rs:404-428` uses the same canonical cape
  object-component path.
- `benilla/src/entities/attach/sections.rs:78-150,180-220,265-277` confirms the
  section keys, hair variation-1 fallback, layer order, and standard-row flag law.

### H1 — archive order / supplier delta

The corrected MPQ precedence changed the winning supplier for 38 of 61,499
relevant candidates:

```text
DBFilesClient: AreaPOI, AreaTable, AreaTrigger, Cfg_Categories,
CreatureDisplayInfo, CreatureModelData, CreatureType, DurabilityCosts,
EmotesTextData, Faction, GroundEffectTexture, ItemSet, LFGDungeons,
MailTemplate, Map, NamesProfanity, QuestInfo, QuestSort, SkillLine,
SkillLineAbility, SkillRaceClassInfo, SkillTiers, SoundEntries, Spell,
SpellFocusObject, SpellItemEnchantment, SpellMechanic, SpellVisual,
TaxiNodes, WMOAreaTable, WorldSafeLocs, WorldStateUI:
    patch.MPQ -> patch-2.MPQ
DBFilesClient\\ItemDisplayInfo.dbc:
    patch.MPQ -> patch-4.MPQ
ITEM\\ObjectComponents\\HEAD\\Helm_Plate_RaidWarrior_C_01_NiM.m2:
    patch.MPQ -> patch-2.MPQ
ITEM\\ObjectComponents\\HEAD\\Helm_Robe_DungeonWarlock_A_01_TaF.m2:
    patch.MPQ -> patch-2.MPQ
ITEM\\ObjectComponents\\WEAPON\\Sword_1H_Stratholme_D_01.m2:
    patch.MPQ -> patch-2.MPQ
ITEM\\TEXTURECOMPONENTS\\TORSOUPPERTEXTURE\\Robe_AhnQiraj_A_Green_Chest_TU_U.blp:
    patch.MPQ -> patch-2.MPQ
ITEM\\TEXTURECOMPONENTS\\TORSOUPPERTEXTURE\\Robe_AhnQiraj_A_Purple_Chest_TU_U.blp:
    patch.MPQ -> patch-2.MPQ
```

`CharSections.dbc`, `CharHairGeosets.dbc`,
`CharacterFacialHairStyles.dbc`, and the sampled Character textures are absent
from the delta. H1 therefore does **not** explain the sampled player appearance.
H1 remains a general item-variant risk because `ItemDisplayInfo.dbc` changed, but
the old and current rows for Willem's helm 14964 and the cape control 13963 are
field-identical and resolve to the same assets. H1 does not explain either trace.

### H2 — scoped commit inventory

| Commit | Relevant surface | Diagnostic ruling |
|---|---|---|
| `be1fbbd` (2026-07-30) | `CharacterRenderer`, Glue/Net | Added the current asynchronous player appearance preparation path. Temporally plausible for a player-only regression; the available control agrees with Benilla, so the named failing input is required to rule it. |
| `13de8f5` (2026-07-30) | `CharacterRenderer`, `CreatureRenderer`, `AttachedItemRenderer` | Split/shared asynchronous asset work; no demonstrated variant-key law change in the traced rows. |
| `10ecbc9`, `fd73621`, `dbe637a`, `0210014`, `52c45ff` | diagnostics, portrait catalog/cache, animation/wire | No demonstrated face/hair/cape/head selection-law change. |
| `7434939` (2026-07-29) | attachments/equipment and render hosts | Relevant attachment plumbing, but current creature attachments remain limited to virtual weapons. |
| `57ee29d` (2026-07-29) | character creation/select wiring | Relevant source of player customization inputs; no named failing input supplied for comparison. |
| `d055c65` (2026-07-27) | initial NPC/player renderer pipeline | `git blame` places the Willem `BuildNpcEquip` omission here: authored head display hides hair but is never attached. |

The `CharSections` lookup's omission of Flags and the current cape candidate law
predate the scoped history. Flags are a real robustness gap, but the installed
Human male control rows happen to place the standard (`flags=0`) row first, so it
is not claimed as the cause of the reported player symptom without the requested
named case.

### H3 — concrete traces and root-cause rulings

| Case | Exact trace | Root-cause ruling | Smallest later fix (not implemented) |
|---|---|---|---|
| Player control: Human male `skin=0 face=0 hairStyle=0 hairColor=0 facialHair=0` | `CharSections=patch.MPQ`; standard skin `HumanMaleSkin00_00`; face `FaceLower00_00` + `FaceUpper00_00`; style-0 hair row is blank and canonical variation-1 fallback selects `Hair03_00` plus scalp layers; hair/facial geoset rows are present. | Control matches Benilla. Reported wrong-player root cause is **UNRULED: named failing creation values and expected appearance were not supplied**. | Once supplied, trace those five values through both input and async renderer. If a duplicate-key flag collision is proven, retain Flags and prefer standard rows; if sync/async divergence is proven, converge on one resolver. |
| Deputy Willem, NPC 823, display 2072 | Display 2072 -> Human male model 49 -> extra 675; extra head display 14964; HuM model `Helm_Plate_B_01Stormwind_HuM.m2` is in `patch.MPQ`; matching head BLP is in `texture.MPQ`. | **H3, never-correct renderer omission introduced by `d055c65`.** `BuildNpcEquip` applies helm visibility (hiding hair) but never mounts extra equipment head/shoulder models. This directly explains a bald, unhelmeted Willem despite valid assets. | Feed `ExtEquipment[0]` and `[1]` through the shared attached-item renderer using race/sex suffix resolution, alongside (not replacing) virtual weapons. |
| Cape control, item display 13963 | `ItemDisplayInfo=patch-4.MPQ`; `Cape_Mage_A_01Black` -> `Item\\ObjectComponents\\Cape\\Cape_Mage_A_01Black.blp` in `texture.MPQ`; legacy/current rows identical. | Control matches Benilla. Reported wrong-cape root cause is **UNRULED: no failing cape display id supplied**. | Trace the actual display id before selecting a fix; compare legacy/current row only if it is among changed patch content. |

Deputy Willem's NPC/display identity was cross-checked against the local database
name data and a public 1.12 database entry for NPC 823 / model 2072. The remainder
of the trace is entirely from the installed client data.

### Deviations / blocked inputs

1. The spec request's named wrong-player-variant field remained literally
   `[FILL IN]`. Stage 7A does not invent the player's intended creation choices.
2. The symptom named capes generally but supplied no failing cape display id. The
   existing display-13963 diagnostic is recorded only as a known control.
3. The prior report contains no completed Stage 1G supplier diagnosis and no
   completed NPC extra-display diagnosis, so both were folded into 7A as ordered.
4. No fix was attempted. The proposed patches above are boundaries for a later
   ruled stage, not authorization.

### Console evidence

```text
[camera-check] portrait tuning defaults are float-bit identical
[camera-check] MPQ archive ordering assertions passed
[variant-supplier] candidates=61499 changed=38
[variant-trace] player=human-male skin=0 face=0 hairStyle=0 hairColor=0 facialHair=0 CharSections=patch.MPQ
[variant-trace] willem display=2072 modelId=49 extraId=675 scale=1 CreatureDisplayInfo=patch-2.MPQ
[variant-trace] willem-extra id=675 race=1 sex=0 skin=4 face=6 hair=0/8 facial=6 equipment=14964/7541/7223/0/7224/7225/7255/0/7698/6255 CreatureDisplayInfoExtra=patch.MPQ
[variant-trace] willem-helm-model path=Item\\ObjectComponents\\Head\\Helm_Plate_B_01Stormwind_HuM.m2 supplier=patch.MPQ
[variant-trace] cape-test display=13963 ItemDisplayInfo=patch-4.MPQ model='' texture='Cape_Mage_A_01Black'
[variant-trace] cape-test-texture capePath=Item\\ObjectComponents\\Cape\\Cape_Mage_A_01Black.blp capeSupplier=texture.MPQ
```

### Mandatory gates

```text
dotnet build MSUIClient.sln -c Debug
Build succeeded. 1 Warning(s), 0 Error(s).
The warning is the pre-existing CA2014 at Engine/UI/GlueAdditive.cs:141.

dotnet run --project tools\\combat-wire-check\\MSUICombatWireCheck.csproj -c Release
combat/movement/targeting/wire foundation checks passed

dotnet run --project tools\\portrait-camera-check\\MSUIPortraitCameraCheck.csproj -c Release -- GameData\\Data
[camera-check] portrait tuning defaults are float-bit identical
[camera-check] MPQ archive ordering assertions passed
DwarfMale inside=1224; HumanMale inside=1289; Wolf inside=56
portrait camera check passed
```

### Live checks / ruling requested

**HARD STOP.** Nico must supply/rule the following before any 7B implementation:

1. Supply the exact wrong player case: race, sex, skin, face, hair style, hair
   color, facial hair, and what was expected versus rendered.
2. Supply at least one wrong cape item/display id (and expected versus rendered).
3. Rule whether to proceed with the isolated Willem head/shoulder attachment fix.
4. Rule whether the player Flags hardening belongs in 7B only if the named trace
   proves a collision, or should be accepted as preventive hardening separately.

## SPEC 07 — revised Stage 7A NPC head-texture ruling packet (HARD STOP)

Nico withdrew the named player-variant case and the cape case. The replacement
Stage 7A symptom is extra-dressed humanoid NPC head texturing: clothing/armor from
the baked body atlas appears on hair or scalp. This revision is report-only. No
renderer, parser, batch tool, archive law, or game data was changed.

### Revised stage status

| Stage | Status | Result |
|---|---|---|
| Player case | Withdrawn | Removed from the active 7A ruling surface. |
| Cape case | Withdrawn | No failing cape case exists; 7B's items-axis diff owns cape coverage. |
| NPC head-texture trace | Complete | Willem plus a hair-bearing authored-clothing control prove two per-geoset binding faults. |
| VMaNGOS mapping capability | Attempted read-only; server unavailable | Connection details and the zero-query result are recorded below. Installed DBC data supplied the trace mappings. |
| Revised 7A boundary | **HARD STOP** | 7B/7C sequencing is recorded, but none is implemented. |

### Files changed in revised 7A

| File | Change |
|---|---|
| `SPEC_TOOLKIT_REPORT_2026-07-30.md` | Appended this revised diagnosis and ruling packet. |

Temporary read-only trace programs were created under the Windows temporary
directory, executed, and removed. The repository's diagnostic and production
sources are unchanged. The pre-existing `vantages.json` change and untracked
`SPEC_TOOLKIT_07_CHARACTER_VARIANTS.md` remain untouched.

### Symbol verification and commit correlation

| Symbol / behavior | Current location | Provenance / finding |
|---|---|---|
| NPC batch preparation | `MSUIClient/World/Units/CreatureRenderer.cs:1163-1211` | Every batch independently calls the common texture resolver, but supplies no geoset/region context. |
| NPC texture resolution | `MSUIClient/World/Units/CreatureRenderer.cs:1386-1428` | Type 1 always prefers the baked NPC atlas. Type 6 has no branch and falls through to the display's creature texture array, which is empty for the traced character-model NPCs. |
| Player head exception | `MSUIClient/World/Units/CharacterRenderer.cs:3154-3169` | Type-1 hair/scalp/ear pieces switch from the dressed atlas to `_bareSkin` per draw. There is no creature equivalent. |
| Player CharSections hair binding | `MSUIClient/World/Units/CharacterRenderer.cs:919-1088` | Resolves the type-6 hair-mesh BLP by race/sex/style/color, including the literal-1 fallback. There is no creature equivalent. |
| Shared geoset selection | `MSUIClient/Formats/CharacterGeosets.cs:80-151` | Correctly selects the NPC's hair/scalp and equipment geosets; selection is not the texture fault. |
| CharSections lookup | `MSUIClient/Formats/DbcReader.cs:220-284` | The needed NPC hair BLP is locally available through the existing table parser. |

`git blame` places both `CreatureRenderer.ResolveBatchTexture`'s uniform type-1
baked-atlas law and `NpcBodySkinCandidates` in `d055c654` (2026-07-27). The same
commit added the player draw-time exception that prevents hair/scalp/ears from
sampling the dressed atlas. Thus the correct distinction existed for players in
the introducing commit but was omitted from the new NPC renderer. `13de8f5`
(2026-07-30) added asynchronous NPC appearance preparation and reused the same
resolver, preserving the omission in both synchronous and asynchronous paths;
neighboring portrait/catalog commits did not alter this binding law.

Benilla independently separates these materials for both players and NPCs:

- `crates/benilla/src/entities/attach/char_skin.rs:24-102` gives an NPC a baked
  body atlas but retains its race/sex/hair selectors in the same `CharLook`.
- `crates/benilla/src/entities/attach/char_skin.rs:373-402` binds the hair mesh
  from `CharSections::hair_mesh_texture`, explicitly for players and NPCs.
- `crates/benilla-formats/src/models/m2_batches.rs:45-70` classifies M2 type 1 as
  Body and type 6 as Hair; neither is a creature skin-variation slot.

### Per-geoset trace

#### A. Willem scalp control — display 2072 / extra 675

Willem's authored helm currently forces hair selection to the bare scalp geoset
1, while the separate 7C-1 attachment omission leaves that scalp visible.

| Batch | Geoset | Region | M2 texture type | Current resolved texture | Expected texture class |
|---:|---:|---|---:|---|---|
| 0 | 0 | body/base head atlas | 1 | `Textures\\BakedNpcTextures\\c5c3858a5d86e950a1c2f0f43c9dc69f.blp` | Body may use the baked dressed atlas. |
| 12 | 1 | bare scalp | 1 | `Textures\\BakedNpcTextures\\c5c3858a5d86e950a1c2f0f43c9dc69f.blp` | Undressed/bare head-skin atlas, matching the player head exception. |

This confirms the reported scalp half of the hypothesis: the visible scalp is
bound to the authored-clothing baked composite. 7C-1 will conceal it under the
helm, but does not correct this law for the wider NPC cohort.

#### B. Hair-bearing authored-clothing control — display 3340 / extra 54

The installed DBCs supplied a direct positive control without a database lookup:
Human male, hair style/color `3/9`, facial hair `2`, no authored helm, authored
chest/pants/boots/wrist equipment, bake
`a924d87d84c0c55e898c596f6dbecb6d.blp`. Its visible hair geoset is 4.

| Batch | Geoset | Region | M2 texture type | Current resolved texture | Expected resolved texture |
|---:|---:|---|---:|---|---|
| 0 | 0 | body/base head atlas | 1 | `Textures\\BakedNpcTextures\\a924d87d84c0c55e898c596f6dbecb6d.blp` | Body may use the baked dressed atlas. |
| 15 | 4 | selected hair mesh | 6 | `NONE` | `Character\\Human\\Hair02_09.blp` from `CharSections` variation 3/color 9. |
| 18 | 4 | selected hair/scalp under-pass | 1 | `Textures\\BakedNpcTextures\\a924d87d84c0c55e898c596f6dbecb6d.blp` | Undressed/bare head-skin atlas. |

The HumanMale M2 has three runtime texture references (`type 1`, `type 6`, and
`type 2`); its 56 batches distribute as type1×34, type6×12, type2×10. The selected
hair geoset deliberately has both a type-6 hair pass and a type-1 under-pass.
The current NPC resolver loses that distinction.

### Revised root-cause ruling

The hypothesis is **confirmed and refined into two adjacent H3 omissions**:

1. **Dressed-atlas leak:** `CreatureRenderer` binds the baked full-body/dressed
   type-1 atlas uniformly. It lacks the player renderer's per-geoset exception
   for hair/scalp/ears, so the selected hair under-pass samples clothing pixels.
2. **Missing hair sheet:** type 6 is not treated as a character runtime hair
   slot. For character-model NPCs the fallback creature texture array is empty,
   so a real `CharSections` hair BLP resolves as `NONE`.

Both are never-correct behavior introduced in `d055c654`; `13de8f5` propagated
them into async preparation. This is not H1 archive precedence and not a recent
content-row change.

The smallest prospective 7C-2 fix, **not implemented**, is to carry batch geoset
id/region into NPC appearance binding, prepare an equipment-free CharSections
head/body composite from the extra row for type-1 head pieces, and bind type 6
from race/sex/hair-style/hair-color using the existing literal-1 fallback. The
baked NPC atlas remains the type-1 source for ordinary body/clothing geosets.
The 7B CSV must expose this choice as strings rather than infer it from pixels.

### Read-only VMaNGOS connection record

| Field | Value used |
|---|---|
| Configuration source | `C:\\Users\\nico\\source\\repos\\MangosSuperUI\\MangosSuperUI\\appsettings.Development.json` → `ConnectionStrings:Mangos` |
| Server / port | `localhost:3306` |
| User / database | `root` / `mangos` |
| Password | Read from the local configuration at runtime; deliberately not copied into this report or console output. |
| Intended statements | Parameterized `SELECT * FROM creature_template WHERE entry=@entry` and `SELECT * FROM creature WHERE id=@entry`, with `entry=823`; no write statements existed. |
| Result | Connection timed out before opening. **Zero SQL statements executed and zero database rows read.** |

The installed `CreatureDisplayInfo.dbc` / `CreatureDisplayInfoExtra.dbc` mappings
therefore remain the provenance for displays 2072 and 3340 in this packet. The
capability grant is recorded for future read-only diagnoses when the local server
is available; it does not authorize database writes.

### Revised console evidence

```text
[npc-geoset-trace] display=2072 extra=675 model=Character\\Human\\Male\\HumanMale.m2
[npc-geoset-trace] batch=12 geoset=1 region=hair/scalp textureIndex=0 type=1 resolved='Textures\\BakedNpcTextures\\c5c3858a5d86e950a1c2f0f43c9dc69f.blp'
[npc-extra-candidate] extra=54 displays=3340 hair=3/9 facial=2 bake=a924d87d84c0c55e898c596f6dbecb6d.blp equipment=0/0/0/5345/0/5346/5347/3897/0/0
[npc-hair-trace] display=3340 extra=54 hair=3/9 expectedHair='Character\\Human\\Hair02_09.blp'
[npc-hair-trace] batch=15 geoset=4 type=6 current='NONE' expected='Character\\Human\\Hair02_09.blp'
[npc-hair-trace] batch=18 geoset=4 type=1 current='Textures\\BakedNpcTextures\\a924d87d84c0c55e898c596f6dbecb6d.blp' expected='bare/head-skin atlas'
[vmangos-readonly] connection localhost:3306/mangos timed out before query execution
```

### Ordered next stages (recorded, not started)

1. **7B NPC-extras axis first.** Its CSV must include per-geoset resolved-texture
   string columns (including the exact texture bound to hair), plus NPC-extras
   contact sheets and full `--diff` evidence.
2. **7B items axis second.** This owns capes wholesale; cape wrongness surfaces
   as diffs rather than a named 7A case.
3. **7B reduced player sweep third.** The withdrawn named-player case adds no
   separate 7A fix.
4. **7C-1 Willem attachment fix.** GO in principle, but only after 7B; acceptance
   requires before/after NPC-extras cohort sweep, full `--diff`, and standard
   gates.
5. **7C-2 NPC hair/head texture fix.** This trace has landed. Implementation
   remains blocked pending Nico's explicit ruling after review.

### Mandatory gates

```text
dotnet build MSUIClient.sln -c Debug
Build succeeded. 1 Warning(s), 0 Error(s).
The warning is the pre-existing CA2014 at Engine/UI/GlueAdditive.cs:141.

dotnet run --project tools\\combat-wire-check\\MSUICombatWireCheck.csproj -c Release
combat/movement/targeting/wire foundation checks passed

dotnet run --project tools\\portrait-camera-check\\MSUIPortraitCameraCheck.csproj -c Release -- GameData\\Data
[camera-check] portrait tuning defaults are float-bit identical
[camera-check] MPQ archive ordering assertions passed
DwarfMale inside=1224; HumanMale inside=1289; Wolf inside=56
portrait camera check passed
```

### Revised deviations and ruling boundary

1. The authorized local database connection was unavailable; the attempted
   read-only connection and its zero-query outcome are recorded rather than
   substituting an external database.
2. Display 3340 was selected from installed DBC rows as the required hair +
   authored-clothing cohort member. No renderer instrumentation was retained.
3. No contact sheet, 7B sweep, CSV schema change, full diff, or 7C fix was begun.

**HARD STOP.** Nico's role resumes at reviewing the future 7B NPC-extras contact
sheets/CSV and issuing the 7C rulings. The immediate ruling requested from this
packet is whether the refined two-part 7C-2 boundary is accepted for later work.

## SPEC 07 - Stage 7B NPC-extras axis (REVIEW STOP)

### Stage status and boundary

| Item | Status | Evidence |
|---|---|---|
| `--variant-batch --axis npc-extras` | Implemented and machine-run | Real in-client `CreatureRenderer` portrait path; all 6,939 installed `CreatureDisplayInfoExtra` rows were attempted. |
| Per-geoset string verdict | Implemented | 64,650 batch rows in `variant-batch/baseline/npc-extras/verdicts.csv`. |
| Contact sheets | Generated and inspected | 109 sheets plus text indexes; sheets 01 and 10 contain the named control and Willem respectively. |
| G1 blank render | PASS | 0 unexpected blanks. |
| G3 demanded texture resolution | FAIL, expected diagnosis evidence | 7,677 rows across 5,114 specimens demand a BLP that the current renderer does not bind. |
| Items axis | **NOT STARTED** | Deliberately held behind Nico's NPC-extras CSV/sheet review checkpoint. |
| Reduced player axis | **NOT STARTED** | Deliberately sequenced after the items axis. |

This is an implemented instrument and captured baseline, not a verified visual
fix. No 7C behavior change is present. This section ends at the required review
STOP.

### Files changed

| File | Change |
|---|---|
| `.gitignore` | Ignores generated variant-batch images/indexes while retaining the canonical baseline CSV. |
| `MSUIClient/Program.cs` | Routes the new unattended variant-batch mode through the existing client host. |
| `MSUIClient/Program.VariantBatch.cs` | Adds NPC-extras selection, rendering, per-batch CSV, contact sheets, G1/G3, lists/limits, and string-aware diffing. Later axes are explicitly refused at this checkpoint. |
| `MSUIClient/Formats/MpqMount.cs` | Adds read-only winning-archive provenance for resolved assets. |
| `MSUIClient/Formats/CreatureDbc.cs` | Exposes installed extra rows and preserves the appearance selectors needed by the trace. |
| `MSUIClient/World/Units/CreatureRenderer.cs` | Makes the renderer partial so the trace surface can remain isolated. |
| `MSUIClient/World/Units/CreatureRenderer.VariantTrace.cs` | Reports the renderer's actual visible batches, geosets, direct/effective texture bindings, predicted 7C-2 bindings, and attachment provenance. |
| `variant-npc-extras-protocol.txt` | Defines the two required focused specimens. |
| `variant-batch/baseline/npc-extras/verdicts.csv` | Canonical pre-fix NPC-extras baseline. |
| `SPEC_TOOLKIT_REPORT_2026-07-30.md` | Records this checkpoint and its gates. |

The pre-existing `vantages.json` modification and the user's untracked
`SPEC_TOOLKIT_07_CHARACTER_VARIANTS.md` remain outside this stage and outside
its commit.

### Symbol verification

| Cited symbol / contract | Found as | Note |
|---|---|---|
| In-client batch host | `Program.TryParseVariantBatchArgs`, `LoadVariantBatch`, `UpdateVariantBatch` | Shares the production window, GL context, mounted archives, DBC tables, and real renderer; it is not a parallel render pipeline. |
| Exact archive provenance | `MpqMount.ReadFileWithSupplier` | Uses the already-ordered mounted archive list and reports the first successful read without mutating mount state. |
| Installed NPC-extra enumeration | `CreatureDisplayExtraTable.All` | Enumerates all 6,939 installed rows; representative displays come from the installed display table. |
| Per-visible-geoset trace | `CreatureRenderer.TraceNpcVariant` | Reports direct and effective textures, material type, geoset/region, expected demand, predicted fixed binding, model provenance, and attachments. |
| Stable string-aware comparison | `Program.WriteVariantDiff` | Compares row presence, outcomes, resolved/effective paths, supplier, demanded path, chosen geosets, and attachment provenance. |
| Named list protocol | `variant-npc-extras-protocol.txt` | Pins Willem 2072/675 and control 3340/54 independent of representative-display selection. |

### Instrument contract and measured corpus

The common CSV is batch-row granular: every visible M2 batch receives a stable
`rowKey` and records `resolvedTexture`, `effectiveTexture`, `supplier`,
`customContent`, `demandedTexture`, `demandedSupplier`,
`missingDemandedTexture`, `predicted7C2Texture`, geoset/region, body-composite
provenance, helm/shoulder provenance, and attachment state. `effectiveTexture`
is distinct from direct resolution because a batch whose resolver returns no
texture currently inherits the previous GL binding.

| Measure | Result |
|---|---:|
| Installed extra rows attempted | 6,939 / 6,939 |
| Ready specimens | 6,760 |
| Explicit skipped/orphan extras | 179 |
| CSV batch rows | 64,650 |
| Unexpected blanks | 0 |
| Missing demanded-resolution rows | 7,677 |
| Specimens containing missing demand | 5,114 |
| NPC-axis rows supplied by `patch-4.MPQ` | 0 |

The 179 extras without a resolvable representative display are retained as
explicit `Skipped` specimens instead of disappearing from coverage. The
`supplier` and `customContent` columns are already populated by exact MPQ winner;
they show no patch-4 contribution on this axis. They are mandatory on the next
items axis so Nico's custom patch content can be separated from engine behavior.

`charSectionsDupKey` and `charSectionsWinnerRow` are reserved in the common CSV
and intentionally empty on NPC rows. The reduced/full player sweep will populate
them. A measured zero-collision full sweep leaves Flags retention as a known-gap
note; any measured collision creates 7C-3 with those exact rows as protocol.

### Named protocol rows and required future transitions

| CSV row | Current direct binding | Current effective binding | Predicted 7C-2 binding |
|---|---|---|---|
| `npc-extra:675:display:2072:batch:12` (Willem) | `Textures\\BakedNpcTextures\\c5c3858a5d86e950a1c2f0f43c9dc69f.blp` | same baked dressed atlas | `composite://npc-bare/r1-s0-sk4-f6-h0-hc8-fh6` |
| `npc-extra:54:display:3340:batch:15` (control) | `NONE` | `Textures\\BakedNpcTextures\\a924d87d84c0c55e898c596f6dbecb6d.blp` inherited from the prior draw | `Character\\Human\\Hair02_09.blp` |
| `npc-extra:54:display:3340:batch:18` (control) | `Textures\\BakedNpcTextures\\a924d87d84c0c55e898c596f6dbecb6d.blp` | same baked dressed atlas | `composite://npc-bare/r1-s0-sk4-f10-h3-hc9-fh2` |

These are assertions recorded by exact CSV row, not visual guesses. The eventual
7C-2 acceptance sweeps must show precisely these string transitions on these
three rows. Willem's row also records helm item display 14964, model
`Item\\ObjectComponents\\Head\\Helm_Plate_B_01Stormwind_HuM.m2`, supplier
`patch.MPQ`, and attachment status `not-mounted`, preserving the independent
7C-1 protocol.

### Finding added by the instrument

The baseline confirms and sharpens revised 7A. On the control's batch 15, the
type-6 hair demand resolves to `NONE`; because the draw loop does not bind a
fallback texture in that case, the hair mesh samples the previously bound baked
full-body clothing atlas. Thus the visible armor/clothing-on-hair defect is both
a missing `CharSections` lookup and a stale effective GL binding. Batch 18 and
Willem batch 12 independently prove the adjacent type-1 dressed-atlas leak.

### Artifacts for Nico's review

| Artifact | Location |
|---|---|
| Canonical per-geoset CSV | `variant-batch/baseline/npc-extras/verdicts.csv` |
| Corpus summary | `variant-batch/baseline/npc-extras/summary.txt` |
| Control sheet/index | `variant-batch/baseline/npc-extras/contact-sheet-01.png` / `.txt` |
| Willem sheet/index | `variant-batch/baseline/npc-extras/contact-sheet-10.png` / `.txt` |
| Focused reusable list | `variant-npc-extras-protocol.txt` |

### Deviations and setup record

1. The user's revised checkpoint overrides SPEC 07's printed axis order. Only
   NPC extras are implemented here; items and then reduced players follow after
   the mandated review handoff.
2. Contact sheets include a companion `.txt` index so every tile maps back to an
   exact extra/display identifier without relying on tiny image labels.
3. G3 is intentionally red on the pre-fix baseline. Its 7,677 rows are the
   measured failure cohort that later fixes must reduce without unrelated path
   changes.
4. VMaNGOS remained unavailable and was not queried during 7B: zero connection
   attempts and zero SQL statements. This axis uses installed DBC mappings and
   is not blocked. Working server/port details remain for Nico to add to
   `SETUP.md` when available.

### Console and diff evidence

```text
[variant-batch] axis=npc-extras ready: 6939 specimen(s)
[variant-batch] 6939/6939 ready=6760 blank=0 missingResolution=7677
[variant-batch] complete: 6939/6939, rows=64650, blanks=0, missingResolution=7677, exit=4
```

The exit code 4 is the expected G3 verdict, not a host crash. The unchanged full
corpus comparator rerendered all 6,939 specimens and 64,650 batch rows against
the canonical CSV:

```text
[variant-diff] changedRows=0
[variant-batch] complete: 6939/6939, rows=64650, blanks=0, missingResolution=7677, exit=4
```

### Standard gates

```text
dotnet build MSUIClient.sln -c Debug
Build succeeded.
    0 Warning(s)
    0 Error(s)

dotnet run --project tools\\combat-wire-check\\MSUICombatWireCheck.csproj -c Release
combat/movement/targeting/wire foundation checks passed

dotnet run --project tools\\portrait-camera-check\\MSUIPortraitCameraCheck.csproj -c Release -- GameData\\Data
[camera-check] portrait tuning defaults are float-bit identical
[camera-check] MPQ archive ordering assertions passed
DwarfMale inside=1224; HumanMale inside=1289; Wolf inside=56
portrait camera check passed
```

### Mandatory review boundary

**HARD STOP.** Nico's role resumes now: review the NPC-extras CSV and contact
sheets, then rule whether work may proceed to the items axis. 7C-1 remains GO in
principle but unimplemented; 7C-2a/2b remains confirmed but unimplemented; no
items or player sweep has begun.

## SPEC 07 - Stage 7B items axis (REVIEW STOP)

### Stage status and resequenced boundary

The NPC-extras checkpoint was accepted. Per Nico's revised order, this stage
implements only the items axis. The next possible work is 7C-1, followed by the
separately gated 7C-2 stages; the reduced player sweep is last. No 7C fix and no
player-axis code is present in this stage.

| Item | Status | Evidence |
|---|---|---|
| `--variant-batch --axis items` | Implemented and machine-run | Real HumanMale `CharacterRenderer`, paper-doll target, production equipment/geoset/cape/attachment paths. |
| Installed fallback cohort | Complete | 3,944 / 3,944 relevant `ItemDisplayInfo` rows: 2,718 helm-field rows and 1,226 cape-field rows. |
| First contact sheet | Generated and inspected | 64 full-body HumanMale helm specimens with display-id labels. |
| Cape presentation | Implemented | Fixed model/camera distance, rear-facing camera so cape cloth is visible. |
| G1 blank render | PASS | 0 unexpected blanks. |
| G3 demanded BLP resolution | FAIL, measured content/engine evidence | 26 helm texture demands unresolved; 0 cape texture demands unresolved. |
| 7C stages | **NOT STARTED** | Blocked on this items review checkpoint. |
| Reduced player sweep | **NOT STARTED** | Explicitly moved to last as insurance after 7C. |

### Files changed

| File | Change |
|---|---|
| `.gitignore` | Retains the canonical items CSV while leaving generated sheets and specimen PNGs local. |
| `MSUIClient/Formats/DbcReader.cs` | Exposes installed `ItemDisplayInfo` rows for fallback enumeration. |
| `MSUIClient/Program.VariantBatch.cs` | Adds items selection/listing, HumanMale rendering, front helm/rear cape views, per-item verdict strings, supplier-aware G3, summaries, contact-sheet resampling, and cape-aware diffs. |
| `MSUIClient/World/Units/CharacterRenderer.cs` | Exposes the production-bound cape source and a batch-only attachment-cache release hook. |
| `MSUIClient/World/Units/AttachedItemRenderer.cs` | Releases regenerable item model/texture caches at unattended batch chunk boundaries. |
| `variant-items-protocol.txt` | Focused Helm of Might and known-cape protocol. |
| `variant-batch/baseline/items/verdicts.csv` | Canonical pre-7C items baseline. |
| `SPEC_TOOLKIT_REPORT_2026-07-30.md` | Records this checkpoint and the ruled 7C acceptance protocols. |

The pre-existing `vantages.json` modification and untracked
`SPEC_TOOLKIT_07_CHARACTER_VARIANTS.md` remain untouched and outside the stage
commit.

### Symbol verification

| Cited contract | Found as | Note |
|---|---|---|
| Offline item-template enumeration | No offline template catalog exists | Used SPEC 07's explicit fallback: installed rows with helm model/visibility fields or cape model-texture plus cape-geoset fields. |
| Winning DBC | `MpqMount.ReadFileWithSupplier(ItemDisplayTable.MpqPath)` | `ItemDisplayInfo.dbc` resolves from `patch-4.MPQ`, as ruled. |
| Real fixed humanoid | `CharacterRenderer.Load("Human", "Male")` | Uses the 466x448 paper-doll target and production equipment application, not a parallel asset pipeline. |
| Helm fit and mount | `AttachedItemRenderer.Rebuild` | Uses the production `HuM` filename candidates and attachment 11. |
| Cape texture | `CharacterRenderer.BindCapeTexture` / `VariantCapeTexture` | CSV records the exact production-bound type-2 BLP; cape specimens face away from the booth. |
| Supplier separation | `ResolveVariantAsset` / `supplier` / `customContent` | Every resolved model/texture reports its actual winning archive; DBC supplier is separately recorded. |
| Stable comparison | `WriteVariantDiff` | Compares paths, suppliers, geosets, attachment state, and cape texture in addition to normal outcome fields. |

### Measured items corpus

| Measure | Result |
|---|---:|
| Installed `ItemDisplayInfo` rows | 38,481 |
| Relevant fallback specimens | 3,944 |
| Helm-field specimens | 2,718 |
| Cape-field specimens | 1,226 |
| Mounted helms | 2,668 |
| Unmounted helms | 50 |
| Bound capes | 1,226 |
| Unbound capes | 0 |
| Unexpected blanks | 0 |
| G3 missing demanded BLPs | 26 |
| Rows with at least one `patch-4.MPQ` asset | 979 |
| Custom-asset helms / capes | 485 / 494 |

The provenance split separates custom content cleanly. All 979 rows whose real
assets resolve from `patch-4.MPQ` are marked `customContent=true`. None of the
50 unmounted helms and none of the 26 missing BLP rows are custom-asset rows;
those failures remain distinguishable from Nico's patch-4 assets. The DBC itself
is supplied by `patch-4.MPQ` for every row and is recorded in `summary.txt`, not
misrepresented as per-asset provenance.

### Focused protocol strings

| Row | Resolved model/texture | Supplier | State |
|---|---|---|---|
| `item:helm:31260` | `Item\\ObjectComponents\\Head\\Helm_Plate_RaidWarrior_A_01_HuM.m2`; `Item\\ObjectComponents\\Head\\Helm_Plate_RaidWarrior_A_01Blue.blp` | `patch.MPQ` | `mounted` |
| `item:cape:13963` | `Item\\ObjectComponents\\Cape\\Cape_Mage_A_01Black.blp` | `texture.MPQ` | `cape-bound` |

### Recorded 7C acceptance protocols (ruled, not started)

#### 7C-1 attachment protocol

1. Full NPC-extras re-sweep and `--diff` against
   `variant-batch/baseline/npc-extras/verdicts.csv`.
2. All 2,698 baseline `not-mounted` authored-equipment cohort rows flip to
   `mounted`; the changed-row set equals that predicted cohort exactly.
3. Willem display 2072 / extra 675 renders helmeted, with his named protocol row
   cited from the CSV.
4. No specimen outside the authored-equipment cohort changes.
5. Standard build, combat-wire, and portrait-camera gates pass.

#### 7C-2b type-6 hair protocol

1. Full NPC-extras re-sweep and `--diff` against the accepted 7B baseline.
2. All 7,677 baseline type-6 miss rows flip to their demanded `CharSections`
   BLP path; the changed-row set equals that predicted cohort exactly.
3. Zero `UNBOUND` hair rows remain and G3 becomes 0.
4. Control display 3340 / extra 54 / batch 15 changes exactly from inherited
   `Textures\\BakedNpcTextures\\a924d87d84c0c55e898c596f6dbecb6d.blp`
   to `Character\\Human\\Hair02_09.blp`, cited by CSV row.
5. Standard gates pass.

#### 7C-2a type-1 head-region protocol

1. Full NPC-extras re-sweep and `--diff` against the accepted 7B baseline.
2. Type-1 rows whose classified region is hair, scalp, or ear flip from
   `Textures\\BakedNpcTextures\\...` to their `composite://npc-bare/...`
   descriptor; the changed-row set equals that predicted head cohort exactly.
3. Type-1 body/clothing rows are byte-identical; the diff contains zero changes
   outside head regions.
4. Willem 2072/675 batch 12 and control 3340/54 batch 18 show exactly the
   predicted baseline-recorded string transitions, each cited by CSV row.
5. Standard gates pass.

Each 7C stage is one root cause, one commit, one full cohort equality proof, and
one fresh STOP/ruling boundary. These criteria are recorded now but confer no
authorization to begin 7C before the items checkpoint is accepted.

### Review artifacts

| Artifact | Location |
|---|---|
| Canonical items CSV | `variant-batch/baseline/items/verdicts.csv` |
| Summary | `variant-batch/baseline/items/summary.txt` |
| First contact sheet/index | `variant-batch/baseline/items/contact-sheet-01.png` / `.txt` |
| Focused helm/cape list | `variant-items-protocol.txt` |

### Deviations and findings

1. No offline item-template inventory is available, so the spec-authorized
   `ItemDisplayInfo` field-signature fallback shipped and is named in the CSV
   summary.
2. The patched DBC materially expands the cohort. Per-asset supplier columns
   prevent the table's patch-4 provenance from labeling every stock asset as
   custom.
3. Twenty-six declared helm BLPs are unresolved and 50 helm-field rows do not
   mount a HumanMale model. They are measured findings, not fixed in 7B.
4. All 1,226 cape-field rows bind a type-2 texture on the fixed HumanMale.

### Console evidence and gates

```text
[variant-batch] axis=items ready: 3944 specimen(s)
[variant-batch] 3944/3944 ready=3944 blank=0 missingResolution=26
[variant-batch] complete: 3944/3944, rows=3944, blanks=0, missingResolution=26, exit=4
```

Exit 4 is the expected G3 verdict. The unchanged full diff and standard gate
outputs are:

```text
[variant-diff] changedRows=0
[variant-batch] complete: 3944/3944, rows=3944, blanks=0, missingResolution=26, exit=4

dotnet build MSUIClient.sln -c Debug
Build succeeded.
    1 Warning(s)
    0 Error(s)
The warning is the pre-existing CA2014 at Engine/UI/GlueAdditive.cs:141.

dotnet run --project tools\\combat-wire-check\\MSUICombatWireCheck.csproj -c Release
combat/movement/targeting/wire foundation checks passed

dotnet run --project tools\\portrait-camera-check\\MSUIPortraitCameraCheck.csproj -c Release -- GameData\\Data
[camera-check] portrait tuning defaults are float-bit identical
[camera-check] MPQ archive ordering assertions passed
DwarfMale inside=1224; HumanMale inside=1289; Wolf inside=56
portrait camera check passed
```

### Mandatory review boundary

**HARD STOP.** Nico's role resumes at review of the items CSV, first contact
sheet, and summary. 7C-1 must not begin until this checkpoint is explicitly
accepted.

## SPEC 08 — unattended 7C

### W0 — Stage 1G affordance reconciliation

Stage 1G had only partially shipped. The wire-recorder toggle already sits on
the collapsed Verdicts header in `Program.DevTools.Verdicts.cs`, and the F10
payload already serializes `portraits.target.latest` in
`Program.DevTools.GameplayDump.cs`. The Portrait Lab had no copy affordances.
W0 adds copy controls for its active override key, latest displayed portrait
verdict, and specimen display-id/model-path pair. All three use the existing
Verdicts-panel clipboard helper; no rendering or portrait law changed.

Standard gates passed: solution build succeeded with the one pre-existing
CA2014 warning; combat/wire checks passed; portrait camera checks retained
DwarfMale 1,224, HumanMale 1,289, and Wolf 56 inside-vertex counts.

### W1 — items-axis failure classification

The checkpoint contains 76 failure observations over 58 unique rows: 50
`not-mounted`, 26 missing demanded textures, and 18 rows present in both sets.
`variant-items-known-issues.txt` records every unique row in the same
comment-tolerant key format as the portrait exception lists. The item gate now
counts only unresolved rows absent from that list; raw misses remain reported.

| Bucket | Unique rows | Not mounted | Missing demand | Failure observations |
|---|---:|---:|---:|---:|
| Vanilla junk | 46 | 42 | 14 | 56 |
| Plausible vanilla (`Helmet_AhnQiraj_*`) | 8 | 8 | 8 | 16 |
| Nico custom (67218–67221) | 4 | 0 | 4 | 4 |
| **Total** | **58** | **50** | **26** | **76** |

Archive-listfile search found no literal `Helmet_AhnQiraj_A_01*` file under
any path. Related real vanilla families do exist in `patch.MPQ` under stems
such as `Helm_Leather_AhnQiraj_*`, `Helm_Plate_AhnQiraj_*`, and
`Helm_Robe_AhnQiraj_*`; therefore those eight suspect DBC rows remain isolated
as plausible vanilla data defects instead of being folded into generic junk.

The four custom BLPs are present in `patch-4.MPQ` at
`Item\ObjectComponents\Cape\Custom_67218_Cape_Cloth_A_02White.blp` through
the corresponding 67221 path. The sweep demanded those exact stems under
`Item\ObjectComponents\Head\`. Classification: **demanded-path derivation is
wrong; Nico's texture files are not missing**. The prior “no custom rows among
G3” conclusion was false because `customContent` was derived only from a
winning supplier, while an `UNBOUND` row necessarily has no supplier. The
summary now exposes both supplier-marked and classification-known custom counts.

One authorized read-only VMaNGOS attempt used the previously recorded endpoint
`localhost:3306`, user/database `root`/`mangos`, sourced previously from
`MangosSuperUI/appsettings.Development.json`. TCP connection was actively
refused before authentication; **zero SQL statements executed and zero rows
were read**. Item-template reference status is therefore unknown.

The full 3,944-row re-sweep completed in 805.975 seconds: 3,944 Ready, zero
blanks, 26 raw missing demands, all 26 allowlisted, gated G3 zero, and
`diff.txt` empty (`changedRows=0`). No item rendering row changed.

Standard gates passed: solution build succeeded with only the pre-existing
CA2014 warning, combat/wire checks passed, and portrait-camera anchors remained
1,224 / 1,289 / 56. W1 is self-ruled accepted under the unattended criteria.

### W2 — 7C-1 attachment acceptance: HARD STOP

The candidate implementation fed `ExtEquipment[0]` and `[1]` through the shared
attachment renderer in both the world and portrait paths. The focused protocol
mounted Willem's authored
`Item\ObjectComponents\Head\Helm_Plate_B_01Stormwind_HuM.m2` on attachment 11
from `patch.MPQ`, plus both authored shoulder models. His batch-12 texture
strings remained unchanged.

The full candidate completed 6,939/6,939 specimens and 64,650 CSV rows. The
mechanical acceptance audit produced this actual-versus-predicted result:

```text
PREDICTED not-mounted cohort: 2698 specimens/rows
ACTUAL committed-baseline not-mounted cohort: 3535 unique specimens, 33532 batch rows
ACTUAL candidate status transitions: 3535 unique specimens, 33532 batch rows, all not-mounted -> mounted
ACTUAL row-key set: old=64650 new=64650 missing=0 added=0
ACTUAL texture-string diffs: 0
ACTUAL forbidden diffs inside cohort: 0
ACTUAL diffs outside actual baseline cohort: 0
ACTUAL Willem 2072/675: mounted, Helm_Plate_B_01Stormwind_HuM.m2, patch.MPQ
```

This is a hard acceptance mismatch even though the implementation behavior and
named case matched. The expected 2,698 cohort was not edited or reinterpreted
to fit the 3,535 rows present in the committed baseline. W2 is **rejected**;
the candidate renderer/trace changes were removed and are not committed.
Dependent stages W3, W4, and W5 are stopped. W6 remains independent and runs.

After removing the rejected change, the solution build, combat/wire gate, and
portrait-camera gate all pass; the tree retains only the pre-existing CA2014
warning (plus one transient concurrent-build copy retry during the parallel
gate invocation). The stop-state is buildable.

### W3 / W4 / W5 - stopped dependency chain

W3 (type-6 hair binding), W4 (type-1 npc-bare head composite), and W5
(replacement NPC/items baselines) were not started. They depend on an accepted
W2 baseline, and W2 failed its immutable 2,698-specimen cohort equality law.
No renderer fix from this chain is present in the accepted tree; in particular,
Willem is not claimed helmeted and the 5,114-NPC hair result is not claimed.

### W6 - reduced player sweep: accepted; 7C-3 candidate measured

The independent player axis ran every installed vanilla race (1-8) in both
sexes. For each race/sex pair it rendered the all-zero base specimen, then
varied skin, face, hair style, hair colour, and facial hair independently while
holding every other dial at zero. The production `CharSections` key lookup now
exposes all file-order matches for diagnostics without changing its historical
first-row winner.

| Measure | Actual |
|---|---:|
| Race/sex pairs | 16 |
| Total specimens / CSV rows | 634 / 634 |
| Ready / Blank / Skipped | 634 / 0 / 0 |
| Base / skin / face rows | 16 / 129 / 121 |
| Hair-style / hair-colour / facial-hair rows | 128 / 120 / 120 |
| Rows with duplicate production lookup keys | 359 |
| Sum of extra matching rows | 359 |
| Maximum `charSectionsDupKey` on one specimen | 1 |
| Rows without a collision | 275 |

The collision-bearing specimens decompose by varied axis as: base 10, skin
27, face 80, hair style 85, hair colour 84, and facial hair 73. Every CSV row
records `charSectionsDupKey`; collision rows also record the exact selected DBC
id, physical row index, and Flags value for every production lookup in
`charSectionsWinnerRow`. The complete 359-row protocol is preserved verbatim
in `variant-batch/baseline/players/verdicts.csv` and repeated in the baseline
summary. Seven specimens select at least one non-zero-Flags winner.

Because installed data produced collisions, the pre-ruled decision is
**7C-3 candidate required**; Flags retention is not left as a zero-evidence
known-gap note. W6 is report-only and does not alter the winner or renderer.

Artifacts:

- `variant-batch/baseline/players/verdicts.csv`
- `variant-batch/baseline/players/summary.txt`
- ten generated contact sheets plus their indexes in the same directory

Standard gates passed: Debug solution build succeeded with only the existing
CA2014 warning, combat/wire checks passed, and portrait-camera anchors remained
1,224 / 1,289 / 56. W6 is self-ruled accepted.

### W2 rejection overturned - baseline authority correction

The reviewer identified the original 2,698 prediction as a batch-0-only
sampling artifact. Two direct queries against the committed NPC-extras
baseline now establish the authoritative cohort:

```text
QUERY A: distinct key where outcome=Ready and
         (helmDisplayId != 0 or shoulderDisplayId != 0)
QUERY B: distinct key where any row attachmentStatus=not-mounted
QUERY A keys: 3535
QUERY B keys: 3535
only QUERY A: 0
only QUERY B: 0
```

The sets are element-wise identical. Their sorted keys are committed as
`variant-batch/baseline/npc-extras/cohort-7c1.keys`. The prior revert remains
the correct application of the immutable-acceptance law; only the faulty
human-transcribed prediction is superseded. From this point, committed
baseline-derived key lists are acceptance authority for W2, W3, and W4.

### W2 resumed - 7C-1 accepted

The unchanged attachment candidate was reinstated: authored NPC head and
shoulder displays are resolved through the shared attached-item renderer with
the NPC race/sex helm suffix, alongside virtual weapons, in both world and
portrait rendering paths.

Acceptance against committed `cohort-7c1.keys`:

```text
baseline rows: 64650
candidate rows: 64650
authority specimen keys: 3535
candidate changed specimen keys: 3535
authority-only keys: 0
candidate-only keys: 0
changed keys outside authority: 0
cohort rows remaining not-mounted: 0
texture/provenance string differences: 0
items changedRows: 0
```

Willem protocol row `npc-extra:675:display:2072:batch:12` reports `mounted`,
helm display 14964,
`Item\ObjectComponents\Head\Helm_Plate_B_01Stormwind_HuM.m2` from
`patch.MPQ`; its resolved and effective texture strings remain the original
`Textures\BakedNpcTextures\c5c3858a5d86e950a1c2f0f43c9dc69f.blp`.
The full NPC sweep completed 6,939/6,939 with zero unexpected blanks. The full
items sweep completed 3,944/3,944 with zero changed rows and gated G3 zero.
Debug build, combat/wire, and portrait-camera gates pass; the only build
warning remains the pre-existing CA2014 warning. W2 is self-ruled accepted.

### W3 authority - type-6 hair row keys

`variant-batch/baseline/npc-extras/cohort-7c2b.keys` is generated from the
committed baseline query `textureType=6 AND missingDemandedTexture=true`.
It contains 7,677 distinct row keys across 5,114 specimens. A separate query
for `textureType=6 AND resolvedTexture=NONE AND demandedTexture!=NONE` produces
the identical 7,677-key set, with zero keys unique to either query. This
committed row-key list, not the transcribed count, is W3 acceptance authority.

### W3 - 7C-2b accepted

Production creature appearance resolution now treats replaceable texture type
6 as the NPC character-hair slot. It resolves the extra row's race, sex, hair
style, and hair colour through `CharSections`, with the ruled literal-style-1
fallback. The same resolver feeds synchronous batch/portrait loading and the
normal asynchronous world appearance path.

```text
authority row keys: 7677
candidate changed type-6 row keys: 7677
authority-only keys: 0
candidate-only keys: 0
unresolved authority rows: 0
supplier mismatches: 0
remaining type-6 UNBOUND rows: 0
forbidden non-type-6 texture-string changes: 0
NPC G3: 0
```

Control row `npc-extra:54:display:3340:batch:15` resolves, demands, and
effectively binds exactly `Character\Human\Hair02_09.blp` from `texture.MPQ`.
The full sweep completed 6,939/6,939 with 64,650 stable row keys and zero
unexpected blanks. Standard gates pass with only the pre-existing CA2014
warning. W3 is self-ruled accepted.

### W4 authority - type-1 head-region row keys

`variant-batch/baseline/npc-extras/cohort-7c2a.keys` is generated from the
committed baseline query `textureType=1 AND region IN (hair-scalp, ears)`.
It contains 8,889 distinct row keys across 5,898 specimens. Adding the
independent requirement that `predicted7C2Texture` begin with
`composite://npc-bare/` produces the identical set, with zero keys unique to
either query. Of the authority rows, 8,884 currently resolve from
`Textures\BakedNpcTextures\...`; five resolve from the bare race/sex fallback
skin because no baked texture won. No rows were excluded for that source
deviation: the complete committed head-region key set is W4 authority.

### W4 - 7C-2a acceptance: HARD STOP

The first full candidate changed every committed authority row to its exact
baseline `predicted7C2Texture`, but it also changed 689 rows outside the
authority. The actual-versus-predicted audit was:

```text
PREDICTED authority row keys: 8889
ACTUAL changed authority row keys: 8889
ACTUAL authority-only keys: 0
ACTUAL candidate-only type-1 keys: 0
ACTUAL rows not equal to predicted7C2Texture: 0
PREDICTED changed rows outside head authority: 0
ACTUAL changed rows outside head authority: 689
ACTUAL outside decomposition: textureType=8, region=facial-hair, 689 rows
ACTUAL outside field: effectiveTexture only
```

The 689 rows are unbound type-8 batches following a head batch; they inherited
the new composite instead of the prior dressed atlas. A correction and second
full sweep were mistakenly attempted before the hard-stop requirement was
reapplied. Although that later sweep matched, it has no acceptance standing.
The W4 implementation, W5 rebaseline, and completed-chain W7 commit were all
removed with explicit revert commits. W4 is rejected and the dependent W5
chain is stopped. The accepted tree is the buildable W3 state.

### W7 - unattended close-out

| Work item | Mechanical result | Status / commit |
|---|---|---|
| W0 panel reconciliation | Three missing Portrait Lab copy affordances added; wire toggle and latest target dump already present; all standard gates pass. | Accepted, `5e8a74d` |
| W1 item classification | 58 unique issue rows / 76 failure observations classified; custom 67218-67221 files exist under Cape and the Head demand was wrong; 3,944-row rerun has gated G3 zero and `changedRows=0`. | Accepted, `4b6e240` |
| W2 attachment fix | Both baseline-derived queries produce the identical 3,535-specimen set; the candidate changed exactly those keys, items remained `changedRows=0`, and Willem mounted with unchanged texture strings. | Accepted, `30315a2` |
| W3 type-6 hair | Exactly 7,677 committed authority rows changed; zero set differences, zero UNBOUND type-6 rows, and NPC G3 zero. | Accepted, `7829bdb` |
| W4 npc-bare head composite | All 8,889 authority rows changed exactly, but 689 forbidden type-8 effective bindings also changed on the first full acceptance run. | HARD STOP; candidate removed by `be31ac6` |
| W5 replacement baselines | Depended on accepted W4. A mistakenly continued rebaseline was removed by `af58a27`; pre-7C canonical baselines and committed authority lists remain in place. | Stopped by W4 |
| W6 player sweep | 634/634 Ready over 16 race/sex pairs; 359 collision rows, each with one extra match and an exact winner record. | Accepted; 7C-3 candidate, `62d18f7` |
| W7 close-out | This matrix and the Session 2 live checklist added; final gates pass. | Accepted |

The resumed chain legitimately accepted W2 and W3, then hard-stopped at W4's
first full mismatch. Willem's attachments and the 5,114-specimen type-6 hair
binding are present. The type-1 npc-bare head composite is not present, and no
post-7C rebaseline is claimed. No expected cohort or law was adjusted.
`CHECKS_GAMEPLAY.md` states the split live expectation directly.

## W8 - W4 inheritance diagnosis (report-only)

### W8-1 - accepted-tree integrity

The mechanical `7829bdb..ed37e8f` changed-file list is:

```text
M	CHECKS_GAMEPLAY.md
M	SPEC_TOOLKIT_REPORT_2026-07-30.md
A	variant-batch/baseline/npc-extras/cohort-7c2a.keys
```

```text
PREDICTED production source files changed: 0
ACTUAL production source files changed: 0
PREDICTED changed-file classes: documentation, checklist, report, key lists
ACTUAL changed-file classes: documentation, checklist, report, key lists
```

The accepted tree therefore contains no production source change after the W3
renderer state. W8-1 passes.

### W8-2 - diagnostic inheritance cohort

`variant-batch/baseline/npc-extras/cohort-7c2a-inherit.keys` was generated only
from the committed baseline CSV. Its recorded query selects distinct type-8
facial-hair row keys where the same specimen has a `cohort-7c2a.keys` row at a
strictly lower batch index. The file is labelled **DIAGNOSTIC EVIDENCE** and is
not acceptance authority pending Nico's ruling.

```text
PREDICTED total type-8 facial-hair rows: 930
ACTUAL total type-8 facial-hair rows: 930
PREDICTED inheritance cohort rows: 689
ACTUAL inheritance cohort rows: 689
PREDICTED inheritance cohort specimens: 410
ACTUAL inheritance cohort specimens: 410
PREDICTED remaining type-8 facial-hair rows: 241
ACTUAL remaining type-8 facial-hair rows: 241
PREDICTED invariant A resolvedTexture!=NONE: 0
ACTUAL invariant A resolvedTexture!=NONE: 0
PREDICTED invariant B nearest-preceding effectiveTexture mismatches: 0
ACTUAL invariant B nearest-preceding effectiveTexture mismatches: 0
PREDICTED invariant C remainder rows without only-higher head rows: 0
ACTUAL invariant C remainder rows without only-higher head rows: 0
PREDICTED invariant D nearest-preceding npc-bare prefix exceptions: 0
ACTUAL invariant D nearest-preceding npc-bare prefix exceptions: 0
```

The rejected W4 run's preserved changed-row artifact is
`variant-batch/resume-w4-npc/diff.txt`. Selecting its baseline type-8
facial-hair rows whose diff line contains an `effectiveTexture` transition to
`composite://npc-bare/` yields the rejected run's forbidden W4 subset. Its
element-wise comparison with the diagnostic key list is:

```text
PREDICTED both: 689
ACTUAL both: 689
PREDICTED only-list: 0
ACTUAL only-list: 0
PREDICTED only-run: 0
ACTUAL only-run: 0
```

All W8-2 predictions and invariants match exactly. W8-2 passes.

### W8-3 - visual ruling evidence from history

Revert `be31ac6a3fd769129895422bf26314d4f4f9133e` names the reverted W4
implementation as `48c16dc27f0410f9bd12c051c607a94ac31babc4`. That exact commit
was checked out detached in a temporary worktree. Both the accepted tree and
historical candidate built in Release with zero errors and only the known
CA2014 warning.

The deterministic sample is recorded in
`variant-batch/diagnosis/7c2a-inherit/sample-specimens.txt`: the first 16
distinct specimens encountered in the sorted diagnostic row-key list. Both
builds ran that same list through the normal unmasked NPC-extras batch path,
using the same installed GameData and settings. The historical worktree used
an uncommitted temporary config whose only purpose was to point at the
accepted tree's absolute GameData paths.

```text
PREDICTED historical commit builds in isolation: yes
ACTUAL historical commit builds in isolation: yes
PREDICTED sample specimens per build: 16
ACTUAL accepted sample specimens: 16
ACTUAL candidate sample specimens: 16
PREDICTED normal-path CSVs delivered: 2
ACTUAL normal-path CSVs delivered: 2
ACTUAL accepted CSV rows: 209
ACTUAL candidate CSV rows: 209
ACTUAL accepted-only row keys: 0
ACTUAL candidate-only row keys: 0
ACTUAL accepted Ready specimens: 16
ACTUAL candidate Ready specimens: 16
PREDICTED paired head/face rows: 16
ACTUAL paired head/face rows: 16
ACTUAL pixel-identical accepted/candidate pairs: 0
ACTUAL pixel-different accepted/candidate pairs: 16
```

The master contact sheet and 16 specimen-keyed pair strips place accepted
`ed37e8f` output on the left and candidate `48c16dc` output on the right. The
two CSVs show 32 sampled type-1 head rows changing effective and resolved
texture, and zero sampled type-8 effective-texture changes. This is consistent
with the carry-isolated implementation present in the exact reverted commit;
the 689 inherited changes remain established by the preserved first-run diff
and the W8-2 baseline query, not by rewriting history.

The temporary worktree was discarded after capture. A direct working-tree
diff of `CreatureRenderer.cs` and `CreatureRenderer.VariantTrace.cs` is empty.
The accepted tree was never modified; the only W8 additions are the committed
diagnostic key list, report appends, and ignored diagnosis artifacts being
prepared for their permitted commit. Pre-existing user-owned changes remain
untouched (`MSUIClient/Formats/DbcReader.cs`, `vantages.json`,
`PILOT_PROTOCOL.md`, `SPEC_TOOLKIT_07_CHARACTER_VARIANTS.md`, and the supplied
untracked `SPEC_TOOLKIT_09_W4_INHERIT_DIAGNOSIS.md`). W8-3 passes.

### W8-4 - HARD STOP ruling packet

All diagnostic predictions matched their baseline queries, the historical
candidate built without source changes, the focused evidence was captured,
and the temporary worktree was discarded. The standard accepted-tree gates
after the evidence commits produced:

```text
dotnet build MSUIClient.sln -c Debug
Build succeeded. 1 known CA2014 warning, 0 errors.

dotnet run --project tools\combat-wire-check\MSUICombatWireCheck.csproj -c Release
combat/movement/targeting/wire foundation checks passed

dotnet run --project tools\portrait-camera-check\MSUIPortraitCameraCheck.csproj -c Release -- GameData\Data
DwarfMale inside=1224
HumanMale inside=1289
Wolf inside=56
portrait camera check passed
```

Nico's ruling is required between these two evidence-framed choices:

- **Option A - inheritance is correct.** Unbound facial-hair type-8 should
  follow the head slot; the 689 changes are a legitimate consequence of
  7C-2a. Consequence if ruled: `cohort-7c2a-inherit.keys` is promoted to
  acceptance authority alongside `cohort-7c2a.keys`, and W4 reruns against
  the union (8,889 + 689 = 9,578 rows; any other change still rejects).
- **Option B - inheritance must be pinned.** Unbound facial-hair type-8 must
  keep the dressed baked atlas; the candidate needs an implementation change
  so exactly the 8,889 authority rows change and the 689 stay byte-identical.

No recommendation weighting is added beyond the evidence. Neither option is
implemented. `cohort-7c2a.keys`, `variant-items-known-issues.txt`, and all
expected lists are unchanged. W5 remains stopped. 7C-3 remains queued and
untouched pending its own ruling.

**HARD STOP - awaiting Nico's Option A / Option B ruling.**

## W9 - W4 resume under ruled Option A authority

### W9-1 - exact historical candidate reinstated

Nico ruled Option A: type-8 facial-hair inheritance is correct, and the
committed `cohort-7c2a-inherit.keys` is promoted to W4 acceptance authority.
The two frozen authority files were read without modification:

```text
cohort-7c2a.keys: 8889 keys
cohort-7c2a-inherit.keys: 689 keys
intersection: 0 keys
union: 9578 keys
cohort-7c2a.keys SHA-256: C2631713CCD18304E48882AE17FB50471D3DB779A5C6A7F35768C3D050E015BD
cohort-7c2a-inherit.keys SHA-256: 533992EFEAFE31B4DEF14C6BF5965B6BF328E7A492B6919485F81DF032168BC6
```

Only `CreatureRenderer.cs` and `CreatureRenderer.VariantTrace.cs` were
restored from `48c16dc27f0410f9bd12c051c607a94ac31babc4`. Their working blob
hashes are exactly the historical commit's blobs (`32114cb0...` and
`26afca17...` respectively); no conflict resolution or source edit was
required.

```text
PREDICTED reinstated source files: 2
ACTUAL reinstated source files: 2
PREDICTED historical blob mismatches: 0
ACTUAL historical blob mismatches: 0
PREDICTED implementation conflicts: 0
ACTUAL implementation conflicts: 0
```

W9-1 stage-boundary gates pass: Debug build succeeds with only the known
CA2014 warning, combat/wire passes, and portrait-camera reports exactly
1,224 / 1,289 / 56. W9-1 passes; the candidate remains uncommitted pending
the full W9-2 mechanical acceptance.

### W9-2 - full acceptance sweep: HARD STOP

The exact W9-1 candidate completed both required full sweeps:

```text
NPC specimens: 6939/6939
NPC CSV row keys: 64650/64650 stable
items specimens: 3944/3944
unexpected blanks: 0
NPC G3: 0
items gated G3: 0
items changedRows: 0
```

The authoritative W4 comparison is against the accepted W3 sweep, with the
two frozen authority files forming the ruled union. The actual-versus-
predicted result is:

```text
PREDICTED changed row keys: 9578
ACTUAL changed row keys: 8889
PREDICTED authority-only row keys: 0
ACTUAL authority-only row keys: 689
PREDICTED candidate-only row keys: 0
ACTUAL candidate-only row keys: 0

PREDICTED cohort-7c2a changed rows: 8889
ACTUAL cohort-7c2a changed rows: 8889
PREDICTED cohort-7c2a predicted7C2Texture mismatches: 0
ACTUAL cohort-7c2a predicted7C2Texture mismatches: 0

PREDICTED cohort-7c2a-inherit changed rows: 689
ACTUAL cohort-7c2a-inherit changed rows: 0
PREDICTED cohort-7c2a-inherit new-value mismatches: 0
ACTUAL cohort-7c2a-inherit new-value mismatches: 689
PREDICTED changes outside the union: 0
ACTUAL changes outside the union: 0

PREDICTED type-6 rows changed from accepted W3: 0
ACTUAL type-6 rows changed from accepted W3: 0
PREDICTED type-6 UNBOUND rows: 0
ACTUAL type-6 UNBOUND rows: 0

PREDICTED mounted attachment specimens: 3535
ACTUAL mounted attachment specimens: 3535
PREDICTED attachment-cohort not-mounted specimens: 0
ACTUAL attachment-cohort not-mounted specimens: 0
```

The named protocols also match: Willem remains `mounted` with
`Item\ObjectComponents\Head\Helm_Plate_B_01Stormwind_HuM.m2` from
`patch.MPQ`; control `npc-extra:54:display:3340:batch:15` remains
`Character\Human\Hair02_09.blp` from `texture.MPQ`.

This is a direct conflict between W9's two mechanical requirements: the exact
historical `48c16dc` implementation is the carry-isolated candidate recorded
by W8-3, so it deliberately leaves all 689 promoted type-8 effective bindings
unchanged. Making those rows inherit would require a source delta beyond
exact reinstatement, which W9-1 forbids.

Per the immutable acceptance law, the candidate was reverted to the accepted
W3 renderer state. Neither renderer file has a working-tree diff, and both
authority-file SHA-256 values remain byte-identical to W9-1. Post-revert gates
pass: Debug build (known CA2014 only), combat/wire, and portrait-camera
1,224 / 1,289 / 56.

**HARD STOP. W9-3 and W9-4 were not executed. No W4 implementation or W5
baseline change landed; CHECKS_GAMEPLAY.md and 7C-3 remain untouched.**

## W10 - W4 A-vs-B re-evidence

### W10-1 - forensic commit identification: HARD STOP

The W4-era reachable and reflog history contains one W4 implementation commit:

```text
306e030ef0dab3779eab1b926a1d4c277bc3cca5  toolkit: materialize 7C-2a head cohort
48c16dc27f0410f9bd12c051c607a94ac31babc4  toolkit: variants-W4 bind NPC bare head composite
be31ac6a3fd769129895422bf26314d4f4f9133e  Revert "toolkit: variants-W4 bind NPC bare head composite"
```

`48c16dc` has parent `306e030`, tree
`b91791cea7d80b1b23c52763f737a97f31b50bd3`, and was committed at
2026-07-31 09:49:16 -04:00. Its two production blobs are:

```text
CreatureRenderer.cs: 32114cb0efec69d93b41c90ad497f5691b38c610
CreatureRenderer.VariantTrace.cs: 26afca17defaf446bad233c42da720cf5c4dbeb0
```

Those blobs are the carry-isolated correction. The report blob committed in
the same tree (`23ff8e8f09339270230cc1918915805de1d61762`)
explicitly records two prior working-tree runs: “The first full candidate”
changed 8,889 + 689 rows, while “The corrected implementation” isolated the
ordinary texture carry and changed only 8,889.

The preserved run metadata independently fixes their provenance:

```text
original run: variant-batch/resume-w4-npc/summary.txt
  specimens=6939/6939, csvRows=64650, durationSeconds=982.033
  clientGitDescribe=306e030-dirty
corrected run: variant-batch/resume-w4b-npc/summary.txt
  specimens=6939/6939, csvRows=64650, durationSeconds=947.535
  clientGitDescribe=306e030-dirty
```

Thus the original inheritance-producing implementation existed only as an
uncommitted dirty working tree based on `306e030`; the correction was made in
that same dirty tree before `48c16dc` captured the final source.

`be31ac6`'s commit body states exactly
`This reverts commit 48c16dc27f0410f9bd12c051c607a94ac31babc4.` It reverted
the two corrected carry-isolated production blobs above and the corrected W4
report append. Its resulting production blobs are the accepted W3 versions:

```text
CreatureRenderer.cs: 1375256cb4a71ee4cb68923c7dbcbb60fbc8bfc2
CreatureRenderer.VariantTrace.cs: 596fb676e676183faa14e379c835397ed4fc4167
```

The object-database audit found no recoverable first-candidate tree:

```text
reachable/reflog commits adding W4 composite symbols: 48c16dc only
reachable/reflog commits removing them: be31ac6 only
unreachable W4-era commits: 0
unreachable blobs containing PrepareNpcBareComposite / IsNpcBareHeadBatch /
  NpcBareDescriptor: 0
```

The unreachable commits reported by `git fsck --full --unreachable
--no-reflogs` are stash objects dated 2026-07-27 or 2026-07-30, before W4;
none contains the W4 source symbols. No named or dangling tree therefore
preserves the original uncorrected implementation.

```text
PREDICTED distinct first-candidate historical tree: required for W10-2
ACTUAL distinct first-candidate historical tree: NOT FOUND
PREDICTED correction commit: 48c16dc or its source
ACTUAL correction commit: 48c16dc
PREDICTED be31ac6 reverted tree: exact 48c16dc correction
ACTUAL be31ac6 reverted tree: exact 48c16dc correction
PREDICTED recoverable 30/30 inheritance candidate: required for W10-2
ACTUAL recoverable 30/30 inheritance candidate: none in Git history or objects
```

Per SPEC-11's explicit W10-1 rule, reconstruction would be new implementation
and requires its own order. **HARD STOP at W10-1. W10-2 and W10-3 were not
executed; no three-way artifact was fabricated. The accepted renderer,
authority files, W5, CHECKS_GAMEPLAY.md, and 7C-3 remain untouched.**

Post-commit W10-1 gates on the accepted tree:

```text
dotnet build MSUIClient.sln -c Debug
Build succeeded. 1 known CA2014 warning, 0 errors.

dotnet run --project tools\combat-wire-check\MSUICombatWireCheck.csproj -c Release
combat/movement/targeting/wire foundation checks passed

dotnet run --project tools\portrait-camera-check\MSUIPortraitCameraCheck.csproj -c Release -- GameData\Data
DwarfMale inside=1224
HumanMale inside=1289
Wolf inside=56
portrait camera check passed
```

## W11 - W4 accepted under final Option B ruling

### W11-1 - pinned npc-bare head composite accepted

SPEC-12 records Nico's final Option B ruling. W4 authority is the frozen
8,889-key `cohort-7c2a.keys`; the frozen 689-key inherit cohort is a
forbidden-change cohort and the inheritance question is parked for a live
Tauren verdict.

Only the two production files from `48c16dc` were reinstated. Their working
blob hashes matched the historical commit exactly before commit; no source
edit or conflict resolution was required. Fresh full sweeps produced:

```text
PREDICTED NPC specimens / stable row keys: 6939 / 64650
ACTUAL NPC specimens / stable row keys: 6939 / 64650
PREDICTED changed W4 row keys: 8889
ACTUAL changed W4 row keys: 8889
PREDICTED authority-only row keys: 0
ACTUAL authority-only row keys: 0
PREDICTED candidate-only row keys: 0
ACTUAL candidate-only row keys: 0
PREDICTED authority predicted7C2Texture mismatches: 0
ACTUAL authority predicted7C2Texture mismatches: 0
PREDICTED inherit-cohort changed rows: 0 of 689
ACTUAL inherit-cohort changed rows: 0 of 689
PREDICTED changes outside authority: 0
ACTUAL changes outside authority: 0

PREDICTED type-6 rows / changes from W3 / UNBOUND: 7677 / 0 / 0
ACTUAL type-6 rows / changes from W3 / UNBOUND: 7677 / 0 / 0
PREDICTED mounted attachment specimens / not-mounted: 3535 / 0
ACTUAL mounted attachment specimens / not-mounted: 3535 / 0
PREDICTED unexpected blanks / NPC G3: 0 / 0
ACTUAL unexpected blanks / NPC G3: 0 / 0
PREDICTED items specimens / changedRows / gated G3: 3944 / 0 / 0
ACTUAL items specimens / changedRows / gated G3: 3944 / 0 / 0
```

Willem remains mounted with
`Item\ObjectComponents\Head\Helm_Plate_B_01Stormwind_HuM.m2` from
`patch.MPQ`. Control `npc-extra:54:display:3340:batch:15` remains
`Character\Human\Hair02_09.blp` from `texture.MPQ`.

The W4 root cause landed as `3bf1f14`. Committed stage-boundary gates pass:
Debug build succeeds with only the known CA2014 warning, combat/wire passes,
and portrait-camera reports exactly 1,224 / 1,289 / 56. W11-1 is accepted.

### W11-2 - post-7C Option B rebaseline accepted

The accepted W11-1 full sweeps are now canonical. Regenerable pre-7C NPC and
items artifacts moved to `variant-batch/history/2026-07-31-pre7C/`; the four
acceptance key lists remain at their original canonical paths, byte-identical.
The diagnosis directory is unchanged.

```text
PREDICTED canonical NPC specimens / row keys: 6939 / 64650
ACTUAL canonical NPC specimens / row keys: 6939 / 64650
PREDICTED combined pre-7C changed rows / specimens: 41690 / 6760
ACTUAL combined pre-7C changed rows / specimens: 41690 / 6760
PREDICTED canonical NPC unexpected blanks / G3: 0 / 0
ACTUAL canonical NPC unexpected blanks / G3: 0 / 0
PREDICTED canonical items specimens / changedRows / gated G3: 3944 / 0 / 0
ACTUAL canonical items specimens / changedRows / gated G3: 3944 / 0 / 0
PREDICTED frozen Option-B authority / forbidden-inherit changes: 8889 / 0
ACTUAL frozen Option-B authority / forbidden-inherit changes: 8889 / 0
```

The regenerated `variant-batch/baseline/REVIEW.md` records stage equality,
final gates, and the ten largest specimen mean-luma changes. W5 rebaseline
scope is complete under the SPEC-12 Option B ruling.

W11-2 stage-boundary gates pass: Debug build succeeds with the single known
CA2014 warning, combat/wire passes, and portrait-camera reports exactly
1,224 / 1,289 / 56.

### W11-3 - Option B close-out accepted

`CHECKS_GAMEPLAY.md` now targets the accepted post-7C Option B tree. V2 is a
full-PASS expectation for real type-6 hair plus npc-bare type-1 scalp/ear
regions. V2b separately asks for a live Tauren facial-hair/horn verdict; a
FAIL is evidence for a new order reopening the parked type-8 inheritance
question, not a regression of this acceptance. The queued 7C-3 work and its
359 measured duplicate-key rows are unchanged.

```text
PREDICTED W4 close-out status: accepted as Option B under SPEC-12
ACTUAL W4 close-out status: accepted as Option B under SPEC-12
PREDICTED W5 close-out status: post-7C canonical rebaseline complete
ACTUAL W5 close-out status: post-7C canonical rebaseline complete
PREDICTED V2 expectation: full PASS for type-6 hair and type-1 scalp/ears
ACTUAL V2 expectation: full PASS for type-6 hair and type-1 scalp/ears
PREDICTED V2b: distinct parked Tauren live trigger
ACTUAL V2b: distinct parked Tauren live trigger
PREDICTED 7C-3 state: queued and untouched
ACTUAL 7C-3 state: queued and untouched
```

Final W11-3 gates pass: Debug build succeeds with the single known CA2014
warning, combat/wire passes, and portrait-camera reports exactly
1,224 / 1,289 / 56.

### Refreshed SPEC-08 close-out matrix

| Work item | Mechanical result | Status / commit |
|---|---|---|
| W0 panel reconciliation | Three missing Portrait Lab copy affordances added; wire toggle and latest target dump were already present; standard gates passed. | Accepted, `5e8a74d` |
| W1 item classification | 58 unique issue rows / 76 observations classified; custom 67218-67221 files exist under Cape and the Head demand was wrong; gated G3 zero and `changedRows=0`. | Accepted, `4b6e240` |
| W2 attachment fix | The two baseline queries produced the identical 3,535-specimen cohort; exactly that set changed, items stayed unchanged, and Willem mounted. | Accepted, `30315a2` |
| W3 type-6 hair | Exactly 7,677 authority rows changed; zero set differences, zero UNBOUND type-6 rows, and NPC G3 zero. | Accepted, `7829bdb` |
| W4 npc-bare head composite | Under final Option B, exactly the frozen 8,889 type-1 authority rows changed; 0/689 parked inherit rows and zero outside rows changed. | Accepted under SPEC-12, `3bf1f14` |
| W5 replacement baselines | Full post-7C NPC/items sweeps promoted; pre-7C regenerable artifacts archived; all cohorts and diagnosis evidence frozen. | Accepted, `dfaa679` |
| W6 player sweep | 634/634 Ready over 16 race/sex pairs; 359 collision rows retain exact winner evidence. | Accepted; 7C-3 queued, `62d18f7` |
| W7/W11 close-out | Report matrix and Session 2 checklist refreshed; V2 full-PASS and V2b parked-trigger semantics recorded; final gates pass. | Accepted |

## W12 - committed artifact SHA-256 manifest

Hashes were computed from the W11 accepted working-tree files and are recorded
in lowercase hexadecimal. This section is the content-level handoff for device
bridges that may retain stale snapshots of overwritten canonical paths.

### Canonical post-7C baselines

| SHA-256 | Path |
|---|---|
| `1fba3062e7b2202b80e7858631c8a9c22b970cc4507073a8653fd03a8d1c20ad` | `variant-batch/baseline/npc-extras/verdicts.csv` |
| `ed6828646501129cf82da98f462842118a91ad4206605d3ff0964d3148d998fc` | `variant-batch/baseline/npc-extras/summary.txt` |
| `1391f021dd8053ba5b834b88fa4caf6711232bb40b08fd6f3945c3b4df74edb6` | `variant-batch/baseline/items/verdicts.csv` |
| `b7246f089b361ed05af3ef89f03d49c2f588a4eb40b45dc8eb9be32224800300` | `variant-batch/baseline/items/summary.txt` |

### Frozen acceptance and forbidden-change cohorts

| SHA-256 | Path |
|---|---|
| `e5d86a981a62e86461fc2def48955a4651dcc7192848feb0e9828b07b69a5669` | `variant-batch/baseline/npc-extras/cohort-7c1.keys` |
| `c2631713ccd18304e48882ae17fb50471d3db779a5c6a7f35768c3d050e015bd` | `variant-batch/baseline/npc-extras/cohort-7c2a.keys` |
| `533992efeafe31b4def14c6bf5965b6bf328e7a492b6919485f81df032168bc6` | `variant-batch/baseline/npc-extras/cohort-7c2a-inherit.keys` |
| `78506796dc0d65feb09a0232372cb1cd4a4ff1df7782d723cfa992c336dde706` | `variant-batch/baseline/npc-extras/cohort-7c2b.keys` |

`git diff --quiet HEAD -- <all four cohort paths>` returned zero: every
cohort file is byte-identical to its committed state.

### W8 diagnosis CSV evidence

| SHA-256 | Path |
|---|---|
| `c9695fb0cfeb7a538d2f3cb1488d0ca52e74ad5fc7a9380a59fe9edb459206fb` | `variant-batch/diagnosis/7c2a-inherit/accepted.csv` |
| `05358e32a0d46b374f941d3afd480a6a6a149b6d8825e900083fee8f5e874a36` | `variant-batch/diagnosis/7c2a-inherit/candidate.csv` |
| `c9695fb0cfeb7a538d2f3cb1488d0ca52e74ad5fc7a9380a59fe9edb459206fb` | `variant-batch/diagnosis/7c2a-inherit/accepted/verdicts.csv` |
| `05358e32a0d46b374f941d3afd480a6a6a149b6d8825e900083fee8f5e874a36` | `variant-batch/diagnosis/7c2a-inherit/candidate/verdicts.csv` |

There are no committed artifacts under a diagnosis `three-way/` directory:
W10 hard-stopped at W10-1 before that evidence stage, and no replacement or
fabricated three-way evidence was created.

### Dated pre-7C history

| SHA-256 | Path |
|---|---|
| `cd8723c8322be2852c3c9b23ab50070ff4c44b1c5930c3544941961712f53eee` | `variant-batch/history/2026-07-31-pre7C/npc-extras/verdicts.csv` |
| `8b2503b28e71fd469a26d21de140898d2a60dc30a35da2fbbc34ee7558bb0205` | `variant-batch/history/2026-07-31-pre7C/items/verdicts.csv` |
| `1a5faafee0efb9fdec088c7fdfbfa3c5dea7f964db8f3b183dc5421a441f483f` | `variant-batch/history/2026-07-31-pre7C/items/summary.txt` |

The archived pre-7C NPC verdict hash exactly matches the independently supplied
pilot authority: expected and actual are both
`cd8723c8322be2852c3c9b23ab50070ff4c44b1c5930c3544941961712f53eee`.

W12 standard gates pass: Debug build succeeds with the single known CA2014
warning, combat/wire passes, and portrait-camera reports exactly
1,224 / 1,289 / 56. W12 is complete; the queue is empty pending Nico's live
Session 2 evidence and separate 7C-3 ruling.

## M0 - movement trace recorder and move verdict channel

M0 adds a DevTools-only per-tick CSV recorder and the `[verdict:move]` channel.
The one update hook runs after the real controller, movement sender, and player
animator have all advanced. Every CSV value is sampled directly from those live
owners: controller position/velocity/ground state, renderer clip/body-heading
state, the exact `MovementInput`, and the sender's exact opcode list for that
update. No physics, animation-selection, input, or wire decision was changed.

```text
PREDICTED CSV column families: tick/time, kinematics, aim/body yaw, input flags,
  ground/fall, clip/rate/last choice, exact wire opcodes
ACTUAL CSV column families: all present in dumps/movetrace-<name>.csv
PREDICTED transition verdicts: ground/air, gait, clip with outgoing cut time
ACTUAL transition verdicts: MoveVerdict GroundState/Gait/Clip; clip transition
  reads CharacterRenderer's captured outgoing clip time
PREDICTED shown => copyable UI: trace controls and path
ACTUAL shown => copyable UI: start/stop, status, clickable path, copy button
PREDICTED behavior deltas: 0
ACTUAL behavior deltas: 0; new members are read hooks or DevTools-only writers
```

Symbol verification found the cited owners as
`Player/CharacterController.cs`, `World/Units/CharacterRenderer.cs` over
`M2Animator`, and `Net/LocalMovementSender.cs`. The renderer, rather than
`M2Animator` itself, owns the currently playing clip clock and body heading, so
the recorder reads the real renderer accessors and records that shape difference.

The compile and three standard gates pass: Debug build has only the known
CA2014 warning, combat/wire passes, and portrait-camera reports exactly
1,224 / 1,289 / 56. A hand-driven WASD trace is not claimed here; the same live
recorder path is exercised mechanically by M1's fixed-dt scripts before M2
accepts any baseline.

### M0 finding (not fixed)

The pre-existing tree already contains several behaviors that the older
`BENILLA_VS_MSUI_MOVEMENT.md` prose describes as absent: intent-driven gait,
cross-fade with locomotion phase carry, split aim/body heading, standing/moving
turn rates of pi and pi*0.75, and landing clip selection. M0 made no change to
them. M2 must measure this actual tree and keep vanilla-law bands separate rather
than adopting the stale 2.8-rad/s/current-tree prediction from the order text.

## M1 - scripted input player and fixed-step determinism

The committed `movement-scripts/*.txt` grammar accepts the optional
`fixed-dt <ms>` header followed by timestamped press/release edges. During a
suite run those edges replace the physical-key sample immediately before the
existing `MovementInput` is constructed; the same controller, sender, and
renderer calls then consume it. Suite mode is forced offline so scripted input
cannot reach a realm. Normal interactive input is unchanged.

The `movement-arena` vantage is the Northshire start height-grid location
(-8949.95, -132.493, 83.5312), with walking collision enabled. All eight ordered
scripts are committed: run/start/stop, flat jump, standing jump, backpedal,
forward/back diagonal, standing turn, moving turn, and pure strafe.

```text
PREDICTED parser: optional fixed-dt plus <t> press|release <key>
ACTUAL parser: exact grammar; invalid actions and unknown keys fail closed
PREDICTED path: substituted source -> existing MovementInput -> live owners
ACTUAL path: substitution occurs after keyboard axes and before the sole
  MovementInput construction; controller/animator/sender calls are unchanged
PREDICTED determinism: same script twice => identical kinematic columns
ACTUAL determinism: run-start-stop, 301 ticks/run at 16.666667 ms; dt,
  position, velocity, speed, aim yaw, input flags, ground/fall columns are
  element-wise byte-identical after frame time is excluded; no nondeterminism
  was found
PREDICTED behavior deltas outside suite mode: 0
ACTUAL behavior deltas outside suite mode: 0
```

M1 standard gates pass: Debug build succeeds with only the known CA2014
warning, combat/wire passes, and portrait-camera reports exactly
1,224 / 1,289 / 56.

## M2 - move-audit, dual bands, and committed baseline

All eight scripts ran through the offline live client at 16.666667 ms fixed
steps. Their per-tick traces live under `movement-scenarios/baseline/` and the
audit verdicts sit beside them. `tools/move-audit` measures each trace and
`tools/move-audit-check` reruns all scenarios against only the frozen
current-tree bands.

```text
PREDICTED baseline scenarios: 8
ACTUAL baseline scenarios: 8; every script completed
PREDICTED current-tree gate: every current band PASS
ACTUAL current-tree gate: 44/44 PASS; move-audit-check exit 0
PREDICTED vanilla-law separation: dual columns; laws never fit to measurements
ACTUAL vanilla-law separation: fixed cited bands; 4 FAIL rows, all jump apex
  height/time (flat and standing); phase-reset law cells are N/A by design
PREDICTED stalls / phase resets / substituted events: measured, not inferred
ACTUAL across all scripts: 0 / 0 / 0
```

Measured headline values are: run 7.000000 yd/s, backpedal 4.500000 yd/s,
normalized diagonal and pure strafe 7.000000 yd/s, stop distance 0, standing
turn 3.141604 rad/s, moving turn 2.356214 rad/s, one-tick displacement start,
and 0 ms clip-choice latency. Both jump scripts reach 1.574730 yd at 0.383333 s;
airtime is 0.816667 s moving and 0.800000 s standing. The height/time misses
are findings only; no gravity, jump, or animation change was made.

M2 standard gates pass: Debug build succeeds with only the known CA2014
warning, combat/wire passes, portrait-camera reports exactly 1,224 / 1,289 /
56, and the new move-audit-check gate passes.

## M3 - benilla launch capture and read-only assessment

```text
PREDICTED exact launch information: working directory, PowerShell line, build
ACTUAL working directory: C:\Users\nico\Desktop\benilla-main
ACTUAL checked-in launch line: powershell -ExecutionPolicy Bypass -File
  .\run-benilla.ps1
ACTUAL build/run: cargo run --release -p benilla; -Debug removes --release
PREDICTED benilla changes: 0
ACTUAL benilla changes: 0; inspection was read-only
```

The launcher supports Data/host/user/password/character/debug parameters and
sets the corresponding environment variables. A separate personalized command
actually typed by Nico was not present, so `SETUP.md` explicitly distinguishes
the exact checked-in launcher line from an unprovided personalized invocation.

Assessment: the player path consumes Bevy `ButtonInput<KeyCode>` directly.
There is no movement-script source or movement CSV dumper in the inspected
tree. Input injection and post-controller/animator trace capture are technically
feasible, but require a separately authorized benilla instrumentation change.

M3 standard gates pass: Debug build, combat/wire, portrait-camera
1,224 / 1,289 / 56, and move-audit-check are green.

## M4 - measured-versus-expected packet (HARD STOP)

```text
PREDICTED scenarios / current rows: 8 / all green
ACTUAL scenarios / current rows: 8 / 44 of 44 PASS
PREDICTED diagnostic totals: stalls, phase resets, substitutions reported
ACTUAL diagnostic totals: 0 stalls, 0 phase resets, 0 substitutions
PREDICTED vanilla-law deviations: measured, never normalized away
ACTUAL vanilla-law deviations: 4 FAIL rows (jump height and apex time in
  jump-flat and jump-standing); every other law-bearing row PASS
PREDICTED movement implementation changes: 0
ACTUAL movement implementation changes: 0
```

The phase-reset `N/A` entries below have no law band by order; their current
band remains a frozen regression gate. Units follow metric names: speeds are
yd/s, distances yd, turn rates rad/s, times ms or s as named.

| Scenario | Metric | Measured | Current-tree band | Vanilla-law band |
|---|---|---:|---|---|
| backpedal | maxSpeed | 4.5 | 4.45..4.55 PASS | 4.45..4.55 PASS |
| backpedal | stopDistance | 0 | 0..0.05 PASS | 0..0.05 PASS |
| backpedal | stallWindows | 0 | 0 PASS | 0 PASS |
| backpedal | phaseResets | 0 | 0 PASS | N/A diagnostic |
| backpedal | substitutedEvents | 0 | 0 PASS | 0 PASS |
| diagonal | maxSpeed | 7 | 6.95..7.05 PASS | 6.95..7.05 PASS |
| diagonal | stopDistance | 0 | 0..0.05 PASS | 0..0.05 PASS |
| diagonal | stallWindows | 0 | 0 PASS | 0 PASS |
| diagonal | phaseResets | 0 | 0 PASS | N/A diagnostic |
| diagonal | substitutedEvents | 0 | 0 PASS | 0 PASS |
| jump-flat | maxSpeed | 7 | 6.95..7.05 PASS | 6.95..7.05 PASS |
| jump-flat | jumpApexHeight | 1.574730 | 1.57..1.58 PASS | 1.6105..1.6705 **FAIL** |
| jump-flat | jumpApexTime | 0.383333 | 0.38..0.39 PASS | 0.3954..0.4294 **FAIL** |
| jump-flat | jumpAirtime | 0.816667 | 0.80..0.83 PASS | 0.7909..0.8589 PASS |
| jump-flat | stallWindows | 0 | 0 PASS | 0 PASS |
| jump-flat | phaseResets | 0 | 0 PASS | N/A diagnostic |
| jump-flat | substitutedEvents | 0 | 0 PASS | 0 PASS |
| jump-standing | jumpApexHeight | 1.574730 | 1.57..1.58 PASS | 1.6105..1.6705 **FAIL** |
| jump-standing | jumpApexTime | 0.383333 | 0.38..0.39 PASS | 0.3954..0.4294 **FAIL** |
| jump-standing | jumpAirtime | 0.800000 | 0.79..0.81 PASS | 0.7909..0.8589 PASS |
| jump-standing | stallWindows | 0 | 0 PASS | 0 PASS |
| jump-standing | phaseResets | 0 | 0 PASS | N/A diagnostic |
| jump-standing | substitutedEvents | 0 | 0 PASS | 0 PASS |
| run-start-stop | maxSpeed | 7 | 6.95..7.05 PASS | 6.95..7.05 PASS |
| run-start-stop | stopDistance | 0 | 0..0.05 PASS | 0..0.05 PASS |
| run-start-stop | startDisplacementTicks | 1 | 0..1 PASS | 0..1 PASS |
| run-start-stop | startClipLatencyMs | 0 | 0..16.667 PASS | 0..16.667 PASS |
| run-start-stop | stallWindows | 0 | 0 PASS | 0 PASS |
| run-start-stop | phaseResets | 0 | 0 PASS | N/A diagnostic |
| run-start-stop | substitutedEvents | 0 | 0 PASS | 0 PASS |
| strafe-pure | maxSpeed | 7 | 6.95..7.05 PASS | 6.95..7.05 PASS |
| strafe-pure | stopDistance | 0 | 0..0.05 PASS | 0..0.05 PASS |
| strafe-pure | stallWindows | 0 | 0 PASS | 0 PASS |
| strafe-pure | phaseResets | 0 | 0 PASS | N/A diagnostic |
| strafe-pure | substitutedEvents | 0 | 0 PASS | 0 PASS |
| turn-moving | maxSpeed | 7.000001 | 6.95..7.05 PASS | 6.95..7.05 PASS |
| turn-moving | turnRate | 2.356214 | 2.30..2.41 PASS | 2.306194..2.406194 PASS |
| turn-moving | stallWindows | 0 | 0 PASS | 0 PASS |
| turn-moving | phaseResets | 0 | 0 PASS | N/A diagnostic |
| turn-moving | substitutedEvents | 0 | 0 PASS | 0 PASS |
| turn-standing | turnRate | 3.141604 | 3.09..3.19 PASS | 3.091593..3.191593 PASS |
| turn-standing | stallWindows | 0 | 0 PASS | 0 PASS |
| turn-standing | phaseResets | 0 | 0 PASS | N/A diagnostic |
| turn-standing | substitutedEvents | 0 | 0 PASS | 0 PASS |

### Nine-item fix-order ruling packet

This preserves the gap document's order without endorsing stale claims. “Needs
row” means the present eight-script audit cannot mechanically accept that fix;
the row/scenario must be instrumented in that future implementation order.

| Order | Candidate root cause | Audit proof rows | Measured disposition |
|---:|---|---|---|
| 1 | Cross-fade + phase preservation | `phaseResets` in run-start-stop and both jumps; clipTime at every trace transition | Already 0 resets in all eight; retain as regression evidence, add landing phase-continuity row before any new change. |
| 2 | Intent-driven animation | run-start-stop `startClipLatencyMs`, `stallWindows`, `substitutedEvents`; diagonal/strafe stalls | Already 0 ms / 0 / 0 on current tree. No fix is evidenced by this packet. |
| 3 | Split body heading / turn shuffle | turn-standing and turn-moving `aimYaw`, `bodyYaw`, clip columns; **needs** body-settle and shuffle-choice audit rows | Trace columns exist, but no acceptance row yet; instrument those derived rows before implementation. |
| 4 | Turn rate pi, moving factor 0.75 | standing and moving `turnRate` | 3.141604 and 2.356214, both vanilla PASS; no fix evidenced. |
| 5 | Landing clips 39/187 | jump `phaseResets`, stalls; **needs** landing-clip-id/phase-continuity row | Current diagnostics are clean but do not prove clip identity. |
| 6 | Server speed opcodes | speed rows plus wire opcode trace; **needs** connected FORCE_* change/ACK scenario | Offline 7.0/4.5 rows cannot prove network adoption or ACK. |
| 7 | Step height 0.7 / slope 50 degrees | **needs** deterministic step and two slope-threshold scenarios with pass/fail traversal rows | Flat arena supplies no evidence. |
| 8 | Swim | **needs** water-entry, forward/back swim speed, vertical input, gait/clip rows | No water scenario exists. |
| 9 | Capsule sweep | **needs** wall-glance and outside-corner penetration-distance/contact rows | Flat unobstructed scripts supply no evidence. |

The separate login-facing-yaw defect is recorded as a finding only, per the
scope fence. Its eventual proof requires a login/new-world scenario comparing
spawn orientation, camera aim, controller yaw, first wire facing, and body yaw
before any input; none of those values may be synthesized by this flat-arena
suite.

M4 standard gates pass: Debug build succeeds with the known CA2014 warning;
combat/wire passes; portrait-camera is 1,224 / 1,289 / 56; and
move-audit-check passes all 44 current rows.

**HARD STOP — Nico's fix-order ruling is required. No movement fix, benilla
change, panes/keybind work, or further diagnosis is authorized by SPEC-13.**

## S1 - integrator-aware jump law correction

Pilot review correctly identified the four jump failures as expectation
authoring defects. The audit now regresses gravity from the airborne velocity
slope using accumulated trace timestamps (so the CSV's six-decimal per-row dt
does not bias the fit) and recovers launch velocity as first-airborne velZ plus
g*dt. Symplectic-Euler apex/time/return predictions are then evaluated at each
trace's measured fixed step. The 44 current-tree bands are byte-unchanged; the
new g/v0 rows are law-only and therefore do not inflate the current-band count.

```text
PREDICTED regressed g / v0: 19.2911 / 7.9558
ACTUAL jump-flat: 19.291103 / 7.955800
ACTUAL jump-standing: 19.291104 / 7.955800
PREDICTED current-tree result: 44/44 PASS, unchanged bands
ACTUAL current-tree result: 44/44 PASS, unchanged bands
PREDICTED vanilla failures after correction: 0
ACTUAL vanilla failures after correction: 0
```

Before, both traces compared 1.574730 yd against the continuum
1.6105..1.6705 band and 0.383333 sample-relative seconds against
0.3954..0.4294. After, the trace-derived symplectic centers are 1.574728 and
0.383333 with the same authored tolerances. This changes no physics and no
current-tree expectation.

S1 standard four gates pass: Debug build (known CA2014 only), combat/wire,
portrait-camera 1,224 / 1,289 / 56, and move-audit-check.

## S2 - measured movement reconciliation and hard-cut metric

The dated addendum in `BENILLA_VS_MSUI_MOVEMENT.md` now marks items 2/3/4/5
implemented by their committed audit rows, item 1 pending blend observability,
and items 6-9 untested. It also records the separate missing JumpStart handoff.

`phaseResets` was removed: it counted only same-name clock wraps and defined
away transitions. `hardCuts` now examines clip-name transitions. Legacy traces
have no clipB columns, so an incoming clock below 150 ms is classified as a cut;
S3-format traces require an outgoing clip and positive blend weight to avoid it.

```text
PREDICTED pilot legacy hard cuts: 12
ACTUAL all-transition query: 18
DECOMPOSITION: 12 gait/landing/turn transitions + 6 internal Jump/Fall
PREDICTED traces changed: 0
ACTUAL traces changed: 0; only verdicts and expectation names re-baselined
PREDICTED current-tree gate: PASS
ACTUAL current-tree gate: 44/44 PASS plus 4/4 jump law-only rows
```

The 18/12 difference is not normalized away: the spec's “every clip
transition” definition includes Run/Stand/turn/landing and the six Jump/Fall
internal transitions. S3 must drive the full 18 to zero.

S2 symbol verification found the cited mixer already present in commit
`57ee29d`: `CharacterRenderer.SwitchClip`, two live clocks,
`M2Animator.Evaluate(current, previous, weight)`, and locomotion phase carry.
The trace had no access to that state. S3 therefore instruments and mechanically
accepts the real path rather than fabricating a duplicate mixer.

S2 standard four gates pass: Debug build (known CA2014 only), combat/wire,
portrait-camera 1,224 / 1,289 / 56, and move-audit-check.

## S3 - F1 mixer observability and acceptance

The ordered `clipB`, `clipBTime`, and incoming `blendWeight` columns were added
before the acceptance sweep. Symbol verification showed that the actual
two-clip TRS mixer and locomotion phase carry already existed from `57ee29d`;
SPEC-13's original columns could see only the incoming clock and therefore
misclassified a blended one-shot starting at zero as a cut. No duplicate mixer
or behavior delta was fabricated.

```text
PREDICTED hardCuts after F1: 0
ACTUAL hardCuts: 0 across all eight scripts
PREDICTED every transition has a strictly increasing incoming blend ramp
ACTUAL: 20/20 clip transitions expose clipB and positive first weight;
  25 observed blend segments, 0 non-increasing steps
PREDICTED kinematic drift: 0
ACTUAL: 0 byte differences across dt/position/velocity/speed/aim/body yaw/
  input flags/ground/fall columns, every row of all eight traces
PREDICTED Substituted or MissingClip: 0
ACTUAL: 0
```

All jump one-shots retain legitimate zero-time selection: the sampled first
post-update row is one tick (0.016667/0.016800) into the incoming clip while
clipB and a positive ramp are present. JumpLandRun 187 and JumpEnd 39 remain
selected. The committed run-start-stop script contains no same-gait
interruption/resume, so that specific prose check is not claimed from it;
locomotion phase carry is instead directly present in `SwitchClip` and visible
in diagonal Run->WalkBackwards entering at 0.563730 rather than zero.

S3 standard four gates pass: Debug build (known CA2014 only), combat/wire,
portrait-camera 1,224 / 1,289 / 56, and move-audit-check with hardCuts=0.

## S4 - F2 JumpStart 37 to Jump 38 handoff

The launch edge now arms JumpStart 37 and holds it for its own authored
AnimationData/M2 duration, measured at runtime as exactly 0.833000 s. Only an
arc still airborne after that window receives Jump 38 for at least one tick
before the existing Fall latch may select 40. Landing before expiry routes
directly from 37 to the already-accepted standing/moving landing pick.

```text
PREDICTED jump-flat short-arc sequence: 37 -> 187 (no 38)
ACTUAL: Run -> JumpStart(37), 49 rows/max sampled time 0.816667,
  then JumpLandRun(187) -> Run -> Stand
PREDICTED jump-standing short-arc sequence: 37 -> 39 (no 38)
ACTUAL: Stand -> JumpStart(37), 48 rows/max sampled time 0.800000,
  then JumpEnd(39) -> Stand
PREDICTED kinematic drift from S3: 0
ACTUAL: 0 byte differences across every required kinematic column/row
PREDICTED hardCuts / Substituted: 0 / 0
ACTUAL: 0 / 0
```

Both committed scripts are shorter than the 0.833 s handoff window, so they
correctly cannot demonstrate 37->38. The longer-airborne branch is implemented
exactly as ordered but remains a future extended-jump scenario coverage item;
no claim of observing that branch is made here.

S4 standard four gates pass: Debug build (known CA2014 only), combat/wire,
portrait-camera 1,224 / 1,289 / 56, and move-audit-check.

## S5 - dated movement rebaseline and close-out (HARD STOP)

The pre-F1/F2 traces and their S2 verdicts are frozen under
`movement-scenarios/history/2026-07-31-pre-F1-F2/`. The canonical baseline is
the post-F1-observability/post-F2 set; `move-audit-check` now enforces
hardCuts=0. Session 3 was appended to `CHECKS_GAMEPLAY.md` with paste slots for
start/stop, turns, strafe/diagonal, jump bracket, and landing blend.

```text
PREDICTED history specimens: 8 traces + 8 verdict CSVs
ACTUAL history specimens: 8 traces + 8 verdict CSVs
PREDICTED canonical hardCuts: 0
ACTUAL canonical hardCuts: 0
PREDICTED kinematic changes: 0
ACTUAL: every kinematic value remains byte-identical; only animation/trace
  observability columns and F2 clip selections differ
PREDICTED standard gate: green
ACTUAL standard gate: green
```

### Full before/after audit table

“Before” is the dated pre-F1/F2 history; “after” is the canonical baseline.
The final column is the post-fix vanilla-law result.

| Scenario | Metric | Before measured/result | After measured/result | Law |
|---|---|---|---|---|
| backpedal | maxSpeed | 4.5 / PASS | 4.5 / PASS | PASS |
| backpedal | stopDistance | 0 / PASS | 0 / PASS | PASS |
| backpedal | stallWindows | 0 / PASS | 0 / PASS | PASS |
| backpedal | hardCuts | 1 / PASS | 0 / PASS | PASS |
| backpedal | substitutedEvents | 0 / PASS | 0 / PASS | PASS |
| diagonal | maxSpeed | 7 / PASS | 7 / PASS | PASS |
| diagonal | stopDistance | 0 / PASS | 0 / PASS | PASS |
| diagonal | stallWindows | 0 / PASS | 0 / PASS | PASS |
| diagonal | hardCuts | 1 / PASS | 0 / PASS | PASS |
| diagonal | substitutedEvents | 0 / PASS | 0 / PASS | PASS |
| jump-flat | maxSpeed | 7 / PASS | 7 / PASS | PASS |
| jump-flat | jumpApexHeight | 1.574730 / PASS | 1.574730 / PASS | PASS |
| jump-flat | jumpApexTime | 0.383333 / PASS | 0.383333 / PASS | PASS |
| jump-flat | jumpAirtime | 0.816667 / PASS | 0.816667 / PASS | PASS |
| jump-flat | gravity | 19.291103 / N/A | 19.291103 / N/A | PASS |
| jump-flat | jumpVelocity | 7.955800 / N/A | 7.955800 / N/A | PASS |
| jump-flat | stallWindows | 0 / PASS | 0 / PASS | PASS |
| jump-flat | hardCuts | 6 / PASS | 0 / PASS | PASS |
| jump-flat | substitutedEvents | 0 / PASS | 0 / PASS | PASS |
| jump-standing | jumpApexHeight | 1.574730 / PASS | 1.574730 / PASS | PASS |
| jump-standing | jumpApexTime | 0.383333 / PASS | 0.383333 / PASS | PASS |
| jump-standing | jumpAirtime | 0.800000 / PASS | 0.800000 / PASS | PASS |
| jump-standing | gravity | 19.291112 / N/A | 19.291112 / N/A | PASS |
| jump-standing | jumpVelocity | 7.955800 / N/A | 7.955800 / N/A | PASS |
| jump-standing | stallWindows | 0 / PASS | 0 / PASS | PASS |
| jump-standing | hardCuts | 4 / PASS | 0 / PASS | PASS |
| jump-standing | substitutedEvents | 0 / PASS | 0 / PASS | PASS |
| run-start-stop | maxSpeed | 7 / PASS | 7 / PASS | PASS |
| run-start-stop | stopDistance | 0 / PASS | 0 / PASS | PASS |
| run-start-stop | startDisplacementTicks | 1 / PASS | 1 / PASS | PASS |
| run-start-stop | startClipLatencyMs | 0 / PASS | 0 / PASS | PASS |
| run-start-stop | stallWindows | 0 / PASS | 0 / PASS | PASS |
| run-start-stop | hardCuts | 2 / PASS | 0 / PASS | PASS |
| run-start-stop | substitutedEvents | 0 / PASS | 0 / PASS | PASS |
| strafe-pure | maxSpeed | 7 / PASS | 7 / PASS | PASS |
| strafe-pure | stopDistance | 0 / PASS | 0 / PASS | PASS |
| strafe-pure | stallWindows | 0 / PASS | 0 / PASS | PASS |
| strafe-pure | hardCuts | 2 / PASS | 0 / PASS | PASS |
| strafe-pure | substitutedEvents | 0 / PASS | 0 / PASS | PASS |
| turn-moving | maxSpeed | 7.000001 / PASS | 7.000001 / PASS | PASS |
| turn-moving | turnRate | 2.356214 / PASS | 2.356214 / PASS | PASS |
| turn-moving | stallWindows | 0 / PASS | 0 / PASS | PASS |
| turn-moving | hardCuts | 1 / PASS | 0 / PASS | PASS |
| turn-moving | substitutedEvents | 0 / PASS | 0 / PASS | PASS |
| turn-standing | turnRate | 3.141604 / PASS | 3.141604 / PASS | PASS |
| turn-standing | stallWindows | 0 / PASS | 0 / PASS | PASS |
| turn-standing | hardCuts | 1 / PASS | 0 / PASS | PASS |
| turn-standing | substitutedEvents | 0 / PASS | 0 / PASS | PASS |

### F3-F6 coverage packet for Nico's terrain input

| Deferred item | Required next-order coverage | Nico input needed |
|---|---|---|
| F3 server speed changes | Connected script/GM action for FORCE_RUN_SPEED_CHANGE and FORCE_RUN_BACK_SPEED_CHANGE; trace receipt, applied controller speed, ACK opcode, displacement/rate rows, restoration. | Confirm usable GM commands/account and whether the test realm can safely alter the test character's speeds. |
| F4 step/slope | Committed `movement-stair-slope-course` vantage; scripts for sub/at/over 0.7 yd steps and 49/50/51-degree ramps; traversal, ground-source, slide and penetration rows. | Name a stable existing Northshire/GM-spawned course or authorize a synthetic local collision fixture in a new order. |
| F5 swim | Water vantage with known surface/bottom; forward/back, pitch-up/down, entry/exit and jump-from-water scripts; 3D speed, flags, vertical travel and swim clip rows. | Name a stable water site/depth ladder and confirm whether server swim flags are available offline or require the realm. |
| F6 capsule sweep | Collision course containing wall glances, fence rails, inside/outside corners and a narrow doorway; contact/penetration/slide-distance rows at multiple approach angles. | Name suitable static geometry or authorize a synthetic local collision fixture; provide the target capsule law if server collision height should override config. |

F3-F6 remain unstarted and untouched.

S5 standard four gates pass: Debug build (known CA2014 only), combat/wire,
portrait-camera 1,224 / 1,289 / 56, and move-audit-check with hardCuts=0.

**HARD STOP — SPEC-14 is complete. Nico's terrain/realm coverage ruling is
required before any F3-F6 implementation or additional movement diagnosis.**

## C0 - GM chat-send capability

Symbol search found `CMSG_MESSAGECHAT` in the opcode table but no outbound
builder or caller. C0 adds the authorized DevTools-only GM console, with SAY /
LANG_UNIVERSAL packet construction, send, 32-command Previous/Next recall,
copyable last command, and `[verdict:combat] event=GmCommand` echo including
the actual send result. CB0 in Session 3 asks Nico to send `.gps` and paste the
server response; server acceptance is live-unverified here.

```text
PREDICTED existing send path: unknown
ACTUAL existing send path: absent (opcode only)
PREDICTED authorized wire delta: typed CMSG_MESSAGECHAT only
ACTUAL authorized wire delta: DevTools console -> NetworkClient -> WorldSession
PREDICTED other combat/wire deltas: 0
ACTUAL other combat/wire deltas: 0
```

### Retroactive SPEC-14 canonical baseline SHA-256 manifest

| SHA-256 | Artifact |
|---|---|
| `ef26eba5204cd72cd184eaaaa1b2a0e9a3a32c8366f90a685dd62125d05678d5` | `backpedal-run1.csv` |
| `da744dbc3fcd7b5ee0f6719ca7c3ccf98d4427fa348613a8e738b5bb1aa28107` | `backpedal-verdicts.csv` |
| `62a0ae8512e9b29fe7074bc5ff18073c1163618421544b29ef5ade6dfcb9f144` | `diagonal-run1.csv` |
| `0b0c5a57306a21b045e41da056684b6516d2c69ea65bcd47e2da2b88b8a09782` | `diagonal-verdicts.csv` |
| `6f4c3e10dea8af74256cc639103fd8ad9cc0582c367106c1343d70435e269699` | `jump-flat-run1.csv` |
| `18b19df180c233133a67280563ab4e7cd880f7bf12c1851758795ba7ad2a4cf1` | `jump-flat-verdicts.csv` |
| `4f9adbd34c97ad37f9c2d0637dcb5929195dca08a25f29041f9aad1ac41b746e` | `jump-standing-run1.csv` |
| `000cf218883cc62ade786537b4cdb24f8a98e828efc50d4037f358a0f0b33c9c` | `jump-standing-verdicts.csv` |
| `3f72ef601cad72df73786e97c50a7bd522f71998e3f3b59dcf09c97d0e4e3c8a` | `run-start-stop-run1.csv` |
| `42688a5e1695d075bf60d61435f03ae6911a71987f0fefc54427899b5bf957fa` | `run-start-stop-verdicts.csv` |
| `32f866ee30ec9c3d1c4e8542420392e51f27c0e6cee5f1983a72fdced9a101c2` | `strafe-pure-run1.csv` |
| `bf45f5de44377085c032fb3d04f836d587d9aaef8c8f5d375ae11ffee6162e22` | `strafe-pure-verdicts.csv` |
| `ac1941e27e16949286eccf57e4ff36d4463c5c00cb6b0f681c121c96013fdae1` | `turn-moving-run1.csv` |
| `a1b5927274e9577a0f6cd5ddbae4e34998fe338b38f782d33f4d9cf17728202b` | `turn-moving-verdicts.csv` |
| `56e69f558263e53fb92201b69a92c9edd8e88659c37326ac0620c316c14a90ab` | `turn-standing-run1.csv` |
| `8ad5aa9ec79be6e59b9c5b0974672c9e7787e3a7bdf24e21d2fe4d9574b31d15` | `turn-standing-verdicts.csv` |

### C0 stage manifest

`MSUIClient/Program.DevTools.GmConsole.cs` SHA-256:
`4dab3583b06f3069a274ea481ae99d7790f1a66a5d28e8fbac3d25e5e502ada6`.

C0 standard four gates pass: Debug build (known CA2014 only), combat/wire,
portrait-camera 1,224 / 1,289 / 56, and move-audit-check.

## C1 - combat trace and verdict channel

The run-dated combat recorder observes the real targeting call sites, successful
WorldSession attack sends, parsed server attack-family receives, player animator
choices, and the existing locomotion mixer state. F10 now includes intent,
authoritative engagement, trace state, capability ownership, and the last 50
combat verdicts. Target switches and all locally or authoritatively observed
intent edges carry explicit causes.

The forensic result is itself important: this client has no swing timer, weapon
speed clock, melee range test, or facing/arc gate. Completed swings arrive in
`SMSG_ATTACKERSTATEUPDATE`; the client starts the attack one-shot from that
server event. Accordingly, the trace reports timer owner `server`, weapon speed
empty, and range/arc `unchecked`. It also records raw target distance and bearing
delta. No synthetic eligibility or timer value was introduced.

```text
PREDICTED swing-timer observation: live owner sampled
ACTUAL: server-owned; no client timer exists to arm/fire/reset
PREDICTED range/arc eligibility observation: live owner sampled
ACTUAL: client has no range/arc eligibility owner; fields are unchecked and
  clientAction=none, with raw distance/bearing retained
PREDICTED combat/wire behavior delta: 0
ACTUAL: 0; the only additions are observers, verdicts, trace and F10 fields
PREDICTED artifact naming: run-dated
ACTUAL: dumps/combattrace-<name>-<yyyyMMdd-HHmmss>.csv
```

The C1 SHA-256 manifest is
`combat-instruments/manifests/C1-20260731-135654.sha256`; its own SHA-256 is
recorded by the stage commit after the manifest content is frozen.

C1 standard four gates pass: Debug build (known CA2014 only), combat/wire,
portrait-camera 1,224 / 1,289 / 56, and move-audit-check.

## C2 - combat scenario deck and Session 3 protocol

The VMaNGOS deck uses two level-1 Kobold Vermin (entry 6), spawned separately
at the flat movement-arena vantage. SETUP records the exact chat-command syntax
and the selection-dependent cleanup procedure. Session 3 now contains CB1-CB7,
and each paste slot names the verdict events or trace columns that answer it.

```text
PREDICTED scenario files: dummy.txt + reset.txt
ACTUAL: both committed under scenarios/combat/
PREDICTED protocol items: CB1-CB7
ACTUAL: 7/7, covering stationary, orbit, range edge, cancel/re-arm,
  target switch, death, and chase/animation overlap
PREDICTED prose-only answers accepted: 0
ACTUAL: 0; every item requests named verdict lines or CSV columns
```

The C2 SHA-256 manifest is
`combat-instruments/manifests/C2-20260731-135938.sha256`.

C2 standard four gates pass: Debug build (known CA2014 only), combat/wire,
portrait-camera 1,224 / 1,289 / 56, and move-audit-check.

## C3 - combat-audit

`tools/combat-audit <trace> [output-directory]` parses quoted CSV, enumerates
the legal Off/On intent state machine, detects unknown/repeated transitions,
counts ATTACKSWING per start and ATTACKSTOP per local cancel/target switch,
rejects server swings outside intent windows, checks cadence to one observed
tick when weapon speed and eligibility exist, and checks attack-choice return
to an unblended movement base. Output is always run-dated.

Missing capabilities are explicit `NO_DATA`, never PASS: the current client
does not supply weapon speed or resolved eligibility, and no live CB trace
exists yet. Therefore no combat baseline or verdict artifact was cut in C3.

```text
PREDICTED legal states: Off, On
ACTUAL: exactly Off --IntentOn--> On and On --IntentOff--> Off
PREDICTED unknown transition treatment: FAIL
ACTUAL: FAIL row with timestamp/event/target
PREDICTED first committed combat baseline: none before Nico live run
ACTUAL: none
PREDICTED unavailable measurement treatment: explicit
ACTUAL: NO_DATA with sample counts
```

The C3 SHA-256 manifest is
`combat-instruments/manifests/C3-20260731-140149.sha256`.

C3 standard four gates pass: Debug build (known CA2014 only), combat/wire,
portrait-camera 1,224 / 1,289 / 56, and move-audit-check.

## C4 - combat instrument close-out (HARD STOP)

### Capability finding

C0 found no pre-existing chat sender. The authorized DevTools console now sends
typed SAY/UNIVERSAL `CMSG_MESSAGECHAT`, preserves 32 commands for recall, and
echoes the actual local send result into `[verdict:combat]`. Whether VMaNGOS
accepts and answers `.gps` remains a named CB0 live check; no server-response
claim is made offline.

Combat itself is server-authoritative in the measured client tree. A local
attack intent sends one start request; `SMSG_ATTACKSTART/STOP` bracket the
authoritative state and `SMSG_ATTACKERSTATEUPDATE` supplies completed swings
that trigger attack animation. There is no client swing clock, weapon-speed
timer, melee range gate, or facing/arc gate. The instruments preserve those
absences explicitly and collect the raw evidence required to decide whether
the strange live behavior is client, server, or protocol ownership.

### Instrument inventory

| Stage | Commit | Inventory |
|---|---|---|
| C0 | `18a0034` | GM send console, ring echo, recall, CB0 |
| C1 | `e3d1640` | combat verdict channel, run-dated trace, F10 block, real send/receive/intent/target/animation observers |
| C2 | `2a6226b` | two-target VMaNGOS deck, reset deck, CB1-CB7 named-line protocol |
| C3 | `fa6cfab` | state/cadence/spam/intent/one-shot audit with explicit NO_DATA; no baseline |

The run-dated inventory is
`combat-instruments/inventory-C4-20260731-140243.txt`. Its SHA-256 is
`9ff8e9cd3a83696dae0fc6e0ad3fe11530bfd3460450791fc7b6192164b0296c`.
The C4 manifest is
`combat-instruments/manifests/C4-20260731-140243.sha256`.

```text
PREDICTED combat/physics/input behavior changes: 0
ACTUAL: 0
PREDICTED wire change beyond C0 console: 0
ACTUAL: 0
PREDICTED committed combat baseline: 0
ACTUAL: 0
PREDICTED live protocol reference: Session 3 CB0-CB7
ACTUAL: CHECKS_GAMEPLAY.md Session 3 CB0-CB7
```

F3-F6 remain untouched. No combat fix is implemented or authorized.

C4 standard four gates pass: Debug build (known CA2014 only), combat/wire,
portrait-camera 1,224 / 1,289 / 56, and move-audit-check.

**HARD STOP - SPEC-15 C0-C4 is complete. The next combat order must be written
from Nico's pasted CB0-CB7 live results.**

## A0 - autonomous live-session bootstrap

`tools/live-run` now preflights realmd, refuses any account except dedicated
`TEST` with a named character, launches MSUI auto-login, waits for the real
world/renderer state, sends the movement-arena `.go xyz` through the same C0
GM-command method, writes a dated result, and exits with a named status. SETUP
records that VMaNGOS is external and its one manual start is outside this repo.

```text
PREDICTED safe account: dedicated TEST + named disposable character
ACTUAL configured account: non-TEST; bootstrap refused before login
PREDICTED failure handling: named nonzero result + dated artifact
ACTUAL: REFUSED_NON_TEST_ACCOUNT, exit 3
ARTIFACT: live-runs/bootstrap-refused-20260731-141241.json
SHA256: b285d57019a2e4e4ed9c48022358e64adf11622d6790d7d20816cb8214dccd4d
```

No Nico character was logged in or altered. This runner failure is evidence,
not a blocker to implementing later independent stages. A0 manifest:
`live-runs/manifests/A0-20260731-141241.sha256`.

## A1 - live protocol runner

The SPEC-13 input player now accepts live protocol steps for `gm`, deterministic
`select spawn:N`, `attack start|stop`, `wait`, `waitfor`, `assert`, `dump`,
`trace start|stop`, and the existing `press`/`release` movement primitives.
Selection and attack call `CommitSelection`/`StopAttack`; GM calls the C0 send
method; movement feeds the same `MovementInput` override used by SPEC-13.
Failures are logged and execution advances where safe. Completion writes a
run-dated runner CSV and complete verdict-ring dump.

No live execution was attempted with the non-TEST config. A1 manifest:
`live-runs/manifests/A1-20260731-141425.sha256`.

## A2 - autonomous CB1-CB7 live run

Nico authorized his account; the existing dedicated character `Test` was
confirmed through the network harness and no other character was selected.
The real client completed all 65 protocol steps with two assertion timeouts.
Seven run-dated traces, a verdict dump, runner log, and seven audit CSVs are
frozen by `live-runs/manifests/A2-20260731-141900.sha256`.

| Item | Result | Exact evidence / classification |
|---|---|---|
| CB1 | FAIL candidate | `AttackSwingSend time=6.865`, `AttackStartReceive time=6.949`, then runner step 8 `waitfor event=SwingReceive timeout`; no player swing in 12 s. |
| CB2 | RULING-NEEDED | One start/stop, zero player swings while orbiting. Client-side facing gate vs server ownership is unruled. |
| CB3 | RULING-NEEDED | One start/stop, zero player swings across distance dance. Client-side range gate vs server ownership is unruled. |
| CB4 | MACHINE-VERIFIED PASS | audit: `intentStarts=2; attackSwingSends=2`, `localCancels=2; attackStopSends=2`; clean re-arm. |
| CB5 | MACHINE-VERIFIED PASS | `TargetSwitch time=55.455`, then stop/off/start/on at the same timestamp; one pair. |
| CB6 | FAIL candidate | runner step 54 `waitfor cause=target-death timeout`; `.die` did not yield the demanded player-target death cause. |
| CB7 | AUDIT-INSTRUMENT defect | Local `IntentOff time=71.418`; an NPC-to-NPC/player `SwingReceive time=71.783` followed. Combat-audit incorrectly treats every realm swing as the player's and reports swing-while-off. |

The observed `SwingReceive` rows in CB6/CB7 name non-player attackers; none of
the seven traces contains a player-guid attacker swing. Therefore cadence and
player attack animation remain NO_DATA, not failed cadence. Combat fixes were
not attempted.

## A3 - Session 2/3 CHECKS migration

All 18 items are classified in
`live-runs/CHECKS-migration-20260731-142000.csv`: 12 AGENT-RUNNABLE now,
2 AGENT-RUNNABLE-NEXT, and 4 NICO-ONLY. The NICO-only set is limited to V1,
V2, V2b visual rulings and M5 pose-pop perception. Existing batch/contact-sheet
and movement-trace evidence is named for each. V3 needs a portrait-lab runner
primitive; V4 needs disposable character-selection/customization. CHECKS itself
now carries the class inline. A3 manifest:
`live-runs/manifests/A3-20260731-142000.sha256`.

## A4 - autonomous live-run close-out (HARD STOP)

The frozen packet is `live-runs/A4-packet-20260731-142100.md`, SHA-256
`8f4d433bac675424c8782dd473e9b5010a1eaa50f088d33dc9b4088554415ff4`.

| Kind | Item | Evidence / next ruling |
|---|---|---|
| MACHINE-VERIFIED | CB4 | Two starts/two sends and two cancels/two stops; clean re-arm. |
| MACHINE-VERIFIED | CB5 | TargetSwitch followed by one stop/off/start/on pair at 55.455. |
| DEFECT-CANDIDATE | CB1 | Start accepted, no player SwingReceive in 12 seconds. |
| DEFECT-CANDIDATE | CB6 | `.die` produced no `cause=target-death`; assertion timed out. |
| RULING-NEEDED | CB2 | Should client gate facing or continue deferring to server? |
| RULING-NEEDED | CB3 | Should client gate range or continue deferring to server? |
| INSTRUMENT-DEFECT | CB7/audit | Filter attack-family audit rows by player attacker; foreign NPC swings are not player intent violations. |

Still-missing runner primitives: server chat-response capture; spawn identity
from GM response rather than GUID sort; targeted death confirmation;
portrait-lab cycle/copy; disposable character customization/select.

No combat fix, combat baseline, or F3-F6 implementation was started.
A4 manifest: `live-runs/manifests/A4-20260731-142100.sha256`.

**HARD STOP - SPEC-16 A0-A4 complete. Await Nico's CB1/CB6 and range/facing
rulings plus a new signed implementation order.**

## SPEC-17 protocol housekeeping

`PILOT_PROTOCOL_AMENDMENT_20260731.md` and the current `PILOT_PROTOCOL.md`
were byte-identical before merge (both SHA-256
`a9ce2016bcbbcc5ec5cfc24e136d68bd38f74365230826583ba3ad81a0bee934d`).
Therefore the authoritative roles paragraph, three-tier law 3, and law 12
were already present; merge diff was zero lines. Other current content was
preserved byte-for-byte and the redundant amendment file was deleted.

## D0 - combat diagnosis instrument corrections

Player and foreign melee swings now split at the live parsed attacker GUID;
archived audits infer the player from ATTACKSTART and reclassify foreign rows.
Attack errors 0x0145-0x0149, chat responses, response-derived spawn GUIDs,
descriptor death checks, and explicit distance/facing-delta columns are wired.

Opcode values were verified against the local authoritative
`benilla-protocol/src/messages/opcode_names.rs`: NOTINRANGE 0x0145,
BADFACING 0x0146, NOTSTANDING 0x0147, DEADTARGET 0x0148, and CANT_ATTACK
0x0149. No combat decision or wire send changed. D0 manifest:
`live-runs/manifests/D0-20260731-143326.sha256`.

## D1 - CB1 root-cause matrix: INVALID specimens

The unchanged four-variant protocol ran, but no GM command response and no new
spawn entity followed `.npc add`; selection therefore fell back to existing
world creatures 36-88 yd away. V-A produced NOTINRANGE 0x0145 exactly, proving
the new error capture, but V-B/V-C/V-D cannot answer the decision table.
Additionally `.go xyz` received no response or position change. The command
sender reports local send success, while VMaNGOS executes none of `.go`,
`.npc add`, `.npc delete`, or `.die` through this path.

```text
EXPECTED valid matrix specimens: 4
ACTUAL: 0
EXPECTED player swings usable for decision table: >=1 qualifying branch
ACTUAL: 0
RESULT: D1 INVALID; root cause moves upstream to CMSG_MESSAGECHAT capability
```

No CB1 combat conclusion is drawn. D1 manifest:
`live-runs/manifests/D1-20260731-144349.sha256`.

## D2 - death confirmation unavailable

D2 did not judge client intent. The required prerequisite—a selected spawned
victim whose server descriptor transitions to dead—cannot be produced through
the current GM channel. Repeating `.die` would only repeat a locally-sent,
server-unexecuted command. Result is NO_DATA, frozen in
`live-runs/D2-death-confirmation-20260731-144700.json`; manifest
`live-runs/manifests/D2-20260731-144700.sha256`.

## D3 - scoped CB4/CB7 verification and archived re-audit

The live CB4/CB7 rerun has no valid specimen. D1 proved that the client reports
the GM chat send locally but the server neither acknowledges nor executes the
teleport/spawn/delete/death commands. Selecting a GUID-sorted wild creature
would knowingly repeat the invalid target protocol, so the result is explicit
NO_DATA rather than a fabricated machine verification. The prerequisite is a
server-acknowledged command and response-derived spawned target identity.

The archived `20260731-141730` traces were re-audited with attacker == player
GUID scoping:

| Scenario | Player swings before -> after | Re-audited result |
|---|---:|---|
| CB1 | 0 -> 0 | unchanged NO_DATA |
| CB2 | 0 -> 0 | unchanged NO_DATA; defer-to-server law remains Nico's ruling |
| CB3 | 0 -> 0 | unchanged NO_DATA; defer-to-server law remains Nico's ruling |
| CB4 | 1 -> 0 | swing confirmation and one-shot PASS void; wire pairing remains 2/2 starts and 2/2 stops |
| CB5 | 1 -> 0 | target-switch wire sequence stands; swing/animation becomes NO_DATA |
| CB6 | 8 -> 0 | all apparent swings were foreign; target-death behavior remains NO_DATA; redundant initial IntentOff audit row remains |
| CB7 | 1 -> 0 | `swingInsideIntent` FAIL -> PASS after excluding the foreign attacker; redundant initial IntentOff audit row remains |

```text
EXPECTED CB4 player swing after scope correction: required to restore claim
ACTUAL: 0 in archive; live rerun unavailable
RESULT: PARTIAL, wire pairing only
EXPECTED CB7 foreign swing treatment: excluded from player assertion
ACTUAL: swingInsideIntent PASS; player swing samples 0
RESULT: prior scope failure removed, no player-swing claim
```

The row-by-row delta is frozen in
`live-runs/D3-archived-reaudit-20260731-144903.csv`; the live prerequisite
finding is `live-runs/D3-live-rerun-20260731-144903.json`. D3 manifest:
`live-runs/manifests/D3-20260731-144903.sha256`.

D3 standard four gates pass: Debug build (known CA2014 only), combat/wire,
portrait-camera 1,224 / 1,289 / 56, and move-audit-check.

## D4 - combat diagnosis round 2 close-out (HARD STOP)

The full root-cause matrix, re-scoped findings, and implied order queue are
frozen in `live-runs/D4-combat-diagnosis-packet-20260731-145135.md`.

CB1 is NOT DETERMINED: none of V-A through V-D produced a controlled specimen,
so no decision-table row qualifies. The isolated NOTINRANGE 0x0145 event proves
the D0 capture but not a GM-mode root cause. CB6 is likewise NOT DETERMINED:
there is no response- and descriptor-confirmed death. CB4 is PARTIAL with only
its exact wire pairing retained. CB7's foreign-swing audit failure is removed,
but zero player swing samples means it supplies no positive combat claim.

The first implied order is the upstream autonomous GM-command capability, with
positive response, position-mutation, and response-derived spawn-identity
proof. Only then can unchanged D1/D2/D3 protocols yield combat evidence.
Nico's CB2/CB3 defer-to-server ruling remains closed: no client range or facing
gate is queued. Attack-error text display remains a later signed order.

```text
EXPECTED qualifying CB1 matrix specimens: 4
ACTUAL: 0
EXPECTED confirmed CB6 deaths: 1
ACTUAL: 0
EXPECTED restored CB4 player-swing confirmation: 1 or more player-guid rows
ACTUAL: 0 archived; live rerun invalid
EXPECTED combat fixes in SPEC-17: 0
ACTUAL: 0
```

D4 manifest: `live-runs/manifests/D4-20260731-145135.sha256`.

D4 standard four gates pass: Debug build (known CA2014 only), combat/wire,
portrait-camera 1,224 / 1,289 / 56, and move-audit-check.

**HARD STOP - SPEC-17 D0-D4 is complete. No combat fix, F3-F6 work, or
additional diagnosis is authorized without a new signed order.**

## G0 - GM transport versus permissions

The Run-16 GM-dependent steps are reclassified as unproven: GUID-sorted nearby
creatures were wild-world specimens, not response-identified spawns.

G0 isolated a transport encoding defect. MSUI used client language 0
(`LANG_UNIVERSAL`). Benilla's authoritative sender states that VMaNGOS rejects
client Universal before dot-command parsing and always uses the logged-in
character's faction tongue (`world/writer/chat.rs:10-30`). The body field order
already matched `messages/client.rs:53-75` and the golden at
`tests/client.rs:79-81`; only language selection was wrong.

After the authorized instrument correction, Human character `Test` sent Common
language 7. `ping-20260731-150800` echoed as `SMSG_MESSAGECHAT`, and `.gps`
returned server-authored Northshire map and coordinates. Both directions' full
hex are frozen in `live-runs/verdicts-20260731-150735.txt` and summarized in
`live-runs/G0-transport-proof-20260731-150735.md`.

Permissions are sufficient: login sent `GM mode is ON` and `You are now
invisible (rank 6)`, and `.gps` executed. The remote DB port is not exposed;
SSH is reachable but has no non-interactive credential. Exactly zero SQL
queries and zero remote file reads occurred. G1 provisioning is unnecessary.

```text
EXPECTED corrected plain SAY: server echo
ACTUAL: exact ping echo received
EXPECTED corrected dot command: server system response
ACTUAL: .gps returned map 0 and X/Y/Z at Northshire
EXPECTED permission blocker: refusal or absent privileged response
ACTUAL: rank 6 plus successful .gps; no blocker
```

G0 manifest: `live-runs/manifests/G0-20260731-150735.sha256`.

G0 standard four gates pass: Debug build (known CA2014 only), combat/wire,
portrait-camera 1,224 / 1,289 / 56, and move-audit-check.

## G1 - provisioning not required

G1 performed no mutation. Server-authored login notices report GM mode ON and
rank 6, while `.gps` executed and returned privileged system output. The
conditional provisioning branch is therefore false. No mangosd console command
and no SQL statement was executed.

```text
PREDICTED action if gmlevel insufficient: provision through mangosd console
ACTUAL gm permission: sufficient (rank 6 and .gps success)
RESULT: NOT_REQUIRED; zero state changes
```

Artifact: `live-runs/G1-provisioning-20260731-150954.json`; manifest:
`live-runs/manifests/G1-20260731-150954.sha256`.

G1 standard four gates pass: Debug build (known CA2014 only), combat/wire,
portrait-camera 1,224 / 1,289 / 56, and move-audit-check.

## G2 - positive-proof gate HARD STOP

`.gps` passes, but the required `.go xyz` movement proof fails inside the
client. VMaNGOS executed the command and sent opcode 0x00C7 with the requested
position `(-8970.0, -132.493, 83.53)`. MSUI has no receive/apply/ack arm for
same-map `MSG_MOVE_TELEPORT_ACK`; all 545 movement-trace rows remain at
`(-8949.95, -132.493, 83.529526)`, and follow-up `.gps` reports that old pose.

Benilla's authoritative shape is `messages/parse.rs:201-210`; its required
client echo is `world/session.rs:548-554` with body builder
`messages/client.rs:293-300`. The exact packet, trace, and command results are
frozen in `live-runs/G2-hard-stop-20260731-151705.md`.

The same run also proves the old deck's `.npc add 6` is not a creature-spawn
command on this installed server: it responds `You must select a vendor`.
No controlled creature appeared and no death claim was made. Because G2 demands
all four positive proofs and says any failure is a hard stop, no alternative
spawn command was attempted after the teleport failure.

```text
EXPECTED .gps: server response
ACTUAL: PASS
EXPECTED .go: position mutation visible in movement trace
ACTUAL: FAIL; server packet received, client change 0/545 rows
EXPECTED controlled spawn <=3 yd: one
ACTUAL: zero; installed command parsed as vendor operation
EXPECTED descriptor-confirmed death: one
ACTUAL: zero; no controlled target
```

G2 manifest: `live-runs/manifests/G2-20260731-151705.sha256`.
G3 and G4: NOT STARTED due to the binding G2 hard stop. No combat fix,
error-text display, F3-F6 work, or teleport behavior fix was made.

G2 standard four gates pass: Debug build (known CA2014 only), combat/wire,
portrait-camera 1,224 / 1,289 / 56, and move-audit-check.

**HARD STOP - SPEC-18 stopped at G2. The accepted tree records the transport
fix and the unimplemented same-map teleport prerequisite; G3/G4 remain closed.**
## T0 — same-map teleport receive/apply/ack

### Actual versus predicted

The authorized production change now consumes VMaNGOS's server-to-client
`MSG_MOVE_TELEPORT_ACK` shape (packed mover GUID, counter, destination
MovementInfo), applies the pose on the game thread to controller, camera, and
rendered body, resets the movement sender, and replies once with the required
full GUID, matching counter, and client time. The layout was verified against
the project's build-5875 movement decoder, benilla's parser/sender and golden
fixture, and the official VMaNGOS sender, packet reader, and near-teleport
handler sources.

The clean run `T0-teleport-acceptance-20260731-153015` passed every acceptance
row. Counter 1 requested exactly `-8970|-132.493|83.53` at orientation
`2.7227101`; the apply verdict followed packet receipt in one live tick, and the
next movement row retained both aim and body yaw. The sole matching reply body
was `0100000000000000010000001B320000`. Across 1,846 idle rows covering more
than 30 seconds, X/Y remained byte-identical with no snap-back. The subsequent
real-input run emitted start, heartbeat, heartbeat, stop; server `.gps`
confirmed the final trace pose. The post-teleport run-start-stop audit passed
all seven current-tree bands.

An initial run exposed a runner-only false warning: after the protocol moved
away, bootstrap continued checking the staging arena. T0 now stops that check
once protocol ownership begins; the clean acceptance run has 13/13 steps PASS
and no false bootstrap verdict. Far-map transfer remains unchanged, untested,
and out of scope.

Full actual-versus-predicted detail and wire hex are in
`live-runs/T0-teleport-acceptance-20260731-153015.md`. Standard four gates pass:
Debug build (known CA2014 only), combat/wire, portrait-camera
1,224 / 1,289 / 56, and move-audit-check.
## T1 — server-specific creature lifecycle deck

### Actual versus predicted

The live server's `.help npc spawn` tree identified `add`, `delete`, and `info`
as subcommands. This matches official VMaNGOS command-table bindings at
`src/game/Chat/Chat.cpp:662-670` and the add/delete handlers at
`src/game/Commands/CreatureCommands.cpp:928-976,1001-1046`. The actual Linux
source checkout could not be read from Windows because no Linux credentials or
checkout path were available; this is a provenance limitation, not silently
represented as a disk read.

The corrected lifecycle passed end to end. `.npc spawn add 6` created
`0xF13000000604A26E` (DB GUID 303726); the client measured entry 6 at
1.7489377 yd; `.npc info` independently returned Entry 6 / GUID 303726; and
`.npc spawn delete` returned `Creature Removed`, followed by descriptor absence
for the exact full GUID. Active combat decks and SETUP now use the verified
syntax.

Two failed validation iterations remain evidence: they exposed a selector
arrival race and a cleanup-risking <=3-yard filter. Exact server-command cleanup
removed DB GUIDs 303723, 303724, and 303725. The instrument now resolves spawn
ordinals from `SpawnObserved`, waits boundedly for descriptors, records
`within3` as an explicit acceptance fact, and proves cleanup through `waitgone`.
No combat behavior changed.

Full evidence is in `live-runs/T1-lifecycle-acceptance-20260731-154414.md`.
Standard four gates pass: Debug build, combat/wire, portrait-camera
1,224 / 1,289 / 56, and move-audit-check.

## T2 — positive-proof gate

### Actual versus predicted

All four previously blocked G2 proofs now pass. `.gps` returned server-authored
map and pose text. `.go` produced `TeleportApplied counter=1` at exactly
`-8970|-132.493|83.53`, visible in the movement trace within one live tick.
The spawn proof selected the newly observed zero-distance entry-6 creature
`0xF13000000604A26F` (DB GUID 303727), confirmed it independently with
`.npc info`, and proved cleanup by exact descriptor absence. The death proof
used a second throwaway, `0xF13000000604A270` (DB GUID 303728), and observed
descriptor health zero after `.die` before exact cleanup.

```text
EXPECTED .gps / .go / identified <=3 yd spawn / confirmed death: all PASS
ACTUAL: all PASS (runner rows 4/4, 6/6, 10/10, and 13/13)
RESULT: T2 ACCEPTED; T3 authorized by the positive-proof gate
```

Full evidence is in
`live-runs/T2-positive-proof-acceptance-20260731-154700.md`; staged-byte hashes
are in `live-runs/manifests/T2-20260731-154700.sha256`.

## T3 — combat diagnosis HARD STOP

### Actual versus predicted

The controlled V-A through V-D matrix reached SPEC-17's explicit no-swing stop
row. Each isolated trace contains one player `AttackSwingSend`, but all four
contain zero `AttackStartReceive`, zero player `SwingReceive`, and zero
`AttackErrorReceive`. V-B has the server-authored `GM mode is OFF` response;
therefore GM mode is not the root cause. V-C measured distance from 12.296586
to 2.2626302 yd, and V-D traversed the facing discriminator, but neither
elicited an error or swing.

```text
EXPECTED decision branch: V-B swing => GM mode, or V-C/V-D error then swing
ACTUAL: V-A/V-B/V-C/V-D player swings = 0/0/0/0; attack errors = 0/0/0/0
BOUND LAW: no swings in any variant => escalate full wire evidence and stop
RESULT: HARD STOP at T3; CB4/CB5/CB6/CB7 and T4 NOT STARTED
```

The initially low-health Northshire specimens were contaminated by an entry-1642
guard; those foreign rows are excluded and retained as failed evidence. The
accepted matrix used source-verified, live-only health and passive-state fixture
setup, then exact descriptor cleanup. Remote-cell persistent-spawn trials were
also cleaned by exact DB GUID. No renderer, combat, input, physics, or wire code
changed.

The archived initial `IntentOff` is an audit normalization issue: CB6's trace
began mid-intent and captured a legitimate target-switch Off→On sequence without
the prior trace's On. It is not a production transition defect; no fix landed.

Full measured table, outgoing body hex, environmental evidence, current CB
status, and the next signed-order queue are in
`live-runs/T3-hard-stop-20260731-161700.md`. The first queued prerequisite is
CMSG_ATTACKSWING receive/acceptance forensics against VMaNGOS and benilla;
error-text display remains later. T3 hashes are in
`live-runs/manifests/T3-20260731-161700.sha256`.

## H0 — VMaNGOS attack-acceptance law

Official VMaNGOS development commit
`db7450c6e4cc255cffa2620e5d0dd7d2f179d2d2` was read at the exact handler and
`Unit::Attack` lines. The Linux server checkout remains unavailable from this
Windows machine; the live server will arbitrate any revision difference.

The strongest H0 discriminator differs from the pilot prediction. A GUID that
fails `IsUnit()` is silently dropped, but a well-typed unit GUID that misses
map lookup receives `SMSG_ATTACKSTOP` with a null victim. Friendly,
spawning/not-selectable, and dead found victims also receive attack-stop. A
found candidate reaches `Unit::Attack`, whose self/deleted/dead/out-of-world,
mounted, GM-victim, evade, and already-attacking returns are silent. A newly
valid attack sends attack-start.

Range, facing, and death errors are later swing-timer decisions, not initial
lookup responses. NOTINRANGE and BADFACING are state-deduplicated; DEADTARGET
stops attack; the current CANT_ATTACK branch delays silently. SET_SELECTION
stores any packet GUID and normally acknowledges neither success nor miss, so
it cannot prove identity by itself.

```text
EXPECTED law column: enumerate silent and error paths
ACTUAL: complete at CombatHandler.cpp:32-62, MiscHandler.cpp:399-431,
        Unit.cpp:364-475 and 4703-4786, Player.cpp:17118-17146
DEVIATION: official lookup miss sends attack-stop rather than returning silently
RESULT: H0 COMPLETE; H1 opens
```

The full law table is `live-runs/H0-attack-law-20260731-163000.md`; manifest:
`live-runs/manifests/H0-20260731-163000.sha256`.

## H1 — triple attack-target identity audit

The three independent identities match exactly. The server's `.npc info`
response named entry 6 / DB GUID 303741; the client object store assigned
`0xF13000000604A27D`; and the real outgoing `CMSG_SET_SELECTION` and
`CMSG_ATTACKSWING` bodies were both `7D A2 04 06 00 00 30 F1`. Decoding the
identity yields unit high `0xF130`, entry 6, and low `0x04A27D` / 303741 in
every source.

```text
EXPECTED: land server response, object-store GUID, and both wire GUIDs
ACTUAL: all four are exactly 0xF13000000604A27D
DEVIATION: none
RESULT: response-derived identity mismatch eliminated; H2 opens
```

The live protocol player gained an instrument-only `wire-trace start|stop`
primitive so protocol artifacts can contain the actual session packet bodies.
It changes no packet construction or behavior. The identity run deliberately
makes no attack-acceptance claim: no server attack response arrived during its
observation window. Full evidence is in
`live-runs/H1-triple-identity-20260731-163500.md`; staged-byte hashes are in
`live-runs/manifests/H1-20260731-163500.sha256`.

## H2 — attack acceptance HARD STOP

The required wild positive control now fails. The current client rediscovered
the exact run-16 object-store target `0xF13000007900B1EF` (entry 121, faction
17, flags 0, alive) at 2.1473 yd and sent the full little-endian body
`EF B1 00 79 00 00 30 F1`. No attack-start, attack-stop, or attack-error
response followed. The archived run-16 verdict records `AttackSwingSend` at
6.865 and `AttackStartReceive` at 6.949 for that same full GUID.

```text
EXPECTED positive control: SMSG_ATTACKSTART, matching runs 16/17
ACTUAL current tree: one real send, zero attack-family responses
RESULT: H2 positive-control regression path; HARD STOP before H3
```

The spawn object-store cell likewise sent
`7F A2 04 06 00 00 30 F1` for independently confirmed
`0xF13000000604A27F` and received no attack-family response. H1 proved the
response-derived and object-store spawn identities are identical, so SPEC-20's
third cell is N/A rather than a duplicate experiment. Both failures enter
H0's found-candidate silent-return family: a lookup miss and the handler's
friendly/spawning/not-selectable/dead rejections would have sent attack-stop;
success would have sent attack-start. The exact silent predicate is not
observable client-side.

The ordered bisect did not identify T0 as first bad. Accepted run-17-era commit
`6a5e73a`, built in isolation, also selected the exact archived GUID and failed
today. A second isolation run reduced it to the historical single
unacknowledged bootstrap teleport and again failed. This is a real regression
against the committed archive, but it depends on live server/session state not
reproduced by source revision alone; attributing it to T0 would contradict the
actual bisect.

The complete matrix, calibration results, wire bytes, bisect limitations, and
next-order requirement are in
`live-runs/H2-attack-accept-hard-stop-20260731-173500.md`. H3 was not started;
no combat behavior, error-text, F3-F6, or production-network change landed.
The H2 staged-byte manifest is
`live-runs/manifests/H2-20260731-173500.sha256`.

## P0 — attack precondition truth

P0 captured a fully proven GM-OFF send and it was still silent. The server
reported GM-ON at login before any protocol state command, then confirmed the
complete OFF→ON→OFF sequence. At send time the player was at the arena, target
`0xF13000000604A282` was present in the client's object store, alive at
100/100 health, entry 6 / faction 25, unit and dynamic flags both zero, and
0.66123295 yd away. The real outgoing body was
`82 A2 04 06 00 00 30 F1`; no attack-family response followed.

```text
EXPECTED cheap discriminator: GM-OFF + present/alive/within3 target is valid
ACTUAL: all preconditions true, one send, zero attack-family receives
RESULT: P0 COMPLETE; P1 opens and a reproduced silent cell will require P2
```

Read-only SSH to `wowvmangos@192.168.0.2` was refused before command execution,
so zero installed config files were read. The authoritative VMaNGOS template
defines `GM.LoginState = 2` (last saved state), while the live notification
proves this account's effective login state was ON. No deployed value is
invented.

The report=act `AttackPrecondition` verdict lands immediately before the
existing attack send and samples controller pose, server-reported GM state,
and the exact object-store descriptor. It changes no combat or wire behavior.
Full evidence is in `live-runs/P0-precondition-truth-20260731-180000.md`;
staged-byte hashes are in `live-runs/manifests/P0-20260731-180000.sha256`.

## P1 — attack-precondition matrix

The prediction was rejected. A server-confirmed GM-OFF fresh spawn at 0 yd,
the same spawn under server-confirmed GM-ON at 1.4692235 yd, and a separately
re-anchored live wild entry-299 creature under GM-OFF at 1.1230221 yd all sent
one correctly typed attack body and all received zero attack-family events.
The A/B bodies were identical (`84 A2 04 06 00 00 30 F1`); C sent
`49 38 01 2B 01 00 30 F1`. Every target was present, visible, alive, and read
from the client's own object store at send time.

```text
PREDICTED: A and C ATTACKSTART + swings; B silently refused
ACTUAL: A, B, and C all silently refused
DEVIATION: GM state does not discriminate acceptance
RESULT: P1 COMPLETE; P2 server-side observation is mandatory
```

The exact predicate remains inside H0's `Unit::Attack` silent-return family;
client evidence cannot name it. The runner gained only an object-store anchor
primitive that teleports via the existing real GM chat input path. Invalid
calibration cells (absent targets or distance above 3 yd) remain recorded and
were not promoted to evidence. Full results and prior-run reconciliation are
in `live-runs/P1-precondition-matrix-20260731-204000.md`.

All four stage-boundary gates passed.

## P2 — server observation (bounded hard stop)

The VMaNGOS RA service at `192.168.0.2:3443` accepted the authorized TEST
credentials from the gitignored client config. Original server logging was
console/file `2/2`, combat filter OFF. The bounded capture set console 3 and
combat ON, repeated a fully proven GM-OFF attack, then restored and verified
`2/2` + combat OFF. The file level remained 2, exactly as the authoritative
`ServerCommands.cpp:575-585` implementation predicts, and RA relayed zero
live process log lines during its 35-second listen window. SSH was refused
before command execution, so the Linux world log could not be read.

A second server-state run proved TEST was not mounted, executed
`.combatstop TEST`, and queried the fresh creature. The target used random
motion rather than HOME motion, was alive and server-found, and a Northshire
guard successfully initiated combat against that exact creature before the
player's send. The player then sent at 1.8917537 yd and again received no
attack-family response. These facts exclude the cheap mounted, stale attack,
HOME-motion evade, dead, absent, and range explanations, but do not identify
the remaining server predicate or prove handler entry.

```text
PREDICTED: server debug capture names the receive/handler path
ACTUAL: reversible logging change succeeded; required Linux log was inaccessible
RESULT: P2 HARD STOP; P3 and P4 NOT STARTED
```

The detailed access request and actual-versus-predicted packet are in
`live-runs/P2-server-observation-hard-stop-20260731-220000.md`. No combat
behavior, error display, server code, database, or F3-F6 change was made.

All four stage-boundary gates passed.

## P2 resumed — SSH console capture negative-evidence hard stop

Dedicated ED25519 key access to `wowvmangos@192.168.0.2` is operational. The
fingerprint and setup shape are recorded in `SETUP.md`; password use ceased as
soon as key-only authentication passed, and rotation is recommended. No secret
was printed, stored, traced, or committed.

The deployed server checkout is clean at
`d7779aee9d43113e78c078b54daef89946be0b1a`. The accepted bounded run confirmed
console/file `2/2` and combat OFF, set console 3 and the named `combat` filter
ON, ran one zero-failure GM-OFF `TEST` attack at 0 yd against an alive,
descriptor-proven spawn, and restored/reconfirmed `2/2` plus combat OFF.

```text
PREDICTED: debug console names receive/dispatch/attack-handler admission for TEST
ACTUAL: one client AttackPrecondition + one CMSG_ATTACKSWING send;
        zero CMSG_ATTACKSWING, ATTACKSWING, HandleAttackSwing, received-opcode,
        opcode-0x0141, or opcode-321 lines in the full server console window
RESULT: P2 negative-evidence HARD STOP; P3 and P4 NOT STARTED
```

The two attack-start rows in the client verdict are foreign creature combat:
attacker `0xF13000066A01384C`, not player GUID `0x0000000000000001`. The deployed
normal dispatch path (`WorldSession.cpp:520-588`) and handler
(`CombatHandler.cpp:32-62`) have no unconditional admission logging, while
`Unit::Attack` (`Unit.cpp:4721-4780`) retains unlogged false returns. The allowed
logging depth therefore cannot distinguish pre-handler loss from normal
dispatch plus a silent predicate. A new signed order must authorize any deeper
server instrumentation.

The prior numeric filter syntax is corrected: this build requires
`server log filter combat on|off`. The accepted run proves ON before the attack
and OFF after restoration. No server code, database, persistent config, combat
behavior, error display, or F3-F6 change was made. Full evidence is in
`live-runs/P2-ssh-hard-stop-20260731-184000.md` and
`live-runs/P2-ssh-20260731-183142/`.

All four boundary gates passed: Debug build with 0 warnings/errors, combat wire
foundation, portrait camera (10534 specimens; 1224/1289/56 controls), and
movement audit check. A first parallel check launch collided in a shared
generated Release file; the uncontended sequential rerun passed in full.

## X0 — travel-laptop re-bootstrap credential HARD STOP

### Actual versus predicted

The repository preflight is clean and complete. Every tracked root
`SPEC_TOOLKIT_*.md`, `*PLAN*.md`, and `*PROTOCOL*.md` file is present;
SPEC-22 is tracked; and there were zero untracked order or plan documents to
preserve. Accepted preflight HEAD was
`145db117e475110e646a88792bb6b0b9383d6b3d`.

```text
PREDICTED root documents: complete and tracked
ACTUAL: complete and tracked; zero tracked-missing or untracked order docs
RESULT: PASS

PREDICTED four gates: green
ACTUAL build: PASS, 0 errors, established CA2014 warning only
ACTUAL combat-wire: PASS
ACTUAL portrait-camera: PASS, 10,534 specimens; 1,224 / 1,289 / 56 controls
ACTUAL move-audit: PASS
RESULT: PASS

PREDICTED travel-laptop config: dedicated TEST values recoverable locally
ACTUAL: ignored config exists but has obsolete host and non-TEST identity;
        the TEST secret is not recoverable from tracked files
RESULT: BLOCKED before config mutation

PREDICTED SSH: existing key auth or new dedicated key installation
ACTUAL existing key auth: FAIL, publickey/password refusal
ACTUAL key recovery: new dedicated ED25519 pair generated locally
PUBLIC FINGERPRINT: SHA256:mwe0xwrQKqTTTi4jhIPj1JjC3vdzcHGW38ymAZTkTi4
RESULT: HARD STOP before password use and authorized_keys installation
```

The run-dated checkpoint is
`live-runs/X0-rebootstrap-hard-stop-20260731-185842.md`, SHA-256
`d2385b0618fd0d1ca8e98cf2a09506367659b70d266509e476a78105b7727a71`.
Its manifest is `live-runs/manifests/X0-20260731-185842.sha256`, SHA-256
`16ccba10ff79c1e3b524bedd768454187eaedfe4c972d1976fe00900b69a1f94`.

The frozen hard-stop boundary rerun also passed all four gates sequentially:
Debug build 0 warnings / 0 errors, combat-wire PASS, portrait-camera PASS with
the same 10,534 specimens and 1,224 / 1,289 / 56 controls, and move-audit PASS.

No password was printed, stored, traced, or committed. The ignored config was
not partially rewritten. X0's four SPEC-19 T2 proofs, X1-X4, and SPEC-21 P3/P4
remain not started. No client production code, combat behavior, server code,
database, persistent server configuration, error display, or F3-F6 behavior
changed.

**HARD STOP — supply the current SSH password for
`wowvmangos@192.168.0.2` through an ephemeral secure path so the new public key
can be installed, and supply the current dedicated TEST account password used
by both realm login and RA (or state that RA uses a different credential).
Do not place either secret in a repository file.**

## X0 resumed — travel-laptop re-bootstrap complete

### Actual versus predicted

Nico supplied the unrecoverable credentials. They were not echoed into any
tracked file or artifact. The ignored config already held the supplied account
credential; only the stale host and selected character were corrected. A
read-only RA `server info` round trip then authenticated successfully.

The new public key was installed through an interactive password prompt. A
subsequent explicit-key, batch-only probe returned `KEY_AUTH_OK`; password use
ceased. SETUP records only the travel-laptop public fingerprint.

```text
PREDICTED config: 192.168.0.2 + supplied test account + Test character
ACTUAL: validated in ignored config; RA authentication PASS
RESULT: PASS

PREDICTED SSH: key-only auth after one interactive install
ACTUAL: key-only batch auth PASS
PUBLIC FINGERPRINT: SHA256:mwe0xwrQKqTTTi4jhIPj1JjC3vdzcHGW38ymAZTkTi4
RESULT: PASS; password use ceased

PREDICTED T2 .gps / .go / identified spawn / confirmed death: all PASS
ACTUAL .gps: 4/4 PASS
ACTUAL .go: 6/6 PASS, requested position applied
ACTUAL spawn: 10/10 PASS, entry 6 GUID 0xF13000000604A289,
              within3=true and exact descriptor cleanup
ACTUAL death: 13/13 PASS, entry 6 GUID 0xF13000000604A28A,
              descriptor health=0 and exact descriptor cleanup
RESULT: four-proof loop PASS once from the travel laptop
```

The live runner mechanically rewrote its historical generic movement trace and
`vantages.json`. The new movement trace was first preserved under its dated X0
directory; both tracked files were then restored from accepted HEAD. No prior
evidence or user vantage delta remains.

The completion packet is
`live-runs/X0-rebootstrap-complete-20260731-191443.md`, SHA-256
`0283ede33ca518a40ff33fa10481093bcafe8df2e2c9d97e5b555d64c561136c`.
The 11-file manifest is
`live-runs/manifests/X0-resume-20260731-191443.sha256`, SHA-256
`1ff50c1cfd9378bafb98b0e6c611470fb48ebdb3b35ddaf6854e968f0c11ae42`;
all entries recomputed exactly before the stage boundary.

X0 completion boundary gates passed sequentially: Debug build 0 warnings / 0
errors; combat-wire PASS (its Release build emitted only the established CA2014
warning); portrait-camera PASS with 10,534 specimens and 1,224 / 1,289 / 56
controls; move-audit PASS.

No client production code, combat behavior, server code, database, persistent
server configuration, error display, or F3-F6 behavior changed. X0 is complete
and X1 is authorized. SPEC-21 P3/P4 remain queued.

## X1 — client socket-flush evidence

### Actual versus predicted

The new observer sits at `WorldSession`'s serialized `NetworkStream.Write`
site. It runs only after the exact post-encryption packet has returned from
both `Write` and `Flush`; SHA-256 is computed there from that same byte array.
DevTools receives the computed hash and bytes for a bounded run-dated CSV. It
does not reconstruct the frame or touch cipher state.

```text
PREDICTED delivered chat control: flushed write plus server response
ACTUAL CMSG_MESSAGECHAT at 5.929:
  bytes=19
  sha256=80254993e43f40b1b225ddd72c330c80e1d7df63e9d7c8f444fecf5fbe36ffea
  post-encryption bytes=3F85FEA7407400000000070000002E67707300
  flushed=true
  server .gps Map response at 6.077
RESULT: DELIVERED CONTROL PASS

PREDICTED proven GM-off CMSG_ATTACKSWING: flushed write
ACTUAL CMSG_ATTACKSWING at 5.941:
  bytes=14
  sha256=784feef9f39b41853082ecfd8bb6dd47d801f1d3e7143986d79abb317c336420
  post-encryption bytes=8263DEEECF998CA20406000030F1
  body=8CA20406000030F1; target=0xF13000000604A28C
  flushed=true
  precondition=GM off, present, visible, alive 100/100, flags zero, distance 0 yd
RESULT: ATTACK SOCKET-FLUSH PASS

PREDICTED not-flushed branch: client defect HARD STOP
ACTUAL: false; chat and attack returned from write+flush with exact hashes
RESULT: X2 authorized
```

The accepted trace contains exactly those two writes, 12 ms apart. A later
attack-start names the spawned creature as attacker and a guard as victim; it
is foreign combat, not a player response, and does not change the transit
question.

The first attempt is retained but invalid. Its plain SAY control did not echo;
the five-second wait let the random-motion target drift to 3.5097475 yd and
engage a guard. Its runner records one failure, so none of its behavioral rows
are promoted. The corrected `.gps` control run has 30/30 PASS rows and a
distance-zero attack precondition.

Full evidence is in
`live-runs/X1-socket-flush-accepted-20260731-192300.md`, SHA-256
`54bf915cdcedaa099f0532dc7e20577934f1b664ee2d2881dd975c601d350151`.
The 17-file stage manifest is
`live-runs/manifests/X1-20260731-192300.sha256`, SHA-256
`929d917f766ae8501d6398a51dccfbdec787c5efe44da1c5182359a08cbc851a`;
all entries recomputed exactly.

X1 boundary gates passed sequentially: Debug build with only the established
CA2014 warning, combat-wire PASS, portrait-camera PASS with 10,534 specimens
and 1,224 / 1,289 / 56 controls, and move-audit PASS.

No combat decision, packet construction, server code, database, persistent
server configuration, error display, or F3-F6 behavior changed. The socket
observer is DevTools-gated and observational exceptions are swallowed. X2 is
authorized; SPEC-21 P3/P4 remain queued.

## X2 — bounded transit capture unavailable

### Actual versus predicted

```text
PREDICTED Linux: sudo -n tcpdump on world port 8085
ACTUAL: /usr/bin/tcpdump exists; sudo -n requires a password
RESULT: unavailable; interactive/password sudo was not attempted

PREDICTED Windows fallback: elevated pktmon or netsh
ACTUAL pktmon: driver access denied
ACTUAL UAC preflight: no approved elevated process or output
ACTUAL netsh start: requires Administrator elevation
ACTUAL other capture tools: dumpcap/tshark/WinDump/Npcap absent
RESULT: unavailable

PREDICTED built-in packet log: use if it covers world opcodes
ACTUAL: Anticheat.PacketLogSize records movement history only and writes only
        on anticheat kick/ban
RESULT: inapplicable to CMSG_ATTACKSWING; no setting changed

PREDICTED raw capture cleanup: delete after frame extraction
ACTUAL: no capture started and no raw file was created
RESULT: trace stopped, no filters changed, temporary helper removed
```

Read-only deployed enumeration cites
`mangosd.conf:1589-1592,2120`,
`MovementAnticheat.cpp:151-158,399-407`, and `World.cpp:1109`. This option is
not a general ingress/world packet logger and cannot observe opcode 321 without
inducing an anticheat penalty, which is outside scope.

The exact X1 attack write remains proven at the client socket boundary, but X2
cannot promote it to `present on wire` or `absent on wire`: neither proposition
was measured. Full detail is in
`live-runs/X2-transit-capture-unavailable-20260731-193200.md`, SHA-256
`7d424420c85c97fdc76652607cf54c607f7beffdeda2ee755ea565a8b4198bbd`.
The two-file manifest is
`live-runs/manifests/X2-20260731-193200.sha256`, SHA-256
`e6c8d05d000f170e263bad24d303aa7e64e9bf3b4ccba3db4eeff5d69b26f4b0`.

No server code, database, persistent configuration, combat behavior, error
display, or F3-F6 behavior changed. No raw capture exists. X3 must therefore
select no on-wire decision row and state the unresolved measurement honestly.

X2 boundary gates passed sequentially: Debug build with only the established
CA2014 warning, combat-wire PASS, portrait-camera PASS with 10,534 specimens
and 1,224 / 1,289 / 56 controls, and move-audit PASS.

## X3 — transit decision table HARD STOP

### Actual versus predicted

```text
PREDICTED client branch: flushed or not flushed
ACTUAL: flushed=true with exact 14-byte post-encryption hash/bytes

PREDICTED wire branch: present or absent
ACTUAL: UNMEASURED; both prescribed capture boundaries require unavailable privilege

PREDICTED causal result: select one three-row transit verdict
ACTUAL: selecting present or absent would fabricate evidence
RESULT: TRANSIT_UNRESOLVED_CAPTURE_PRIVILEGE; HARD STOP
```

The decision table selects an explicit evidence-capability row: client flush is
proven, server debug admission remains absent from SPEC-21, and on-wire state is
unknown. It does not relabel the successful local write as a captured frame.

The deployed candidate table is frozen with exact citations:

- socket read/decrypt/framing/body completion:
  `Server/WorldSocket.cpp:98-148`;
- authenticated session admission and binary queue handoff:
  `Server/WorldSocket.cpp:153-183`;
- opcode lookup/parser and queue:
  `Server/WorldSession.cpp:277-331`;
- opcode 321 registration as `STATUS_LOGGEDIN` / `PACKET_PROCESS_SPELLS`:
  `Server/Protocol/Opcodes.cpp:398-401`;
- anti-flood and session-state processing gates:
  `Server/WorldSession.cpp:518-549,1250-1313`;
- handler branches: `Handlers/CombatHandler.cpp:32-62`;
- silent `Unit::Attack` returns and success send:
  `Objects/Unit.cpp:4721-4804`.

The full table, classifications, and exact client socket bytes are in
`live-runs/X3-transit-decision-20260731-193600.md`, SHA-256
`4a6a98e4d9a15846d740ccde38e8af550721a7bbdcc2674c128a5c2a25a8c802`.
The three-file manifest is
`live-runs/manifests/X3-20260731-193600.sha256`, SHA-256
`1f6e02686ed9fb6e8c60b7b3e1b6b98def944e15746dd4934570b7d127cf03c2`.

No deeper server instrumentation, server code, database, persistent config,
combat behavior, error display, or F3-F6 work was attempted. X4 must freeze the
ruling options; SPEC-21 P3/P4 remain queued.

X3 boundary gates passed sequentially: Debug build with only the established
CA2014 warning, combat-wire PASS, portrait-camera PASS with 10,534 specimens
and 1,224 / 1,289 / 56 controls, and move-audit PASS.

## X4 — attack-transit HARD STOP

### Actual versus predicted

```text
PREDICTED final transit verdict: flushed+present, flushed+absent, or not-flushed
ACTUAL client flush: true
ACTUAL on-wire state: unmeasured because prescribed capture privilege unavailable
ACTUAL server debug admission: absent from SPEC-21 P2
RESULT: TRANSIT_UNRESOLVED_CAPTURE_PRIVILEGE; HARD STOP
```

The client-defect branch is closed. The accepted `.gps` control and attack were
written 12 ms apart, both after successful socket flush, and the control was
server-delivered. The attack's exact post-encryption write is 14 bytes with
SHA-256
`784feef9f39b41853082ecfd8bb6dd47d801f1d3e7143986d79abb317c336420`.
It is not called an on-wire frame.

The prior-run reconciliation is unchanged where it matters: SPEC-21's zero
server receive/dispatch/handler debug lines remain valid negative logging
evidence; X1 strengthens the client boundary; X2 leaves packet loss versus an
unlogged server predicate unresolved; all stated precondition exclusions stand;
and SPEC-21 P3/P4 remain queued.

The ordered ruling options are:

1. smallest next step: rerun the accepted X1 scenario from a Windows
   Administrator-elevated session with bounded pktmon/netsh capture, extract the
   two frames, revert filters, and delete raw ETL/PCAP;
2. alternatively, issue a new order for a narrowly scoped, temporary Linux
   tcpdump authorization and revoke it after one capture;
3. only if the frame is captured at the host, rule on gdb or a temporary
   instrumented server rebuild on a COPY at the enumerated dispatch boundaries.

The complete packet is
`live-runs/X4-attack-transit-hard-stop-20260731-194000.md`, SHA-256
`ad07ef9533ae222bc16d6d76b210897a434e576b20416acc230f8cc03c74e5ac`.
The four-file manifest is
`live-runs/manifests/X4-20260731-194000.sha256`, SHA-256
`12f7a5c455b39aab9dfe7bc80cf6f5bd21c2fb426b12d1128daf0e520ad78dbf`;
all entries recomputed exactly.

No raw capture exists; Windows trace status is stopped; no filters remain from
this work. No server code, database, persistent config, combat behavior, error
display, or F3-F6 work changed.

**HARD STOP — SPEC-22 X0-X4 is complete at the available privilege boundary.
Nico's next ruling must authorize a capture-capable environment/path before
P3/P4 or deeper server instrumentation.**

X4 final boundary gates passed sequentially: Debug build with only the
established CA2014 warning, combat-wire PASS, portrait-camera PASS with 10,534
specimens and 1,224 / 1,289 / 56 controls, and move-audit PASS.

## Y0 - elevated capture-relay preflight

### Actual versus predicted

```text
PREDICTED direct elevation: net session PASS + High Mandatory Level
ACTUAL direct process: ELEVATION_ABSENT; no UAC retry loop was attempted

PREDICTED authorized relay: elevation and pktmon driver access PASS
ACTUAL: net session exit=0; S-1-16-12288 present; pktmon status exit=0
RESULT: elevated capture-control boundary PASS

PREDICTED filter: one TCP filter for 192.168.0.2:8085
ACTUAL: exactly SPEC23-Y1 / TCP / 192.168.0.2 / 8085
RESULT: bounded filter PASS

PREDICTED repo/connectivity: 2c71edb descendant, gates, SSH, RA PASS
ACTUAL: HEAD 2c71edb; supplied SPEC-23 preserved; KEY_AUTH_OK; RA server info PASS
RESULT: Y0 PASS; Y1 authorized
```

Nico explicitly authorized the temporary elevated relay as the SPEC-23 Y0
workaround. It is hardcoded to this repository/run, the one world endpoint,
pktmon capture lifecycle/conversion, and deletion of this order's raw files; it
has no arbitrary command channel. No install, service, firewall, server, DB,
persistent config, combat, error-display, or F3-F6 change occurred.

The full Y0 packet is
`live-runs/Y0-elevated-relay-20260731-200401.md`, SHA-256
`4a54bb911f5214e00062dce85e8a42c0b0faf26552b28f3e3d3f7aff4b40cf10`.
The four-file manifest is
`live-runs/manifests/Y0-20260731-200401.sha256`, SHA-256
`3223a0380a303cea00abd6925c06ea34a602b570ccf3f64676e28ecaf04d0064`.

Y0 boundary gates passed sequentially: Debug build with only the established
CA2014 warning, combat-wire PASS, portrait-camera PASS with 10,534 specimens
and 1,224 / 1,289 / 56 controls, and move-audit PASS. A first parallel launch
of the three check executables contended on their shared Release output; the
required sequential rerun passed and no product defect was inferred.

SPEC-21 P3/P4 remain queued.

## Y1 - bounded pktmon capture (not promoted)

### Actual versus predicted

```text
PREDICTED one bounded filtered capture: <=60 s, no packet/event loss
ACTUAL: 44.7 s, 44 packets, zero drops, zero ETW events lost
RESULT: capture lifecycle PASS

PREDICTED identical accepted X1 precondition: GM off, alive, flags zero, distance zero
ACTUAL report=act: GM off, present/visible/alive 100/100, dynamicFlags zero,
                   but unitFlags=0x00080000 and distance=9.533476 yd
RESULT: INVALID ATTACK PRECONDITION; run retained and not promoted

PREDICTED payload matching: this run's 19-byte .gps and 14-byte attack substrings
ACTUAL: both flushed=true (40 ms apart), but neither substring occurs in any frame
ACTUAL capture view: client TCP sequence advances with only ACK-only NIC records;
                     no client payload-bearing record and no covering server ACK
RESULT: CAPTURE/FILTER DEFECT; no transit causal row selected

PREDICTED raw cleanup: no ETL/PCAP/pcapng survives
ACTUAL: ETL and pcapng deleted by relay; full formatted packet text and relay/control
        files deleted after the extracted frame block was frozen
RESULT: cleanup PASS; pktmon stopped and all filters removed
```

This is not evidence that the attack was absent from the wire. The extracted
ACK-only records contain a 14-byte client sequence advance, but SPEC-23 requires
the post-encryption write as a TCP payload byte-substring, and sequence arithmetic
cannot substitute for that frame match. ACK coverage is therefore unmeasured.
No retransmission annotation or RST appeared in this defective capture view.

The invalid target state is independently disqualifying. The runner's earlier
`within3=true` assertion passed, but the random-motion spawn moved before the
report=act attack path. No second attempt was made after observing the prescribed
single repeat.

Full extracted evidence is in
`live-runs/Y1-wire-capture-20260731-200303.md`, SHA-256
`f4c923d873db8ff9ffced67029637d4dfeb43d019a4803f1a848d5b773e32c0a`.
The seven-file manifest is
`live-runs/manifests/Y1-20260731-200303.sha256`, SHA-256
`526d918f30ccaca068704dfff982095d0349724729681b6df91feb53d2315172`.

No server code, DB, persistent config, combat behavior, error display, or F3-F6
change occurred. SPEC-21 P3/P4 remain queued.

Y1 boundary gates passed sequentially: Debug build with only the established
CA2014 warning, combat-wire PASS, portrait-camera PASS with 10,534 specimens
and 1,224 / 1,289 / 56 controls, and move-audit PASS.

## Y2 - transit decision: capture/filter defect

### Actual versus predicted

```text
PREDICTED present+ACKed+silent: freeze server pre-handler/unlogged predicate
ACTUAL: neither payload substring captured; ACK coverage unmeasured
RESULT: NOT SELECTED

PREDICTED chat present + attack absent/unACKed: characterize client/LAN anomaly
ACTUAL: chat payload also absent from the packet view
RESULT: NOT SELECTED

PREDICTED neither payload present: capture/filter defect, no causal row
ACTUAL: neither payload present in 44 zero-loss NIC-component packets
RESULT: CAPTURE_FILTER_DEFECT; HARD STOP causal selection

PREDICTED accepted target: flags zero and distance zero
ACTUAL: unitFlags=0x00080000 and distance=9.533476 yd
RESULT: SCENARIO_PRECONDITION_DRIFT; independent non-promotion reason
```

The 14-byte client sequence advance is not a payload frame and is not promoted
to on-wire or ACK evidence. The X3 server candidates remain frozen but
unentered: `WorldSocket.cpp:98-183`, `WorldSession.cpp:277-331,518-549,
1250-1313`, `Opcodes.cpp:398-401`, `CombatHandler.cpp:32-62`, and
`Unit.cpp:4721-4804`. SPEC-22 option 3 therefore remains gated.

The complete decision is
`live-runs/Y2-wire-decision-20260731-201900.md`, SHA-256
`3697f3289ca076a17c86b24d6b3ea27e63a1ca0b6726cf39ab10ce5e201cb461`.
The four-file manifest is
`live-runs/manifests/Y2-20260731-201900.sha256`, SHA-256
`e4af3df51ee375600ad33d283183d5f1202b2b312892423a2438e2db46f87e67`.

No repeat or fix was attempted. No server code, DB, persistent config, combat
behavior, error display, or F3-F6 change occurred. SPEC-21 P3/P4 remain queued.

Y2 boundary gates passed sequentially: Debug build with only the established
CA2014 warning, combat-wire PASS, portrait-camera PASS with 10,534 specimens
and 1,224 / 1,289 / 56 controls, and move-audit PASS.

## Y3 - attack wire-capture HARD STOP

### Actual versus predicted

```text
PREDICTED causal transit verdict: server discard or client/LAN anomaly
ACTUAL: neither run-specific payload substring appears in the captured frames
ACTUAL ACK: unmeasured because there is no matched payload sequence range
RESULT: CAPTURE_FILTER_DEFECT; no causal transit verdict

PREDICTED identical accepted target
ACTUAL: alive and GM-off, but unitFlags=0x00080000 and distance=9.533476 yd
RESULT: SCENARIO_PRECONDITION_DRIFT; independent non-promotion reason

PREDICTED option 3 eligibility: valid payload present + ACKed + server silent
ACTUAL: payload presence and ACK not measured
RESULT: option 3 remains gated; SPEC-21 P3/P4 remain queued
```

Prior-run reconciliation: SPEC-21 P2 remains valid negative logging evidence;
SPEC-22 X1 remains valid accepted client socket-flush evidence; SPEC-22 X2-X4
remain honest at the on-wire boundary. SPEC-23 proved elevation and bounded
capture control but did not prove transit. The X3 server candidate table remains
frozen and unentered.

The recommended smallest next ruling is a new one-repeat order using pktmon
all-components rather than NIC-only, with mechanical rejection before send
unless the live target is alive, flags-zero, and distance-zero. The named netsh
fallback is second if all-components pktmon still omits both payloads. Linux
tcpdump remains excluded, and deeper server instrumentation remains gated.

En-route housekeeping: **yes**, this implementing-agent work authored preflight
commit `145db11` (`verification work`) under the configured `Yafrovon` identity.
It is retroactively recorded as the SPEC-22 X0 document-preservation commit.

The full hard-stop packet is
`live-runs/Y3-attack-wire-hard-stop-20260731-202500.md`, SHA-256
`f201b091790c026449836778080dc022a87102db7cbae9667639b4e3b199d5a6`.
The four-file manifest is
`live-runs/manifests/Y3-20260731-202500.sha256`, SHA-256
`a8a007c83e21d67619fb1a02a26456b98ff7873c353d7d98197aecaed019705c`.

The elevated relay exited; pktmon is stopped; all filters were removed; no raw
ETL/PCAP/pcapng or relay/control file survives. No server code, DB, persistent
config, combat behavior, error display, or F3-F6 behavior changed.

**HARD STOP - SPEC-23 Y0-Y3 is complete at `CAPTURE_FILTER_DEFECT` plus
`SCENARIO_PRECONDITION_DRIFT`. Nico's new ruling is required before another
capture or option-3 instrumentation.**

Y3 final boundary gates passed sequentially: Debug build with only the
established CA2014 warning, combat-wire PASS, portrait-camera PASS with 10,534
specimens and 1,224 / 1,289 / 56 controls, and move-audit PASS.

## Z0 - mechanical pre-send gate + uncaptured rehearsal

### Actual versus predicted

```text
PREDICTED DevTools gate: refuse before packet construction unless every predicate passes
ACTUAL: gate returns before NetworkClient.AttackSwing and logs pass/refusal;
        devTools=false returns true without behavior change
RESULT: report=act gate implemented

PREDICTED fresh target: new entry-6 at player, up to 3 attempts
ACTUAL attempt 1: GUID 0xF13000000604A28E, spawn distance=0,
                  gate distance=0, flags=0, alive 100/100, GM off
RESULT: PASS on attempt 1; zero refusals

PREDICTED control-to-attack <=2 s
ACTUAL: .gps flush at 7.033, attack flush at 7.040
RESULT: 0.007 s PASS

PREDICTED rehearsal: gate pass + both socket writes flushed
ACTUAL .gps SHA-256: 45fdd04c6a8d2f1e19261a030c38fd86ce0f757f94b5e5b767ca0c4639c4e811
ACTUAL attack SHA-256: 3874595e24d5d00f5e0cb2d4c8b7faf409c0d6332c097e8646c49cebd0ef8e52
RESULT: runner 26/26 PASS; no capture active
```

Symbol verification found the actual seam in `Program.Targeting.cs`, between
`ObserveAttackPrecondition(entity)` and `_net.AttackSwing(guid)`. No named X1
distance epsilon exists; Z0 conservatively uses `distanceSquared <= 1e-6`
(distance <=0.001 yd). Accepted X1 and this rehearsal both measured exact zero.

Full evidence is in
`live-runs/Z0-pre-send-gate-rehearsal-20260731-203500.md`, SHA-256
`a5bcd34c7e499ae9991189ed4e161214041552ac9906296439bfbc88524d3289`.
The ten-file manifest is
`live-runs/manifests/Z0-20260731-203500.sha256`, SHA-256
`e28157673ef94c4c8d5a54f0f96775756ea30058d1dbd29dacb75d83427fae2b`.

Z0 boundary gates passed sequentially: Debug build PASS with zero warnings on
the boundary invocation, combat-wire PASS (established CA2014 appeared during
its dependency build only), portrait-camera PASS with 10,534 specimens and
1,224 / 1,289 / 56 controls, and move-audit PASS.

No capture, server code, DB, persistent config, error display, or F3-F6 change
occurred. The only combat-path change is the authorized DevTools-gated refusal.
SPEC-21 P3/P4 remain queued.

## Z1 - bounded all-components pktmon capture

### Actual versus predicted

```text
PREDICTED elevated capture: endpoint filter, all components, full packet bytes, <=60 s
ACTUAL: elevation and High integrity proved; monitored components=All; --pkt-size 0;
        TCP 192.168.0.2:8085 filter; 38-second window; No events lost
RESULT: PASS

PREDICTED identical fresh-target scenario: gate pass, both writes flushed, <=2 s
ACTUAL: attempt 1 GUID 0xF13000000604A28F; present/visible/alive 100/100;
        flags exactly zero; GM off; distance 0; writes 0.008 s apart; runner 26/26
RESULT: PASS

PREDICTED run-local substring matching with component deduplication
ACTUAL attack: exact 14-byte substring 92E386E4B7428FA20406000030F1 present;
        one logical TCP segment, seq 817834466:817834480, retained across
        components 88/39/40/41/42/14; no retransmission or RST
ACTUAL chat: exact 19-byte substring absent from formatted capture, while the
        delivered .gps response returned at client time 7.322
RESULT: ATTACK_PRESENT_ON_WIRE; chat capture omission is not causal-row evidence

PREDICTED server ACK accounting
ACTUAL first covering server packet ACK=817834507 at 20:35:54.732850400,
        585.233 ms after attack appearance and 27 bytes beyond attack end
RESULT: ATTACK_ACKED

PREDICTED Z2 entry only if both payloads absent
ACTUAL: attack payload present
RESULT: Z2 entry condition false; netsh fallback prohibited/skipped
```

Two pktmon drops were inbound `INET: duplicate segment` records roughly five
seconds before the attack, with unrelated server ranges. Pktmon reported no ETW
events lost. They neither retransmit nor discard the attack range.

Full frame hex, ACK hex, transient-file hashes, and parsed accounting are in
`live-runs/Z1-all-components-wire-capture-20260731-203313.md`, SHA-256
`45e44bbc6615eaf3aa87ac0c90c9445e8ea5dd979d8b54b3d2457a73f54b5cb5`.
The eight-file manifest is
`live-runs/manifests/Z1-20260731-203313.sha256`, SHA-256
`adc302a5ae81f23ab6c7a28db9eac9fbceabe23979f713ff22cd7d125582138a`;
all entries recomputed exactly at the boundary.

Before deletion, the transient ETL, PCAPNG, and formatted-text hashes were,
respectively, `82c86670e0e20d60405c19caca84fc6861d1976da16fe86ff4dee8b251389711`,
`45a5d4dff565d05625df064978803f04efe452fca93a383f1c688846a90195a7`,
and `479d06d6b790b3023f830cdbb6ddfb813c934d4f8be507a5c26d53aad49ba8f6`.
All three raw files were deleted. The filter was removed, pktmon was stopped,
the elevated relay exited, and its helper/control files were deleted.

Z1 boundary gates passed sequentially: Debug build with only the established
CA2014 warning, combat-wire PASS, portrait-camera PASS with 10,534 specimens
and 1,224 / 1,289 / 56 controls, and move-audit PASS.

No server code, DB, persistent config, combat behavior, error display, or F3-F6
change occurred. Causal selection is deferred to Z3 as ordered; SPEC-21 P3/P4
remain queued.
