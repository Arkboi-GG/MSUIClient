# SPEC TOOLKIT 03 — Batch portrait bake: contact sheets + verdicts.csv

Instrument I3. Bake **every** specimen's portrait unattended and emit machine-readable
evidence, so framing bugs are found as a pattern ("all quadrupeds blank") instead of
one wolf at a time, and so every framing change has a regression diff. Requires
SPEC 01 stage 1A and SPEC 02 stage 2A (tuning/overrides must be honored so the batch
measures what the game will actually show).

---

## 0. Architecture decision (made — do not revisit)

The batch runs **inside the client process** as a command-line mode, not as a separate
tool: `MSUIClient.exe --portrait-batch [options]`. Rationale: the bake requires the
real GL context, the real booth (`PortraitRenderTarget`), the real renderers and the
real framing code; a standalone tool would either duplicate them (drift — forbidden)
or drag the engine into a second host (the hard part of headless GL for zero gain).
`tools/portrait-camera-check` stays as-is (it is the no-GL vertex-count harness; the
batch is its pixel-level sibling).

The mode initializes the window + GL + GameData exactly as a normal launch (window
may be visible; do not fight for true headless), **skips login/networking entirely**,
runs the sweep, writes outputs, prints a summary, and exits with a meaningful code.
Find the earliest point in startup where GL + MPQ/DBC + the creature renderer are
usable but before any login/realm UI work begins; hook there. If startup is too
entangled to stop before login cleanly, fallback: run the sweep at the login screen
state and exit (record which).

## 1. CLI

```
MSUIClient.exe --portrait-batch
    [--out <dir>]            default: portrait-batch/<yyyyMMdd-HHmmss>/ at repo root
    [--list <file>]          specimen list file; default: all creature display ids
    [--limit <n>]            first n specimens (smoke runs)
    [--diff <verdicts.csv>]  compare against a previous run, print + write changes
    [--unmasked]             skip circular mask in saved PNGs (default: masked)
```

Parse args wherever the client already reads its command line (grep `args` in
`Program.cs` / `ClientWindow` startup; follow the existing style; unknown flags =
print usage, exit 2).

Specimen list file format, one per line, `#` comments:
```
creature:30        # wolf
creature:1141
player:dwarf-male  # accepted but SKIPPED in v1 unless 2C found a cheap path — see §4
```

## 2. The sweep

For each specimen, sequentially on the main loop thread (no parallelism — GL):

1. Resolve tuning via the same `ResolveTuning(key)` as the live game (overrides honored).
2. Bake through **the shared camera-selection helper from SPEC 02 stage 2C** (the
   authored → override → bounds precedence extracted there). Reuse one
   `PortraitRenderTarget`; do not allocate per specimen.
3. Asynchronous model streaming: after requesting the bake, pump the client's
   update/streaming loop until the model reports loaded or a **10 s per-specimen
   timeout** elapses (find how the world loop pumps streaming; drive the same calls).
   Timeout → outcome `Skipped`, note `timeout` in CSV, continue. Never hang the sweep.
4. `Analyze()` → build the same `PortraitVerdict` the live game builds (ring +
   collect into the batch list).
5. Save `<out>/<key>.png` (256², masked unless `--unmasked`) via `SavePng`.
6. Every 25 specimens print progress: `[batch] 150/1240 ok=141 blank=6 notdrawn=2 skipped=1`.

Memory: if the creature model cache grows unbounded across hundreds of models, find
its eviction mechanism and call it periodically; if none exists, record a deviation
and cap the run with an internal batch size (process in chunks with cache reset
between chunks if a reset exists; otherwise document the practical `--limit`).

## 3. Outputs

All under `<out>/`:

- **`verdicts.csv`** — header row then one row per specimen, exactly these columns:
  `key,kind,displayId,modelPath,outcome,cameraSource,authoredRetried,subjectPx,rgbLo,rgbHi,alphaLo,alphaHi,pieces,bindPoseHeight,eyeHeight,distance,fovyDeg,nearPlane,elapsedMs,note`
  Plain `File.WriteAllLines`, invariant culture, no quoting needed (no commas in
  fields; replace any with `;`).
- **`contact-sheet-<nn>.png`** — 8×8 grids of the 256² bakes (2048² sheets), row-major
  in sweep order, each cell labeled bottom-left with `displayId` burned into pixels
  (a tiny 5×7 digit stamper over the bake — digits only, ~30 lines of code; if that
  is more trouble than it looks, fallback: no in-image label + a
  `contact-sheet-<nn>.txt` index mapping cell → key, and record the deviation).
  Compose CPU-side: keep each bake's RGBA readback (the readback path exists —
  `Analyze`/`SavePng` read pixels; reuse it), blit into a 2048×2048×4 byte buffer,
  encode with the same PNG encoder `SavePng` uses.
- **`summary.txt`** — totals per outcome, the gate results (§5), top 20 worst
  specimens (blank first, then lowest subjectPx), run duration, client git describe
  if cheaply available.
- **with `--diff`**: `diff.txt` — rows whose `outcome` changed, or whose `subjectPx`
  moved by more than 15% relative; format `key: old → new`. Print the same to console.

## 4. Specimen sources

- **Creatures (v1 core):** every display id the client's CreatureDisplayInfo catalog
  can enumerate (same source the Lab's specimen list uses — share the enumeration
  code with SPEC 02 2C, one implementation). Sort ascending.
- **Players (v1 optional):** only if SPEC 02's discovery found that a naked
  race/gender model can be built through the char-create pipeline without a server —
  then `player:<race>-<gender>` keys for all 8 races × 2 genders join the default
  sweep. Otherwise the batch **skips** `player:*` list entries with outcome
  `Skipped`/`unsupported-v1` and the report says so. Do not force this; creatures
  are the volume win.

## 5. Gates (computed in summary.txt, drive the exit code)

- `G1 blanks`: count(outcome==Blank) — target 0.
- `G2 subject band`: count(Ready with subjectPx outside [800, 30000]) — suspicious
  crop/zoom; listed, not failing.
- Exit code: 0 = G1 pass; 3 = G1 fail; 1 = crash/incomplete; 2 = usage. (`--diff`
  never changes the exit code; it is informational.)

The band constants live at the top of the batch file with a comment that they are
heuristics Nico may retune; they are not law.

## 6. Files

| File | New/Edit | What |
|---|---|---|
| `MSUIClient/Program.PortraitBatch.cs` *(new, partial GameLoop)* | new | arg parse hook, sweep, CSV/sheet/summary writers |
| `Program.cs` (or actual startup site) | edit | one hook: divert into batch mode when the flag is present |
| SPEC 02's shared camera-selection helper | edit | none beyond consuming it — if 02 shipped its fallback (no helper), extract it now as part of this spec |

## 7. Test protocol / definition of done

1. `--portrait-batch --limit 20` completes end-to-end, writes 20 PNGs + CSV + one
   contact sheet + summary; exit code reflects G1.
2. CSV numbers for any specimen match a live-game bake of the same specimen at the
   same overrides (spot-check 2 via the Portrait Lab) — proving batch == game path.
3. Full run (no limit) completes without OOM/hang; duration + totals in the report.
4. Change one override, rerun with `--diff` → exactly that specimen appears in diff.txt.
5. Normal launch (no flag) is completely unaffected.

## Live checks for Nico (copy into report verbatim)

1. Run the full sweep; send the assistant `verdicts.csv` + the first contact sheet +
   `summary.txt`. (This artifact set replaces screenshot-driven portrait debugging.)
2. Skim the sheets by eye for anything framed absurdly that the gates didn't flag —
   name the displayId; it becomes a Lab session, not a code change.
