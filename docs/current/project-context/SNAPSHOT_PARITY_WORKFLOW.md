# Benilla snapshot versus MSUI snapshot parity workflow

This workflow compares one immutable Benilla source snapshot with one immutable MSUI working-tree
snapshot. Benilla defines the reference surface. MSUI does not need to contain a recognizable feature
name for a missing behavior to enter the work queue.

The implementation is `tools/snapshot-parity`. It has no external package dependencies and does not
modify either input tree.

## Mandatory overwrite gate

The report and packet materializer are read-only with respect to MSUI. A structural difference is not
permission to replace existing MSUI behavior. Every packet starts with a machine-checked `blocked`
write policy and must record **MSUI before**, the exact Benilla requirement, the change or preservation
decision, and **MSUI now**.

| Classification | Meaning | Required write policy |
|---|---|---|
| `missing` | The observable behavior does not exist in MSUI | `port` |
| `broken` | MSUI has the behavior, but reproduced evidence proves it is defective | `repair` |
| `different` | Both are valid but observably different | `preserve` |
| `intentional` | The MSUI difference is user-approved or project-authored | `preserve` plus decision id |
| `equivalent` | Different implementation, equivalent observable result | `preserve` |
| `notRuntime`, `internalSupport`, `testOnly` | No shipped parity mutation belongs here | `preserve` |
| `unreviewed` or `unclear` | Evidence is incomplete | `blocked` |

No automatic process may edit MSUI from a packet. Port and repair changes are manual implementation
steps performed only after the packet audit records the before-state and evidence. A preserve packet
may not list changed files. An implementation cannot become verified without an exact file list,
after-state evidence, and verification evidence.

Mechanical UI diffs may accept a retained MSUI difference only through an exact adjudication row naming
the element, field, expected value, actual value, decision ID, and reason. Wildcards are forbidden, every
rule must be consumed exactly once, and an unused rule fails as stale. The diff output labels these rows
`PRESERVED-DIFFERENCE`; they are never rewritten into a false equality.

## Mandatory acceptance checkpoints

A successful build, correct outer-frame dimensions, or a screenshot that merely shows the surface is not
proof of parity. Every `verified` runtime packet must record a pass or an evidence-backed not-applicable
ruling for all of these checkpoints:

| Checkpoint | What must be proven |
|---|---|
| `reference-dependency-closure` | The entire reference file plus imported, registered, called, protocol, data, template, and asset dependencies were read. |
| `state-reachability` | Every panel/mode can actually be reached and stale competing state cannot mask it. |
| `runtime-wire-contract` | Packet bytes, opcodes, indices, state ownership, event ordering, and client/server echo behavior are exact. |
| `input-modifier-contract` | Normal, Shift/Ctrl/Alt, click, double-click, drag, drop, Escape, and close behavior match where applicable. |
| `visual-geometry-anchors` | Sizes, positions, anchors, spacing, managed stacking, and scale behavior are measured from live draws. |
| `visual-containment-cropping` | Icons and models remain inside authored round/slot/mask spaces; clipping, padding, aspect fit, and overflow are inspected at pixel level. |
| `texture-coordinates-layering` | UV crops, atlas regions, blend modes, draw order, masks, highlights, disabled art, and borders are exact. |
| `interaction-bounds-states` | Hit rectangles, hover/pressed/disabled/checked/selected states, tooltips, and click-through behavior are exercised. |
| `dynamic-content-boundaries` | Zero/minimum/maximum/overflow cases and project-specific server extensions are tested without treating intentional extensions as defects. |
| `audio-count-timing` | Exact sound identity, trigger, timing, and count are captured; duplicate or unrelated sounds fail the packet. |
| `negative-behavior` | Forbidden packets, unintended opens/closes, premature sends, extra state mutations, and collateral UI replacement are explicitly tested. |
| `preserved-difference-regression` | Every retained MSUI difference is shown before and after, untouched by repair hunks, and protected by a regression scenario. |
| `deterministic-verification` | Exact laws and wire assertions cover the behavior rather than approximate constants or name-only checks. |
| `live-visual-verification` | The actual client is exercised, draw evidence and screenshots are captured for every state, and a human visual review records the result. |

For example, a bag button does not pass because its 32×32 rectangle is correct: its icon must be measured
inside the circular authored aperture, the UV/crop and layer order must be inspected, its hit target must be
tested, `B` and `Shift+B` must be distinguished, and one—and only one—correct sound may play. Likewise,
custom vMaNGOS container-capacity extensions are intentional dynamic-content cases, not reference defects.

