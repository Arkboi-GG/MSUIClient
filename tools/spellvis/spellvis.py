#!/usr/bin/env python3
"""Spell-visual chain extractor (NIGHT_03).

Resolves spell id -> SpellVisual -> the five stage kits -> each kit's nine
SpellVisualEffectName slots (attach tag + .mdx path) + the missile block.

Schema per benilla-formats/src/spell_visual.rs, byte-verified vs build 5875.
This tool VERIFIES that schema against the local MPQs before trusting it.
Read-only. Emits the query-derived reference table the parity harness needs.
"""
import struct, sys, os, csv, json

sys.path.insert(0, os.path.join(os.path.dirname(__file__), '..', 'mpqpeek'))
from mpq import Mpq

# Kit fields 3-11 -> M2 AttachmentID, in kit-field order.
# Head, Chest, Base, LeftHand, RightHand, Breath, Special1..3
KIT_SLOT_TAGS = [0x14, 0x22, 0x13, 0x15, 0x16, 0x11, 0x17, 0x18, 0x19]
# SpellVisual field 9 indexes this for the missile's destination attach point.
MISSILE_ATTACH_TABLE = KIT_SLOT_TAGS + [0x0f, 0x10]
# The client's literal fallback when the missile chain resolves to nothing.
ERROR_CUBE = r"Spells\ErrorCube.mdx"

# "no value" is written as EITHER 0 or 0xFFFFFFFF on the real table.
def fk(v):
    return None if v == 0 or v == 0xFFFFFFFF else v


class Mount:
    """Supplier-ordered MPQ mount; later entries win (patches override base)."""
    ORDER = ["dbc.MPQ", "base.MPQ", "misc.MPQ", "patch.MPQ", "patch-2.MPQ", "patch-4.MPQ"]

    def __init__(self, data_dir):
        self.archives = []
        for name in self.ORDER:
            p = os.path.join(data_dir, name)
            if os.path.exists(p):
                try:
                    self.archives.append((name, Mpq(p)))
                except Exception as e:
                    print(f"  [warn] {name}: {e}", file=sys.stderr)

    def read(self, path):
        for name, a in reversed(self.archives):   # last supplier wins
            try:
                if a.has(path):
                    return a.read(path), name
            except Exception:
                continue
        return None, None


class Dbc:
    def __init__(self, blob, path, supplier):
        magic, self.n, self.fields, self.recsize, self.sbsize = struct.unpack_from("<4sIIII", blob, 0)
        if magic != b"WDBC":
            raise ValueError(f"{path}: not WDBC")
        self.path, self.supplier = path, supplier
        self.body = blob[20:20 + self.n * self.recsize]
        self.strings = blob[20 + self.n * self.recsize:]

    def u32(self, row, field):
        return struct.unpack_from("<I", self.body, row * self.recsize + field * 4)[0]

    def s(self, row, field):
        off = self.u32(row, field)
        if off == 0 or off >= len(self.strings):
            return ""
        end = self.strings.find(b"\0", off)
        return self.strings[off:end].decode("utf-8", "replace")

    def rows(self):
        return range(self.n)

    def __repr__(self):
        return f"{self.path} [{self.supplier}] {self.n}rec x {self.fields}f x {self.recsize}B"


def load(mount, name):
    blob, sup = mount.read(f"DBFilesClient\\{name}.dbc")
    if blob is None:
        raise SystemExit(f"FATAL: {name}.dbc not found in any MPQ")
    return Dbc(blob, name, sup)


