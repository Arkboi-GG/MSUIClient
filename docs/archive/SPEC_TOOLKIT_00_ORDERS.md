# SPEC TOOLKIT 00 — Orders for the implementing model

You are implementing **Slice 1 of the Gameplay Foundation** (`GAMEPLAY_FOUNDATION_PLAN.md`)
in the MSUIClient repository. This document is binding. The three sibling specs are the
work orders, executed **in this order, one stage at a time**:

1. `SPEC_TOOLKIT_01_VERDICTS.md` — verdict structs + ring (stages 1A–1E; build 1E
   right after 1A — the operator must be able to copy evidence in-client, never
   fish it out of terminal scrollback)
2. `SPEC_TOOLKIT_02_PORTRAIT_LAB.md` — in-game Portrait Lab + override store
3. `SPEC_TOOLKIT_03_BATCH_BAKE.md` — batch portrait bake + contact sheets + CSV

Specs for the remaining instruments (F10 gameplay dump, Action Bar Lab, Animation Lab,
wire tap/replay) are deliberately **not** issued yet; they will be written against what
Slice 1 actually lands. Do not pre-build them.

---

## 1. Read before you write

Read these fully before editing anything, in this order:

- `PROJECT_HANDBOOK.md` §on conventions (skim), `FOUNDATION_PLAN.md` §12 (DevTools layer rules)
- `GAMEPLAY_FOUNDATION_PLAN.md` (the why for everything you're building)
- `MSUIClient/Program.DevTools.cs` (the layer you are extending; copy its idioms)
- `MSUIClient/Program.Portraits.cs` (the subsystem stage 1A and spec 02/03 instrument)
- `MSUIClient/Engine/PortraitRenderTarget.cs`
- `MSUIClient/Program.ActionBars.cs` (before stage 1D only)
- `MSUIClient/Net/CastTargetLaw.cs` + its test file (before stage 1B only)
- The animator source — find it: `grep -rn "FindOrBake" MSUIClient/` (before stage 1C only)
- `July-30-20206-9AM-HANDOFF.md` and `SYSTEM_GAMEPLAY_UI.md` (current status + live-check culture)

## 2. Symbol-verification rule (anti-drift, non-negotiable)

The specs cite concrete symbols (`BoundsPortraitCamera`, `ReadbackStats`, `_config.DevTools`,
`NowSeconds()`, …). Some were read directly from source on 2026-07-30; others were inferred
from project docs. **Before using any cited symbol, confirm it exists with grep.** Then:

- Exists as cited → proceed.
- Exists under a different name/shape → use the real one, record the difference in the
  report's DEVIATIONS table.
- Does not exist and the spec step depends on it → **stop that step**, record it as a
  BLOCKED deviation with what you searched, and continue with the next independent step.
  Never invent a parallel implementation of something that "should" exist.

## 3. Hard boundaries

- **Additive only.** New behavior goes in new files (partial `GameLoop` classes in the
  `Program.DevTools.*` pattern, or `Engine/*.cs`). Edits to existing files are limited to
  the specific hooks each spec names. No drive-by refactors, renames, or reformatting;
  do not touch whitespace outside your edits.
- **Core never references DevTools** (FOUNDATION_PLAN §12). Verdict structs live in
  `Engine/` because core emits them; labs/HUD live in DevTools files and only read.
- **With `"devTools": false` in client-config.json the game must be bit-identical** in
  behavior and visuals, except: verdict ring population (cheap, allowed) and the
  already-existing console lines. New console output added by these specs must be
  either dev-gated or on-transition-only as each spec states.
- **No new package dependencies.** PNG/CSV/JSON needs are covered by what the repo
  already uses (`PortraitRenderTarget.SavePng`'s encoder, `System.Text.Json`,
  `ClientConfig`'s JSON options, plain `File.WriteAllText` for CSV).
- **Do not fix gameplay bugs you notice along the way** — including portrait framing
  values you think are wrong. The instruments exist to measure them; tuning is Nico's.
  File observations in the report under FINDINGS.
- **Do not touch** the renderer core, the network protocol, `Program.Casting.cs` logic
  (stage 1B adds an echo only), or any `NEXT_*`/`SYSTEM_*` claims.

## 4. Gates — run after EVERY stage, paste output in the report

```powershell
dotnet build MSUIClient.sln -c Debug          # gate: no new errors OR warnings
dotnet run --project tools\combat-wire-check\MSUICombatWireCheck.csproj -c Release
dotnet run --project tools\portrait-camera-check\MSUIPortraitCameraCheck.csproj -c Release -- GameData\Data
```

A stage is not done until all three pass. The pre-existing CA2014 warning is known;
anything else new is yours. If you cannot run these (no SDK in your sandbox), say so
explicitly at the top of the report and mark every stage **UNCOMPILED** — this repo has
an established protocol for that (see PORT_SESSION_2026-07-30 header) and honesty about
it is expected, not penalized.

## 5. Status language (repo law)

- **implemented** = code and wiring exist. Never implies it works.
- **verified** = a human observed the behavior in the running client. You can never
  write this word about your own work.
- Every spec ends with a *Live check* list — copy it into the report verbatim for Nico
  to run; do not shrink it.

## 6. The implementation report

End your work with `SPEC_TOOLKIT_REPORT_<date>.md` at the repo root:

```markdown
# Toolkit Slice 1 — implementation report <date>
Build status: BUILT+GATES-PASS | UNCOMPILED (reason)
## Stages completed
| Stage | Status | Commit/summary |
## Files touched
| File | New/Edit | What |
## Symbol verification
| Cited symbol | Found as | Note |
## Deviations
| Spec § | What differed | What I did instead / BLOCKED |
## Findings (bugs noticed, NOT fixed)
## Console evidence
(paste the actual [verdict:*] lines, gate outputs, batch CSV head)
## Live checks for Nico
(verbatim union of the specs' Live check lists, unmodified)
```

## 7. Working style

- One stage per commit. Commit message: `toolkit: <spec>-<stage> <summary>`.
- If context/session limits force a stop, stop **at a stage boundary** with the report
  updated to that point — a half-implemented stage is worse than a missing one.
- When a spec offers a Fallback, attempting the primary and *then* falling back is
  correct behavior, not failure — record which path shipped.
