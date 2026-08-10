#!/usr/bin/env python3
"""Regenerate parity/backlog.md from the registry. Safe to run any time."""
import json, datetime
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
entries = [json.loads(p.read_text(encoding="utf-8")) for p in (ROOT / "registry").rglob("*.json")]
entries = [e for e in entries if e["area"] != "engine"]
now = datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%d %H:%M UTC")

def open_items(e, kind):
    return [i for i in e.get("openItems", []) if i["kind"] == kind and not i.get("resolved")]

lines = [f"# MSUI ⇄ Benilla implementation backlog", "",
         f"_Generated {now} by tools/rebuild_backlog.py — do not edit; edit registry/*.json instead._", ""]

# 1. adjudicate
adj = [(e, i) for e in entries for i in open_items(e, "adjudicate")]
lines += ["## 1. Divergences awaiting Nico's ruling", "",
          "MSUI differs from Benilla. Each needs one of: port the Benilla behavior, or record a",
          "decision in `decisions/` preserving MSUI (allowed only when `deviationPolicy: ui-allowed`).", ""]
for e, i in sorted(adj, key=lambda x: x[0]["id"]):
    lines.append(f"- **{e['id']}** ({e['deviationPolicy']}): {i['text']}  ← `{i['source']}`")
if not adj: lines.append("- none")
lines.append("")

# 2. gaps
gaps = [(e, i) for e in entries for i in open_items(e, "gap")]
lines += ["## 2. Known gaps — implement these", ""]
for e, i in sorted(gaps, key=lambda x: x[0]["id"]):
    lines.append(f"- **{e['id']}**: {i['text']}  ← `{i['source']}`")
if not gaps: lines.append("- none")
lines.append("")

# 3. verification debt
ver = [e for e in entries if open_items(e, "verify")]
lines += ["## 3. Verification debt — blocked on a live authenticated session", "",
          f"{sum(len(open_items(e, 'verify')) for e in ver)} claims across {len(ver)} entries are "
          "implemented but nonterminal until live verification runs.", ""]
for e in sorted(ver, key=lambda e: -len(open_items(e, "verify"))):
    lines.append(f"- **{e['id']}** — {len(open_items(e, 'verify'))} claims to verify")
lines.append("")

# 4. unreviewed frontier
lines += ["## 4. Not yet reviewed — triage frontier", "",
          "No claims cover these yet. Review each against MSUI: classify as equivalent / missing /",
          "divergent, then promote gaps into section 2.", ""]
for area in ("protocol", "systems", "ui"):
    un = sorted([e for e in entries if e["area"] == area and e["status"] == "unreviewed"],
                key=lambda e: e["id"])
    if not un: continue
    lines.append(f"### {area} ({len(un)})")
    lines.append("")
    lines.append(", ".join(f"`{e['id'].split('/')[1]}`" for e in un))
    lines.append("")

# 5. preserved
pres = [e for e in entries if e["status"] == "preserved-msui"]
lines += ["## 5. Deliberate MSUI preferences (preserved)", ""]
for e in sorted(pres, key=lambda e: e["id"]):
    lines.append(f"- **{e['id']}** — see decisions: {', '.join(e['decisions']) or '(missing decision record!)'}")
if not pres: lines.append("- none recorded yet")
lines.append("")

(ROOT / "backlog.md").write_text("\n".join(lines), encoding="utf-8")
print(f"backlog.md: {len(adj)} adjudicate, {len(gaps)} gaps, {len(ver)} entries with verify debt, "
      f"{sum(1 for e in entries if e['status'] == 'unreviewed')} unreviewed")
