#!/usr/bin/env python3
"""Refresh only the non-behavioral engine inventory from snapshots/current.

Unlike seed_registry.py --force, this preserves every curated UI/system/protocol entry.
New behavioral files remain UNCOVERED so check_drift.py still forces their review.
"""
import json
from pathlib import Path

from seed_registry import classify

ROOT = Path(__file__).resolve().parent.parent
facts_doc = json.loads((ROOT / "snapshots/current/benilla.facts.json").read_text(encoding="utf-8"))
snapshot_id = facts_doc["snapshotId"]
files = {fact["path"]: fact["fileSha256"] for fact in facts_doc["facts"]}
engine_paths = sorted(path for path in files if classify(path)[0] is None)

entry_path = ROOT / "registry/engine/internals.json"
entry = json.loads(entry_path.read_text(encoding="utf-8"))
entry["benillaSources"] = [
    {"path": path, "fileSha256": files[path], "snapshotId": snapshot_id}
    for path in engine_paths
]
entry_path.write_text(json.dumps(entry, indent=2) + "\n", encoding="utf-8")
print(f"engine/internals: refreshed {len(engine_paths)} files against {snapshot_id}")
