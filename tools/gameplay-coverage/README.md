# Full-game discovery inventory

Download `quest_template` from the configured data service's read-only
`/Database/Export/mangos/quest_template` endpoint into an ignored local file, then:

```
dotnet run --project tools/gameplay-coverage -- <quest_template.csv> <output-directory>
```

Reads all mounted Spell.dbc records through the shipping catalogs. Writes every
spell ID, stage/animation/model references, skill routing, and candidate cohorts;
every latest quest row at content patch 10; overlapping quest families; and a
summary with the quest export hash. Output is deterministic for unchanged inputs.
Place outputs under `docs/current/`; the tool and coverage contract are tracked.

Cohorts include cast classification, visual, effect/aura/target lanes, missile
presence and passive status. They prioritize investigation only: ranks, resources,
race/sex models, talents, equipment and conditions can still differ. A candidate
is not necessarily learnable or safe to cast. No live passes are generated.

Quest negative integers carry a spreadsheet-protection apostrophe in this service;
the parser retains their sign. It rejects invalid numeric fields rather than
silently assigning them zero. Disabled quest methods remain visible. Scripted
escort/defense semantics cannot be determined solely from quest_template.

Asset checks cover referenced stage/missile/area models, not their complete nested
texture/sound/chain dependencies or rendered correctness. Unresolved references
are triage leads, including historical dead rows, not automatic product defects.
See `shared_docs/FULL_GAME_COVERAGE.md` for execution and acceptance requirements.

`./tools/gameplay-coverage/Summarize-Live.ps1 -RunDirectory <native-run-output>`
collects each animation cell's wire evidence only between that cell's first sample
and end timestamp. It does not borrow another attempt's successful cast. The output
preserves diagnostic findings and requires frame/mechanic review; it never emits a
gameplay PASS. Triggered child spells and replies after the sampled interval need
separate lifecycle review.

`./tools/gameplay-coverage/Test-SummarizeLive.ps1` verifies that a failed second
attempt cannot inherit a first attempt's success, another spell's GO, or a late GO.

Spell cast classification follows build-5875 AttributesEx channel/self-channel bits,
not the presence of channel-interrupt flags. `--spell-classification-only` in
interface-wire-check covers mounted trap placement9437, ordinary casts/instant
spells and true channels. Inventories generated before the September8 correction
retain their historical classifications; regenerate into a fresh output directory
and preserve their evidence rather than treating reclassified cohorts as new passes.
