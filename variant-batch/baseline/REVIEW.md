# Variant baseline review - post 7C Option B

Canonical post-7C baselines were generated from the exact pinned W4 source
accepted in SPEC-12/W11-1. The regenerable pre-7C baseline artifacts are in
`variant-batch/history/2026-07-31-pre7C/`. All four acceptance cohort files
remain frozen at `variant-batch/baseline/npc-extras/`; the forensic diagnosis
directory is also unchanged.

## Acceptance equality

| Stage | Committed authority | Actual changed set | Set difference |
|---|---:|---:|---:|
| 7C-1 NPC attachments | 3,535 specimen keys / 33,532 batch rows | 3,535 specimen keys / 33,532 batch rows | 0 |
| 7C-2b type-6 hair | 7,677 row keys | 7,677 row keys | 0 |
| 7C-2a type-1 head composite (Option B) | 8,889 row keys | 8,889 row keys | 0 |
| Parked type-8 inheritance (forbidden change) | 689 row keys | 0 changed | 0 forbidden changes |

The combined final NPC diff against pre-7C contains 41,690 changed CSV rows
across 6,760 specimens. Items remain byte-stable under the batch diff:
`changedRows=0`.

## Final gates

| Axis | Specimens | Unexpected blanks | G3 gated | G3 raw |
|---|---:|---:|---:|---:|
| NPC extras | 6,939 | 0 | 0 | 0 |
| Items | 3,944 | 0 | 0 | 26 known/allowlisted |
| Players | 634 | 0 | 0 | 0 |

The player baseline still reports 359 duplicate-key rows. That is the queued
7C-3 protocol and was not changed by this chain.

## Viewing guide - largest absolute mean-luma changes

Mean luma is specimen-level and repeated on its CSV batch rows. These are the
ten largest absolute pre-7C to post-7C deltas; use the named sheet/cell for a
fast visual review.

| Rank | Specimen | Old | New | Delta | Sheet / cell |
|---:|---|---:|---:|---:|---|
| 1 | `npc-extra:985:display:2494` | 21.3600 | 136.2818 | +114.9218 | 14 / 51 |
| 2 | `npc-extra:7935:display:11154` | 28.1664 | 136.8135 | +108.6471 | 77 / 44 |
| 3 | `npc-extra:3281:display:5070` | 39.1560 | 147.2118 | +108.0558 | 44 / 42 |
| 4 | `npc-extra:10978:display:15967` | 35.1645 | 141.0272 | +105.8627 | 106 / 41 |
| 5 | `npc-extra:7532:display:10549` | 35.1645 | 141.0272 | +105.8627 | 74 / 41 |
| 6 | `npc-extra:8087:display:11043` | 34.7668 | 139.6030 | +104.8362 | 80 / 1 |
| 7 | `npc-extra:8337:display:11768` | 47.0487 | 151.5501 | +104.5014 | 82 / 25 |
| 8 | `npc-extra:2466:display:4216` | 43.5385 | 148.0241 | +104.4856 | 34 / 46 |
| 9 | `npc-extra:4719:display:6989` | 42.4812 | 146.9226 | +104.4414 | 53 / 51 |
| 10 | `npc-extra:7844:display:10980` | 51.0704 | 155.4957 | +104.4253 | 76 / 40 |

Named protocol rows remain the first review stop: Willem 2072/675 batch 12 is
helmeted and uses his exact npc-bare composite; control 3340/54 batch 15 binds
`Character\Human\Hair02_09.blp`, and batch 18 uses its exact npc-bare
composite. Tauren facial-hair/horn type-8 behavior remains parked for the V2b
live verdict; those 689 rows were intentionally unchanged by Option B.
