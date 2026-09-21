"""DBC-level census for shared_docs/SPELL_IDE_MAP.md: which parts of the spell-visual
schema real spells actually use (stages, slots, animations, missiles, kit extras).

    python tools/spellvis/spell_visual_census.py [GameData/Data]

Read-only. Reuses spellvis.py's mount/DBC readers. The numbers in SPELL_IDE_MAP.md
section 1.2 come from this script; re-run it after any DBC change and update them."""
import os, sys, struct, collections
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from spellvis import Mount, Dbc, load, fk, KIT_SLOT_TAGS, MISSILE_ATTACH_TABLE

mount = Mount(sys.argv[1] if len(sys.argv) > 1 else os.path.join("GameData", "Data"))
sv = load(mount, "SpellVisual"); kit = load(mount, "SpellVisualKit"); sven = load(mount, "SpellVisualEffectName")
spell = load(mount, "Spell"); anim = load(mount, "AnimationData")
try:
    shakes = load(mount, "SpellEffectCameraShakes")
except Exception:
    shakes = None
print(sv, kit, sven, spell, anim, shakes, sep="\n")

effect = {sven.u32(r, 0): (sven.s(r, 1), sven.s(r, 2), struct.unpack_from("<f", sven.body, r*sven.recsize + 4*4)[0]) for r in sven.rows()}
animname = {anim.u32(r, 0): anim.s(r, 1) for r in anim.rows()}

# kits
kits = {}
kitfield_nonzero = collections.Counter()
for r in kit.rows():
    kid = kit.u32(r, 0)
    row = [kit.u32(r, f) for f in range(kit.fields)]
    for f, v in enumerate(row):
        if fk(v) is not None: kitfield_nonzero[f] += 1
    slots = [fk(row[3+i]) for i in range(9)]
    kits[kid] = dict(anim=fk(row[2]), sound=fk(row[13]), slots=slots, row=row)

# visuals
visuals = {}
svfield_nonzero = collections.Counter()
for r in sv.rows():
    vid = sv.u32(r, 0)
    row = [sv.u32(r, f) for f in range(sv.fields)]
    for f, v in enumerate(row):
        if fk(v) is not None: svfield_nonzero[f] += 1
    visuals[vid] = row

# spells -> visual: find the visual column empirically (Fireball 133 -> 67)
idrow = {spell.u32(r, 0): r for r in spell.rows()}
fb = idrow[133]
viscol = [f for f in range(spell.fields) if spell.u32(fb, f) == 67]
print("visual column candidates", viscol)
viscol = viscol[0]
speedcol = None
for f in range(spell.fields):
    v = struct.unpack_from("<f", spell.body, fb*spell.recsize + f*4)[0]
    if abs(v - 24.0) < 0.01: speedcol = f
print("speed column", speedcol)

used_visuals = collections.Counter()
spells_with_visual = 0
for r in spell.rows():
    vid = fk(spell.u32(r, viscol))
    if vid and vid in visuals:
        spells_with_visual += 1
        used_visuals[vid] += 1
print(f"spells={spell.n} with a visual={spells_with_visual} distinct visuals used={len(used_visuals)} of {len(visuals)}")

def pct(n, d): return f"{n} ({100.0*n/d:.0f}%)"
V = [visuals[v] for v in used_visuals]
n = len(V)
print("\n== SpellVisual stages, over the", n, "visuals real spells use ==")
names = {1:"precast",2:"cast",3:"impact",4:"state",5:"channel",6:"hasMissile(gate)",7:"missile effect",9:"missile attach",10:"missile sound",11:"strike/impact sound?",12:"?",13:"area kit",14:"?",15:"?"}
for f in range(1, sv.fields):
    c = sum(1 for row in V if fk(row[f]) is not None)
    print(f"  f{f:<2} {names.get(f,'?'):<22} {pct(c, n)}")