Packet-wide `broken` or `missing` classification is not broad edit authority. A verified packet also carries
a per-trace disposition: each changed symbol or hunk must map to a specifically reproduced `broken` trace
or a specifically absent `missing` trace. Present, equivalent, different, and intentional behavior records
the exact protected MSUI symbols and may not list changed symbols.

This dossier gate begins at `implemented`, not only at `verified`. An implemented packet must already contain
real files under its own `evidence/` tree for MSUI-before, frozen reference, and MSUI-after; a directory name,
README anchor, or workspace source path is not a substitute. It must also include every acceptance checkpoint
with an honest current result or remaining-gate statement and a packet-local evidence file, plus a disposition
for every linked trace with exact changed or preserved symbols. Verification may remain open, but the clinical
record may not. Its evidence tree is SHA-256 sealed as soon as the packet becomes `implemented`; adding or
changing any dossier file without resealing makes workspace validation fail.

## Evidence layers

1. **Snapshot manifest** — every included byte is named and SHA-256 hashed. The aggregate hash is the
   snapshot identity. MSUI capture uses tracked plus untracked, non-ignored working-tree files, so dirty
   work is part of the identity. The comparison tool, its workflow document, and generated parity data
   are excluded from the MSUI identity so changing the observer cannot invalidate the observed target.
2. **Structural facts** — deterministic extractors enumerate files, symbols, protocol names, update
   fields, events, UI controls and handlers, assets, DBC/catalog references, shaders, and tests.
3. **Behavior traces** — reviewed JSONL rows connect reference facts into an atomic observable behavior.
   Every trace requires a trigger, preconditions, positive behavior, negative behavior, and hash-pinned
   reference facts.
4. **Comparison claims** — reviewed JSONL rows map traces to hash-pinned MSUI facts and give an explicit
   verdict. Mechanical candidates are discovery aids and can never certify equivalence.
5. **Typed verification artifacts** — `evidence/verification-manifest.json` maps stable artifact IDs to
   passing assertions, scenario and fixture provenance, covered traces/checkpoints, current tool/file hashes,
   and packet-local files. `evidence/evidence-index.json` seals every evidence byte by size and SHA-256.

Every typed artifact also names one packet-local `resultFile`. That file is a structured verification-result
envelope pinned to the pair, packet, both snapshot hashes, artifact ID/kind, scenario, provenance, target-snapshot
tool source, assertion counts, and SHA-256 of every raw file behind the result. The envelope must name a
structured proof file whose `kind`, `result`, and assertion counts are parsed rather than trusted as prose.
Changing a raw file, copying a result from another packet/pair, or writing `PASS` only in the outer manifest
fails validation.

Evidence text is never itself a pass result. Verified artifacts must be nonempty, hash-pinned to the exact
pair/reference/target, produced by a current target-snapshot tool, and resolve every claim and checkpoint ID.
Synthetic staging can prove rendering fixtures only; it cannot prove input routing, server wires, sound,
state reachability, dynamic behavior, negative behavior, or a live visual result. Visual acceptance requires
both a machine UI diff and an explicit screenshot review. A build cannot satisfy any visual or interaction
checkpoint.

Staged state must be explicitly labelled, must be restored after capture, and may not leak into a later
observational run. For actor- or service-dependent surfaces, fixture substitution is forbidden as runtime
proof: a player cannot stand in for a pet, a local row cannot stand in for a mailbox or quest giver, and a
synthetic panel cannot establish server-authored identity, ownership, lifecycle, or wire behavior. A surface
whose identity is itself part of the contract should reject `ui-parity-stage` rather than produce a capture
that could be mistaken for live evidence.

For `ui-mechanical-diff`, validation independently requires zero unresolved deltas, complete reference-row
instrumentation (drawn or explicitly `NOT-DRAWN`), a nonempty verdict CSV, and exact hashes for the reference,
actual, selection, output, adjudication, and executing tool files. Every `PRESERVED-DIFFERENCE` row must resolve
to an approved-deviation decision ID linked to the packet. For `ui-containment`, inside-aperture pixels must
change and outside-aperture pixels must remain unchanged. A `live-protocol` proof is rejected unless its parsed
result records `inWorld=true` and `networkState=InWorld`.

