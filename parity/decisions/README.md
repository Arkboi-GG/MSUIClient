# decisions/ — preserved-MSUI rulings

One numbered markdown file per deliberate deviation from Benilla: `NNNN-<slug>.md`
(e.g. `0001-bagframe-slot-art.md`). These exist so a deviation is never mistaken for a gap.

**Scope rule (hard):** decisions may only cover UI/graphics presentation. Gameplay behavior,
wire protocol, timing, and state machines always match Benilla — a decision file proposing
otherwise is invalid, and agents must refuse to create one for a `must-match` registry entry.

Template:

```markdown
# NNNN — <title>

- Registry entry: <ui/...>
- Date: YYYY-MM-DD
- Decided by: Nico

## Benilla behavior
<what Benilla does, with file references>

## MSUI behavior (preserved)
<what MSUI does instead, with file references>

## Why
<the actual reason>

## Boundaries
<what parts of the entry still must match Benilla — e.g. layout preserved but the
underlying click/packet behavior still matches>
```

After writing one: set the registry entry's `status` to `preserved-msui` (or `partial` if only
part of the entry is preserved), append the decision id to its `decisions[]`, resolve the
originating adjudicate item, then run `tools/rebuild_backlog.py`.
