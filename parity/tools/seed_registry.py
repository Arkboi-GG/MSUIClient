#!/usr/bin/env python3
"""Seed parity/registry from the current snapshot pair, claims, and packet audits.

One registry entry per logical "thing" (UI surface, protocol module, client system).
Rerunnable with --force; without it, refuses to overwrite an existing registry so
curated edits are never clobbered by accident.
"""
import json, re, sys, collections, datetime
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SNAP = ROOT / "snapshots" / "current"
PAIR_DIR = next((ROOT / "packets").glob("pair-*"))
NOW = datetime.datetime.now(datetime.timezone.utc).isoformat()

# ---- benilla app modules that are pure renderer/harness/infrastructure: MSUI has its
# own engine, so these are inventory, not backlog ----
ENGINE_MODULES = {
    "terrain_stream", "wmo_portal", "wmo_sky", "wdl", "sky", "sky_order", "clouds",
    "clutter", "collision", "lighting", "liquid", "particles", "creature_anim",
    "doodad_anim", "go_anim", "decal", "footprints", "water_fx", "weather", "sun",
    "model_fade", "model_forms", "model_render", "mesh_tag", "rig_palette", "ribbons",
    "billboard", "blob_shadow", "bowstring", "entity_shade", "exterior_cull",
    "ffx_glow", "ground_fx", "instance_tint", "interior", "map_proj", "zfill",
    "vplates", "art_scope", "asset_churn", "pipe_warm", "perf", "thread_qos",
    "preflight", "dbg_trace", "hover_log", "debug_panel", "probe_shield", "capture",
    "smart_rect", "schedule", "bgwin", "build_id", "lib", "view", "assets",
    "ui_script", "wmo", "exterior", "zone_light",
}

# systems whose look-and-feel may deviate per Nico's preference (UI/graphics only)
VISUAL_SYSTEMS = {
    "combat_text", "chat_bubble", "aura_visual", "portrait", "quest_markers",
    "raid_marks", "loading_screen", "glue_strings", "cursor",
}

# ui_<module> -> assets/ui/<Xml>.xml surface key (lowercase basename, no extension)
UI_MODULE_MAP = {
    "aura": "buffframe", "items": "bagframe", "action": "actionbar",
    "loot_roll": "grouplootframe", "social": "friendsframe", "char": "characterframe",
    "cast": "castingbar", "item_text": "itemtextframe", "world_map": "worldmapframe",
    "quest_log": "questlogframe", "pet": "petactionbar", "pet_stats": "petactionbar",
    "mirror": "mirrortimer", "unit": "unitframes", "tooltip": "gametooltip",
    "shapeshift": "stancebar", "quest": "questframe", "mail": "mailframe",
    "trade": "tradeframe", "spellbook": "spellbookframe", "inspect": "inspectframe",
    "bank": "bankframe", "chat": "chatframe", "gossip": "gossipframe",
    "loot": "lootframe", "merchant": "merchantframe", "macro": "macroframe",
    "craft": "craftframe", "tradeskill": "tradeskillframe", "trainer": "trainerframe",
    "taxi": "taxiframe", "talent": "talentframe", "duel": "duelframe",
}

def load_json(p):
    return json.loads(Path(p).read_text(encoding="utf-8"))

def load_jsonl(p):
    return [json.loads(l) for l in Path(p).read_text(encoding="utf-8").splitlines() if l.strip()]

def classify(path):
    """-> (entry_id, area) or (None, 'engine') for the shared inventory."""
    m = re.match(r"crates/benilla-app/assets/ui/([^/]+)\.xml$", path)
    if m:
        return "ui/" + m.group(1).lower(), "ui"
    m = re.match(r"crates/benilla-app/src/([^/.]+)", path)
    if m:
        mod = m.group(1)
        if mod in ENGINE_MODULES or "/tests" in path or path.endswith("tests.rs"):
            return None, "engine"
        if mod.startswith("ui_"):
            key = UI_MODULE_MAP.get(mod[3:])
            if key:
                return "ui/" + key, "ui"
            return "systems/" + mod, "systems"
        return "systems/" + mod, "systems"
    m = re.match(r"crates/benilla-protocol/src/messages/([^/.]+)", path)
    if m:
        return "protocol/" + m.group(1), "protocol"
    m = re.match(r"crates/benilla-protocol/src/world/writer/([^/.]+)", path)
    if m:
        return "protocol/" + m.group(1), "protocol"
    m = re.match(r"crates/benilla-protocol/src/(?:world/)?([^/.]+)", path)
    if m:
        return "protocol/" + m.group(1), "protocol"
    return None, "engine"  # other crates, root files, workflows