Reference UI extraction reads `benilla.source.zip` from the frozen pair, not a later desktop checkout and
not stock FrameXML from the game archives. Its manifest pins the ZIP hash, source entry, dependencies,
visual state, and output CSV hash. XML extraction is explicitly labelled `authoredXmlOnly`; when the source
contains executable scripts, `runtimeScriptStateApplied=false` remains a hard warning. A packet with
script-sized geometry, visibility, text, or data must add a hash-pinned runtime adapter or reference render;
the authored XML CSV alone cannot satisfy a visual checkpoint.

The validator fails when a required Benilla fact is unreviewed, evidence hashes are stale, a runtime
claim lacks a behavior trace, verified equivalence lacks target and verification evidence, or an approved
deviation lacks a decision id.

## Generated and reviewed data

- `parity/snapshots/` contains manifests, source-only ZIPs, fact indexes, and pair documents. It is local,
  content-addressed, reproducible output and is ignored by Git.
- `parity/reports/` contains generated dashboards and is ignored by Git.
- `parity/traces/current.jsonl` and `parity/claims/current.jsonl` are reviewed source-of-truth ledgers and
  remain source-controlled.

## Commands

Run from the MSUI repository root. Replace `$pairRoot` with a local output directory.

```powershell
dotnet run --project tools/snapshot-parity -- capture --kind benilla `
  --root C:\Users\nico\Desktop\benilla-main `
  --manifest $pairRoot\benilla.manifest.json --bundle $pairRoot\benilla.source.zip

dotnet run --project tools/snapshot-parity -- capture --kind msui `
  --root C:\Users\nico\source\repos\MSUIClient `
  --manifest $pairRoot\msui.manifest.json --bundle $pairRoot\msui.source.zip

dotnet run --project tools/snapshot-parity -- index `
  --manifest $pairRoot\benilla.manifest.json --facts $pairRoot\benilla.facts.json

dotnet run --project tools/snapshot-parity -- index `
  --manifest $pairRoot\msui.manifest.json --facts $pairRoot\msui.facts.json

dotnet run --project tools/snapshot-parity -- compare `
  --reference-manifest $pairRoot\benilla.manifest.json `
  --reference-facts $pairRoot\benilla.facts.json `
  --target-manifest $pairRoot\msui.manifest.json `
  --target-facts $pairRoot\msui.facts.json --pair $pairRoot\pair.json

dotnet run --project tools/snapshot-parity -- report `
  --pair $pairRoot\pair.json --traces parity\traces\current.jsonl `
  --claims parity\claims\current.jsonl --out parity\reports\current.md

dotnet run --project tools/snapshot-parity -- validate `
  --pair $pairRoot\pair.json --traces parity\traces\current.jsonl `
  --claims parity\claims\current.jsonl

dotnet run --project tools/snapshot-parity -- ledger-refresh `
  --pair $pairRoot\pair.json --traces parity\traces\current.jsonl `
  --claims parity\claims\current.jsonl

dotnet run --project tools/snapshot-parity -- queue `
  --pair $pairRoot\pair.json --traces parity\traces\current.jsonl `
  --claims parity\claims\current.jsonl --out parity\reports\review-queue.json

dotnet run --project tools/snapshot-parity -- packet `
  --pair $pairRoot\pair.json --queue parity\reports\review-queue.json `
  --id REVIEW_PACKET_ID --out parity\reports\active-packet.md

dotnet run --project tools/snapshot-parity -- workspace `
  --pair $pairRoot\pair.json --queue parity\reports\review-queue.json `
  --out parity\packets

dotnet run --project tools/snapshot-parity -- workspace-validate `
  --pair $pairRoot\pair.json --queue parity\reports\review-queue.json `
  --root parity\packets --out parity\reports\packet-workspace-validation.json

dotnet run --project tools/ui-parity -- extract `
  --data GameData\Data --source-zip $pairRoot\benilla.source.zip `
  --xml crates/benilla-app/assets/ui/BagFrame.xml --root BenillaBagFrame4 `
  --panel equipped-bag-reference --state normal --out parity\reports\bag-reference.csv
