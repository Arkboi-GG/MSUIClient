#!/usr/bin/env python3
"""Compare registry entries against the latest Benilla snapshot facts.

Run after every new Benilla snapshot lands in snapshots/current/. Reports:
  - CHANGED: a benilla source file's hash differs from what the entry was reviewed against
  - REMOVED: a benilla source file no longer exists
  - UNCOVERED: benilla files not claimed by any registry entry (new features)
Exit code 1 if anything drifted, so it can gate automation.
"""
import json, sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
benilla = json.loads((ROOT / "snapshots" / "current" / "benilla.facts.json").read_text(encoding="utf-8"))
latest = {f["path"]: f["fileSha256"] for f in benilla["facts"]}
covered, drifted = set(), False

for p in sorted((ROOT / "registry").rglob("*.json")):
    e = json.loads(p.read_text(encoding="utf-8"))
    for s in e["benillaSources"]:
        covered.add(s["path"])
        now = latest.get(s["path"])
        if now is None:
            print(f"REMOVED   {e['id']}: {s['path']}")
            drifted = True
        elif now != s["fileSha256"]:
            print(f"CHANGED   {e['id']}: {s['path']}")
            drifted = True

uncovered = sorted(set(latest) - covered)
for path in uncovered:
    print(f"UNCOVERED {path}")
if uncovered: drifted = True

if not drifted:
    print("registry is current with the snapshot — no drift")
sys.exit(1 if drifted else 0)