combos = collections.Counter()
for row in V:
    combo = "+".join(k for f,k in ((1,"pre"),(2,"cast"),(3,"impact"),(4,"state"),(5,"chan")) if fk(row[f]) is not None)
    if fk(row[7]) is not None: combo += "+missile"
    combos[combo] += 1
print("\n== stage combinations (top 15) ==")
for combo, c in combos.most_common(15): print(f"  {combo:<40} {pct(c, n)}")

# kits reached by used visuals
reached = set()
for row in V:
    for f in (1,2,3,4,5,13):
        k = fk(row[f])
        if k in kits: reached.add(k)
K = [kits[k] for k in reached]
kn = len(K)
print(f"\n== SpellVisualKit over the {kn} kits real spells reach ==")
print("  with caster animation:", pct(sum(1 for k in K if k['anim']), kn))
print("  with sound:", pct(sum(1 for k in K if k['sound']), kn))
slotuse = collections.Counter()
for k in K:
    for i, s in enumerate(k['slots']):
        if s: slotuse[i] += 1
slotnames = ["Head","Chest","Base","LeftHand","RightHand","Breath","Special1","Special2","Special3"]
for i, name in enumerate(slotnames): print(f"  slot {name:<10} {pct(slotuse[i], kn)}")
cnt = collections.Counter(sum(1 for s in k['slots'] if s) for k in K)
print("  models per kit:", dict(sorted(cnt.items())))
print("  kit fields non-empty (all 1772 kits), by field index:")
print("   ", {f: c for f, c in sorted(kitfield_nonzero.items())})
animuse = collections.Counter(k['anim'] for k in K if k['anim'])
print("\n== caster animations used by reached kits (top 20) ==")
for a, c in animuse.most_common(20): print(f"  {a:>4} {animname.get(a,'?'):<24} {c}")
print("  distinct animation ids used:", len(animuse))

# effect models
paths = collections.Counter()
scales = collections.Counter()
for k in K:
    for s in k['slots']:
        if s and s in effect:
            paths[effect[s][1]] += 1
            scales[round(effect[s][2], 2)] += 1
print("\n== effect models ==")
print("  distinct model paths reached:", len(paths), " scale!=1:", sum(c for s,c in scales.items() if s != 1.0), "of", sum(scales.values()))

# missiles
ms = [row for row in V if fk(row[7]) is not None]
print("\n== missiles ==")
print("  visuals with a missile model:", pct(len(ms), n))
mpaths = collections.Counter(effect.get(fk(row[7]), ("", ""))[1] for row in ms)
print("  distinct missile models:", len(mpaths))
print("  missile attach ordinals:", collections.Counter(row[9] for row in ms).most_common(6))
print("  with missile sound:", sum(1 for row in ms if fk(row[10]) is not None))
if speedcol is not None:
    sp = [struct.unpack_from("<f", spell.body, r*spell.recsize + speedcol*4)[0] for r in spell.rows() if fk(spell.u32(r, viscol)) in visuals]
    print("  spells with speed>0:", pct(sum(1 for s in sp if s > 0), len(sp)))
if shakes: print("\ncamera shakes table:", shakes)

print("\n== CharProc types over reached kits (f15..18; -1 = none) ==")
pt = collections.Counter()
for k in K:
    for i in range(4):
        t = k['row'][15+i]
        if t != 0xFFFFFFFF: pt[t] += 1
print("  ", sorted(pt.items()))
print("  kits with StartAnimId (f1):", pct(sum(1 for k in K if fk(k['row'][1]) is not None), kn))
sa = collections.Counter(k['row'][1] for k in K if fk(k['row'][1]) is not None)
print("  top StartAnimIds:", [(a, animname.get(a,'?'), c) for a, c in sa.most_common(8)])
print("  kits with world effect (f12):", sum(1 for k in K if fk(k['row'][12]) is not None))
print("  kits with camera shake (f14):", sum(1 for k in K if fk(k['row'][14]) is not None))
