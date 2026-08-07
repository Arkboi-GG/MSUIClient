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
  --root parity\packets
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
be deliberately migrated by re-reviewing their evidence against the new pair.