def main():
    data = sys.argv[1] if len(sys.argv) > 1 else os.path.join("GameData", "Data")
    out = sys.argv[2] if len(sys.argv) > 2 else "spellvis-reference.csv"
    mount = Mount(data)
    print("Mounted:", ", ".join(n for n, _ in mount.archives))

    sv   = load(mount, "SpellVisual")
    kit  = load(mount, "SpellVisualKit")
    sven = load(mount, "SpellVisualEffectName")
    spell = load(mount, "Spell")
    for d in (sv, kit, sven, spell):
        print("  ", d)

    # ---- schema assertions (Benilla's byte-verified layout) -------------
    ok = True
    for d, f, sz in ((sv, 16, 64), (kit, 35, 140), (sven, 5, 20)):
        if (d.fields, d.recsize) != (f, sz):
            print(f"  SCHEMA MISMATCH {d.path}: got {d.fields}f/{d.recsize}B expected {f}f/{sz}B")
            ok = False
    print("  schema:", "MATCHES build-5875 layout" if ok else "DIVERGES — stop and re-derive")

    # ---- effect-name table: id -> (label, model path) ------------------
    effect = {}
    for r in sven.rows():
        effect[sven.u32(r, 0)] = (sven.s(r, 1), sven.s(r, 2))

    # ---- kits: id -> dict ----------------------------------------------
    kits = {}
    for r in kit.rows():
        kid = kit.u32(r, 0)
        slots = []
        for i in range(9):
            eid = fk(kit.u32(r, 3 + i))
            if eid and eid in effect:
                slots.append((KIT_SLOT_TAGS[i], eid, effect[eid][1]))
        kits[kid] = {
            "anim": fk(kit.u32(r, 2)),
            "sound": fk(kit.u32(r, 13)),
            "slots": slots,
        }

    # ---- visuals: id -> stages + missile block -------------------------
    visuals = {}
    for r in sv.rows():
        vid = sv.u32(r, 0)
        mm = fk(sv.u32(r, 7))
        ma = sv.u32(r, 9)
        visuals[vid] = {
            "precast": fk(sv.u32(r, 1)), "cast": fk(sv.u32(r, 2)),
            "impact":  fk(sv.u32(r, 3)), "state": fk(sv.u32(r, 4)),
            "channel": fk(sv.u32(r, 5)),
            "missile_model": effect.get(mm, ("", ""))[1] if mm else "",
            "missile_attach": MISSILE_ATTACH_TABLE[ma] if ma < len(MISSILE_ATTACH_TABLE) else None,
            "missile_sound": fk(sv.u32(r, 10)),
        }

    # ---- calibrate Spell.dbc columns empirically -----------------------
    # Fireball = spell 133, SpellVisual 67 (benilla verified). Find the field
    # holding the visual id rather than trusting a remembered offset.
    idrow = {spell.u32(r, 0): r for r in spell.rows()}
    vis_field = speed_field = None
    probe = idrow.get(133)
    if probe is not None:
        for f in range(spell.fields):
            if spell.u32(probe, f) == 67 and all(
                spell.u32(idrow[s], f) in visuals for s in (133, 168, 587) if s in idrow):
                vis_field = f
                break
        for f in range(spell.fields):   # Speed is a float; Fireball travels
            v = struct.unpack_from("<f", spell.body, probe * spell.recsize + f * 4)[0]
            if 10.0 < v < 60.0 and struct.unpack_from(
                    "<f", spell.body, idrow[168] * spell.recsize + f * 4)[0] == 0.0:
                speed_field = f
                break
    print(f"  Spell.dbc: visual field = {vis_field}, speed field = {speed_field}")

    # ---- verification: the Fireball chain ------------------------------
    print("\n=== VERIFY: Fireball (spell 133) ===")
    if vis_field is not None and 133 in idrow:
        vid = spell.u32(idrow[133], vis_field)
        st = visuals.get(vid, {})
        print(f"  spell 133 -> visual {vid}")
        print(f"  stages: precast={st.get('precast')} cast={st.get('cast')} "
              f"impact={st.get('impact')} state={st.get('state')} channel={st.get('channel')}")
        for stage in ("precast", "cast", "impact", "state", "channel"):
            k = st.get(stage)
            if not k or k not in kits:
                continue
            kd = kits[k]
            print(f"  kit {k} ({stage}): anim={kd['anim']} sound={kd['sound']} slots={len(kd['slots'])}")
            for tag, eid, path in kd["slots"]:
                print(f"      attach 0x{tag:02x}  effect {eid}  {path}")
        print(f"  missile: model={st.get('missile_model') or '(none -> ammo/ErrorCube)'} "
              f"attach=0x{st['missile_attach']:02x} sound={st.get('missile_sound')}"
              if st.get("missile_attach") is not None else "  missile: -")

    # ---- emit the reference table --------------------------------------
    n = 0
    with open(out, "w", newline="", encoding="utf-8") as fh:
        w = csv.writer(fh)
        w.writerow(["spell_id", "visual_id", "stage", "kit_id", "anim_id", "kit_sound",
                    "slot_index", "attach_tag", "effect_id", "effect_model",
                    "missile_model", "missile_attach", "missile_sound", "speed"])
        for sid, r in sorted(idrow.items()):
            if vis_field is None:
                break
            vid = spell.u32(r, vis_field)
            st = visuals.get(vid)
            if not st:
                continue
            speed = struct.unpack_from("<f", spell.body, r * spell.recsize + speed_field * 4)[0] \
                if speed_field is not None else 0.0
            for stage in ("precast", "cast", "impact", "state", "channel"):
                k = st.get(stage)
                if not k or k not in kits:
                    continue
                kd = kits[k]
                rows = kd["slots"] or [(None, None, "")]
                for i, (tag, eid, path) in enumerate(rows):
                    w.writerow([sid, vid, stage, k, kd["anim"], kd["sound"], i,
                                f"0x{tag:02x}" if tag else "", eid or "", path,
                                st["missile_model"],
                                f"0x{st['missile_attach']:02x}" if st["missile_attach"] is not None else "",
                                st["missile_sound"] or "", round(speed, 3)])
                    n += 1
    print(f"\nwrote {out}: {n} rows")


if __name__ == "__main__":
    main()
