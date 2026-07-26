"""
Derive the MLIQ coordinate convention from real bytes.

The client's WmoLiquid docstring claims Noggit's Y-up layout
    tile (i,j) -> (CornerX + i*U, height, CornerY - j*U)
while every WMO vertex the client actually renders is Z-up (handbook 3.4).
That is the same shape as the MOVT doc-comment trap. So: don't believe either.
Score candidate conventions against the group's OWN MOGP bounding box, which is
in the same local space as MOVT and is authored, not derived.

A liquid surface must lie inside its group's bounding box. A wrong convention
permutes axes and falls outside. Same method that caught the 26,000-unit vmap
coordinate error: check WHERE the geometry is before checking anything else.
"""
import struct, sys
from mpq import MpqArchive

U = 33.3333 / 8.0   # WMO_LIQUID_UNIT, as the client has it


def chunks(data, pos, end):
    while pos + 8 <= end:
        magic = data[pos:pos + 4][::-1].decode('ascii', 'replace')
        size = struct.unpack_from('<I', data, pos + 4)[0]
        yield magic, pos + 8, size
        pos += 8 + size


def parse_group(data):
    """Return dict with bbox, flags, groupLiquid, movt bounds, mliq."""
    out = {}
    for magic, off, size in chunks(data, 0, len(data)):
        if magic != 'MOGP':
            continue
        g = {}
        g['flags'] = struct.unpack_from('<I', data, off + 0x08)[0]
        g['bbmin'] = struct.unpack_from('<3f', data, off + 0x0C)
        g['bbmax'] = struct.unpack_from('<3f', data, off + 0x18)
        g['groupLiquid'] = struct.unpack_from('<I', data, off + 0x34)[0]
        gend = off + size
        sub = off + 0x44
        g['movt'] = None
        g['mliq'] = None
        for sm, so, ss in chunks(data, sub, gend):
            if sm == 'MOVT':
                n = ss // 12
                vs = struct.unpack_from('<%df' % (n * 3), data, so)
                xs = vs[0::3]; ys = vs[1::3]; zs = vs[2::3]
                g['movt'] = ((min(xs), min(ys), min(zs)), (max(xs), max(ys), max(zs)), n)
            elif sm == 'MLIQ':
                xv, yv, xt, yt = struct.unpack_from('<4I', data, so)
                cx, cy, cz = struct.unpack_from('<3f', data, so + 0x10)
                mtl = struct.unpack_from('<H', data, so + 0x1C)[0]
                nv = xv * yv
                nt = xt * yt
                need = 30 + nv * 8 + nt
                if need > ss:
                    continue
                hs = [struct.unpack_from('<f', data, so + 30 + i * 8 + 4)[0] for i in range(nv)]
                tiles = data[so + 30 + nv * 8: so + 30 + nv * 8 + nt]
                g['mliq'] = dict(xv=xv, yv=yv, xt=xt, yt=yt, cx=cx, cy=cy, cz=cz,
                                 mtl=mtl, h=hs, tiles=tiles)
        out = g
        break
    return out


# Candidate placements: (name, fn(i, j, h, cx, cy, cz) -> (x, y, z))
CANDIDATES = [
    ('A  Z-up  (cx+iU, cy+jU, h)',        lambda i, j, h, cx, cy, cz: (cx + i * U, cy + j * U, h)),
    ('B  Z-up  (cx+iU, cy-jU, h)',        lambda i, j, h, cx, cy, cz: (cx + i * U, cy - j * U, h)),
    ('C  Z-up  swapped (cx+jU, cy+iU, h)',lambda i, j, h, cx, cy, cz: (cx + j * U, cy + i * U, h)),
    ('D  Y-up  (cx+iU, h, cy-jU)  Noggit',lambda i, j, h, cx, cy, cz: (cx + i * U, h, cy - j * U)),
    ('E  Y-up  (cx+iU, h, cy+jU)',        lambda i, j, h, cx, cy, cz: (cx + i * U, h, cy + j * U)),
]


def score(g):
    """For each candidate, how far outside the group bbox does the liquid go?"""
    m = g['mliq']
    lo, hi = g['bbmin'], g['bbmax']
    rows = []
    for name, fn in CANDIDATES:
        mn = [1e30] * 3
        mx = [-1e30] * 3
        for j in range(m['yv']):
            for i in range(m['xv']):
                h = m['h'][j * m['xv'] + i]
                p = fn(i, j, h, m['cx'], m['cy'], m['cz'])
                for k in range(3):
                    mn[k] = min(mn[k], p[k]); mx[k] = max(mx[k], p[k])
        # Escape = how far outside the authored bbox, summed over axes.
        esc = 0.0
        for k in range(3):
            esc += max(0.0, lo[k] - mn[k]) + max(0.0, mx[k] - hi[k])
        rows.append((esc, name, tuple(round(v, 1) for v in mn), tuple(round(v, 1) for v in mx)))
    return rows


def main(paths):
    a = MpqArchive('/mnt/user-data/uploads/MSUIClient/GameData/Data/wmo.MPQ')
    totals = {name: 0.0 for _, name in [(0, n) for n, _ in CANDIDATES]}
    counted = 0
    for p in paths:
        data = a.read_file(p)
        if not data:
            print('MISSING', p); continue
        g = parse_group(data)
        if not g or not g.get('mliq'):
            continue
        m = g['mliq']
        counted += 1
        print('\n=== %s' % p.rsplit('\\', 1)[-1])
        print('    flags 0x%08X  LIQUIDSURFACE=%s  groupLiquid=%d  mtl=%d'
              % (g['flags'], bool(g['flags'] & 0x1000), g['groupLiquid'], m['mtl']))
        print('    grid %dx%d verts / %dx%d tiles   corner (%.2f, %.2f, %.2f)'
              % (m['xv'], m['yv'], m['xt'], m['yt'], m['cx'], m['cy'], m['cz']))
        print('    heights %.2f .. %.2f' % (min(m['h']), max(m['h'])))
        print('    MOGP bbox  min %s  max %s'
              % (tuple(round(v, 1) for v in g['bbmin']), tuple(round(v, 1) for v in g['bbmax'])))
        if g['movt']:
            print('    MOVT bounds min %s max %s (%d verts)'
                  % (tuple(round(v, 1) for v in g['movt'][0]),
                     tuple(round(v, 1) for v in g['movt'][1]), g['movt'][2]))
        rows = score(g)
        for esc, name, mn, mx in sorted(rows):
            totals[name] += esc
            print('      escape %9.2f  %-38s min %s max %s' % (esc, name, mn, mx))
    print('\n===== TOTAL ESCAPE over %d group(s) with MLIQ =====' % counted)
    for name, t in sorted(totals.items(), key=lambda kv: kv[1]):
        print('  %10.2f  %s' % (t, name))


if __name__ == '__main__':
    main(sys.argv[1:])
