# NIGHT_03 run-scoped sweep policy

- World-ready bootstrap is bounded to 180 seconds in `Program.LiveRun.cs`.
- A launcher may retry a bootstrap-only failure at most three times. Protocol
  failures are evidence and are not retried as bootstrap failures.
- The resolved GM syntax, disposable target, and world-ready setup are reused
  for every cell in one run. `spell-matrix-scenario` spawns one entry-6 target,
  clears its auras and refills its health between cells, and deletes it only at
  the end of that run.
- A corrected instrument reruns only cells whose verdict can change. The
  layer-2 repair changes every Mage spell-visual verdict, so the Mage layer-2
  recapture covers all 348 cells while the withdrawn layer-1 capture remains
  historical evidence.
