#!/usr/bin/env python3
"""Compare registry entries against the latest Benilla snapshot facts.

Run after every new Benilla snapshot lands in snapshots/current/. Reports:
  - CHANGED: a benilla source file's hash differs from what the entry was reviewed against
  - REMOVED: a benilla source file no longer exists
  - UNCOVERED: benilla files not claimed by any registry entry (new features)
Exit code 1 if anything drifted, so it can gate automation.
"""
import json, re, sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
benilla = json.loads((ROOT / "snapshots" / "current" / "benilla.facts.json").read_text(encoding="utf-8"))
latest = {f["path"]: f["fileSha256"] for f in benilla["facts"]}
snapshot_id = benilla["snapshotId"]
covered, drifted = set(), False
accept_current = "--accept-current" in sys.argv
accepted = 0

for p in sorted((ROOT / "registry").rglob("*.json")):
    e = json.loads(p.read_text(encoding="utf-8"))
    replacements = []
    for s in e["benillaSources"]:
        covered.add(s["path"])
        now = latest.get(s["path"])
        if now is None:
            print(f"REMOVED   {e['id']}: {s['path']}")
            drifted = True
        elif now != s["fileSha256"]:
            if accept_current:
                replacements.append((s["path"], now))
                accepted += 1
            else:
                print(f"CHANGED   {e['id']}: {s['path']}")
                drifted = True
    if replacements:
        text = p.read_text(encoding="utf-8")
        for path, sha in replacements:
            pattern = (
                r'("path"\s*:\s*"' + re.escape(path) +
                r'"\s*,\s*"fileSha256"\s*:\s*")[^"]+("\s*,\s*"snapshotId"\s*:\s*")[^"]+("\s*})'
            )
            text, count = re.subn(pattern, rf'\g<1>{sha}\g<2>{snapshot_id}\g<3>', text,
                                  count=1, flags=re.DOTALL)
            if count != 1:
                raise RuntimeError(f"could not update reviewed source {path} in {p}")
        p.write_text(text, encoding="utf-8")

uncovered = sorted(set(latest) - covered)
for path in uncovered:
    print(f"UNCOVERED {path}")
if uncovered: drifted = True

if accept_current:
    print(f"accepted {accepted} reviewed source hash(es) against {snapshot_id}")

if not drifted:
    print("registry is current with the snapshot — no drift")
sys.exit(1 if drifted else 0)
