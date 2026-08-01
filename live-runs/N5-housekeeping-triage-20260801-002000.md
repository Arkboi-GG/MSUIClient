# NIGHT_01 housekeeping triage — items 5-1, 5-2, 5-4, and 5-6

Run: 2026-08-01 00:20 local

## Actual versus predicted

```text
5-1 PREDICTED: I16 layout verdicts then I17 pane sweep and committed baseline
5-1 ACTUAL: I17 acceptance necessarily creates a baseline/cohort, which NIGHT_01
            reserves to Nico; no partial pane implementation was started
5-1 RESULT: SHELVED-RULING (Q9)

5-2 PREDICTED: I18 store -> vanilla defaults -> matrix -> menu
5-2 ACTUAL: I18.2 requires new committed vanilla acceptance-baseline data, which
            NIGHT_01 reserves to Nico; no partial keybind implementation was started
5-2 RESULT: SHELVED-RULING (Q10)

5-4 PREDICTED: launch Benilla with the recorded exact invocation and capture Stage B
5-4 ACTUAL: the required Stage-A invocation/working directory is absent from SETUP
            and repo search; later interaction is forbidden by the night order
5-4 RESULT: SHELVED-BLOCKED (Q11)

5-6 PREDICTED: mechanically recheck every deferred model/item entry
5-6 ACTUAL: no cheap non-perceptual batch can distinguish rendering blanks/variants;
            all 15 portrait and 58 item entries are classified needs-Nico with this
            fresh source-list hash evidence
5-6 RESULT: CLOSED-FINDING (Q13)
```

Deferred source hashes:

- `portrait-known-blank.txt` (15 entries): `3a4665110889c5812053c47da332b74a55e6bff7825901113032aeb7daba9c20`
- `variant-items-known-issues.txt` (58 entries): `5b7b5f9ab09665655433ce139d4046241ab14645e6ee35348b21c649118fd06a`

No deferred list, baseline, cohort, law, F3-F6 path, or client behavior changed.
