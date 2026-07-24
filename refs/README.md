# refs/ — real-client reference captures

Ground truth for emulation-core "done" (`FOUNDATION_PLAN.md` §2, §3.3).

One image per vantage, named to match: **`refs/<vantage-name>.png`**.

To capture: stand in the real 1.12 client at the vantage's position and facing (its
`.gps` shows coordinates; the vantage in `vantages.json` records `X` / `Y` / `Z` /
`Facing` — get close, the scene dump records any residual mismatch), screenshot, save
it here under the vantage's name.

A **paired artifact** is three things sharing one vantage name `<v>`:

- `refs/<v>.png` — what the real client draws (the target, for core work)
- your MSUI screenshot — what MSUI draws (your eyes)
- `dumps/<v>.json` — what MSUI decided and why (the assistant's data)

That shared name is what lets your eyes, the real client, and the assistant's data all
line up on the exact same frame — which is the whole point of the foundation.

These are committed to git (unlike `dumps/`, which are transient and gitignored).
