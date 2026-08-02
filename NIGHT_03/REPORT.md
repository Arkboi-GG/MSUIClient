# NIGHT_03 — full spell validation, animation-first — report

Acceptance instrument: the animation harness in `2_ANIMATION_HARNESS.md`.
A spell passes only when it visibly ANIMATES — expected DBC animation ID
played, proven by state change across a captured frame sequence — and its
mechanical effect lands.

Per-item sections append below.

Main toolkit report: `../SPEC_TOOLKIT_REPORT_2026-08-02.md`.

## 0-1 — silence discrimination and roster

Status: `CLOSED-FINDING`.

- The executable deletion fence requires both an `NB` prefix and an
  `AGENT_CREATED` row in `NIGHT_03/roster.csv`. Its committed test refused
  `TEST` with no delete call.
- Initial greg roster inspection found one pre-existing character, `Gondolfo`.
  It was never entered, renamed, stripped, or deleted. Eight NB characters were
  created. The ninth class representative was refused at the realm slot limit
  and remains a later wave after an owned character finishes its class sweep.
- Entry-symbol GDB discrimination was re-attempted. Key-only SSH to the recorded
  host was refused because this machine has no installed private key; no ptrace
  claim is made. RA health succeeded and the world stayed healthy.
- On the supplied full-admin account, GM-off Arcane Intellect 1459 received
  `SMSG_SPELL_START` and `SMSG_SPELL_GO` and applied its aura. GM-off hostile
  Fireball 133 against disposable entry 11583 passed the acting-path target
  gate, received both packets, and completed its 1,500 ms cast. Historical
  `F-SILENT-INTERACT` does not reproduce on greg. Failed entry-6 probes remain
  findings: acting-path evidence showed the target was dead before cast, so
  those were local `NoValidUnit`, not silence.

Evidence: `live-runs/N3-0-1-silence-roster-20260801-200000/`.

## 0-2 — animation acceptance harness

Status: `CLOSED-PASS`.

Coverage first: `2 / 2` proof cells `MEASURED`; `0 NOT-PRESENT`;
`2 ANIM-EXACT`; `0 ANIM-FALLBACK`; `0 ANIM-STATIC`;
`0 ANIM-ASSET-MISSING`.

The sequence channel samples the acting renderer mixer, active spell-effect
instances, and server-driven cast stage. It never invokes an animation. Every
sample records timestamp, requested/played IDs, resolution kind, presentation,
base/action/hold states, blend weight, locomotion, GM state, active model
bindings, and MPQ suppliers. Cell verdicts are derived from the samples and
refuse coverage below 14 frames.

Arcane Intellect rank 1 standing passed `ANIM-EXACT` (DBC cast animation 54;
renderer played 54 with changing `Stand -> Anim54 -> Stand`). Moving passed
`ANIM-EXACT` and `BLEND-CROSSFADE`. Both cells were GM OFF and independently
proved the aura landed. Fourteen actual frames per cell are labeled with time,
expected ID, played ID, renderer state, and blend weight.

Evidence: `live-runs/N3-0-2-animation-harness-20260801-194500/`.

Boundary gates: Debug build PASS (0 warnings, 0 errors); combat-wire PASS;
interface-wire PASS; portrait-camera PASS (10,534 / 1,224 / 1,289 / 56);
move-audit PASS.

## 3-1 — Mage cohort start

Status: `OPEN`.

The predeclared untalented level-60 Mage cohort contains 174 non-passive
spell-ranks and 15 separately materialized passives. The DBC/MPQ reference
table covers all 174 castable rows: 169 visual chains measured and five
`NOT-PRESENT` because Spell.dbc has no visual ID; no referenced model path is
missing from the mounted MPQs. No full-sweep PASS count is claimed yet.

Reference: `live-runs/N3-0-3-mage-20260801-195500/mage-expected-animation.csv`.
Keys: `NIGHT_03/cohorts/mage-untalented-level60-nonpassive.keys` and
`NIGHT_03/cohorts/mage-untalented-level60-passive.keys`.

## Instrument-truth correction -- spell visual layer

Status: `INSTRUMENT-BLOCKER`; the earlier 0-2 acceptance and all Mage PASS
counts are withdrawn. Item 3-2 has not opened.

The first Mage matrix measured only the caster skeleton. Its 116 `ANIM-EXACT`
cells did not prove that a spell visual rendered: 341 of 348 cell summaries had
an empty `active_models`, including 114 of the 116 exact-animation rows. The
corrected schema carries independent `CASTER-ANIM-*` and `SPELL-VISUAL-*`
verdicts. Layer 2 has renderer-derived PRECAST, CAST, MISSILE, and IMPACT
sub-verdicts; missile PRESENT additionally requires distinct world positions
over time. `asset_sources` is only resolution evidence. It cannot substitute
for a live renderer instance or a draw submission.

Diagnosis found 44 distinct Mage model paths. Before the repair, 17 existing
particle-only M2 files failed parsing because `M2Reader` rejected zero-vertex
models before reading their particle emitters. After allowing a valid model to
contain emitters without mesh vertices, all 44 parse as drawable. This explains
the broad hand/cast/impact instantiation failure. A second bounded control found
that entry 11583 rejected Fireball, while a fresh entry-6 target produced a real
hit list. The final Fireball proof, GM OFF, records PRECAST PRESENT, CAST
PRESENT, MISSILE PRESENT at six changing positions between caster and target,
and IMPACT PRESENT. Its composite spell-visual verdict is
`SPELL-VISUAL-PRESENT`.

The historical `asset_sources` duplication was a citation-join defect, not two
stages resolving to one file. The join is now case-insensitively deduplicated,
and cell summaries cite the union of all expected stage paths while
`active_models` cites the union actually instantiated during the sequence.

The 108 historical `ANIM-STATIC` cells group by expected animation ID as:
`-1:78`, `54:19`, `53:6`, `125:4`, `124:1`. No single missing model source
explains them. The largest group has no authored animation ID; the remaining
groups span fire, magic, ice, and ritual assets. The committed cell list is
`live-runs/N3-0-3-mage-20260801-195500/mage-caster-static-by-expected.csv`.

Evidence: `mage-visual-load-diagnosis.csv`,
`mage-visual-load-diagnosis-after.csv`, `fireball-layer2-proof-v6/`,
`NIGHT_03/GM_SYNTAX.md`, and `NIGHT_03/RUN_POLICY.md`.