```

`workspace` gives every stable pair/source/chunk packet its own directory containing `README.md`,
`audit.json`, `reference.md`, and an `evidence/` directory. Packet identity does not change when work
moves from review to implementation, so the complete before/change/after history stays together.

Validation is expected to fail at the beginning: the failure is the complete, explicit unreviewed queue.
It becomes green only when every required reference fact has a reviewed disposition.

The queue has two strict phases. Nonterminal reviewed facts are emitted first as `implementation` packets,
including their current claim obligations. Facts with no reviewed disposition follow as `review` packets.
Do not start a review packet while an implementation packet exists. This keeps one port active through
code, verification, resnapshot, and re-review instead of letting a documented gap disappear or lose
priority. Every packet is source-file bounded to at most 200 facts; boundaries limit workload and do not
define behavior boundaries. Follow dependencies across files and cite facts from other packets when one
behavior crosses the boundary.

### Dependency-promotion checkpoint

Whenever a clinical trace gains facts from another frozen source file, regenerate the review queue before
selecting the next packet. The stable packet IDs do not change, but the newly linked source packets may move
into the implementation phase. Refresh their generated `reference.md` records before audit.

A promoted dependency packet is **not** complete merely because the narrow claim that promoted it is already
implemented. Review every co-resident fact in that packet and its required dependency closure. The remaining
facts may expose additional shipped behavior, internal support, tests, intentional tooling, or a separate
missing runtime surface. Give each one an atomic disposition; create or split traces as needed. Never use one
implemented cross-file claim to rubber-stamp the rest of a source packet or to hide newly discovered gaps.

This checkpoint also applies in reverse: source-level helpers and tests do not authorize a user-facing port
unless a shipped observable trace actually depends on them. Classify them `internalSupport`, `testOnly`, or
`notRuntime` when appropriate, and turn their assertions into deterministic evidence for the real trace.

## Verdict rules

- `candidateMatch`: structural resemblance only; never parity.
- `gap`: reference behavior is absent from the target snapshot.
- `divergent`: both sides exist but observable behavior differs.
- `implementedUnverified`: target evidence exists, but required verification is incomplete.
- `verifiedEquivalent`: behavior trace, target evidence, negative behavior, and verification are complete.
- `approvedDeviation`: difference is intentional and cites a user-approved decision.
- `notRuntime`, `internalSupport`, and `testOnly`: explicit classifications for facts that are not direct
  shipped behaviors. They still prevent silent omission.

Only `verifiedEquivalent`, `approvedDeviation`, `notRuntime`, `internalSupport`, and `testOnly` are
terminal. Candidate, gap, divergence, and implemented-but-unverified claims keep validation red and stay
in the generated work queue.

There is no `partial` verdict. A mixed trace must be divided until each child behavior has one honest
verdict.

## Snapshot changes

Facts carry both file hashes and evidence hashes. A new Benilla or MSUI capture produces a new pair id.
Old traces and claims then fail validation instead of silently carrying forward. Unchanged constructs can
be deliberately migrated by re-reviewing their evidence against the new pair. Runtime-equivalence and
approved-deviation claims are always downgraded to current-pair re-verification after migration; copied live
artifacts remain historical. Fact references include the entire source-file hash so a method-body change
cannot retain a declaration-only claim.

`ledger-refresh` is the conservative same-pair maintenance command. It re-pins every trace and claim to
the complete current source-file hash, rebuilds a claim's reference facts from its linked traces, refreshes
target references, and removes verification IDs from every nonterminal claim. It fails on any missing or
cross-pair fact rather than guessing a replacement.

## Live-environment truth

A checked-in `server.enabled=false` value says only that this particular launch configuration will not
connect. It is not evidence that the external server is down. A server-dependent packet is blocked only
after an attempted connection with the packet's explicit live configuration fails and the failure artifact
is captured. Temporary enabled configurations belong under ignored `live-runs/`; credentials must never be
copied into packet evidence or committed.

`--live-bootstrap` and `--live-protocol` fail before opening a window when their selected configuration has
`server.enabled=false`. An offline world-viewer window is synthetic regardless of how realistic it looks;
it cannot produce live evidence. Server-connected artifacts must additionally pin `inWorld=true` and
`networkState=InWorld`. A failed or timed-out launch and a later enabled launch are separate runs and must
never be presented as one continuous test.

When the visible window or selected session has previously been disputed, parsed connection state is
necessary but not sufficient for user-visible proof. The user must confirm that the window is the intended
authenticated game session before its screenshots or interactions can satisfy a live or visual checkpoint.
That confirmation does not replace machine connection evidence; both are required.

Live runner rows distinguish setup, action, assertion, and artifact production. Staging a panel, waiting,
arming a capture, or taking a screenshot cannot count as a behavioral assertion. A live pass requires at
least one named assertion, zero failed assertions, and every required artifact to exist and be hash-pinned.
