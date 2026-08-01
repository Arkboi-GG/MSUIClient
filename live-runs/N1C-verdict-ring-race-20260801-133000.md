# N1C verdict-ring concurrency finding — 2026-08-01 13:30 local

## Instrument

The first 2-11 live run retained the full thrown stack: `VerdictRing.ChannelRing.Snapshot()`
dereferenced an empty nullable slot while the network/render paths were concurrently adding and
snapshotting verdicts. The failure occurred before the scenario's first `waitfor` completed.

## Root cause and increment

`ChannelRing.Add` published `_count` independently from the nullable array slot and rotated `_start`
without synchronization. `Snapshot` could therefore observe a new count or start with the matching
slot not yet visible. Channel mutation/snapshot is now guarded by one channel-local lock and global
sequence allocation uses `Interlocked.Increment`.

## Regression

`combat-wire-check` now performs 10,000 parallel adds interleaved with full-ring snapshots and
asserts the channel remains non-null and bounded at 128 entries.