def main():
    force = "--force" in sys.argv
    reg_dir = ROOT / "registry"
    if reg_dir.exists() and any(reg_dir.rglob("*.json")) and not force:
        sys.exit("registry/ already exists — pass --force to regenerate (curated edits will be lost)")

    benilla = load_json(SNAP / "benilla.facts.json")
    msui = load_json(SNAP / "msui.facts.json")
    fact_path = {f["id"]: f["path"] for f in benilla["facts"]}
    msui_fact_path = {f["id"]: f["path"] for f in msui["facts"]}
    file_sha = {f["path"]: f["fileSha256"] for f in benilla["facts"]}
    snapshot_id = benilla["snapshotId"]

    claims = load_jsonl(ROOT / "claims" / "current.jsonl")
    traces = load_jsonl(ROOT / "traces" / "current.jsonl")

    # entries keyed by id
    entries, engine_files = {}, []
    for path in sorted({f["path"] for f in benilla["facts"]}):
        eid, area = classify(path)
        if eid is None:
            engine_files.append(path)
            continue
        e = entries.setdefault(eid, {
            "schemaVersion": 1, "id": eid, "title": eid.split("/")[1], "area": area,
            "deviationPolicy": "must-match", "benillaSources": [], "msuiAnchors": [],
            "status": "unreviewed", "summary": "", "openItems": [],
            "claims": [], "traces": [], "decisions": [], "lastReviewedUtc": None,
            "seededUtc": NOW,
        })
        e["benillaSources"].append({"path": path, "fileSha256": file_sha[path], "snapshotId": snapshot_id})

    for e in entries.values():
        mod = e["id"].split("/")[1]
        if e["area"] == "ui" or mod in VISUAL_SYSTEMS:
            e["deviationPolicy"] = "ui-allowed"

    # ---- fold in claims ----
    def entry_for_fact(fid):
        p = fact_path.get(fid)
        if not p: return None
        eid, _ = classify(p)
        return entries.get(eid) if eid else None

    trace_by_id = {t["id"]: t for t in traces}
    for c in claims:
        touched = {}
        for fr in c["referenceFacts"]:
            e = entry_for_fact(fr["id"])
            if e is not None: touched[e["id"]] = e
        for e in touched.values():
            e["claims"].append(c["id"])
            for tid in c.get("traceIds", []):
                if tid not in e["traces"]: e["traces"].append(tid)
            for tf in c.get("targetFacts", []):
                p = msui_fact_path.get(tf["id"])
                if p and p not in e["msuiAnchors"]: e["msuiAnchors"].append(p)
            v = c.get("verdict")
            if v == "gap":
                e["openItems"].append({"id": f"item-{c['id']}", "kind": "gap",
                    "text": c.get("summary", ""), "source": c["id"], "added": NOW[:10], "resolved": False})
            elif v == "divergent":
                e["openItems"].append({"id": f"item-{c['id']}", "kind": "adjudicate",
                    "text": ("MSUI diverges from Benilla — decide: port Benilla behavior, or record a "
                             "decision preserving MSUI (only allowed if UI/graphics). " + c.get("summary", "")),
                    "source": c["id"], "added": NOW[:10], "resolved": False})
            elif v == "implementedUnverified":
                e["openItems"].append({"id": f"item-{c['id']}-verify", "kind": "verify",
                    "text": "Implemented but needs live-session verification. " + c.get("summary", ""),
                    "source": c["id"], "added": NOW[:10], "resolved": False})
            if c.get("reviewedUtc") and (e["lastReviewedUtc"] or "") < c["reviewedUtc"]:
                e["lastReviewedUtc"] = c["reviewedUtc"]

    # ---- fold in implemented packet audits (msui anchors + summaries) ----
    for audit_path in PAIR_DIR.glob("*/audit.json"):
        a = load_json(audit_path)
        if a.get("status") == "unreviewed": continue
        ref_md = audit_path.parent / "reference.md"
        src = None
        if ref_md.exists():
            m = re.search(r"Reference source: `([^`]+)`", ref_md.read_text(encoding="utf-8", errors="replace")[:2000])
            if m: src = m.group(1)
        if not src: continue
        eid, _ = classify(src)
        e = entries.get(eid)
        if e is None: continue
        for f in a.get("change", {}).get("files", []):
            if f not in e["msuiAnchors"]: e["msuiAnchors"].append(f)
        if not e["summary"]:
            e["summary"] = a.get("msuiAfter", {}).get("summary", "")

    # ---- derive status ----
    verdicts_by_entry = {e["id"]: [c.get("verdict") for c in claims if c["id"] in e["claims"]]
                         for e in entries.values()}
    for e in entries.values():
        vs = set(verdicts_by_entry[e["id"]])
        substantive = vs - {"notRuntime", "internalSupport", None}
        if not substantive:
            e["status"] = "unreviewed" if not vs else "support-only"
            continue
        has_impl = "implementedUnverified" in vs or "verifiedEquivalent" in vs
        has_open = bool([i for i in e["openItems"] if i["kind"] in ("gap", "adjudicate") and not i["resolved"]])
        if "verifiedEquivalent" in vs and not has_open and "implementedUnverified" not in vs:
            e["status"] = "verified"
        elif has_impl and has_open:
            e["status"] = "partial"
        elif has_impl:
            e["status"] = "implemented-unverified"
        else:
            e["status"] = "missing"

    # ---- known open gaps recorded in prior sessions ----
    known = [
        ("ui/spellbookframe", "gap", "Cooldown child overlay, checked overlay, shift-click behavior, and tabs 5-8 (blocked on unpromoted dependency packets)."),
        ("ui/bagframe", "gap", "Keyring pushed+hover art; bag item-slot depress art."),
        ("protocol/session", "gap", "SMSG_TRANSFER_ABORTED handling not implemented."),
        ("protocol/session", "gap", "SMSG_LOGIN_SETTIMESPEED handling not implemented."),
    ]
    for eid, kind, text in known:
        e = entries.get(eid)
        if e is None: continue
        e["openItems"].append({"id": f"item-known-{len(e['openItems'])}", "kind": kind,
                               "text": text, "source": "session-memory-2026-08-08", "added": NOW[:10], "resolved": False})
        if e["status"] in ("unreviewed", "support-only"): e["status"] = "partial"

    # ---- engine inventory entry ----
    entries["engine/internals"] = {
        "schemaVersion": 1, "id": "engine/internals", "title": "Benilla engine internals (inventory)",
        "area": "engine", "deviationPolicy": "not-applicable",
        "benillaSources": [{"path": p, "fileSha256": file_sha[p], "snapshotId": snapshot_id} for p in engine_files],
        "msuiAnchors": [], "status": "not-applicable",
        "summary": ("Benilla's renderer, script engine, asset/format crates, capture harness, tests, and repo "
                    "plumbing. MSUI has its own engine; these files are tracked for drift awareness only and "
                    "are never implementation backlog."),
        "openItems": [], "claims": [], "traces": [], "decisions": [], "lastReviewedUtc": None, "seededUtc": NOW,
    }

    # ---- write ----
    for e in entries.values():
        out = reg_dir / (e["id"] + ".json")
        out.parent.mkdir(parents=True, exist_ok=True)
        out.write_text(json.dumps(e, indent=2), encoding="utf-8")

    counts = collections.Counter((e["area"], e["status"]) for e in entries.values())
    print(f"wrote {len(entries)} entries to {reg_dir}")
    for (area, status), n in sorted(counts.items()):
        print(f"  {area:9s} {status:24s} {n}")
    print(f"engine inventory files: {len(engine_files)}")

if __name__ == "__main__":
    main()
